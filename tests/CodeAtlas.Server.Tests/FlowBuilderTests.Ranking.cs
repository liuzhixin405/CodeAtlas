using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>扇出怎么选（rank：分派、实现、能往下走、叶子、DTO 构造）与选进来的怎么排（调用处的源码位置）。</summary>
public partial class FlowBuilderTests
{
    private const string AppFile = "app/Svc.cs";
    private static readonly List<ArchNode> AppContainers = [Container("app", "app")];

    private static EntryPoint AppEntry(long id) => new(id, "app", "x", "http", "/x", "GET", AppFile, 1);

    // 根 Svc.Run（1..40 行）调了：Leaf（叶子，第 5 行）、Deep（往下调 Inner，第 20 行）、ExtOnly（只调框架，第 12 行）、
    // new OrderDto（类型，没有构造函数，第 3 行）、new Boom(...)（构造函数只调框架 base(...)，第 4 行）、分派到 Handler（第 30 行）
    private static (List<MapSymbol> Symbols, List<MapEdge> Edges) RankFixture()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Class, "App.Svc", AppFile, null, 1, 100),
            Sym(2, SymbolKind.Method, "App.Svc.Run()", AppFile, 1, 1, 40),
            Sym(3, SymbolKind.Method, "App.Svc.ALeaf()", AppFile, 1, 41, 42),
            Sym(4, SymbolKind.Method, "App.Svc.ZDeep()", AppFile, 1, 43, 45),
            Sym(5, SymbolKind.Method, "App.Svc.Inner()", AppFile, 1, 46, 47),
            Sym(6, SymbolKind.Method, "App.Svc.MExtOnly()", AppFile, 1, 48, 49),
            Sym(7, SymbolKind.Class, "App.OrderDto", "app/Dto.cs", null, 1, 5),
            Sym(8, SymbolKind.Class, "App.Boom", "app/Boom.cs", null, 1, 9),
            Sym(9, SymbolKind.Method, "App.Boom.Boom(string)", "app/Boom.cs", 8, 3, 3),
            Sym(10, SymbolKind.Method, "App.Handler.Handle()", "app/Handler.cs", null, 1, 9),
            Sym(90, SymbolKind.External, "external:System.Exception.Exception", "<external>", null),
        ];
        List<MapEdge> edges =
        [
            new(2, 3, EdgeKind.Calls, 1.0, Line: 5),
            new(2, 4, EdgeKind.Calls, 1.0, Line: 20),
            new(2, 6, EdgeKind.Calls, 1.0, Line: 12),
            new(2, 7, EdgeKind.Calls, 1.0, Line: 3),
            new(2, 9, EdgeKind.Calls, 1.0, Line: 4),
            new(2, 10, EdgeKind.Dispatches, 1.0, "dispatch:RunCommand", Line: 30),
            new(4, 5, EdgeKind.Calls, 1.0, Line: 44),
            new(6, 90, EdgeKind.Calls, 0.5, Line: 48),
            new(9, 90, EdgeKind.Calls, 0.5, Line: 3),
        ];
        return (symbols, edges);
    }

    [Fact]
    public void Fanout_SelectsByRank_ButDisplaysSelectedChildrenInSourceOrder()
    {
        var (symbols, edges) = RankFixture();
        // 扇出 3：选分派（100）、能往下走的 ZDeep（30）、只调框架的 MExtOnly（28）；叶子 ALeaf（10）进"还有 1 个"。
        // 两个 DTO 构造（1）有别的调用时合成一行"构造 2 个对象"、不占扇出名额（原来它们也算进"还有 3 个"）
        var f = FlowBuilder.Build(symbols, edges, AppEntry(2), AppContainers, [], [], fanout: 3);
        // 显示按调用处的行：构造@3（第一个 new 的位置）、MExtOnly@12、ZDeep@20、分派@30，"还有 N 个"最后
        Assert.Equal(new[] { ("构造 2 个对象", 3), ("Svc.MExtOnly", 12), ("Svc.ZDeep", 20), ("Handle", 30), ("还有 1 个", 0) },
            f.Root.Children.Select(c => (c.Label, c.Line ?? 0)));
        Assert.Equal(1, f.Root.Children[^1].Hidden);
    }

    [Fact]
    public void Ranking_DeepBeatsLeaf_ExternalOnlyBeatsLeaf_TrivialConstructionIsLast()
    {
        var (symbols, edges) = RankFixture();
        // 不要分派，只看普通调用：扇出 1 时按限定名会选 ALeaf（A < M < Z），"能往下走"加权后选 ZDeep
        // （DTO 构造合成的那一行没有符号 id，不占扇出：看第一个有符号的孩子）
        static string Picked(FlowDto f) => f.Root.Children.First(c => c.Kind == "call" && c.SymbolId is not null).Label;
        var calls = edges.Where(e => e.Kind != EdgeKind.Dispatches).ToList();
        Assert.Equal("Svc.ZDeep", Picked(FlowBuilder.Build(symbols, calls, AppEntry(2), AppContainers, [], [], fanout: 1)));
        // 去掉 ZDeep：只调框架的 MExtOnly 排在叶子 ALeaf 前
        var noDeep = calls.Where(e => e.ToId != 4).ToList();
        Assert.Equal("Svc.MExtOnly", Picked(FlowBuilder.Build(symbols, noDeep, AppEntry(2), AppContainers, [], [], fanout: 1)));
        // 只剩叶子和两个 DTO 构造：叶子先选进来；DTO 不丢，合成一行"构造 2 个对象"出现在第一个 new 的位置（第 3 行）
        var leafAndDto = noDeep.Where(e => e.ToId != 6).ToList();
        Assert.Equal("Svc.ALeaf", Picked(FlowBuilder.Build(symbols, leafAndDto, AppEntry(2), AppContainers, [], [], fanout: 1)));
        var merged = FlowBuilder.Build(symbols, leafAndDto, AppEntry(2), AppContainers, [], [], fanout: 6).Root.Children.Where(c => c.Kind == "call").ToList();
        Assert.Equal(new[] { "构造 2 个对象", "Svc.ALeaf" }, merged.Select(c => c.Label));
        Assert.Equal(((long?)null, "OrderDto, Boom", "OrderDto, Boom", 3), (merged[0].SymbolId, merged[0].QualifiedName, merged[0].Note, merged[0].Line ?? 0));
        // 全是 DTO 构造（没有别的调用）：照常逐个列
        var onlyDto = leafAndDto.Where(e => e.ToId != 3).ToList();
        Assert.Equal(new[] { "OrderDto", "Boom" },
            FlowBuilder.Build(symbols, onlyDto, AppEntry(2), AppContainers, [], [], fanout: 6).Root.Children.Where(c => c.Kind == "call").Select(c => c.Label));
    }

    [Fact]
    public void ConstructingTheDispatchedMessage_IsNotListedSeparately()
    {
        // new CreateOrderCommand(...) 再 Send：分派那步已经代表它
        List<MapSymbol> symbols = [Sym(1, SymbolKind.Method, "App.Ctl.Post()", AppFile, null, 1, 9), Sym(2, SymbolKind.Class, "App.CreateOrderCommand", "app/Cmd.cs", null), Sym(3, SymbolKind.Method, "App.H.Handle()", "app/H.cs", null)];
        List<MapEdge> edges = [new(1, 2, EdgeKind.Calls, 1.0, Line: 3), new(1, 3, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand", Line: 4)];
        var f = FlowBuilder.Build(symbols, edges, AppEntry(1), AppContainers, [], []);
        Assert.Equal(new[] { ("Handle", "dispatch") }, f.Root.Children.Select(c => (c.Label, c.Kind)));
    }

    [Fact]
    public void InterfaceCall_UniqueImplMergesIntoOneStepWithNote_MultipleImplsNestUnderTheInterface()
    {
        List<MapSymbol> symbols =
        [
            Sym(1, SymbolKind.Method, "App.H.Handle()", AppFile, null, 1, 20),
            Sym(10, SymbolKind.Interface, "App.IRepo", "app/IRepo.cs", null), Sym(11, SymbolKind.Method, "App.IRepo.Save()", "app/IRepo.cs", 10),
            Sym(12, SymbolKind.Class, "App.Repo", "app/Repo.cs", null), Sym(13, SymbolKind.Method, "App.Repo.Save()", "app/Repo.cs", 12, 1, 3),
            Sym(20, SymbolKind.Interface, "App.INotifier", "app/INotifier.cs", null), Sym(21, SymbolKind.Method, "App.INotifier.Send()", "app/INotifier.cs", 20),
            Sym(22, SymbolKind.Class, "App.Mail", "app/Mail.cs", null), Sym(23, SymbolKind.Method, "App.Mail.Send()", "app/Mail.cs", 22, 1, 3),
            Sym(24, SymbolKind.Class, "App.Sms", "app/Sms.cs", null), Sym(25, SymbolKind.Method, "App.Sms.Send()", "app/Sms.cs", 24, 1, 3),
            Sym(30, SymbolKind.Method, "App.Db.Write()", "app/Db.cs", null),
        ];
        List<MapEdge> edges =
        [
            new(1, 11, EdgeKind.Calls, 1.0, Line: 5, Pos: 120), new(1, 13, EdgeKind.Calls, 0.9, "impl", Line: 5, Pos: 120),   // 唯一实现
            new(1, 21, EdgeKind.Calls, 1.0, Line: 8, Pos: 200), new(1, 23, EdgeKind.Calls, 0.5, "impl", Line: 8, Pos: 200),   // 两个候选实现
            new(1, 25, EdgeKind.Calls, 0.5, "impl", Line: 8, Pos: 200),
            new(13, 30, EdgeKind.Calls, 1.0, Line: 2, Pos: 10), new(23, 30, EdgeKind.Calls, 1.0, Line: 2, Pos: 10),
        ];
        var f = FlowBuilder.Build(symbols, edges, AppEntry(1), AppContainers, [], []);
        Assert.Equal(new[] { ("Repo.Save", (string?)"impl", (string?)"经接口 IRepo"), ("INotifier.Send", null, null) },
            f.Root.Children.Select(c => (c.Label, c.Via, c.Note)));
        var iface = f.Root.Children[1];
        Assert.Equal(new[] { ("Mail.Send", 0.5), ("Sms.Send", 0.5) }, iface.Children.Select(c => (c.Label, c.Confidence)));
        Assert.All(iface.Children, c => Assert.Equal(("impl", AppFile, 8, (int?)200), (c.Via!, c.Path!, c.Line!.Value, c.Pos)));  // 调用处还是 Handle 里那一行
        Assert.Equal("Write", Assert.Single(iface.Children[0].Children).Label);                                           // 实现照常往下展开
    }

    [Fact]
    public void Nodes_CarryCallSite_PathLinePos_RootAndHopTargetCarryDefinition()
    {
        var f = FlowBuilder.Build(Symbols, Edges.Select(e => e with { Line = 7, Pos = e.FromId == 11 ? 300 : null }).ToList(), Entry, Containers, Matches, AllEntries);
        Assert.Equal(("Acme.Api/Ctl.cs", (int?)5, (int?)null), (f.Root.Path, f.Root.Line, f.Root.Pos));                 // 根：定义处
        var handler = f.Root.Children[0];
        Assert.Equal(("Acme.Api/Ctl.cs", (int?)7, (int?)null), (handler.Path, handler.Line, handler.Pos));              // 分派：调用方文件 + 边的行
        var save = handler.Children.Single(c => c.Label == "OrderRepo.Save");
        Assert.Equal(("Acme.App/H.cs", (int?)7, (int?)300), (save.Path, save.Line, save.Pos));                         // 调用处在 Handler 的文件里
        var hop = handler.Children.Single(c => c.Kind == "hop");
        Assert.Equal(("Acme.App/H.cs", (int?)15, (int?)null), (hop.Path, hop.Line, hop.Pos));                          // hop：对上路由的那一行
        Assert.Equal(("billing/Billing.Api/Ctl.cs", (int?)5), (hop.Children[0].Path, hop.Children[0].Line));           // 对端入口：定义处
        var more = FlowBuilder.Build(Symbols, Edges, new EntryPoint(30, "Acme.Api", "x", "http", "/log", "GET", "Acme.App/Util.cs", 1), Containers, [], AllEntries, fanout: 1)
            .Root.Children.Single(c => c.Kind == "more");
        Assert.Null(more.Path);
    }

    [Fact]
    public void SameLine_OrdersByPos_WhenEverySiblingHasPos()
    {
        List<MapSymbol> symbols = [Sym(1, SymbolKind.Function, "Main", AppFile, null, 1, 5), Sym(2, SymbolKind.Function, "A", AppFile, null), Sym(3, SymbolKind.Function, "B", AppFile, null)];
        // 同一行 B 在前（pos 20）、A 在后（pos 50）；限定名排序会是 A、B
        List<MapEdge> edges = [new(1, 2, EdgeKind.Calls, 1.0, Line: 2, Pos: 50), new(1, 3, EdgeKind.Calls, 1.0, Line: 2, Pos: 20)];
        var f = FlowBuilder.Build(symbols, edges, AppEntry(1), AppContainers, [], []);
        Assert.Equal(new[] { "B", "A" }, f.Root.Children.Select(c => c.Label));
        // 参与者顺序跟着显示顺序走（先序首次出现）
        Assert.Equal(new[] { "app|app" }, f.Actors.Select(a => a.Key));
    }
}
