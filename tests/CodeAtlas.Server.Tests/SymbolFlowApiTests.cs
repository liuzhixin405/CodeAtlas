using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

/// <summary>HTTP 层：GET /api/symbols/{id}/flow 在真实 tree-sitter 索引的小 TS 仓库（ServerFixture，没有容器）上。</summary>
public class SymbolFlowApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private async Task<long> IdOf(string query, string qualifiedName) =>
        (await C.GetFromJsonAsync<List<SymbolDto>>($"/api/search?q={Uri.EscapeDataString(query)}"))!.Single(s => s.QualifiedName == qualifiedName).Id;

    [Fact]
    public async Task Flow_FromANonEntryMethod_OutsideAnyContainer()
    {
        // main(): new Svc().find(1); fetch('x'); util.helper2();（helper2 两个候选各 0.3，不到流程的 0.4）
        var main = await IdOf("main", "src/app.ts:main");
        var r = await C.GetAsync($"/api/symbols/{main}/flow");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var f = (await r.Content.ReadFromJsonAsync<FlowDto>())!;
        Assert.Equal((main, "main", (string?)null, "", (string?)null), (f.EntrySymbolId, f.EntryLabel, f.Route, f.ContainerKey, f.Note));
        Assert.Equal(new[] { ("Svc", "call"), ("Svc.find", "call"), ("fetch", "external-group") }, f.Root.Children.Select(c => (c.Label, c.Kind)));
        var find = f.Root.Children[1];
        Assert.Equal(("src/app.ts", (int?)2), (find.Path, find.Line));
        Assert.Equal("helper", Assert.Single(find.Children).Label);
        // 整库伪容器当 home：仓库里没有项目文件时只有一个成员（主根），参与者是 project 类
        Assert.Equal("project", f.Actors[0].Kind);

        var raw = await C.GetStringAsync($"/api/symbols/{main}/flow");
        Assert.Contains("\"path\":\"src/app.ts\"", raw);   // 可选字段 camelCase
        Assert.Contains("\"pos\":", raw);
    }

    [Fact]
    public async Task CacheKeys_KeepEntryOnlyAndAnySymbolFlowsApart_AndDepthIsHonoured()
    {
        var main = await IdOf("main", "src/app.ts:main");
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync($"/api/arch/flow?entry={main}")).StatusCode);   // 先缓存一个"不是入口"
        Assert.Equal(HttpStatusCode.OK, (await C.GetAsync($"/api/symbols/{main}/flow")).StatusCode);          // 任意符号的接口不受它影响
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync($"/api/arch/flow?entry={main}")).StatusCode);   // 反过来也不串
        var shallow = (await C.GetFromJsonAsync<FlowDto>($"/api/symbols/{main}/flow?depth=1"))!;
        Assert.All(shallow.Root.Children, c => Assert.Empty(c.Children));
        Assert.NotEmpty((await C.GetFromJsonAsync<FlowDto>($"/api/symbols/{main}/flow"))!.Root.Children[1].Children);
    }

    [Fact]
    public async Task Flow_OnATypeWithoutConstructor_ReturnsRootOnlyWithNote_UnknownSymbol404()
    {
        var svc = await IdOf("Svc", "src/svc.ts:Svc");
        var f = (await C.GetFromJsonAsync<FlowDto>($"/api/symbols/{svc}/flow"))!;
        Assert.Equal(("Svc", 1, 0), (f.Root.Label, f.Nodes, f.Root.Children.Count));
        Assert.Equal("Svc 是类型，没有显式构造函数，没有可展开的调用；在它的方法上打开时序图", f.Note);

        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/symbols/987654321/flow")).StatusCode);
    }
}
