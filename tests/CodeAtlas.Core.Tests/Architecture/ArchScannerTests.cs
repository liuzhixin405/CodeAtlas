using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests.Architecture;

public class ArchScannerTests : IDisposable
{
    private readonly ArchFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    private RuleContext Context()
    {
        var inv = ProjectInventory.Load(_fx.Root);
        return new RuleContext(inv, ContainerDiscovery.Discover(inv));
    }

    [Fact]
    public void HttpClientRule_FindsRegistrations_ResolvesAddresses_SkipsCommentsStringsAndTests()
    {
        var ctx = Context();
        new HttpClientRule().Apply(ctx);
        var api = ctx.Outbound.Where(o => o.ContainerKey == "Acme.Api").OrderBy(o => o.Line).ToList();
        Assert.Equal(new[] { 9, 15, 16, 17 }, api.Select(o => o.Line)); // 第 13 行注释、第 14 行字符串里的不算
        Assert.Equal(("Billing", "Billing:Url", null), (api[0].ClientName, api[0].ConfigKey, api[0].Url));            // GetSection("Billing") + billing.Url → Billing:Url
        Assert.Equal(("LarkClient", null, null), (api[1].ClientName, api[1].ConfigKey, api[1].Url));                 // Names.LarkClient → 最后一段
        Assert.Equal(("Reports", "Reports:Url", null), (api[2].ClientName, api[2].ConfigKey, api[2].Url));            // configuration["Reports:Url"]
        Assert.Equal(("Inline", null, "http://localhost:5002/api/"), (api[3].ClientName, api[3].ConfigKey, api[3].Url)); // 字面量
        Assert.DoesNotContain(ctx.Outbound, o => o.ClientName == "FromTests"); // 测试项目不是容器
    }

    [Fact]
    public void AddressResolver_FollowsVariableChains_LikeCcsBackend()
    {
        string[] lines =
        [
            "var apiConfig = configuration",
            "        .GetSection(nameof(MetaTraderManagerMT4Api))",
            "        .Get<MetaTraderManagerMT4Api>();",
            "var baseUrl = apiConfig.Url?.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar;",
            "services.AddHttpClient(ConstConfig.MT4MANAGERAPICLIENT, client =>",
            "{",
            "    client.BaseAddress = new Uri(baseUrl);",
        ];
        // 多行链式调用：GetSection 不在 var 那一行。解析器按行回溯，这里要能把三行当一个表达式看
        var joined = new[] { string.Join(" ", lines[0..3]), lines[3], lines[4], lines[5], lines[6] };
        var a = AddressResolver.Resolve("baseUrl", joined, 4);
        Assert.Equal("MetaTraderManagerMT4Api:Url", a.ConfigKey);
        Assert.Equal("MT4MANAGERAPICLIENT", AddressResolver.ClientName(lines[4]));
        Assert.Equal("PaymentsClient", AddressResolver.ClientName("services.AddHttpClient<PaymentsClient>()"));
        Assert.Equal("Lark", AddressResolver.ClientName("services.AddHttpClient(\"Lark\")"));
    }

    [Fact]
    public void InfrastructureAndPackageRules_ApplyToEveryContainerSharingTheLibrary()
    {
        var ctx = Context();
        new PackageReferenceRule().Apply(ctx);
        new InfrastructurePatternRule().Apply(ctx);
        new GrpcChannelRule().Apply(ctx);
        new LocalAssemblyRule().Apply(ctx);

        // Acme.App 的包引用：Api 与 Worker 各得三条直接边
        Assert.Equal(3, ctx.DirectEdges.Count(d => d.ContainerKey == "Acme.Api" && d.Evidence.File == "Acme.App/Acme.App.csproj"));
        Assert.Equal(3, ctx.DirectEdges.Count(d => d.ContainerKey == "Acme.Worker" && d.Evidence.File == "Acme.App/Acme.App.csproj"));
        Assert.Contains(ctx.DirectEdges, d => d.ExternalKey == "external:database:postgresql" && d.Protocol == "sql" && d.Evidence.Line == 4);
        Assert.DoesNotContain(ctx.DirectEdges, d => d.Evidence.Note == "Grpc.Net.Client"); // 客户端库不是外部系统

        // Infra.cs 的代码模式：DbContext / Redis / Kafka 生产，两个容器都有
        foreach (var c in new[] { "Acme.Api", "Acme.Worker" })
        {
            var infra = ctx.Outbound.Where(o => o.ContainerKey == c && o.File == "Acme.App/Infra.cs").OrderBy(o => o.Line).ToList();
            Assert.Equal(new[] { "sql", "redis", "kafka-produce" }, infra.Select(o => o.Protocol));
        }
        // Worker 自己的消费者：IConsumer 与 Subscribe 两行
        Assert.Equal(2, ctx.Outbound.Count(o => o.ContainerKey == "Acme.Worker" && o.Protocol == "kafka-consume"));
        Assert.DoesNotContain(ctx.Outbound, o => o.ContainerKey == "Acme.Api" && o.Protocol == "kafka-consume");
        // gRPC：配置键 Pricing:Grpc
        var grpc = Assert.Single(ctx.Outbound, o => o.Protocol == "grpc" && o.ContainerKey == "Acme.Api");
        Assert.Equal(("Acme.Api", "Pricing:Grpc", 18), (grpc.ContainerKey, grpc.ConfigKey, grpc.Line));
        // AddGrpcClient<Basket.BasketClient>(o => o.Address = new("http://basket-api"))：客户端名取泛型最后一段，地址取目标类型 new 的参数
        var reg = Assert.Single(ctx.Outbound, o => o.Protocol == "grpc" && o.ContainerKey == "services/Web.App");
        Assert.Equal(("BasketClient", "http://basket-api", 6), (reg.ClientName, reg.Url, reg.Line));
        // 本地程序集与 DllImport 只挂到引用 vendor 的 Billing.Api
        var native = Assert.Single(ctx.DirectEdges, d => d.Protocol == "native");
        Assert.Equal(("billing/Billing.Api", "external:native-sdk:metaquotes-mt5managerapi"), (native.ContainerKey, native.ExternalKey));
        var dll = Assert.Single(ctx.Outbound, o => o.Protocol == "native");
        Assert.Equal(("billing/Billing.Api", "mt4lib"), (dll.ContainerKey, dll.ClientName));
    }

    [Fact]
    public void FrontendRule_TakesPathFromLiteralsAndTemplates_SkipsVariables()
    {
        var ctx = Context();
        new FrontendHttpRule().Apply(ctx);
        var web = ctx.Outbound.Where(o => o.ContainerKey == "web").OrderBy(o => o.Line).ToList();
        Assert.Equal(new[] { "/api/v5/orders", "/api/v5/users" }, web.Select(o => o.Url));
        Assert.All(web, o => Assert.Equal("external:http-service:api-api-v5", o.TargetKey));
        Assert.Equal("API /api/v5", ctx.Externals["external:http-service:api-api-v5"].Name);
        Assert.Null(FrontendHttpRule.PathOf("${url}"));
        Assert.Equal("/api/x", FrontendHttpRule.PathOf("${base}/api/x?y=1"));
    }

    [Fact]
    public void Matcher_ResolvesConfigToContainerPort_OrFallsBackToExternal_AndMergesParallelEdges()
    {
        var ctx = Context();
        foreach (var r in ArchitectureScanner.DefaultRules) r.Apply(ctx);
        var (edges, resolved) = EdgeMatcher.Match(ctx);

        var billing = Assert.Single(edges, e => e.FromKey == "Acme.Api" && e.ToKey == "billing/Billing.Api");
        Assert.Equal(("http", 0.7, "Billing:Url", "http://localhost:5002/"), (billing.Protocol, billing.Confidence, billing.ConfigKey, billing.Url));
        Assert.Equal(new[] { 9, 17 }, billing.Evidence.Select(e => e.Line!.Value).OrderBy(l => l)); // "Billing"（配置）与 "Inline"（字面量 5002）合成一条

        Assert.Equal(0.5, Assert.Single(edges, e => e.ToKey == "external:http-service:reports").Confidence);   // 5099 没有容器
        Assert.Equal(0.5, Assert.Single(edges, e => e.ToKey == "external:http-service:larkclient").Confidence); // 只有客户端名
        Assert.Equal("grpc", Assert.Single(edges, e => e.ToKey == "external:grpc-service:pricing").Protocol);   // 配置键没有值 → 按节名命名

        // 消费方向反转：Kafka → Worker；包引用的泛协议 kafka 边被具体的 produce / consume 替掉
        Assert.Contains(edges, e => e.FromKey == "external:broker:kafka" && e.ToKey == "Acme.Worker" && e.Protocol == "kafka-consume");
        Assert.Contains(edges, e => e.FromKey == "Acme.Worker" && e.ToKey == "external:broker:kafka" && e.Protocol == "kafka-produce");
        Assert.DoesNotContain(edges, e => e.Protocol == "kafka");
        Assert.Contains(resolved, o => o.ClientName == "Billing" && o.TargetKey == "billing/Billing.Api" && o.Url == "http://localhost:5002/");
    }

    [Fact]
    public void Scan_FullModel_NodesAndEdgesPinned()
    {
        var model = ArchitectureScanner.Scan(_fx.Root);
        Assert.Equal(new[] { "Acme.Api", "Acme.Worker", "billing/Billing.Api", "services/Catalog.Api", "services/Web.App", "web" }, model.Nodes.Where(n => n.Kind == ArchNodeKind.Container).Select(n => n.Key));
        Assert.Equal(new[]
        {
            "external:broker:kafka", "external:broker:rabbitmq", "external:cache:redis", "external:database:postgresql",
            "external:grpc-service:basketclient", "external:grpc-service:pricing", "external:http-service:api-api-v5", "external:http-service:larkclient", "external:http-service:reports",
            "external:native-sdk:metaquotes-mt5managerapi", "external:native-sdk:mt4lib",
        }, model.Nodes.Where(n => n.Kind == ArchNodeKind.External).Select(n => n.Key));
        Assert.Equal(18, model.Edges.Count); // 14 + Catalog→PostgreSQL + Catalog→RabbitMQ(produce) + Web→Catalog(http 0.7) + Web→BasketClient(grpc 外部 0.5)
        Assert.Equal(4, model.Edges.Count(e => e.FromKey == "Acme.Worker" || e.ToKey == "Acme.Worker"));
        Assert.Equal(2, model.Edges.Count(e => e.FromKey == "billing/Billing.Api"));
        Assert.Empty(model.Conflicts);
    }

    [Fact]
    public void AspireStyle_ServiceDiscoveryNames_TargetTypedNew_AspirePackages_AsyncPublish()
    {
        var model = ArchitectureScanner.Scan(_fx.Root);
        // https+http://catalog-api 按服务发现名对上 services/Catalog.Api（目标类型 new 也能抓到）
        var web = Assert.Single(model.Edges, e => e.FromKey == "services/Web.App" && e.ToKey == "services/Catalog.Api");
        Assert.Equal(("http", 0.7, "https+http://catalog-api"), (web.Protocol, web.Confidence, web.Url));
        // AddGrpcClient<Basket.BasketClient>：没有叫 basket-api 的容器 → gRPC 外部系统，名字按既有优先级取客户端名
        var grpc = Assert.Single(model.Edges, e => e.FromKey == "services/Web.App" && e.Protocol == "grpc");
        Assert.Equal(("external:grpc-service:basketclient", 0.5, "http://basket-api"), (grpc.ToKey, grpc.Confidence, grpc.Url));
        // Aspire.Npgsql.* 包 + AddNpgsqlDbContext → PostgreSQL；BasicPublishAsync → RabbitMQ 生产
        Assert.Contains(model.Edges, e => e.FromKey == "services/Catalog.Api" && e.ToKey == "external:database:postgresql" && e.Protocol == "sql");
        Assert.Contains(model.Edges, e => e.FromKey == "services/Catalog.Api" && e.ToKey == "external:broker:rabbitmq" && e.Protocol == "amqp-produce");
        Assert.DoesNotContain(model.Nodes, n => n.Key == "Acme.MsTests");
    }

    [Fact]
    public void Declarations_RenameHideAddAndRelate_WithConflicts()
    {
        _fx.Write(".codeatlas/architecture.yaml", """
            containers:
              - key: billing/Billing.Api
                name: 计费服务
              - key: Acme.Worker
                hide: true
            externals:
              - key: external:http-service:larkclient
                name: Lark 飞书
                kind: saas
              - name: MT5 服务器
            people:
              - key: person:ops
                name: 运维
            relationships:
              - from: person:ops
                to: web
                protocol: https
              - from: billing/Billing.Api
                to: external:other:mt5-服务器
                protocol: native
              - from: nope
                to: web
            """);
        var model = ArchitectureScanner.Scan(_fx.Root);
        Assert.Equal("计费服务", model.Nodes.Single(n => n.Key == "billing/Billing.Api").Name);
        Assert.DoesNotContain(model.Nodes, n => n.Key == "Acme.Worker");
        Assert.DoesNotContain(model.Edges, e => e.FromKey == "Acme.Worker" || e.ToKey == "Acme.Worker");
        var lark = model.Nodes.Single(n => n.Key == "external:http-service:larkclient");
        Assert.Equal(("Lark 飞书", "saas", ArchSource.Discovered), (lark.Name, lark.SubKind, lark.Source));
        var mt5 = model.Nodes.Single(n => n.Key == "external:other:mt5-服务器");
        Assert.Equal(ArchSource.Declared, mt5.Source);
        Assert.Equal(ArchSource.Declared, model.Nodes.Single(n => n.Key == "person:ops").Source);
        Assert.Contains(model.Edges, e => e.FromKey == "person:ops" && e.ToKey == "web" && e.Protocol == "https" && e.Confidence == 1.0 && e.Source == ArchSource.Declared);
        Assert.Contains(model.Edges, e => e.FromKey == "billing/Billing.Api" && e.ToKey == "external:other:mt5-服务器");
        Assert.Equal(new[] { "declared-not-found", "declared-not-found", "declared-overrides", "declared-overrides", "hidden-but-used" },
            model.Conflicts.Select(c => c.Kind).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ScanAndStore_WritesModel_AndReportsCounts()
    {
        using var db = IndexDatabase.OpenInMemory();
        var (model, report) = ArchitectureScanner.ScanAndStore(_fx.Root, db);
        Assert.Equal((6, 11, 18), (report.Containers, report.Externals, report.Edges)); // 外部 +RabbitMQ +BasketClient
        Assert.Equal(model.Outbound.Count, report.OutboundTotal);
        Assert.Equal(6, report.OutboundUnmatched); // LarkClient、Reports、Pricing、前端两处、Web.App 的 basket-api gRPC
        var read = ArchStorage.Read(db);
        Assert.Equal(model.Nodes.Count, read.Nodes.Count);
        Assert.Equal(model.Edges.Count, read.Edges.Count);
    }
}
