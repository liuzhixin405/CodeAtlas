using System.Net;
using System.Net.Http.Json;

namespace CodeAtlas.Server.Tests;

public class ApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private HttpClient C => fx.Client;

    [Fact]
    public async Task Health_ReportsCounts()
    {
        var h = await C.GetFromJsonAsync<HealthDto>("/api/health");
        Assert.NotNull(h);
        Assert.Equal(5, h!.Files);
        Assert.True(h.Symbols >= 9, $"symbols={h.Symbols}");
        Assert.NotNull(h.IndexedAt);
        Assert.Equal(fx.Repo, h.Repo);
        Assert.Null(h.RoslynAt);             // 纯 TS 仓库没跑过 Roslyn
        Assert.Equal(0, h.RoslynStaleFiles); // 也没有待 Roslyn 处理的 C# 文件
    }

    [Fact]
    public async Task Search_ReturnsSymbolsWithPath()
    {
        var hits = await C.GetFromJsonAsync<List<SymbolDto>>("/api/search?q=hel");
        Assert.NotNull(hits);
        var helper = Assert.Single(hits!, s => s.QualifiedName == "src/util/format.ts:helper");
        Assert.Equal("src/util/format.ts", helper.Path);
        Assert.Equal("function", helper.Kind);
    }

    [Fact]
    public async Task Cors_AllowsWebviewAndBrowserOrigins_IncludingPreflight()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        req.Headers.Add("Origin", "vscode-webview://abc");
        var r = await C.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("*", r.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/index");
        preflight.Headers.Add("Origin", "null");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var p = await C.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, p.StatusCode);
        Assert.Contains("POST", p.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_RootAndDrilldown_OnScriptRepo()
    {
        var top = await C.GetFromJsonAsync<MapDto>("/api/map");
        Assert.Equal("src", Assert.Single(top!.Groups).Key);
        Assert.True(top.Groups[0].HasChildren);

        var src = await C.GetFromJsonAsync<MapDto>("/api/map?root=src");
        Assert.Contains(src!.Groups, g => g.Key == "src/util");
        // src/svc.ts 直接在 src 下：模块本身没有类型祖先，不成节点；Svc 类成为类型节点
        Assert.Contains(src.Groups, g => g.Key == "src/svc.ts:Svc" && g.SymbolId is not null);
        Assert.Contains(src.Edges, e => e.From == "src/svc.ts:Svc" && e.To == "src/util" && e.Kinds.ContainsKey("calls"));
    }

    [Fact]
    public async Task Pipeline_OnScriptRepoWithoutDispatch_ReturnsEmptyRows()
    {
        var r = await C.GetAsync("/api/pipeline");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var p = await r.Content.ReadFromJsonAsync<PipelineDto>();
        Assert.Equal("", p!.Root);
        Assert.Empty(p.Rows);
        Assert.Empty(p.Behaviors);
        Assert.False(p.BehaviorsOrdered);
    }

    [Fact]
    public async Task ArchContainers_OnScriptRepoWithoutDeployables_ReturnsEmptyWithNote()
    {
        var a = await C.GetFromJsonAsync<ArchDto>("/api/arch/containers");
        Assert.NotNull(a);
        Assert.Empty(a!.Nodes);
        Assert.Empty(a.Edges);
        Assert.Contains("没有发现可部署单元", a.Note);
    }

    [Fact]
    public async Task ArchDsm_UnknownContainer_404()
    {
        var r = await C.GetAsync("/api/arch/dsm?container=nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task ArchEntries_UnknownContainer_ReturnsEmptyList()
    {
        var r = await C.GetAsync("/api/arch/entries?container=nope");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var list = await r.Content.ReadFromJsonAsync<List<EntryDto>>();
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task ArchFlow_UnknownEntry_404()
    {
        var r = await C.GetAsync("/api/arch/flow?entry=999999");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task ArchNarrative_UnknownObject_200Failed_BadKind_400()
    {
        var r = await C.GetAsync("/api/arch/narrative?kind=container&key=nope");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<NarrativeDto>();
        Assert.Equal(("container", "nope", "failed"), (dto!.Kind, dto.Key, dto.Status));
        Assert.Contains("没有这个对象", dto.Error);
        Assert.Empty(dto.KeyPoints);

        // JSON 字段名是 camelCase（页面契约）
        var raw = await C.GetStringAsync("/api/arch/narrative?kind=flow&key=1");
        Assert.Contains("\"keyPoints\":", raw);
        Assert.Contains("\"generatedAt\":", raw);

        Assert.Equal(HttpStatusCode.BadRequest, (await C.GetAsync("/api/arch/narrative?kind=bogus&key=x")).StatusCode);
    }

    [Fact]
    public async Task ArchNarrate_Returns202_AndStatusTracksQueue()
    {
        var r = await C.PostAsJsonAsync("/api/arch/narrate", new NarrateRequest("component", "nope|x", true));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var dto = await r.Content.ReadFromJsonAsync<NarrativeDto>();
        Assert.Equal("component", dto!.Kind);
        Assert.Contains(dto.Status, new[] { "pending", "running", "failed" });

        NarrationStatusDto status;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { await Task.Delay(50); status = (await C.GetFromJsonAsync<NarrationStatusDto>("/api/arch/narrate/status"))!; }
        while ((status.Queued > 0 || status.Running is not null) && DateTime.UtcNow < deadline);
        Assert.True(status.Enabled);
        Assert.Equal("fake-model", status.Model);
        Assert.Equal("http://127.0.0.1:1/v1", status.Endpoint);
        Assert.Null(status.RemoteWarning);
        Assert.False(status.Paused);
        Assert.Equal(0, status.Queued);
        Assert.Null(status.Running);
        // 这个脚本仓库没有容器：对象不存在 → failed，有中文原因
        var after = await C.GetFromJsonAsync<NarrativeDto>("/api/arch/narrative?kind=component&key=nope%7Cx");
        Assert.Equal("failed", after!.Status);
        Assert.Contains("没有这个对象", after.Error);

        Assert.Equal(HttpStatusCode.BadRequest, (await C.PostAsJsonAsync("/api/arch/narrate", new NarrateRequest("nope", "x"))).StatusCode);
    }

    [Fact]
    public async Task ArchNarrate_StopAndResume_TogglePaused()
    {
        var stopped = await (await C.PostAsync("/api/arch/narrate/stop", null)).Content.ReadFromJsonAsync<NarrationStatusDto>();
        Assert.True(stopped!.Paused);
        var resumed = await (await C.PostAsync("/api/arch/narrate/resume", null)).Content.ReadFromJsonAsync<NarrationStatusDto>();
        Assert.False(resumed!.Paused);
    }

    [Fact]
    public async Task GetSymbol_404WhenMissing()
    {
        var r = await C.GetAsync("/api/symbols/999999");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task SymbolAt_FindsMethodUnderCursor()
    {
        var s = await C.GetFromJsonAsync<SymbolDto>("/api/symbols/at?path=src/svc.ts&line=3&col=10");
        Assert.Equal("src/svc.ts:Svc.find", s!.QualifiedName);
    }

    [Fact]
    public async Task SymbolAt_AcceptsAbsoluteWindowsPath()
    {
        var abs = Path.Combine(fx.Repo, "src", "svc.ts");
        var s = await C.GetFromJsonAsync<SymbolDto>($"/api/symbols/at?path={Uri.EscapeDataString(abs)}&line=3&col=10");
        Assert.Equal("src/svc.ts:Svc.find", s!.QualifiedName);
    }

    [Fact]
    public async Task SymbolAt_404OnBlankLineOrUnknownFile()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/symbols/at?path=src/svc.ts&line=1&col=1")).StatusCode); // import 行
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/symbols/at?path=src/nope.ts&line=1&col=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/symbols/at?path=C:/elsewhere/x.ts&line=1&col=1")).StatusCode);
    }

    [Fact]
    public async Task Neighbors_MarksAmbiguousGroup()
    {
        var main = (await C.GetFromJsonAsync<List<SymbolDto>>("/api/search?q=main"))!.Single();
        var n = await C.GetFromJsonAsync<List<NeighborDto>>($"/api/symbols/{main.Id}/neighbors?direction=out&kinds=calls");
        var ambiguous = n!.Where(x => x.AmbiguousGroup is not null).ToList();
        Assert.Equal(2, ambiguous.Count);
        Assert.All(ambiguous, x => Assert.Equal("calls:2", x.AmbiguousGroup));
        Assert.All(ambiguous, x => Assert.Equal(0.3, x.Confidence));
        Assert.Contains(n, x => x.Other?.QualifiedName == "src/svc.ts:Svc.find" && x.AmbiguousGroup is null && x.Direction == "out");
        Assert.Contains(n, x => x.ToName == "external:fetch" && x.Other!.Kind == "external");
    }

    [Fact]
    public async Task Neighbors_OtherCarriesCallerCount_ForUtilityDetection()
    {
        var find = (await C.GetFromJsonAsync<List<SymbolDto>>("/api/search?q=find"))!.Single();
        var n = await C.GetFromJsonAsync<List<NeighborDto>>($"/api/symbols/{find.Id}/neighbors?direction=out&kinds=calls");
        var helper = n!.Single(x => x.Other?.Name == "helper");
        Assert.Equal(1, helper.Other!.Callers); // 只有 Svc.find 调它
    }

    [Fact]
    public async Task Neighbors_DirectionAndKindsFilter()
    {
        var find = (await C.GetFromJsonAsync<List<SymbolDto>>("/api/search?q=find"))!.Single();
        var incoming = await C.GetFromJsonAsync<List<NeighborDto>>($"/api/symbols/{find.Id}/neighbors?direction=in&kinds=calls");
        Assert.Single(incoming!);
        Assert.Equal("src/app.ts:main", incoming![0].Other!.QualifiedName);
        Assert.Equal("in", incoming[0].Direction);

        var impl = await C.GetFromJsonAsync<List<NeighborDto>>($"/api/symbols/{find.Id}/neighbors?kinds=implements");
        Assert.Empty(impl!);

        var bad = await C.GetAsync($"/api/symbols/{find.Id}/neighbors?kinds=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Neighbors_404WhenSymbolMissing()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/symbols/999999/neighbors")).StatusCode);
    }

    [Fact]
    public async Task Index_ConcurrentPosts_OnlyOneAccepted_ThenStatusCompletes_AndSearchWorksMeanwhile()
    {
        var first = C.PostAsJsonAsync("/api/index", new IndexRequest(true));
        var second = C.PostAsJsonAsync("/api/index", new IndexRequest(true));
        var search = C.GetAsync("/api/search?q=hel");
        await Task.WhenAll(first, second, search);

        var codes = new[] { first.Result.StatusCode, second.Result.StatusCode };
        Assert.Contains(HttpStatusCode.Accepted, codes);
        Assert.Contains(HttpStatusCode.Conflict, codes);
        Assert.Equal(HttpStatusCode.OK, search.Result.StatusCode);

        IndexStatusDto status;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(100);
            status = (await C.GetFromJsonAsync<IndexStatusDto>("/api/index/status"))!;
        } while (status.Running && DateTime.UtcNow < deadline);

        Assert.False(status.Running);
        Assert.Null(status.Error);
        Assert.NotNull(status.LastReport);
        Assert.Equal(5, status.LastReport!.FilesScanned);
        Assert.Equal(5, status.LastReport.FilesParsed);
    }
}
