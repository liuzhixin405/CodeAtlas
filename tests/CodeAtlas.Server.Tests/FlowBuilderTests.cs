using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public class FlowBuilderTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent, int start = 1, int end = 1) => new(id, kind, qname, path, parent, start, end);

    private static readonly List<MapSymbol> Symbols =
    [
        Sym(2, SymbolKind.Class, "Acme.Api.OrdersController", "Acme.Api/Ctl.cs", null, 1, 30),
        Sym(3, SymbolKind.Method, "Acme.Api.OrdersController.Create(Order)", "Acme.Api/Ctl.cs", 2, 5, 12),
        Sym(10, SymbolKind.Class, "Acme.App.CreateOrderHandler", "Acme.App/H.cs", null),
        Sym(11, SymbolKind.Method, "Acme.App.CreateOrderHandler.HandleAsync(CreateOrderCommand)", "Acme.App/H.cs", 10, 3, 20),
        Sym(12, SymbolKind.Interface, "Acme.App.IOrderRepo", "Acme.App/IRepo.cs", null),
        Sym(13, SymbolKind.Method, "Acme.App.IOrderRepo.Save(Order)", "Acme.App/IRepo.cs", 12),
        Sym(20, SymbolKind.Class, "Acme.Infra.OrderRepo", "Acme.Infra/Repo.cs", null),
        Sym(21, SymbolKind.Method, "Acme.Infra.OrderRepo.Save(Order)", "Acme.Infra/Repo.cs", 20, 4, 9),
        Sym(22, SymbolKind.Method, "Acme.Infra.OrderRepo.Audit()", "Acme.Infra/Repo.cs", 20, 10, 12),
        Sym(30, SymbolKind.Method, "Acme.App.Util.Log()", "Acme.App/Util.cs", null),
        Sym(31, SymbolKind.Method, "Acme.App.Util.A()", "Acme.App/Util.cs", null),
        Sym(32, SymbolKind.Method, "Acme.App.Util.B()", "Acme.App/Util.cs", null),
        Sym(33, SymbolKind.Method, "Acme.App.Util.C()", "Acme.App/Util.cs", null),
        Sym(60, SymbolKind.Method, "Billing.Api.InvoiceController.Post()", "billing/Billing.Api/Ctl.cs", null, 5, 8),
        Sym(61, SymbolKind.Method, "Billing.Api.Ledger.Append()", "billing/Billing.Api/Ledger.cs", null),
        Sym(70, SymbolKind.Method, "Acme.Tests.Helper.X()", "Acme.Tests/T.cs", null),
        Sym(80, SymbolKind.Class, "Acme.App.AuditBehavior", "Acme.App/Beh.cs", null),
        Sym(81, SymbolKind.Interface, "Acme.App.IPipelineBehavior", "Acme.App/Beh.cs", null),
        Sym(90, SymbolKind.External, "external:System.Linq.Enumerable.ToList<T>()", "<external>", null),
        Sym(91, SymbolKind.External, "external:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation", "<external>", null),
        Sym(92, SymbolKind.External, "external:Npgsql.NpgsqlConnection.Open", "<external>", null),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(3, 11, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand"),
        new(3, 90, EdgeKind.Calls, 0.5),
        new(3, 91, EdgeKind.Calls, 0.5),
        new(3, 30, EdgeKind.Calls, 1.0, "conduit"),           // 管道细节不走
        new(11, 13, EdgeKind.Calls, 1.0),                      // Handler → 接口方法
        new(11, 21, EdgeKind.Calls, 0.9, "impl"),              // → 唯一实现（派生边，流程要走）
        new(11, 30, EdgeKind.Calls, 1.0), new(11, 30, EdgeKind.Calls, 1.0), // 同一目标两次：去重
        new(11, 70, EdgeKind.Calls, 1.0),                      // 测试代码不走
        new(21, 92, EdgeKind.Calls, 0.5),
        new(21, 22, EdgeKind.Calls, 1.0),
        new(22, 21, EdgeKind.Calls, 1.0),                      // 环：祖先剪掉
        new(13, 21, EdgeKind.Implements),                      // implements 不走
        new(30, 31, EdgeKind.Calls, 1.0), new(30, 32, EdgeKind.Calls, 1.0), new(30, 33, EdgeKind.Calls, 1.0),
        new(60, 61, EdgeKind.Calls, 1.0),
        new(80, 81, EdgeKind.Implements),
    ];

    private static ArchNode Container(string key, params string[] members) => new(key, ArchNodeKind.Container, "dotnet-web", key, key, ArchSource.Discovered, [], members, []);
    private static readonly List<ArchNode> Containers = [Container("Acme.Api", "Acme.Api", "Acme.App", "Acme.Infra"), Container("billing/Billing.Api", "billing/Billing.Api")];

    private static readonly EntryPoint Entry = new(3, "Acme.Api", "aspnet.controller", "http", "/api/v5/Orders", "POST", "Acme.Api/Ctl.cs", 5);
    private static readonly List<EntryPoint> AllEntries = [Entry, new(60, "billing/Billing.Api", "aspnet.controller", "http", "/api/billing/invoices", "POST", "billing/Billing.Api/Ctl.cs", 5)];
    // Handler 的 HandleAsync（3..20 行）里第 15 行对上了 Billing 的路由
    private static readonly List<RouteMatch> Matches = [new("Acme.Api", "Acme.App/H.cs", 15, "/api/billing/invoices", 60, "billing/Billing.Api")];

    [Fact]
    public void Flow_FollowsDispatchAndImplEdges_GroupsExternals_SkipsConduitTestsAndCycles()
    {
        var f = FlowBuilder.Build(Symbols, Edges, Entry, Containers, Matches, AllEntries);
        Assert.Equal(("/api/v5/Orders", "Acme.Api"), (f.EntryLabel, f.ContainerKey));
        var root = f.Root;
        Assert.Equal(("OrdersController.Create", "entry", "Acme.Api|Acme.Api"), (root.Label, root.Kind, root.Actor));
        // 根的孩子：分派到 Handler（排第一），然后一个"2 个框架调用"；conduit 那条不出现
        Assert.Equal(new[] { "dispatch", "external-group" }, root.Children.Select(c => c.Kind));
        var handler = root.Children[0];
        Assert.Equal(("CreateOrderHandler.HandleAsync", "Acme.Api|Acme.App", "dispatch:CreateOrderCommand"), (handler.Label, handler.Actor, handler.Via));
        Assert.Equal("2 个框架调用", root.Children[1].Label);
        Assert.Equal("ext|System", root.Children[1].Actor);

        // Handler 的孩子：impl 派生边到实现（rank 40）在接口方法（30）之前；Log 去重后一个；测试代码没有；最后是跨容器续接
        Assert.Equal(new[] { "OrderRepo.Save", "IOrderRepo.Save", "Log", "HTTP /api/billing/invoices" }, handler.Children.Select(c => c.Label));
        Assert.Equal(("Acme.Api|Acme.Infra", "impl", 0.9), (handler.Children[0].Actor, handler.Children[0].Via, handler.Children[0].Confidence));
        Assert.Equal("Acme.Api|Acme.App", handler.Children[1].Actor); // 接口方法
        Assert.DoesNotContain(handler.Children, c => c.QualifiedName.StartsWith("Acme.Tests", StringComparison.Ordinal));

        // 实现里：Audit → Save 是环，被祖先集合剪掉；Npgsql 外部单独一个节点
        var impl = handler.Children[0];
        Assert.Equal(new[] { "OrderRepo.Audit", "Open" }, impl.Children.Select(c => c.Label));
        Assert.Empty(impl.Children[0].Children);
        Assert.Equal("ext|Npgsql", impl.Children[1].Actor);

        // 续接：hop 节点下接 Billing 的入口，actor 是另一个容器
        var hop = handler.Children[3];
        Assert.Equal(("hop", 0.9), (hop.Kind, hop.Confidence));
        Assert.Contains("Billing", hop.Note);
        var billingEntry = Assert.Single(hop.Children);
        Assert.Equal(("Post", "billing/Billing.Api|billing/Billing.Api"), (billingEntry.Label, billingEntry.Actor));
        Assert.Equal("Append", Assert.Single(billingEntry.Children).Label);

        // actor 顺序：入口项目第一，然后按遍历中首次出现的顺序（深度优先：Npgsql 在实现里先被走到，根的框架调用组在分派子树之后才建），外部最后
        Assert.Equal(new[] { "Acme.Api|Acme.Api", "Acme.Api|Acme.App", "Acme.Api|Acme.Infra", "billing/Billing.Api|billing/Billing.Api", "ext|Npgsql", "ext|System" }, f.Actors.Select(a => a.Key));
        Assert.Equal(new[] { "project", "project", "project", "container", "external", "external" }, f.Actors.Select(a => a.Kind));
        Assert.Equal(new[] { "AuditBehavior" }, f.Behaviors);
        Assert.False(f.Truncated);
    }

    [Fact]
    public void Flow_LimitsFanout_AndFoldsIdenticalSiblings()
    {
        // Log 调了 A、B、C：fanout=2 → A、B + "还有 1 个"
        var logEntry = new EntryPoint(30, "Acme.Api", "x", "http", "/log", "GET", "Acme.App/Util.cs", 1);
        var f = FlowBuilder.Build(Symbols, Edges, logEntry, Containers, [], AllEntries, fanout: 2);
        Assert.Equal(new[] { "A", "B", "还有 1 个" }, f.Root.Children.Select(c => c.Label));
        Assert.Equal(1, f.Root.Children[2].Hidden);

        // 相邻相同子树折叠
        var a = new FlowNodeDto(1, "x", "q", "call", "p", null, 1, 1, 1, [], 0, null);
        var folded = FlowBuilder.Fold([a, a, a with { Count = 1 }, new FlowNodeDto(2, "y", "r", "call", "p", null, 1, 1, 1, [], 0, null), a]);
        Assert.Equal(new[] { 3, 1, 1 }, folded.Select(n => n.Count));
    }

    [Fact]
    public void Flow_DepthAndNodeBudget_MarkTruncated()
    {
        var f = FlowBuilder.Build(Symbols, Edges, Entry, Containers, Matches, AllEntries, maxDepth: 2);
        Assert.Equal("dispatch", f.Root.Children[0].Kind);
        Assert.All(f.Root.Children[0].Children, c => Assert.Empty(c.Children)); // 深度 2 停
        var tiny = FlowBuilder.Build(Symbols, Edges, Entry, Containers, Matches, AllEntries, maxNodes: 3);
        Assert.True(tiny.Truncated);
    }
}
