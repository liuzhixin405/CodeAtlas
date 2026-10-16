using System.Text.Json;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>任意符号为根的时序树（SymbolFlow）：入口、非入口方法、不在容器里、类型、不可执行的符号。数据复用 FlowBuilderTests 的 eShop 风格夹具。</summary>
public class SymbolFlowTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent, int start = 1, int end = 1) => new(id, kind, qname, path, parent, start, end);
    private static ArchNode Container(string key, params string[] members) => new(key, ArchNodeKind.Container, "dotnet-web", key, key, ArchSource.Discovered, [], members, []);

    private static readonly List<MapSymbol> Symbols =
    [
        Sym(3, SymbolKind.Method, "Acme.Api.OrdersController.Create(Order)", "Acme.Api/Ctl.cs", null, 5, 12),
        Sym(10, SymbolKind.Class, "Acme.App.CreateOrderHandler", "Acme.App/H.cs", null, 1, 30),
        Sym(11, SymbolKind.Method, "Acme.App.CreateOrderHandler.HandleAsync(CreateOrderCommand)", "Acme.App/H.cs", 10, 3, 20),
        Sym(20, SymbolKind.Class, "Acme.Infra.OrderRepo", "Acme.Infra/Repo.cs", null, 1, 20),
        Sym(21, SymbolKind.Method, "Acme.Infra.OrderRepo.Save(Order)", "Acme.Infra/Repo.cs", 20, 4, 9),
        Sym(22, SymbolKind.Method, "Acme.Infra.OrderRepo.Audit()", "Acme.Infra/Repo.cs", 20, 10, 12),
        Sym(23, SymbolKind.Method, "Acme.Infra.OrderRepo.OrderRepo(Db)", "Acme.Infra/Repo.cs", 20, 2, 3),
        Sym(24, SymbolKind.Method, "Acme.Infra.OrderRepo.OrderRepo()", "Acme.Infra/Repo.cs", 20, 13, 13),
        Sym(25, SymbolKind.Field, "Acme.Infra.OrderRepo._db", "Acme.Infra/Repo.cs", 20, 1, 1),
        Sym(30, SymbolKind.Class, "Acme.App.OrderDto", "Acme.App/Dto.cs", null, 1, 3),
        Sym(31, SymbolKind.Interface, "Acme.App.IOrderRepo", "Acme.App/IRepo.cs", null),
        Sym(40, SymbolKind.Method, "Tools.Seed.Run()", "tools/Seed.cs", null, 1, 9),
        Sym(90, SymbolKind.External, "external:Npgsql.NpgsqlConnection.Open", "<external>", null),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(3, 11, EdgeKind.Dispatches, 1.0, "dispatch:CreateOrderCommand", Line: 8),
        new(11, 21, EdgeKind.Calls, 1.0, Line: 10),
        new(21, 22, EdgeKind.Calls, 1.0, Line: 6),
        new(21, 90, EdgeKind.Calls, 0.5, Line: 7),
        new(23, 90, EdgeKind.Calls, 0.5, Line: 2),
        new(23, 22, EdgeKind.Calls, 1.0, Line: 3),
        new(40, 21, EdgeKind.Calls, 1.0, Line: 3),
    ];

    // Acme.Api 收了 Acme.App 与 Acme.Infra；Acme.Worker 也收了 Acme.Infra，但它的成员目录 "Acme.Infra/Repo.cs" 更长（更近）
    private static readonly List<ArchNode> Containers = [Container("Acme.Api", "Acme.Api", "Acme.App", "Acme.Infra"), Container("Acme.Worker", "Acme.Worker", "Acme.Infra/Repo.cs")];
    private static readonly EntryPoint Entry = new(3, "Acme.Api", "aspnet.controller", "http", "/api/orders", "POST", "Acme.Api/Ctl.cs", 5);

    private static MapSymbol S(long id) => Symbols.Single(s => s.Id == id);

    [Fact]
    public void EntrySymbol_GivesTheSameFlowAsTheEntryFlow()
    {
        var viaSymbol = SymbolFlow.Build(Symbols, Edges, S(3), Containers, [], [Entry]);
        var viaEntry = FlowBuilder.Build(Symbols, Edges, Entry, Containers, [], [Entry]);
        Assert.Equal(JsonSerializer.Serialize(viaEntry), JsonSerializer.Serialize(viaSymbol));
        Assert.Equal(("/api/orders", "/api/orders"), (viaSymbol.EntryLabel, viaSymbol.Route));
    }

    [Fact]
    public void NonEntryMethod_SynthesizesAnEntry_InTheContainerWithTheLongestMatchingMember()
    {
        var f = SymbolFlow.Build(Symbols, Edges, S(21), Containers, [], [Entry]);
        Assert.Equal((21L, "OrderRepo.Save", (string?)null, "Acme.Worker", (string?)null), (f.EntrySymbolId, f.EntryLabel, f.Route, f.ContainerKey, f.Note));
        Assert.Equal(("OrderRepo.Save", "entry", "Acme.Worker|Acme.Infra/Repo.cs"), (f.Root.Label, f.Root.Kind, f.Root.Actor));
        Assert.Equal(new[] { "OrderRepo.Audit", "NpgsqlConnection.Open" }, f.Root.Children.Select(c => c.Label)); // 数据库驱动调用单独一行，标签带类型
        Assert.Equal("Acme.Worker|Acme.Infra/Repo.cs", f.Actors[0].Key); // 根所在的列第一
        Assert.Equal("project", f.Actors[0].Kind);

        Assert.Equal("Acme.Api", SymbolFlow.ContainerOf("Acme.App/H.cs", Containers)?.Key);
        Assert.Equal("Acme.Worker", SymbolFlow.ContainerOf("Acme.Infra/Repo.cs", Containers)?.Key);
        Assert.Null(SymbolFlow.ContainerOf("tools/Seed.cs", Containers));
    }

    [Fact]
    public void NotInAnyContainer_UsesEmptyKey_RepoPseudoContainerSplitsActorsByProject_HopsAreNotDrawn()
    {
        // 不在容器里的文件即使有一条 RouteMatch（按容器键登记，这里用空键）也对不上：hop 判定按 home?.Key ?? entry.ContainerKey 过滤，空键只在没有容器时出现
        List<RouteMatch> matches = [new("Acme.Api", "tools/Seed.cs", 3, "/api/x", 3, "Acme.Api")];
        var bare = SymbolFlow.Build(Symbols, Edges, S(40), Containers, matches, [Entry]);
        Assert.Equal(("", "Run"), (bare.ContainerKey, bare.EntryLabel)); // 没有父类型的方法只显示方法名
        Assert.Equal("unknown", bare.Root.Actor);
        Assert.Equal("unknown", bare.Actors[0].Key);                 // 根列仍然第一（虽然"未归属"是 external 类）
        Assert.DoesNotContain(bare.Root.Children, c => c.Kind == "hop");
        Assert.Equal("OrderRepo.Save", Assert.Single(bare.Root.Children).Label);

        var repo = new ArchNode("*", ArchNodeKind.Container, "repo", "acme", "", ArchSource.Discovered, [], ["tools", "Acme.Infra"], []);
        var withRepo = SymbolFlow.Build(Symbols, Edges, S(40), Containers, matches, [Entry], repoContainer: () => repo);
        Assert.Equal("", withRepo.ContainerKey);
        Assert.Equal(new[] { ("|tools", "tools", "project"), ("|Acme.Infra", "Infra", "project") }, withRepo.Actors.Take(2).Select(a => (a.Key, a.Label, a.Kind)));
        Assert.DoesNotContain(withRepo.Root.Children, c => c.Kind == "hop");
    }

    [Fact]
    public void Type_ExpandsItsConstructorWithTheMostCalls_TypeWithoutCtorCallsGivesANote()
    {
        var f = SymbolFlow.Build(Symbols, Edges, S(20), Containers, [], [Entry]);
        Assert.Equal((23L, "OrderRepo"), (f.EntrySymbolId, f.Root.Label));
        Assert.Equal("OrderRepo 有 2 个构造函数，展开的是调用最多的那个（第 2 行）", f.Note);
        Assert.Equal(new[] { "OrderRepo.Audit", "NpgsqlConnection.Open" }, f.Root.Children.Select(c => c.Label));

        var dto = SymbolFlow.Build(Symbols, Edges, S(30), Containers, [], [Entry]);
        Assert.Equal((30L, "OrderDto", 1, 0), (dto.EntrySymbolId, dto.Root.Label, dto.Nodes, dto.Root.Children.Count));
        Assert.Equal("OrderDto 是类型，没有显式构造函数，没有可展开的调用；在它的方法上打开时序图", dto.Note);
        Assert.Equal("Acme.Api", dto.ContainerKey);
    }

    [Fact]
    public void NonExecutableSymbols_GiveARootOnlyFlowWithANote()
    {
        var iface = SymbolFlow.Build(Symbols, Edges, S(31), Containers, [], [Entry]);
        Assert.Equal(("IOrderRepo 是接口，不是可执行的方法；在方法上打开时序图", 0), (iface.Note, iface.Root.Children.Count));
        var field = SymbolFlow.Build(Symbols, Edges, S(25), Containers, [], [Entry]);
        Assert.Equal("_db 是字段，没有调用可展开", field.Note);
        var ext = SymbolFlow.Build(Symbols, Edges, S(90), Containers, [], [Entry]);
        Assert.Equal("Open 是框架 / 第三方符号，索引里没有它的实现", ext.Note);
        Assert.Single(ext.Actors);
    }
}
