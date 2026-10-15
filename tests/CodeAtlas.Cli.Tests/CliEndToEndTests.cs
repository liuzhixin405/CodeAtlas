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
        // helper 是 import { helper } from './util/format' 绑定的名字：按导出解析 0.95（"0.0" 格式四舍五入打印成 1.0）；以前按 import 范围同名 0.7
        Assert.Contains("→ calls  src/util/format.ts:helper  (0.95)", query.Out);
        Assert.Contains("← calls  src/app.ts:main  (0.70)", query.Out);
    }

    [Fact]
    public void Query_Outline_PrintsStepsInOrder_AndDepthExpandsInPlace()
    {
        Run("index", _repo);
        var flat = Run("query", _repo, "src/app.ts:main", "--outline");
        Assert.Equal(0, flat.Code);
        var lines = flat.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Matches(@"^1\.\s+new\s+Svc .*@ L2$", lines[0]);
        Assert.Matches(@"^2\. \+ call\s+Svc\.find .*@ L2$", lines[1]); // + = 能展开
        Assert.Matches(@"^3\.\s+external\s+fetch .*@ L2$", lines[2]);

        var deep = Run("query", _repo, "src/app.ts:main", "--outline", "--depth", "2", "--no-externals");
        Assert.Equal(0, deep.Code);
        Assert.DoesNotContain("fetch", deep.Out);
        Assert.Contains("\n    1.   call      helper", deep.Out.Replace("\r", "")); // find 就地展开一层（多缩进一级）
    }

    [Fact]
    public void Query_Impact_PrintsEntriesByContainer_ThenCallerTree_WithRecursionMark()
    {
        // Express 服务（package.json 依赖 express + Dockerfile → node-service 容器）：POST /api/orders → createOrder → save → helper；save ↔ retry 互相调用
        var api = Path.Combine(_repo, "api");
        Directory.CreateDirectory(api);
        File.WriteAllText(Path.Combine(api, "package.json"), "{ \"name\": \"api\", \"dependencies\": { \"express\": \"4.19.0\" } }");
        File.WriteAllText(Path.Combine(api, "Dockerfile"), "FROM node:20\nEXPOSE 3000\n");
        File.WriteAllText(Path.Combine(api, "app.js"), string.Join("\n",
            "const express = require('express');",
            "const app = express();",
            "app.post('/api/orders', createOrder);",
            "function createOrder(req, res) { save(req.body); }",
            "function save(o) { audit(o); retry(o); }",
            "function retry(o) { if (o) save(o); }",
            "function audit(o) { return o; }",
            "app.listen(3000);") + "\n");
        Assert.Equal(0, Run("index", _repo).Code);

        var r = Run("query", _repo, "audit", "--impact");
        Assert.Equal(0, r.Code);
        Assert.Equal(
            [
                "api/app.js:audit  [function]  api/app.js:7",
                "  入口（1）",
                "    [api] HTTP POST /api/orders  ← 深度 2",
                "  调用方",
                "    ← save  call 0.9  @ L5",
                "      ← createOrder  call 0.9  @ L4  ⇐ HTTP POST /api/orders",
                "      ← retry  call 0.9  @ L6[cond]",
                "        ← save  call 0.9  @ L5  ↺",
            ],
            r.Out.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries));

        // --depth 1：只有直接调用方，能往上展开的标 +
        var flat = Run("query", _repo, "audit", "--impact", "--depth", "1");
        Assert.Contains("\n    ← save  call 0.9  @ L5  +\n", flat.Out.Replace("\r", ""));
        Assert.DoesNotContain("createOrder  call", flat.Out);
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
        var selfEdgeLines = query.Out.Split('\n').Count(l => l.Contains("src/rec.ts:rec  (0.90)"));
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
