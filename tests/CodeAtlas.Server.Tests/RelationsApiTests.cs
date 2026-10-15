using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

/// <summary>HTTP 层：真实 tree-sitter 索引的小 TS 仓库（ServerFixture）上取模块关系。</summary>
public class RelationsApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private async Task<long> IdOf(string query, string qualifiedName) =>
        (await C.GetFromJsonAsync<List<SymbolDto>>($"/api/search?q={Uri.EscapeDataString(query)}"))!.Single(s => s.QualifiedName == qualifiedName).Id;

    private static string Shape(RelationsDto r)
    {
        var name = r.Nodes.ToDictionary(n => n.Unit.Id, n => n.Unit.Name);
        return string.Join(" | ", r.Edges.Select(e => $"{name[e.From]}→{name[e.To]} {string.Join(",", e.Kinds.Select(k => $"{k.Key}{k.Value}"))} {e.Weight}"));
    }

    [Fact]
    public async Task Relations_TopLevelFunction_CentersOnModule_AndSummarizesNewAndCall()
    {
        // main(): new Svc().find(1); fetch('x'); util.helper2() —— fetch 是外部调用不画，helper2 是 0.3 的多候选不算
        var main = await IdOf("main", "src/app.ts:main");
        var r = await C.GetAsync($"/api/symbols/{main}/relations");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rel = (await r.Content.ReadFromJsonAsync<RelationsDto>())!;
        Assert.Equal("module", rel.Focus.Kind);
        Assert.Equal("src/app.ts", rel.Focus.Path);
        Assert.False(rel.Truncated);
        Assert.Equal(["app.ts:0", "Svc:1"], rel.Nodes.Select(n => $"{n.Unit.Name}:{n.Distance}"));
        Assert.Equal("app.ts→Svc call1,new1 2", Shape(rel));
        Assert.Equal(["main→Svc new", "main→Svc.find call"], rel.Edges[0].Samples.Select(s => $"{s.FromLabel}→{s.ToLabel} {s.Kind}")); // 置信度降序：new 解析得比 find 准
        Assert.All(rel.Edges[0].Samples, s => Assert.Equal(("src/app.ts", 2), (s.File, s.Line)));
        // 没有容器：退到文件所在目录
        Assert.Equal(["src:src:0"], rel.Components.Select(c => $"{c.Key}:{c.Label}:{c.Order}"));
    }

    [Fact]
    public async Task Relations_Depth2_AddsSecondRing_AndOrdersComponentsByDependency()
    {
        var main = await IdOf("main", "src/app.ts:main");
        var rel = (await C.GetFromJsonAsync<RelationsDto>($"/api/symbols/{main}/relations?depth=2&tests=false"))!;
        Assert.Equal(["app.ts:0", "Svc:1", "format.ts:2"], rel.Nodes.Select(n => $"{n.Unit.Name}:{n.Distance}"));
        Assert.Equal("app.ts→Svc call1,new1 2 | Svc→format.ts call1 1", Shape(rel));
        // src 依赖 src/util：被依赖的在前
        Assert.Equal(["src/util:util:0", "src:src:1"], rel.Components.Select(c => $"{c.Key}:{c.Label}:{c.Order}"));
        Assert.Equal("src/util", rel.Nodes.Single(n => n.Unit.Name == "format.ts").ComponentKey);
    }

    [Fact]
    public async Task Relations_UnknownSymbol_Returns404()
    {
        var r = await C.GetAsync("/api/symbols/987654321/relations?depth=2");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
