using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 临时目录：parent/main 是主仓（自己有 .git 目录），里面有
/// 独立克隆 clone/（.git 目录）、子模块 sub/（.git 文件 → ../.git/modules/sub）、
/// worktree wt/（.git 文件 → main/.git/worktrees/wt，绝对路径、反斜杠）、.claude/worktrees/w2（.git 文件 → 相对路径的 worktrees）；
/// parent/other 是外部根；parent/top.ts 用来测"外部根包含主根"。
/// </summary>
public sealed class MultiRootFixture : IDisposable
{
    public string Parent { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-multi-" + Guid.NewGuid().ToString("N"));
    public string Main => Path.Combine(Parent, "main");
    public string Other => Path.Combine(Parent, "other");

    public MultiRootFixture()
    {
        Directory.CreateDirectory(Path.Combine(Main, ".git"));
        Write("main/src/app.ts", "export function mainApp() {}\n");
        Directory.CreateDirectory(Path.Combine(Main, "clone", ".git"));
        Write("main/clone/lib.ts", "export function fromClone() {}\n");
        Write("main/sub/.git", "gitdir: ../.git/modules/sub\n");
        Write("main/sub/s.ts", "export function fromSubmodule() {}\n");
        Write("main/wt/.git", $"gitdir: {Path.Combine(Main, ".git", "worktrees", "wt")}\n");
        Write("main/wt/src/app.ts", "export function duplicated() {}\n");
        Write("main/.claude/worktrees/w2/.git", "gitdir: ../../../.git/worktrees/w2\n");
        Write("main/.claude/worktrees/w2/x.ts", "export function duplicated2() {}\n");
        Write("other/api/foo.ts", "export function fromOther() {}\n");
        Write("top.ts", "export function fromParent() {}\n");
    }

    public void Write(string relativeToParent, string content)
    {
        var full = Path.Combine(Parent, relativeToParent.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
    }
}

public class RepositoryScannerTests : IDisposable
{
    private readonly MultiRootFixture _fx = new();
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase) { [".ts"] = "typescript" };
    private static readonly string[] Excluded = ["node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea"];

    private static List<string> Paths(ProjectRoots roots) =>
        new RepositoryScanner(Languages, Excluded).Scan(roots).Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();

    [Fact]
    public void Scan_IncludesClonesAndSubmodules_SkipsWorktrees()
    {
        Assert.Equal(["clone/lib.ts", "src/app.ts", "sub/s.ts"], Paths(ProjectRoots.Single(_fx.Main)));
    }

    [Fact]
    public void Scan_NoNested_RestoresOldBehavior()
    {
        Assert.Equal(["src/app.ts"], Paths(ProjectRoots.Single(_fx.Main, includeNested: false)));
    }

    [Fact]
    public void Scan_ExternalRoot_PathsAreRelativeToMain_AndCombineBackToTheFile()
    {
        var files = new RepositoryScanner(Languages, Excluded).Scan(ProjectRoots.Create(_fx.Main, [_fx.Other])).ToList();

        Assert.Equal(["../other/api/foo.ts", "clone/lib.ts", "src/app.ts", "sub/s.ts"], files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
        var other = files.Single(f => f.RelativePath == "../other/api/foo.ts");
        Assert.Equal(Path.Combine(_fx.Other, "api", "foo.ts"), other.FullPath);
        Assert.Equal(other.FullPath, Path.GetFullPath(Path.Combine(_fx.Main, other.RelativePath)));
    }

    [Fact]
    public void Scan_ExternalRootContainingMain_ListsEveryFileOnce_MainFilesKeepTheirShortPaths()
    {
        Assert.Equal(["../other/api/foo.ts", "../top.ts", "clone/lib.ts", "src/app.ts", "sub/s.ts"], Paths(ProjectRoots.Create(_fx.Main, [_fx.Parent])));
    }

    [Fact]
    public void Scan_ExternalRootInsideMain_IsScannedOnceEvenWhenNestedReposAreOff()
    {
        var clone = Path.Combine(_fx.Main, "clone");
        Assert.Equal(["clone/lib.ts", "src/app.ts"], Paths(ProjectRoots.Create(_fx.Main, [clone], includeNested: false)));
        Assert.Equal(["clone/lib.ts", "src/app.ts", "sub/s.ts"], Paths(ProjectRoots.Create(_fx.Main, [clone], includeNested: true)));
    }

    public void Dispose() => _fx.Dispose();
}

public class MultiRootIndexerTests : IDisposable
{
    private readonly MultiRootFixture _fx = new();
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

    private TreeSitterIndexer NewIndexer() => new(_db, _adapters, new HeuristicEdgeResolver());
    private IndexReader Reader => new(_db);

    [Fact]
    public async Task SecondRun_WithExternalRoot_ReparsesNothing_AndKeepsExternalFiles()
    {
        var roots = ProjectRoots.Create(_fx.Main, [_fx.Other]);
        var first = await NewIndexer().IndexAsync(roots);
        var second = await NewIndexer().IndexAsync(roots);

        Assert.Equal(4, first.FilesScanned);
        Assert.Equal((0, 4, 0), (second.FilesParsed, second.FilesUnchanged, second.FilesRemoved));
        Assert.Equal(["../other/api/foo.ts", "clone/lib.ts", "src/app.ts", "sub/s.ts"], Reader.GetAllFiles().Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Single(Reader.FindByName("fromOther"));
    }

    [Fact]
    public async Task MissingExternalRoot_DoesNotDeleteItsFiles_DroppingTheRootDoes()
    {
        await NewIndexer().IndexAsync(ProjectRoots.Create(_fx.Main, [_fx.Other]));
        Directory.Move(_fx.Other, _fx.Other + "-moved");

        var missing = ProjectRoots.Create(_fx.Main, [_fx.Other]);
        Assert.Equal([_fx.Other], missing.Missing);
        var whileMissing = await NewIndexer().IndexAsync(missing);
        Assert.Equal(0, whileMissing.FilesRemoved);
        Assert.NotNull(Reader.GetFile("../other/api/foo.ts"));

        var dropped = await NewIndexer().IndexAsync(ProjectRoots.Single(_fx.Main));
        Assert.Equal(1, dropped.FilesRemoved);
        Assert.Null(Reader.GetFile("../other/api/foo.ts"));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        _fx.Dispose();
    }
}
