using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

/// <summary>HTTP 层：真实 tree-sitter 索引的小 TS 仓库（ServerFixture）上取影响范围。这个仓库没跑入口识别，入口汇总是空表。</summary>
public class ImpactApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    private async Task<long> IdOf(string query, string qualifiedName) =>
        (await C.GetFromJsonAsync<List<SymbolDto>>($"/api/search?q={Uri.EscapeDataString(query)}"))!.Single(s => s.QualifiedName == qualifiedName).Id;

    [Fact]
    public async Task Impact_ListsCallers_WithKindSitesExpandable_AndEntriesOnlyWhenAsked()
    {
        // helper ← Svc.find ← main
        var helper = await IdOf("helper", "src/util/format.ts:helper");
        var r = await C.GetAsync($"/api/symbols/{helper}/impact");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var impact = await r.Content.ReadFromJsonAsync<ImpactDto>();
        Assert.Equal(helper, impact!.Symbol.Id);
        var caller = Assert.Single(impact.Callers);
        Assert.Equal(("call", "Svc.find", 0.95), (caller.Kind, caller.Label, caller.Confidence));
        Assert.Equal("src/svc.ts:Svc.find", caller.Caller.QualifiedName);
        var site = Assert.Single(caller.Sites);
        Assert.Equal(3, site.Line);
        Assert.NotNull(site.Pos);
        Assert.True(caller.Expandable); // main 调了 find
        Assert.Empty(caller.AsEntry);
        Assert.Equal([], impact.Entries);
        Assert.False(impact.EntriesTruncated);
        Assert.Equal(0, impact.TestCallers);
        Assert.False(impact.Truncated);

        // 展开 find：再取它的影响范围（不要入口汇总）
        var find = await C.GetFromJsonAsync<ImpactDto>($"/api/symbols/{caller.Caller.Id}/impact?entries=false&tests=false");
        var main = Assert.Single(find!.Callers);
        Assert.Equal(("call", "main", 0.7, false), (main.Kind, main.Label, main.Confidence, main.Expandable));
        Assert.Null(find.Entries);
    }

    [Fact]
    public async Task Impact_OfType_ListsConstructorCallsAsNew()
    {
        var svc = await IdOf("Svc", "src/svc.ts:Svc");
        var impact = await C.GetFromJsonAsync<ImpactDto>($"/api/symbols/{svc}/impact?entries=false");
        Assert.Equal(["new:main"], impact!.Callers.Select(c => $"{c.Kind}:{c.Label}"));
    }

    [Fact]
    public async Task Impact_UnknownSymbol_Returns404()
    {
        var r = await C.GetAsync("/api/symbols/987654321/impact");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
