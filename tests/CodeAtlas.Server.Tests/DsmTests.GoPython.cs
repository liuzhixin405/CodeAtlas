using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Go / Python 的 DSM：组件 = 包目录。容器层第一级是成员目录本身（直接放在里面的文件）加它下面的顶层子目录；钻进去是子目录。目录名里的 '.' 不切。</summary>
public partial class DsmTests
{
    private static readonly List<MapSymbol> GoSymbols =
    [
        Sym(3000, SymbolKind.Module, "src/frontend/main.go", "src/frontend/main.go"),
        Sym(3001, SymbolKind.Function, "src/frontend.main", "src/frontend/main.go", 3000),
        Sym(3002, SymbolKind.Method, "src/frontend.frontendServer.homeHandler", "src/frontend/handlers.go"),
        Sym(3003, SymbolKind.Class, "src/frontend.frontendServer", "src/frontend/main.go", 3000),
        Sym(3010, SymbolKind.Function, "src/frontend/money.Sum", "src/frontend/money/money.go"),
        Sym(3011, SymbolKind.Function, "src/frontend/money.IsValid", "src/frontend/money/money.go"),
        Sym(3020, SymbolKind.Function, "src/frontend/validator.Validate", "src/frontend/validator/validator.go"),
        Sym(3021, SymbolKind.Function, "src/frontend/validator/rules.Required", "src/frontend/validator/rules/required.go"),
        Sym(3030, SymbolKind.Function, "src/frontend/v1.2.Legacy", "src/frontend/v1.2/legacy.go"),
        Sym(3099, SymbolKind.External, "external:fmt.Sprintf", "<external>"),
    ];

    private static readonly List<MapEdge> GoEdges =
    [
        new(3002, 3010, EdgeKind.Calls),
        new(3002, 3020, EdgeKind.Calls),
        new(3020, 3021, EdgeKind.Calls),
        new(3020, 3011, EdgeKind.References),
        new(3021, 3011, EdgeKind.Calls),
        new(3030, 3010, EdgeKind.Calls),
        new(3001, 3099, EdgeKind.Calls),
    ];

    [Fact]
    public void Go_ContainerLevel_ComponentsAreTopLevelPackages_DotInDirectoryNameKept()
    {
        var c = new ArchNode("src/frontend", ArchNodeKind.Container, "go", "frontend", "src/frontend", ArchSource.Discovered, [], ["src/frontend"], []);
        var d = DsmBuilder.Build(GoSymbols, GoEdges, c, "", null);
        // money 被所有人依赖在前；validator（2 个符号）先于 v1.2；frontend 本级依赖 money 与 validator
        Assert.Equal(new[] { "src/frontend/money", "src/frontend/validator", "src/frontend", "src/frontend/v1.2" }, d.Components.Select(x => x.Key));
        Assert.Equal(new[] { "money", "validator", "frontend", "v1.2" }, d.Components.Select(x => x.Label));
        Assert.Equal(new[] { 2, 2, 3, 1 }, d.Components.Select(x => x.Symbols));                 // validator 含子包 rules；module 符号不算
        Assert.Equal(new[] { false, true, false, false }, d.Components.Select(x => x.HasChildren)); // validator 下还有 rules；本级的子目录已经摊在这一层
        var cells = d.Cells.ToDictionary(x => (x.Row, x.Col));
        Assert.Equal(4, cells.Count);
        Assert.Equal((2, 1, 1), (cells[(1, 0)].Count, cells[(1, 0)].Kinds["calls"], cells[(1, 0)].Kinds["references"])); // validator → money
        Assert.Equal(1, cells[(2, 0)].Count); // frontend → money
        Assert.Equal(1, cells[(2, 1)].Count); // frontend → validator
        Assert.Equal(1, cells[(3, 0)].Count); // v1.2 → money
        Assert.Empty(d.Cycles);
    }

    [Fact]
    public void Go_DrillIntoPackage_ChildrenAreSubdirectories()
    {
        var c = new ArchNode("src/frontend", ArchNodeKind.Container, "go", "frontend", "src/frontend", ArchSource.Discovered, [], ["src/frontend"], []);
        var d = DsmBuilder.Build(GoSymbols, GoEdges, c, "src/frontend/validator", null);
        Assert.Equal(new[] { "src/frontend/validator/rules", "src/frontend/validator" }, d.Components.Select(x => x.Key));
        Assert.Equal(new[] { "rules", DsmBuilder.SelfLabel }, d.Components.Select(x => x.Label));
        Assert.Equal((1, 0, 1), (d.Cells.Single().Row, d.Cells.Single().Col, d.Cells.Single().Count));

        var dotted = DsmBuilder.Build(GoSymbols, GoEdges, c, "src/frontend/v1.2", null);
        Assert.Equal(("src/frontend/v1.2", DsmBuilder.SelfLabel), (Assert.Single(dotted.Components).Key, dotted.Components[0].Label));
    }

    [Theory]
    [InlineData(SymbolKind.Function, "src/frontend.homeHandler", "homeHandler")]
    [InlineData(SymbolKind.Method, "src/frontend.frontendServer.homeHandler", "homeHandler")]
    [InlineData(SymbolKind.Function, "golang-gin-realworld-example-app/users.UsersRegister", "UsersRegister")]
    [InlineData(SymbolKind.Function, "pkg/v1.2.Legacy", "Legacy")]
    [InlineData(SymbolKind.External, "external:http.HandleFunc", "HandleFunc")]
    [InlineData(SymbolKind.Method, "app.api.routes.items.ItemRepo.read_items", "read_items")]
    public void ShortNames_GoAndPythonQualifiedNames(SymbolKind kind, string qualified, string expected) =>
        Assert.Equal(expected, new MapSymbol(1, kind, qualified, "x", null).Name);

    [Fact]
    public void MapBuilder_DirectoryKeys_DotInDirectoryNameIsNotASeparator()
    {
        var map = MapBuilder.Build(GoSymbols, GoEdges, "src/frontend");
        // 组键按 '/' 切：v1.2 是一个目录，不是 v1 下的 2
        Assert.Equal(new[] { ("src/frontend/money", "money"), ("src/frontend/v1.2", "v1.2"), ("src/frontend/validator", "validator") },
            map.Groups.Where(g => g.SymbolId is null).Select(g => (g.Key, g.Label)));
        var top = MapBuilder.Build(GoSymbols, GoEdges, "");
        Assert.Equal("src", Assert.Single(top.Groups).Key);
    }

    private static readonly List<MapSymbol> PySymbols =
    [
        Sym(3100, SymbolKind.Function, "backend.app.main.healthz", "backend/app/main.py"),
        Sym(3101, SymbolKind.Class, "backend.app.models.Item", "backend/app/models.py"),
        Sym(3102, SymbolKind.Function, "backend.app.api.routes.items.read_items", "backend/app/api/routes/items.py"),
        Sym(3103, SymbolKind.Function, "backend.app.api.deps.get_db", "backend/app/api/deps.py"),
        Sym(3104, SymbolKind.Class, "backend.app.core.config.Settings", "backend/app/core/config.py"),
        Sym(3105, SymbolKind.Function, "backend.alembic.env.run", "backend/alembic/env.py"),
        Sym(3106, SymbolKind.Module, "backend.app.main", "backend/app/main.py"),
    ];

    private static readonly List<MapEdge> PyEdges =
    [
        new(3102, 3101, EdgeKind.References),
        new(3102, 3103, EdgeKind.Calls),
        new(3103, 3104, EdgeKind.References),
        new(3105, 3101, EdgeKind.References),
    ];

    [Fact]
    public void Python_ContainerLevel_TopLevelPackages_DrillIntoAppPackage()
    {
        var c = new ArchNode("backend", ArchNodeKind.Container, "python", "backend", "backend", ArchSource.Discovered, [], ["backend"], []);
        var top = DsmBuilder.Build(PySymbols, PyEdges, c, "", null);
        Assert.Equal(new[] { "backend/app", "backend/alembic" }, top.Components.Select(x => x.Key));
        Assert.Equal(new[] { "app", "alembic" }, top.Components.Select(x => x.Label));
        Assert.Equal(new[] { 5, 1 }, top.Components.Select(x => x.Symbols));
        Assert.Equal(new[] { true, false }, top.Components.Select(x => x.HasChildren));
        Assert.Equal((1, 0, 1), (top.Cells.Single().Row, top.Cells.Single().Col, top.Cells.Single().Count)); // alembic → app

        var app = DsmBuilder.Build(PySymbols, PyEdges, c, "backend/app", null);
        // 本级（main.py、models.py）没有依赖在前；api 依赖本级与 core 在最后
        Assert.Equal(new[] { "backend/app", "backend/app/core", "backend/app/api" }, app.Components.Select(x => x.Key));
        Assert.Equal(new[] { DsmBuilder.SelfLabel, "core", "api" }, app.Components.Select(x => x.Label));
        Assert.Equal(new[] { false, false, true }, app.Components.Select(x => x.HasChildren)); // api 下还有 routes
        Assert.Equal(new[] { (2, 0, 1), (2, 1, 1) }, app.Cells.Select(x => (x.Row, x.Col, x.Count)));
    }
}
