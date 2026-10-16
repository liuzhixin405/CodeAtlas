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
        // new Svc()：Svc 是 import { Svc } from './svc' 绑定的名字 → 按导出解析 0.95（以前按 import 范围同名 0.7）
        Assert.Contains(mainOut, n => n.Other?.Id == svc.Id && n.Edge.Confidence == 0.95);
        // new Svc().find()：接收者是 new 出来的 Svc 实例 → Svc 的成员 0.9（以前当类型未知、按 import 范围同名 0.7）
        Assert.Contains(mainOut, n => n.Other?.Id == find.Id && n.Edge.Confidence == 0.9);
        Assert.Contains(mainOut, n => n.Edge.ToName == "external:fetch" && n.Edge.Confidence == 0.5);

        // helper 是 import { helper } from './util/format' 绑定的名字 → 0.95（以前 0.7）
        var findOut = Reader.GetNeighbors(find.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(helper.Id, findOut.Single().Other!.Id);
        Assert.Equal(0.95, findOut.Single().Edge.Confidence);
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
    public async Task Indexer_SkipsWorktrees_ButIncludesIndependentClones_UnlessNestedIsOff()
    {
        // git worktree 的标记是一个 .git 文件（gitdir 指向 …/worktrees/…），是本仓库的副本，跳过；
        // .git 目录是独立克隆，内容只在这里，默认纳入（多项目索引）；IncludeNested=false 恢复旧行为。
        Directory.CreateDirectory(Path.Combine(_repo, ".claude", "worktrees", "wt1", "src"));
        File.WriteAllText(Path.Combine(_repo, ".claude", "worktrees", "wt1", ".git"), "gitdir: ../../../.git/worktrees/wt1");
        File.WriteAllText(Path.Combine(_repo, ".claude", "worktrees", "wt1", "src", "dup.ts"), "export function duplicated() {}");
        Directory.CreateDirectory(Path.Combine(_repo, "vendor", "sub", ".git"));
        File.WriteAllText(Path.Combine(_repo, "vendor", "sub", "lib.ts"), "export function fromSubmodule() {}");

        var report = await NewIndexer().IndexAsync(_repo);

        // 旧期望 3（fixture 的 src/app.ts、src/svc.ts、src/util/format.ts）；现在 vendor/sub/lib.ts 是独立克隆，纳入 → 3 + 1 = 4
        Assert.Equal(4, report.FilesScanned);
        Assert.Empty(Reader.FindByName("duplicated"));
        Assert.Equal("vendor/sub/lib.ts", Reader.GetFileById(Reader.FindByName("fromSubmodule").Single().FileId)!.Path);

        // 关掉嵌套仓库：回到 3 个文件，克隆里的文件按"磁盘上没有了"删掉
        var off = await NewIndexer().IndexAsync(CodeAtlas.Core.Workspace.ProjectRoots.Single(_repo, includeNested: false));
        Assert.Equal((3, 1), (off.FilesScanned, off.FilesRemoved));
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

            // 链条继续：HandleAsync → repo.ListFeedsAsync。接收者是 IFeedRepository 字段，接口定义不在夹具里，
            // FeedRepository 的 base_list 写了它 → 连到实现上的同名方法，名字匹配的低档 0.5（以前按"全仓库同名唯一"也是 0.5）
            var next = Reader.GetNeighbors(handle.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
            Assert.Contains(next, n => n.Other?.QualifiedName == "Acme.Infrastructure.FeedRepository.ListFeedsAsync" && n.Edge.Confidence == 0.5);
        }
        finally { Directory.Delete(cqrs, recursive: true); }
    }

    [Fact]
    public async Task Indexer_FailedResolution_DoesNotPublishFileChanges()
    {
        await NewIndexer().IndexAsync(_repo);
        var original = Reader.GetFile("src/app.ts")!;
        var appPath = Path.Combine(_repo, "src", "app.ts");
        File.AppendAllText(appPath, "\nexport function added() {}\n");
        var failing = new TreeSitterIndexer(_db, _adapters, new FailingResolver());

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.IndexAsync(_repo));

        Assert.Equal(original.Hash, Reader.GetFile("src/app.ts")!.Hash);
        Assert.Empty(Reader.FindByName("added"));
        var retry = await NewIndexer().IndexAsync(_repo);
        Assert.Equal(1, retry.FilesParsed);
        Assert.Contains(Reader.GetNeighbors(Sym("src/app.ts:main").Id, Direction.Outgoing),
            neighbor => neighbor.Other?.QualifiedName == "src/svc.ts:Svc.find");
    }

    [Fact]
    public async Task Indexer_NewTarget_ResolvesUnchangedCaller()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"),
            "import { later } from './later';\nexport function main() { later(); }\n");
        await NewIndexer().IndexAsync(_repo);
        File.WriteAllText(Path.Combine(_repo, "src", "later.ts"), "export function later() {}\n");

        await NewIndexer().IndexAsync(_repo);

        var calls = Reader.GetNeighbors(Sym("src/app.ts:main").Id, Direction.Outgoing,
            new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(Sym("src/later.ts:later").Id, Assert.Single(calls).Other?.Id);
        Assert.Equal(0.95, calls[0].Edge.Confidence);
    }

    [Theory]
    [InlineData("var unused = new Query();", false)]
    [InlineData("var unused = new Query(); Send(\"other\");", false)]
    [InlineData("Send(new Query());", true)]
    [InlineData("Send(\n new Query());", true)]
    public async Task Indexer_DispatchRequiresMessageArgument(string body, bool dispatched)
    {
        File.WriteAllText(Path.Combine(_repo, "src", "dispatch.cs"), $$"""
            namespace DispatchTest;
            public class Query {}
            public interface IRequestHandler<T> {}
            public class Handler : IRequestHandler<Query> {
                public void Handle(Query query) {}
            }
            public class Sender {
                public void Run() { {{body}} }
                public void Send(object message) {}
            }
            """);
        await NewIndexer().IndexAsync(_repo);

        var sender = Sym("DispatchTest.Sender.Run");
        var outgoing = Reader.GetNeighbors(sender.Id, Direction.Outgoing);
        Assert.Equal(dispatched ? 1 : 0, outgoing.Count(neighbor => neighbor.Edge.Kind == EdgeKind.Dispatches));
        Assert.Equal(dispatched ? 2 : 0, outgoing.Count(neighbor => neighbor.Edge.Via == "conduit"));
    }

    [Fact]
    public async Task Indexer_ChangedCSharpTarget_RefreshesPreciseCallersWithoutLosingTheCall()
    {
        var targetPath = Path.Combine(_repo, "src", "target.cs");
        File.WriteAllText(targetPath, "namespace Refresh; public class Target { public static int Value() => 1; }");
        File.WriteAllText(Path.Combine(_repo, "src", "caller.cs"), "namespace Refresh; public class Caller { public int Run() => Target.Value(); }");
        await NewIndexer().IndexAsync(_repo);
        var caller = Sym("Refresh.Caller.Run");
        var target = Sym("Refresh.Target.Value");
        _db.Execute("UPDATE symbols SET qualified_name = 'Refresh.Target.Value()', source = 'roslyn' WHERE id = $id", ("$id", target.Id));
        _db.Execute("UPDATE edges SET source = 'roslyn', to_name = 'Refresh.Target.Value()', confidence = 1.0 WHERE to_id = $id AND kind = 'calls'", ("$id", target.Id));
        _db.Execute("UPDATE files SET roslyn_at = '2026-10-16T00:00:00Z' WHERE language = 'csharp'");
        File.AppendAllText(targetPath, "\n");

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesParsed);
        var bound = Reader.GetNeighbors(caller.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Where(neighbor => neighbor.Other is not null).ToList();
        Assert.Equal(Sym("Refresh.Target.Value").Id, Assert.Single(bound).Other!.Id);
        Assert.Equal(IndexSource.TreeSitter, bound[0].Edge.Source);
        Assert.Equal(0, _db.Scalar<long>("SELECT COUNT(*) FROM files WHERE id = $file AND roslyn_at IS NOT NULL", ("$file", caller.FileId)));
    }

    [Fact]
    public async Task Indexer_ReExportChange_RebindsPreviouslyResolvedCaller()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { target } from './target'; export function main() { target(); }");
        var targetPath = Path.Combine(_repo, "src", "target.ts");
        File.WriteAllText(targetPath, "export function target() {}");
        File.WriteAllText(Path.Combine(_repo, "src", "moved.ts"), "export function target() {}");
        await NewIndexer().IndexAsync(_repo);
        File.WriteAllText(targetPath, "export { target } from './moved';");

        await NewIndexer().IndexAsync(_repo);

        var calls = Reader.GetNeighbors(Sym("src/app.ts:main").Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(Sym("src/moved.ts:target").Id, Assert.Single(calls).Other?.Id);
    }

    [Fact]
    public async Task Indexer_CanceledBetweenWriteChunks_RollsBackAllChunks()
    {
        await NewIndexer().IndexAsync(_repo);
        var original = Reader.GetFile("src/app.ts")!;
        File.AppendAllText(Path.Combine(_repo, "src", "app.ts"), "\nexport function added() {}\n");
        File.AppendAllText(Path.Combine(_repo, "src", "svc.ts"), "\nexport function changed() {}\n");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value => { if (value.Phase == "write") cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewIndexer(new IndexOptions { WriteBatchSize = 1 }).IndexAsync(_repo, progress, cancellation.Token));

        Assert.Equal(original.Hash, Reader.GetFile("src/app.ts")!.Hash);
        Assert.Empty(Reader.FindByName("added"));
        Assert.Equal(2, (await NewIndexer().IndexAsync(_repo)).FilesParsed);
    }

    private sealed class ImmediateProgress(Action<IndexProgress> report) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => report(value);
    }

    private sealed class FailingResolver : IEdgeResolver
    {
        public Task ResolveAsync(ResolveContext context, CancellationToken ct) =>
            throw new InvalidOperationException("resolution failed");
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
