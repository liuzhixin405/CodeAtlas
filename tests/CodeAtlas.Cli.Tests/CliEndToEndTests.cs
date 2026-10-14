namespace CodeAtlas.Cli.Tests;

public class CliEndToEndTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-" + Guid.NewGuid().ToString("N"));

    public CliEndToEndTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "util"));
        File.WriteAllText(Path.Combine(_repo, "src", "util", "format.ts"), "export function helper(id: number): string { return String(id); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "svc.ts"), "import { helper } from './util/format';\nexport class Svc {\n  find(id: number): string { return helper(id); }\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); fetch('x'); }\n");
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Index_ThenQuery_ShowsCallersAndCallees()
    {
        var index = Run("index", _repo);
        Assert.Equal(0, index.Code);
        Assert.Contains("files: 3", index.Out);
        Assert.True(File.Exists(Path.Combine(_repo, ".codeatlas", "index.db")));

        var query = Run("query", _repo, "find");
        Assert.Equal(0, query.Code);
        Assert.Contains("src/svc.ts:Svc.find", query.Out);
        Assert.Contains("→ calls  src/util/format.ts:helper  (0.7)", query.Out);
        Assert.Contains("← calls  src/app.ts:main  (0.7)", query.Out);
    }

    [Fact]
    public void Index_RoslynOn_WithoutWorker_FailsWithHint_AndBadValueIsUsageError()
    {
        var on = Run("index", _repo, "--roslyn", "on");
        Assert.Equal(1, on.Code);
        Assert.Contains("codeatlas-roslyn", on.Err);

        var bogus = Run("index", _repo, "--roslyn", "bogus");
        Assert.Equal(2, bogus.Code);
    }

    [Fact]
    public void Index_RoslynAuto_OnRepoWithoutSolution_StillSucceeds()
    {
        var r = Run("index", _repo, "--roslyn", "auto");
        Assert.Equal(0, r.Code);
        Assert.Contains("files: 3", r.Out);
    }

    [Fact]
    public void Query_WithDepth_ExpandsTransitively()
    {
        Run("index", _repo);
        var query = Run("query", _repo, "src/app.ts:main", "--out", "--depth", "2");
        Assert.Equal(0, query.Code);
        Assert.Contains("→ calls  src/svc.ts:Svc.find", query.Out);
        Assert.Contains("    → calls  src/util/format.ts:helper", query.Out);
        Assert.Contains("external:fetch", query.Out);
    }

    [Fact]
    public void Query_InvalidKinds_ReturnsTwo_AndListsValidKinds()
    {
        Run("index", _repo);
        var query = Run("query", _repo, "find", "--kinds", "calls,bogus");
        Assert.Equal(2, query.Code);
        Assert.Contains("bogus", query.Err);
        Assert.Contains("implements", query.Err);
    }

    [Fact]
    public void Query_RecursiveFunction_ShowsSelfEdgeOnce()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "rec.ts"), "export function rec(n: number): number { return n ? rec(n - 1) : 0; }\n");
        Run("index", _repo);
        var query = Run("query", _repo, "src/rec.ts:rec");
        Assert.Equal(0, query.Code);
        var selfEdgeLines = query.Out.Split('\n').Count(l => l.Contains("src/rec.ts:rec  (0.9)"));
        Assert.Equal(1, selfEdgeLines);
    }

    [Fact]
    public void Query_UnknownSymbol_ReturnsOne()
    {
        Run("index", _repo);
        var query = Run("query", _repo, "doesNotExist");
        Assert.Equal(1, query.Code);
        Assert.Contains("未找到", query.Err);
    }

    [Fact]
    public void Search_ListsMatches()
    {
        Run("index", _repo);
        var search = Run("search", _repo, "hel");
        Assert.Equal(0, search.Code);
        Assert.Contains("src/util/format.ts:helper", search.Out);
        Assert.Contains("[function]", search.Out);
    }

    [Fact]
    public void UnknownCommand_PrintsUsage_ReturnsTwo()
    {
        var r = Run("frobnicate");
        Assert.Equal(2, r.Code);
        Assert.Contains("codeatlas index", r.Err);
    }

    [Fact]
    public void Index_WarnsWhenGitignoreLacksCodeatlas()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "node_modules/\n");
        var r = Run("index", _repo);
        Assert.Contains(".codeatlas/", r.Err);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
