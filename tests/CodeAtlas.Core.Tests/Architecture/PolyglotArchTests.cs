using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Tests.Architecture;

public class PolyglotArchTests : IDisposable
{
    private readonly PolyglotFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private RuleContext Context()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        return new RuleContext(inv, ContainerDiscovery.Discover(inv));
    }

    [Fact]
    public void Inventory_GoModules_DirectRequiresOnly_MainPackages_AndPorts()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        Assert.Equal(["go/gw", "go/shop", "go/single"], inv.GoModules.Select(m => m.Dir));
        var shop = inv.GoModules.Single(m => m.Dir == "go/shop");
        Assert.Equal(("example.com/shop", "go/shop/go.mod"), (shop.ModulePath, shop.File));
        // require ( … ) 块与单行 require 都读；// indirect 的不算
        Assert.Equal(new (string, int)[]
        {
            ("cloud.google.com/go/profiler", 6), ("github.com/redis/go-redis/v9", 7), ("github.com/segmentio/kafka-go", 8), ("go.opentelemetry.io/otel", 9),
            ("go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc", 10), ("google.golang.org/grpc", 11), ("gorm.io/driver/postgres", 12), ("gorm.io/gorm", 13),
            ("github.com/rabbitmq/amqp091-go", 17),
        }, shop.Requires.Select(r => (r.Name, r.Line)));
        Assert.All(shop.Requires, r => Assert.Equal("go/shop/go.mod", r.File));

        Assert.Equal(["go/shop/cmd/api", "go/shop/cmd/worker"], shop.Mains.Select(m => m.Dir));
        var api = shop.Mains[0];
        Assert.Equal(("go/shop/cmd/api/main.go", 14), (api.File, api.Line)); // func main() 那一行；main_test.go 不算
        Assert.Equal([8081, 9090], api.Ports); // port = "8081"（PORT 环境变量的缺省值）+ Dockerfile EXPOSE
        Assert.Equal("go/shop/cmd/api/Dockerfile", api.Dockerfile);
        Assert.Empty(shop.Mains[1].Ports);

        var single = Assert.Single(inv.GoModules.Single(m => m.Dir == "go/single").Mains);
        Assert.Equal(("go/single", 9), (single.Dir, single.Line));
        Assert.Equal([50051], single.Ports); // net.Listen("tcp", ":50051")

        Assert.Contains("go/shop/cmd/api/main.go", inv.SourceFiles);
        Assert.Contains("py/cart/app/clients.py", inv.SourceFiles);
    }

    [Fact]
    public void Inventory_PythonProjects_Dependencies_Deployability_Ports_AndNodeServices()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        Assert.Equal(["py/blog", "py/cart", "py/lib", "py/mailer", "py/tests", "py/ws", "py/ws/svc"], inv.PythonProjects.Select(p => p.Dir));

        var cart = inv.PythonProjects.Single(p => p.Dir == "py/cart");
        // [project].dependencies：去掉 extras 与版本；[dependency-groups] 的开发依赖不算
        Assert.Equal(new (string, int)[]
        {
            ("fastapi", 4), ("psycopg", 5), ("sqlmodel", 6), ("redis", 7), ("celery", 8), ("sentry-sdk", 9), ("emails", 10), ("opentelemetry-exporter-otlp-proto-grpc", 11),
        }, cart.Dependencies.Select(d => (d.Name, d.Line)));
        Assert.All(cart.Dependencies, d => Assert.Equal("py/cart/pyproject.toml", d.File));
        Assert.Equal(("cart", "fastapi", false), (cart.Name, cart.Framework, cart.IsTest));
        Assert.Equal([8000, 50061], cart.Ports); // uvicorn.run(port=8000) + add_insecure_port("[::]:50061")
        Assert.Contains(cart.DeployEvidence, e => e.File == "py/cart/app/main.py" && e.Line == 4);

        // requirements.in 在就只读 .in（pip-tools 的直接依赖），编译出来的 .txt 里的 redis 是传递依赖
        var mailer = inv.PythonProjects.Single(p => p.Dir == "py/mailer");
        Assert.Equal(new (string, string, int)[] { ("grpcio", "py/mailer/requirements.in", 1), ("pika", "py/mailer/requirements.in", 2) },
            mailer.Dependencies.Select(d => (d.Name, d.File, d.Line)));
        Assert.Equal(("grpc", "py/mailer/Dockerfile"), (mailer.Framework, mailer.Dockerfile));
        Assert.Equal([8080], mailer.Ports);

        var blog = inv.PythonProjects.Single(p => p.Dir == "py/blog");
        Assert.Equal(new (string, int)[] { ("django", 1), ("djangorestframework", 4) }, blog.Dependencies.Select(d => (d.Name, d.Line))); // 注释、-r 跳过
        Assert.Equal("django", blog.Framework);
        Assert.Contains(blog.DeployEvidence, e => e.File == "py/blog/manage.py");

        Assert.True(inv.PythonProjects.Single(p => p.Dir == "py/tests").IsTest);
        Assert.Empty(inv.PythonProjects.Single(p => p.Dir == "py/lib").DeployEvidence);
        // 工作区根：scripts/ 下的 __main__ 是工具脚本，不算入口；成员 svc 靠 Flask( 算
        Assert.Empty(inv.PythonProjects.Single(p => p.Dir == "py/ws").DeployEvidence);
        Assert.Equal(("flask", "py/ws/svc/svc/app.py", 2), (inv.PythonProjects.Single(p => p.Dir == "py/ws/svc").Framework, inv.PythonProjects.Single(p => p.Dir == "py/ws/svc").DeployEvidence.Single().File, inv.PythonProjects.Single(p => p.Dir == "py/ws/svc").DeployEvidence.Single().Line!.Value));

        var node = Assert.Single(inv.NodeServices);
        Assert.Equal(("node/payment", "payment", "@grpc/grpc-js", "node/payment/Dockerfile"), (node.Dir, node.Name, node.Server, node.Dockerfile));
        Assert.Equal([50052], node.Ports);
    }

    [Fact]
    public void Containers_GoMainsPythonAppsNodeServices_WithMembersAndPorts()
    {
        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_fx.Root));
        Assert.Equal(["dotnet/Ledger", "go/gw", "go/shop/cmd/api", "go/shop/cmd/worker", "go/single", "node/payment", "py/blog", "py/cart", "py/mailer", "py/ws/svc", "web"], containers.Select(c => c.Key));

        var api = containers.Single(c => c.Key == "go/shop/cmd/api");
        Assert.Equal(("go-service", "api", "go/shop/cmd/api"), (api.SubKind, api.Name, api.Path));
        // 同模块两个 main：各自的 main 目录 + 模块里不含别的 main 的包目录
        Assert.Equal(["go/shop/cmd/api", "go/shop/genproto", "go/shop/internal/store"], api.Members);
        Assert.Equal([8081, 9090], api.Ports);
        Assert.Contains(api.Evidence, e => e.File == "go/shop/cmd/api/main.go" && e.Line == 14);
        Assert.Equal(["go/shop/cmd/worker", "go/shop/genproto", "go/shop/internal/store"], containers.Single(c => c.Key == "go/shop/cmd/worker").Members);

        var single = containers.Single(c => c.Key == "go/single");
        Assert.Equal(("go-service", "pricing"), (single.SubKind, single.Name)); // 模块根是 main：名字取模块路径最后一段
        Assert.Equal(["go/single"], single.Members);
        Assert.Equal([50051], single.Ports);

        var cart = containers.Single(c => c.Key == "py/cart");
        Assert.Equal(("python-service", "cart"), (cart.SubKind, cart.Name));
        Assert.Equal(["py/cart"], cart.Members);
        Assert.Equal([8000, 50061], cart.Ports);
        Assert.Contains(cart.Evidence, e => e.File == "py/cart/pyproject.toml");
        Assert.Equal(("python-service", "blog"), (containers.Single(c => c.Key == "py/blog").SubKind, containers.Single(c => c.Key == "py/blog").Name));
        Assert.Equal([8080], containers.Single(c => c.Key == "py/mailer").Ports);

        var node = containers.Single(c => c.Key == "node/payment");
        Assert.Equal(("node-service", "payment"), (node.SubKind, node.Name));
        Assert.Equal([50052], node.Ports);
    }

    [Fact]
    public void PackageRule_GoModAndPythonDependencies_MapToExternals_OrmOnlyWhenNoDriver()
    {
        var ctx = Context();
        new PackageReferenceRule().Apply(ctx);
        string[] Edges(string c) => ctx.DirectEdges.Where(d => d.ContainerKey == c).Select(d => $"{d.ExternalKey} {d.Protocol} {d.Evidence.Line}").ToArray();

        string[] shop =
        [
            "external:observability:google-cloud-profiler http 6", "external:cache:redis redis 7", "external:broker:kafka kafka 8",
            "external:observability:opentelemetry-collector otlp 10", "external:database:postgresql sql 12", "external:broker:rabbitmq amqp 17",
        ];
        Assert.Equal(shop, Edges("go/shop/cmd/api")); // otel API、grpc 不是外部系统；gorm 有驱动时不另造节点
        Assert.Equal(shop, Edges("go/shop/cmd/worker"));
        Assert.Equal(["external:database:数据库-gorm sql 5"], Edges("go/single")); // 只有 ORM、没有驱动
        Assert.Equal(
        [
            "external:database:postgresql sql 5", "external:cache:redis redis 7", "external:broker:celery-任务队列 celery 8",
            "external:observability:sentry http 9", "external:email:smtp-邮件服务 smtp 10", "external:observability:opentelemetry-collector otlp 11",
        ], Edges("py/cart"));
        Assert.Equal(["external:broker:rabbitmq amqp 2"], Edges("py/mailer"));
        Assert.Empty(Edges("py/blog"));
        Assert.Equal("数据库（GORM）", ctx.Externals["external:database:数据库-gorm"].Name);
    }

    [Fact]
    public void OutboundRules_GoAndPython_HttpAndGrpcChannels_EnvVarsAsConfigKeys_SkipCommentsTestsAndUnresolved()
    {
        var ctx = Context();
        new GoOutboundRule().Apply(ctx);
        new PythonOutboundRule().Apply(ctx);
        var list = ctx.Outbound.OrderBy(o => o.ContainerKey, StringComparer.Ordinal).ThenBy(o => o.Line)
            .Select(o => (o.ContainerKey, o.Line, o.Protocol, o.ConfigKey, o.Url)).ToList();
        Assert.Equal(new (string, int, string, string?, string?)[]
        {
            ("go/gw", 7, "http", "ASSISTANT_ADDR", null),                                    // assist.go：url := "http://" + fe.assistantAddr → 同包 main.go 的 mustMapEnv(&svc.assistantAddr, …)
            ("go/gw", 22, "grpc", "AD_SERVICE_ADDR", null),                                  // grpc.NewClient(addr) 的 addr 是 mustConnGRPC 的形参：在调用处取实参
            ("go/gw", 23, "grpc", "COLLECTOR_SERVICE_ADDR", null),
            ("go/shop/cmd/api", 19, "grpc", "CART_SERVICE_ADDR", null),                       // grpc.NewClient(os.Getenv(…))
            ("go/shop/cmd/api", 28, "http", null, "https://api.github.com/repos"),
            ("go/shop/cmd/api", 30, "http", "BILLING_URL", null),                             // billing+billingPath → 回溯 billing := os.Getenv(…)
            ("py/cart", 8, "grpc", "PRICING_SERVICE_ADDR", "localhost:50051"),                // environ.get 的缺省值记为地址
            ("py/cart", 10, "grpc", null, "catalog:7000"),
            ("py/cart", 14, "http", null, "https://hooks.slack.com/services/x"),              // 第 13 行注释不算
            ("py/cart", 17, "http", "CATALOG_URL", null),                                     // httpx.Client(base_url=变量) → 回溯到 os.getenv
        }, list); // requests.get(url) 的参数解析不出来：不登记；测试文件不算
        Assert.All(ctx.Outbound, o => Assert.Null(o.TargetKey));
    }

    [Fact]
    public void GrpcServiceRule_MatchesClientStubsToServerRegistrations_AcrossLanguages()
    {
        var model = ArchitectureScanner.Scan(_fx.Root);
        var grpc = model.Edges.Where(e => e.Protocol == "grpc").Select(e => $"{e.FromKey} → {e.ToKey} {e.Confidence} {string.Join(",", e.Evidence.Select(x => $"{x.File}:{x.Line}"))}").ToList();
        Assert.Equal(
        [
            "dotnet/Ledger → go/single 0.9 dotnet/Ledger/Program.cs:5",                 // C# new PricingService.PricingServiceClient → Go RegisterPricingServiceServer；第 4 行通道并入
            "go/gw → external:grpc-service:adservice 0.5 go/gw/main.go:24",            // 没人提供 AdService：挂外部；只有 AD_SERVICE_ADDR 配置键的通道（第 22 行）让给桩
            "go/gw → external:grpc-service:collector-service-addr 0.5 go/gw/main.go:23", // 没有桩解释的通道照旧按配置键命名
            "go/shop/cmd/api → dotnet/Ledger 0.9 go/shop/cmd/api/main.go:22",           // Go NewLedgerClient → C# MapGrpcService<LedgerApi>（: Ledger.LedgerBase）
            "go/shop/cmd/api → external:grpc-service:auditservice 0.5 go/shop/cmd/api/main.go:25", // pricing 与 mailer 都注册 AuditService：不猜
            "go/shop/cmd/api → external:grpc-service:unknownservice 0.5 go/shop/cmd/api/main.go:24",
            "go/shop/cmd/api → node/payment 0.9 go/shop/cmd/api/main.go:21",            // Node server.addService(proto.PaymentService.service, …)
            "go/shop/cmd/api → py/cart 0.9 go/shop/cmd/api/main.go:20",                 // Python add_CartServiceServicer_to_server；第 19 行 CART_SERVICE_ADDR 通道并入
            "go/shop/cmd/api → py/mailer 0.9 go/shop/cmd/api/main.go:23",
            "py/cart → go/shop/cmd/api 0.9 py/cart/app/clients.py:10",                  // CatalogServiceStub → Go RegisterCatalogServiceServer（生成代码里的定义不算）
            "py/cart → go/single 0.9 py/cart/app/clients.py:9",
        ], grpc);
        // 非 proto 包的 kms.NewKeyManagementClient、注释里的、Health 服务、测试文件里的：都不是 gRPC 客户端
        Assert.DoesNotContain(model.Outbound, o => o.ClientName is "KeyManagement" or "CommentedService" or "TestOnlyService" or "Health");
        Assert.Contains(model.Outbound, o => o.RuleId == "grpc-service" && o.ClientName == "CartService" && o.TargetKey == "py/cart" && o.Line == 20);
    }

    [Fact]
    public void Scan_FullModel_PolyglotNodesAndEdgesPinned()
    {
        var model = ArchitectureScanner.Scan(_fx.Root);
        Assert.Equal(
        [
            "external:broker:celery-任务队列", "external:broker:kafka", "external:broker:rabbitmq", "external:cache:redis",
            "external:database:postgresql", "external:database:sqlite", "external:database:数据库-gorm", "external:email:smtp-邮件服务",
            "external:grpc-service:adservice", "external:grpc-service:auditservice", "external:grpc-service:collector-service-addr", "external:grpc-service:unknownservice",
            "external:http-service:api-api-v1", "external:http-service:api-github-com", "external:http-service:assistant-addr", "external:http-service:billing-url", "external:http-service:catalog-url", "external:http-service:hooks-slack-com",
            "external:observability:google-cloud-profiler", "external:observability:opentelemetry-collector", "external:observability:sentry",
        ], model.Nodes.Where(n => n.Kind == ArchNodeKind.External).Select(n => n.Key));
        Assert.Equal(39, model.Edges.Count); // gw 3、api 14、worker 6、pricing 1、cart 10、mailer 2、blog 1、Ledger 1、web 1
        Assert.Equal(14, model.Edges.Count(e => e.FromKey == "go/shop/cmd/api"));
        // 代码给出方向时替掉包引用的泛协议：worker → Kafka 生产、mailer → RabbitMQ 发布
        Assert.Contains(model.Edges, e => e.FromKey == "go/shop/cmd/worker" && e.ToKey == "external:broker:kafka" && e.Protocol == "kafka-produce");
        Assert.DoesNotContain(model.Edges, e => e.FromKey == "go/shop/cmd/worker" && e.Protocol == "kafka");
        Assert.Contains(model.Edges, e => e.FromKey == "py/mailer" && e.ToKey == "external:broker:rabbitmq" && e.Protocol == "amqp-produce");
        // gorm.Open(postgres.Open(…)) 与 go.mod 的驱动合成一条；Django settings 的 ENGINE → SQLite；smtplib → 邮件
        var pg = Assert.Single(model.Edges, e => e.FromKey == "go/shop/cmd/api" && e.ToKey == "external:database:postgresql");
        Assert.Equal(["go/shop/go.mod:12", "go/shop/internal/store/store.go:9"], pg.Evidence.Select(x => $"{x.File}:{x.Line}"));
        Assert.Equal("py/blog/blog/settings.py", Assert.Single(model.Edges, e => e.FromKey == "py/blog").Evidence.Single().File);
        Assert.Contains(model.Edges, e => e.FromKey == "py/mailer" && e.ToKey == "external:email:smtp-邮件服务" && e.Evidence.Any(x => x.Line == 12));
    }

    [Fact]
    public void FrontendRule_OpenApiGeneratedClient_ParenthesizedReceiver_UrlFromObjectLiteral()
    {
        var ctx = Context();
        new FrontendHttpRule().Apply(ctx);
        var web = ctx.Outbound.Where(o => o.ContainerKey == "web").OrderBy(o => o.Line).Select(o => (o.File, o.Line, o.ClientName, o.Url)).ToList();
        Assert.Equal(new (string, int, string?, string?)[]
        {
            ("web/src/client/sdk.gen.ts", 5, "GET", "/api/v1/items/"),   // (options?.client ?? client).get<…>({ url: … })
            ("web/src/client/sdk.gen.ts", 12, "POST", "/api/v1/items/"),
            ("web/src/client/sdk.gen.ts", 18, "GET", "/api/v1/items/{id}"),
        }, web);
        Assert.All(web, o => Assert.Equal("external:http-service:api-api-v1", ctx.Outbound.First(x => x.Line == o.Line).TargetKey));
    }
}
