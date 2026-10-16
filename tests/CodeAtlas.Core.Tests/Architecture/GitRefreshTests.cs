using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Tests.Architecture;

/// <summary>GitStorage.RefreshIfChanged：各仓库 ref 没变就跳过 git log；工作区改动不影响统计；新提交、新分支、嵌套仓库的提交都会重算。没有 git 时跳过。</summary>
public sealed class GitRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codeatlas-gitrefresh-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private ProjectRoots Roots => ProjectRoots.Single(_root);

    private bool Git(string dir, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private bool Commit(string dir, string file, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
        return Git(dir, "add", file) && Git(dir, "commit", "-q", "-m", "c " + file);
    }

    private bool InitRepo(string dir) { Directory.CreateDirectory(dir); return Git(dir, "init", "-q", "-b", "main"); }

    [Fact]
    public void SkipsWhenRefsUnchanged_IgnoresWorkingTree_RerunsOnCommitBranchAndNestedRepo()
    {
        var nested = Path.Combine(_root, "nested");
        if (!InitRepo(_root) || !Commit(_root, "a.cs", "a\n") || !InitRepo(nested) || !Commit(nested, "n.cs", "n\n")) return; // 没有 git：跳过

        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
        var first = GitStorage.Read(_db);
        Assert.Equal(2, first.Commits);
        Assert.False(GitStorage.RefreshIfChanged(_db, Roots));

        // 工作区里改文件、加未跟踪文件：ref 没变，统计也确实不变
        File.WriteAllText(Path.Combine(_root, "a.cs"), "changed but not committed\n");
        File.WriteAllText(Path.Combine(_root, "new.cs"), "untracked\n");
        Assert.False(GitStorage.RefreshIfChanged(_db, Roots));
        Assert.Equal(Stats(first), Stats(GitHistory.Run(Roots)));

        // 新提交
        Assert.True(Commit(_root, "b.cs", "b\n"));
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
        Assert.Equal(3, GitStorage.Read(_db).Commits);
        Assert.False(GitStorage.RefreshIfChanged(_db, Roots));

        // 别的分支上的提交（HEAD 没变，但 git log --all 看得到）
        Assert.True(Git(_root, "branch", "side") && Git(_root, "checkout", "-q", "side") && Commit(_root, "c.cs", "c\n") && Git(_root, "checkout", "-q", "main"));
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
        Assert.Equal(4, GitStorage.Read(_db).Commits);

        // 嵌套仓库里的提交
        Assert.True(Commit(nested, "m.cs", "m\n"));
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
        Assert.Contains(GitStorage.Read(_db).Files, f => f.Path == "nested/m.cs");
        Assert.Equal(Stats(GitStorage.Read(_db)), Stats(GitHistory.Run(Roots)));

        // 别处直接整表替换（没有指纹）：下次一定重算
        GitStorage.Replace(_db, GitHistory.Run(Roots));
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
    }

    [Fact]
    public void NotARepository_StillRuns_AndRecordsTheError()
    {
        Directory.CreateDirectory(_root);
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots));
        Assert.NotNull(GitStorage.Read(_db).Error);
        Assert.True(GitStorage.RefreshIfChanged(_db, Roots)); // 出错的结果不记指纹
    }

    private static List<string> Stats(GitHistoryResult r) =>
        r.Files.Select(f => $"{f.Path}|{f.Revisions}|{f.Added}|{f.Deleted}|{f.FirstCommit}|{f.LastCommit}|{f.Authors}").Order(StringComparer.Ordinal)
            .Concat(r.Coupling.Select(c => $"{c.PathA}|{c.PathB}|{c.Shared}|{c.Degree}")).Append($"commits {r.Commits}").ToList();

    public void Dispose()
    {
        _db.Dispose();
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); // git 对象是只读的
        Directory.Delete(_root, recursive: true);
    }
}
