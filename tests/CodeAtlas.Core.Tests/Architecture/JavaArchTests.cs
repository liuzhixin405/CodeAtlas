using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Tests.Architecture;

public class JavaArchTests : IDisposable
{
    private readonly JavaFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private const string OrdersYml = "shop/orders/src/main/resources/application.yml";
    private const string GatewayYml = "shop/gateway/src/main/resources/application.yml";

    private RuleContext Context()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        return new RuleContext(inv, ContainerDiscovery.Discover(inv));
    }

    [Fact]
    public void SpringConfig_Yaml_FlattensNestedKeysListsAndDocuments_ProfileFromActivation()
    {
        string[] yaml =
        [
            "server.port: 8888          # 顶层直接写点分键",
            "spring:",
            "  application:",
            "    name: 'gateway'",
            "  cloud:",
            "    gateway:",
            "      routes:",
            "        - id: a",
            "          uri: lb://a-service",
            "          predicates:",
            "          - Path=/a/**",
            "          - Method=GET",
            "        - id: b",
            "  banner: |",
            "    not: a key",
            "---",
            "spring:",
            "  config:",
            "    activate:",
            "      on-profile: docker",
            "  datasource.url: \"jdbc:mysql://db/x\"",
        ];
        var entries = SpringConfigReader.Parse("app/application.yml", yaml, null);
        Assert.Equal(new (string, string, int, string?)[]
        {
            ("server.port", "8888", 1, null),
            ("spring.application.name", "gateway", 4, null),
            ("spring.cloud.gateway.routes[0].id", "a", 8, null),
            ("spring.cloud.gateway.routes[0].uri", "lb://a-service", 9, null),
            ("spring.cloud.gateway.routes[0].predicates[0]", "Path=/a/**", 11, null),       // 列表与父键同缩进也算
            ("spring.cloud.gateway.routes[0].predicates[1]", "Method=GET", 12, null),
            ("spring.cloud.gateway.routes[1].id", "b", 13, null),
            ("spring.config.activate.on-profile", "docker", 20, "docker"),                   // 块标量（banner: |）的内容不是键
            ("spring.datasource.url", "jdbc:mysql://db/x", 21, "docker"),
        }, entries.Select(e => (e.Key, e.Value, e.Line, e.Profile)));

        string[] props = ["# 注释", "spring.datasource.url=${MYSQL_URL:jdbc:mysql://localhost/x}", "server.port: 9000", "", "#---", "spring.config.activate.on-profile=dev", "server.port=1"];
        Assert.Equal(new (string, string, int, string?)[]
        {
            ("spring.datasource.url", "${MYSQL_URL:jdbc:mysql://localhost/x}", 2, "mysql"),
            ("server.port", "9000", 3, "mysql"),
            ("spring.config.activate.on-profile", "dev", 6, "dev"),
            ("server.port", "1", 7, "dev"),
        }, SpringConfigReader.Parse("app/application-mysql.properties", props, "mysql").Select(e => (e.Key, e.Value, e.Line, e.Profile)));

        // ${ENV:缺省值}：取缺省值（缺省值里可以再有冒号）
        Assert.Equal("jdbc:mysql://localhost/x", SpringConfigReader.Resolve("${MYSQL_URL:jdbc:mysql://localhost/x}"));
        Assert.Equal("8081", SpringConfigReader.Resolve("${PORT:8081}"));
        Assert.Null(SpringConfigReader.Resolve("${PORT}"));
        Assert.Equal("plain", SpringConfigReader.Resolve("plain"));
    }

    [Fact]
    public void Inventory_JavaModules_MavenAndGradle_DependenciesModuleRefsConfigAndPorts()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        Assert.Equal(["ads", "clinic", "inventory", "inventory/app", "inventory/lib", "payments", "shop", "shop/common", "shop/config", "shop/gateway", "shop/orders", "shop/orders-it", "shop/registry"],
            inv.JavaModules.Select(m => m.Dir));
        JavaModule M(string dir) => inv.JavaModules.Single(m => m.Dir == dir);

        var shop = M("shop");
        Assert.Equal(("maven", "com.acme.shop", "shop-parent", "pom"), (shop.BuildTool, shop.GroupId, shop.ArtifactId, shop.Packaging));
        Assert.Equal(["shop/common", "shop/orders", "shop/gateway", "shop/registry", "shop/config", "shop/orders-it"], shop.SubModules);
        Assert.Empty(shop.Dependencies); // dependencyManagement 不是依赖
        Assert.Empty(shop.DeployEvidence);

        var orders = M("shop/orders");
        Assert.Equal(("com.acme.shop", "shop-orders", "orders-service", true), (orders.GroupId, orders.ArtifactId, orders.Name, orders.IsSpringBoot));
        // test 范围的 h2（第 11 行）不算；${project.groupId} 换成继承来的 groupId
        Assert.Equal(new (string, int)[]
        {
            ("com.acme.shop:shop-common", 7), ("org.springframework.boot:spring-boot-starter-web", 8), ("org.springframework.boot:spring-boot-starter-data-jpa", 9),
            ("org.postgresql:postgresql", 10), ("org.springframework.kafka:spring-kafka", 12), ("org.springframework.cloud:spring-cloud-starter-netflix-eureka-client", 13),
            ("org.springframework.cloud:spring-cloud-starter-config", 14), ("io.micrometer:micrometer-registry-prometheus", 15), ("org.springframework.boot:spring-boot-starter-mail", 16),
            ("io.grpc:grpc-stub", 17), ("org.springframework.cloud:spring-cloud-starter-openfeign", 18),
        }, orders.Dependencies.Select(d => (d.Name, d.Line)));
        Assert.All(orders.Dependencies, d => Assert.Equal("shop/orders/pom.xml", d.File));
        Assert.Equal(["shop/common"], orders.ModuleRefs);
        Assert.Equal([8081], orders.Ports); // server.port: ${ORDERS_PORT:8081}
        Assert.Contains(new Evidence("shop/orders/src/main/java/com/acme/orders/OrdersApplication.java", 6, "@SpringBootApplication"), orders.DeployEvidence);
        Assert.Contains(new Evidence("shop/orders/src/main/java/com/acme/orders/OrdersApplication.java", 8, "main(String[])"), orders.DeployEvidence);
        Assert.Equal("${ORDERS_PORT:8081}", orders.Setting("server.port")!.Value);
        Assert.Contains(new SpringConfigEntry("spring.datasource.url", "jdbc:mariadb://mariadb:3306/orders", OrdersYml, 18, "docker"), orders.Config);
        Assert.Contains(new SpringConfigEntry("spring.datasource.url", "${MYSQL_URL:jdbc:mysql://localhost/orders}", "shop/orders/src/main/resources/application-mysql.properties", 1, "mysql"), orders.Config);

        Assert.Empty(M("shop/common").DeployEvidence); // 有 main，但被别的模块依赖：是库
        Assert.True(M("shop/orders-it").IsTest);
        Assert.Empty(M("shop/orders-it").DeployEvidence);

        var clinic = M("clinic");
        Assert.Equal(("maven", "clinic/pom.xml"), (clinic.BuildTool, clinic.BuildFile)); // 同目录还有 build.gradle：Maven 优先
        Assert.Equal(["org.springframework.boot:spring-boot-starter-webmvc", "org.springframework.boot:spring-boot-starter-data-jpa", "com.h2database:h2", "com.mysql:mysql-connector-j"], clinic.Dependencies.Select(d => d.Name));
        Assert.Equal([8080], clinic.Ports); // Spring Boot 缺省端口
        Assert.Contains(clinic.DeployEvidence, e => e.Note == "Spring Boot 缺省端口 8080");

        var app = M("inventory/app");
        Assert.Equal(("gradle", "inventory/app/build.gradle.kts", "inventory-service"), (app.BuildTool, app.BuildFile, app.Name));
        Assert.Equal(new (string, int)[] { ("org.springframework.boot:spring-boot-starter-web", 8), ("org.springframework.boot:spring-boot-starter-data-jpa", 9), ("org.hsqldb:hsqldb", 10) },
            app.Dependencies.Select(d => (d.Name, d.Line)));
        Assert.Equal(["inventory/lib"], app.ModuleRefs); // project(":lib") 按 settings.gradle 所在目录解析
        Assert.Equal([8082], app.Ports);
        Assert.Equal(["inventory/app", "inventory/lib"], M("inventory").SubModules);

        var ads = M("ads");
        Assert.Equal(("gradle", "ads", false, "ads/Dockerfile"), (ads.BuildTool, ads.Name, ads.IsSpringBoot, ads.Dockerfile)); // 名字取目录名，不是 rootProject.name
        Assert.Equal(new (string, int)[] { ("io.grpc:grpc-stub", 6), ("io.grpc:grpc-netty", 7), ("com.google.cloud:google-cloud-pubsub", 8), ("io.netty:netty-tcnative-boringssl-static", 9) },
            ads.Dependencies.Select(d => (d.Name, d.Line))); // 一个配置跨行列多个坐标
        Assert.Equal([9555], ads.Ports);

        Assert.Equal(("payments-service", 8083), (M("payments").Name, M("payments").Ports.Single())); // bootstrap.yml 的应用名

        Assert.Contains("shop/orders/src/main/java/com/acme/orders/AdsClient.java", inv.SourceFiles);
        Assert.DoesNotContain(inv.SourceFiles, f => f.Contains("/target/", StringComparison.Ordinal)); // pom 旁的 target/ 是构建输出
    }

    [Fact]
    public void Containers_JavaServices_NamesPortsMembers()
    {
        var containers = ContainerDiscovery.Discover(ProjectInventory.Load(_fx.Root));
        Assert.Equal(["ads", "clinic", "inventory/app", "payments", "shop/config", "shop/gateway", "shop/orders", "shop/registry"], containers.Select(c => c.Key));
        Assert.All(containers, c => Assert.Equal("java-service", c.SubKind));
        Assert.Equal(["ads", "clinic", "inventory-service", "payments-service", "shop-config", "gateway", "orders-service", "registry"], containers.Select(c => c.Name));
        Assert.Equal(new[] { 9555, 8080, 8082, 8083, 8888, 8080, 8081, 8761 }, containers.Select(c => c.Ports.Single()));
        var orders = containers.Single(c => c.Key == "shop/orders");
        Assert.Equal(["shop/common", "shop/orders"], orders.Members); // 被依赖的库模块并进成员
        Assert.Equal(new Evidence("shop/orders/pom.xml", null, "artifactId=shop-orders"), orders.Evidence[0]);
        Assert.Equal(["inventory/app", "inventory/lib"], containers.Single(c => c.Key == "inventory/app").Members);
        Assert.Equal(new Evidence("inventory/app/build.gradle.kts", null, "gradle"), containers.Single(c => c.Key == "inventory/app").Evidence[0]);
        Assert.Equal(["clinic"], containers.Single(c => c.Key == "clinic").Members);
    }

    [Fact]
    public void OutboundRule_RestTemplateWebClientHttpClientFeignDiscoveryClient()
    {
        var ctx = Context();
        var rule = new JavaOutboundRule();
        rule.Apply(ctx);
        var list = ctx.Outbound.OrderBy(o => o.ContainerKey, StringComparer.Ordinal).ThenBy(o => o.File, StringComparer.Ordinal).ThenBy(o => o.Line)
            .Select(o => (o.ContainerKey, System.IO.Path.GetFileName(o.File), o.Line, o.ClientName, o.ConfigKey, o.Url)).ToList();
        Assert.Equal(new (string, string, int, string?, string?, string?)[]
        {
            ("inventory/app", "InventoryApp.java", 12, null, "PRICING_URL", null),                              // HttpRequest.newBuilder(URI.create(System.getenv(…)))
            ("shop/gateway", "OrdersProxy.java", 13, null, null, "http://orders-service/orders/{id}"),         // .uri(字段 + "…")：字段的初值
            ("shop/gateway", "OrdersProxy.java", 18, null, null, "lb://payments-service"),                     // WebClient.create(…) 先于同一行的相对路径 .uri("/payments")
            ("shop/gateway", "OrdersProxy.java", 22, null, null, "http://inventory-service"),                  // discoveryClient.getInstances("服务名")
            ("shop/orders", "InventoryClient.java", 13, null, "inventory.url", "http://inventory-service/items/{id}"), // @Value("${inventory.url}") 字段 → application.yml
            ("shop/orders", "InventoryClient.java", 17, null, null, "https://api.stripe.com/v1/charges"),
            ("shop/orders", "PaymentsClient.java", 9, "payments-service", null, "http://payments-service/payments/{id}"), // @FeignClient(name) 每个方法一条
            ("shop/orders", "PaymentsClient.java", 12, "payments-service", null, "http://payments-service/payments"),
            ("shop/orders", "PricingClient.java", 5, "pricing", "pricing.base-url", "https://pricing.example.com/quote"), // url = "${键:缺省}"
        }, list); // 注释、测试源码、target/ 下的生成代码都不算
        Assert.Equal([("shop/orders/src/main/java/com/acme/orders/InventoryClient.java", 21)], rule.Unresolved); // postForEntity(path …)：形参解析不出
        Assert.All(ctx.Outbound, o => Assert.Equal("http", o.Protocol));
    }

    [Fact]
    public void SpringCloudRule_GatewayRoutes_EurekaAndConfigClients()
    {
        var ctx = Context();
        new SpringCloudRule().Apply(ctx);
        var list = ctx.Outbound.OrderBy(o => o.ContainerKey, StringComparer.Ordinal).ThenBy(o => o.File, StringComparer.Ordinal).ThenBy(o => o.Line)
            .Select(o => (o.ContainerKey, o.File, o.Line, o.ClientName, o.ConfigKey, o.Url, o.TargetKey)).ToList();
        Assert.Equal(new (string, string, int, string?, string?, string?, string?)[]
        {
            ("payments", "payments/build.gradle", 9, "Eureka", null, null, "shop/registry"),                         // 仓库里有 Eureka 服务端：连到它
            ("shop/gateway", "shop/gateway/pom.xml", 8, "Eureka", null, null, "shop/registry"),
            ("shop/gateway", GatewayYml, 8, "Path=/api/orders/**", "orders", "lb://orders-service", null),          // 网关路由：ConfigKey 记路由 id
            ("shop/gateway", GatewayYml, 14, "Path=/legacy/**", "legacy", "https://legacy.example.com", null),
            ("shop/orders", "shop/orders/pom.xml", 13, "Eureka", null, null, "shop/registry"),
            ("shop/orders", OrdersYml, 7, "Spring Cloud Config", "spring.config.import", "http://config:8888", null), // configserver: 的地址
        }, list);
    }

    [Fact]
    public void GrpcServiceRule_JavaImplBaseServerAndStubClient_SkipsGeneratedHealthAndSelf()
    {
        var ctx = Context();
        var rule = new GrpcServiceRule();
        rule.Apply(ctx);
        Assert.Equal([new GrpcServiceRule.Site("ads", "AdService", "ads/src/main/java/ads/AdService.java", 14)], rule.Servers); // HealthImplBase 不算
        Assert.Equal(
        [
            new GrpcServiceRule.Site("ads", "AdService", "ads/src/main/java/ads/AdServiceClient.java", 6),            // 调自己：不出边
            new GrpcServiceRule.Site("shop/orders", "AdService", "shop/orders/src/main/java/com/acme/orders/AdsClient.java", 8),
        ], rule.Clients.OrderBy(c => c.ContainerKey, StringComparer.Ordinal)); // 生成的 AdServiceGrpc.java、测试里的 CartServiceGrpc 都不算
        var o = Assert.Single(ctx.Outbound);
        Assert.Equal(("shop/orders", "AdService", "ads"), (o.ContainerKey, o.ClientName, o.TargetKey));
    }

    [Fact]
    public void Scan_FullModel_JavaEdgesPinned()
    {
        var model = ArchitectureScanner.Scan(_fx.Root);
        var edges = model.Edges.Select(e => $"{e.FromKey} → {e.ToKey} {e.Protocol} {e.Confidence}").ToList();
        Assert.Equal(
        [
            "ads → external:broker:google-cloud-pub-sub grpc 0.5",
            "clinic → external:database:h2-内嵌 sql 0.5",
            "clinic → external:database:mysql sql 0.5",
            "clinic → external:database:postgresql sql 0.5",            // application-postgres.properties 的 datasource url
            "inventory/app → external:broker:kafka kafka 0.5",          // 来自被依赖的 lib 模块
            "inventory/app → external:database:hsqldb-内嵌 sql 0.5",
            "inventory/app → external:http-service:pricing-url http 0.5",
            "payments → external:database:数据库-jpa sql 0.5",           // 只有 JPA、没有驱动（h2 只在测试里）
            "payments → shop/registry http 0.5",
            "shop/gateway → external:http-service:legacy http 0.5",
            "shop/gateway → external:observability:zipkin http 0.5",
            "shop/gateway → inventory/app http 0.7",
            "shop/gateway → payments http 0.7",
            "shop/gateway → shop/orders http 0.7",                       // 网关路由 lb://orders-service + WebClient，按服务名对上
            "shop/gateway → shop/registry http 0.5",
            "shop/orders → ads grpc 0.9",
            "shop/orders → external:broker:kafka kafka 0.5",
            "shop/orders → external:cache:redis redis 0.5",             // 来自 common 模块
            "shop/orders → external:database:mariadb sql 0.5",          // docker profile 的 datasource url
            "shop/orders → external:database:mysql sql 0.5",            // mysql profile 文件
            "shop/orders → external:database:postgresql sql 0.5",
            "shop/orders → external:email:smtp-邮件服务 smtp 0.5",
            "shop/orders → external:http-service:api-stripe-com http 0.5",
            "shop/orders → external:http-service:pricing-base-url http 0.5",
            "shop/orders → external:observability:prometheus http 0.5",
            "shop/orders → inventory/app http 0.7",
            "shop/orders → payments http 0.7",                           // Feign name = 服务名
            "shop/orders → shop/config http 0.7",                        // configserver:http://config:8888 ↔ shop-config
            "shop/orders → shop/registry http 0.5",
        ], edges);
        var route = model.Edges.Single(e => e.FromKey == "shop/gateway" && e.ToKey == "shop/orders");
        Assert.Equal("orders", route.ConfigKey);
        // 证据顺序 = 规则顺序：java-outbound（WebClient）先于 spring-cloud（路由）；ConfigKey 取第一个非空的，即路由 id
        Assert.Equal(["shop/gateway/src/main/java/com/acme/gateway/OrdersProxy.java:13", $"{GatewayYml}:8"], route.Evidence.Select(x => $"{x.File}:{x.Line}"));
        var pg = model.Edges.Single(e => e.FromKey == "shop/orders" && e.ToKey == "external:database:postgresql");
        Assert.Equal(["shop/orders/pom.xml:10", $"{OrdersYml}:9"], pg.Evidence.Select(x => $"{x.File}:{x.Line}"));
        var maria = model.Edges.Single(e => e.ToKey == "external:database:mariadb");
        Assert.Equal(new Evidence(OrdersYml, 18, "spring.datasource.url=jdbc:mariadb://mariadb:3306/orders（profile docker）"), maria.Evidence.Single());
        Assert.Equal("数据库（JPA）", model.Nodes.Single(n => n.Key == "external:database:数据库-jpa").Name);
    }
}
