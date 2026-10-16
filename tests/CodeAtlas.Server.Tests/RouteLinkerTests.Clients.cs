using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// C# 后端经已注册的 HttpClient 调另一个容器：封装类（base(…, ClientNames.X) 点名注册）注入后调用、派生类调基类的 SendRequestAsync、
/// 方法里 CreateClient(名字)；路径来自别的文件的常量类（string.Format(ShopUrls.X, …)）与相对基地址（BaseAddress …/api/ + "stock/x"）。
/// </summary>
public partial class RouteLinkerTests
{
    private static readonly List<ArchNode> ClientContainers =
    [
        Container("shop-api", "dotnet-web", "shop-api"), Container("pricing", "dotnet-web", "pricing"), Container("stock", "dotnet-web", "stock"), Container("audit", "dotnet-web", "audit"),
    ];

    private static readonly List<EntryPoint> ClientEntries =
    [
        new(300, "pricing", "aspnet.controller", "http", "/api/prices", "GET", "pricing/Ctl.cs", 5),
        new(301, "pricing", "aspnet.controller", "http", "/api/prices/bulk-update", "POST", "pricing/Ctl.cs", 9),
        new(302, "pricing", "aspnet.controller", "http", "/api/prices/{sku}", "DELETE", "pricing/Ctl.cs", 13),
        new(310, "stock", "aspnet.controller", "http", "/api/stock/bulk-delete", "POST", "stock/Ctl.cs", 5),
        new(311, "stock", "aspnet.controller", "http", "/api/stock/levels", "GET", "stock/Ctl.cs", 9),
        new(320, "audit", "aspnet.controller", "http", "/api/prices", "GET", "audit/Ctl.cs", 5), // 同一路由在别的容器：有注册点名目标就不算歧义
    ];

    // 注册：容器图 http-client 出口（名字是常量标识符，基地址带 /api/ 的那个）
    private static readonly List<Outbound> ClientRegistrations =
    [
        new("shop-api", "http-client", "http", "PRICINGCLIENT", "Pricing:Url", "http://localhost:5100/", "shop-api/Setup.cs", 3, "pricing"),
        new("shop-api", "http-client", "http", "STOCKCLIENT", "Stock:Url", "http://localhost:5200/api/", "shop-api/Setup.cs", 4, "stock"),
    ];

    private static readonly List<MapSymbol> ClientSymbols =
    [
        new(400, SymbolKind.Class, "Shop.ClientNames", "shop-api/ClientNames.cs", null, 1, 5),
        new(401, SymbolKind.Class, "Shop.ShopUrls", "shop-api/ShopUrls.cs", null, 1, 6),
        new(402, SymbolKind.Interface, "Shop.IHttpHelper", "shop-api/HttpHelper.cs", null, 1, 5),
        new(403, SymbolKind.Method, "Shop.IHttpHelper.GetAsync<T>(string, CancellationToken)", "shop-api/HttpHelper.cs", 402, 3, 3),
        new(404, SymbolKind.Method, "Shop.IHttpHelper.PostJsonAsync<T>(string, object)", "shop-api/HttpHelper.cs", 402, 4, 4),
        new(405, SymbolKind.Interface, "Shop.IPricingHelper", "shop-api/PricingHelper.cs", null, 1, 1),
        new(406, SymbolKind.Class, "Shop.PricingHelper", "shop-api/PricingHelper.cs", null, 2, 5),
        new(410, SymbolKind.Class, "Shop.PriceService", "shop-api/PriceService.cs", null, 1, 20),
        new(411, SymbolKind.Method, "Shop.PriceService.List(string)", "shop-api/PriceService.cs", 410, 4, 8),
        new(412, SymbolKind.Method, "Shop.PriceService.BulkUpdate()", "shop-api/PriceService.cs", 410, 9, 13),
        new(413, SymbolKind.Method, "Shop.PriceService.Remove(string)", "shop-api/PriceService.cs", 410, 14, 19),
        new(420, SymbolKind.Class, "Shop.StockApiClient", "shop-api/StockApiClient.cs", null, 1, 10),
        new(421, SymbolKind.Method, "Shop.StockApiClient.SendRequestAsync(int, HttpMethod, string, object?)", "shop-api/StockApiClient.cs", 420, 5, 9),
        new(430, SymbolKind.Class, "Shop.StockLevels", "shop-api/StockLevels.cs", null, 1, 12),
        new(431, SymbolKind.Method, "Shop.StockLevels.Delete(int)", "shop-api/StockLevels.cs", 430, 3, 6),
        new(432, SymbolKind.Method, "Shop.StockLevels.Levels(int)", "shop-api/StockLevels.cs", 430, 7, 11),
        new(440, SymbolKind.Class, "Shop.Untyped", "shop-api/Untyped.cs", null, 1, 8),
        new(441, SymbolKind.Method, "Shop.Untyped.Run(HttpClient)", "shop-api/Untyped.cs", 440, 3, 7),
        new(450, SymbolKind.Class, "Shop.Direct", "shop-api/Direct.cs", null, 1, 9),
        new(451, SymbolKind.Method, "Shop.Direct.Run()", "shop-api/Direct.cs", 450, 3, 8),
    ];

    private static readonly Dictionary<string, string[]> ClientFiles = new()
    {
        ["shop-api/Setup.cs"] =
        [
            "public static class Setup {",                                                                // 1
            "  public static void Add(IServiceCollection s) {",                                           // 2
            "    s.AddHttpClient(ClientNames.PRICINGCLIENT, c => c.BaseAddress = new Uri(cfg));",         // 3
            "    s.AddHttpClient(ClientNames.STOCKCLIENT, c => c.BaseAddress = new Uri(cfg2)); } }",      // 4
        ],
        ["shop-api/ClientNames.cs"] =
        [
            "public static class ClientNames",                          // 1
            "{",                                                        // 2
            "    public const string PRICINGCLIENT = \"PricingClient\";", // 3
            "    public const string STOCKCLIENT = \"StockClient\";",     // 4
            "}",                                                        // 5
        ],
        // 别的文件里的路径常量：插值引用同类常量，{{0}} 是 string.Format 的占位
        ["shop-api/ShopUrls.cs"] =
        [
            "public static class ShopUrls",                                        // 1
            "{",                                                                   // 2
            "    public const string Prices = \"api/prices\";",                    // 3
            "    public const string ListPrices = $\"{Prices}?region={{0}}\";",    // 4
            "    public const string BulkUpdate = $\"{Prices}/bulk-update\";",     // 5
            "}",                                                                   // 6
        ],
        ["shop-api/HttpHelper.cs"] =
        [
            "public interface IHttpHelper",                                                    // 1
            "{",                                                                               // 2
            "    Task<T?> GetAsync<T>(string url, CancellationToken ct = default);",           // 3
            "    Task<T?> PostJsonAsync<T>(string url, object body);",                         // 4
            "}",                                                                               // 5
        ],
        // 封装类：构造时把注册名交给通用 helper（类一级点名 → 这个类与它实现的 IPricingHelper 绑定到 PRICINGCLIENT）
        ["shop-api/PricingHelper.cs"] =
        [
            "public interface IPricingHelper : IHttpHelper { }",                                         // 1
            "public class PricingHelper : HttpHelperBase, IPricingHelper",                               // 2
            "{",                                                                                          // 3
            "    public PricingHelper(IHttpHelper inner) : base(inner, ClientNames.PRICINGCLIENT) { }",  // 4
            "}",                                                                                          // 5
        ],
        ["shop-api/PriceService.cs"] =
        [
            "public class PriceService(IPricingHelper prices)",                                         // 1
            "{",                                                                                        // 2
            "    private readonly IPricingHelper _prices = prices;",                                   // 3
            "    public async Task List(string region)",                                               // 4
            "    {",                                                                                    // 5
            "        string url = string.Format(ShopUrls.ListPrices, region);",                        // 6
            "        var r = await _prices.GetAsync<List<X>>(url, ct: default);",                      // 7：GET /api/prices（不是 audit 的同名路由）
            "    }",                                                                                    // 8
            "    public async Task BulkUpdate()",                                                       // 9
            "    {",                                                                                    // 10
            "        await _prices.PostJsonAsync<X>(ShopUrls.BulkUpdate, new { });",                  // 11：POST /api/prices/bulk-update
            "        await _prices.GetAsync<X>(ShopUrls.BulkUpdate);",                                 // 12：GET 没有入口 → 不连
            "    }",                                                                                    // 13
            "    public async Task Remove(string sku)",                                                // 14
            "    {",                                                                                    // 15
            "        var url = $\"{ShopUrls.Prices}/{sku}\";",                                         // 16
            "        await prices.Describe(url);",                                                      // 17：封装上没有声明的方法 → 不算
            "        await _prices.PostJsonAsync<X>(url, sku);",                                       // 18：POST，入口只有 DELETE → 不连
            "    }",                                                                                    // 19
        ],
        // 派生类调基类的 SendRequestAsync：路径实参是声明里的 endpoint，方法是 HttpMethod 实参；基类点名 STOCKCLIENT（基地址 …/api/）
        ["shop-api/StockApiClient.cs"] =
        [
            "public abstract class StockApiClient",                                                                // 1
            "{",                                                                                                   // 2
            "    protected const string ClientName = \"StockClient\";",                                            // 3
            "    protected readonly IHttpClientFactory _factory;",                                                 // 4
            "    protected async Task<string> SendRequestAsync(int id, HttpMethod method, string endpoint, object? payload)", // 5
            "    {",                                                                                               // 6
            "        var http = _factory.CreateClient(ClientName);",                                               // 7
            "        using var req = new HttpRequestMessage(method, $\"{Base(id)}{endpoint}\"); return await (await http.SendAsync(req)).Content.ReadAsStringAsync();", // 8
            "    }",                                                                                               // 9
            "}",                                                                                                   // 10
        ],
        ["shop-api/StockLevels.cs"] =
        [
            "public sealed class StockLevels : StockApiClient, IStockLevels",                                     // 1
            "{",                                                                                                   // 2
            "    public Task<string> Delete(int id)",                                                              // 3
            "    {",                                                                                               // 4
            "        return SendRequestAsync(id, HttpMethod.Post, \"stock/bulk-delete\", new { id });",           // 5：POST /api/stock/bulk-delete
            "    }",                                                                                               // 6
            "    public Task<string> Levels(int id)",                                                              // 7
            "    {",                                                                                               // 8
            "        var endpoint = $\"stock/levels?id={id}\";",                                                   // 9
            "        return SendRequestAsync(id, HttpMethod.Get, endpoint, null);",                                // 10：GET /api/stock/levels
            "    }",                                                                                               // 11
            "}",                                                                                                   // 12
        ],
        // 没有点名任何注册的 HttpClient：相对路径不猜目标容器
        ["shop-api/Untyped.cs"] =
        [
            "public class Untyped",                                       // 1
            "{",                                                          // 2
            "    public async Task Run(HttpClient http)",                 // 3
            "    {",                                                      // 4
            "        await http.PostAsJsonAsync(\"stock/bulk-delete\", 1);", // 5
            "    }",                                                      // 6
            "}",                                                          // 7
            "",                                                           // 8
        ],
        // 方法里 CreateClient(注册名)：基地址的孔丢掉，相对路径接在 /api/ 后
        ["shop-api/Direct.cs"] =
        [
            "public class Direct(IHttpClientFactory f)",                                          // 1
            "{",                                                                                  // 2
            "    public async Task Run()",                                                        // 3
            "    {",                                                                              // 4
            "        var url = $\"{_resolver.BaseUrl(1)}stock/bulk-delete\";",                    // 5
            "        var http = f.CreateClient(ClientNames.STOCKCLIENT);",                        // 6
            "        using var req = new HttpRequestMessage(HttpMethod.Post, url); await http.SendAsync(req);", // 7：POST /api/stock/bulk-delete
            "    }",                                                                              // 8
            "}",                                                                                  // 9
        ],
    };

    [Fact]
    public void Match_RegisteredClientWrappers_ResolveCrossFileConstantsAndRelativeBase()
    {
        var m = RouteLinker.Match(ClientRegistrations, ClientEntries, ClientContainers, ClientSymbols, p => ClientFiles.TryGetValue(p, out var l) ? l : [])
            .Select(x => (x.File, x.Line, x.Path, x.EntrySymbolId, x.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            ("shop-api/Direct.cs", 7, "/api/stock/bulk-delete", 310L, "stock"),
            ("shop-api/PriceService.cs", 7, "/api/prices", 300L, "pricing"),
            ("shop-api/PriceService.cs", 11, "/api/prices/bulk-update", 301L, "pricing"),
            ("shop-api/StockLevels.cs", 5, "/api/stock/bulk-delete", 310L, "stock"),
            ("shop-api/StockLevels.cs", 10, "/api/stock/levels", 311L, "stock"),
        }, m);
    }

    [Fact]
    public void Match_WithoutRegistrations_RelativeWrapperCallsStayUnmatched()
    {
        // 去掉注册：同样的源码一条都不连（不靠 /api 开头的字面量就不猜）
        var m = RouteLinker.Match([], ClientEntries, ClientContainers, ClientSymbols, p => ClientFiles.TryGetValue(p, out var l) ? l : []);
        Assert.Empty(m);
    }

    [Theory]
    [InlineData("http://localhost:5003/api/", "/api/")]
    [InlineData("http://localhost:5162/", "/")]
    [InlineData("https+http://catalog-api", "/")]
    [InlineData(null, "/")]
    public void BasePathOf_TakesThePathOfTheBaseAddress(string? url, string expected) => Assert.Equal(expected, RouteLinker.BasePathOf(url));

    [Theory]
    [InlineData("gateway/bulk-delete", "/api/", "/api/gateway/bulk-delete")]
    [InlineData("${managerUrl}group/batch-delete", "/api/", "/api/group/batch-delete")]
    [InlineData("/health", "/api/", "/health")]                           // 绝对路径：BaseAddress 只换路径
    [InlineData("api/symbols?serverName=${x}", "/", "/api/symbols")]
    [InlineData("http://other/x", "/", null)]
    [InlineData("${a}${b}", "/", null)]
    public void BoundPath_JoinsRelativePathsToTheBase(string value, string basePath, string? expected) => Assert.Equal(expected, RouteLinker.BoundPath(value, basePath));

    [Fact]
    public void CSharpSource_EvaluatesConstantsInterpolationsAndFormat()
    {
        var cs = new RouteLinker.CSharpSource(ClientSymbols, p => ClientFiles.TryGetValue(p, out var l) ? l : []);
        Assert.Equal("api/prices?region=${x}", cs.Eval("string.Format(ShopUrls.ListPrices, region)", "shop-api/PriceService.cs", "", 0));
        Assert.Equal("api/prices/bulk-update", cs.Eval("ShopUrls.BulkUpdate", "shop-api/PriceService.cs", "", 0));
        Assert.Equal("api/prices/${sku}", cs.Eval("$\"{ShopUrls.Prices}/{sku}\"", "shop-api/PriceService.cs", "", 0));
        Assert.Equal("api/prices/x", cs.Eval("new Uri(new Uri(baseUrl, UriKind.Absolute), $\"{ShopUrls.Prices}/x\").ToString()", "shop-api/PriceService.cs", "", 0));
        Assert.Equal("${Combine}?v=1", cs.Eval("$\"{Combine(a, $\"{ShopUrls.Prices}/1\")}?v=1\"", "shop-api/PriceService.cs", "", 0)); // 嵌套插值串不切歪
        Assert.Equal("StockClient", cs.Eval("ClientName", "shop-api/StockApiClient.cs", "", 0));
        Assert.Null(cs.Eval("Unknown.Thing", "shop-api/PriceService.cs", "", 0));
    }
}
