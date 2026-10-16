using System.Net;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 框架流程（FrameworkFlowBuilder）：两个夹具——MediatR 风格（外部 ISender.Send、IPipelineBehavior、AddOpenBehavior 书写顺序）
/// 与自研分派器风格（仓库里的 Dispatcher + 包装器、ICommandHandler、自研 IPipelineBehavior、DI 注册顺序）；外加 Worker 容器与找不到分派器的容器。
/// </summary>
public class FrameworkFlowTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null, int start = 1, int end = 1) => new(id, kind, qname, path, parent, start, end);

    private static FrameworkCall Call(long? id, string label, string qn, int pos, string? ctx = null, string kind = "call", bool expandable = true, params FrameworkCall[] kids) =>
        new(id, kind, label, qn, pos / 100 + 1, pos, ctx, expandable, kids);

    private static FrameworkCall Ext(long id, string label, string qn, int pos, string? ctx = null) => Call(id, label, qn, pos, ctx, "external", false);

    private static ArchNode Container(string key, string subKind, params string[] members) =>
        new(key, ArchNodeKind.Container, subKind, key, members[0], ArchSource.Discovered, [], members, []);

    private static FrameworkSources Sources(Dictionary<long, IReadOnlyList<FrameworkCall>> calls, Dictionary<long, string> signatures, Dictionary<string, string> files) => new()
    {
        Calls = id => calls.GetValueOrDefault(id) ?? [],
        Signature = id => signatures.GetValueOrDefault(id),
        ReadFile = path => files.GetValueOrDefault(path),
    };

    private static List<FlowNodeDto> All(FlowNodeDto n) => [n, .. n.Children.SelectMany(All)];

    // ================= 夹具一：MediatR 风格 =================

    private const string ShopProgram = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<Program>();
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuditBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        builder.Services.AddControllers(o => o.Filters.Add<ApiExceptionFilter>());
        var app = builder.Build();
        // app.UseCommentedOut();
        app.UseExceptionHandler("/error");
        app.UseMiddleware<RequestTimingMiddleware>();
        app.UseAuthentication().UseAuthorization();
        app.MapControllers();
        app.Run();
        app.UseAfterRun();
        """;

    private const string BehaviorSignature = "Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)";
    private const string NextQn = "external:MediatR.RequestHandlerDelegate<TResponse>.Invoke(CancellationToken)";

    internal static FrameworkInput MediatRFixture()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "Shop.Api/Program.cs", "Shop.Api/Program.cs"),
            Sym(2, SymbolKind.Namespace, "Shop.Api.Application.Orders", "Shop.Api/Application/Orders/CreateOrderCommand.cs"),
            Sym(3, SymbolKind.Namespace, "Shop.Api.Application.Queries", "Shop.Api/Application/Queries/GetOrderQuery.cs"),
            // 行为：符号表里按字母序（Audit、Logging、Transaction），注册顺序是 Logging、Audit、Transaction
            Sym(30, SymbolKind.Class, "Shop.Api.Behaviors.AuditBehavior<TRequest, TResponse>", "Shop.Api/Behaviors/AuditBehavior.cs", null, 1, 30),
            Sym(31, SymbolKind.Method, "Shop.Api.Behaviors.AuditBehavior<TRequest, TResponse>.Handle(TRequest, RequestHandlerDelegate<TResponse>, CancellationToken)", "Shop.Api/Behaviors/AuditBehavior.cs", 30, 5, 25),
            Sym(20, SymbolKind.Class, "Shop.Api.Behaviors.LoggingBehavior<TRequest, TResponse>", "Shop.Api/Behaviors/LoggingBehavior.cs", null, 1, 20),
            Sym(21, SymbolKind.Method, "Shop.Api.Behaviors.LoggingBehavior<TRequest, TResponse>.Handle(TRequest, RequestHandlerDelegate<TResponse>, CancellationToken)", "Shop.Api/Behaviors/LoggingBehavior.cs", 20, 5, 15),
            Sym(40, SymbolKind.Class, "Shop.Api.Behaviors.TransactionBehavior<TRequest, TResponse>", "Shop.Api/Behaviors/TransactionBehavior.cs", null, 1, 40),
            Sym(41, SymbolKind.Method, "Shop.Api.Behaviors.TransactionBehavior<TRequest, TResponse>.Handle(TRequest, RequestHandlerDelegate<TResponse>, CancellationToken)", "Shop.Api/Behaviors/TransactionBehavior.cs", 40, 5, 35),
            Sym(32, SymbolKind.Method, "Shop.Api.Behaviors.AuditBehavior<TRequest, TResponse>.Prepare(TRequest)", "Shop.Api/Behaviors/AuditBehavior.cs", 30, 26, 28),
            Sym(33, SymbolKind.Class, "Shop.Api.Auditing.AuditWriter", "Shop.Api/Auditing/AuditWriter.cs", null, 1, 10),
            Sym(34, SymbolKind.Method, "Shop.Api.Auditing.AuditWriter.Write(object)", "Shop.Api/Auditing/AuditWriter.cs", 33, 3, 8),
            // 控制器 / 查询辅助
            Sym(49, SymbolKind.Class, "Shop.Api.Controllers.OrdersController", "Shop.Api/Controllers/OrdersController.cs", null, 1, 40),
            Sym(50, SymbolKind.Method, "Shop.Api.Controllers.OrdersController.Create(CreateOrderCommand)", "Shop.Api/Controllers/OrdersController.cs", 49, 10, 14),
            Sym(51, SymbolKind.Method, "Shop.Api.Controllers.OrdersController.Get(int)", "Shop.Api/Controllers/OrdersController.cs", 49, 16, 20),
            Sym(52, SymbolKind.Method, "Shop.Api.Controllers.OrdersController.Load(int)", "Shop.Api/Controllers/OrdersController.cs", 49, 22, 26),
            // Handler 与消息
            Sym(60, SymbolKind.Class, "Shop.Api.Application.Orders.CreateOrderCommandHandler", "Shop.Api/Application/Orders/CreateOrderCommandHandler.cs", 2, 1, 30),
            Sym(61, SymbolKind.Method, "Shop.Api.Application.Orders.CreateOrderCommandHandler.Handle(CreateOrderCommand, CancellationToken)", "Shop.Api/Application/Orders/CreateOrderCommandHandler.cs", 60, 5, 25),
            Sym(62, SymbolKind.Class, "Shop.Api.Application.Queries.GetOrderQueryHandler", "Shop.Api/Application/Queries/GetOrderQueryHandler.cs", 3, 1, 20),
            Sym(63, SymbolKind.Method, "Shop.Api.Application.Queries.GetOrderQueryHandler.Handle(GetOrderQuery, CancellationToken)", "Shop.Api/Application/Queries/GetOrderQueryHandler.cs", 62, 5, 15),
            Sym(64, SymbolKind.Class, "Shop.Api.Application.Orders.OrderPlacedDomainEventHandler", "Shop.Api/Application/Orders/OrderPlacedDomainEventHandler.cs", 2, 1, 20),
            Sym(65, SymbolKind.Method, "Shop.Api.Application.Orders.OrderPlacedDomainEventHandler.Handle(OrderPlacedDomainEvent, CancellationToken)", "Shop.Api/Application/Orders/OrderPlacedDomainEventHandler.cs", 64, 5, 15),
            Sym(70, SymbolKind.Class, "Shop.Api.Application.Orders.CreateOrderCommand", "Shop.Api/Application/Orders/CreateOrderCommand.cs", 2),
            Sym(72, SymbolKind.Class, "Shop.Api.Application.Queries.GetOrderQuery", "Shop.Api/Application/Queries/GetOrderQuery.cs", 3),
            Sym(74, SymbolKind.Class, "Shop.Api.Application.Orders.OrderPlacedDomainEvent", "Shop.Api/Application/Orders/OrderPlacedDomainEvent.cs", 2),
            // 自研中间件、全局过滤器
            Sym(80, SymbolKind.Class, "Shop.Api.Middleware.RequestTimingMiddleware", "Shop.Api/Middleware/RequestTimingMiddleware.cs", null, 1, 30),
            Sym(81, SymbolKind.Method, "Shop.Api.Middleware.RequestTimingMiddleware.InvokeAsync(HttpContext)", "Shop.Api/Middleware/RequestTimingMiddleware.cs", 80, 8, 20),
            Sym(82, SymbolKind.Method, "Shop.Api.Middleware.Metrics.Record(TimeSpan)", "Shop.Api/Middleware/Metrics.cs", null, 3, 6),
            Sym(85, SymbolKind.Class, "Shop.Api.Filters.ApiExceptionFilter", "Shop.Api/Filters/ApiExceptionFilter.cs", null, 1, 20),
            Sym(86, SymbolKind.Method, "Shop.Api.Filters.ApiExceptionFilter.OnException(ExceptionContext)", "Shop.Api/Filters/ApiExceptionFilter.cs", 85, 5, 15),
            // 工作单元
            Sym(90, SymbolKind.Class, "Shop.Api.Data.ShopDbContext", "Shop.Api/Data/ShopDbContext.cs", null, 1, 60),
            Sym(91, SymbolKind.Method, "Shop.Api.Data.ShopDbContext.SaveEntitiesAsync(CancellationToken)", "Shop.Api/Data/ShopDbContext.cs", 90, 10, 20),
            Sym(92, SymbolKind.Method, "Shop.Api.Data.ShopDbContext.DispatchDomainEventsAsync()", "Shop.Api/Data/ShopDbContext.cs", 90, 22, 30),
            Sym(93, SymbolKind.Method, "Shop.Api.Data.ShopDbContext.CommitTransactionAsync(IDbContextTransaction)", "Shop.Api/Data/ShopDbContext.cs", 90, 32, 40),
            Sym(94, SymbolKind.Method, "Shop.Api.IntegrationEvents.IntegrationEventService.PublishThroughEventBusAsync(Guid)", "Shop.Api/IntegrationEvents/IntegrationEventService.cs", null, 5, 15),
            Sym(95, SymbolKind.Method, "Shop.Api.Data.ShopDbContext.BeginTransactionAsync()", "Shop.Api/Data/ShopDbContext.cs", 90, 42, 50),
            // 测试里的行为不算
            Sym(99, SymbolKind.Class, "Shop.Api.Tests.FakeBehavior", "Shop.Api.Tests/FakeBehavior.cs", null),
            // 外部
            Sym(800, SymbolKind.External, "external:MediatR.IPipelineBehavior<TRequest, TResponse>", "<external>"),
            Sym(801, SymbolKind.External, "external:MediatR.ISender.Send<TResponse>(IRequest<TResponse>, CancellationToken)", "<external>"),
            Sym(802, SymbolKind.External, "external:MediatR.IRequest<TResponse>", "<external>"),
            Sym(803, SymbolKind.External, "external:MediatR.INotification", "<external>"),
            Sym(804, SymbolKind.External, "external:MediatR.MediatRServiceConfiguration.AddOpenBehavior(Type, ServiceLifetime)", "<external>"),
            Sym(805, SymbolKind.External, "external:Microsoft.EntityFrameworkCore.DbContext", "<external>"),
            Sym(806, SymbolKind.External, "external:Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllers(IServiceCollection, Action<MvcOptions>)", "<external>"),
            Sym(810, SymbolKind.External, NextQn, "<external>"),
            Sym(811, SymbolKind.External, "external:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(ILogger, string, object[])", "<external>"),
            Sym(812, SymbolKind.External, "external:Microsoft.AspNetCore.Http.RequestDelegate.Invoke(HttpContext)", "<external>"),
            Sym(813, SymbolKind.External, "external:System.Diagnostics.Stopwatch.StartNew()", "<external>"),
            Sym(814, SymbolKind.External, "external:System.Linq.Enumerable.ToList<TSource>(IEnumerable<TSource>)", "<external>"),
        ];
        List<MapEdge> edges =
        [
            new(20, 800, EdgeKind.Implements), new(30, 800, EdgeKind.Implements), new(40, 800, EdgeKind.Implements), new(99, 800, EdgeKind.Implements),
            new(72, 802, EdgeKind.Implements), new(74, 803, EdgeKind.Implements),
            new(90, 805, EdgeKind.Inherits),
            new(1, 804, EdgeKind.Calls, 1.0, null, 5), new(1, 804, EdgeKind.Calls, 1.0, null, 6), new(1, 804, EdgeKind.Calls, 1.0, null, 7),
            new(1, 806, EdgeKind.Calls, 1.0, null, 9),
            // 分派：控制器 → ISender.Send（conduit）；分派边 → Handler
            new(50, 801, EdgeKind.Calls, 1.0, "conduit", 12), new(52, 801, EdgeKind.Calls, 1.0, "conduit", 24), new(50, 70, EdgeKind.Calls, 1.0, "conduit", 12),
            new(50, 61, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand", 12),
            new(51, 52, EdgeKind.Calls, 1.0, null, 18),
            new(52, 63, EdgeKind.Dispatches, 1.0, "dispatch:GetOrderQuery", 24),
            new(60, 70, EdgeKind.Dispatches, 1.0, "handles"), new(62, 72, EdgeKind.Dispatches, 1.0, "handles"), new(64, 74, EdgeKind.Dispatches, 1.0, "handles"),
            // Handler 里提交工作单元
            new(61, 91, EdgeKind.Calls, 1.0, null, 20),
            new(41, 93, EdgeKind.Calls, 1.0, null, 20),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [21] = [Ext(811, "LoggerExtensions.LogInformation", "external:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(ILogger, string, object[])", 100),
                    Ext(810, "RequestHandlerDelegate.Invoke", NextQn, 200),
                    Ext(811, "LoggerExtensions.LogInformation", "external:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(ILogger, string, object[])", 300)],
            // Audit：if 分支里直接 next()（短路），主 next 在后面
            [31] = [Ext(810, "RequestHandlerDelegate.Invoke", NextQn, 100, CallContext.Cond),
                    Call(32, "AuditBehavior.Prepare", "Shop.Api.Behaviors.AuditBehavior<TRequest, TResponse>.Prepare(TRequest)", 150),
                    Ext(814, "Enumerable.ToList", "external:System.Linq.Enumerable.ToList<TSource>(IEnumerable<TSource>)", 160),
                    Ext(810, "RequestHandlerDelegate.Invoke", NextQn, 200),
                    Call(34, "AuditWriter.Write", "Shop.Api.Auditing.AuditWriter.Write(object)", 300)],
            [41] = [Call(95, "ShopDbContext.BeginTransactionAsync", "Shop.Api.Data.ShopDbContext.BeginTransactionAsync()", 100),
                    Ext(810, "RequestHandlerDelegate.Invoke", NextQn, 200, CallContext.Callback),
                    Call(93, "ShopDbContext.CommitTransactionAsync", "Shop.Api.Data.ShopDbContext.CommitTransactionAsync(IDbContextTransaction)", 300, CallContext.Callback),
                    Call(94, "IntegrationEventService.PublishThroughEventBusAsync", "Shop.Api.IntegrationEvents.IntegrationEventService.PublishThroughEventBusAsync(Guid)", 400, CallContext.Callback)],
            [81] = [Ext(813, "Stopwatch.StartNew", "external:System.Diagnostics.Stopwatch.StartNew()", 100),
                    Ext(812, "RequestDelegate.Invoke", "external:Microsoft.AspNetCore.Http.RequestDelegate.Invoke(HttpContext)", 200),
                    Call(82, "Metrics.Record", "Shop.Api.Middleware.Metrics.Record(TimeSpan)", 300)],
            [91] = [Call(92, "ShopDbContext.DispatchDomainEventsAsync", "Shop.Api.Data.ShopDbContext.DispatchDomainEventsAsync()", 100)],
        };
        var signatures = new Dictionary<long, string>
        {
            [21] = BehaviorSignature, [31] = BehaviorSignature, [41] = BehaviorSignature,
            [81] = "Task InvokeAsync(HttpContext context)",
        };
        var files = new Dictionary<string, string>
        {
            ["Shop.Api/Program.cs"] = ShopProgram,
            ["Shop.Api/Behaviors/AuditBehavior.cs"] = "public class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IAuditableCommand\n{\n}\n",
            ["Shop.Api/Behaviors/LoggingBehavior.cs"] = "public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>\n{\n}\n",
        };
        List<EntryPoint> entries =
        [
            new(50, "Shop.Api", "aspnet.controller", "http", "/api/orders", "POST", "Shop.Api/Controllers/OrdersController.cs", 10),
            new(51, "Shop.Api", "aspnet.controller", "http", "/api/orders/{id}", "GET", "Shop.Api/Controllers/OrdersController.cs", 16),
            new(61, "Shop.Api", "mediatr.handler", "handler", "CreateOrderCommand", null, "Shop.Api/Application/Orders/CreateOrderCommandHandler.cs", 5),
        ];
        return new FrameworkInput(symbols, edges, Container("Shop.Api", "dotnet-web", "Shop.Api"), entries, [], Sources(calls, signatures, files));
    }

    [Fact]
    public void MediatR_StagesInOrder_BehaviorsByRegistration_NextSplit_TailAndHandlers()
    {
        var f = FrameworkFlowBuilder.Build(MediatRFixture());

        Assert.Equal(["middleware", "controller", "dispatcher", "behavior", "handler", "tail"], f.Stages.Select(s => s.Key));
        Assert.Equal(f.Stages.Select(s => s.Key), f.Flow.Actors.Select(a => a.Key));
        Assert.All(f.Flow.Actors, a => Assert.Equal("stage", a.Kind));
        // 行为顺序 = AddOpenBehavior 的书写顺序（不是字母序）；测试项目里的 FakeBehavior 不算
        Assert.Equal(["LoggingBehavior", "AuditBehavior", "TransactionBehavior"], f.Flow.Behaviors);
        Assert.True(f.Stages.Single(s => s.Key == "behavior").OrderKnown);

        // 中间件：书写顺序；注释里的、app.Run() 之后的、MapControllers 都不算；链式调用都认；全局过滤器排在中间件之后
        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "entry", "middleware"), (root.Label, root.Kind, root.Actor));
        Assert.Equal(["UseExceptionHandler", "RequestTimingMiddleware.InvokeAsync", "UseAuthentication", "UseAuthorization", "ApiExceptionFilter.OnException", "控制器"],
            root.Children.Select(c => c.Label));
        Assert.Equal("external-group", root.Children[0].Kind);
        var timing = root.Children[1];
        Assert.Equal((81L, "call"), (timing.SymbolId!.Value, timing.Kind));
        Assert.Contains("自研中间件", timing.Note);
        Assert.Equal(["Stopwatch.StartNew", "Metrics.Record"], timing.Children.Select(c => c.Label));
        Assert.Null(timing.Children[0].Note);
        Assert.Contains("next 之后", timing.Children[1].Note);
        Assert.Contains("MVC 全局过滤器", root.Children[4].Note);

        // 控制器占位 → 外部分派器 → 行为（前置）→ Handler 占位 → 后置（倒序）
        var controllers = root.Children[5];
        Assert.Equal(("controllers", 2), (controllers.Kind, controllers.Count));
        var dispatcher = Assert.Single(controllers.Children);
        Assert.Equal(("ISender.Send", 801L, "dispatcher"), (dispatcher.Label, dispatcher.SymbolId!.Value, dispatcher.Actor));
        Assert.Contains("MediatR", dispatcher.Note);
        Assert.Equal(
            ["LoggingBehavior.Handle", "AuditBehavior.Handle", "TransactionBehavior.Handle", "Handler",
             "ShopDbContext.CommitTransactionAsync", "IntegrationEventService.PublishThroughEventBusAsync", "AuditWriter.Write", "LoggerExtensions.LogInformation"],
            dispatcher.Children.Select(c => c.Label));
        var logging = dispatcher.Children[0];
        Assert.Equal(["LoggerExtensions.LogInformation"], logging.Children.Select(c => c.Label)); // next 之前
        Assert.DoesNotContain("有条件", logging.Note);
        var audit = dispatcher.Children[1];
        Assert.Equal(["AuditBehavior.Prepare"], audit.Children.Select(c => c.Label)); // 分支里的 next 不是切分点；LINQ 不算关键调用
        Assert.Contains("有条件", audit.Note);
        Assert.Contains("IAuditableCommand", audit.Note);   // where TRequest : IAuditableCommand
        Assert.DoesNotContain("只对", logging.Note);         // IRequest<TResponse> 约束不算条件
        Assert.Equal(["ShopDbContext.BeginTransactionAsync"], dispatcher.Children[2].Children.Select(c => c.Label));

        var handlers = dispatcher.Children[3];
        Assert.Equal(("handlers", 3, "handler"), (handlers.Kind, handlers.Count, handlers.Actor));
        var uow = Assert.Single(handlers.Children);
        Assert.Equal(("ShopDbContext.SaveEntitiesAsync", "tail"), (uow.Label, uow.Actor));
        // Handler 自己调的提交：不是框架统一提交，写清几个 Handler（3 个里 1 个直接调）
        Assert.StartsWith("部分 Handler 自己提交（1 个 Handler 直接调用，共 3 个 Handler）", uow.Note);
        Assert.Contains("领域事件", uow.Note);
        Assert.Equal(["ShopDbContext.DispatchDomainEventsAsync"], uow.Children.Select(c => c.Label));

        var tail = dispatcher.Children.Skip(4).ToList();
        Assert.All(tail, t => Assert.Equal("tail", t.Actor));
        Assert.Equal(["TransactionBehavior 后置 · 回调 / lambda 里", "TransactionBehavior 后置 · 回调 / lambda 里", "AuditBehavior 后置", "LoggingBehavior 后置"], tail.Select(t => t.Note));
        Assert.Contains(f.Stages.Single(s => s.Key == "tail").Symbols, s => s.Id == 93);

        // 深度与节点数一致
        Assert.All(All(root), n => Assert.All(n.Children, c => Assert.Equal(n.Depth + 1, c.Depth)));
        Assert.Equal(All(root).Count, f.Flow.Nodes);
        Assert.Equal(2, dispatcher.Depth);

        // Handler 清单：模块 = 消息命名空间段；种类；入口（GetOrderQuery 经 Load 反查到 Get）
        Assert.Equal(["Orders", "Queries"], f.Handlers.Select(g => g.Module));
        var orders = f.Handlers[0].Items;
        Assert.Equal([("CreateOrderCommand", "command"), ("OrderPlacedDomainEvent", "event")], orders.Select(i => (i.MessageType!, i.MessageKind!)));
        Assert.Equal(61, orders[0].Handler.Id);
        Assert.Equal([50L], orders[0].Entries.Select(e => e.SymbolId)); // 自己那条 handler 入口不算
        Assert.Empty(orders[1].Entries);
        var query = Assert.Single(f.Handlers[1].Items);
        Assert.Equal(("GetOrderQuery", "query", 63L), (query.MessageType, query.MessageKind, query.Handler.Id));
        Assert.Equal([51L], query.Entries.Select(e => e.SymbolId));

        Assert.Contains(f.Notes, n => n.Contains("MediatR") && n.Contains("ISender.Send"));
        Assert.Contains(f.Notes, n => n.Contains("AddOpenBehavior"));
    }

    [Fact]
    public void InheritedHandle_SubclassesTitledByBaseGenericArgs()
    {
        // eShop 的 IdentifiedCommandHandler<T, R>：子类自己不写 Handle，都落到基类那一个方法上
        var input = MediatRFixture();
        const string orders = "Shop.Api/Application/Orders/";
        List<MapSymbol> extra =
        [
            Sym(100, SymbolKind.Class, "Shop.Api.Application.Orders.IdentifiedCommandHandler<T, R>", orders + "IdentifiedCommandHandler.cs", 2, 1, 30),
            Sym(101, SymbolKind.Method, "Shop.Api.Application.Orders.IdentifiedCommandHandler<T, R>.Handle(IdentifiedCommand<T, R>, CancellationToken)", orders + "IdentifiedCommandHandler.cs", 100, 5, 25),
            Sym(102, SymbolKind.Class, "Shop.Api.Application.Orders.CancelOrderIdentifiedCommandHandler", orders + "CancelOrderIdentifiedCommandHandler.cs", 2, 2, 6),
            Sym(103, SymbolKind.Class, "Shop.Api.Application.Orders.ShipOrderIdentifiedCommandHandler", orders + "ShipOrderIdentifiedCommandHandler.cs", 2, 1, 5),
            Sym(104, SymbolKind.Class, "Shop.Api.Application.Orders.IdentifiedCommand<T, R>", orders + "IdentifiedCommand.cs", 2),
        ];
        List<MapEdge> extraEdges =
        [
            new(102, 100, EdgeKind.Inherits), new(103, 100, EdgeKind.Inherits),
            new(100, 104, EdgeKind.Dispatches, 1.0, "handles"), new(102, 104, EdgeKind.Dispatches, 1.0, "handles"), new(103, 104, EdgeKind.Dispatches, 1.0, "handles"),
        ];
        var files = new Dictionary<string, string>
        {
            // 主构造函数、基类实参跨行；Ship 那个读不到文件
            [orders + "CancelOrderIdentifiedCommandHandler.cs"] = "// header\npublic class CancelOrderIdentifiedCommandHandler(\n    IMediator mediator) : IdentifiedCommandHandler<CancelOrderCommand,\n        bool>(mediator)\n{\n}\n",
        };
        var src = input.Sources;
        var sources = new FrameworkSources { Calls = src.Calls, Signature = src.Signature, ReadFile = p => files.GetValueOrDefault(p) ?? src.ReadFile(p) };
        var f = FrameworkFlowBuilder.Build(input with { Symbols = [.. input.Symbols, .. extra], Edges = [.. input.Edges, .. extraEdges], Sources = sources });

        var items = f.Handlers.Single(g => g.Module == "Orders").Items.Where(i => i.Handler.Id == 101).ToList();
        // 基类 IdentifiedCommandHandler<T, R> 自己那条（"IdentifiedCommand"）和子类处理的是同一个消息，去掉，只留子类
        Assert.Equal(
            ["IdentifiedCommand<CancelOrderCommand, bool>", "IdentifiedCommand（ShipOrderIdentifiedCommandHandler）"],
            items.Select(i => i.MessageType!).Order(StringComparer.Ordinal));
        Assert.All(items, i => Assert.Equal("command", i.MessageKind));
    }

    [Theory]
    [InlineData("Ns.IdentifiedCommand<T, R>", 2)]
    [InlineData("Ns.Outer<A>.Envelope<Dictionary<K, V>>", 1)]
    [InlineData("Ns.Plain", 0)]
    public void GenericArity_CountsTrailingTypeParameters(string qn, int expected) =>
        Assert.Equal(expected, FrameworkFlowBuilder.GenericArity(qn));

    // ================= 夹具二：自研分派器 =================

    private const string AcmeStartup = """
        public class Startup
        {
            public void Configure(IApplicationBuilder app)
            {
                app.UseRouting();
                app.UseApiPipeline();
                app.UseEndpoints(e => e.MapControllers());
            }
        }
        """;

    private const string AcmePipelineExtensions = """
        public static class PipelineExtensions
        {
            public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
            {
                return app.UseMiddleware<TenantMiddleware>();
            }
        }
        """;

    private const string DelegateNext = "Acme.Application.CommandHandlerDelegate<TResponse>.Invoke()";
    private const string CustomBehaviorSignature = "Task<TResponse> HandleAsync(TRequest request, CommandHandlerDelegate<TResponse> next, CancellationToken ct)";

    private static FrameworkInput CustomFixture(ArchNode? container = null, List<EntryPoint>? entries = null)
    {
        List<MapSymbol> symbols =
        [
            Sym(100, SymbolKind.Class, "Acme.Web.Startup", "Acme.Web/Startup.cs", null, 1, 9),
            Sym(101, SymbolKind.Method, "Acme.Web.Startup.Configure(IApplicationBuilder)", "Acme.Web/Startup.cs", 100, 3, 8),
            Sym(102, SymbolKind.Method, "Acme.Web.PipelineExtensions.UseApiPipeline(IApplicationBuilder)", "Acme.Web/PipelineExtensions.cs", 103, 3, 6),
            Sym(103, SymbolKind.Class, "Acme.Web.PipelineExtensions", "Acme.Web/PipelineExtensions.cs", null, 1, 7),
            Sym(110, SymbolKind.Class, "Acme.Web.TenantMiddleware", "Acme.Web/TenantMiddleware.cs", null, 1, 20),
            Sym(111, SymbolKind.Method, "Acme.Web.TenantMiddleware.InvokeAsync(HttpContext, RequestDelegate)", "Acme.Web/TenantMiddleware.cs", 110, 5, 15),
            Sym(112, SymbolKind.Method, "Acme.Web.TenantResolver.Resolve(HttpContext)", "Acme.Web/TenantResolver.cs", null, 3, 8),
            Sym(115, SymbolKind.Class, "Acme.Web.LegacyMiddleware", "Acme.Web/LegacyMiddleware.cs", null, 1, 20),
            Sym(116, SymbolKind.Method, "Acme.Web.LegacyMiddleware.InvokeAsync(HttpContext)", "Acme.Web/LegacyMiddleware.cs", 115, 5, 15),
            // 分派器：接口 + 实现 + 同文件里的包装器（抽象 Handle + 实现类重写）
            Sym(120, SymbolKind.Interface, "Acme.Application.IDispatcher", "Acme.Application/IDispatcher.cs", null, 1, 5),
            Sym(121, SymbolKind.Method, "Acme.Application.IDispatcher.SendAsync<TResponse>(ICommand<TResponse>, CancellationToken)", "Acme.Application/IDispatcher.cs", 120, 3, 3),
            Sym(122, SymbolKind.Class, "Acme.Infrastructure.Dispatcher", "Acme.Infrastructure/Dispatcher.cs", null, 30, 50),
            Sym(123, SymbolKind.Method, "Acme.Infrastructure.Dispatcher.SendAsync<TResponse>(ICommand<TResponse>, CancellationToken)", "Acme.Infrastructure/Dispatcher.cs", 122, 35, 45),
            Sym(127, SymbolKind.Class, "Acme.Infrastructure.RequestWrapper<TResponse>", "Acme.Infrastructure/Dispatcher.cs", null, 1, 5),
            Sym(124, SymbolKind.Method, "Acme.Infrastructure.RequestWrapper<TResponse>.Handle(ICommand<TResponse>, IServiceProvider)", "Acme.Infrastructure/Dispatcher.cs", 127, 3, 3),
            Sym(128, SymbolKind.Class, "Acme.Infrastructure.RequestWrapperImpl<TCommand, TResponse>", "Acme.Infrastructure/Dispatcher.cs", null, 7, 28),
            Sym(129, SymbolKind.Method, "Acme.Infrastructure.RequestWrapperImpl<TCommand, TResponse>.Handle(ICommand<TResponse>, IServiceProvider)", "Acme.Infrastructure/Dispatcher.cs", 128, 9, 27),
            Sym(125, SymbolKind.Interface, "Acme.Application.IEventPublisher", "Acme.Application/IEventPublisher.cs", null, 1, 5),
            Sym(126, SymbolKind.Method, "Acme.Application.IEventPublisher.PublishAsync<TEvent>(TEvent, CancellationToken)", "Acme.Application/IEventPublisher.cs", 125, 3, 3),
            Sym(190, SymbolKind.Class, "Acme.Infrastructure.EventPublisher", "Acme.Infrastructure/EventPublisher.cs", null, 1, 30),
            Sym(191, SymbolKind.Method, "Acme.Infrastructure.EventPublisher.PublishAsync<TEvent>(TEvent, CancellationToken)", "Acme.Infrastructure/EventPublisher.cs", 190, 5, 25),
            // 控制器
            Sym(130, SymbolKind.Method, "Acme.Web.UsersController.Create(CreateUserCommand)", "Acme.Web/UsersController.cs", null, 5, 9),
            Sym(131, SymbolKind.Method, "Acme.Web.UsersController.Rename(RenameUserCommand)", "Acme.Web/UsersController.cs", null, 11, 15),
            Sym(135, SymbolKind.Method, "Acme.Worker.Jobs.Run()", "Acme.Worker/Jobs.cs", null, 3, 9),
            // Handler 接口与 Handler
            Sym(150, SymbolKind.Interface, "Acme.Application.ICommandHandler<TCommand, TResponse>", "Acme.Application/ICommandHandler.cs", null, 1, 5),
            Sym(151, SymbolKind.Method, "Acme.Application.ICommandHandler<TCommand, TResponse>.HandleAsync(TCommand, CancellationToken)", "Acme.Application/ICommandHandler.cs", 150, 3, 3),
            Sym(139, SymbolKind.Namespace, "Acme.Application.Users", "Acme.Application/Users/CreateUserCommand.cs"),
            Sym(140, SymbolKind.Class, "Acme.Application.Users.CreateUserCommandHandler", "Acme.Application/Users/CreateUserCommandHandler.cs", 139, 1, 30),
            Sym(141, SymbolKind.Method, "Acme.Application.Users.CreateUserCommandHandler.HandleAsync(CreateUserCommand, CancellationToken)", "Acme.Application/Users/CreateUserCommandHandler.cs", 140, 5, 25),
            Sym(145, SymbolKind.Class, "Acme.Application.Users.CreateUserCommand", "Acme.Application/Users/CreateUserCommand.cs", 139),
            Sym(146, SymbolKind.Interface, "Acme.Application.ICommand<TResponse>", "Acme.Application/ICommand.cs"),
            // 行为
            Sym(160, SymbolKind.Interface, "Acme.Application.IPipelineBehavior<TRequest, TResponse>", "Acme.Application/IPipelineBehavior.cs", null, 1, 5),
            Sym(161, SymbolKind.Method, "Acme.Application.IPipelineBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", "Acme.Application/IPipelineBehavior.cs", 160, 3, 3),
            Sym(175, SymbolKind.Class, "Acme.Application.Behaviors.UnitOfWorkBehavior<TRequest, TResponse>", "Acme.Application/Behaviors/UnitOfWorkBehavior.cs", null, 1, 20),
            Sym(176, SymbolKind.Method, "Acme.Application.Behaviors.UnitOfWorkBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", "Acme.Application/Behaviors/UnitOfWorkBehavior.cs", 175, 5, 15),
            Sym(170, SymbolKind.Class, "Acme.Application.Behaviors.ValidationBehavior<TRequest, TResponse>", "Acme.Application/Behaviors/ValidationBehavior.cs", null, 1, 20),
            Sym(171, SymbolKind.Method, "Acme.Application.Behaviors.ValidationBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", "Acme.Application/Behaviors/ValidationBehavior.cs", 170, 5, 15),
            Sym(172, SymbolKind.Method, "Acme.Application.Behaviors.ValidationBehavior<TRequest, TResponse>.Validate(TRequest)", "Acme.Application/Behaviors/ValidationBehavior.cs", 170, 16, 19),
            Sym(180, SymbolKind.Interface, "Acme.Application.IUnitOfWork", "Acme.Application/IUnitOfWork.cs", null, 1, 5),
            Sym(181, SymbolKind.Method, "Acme.Application.IUnitOfWork.CommitAsync(CancellationToken)", "Acme.Application/IUnitOfWork.cs", 180, 3, 3),
            Sym(800, SymbolKind.External, "external:Microsoft.AspNetCore.Http.IMiddleware", "<external>"),
            Sym(801, SymbolKind.External, "external:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(ILogger, string, object[])", "<external>"),
        ];
        List<MapEdge> edges =
        [
            new(110, 800, EdgeKind.Implements),
            new(122, 120, EdgeKind.Implements), new(190, 125, EdgeKind.Implements), new(140, 150, EdgeKind.Implements), new(145, 146, EdgeKind.Implements),
            new(170, 160, EdgeKind.Implements), new(175, 160, EdgeKind.Implements),
            new(128, 127, EdgeKind.Inherits),
            new(130, 121, EdgeKind.Calls, 1.0, "conduit", 7), new(131, 121, EdgeKind.Calls, 1.0, "conduit", 13), new(135, 121, EdgeKind.Calls, 1.0, "conduit", 5),
            new(130, 141, EdgeKind.Dispatches, 1.0, "dispatch:CreateUserCommand", 7),
            new(141, 126, EdgeKind.Calls, 1.0, "conduit", 20),
            new(140, 145, EdgeKind.Dispatches, 1.0, "handles"),
        ];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [111] = [Call(112, "TenantResolver.Resolve", "Acme.Web.TenantResolver.Resolve(HttpContext)", 100),
                     Call(null, "next", "next", 200, kind: "unresolved")],   // tree-sitter：next(context) 没解析到符号
            [123] = [Ext(802, "ArgumentNullException.ThrowIfNull", "external:System.ArgumentNullException.ThrowIfNull(object, string)", 100),
                     Call(124, "RequestWrapper.Handle", "Acme.Infrastructure.RequestWrapper<TResponse>.Handle(ICommand<TResponse>, IServiceProvider)", 200),
                     Ext(801, "LoggerExtensions.LogDebug", "external:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(ILogger, string, object[])", 300)],
            [129] = [Call(151, "ICommandHandler.HandleAsync", "Acme.Application.ICommandHandler<TCommand, TResponse>.HandleAsync(TCommand, CancellationToken)", 100, CallContext.Callback),
                     Call(161, "IPipelineBehavior.HandleAsync", "Acme.Application.IPipelineBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", 200, CallContext.Callback),
                     Call(null, DelegateNext, DelegateNext, 300, kind: "unresolved")],
            [171] = [Call(172, "ValidationBehavior.Validate", "Acme.Application.Behaviors.ValidationBehavior<TRequest, TResponse>.Validate(TRequest)", 100),
                     Call(null, DelegateNext, DelegateNext, 200, kind: "unresolved")],
            [176] = [Call(null, DelegateNext, DelegateNext, 100, kind: "unresolved"),
                     Call(181, "IUnitOfWork.CommitAsync", "Acme.Application.IUnitOfWork.CommitAsync(CancellationToken)", 200)],
        };
        var signatures = new Dictionary<long, string>
        {
            [101] = "void Configure(IApplicationBuilder app)",
            [102] = "IApplicationBuilder UseApiPipeline(IApplicationBuilder app)", // Roslyn 的签名里没有 this
            [111] = "Task InvokeAsync(HttpContext context, RequestDelegate next)",
            [116] = "Task InvokeAsync(HttpContext context)",
            [171] = CustomBehaviorSignature, [176] = CustomBehaviorSignature,
        };
        var files = new Dictionary<string, string>
        {
            ["Acme.Web/Startup.cs"] = AcmeStartup,
            ["Acme.Web/PipelineExtensions.cs"] = AcmePipelineExtensions,
        };
        entries ??=
        [
            new(130, "Acme.Web", "aspnet.controller", "http", "/api/users", "POST", "Acme.Web/UsersController.cs", 5),
            new(131, "Acme.Web", "aspnet.controller", "http", "/api/users/rename", "POST", "Acme.Web/UsersController.cs", 11),
        ];
        // DI：Validation 在第 10 行、UnitOfWork 在第 11 行（符号表里 UnitOfWork 在前）；别的容器里的注册不算
        List<FrameworkRegistration> di =
        [
            new(160, 175, "Acme.Application/DependencyInjection.cs", 11, false),
            new(160, 170, "Acme.Application/DependencyInjection.cs", 10, false),
            new(160, 170, "Other.Host/Program.cs", 3, false),
        ];
        container ??= Container("Acme.Web", "dotnet-web", "Acme.Web", "Acme.Application", "Acme.Infrastructure");
        return new FrameworkInput(symbols, edges, container, entries, di, Sources(calls, signatures, files));
    }

    [Fact]
    public void CustomDispatcher_ImplementationExpanded_DiOrder_ExtensionMiddleware_TailFromBehaviorAndPublisher()
    {
        var f = FrameworkFlowBuilder.Build(CustomFixture());

        Assert.Equal(["middleware", "controller", "dispatcher", "behavior", "handler", "tail"], f.Stages.Select(s => s.Key));
        // 中间件：Startup.Configure 里的书写顺序；UseApiPipeline 是仓库里的扩展方法，进去找到 UseMiddleware<TenantMiddleware>；UseEndpoints 不算；
        // LegacyMiddleware 没找到注册位置 → 追加在最后，阶段顺序未知
        var root = f.Flow.Root;
        Assert.Equal(["UseRouting", "TenantMiddleware.InvokeAsync", "LegacyMiddleware.InvokeAsync", "控制器"], root.Children.Select(c => c.Label));
        Assert.Contains("经 UseApiPipeline()", root.Children[1].Note);
        Assert.Equal(["TenantResolver.Resolve"], root.Children[1].Children.Select(c => c.Label)); // next(context) 之前；next 本身不画
        Assert.Contains("顺序未知", root.Children[2].Note);
        Assert.False(f.Stages.Single(s => s.Key == "middleware").OrderKnown);

        var dispatcher = Assert.Single(root.Children[3].Children);
        // 接口方法换成实现；包装器的抽象 Handle 落到同文件里的重写，再列一层（它怎么解析 Handler、怎么串行为）
        Assert.Equal((123L, "Dispatcher.SendAsync"), (dispatcher.SymbolId!.Value, dispatcher.Label));
        Assert.Contains("自研分派器", dispatcher.Note);
        Assert.Contains("经接口 IDispatcher", dispatcher.Note);
        var wrapper = dispatcher.Children[0];
        Assert.Equal("RequestWrapper.Handle", wrapper.Label); // ThrowIfNull 是噪音，不画
        Assert.Equal(["ICommandHandler.HandleAsync", "IPipelineBehavior.HandleAsync"], wrapper.Children.Select(c => c.Label));

        Assert.Equal(["ValidationBehavior", "UnitOfWorkBehavior"], f.Flow.Behaviors); // DI 注册顺序
        Assert.Equal(
            ["RequestWrapper.Handle", "ValidationBehavior.HandleAsync", "UnitOfWorkBehavior.HandleAsync", "Handler", "IUnitOfWork.CommitAsync", "LoggerExtensions.LogDebug"],
            dispatcher.Children.Select(c => c.Label));
        Assert.Equal(["ValidationBehavior.Validate"], dispatcher.Children[1].Children.Select(c => c.Label));
        Assert.Empty(dispatcher.Children[2].Children);                       // next() 在最前：没有前置
        Assert.Equal("UnitOfWorkBehavior 后置", dispatcher.Children[4].Note);
        Assert.Equal(("tail", "分派器后置"), (dispatcher.Children[5].Actor, dispatcher.Children[5].Note));

        // Handler 里调的事件发布（多数调用方是 Handler）放进收尾，挂在 Handler 占位下面，不算分派器
        var handlers = dispatcher.Children[3];
        Assert.Equal(1, handlers.Count);
        var publish = Assert.Single(handlers.Children);
        Assert.Equal(("EventPublisher.PublishAsync", 191L, "tail"), (publish.Label, publish.SymbolId!.Value, publish.Actor));
        Assert.Contains("事件发布", publish.Note);
        Assert.Single(f.Stages.Single(s => s.Key == "dispatcher").Symbols);

        var item = Assert.Single(Assert.Single(f.Handlers).Items);
        Assert.Equal(("Users", "CreateUserCommand", "command", 141L), (f.Handlers[0].Module, item.MessageType, item.MessageKind, item.Handler.Id));
        Assert.Equal([130L], item.Entries.Select(e => e.SymbolId));
        Assert.Contains(f.Notes, n => n.Contains("自研") && n.Contains("Dispatcher.SendAsync"));
        Assert.Contains(f.Notes, n => n.Contains("DI 注册"));
    }

    [Fact]
    public void WorkerContainer_HasNoMiddlewareOrControllers_EntryPlaceholderInstead()
    {
        var worker = Container("Acme.Worker", "dotnet-worker", "Acme.Worker", "Acme.Application", "Acme.Infrastructure");
        var f = FrameworkFlowBuilder.Build(CustomFixture(worker, [new(135, "Acme.Worker", "hosted-service", "hosted", null, null, "Acme.Worker/Jobs.cs", 3)]));

        Assert.DoesNotContain(f.Stages, s => s.Key == "middleware");
        var controller = f.Stages.First();
        Assert.Equal(("controller", "入口"), (controller.Key, controller.Label));
        var root = f.Flow.Root;
        Assert.Equal(("入口", "controller"), (root.Label, root.Actor));
        var entry = Assert.Single(root.Children);
        Assert.Equal(("controllers", 1), (entry.Kind, entry.Count));
        Assert.StartsWith("入口", entry.Label);
        // 占位下面是每个常驻服务入口自己（它的关键调用）；它调到了分派器（conduit），分派器 → 行为 → Handler 挂在它下面
        var job = Assert.Single(entry.Children);
        Assert.Equal((135L, "Run", "controller"), (job.SymbolId!.Value, job.Label, job.Actor));
        Assert.Equal("常驻服务：下面是它自己的关键调用；往下调到分派器", job.Note);
        Assert.Equal("Dispatcher.SendAsync", Assert.Single(job.Children).Label);
        Assert.Contains(f.Notes, n => n.Contains("不是 Web 服务"));
    }

    [Fact]
    public void NoDispatcher_NoBehaviors_NotesExplainMissingStages()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "Plain.Api.HomeController.Index()", "Plain.Api/HomeController.cs", null, 3, 9),
            Sym(2, SymbolKind.Method, "Plain.Api.Services.Catalog.List()", "Plain.Api/Services/Catalog.cs", null, 3, 9),
        ];
        var input = new FrameworkInput(symbols, [new(1, 2, EdgeKind.Calls)], Container("Plain.Api", "dotnet-web", "Plain.Api"),
            [new(1, "Plain.Api", "aspnet.controller", "http", "/", "GET", "Plain.Api/HomeController.cs", 3)], [],
            Sources(new(), new(), new()));
        var f = FrameworkFlowBuilder.Build(input);

        Assert.Equal(["controller"], f.Stages.Select(s => s.Key));
        Assert.Empty(f.Handlers);
        Assert.Contains("没有发现分派器：入口直接调用服务", f.Notes);
        Assert.Contains(f.Notes, n => n.StartsWith("没有发现行为链", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("没有找到管道配置", StringComparison.Ordinal));
        Assert.Equal(("controllers", 1), (f.Flow.Root.Children.Single().Kind, f.Flow.Root.Children.Single().Count));
    }

    [Fact]
    public void UnknownContainerKind_ReturnsNoteOnly()
    {
        // 别的语言各有自己的 Build（Spring / Go / Python / Node / 前端）；命令行工具这类不是请求服务的容器只给一句说明
        var input = new FrameworkInput([], [], Container("tool", "node-cli", "tool"), [], [], Sources(new(), new(), new()));
        var f = FrameworkFlowBuilder.Build(input);
        Assert.Empty(f.Stages);
        Assert.Contains(f.Notes, n => n.Contains("node-cli"));
    }

    [Fact]
    public void ScanReceiver_ReadsChainsSkipsPropertiesAndComments()
    {
        var text = FrameworkFlowBuilder.StripCommentsAndStrings("""
            app.UseA().UseB<Foo>(x => x.Y("app.UseFake()"));
            // app.UseCommented();
            if (app.Environment.IsDevelopment()) app.UseDev();
            other.UseNot();
            """);
        var calls = FrameworkFlowBuilder.ScanReceiver(text, "app", 0, text.Length);
        Assert.Equal(["UseA", "UseB", "UseDev"], calls.Select(c => c.Name));
        Assert.Equal("Foo", calls[1].Generic);
    }

    [Fact]
    public void BehaviorRegistrationsInText_ReadsMediatRForms()
    {
        var found = FrameworkFlowBuilder.BehaviorRegistrationsInText("""
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddBehavior<IPipelineBehavior<Ping, Pong>, PingBehavior>();
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(Ns.ValidationBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditBehavior<,>));
            """);
        Assert.Equal(["LoggingBehavior", "PingBehavior", "ValidationBehavior", "AuditBehavior"], found.Select(f => f.Name));
    }

    [Fact]
    public void DelegateParameters_And_NextCall()
    {
        var (names, types) = FrameworkFlowBuilder.DelegateParameters(CustomBehaviorSignature);
        Assert.Equal(["next"], names);
        Assert.Equal(["CommandHandlerDelegate"], types);
        Assert.True(FrameworkFlowBuilder.IsNextCall(new FrameworkCall(null, "unresolved", DelegateNext, DelegateNext, 1, 1, null, false, []), names, types));
        Assert.True(FrameworkFlowBuilder.IsNextCall(new FrameworkCall(null, "unresolved", "next", "next", 1, 1, null, false, []), names, types));
        Assert.False(FrameworkFlowBuilder.IsNextCall(new FrameworkCall(5, "call", "Svc.Next", "Acme.Svc.next()", 1, 1, null, true, []), names, types));
    }

    // ================= 真实项目审计（2026-10-16）挑出的误判 =================

    private const string AcmeHost = """
        public static class ServerHost
        {
            public static WebApplication Build(Options o)
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
                var app = builder.Build();
                app.UseCors();
                if (o.Token != null)
                {
                    app.Use(async (ctx, next) => { await next(); });
                }
                app.MapGet("/api/health", () => 1);
                app.MapPost("/api/index", () => 2);
                return app;
            }
        }
        """;

    [Fact]
    public void ConsoleContainer_HostingKestrel_DrawnAsWeb_FromTheFileThatBuildsTheApp()
    {
        // 控制台程序（dotnet-exe）的 serve 子命令在成员项目的 ServerHost.Build 里起 Kestrel：不叫 Program.cs，SubKind 也不是 dotnet-web
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "Acme.Cli.Program.Main(string[])", "Acme.Cli/Program.cs", null, 1, 5),
            Sym(10, SymbolKind.Class, "Acme.Server.ServerHost", "Acme.Server/ServerHost.cs", null, 1, 16),
            Sym(11, SymbolKind.Method, "Acme.Server.ServerHost.Build(Options)", "Acme.Server/ServerHost.cs", 10, 3, 15),
        ];
        List<EntryPoint> entries =
        [
            new(1, "Acme.Cli", "csharp.main", "hosted", "Acme.Cli", null, "Acme.Cli/Program.cs", 1),
            new(11, "Acme.Cli", "aspnet.minimal", "http", "/api/health", "GET", "Acme.Server/ServerHost.cs", 12),
            new(11, "Acme.Cli", "aspnet.minimal", "http", "/api/index", "POST", "Acme.Server/ServerHost.cs", 13),
        ];
        var input = new FrameworkInput(symbols, [], Container("Acme.Cli", "dotnet-exe", "Acme.Cli", "Acme.Server"), entries, [],
            Sources([], [], new Dictionary<string, string> { ["Acme.Server/ServerHost.cs"] = AcmeHost }));
        var f = FrameworkFlowBuilder.Build(input);

        var root = f.Flow.Root;
        Assert.Equal(("HTTP 请求", "middleware"), (root.Label, root.Actor));
        Assert.Equal(["UseCors", "Use（内联中间件）", "终结点"], root.Children.Select(c => c.Label));
        Assert.Equal("框架内置（ServerHost.cs:7）", root.Children[0].Note);
        Assert.Equal("内联中间件（ServerHost.cs:10，lambda 写在管道配置里）；在 if 里，按条件注册", root.Children[1].Note);
        Assert.Equal(("controllers", 2), (root.Children[2].Kind, root.Children[2].Count));
        // 阶段个数与骨架一致：外部的中间件也算（Id = 0）；同一个宿主方法上的两条路由各算一条
        Assert.Equal([("middleware", 2), ("controller", 2)], f.Stages.Select(s => (s.Key, s.Symbols.Count)));
        Assert.Equal("终结点", f.Stages[1].Label);
        Assert.DoesNotContain(f.Notes, n => n.Contains("不是 Web 服务"));
        Assert.Contains("项目类型是 dotnet-exe，但 ServerHost.cs 里起了 Web 宿主（WebApplication / Kestrel），有 2 个 HTTP 入口：按 Web 服务画", f.Notes);
        Assert.Contains(f.Notes, n => n.StartsWith("中间件按 ServerHost.cs 里 app.UseXxx 的书写顺序；另有 2 处终结点注册（MapGet、MapPost），不算中间件", StringComparison.Ordinal));
        Assert.Contains("另有 1 个程序入口（Main：宿主本身，命令行的其他子命令也从这里进），没画在这条请求链上", f.Notes);
    }

    [Fact]
    public void WorkerLike_HttpDoesNotDispatch_DispatcherHangsUnderTheHostedServiceThatCallsIt()
    {
        // HTTP 只有一个不调分派器的终结点；真正调分派器的是常驻服务 Jobs.Run：分派器 → 行为 → Handler 挂在它下面，不挂在 HTTP 下
        var web = Container("Acme.Web", "dotnet-web", "Acme.Web", "Acme.Application", "Acme.Infrastructure", "Acme.Worker");
        var f = FrameworkFlowBuilder.Build(CustomFixture(web,
        [
            new(112, "Acme.Web", "aspnet.minimal", "http", "/version", "GET", "Acme.Web/TenantResolver.cs", 3),
            new(135, "Acme.Web", "hosted.background", "hosted", null, null, "Acme.Worker/Jobs.cs", 3),
        ]));

        var root = f.Flow.Root;
        Assert.Equal("入口", root.Label);
        var http = root.Children[0];
        Assert.Equal(("HTTP 请求", "entry"), (http.Label, http.Kind));
        var endpoints = http.Children.Single(c => c.Kind == "controllers");
        Assert.Equal(("终结点", 1, "这 1 个入口都不经分派器"), (endpoints.Label, endpoints.Count, endpoints.Note));
        Assert.Empty(endpoints.Children);
        var hosted = root.Children[1];
        Assert.Equal(("常驻服务 / 任务", "controllers", 1), (hosted.Label, hosted.Kind, hosted.Count));
        var job = Assert.Single(hosted.Children);
        Assert.Equal((135L, "常驻服务：下面是它自己的关键调用；往下调到分派器"), (job.SymbolId!.Value, job.Note));
        Assert.Equal("Dispatcher.SendAsync", job.Children.Single(c => c.Actor == "dispatcher").Label);
        Assert.Contains(f.Notes, n => n.Contains("HTTP 的 1 个终结点都不调分派器，真正的活在这些常驻服务里"));
    }

    [Fact]
    public void SendAndQueryOnOneDispatcher_MergedRow_NoDuplicateBehaviorImpls_DirectEntriesCounted()
    {
        var input = CustomFixture();
        List<MapSymbol> extra =
        [
            Sym(200, SymbolKind.Method, "Acme.Application.IDispatcher.QueryAsync<TResponse>(IQuery<TResponse>, CancellationToken)", "Acme.Application/IDispatcher.cs", 120, 4, 4),
            Sym(201, SymbolKind.Method, "Acme.Infrastructure.Dispatcher.QueryAsync<TResponse>(IQuery<TResponse>, CancellationToken)", "Acme.Infrastructure/Dispatcher.cs", 122, 46, 49),
            Sym(132, SymbolKind.Method, "Acme.Web.UsersController.Get(GetUserQuery)", "Acme.Web/UsersController.cs", null, 17, 19),
            Sym(133, SymbolKind.Method, "Acme.Web.UsersController.Ping()", "Acme.Web/UsersController.cs", null, 21, 22),
        ];
        List<MapEdge> extraEdges = [new(132, 200, EdgeKind.Calls, 1.0, "conduit", 18)];
        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>
        {
            [201] = [Call(124, "RequestWrapper.Handle", "Acme.Infrastructure.RequestWrapper<TResponse>.Handle(ICommand<TResponse>, IServiceProvider)", 4700)],
            // 包装器里调行为接口，大纲把实现按字母序挂在下面（via impl）——和下面按注册顺序画的行为链重复
            [129] = [Call(151, "ICommandHandler.HandleAsync", "Acme.Application.ICommandHandler<TCommand, TResponse>.HandleAsync(TCommand, CancellationToken)", 100, CallContext.Callback),
                     Call(161, "IPipelineBehavior.HandleAsync", "Acme.Application.IPipelineBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", 200, CallContext.Callback, "call", true,
                         Call(176, "UnitOfWorkBehavior.HandleAsync", "Acme.Application.Behaviors.UnitOfWorkBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", 200, null, "impl"),
                         Call(171, "ValidationBehavior.HandleAsync", "Acme.Application.Behaviors.ValidationBehavior<TRequest, TResponse>.HandleAsync(TRequest, CommandHandlerDelegate<TResponse>, CancellationToken)", 200, null, "impl")),
                     Call(null, DelegateNext, DelegateNext, 300, kind: "unresolved")],
        };
        var src = input.Sources;
        var sources = new FrameworkSources { Calls = id => calls.TryGetValue(id, out var c) ? c : src.Calls(id), Signature = src.Signature, ReadFile = src.ReadFile };
        var f = FrameworkFlowBuilder.Build(input with
        {
            Symbols = [.. input.Symbols, .. extra], Edges = [.. input.Edges, .. extraEdges], Sources = sources,
            Entries = [.. input.Entries,
                new(132, "Acme.Web", "aspnet.controller", "http", "/api/users/{id}", "GET", "Acme.Web/UsersController.cs", 17),
                new(133, "Acme.Web", "aspnet.controller", "http", "/api/ping", "GET", "Acme.Web/UsersController.cs", 21)],
        });

        var controllers = f.Flow.Root.Children.Single(c => c.Kind == "controllers");
        Assert.Equal("其中 1 个入口不经分派器（直接调服务），3 个经 Dispatcher.SendAsync / QueryAsync", controllers.Note);
        // Send / Query 对等，并成一行（不是"QueryAsync 主、SendAsync 另一个分派方法"）；下面各列一行它自己的包装器步骤
        var dispatcher = Assert.Single(controllers.Children);
        Assert.Equal(("Dispatcher.SendAsync / QueryAsync", "自研分派器（3 处调用：SendAsync 2、QueryAsync 1，经接口 IDispatcher）"), (dispatcher.Label, dispatcher.Note));
        Assert.Equal(["Dispatcher.SendAsync", "Dispatcher.QueryAsync", "ValidationBehavior.HandleAsync", "UnitOfWorkBehavior.HandleAsync", "Handler"],
            dispatcher.Children.Take(5).Select(c => c.Label));
        Assert.Equal(("2 处调用", "1 处调用"), (dispatcher.Children[0].Note, dispatcher.Children[1].Note));
        var pipe = All(dispatcher.Children[0]).Single(n => n.Label == "IPipelineBehavior.HandleAsync");
        Assert.Empty(pipe.Children);
        Assert.Equal("回调 / lambda 里 · 2 个实现按注册顺序画在下面的行为链", pipe.Note);
        // 并成一行的分派器在阶段里算一条（下面各方法那几行挂在它下面，不另算）
        Assert.Single(f.Stages.Single(s => s.Key == "dispatcher").Symbols);
        Assert.Contains(f.Notes, n => n.StartsWith("分派器：自研 Dispatcher.SendAsync / QueryAsync，3 处调用（Dispatcher.SendAsync 2、Dispatcher.QueryAsync 1", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.StartsWith("1 个 HTTP 入口不经分派器（8 层内没调到 Dispatcher.SendAsync / QueryAsync，直接调服务）", StringComparison.Ordinal));
    }

    /// <summary>
    /// 没有分派器的服务（集成事件经 RabbitMQ 进来）：Handler 不挂在 HTTP 链下，挂在事件总线那个常驻服务下；Handler 清单按 Handler 所在命名空间分模块；
    /// 开放泛型 Handler 接多种消息时各条只算分派自己那种消息的入口；Handler 自己调仓储 SaveChangesAsync 写成"部分 Handler 自己提交"。
    /// </summary>
    [Fact]
    public void IntegrationEventHandlers_UnderBusConsumer_ModuleByHandlerNamespace_EntriesPerMessage_PartialCommit()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Module, "Shop.Catalog/Program.cs", "Shop.Catalog/Program.cs", null, 1, 4),
            Sym(2, SymbolKind.Method, "Shop.Catalog.CatalogApi.Create(CreateItem)", "Shop.Catalog/CatalogApi.cs", null, 3, 5),
            Sym(3, SymbolKind.Method, "Shop.Catalog.CatalogApi.Delete(DeleteItem)", "Shop.Catalog/CatalogApi.cs", null, 7, 9),
            // 集成事件 Handler：Handler 在 Shop.Catalog.IntegrationEvents.EventHandling，消息在共享的 Shop.Domain.Events
            Sym(9, SymbolKind.Namespace, "Shop.Catalog.IntegrationEvents.EventHandling", "Shop.Catalog/IntegrationEvents/EventHandling/OrderPaidHandler.cs"),
            Sym(10, SymbolKind.Class, "Shop.Catalog.IntegrationEvents.EventHandling.OrderPaidIntegrationEventHandler", "Shop.Catalog/IntegrationEvents/EventHandling/OrderPaidHandler.cs", 9, 1, 9),
            Sym(11, SymbolKind.Method, "Shop.Catalog.IntegrationEvents.EventHandling.OrderPaidIntegrationEventHandler.Handle(OrderPaidIntegrationEvent)", "Shop.Catalog/IntegrationEvents/EventHandling/OrderPaidHandler.cs", 10, 3, 8),
            Sym(12, SymbolKind.Interface, "Shop.EventBus.IIntegrationEventHandler<TEvent>", "Shop.EventBus/IIntegrationEventHandler.cs"),
            Sym(13, SymbolKind.Namespace, "Shop.Domain.Events", "Shop.Domain/Events/OrderPaidIntegrationEvent.cs"),
            Sym(14, SymbolKind.Class, "Shop.Domain.Events.OrderPaidIntegrationEvent", "Shop.Domain/Events/OrderPaidIntegrationEvent.cs", 13),
            Sym(20, SymbolKind.Class, "Shop.EventBus.RabbitMQEventBus", "Shop.EventBus/RabbitMQEventBus.cs", null, 1, 40),
            Sym(21, SymbolKind.Method, "Shop.EventBus.RabbitMQEventBus.StartAsync(CancellationToken)", "Shop.EventBus/RabbitMQEventBus.cs", 20, 10, 30),
            // 一个开放泛型 Handler 接两种命令（入口各分派一种）；它自己调仓储接口的 SaveChangesAsync
            Sym(29, SymbolKind.Namespace, "Shop.Catalog.Jobs", "Shop.Catalog/Jobs/EnqueueJobHandler.cs"),
            Sym(30, SymbolKind.Class, "Shop.Catalog.Jobs.EnqueueJobHandler<TCommand>", "Shop.Catalog/Jobs/EnqueueJobHandler.cs", 29, 1, 20),
            Sym(31, SymbolKind.Method, "Shop.Catalog.Jobs.EnqueueJobHandler<TCommand>.HandleAsync(TCommand, CancellationToken)", "Shop.Catalog/Jobs/EnqueueJobHandler.cs", 30, 3, 19),
            Sym(32, SymbolKind.Class, "Shop.Catalog.Jobs.CreateItem", "Shop.Catalog/Jobs/Commands.cs", 29),
            Sym(33, SymbolKind.Class, "Shop.Catalog.Jobs.DeleteItem", "Shop.Catalog/Jobs/Commands.cs", 29),
            Sym(40, SymbolKind.Interface, "Shop.Catalog.Data.IJobRepository", "Shop.Catalog/Data/IJobRepository.cs", null, 1, 5),
            Sym(41, SymbolKind.Method, "Shop.Catalog.Data.IJobRepository.SaveChangesAsync(CancellationToken)", "Shop.Catalog/Data/IJobRepository.cs", 40, 3, 3),
            Sym(42, SymbolKind.Class, "Shop.Catalog.Data.JobRepository", "Shop.Catalog/Data/JobRepository.cs", null, 1, 9),
            Sym(43, SymbolKind.Method, "Shop.Catalog.Data.JobRepository.SaveChangesAsync(CancellationToken)", "Shop.Catalog/Data/JobRepository.cs", 42, 3, 4),
        ];
        List<MapEdge> edges =
        [
            new(10, 12, EdgeKind.Implements), new(42, 40, EdgeKind.Implements), new(43, 41, EdgeKind.Implements),
            new(10, 14, EdgeKind.Dispatches, 1.0, "handles"),
            new(30, 32, EdgeKind.Dispatches, 1.0, "handles"), new(30, 33, EdgeKind.Dispatches, 1.0, "handles"),
            new(2, 31, EdgeKind.Dispatches, 1.0, "dispatch:CreateItem", 4), new(3, 31, EdgeKind.Dispatches, 1.0, "dispatch:DeleteItem", 8),
            new(31, 41, EdgeKind.Calls, 1.0, null, 12),
        ];
        List<EntryPoint> entries =
        [
            new(2, "Shop.Catalog", "aspnet.minimal", "http", "/items", "POST", "Shop.Catalog/CatalogApi.cs", 3),
            new(3, "Shop.Catalog", "aspnet.minimal", "http", "/items/{id}", "DELETE", "Shop.Catalog/CatalogApi.cs", 7),
            new(11, "Shop.Catalog", "dispatch.handler", "handler", "OrderPaidIntegrationEvent", null, "Shop.Catalog/IntegrationEvents/EventHandling/OrderPaidHandler.cs", 3),
            new(21, "Shop.Catalog", "hosted.background", "hosted", "RabbitMQEventBus", null, "Shop.EventBus/RabbitMQEventBus.cs", 10),
        ];
        var files = new Dictionary<string, string> { ["Shop.Catalog/Program.cs"] = "var app = builder.Build();\napp.UseStatusCodePages();\napp.MapCatalogApi();\napp.Run();\n" };
        var f = FrameworkFlowBuilder.Build(new FrameworkInput(symbols, edges, Container("Shop.Catalog", "dotnet-web", "Shop.Catalog", "Shop.EventBus"), entries, [], Sources([], [], files)));

        var root = f.Flow.Root;
        Assert.Equal("入口", root.Label);
        var http = root.Children[0];
        Assert.Equal(["UseStatusCodePages", "终结点"], http.Children.Select(c => c.Label));
        var endpoints = http.Children[1];
        // HTTP 下只有它真分派到的那个 Handler（开放泛型 EnqueueJobHandler 的两条），集成事件 Handler 不在这里
        var viaHttp = Assert.Single(endpoints.Children);
        Assert.Equal(("handlers", 2), (viaHttp.Kind, viaHttp.Count));
        var commit = Assert.Single(viaHttp.Children);
        Assert.Equal(("JobRepository.SaveChangesAsync", "tail"), (commit.Label, commit.Actor));
        Assert.Equal("部分 Handler 自己提交（1 个 Handler 直接调用，共 2 个 Handler）：不是框架统一提交，没调到的 Handler 不落库或在别处提交", commit.Note);

        var hosted = root.Children[1];
        Assert.Equal(("常驻服务 / 任务 / 消息消费", 1), (hosted.Label, hosted.Count));
        var bus = Assert.Single(hosted.Children);
        Assert.Equal("RabbitMQEventBus.StartAsync", bus.Label);
        var consumed = Assert.Single(bus.Children);
        Assert.Equal(("handlers", 1), (consumed.Kind, consumed.Count));
        Assert.StartsWith("消息消费：1 个 Handler 处理集成事件 / 消息", consumed.Note);
        Assert.Equal(3, f.Stages.Single(s => s.Key == "handler").Symbols.Count); // 两个占位 2 + 1

        // 清单：模块按 Handler 所在命名空间（Shop.Catalog 下的 IntegrationEvents / Jobs；按消息分会是 Domain / Catalog）；各条只列分派它那种消息的入口
        Assert.Equal(["IntegrationEvents", "Jobs"], f.Handlers.Select(g => g.Module));
        var jobs = f.Handlers.Single(g => g.Module == "Jobs").Items;
        Assert.Equal([("CreateItem", 2L), ("DeleteItem", 3L)], jobs.Select(i => (i.MessageType!, Assert.Single(i.Entries).SymbolId)));
        Assert.Contains(f.Notes, n => n.StartsWith("1 个 Handler 是消息消费", StringComparison.Ordinal));
    }
}

/// <summary>HTTP 层：/api/arch/framework 对不存在的容器给 404。</summary>
public class FrameworkApiTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Framework_UnknownContainer_Returns404()
    {
        var r = await fx.Client.GetAsync("/api/arch/framework?container=nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
