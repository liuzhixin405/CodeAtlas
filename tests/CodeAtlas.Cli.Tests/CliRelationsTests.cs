namespace CodeAtlas.Cli.Tests;

/// <summary>query --relations：按组件（依赖序）列单元，再列汇总边与样例。</summary>
public class CliRelationsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-rel-" + Guid.NewGuid().ToString("N"));

    public CliRelationsTests()
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
    public void Query_Relations_GroupsUnitsByComponent_ThenListsEdgesWithSamples()
    {
        Assert.Equal(0, Run("index", _repo).Code);

        // 光标在 Svc.find 上：中心是 Svc；app.ts 调它、它调 format.ts，被依赖的 util 组件排在前
        var r = Run("query", _repo, "src/svc.ts:Svc.find", "--relations");
        Assert.Equal(0, r.Code);
        Assert.Equal(
            [
                "中心：src/svc.ts:Svc  [class]  src/svc.ts:2",
                "depth 1：3 个单元、2 条边",
                "组件（依赖序，被依赖的在前）：",
                "  1. util  src/util",
                "         format.ts  [module]  d1",
                "  2. src  src",
                "       * Svc  [class]  d0",
                "         app.ts  [module]  d1",
                "边：",
            ],
            Lines(r.Out).Take(9));
        Assert.Contains("  app.ts → Svc  call 1 · new 1  （权重 2）", Lines(r.Out));
        Assert.Contains("  Svc → format.ts  call 1  （权重 1）", Lines(r.Out));
        Assert.Contains("      Svc.find → helper  call  src/svc.ts:3  0.95", Lines(r.Out));
        Assert.DoesNotContain("fetch", r.Out); // 外部调用不画
    }

    private static List<string> Lines(string s) => s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
