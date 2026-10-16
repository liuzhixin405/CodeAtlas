using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 匹配收紧：路径里的变量段只配模板的参数段、方法都知道且不同就排除、同等候选取字面量段多的；C# 局部变量只在声明它的方法里可见。
/// 来源：真实项目里前端 GET /accounts/${login} 连到了"备份列表" /accounts/backups、DELETE …/parameters/${id} 连到 POST …/parameters/preview、
/// 一个方法里的局部 const url = "…/bulk-update" 被同文件别的方法里的 url 借走，GET 列表请求被画成 POST 批量写入。
/// </summary>
public partial class RouteLinkerTests
{
    [Theory]
    [InlineData("/api/shop/accounts/backups", "/api/shop/accounts/${login}", false)]      // 变量值不知道，不能当字面量 backups
    [InlineData("/api/shop/accounts/{login:int}", "/api/shop/accounts/${login}", true)]
    [InlineData("/api/shop/accounts/backups", "/api/shop/accounts/backups", true)]
    [InlineData("/api/shop/accounts/{login:int}", "/api/shop/accounts/backups", true)]     // 字面量配参数段照旧
    [InlineData("/api/shop/items/preview", "/api/shop/items/{id}", false)]                // 生成 SDK 的 {id} 也是变量
    [InlineData("/api/shop/files/{**path}", "/api/shop/files/${a}/${b}", true)]
    [InlineData("/api/shop/v${n}/items", "/api/shop/v${n}/items", false)]                 // 半截变量段同样只配参数段
    public void PathMatches_VariableSegmentsOnlyMatchParameters(string template, string path, bool expected) =>
        Assert.Equal(expected, RouteLinker.PathMatches(template, path));

    private static readonly List<ArchNode> ShopContainers = [Container("shop-api", "dotnet-web", "shop-api"), Container("web", "frontend", "web")];

    private static readonly List<EntryPoint> ShopEntries =
    [
        new(1, "shop-api", "aspnet.controller", "http", "/api/shop/accounts/backups", "GET", "shop-api/Accounts.cs", 10),
        new(2, "shop-api", "aspnet.controller", "http", "/api/shop/accounts/{login:int}", "GET", "shop-api/Accounts.cs", 20),
        new(3, "shop-api", "aspnet.controller", "http", "/api/shop/params/preview", "POST", "shop-api/Params.cs", 10),
        new(4, "shop-api", "aspnet.controller", "http", "/api/shop/items", "GET", "shop-api/Items.cs", 10),
        new(5, "shop-api", "aspnet.controller", "http", "/api/shop/items/{**name}", "GET", "shop-api/Items.cs", 20),
        new(6, "shop-api", "aspnet.controller", "http", "/api/shop/import/run", "POST", "shop-api/Import.cs", 10),
    ];

    private static Outbound Front(string method, string url, int line) =>
        new("web", "frontend-http", "http", method, null, url, "web/src/api.ts", line, "external:http-service:api-api-shop");

    [Fact]
    public void Match_VariableSegment_PicksParameterTemplate_NotLiteral()
    {
        var m = RouteLinker.Match([Front("GET", "/api/shop/accounts/${login}", 1)], ShopEntries, ShopContainers, [], _ => []);
        Assert.Equal((1, 2L), (Assert.Single(m).Line, m[0].EntrySymbolId)); // 先前字面量模板总赢 → 1（备份列表）
    }

    [Fact]
    public void Match_LiteralSegment_PrefersMoreLiteralTemplate()
    {
        // 路径那一段本来就是 backups：两个模板都匹配，取字面量段多的
        var m = RouteLinker.Match([Front("GET", "/api/shop/accounts/backups", 1)], ShopEntries, ShopContainers, [], _ => []);
        Assert.Equal(1L, Assert.Single(m).EntrySymbolId);
        // /items 同时匹配 /items 与 /items/{**name}（通配能配零段）：取没有通配的
        var items = RouteLinker.Match([Front("GET", "/api/shop/items", 2)], ShopEntries, ShopContainers, [], _ => []);
        Assert.Equal(4L, Assert.Single(items).EntrySymbolId);
        // 两段参数的模板比通配具体（先前按参数个数，{**path} 只算 1 个反而赢）
        List<EntryPoint> files =
        [
            new(7, "shop-api", "aspnet.controller", "http", "/api/shop/files/{**path}", "GET", "shop-api/Files.cs", 10),
            new(8, "shop-api", "aspnet.controller", "http", "/api/shop/files/{id}/{name}", "GET", "shop-api/Files.cs", 20),
        ];
        var f = RouteLinker.Match([Front("GET", "/api/shop/files/${id}/${name}", 3)], files, ShopContainers, [], _ => []);
        Assert.Equal(8L, Assert.Single(f).EntrySymbolId);
    }

    [Fact]
    public void Match_KnownMethodsThatDiffer_AreNeverLinked()
    {
        List<Outbound> outbound =
        [
            Front("DELETE", "/api/shop/params/${id}", 1),   // 只有 POST …/preview：方法不同 + 变量不配字面量
            Front("GET", "/api/shop/import/run", 2),        // 路径完全相同，但入口只有 POST
            Front("POST", "/api/shop/import/run", 3),
            new("web", "frontend-http", "http", null, null, "/api/shop/import/run", "web/src/api.ts", 4, null), // 方法不知道：照旧连
        ];
        var m = RouteLinker.Match(outbound, ShopEntries, ShopContainers, [], _ => []).Select(x => (x.Line, x.EntrySymbolId)).ToList();
        Assert.Equal(new[] { (3, 6L), (4, 6L) }, m);
    }

    // C#：一个方法里的局部变量 url 不借给同文件别的方法；同一方法里先 GET 后 POST，各按自己那一处的调用定方法
    private static readonly List<ArchNode> LedgerContainers = [Container("shop-api", "dotnet-web", "shop-api"), Container("ledger", "dotnet-web", "ledger")];

    private static readonly List<EntryPoint> LedgerEntries =
    [
        new(10, "ledger", "aspnet.controller", "http", "/api/ledger/bulk-update", "POST", "ledger/Ctl.cs", 5),
        new(11, "ledger", "aspnet.controller", "http", "/api/ledger", "GET", "ledger/Ctl.cs", 9),
        new(12, "ledger", "aspnet.controller", "http", "/api/ledger/rows", "POST", "ledger/Ctl.cs", 13),
    ];

    private static readonly List<MapSymbol> LedgerSymbols =
    [
        new(200, SymbolKind.Method, "Shop.LedgerClient.Export()", "shop-api/LedgerClient.cs", null, 4, 8),
        new(201, SymbolKind.Method, "Shop.LedgerClient.BulkUpdate()", "shop-api/LedgerClient.cs", null, 9, 13),
        new(202, SymbolKind.Method, "Shop.LedgerClient.Sync()", "shop-api/LedgerClient.cs", null, 14, 19),
    ];

    private static readonly Dictionary<string, string[]> LedgerFiles = new()
    {
        ["shop-api/LedgerClient.cs"] =
        [
            "namespace Shop;",                                                          // 1
            "public class LedgerClient(HttpClient http)",                               // 2
            "{",                                                                        // 3
            "    public async Task Export()",                                           // 4
            "    {",                                                                    // 5
            "        var url = string.Format(Unknown.ExportUrl, 1);",                   // 6：求不出；先前借到下面方法里的局部 url → POST bulk-update
            "        await http.PostAsJsonAsync(url, 1);",                              // 7
            "    }",                                                                    // 8
            "    public async Task BulkUpdate()",                                       // 9
            "    {",                                                                    // 10
            "        const string url = \"api/ledger/bulk-update\";",                   // 11：局部常量，只在本方法里
            "        await http.PostAsJsonAsync(url, new { });",                        // 12
            "    }",                                                                    // 13
            "    public async Task Sync()",                                             // 14
            "    {",                                                                    // 15
            "        var all = await http.GetFromJsonAsync<X>(\"api/ledger\");",         // 16：GET
            "        var path = \"api/ledger/rows\";",                                  // 17
            "        await http.PostAsJsonAsync(path, all);",                           // 18：POST（先前取方法体第一个提示 = GET）
            "    }",                                                                    // 19
        ],
    };

    [Fact]
    public void Match_CSharpLocalPathVariables_StayInTheirMethod_AndMethodHintIsPerCall()
    {
        var m = RouteLinker.Match([], LedgerEntries, LedgerContainers, LedgerSymbols, p => LedgerFiles.TryGetValue(p, out var l) ? l : [])
            .Select(x => (x.Line, x.Path, x.EntrySymbolId)).ToList();
        Assert.Equal(new[] { (11, "/api/ledger/bulk-update", 10L), (12, "/api/ledger/bulk-update", 10L), (16, "/api/ledger", 11L), (17, "/api/ledger/rows", 12L) }, m);
    }

    [Fact]
    public void Upgrade_ApiPlaceholder_RemovedWhenMostCallsMatchOneContainer_KeptWhenSplit()
    {
        var api = new ArchNode("external:http-service:api-api-shop", ArchNodeKind.External, "http-service", "API /api/shop", null, ArchSource.Discovered, [], [], []);
        List<ArchNode> containers = [.. ShopContainers, Container("other-api", "dotnet-web", "other-api")];
        List<Outbound> outbound = [Front("GET", "/api/shop/items", 1), Front("GET", "/api/shop/items/x/y", 2), Front("POST", "/api/shop/missing", 3)];
        var model = new ArchModel([.. containers, api],
            [new ArchEdge("web", api.Key, "http", 0.5, ArchSource.Discovered, [new Evidence("web/src/api.ts", 1)])], outbound, []);
        // 3 处对上 2 处、都在 shop-api → 外部节点去掉，shop-api 的边上记 1/3 对不上
        var one = RouteLinker.Upgrade(model, [new("web", "web/src/api.ts", 1, "/api/shop/items", 4, "shop-api"), new("web", "web/src/api.ts", 2, "/api/shop/items/x/y", 5, "shop-api")]);
        Assert.DoesNotContain(one.Nodes, n => n.Key == api.Key);
        Assert.Contains(Assert.Single(one.Edges, e => e.FromKey == "web" && e.ToKey == "shop-api").Evidence, e => e.Note == "1/3 处对不上后端路由");
        // 对上的分在两个容器：说不清这个 API 根是谁，外部节点保留
        var split = RouteLinker.Upgrade(model, [new("web", "web/src/api.ts", 1, "/api/shop/items", 4, "shop-api"), new("web", "web/src/api.ts", 2, "/api/shop/items/x/y", 9, "other-api")]);
        Assert.Contains(Assert.Single(split.Edges, e => e.ToKey == api.Key).Evidence, e => e.Note == "2/3 处已对上路由");
    }

    [Fact]
    public void Match_InterpolatedCallInsideSegment_NeverMatchesLiteralSibling()
    {
        // 前端 GET /groups/${encodeURIComponent(name)}：后端同级有字面量 columns 与参数 {groupName}，只能是后者
        List<EntryPoint> entries =
        [
            new(20, "shop-api", "aspnet.controller", "http", "/api/shop/groups/columns", "GET", "shop-api/Groups.cs", 10),
            new(21, "shop-api", "aspnet.controller", "http", "/api/shop/groups/{groupName}", "GET", "shop-api/Groups.cs", 20),
        ];
        var m = RouteLinker.Match([Front("GET", "/api/shop/groups/${encodeURIComponent(groupName)}", 1)], entries, ShopContainers, [], _ => []);
        Assert.Equal(21L, Assert.Single(m).EntrySymbolId);
    }
}
