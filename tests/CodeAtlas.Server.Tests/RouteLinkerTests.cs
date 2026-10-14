using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public class RouteLinkerTests
{
    [Theory]
    [InlineData("/api/v5/accounts/{id}", "/api/v5/accounts/123", true)]
    [InlineData("/api/v5/accounts/{id}", "/api/v5/accounts/${id}", true)]
    [InlineData("/api/v5/accounts/{id}", "/api/v5/accounts", false)]
    [InlineData("/api/v5/accounts/{id?}", "/api/v5/accounts", true)]
    [InlineData("/api/v5/system/brands", "/api/v5/system/brands?mtVersion=4", true)]
    [InlineData("/api/v5/System/Brands", "/api/v5/system/brands", true)]
    [InlineData("/api/v5/files/{*path}", "/api/v5/files/a/b/c", true)]
    [InlineData("/api/v5/system/brands", "/api/proxy-gateway/api/v5/system/brands", false)] // 代理前缀不猜
    [InlineData("/api/v5/system/brands", "/api/v5/system", false)]
    public void PathMatches(string template, string path, bool expected) => Assert.Equal(expected, RouteLinker.PathMatches(template, path));

    private static ArchNode Container(string key, string subKind, params string[] members) =>
        new(key, ArchNodeKind.Container, subKind, key, key, ArchSource.Discovered, [], members, []);

    private static readonly List<ArchNode> Containers = [Container("Acme.Api", "dotnet-web", "Acme.Api"), Container("billing/Billing.Api", "dotnet-web", "billing/Billing.Api"), Container("web", "frontend", "web")];

    private static readonly List<EntryPoint> Entries =
    [
        new(3, "Acme.Api", "aspnet.controller", "http", "/api/v5/orders/{id}", "GET", "Acme.Api/Ctl.cs", 7),
        new(4, "Acme.Api", "aspnet.controller", "http", "/api/v5/orders", "POST", "Acme.Api/Ctl.cs", 11),
        new(60, "billing/Billing.Api", "aspnet.controller", "http", "/api/billing/invoices", "POST", "billing/Billing.Api/Ctl.cs", 5),
        new(61, "billing/Billing.Api", "aspnet.controller", "http", "/api/v5/orders/{id}", "GET", "billing/Billing.Api/Ctl.cs", 9), // 和 Acme.Api 同一个路由 → 前端的 /api/v5/orders/1 对到两个容器，不猜
        new(62, "billing/Billing.Api", "aspnet.minimal", "http", "/api/billing", "GET", "billing/Billing.Api/Api.cs", 5),
        new(63, "billing/Billing.Api", "aspnet.minimal", "http", "/api/billing", "POST", "billing/Billing.Api/Api.cs", 6),
    ];

    private static readonly List<MapSymbol> Symbols =
    [
        new(100, SymbolKind.Method, "Acme.Api.BillingClient.Pay()", "Acme.Api/BillingClient.cs", null, 3, 8),
        new(101, SymbolKind.Method, "Acme.Api.BillingClient.Ping()", "Acme.Api/BillingClient.cs", null, 10, 12),
        new(102, SymbolKind.Method, "Acme.Api.CatalogClient.Get(int)", "Acme.Api/CatalogClient.cs", null, 4, 8),
        new(103, SymbolKind.Method, "Acme.Api.CatalogClient.Create()", "Acme.Api/CatalogClient.cs", null, 9, 13),
        new(104, SymbolKind.Method, "Acme.Api.CatalogClient.Facets()", "Acme.Api/CatalogClient.cs", null, 14, 17),
    ];

    private static readonly Dictionary<string, string[]> Files = new()
    {
        ["Acme.Api/BillingClient.cs"] =
        [
            "namespace Acme.Api;",                                                          // 1
            "public sealed class BillingClient(HttpClient http)",                           // 2
            "{",                                                                             // 3
            "    public async Task Pay()",                                                   // 4
            "    {",                                                                         // 5
            "        var r = await http.PostAsJsonAsync(\"api/billing/invoices\", new { });", // 6
            "        var p = await http.GetAsync($\"api/v5/orders/{id}\");",                 // 7：对到两个容器，不猜
            "    }",                                                                         // 8
            "",                                                                              // 9
            "    public Task Ping() {",                                                      // 10
            "        var s = \"api/billing/invoices\"; // 这个方法没有 HttpClient 调用，不算",  // 11
            "    }",                                                                         // 12
        ],
        // eShop 的写法：路径前缀放在字段里，方法里用插值串 / 直接传字段名
        ["Acme.Api/CatalogClient.cs"] =
        [
            "public class CatalogClient(HttpClient http)",                                   // 1
            "{",                                                                             // 2
            "    private readonly string remoteServiceBaseUrl = \"api/billing/\";",          // 3
            "    public Task<X?> Get(int id)",                                               // 4
            "    {",                                                                         // 5
            "        var uri = $\"{remoteServiceBaseUrl}invoices?id={id}\";",                 // 6：{字段} 展开 → /api/billing/invoices
            "        return http.GetFromJsonAsync<X>(uri);",                                 // 7
            "    }",                                                                         // 8
            "    public async Task Create() {",                                              // 9
            "        var req = new HttpRequestMessage(HttpMethod.Post, remoteServiceBaseUrl);", // 10：字段名直接当参数 → /api/billing，按 POST 挑入口
            "        await http.SendAsync(req);",                                            // 11
            "    }",                                                                         // 12
            "    public Task Facets() {",                                                    // 13
            "        var uri = $\"{unknownBase}facets\";",                                   // 14：未知变量开头拼不出路径
            "        return http.GetStringAsync(uri);",                                      // 15
            "    }",                                                                         // 16
            "}",                                                                             // 17
        ],
    };

    private static readonly List<Outbound> Outbound =
    [
        new("web", "frontend-http", "http", null, null, "/api/v5/orders/${id}", "web/src/api.ts", 2, "external:http-service:api-api-v5"),
        new("web", "frontend-http", "http", null, null, "/api/v5/orders", "web/src/api.ts", 3, "external:http-service:api-api-v5"),
        new("web", "frontend-http", "http", null, null, "/api/v5/nothing", "web/src/api.ts", 4, "external:http-service:api-api-v5"),
    ];

    [Fact]
    public void Match_FrontendAndCSharpLiterals_SkipsAmbiguousAndMethodsWithoutHttpCalls()
    {
        var matches = RouteLinker.Match(Outbound, Entries, Containers, Symbols, p => Files.TryGetValue(p, out var l) ? l : []);
        // 前端：/api/v5/orders（POST 入口，唯一容器）对上；/api/v5/orders/${id} 对到两个容器 → 不猜；/api/v5/nothing 没有入口
        var web = matches.Where(m => m.FromContainerKey == "web").ToList();
        var only = Assert.Single(web);
        Assert.Equal((3, "/api/v5/orders", 4L, "Acme.Api"), (only.Line, only.Path, only.EntrySymbolId, only.ToContainerKey));
        // C#：Pay 里的 api/billing/invoices 对上 Billing.Api；api/v5/orders/{id} 排除本容器后只剩 Billing 的那条 → 也对上；Ping 没有 HttpClient 调用
        var cs = matches.Where(m => m.FromContainerKey == "Acme.Api" && m.File == "Acme.Api/BillingClient.cs").OrderBy(m => m.Line).ToList();
        Assert.Equal(2, cs.Count);
        Assert.Equal(("Acme.Api/BillingClient.cs", 6, "/api/billing/invoices", 60L, "billing/Billing.Api"), (cs[0].File, cs[0].Line, cs[0].Path, cs[0].EntrySymbolId, cs[0].ToContainerKey));
        Assert.Equal((7, "/api/v5/orders/${id}", 61L), (cs[1].Line, cs[1].Path, cs[1].EntrySymbolId));
        Assert.DoesNotContain(matches, m => m.Line == 11);
    }

    [Fact]
    public void Match_ExpandsPathFields_InInterpolationsAndBareArguments_PrefersCallMethod()
    {
        var matches = RouteLinker.Match([], Entries, Containers, Symbols, p => Files.TryGetValue(p, out var l) ? l : [])
            .Where(m => m.File == "Acme.Api/CatalogClient.cs").OrderBy(m => m.Line).ToList();
        Assert.Equal(2, matches.Count);
        Assert.Equal((6, "/api/billing/invoices", 60L), (matches[0].Line, matches[0].Path, matches[0].EntrySymbolId));
        Assert.Equal((10, "/api/billing", 63L), (matches[1].Line, matches[1].Path, matches[1].EntrySymbolId)); // GET 与 POST 两个入口，HttpMethod.Post → POST 的那条
        Assert.DoesNotContain(matches, m => m.Line == 14);
    }

    [Fact]
    public void CandidatePaths_LiteralsInterpolationsAndFields()
    {
        var fields = new Dictionary<string, string> { ["baseUrl"] = "/api/Orders/" };
        Assert.Equal(["/api/Orders/${id}"], RouteLinker.CandidatePaths("var u = $\"{baseUrl}{id}\";", fields));
        Assert.Equal(["/api/Orders"], RouteLinker.CandidatePaths("http.GetFromJsonAsync<T>(baseUrl)", fields));
        Assert.Equal(["/api/v5/x"], RouteLinker.CandidatePaths("http.GetAsync(\"api/v5/x?y=1\")", fields));
        Assert.Empty(RouteLinker.CandidatePaths("http.GetAsync(\"http://localhost:5000/api/v5/x\")", fields)); // 绝对 URL 由容器图规则管
        Assert.Empty(RouteLinker.CandidatePaths("log.Info(\"items/{id}\")", fields));                          // 不像 API 路径
    }

    [Fact]
    public void Upgrade_AddsRouteEdges_AndRemovesApiPlaceholderOnlyWhenEverythingMatched()
    {
        var api = new ArchNode("external:http-service:api-api-v5", ArchNodeKind.External, "http-service", "API /api/v5", null, ArchSource.Discovered, [], [], []);
        var model = new ArchModel(
            [.. Containers, api],
            [new ArchEdge("web", "external:http-service:api-api-v5", "http", 0.5, ArchSource.Discovered, [new Evidence("web/src/api.ts", 2)])],
            Outbound, []);
        var matches = new List<RouteMatch>
        {
            new("web", "web/src/api.ts", 3, "/api/v5/orders", 4, "Acme.Api"),
            new("Acme.Api", "Acme.Api/BillingClient.cs", 6, "/api/billing/invoices", 60, "billing/Billing.Api"),
        };
        var up = RouteLinker.Upgrade(model, matches);
        var webApi = Assert.Single(up.Edges, e => e.FromKey == "web" && e.ToKey == "Acme.Api");
        Assert.Equal((0.9, "http", 3), (webApi.Confidence, webApi.Protocol, webApi.Evidence[0].Line));
        Assert.Contains(up.Edges, e => e.FromKey == "Acme.Api" && e.ToKey == "billing/Billing.Api" && e.Confidence == 0.9);
        // 三处前端出口只对上一处：占位外部节点保留，证据里标 1/3
        var placeholder = Assert.Single(up.Edges, e => e.ToKey == "external:http-service:api-api-v5");
        Assert.Contains(placeholder.Evidence, e => e.Note == "1/3 处已对上路由");
        Assert.Contains(up.Nodes, n => n.Key == "external:http-service:api-api-v5");

        // 全部对上 → 占位节点与边一起消失
        var all = RouteLinker.Upgrade(model, [
            new("web", "web/src/api.ts", 2, "/api/v5/orders/1", 3, "Acme.Api"),
            new("web", "web/src/api.ts", 3, "/api/v5/orders", 4, "Acme.Api"),
            new("web", "web/src/api.ts", 4, "/api/v5/nothing", 4, "Acme.Api"),
        ]);
        Assert.DoesNotContain(all.Edges, e => e.ToKey == "external:http-service:api-api-v5");
        Assert.DoesNotContain(all.Nodes, n => n.Key == "external:http-service:api-api-v5");
        Assert.Equal(3, Assert.Single(all.Edges, e => e.FromKey == "web" && e.ToKey == "Acme.Api").Evidence.Count);
    }
}
