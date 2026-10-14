using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public class EntryDiscoveryTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent, int start, int end, string? vis = null) => new(id, kind, qname, path, parent, start, end, vis);

    // 文件内容：行号与符号的 StartLine 对应
    private static readonly Dictionary<string, string[]> Files = new()
    {
        ["Acme.Api/Ctl.cs"] =
        [
            "namespace Acme.Api;",                                   // 1
            "[ApiController]",                                       // 2
            "[Route(\"api/v5/[controller]\")]",                       // 3
            "public class OrdersController : BaseController",         // 4
            "{",                                                      // 5
            "    [HttpGet(\"{id}\")]",                               // 6
            "    public Task<Order> Get(int id) => default!;",       // 7
            "",                                                       // 8
            "    /// <summary>批量</summary>",                        // 9
            "    [HttpPost(\"batch\")]",                              // 10
            "    public Task Batch() => default!;",                  // 11
            "",                                                       // 12
            "    [NonAction]",                                        // 13
            "    public void Helper() { }",                          // 14
            "",                                                       // 15
            "    [HttpDelete]",                                       // 16
            "    public Task Delete() => default!;",                 // 17
            "}",                                                      // 18
            "public abstract class BaseController : ControllerBase { }", // 19
        ],
        ["Acme.Api/Program.cs"] =
        [
            "var app = builder.Build();",                             // 1
            "app.MapGet(\"/health\", () => Results.Ok());",           // 2
            "// app.MapGet(\"/commented\", () => 1);",                 // 3
            "var vApi = app.NewVersionedApi(\"Orders\");",            // 4
            "var api = vApi.MapGroup(\"api/orders\").HasApiVersion(1.0);", // 5：eShop 写法，前缀不带 /
            "api.MapGet(\"{id:int}\", GetOrder);",                     // 6
            "api.MapPost(\"/\", CreateOrder);",                        // 7
            "app.MapGroup(\"/admin\").MapGroup(\"jobs\").MapDelete(\"/{id}\", Kill);", // 8：同一行上链式 MapGroup；Kill 不在本文件 → 落在顶级语句上
            "app.Run();",                                             // 9
            "static Task GetOrder(int id) => Task.CompletedTask;",    // 10：方法组处理器
            "app.MapGrpcService<GreeterService>();",                  // 11：gRPC 服务注册字面量
        ],
        ["Acme.Api/Grpc.cs"] =
        [
            "public class GreeterService : Greeter.GreeterBase",      // 1
            "{",                                                      // 2
            "    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) => default!;", // 3
            "    public override Task<Empty> Ping(Empty request, ServerCallContext context) => default!;",                // 4
            "    public Task Helper() => Task.CompletedTask;",       // 5：不是 override → 不算
            "}",                                                      // 6
        ],
        ["Acme.Infra/Grpc2.cs"] =
        [
            // 没有 MapGrpcService 字面量：父类是 external 的 *Base 且方法签名里有 ServerCallContext，也算
            "public class StockService : Stock.StockBase { public override Task<Reply> Check(Req r, ServerCallContext context) => default!; }", // 1
            "public class Order : EntityBase { public void Touch() { } }", // 2：external 的 *Base 但没有 ServerCallContext → 不算
        ],
        ["Acme.Infra/Cleanup.cs"] =
        [
            "public sealed class Cleanup",                            // 1
            "{ public Task Sweep() => Task.CompletedTask;",          // 2
            "  public Task Purge(int days) => Task.CompletedTask;",  // 3
            "  public static Task Vacuum() => Task.CompletedTask; }", // 4
        ],
        ["Acme.Worker/Startup.cs"] =
        [
            "RecurringJob.AddOrUpdate<Cleanup>(\"sweep\", x => x.Sweep(), Cron.Daily);", // 1
            "BackgroundJob.Enqueue<Cleanup>(x => x.Purge(30));",                         // 2
            "RecurringJob.AddOrUpdate(\"vacuum\", () => Cleanup.Vacuum(), Cron.Weekly);", // 3：静态方法形式
            "// RecurringJob.AddOrUpdate<Cleanup>(\"no\", x => x.Missing(), Cron.Daily);", // 4：注释里的不算
        ],
        ["Acme.Infra/Jobs.cs"] =
        [
            "public sealed class Reaper : BackgroundService",          // 1
            "{ protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }", // 2
            "public sealed class Nightly : IJob",                      // 3
            "{ public Task Execute(IJobExecutionContext ctx) => Task.CompletedTask; }", // 4
            "public sealed class LiveHub : Hub { public Task Ping() => Task.CompletedTask; }", // 5
        ],
    };

    private static string[] Read(string path) => Files.TryGetValue(path, out var l) ? l : [];

    private static readonly List<MapSymbol> Symbols =
    [
        Sym(1, SymbolKind.Namespace, "Acme.Api", "Acme.Api/Ctl.cs", null, 1, 19),
        Sym(2, SymbolKind.Class, "Acme.Api.OrdersController", "Acme.Api/Ctl.cs", 1, 4, 18),
        Sym(3, SymbolKind.Method, "Acme.Api.OrdersController.Get(int)", "Acme.Api/Ctl.cs", 2, 7, 7, "public"),
        Sym(4, SymbolKind.Method, "Acme.Api.OrdersController.Batch()", "Acme.Api/Ctl.cs", 2, 11, 11, "public"),
        Sym(5, SymbolKind.Method, "Acme.Api.OrdersController.Helper()", "Acme.Api/Ctl.cs", 2, 14, 14, "public"),
        Sym(6, SymbolKind.Method, "Acme.Api.OrdersController.Delete()", "Acme.Api/Ctl.cs", 2, 17, 17, "public"),
        Sym(7, SymbolKind.Method, "Acme.Api.OrdersController.Secret()", "Acme.Api/Ctl.cs", 2, 17, 17, "private"),
        Sym(8, SymbolKind.Class, "Acme.Api.BaseController", "Acme.Api/Ctl.cs", 1, 19, 19),
        Sym(9, SymbolKind.Function, "<top-level>", "Acme.Api/Program.cs", null, 1, 9),
        Sym(10, SymbolKind.Method, "GetOrder(int)", "Acme.Api/Program.cs", null, 10, 10),
        Sym(20, SymbolKind.Class, "Acme.Infra.Reaper", "Acme.Infra/Jobs.cs", null, 1, 2),
        Sym(21, SymbolKind.Method, "Acme.Infra.Reaper.ExecuteAsync(CancellationToken)", "Acme.Infra/Jobs.cs", 20, 2, 2, "protected"),
        Sym(22, SymbolKind.Class, "Acme.Infra.Nightly", "Acme.Infra/Jobs.cs", null, 3, 4),
        Sym(23, SymbolKind.Method, "Acme.Infra.Nightly.Execute(IJobExecutionContext)", "Acme.Infra/Jobs.cs", 22, 4, 4, "public"),
        Sym(24, SymbolKind.Class, "Acme.Infra.LiveHub", "Acme.Infra/Jobs.cs", null, 5, 5),
        Sym(25, SymbolKind.Method, "Acme.Infra.LiveHub.Ping()", "Acme.Infra/Jobs.cs", 24, 5, 5, "public"),
        // 分发处理器
        Sym(30, SymbolKind.Class, "Acme.App.CreateOrderHandler", "Acme.App/H.cs", null, 1, 5),
        Sym(31, SymbolKind.Method, "Acme.App.CreateOrderHandler.HandleAsync(CreateOrderCommand)", "Acme.App/H.cs", 30, 2, 4, "public"),
        Sym(32, SymbolKind.Class, "Acme.App.CreateOrderCommand", "Acme.App/C.cs", null, 1, 1),
        // 前端
        Sym(40, SymbolKind.Module, "web/src/app/orders/page.tsx", "web/src/app/orders/page.tsx", null, 1, 30),
        Sym(41, SymbolKind.Module, "web/src/app/(auth)/login/[id]/page.tsx", "web/src/app/(auth)/login/[id]/page.tsx", null, 1, 30),
        Sym(42, SymbolKind.Module, "web/src/app/api/chat/route.ts", "web/src/app/api/chat/route.ts", null, 1, 30),
        Sym(43, SymbolKind.Module, "web/src/components/Btn.tsx", "web/src/components/Btn.tsx", null, 1, 30),
        // 测试项目里的控制器：排除
        Sym(50, SymbolKind.Class, "Acme.Tests.FakeController", "Acme.Tests/Fake.cs", null, 1, 3),
        Sym(51, SymbolKind.Method, "Acme.Tests.FakeController.Get()", "Acme.Tests/Fake.cs", 50, 2, 2, "public"),
        // gRPC：注册字面量形式
        Sym(55, SymbolKind.Class, "Acme.Api.GreeterService", "Acme.Api/Grpc.cs", null, 1, 6),
        Sym(56, SymbolKind.Method, "Acme.Api.GreeterService.SayHello(HelloRequest, ServerCallContext)", "Acme.Api/Grpc.cs", 55, 3, 3, "public"),
        Sym(57, SymbolKind.Method, "Acme.Api.GreeterService.Ping(Empty, ServerCallContext)", "Acme.Api/Grpc.cs", 55, 4, 4, "public"),
        Sym(58, SymbolKind.Method, "Acme.Api.GreeterService.Helper()", "Acme.Api/Grpc.cs", 55, 5, 5, "public"),
        // gRPC：无字面量，靠 external *Base + ServerCallContext
        Sym(65, SymbolKind.Class, "Acme.Infra.StockService", "Acme.Infra/Grpc2.cs", null, 1, 1),
        Sym(66, SymbolKind.Method, "Acme.Infra.StockService.Check(Req, ServerCallContext)", "Acme.Infra/Grpc2.cs", 65, 1, 1, "public"),
        Sym(67, SymbolKind.Class, "Acme.Infra.Order", "Acme.Infra/Grpc2.cs", null, 2, 2),
        Sym(68, SymbolKind.Method, "Acme.Infra.Order.Touch()", "Acme.Infra/Grpc2.cs", 67, 2, 2, "public"),
        // Hangfire：作业类在 Infra，注册在 Worker
        Sym(60, SymbolKind.Function, "<top-level>", "Acme.Worker/Startup.cs", null, 1, 4),
        Sym(61, SymbolKind.Class, "Acme.Infra.Cleanup", "Acme.Infra/Cleanup.cs", null, 1, 4),
        Sym(62, SymbolKind.Method, "Acme.Infra.Cleanup.Sweep()", "Acme.Infra/Cleanup.cs", 61, 2, 2, "public"),
        Sym(63, SymbolKind.Method, "Acme.Infra.Cleanup.Purge(int)", "Acme.Infra/Cleanup.cs", 61, 3, 3, "public"),
        Sym(64, SymbolKind.Method, "Acme.Infra.Cleanup.Vacuum()", "Acme.Infra/Cleanup.cs", 61, 4, 4, "public"),
        // MassTransit：IConsumer<T> 实现（handles 边 + implements 边指向 external 的 IConsumer）
        Sym(70, SymbolKind.Class, "Acme.App.OrderPlacedConsumer", "Acme.App/Consumer.cs", null, 1, 4),
        Sym(71, SymbolKind.Method, "Acme.App.OrderPlacedConsumer.Consume(ConsumeContext<OrderPlaced>)", "Acme.App/Consumer.cs", 70, 3, 3, "public"),
        Sym(72, SymbolKind.Class, "Acme.App.OrderPlaced", "Acme.App/E.cs", null, 1, 1),
        // 外部基类
        Sym(90, SymbolKind.External, "external:ControllerBase", "<external>", null, 0, 0),
        Sym(91, SymbolKind.External, "external:BackgroundService", "<external>", null, 0, 0),
        Sym(92, SymbolKind.External, "external:IJob", "<external>", null, 0, 0),
        Sym(93, SymbolKind.External, "external:Hub", "<external>", null, 0, 0),
        Sym(94, SymbolKind.External, "external:MassTransit.IConsumer<TMessage>", "<external>", null, 0, 0),
        Sym(95, SymbolKind.External, "external:Greeter.GreeterBase", "<external>", null, 0, 0),
        Sym(96, SymbolKind.External, "external:Stock.StockBase", "<external>", null, 0, 0),
        Sym(97, SymbolKind.External, "external:EntityBase", "<external>", null, 0, 0),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(2, 8, EdgeKind.Inherits), new(8, 90, EdgeKind.Inherits),   // OrdersController → BaseController → ControllerBase
        new(20, 91, EdgeKind.Inherits),
        new(22, 92, EdgeKind.Implements),
        new(24, 93, EdgeKind.Inherits),
        new(30, 32, EdgeKind.Dispatches, 1.0, "handles"),
        new(50, 90, EdgeKind.Inherits),
        new(55, 95, EdgeKind.Inherits),
        new(65, 96, EdgeKind.Inherits),
        new(67, 97, EdgeKind.Inherits),
        new(70, 94, EdgeKind.Implements),
        new(70, 72, EdgeKind.Dispatches, 1.0, "handles"),
    ];

    private static ArchNode Container(string key, string subKind, params string[] members) =>
        new(key, ArchNodeKind.Container, subKind, key, key, ArchSource.Discovered, [], members, []);

    private static readonly List<ArchNode> Containers =
    [
        Container("Acme.Api", "dotnet-web", "Acme.Api", "Acme.App", "Acme.Infra"),
        Container("Acme.Worker", "dotnet-exe", "Acme.Worker", "Acme.App", "Acme.Infra"),
        Container("web", "frontend", "web"),
    ];

    [Fact]
    public void Controllers_ThroughBaseClass_WithRoutes_SkipNonActionAndNonPublic()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read);
        var http = entries.Where(e => e.RuleId == "aspnet.controller").ToDictionary(e => e.SymbolId);
        Assert.Equal(3, http.Count);
        // [controller] 按 ASP.NET 的做法保留类名原样（Orders）；匹配时不区分大小写
        Assert.Equal(("/api/v5/Orders/{id}", "GET", 7), (http[3].Route, http[3].HttpMethod, http[3].Line));
        Assert.Equal(("/api/v5/Orders/batch", "POST", 11), (http[4].Route, http[4].HttpMethod, http[4].Line));
        Assert.Equal(("/api/v5/Orders", "DELETE", 17), (http[6].Route, http[6].HttpMethod, http[6].Line));
        Assert.All(http.Values, e => Assert.Equal("Acme.Api", e.ContainerKey));
        Assert.DoesNotContain(entries, e => e.SymbolId == 5);  // [NonAction]
        Assert.DoesNotContain(entries, e => e.SymbolId == 7);  // private
        Assert.DoesNotContain(entries, e => e.SymbolId == 51); // 测试项目
    }

    [Fact]
    public void MinimalApi_Hosted_Job_Hub_Handler_AreEntries_HostedInBothContainersSharingInfra()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read);
        var minimal = entries.Where(e => e.RuleId == "aspnet.minimal").OrderBy(e => e.Line).ToList();
        Assert.Equal(new[]
        {
            ("/health", "GET", 2, 9L),                 // 注释里的那条不算；lambda → 顶级语句
            ("/api/orders/{id:int}", "GET", 6, 10L),   // MapGroup 变量的前缀拼进去；方法组 GetOrder → 入口是那个方法
            ("/api/orders", "POST", 7, 9L),            // "/" 不留尾斜杠；CreateOrder 不在本文件
            ("/admin/jobs/{id}", "DELETE", 8, 9L),     // 同一行链式 MapGroup
        }, minimal.Select(e => (e.Route!, e.HttpMethod!, e.Line, e.SymbolId)));
        var hosted = entries.Where(e => e.Kind == "hosted").ToList();
        Assert.Equal(new[] { "Acme.Api", "Acme.Worker" }, hosted.Select(e => e.ContainerKey)); // Infra 同时进两个容器
        Assert.All(hosted, e => Assert.Equal((21L, "Reaper"), (e.SymbolId, e.Route)));
        Assert.Equal(23, entries.First(e => e.Kind == "job").SymbolId);
        Assert.Equal("LiveHub.Ping", entries.First(e => e.Kind == "hub").Route);
        var handler = entries.Where(e => e.RuleId == "dispatch.handler").ToList();
        Assert.Equal(2, handler.Count); // App 也在两个容器里
        Assert.Equal((31L, "CreateOrderCommand"), (handler[0].SymbolId, handler[0].Route));
    }

    [Fact]
    public void Grpc_RegisteredService_PublicOverrides_AreEntries_HelperIsNot()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read);
        var grpc = entries.Where(e => e.RuleId == "grpc.service" && e.ContainerKey == "Acme.Api").OrderBy(e => e.Line).ToList();
        Assert.All(grpc, e => Assert.Equal(("grpc", (string?)null), (e.Kind, e.HttpMethod)));
        Assert.Equal(new[]
        {
            ("Greeter/SayHello", 3, 56L, "Acme.Api/Grpc.cs"),   // 服务名取自基类 GreeterBase 去掉 Base
            ("Greeter/Ping", 4, 57L, "Acme.Api/Grpc.cs"),
            ("Stock/Check", 1, 66L, "Acme.Infra/Grpc2.cs"),     // 无字面量：external *Base + ServerCallContext
        }, grpc.OrderBy(e => e.SymbolId).Select(e => (e.Route!, e.Line, e.SymbolId, e.File)));
        Assert.DoesNotContain(entries, e => e.SymbolId == 58); // Helper 不是 override
        Assert.DoesNotContain(entries, e => e.SymbolId == 68); // Order : EntityBase 没有 ServerCallContext
        // Infra 里的 StockService 同时进两个容器
        Assert.Equal(new[] { "Acme.Api", "Acme.Worker" }, entries.Where(e => e.SymbolId == 66).Select(e => e.ContainerKey).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Hangfire_RegistrationLiterals_PointAtJobMethods_InTheRegisteringContainer()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read);
        var jobs = entries.Where(e => e.RuleId == "hangfire.job").ToList();
        Assert.All(jobs, e => Assert.Equal(("job", "Acme.Worker", "Acme.Infra/Cleanup.cs"), (e.Kind, e.ContainerKey, e.File))); // 注册在 Worker → 只进 Worker
        Assert.Equal(new[]
        {
            ("Cleanup.Purge", 3, 63L),   // BackgroundJob.Enqueue<T>(x => x.Purge(30))
            ("Cleanup.Sweep", 2, 62L),   // RecurringJob.AddOrUpdate<T>("id", x => x.Sweep(), …)
            ("Cleanup.Vacuum", 4, 64L),  // RecurringJob.AddOrUpdate("id", () => Cleanup.Vacuum(), …)
        }, jobs.OrderBy(e => e.Route, StringComparer.Ordinal).Select(e => (e.Route!, e.Line, e.SymbolId)));
        // Quartz 的 IJob 规则不受影响
        Assert.Equal(23, entries.Single(e => e.RuleId == "quartz.job" && e.ContainerKey == "Acme.Api").SymbolId);
    }

    [Fact]
    public void MassTransit_Consumer_UsesHandlesEdge_WithItsOwnRuleId()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read);
        var consumers = entries.Where(e => e.RuleId == "masstransit.consumer").OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Acme.Api", "Acme.Worker" }, consumers.Select(e => e.ContainerKey)); // App 在两个容器里
        Assert.All(consumers, e => Assert.Equal(("handler", "OrderPlaced", 71L, 3, "Acme.App/Consumer.cs"), (e.Kind, e.Route, e.SymbolId, e.Line, e.File)));
        Assert.DoesNotContain(entries, e => e.SymbolId == 71 && e.RuleId == "dispatch.handler");
    }

    [Fact]
    public void Frontend_NextRoutes_FromFilePaths()
    {
        var entries = EntryDiscovery.Discover(Symbols, Edges, Containers, Read).Where(e => e.ContainerKey == "web").ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Kind == "page" && e.Route == "/orders" && e.SymbolId == 40);
        Assert.Contains(entries, e => e.Kind == "page" && e.Route == "/login/{id}" && e.SymbolId == 41); // (auth) 分组段去掉
        Assert.Contains(entries, e => e.Kind == "api-route" && e.Route == "/api/chat" && e.SymbolId == 42);
        Assert.DoesNotContain(entries, e => e.SymbolId == 43);
    }

    [Theory]
    [InlineData("api/v5/[controller]", "brands", "SystemController", "List", "/api/v5/System/brands")]
    [InlineData("api/v5/[controller]", null, "SystemController", "List", "/api/v5/System")]
    [InlineData(null, "ping", "HealthController", "Ping", "/ping")]
    [InlineData("api/[controller]/[action]", null, "AccountController", "Login", "/api/Account/Login")]
    [InlineData("api/v5/x", "~/root", "XController", "M", "/root")]
    public void CombineRoute_ExpandsTokens(string? classRoute, string? methodRoute, string cls, string method, string expected)
    {
        Assert.Equal(expected, EntryDiscovery.CombineRoute(classRoute, methodRoute, cls, method));
    }

    [Theory]
    [InlineData("orders/", "/orders")]
    [InlineData("(auth)/login/[id]/", "/login/{id}")]
    [InlineData("docs/[...slug]/", "/docs/{*slug}")]
    [InlineData("", "/")]
    public void NextRoute_Conventions(string dir, string expected) => Assert.Equal(expected, EntryDiscovery.NextRoute(dir));
}
