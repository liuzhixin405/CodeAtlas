using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Tests;

/// <summary>parent/main（主根，含独立克隆 clone/）+ parent/other（外部根）：--project、config.json、--no-nested 与增量。</summary>
public class CliMultiRootTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "codeatlas-climulti-" + Guid.NewGuid().ToString("N"));
    private string Main => Path.Combine(_parent, "main");
    private string Other => Path.Combine(_parent, "other");

    public CliMultiRootTests()
    {
        Write("main/src/app.ts", "import { helper } from '../../other/lib/format';\nexport function main(): void { helper(1); }\n");
        Directory.CreateDirectory(Path.Combine(Main, "clone", ".git"));
        Write("main/clone/c.ts", "export function fromClone(): void {}\n");
        Write("other/lib/format.ts", "export function helper(id: number): string { return String(id); }\n");
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_parent, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private List<string> IndexedPaths()
    {
        using var db = IndexDatabase.Open(Path.Combine(Main, ".codeatlas", "index.db"));
        return new IndexReader(db).GetAllFiles().Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Index_WithProject_StoresExternalFilesRelativeToMain_SecondRunKeepsThem()
    {
        var first = Run("index", Main, "--project", Other, "--roslyn", "off", "--no-git");
        Assert.Equal(0, first.Code);
        Assert.Contains("files: 3  parsed: 3", first.Out);
        Assert.Equal(["../other/lib/format.ts", "clone/c.ts", "src/app.ts"], IndexedPaths());

        var second = Run("index", Main, "--project", Other, "--roslyn", "off", "--no-git");
        Assert.Contains("files: 3  parsed: 0  unchanged: 3  skipped: 0  removed: 0", second.Out);

        // 外部根里的符号能被 query 到，路径带 ../other 前缀
        var query = Run("query", Main, "helper");
        Assert.Equal(0, query.Code);
        Assert.Contains("../other/lib/format.ts:helper", query.Out);
    }

    [Fact]
    public void Index_ConfigJsonProjects_AndNoNested()
    {
        Write("main/.codeatlas/config.json", """{ "projects": ["../other"] }""");
        var r = Run("index", Main, "--no-nested", "--roslyn", "off", "--no-git");
        Assert.Equal(0, r.Code);
        Assert.Equal(["../other/lib/format.ts", "src/app.ts"], IndexedPaths()); // config.json 的外部根进来了，clone/ 被 --no-nested 关掉
    }

    [Fact]
    public void Index_MissingProject_WarnsAndContinues()
    {
        var r = Run("index", Main, "--project", Path.Combine(_parent, "gone"), "--roslyn", "off", "--no-git");
        Assert.Equal(0, r.Code);
        Assert.Contains("外部项目根不存在", r.Err);
        Assert.Equal(["clone/c.ts", "src/app.ts"], IndexedPaths());
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }
}
