using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 同档分支谁先进扇出（分量：子树规模、I/O、公共工具降权、hook 名字相关度），new T 换成有逻辑的构造函数，
/// 外部调用按列分组 / 关键类别单独成行，局部函数与顶级语句的显示名。
/// </summary>
public partial class FlowBuilderTests
{
    private static FlowDto AppFlow(List<MapSymbol> symbols, List<MapEdge> edges, long root, int fanout) =>
        FlowBuilder.Build(symbols, edges, AppEntry(root), AppContainers, [], [], fanout: fanout);

    /// <summary>选进扇出的、有符号的孩子（不含"构造 N 个对象"与外部行）。</summary>
    private static string[] PickedCalls(FlowDto f) => f.Root.Children.Where(c => c.Kind == "call" && c.SymbolId is not null).Select(c => c.Label).ToArray();

    [Fact]
    public void SameRank_PrefersTheBiggerSubtree_AndPushesWidelyUsedHelpersDown()
    {
        // 根 Run 调了三个都"能往下走"的方法（同一档 30）：
        //   Args.Value → Lookup（子树 1），另有 20 个方法也调它（公共工具，被 21 处调用）；
        //   Zz.Indexer.IndexAsync → I1 → I2 → I3（子树 3，限定名字母序最后）；
        //   Mid.Helper → H1（子树 1，只有根调它）
        // 分量：IndexAsync = log2(4) = 2；Helper = log2(2) = 1；Value = 1 − log2(21/8) ≈ −0.39。原来按限定名：Args.Value 第一
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, "App.Run()", AppFile, null, 1, 40),
            Sym(3, SymbolKind.Function, "App.Args.Value()", AppFile, null), Sym(4, SymbolKind.Function, "App.Args.Lookup()", AppFile, null),
            Sym(5, SymbolKind.Function, "App.Zz.Indexer.IndexAsync()", AppFile, null),
            Sym(6, SymbolKind.Function, "App.Zz.I1()", AppFile, null), Sym(7, SymbolKind.Function, "App.Zz.I2()", AppFile, null), Sym(8, SymbolKind.Function, "App.Zz.I3()", AppFile, null),
            Sym(9, SymbolKind.Function, "App.Mid.Helper()", AppFile, null), Sym(10, SymbolKind.Function, "App.Mid.H1()", AppFile, null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 1.0, Line: 5), new(2, 5, EdgeKind.Calls, 1.0, Line: 6), new(2, 9, EdgeKind.Calls, 1.0, Line: 7),
            new(3, 4, EdgeKind.Calls), new(5, 6, EdgeKind.Calls), new(6, 7, EdgeKind.Calls), new(7, 8, EdgeKind.Calls), new(9, 10, EdgeKind.Calls),
        ];
        for (var i = 0; i < 20; i++)
        {
            symbols.Add(Sym(100 + i, SymbolKind.Function, $"App.Other.Caller{i}()", AppFile, null));
            edges.Add(new(100 + i, 3, EdgeKind.Calls));
        }
        Assert.Equal(new[] { "IndexAsync" }, PickedCalls(AppFlow(symbols, edges, 2, fanout: 1)));
        // 扇出 2：IndexAsync 与 Helper；公共工具 Args.Value 进"还有 1 个"（显示仍按调用处的行）
        var two = AppFlow(symbols, edges, 2, fanout: 2);
        Assert.Equal(new[] { "IndexAsync", "Helper" }, PickedCalls(two));
        Assert.Equal(("more", 1), (two.Root.Children[^1].Kind, two.Root.Children[^1].Hidden));
    }

    [Fact]
    public void SameRank_IoBoundaryWins_AndADirectIoMethodIsNotPenalizedForManyCallers()
    {
        // Fmt.Format → Pad → Pad2 → Pad3（子树 3，分量 2）；Store.Save → Map（子树 1）且直接调 EF 的 SaveChangesAsync，被 30 处调用。
        // Save 的分量 = log2(2) + 2（I/O）= 3，自己就是 I/O 边界不按被调用次数降权；要是降权就是 3 − log2(30/8) ≈ 1.91，输给 Format
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, "App.Run()", AppFile, null, 1, 40),
            Sym(3, SymbolKind.Function, "App.A.Fmt.Format()", AppFile, null), Sym(4, SymbolKind.Function, "App.A.Fmt.Pad()", AppFile, null),
            Sym(5, SymbolKind.Function, "App.A.Fmt.Pad2()", AppFile, null), Sym(6, SymbolKind.Function, "App.A.Fmt.Pad3()", AppFile, null),
            Sym(7, SymbolKind.Function, "App.Z.Store.Save()", AppFile, null), Sym(8, SymbolKind.Function, "App.Z.Store.Map()", AppFile, null),
            Sym(90, SymbolKind.External, "external:Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(CancellationToken)", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 1.0, Line: 5), new(2, 7, EdgeKind.Calls, 1.0, Line: 6),
            new(3, 4, EdgeKind.Calls), new(4, 5, EdgeKind.Calls), new(5, 6, EdgeKind.Calls),
            new(7, 8, EdgeKind.Calls), new(7, 90, EdgeKind.Calls, 0.5),
        ];
        for (var i = 0; i < 29; i++)
        {
            symbols.Add(Sym(100 + i, SymbolKind.Function, $"App.Other.Caller{i}()", AppFile, null));
            edges.Add(new(100 + i, 7, EdgeKind.Calls));
        }
        Assert.Equal(new[] { "Save" }, PickedCalls(AppFlow(symbols, edges, 2, fanout: 1)));
        Assert.Equal(2.0, FlowBuilder.Weight(3, io: false, callers: 1));
        Assert.Equal(3.0, FlowBuilder.Weight(1, io: true, callers: 30, directIo: true));
        Assert.Equal(1.0 + 2 - Math.Log2(30 / 8.0), FlowBuilder.Weight(1, io: true, callers: 30, directIo: false), 6);
        // 只有 8 个以内的调用方不降权；子树规模封顶 200
        Assert.Equal(FlowBuilder.Weight(5, false, 1), FlowBuilder.Weight(5, false, 8));
        Assert.Equal(FlowBuilder.Weight(200, false, 1), FlowBuilder.Weight(5000, false, 1));
    }

    [Fact]
    public void Hooks_NamedAfterTheComponent_ComeFirst()
    {
        // 组件 OrderTable 调了三个 hook，都只调 useApi（分量一样）：按名字相关度 useOrders（order 全在，1.0）> useOrderExport（0.5）> useServerData（0）。
        // 原来按限定名：web/hooks.ts:useOrderExport 在 useOrders 前（'E' < 's'）
        const string ui = "web/OrderTable.tsx", hooks = "web/hooks.ts";
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, ui + ":OrderTable", ui, null, 1, 40),
            Sym(3, SymbolKind.Function, hooks + ":useOrderExport", hooks, null), Sym(4, SymbolKind.Function, hooks + ":useOrders", hooks, null),
            Sym(5, SymbolKind.Function, hooks + ":useServerData", hooks, null), Sym(6, SymbolKind.Function, "web/api.ts:useApi", "web/api.ts", null),
            Sym(7, SymbolKind.Function, "web/api.ts:createClient", "web/api.ts", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 0.95, Line: 3), new(2, 4, EdgeKind.Calls, 0.95, Line: 4), new(2, 5, EdgeKind.Calls, 0.95, Line: 5),
            new(3, 6, EdgeKind.Calls), new(4, 6, EdgeKind.Calls), new(5, 6, EdgeKind.Calls), new(6, 7, EdgeKind.Calls),
        ];
        var web = new List<ArchNode> { Container("web", "web") };
        var entry = new EntryPoint(2, "web", "x", "page", "/orders", null, ui, 1);
        string[] Pick(int fanout) => FlowBuilder.Build(symbols, edges, entry, web, [], [], fanout: fanout).Root.Children.Where(c => c.SymbolId is not null).Select(c => c.Label).ToArray();
        Assert.Equal(new[] { "useOrders" }, Pick(1));
        Assert.Equal(new[] { "useOrderExport", "useOrders" }, Pick(2)); // 选进来的按调用处的行显示
        Assert.Equal(1.0, FlowBuilder.NameAffinity("OrderTable", "useOrders"));
        Assert.Equal(0.5, FlowBuilder.NameAffinity("OrderTable", "useOrderExport"));
        Assert.Equal(0.0, FlowBuilder.NameAffinity("OrderTable", "useServerData"));
        Assert.Equal(2 / 3.0, FlowBuilder.NameAffinity("V2AccountsTable", "useV2AccountList"), 6); // 数字连在大写词后面：V2 / Account(s) / Table
    }

    [Fact]
    public void Hooks_ThatReadData_BeatActionHooks_WhenNamesDoNotTell()
    {
        // GroupTable 调两个 hook，都走 useApi：useGroupByServerId（名字相关度 1/4，自己调 useQuery 取数据）、useGroupImportTemplate（1/3，只调 useMutation）。
        // 光看名字后者赢；取数据的是组件的主数据，应排前
        const string ui = "web/GroupTable.tsx", hooks = "web/hooks.ts";
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, ui + ":GroupTable", ui, null, 1, 40),
            Sym(3, SymbolKind.Function, hooks + ":useGroupImportTemplate", hooks, null), Sym(4, SymbolKind.Function, hooks + ":useGroupByServerId", hooks, null),
            Sym(6, SymbolKind.Function, "web/api.ts:useApi", "web/api.ts", null),
            Sym(8, SymbolKind.External, "external:@tanstack/react-query.useQuery", "<external>", null),
            Sym(9, SymbolKind.External, "external:@tanstack/react-query.useMutation", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 0.95, Line: 3), new(2, 4, EdgeKind.Calls, 0.95, Line: 4),
            new(3, 6, EdgeKind.Calls), new(4, 6, EdgeKind.Calls), new(3, 9, EdgeKind.Calls, 0.5), new(4, 8, EdgeKind.Calls, 0.5),
        ];
        var web = new List<ArchNode> { Container("web", "web") };
        var entry = new EntryPoint(2, "web", "x", "page", "/groups", null, ui, 1);
        var first = FlowBuilder.Build(symbols, edges, entry, web, [], [], fanout: 1).Root.Children.Where(c => c.SymbolId is not null).Select(c => c.Label).ToArray();
        Assert.Equal(new[] { "useGroupByServerId" }, first);
        Assert.True(FlowBuilder.NameAffinity("GroupTable", "useGroupImportTemplate") > FlowBuilder.NameAffinity("GroupTable", "useGroupByServerId"));
        Assert.True(FlowBuilder.IsDataReadApi("external:@tanstack/react-query.useQuery<TData>(options)"));
        Assert.True(FlowBuilder.IsDataReadApi("external:swr.useSWR"));
        Assert.False(FlowBuilder.IsDataReadApi("external:@tanstack/react-query.useMutation"));
    }

    [Fact]
    public void NewOfATypeWithOverloadedConstructors_ExpandsTheConstructorThatHasLogic()
    {
        // new Order(...)：边指向类型 Order（两个构造函数，解析不到是哪个）。Order() 只 new 一个 List（外部），Order(string) 登记领域事件 Raise。
        // 原来当成"没有构造逻辑的 new"（最低档、没有子节点，扇出满时被折掉）；现在换成 Order(string)，按普通调用排、能展开
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, "App.Run()", AppFile, null, 1, 40),
            Sym(3, SymbolKind.Class, "App.Order", "app/Order.cs", null, 1, 30),
            Sym(4, SymbolKind.Method, "App.Order.Order()", "app/Order.cs", 3, 3, 4),
            Sym(5, SymbolKind.Method, "App.Order.Order(string)", "app/Order.cs", 3, 6, 9),
            Sym(6, SymbolKind.Method, "App.Order.Raise(string)", "app/Order.cs", 3, 10, 12),
            Sym(7, SymbolKind.Function, "App.Store.Add()", AppFile, null), Sym(8, SymbolKind.Function, "App.Store.Flush()", AppFile, null),
            Sym(90, SymbolKind.External, "external:System.Collections.Generic.List<T>", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 1.0, Line: 5), new(2, 7, EdgeKind.Calls, 1.0, Line: 6),
            new(4, 90, EdgeKind.Calls, 0.5), new(5, 6, EdgeKind.Calls), new(7, 8, EdgeKind.Calls),
        ];
        var f = AppFlow(symbols, edges, 2, fanout: 6);
        var order = f.Root.Children.Single(c => c.Label == "Order");
        Assert.Equal((5L, "App.Order.Order(string)", 5), (order.SymbolId ?? 0, order.QualifiedName, order.Line ?? 0));
        Assert.Equal(new[] { "Order.Raise" }, order.Children.Select(c => c.Label));
        // 扇出 1：Order(string) 与 Store.Add 同档（30）、子树都是 1，按限定名 App.Order.Order(string) 在前——原来它是最低档，一定被折掉
        Assert.Equal(new[] { "Order" }, PickedCalls(AppFlow(symbols, edges, 2, fanout: 1)));
    }

    [Fact]
    public void ExternalCalls_OneRowPerPackageColumn_KeyCategoriesOnTheirOwn_ExtraGroupsMergeIntoTheGenericColumn()
    {
        // 原来全部并成一行"6 个框架调用"挂在第一个外部符号（Logger）的列上：JpaRepository.save 藏在 [Logger] 下
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, "App.Run()", AppFile, null, 1, 40),
            Sym(91, SymbolKind.External, "external:Logger.info", "<external>", null),
            Sym(92, SymbolKind.External, "external:JpaRepository.save", "<external>", null),
            Sym(93, SymbolKind.External, "external:Optional.of", "<external>", null),
            Sym(94, SymbolKind.External, "external:List.of", "<external>", null),
            Sym(95, SymbolKind.External, "external:Map.get", "<external>", null),
            Sym(96, SymbolKind.External, "external:Set.of", "<external>", null),
        ];
        List<MapEdge> edges = [.. Enumerable.Range(1, 6).Select(i => new MapEdge(2, 90 + i, EdgeKind.Calls, 0.5, Line: i))];
        var rows = AppFlow(symbols, edges, 2, fanout: 6).Root.Children;
        // 关键类别（数据库）单独一行；其余每列一行，超过 4 组时调用少 / 靠后的并进"外部 / 框架"列；行按第一处调用的位置排
        Assert.Equal(new (string, string, string?)[]
        {
            ("info", "ext|Logger", null), ("JpaRepository.save", "ext|JpaRepository", "数据库"), ("of", "ext|Optional", null), ("of", "ext|List", null),
            ("2 个框架调用", "ext|", null),
        }, rows.Select(r => (r.Label, r.Actor, r.Note)));
        Assert.All(rows, r => Assert.Equal("external-group", r.Kind));
        Assert.Equal("get, of", rows[^1].QualifiedName);
    }

    [Fact]
    public void ASymbolIsFullyExpandedOncePerFlow_LaterOccurrencesAreLeavesPointingUpOrDown()
    {
        // 根 Run 调 A（第 5 行）与 B（第 10 行），两者都调 Shared → Leaf1、Leaf2；A 还直接调 Leaf1（叶子，没有可展开的，不算重复）。
        // 原来 Shared 的子树画两遍（ccs-ui 每个 hook 经 useApi 都把 ApiClient 的构造函数展开一遍）
        List<MapSymbol> symbols =
        [
            Sym(2, SymbolKind.Function, "App.Run()", AppFile, null, 1, 40),
            Sym(3, SymbolKind.Function, "App.A()", AppFile, null), Sym(4, SymbolKind.Function, "App.B()", AppFile, null),
            Sym(5, SymbolKind.Function, "App.Shared()", AppFile, null),
            Sym(6, SymbolKind.Function, "App.Leaf1()", AppFile, null), Sym(7, SymbolKind.Function, "App.Leaf2()", AppFile, null),
            Sym(8, SymbolKind.Function, "App.B2()", AppFile, null), Sym(9, SymbolKind.Function, "App.B3()", AppFile, null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 1.0, Line: 5), new(2, 4, EdgeKind.Calls, 1.0, Line: 10),
            new(3, 5, EdgeKind.Calls, 1.0, Line: 20), new(3, 6, EdgeKind.Calls, 1.0, Line: 21), new(4, 5, EdgeKind.Calls, 1.0, Line: 30),
            new(5, 6, EdgeKind.Calls), new(5, 7, EdgeKind.Calls),
        ];
        // A、B 分量相同，按限定名先展开 A：A 下的 Shared 完整，B 下的是叶子，完整的在它上面
        var f = AppFlow(symbols, edges, 2, fanout: 6);
        var (a, b) = (f.Root.Children[0], f.Root.Children[1]);
        Assert.Equal(new[] { "Leaf1", "Leaf2" }, a.Children.Single(c => c.Label == "Shared").Children.Select(c => c.Label));
        var again = b.Children.Single(c => c.Label == "Shared");
        Assert.Equal((5L, 0, "上面已展开，这里不再重复"), (again.SymbolId ?? 0, again.Children.Count, again.Note));
        Assert.Null(a.Children.Single(c => c.Label == "Leaf1").Note); // 叶子本来就没东西可展开：照常画，不写"已展开"
        Assert.False(f.Truncated);
        // 节点计数与树一致：根、A、B、两个 Shared、A 下的 Leaf1、Shared 下的 Leaf1 / Leaf2
        Assert.Equal(8, f.Nodes);
        Assert.Equal(f.Nodes, Count(f.Root));

        // B 的子树更大（还调 B2 → B3）：先展开 B，B 下的 Shared 完整；显示仍按行，A（第 5 行）在上面，它的 Shared 指向下面
        edges.AddRange([new(4, 8, EdgeKind.Calls, 1.0, Line: 31), new(8, 9, EdgeKind.Calls)]);
        var g = AppFlow(symbols, edges, 2, fanout: 6);
        Assert.Equal(new[] { "A", "B" }, g.Root.Children.Select(c => c.Label));
        var first = g.Root.Children[0].Children.Single(c => c.Label == "Shared");
        Assert.Equal((0, "下面已展开，这里不再重复"), (first.Children.Count, first.Note));
        Assert.Equal(2, g.Root.Children[1].Children.Single(c => c.Label == "Shared").Children.Count);

        static int Count(FlowNodeDto n) => (n.Kind is "more" or "external-group" ? 0 : 1) + n.Children.Sum(Count);
    }

    [Theory]
    [InlineData("external:JpaRepository.save", "数据库")]
    [InlineData("external:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync<TSource>(IQueryable<TSource>, CancellationToken)", "数据库")]
    [InlineData("external:Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader()", "数据库")]
    [InlineData("external:Acme.Data.ShopDbContext.SaveChangesAsync", "数据库")]
    [InlineData("external:System.Net.Http.HttpClient.GetAsync(string)", "HTTP")]
    [InlineData("external:axios.get", "HTTP")]
    [InlineData("external:fetch", "HTTP")]
    [InlineData("external:MassTransit.IPublishEndpoint.Publish<T>(T, CancellationToken)", "消息")]
    [InlineData("external:Amazon.S3.IAmazonS3.PutObjectAsync(PutObjectRequest, CancellationToken)", "云服务")]
    [InlineData("external:System.Diagnostics.Process.Start(ProcessStartInfo)", "外部进程")]
    [InlineData("external:System.IO.File.ReadAllText(string)", "文件")]
    [InlineData("external:System.IO.File.Exists(string)", null)]
    [InlineData("external:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(ILogger, string, object[])", null)]
    [InlineData("external:System.Linq.Enumerable.ToList<TSource>(IEnumerable<TSource>)", null)]
    [InlineData("external:react.useState", null)]
    [InlineData("external:Repository", null)]
    public void ExternalCategory_ByQualifiedNameSegments(string qualifiedName, string? expected) =>
        Assert.Equal(expected, FlowBuilder.ExternalCategory(qualifiedName));

    [Fact]
    public void LocalFunctionsAndTopLevelStatements_ShowTheirOwnNames()
    {
        // 局部函数的限定名是 外层方法(参数).局部(参数)：原来从第一个 '(' 截断，显示成外层方法名 Build；顶级语句 <Main>$ 被剥成 "$"
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Class, "App.Svc", AppFile, null, 1, 100),
            Sym(2, SymbolKind.Method, "App.Svc.Build(IReadOnlyList<int>, int)", AppFile, 1, 1, 40),
            Sym(3, SymbolKind.Function, "App.Svc.Build(IReadOnlyList<int>, int).Reach(Node, Dictionary<long, int>)", AppFile, 2, 10, 20),
            Sym(4, SymbolKind.Function, "App.Svc.Build(IReadOnlyList<int>, int).Reach(Node, Dictionary<long, int>).Walk()", AppFile, 3, 12, 14),
            Sym(10, SymbolKind.Module, "app/Program.cs", "app/Program.cs", null, 1, 30),
            Sym(11, SymbolKind.Function, "Program.<Main>$(string[])", "app/Program.cs", 10, 1, 30),
            Sym(12, SymbolKind.Function, "Program.<Main>$(string[]).Usage()", "app/Program.cs", 11, 25, 28),
            // 分部类的方法挂在命名空间下（类型符号没入库）：类型名从限定名里取
            Sym(20, SymbolKind.Namespace, "App.Flows", "app/Front.cs", null, 1, 50),
            Sym(21, SymbolKind.Method, "App.Flows.FrontFlow.Build(Input)", "app/Front.cs", 20, 3, 9),
        ];
        List<MapEdge> edges = [new(2, 3, EdgeKind.Calls, 1.0, Line: 30), new(3, 4, EdgeKind.Calls, 1.0, Line: 13), new(11, 12, EdgeKind.Calls, 1.0, Line: 3), new(11, 21, EdgeKind.Calls, 1.0, Line: 4)];
        var f = AppFlow(symbols, edges, 2, fanout: 6);
        Assert.Equal(("Svc.Build", "Reach", "Walk"), (f.Root.Label, f.Root.Children[0].Label, f.Root.Children[0].Children[0].Label));
        var main = AppFlow(symbols, edges, 11, fanout: 6);
        Assert.Equal(new[] { "Usage", "FrontFlow.Build" }, main.Root.Children.Select(c => c.Label));
        Assert.Equal("Program.Main", main.Root.Label);
    }
}
