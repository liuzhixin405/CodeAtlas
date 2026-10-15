using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

/// <summary>HTTP 层：真实 tree-sitter 索引的小 TS 仓库（ServerFixture）上取整个文件的调用计数。</summary>
public class LensApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private Task<FileLensDto?> Lens(string path) => C.GetFromJsonAsync<FileLensDto>($"/api/files/lens?path={Uri.EscapeDataString(path)}");

    [Fact]
    public async Task Lens_RelativePath_CountsCallersAndCallees()
    {
        var r = await C.GetAsync("/api/files/lens?path=src/svc.ts");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var lens = await r.Content.ReadFromJsonAsync<FileLensDto>();
        Assert.Equal("src/svc.ts", lens!.Path);
        Assert.False(lens.Stale);
        var find = Assert.Single(lens.Symbols);   // 类不列，只列方法
        Assert.Equal("find", find.Symbol.Name);
        Assert.Equal("src/svc.ts", find.Symbol.Path);
        Assert.Equal(1, find.Callers);            // main
        Assert.Equal(1, find.CallSites);
        Assert.Equal(0, find.TestCallers);
        Assert.Equal(1, find.Callees);            // helper
        Assert.False(find.IsEntry);

        var helper = Assert.Single((await Lens("src/util/format.ts"))!.Symbols);
        Assert.Equal(1, helper.Callers);          // find
        Assert.Equal(1, helper.Callees);          // String()（框架调用也算）
    }

    [Fact]
    public async Task Lens_Callees_EqualOutlineEndpointSteps()
    {
        var lens = await Lens("src/app.ts");
        var main = Assert.Single(lens!.Symbols, s => s.Symbol.Name == "main");
        foreach (var s in lens.Symbols)
        {
            var outline = await C.GetFromJsonAsync<OutlineDto>($"/api/symbols/{s.Symbol.Id}/outline");
            Assert.Equal(outline!.Steps.Count, s.Callees);
        }
        Assert.Equal(4, main.Callees); // new Svc | find | fetch | 2 个可能目标：helper2
    }

    [Fact]
    public async Task Lens_AbsoluteAndSlashVariants_SameAsRelative()
    {
        var relative = await Lens("src/svc.ts");
        foreach (var path in new[] { Path.Combine(fx.Repo, "src", "svc.ts"), Path.Combine(fx.Repo, "src", "svc.ts").Replace('\\', '/'), @"src\svc.ts" })
        {
            var lens = await Lens(path);
            Assert.Equal("src/svc.ts", lens!.Path);
            Assert.Equal(relative!.Symbols.Select(s => (s.Symbol.Id, s.Callers, s.Callees)), lens.Symbols.Select(s => (s.Symbol.Id, s.Callers, s.Callees)));
        }
    }

    [Fact]
    public async Task Lens_NotIndexed_ReturnsEmptySymbols()
    {
        var missing = await C.GetAsync("/api/files/lens?path=src/nope.ts");
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode); // 不是 404
        var lens = await missing.Content.ReadFromJsonAsync<FileLensDto>();
        Assert.Empty(lens!.Symbols);
        Assert.False(lens.Stale);

        var outside = await Lens(Path.Combine(Path.GetTempPath(), "not-under-repo", "x.ts"));
        Assert.Empty(outside!.Symbols);
    }

    [Fact]
    public async Task Lens_FileEditedAfterIndex_IsStale()
    {
        Assert.False((await Lens("src/dup2.ts"))!.Stale);
        await File.AppendAllTextAsync(Path.Combine(fx.Repo, "src", "dup2.ts"), "// edited\n");
        var lens = await Lens("src/dup2.ts");
        Assert.True(lens!.Stale);
        Assert.Single(lens.Symbols); // 计数照给
    }
}
