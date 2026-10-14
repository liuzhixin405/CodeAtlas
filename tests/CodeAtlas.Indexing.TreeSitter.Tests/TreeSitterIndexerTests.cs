using System.Text;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class TreeSitterIndexerTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

    public TreeSitterIndexerTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-repo-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("repo"), _repo);
    }

    private TreeSitterIndexer NewIndexer(IndexOptions? options = null) =>
        new(_db, _adapters, new HeuristicEdgeResolver(), options);

    private IndexReader Reader => new(_db);

    private SymbolRecord Sym(string qualifiedName) => Reader.FindByQualifiedName(qualifiedName).Single();

    [Fact]
    public async Task Indexer_FullIndex_ProducesExpectedSymbolsAndEdges()
    {
        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(3, report.FilesScanned);
        Assert.Equal(3, report.FilesParsed);
        Assert.Equal(0, report.FilesFailed);
        Assert.Empty(report.Errors);

        var main = Sym("src/app.ts:main");
        var find = Sym("src/svc.ts:Svc.find");
        var svc = Sym("src/svc.ts:Svc");
        var helper = Sym("src/util/format.ts:helper");

        var mainOut = Reader.GetNeighbors(main.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Contains(mainOut, n => n.Other?.Id == svc.Id && n.Edge.Confidence == 0.7);
        Assert.Contains(mainOut, n => n.Other?.Id == find.Id && n.Edge.Confidence == 0.7);
        Assert.Contains(mainOut, n => n.Edge.ToName == "external:fetch" && n.Edge.Confidence == 0.5);

        var findOut = Reader.GetNeighbors(find.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(helper.Id, findOut.Single().Other!.Id);
        Assert.Equal(0.7, findOut.Single().Edge.Confidence);
        Assert.Equal(3, findOut.Single().Edge.Line);

        var appModule = Sym("src/app.ts");
        var imports = Reader.GetNeighbors(appModule.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Imports });
        Assert.Equal(Sym("src/svc.ts").Id, imports.Single().Other!.Id);
    }

    [Fact]
    public async Task Indexer_SecondRun_ReparsesNothing()
    {
        await NewIndexer().IndexAsync(_repo);
        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(0, report.FilesParsed);
        Assert.Equal(3, report.FilesUnchanged);
        Assert.Equal(3, Reader.GetAllFiles().Count);
    }

    [Fact]
    public async Task Indexer_ModifiedFile_ReparsesOnlyThatFile_AndReattachesIncomingEdges()
    {
        await NewIndexer().IndexAsync(_repo);
        var oldHelperId = Sym("src/util/format.ts:helper").Id;
        var formatPath = Path.Combine(_repo, "src", "util", "format.ts");
        File.WriteAllText(formatPath, "export function extra(): void {}\n" + File.ReadAllText(formatPath));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesParsed);
        Assert.Equal(2, report.FilesUnchanged);
        var newHelper = Sym("src/util/format.ts:helper");
        Assert.NotEqual(oldHelperId, newHelper.Id);
        Assert.Equal(2, newHelper.StartLine);
        var findOut = Reader.GetNeighbors(Sym("src/svc.ts:Svc.find").Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(newHelper.Id, findOut.Single().Other!.Id);
        Assert.Empty(Reader.GetDanglingEdges());
    }

    [Fact]
    public async Task Indexer_RemovedFile_DeletesSymbolsAndDanglesEdges()
    {
        await NewIndexer().IndexAsync(_repo);
        File.Delete(Path.Combine(_repo, "src", "util", "format.ts"));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesRemoved);
        Assert.Null(Reader.GetFile("src/util/format.ts"));
        Assert.Empty(Reader.FindByQualifiedName("src/util/format.ts:helper"));
        var dangling = Reader.GetDanglingEdges();
        Assert.Contains(dangling, e => e.ToName == "src/util/format.ts:helper" && e.Kind == EdgeKind.Calls);
        Assert.Contains(dangling, e => e.ToName == "src/util/format.ts" && e.Kind == EdgeKind.Imports);
    }

    [Fact]
    public async Task Indexer_ReadsBomFile_WithoutOffsetShift()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("export class Bom {}\n")).ToArray();
        File.WriteAllBytes(Path.Combine(_repo, "src", "bom.ts"), bytes);

        await NewIndexer().IndexAsync(_repo);

        var bom = Sym("src/bom.ts:Bom");
        Assert.Equal(1, bom.StartLine);
        Assert.Equal(8, bom.StartCol);
    }

    [Fact]
    public async Task Indexer_EmptyAndLargeFiles_AreIndexedOrSkipped()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "empty.ts"), "");
        File.WriteAllText(Path.Combine(_repo, "src", "large.ts"), new string('/', 2_500_000));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesSkipped);
        Assert.Equal(FileStatus.Skipped, Reader.GetFile("src/large.ts")!.Status);
        Assert.Empty(Reader.GetSymbolsInFile(Reader.GetFile("src/large.ts")!.Id));
        Assert.Equal(FileStatus.Ok, Reader.GetFile("src/empty.ts")!.Status);
        Assert.Single(Reader.GetSymbolsInFile(Reader.GetFile("src/empty.ts")!.Id));
    }

    private sealed class ExplodingAdapter : ILanguageAdapter
    {
        public string LanguageId => "boom";
        public IReadOnlyList<string> Extensions => [".boom"];
        public ParsedFile Parse(string relativePath, string source) => throw new InvalidOperationException("kaboom");
    }

    [Fact]
    public async Task Indexer_ParseFailure_IsRecordedNotThrown()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "x.boom"), "anything");
        var indexer = new TreeSitterIndexer(_db, [.. _adapters, new ExplodingAdapter()], new HeuristicEdgeResolver());

        var report = await indexer.IndexAsync(_repo);

        Assert.Equal(1, report.FilesFailed);
        Assert.Equal(3, report.FilesParsed);
        Assert.Contains(report.Errors, e => e.Path == "src/x.boom" && e.Error.Contains("kaboom"));
        Assert.Equal(FileStatus.ParseFailed, Reader.GetFile("src/x.boom")!.Status);
        Assert.NotNull(Reader.GetFile("src/app.ts"));
    }

    [Fact]
    public async Task Indexer_ExcludesConfiguredDirectories_AndReportsPhases()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_repo, "node_modules", "pkg", "index.ts"), "export function hidden() {}");
        var phases = new List<string>();
        var progress = new Progress<IndexProgress>(p => { lock (phases) phases.Add(p.Phase); });

        var report = await NewIndexer().IndexAsync(_repo, progress);
        await Task.Delay(50); // Progress<T> 回调是异步投递的

        Assert.Equal(3, report.FilesScanned);
        Assert.Empty(Reader.FindByName("hidden"));
        Assert.Contains("scan", phases);
        Assert.Contains("parse", phases);
        Assert.Contains("write", phases);
        Assert.Contains("resolve", phases);
    }

    [Fact]
    public async Task Indexer_SkipsNestedGitCheckouts_WorktreesAndSubmodules()
    {
        // git worktree 的标记是一个 .git 文件；子模块/嵌套仓库是 .git 目录。两者都不属于本仓库。
        Directory.CreateDirectory(Path.Combine(_repo, ".claude", "worktrees", "wt1", "src"));
        File.WriteAllText(Path.Combine(_repo, ".claude", "worktrees", "wt1", ".git"), "gitdir: ../../../.git/worktrees/wt1");
        File.WriteAllText(Path.Combine(_repo, ".claude", "worktrees", "wt1", "src", "dup.ts"), "export function duplicated() {}");
        Directory.CreateDirectory(Path.Combine(_repo, "vendor", "sub", ".git"));
        File.WriteAllText(Path.Combine(_repo, "vendor", "sub", "lib.ts"), "export function fromSubmodule() {}");

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(3, report.FilesScanned);
        Assert.Empty(Reader.FindByName("duplicated"));
        Assert.Empty(Reader.FindByName("fromSubmodule"));
    }

    [Fact]
    public async Task Indexer_CqrsChain_ControllerReachesHandlerThroughDispatchEdge()
    {
        var cqrs = Path.Combine(Path.GetTempPath(), "codeatlas-cqrs-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("cqrs"), cqrs);
        try
        {
            await NewIndexer().IndexAsync(cqrs);
            var list = Sym("Acme.Api.Mt4Controller.ListFeeds");
            var handle = Sym("Acme.Handlers.ListFeedsHandler.HandleAsync");

            var dispatch = Reader.GetNeighbors(list.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Dispatches }).Single();
            Assert.Equal(handle.Id, dispatch.Other!.Id);
            Assert.Equal("dispatch:ListFeedsQuery", dispatch.Edge.Via);
            // 同一行里 dispatcher.QueryAsync 与 new ListFeedsQuery 都是管道细节
            var conduits = Reader.GetNeighbors(list.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).Where(n => n.Edge.Via == "conduit").ToList();
            Assert.Equal(2, conduits.Count);
            Assert.Equal(7, dispatch.Edge.EvidenceLine); // new ListFeedsQuery 所在行

            // 链条继续：HandleAsync → repo.ListFeedsAsync（同名唯一，全仓库 0.5）
            var next = Reader.GetNeighbors(handle.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
            Assert.Contains(next, n => n.Other?.QualifiedName == "Acme.Infrastructure.FeedRepository.ListFeedsAsync");
        }
        finally { Directory.Delete(cqrs, recursive: true); }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
