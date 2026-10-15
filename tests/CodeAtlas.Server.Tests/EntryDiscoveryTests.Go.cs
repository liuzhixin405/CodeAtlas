using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Go 入口：net/http / gorilla mux、gin（含跨函数传 group 的前缀链）、echo、chi（Route 闭包前缀）、gRPC 注册、main 包。</summary>
public partial class EntryDiscoveryTests
{
    // 文件内容：行号与符号的 StartLine 对应（Go 方法的 ParentId 可能是 Module——接收者类型在别的文件）
    private static readonly Dictionary<string, string[]> GoFiles = new()
    {
        ["src/frontend/main.go"] =
        [
            "package main",                                                                                       // 1
            "",                                                                                                   // 2
            "func main() {",                                                                                      // 3
            "\tr := mux.NewRouter()",                                                                             // 4
            "\tr.HandleFunc(baseUrl+\"/\", svc.homeHandler).Methods(http.MethodGet, http.MethodHead)",            // 5
            "\tr.HandleFunc(baseUrl+\"/product/{id}\", svc.productHandler).Methods(http.MethodGet, http.MethodHead)", // 6
            "\tr.HandleFunc(baseUrl+\"/cart\", svc.addToCartHandler).Methods(http.MethodPost)",                   // 7
            "\tr.PathPrefix(baseUrl + \"/static/\").Handler(http.StripPrefix(baseUrl+\"/static/\", http.FileServer(http.Dir(\"./static/\"))))", // 8：忽略
            "\tr.HandleFunc(baseUrl+\"/_healthz\", func(w http.ResponseWriter, _ *http.Request) { fmt.Fprint(w, \"ok\") })", // 9：闭包 → main
            "\t// r.HandleFunc(\"/commented\", svc.homeHandler)",                                                // 10：注释
            "}",                                                                                                  // 11
        ],
        ["src/frontend/handlers.go"] =
        [
            "package main",                                                                    // 1
            "",                                                                                // 2
            "func (fe *frontendServer) homeHandler(w http.ResponseWriter, r *http.Request) {", // 3
            "}",                                                                               // 4
            "func (fe *frontendServer) productHandler(w http.ResponseWriter, r *http.Request) {", // 5
            "}",                                                                               // 6
            "func (fe *frontendServer) addToCartHandler(w http.ResponseWriter, r *http.Request) {", // 7
            "}",                                                                               // 8
        ],
        ["src/productcatalogservice/server.go"] =
        [
            "package main",                                                 // 1
            "",                                                             // 2
            "func main() {",                                                // 3
            "\tsrv := grpc.NewServer()",                                    // 4
            "\tsvc := &productCatalog{}",                                   // 5
            "\tpb.RegisterProductCatalogServiceServer(srv, svc)",          // 6
            "\thealthpb.RegisterHealthServer(srv, health.NewServer())",    // 7：实现是外部类型 → 不算
            "}",                                                            // 8
        ],
        ["src/productcatalogservice/product_catalog.go"] =
        [
            "package main",                                                                                     // 1
            "",                                                                                                 // 2
            "type productCatalog struct {",                                                                     // 3
            "}",                                                                                                // 4
            "func (p *productCatalog) ListProducts(context.Context, *pb.Empty) (*pb.ListProductsResponse, error) {", // 5：首参未命名
            "}",                                                                                                // 6
            "func (p *productCatalog) GetProduct(ctx context.Context, req *pb.GetProductRequest) (*pb.Product, error) {", // 7
            "}",                                                                                                // 8
            "func (p *productCatalog) parseCatalog() []*pb.Product {",                                          // 9：未导出
            "}",                                                                                                // 10
            "func (p *productCatalog) Check(ctx context.Context, req *healthpb.HealthCheckRequest) (*healthpb.HealthCheckResponse, error) {", // 11：不在服务接口里
            "}",                                                                                                // 12
            "func (p *productCatalog) Describe(w io.Writer) {",                                                 // 13
            "}",                                                                                                // 14
        ],
        ["src/productcatalogservice/genproto/demo_grpc.pb.go"] =
        [
            "package hipstershop",                                                          // 1
            "type ProductCatalogServiceServer interface {",                                 // 2
            "\tListProducts(context.Context, *Empty) (*ListProductsResponse, error)",      // 3
            "\tGetProduct(context.Context, *GetProductRequest) (*Product, error)",         // 4
            "\tmustEmbedUnimplementedProductCatalogServiceServer()",                       // 5
            "}",                                                                            // 6
        ],
        ["src/shippingservice/main.go"] =
        [
            "package main",                                                                                     // 1
            "",                                                                                                 // 2
            "type server struct{}",                                                                             // 3
            "",                                                                                                 // 4
            "func main() {",                                                                                    // 5
            "\tpb.RegisterShippingServiceServer(grpc.NewServer(), &server{})",                                  // 6：实现直接写成 &server{}
            "}",                                                                                                // 7
            "func (s *server) GetQuote(ctx context.Context, in *pb.GetQuoteRequest) (*pb.GetQuoteResponse, error) {", // 8
            "}",                                                                                                // 9
            "func (s *server) ShipOrder(ctx context.Context, in *pb.ShipOrderRequest) (*pb.ShipOrderResponse, error) {", // 10
            "}",                                                                                                // 11
            "func (s *server) Log(msg string) {}",                                                              // 12：首参不是 context.Context
        ],
        ["realworld/hello.go"] =
        [
            "package main",                                           // 1
            "",                                                       // 2
            "func main() {",                                          // 3
            "\tr := gin.Default()",                                   // 4
            "\tv1 := r.Group(\"/api\")",                               // 5
            "\tusers.UsersRegister(v1.Group(\"/users\"))",             // 6：跨函数传 group
            "\tarticles.ArticlesRegister(v1.Group(\"/articles\"))",    // 7
            "\ttestAuth := r.Group(\"/api/ping\")",                    // 8
            "\ttestAuth.GET(\"/\", func(c *gin.Context) {",            // 9：闭包 → main
            "\t\tc.JSON(200, gin.H{})",                               // 10
            "\t})",                                                   // 11
            "}",                                                      // 12
        ],
        ["realworld/users/routers.go"] =
        [
            "package users",                                   // 1
            "",                                                // 2
            "func UsersRegister(router *gin.RouterGroup) {",   // 3
            "\trouter.POST(\"\", UsersRegistration)",          // 4
            "\trouter.POST(\"/\", UsersRegistration)",         // 5：与上一行同一路由 → 去重
            "\trouter.POST(\"/login\", UsersLogin)",           // 6
            "}",                                               // 7
            "func UsersRegistration(c *gin.Context) {",        // 8
            "}",                                               // 9
            "func UsersLogin(c *gin.Context) {",               // 10
            "}",                                               // 11
        ],
        ["realworld/users/unit_test.go"] =
        [
            "package users",                                  // 1
            "",                                               // 2
            "func TestRegister(t *testing.T) {",              // 3
            "\tUsersRegister(r.Group(\"/users\"))",           // 4：测试里的注册不算前缀
            "}",                                              // 5
        ],
        ["realworld/articles/routers.go"] =
        [
            "package articles",                                                  // 1
            "",                                                                  // 2
            "func ArticlesRegister(router *gin.RouterGroup) {",                  // 3
            "\trouter.GET(\"/:slug\", ArticleRetrieve)",                         // 4
            "\trouter.DELETE(\"/:slug/comments/:id\", authed, ArticleCommentDelete)", // 5：中间件在前，处理函数是最后一个实参
            "}",                                                                 // 6
            "func ArticleRetrieve(c *gin.Context) {",                            // 7
            "}",                                                                 // 8
            "func ArticleCommentDelete(c *gin.Context) {",                       // 9
            "}",                                                                 // 10
        ],
        ["svc/chi.go"] =
        [
            "package main",                                        // 1
            "",                                                    // 2
            "func routes() http.Handler {",                        // 3
            "\tr := chi.NewRouter()",                              // 4
            "\tr.Get(\"/health\", health)",                        // 5
            "\tr.Route(\"/orders\", func(r chi.Router) {",         // 6
            "\t\tr.Post(\"/\", createOrder)",                      // 7
            "\t\tr.Get(\"/{id}\", getOrder)",                      // 8
            "\t})",                                                // 9
            "\tr.Get(\"/after\", health)",                         // 10：Route 闭包结束，前缀不再带
            "\thttp.HandleFunc(\"GET /v2/items/{id}\", getOrder)", // 11：Go 1.22 模式里带方法
            "\tresp, _ := http.Get(\"/api/not-a-route\")",         // 12：客户端调用，不是路由
            "\treturn r",                                          // 13
            "}",                                                   // 14
            "func health(w http.ResponseWriter, r *http.Request) {}",      // 15
            "func createOrder(w http.ResponseWriter, r *http.Request) {}", // 16
            "func getOrder(w http.ResponseWriter, r *http.Request) {}",    // 17
        ],
        ["svc/echo.go"] =
        [
            "package main",                                   // 1
            "",                                               // 2
            "import \"github.com/labstack/echo/v4\"",         // 3
            "",                                               // 4
            "func echoRoutes(e *echo.Echo) {",                // 5
            "\tg := e.Group(\"/admin\")",                     // 6
            "\tg.PUT(\"/users/:id\", updateUser)",            // 7
            "}",                                              // 8
            "func updateUser(c echo.Context) error { return nil }", // 9
        ],
    };

    private static readonly List<MapSymbol> GoSymbols =
    [
        Sym(1000, SymbolKind.Module, "src/frontend/main.go", "src/frontend/main.go", null, 1, 11),
        Sym(1001, SymbolKind.Function, "src/frontend.main", "src/frontend/main.go", 1000, 3, 11),
        Sym(1002, SymbolKind.Module, "src/frontend/handlers.go", "src/frontend/handlers.go", null, 1, 8),
        Sym(1003, SymbolKind.Method, "src/frontend.frontendServer.homeHandler", "src/frontend/handlers.go", 1002, 3, 4),
        Sym(1004, SymbolKind.Method, "src/frontend.frontendServer.productHandler", "src/frontend/handlers.go", 1002, 5, 6),
        Sym(1005, SymbolKind.Method, "src/frontend.frontendServer.addToCartHandler", "src/frontend/handlers.go", 1002, 7, 8),
        Sym(1010, SymbolKind.Module, "src/productcatalogservice/server.go", "src/productcatalogservice/server.go", null, 1, 8),
        Sym(1011, SymbolKind.Function, "src/productcatalogservice.main", "src/productcatalogservice/server.go", 1010, 3, 8),
        Sym(1012, SymbolKind.Module, "src/productcatalogservice/product_catalog.go", "src/productcatalogservice/product_catalog.go", null, 1, 14),
        Sym(1013, SymbolKind.Class, "src/productcatalogservice.productCatalog", "src/productcatalogservice/product_catalog.go", 1012, 3, 4, "private"),
        Sym(1014, SymbolKind.Method, "src/productcatalogservice.productCatalog.ListProducts", "src/productcatalogservice/product_catalog.go", 1013, 5, 6, "public"),
        Sym(1015, SymbolKind.Method, "src/productcatalogservice.productCatalog.GetProduct", "src/productcatalogservice/product_catalog.go", 1013, 7, 8, "public"),
        Sym(1016, SymbolKind.Method, "src/productcatalogservice.productCatalog.parseCatalog", "src/productcatalogservice/product_catalog.go", 1013, 9, 10, "private"),
        Sym(1017, SymbolKind.Method, "src/productcatalogservice.productCatalog.Check", "src/productcatalogservice/product_catalog.go", 1013, 11, 12, "public"),
        Sym(1018, SymbolKind.Method, "src/productcatalogservice.productCatalog.Describe", "src/productcatalogservice/product_catalog.go", 1013, 13, 14, "public"),
        Sym(1019, SymbolKind.Module, "src/productcatalogservice/genproto/demo_grpc.pb.go", "src/productcatalogservice/genproto/demo_grpc.pb.go", null, 1, 6),
        Sym(1020, SymbolKind.Interface, "src/productcatalogservice/genproto.ProductCatalogServiceServer", "src/productcatalogservice/genproto/demo_grpc.pb.go", 1019, 2, 6, "public"),
        Sym(1030, SymbolKind.Module, "src/shippingservice/main.go", "src/shippingservice/main.go", null, 1, 12),
        Sym(1031, SymbolKind.Class, "src/shippingservice.server", "src/shippingservice/main.go", 1030, 3, 3, "private"),
        Sym(1032, SymbolKind.Function, "src/shippingservice.main", "src/shippingservice/main.go", 1030, 5, 7),
        Sym(1033, SymbolKind.Method, "src/shippingservice.server.GetQuote", "src/shippingservice/main.go", 1031, 8, 9, "public"),
        Sym(1034, SymbolKind.Method, "src/shippingservice.server.ShipOrder", "src/shippingservice/main.go", 1031, 10, 11, "public"),
        Sym(1035, SymbolKind.Method, "src/shippingservice.server.Log", "src/shippingservice/main.go", 1031, 12, 12, "public"),
        Sym(1040, SymbolKind.Module, "realworld/hello.go", "realworld/hello.go", null, 1, 12),
        Sym(1041, SymbolKind.Function, "realworld.main", "realworld/hello.go", 1040, 3, 12),
        Sym(1042, SymbolKind.Module, "realworld/users/routers.go", "realworld/users/routers.go", null, 1, 11),
        Sym(1043, SymbolKind.Function, "realworld/users.UsersRegister", "realworld/users/routers.go", 1042, 3, 7, "public"),
        Sym(1044, SymbolKind.Function, "realworld/users.UsersRegistration", "realworld/users/routers.go", 1042, 8, 9, "public"),
        Sym(1045, SymbolKind.Function, "realworld/users.UsersLogin", "realworld/users/routers.go", 1042, 10, 11, "public"),
        Sym(1046, SymbolKind.Module, "realworld/articles/routers.go", "realworld/articles/routers.go", null, 1, 10),
        Sym(1047, SymbolKind.Function, "realworld/articles.ArticlesRegister", "realworld/articles/routers.go", 1046, 3, 6, "public"),
        Sym(1048, SymbolKind.Function, "realworld/articles.ArticleRetrieve", "realworld/articles/routers.go", 1046, 7, 8, "public"),
        Sym(1049, SymbolKind.Function, "realworld/articles.ArticleCommentDelete", "realworld/articles/routers.go", 1046, 9, 10, "public"),
        Sym(1050, SymbolKind.Module, "realworld/users/unit_test.go", "realworld/users/unit_test.go", null, 1, 5),
        Sym(1051, SymbolKind.Function, "realworld/users.TestRegister", "realworld/users/unit_test.go", 1050, 3, 5, "public"),
        Sym(1060, SymbolKind.Module, "svc/chi.go", "svc/chi.go", null, 1, 17),
        Sym(1061, SymbolKind.Function, "svc.routes", "svc/chi.go", 1060, 3, 14),
        Sym(1062, SymbolKind.Function, "svc.health", "svc/chi.go", 1060, 15, 15),
        Sym(1063, SymbolKind.Function, "svc.createOrder", "svc/chi.go", 1060, 16, 16),
        Sym(1064, SymbolKind.Function, "svc.getOrder", "svc/chi.go", 1060, 17, 17),
        Sym(1065, SymbolKind.Module, "svc/echo.go", "svc/echo.go", null, 1, 9),
        Sym(1066, SymbolKind.Function, "svc.echoRoutes", "svc/echo.go", 1065, 5, 8),
        Sym(1067, SymbolKind.Function, "svc.updateUser", "svc/echo.go", 1065, 9, 9),
    ];

    private static readonly List<ArchNode> GoContainers =
    [
        Container("src/frontend", "go", "src/frontend"),
        Container("src/productcatalogservice", "go", "src/productcatalogservice"),
        Container("src/shippingservice", "go", "src/shippingservice"),
        Container("realworld", "go", "realworld"),
        Container("svc", "go", "svc"),
    ];

    private static List<EntryPoint> GoEntries() =>
        EntryDiscovery.Discover(GoSymbols, [], GoContainers, p => GoFiles.TryGetValue(p, out var l) ? l : []);

    private static IEnumerable<(string Container, string Route, string? Method, long Symbol, string File, int Line)> Rows(IEnumerable<EntryPoint> entries) =>
        entries.OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.HttpMethod, StringComparer.Ordinal)
            .Select(e => (e.ContainerKey, e.Route!, e.HttpMethod, e.SymbolId, e.File, e.Line));

    [Fact]
    public void Go_NetHttpAndGorillaMux_MethodsFromMethodsCall_MethodValueHandlersFoundInPackage()
    {
        var http = GoEntries().Where(e => e.RuleId == "go.nethttp").ToList();
        Assert.All(http, e => Assert.Equal("http", e.Kind));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("src/frontend", "/", "GET", 1003, "src/frontend/main.go", 5),                 // baseUrl+"/"：未知前缀变量丢掉；Methods 取第一个
            ("src/frontend", "/_healthz", null, 1001, "src/frontend/main.go", 9),          // 闭包 → 落在 main 上；没写 Methods → null
            ("src/frontend", "/cart", "POST", 1005, "src/frontend/main.go", 7),            // svc.addToCartHandler → 同目录 handlers.go 的方法
            ("src/frontend", "/product/{id}", "GET", 1004, "src/frontend/main.go", 6),
            ("svc", "/v2/items/{id}", "GET", 1064, "svc/chi.go", 11),                      // "GET /v2/items/{id}"：方法写在模式里
        }, Rows(http));
        // PathPrefix 与注释里的不算；http.Get 是客户端调用
        Assert.DoesNotContain(GoEntries(), e => e.Route is "/static" or "/static/" or "/commented" or "/api/not-a-route");
    }

    [Fact]
    public void Go_Gin_GroupPrefixes_FollowGroupsPassedToRegisterFunctions_TestFilesIgnored()
    {
        var gin = GoEntries().Where(e => e.RuleId == "go.gin").ToList();
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("realworld", "/api/articles/:slug", "GET", 1048, "realworld/articles/routers.go", 4),            // 调用处 v1.Group("/articles") + 被调函数里的 "/:slug"
            ("realworld", "/api/articles/:slug/comments/:id", "DELETE", 1049, "realworld/articles/routers.go", 5),
            ("realworld", "/api/ping", "GET", 1041, "realworld/hello.go", 9),                                 // 同文件 group 变量；闭包 → main
            ("realworld", "/api/users", "POST", 1044, "realworld/users/routers.go", 4),                       // "" 与 "/" 两行同一路由，留第一处
            ("realworld", "/api/users/login", "POST", 1045, "realworld/users/routers.go", 6),
        }, Rows(gin));
        // unit_test.go 里的 UsersRegister(r.Group("/users")) 不产生没有 /api 的前缀
        Assert.DoesNotContain(GoEntries(), e => e.Route == "/users/login");
    }

    [Fact]
    public void Go_Echo_And_Chi_RouteClosurePrefix()
    {
        var entries = GoEntries();
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("svc", "/admin/users/:id", "PUT", 1067, "svc/echo.go", 7),
        }, Rows(entries.Where(e => e.RuleId == "go.echo")));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("svc", "/after", "GET", 1062, "svc/chi.go", 10),          // 闭包结束后不再带 /orders
            ("svc", "/health", "GET", 1062, "svc/chi.go", 5),
            ("svc", "/orders", "POST", 1063, "svc/chi.go", 7),         // r.Route("/orders", func(r chi.Router){ r.Post("/") })
            ("svc", "/orders/{id}", "GET", 1064, "svc/chi.go", 8),
        }, Rows(entries.Where(e => e.RuleId == "go.chi")));
    }

    [Fact]
    public void Go_GrpcRegister_ImplTypeExportedContextMethods_FilteredByServerInterface()
    {
        var grpc = GoEntries().Where(e => e.RuleId == "go.grpc").ToList();
        Assert.All(grpc, e => Assert.Equal(("grpc", (string?)null), (e.Kind, e.HttpMethod)));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            // svc := &productCatalog{} → productCatalog 的导出方法 ∩ ProductCatalogServiceServer 接口（Check / Describe / parseCatalog 不算）
            ("src/productcatalogservice", "ProductCatalogService/GetProduct", null, 1015, "src/productcatalogservice/product_catalog.go", 7),
            ("src/productcatalogservice", "ProductCatalogService/ListProducts", null, 1014, "src/productcatalogservice/product_catalog.go", 5),
            // 找不到服务接口：导出且首参是 context.Context 的方法（Log 不算）
            ("src/shippingservice", "ShippingService/GetQuote", null, 1033, "src/shippingservice/main.go", 8),
            ("src/shippingservice", "ShippingService/ShipOrder", null, 1034, "src/shippingservice/main.go", 10),
        }, Rows(grpc));
        Assert.DoesNotContain(GoEntries(), e => e.Route is not null && e.Route.StartsWith("Health/", StringComparison.Ordinal)); // health.NewServer() 是外部类型
    }

    [Fact]
    public void Go_MainFunctions_OfMainPackages_AreHostedEntries_NamedByDirectory()
    {
        var hosted = GoEntries().Where(e => e.Kind == "hosted").ToList();
        Assert.All(hosted, e => Assert.Equal("go.main", e.RuleId));
        Assert.Equal(new (string, string, string?, long, string, int)[]
        {
            ("realworld", "realworld", null, 1041, "realworld/hello.go", 3),
            ("src/frontend", "frontend", null, 1001, "src/frontend/main.go", 3),
            ("src/productcatalogservice", "productcatalogservice", null, 1011, "src/productcatalogservice/server.go", 3),
            ("src/shippingservice", "shippingservice", null, 1032, "src/shippingservice/main.go", 5),
        }, Rows(hosted));
    }
}
