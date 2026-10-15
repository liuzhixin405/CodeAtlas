using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

/// <summary>HTTP 层：真实 tree-sitter 索引的小 TS 仓库（ServerFixture）上取大纲。</summary>
public class OutlineApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private async Task<long> IdOf(string query, string qualifiedName) =>
        (await C.GetFromJsonAsync<List<SymbolDto>>($"/api/search?q={Uri.EscapeDataString(query)}"))!.Single(s => s.QualifiedName == qualifiedName).Id;

    [Fact]
    public async Task Outline_ListsCallsInSourceOrder_WithKindsLabelsAndExpandable()
    {
        // main(): new Svc().find(1); fetch('x'); util.helper2();
        var main = await IdOf("main", "src/app.ts:main");
        var r = await C.GetAsync($"/api/symbols/{main}/outline");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var o = await r.Content.ReadFromJsonAsync<OutlineDto>();
        Assert.Equal(main, o!.Symbol.Id);
        Assert.False(o.Truncated);
        Assert.Equal(["1:new:Svc", "2:call:Svc.find", "3:external:fetch", "4:ambiguous:2 个可能目标：helper2"],
            o.Steps.Select(s => $"{s.Order}:{s.Kind}:{s.Label}"));
        Assert.All(o.Steps, s => Assert.Equal(2, s.Line));
        Assert.True(o.Steps[1].Expandable);   // find 里调了 helper
        Assert.False(o.Steps[2].Expandable);  // 外部
        Assert.Equal(["Dup1.helper2", "Dup2.helper2"], o.Steps[3].Children.Select(c => c.Label).Order());
        Assert.All(o.Steps[3].Children, c => Assert.Equal("call", c.Kind));

        // 展开 find：再取它的大纲
        var find = await C.GetFromJsonAsync<OutlineDto>($"/api/symbols/{o.Steps[1].Target!.Id}/outline");
        Assert.Equal(["1:call:helper"], find!.Steps.Select(s => $"{s.Order}:{s.Kind}:{s.Label}"));
        Assert.True(find.Steps[0].Expandable); // helper 调了外部的 String()：要外部调用时能展开……
        var findNoExt = await C.GetFromJsonAsync<OutlineDto>($"/api/symbols/{o.Steps[1].Target!.Id}/outline?externals=false");
        Assert.False(Assert.Single(findNoExt!.Steps).Expandable); // ……不要外部调用时展开是空的，不能展开
    }

    [Fact]
    public async Task Outline_ExternalsFalse_DropsExternalSteps()
    {
        var main = await IdOf("main", "src/app.ts:main");
        var o = await C.GetFromJsonAsync<OutlineDto>($"/api/symbols/{main}/outline?externals=false&tests=false");
        Assert.DoesNotContain(o!.Steps, s => s.Kind == "external");
        Assert.Equal([1, 2, 3], o.Steps.Select(s => s.Order));
    }

    [Fact]
    public async Task Outline_UnknownSymbol_Returns404()
    {
        var r = await C.GetAsync("/api/symbols/987654321/outline");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
