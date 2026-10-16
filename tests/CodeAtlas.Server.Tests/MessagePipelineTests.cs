using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 消息管道（MessagePipeline）：请求流程里 dispatch:M 前面插入这条消息经过的行为；按约束 / 守卫跳过不生效的；
/// 行为里按消息类型解析的依赖（IValidator&lt;TRequest&gt;）连到 <c>: AbstractValidator&lt;M&gt;</c> 的类（DI 扫描注册，源码里没有直接引用）。
/// 夹具在 FrameworkFlowTests 的 MediatR 夹具上加：验证器、事务守卫、一个事件 Handler。
/// </summary>
public class MessagePipelineTests
{
    private const string Orders = "Shop.Api/Application/Orders/";

    private static (MessagePipeline Pipe, FrameworkInput Input) Build(
        IReadOnlyList<MapSymbol>? moreSymbols = null, IReadOnlyList<MapEdge>? moreEdges = null, IReadOnlyDictionary<string, string>? moreFiles = null)
    {
        var input = FrameworkFlowTests.MediatRFixture();
        List<MapSymbol> extra =
        [
            new(75, SymbolKind.Interface, "Shop.Api.Application.ITransactionalCommand", "Shop.Api/Application/ITransactionalCommand.cs", null, 1, 3),
            new(76, SymbolKind.Class, "Shop.Api.Application.Orders.CreateOrderCommandValidator", Orders + "CreateOrderCommandValidator.cs", 2, 1, 10),
            new(77, SymbolKind.Class, "Shop.Api.Application.Queries.GetOrderQueryValidator", "Shop.Api/Application/Queries/GetOrderQueryValidator.cs", 3, 1, 10),
            new(816, SymbolKind.External, "external:FluentValidation.AbstractValidator<T>", "<external>", null, 1, 1),
            new(817, SymbolKind.External, "external:MediatR.INotificationHandler<TNotification>", "<external>", null, 1, 1),
        ];
        List<MapEdge> extraEdges =
        [
            new(70, 75, EdgeKind.Implements),                       // CreateOrderCommand : ITransactionalCommand
            new(76, 816, EdgeKind.Inherits), new(77, 816, EdgeKind.Inherits),
            new(64, 817, EdgeKind.Implements),                      // 领域事件 Handler：不走行为链
        ];
        var files = new Dictionary<string, string>
        {
            // 行为：Logging 构造注入验证器（第 5 行是 Handle）；Transaction 开头的守卫只放行 ITransactionalCommand；Audit 的约束是 IAuditableCommand（原夹具）
            ["Shop.Api/Behaviors/LoggingBehavior.cs"] = """
                public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
                {
                    private readonly IEnumerable<IValidator<TRequest>> _validators;
                    public LoggingBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;
                    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
                    {
                        var response = await next();
                        return response;
                    }
                }
                """,
            ["Shop.Api/Behaviors/TransactionBehavior.cs"] = """
                public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
                {
                    // if (request is not IIgnoredInComment) return await next();
                    private readonly ShopDbContext _db;
                    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
                    {
                        if (request is not ITransactionalCommand)
                        {
                            var plain = await next();
                            return plain;
                        }
                        var response = await next();
                        await _db.CommitTransactionAsync(null);
                        return response;
                    }
                }
                """,
            [Orders + "CreateOrderCommandValidator.cs"] = "public class CreateOrderCommandValidator\n    : AbstractValidator<CreateOrderCommand>\n{\n}\n",
            ["Shop.Api/Application/Queries/GetOrderQueryValidator.cs"] = "public class GetOrderQueryValidator : AbstractValidator<Shop.Api.Application.Queries.GetOrderQuery>\n{\n}\n",
        };
        if (moreSymbols is not null) extra.AddRange(moreSymbols);
        if (moreEdges is not null) extraEdges.AddRange(moreEdges);
        if (moreFiles is not null) foreach (var (k, v) in moreFiles) files[k] = v;
        var src = input.Sources;
        var sources = new FrameworkSources { Calls = src.Calls, Signature = src.Signature, ReadFile = p => files.GetValueOrDefault(p) ?? src.ReadFile(p) };
        input = input with { Symbols = [.. input.Symbols, .. extra], Edges = [.. input.Edges, .. extraEdges], Sources = sources };
        var fw = FrameworkFlowBuilder.Build(input);
        var pipe = MessagePipeline.From(fw, input.Symbols, input.Edges, sources.ReadFile);
        Assert.NotNull(pipe);
        return (pipe!, input);
    }

    [Fact]
    public void For_KeepsRegistrationOrder_SkipsByConstraintAndGuard_LinksValidatorsByMessageType()
    {
        var (pipe, _) = Build();
        Assert.Equal(["LoggingBehavior", "AuditBehavior", "TransactionBehavior"], pipe.BehaviorNames); // AddOpenBehavior 的书写顺序

        var (steps, skipped) = pipe.For("CreateOrderCommand");
        Assert.Equal(["LoggingBehavior", "TransactionBehavior"], steps.Select(s => s.Name));
        Assert.Equal(["AuditBehavior（CreateOrderCommand 没实现 IAuditableCommand）"], skipped);
        var validator = Assert.Single(steps[0].Components);
        Assert.Equal((76L, "AbstractValidator", "IValidator"), (validator.Type.Id, validator.Base, validator.Dependency));
        Assert.Empty(steps[1].Components);
        Assert.Equal(41L, steps[1].Behavior.SymbolId);

        // 查询没实现 ITransactionalCommand：代码块写法的守卫（{ var r = await next(); return r; }）也认；验证器的类型实参带命名空间也认
        var (qSteps, qSkipped) = pipe.For("GetOrderQuery");
        Assert.Equal(["LoggingBehavior"], qSteps.Select(s => s.Name));
        Assert.Equal(["AuditBehavior（GetOrderQuery 没实现 IAuditableCommand）", "TransactionBehavior（GetOrderQuery 没实现 ITransactionalCommand）"], qSkipped);
        Assert.Equal(77L, Assert.Single(qSteps[0].Components).Type.Id);
    }

    [Fact]
    public void Inject_InsertsStepsBeforeCommandDispatch_NotBeforeEventHandlers()
    {
        var (pipe, _) = Build();
        FlowNodeDto Dispatch(long id, string message, string path) =>
            new(id, "Handle", "x", "dispatch", "app", "dispatch:" + message, 1.0, 1, 1, [new(null, "Save", "y", "call", "app", null, 1.0, 1, 2, [], 0, null)], 0, null, path, 5);
        var root = new FlowNodeDto(50, "OrdersController.Create", "c", "entry", "api", null, 1.0, 1, 0,
            [Dispatch(61, "CreateOrderCommand", Orders + "CreateOrderCommandHandler.cs"), Dispatch(65, "OrderPlacedDomainEvent", Orders + "OrderPlacedDomainEventHandler.cs")], 0, null);
        var flow = new FlowDto(50, "POST /api/orders", "/api/orders", "Shop.Api", [], root, ["AuditBehavior", "LoggingBehavior", "TransactionBehavior"], 4, false, null);

        var after = MessagePipeline.Inject(flow, _ => pipe);

        // 前置（按注册顺序）→ Handler → 后置（洋葱倒序：Transaction 的提交 / 发布，再 Logging 的收尾日志；Audit 被跳过，它的后置也没有）→ 事件 Handler
        Assert.Equal(["LoggingBehavior.Handle", "TransactionBehavior.Handle", "Handle",
                      "ShopDbContext.CommitTransactionAsync", "IntegrationEventService.PublishThroughEventBusAsync", "LoggerExtensions.LogInformation", "Handle"],
            after.Root.Children.Select(c => c.Label));
        var logging = after.Root.Children[0];
        Assert.Equal((21L, "call", "app", 1), (logging.SymbolId, logging.Kind, logging.Actor, logging.Depth));
        Assert.StartsWith("管道 1/2 · AddOpenBehavior", logging.Note);
        var v = Assert.Single(logging.Children, c => c.SymbolId == 76);
        Assert.Equal(("CreateOrderCommandValidator", 2), (v.Label, v.Depth));
        Assert.Contains("AbstractValidator<CreateOrderCommand>", v.Note);
        Assert.Equal(61L, after.Root.Children[2].SymbolId);
        var commit = after.Root.Children[3];
        Assert.Equal((93L, "app", 1), (commit.SymbolId, commit.Actor, commit.Depth));
        Assert.StartsWith("TransactionBehavior 后置", commit.Note);
        Assert.StartsWith("LoggingBehavior 后置", after.Root.Children[5].Note);
        Assert.Equal(65L, after.Root.Children[6].SymbolId); // 事件 Handler 前后都没有插
        Assert.Equal(["LoggingBehavior", "TransactionBehavior"], after.Behaviors); // 按注册顺序、只列生效的
        Assert.Equal("管道里跳过：AuditBehavior（CreateOrderCommand 没实现 IAuditableCommand）", after.Note);
        Assert.Equal(flow.Nodes + 1 + after.Root.Children[0].Children.Count + 1 + after.Root.Children[1].Children.Count + 3, after.Nodes);
    }

    [Fact]
    public void Inject_WithoutAnyDispatch_ClearsBehaviors()
    {
        // 入口直接调服务（不经分派器）：行为一个都不经过，流程头不该再列"管道行为"
        var (pipe, _) = Build();
        var root = new FlowNodeDto(50, "RolesController.List", "c", "entry", "api", null, 1.0, 1, 0,
            [new(null, "RoleService.ListAsync", "s", "call", "app", "impl", 1.0, 1, 1, [], 0, null, "Shop.Api/Roles/RoleService.cs", 3)], 0, null);
        var flow = new FlowDto(50, "GET /api/roles", "/api/roles", "Shop.Api", [], root, ["AuditBehavior", "LoggingBehavior", "TransactionBehavior"], 2, false, null);

        var after = MessagePipeline.Inject(flow, _ => pipe);

        Assert.Empty(after.Behaviors);
        Assert.Same(flow.Root, after.Root);
        Assert.Equal(2, after.Nodes);

        // 只分派到事件 Handler（不走行为链）：同样清空
        var evt = flow with { Root = root with { Children = [new(65, "Handle", "x", "dispatch", "app", "dispatch:OrderPlacedDomainEvent", 1.0, 1, 1, [], 0, null, Orders + "OrderPlacedDomainEventHandler.cs", 5)] } };
        Assert.Empty(MessagePipeline.Inject(evt, _ => pipe).Behaviors);
    }

    [Fact]
    public void Inject_BehaviorWhoseLastNextIsAReturn_HasNoPost()
    {
        // 所有 next() 都是 return await next()（分支里放行）：最后一个之后的调用是"不放行"那条路，不画在 Handler 之后
        var (pipe, _) = Build(moreFiles: new Dictionary<string, string>
        {
            ["Shop.Api/Behaviors/LoggingBehavior.cs"] = """
                public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
                {
                    private readonly IEnumerable<IValidator<TRequest>> _validators;
                    public LoggingBehavior(IEnumerable<IValidator<TRequest>> validators) => _validators = validators;
                    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
                    {
                        if (request is null) { return await next(); }
                        if (_validators is null) return await next();
                        return default!;
                    }
                }
                """,
        });
        var root = new FlowNodeDto(50, "OrdersController.Create", "c", "entry", "api", null, 1.0, 1, 0,
            [new(61, "Handle", "x", "dispatch", "app", "dispatch:CreateOrderCommand", 1.0, 1, 1, [], 0, null, Orders + "CreateOrderCommandHandler.cs", 5)], 0, null);
        var after = MessagePipeline.Inject(new FlowDto(50, "e", null, "Shop.Api", [], root, [], 2, false, null), _ => pipe);
        Assert.Equal(["LoggingBehavior.Handle", "TransactionBehavior.Handle", "Handle", "ShopDbContext.CommitTransactionAsync", "IntegrationEventService.PublishThroughEventBusAsync"],
            after.Root.Children.Select(c => c.Label));
        Assert.True(MessagePipeline.LastNextReturns("{ if (x) return await next(); return await next(ct); }"));
        Assert.False(MessagePipeline.LastNextReturns("{ if (x) return await next(); var r = await next(); return r; }"));
    }

    [Fact]
    public void For_ValidatorThroughGenericBaseClass_MapsTheTypeParameter_AndSkipsPrimaryConstructorArgs()
    {
        // OrderValidatorBase<TOptions, TOrder> : AbstractValidator<TOrder>；子类没有类型体：class X(…) : OrderValidatorBase<ShopOptions, CreateOrderCommand>(options, new LineValidator());
        var (pipe, _) = Build(
            [new(700, SymbolKind.Class, "Shop.Api.Application.Orders.OrderValidatorBase<TOptions, TOrder>", Orders + "OrderValidatorBase.cs", 2, 1, 5),
             new(701, SymbolKind.Class, "Shop.Api.Application.Orders.CreateOrderStrictValidator", Orders + "CreateOrderStrictValidator.cs", 2, 1, 5)],
            [new(700, 816, EdgeKind.Inherits), new(701, 700, EdgeKind.Inherits)],
            new Dictionary<string, string>
            {
                [Orders + "OrderValidatorBase.cs"] = "public abstract class OrderValidatorBase<TOptions, TOrder> : AbstractValidator<TOrder>\n{\n}\n",
                [Orders + "CreateOrderStrictValidator.cs"] = "public class CreateOrderStrictValidator(ShopOptions options)\n    : OrderValidatorBase<ShopOptions, CreateOrderCommand>(options, new LineValidator());\npublic sealed class LineValidator : AbstractValidator<OrderLine> { }\n",
            });

        var (steps, _) = pipe.For("CreateOrderCommand");
        Assert.Equal([(76L, "AbstractValidator", "IValidator"), (701L, "AbstractValidator", "IValidator")],
            steps[0].Components.Select(c => (c.Type.Id, c.Base, c.Dependency)).OrderBy(c => c.Item1));
        Assert.DoesNotContain(pipe.For("ShopOptions").Steps.SelectMany(x => x.Components), c => c.Type.Id == 701); // 不是按第一个实参
    }

    [Fact]
    public void For_FetcherResolvedByReflectionInsideABehaviorService_IsAComponent()
    {
        // LoggingBehavior 注入 IOrderStateProvider；它的实现里 typeof(IStateFetcher<>).MakeGenericType(request.GetType()) → CreateOrderFetcher : IStateFetcher<CreateOrderCommand>
        var (pipe, _) = Build(
            [new(710, SymbolKind.Interface, "Shop.Api.Auditing.IOrderStateProvider", "Shop.Api/Auditing/IOrderStateProvider.cs", null, 1, 3),
             new(711, SymbolKind.Class, "Shop.Api.Auditing.OrderStateProvider", "Shop.Api/Auditing/OrderStateProvider.cs", null, 1, 6),
             new(712, SymbolKind.Interface, "Shop.Api.Auditing.IStateFetcher<TCommand>", "Shop.Api/Auditing/IStateFetcher.cs", null, 1, 3),
             new(713, SymbolKind.Class, "Shop.Api.Application.Orders.CreateOrderFetcher", Orders + "CreateOrderFetcher.cs", 2, 1, 4)],
            [new(711, 710, EdgeKind.Implements), new(713, 712, EdgeKind.Implements)],
            new Dictionary<string, string>
            {
                ["Shop.Api/Behaviors/LoggingBehavior.cs"] = """
                    public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
                    {
                        private readonly IOrderStateProvider _states;
                        public LoggingBehavior(IOrderStateProvider states) => _states = states;
                        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
                        {
                            return await next();
                        }
                    }
                    """,
                ["Shop.Api/Auditing/OrderStateProvider.cs"] = """
                    public class OrderStateProvider(IServiceProvider sp) : IOrderStateProvider
                    {
                        public object? Resolve(object request) =>
                            sp.GetService(typeof(IStateFetcher<>).MakeGenericType(request.GetType()));
                    }
                    """,
                [Orders + "CreateOrderFetcher.cs"] = "public class CreateOrderFetcher : IStateFetcher<CreateOrderCommand>\n{\n}\n",
            });

        var (steps, _) = pipe.For("CreateOrderCommand");
        var fetcher = Assert.Single(steps[0].Components);
        Assert.Equal((713L, "IStateFetcher", "IStateFetcher", "IOrderStateProvider"), (fetcher.Type.Id, fetcher.Base, fetcher.Dependency, fetcher.Via));

        var root = new FlowNodeDto(50, "OrdersController.Create", "c", "entry", "api", null, 1.0, 1, 0,
            [new(61, "Handle", "x", "dispatch", "app", "dispatch:CreateOrderCommand", 1.0, 1, 1, [], 0, null, Orders + "CreateOrderCommandHandler.cs", 5)], 0, null);
        var after = MessagePipeline.Inject(new FlowDto(50, "e", null, "Shop.Api", [], root, [], 2, false, null), _ => pipe);
        var node = Assert.Single(after.Root.Children[0].Children, c => c.SymbolId == 713);
        Assert.Contains("IOrderStateProvider 里按消息类型反射解析 IStateFetcher<…>", node.Note);
    }

    [Fact]
    public void InjectByContainer_OnlyDotnetContainers_ByDispatchTargetPath()
    {
        var (pipe, input) = Build();
        var root = new FlowNodeDto(50, "e", "e", "entry", "api", null, 1.0, 1, 0,
            [new(61, "Handle", "x", "dispatch", "app", "dispatch:CreateOrderCommand", 1.0, 1, 1, [], 0, null, Orders + "CreateOrderCommandHandler.cs", 5)], 0, null);
        var flow = new FlowDto(50, "e", null, "Shop.Api", [], root, [], 2, false, null);
        var asked = new List<string>();
        var after = MessagePipeline.InjectByContainer(flow, [input.Container, new ArchNode("web", ArchNodeKind.Container, "frontend", "web", "web", ArchSource.Discovered, [], ["web"], [])],
            key => { asked.Add(key); return pipe; });
        Assert.Equal(["Shop.Api"], asked);
        Assert.Equal(6, after.Root.Children.Count); // Logging、Transaction 前置 + Handler + 3 行后置（提交、发布、收尾日志）
        // 只有前端容器：不动
        Assert.Same(flow, MessagePipeline.InjectByContainer(flow, [new ArchNode("web", ArchNodeKind.Container, "frontend", "web", "web", ArchSource.Discovered, [], ["web"], [])], _ => pipe));
    }
}
