namespace CodeAtlas.Server.Tests;

/// <summary>主根 main（前端）+ 外部根 other（ASP.NET 控制器），走 IndexService 全流程：索引、容器图、入口、按绝对路径定位符号、增量不误删。</summary>
public sealed class MultiRootServerTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "codeatlas-srvmulti-" + Guid.NewGuid().ToString("N"));
    private string Main => Path.Combine(_parent, "main");
    private string Other => Path.Combine(_parent, "other");

    public MultiRootServerTests()
    {
        Write("main/package.json", """{ "name": "main-ui", "scripts": { "build": "vite build" }, "dependencies": { "vue": "3.5.0" } }""");
        Write("main/src/app.ts", "export function boot(): void { fetch('/api/hello'); }\n");
        Write("other/Other.Api/Other.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("other/Other.Api/HelloController.cs", """
            namespace Other.Api;

            [ApiController]
            [Route("api/hello")]
            public class HelloController : ControllerBase
            {
                [HttpGet]
                public string Get() => "hi";
            }
            """);
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_parent, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private IndexService NewService() =>
        new(new ServerOptions(Main, ServerOptions.DefaultDbPath(Main)) { Projects = [Other], Roslyn = false });

    private static async Task<IndexReportDto> Index(IndexService s, bool full)
    {
        Assert.True(s.TryStartIndex(full, roslyn: false));
        await s.WaitForIndexAsync();
        Assert.Null(s.Status().Error);
        return s.Status().LastReport!;
    }

    [Fact]
    public async Task ExternalRoot_IsIndexed_AppearsAsContainerWithEntries_AndSymbolAtAcceptsItsAbsolutePaths()
    {
        using var s = NewService();
        var first = await Index(s, full: true);
        Assert.Equal(2, first.FilesScanned); // main/src/app.ts + other/Other.Api/HelloController.cs（csproj / package.json 不是源码）

        Assert.Equal(["../other/Other.Api/HelloController.cs", "src/app.ts"], s.FileHashes().Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["../other/Other.Api", "main-ui"], s.Architecture().Nodes.Where(n => n.Kind == "container").Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal));

        var entry = Assert.Single(s.Entries("../other/Other.Api", "http", null));
        Assert.Equal(("Get", "../other/Other.Api/HelloController.cs"), (entry.Name, entry.File));

        // 扩展发来的是绝对路径：落在外部根下也要能定位（第 8 行 "    public string Get()" 的 G 在第 19 列）
        var at = s.SymbolAt(Path.Combine(Other, "Other.Api", "HelloController.cs"), 8, 19);
        Assert.Equal("Other.Api.HelloController.Get", at!.QualifiedName);
        Assert.Equal("../other/Other.Api/HelloController.cs", at.Path);
        Assert.Null(s.SymbolAt(Path.Combine(_parent, "elsewhere", "x.cs"), 1, 1));
    }

    [Fact]
    public async Task SecondIncrementalRun_KeepsExternalFiles()
    {
        using var s = NewService();
        await Index(s, full: true);
        var second = await Index(s, full: false);
        Assert.Equal((2, 0, 2, 0), (second.FilesScanned, second.FilesParsed, second.FilesUnchanged, second.FilesRemoved));
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }
}
