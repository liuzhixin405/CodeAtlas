using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public class DsmTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null) => new(id, kind, qname, path, parent);

    private static readonly List<MapSymbol> Symbols =
    [
        Sym(1, SymbolKind.Namespace, "Acme.Api", "Acme.Api/Ctl.cs"),
        Sym(2, SymbolKind.Class, "Acme.Api.Ctl", "Acme.Api/Ctl.cs", 1),
        Sym(3, SymbolKind.Method, "Acme.Api.Ctl.Go", "Acme.Api/Ctl.cs", 2),
        Sym(10, SymbolKind.Namespace, "Acme.App.Orders", "Acme.App/Orders/Svc.cs"),
        Sym(11, SymbolKind.Class, "Acme.App.Orders.Svc", "Acme.App/Orders/Svc.cs", 10),
        Sym(12, SymbolKind.Method, "Acme.App.Orders.Svc.Run", "Acme.App/Orders/Svc.cs", 11),
        Sym(13, SymbolKind.Namespace, "Acme.App.Users", "Acme.App/Users/Svc.cs"),
        Sym(14, SymbolKind.Class, "Acme.App.Users.Svc", "Acme.App/Users/Svc.cs", 13),
        Sym(15, SymbolKind.Method, "Acme.App.Users.Svc.Run", "Acme.App/Users/Svc.cs", 14),
        Sym(16, SymbolKind.Namespace, "Acme.App.Shared", "Acme.App/Shared/Util.cs"),
        Sym(17, SymbolKind.Class, "Acme.App.Shared.Util", "Acme.App/Shared/Util.cs", 16),
        Sym(18, SymbolKind.Method, "Acme.App.Shared.Util.Help", "Acme.App/Shared/Util.cs", 17),
        Sym(20, SymbolKind.Namespace, "Acme.Infra", "Acme.Infra/Repo.cs"),
        Sym(21, SymbolKind.Class, "Acme.Infra.Repo", "Acme.Infra/Repo.cs", 20),
        Sym(22, SymbolKind.Method, "Acme.Infra.Repo.Save", "Acme.Infra/Repo.cs", 21),
        Sym(30, SymbolKind.Namespace, "Acme.Worker", "Acme.Worker/Job.cs"),
        Sym(31, SymbolKind.Class, "Acme.Worker.Job", "Acme.Worker/Job.cs", 30),
        Sym(32, SymbolKind.Method, "Acme.Worker.Job.Run", "Acme.Worker/Job.cs", 31),
        Sym(40, SymbolKind.Module, "Acme.App/Orders/Svc.cs", "Acme.App/Orders/Svc.cs"),      // 文件容器节点，不算组件成员
        Sym(50, SymbolKind.Namespace, "Stray.Lib", "Acme.App/Stray/X.cs"),                   // 漏写前缀的命名空间
        Sym(51, SymbolKind.Class, "Stray.Lib.X", "Acme.App/Stray/X.cs", 50),
        Sym(52, SymbolKind.Class, "GlobalThing", "Acme.App/Global.cs"),                        // 全局命名空间
        Sym(99, SymbolKind.External, "external:ToList", "<external>"),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(3, 12, EdgeKind.Calls), new(3, 12, EdgeKind.Calls),     // Api → App ×2
        new(3, 22, EdgeKind.Calls),                                  // Api → Infra
        new(12, 22, EdgeKind.Calls),                                 // App → Infra
        new(22, 18, EdgeKind.Calls),                                 // Infra → App（环）
        new(12, 18, EdgeKind.Calls), new(15, 18, EdgeKind.References), // App 内：Orders → Shared，Users → Shared
        new(32, 12, EdgeKind.Calls),                                 // Worker → App
        new(3, 99, EdgeKind.Calls),                                  // external 不算
        new(3, 12, EdgeKind.Calls, 0.3),                             // 多候选不算
        new(2, 3, EdgeKind.Contains),
        new(12, 32, EdgeKind.Calls, 0.9, "impl"),                    // 接口调用 → 实现 的派生边（运行时去向）不算：否则 App 会"依赖" Worker
        new(3, 32, EdgeKind.Dispatches, 1.0, "dispatch:X"),          // 分派边不算
    ];

    private static ArchNode Container(string key, params string[] members) =>
        new(key, ArchNodeKind.Container, "dotnet-web", key, key, ArchSource.Discovered, [], members, []);

    private static readonly ArchNode Api = Container("Acme.Api", "Acme.Api", "Acme.App", "Acme.Infra");
    private static readonly ArchNode Worker = Container("Acme.Worker", "Acme.Worker", "Acme.App");

    private static readonly GitHistoryResult Git = new(
        [
            new GitFileStats("Acme.App/Orders/Svc.cs", 10, 100, 20, new(2026, 1, 1), new(2026, 9, 1), 2),
            new GitFileStats("Acme.App/Shared/Util.cs", 4, 10, 2, new(2026, 1, 1), new(2026, 3, 1), 1),
            new GitFileStats("Acme.App/Users/Svc.cs", 7, 30, 5, new(2026, 1, 1), new(2026, 6, 1), 1),
            new GitFileStats("Acme.Infra/Repo.cs", 3, 5, 5, new(2026, 1, 1), new(2026, 2, 1), 1),
        ],
        [
            new GitCoupling("Acme.App/Users/Svc.cs", "Acme.Infra/Repo.cs", 6, 0.5),   // 容器层 App↔Infra 有静态边 → 不算隐性
            new GitCoupling("Acme.App/Orders/Svc.cs", "Acme.App/Users/Svc.cs", 7, 0.6), // App 内 Orders↔Users 没有静态边 → 隐性耦合
        ], 42, 1, null);

    [Fact]
    public void ContainerLevel_ComponentsAreProjects_CycleDetected_DependenciesBeforeDependents()
    {
        var d = DsmBuilder.Build(Symbols, Edges, Api, "", Git, today: new DateOnly(2026, 10, 14));
        Assert.Equal(new[] { "Acme.App", "Acme.Infra", "Acme.Api" }, d.Components.Select(c => c.Key)); // 环 {App, Infra} 先（App 符号多在前），Api 依赖它们所以在最后
        Assert.Equal(new[] { "App", "Infra", "Api" }, d.Components.Select(c => c.Label));
        Assert.Equal((12, 5, true), (d.Components[0].Symbols, d.Components[0].Files, d.Components[0].HasChildren)); // 9 + Stray 2 + 全局 1；module 符号不算
        Assert.False(d.Components[2].HasChildren); // Api 只有 Acme.Api 一个命名空间，没有更深的
        Assert.Equal(d.Components[0].Scc, d.Components[1].Scc);
        Assert.NotEqual(d.Components[0].Scc, d.Components[2].Scc);
        Assert.Equal(new[] { "Acme.App", "Acme.Infra" }, Assert.Single(d.Cycles));

        var cells = d.Cells.ToDictionary(c => (c.Row, c.Col));
        Assert.Equal(4, cells.Count);
        Assert.Equal(1, cells[(0, 1)].Count); // App → Infra（环的上三角那半）
        Assert.Equal(1, cells[(1, 0)].Count); // Infra → App
        Assert.Equal(2, cells[(2, 0)].Count); // Api → App ×2
        Assert.Equal(1, cells[(2, 1)].Count); // Api → Infra
        Assert.Equal(2, cells[(2, 0)].Kinds["calls"]);

        // git：App = 10 + 4 + 7 修订，最近 2026-09-01 → 1 个月；Api 没有 git 记录
        Assert.Equal((21, 140, 27, 1, "2026-09-01"), (d.Components[0].Hot!.Revisions, d.Components[0].Hot!.Added, d.Components[0].Hot!.Deleted, d.Components[0].Hot!.AgeMonths, d.Components[0].Hot!.LastCommit));
        Assert.Null(d.Components[2].Hot);
        Assert.True(d.GitAvailable);
        Assert.Empty(d.Coupling); // App↔Infra 有静态边，不算隐性
    }

    [Fact]
    public void OtherContainer_SeesOnlyItsOwnMembers()
    {
        var d = DsmBuilder.Build(Symbols, Edges, Worker, "", Git);
        Assert.Equal(new[] { "Acme.App", "Acme.Worker" }, d.Components.Select(c => c.Key));
        var cell = Assert.Single(d.Cells);
        Assert.Equal((1, 0, 1), (cell.Row, cell.Col, cell.Count)); // Worker → App 在对角线下；App → Worker 的 impl 派生边与分派边没有算进来
        Assert.Empty(d.Cycles);
        Assert.False(d.Components[1].HasChildren);
    }

    [Fact]
    public void DrillIntoProject_ComponentsAreChildNamespaces_LowerTriangular_WithImplicitCoupling()
    {
        var d = DsmBuilder.Build(Symbols, Edges, Api, "Acme.App", Git);
        // Shared 被依赖在前；Orders / Users 同为 3 个符号按名字；漏前缀的 Stray.Lib 与全局命名空间单独成组件并标全名
        Assert.Equal(new[] { "Acme.App.Shared", "Acme.App.Orders", "Acme.App.Users", "Stray", "" }, d.Components.Select(c => c.Key));
        Assert.Equal(new[] { "Shared", "Orders", "Users", DsmBuilder.StrayPrefix + "Stray", DsmBuilder.GlobalLabel }, d.Components.Select(c => c.Label));
        Assert.Equal(new[] { false, false, false, true, false }, d.Components.Select(c => c.HasChildren)); // Stray 下面还有 Stray.Lib
        Assert.All(d.Cells, c => Assert.True(c.Row > c.Col, $"({c.Row},{c.Col}) 不在对角线下")); // 无环 → 全部下三角
        Assert.Equal(2, d.Cells.Count);
        Assert.Equal("references", Assert.Single(d.Cells.Single(c => c.Row == 2).Kinds).Key);
        Assert.Empty(d.Cycles);
        // Infra → App.Shared 的边起点在 root 之外，不进矩阵
        var coupling = Assert.Single(d.Coupling);
        Assert.Equal((1, 2, 7, 0.6), (coupling.A, coupling.B, coupling.Shared, coupling.Degree)); // Orders(1) ↔ Users(2)

        // 容器层的 git 热点也不该把 module 符号的文件重复算：App 的 Hot 只来自有符号的 5 个文件里有统计的 3 个
        var top = DsmBuilder.Build(Symbols, Edges, Api, "", Git, today: new DateOnly(2026, 10, 14));
        Assert.Equal(21, top.Components[0].Hot!.Revisions);
    }

    [Fact]
    public void UnknownRoot_OrContainerWithoutSymbols_ReturnsNoteNotCrash()
    {
        var nope = DsmBuilder.Build(Symbols, Edges, Api, "Acme.Nope", Git);
        Assert.Empty(nope.Components);
        Assert.Contains("没有符号", nope.Note);
        var empty = DsmBuilder.Build(Symbols, Edges, Container("ghost", "ghost/dir"), "", null);
        Assert.Empty(empty.Components);
        Assert.False(empty.GitAvailable);
        Assert.Contains("没有索引到符号", empty.Note);
    }

    private static ArchRule Rule(string kind, string from, string to, string test, string fromMatch = ArchRuleMatch.Prefix) =>
        new(kind, from, fromMatch, to, ArchRuleMatch.Prefix, "Acme.Tests/ArchTests.cs", 10, test, "netarchtest");

    [Fact]
    public void Rules_ForbidViolationsPinnedToCells_RequireUnmetIsCountZero_PlacementOnlyListed()
    {
        var rules = new List<ArchRule>
        {
            Rule(ArchRuleKind.ForbidDependency, "Acme.App", "Acme.Infra", "App_NotInfra"),          // App → Infra 有 1 条边 → 违规
            Rule(ArchRuleKind.ForbidDependency, "Acme.Infra", "Acme.Api", "Infra_NotApi"),          // Infra → Api 没有边 → 不违规
            Rule(ArchRuleKind.RequireDependency, "Acme.Api", "Acme.Infra", "Api_UsesInfra"),        // Api → Infra 有边 → 满足
            Rule(ArchRuleKind.RequireDependency, "Acme.Infra", "Acme.App", "Infra_UsesApp"),        // Infra → App 有边 → 满足
            Rule(ArchRuleKind.RequireDependency, "Acme.App", "Acme.Api", "App_UsesApi"),            // App → Api 没有边 → Count 0 的违规
            Rule(ArchRuleKind.Placement, "ICommand", "Modules", "Cmd_InModules", ArchRuleMatch.Exact), // 只展示
            Rule(ArchRuleKind.ForbidDependency, "Acme.App", "Acme.Worker", "App_NotWorker"),        // Worker 不在 Api 容器 → 这里没有列，不判
        };
        var d = DsmBuilder.Build(Symbols, Edges, Api, "", Git, rules: rules);
        Assert.Equal(new[] { "Acme.App", "Acme.Infra", "Acme.Api" }, d.Components.Select(c => c.Key));
        Assert.Equal(7, d.Rules.Count);
        Assert.Equal((0, ArchRuleKind.ForbidDependency, "Acme.App", "Acme.Infra", "Acme.Tests/ArchTests.cs", 10, "App_NotInfra", "netarchtest"), (d.Rules[0].Id, d.Rules[0].Kind, d.Rules[0].From, d.Rules[0].To, d.Rules[0].File, d.Rules[0].Line, d.Rules[0].TestName, d.Rules[0].Source));
        Assert.Equal(ArchRuleKind.Placement, d.Rules[5].Kind);

        Assert.Equal(new[] { (0, 1, 0, 1), (0, 2, 4, 0) }, d.Violations.Select(v => (v.Row, v.Col, v.RuleId, v.Count)));
        // (App=0 → Infra=1) 违反规则 0，1 条边；(App=0 → Api=2) 规则 4 要求依赖却没有 → Count 0
    }

    [Fact]
    public void Rules_MatchByNamespaceNotDirectory_AndAnySubject()
    {
        // 项目层组件键是目录（Acme.App），规则按命名空间匹配；* 主语匹配任何行
        var rules = new List<ArchRule>
        {
            Rule(ArchRuleKind.ForbidDependency, "*", "Acme.Infra", "NothingUsesInfra"),
        };
        var d = DsmBuilder.Build(Symbols, Edges, Api, "", null, rules: rules);
        Assert.Equal(new[] { (0, 1, 0, 1), (2, 1, 0, 1) }, d.Violations.Select(v => (v.Row, v.Col, v.RuleId, v.Count))); // App → Infra 与 Api → Infra
    }

    [Fact]
    public void Rules_CoarserComponentsAreNotJudged_FinerLevelIs()
    {
        // 规则说 Acme.App.Orders 不得依赖 Acme.App.Shared：项目层组件 Acme.App 比规则粗 → 不判；钻进 Acme.App 才判
        var rules = new List<ArchRule> { Rule(ArchRuleKind.ForbidDependency, "Acme.App.Orders", "Acme.App.Shared", "Orders_NotShared") };
        var top = DsmBuilder.Build(Symbols, Edges, Api, "", null, rules: rules);
        Assert.Empty(top.Violations);
        Assert.Single(top.Rules);

        var inner = DsmBuilder.Build(Symbols, Edges, Api, "Acme.App", null, rules: rules);
        Assert.Equal(new[] { "Acme.App.Shared", "Acme.App.Orders", "Acme.App.Users", "Stray", "" }, inner.Components.Select(c => c.Key));
        var v = Assert.Single(inner.Violations);
        Assert.Equal((1, 0, 0, 1), (v.Row, v.Col, v.RuleId, v.Count)); // Orders(1) → Shared(0)

        // contains 规则在细层按包含判；Users 也依赖 Shared
        var contains = DsmBuilder.Build(Symbols, Edges, Api, "Acme.App", null, rules: [Rule(ArchRuleKind.ForbidDependency, "Users", "Acme.App.Shared", "Users_NotShared", ArchRuleMatch.Contains)]);
        Assert.Equal((2, 0), (Assert.Single(contains.Violations).Row, contains.Violations[0].Col));

        // 没有规则 → 空表，不是 null
        var none = DsmBuilder.Build(Symbols, Edges, Api, "", null);
        Assert.Empty(none.Rules);
        Assert.Empty(none.Violations);
    }

    [Fact]
    public void DominantPrefix_IgnoresOutliers()
    {
        Assert.Equal("Acme.App", DsmBuilder.DominantPrefix(["Acme.App.Orders", "Acme.App.Users", "Stray.Lib", ""], "x"));
        Assert.Equal("x", DsmBuilder.DominantPrefix([""], "x"));
        Assert.Equal("src/app", DsmBuilder.DominantPrefix(["src/app/a", "src/app/b"], "x"));
    }
}
