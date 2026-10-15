using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Go / Python 的 HTTP 出口（http.Get / NewRequest / Sprintf，requests / httpx 的 f-string）、gRPC 客户端调用 → 对端 grpc 入口、生成的前端 SDK（url: '/api/…'）。</summary>
public partial class RouteLinkerTests
{
    private static readonly List<ArchNode> PolyContainers =
    [
        Container("goweb", "go", "goweb"),
        Container("pyweb", "python", "pyweb"),
        Container("py-api", "python", "py-api"),
        Container("catalog", "go", "catalog"),
        Container("frontend", "frontend", "frontend"),
    ];

    private static readonly List<EntryPoint> PolyEntries =
    [
        new(500, "py-api", "python.fastapi", "http", "/api/packaging/{id}", "GET", "py-api/a.py", 3),
        new(501, "py-api", "python.fastapi", "http", "/api/orders", "POST", "py-api/a.py", 6),
        new(502, "py-api", "python.fastapi", "http", "/api/orders", "GET", "py-api/a.py", 9),
        new(503, "py-api", "python.fastapi", "http", "/api/v1/orders", "POST", "py-api/a.py", 12),
        new(504, "py-api", "python.fastapi", "http", "/api/v1/items/", "GET", "py-api/items.py", 7),
        new(505, "py-api", "python.fastapi", "http", "/api/v1/items/{item_id}", "PUT", "py-api/items.py", 14),
        new(600, "catalog", "go.grpc", "grpc", "ProductCatalogService/ListProducts", null, "catalog/product_catalog.go", 5),
        new(601, "catalog", "go.grpc", "grpc", "ProductCatalogService/GetProduct", null, "catalog/product_catalog.go", 7),
        new(602, "pyweb", "python.grpc", "grpc", "EmailService/SendOrderConfirmation", null, "pyweb/email_server.py", 9),
    ];

    private static readonly List<MapSymbol> PolySymbols =
    [
        new(700, SymbolKind.Function, "goweb.fetchPackaging", "goweb/packaging.go", 799, 3, 6),
        new(701, SymbolKind.Function, "goweb.post", "goweb/packaging.go", 799, 7, 10),
        new(702, SymbolKind.Method, "goweb.frontendServer.getProducts", "goweb/rpc.go", 798, 3, 7),
        new(703, SymbolKind.Method, "goweb.frontendServer.getProduct", "goweb/rpc.go", 798, 8, 12),
        new(704, SymbolKind.Method, "goweb.checkoutService.sendEmail", "goweb/rpc.go", 798, 13, 16),
        new(710, SymbolKind.Function, "pyweb.client.load", "pyweb/client.py", 797, 4, 6),
        new(711, SymbolKind.Function, "pyweb.client.save", "pyweb/client.py", 797, 8, 9),
        new(712, SymbolKind.Function, "pyweb.client.nothing", "pyweb/client.py", 797, 11, 12),
        new(713, SymbolKind.Method, "pyweb.client.Recommender.list", "pyweb/client.py", 796, 15, 17),
        new(720, SymbolKind.Method, "frontend/src/client/sdk.gen.ts:ItemsService.readItems", "frontend/src/client/sdk.gen.ts", 795, 2, 8),
        new(721, SymbolKind.Method, "frontend/src/client/sdk.gen.ts:ItemsService.updateItem", "frontend/src/client/sdk.gen.ts", 795, 9, 15),
    ];

    private static readonly Dictionary<string, string[]> PolyFiles = new()
    {
        ["goweb/packaging.go"] =
        [
            "package main",                                                       // 1
            "",                                                                   // 2
            "func fetchPackaging(base string, id string) {",                     // 3
            "\turl := fmt.Sprintf(\"%s/api/packaging/%s\", base, id)",           // 4：开头的 %s 是主机地址
            "\tresp, err := http.Get(url)",                                       // 5
            "}",                                                                  // 6
            "func post(base string) {",                                           // 7
            "\treq, _ := http.NewRequest(http.MethodPost, base+\"/api/orders\", body)", // 8：按 POST 挑入口
            "\tclient.Do(req)",                                                   // 9
            "}",                                                                  // 10
        ],
        ["goweb/rpc.go"] =
        [
            "package main",                                                                   // 1
            "",                                                                               // 2
            "func (fe *frontendServer) getProducts(ctx context.Context) ([]*pb.Product, error) {", // 3
            "\tresp, err := pb.NewProductCatalogServiceClient(fe.productCatalogSvcConn).",    // 4
            "\t\tListProducts(ctx, &pb.Empty{})",                                             // 5：链式调用跨行
            "\treturn resp.GetProducts(), err",                                               // 6
            "}",                                                                              // 7
            "func (fe *frontendServer) getProduct(ctx context.Context, id string) (*pb.Product, error) {", // 8
            "\tcl := pb.NewProductCatalogServiceClient(fe.productCatalogSvcConn)",            // 9
            "\tresp, err := cl.GetProduct(ctx, &pb.GetProductRequest{Id: id})",              // 10：客户端存进变量
            "\treturn resp, err",                                                             // 11
            "}",                                                                              // 12
            "func (cs *checkoutService) sendEmail(ctx context.Context) error {",              // 13
            "\t_, err := pb.NewEmailServiceClient(cs.emailSvcConn).SendOrderConfirmation(ctx, &pb.SendOrderConfirmationRequest{})", // 14：对端是 Python
            "\treturn err",                                                                   // 15
            "}",                                                                              // 16
        ],
        ["pyweb/client.py"] =
        [
            "import requests",                                       // 1
            "API = \"/api/v1\"",                                     // 2
            "",                                                      // 3
            "def load(base, pid):",                                  // 4
            "    r = requests.get(f\"{base}/api/packaging/{pid}\")", // 5
            "    return r",                                          // 6
            "",                                                      // 7
            "def save(base):",                                       // 8
            "    return httpx.post(f'{base}{API}/orders', json={})", // 9：单引号 f-string；{API} 用模块常量展开
            "",                                                      // 10
            "def nothing():",                                        // 11
            "    s = \"/api/orders\"",                               // 12：没有 HTTP 调用
            "",                                                      // 13
            "class Recommender:",                                    // 14
            "    def list(self):",                                   // 15
            "        return catalog_stub.ListProducts(demo_pb2.Empty())", // 16：stub 在模块级赋值
            "        ",                                              // 17
            "catalog_stub = demo_pb2_grpc.ProductCatalogServiceStub(channel)", // 18
        ],
        ["frontend/src/client/sdk.gen.ts"] =
        [
            "export class ItemsService {",                                                     // 1
            "    public static readItems(options?: Options) {",                                // 2
            "        return (options?.client ?? client).get<ItemsReadItemsResponses>({",       // 3
            "            responseType: 'json',",                                               // 4
            "            url: '/api/v1/items/',",                                              // 5
            "            ...options",                                                          // 6
            "        });",                                                                     // 7
            "    }",                                                                           // 8
            "    public static updateItem(options: Options) {",                                // 9
            "        return (options.client ?? client).put<ItemsUpdateItemResponses>({",       // 10
            "            url: '/api/v1/items/{id}',",                                          // 11：生成代码的路径参数名与后端不同也能对上
            "            ...options,",                                                         // 12
            "        });",                                                                     // 13
            "    }",                                                                           // 14
            "}",                                                                               // 15
        ],
    };

    private static List<RouteMatch> PolyMatches() =>
        RouteLinker.Match([], PolyEntries, PolyContainers, PolySymbols, p => PolyFiles.TryGetValue(p, out var l) ? l : []);

    [Fact]
    public void Match_GoHttpCalls_SprintfAndConcatenation_PickEntryByMethod()
    {
        var go = PolyMatches().Where(m => m.File == "goweb/packaging.go").Select(m => (m.FromContainerKey, m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            ("goweb", 4, "/api/packaging/${x}", 500L, "py-api"),   // "%s/api/packaging/%s"：开头的 %s 丢掉，其余 %s → ${x}
            ("goweb", 8, "/api/orders", 501L, "py-api"),           // http.MethodPost → POST 的那条
        }, go);
    }

    [Fact]
    public void Match_PythonRequestsAndHttpx_FStrings_ModuleConstants_SkipsFunctionsWithoutHttpCalls()
    {
        var py = PolyMatches().Where(m => m.File == "pyweb/client.py" && !m.Path.StartsWith("grpc:", StringComparison.Ordinal))
            .Select(m => (m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            (5, "/api/packaging/${pid}", 500L, "py-api"),
            (9, "/api/v1/orders", 503L, "py-api"),                 // {base} 是主机地址；{API} = /api/v1
        }, py);
    }

    [Fact]
    public void Match_GrpcClientCalls_GoAndPython_ToGrpcEntriesInOtherContainers()
    {
        var grpc = PolyMatches().Where(m => m.Path.StartsWith("grpc:", StringComparison.Ordinal))
            .Select(m => (m.FromContainerKey, m.File, m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            ("goweb", "goweb/rpc.go", 5, "grpc:ProductCatalogService/ListProducts", 600L, "catalog"),          // 行号是方法名那一行
            ("goweb", "goweb/rpc.go", 10, "grpc:ProductCatalogService/GetProduct", 601L, "catalog"),
            ("goweb", "goweb/rpc.go", 14, "grpc:EmailService/SendOrderConfirmation", 602L, "pyweb"),
            ("pyweb", "pyweb/client.py", 16, "grpc:ProductCatalogService/ListProducts", 600L, "catalog"),     // Python：XxxStub(channel) 赋给变量
        }, grpc);
    }

    [Fact]
    public void Match_GeneratedFrontendSdk_UrlProperty_TrailingSlashAndPathParams()
    {
        var ts = PolyMatches().Where(m => m.FromContainerKey == "frontend").Select(m => (m.Line, m.Path, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[]
        {
            (5, "/api/v1/items", 504L, "py-api"),          // 尾斜杠不影响匹配
            (11, "/api/v1/items/{id}", 505L, "py-api"),    // {id} 对 {item_id}；.put → PUT
        }, ts);
    }

    [Fact]
    public void Match_GeneratedSdkFallback_SkipsMethodsTheFrontendHttpRuleAlreadyCovered()
    {
        // 容器图规则已登记了 readItems 里的调用（第 3 行）：兜底不再按 url: 那一行（第 5 行）重复记一次
        List<Outbound> outbound = [new("frontend", "frontend-http", "http", "GET", null, "/api/v1/items/", "frontend/src/client/sdk.gen.ts", 3, "external:http-service:api-api-v1")];
        var ts = RouteLinker.Match(outbound, PolyEntries, PolyContainers, PolySymbols, p => PolyFiles.TryGetValue(p, out var l) ? l : [])
            .Where(m => m.FromContainerKey == "frontend").Select(m => (m.Line, m.EntrySymbolId)).ToList();
        Assert.Equal(new[] { (3, 504L), (11, 505L) }, ts);
    }

    [Fact]
    public void Match_SameEntryInNestedContainers_ResolvesToTheMostSpecificContainer()
    {
        // 同一个入口符号因为容器嵌套（成员 "" 包住 py-api）出现在两个容器里：不算歧义，取成员目录更具体的那个
        List<ArchNode> containers = [.. PolyContainers, Container("repo-root", "python", "")];
        List<EntryPoint> entries = [.. PolyEntries, new(504, "repo-root", "python.fastapi", "http", "/api/v1/items/", "GET", "py-api/items.py", 7)];
        var ts = RouteLinker.Match([], entries, containers, PolySymbols, p => PolyFiles.TryGetValue(p, out var l) ? l : [])
            .Where(m => m.FromContainerKey == "frontend").Select(m => (m.Line, m.EntrySymbolId, m.ToContainerKey)).ToList();
        Assert.Equal(new[] { (5, 504L, "py-api"), (11, 505L, "py-api") }, ts);
    }

    [Theory]
    [InlineData("/api/v1/items/", "/api/v1/items", true)]
    [InlineData("/api/v1/items", "/api/v1/items/", true)]
    [InlineData("/api/v1/items/{item_id}", "/api/v1/items/{id}", true)]
    [InlineData("/api/articles/:slug", "/api/articles/abc", true)]
    public void PathMatches_TrailingSlashAndParams(string template, string path, bool expected) => Assert.Equal(expected, RouteLinker.PathMatches(template, path));

    [Fact]
    public void Upgrade_GrpcMatches_AddOrRaiseGrpcEdge_HttpUntouched()
    {
        var model = new ArchModel([.. PolyContainers],
            [new ArchEdge("goweb", "catalog", "grpc", 0.7, ArchSource.Discovered, [new Evidence("goweb/main.go", 40, "PRODUCT_CATALOG_SERVICE_ADDR")])],
            [], []);
        var up = RouteLinker.Upgrade(model, [
            new("goweb", "goweb/rpc.go", 5, "grpc:ProductCatalogService/ListProducts", 600, "catalog"),
            new("goweb", "goweb/rpc.go", 14, "grpc:EmailService/SendOrderConfirmation", 602, "pyweb"),
        ]);
        var catalog = Assert.Single(up.Edges, e => e.FromKey == "goweb" && e.ToKey == "catalog");
        Assert.Equal(("grpc", 0.9, 2), (catalog.Protocol, catalog.Confidence, catalog.Evidence.Count)); // 已有的 0.7 边升到 0.9，证据追加
        var email = Assert.Single(up.Edges, e => e.FromKey == "goweb" && e.ToKey == "pyweb");
        Assert.Equal(("grpc", 0.9, "grpc:EmailService/SendOrderConfirmation"), (email.Protocol, email.Confidence, email.Evidence[0].Note));
        Assert.DoesNotContain(up.Edges, e => e.Protocol == "http");
    }
}
