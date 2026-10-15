using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Tests;

/// <summary>影响范围：内存库（OutlineRepo）上手造符号与边，经 ImpactQuery 取数、ImpactBuilder 成形。</summary>
public class ImpactTests
{
    private static ImpactDto Impact(OutlineRepo r, long id, bool entries = true, bool tests = false) =>
        ImpactQuery.Build(r.Db, id, new ImpactOptions(entries, tests)) ?? throw new InvalidOperationException("符号不存在");

    /// <summary>kind:Label:置信度:各处（L行@pos[ctx]）。</summary>
    private static string Shape(IEnumerable<ImpactCallerDto> callers) =>
        string.Join(" | ", callers.Select(c => $"{c.Kind}:{c.Label}:{c.Confidence:0.##}:" +
                                                string.Join(",", c.Sites.Select(s => $"L{s.Line}" + (s.Pos is { } p ? $"@{p}" : "") + (s.Ctx is null ? "" : $"[{s.Ctx}]")))));

    private static string EntryShape(IEnumerable<ImpactEntryDto> entries) =>
        string.Join(" | ", entries.Select(e => $"{e.ContainerName}:{e.Entry.Kind}:{e.Entry.Route}@{e.Depth}"));

    private static readonly ArchNode Api = new("src/Api", ArchNodeKind.Container, "dotnet-web", "Ordering.API", "src/Api", ArchSource.Discovered, [], ["src/Api"], []);
    private static readonly ArchNode Web = new("web", ArchNodeKind.Container, "frontend", "WebApp", "web", ArchSource.Discovered, [], ["web"], []);

    private static EntryPoint Http(long symbol, string route, string method = "POST", string container = "src/Api") =>
        new(symbol, container, "aspnet.minimal", "http", route, method, "src/App/App.cs", 1);

    private static EntryPoint Handler(long symbol, string message) => new(symbol, "src/Api", "dispatch.handler", "handler", message, null, "src/App/App.cs", 1);

    private static void Entries(OutlineRepo r, IReadOnlyList<EntryPoint> entries, IReadOnlyList<RouteMatch>? matches = null)
    {
        ArchStorage.Replace(r.Db, new ArchModel([Api, Web], [], [], []));
        EntryStorage.Replace(r.Db, entries, matches ?? []);
    }

    // ---------- 四种 Kind ----------

    [Fact]
    public void Kinds_CallImplDispatchNew()
    {
        using var r = new OutlineRepo();
        var iface = r.Type("IRepo", SymbolKind.Interface);
        var ifaceAdd = r.Method(iface, "Add");
        var repo = r.Type("Repo");
        var add = r.Method(repo, "Add");
        var direct = r.Method(r.Type("Direct"), "Run");
        var viaIface = r.Method(r.Type("ViaIface"), "Run");
        var sender = r.Method(r.Type("Sender"), "Send");
        r.Edge(direct, add, line: 10, pos: 100);
        r.Edge(viaIface, ifaceAdd, line: 20, pos: 200);
        r.Edge(viaIface, add, line: 20, pos: 200, conf: 0.9, via: "impl");
        r.Edge(sender, add, line: 30, pos: 300, via: "dispatch:AddCommand", kind: EdgeKind.Dispatches);
        r.Edge(sender, add, line: 31, via: "handles", kind: EdgeKind.Dispatches); // handles 不算
        r.Edge(sender, add, line: 32, via: "conduit");                            // conduit 不算

        Assert.Equal("call:Direct.Run:1:L10@100 | dispatch:Sender.Send:1:L30@300 | impl:ViaIface.Run:0.9:L20@200", Shape(Impact(r, add).Callers));

        // 类型：new 它的调用方；构造函数：指向所属类型的 new 也算
        var order = r.Type("Order");
        var ctor = r.Method(order, "Order");
        var maker = r.Method(r.Type("Maker"), "Make");
        r.Edge(maker, order, line: 40, pos: 400, ctx: "loop");
        Assert.Equal("new:Maker.Make:1:L40@400[loop]", Shape(Impact(r, order).Callers));
        Assert.Equal("new:Maker.Make:1:L40@400[loop]", Shape(Impact(r, ctor).Callers));
    }

    [Fact]
    public void Impl_CallerOfInterfaceMethodWithoutDerivedEdge_IsAddedCappedAtHalf_AndNotDuplicatedWhenDerivedExists()
    {
        using var r = new OutlineRepo();
        var iface = r.Type("IStore", SymbolKind.Interface);
        var ifaceSave = r.Method(iface, "Save", parameters: "int");
        var sql = r.Type("SqlStore");
        var save = r.Method(sql, "Save", parameters: "int");
        r.Edge(save, ifaceSave, line: 1, kind: EdgeKind.Implements);              // Roslyn 方法级：实现 → 接口成员
        var bound = r.Method(r.Type("Bound"), "Run");                              // Roslyn 绑到接口、没有派生边（>20 个实现）
        var derived = r.Method(r.Type("Derived"), "Run");                          // 有派生边：不重复
        r.Edge(bound, ifaceSave, line: 10, pos: 100);
        r.Edge(derived, ifaceSave, line: 20, pos: 200);
        r.Edge(derived, save, line: 20, pos: 200, conf: 0.9, via: "impl");

        // 基类虚方法：override → 被重写的方法（inherits），调基类方法的也算
        var baseType = r.Type("StoreBase");
        var baseFlush = r.Method(baseType, "Flush");
        var flush = r.Method(sql, "Flush");
        r.Edge(flush, baseFlush, line: 1, kind: EdgeKind.Inherits);
        r.Edge(bound, baseFlush, line: 11, pos: 110, conf: 0.9);

        Assert.Equal("impl:Derived.Run:0.9:L20@200 | impl:Bound.Run:0.5:L10@100", Shape(Impact(r, save).Callers));
        Assert.Equal("impl:Bound.Run:0.5:L11@110", Shape(Impact(r, flush).Callers));
    }

    [Fact]
    public void Impl_TreeSitterSymbols_MatchInterfaceMethodBySameNameAndParameterCount()
    {
        using var r = new OutlineRepo();
        var iface = r.Type("IStore", SymbolKind.Interface, source: "treesitter");
        var save1 = r.Method(iface, "Save", parameters: "int id", source: "treesitter");
        var save2 = r.Method(iface, "Save", parameters: "int id, Dictionary<string, int> bag", source: "treesitter"); // 重载：两个参数
        var sql = r.Type("SqlStore", source: "treesitter");
        r.Edge(sql, iface, line: 1, kind: EdgeKind.Implements);                     // 只有类型级边
        var save = r.Method(sql, "Save", parameters: "int id", source: "treesitter");
        var one = r.Method(r.Type("One"), "Run");
        var two = r.Method(r.Type("Two"), "Run");
        r.Edge(one, save1, line: 10);
        r.Edge(two, save2, line: 20);

        Assert.Equal("impl:One.Run:0.5:L10", Shape(Impact(r, save).Callers));
        Assert.Equal(1, ImpactQuery.ParamCount(new SymbolRecord(0, 0, SymbolKind.Method, "Save", "x", "void Save(int id)", 0, 0, 0, 0, null, null, IndexSource.TreeSitter)));
        Assert.Equal(2, ImpactQuery.ParamCount(new SymbolRecord(0, 0, SymbolKind.Method, "Save", "App.S.Save(Dictionary<string, int>, Func<int, (int, int)>)", null, 0, 0, 0, 0, null, null, IndexSource.Roslyn)));
        Assert.Equal(0, ImpactQuery.ParamCount(new SymbolRecord(0, 0, SymbolKind.Method, "Save", "App.S.Save()", null, 0, 0, 0, 0, null, null, IndexSource.Roslyn)));
        Assert.Null(ImpactQuery.ParamCount(new SymbolRecord(0, 0, SymbolKind.Method, "Save", "src/a.ts:S.save", null, 0, 0, 0, 0, null, null, IndexSource.TreeSitter)));
    }

    // ---------- 合并、测试代码、排序、Expandable、截断 ----------

    [Fact]
    public void SameCaller_ManySites_MergeIntoOneSortedByPos_MaxConfidence_DifferentKindsStaySeparate()
    {
        using var r = new OutlineRepo();
        var svc = r.Type("Svc");
        var target = r.Method(svc, "Find");
        var caller = r.Method(r.Type("Host"), "Run");
        r.Edge(caller, target, line: 12, pos: 300, conf: 0.7);
        r.Edge(caller, target, line: 10, pos: 100, ctx: "try");
        r.Edge(caller, target, line: 11, pos: 200, conf: 0.9, ctx: "loop");
        r.Edge(caller, target, line: 13, pos: 400, conf: 0.9, via: "impl"); // 另一种 Kind：单独一项

        var callers = Impact(r, target).Callers;
        Assert.Equal("call:Host.Run:1:L10@100[try],L11@200[loop],L12@300 | impl:Host.Run:0.9:L13@400", Shape(callers));
        Assert.All(callers, c => Assert.Equal(caller, c.Caller.Id));
    }

    [Fact]
    public void Sites_WithoutPos_SortByLine()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var caller = r.Method(r.Type("Host"), "Run");
        r.Edge(caller, target, line: 30);
        r.Edge(caller, target, line: 20, pos: 900);
        r.Edge(caller, target, line: 20);
        Assert.Equal("call:Host.Run:1:L20@900,L20,L30", Shape(Impact(r, target).Callers));
    }

    [Fact]
    public void TestCallers_OnlyCounted_UnlessTestsTrue()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var prod = r.Method(r.Type("Host"), "Run");
        var t1 = r.Method(r.Type("SvcTests", test: true), "Finds", test: true);
        var t2 = r.Method(r.Type("OtherTests", test: true), "AlsoFinds", test: true);
        r.Edge(prod, target, line: 10);
        r.Edge(t1, target, line: 20);
        r.Edge(t1, target, line: 21);
        r.Edge(t2, target, line: 30);

        var off = Impact(r, target);
        Assert.Equal("call:Host.Run:1:L10", Shape(off.Callers));
        Assert.Equal(2, off.TestCallers);

        var on = Impact(r, target, tests: true);
        Assert.Equal("call:Host.Run:1:L10 | call:OtherTests.AlsoFinds:1:L30 | call:SvcTests.Finds:1:L20,L21", Shape(on.Callers));
        Assert.Equal(2, on.TestCallers);
    }

    [Fact]
    public void Sort_EntryCallersFirst_ThenConfidenceDesc_ThenLabel_AndAsEntryCarriesContainerName()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var api = r.Method(r.Type("OrdersApi"), "Create");
        var b = r.Method(r.Type("B"), "Run");
        var a = r.Method(r.Type("A"), "Run");
        var low = r.Method(r.Type("Low"), "Run");
        r.Edge(api, target, line: 10, conf: 0.7);
        r.Edge(b, target, line: 20);
        r.Edge(a, target, line: 30);
        r.Edge(low, target, line: 40, conf: 0.9);
        Entries(r, [Http(api, "/api/orders")]);

        var callers = Impact(r, target).Callers;
        Assert.Equal(["OrdersApi.Create", "A.Run", "B.Run", "Low.Run"], callers.Select(c => c.Label));
        var entry = Assert.Single(callers[0].AsEntry);
        Assert.Equal("Ordering.API", entry.ContainerName);
        Assert.Equal("src/Api", entry.ContainerKey);
        Assert.Equal(1, entry.Depth);
        Assert.Equal(("http", "POST", "/api/orders", "Create"), (entry.Entry.Kind, entry.Entry.HttpMethod, entry.Entry.Route, entry.Entry.Name));
        Assert.All(callers.Skip(1), c => Assert.Empty(c.AsEntry));
    }

    [Fact]
    public void Expandable_OnlyWhenCallerHasQualifyingCallers()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var up = r.Method(r.Type("Up"), "Run");         // 自己还有调用方
        var testOnly = r.Method(r.Type("TestOnly"), "Run"); // 只被测试调
        var weak = r.Method(r.Type("Weak"), "Run");     // 只有 0.3 的多候选入边
        var top = r.Method(r.Type("Top"), "Run");
        var test = r.Method(r.Type("UpTests", test: true), "Works", test: true);
        foreach (var c in new[] { up, testOnly, weak }) r.Edge(c, target, line: 10);
        r.Edge(top, up, line: 20);
        r.Edge(test, testOnly, line: 30);
        r.Edge(top, weak, line: 40, conf: 0.3);

        var callers = Impact(r, target).Callers.ToDictionary(c => c.Label, c => c.Expandable);
        Assert.Equal(new Dictionary<string, bool> { ["TestOnly.Run"] = false, ["Up.Run"] = true, ["Weak.Run"] = false }, callers);
        Assert.True(Impact(r, target, tests: true).Callers.Single(c => c.Label == "TestOnly.Run").Expandable);
    }

    [Fact]
    public void Callers_TruncatedAt300()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var host = r.Type("Host");
        for (var i = 0; i < ImpactBuilder.MaxCallers + 5; i++) r.Edge(r.Method(host, $"M{i:000}"), target, line: 10);

        var impact = Impact(r, target, entries: false);
        Assert.True(impact.Truncated);
        Assert.Equal(ImpactBuilder.MaxCallers, impact.Callers.Count);
        Assert.Equal("Host.M000", impact.Callers[0].Label);
        Assert.Equal("Host.M299", impact.Callers[^1].Label);
        Assert.Null(impact.Entries);
        Assert.False(impact.EntriesTruncated);
    }

    [Fact]
    public void RecursiveCall_IsNotACaller_AndUnknownSymbolIsNull()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Walk");
        r.Edge(target, target, line: 10);
        Assert.Empty(Impact(r, target).Callers);
        Assert.Null(ImpactQuery.Build(r.Db, 987654, new ImpactOptions()));
    }

    // ---------- 入口汇总 ----------

    [Fact]
    public void Entries_MinDepth_Dedup_SelfIsDepthZero_ThroughDispatchAndNew()
    {
        using var r = new OutlineRepo();
        var item = r.Method(r.Type("Order"), "AddItem");
        var handle = r.Method(r.Type("CreateOrderHandler"), "Handle");
        var draft = r.Method(r.Type("DraftHandler"), "Handle");
        var api = r.Method(r.Type("OrdersApi"), "Create");
        var builder = r.Type("OrderBuilder");
        var job = r.Method(r.Type("Nightly"), "Execute");
        r.Edge(handle, item, line: 10);
        r.Edge(draft, item, line: 20);
        r.Edge(api, handle, line: 30, via: "dispatch:CreateOrder", kind: EdgeKind.Dispatches); // 深度 2
        r.Edge(api, draft, line: 31, via: "dispatch:Draft", kind: EdgeKind.Dispatches);        // 菱形：api 只算一次
        r.Edge(builder, item, line: 40);   // 类型（字段初始化器）调用：继续往上走 new 它的
        r.Edge(job, builder, line: 50);    // new OrderBuilder() → 深度 2
        Entries(r, [Handler(handle, "CreateOrder"), Http(api, "/api/orders"), Http(item, "/internal/items", "PUT"), new EntryPoint(job, "src/Api", "quartz.job", "job", "Nightly", null, "src/App/App.cs", 1)]);

        var e = Impact(r, item).Entries!;
        Assert.Equal("Ordering.API:http:/internal/items@0 | Ordering.API:handler:CreateOrder@1 | Ordering.API:http:/api/orders@2 | Ordering.API:job:Nightly@2", EntryShape(e));
    }

    [Fact]
    public void Entries_LowConfidenceEdgesAreNotWalked_ButStillListedAsCallers()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var guess = r.Method(r.Type("Guess"), "Run");
        var api = r.Method(r.Type("OrdersApi"), "Create");
        r.Edge(guess, target, line: 10, conf: 0.45); // ≥ 0.4 列出，< 0.5 不往上走
        r.Edge(api, guess, line: 20);
        r.Edge(api, target, line: 30, conf: 0.3);    // 多候选：既不列也不走
        Entries(r, [Http(api, "/api/orders")]);

        var impact = Impact(r, target);
        Assert.Equal("call:Guess.Run:0.45:L10", Shape(impact.Callers));
        Assert.Empty(impact.Entries!);
        Assert.False(impact.EntriesTruncated);
    }

    [Fact]
    public void Entries_TreeSitterDispatchAt04_IsWalked_UnlikeLowConfidenceCalls()
    {
        // 只用 tree-sitter 时分派边固定 0.4（按 new T 推出 Handler）：eShop 实测不走它就从 Handler 到不了 POST /api/orders
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Order"), "AddItem");
        var handler = r.Method(r.Type("CreateOrderHandler"), "Handle");
        var api = r.Method(r.Type("OrdersApi"), "Create");
        r.Edge(handler, target, line: 10, conf: 0.9);
        r.Edge(api, handler, line: 20, conf: 0.4, via: "dispatch:CreateOrder", kind: EdgeKind.Dispatches);
        Entries(r, [Http(api, "/api/orders")]);

        var impact = Impact(r, target);
        Assert.Equal("Ordering.API:http:/api/orders@2", EntryShape(impact.Entries!));
    }

    [Fact]
    public void Entries_CyclesTerminate_TestCodeNotWalked()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Svc"), "Find");
        var a = r.Method(r.Type("A"), "Run");
        var b = r.Method(r.Type("B"), "Run");
        var test = r.Method(r.Type("FindTests", test: true), "Works", test: true);
        r.Edge(a, target, line: 10);
        r.Edge(b, a, line: 20);
        r.Edge(a, b, line: 21);       // A ↔ B
        r.Edge(target, a, line: 22);  // 回到起点
        r.Edge(test, a, line: 30);
        Entries(r, [Http(b, "/b"), new EntryPoint(test, "src/Api", "x", "http", "/test", "GET", "tests/x.cs", 1)]);

        var impact = Impact(r, target);
        Assert.Equal("Ordering.API:http:/b@2", EntryShape(impact.Entries!));
        Assert.False(impact.EntriesTruncated);
    }

    [Fact]
    public void Entries_DepthLimit_SetsEntriesTruncated()
    {
        using var r = new OutlineRepo();
        var host = r.Type("Chain");
        var chain = Enumerable.Range(0, ImpactBuilder.MaxDepth + 2).Select(i => r.Method(host, $"M{i:00}")).ToList();
        for (var i = 1; i < chain.Count; i++) r.Edge(chain[i], chain[i - 1], line: 10 + i);
        Entries(r, [Http(chain[ImpactBuilder.MaxDepth], "/deep"), Http(chain[ImpactBuilder.MaxDepth + 1], "/too-deep")]);

        var impact = Impact(r, chain[0]);
        Assert.Equal("Ordering.API:http:/deep@15", EntryShape(impact.Entries!));
        Assert.True(impact.EntriesTruncated);
    }

    [Fact]
    public void Reach_NodeLimit_AndCycle()
    {
        // 纯函数：1 ← 2 ← 3 ← … ，再加一个 3 → 1 的环
        IEnumerable<long> Up(IReadOnlyCollection<long> f) => f.Select(id => id < 10 ? id + 1 : 1L);
        var (all, cutAll) = ImpactBuilder.Reach(1, Up);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), all.Keys.Order());
        Assert.Equal(9, all[10]);
        Assert.False(cutAll);

        var (some, cut) = ImpactBuilder.Reach(1, Up, maxNodes: 4);
        Assert.Equal([1L, 2, 3, 4], some.Keys.Order());
        Assert.True(cut);
    }

    [Fact]
    public void Entries_RouteMatchHop_WalksFromBackendEntryToFrontendCaller()
    {
        using var r = new OutlineRepo();
        var target = r.Method(r.Type("Order"), "AddItem");
        var api = r.Method(r.Type("OrdersApi"), "Create");
        r.Edge(api, target, line: 10);
        // 前端：web/src/api.ts 里的 createOrder() 发 fetch（route_matches 记的是那一行），页面组件调了 createOrder
        var webFile = r.File("web/src/api.ts");
        var client = r.Symbol(SymbolKind.Function, "createOrder", "web/src/api.ts:createOrder", file: webFile, source: "treesitter");
        var page = r.Symbol(SymbolKind.Function, "CheckoutPage", "web/src/api.ts:CheckoutPage", file: webFile, source: "treesitter");
        r.Edge(page, client, line: r.StartLine(page) + 2);
        Entries(r,
            [Http(api, "/api/orders"), new EntryPoint(page, "web", "next.page", "page", "/checkout", null, "web/src/api.ts", r.StartLine(page))],
            [new RouteMatch("web", "web/src/api.ts", r.StartLine(client) + 3, "/api/orders", api, "src/Api")]);

        var impact = Impact(r, target);
        Assert.Equal("Ordering.API:http:/api/orders@1 | WebApp:page:/checkout@3", EntryShape(impact.Entries!));
        // 直接调用方只有后端的：hop 接在入口 OrdersApi.Create 上，不是 AddItem 的直接调用方
        Assert.Equal("call:OrdersApi.Create:1:L10", Shape(impact.Callers));

        // 在后端入口本身上看：前端发起 fetch 的 createOrder 是它的跨服务直接调用方（Kind=hop，置信度按路由 0.9，行 = fetch 那一行）
        var atApi = Impact(r, api);
        var hop = Assert.Single(atApi.Callers);
        Assert.Equal(("hop", "createOrder", 0.9, r.StartLine(client) + 3), (hop.Kind, hop.Label, hop.Confidence, Assert.Single(hop.Sites).Line));
        Assert.True(hop.Expandable); // CheckoutPage 调了它
    }
}
