using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>
/// parent/main：前端（package.json 在根）+ Main.Web（引用外部根的 ../../other/Lib）+ Main.sln，
/// 独立克隆 nested/（.git 目录，Nested.Svc + Nested.sln），非点目录的 worktree wt2/（.git 文件，里面是 Main.Web 的副本）；
/// parent/other：外部根，Other.Api（Web，引用 ../Lib）+ Lib + Other.sln。
/// </summary>
public sealed class MultiRootArchFixture : IDisposable
{
    public string Parent { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-multiarch-" + Guid.NewGuid().ToString("N"));
    public string Main => Path.Combine(Parent, "main");
    public string Other => Path.Combine(Parent, "other");

    public MultiRootArchFixture()
    {
        Write("main/package.json", """{ "name": "main-ui", "scripts": { "build": "vite build" }, "dependencies": { "vue": "3.5.0" } }""");
        Write("main/Main.sln", "");
        Write("main/Main.Web/Main.Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup><ProjectReference Include="..\..\other\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(Main, "nested", ".git"));
        Write("main/nested/Nested.sln", "");
        Write("main/nested/Nested.Svc/Nested.Svc.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"></Project>""");
        Write("main/wt2/.git", $"gitdir: {Path.Combine(Main, ".git", "worktrees", "wt2")}\n");
        Write("main/wt2/Main.Web/Main.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("main/wt2/Copy.sln", "");
        Write("other/Other.sln", "");
        Write("other/Other.Api/Other.Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """);
        Write("other/Other.Api/Program.cs", "var x = 1;\n");
        Write("other/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
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

public class ProjectRootsTests : IDisposable
{
    private readonly MultiRootArchFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Classify_GitDirIsClone_GitFileByGitdir()
    {
        Assert.Equal(NestedRepoKind.Clone, ProjectRoots.Classify(Path.Combine(_fx.Main, "nested")));
        Assert.Equal(NestedRepoKind.Worktree, ProjectRoots.Classify(Path.Combine(_fx.Main, "wt2")));
        Assert.Equal(NestedRepoKind.None, ProjectRoots.Classify(Path.Combine(_fx.Main, "Main.Web")));
        _fx.Write("main/sub/.git", "gitdir: ../.git/modules/sub\n");
        Assert.Equal(NestedRepoKind.Submodule, ProjectRoots.Classify(Path.Combine(_fx.Main, "sub")));
        _fx.Write("main/odd/.git", "gitdir: D:/elsewhere/odd.git\n"); // --separate-git-dir 之类认不出的：按 worktree 跳过
        Assert.Equal(NestedRepoKind.Worktree, ProjectRoots.Classify(Path.Combine(_fx.Main, "odd")));
    }

    [Fact]
    public void Load_MergesCliAndConfigProjects_RelativeToMain_DedupesAndRecordsMissing()
    {
        _fx.Write("main/.codeatlas/config.json", """
            // 注释与尾逗号都允许
            { "projects": ["../other", "../other/", "../gone", "."], }
            """);
        var roots = ProjectRoots.Load(_fx.Main + Path.DirectorySeparatorChar, [_fx.Other]);

        Assert.Equal(_fx.Main, roots.Main);
        Assert.Equal([_fx.Other], roots.Externals);                       // CLI 与 config 的同一目录、结尾斜杠、主根本身都去掉
        Assert.Equal([Path.Combine(_fx.Parent, "gone")], roots.Missing);
        Assert.Equal(["../gone/"], roots.MissingPrefixes);
        Assert.True(roots.IsUnderMissingRoot("../gone/a.cs"));
        Assert.False(roots.IsUnderMissingRoot("../gone-not/a.cs"));
    }

    [Fact]
    public void Load_BrokenConfig_IsIgnored()
    {
        _fx.Write("main/.codeatlas/config.json", "{ not json");
        Assert.Empty(ProjectRoots.Load(_fx.Main, null).Externals);
    }

    [Fact]
    public void ToRelative_AcceptsFilesUnderAnyRoot_RejectsOthers()
    {
        var roots = ProjectRoots.Create(_fx.Main, [_fx.Other]);
        Assert.Equal("Main.Web/X.cs", roots.ToRelative(Path.Combine(_fx.Main, "Main.Web", "X.cs")));
        Assert.Equal("../other/Other.Api/Program.cs", roots.ToRelative(Path.Combine(_fx.Other, "Other.Api", "Program.cs")));
        Assert.Null(roots.ToRelative(Path.Combine(_fx.Parent, "elsewhere", "Y.cs")));
        Assert.Null(roots.ToRelative(_fx.Other + "-old" + Path.DirectorySeparatorChar + "Z.cs")); // 前缀相同但不是子目录
    }

    [Fact]
    public void Repositories_ListRootsAndIncludedNestedRepos_WithPrefixes()
    {
        var repos = ProjectRoots.Create(_fx.Main, [_fx.Other]).Repositories();
        Assert.Equal([(_fx.Main, ""), (Path.Combine(_fx.Main, "nested"), "nested/"), (_fx.Other, "../other/")], repos.Select(r => (r.FullPath, r.Prefix)));
        Assert.Equal([false, true, false], repos.Select(r => r.IsNested));
        Assert.Equal([(_fx.Main, "")], ProjectRoots.Single(_fx.Main, includeNested: false).Repositories().Select(r => (r.FullPath, r.Prefix)));
    }

    [Fact]
    public void DiscoverSolutions_OnePerRepo_SkipsWorktreesAndOtherRoots()
    {
        Assert.Equal(
            [Path.Combine(_fx.Main, "Main.sln"), Path.Combine(_fx.Main, "nested", "Nested.sln"), Path.Combine(_fx.Other, "Other.sln")],
            RoslynRunner.DiscoverSolutions(ProjectRoots.Create(_fx.Main, [_fx.Other])));
        Assert.Equal([Path.Combine(_fx.Main, "Main.sln")], RoslynRunner.DiscoverSolutions(ProjectRoots.Single(_fx.Main, includeNested: false)));
    }

    [Fact]
    public void DiscoverSolution_SingleRepo_DoesNotPickASolutionInsideANestedClone()
    {
        File.Delete(Path.Combine(_fx.Main, "Main.sln"));
        Assert.Null(RoslynRunner.DiscoverSolution(_fx.Main)); // 只剩 nested/Nested.sln（独立仓库）与 wt2/Copy.sln（worktree）
    }
}

public class MultiRootInventoryTests : IDisposable
{
    private readonly MultiRootArchFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Inventory_SingleRoot_IncludesNestedClone_SkipsWorktree_AndDropsRefsOutsideRoots()
    {
        var inv = ProjectInventory.Load(ProjectRoots.Single(_fx.Main));
        Assert.Equal(["Main.Web", "nested/Nested.Svc"], inv.Projects.Select(p => p.Dir));
        Assert.Empty(inv.Projects.Single(p => p.Dir == "Main.Web").ProjectRefs); // ../../other 不在任何根里

        Assert.Equal(["Main.Web"], ProjectInventory.Load(ProjectRoots.Single(_fx.Main, includeNested: false)).Projects.Select(p => p.Dir));
    }

    [Fact]
    public void Inventory_ExternalRoot_ProjectsAndRefsAreRelativeToMain()
    {
        var inv = ProjectInventory.Load(ProjectRoots.Create(_fx.Main, [_fx.Other]));

        Assert.Equal(["../other/Lib", "../other/Other.Api", "Main.Web", "nested/Nested.Svc"], inv.Projects.Select(p => p.Dir));
        Assert.Equal(["../other/Lib"], inv.Projects.Single(p => p.Dir == "Main.Web").ProjectRefs);
        Assert.Equal(["../other/Lib"], inv.Projects.Single(p => p.Dir == "../other/Other.Api").ProjectRefs);
        Assert.Equal("../other/Other.Api/Other.Api.csproj", inv.Projects.Single(p => p.Dir == "../other/Other.Api").File);
        Assert.Contains("../other/Other.Api/Program.cs", inv.SourceFiles);
        Assert.Equal(("", "main-ui"), (inv.Frontends.Single().Dir, inv.Frontends.Single().Name));

        var containers = ContainerDiscovery.Discover(inv);
        Assert.Equal(["../other/Other.Api", "Main.Web", "main-ui", "nested/Nested.Svc"], containers.Select(c => c.Key));
        Assert.Equal(["../other/Lib", "Main.Web"], containers.Single(c => c.Key == "Main.Web").Members);
        Assert.Equal(["../other/Lib", "../other/Other.Api"], containers.Single(c => c.Key == "../other/Other.Api").Members);

        var ctx = new RuleContext(inv, containers);
        Assert.Equal(["var x = 1;"], ctx.Lines("../other/Other.Api/Program.cs"));
        Assert.Equal(["../other/Other.Api"], ctx.ContainersOf("../other/Other.Api/Program.cs").Select(c => c.Key));
    }

    [Fact]
    public void Scanner_ExternalRoot_AddsItsContainers()
    {
        var model = ArchitectureScanner.Scan(ProjectRoots.Create(_fx.Main, [_fx.Other]));
        Assert.Equal(["../other/Other.Api", "Main.Web", "main-ui", "nested/Nested.Svc"], model.Nodes.Where(n => n.Kind == ArchNodeKind.Container).Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Inventory_ExternalRootContainingMain_ListsEachProjectOnce()
    {
        // 外部根是 parent（包含主根 main）：main 下的项目只出现一次，路径仍是短的相对路径
        var inv = ProjectInventory.Load(ProjectRoots.Create(_fx.Main, [_fx.Parent]));
        Assert.Equal(["../other/Lib", "../other/Other.Api", "Main.Web", "nested/Nested.Svc"], inv.Projects.Select(p => p.Dir));
    }
}

public class MultiRootGitTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "codeatlas-multigit-" + Guid.NewGuid().ToString("N"));

    private static bool Git(string dir, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private bool Repo(string rel, string file)
    {
        var dir = Path.Combine(_parent, rel);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), "x\n");
        return Git(dir, "init", "-q") && Git(dir, "add", file)
            && Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
    }

    [Fact]
    public void Run_OverRoots_PrefixesNestedAndExternalPaths()
    {
        if (!Repo("main", "m.cs") || !Repo(Path.Combine("main", "nested"), "n.cs") || !Repo("other", "o.cs")) return; // 没有 git：跳过
        var r = GitHistory.Run(ProjectRoots.Create(Path.Combine(_parent, "main"), [Path.Combine(_parent, "other")]));
        Assert.Null(r.Error);
        Assert.Equal(["../other/o.cs", "m.cs", "nested/n.cs"], r.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(3, r.Commits);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_parent)) return;
        foreach (var f in Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git 对象是只读的
        Directory.Delete(_parent, recursive: true);
    }
}

public class RoslynApplierMultiSolutionTests
{
    [Fact]
    public void FileSeenInAnEarlierSolution_IsNotAppliedTwice()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("../other/A.cs", "csharp", "h", 1, FileStatus.Ok);
        w.WriteSymbols(a,
        [
            new ParsedSymbol(SymbolKind.Module, "A.cs", "../other/A.cs", null, 1, 1, 50, 1, null, null),
            new ParsedSymbol(SymbolKind.Class, "C", "Ns.C", "class C", 3, 1, 20, 2, 0, "public"),
        ], IndexSource.TreeSitter);

        var applier = new RoslynApplier(db);
        foreach (var r in new RoslynRecord[]
        {
            new FileRec("../other/A.cs"),
            new SymbolRec("../other/A.cs", "class", "C", "Ns.C", "class C", 3, 1, 20, 2, null, "public"),
            new DoneRec(1, 1, 0),
            // 第二个解决方案也包含这个文件（跨仓库 ProjectReference）：整文件跳过
            new FileRec("../other/A.cs"),
            new SymbolRec("../other/A.cs", "method", "Ghost", "Ns.C.Ghost()", "void Ghost()", 9, 5, 9, 30, "Ns.C", "public"),
        }) applier.Apply(r);
        var report = applier.Finish();

        Assert.Equal((1, 1, 0), (report.Files, report.SymbolsMerged, report.SymbolsAdded));
        Assert.Empty(new IndexReader(db).FindByName("Ghost"));
    }
}
