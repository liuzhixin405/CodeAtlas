using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests.Architecture;

public class GitHistoryTests
{
    // 5 个提交：a 与 b 一起改了 5 次（耦合），c 偶尔改；一个二进制文件；一个 31 文件的大提交只算修订不算耦合
    private static string Log()
    {
        var big = string.Join("\n", Enumerable.Range(0, 31).Select(i => $"1\t0\tgen/file{i:D2}.cs"));
        return $"""
            --a1--2026-01-10--Ann
            10	2	src/a.cs
            5	1	src/b.cs
            -	-	assets/logo.png

            --a2--2026-02-11--Ann
            3	3	src/a.cs
            1	1	src/b.cs

            --a3--2026-03-12--Bob
            2	0	src/a.cs
            2	0	src/b.cs
            7	7	src/c.cs

            --a4--2026-04-13--Bob
            1	1	src/a.cs
            1	1	src/b.cs

            --a5--2026-05-14--Cid
            1	1	src/a.cs
            1	1	src/b.cs
            {big}
            """;
    }

    [Fact]
    public void Parse_ComputesRevisionsChurnAgeAuthors_AndCouplingWithThresholds()
    {
        var r = GitHistory.Parse(Log());
        Assert.Equal(5, r.Commits);
        Assert.Equal(1, r.CommitsSkippedForCoupling); // 最后一个提交 33 个文件 > 30

        var a = r.Files.Single(f => f.Path == "src/a.cs");
        Assert.Equal((5, 17, 7, 3), (a.Revisions, a.Added, a.Deleted, a.Authors));
        Assert.Equal((new DateOnly(2026, 1, 10), new DateOnly(2026, 5, 14)), (a.FirstCommit, a.LastCommit));
        var logo = r.Files.Single(f => f.Path == "assets/logo.png");
        Assert.Equal((1, 0, 0), (logo.Revisions, logo.Added, logo.Deleted)); // 二进制 "-" 不计行数
        Assert.Equal(1, r.Files.Single(f => f.Path == "gen/file30.cs").Revisions);

        // a 与 b 在前 4 个提交里一起出现 4 次（第 5 个提交太大不算）→ shared 4 < 5，不保留
        Assert.DoesNotContain(r.Coupling, c => c.PathA == "src/a.cs" && c.PathB == "src/b.cs");
    }

    [Fact]
    public void Parse_KeepsCoupling_WhenSharedAtLeast5AndDegreeAtLeast30Percent()
    {
        var log = string.Join("\n", Enumerable.Range(1, 6).Select(i => $"--h{i}--2026-0{i}-01--Ann\n1\t1\tsrc/a.cs\n1\t1\tsrc/b.cs\n"))
                  + string.Join("\n", Enumerable.Range(1, 14).Select(i => $"--k{i}--2026-07-{i:D2}--Ann\n1\t1\tsrc/a.cs\n"));
        var r = GitHistory.Parse(log);
        // a 改了 20 次，b 改了 6 次，一起 6 次：degree = 6 / 13 = 0.46 ≥ 0.3 → 保留
        var c = Assert.Single(r.Coupling);
        Assert.Equal(("src/a.cs", "src/b.cs", 6, 0.462), (c.PathA, c.PathB, c.Shared, c.Degree));

        // 把 a 再改 30 次：degree = 6 / 28 = 0.21 → 不保留
        var more = log + string.Join("\n", Enumerable.Range(1, 30).Select(i => $"--m{i}--2026-08-01--Ann\n1\t1\tsrc/a.cs\n"));
        Assert.Empty(GitHistory.Parse(more).Coupling);
    }

    [Fact]
    public void Parse_PrefixesPathsForNestedRepos()
    {
        var r = GitHistory.Parse("--x--2026-01-01--A\n1\t1\tsrc/p.cs\n", "sub/");
        Assert.Equal("sub/src/p.cs", Assert.Single(r.Files).Path);
    }

    [Fact]
    public void Storage_RoundTrips()
    {
        using var db = IndexDatabase.OpenInMemory();
        var r = GitHistory.Parse(Log());
        GitStorage.Replace(db, r with { Coupling = [new GitCoupling("src/a.cs", "src/b.cs", 9, 0.75)] });
        var read = GitStorage.Read(db);
        Assert.Equal(r.Files.Count, read.Files.Count);
        Assert.Equal(5, read.Files.Single(f => f.Path == "src/a.cs").Revisions);
        Assert.Equal(new DateOnly(2026, 5, 14), read.Files.Single(f => f.Path == "src/a.cs").LastCommit);
        Assert.Equal((9, 0.75), (read.Coupling[0].Shared, read.Coupling[0].Degree));
        Assert.Equal((5, 1), (read.Commits, read.CommitsSkippedForCoupling));
    }

    [Fact]
    public void Run_OnThisRepo_ReturnsCommits_OrAClearErrorWithoutGit()
    {
        // 当前仓库（CodeAtlas）本身是 git 仓库：能拿到提交；拿不到就必须说清原因
        var root = FindRepoRoot();
        var r = GitHistory.Run(root, nestedRepoDirs: [], timeout: TimeSpan.FromMinutes(1));
        if (r.Error is not null) { Assert.Contains("git", r.Error); return; }
        Assert.True(r.Commits > 10, $"commits={r.Commits}");
        Assert.Contains(r.Files, f => f.Path == "README.md" || f.Path.EndsWith(".cs", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git"))) dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }
}
