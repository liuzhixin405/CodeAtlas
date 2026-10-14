using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

public class MapTests
{
    private static MapSymbol Sym(long id, SymbolKind kind, string qname, string path, long? parent = null) => new(id, kind, qname, path, parent);

    private static readonly List<MapSymbol> Symbols =
    [
        // C#：命名空间链 Acme.Api / Acme.Handlers / Acme.Infra
        Sym(1, SymbolKind.Namespace, "Acme.Api", "Api/Ctl.cs"),
        Sym(2, SymbolKind.Class, "Acme.Api.Ctl", "Api/Ctl.cs", 1),
        Sym(3, SymbolKind.Method, "Acme.Api.Ctl.List", "Api/Ctl.cs", 2),
        Sym(4, SymbolKind.Namespace, "Acme.Handlers", "Handlers/H.cs"),
        Sym(5, SymbolKind.Class, "Acme.Handlers.ListHandler", "Handlers/H.cs", 4),
        Sym(6, SymbolKind.Method, "Acme.Handlers.ListHandler.HandleAsync", "Handlers/H.cs", 5),
        Sym(7, SymbolKind.Namespace, "Acme.Infra", "Infra/Repo.cs"),
        Sym(8, SymbolKind.Class, "Acme.Infra.Repo", "Infra/Repo.cs", 7),
        Sym(9, SymbolKind.Method, "Acme.Infra.Repo.ListAsync", "Infra/Repo.cs", 8),
        Sym(10, SymbolKind.Class, "Acme.Infra.Other", "Infra/Other.cs", 7),
        // TS：按目录
        Sym(20, SymbolKind.Module, "src/app/main.ts", "src/app/main.ts"),
        Sym(21, SymbolKind.Function, "src/app/main.ts:main", "src/app/main.ts", 20),
        Sym(22, SymbolKind.Module, "src/lib/util.ts", "src/lib/util.ts"),
        Sym(23, SymbolKind.Function, "src/lib/util.ts:helper", "src/lib/util.ts", 22),
        // external 不参与
        Sym(99, SymbolKind.External, "external:fetch", "<external>"),
    ];

    private static readonly List<MapEdge> Edges =
    [
        new(3, 6, EdgeKind.Dispatches, 0.4), // Api → Handlers
        new(6, 9, EdgeKind.Calls),      // Handlers → Infra
        new(6, 9, EdgeKind.Calls),      // 同一对再来一条，权重累加
        new(9, 10, EdgeKind.Calls),     // Infra 内部，不算跨组
        new(21, 23, EdgeKind.Calls),    // src/app → src/lib
        new(21, 99, EdgeKind.Calls),    // 到 external，忽略
        new(2, 5, EdgeKind.Contains),   // contains 不算依赖
    ];

    [Fact]
    public void GroupKey_UsesNamespaceForCSharpAndDirectoryForScripts()
    {
        var keys = MapBuilder.GroupKeys(Symbols);
        Assert.Equal("Acme.Api", keys[3]);
        Assert.Equal("Acme.Infra", keys[10]);
        Assert.Equal("src/app", keys[21]);
        Assert.Equal("src/lib", keys[23]);
        Assert.False(keys.ContainsKey(99));
    }

    [Fact]
    public void Root_Empty_ShowsTopLevelGroups_WithAggregatedEdges()
    {
        var map = MapBuilder.Build(Symbols, Edges, root: "");
        Assert.Equal(new[] { "Acme", "src" }, map.Groups.Select(g => g.Key).OrderBy(k => k));
        var acme = map.Groups.Single(g => g.Key == "Acme");
        Assert.Equal(10, acme.Symbols); // 命名空间 3 个 + 类 4 个 + 方法 3 个，不含 external
        Assert.Equal(4, acme.Files);
        Assert.Empty(map.Edges);        // Acme 与 src 之间没有边
        Assert.All(map.Groups, g => Assert.True(g.HasChildren));
    }

    [Fact]
    public void Root_Acme_ShowsSubNamespaces_AndCrossGroupEdgeWeights()
    {
        var map = MapBuilder.Build(Symbols, Edges, root: "Acme");
        Assert.Equal(new[] { "Acme.Api", "Acme.Handlers", "Acme.Infra" }, map.Groups.Select(g => g.Key).OrderBy(k => k));
        Assert.Equal(2, map.Edges.Count);
        var apiToHandlers = map.Edges.Single(e => e.From == "Acme.Api" && e.To == "Acme.Handlers");
        Assert.Equal(1, apiToHandlers.Weight);
        Assert.Equal(1, apiToHandlers.Kinds["dispatches"]);
        var handlersToInfra = map.Edges.Single(e => e.From == "Acme.Handlers" && e.To == "Acme.Infra");
        Assert.Equal(2, handlersToInfra.Weight);
        Assert.Equal(2, handlersToInfra.Kinds["calls"]);
        // 叶子命名空间没有子命名空间，下一层是类型
        Assert.All(map.Groups, g => Assert.False(g.HasChildren));
    }

    [Fact]
    public void Root_LeafNamespace_ShowsTypes_AsSymbolNodes()
    {
        var map = MapBuilder.Build(Symbols, Edges, root: "Acme.Infra");
        Assert.Equal(new[] { "Acme.Infra.Other", "Acme.Infra.Repo" }, map.Groups.Select(g => g.Key).OrderBy(k => k));
        var repo = map.Groups.Single(g => g.Key == "Acme.Infra.Repo");
        Assert.Equal(8, repo.SymbolId);  // 可以直接切到调用图聚焦这个类
        Assert.Equal("Repo", repo.Label);
        var e = Assert.Single(map.Edges);
        Assert.Equal(("Acme.Infra.Repo", "Acme.Infra.Other", 1), (e.From, e.To, e.Weight));
    }

    [Fact]
    public void Root_Directory_ForScripts()
    {
        var map = MapBuilder.Build(Symbols, Edges, root: "src");
        Assert.Equal(new[] { "src/app", "src/lib" }, map.Groups.Select(g => g.Key).OrderBy(k => k));
        var e = Assert.Single(map.Edges);
        Assert.Equal(("src/app", "src/lib"), (e.From, e.To));
    }

    [Fact]
    public void MixedSeparators_ScriptDirInsideCSharpProjectFolder_FoldsIntoThatNamespaceGroup()
    {
        // C# 项目目录里放了前端静态页：目录键 "Acme.Api/DevPages/wwwroot" 应归到 Acme.Api 组，而不是单独成组
        var symbols = new List<MapSymbol>
        {
            Sym(1, SymbolKind.Namespace, "Acme.Api", "Acme.Api/Ctl.cs"),
            Sym(2, SymbolKind.Class, "Acme.Api.Ctl", "Acme.Api/Ctl.cs", 1),
            Sym(3, SymbolKind.Module, "Acme.Api/DevPages/wwwroot/app.js", "Acme.Api/DevPages/wwwroot/app.js"),
            Sym(4, SymbolKind.Function, "Acme.Api/DevPages/wwwroot/app.js:boot", "Acme.Api/DevPages/wwwroot/app.js", 3),
            Sym(5, SymbolKind.Namespace, "Acme.Core", "Acme.Core/X.cs"),
        };
        var top = MapBuilder.Build(symbols, [], root: "");
        Assert.Equal(new[] { "Acme" }, top.Groups.Select(g => g.Key));
        var acme = MapBuilder.Build(symbols, [], root: "Acme");
        Assert.Equal(new[] { "Acme.Api", "Acme.Core" }, acme.Groups.Select(g => g.Key).OrderBy(k => k));
        Assert.Equal(4, acme.Groups.Single(g => g.Key == "Acme.Api").Symbols);
    }

    [Fact]
    public void AmbiguousEdges_AreExcludedFromModuleDependencies()
    {
        var edges = new List<MapEdge>
        {
            new(3, 6, EdgeKind.Calls, 0.3), // 多候选噪声
            new(3, 9, EdgeKind.Calls, 0.7),
        };
        var map = MapBuilder.Build(Symbols, edges, root: "Acme");
        var e = Assert.Single(map.Edges);
        Assert.Equal(("Acme.Api", "Acme.Infra"), (e.From, e.To));
    }

    [Fact]
    public void Root_Unknown_ReturnsEmptyMap()
    {
        var map = MapBuilder.Build(Symbols, Edges, root: "Nope");
        Assert.Empty(map.Groups);
        Assert.Empty(map.Edges);
    }
}
