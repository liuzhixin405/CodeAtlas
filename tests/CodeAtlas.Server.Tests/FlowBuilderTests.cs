using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public partial class FlowBuilderTests
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
        // 根的孩子：分派到 Handler（排第一），然后外部调用按列分组各一行（System 的 ToList、Microsoft 的 LogInformation——
        // 原来并成一行"2 个框架调用"挂在第一个外部符号的 [System] 列，日志也算进 System）；conduit 那条不出现
        Assert.Equal(new[] { "dispatch", "external-group", "external-group" }, root.Children.Select(c => c.Kind));
        var handler = root.Children[0];
        Assert.Equal(("CreateOrderHandler.HandleAsync", "Acme.Api|Acme.App", "dispatch:CreateOrderCommand"), (handler.Label, handler.Actor, handler.Via));
        Assert.Equal(("ToList", "ext|System"), (root.Children[1].Label, root.Children[1].Actor));
        Assert.Equal(("LogInformation", "ext|Microsoft"), (root.Children[2].Label, root.Children[2].Actor));

        // Handler 的孩子：接口调用与它唯一的实现（impl 0.9，同一处）合成一步，只留实现；Log 去重后一个；测试代码没有；最后是跨容器续接
        // （夹具的边没有行号：位置全相同时保持 rank 顺序；续接在第 15 行，排在后面）
        Assert.Equal(new[] { "OrderRepo.Save", "Log", "HTTP /api/billing/invoices" }, handler.Children.Select(c => c.Label));
        Assert.Equal(("Acme.Api|Acme.Infra", "impl", 0.9), (handler.Children[0].Actor, handler.Children[0].Via, handler.Children[0].Confidence));
        Assert.DoesNotContain(handler.Children, c => c.QualifiedName.StartsWith("Acme.App.IOrderRepo", StringComparison.Ordinal)); // 接口方法那步合进实现了
        Assert.DoesNotContain(handler.Children, c => c.QualifiedName.StartsWith("Acme.Tests", StringComparison.Ordinal));

        // 实现里：Audit → Save 是环，被祖先集合剪掉；Npgsql 是数据库驱动：关键外部调用单独一行，标签带类型（NpgsqlConnection.Open），Note 写类别
        var impl = handler.Children[0];
        Assert.Equal(new[] { "OrderRepo.Audit", "NpgsqlConnection.Open" }, impl.Children.Select(c => c.Label));
        Assert.Empty(impl.Children[0].Children);
        Assert.Equal(("ext|Npgsql", "数据库"), (impl.Children[1].Actor, impl.Children[1].Note));

        // 续接：hop 节点下接 Billing 的入口，actor 是另一个容器
        var hop = handler.Children[2];
        Assert.Equal(("hop", 0.9), (hop.Kind, hop.Confidence));
        Assert.Contains("Billing", hop.Note);
        var billingEntry = Assert.Single(hop.Children);
        Assert.Equal(("Post", "billing/Billing.Api|billing/Billing.Api"), (billingEntry.Label, billingEntry.Actor));
        Assert.Equal("Append", Assert.Single(billingEntry.Children).Label);

        // actor 顺序：入口项目第一，然后按遍历中首次出现的顺序（深度优先：Npgsql 在实现里先被走到，根的框架调用组在分派子树之后才建），外部最后
        Assert.Equal(new[] { "Acme.Api|Acme.Api", "Acme.Api|Acme.App", "Acme.Api|Acme.Infra", "billing/Billing.Api|billing/Billing.Api", "ext|Npgsql", "ext|System", "ext|Microsoft" }, f.Actors.Select(a => a.Key));
        Assert.Equal(new[] { "project", "project", "project", "container", "external", "external", "external" }, f.Actors.Select(a => a.Kind));
        Assert.Equal(new[] { "AuditBehavior" }, f.Behaviors);
        Assert.False(f.Truncated);
    }

    [Fact]
    public void NodeBudget_IsSharedAcrossBranches_ADeepFirstBranchCannotStarveTheSecond()
    {
        // 入口 1 → A（链 A→A2→…→A40，很深）、B → B1（浅）。总名额 12：深度优先时 A 链吃光名额，B1 出不来
        var symbols = new List<MapSymbol> { Sym(1, SymbolKind.Function, "Entry", "a/E.ts", null) };
        var edges = new List<MapEdge> { new(1, 100, EdgeKind.Calls, 1.0), new(1, 200, EdgeKind.Calls, 1.0), new(200, 201, EdgeKind.Calls, 1.0) };
        for (var i = 0; i <= 40; i++)
        {
            symbols.Add(Sym(100 + i, SymbolKind.Function, $"A{i}", "a/A.ts", null));
            if (i < 40) edges.Add(new(100 + i, 101 + i, EdgeKind.Calls, 1.0));
        }
        symbols.Add(Sym(200, SymbolKind.Function, "B", "a/B.ts", null));
        symbols.Add(Sym(201, SymbolKind.Function, "B1", "a/B.ts", null));
        var containers = new List<ArchNode> { Container("app", "a") };
        var entry = new EntryPoint(1, "app", "x", "http", "/e", "GET", "a/E.ts", 1);

        var f = FlowBuilder.Build(symbols, edges, entry, containers, [], [entry], maxDepth: 50, maxNodes: 12);
        var b = Assert.Single(f.Root.Children, c => c.Label == "B");
        Assert.Equal("B1", Assert.Single(b.Children).Label);  // 浅分支完整展开
        Assert.True(f.Truncated);                             // 深分支被截断并如实标出
        Assert.True(f.Nodes <= 12, $"nodes={f.Nodes}");
    }

    [Fact]
    public void NodeBudget_LeftoverFromShallowSiblings_GoesBackToTheDeepFirstBranch()
    {
        // 入口 → A（链 40 层）+ 五个叶子 L1..L5。名额 30：平均分时 A 只拿 29/6≈4；叶子各用 1 个，剩下的应补回 A
        var symbols = new List<MapSymbol> { Sym(1, SymbolKind.Function, "Entry", "a/E.ts", null) };
        var edges = new List<MapEdge> { new(1, 100, EdgeKind.Calls, 1.0) };
        for (var i = 0; i <= 40; i++)
        {
            symbols.Add(Sym(100 + i, SymbolKind.Function, $"A{i:00}", "a/A.ts", null));
            if (i < 40) edges.Add(new(100 + i, 101 + i, EdgeKind.Calls, 1.0));
        }
        for (var i = 1; i <= 5; i++) { symbols.Add(Sym(300 + i, SymbolKind.Function, $"L{i}", "a/L.ts", null)); edges.Add(new(1, 300 + i, EdgeKind.Calls, 1.0)); }
        var containers = new List<ArchNode> { Container("app", "a") };
        var entry = new EntryPoint(1, "app", "x", "http", "/e", "GET", "a/E.ts", 1);

        var f = FlowBuilder.Build(symbols, edges, entry, containers, [], [entry], maxDepth: 50, fanout: 10, maxNodes: 30);
        int Depth(FlowNodeDto n) => n.Children.Count == 0 ? 1 : 1 + n.Children.Max(Depth);
        var a = Assert.Single(f.Root.Children, c => c.Label == "A00");
        Assert.True(Depth(a) >= 20, $"A 链只展开了 {Depth(a)} 层");  // 1 + 5 个叶子 + A 链 ≈ 30
        Assert.Equal(5, f.Root.Children.Count(c => c.Label.StartsWith('L')));
        Assert.True(f.Nodes <= 30, $"nodes={f.Nodes}");
        Assert.True(f.Truncated);
    }

    [Fact]
    public void ExternalRootFiles_BelongToTheirOwnContainer_NotToARootLevelContainer()
    {
        // 主根是前端（成员路径 ""，项目就在主根），后端是 --project 加进来的外部根（路径 ../backend/…）
        var symbols = new List<MapSymbol>
        {
            Sym(1, SymbolKind.Function, "Page", "src/app/page.tsx", null, 1, 5),
            Sym(2, SymbolKind.Method, "Backend.Api.Ctl.Get()", "../backend/Api/Ctl.cs", null, 1, 5),
        };
        var edges = new List<MapEdge> { new(1, 2, EdgeKind.Calls, 1.0) };
        var containers = new List<ArchNode> { Container("web", ""), Container("../backend/Api", "../backend/Api") };
        var entry = new EntryPoint(1, "web", "frontend.route", "page", "/", null, "src/app/page.tsx", 1);
        var f = FlowBuilder.Build(symbols, edges, entry, containers, [], [entry]);
        var call = Assert.Single(f.Root.Children);
        Assert.Equal("../backend/Api|../backend/Api", call.Actor); // 旧判据下成员 "" 吞掉一切，这里会是 "web|"
        Assert.Equal("container", f.Actors.Single(a => a.Key == call.Actor).Kind);
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

    // 前端：页面默认导出的组件 → JSX 渲染边 → 组件 → hook → 框架调用。路径里的 (main) 曾让短名切成空串、模块切成 "tsx"
    private const string PagePath = "web/src/app/[lang]/(main)/groups/page.tsx";
    private static readonly List<MapSymbol> WebSymbols =
    [
        new(100, SymbolKind.Module, PagePath, PagePath, null, 1, 30),
        new(101, SymbolKind.Function, PagePath + ":GroupsPage", PagePath, 100, 15, 29, "export default"),
        new(102, SymbolKind.Function, "web/src/components/MT5Group/MT5GroupTable.tsx:MT5GroupTable", "web/src/components/MT5Group/MT5GroupTable.tsx", null, 159, 900, "export default"),
        new(103, SymbolKind.Function, "web/src/hooks/MT5/Group/useMT5Group.ts:useMT5GroupByServerId", "web/src/hooks/MT5/Group/useMT5Group.ts", null, 35, 70, "export"),
        new(104, SymbolKind.External, "external:@mui/material.Stack", "<external>", null),
        new(105, SymbolKind.External, "external:get", "<external>", null),
    ];
    private static readonly List<MapEdge> WebEdges =
    [
        new(101, 102, EdgeKind.Calls, 0.95, "render"),
        new(101, 104, EdgeKind.Calls, 0.5, "render"),
        new(102, 103, EdgeKind.Calls, 0.95),
        new(103, 105, EdgeKind.Calls, 0.5),
    ];
    private static readonly List<ArchNode> WebContainers = [new("web", ArchNodeKind.Container, "frontend", "web", "web", ArchSource.Discovered, [], ["web"], [])];

    [Fact]
    public void Flow_FrontendPage_FromDefaultExport_FollowsRenderEdges_WithReadableLabels()
    {
        var entry = new EntryPoint(101, "web", "frontend.route", "page", "/{lang}/groups", null, PagePath, 15);
        var f = FlowBuilder.Build(WebSymbols, WebEdges, entry, WebContainers, [], [entry]);

        Assert.Equal(("GroupsPage", "entry"), (f.Root.Label, f.Root.Kind));
        Assert.Equal(new[] { ("MT5GroupTable", "call", (string?)"render", 0.95), ("Stack", "external-group", null, 1.0) },
            f.Root.Children.Select(c => (c.Label, c.Kind, c.Via, c.Confidence)));
        var table = f.Root.Children[0];
        var hook = Assert.Single(table.Children, c => c.Kind == "call");
        Assert.Equal(("useMT5GroupByServerId", (string?)null, 0.95), (hook.Label, hook.Via, hook.Confidence));
        Assert.Equal("get", Assert.Single(hook.Children).Label); // api.get 是框架调用
        Assert.Equal("@mui/material", f.Actors.Single(a => a.Key == f.Root.Children[1].Actor).Label); // 外部按包名成列
    }

    // 单独打开前端时后端不在索引里：hook 里的接口调用是容器图规则识别的出口，没有 RouteMatch，原来时序图里什么都不画
    private const string HookPath = "web/src/hooks/MT5/Group/useMT5Group.ts";
    private const string ApiNodeKey = "external:http-service:api-api-v5";
    private static readonly ArchNode ApiNode = new(ApiNodeKey, ArchNodeKind.External, "http-service", "API /api/v5", null, ArchSource.Discovered, [], [], []);
    private static Outbound WebOut(int line, string url, string? clientName = "GET 经代理 /api/proxy-gateway", string container = "web") =>
        new(container, "frontend-http", "http", clientName, null, url, HookPath, line, ApiNodeKey);
    // hook 行范围 35..70：53、66 同一个接口（折成一条 ×2）；60 已对上路由（画成 hop，不重复）；80 在范围外；别的容器的出口不算
    private static readonly List<Outbound> WebOutbound =
    [
        WebOut(53, "/api/proxy-gateway/api/v5/mt5/groups"),
        WebOut(60, "/api/proxy-gateway/api/v5/mt5/servers"),
        WebOut(66, "/api/proxy-gateway/api/v5/mt5/groups"),
        WebOut(80, "/api/proxy-gateway/api/v5/mt5/far"),
        WebOut(54, "/api/proxy-gateway/api/v5/mt5/other", container: "admin"),
    ];
    private static readonly List<RouteMatch> WebMatches = [new("web", HookPath, 60, "/api/v5/mt5/servers", 999, "backend")];

    [Fact]
    public void Flow_FrontendHttpCallWithoutRouteMatch_BecomesHttpNodeUnderTheHook()
    {
        var entry = new EntryPoint(101, "web", "frontend.route", "page", "/{lang}/groups", null, PagePath, 15);
        var f = FlowBuilder.Build(WebSymbols, WebEdges, entry, WebContainers, WebMatches, [entry], outbound: WebOutbound, externalNodes: [ApiNode]);

        var hook = Assert.Single(f.Root.Children[0].Children, c => c.Label == "useMT5GroupByServerId");
        // 顺序按出口所在行：http（第 53 行，没对上的）→ hop（第 60 行，对上路由的那条）→ 框架调用组最后；第 60 行没有重复成 http，第 80 行与别的容器的不挂
        Assert.Equal(new[] { ("GET /api/v5/mt5/groups", "http"), ("HTTP /api/v5/mt5/servers", "hop"), ("get", "external-group") },
            hook.Children.Select(c => (c.Label, c.Kind)));
        var http = hook.Children[0];
        Assert.Equal(((long?)null, "/api/proxy-gateway/api/v5/mt5/groups", ApiNodeKey, 0.5, 2, 3, "未对上后端（后端不在索引里，或路由没匹配上）"),
            (http.SymbolId, http.QualifiedName, http.Actor, http.Confidence, http.Count, http.Depth, http.Note));
        Assert.Empty(http.Children);
        var actor = Assert.Single(f.Actors, a => a.Key == ApiNodeKey);
        Assert.Equal(("API /api/v5", "external"), (actor.Label, actor.Kind));
    }

    [Fact]
    public void Flow_HttpNode_NoteSaysRouteMismatch_WhenAnotherDotnetContainerIsIndexed()
    {
        var entry = new EntryPoint(103, "web", "frontend.route", "page", "/x", null, HookPath, 35);
        List<ArchNode> containers = [.. WebContainers, Container("backend", "backend")];
        var f = FlowBuilder.Build(WebSymbols, WebEdges, entry, containers, [], [entry], outbound: [WebOut(53, "/api/proxy-gateway/api/v5/mt5/groups")], externalNodes: [ApiNode]);

        var http = Assert.Single(f.Root.Children, c => c.Kind == "http");
        Assert.Equal(("GET /api/v5/mt5/groups", 1, "未对上后端（路由没匹配上）"), (http.Label, http.Count, http.Note));
    }

    [Fact]
    public void Flow_CSharpHttpClientOutbound_LabelUsesConfigKey_AndGoesToExternalColumnWithoutTarget()
    {
        // Handler.HandleAsync（3..20 行）第 10 行登记了 HttpClient "Billing"，地址来自配置键；没有匹配到外部节点
        List<Outbound> outbound = [new("Acme.Api", "http-client", "http", "Billing", "Billing:Url", null, "Acme.App/H.cs", 10, null)];
        var f = FlowBuilder.Build(Symbols, Edges, Entry, Containers, Matches, AllEntries, outbound: outbound);

        var handler = f.Root.Children[0];
        // 出口在第 10 行、续接在第 15 行：按位置，出口在前
        Assert.Equal(new[] { "OrderRepo.Save", "Log", "HTTP Billing:Url", "HTTP /api/billing/invoices" }, handler.Children.Select(c => c.Label));
        var http = handler.Children[2];
        Assert.Equal(("http", "Billing:Url", "ext|", 0.5, "未对上后端（路由没匹配上）"), (http.Kind, http.QualifiedName, http.Actor, http.Confidence, http.Note));
        Assert.Equal("外部 / 框架", f.Actors.Single(a => a.Key == "ext|").Label);
    }

    [Fact]
    public void Flow_HooksRankBeforeChildComponents_SoFanoutKeepsTheDataPath()
    {
        // 组件里先按限定名排的子组件（components/… < hooks/…）会把 hook 挤进"还有 N 个"；hook 排前面
        List<MapSymbol> symbols = [.. WebSymbols, new(106, SymbolKind.Function, "web/src/components/MT5Group/MT5GroupTable.tsx:ActionButton", "web/src/components/MT5Group/MT5GroupTable.tsx", null, 100, 150)];
        List<MapEdge> edges = [.. WebEdges, new(102, 106, EdgeKind.Calls, 0.9, "render")];
        var entry = new EntryPoint(102, "web", "frontend.route", "page", "/x", null, "web/src/components/MT5Group/MT5GroupTable.tsx", 159);
        var f = FlowBuilder.Build(symbols, edges, entry, WebContainers, [], [entry], fanout: 1);

        Assert.Equal(new[] { "useMT5GroupByServerId", "还有 1 个" }, f.Root.Children.Select(c => c.Label));
    }

    [Fact]
    public void Flow_ModuleFallbackEntry_ShowsFileName_AndChildFunctionName()
    {
        var entry = new EntryPoint(100, "web", "frontend.route", "page", "/{lang}/groups", null, PagePath, 1);
        var f = FlowBuilder.Build(WebSymbols, WebEdges, entry, WebContainers, [], [entry]);

        Assert.Equal("page.tsx", f.Root.Label);
        Assert.Equal(("GroupsPage", "function"), (f.Root.Children[0].Label, f.Root.Children[0].Kind));
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
