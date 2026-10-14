using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 管道视图的聚合规则。数据：Acme.Api 两个控制器 → Acme.App 两个业务模块（Orders 两个消息、Users 两个消息）→ Acme.Infra 仓库；
/// 两个 Behavior（DI 注册顺序与名字顺序相反）；一个测试类也在分发（要被排除）；一条 Handler 嵌套分发；两条绕过管道的直接调用。
/// </summary>
public class PipelineTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null) => new(id, kind, qname, path, parent);

    private static readonly List<MapSymbol> Symbols =
    [
        // 入口：控制器
        Sym(1, SymbolKind.Namespace, "Acme.Api", "Acme.Api/OrdersController.cs"),
        Sym(2, SymbolKind.Class, "Acme.Api.OrdersController", "Acme.Api/OrdersController.cs", 1),
        Sym(3, SymbolKind.Method, "Acme.Api.OrdersController.Create", "Acme.Api/OrdersController.cs", 2),
        Sym(4, SymbolKind.Method, "Acme.Api.OrdersController.List", "Acme.Api/OrdersController.cs", 2),
        Sym(5, SymbolKind.Method, "Acme.Api.OrdersController.Direct", "Acme.Api/OrdersController.cs", 2),
        Sym(6, SymbolKind.Class, "Acme.Api.UsersController", "Acme.Api/UsersController.cs", 1),
        Sym(7, SymbolKind.Method, "Acme.Api.UsersController.Notify", "Acme.Api/UsersController.cs", 6),
        // Orders 模块：命令 + 查询 + 两个 Handler
        Sym(10, SymbolKind.Namespace, "Acme.App.Orders.Commands", "Acme.App/Orders/CreateOrder.cs"),
        Sym(11, SymbolKind.Class, "Acme.App.Orders.Commands.CreateOrderCommand", "Acme.App/Orders/CreateOrder.cs", 10),
        Sym(12, SymbolKind.Namespace, "Acme.App.Orders.Queries", "Acme.App/Orders/ListOrders.cs"),
        Sym(13, SymbolKind.Class, "Acme.App.Orders.Queries.ListOrdersQuery", "Acme.App/Orders/ListOrders.cs", 12),
        Sym(14, SymbolKind.Namespace, "Acme.App.Orders.Handlers", "Acme.App/Orders/Handlers.cs"),
        Sym(15, SymbolKind.Class, "Acme.App.Orders.Handlers.CreateOrderHandler", "Acme.App/Orders/Handlers.cs", 14),
        Sym(16, SymbolKind.Method, "Acme.App.Orders.Handlers.CreateOrderHandler.HandleAsync", "Acme.App/Orders/Handlers.cs", 15),
        Sym(17, SymbolKind.Class, "Acme.App.Orders.Handlers.ListOrdersHandler", "Acme.App/Orders/Handlers.cs", 14),
        Sym(18, SymbolKind.Method, "Acme.App.Orders.Handlers.ListOrdersHandler.HandleAsync", "Acme.App/Orders/Handlers.cs", 17),
        // Users 模块：事件（名字不带 Event 后缀，靠接口判类别）+ 一个只有测试在分发的命令
        Sym(20, SymbolKind.Namespace, "Acme.App.Users.Events", "Acme.App/Users/UserCreated.cs"),
        Sym(21, SymbolKind.Class, "Acme.App.Users.Events.UserCreated", "Acme.App/Users/UserCreated.cs", 20),
        Sym(22, SymbolKind.Namespace, "Acme.App.Users.Handlers", "Acme.App/Users/Handlers.cs"),
        Sym(23, SymbolKind.Class, "Acme.App.Users.Handlers.UserCreatedHandler", "Acme.App/Users/Handlers.cs", 22),
        Sym(24, SymbolKind.Method, "Acme.App.Users.Handlers.UserCreatedHandler.HandleAsync", "Acme.App/Users/Handlers.cs", 23),
        Sym(25, SymbolKind.Namespace, "Acme.App.Users.Commands", "Acme.App/Users/DeleteUser.cs"),
        Sym(26, SymbolKind.Class, "Acme.App.Users.Commands.DeleteUserCommand", "Acme.App/Users/DeleteUser.cs", 25),
        Sym(27, SymbolKind.Class, "Acme.App.Users.Handlers.DeleteUserHandler", "Acme.App/Users/Handlers.cs", 22),
        Sym(28, SymbolKind.Method, "Acme.App.Users.Handlers.DeleteUserHandler.HandleAsync", "Acme.App/Users/Handlers.cs", 27),
        // 抽象与行为
        Sym(30, SymbolKind.Namespace, "Acme.App.Abstractions", "Acme.App/Abstractions.cs"),
        Sym(31, SymbolKind.Interface, "Acme.App.Abstractions.ICommandHandler", "Acme.App/Abstractions.cs", 30),
        Sym(32, SymbolKind.Interface, "Acme.App.Abstractions.IQueryHandler", "Acme.App/Abstractions.cs", 30),
        Sym(33, SymbolKind.Interface, "Acme.App.Abstractions.IEventHandler", "Acme.App/Abstractions.cs", 30),
        Sym(34, SymbolKind.Interface, "Acme.App.Abstractions.IPipelineBehavior<TRequest, TResponse>", "Acme.App/Abstractions.cs", 30),
        Sym(40, SymbolKind.Namespace, "Acme.App.Behaviors", "Acme.App/Behaviors.cs"),
        Sym(41, SymbolKind.Class, "Acme.App.Behaviors.ValidationBehavior<TRequest, TResponse>", "Acme.App/Behaviors.cs", 40), // Roslyn 风格限定名，显示时要去掉泛型参数
        Sym(42, SymbolKind.Class, "Acme.App.Behaviors.AuditBehavior", "Acme.App/Behaviors.cs", 40),
        // 下游
        Sym(50, SymbolKind.Namespace, "Acme.Infra.Repos", "Acme.Infra/OrderRepo.cs"),
        Sym(51, SymbolKind.Class, "Acme.Infra.Repos.OrderRepo", "Acme.Infra/OrderRepo.cs", 50),
        Sym(52, SymbolKind.Method, "Acme.Infra.Repos.OrderRepo.Save", "Acme.Infra/OrderRepo.cs", 51),
        Sym(53, SymbolKind.Method, "Acme.Infra.Repos.OrderRepo.All", "Acme.Infra/OrderRepo.cs", 51),
        // 测试代码里的入口
        Sym(60, SymbolKind.Namespace, "Acme.Tests", "Acme.Tests/OrdersTests.cs"),
        Sym(61, SymbolKind.Class, "Acme.Tests.OrdersTests", "Acme.Tests/OrdersTests.cs", 60),
        Sym(62, SymbolKind.Method, "Acme.Tests.OrdersTests.CreatesOrder", "Acme.Tests/OrdersTests.cs", 61),
        // 测试里的 Behavior 也要排除
        Sym(63, SymbolKind.Class, "Acme.Tests.FakeBehavior", "Acme.Tests/FakeBehavior.cs", 60),
        // 测试里的夹具消息与 Handler：整体排除，不能冒出一条 Acme.Tests 泳道
        Sym(64, SymbolKind.Class, "Acme.Tests.SampleCommand", "Acme.Tests/Fixtures.cs", 60),
        Sym(65, SymbolKind.Class, "Acme.Tests.SampleHandler", "Acme.Tests/Fixtures.cs", 60),
        Sym(99, SymbolKind.External, "external:ToList", "<external>"),
    ];

    private static readonly List<MapEdge> Edges =
    [
        // handles：Handler 类型 → 消息类型
        new(15, 11, EdgeKind.Dispatches, 1.0, "handles"),
        new(17, 13, EdgeKind.Dispatches, 1.0, "handles"),
        new(23, 21, EdgeKind.Dispatches, 1.0, "handles"),
        new(27, 26, EdgeKind.Dispatches, 1.0, "handles"),
        new(65, 64, EdgeKind.Dispatches, 1.0, "handles"), // 测试夹具
        // implements
        new(15, 31, EdgeKind.Implements), new(17, 32, EdgeKind.Implements), new(23, 33, EdgeKind.Implements), new(27, 31, EdgeKind.Implements),
        new(41, 34, EdgeKind.Implements), new(42, 34, EdgeKind.Implements), new(63, 34, EdgeKind.Implements),
        // 入口分发（控制器 → Handler 入口方法）
        new(3, 16, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand"),
        new(4, 18, EdgeKind.Dispatches, 1.0, "dispatch:ListOrdersQuery"),
        new(3, 16, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand"), // 同一方法里分发两次，入口权重 +1
        new(7, 24, EdgeKind.Dispatches, 1.0, "dispatch:UserCreated"),
        // 测试代码里的分发：排除；DeleteUserCommand 只有它分发 → 孤儿消息
        new(62, 16, EdgeKind.Dispatches, 0.4, "dispatch:CreateOrderCommand"),
        new(62, 28, EdgeKind.Dispatches, 0.4, "dispatch:DeleteUserCommand"),
        // Handler 嵌套分发：Orders 的 Handler 里分发 Users 的事件（跨行，画流）；ListOrdersHandler 里分发 CreateOrderCommand（同行，只计数）
        new(16, 24, EdgeKind.Dispatches, 1.0, "dispatch:UserCreated"),
        new(18, 16, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand"),
        // 绕过管道：控制器直接调 Handler 方法（一条同模块、一条跨模块）
        new(5, 18, EdgeKind.Calls, 1.0),
        new(7, 16, EdgeKind.Calls, 1.0),
        // Handler 下游调用
        new(16, 52, EdgeKind.Calls, 1.0),
        new(16, 53, EdgeKind.Calls, 0.9),
        new(18, 53, EdgeKind.Calls, 0.7),
        new(16, 99, EdgeKind.Calls, 0.5),  // external → 框架调用
        new(16, 52, EdgeKind.Calls, 0.3),  // 多候选噪声，不算
        new(18, 16, EdgeKind.Calls, 1.0),  // Handler 调 Handler 的方法：不算下游，也不算绕过（起点是 Handler）
        // 管道细节与 contains 不参与
        new(3, 11, EdgeKind.Calls, 1.0, "conduit"),
        new(2, 3, EdgeKind.Contains),
    ];

    // DI 注册顺序：Validation 在前、Audit 在后（与名字顺序相反，证明顺序来自 DI 而不是名字）
    private static readonly List<MapDi> Di = [new(34, 41, 0), new(34, 42, 1), new(31, 15, 2)];

    [Fact]
    public void AutoRoot_IsCommonPrefixOfMessageNamespaces_RowsAreBusinessModules()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "");
        Assert.Equal("Acme.App", p.Root);
        Assert.False(p.Leaf);
        Assert.Equal(new[] { "Acme.App.Orders", "Acme.App.Users" }, p.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "Orders", "Users" }, p.Rows.Select(r => r.Label));
        Assert.All(p.Rows, r => { Assert.True(r.HasChildren); Assert.Null(r.SymbolId); });
        Assert.Equal(0, p.MessagesOutsideRoot);
    }

    [Fact]
    public void AutoRoot_DescendsIntoRowHoldingAtLeast90Percent()
    {
        // 再给 Orders.Commands 加 18 个命令（都由 CreateOrderHandler 处理）：Orders 20 / 22 = 90.9% → 自动下钻到 Orders
        var symbols = Symbols.ToList();
        var edges = Edges.ToList();
        for (var i = 0; i < 18; i++)
        {
            symbols.Add(Sym(100 + i, SymbolKind.Class, $"Acme.App.Orders.Commands.Extra{i}Command", "Acme.App/Orders/Extra.cs", 10));
            edges.Add(new MapEdge(15, 100 + i, EdgeKind.Dispatches, 1.0, "handles"));
        }
        var p = PipelineBuilder.Build(symbols, edges, Di, root: "");
        Assert.Equal("Acme.App.Orders", p.Root);
        Assert.Equal(new[] { "Acme.App.Orders.Commands", "Acme.App.Orders.Queries" }, p.Rows.Select(r => r.Key));
        Assert.Equal(19, p.Rows[0].Messages.Count);
        Assert.Equal(2, p.MessagesOutsideRoot); // Users 的两个消息在自动 root 之外，页面要提示

        // 17 个就只有 19 / 21 = 90.4%——仍然 ≥ 90%；16 个是 18 / 20 = 90%，也下钻；15 个是 17 / 19 = 89.5%，不下钻
        var fewer = edges.Where(e => e.ToId < 115).ToList();
        Assert.Equal("Acme.App", PipelineBuilder.Build(symbols, fewer, Di, root: "").Root);
    }

    [Fact]
    public void AutoRoot_StopsWhenChildrenAreBalanced_OrAtLeaf()
    {
        // 只保留 Orders 的两个消息：公共前缀 Acme.App.Orders，其下 Commands / Queries 各一半，不再下钻
        var edges = Edges.Where(e => e.Via != "handles" || e.ToId is 11 or 13).ToList();
        var p = PipelineBuilder.Build(Symbols, edges, Di, root: "");
        Assert.Equal("Acme.App.Orders", p.Root);
        Assert.Equal(new[] { "Acme.App.Orders.Commands", "Acme.App.Orders.Queries" }, p.Rows.Select(r => r.Key));

        // 只保留 CreateOrderCommand：公共前缀就是它的命名空间，到了叶子，行就是消息本身
        var one = Edges.Where(e => e.Via != "handles" || e.ToId is 11).ToList();
        var leaf = PipelineBuilder.Build(Symbols, one, Di, root: "");
        Assert.Equal("Acme.App.Orders.Commands", leaf.Root);
        Assert.True(leaf.Leaf);
        Assert.Equal(11, Assert.Single(leaf.Rows).SymbolId);
    }

    [Fact]
    public void Row_Counts_Entries_Messages_Handlers_Nested_Bypass_Downstream()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "");
        var orders = p.Rows.Single(r => r.Key == "Acme.App.Orders");
        var users = p.Rows.Single(r => r.Key == "Acme.App.Users");

        // 入口：OrdersController 分发了 3 次（Create 两次 + List 一次）；UsersController 只分发 Users
        var entry = Assert.Single(orders.Entries.Items);
        Assert.Equal(("OrdersController", "Acme.Api.OrdersController", 2L, 3), (entry.Name, entry.QualifiedName, entry.SymbolId, entry.Weight));
        Assert.Equal(1, orders.Entries.Count);
        Assert.Equal("UsersController", Assert.Single(users.Entries.Items).Name);

        // 消息：Orders 1 命令 + 1 查询；Users 1 事件（按接口判）+ 1 命令，其中命令只有测试在分发 → 孤儿
        Assert.Equal((2, 1, 1, 0, 0), (orders.Messages.Count, orders.Messages.Commands, orders.Messages.Queries, orders.Messages.Events, orders.Messages.Orphans));
        Assert.Equal((2, 1, 0, 1, 1), (users.Messages.Count, users.Messages.Commands, users.Messages.Queries, users.Messages.Events, users.Messages.Orphans));

        // Handler
        Assert.Equal(new[] { "CreateOrderHandler", "ListOrdersHandler" }, orders.Handlers.Items.Select(i => i.Name).OrderBy(n => n));
        Assert.Equal(2, users.Handlers.Count);

        // 嵌套分发：CreateOrderHandler 里分发了 UserCreated、ListOrdersHandler 里分发了 CreateOrderCommand；绕过：Direct → ListOrdersHandler、Notify → CreateOrderHandler
        Assert.Equal(2, orders.Nested);
        Assert.Equal(0, users.Nested);
        Assert.Equal(2, orders.Bypass);
        Assert.Equal(0, users.Bypass);

        // 下游：截到与自动 root（Acme.App，2 段）同样的段数；0.3 的边与指向 Handler 的边不算；external 单独计数
        var down = Assert.Single(orders.Downstream);
        Assert.Equal(("Acme.Infra", "Infra", 3), (down.Key, down.Label, down.Weight));
        Assert.Equal(1, orders.ExternalCalls);
        Assert.Empty(users.Downstream);
    }

    [Fact]
    public void TestCode_IsExcludedFromEntriesMessagesHandlersAndBehaviors_AndCounted()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "");
        Assert.Equal(1, p.TestEntriesSkipped);
        Assert.Equal(1, p.TestMessagesSkipped);
        Assert.DoesNotContain(p.Rows.SelectMany(r => r.Entries.Items), i => i.QualifiedName.StartsWith("Acme.Tests", StringComparison.Ordinal));
        Assert.DoesNotContain(p.Behaviors, b => b.Name == "FakeBehavior");
        // 夹具消息若没排除，公共前缀会退到 Acme、冒出一条 Acme.Tests 泳道
        Assert.Equal("Acme.App", p.Root);
        Assert.Equal("Acme.App", p.DefaultRoot);
        Assert.DoesNotContain(p.Rows, r => r.Key == "Acme.Tests");
    }

    [Fact]
    public void DefaultRoot_IsReportedEvenWhenAnExplicitRootIsRequested()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "Acme.App.Orders");
        Assert.Equal("Acme.App.Orders", p.Root);
        Assert.Equal("Acme.App", p.DefaultRoot);
    }

    [Fact]
    public void Flows_CrossRowNestedAndBypass_Only()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "");
        Assert.Equal(2, p.Flows.Count);
        var nested = Assert.Single(p.Flows, f => f.Kind == "nested");
        Assert.Equal(("Acme.App.Orders", "Acme.App.Users", 1), (nested.From, nested.To, nested.Weight));
        var bypass = Assert.Single(p.Flows, f => f.Kind == "bypass");
        Assert.Equal(("Acme.App.Users", "Acme.App.Orders", 1), (bypass.From, bypass.To, bypass.Weight)); // 同模块的那条绕过不画流
    }

    [Fact]
    public void Behaviors_OrderedByDiRegistration_FallsBackToNameWhenNoDi()
    {
        var p = PipelineBuilder.Build(Symbols, Edges, Di, root: "");
        Assert.True(p.BehaviorsOrdered);
        Assert.Equal(new[] { "ValidationBehavior", "AuditBehavior" }, p.Behaviors.Select(b => b.Name));
        Assert.Equal(new[] { 1, 2 }, p.Behaviors.Select(b => b.Order));
        Assert.Equal(41, p.Behaviors[0].SymbolId);

        var noDi = PipelineBuilder.Build(Symbols, Edges, [], root: "");
        Assert.False(noDi.BehaviorsOrdered);
        Assert.Equal(new[] { "AuditBehavior", "ValidationBehavior" }, noDi.Behaviors.Select(b => b.Name));

        // 只有一个 Behavior 注册了：顺序不可信
        var partial = PipelineBuilder.Build(Symbols, Edges, [new(34, 42, 0)], root: "");
        Assert.False(partial.BehaviorsOrdered);
    }

    [Fact]
    public void ExplicitRoot_SubNamespaces_LeafRows_OutsideCount_UnknownRoot()
    {
        var orders = PipelineBuilder.Build(Symbols, Edges, Di, root: "Acme.App.Orders");
        Assert.Equal(new[] { "Acme.App.Orders.Commands", "Acme.App.Orders.Queries" }, orders.Rows.Select(r => r.Key));
        Assert.Equal(2, orders.MessagesOutsideRoot);
        // 嵌套分发的目标 Users 不在 root 内，没有行可连；Notify 的绕过起点 UsersController 只分发过 root 外的消息，也没有起点行。
        // 剩下：Direct → ListOrdersHandler——OrdersController 在 Commands 与 Queries 两行都是入口，目标 Handler 在 Queries 行，得到 Commands → Queries 的绕过；
        // ListOrdersHandler 分发 CreateOrderCommand 在上一层是同行，在这一层成了 Queries → Commands 的嵌套分发。
        Assert.Equal(2, orders.Flows.Count);
        var bypass = Assert.Single(orders.Flows, f => f.Kind == "bypass");
        Assert.Equal(("Acme.App.Orders.Commands", "Acme.App.Orders.Queries", 1), (bypass.From, bypass.To, bypass.Weight));
        var nested = Assert.Single(orders.Flows, f => f.Kind == "nested");
        Assert.Equal(("Acme.App.Orders.Queries", "Acme.App.Orders.Commands", 1), (nested.From, nested.To, nested.Weight));

        var leaf = PipelineBuilder.Build(Symbols, Edges, Di, root: "Acme.App.Orders.Commands");
        Assert.True(leaf.Leaf);
        var row = Assert.Single(leaf.Rows);
        Assert.Equal(("Acme.App.Orders.Commands.CreateOrderCommand", "CreateOrderCommand", 11L, false), (row.Key, row.Label, row.SymbolId, row.HasChildren));
        Assert.Equal(1, row.Entries.Count);
        Assert.Equal("CreateOrderHandler", Assert.Single(row.Handlers.Items).Name);
        Assert.Equal(1, row.Nested);
        Assert.Equal(1, row.Bypass); // Notify → CreateOrderHandler.HandleAsync
        Assert.Equal(2, Assert.Single(row.Downstream).Weight);

        var nope = PipelineBuilder.Build(Symbols, Edges, Di, root: "Nope");
        Assert.Equal("Nope", nope.Root);
        Assert.Empty(nope.Rows);
        Assert.Empty(nope.Flows);
    }

    [Fact]
    public void NoHandlesEdges_ReturnsEmptyPipeline()
    {
        var edges = Edges.Where(e => e.Kind != EdgeKind.Dispatches).ToList();
        var p = PipelineBuilder.Build(Symbols, edges, Di, root: "");
        Assert.Equal("", p.Root);
        Assert.Empty(p.Rows);
        Assert.Empty(p.Behaviors);

        // 只剩测试夹具的 handles 边：同样算没有管道，但要报出排除了多少
        var onlyTests = Edges.Where(e => e.Via != "handles" || e.ToId == 64).ToList();
        var t = PipelineBuilder.Build(Symbols, onlyTests, Di, root: "");
        Assert.Empty(t.Rows);
        Assert.Equal(1, t.TestMessagesSkipped);
    }

    [Theory]
    [InlineData("Acme.Tests/OrdersTests.cs", "Acme.Tests.OrdersTests", true)]
    [InlineData("src/__tests__/a.ts", "src/__tests__/a.ts:f", true)]
    [InlineData("src/app.spec.ts", "src/app.spec.ts:f", true)]
    [InlineData("Acme.Api/OrdersController.cs", "Acme.Api.OrdersController", false)]
    [InlineData("Acme.Api/Testimony.cs", "Acme.Api.Testimony", false)]
    public void IsTest_MatchesWebviewRule(string path, string qname, bool expected)
    {
        Assert.Equal(expected, PipelineBuilder.IsTest(path, qname));
    }
}
