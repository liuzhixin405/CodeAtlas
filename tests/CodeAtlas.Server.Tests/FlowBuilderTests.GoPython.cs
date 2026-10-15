using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Go / Python 的时序图：方法标签（Go 的接收者类型可能在别的文件，从限定名取）、gRPC 客户端调用续接到对端 grpc 入口。</summary>
public partial class FlowBuilderTests
{
    private static ArchNode GoContainer(string key, params string[] members) => new(key, ArchNodeKind.Container, "go", key, key, ArchSource.Discovered, [], members, []);

    [Fact]
    public void Go_MethodLabels_FromQualifiedName_GrpcCallHopsToOtherContainersGrpcEntry()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "src/frontend/handlers.go", "src/frontend/handlers.go", null, 1, 20),
            Sym(2, SymbolKind.Method, "src/frontend.frontendServer.homeHandler", "src/frontend/handlers.go", 1, 3, 10),   // 接收者 frontendServer 在 main.go → ParentId 是 Module
            Sym(3, SymbolKind.Module, "src/frontend/rpc.go", "src/frontend/rpc.go", null, 1, 20),
            Sym(4, SymbolKind.Method, "src/frontend.frontendServer.getProducts", "src/frontend/rpc.go", 3, 5, 9),
            Sym(5, SymbolKind.Function, "src/frontend/v1.2.Render", "src/frontend/v1.2/render.go", null, 1, 3),         // 目录名带点的包里的顶层函数
            Sym(10, SymbolKind.Class, "src/productcatalogservice.productCatalog", "src/productcatalogservice/product_catalog.go", null, 3, 4),
            Sym(11, SymbolKind.Method, "src/productcatalogservice.productCatalog.ListProducts", "src/productcatalogservice/product_catalog.go", 10, 5, 8),
            Sym(12, SymbolKind.Method, "src/productcatalogservice.productCatalog.parseCatalog", "src/productcatalogservice/product_catalog.go", 10, 9, 12),
            Sym(90, SymbolKind.External, "external:http.Error", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 4, EdgeKind.Calls, 1.0),
            new(2, 5, EdgeKind.Calls, 1.0),
            new(2, 90, EdgeKind.Calls, 0.5),
            new(11, 12, EdgeKind.Calls, 1.0),
        ];
        List<ArchNode> containers = [GoContainer("src/frontend", "src/frontend"), GoContainer("src/productcatalogservice", "src/productcatalogservice")];
        var entry = new EntryPoint(2, "src/frontend", "go.nethttp", "http", "/", "GET", "src/frontend/main.go", 150);
        var grpcEntry = new EntryPoint(11, "src/productcatalogservice", "go.grpc", "grpc", "ProductCatalogService/ListProducts", null, "src/productcatalogservice/product_catalog.go", 5);
        List<RouteMatch> matches = [new("src/frontend", "src/frontend/rpc.go", 7, "grpc:ProductCatalogService/ListProducts", 11, "src/productcatalogservice")];

        var f = FlowBuilder.Build(symbols, edges, entry, containers, matches, [entry, grpcEntry]);
        Assert.Equal(("frontendServer.homeHandler", "entry"), (f.Root.Label, f.Root.Kind));
        Assert.Equal(new[] { "frontendServer.getProducts", "Render", "Error" }, f.Root.Children.Select(c => c.Label));
        Assert.Equal("ext|http", f.Root.Children[2].Actor);

        var hop = Assert.Single(f.Root.Children[0].Children);
        Assert.Equal(("hop", "gRPC ProductCatalogService/ListProducts", 0.9, "→ src/productcatalogservice ProductCatalogService/ListProducts"), (hop.Kind, hop.Label, hop.Confidence, hop.Note));
        var remote = Assert.Single(hop.Children);
        Assert.Equal(("productCatalog.ListProducts", "src/productcatalogservice|src/productcatalogservice"), (remote.Label, remote.Actor));
        Assert.Equal("productCatalog.parseCatalog", Assert.Single(remote.Children).Label);
        Assert.Equal(new[] { "src/frontend|src/frontend", "src/productcatalogservice|src/productcatalogservice", "ext|http" }, f.Actors.Select(a => a.Key));
    }

    [Fact]
    public void Grpc_GeneratedStubsHiddenBehindHop_HopCarryingCalleeBeatsFanout_HopsChainThroughHoppedContainer()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "web.server.home", "web/handlers.go", null, 1, 20),
            // 6 个按名字排在前面的普通调用：不提升的话 zCheckout 会被扇出上限 6 折进"还有 1 个"
            Sym(2, SymbolKind.Function, "web.a1", "web/util.go", null, 1, 1),
            Sym(3, SymbolKind.Function, "web.a2", "web/util.go", null, 2, 2),
            Sym(4, SymbolKind.Function, "web.a3", "web/util.go", null, 3, 3),
            Sym(5, SymbolKind.Function, "web.a4", "web/util.go", null, 4, 4),
            Sym(6, SymbolKind.Function, "web.a5", "web/util.go", null, 5, 5),
            Sym(7, SymbolKind.Function, "web.a6", "web/util.go", null, 6, 6),
            Sym(8, SymbolKind.Method, "web.server.zCheckout", "web/rpc.go", null, 1, 6),
            Sym(9, SymbolKind.Method, "web/genproto.CheckoutServiceClient.PlaceOrder", "web/genproto/demo_grpc.pb.go", null, 10, 12), // 生成的桩
            Sym(10, SymbolKind.Function, "web/genproto.NewCheckoutServiceClient", "web/genproto/demo_grpc.pb.go", null, 3, 5),
            Sym(20, SymbolKind.Method, "checkout.checkoutService.PlaceOrder", "checkout/main.go", null, 1, 10),
            Sym(30, SymbolKind.Method, "payment.service.Charge", "payment/charge.go", null, 1, 5),
        ];
        List<MapEdge> edges =
        [
            new(1, 2, EdgeKind.Calls), new(1, 3, EdgeKind.Calls), new(1, 4, EdgeKind.Calls), new(1, 5, EdgeKind.Calls), new(1, 6, EdgeKind.Calls), new(1, 7, EdgeKind.Calls),
            new(1, 8, EdgeKind.Calls),
            new(8, 9, EdgeKind.Calls, 0.9), new(8, 10, EdgeKind.Calls, 0.95),
        ];
        List<ArchNode> containers = [GoContainer("web", "web"), GoContainer("checkout", "checkout"), GoContainer("payment", "payment")];
        var entry = new EntryPoint(1, "web", "go.nethttp", "http", "/", "GET", "web/main.go", 10);
        List<EntryPoint> all = [entry, new(20, "checkout", "go.grpc", "grpc", "CheckoutService/PlaceOrder", null, "checkout/main.go", 1), new(30, "payment", "go.grpc", "grpc", "PaymentService/Charge", null, "payment/charge.go", 1)];
        List<RouteMatch> matches =
        [
            new("web", "web/rpc.go", 3, "grpc:CheckoutService/PlaceOrder", 20, "checkout"),
            new("checkout", "checkout/main.go", 5, "grpc:PaymentService/Charge", 30, "payment"),   // 对端容器自己的出口
        ];

        var f = FlowBuilder.Build(symbols, edges, entry, containers, matches, all);
        Assert.Equal("server.zCheckout", f.Root.Children[0].Label);                       // 带续接的调用排到前面
        Assert.Equal(new[] { "a1", "a2", "a3", "a4", "a5", "还有 1 个" }, f.Root.Children.Skip(1).Select(c => c.Label));
        var hop = Assert.Single(f.Root.Children[0].Children);                               // 生成的 XxxClient.PlaceOrder / NewXxxClient 不再单列
        Assert.Equal("gRPC CheckoutService/PlaceOrder", hop.Label);
        var checkout = Assert.Single(hop.Children);
        var second = Assert.Single(checkout.Children);                                      // checkout 容器里的出口接着续接
        Assert.Equal(("hop", "gRPC PaymentService/Charge"), (second.Kind, second.Label));
        Assert.Equal("service.Charge", Assert.Single(second.Children).Label);
    }

    [Fact]
    public void CalleeWhoseCalleeHops_RanksBeforePlainCalls_SoFanoutKeepsTheCrossServiceBranch()
    {
        // microservices-demo 首页：homeHandler → chooseAd → getAd → gRPC AdService；chooseAd 自己不续接，不提升会被 6 个普通调用挤进"还有 N 个"
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "web.server.home", "web/handlers.go", null, 1, 20),
            Sym(2, SymbolKind.Function, "web.a1", "web/util.go", null, 1, 1),
            Sym(3, SymbolKind.Function, "web.a2", "web/util.go", null, 2, 2),
            Sym(4, SymbolKind.Function, "web.a3", "web/util.go", null, 3, 3),
            Sym(5, SymbolKind.Function, "web.a4", "web/util.go", null, 4, 4),
            Sym(6, SymbolKind.Function, "web.a5", "web/util.go", null, 5, 5),
            Sym(7, SymbolKind.Function, "web.a6", "web/util.go", null, 6, 6),
            Sym(8, SymbolKind.Method, "web.server.zChooseAd", "web/handlers.go", null, 30, 35),
            Sym(9, SymbolKind.Method, "web.server.getAd", "web/rpc.go", null, 1, 6),
            Sym(20, SymbolKind.Method, "ad.AdServiceImpl.getAds", "ad/AdService.java", null, 1, 10),
        ];
        List<MapEdge> edges =
        [
            new(1, 2, EdgeKind.Calls), new(1, 3, EdgeKind.Calls), new(1, 4, EdgeKind.Calls), new(1, 5, EdgeKind.Calls), new(1, 6, EdgeKind.Calls), new(1, 7, EdgeKind.Calls),
            new(1, 8, EdgeKind.Calls), new(8, 9, EdgeKind.Calls),
        ];
        List<ArchNode> containers = [GoContainer("web", "web"), GoContainer("ad", "ad")];
        var entry = new EntryPoint(1, "web", "go.nethttp", "http", "/", "GET", "web/main.go", 10);
        List<EntryPoint> all = [entry, new(20, "ad", "java.grpc", "grpc", "AdService/GetAds", null, "ad/AdService.java", 1)];
        List<RouteMatch> matches = [new("web", "web/rpc.go", 3, "grpc:AdService/GetAds", 20, "ad")];

        var f = FlowBuilder.Build(symbols, edges, entry, containers, matches, all);
        Assert.Equal("server.zChooseAd", f.Root.Children[0].Label);
        Assert.Equal(new[] { "a1", "a2", "a3", "a4", "a5", "还有 1 个" }, f.Root.Children.Skip(1).Select(c => c.Label));
        var getAd = Assert.Single(f.Root.Children[0].Children);
        Assert.Equal(("server.getAd", "gRPC AdService/GetAds"), (getAd.Label, Assert.Single(getAd.Children).Label));
    }

    [Fact]
    public void GrpcExitsWithoutEntries_ContainerWithoutSymbolsStopsAtHop_ExternalIsLeaf_DuplicatesOfMatchedHopsSkipped()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "web.server.home", "web/handlers.go", null, 1, 10),
            Sym(2, SymbolKind.Method, "web.server.getCurrencies", "web/rpc.go", null, 1, 5),
            Sym(3, SymbolKind.Method, "web.server.getAd", "web/rpc.go", null, 6, 10),
            Sym(4, SymbolKind.Method, "web.server.getProducts", "web/rpc.go", null, 11, 15),
            Sym(5, SymbolKind.Method, "web/genproto.CurrencyServiceClient.GetSupportedCurrencies", "web/genproto/demo_grpc.pb.go", null, 1, 3),
            Sym(20, SymbolKind.Method, "catalog.productCatalog.ListProducts", "catalog/c.go", null, 1, 5),
        ];
        List<MapEdge> edges = [new(1, 2, EdgeKind.Calls), new(1, 3, EdgeKind.Calls), new(1, 4, EdgeKind.Calls), new(2, 5, EdgeKind.Calls, 0.9)];
        List<ArchNode> containers = [GoContainer("web", "web"), GoContainer("catalog", "catalog"), new("currency", ArchNodeKind.Container, "node-service", "currencyservice", "currency", ArchSource.Discovered, [], ["currency"], [])];
        List<ArchNode> externals = [new("external:grpc-service:adservice", ArchNodeKind.External, "grpc-service", "AdService", null, ArchSource.Discovered, [], [], [])];
        List<Outbound> outbound =
        [
            new("web", "grpc-service", "grpc", "CurrencyService", null, null, "web/rpc.go", 2, "currency"),        // Node 服务：有容器没符号
            new("web", "grpc-service", "grpc", "AdService", null, null, "web/rpc.go", 7, "external:grpc-service:adservice"),
            new("web", "grpc-service", "grpc", "ProductCatalogService", null, null, "web/rpc.go", 12, "catalog"),  // 已经按方法对上（第 13 行）→ 不重复
        ];
        var entry = new EntryPoint(1, "web", "go.nethttp", "http", "/", "GET", "web/main.go", 1);
        List<EntryPoint> all = [entry, new(20, "catalog", "go.grpc", "grpc", "ProductCatalogService/ListProducts", null, "catalog/c.go", 1)];
        List<RouteMatch> matches = [new("web", "web/rpc.go", 13, "grpc:ProductCatalogService/ListProducts", 20, "catalog")];

        var f = FlowBuilder.Build(symbols, edges, entry, containers, matches, all, outbound: outbound, externalNodes: externals);
        var byLabel = f.Root.Children.ToDictionary(c => c.Label);
        var currency = Assert.Single(byLabel["server.getCurrencies"].Children);                   // 生成的桩被 hop 代替
        Assert.Equal(("hop", "gRPC CurrencyService", "currency|currency", "→ currencyservice（没有索引到符号，停在这里）"), (currency.Kind, currency.Label, currency.Actor, currency.Note));
        Assert.Empty(currency.Children);
        var ad = Assert.Single(byLabel["server.getAd"].Children);
        Assert.Equal(("http", "gRPC AdService", "external:grpc-service:adservice"), (ad.Kind, ad.Label, ad.Actor));
        var products = Assert.Single(byLabel["server.getProducts"].Children);
        Assert.Equal("gRPC ProductCatalogService/ListProducts", products.Label);
    }

    [Fact]
    public void Python_MethodLabels_ClassDotMethod_ModuleFunctionsBareName()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "app.api.routes.items", "backend/app/api/routes/items.py", null, 1, 30),
            Sym(2, SymbolKind.Function, "app.api.routes.items.read_items", "backend/app/api/routes/items.py", 1, 5, 12),
            Sym(3, SymbolKind.Class, "app.crud.ItemRepo", "backend/app/crud.py", null, 1, 20),
            Sym(4, SymbolKind.Method, "app.crud.ItemRepo.list_items", "backend/app/crud.py", 3, 3, 9),
        ];
        List<MapEdge> edges = [new(2, 4, EdgeKind.Calls, 1.0)];
        var entry = new EntryPoint(2, "backend", "python.fastapi", "http", "/api/v1/items/", "GET", "backend/app/api/routes/items.py", 5);
        var f = FlowBuilder.Build(symbols, edges, entry, [GoContainer("backend", "backend")], [], [entry]);
        Assert.Equal("read_items", f.Root.Label);
        Assert.Equal("ItemRepo.list_items", Assert.Single(f.Root.Children).Label);
    }
}
