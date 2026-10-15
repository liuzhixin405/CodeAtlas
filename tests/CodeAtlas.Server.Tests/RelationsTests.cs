using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Tests;

/// <summary>手写的内存库：多文件、命名空间 → 类型 → 成员的父子链、容器（arch_nodes），边用 IndexWriter 写。</summary>
internal sealed class RelationsRepo : IDisposable
{
    public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
    private readonly IndexWriter _w;
    private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _namespaces = new(StringComparer.Ordinal);
    private int _line = 1;

    public RelationsRepo() => _w = new IndexWriter(Db);

    public long File(string path) =>
        _files.TryGetValue(path, out var id) ? id : _files[path] = _w.UpsertFile(path, path.EndsWith(".cs") ? "csharp" : "typescript", "h", 1, FileStatus.Ok);

    public long Symbol(SymbolKind kind, string name, string qualifiedName, string path, long? parent)
    {
        var line = _line += 10;
        return Db.Scalar<long>("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, parent_id, source)
            VALUES ($f, $k, $n, $q, $l, 0, $e, 0, $p, 'roslyn') RETURNING id
            """, ("$f", File(path)), ("$k", DbNames.ToDb(kind)), ("$n", name), ("$q", qualifiedName), ("$l", line), ("$e", line + 8), ("$p", parent));
    }

    private long Namespace(string ns, string path)
    {
        var key = path + "|" + ns;
        return _namespaces.TryGetValue(key, out var id) ? id : _namespaces[key] = Symbol(SymbolKind.Namespace, ns, ns, path, null);
    }

    /// <summary>C# 类型：放在 path 文件、ns 命名空间下（父链 类型 → 命名空间）。</summary>
    public long Type(string path, string ns, string name, SymbolKind kind = SymbolKind.Class) =>
        Symbol(kind, name, ns + "." + name, path, Namespace(ns, path));

    public long Nested(long outer, string name)
    {
        var (q, path) = Info(outer);
        return Symbol(SymbolKind.Class, name, q + "." + name, path, outer);
    }

    public long Method(long type, string name)
    {
        var (q, path) = Info(type);
        return Symbol(SymbolKind.Method, name, q + "." + name + "()", path, type);
    }

    public long Field(long type, string name)
    {
        var (q, path) = Info(type);
        return Symbol(SymbolKind.Field, name, q + "." + name, path, type);
    }

    public long Ctor(long type)
    {
        var (q, path) = Info(type);
        return Symbol(SymbolKind.Method, ".ctor", q + "." + q[(q.LastIndexOf('.') + 1)..] + "()", path, type);
    }

    public long Module(string path) => Symbol(SymbolKind.Module, path[(path.LastIndexOf('/') + 1)..], path, path, null);

    public long Function(long module, string name)
    {
        var (_, path) = Info(module);
        return Symbol(SymbolKind.Function, name, path + ":" + name, path, module);
    }

    public long External(string name) => _w.GetOrCreateExternal(name);

    private (string QualifiedName, string Path) Info(long id) =>
        (Db.Query<string>("SELECT qualified_name FROM symbols WHERE id = $id", ("$id", id)).Single(),
         Db.Query<string>("SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id = $id", ("$id", id)).Single());

    public void Edge(long from, long to, EdgeKind kind = EdgeKind.Calls, int line = 0, double conf = 1.0, string? via = null, string file = "src/App/App.cs") =>
        _w.WriteEdges([new NewEdge(from, to, "x", kind, IndexSource.Roslyn, conf, File(file), line == 0 ? ++_line : line, via)]);

    public void Containers(params (string Key, string[] Members)[] containers) =>
        ArchStorage.Replace(Db, new ArchModel(
            containers.Select(c => new ArchNode(c.Key, ArchNodeKind.Container, "web", c.Key.Split('/')[^1], c.Key, ArchSource.Discovered, [], c.Members, [])).ToList(),
            [], [], []));

    public RelationsDto Relations(long id, int depth = 1, bool tests = false) =>
        RelationsQuery.Build(Db, id, new RelationsOptions(depth, tests)) ?? throw new InvalidOperationException("符号不存在");

    public void Dispose() => Db.Dispose();
}

public class RelationsTests
{
    private const string App = "src/App/App.cs";

    private static string Label(RelationsDto r, long id) =>
        new MapSymbol(id, DbNames.ParseSymbolKind(r.Nodes.Single(n => n.Unit.Id == id).Unit.Kind), r.Nodes.Single(n => n.Unit.Id == id).Unit.QualifiedName, r.Nodes.Single(n => n.Unit.Id == id).Unit.Path, null).Name;

    /// <summary>"A→B call 1 · new 1 w=2"，按 Edges 的顺序。</summary>
    private static List<string> EdgeShape(RelationsDto r) =>
        r.Edges.Select(e => $"{Label(r, e.From)}→{Label(r, e.To)} {string.Join(" · ", e.Kinds.Select(k => $"{k.Key} {k.Value}"))} w={e.Weight}").ToList();

    private static List<string> NodeShape(RelationsDto r) => r.Nodes.Select(n => $"{Label(r, n.Unit.Id)}:{n.Distance}{(n.External ? ":ext" : "")}").ToList();

    [Fact]
    public void Unit_MethodMapsToClass_NestedToInnermost_TopLevelFunctionToModule()
    {
        using var r = new RelationsRepo();
        var handler = r.Type(App, "App", "Handler");
        var handle = r.Method(handler, "Handle");
        var validator = r.Nested(handler, "Validator");
        var validate = r.Method(validator, "Validate");
        var module = r.Module("src/web/app.ts");
        var main = r.Function(module, "main");
        r.Edge(validate, handle);

        Assert.Equal(handler, r.Relations(handle).Focus.Id);
        // 嵌套类取最内层：光标在 Validator.Validate 里，中心是 Validator；它调外层的方法画成 Validator → Handler
        var nested = r.Relations(validate);
        Assert.Equal(validator, nested.Focus.Id);
        Assert.Equal(["Validator→Handler call 1 w=1"], EdgeShape(nested));
        Assert.Equal("Validator.Validate", nested.Edges[0].Samples[0].FromLabel);
        Assert.Equal("Handler.Handle", nested.Edges[0].Samples[0].ToLabel);
        // 顶层函数 → 所在文件的模块
        Assert.Equal(module, r.Relations(main).Focus.Id);
        Assert.Equal("module", r.Relations(main).Focus.Kind);
    }

    [Fact]
    public void Kinds_AreCountedAndWeighted_PerUnitPair()
    {
        using var r = new RelationsRepo();
        var a = r.Type(App, "App", "Handler");
        var run = r.Method(a, "Run");
        var repoField = r.Field(a, "_repo");
        var b = r.Type(App, "App", "Repo");
        var save = r.Method(b, "Save");
        var bCtor = r.Ctor(b);
        var msg = r.Type(App, "App", "Command");
        var impl = r.Type(App, "App", "SqlStore");
        var implSave = r.Method(impl, "Save");
        var h = r.Type(App, "App", "CommandHandler");
        var hHandle = r.Method(h, "Handle");
        var baseType = r.Type(App, "App", "HandlerBase");
        var iface = r.Type(App, "App", "IHandler", SymbolKind.Interface);
        var low = r.Type(App, "App", "Maybe");
        var lowM = r.Method(low, "M");

        r.Edge(run, save, line: 10, conf: 1.0);                          // call
        r.Edge(run, bCtor, line: 11, conf: 0.9);                         // 构造函数 → new
        r.Edge(repoField, b, EdgeKind.References, line: 3);              // reference（另一行）
        r.Edge(run, b, EdgeKind.References, line: 11);                   // 与 new 同一行的类型引用：不算
        r.Edge(run, msg, line: 12, via: "conduit");                      // conduit 构造消息 → new
        r.Edge(run, implSave, line: 10, conf: 0.9, via: "impl");         // impl 派生调用 → call
        r.Edge(run, hHandle, EdgeKind.Dispatches, line: 12, via: "dispatch:Command"); // dispatch
        r.Edge(hHandle, msg, EdgeKind.Dispatches, via: "handles");       // handles 不算（不在中心上，也不该被算）
        r.Edge(a, baseType, EdgeKind.Inherits);
        r.Edge(a, iface, EdgeKind.Implements);
        r.Edge(run, lowM, line: 13, conf: 0.3);                          // 多候选档：不算

        var rel = r.Relations(run);
        Assert.Equal(a, rel.Focus.Id);
        Assert.Equal(
            [
                "Handler→HandlerBase inherits 1 w=5",
                "Handler→IHandler implements 1 w=5",
                "Handler→Repo call 1 · new 1 · reference 1 w=2.5",
                "Handler→Command new 1 w=1",
                "Handler→CommandHandler dispatch 1 w=1",
                "Handler→SqlStore call 1 w=1",
            ],
            EdgeShape(rel));
        Assert.DoesNotContain(rel.Nodes, n => n.Unit.Id == low);
        var repoEdge = rel.Edges.Single(e => e.To == b);
        // 样例：置信度降序，同分按 Kind 固定顺序（call 在 reference 前）
        Assert.Equal(["Handler.Run→Repo.Save call 1 @10", "Handler._repo→Repo reference 1 @3", "Handler.Run→Repo new 0.9 @11"],
            repoEdge.Samples.Select(s => $"{s.FromLabel}→{s.ToLabel} {s.Kind} {s.Confidence} @{s.Line}"));
        Assert.Equal("Handler→HandlerBase", $"{rel.Edges[0].Samples[0].FromLabel}→{rel.Edges[0].Samples[0].ToLabel}"); // 继承样例是类型名
    }

    [Fact]
    public void Samples_AreCappedAtFive_ByConfidence()
    {
        using var r = new RelationsRepo();
        var a = r.Type(App, "App", "A");
        var b = r.Type(App, "App", "B");
        var target = r.Method(b, "T");
        var confs = new[] { 0.5, 0.95, 0.6, 1.0, 0.7, 0.9, 0.8 };
        for (var i = 0; i < confs.Length; i++) r.Edge(r.Method(a, "M" + i), target, conf: confs[i]);

        var e = Assert.Single(r.Relations(a).Edges);
        Assert.Equal(7, Assert.Single(e.Kinds, k => k.Key == "call").Value);
        Assert.Single(e.Kinds);
        Assert.Equal(7.0, e.Weight);
        Assert.Equal([1.0, 0.95, 0.9, 0.8, 0.7], e.Samples.Select(s => s.Confidence));
    }

    [Fact]
    public void SameUnitEdges_AreIgnored()
    {
        using var r = new RelationsRepo();
        var a = r.Type(App, "App", "A");
        var m1 = r.Method(a, "M1");
        var m2 = r.Method(a, "M2");
        var b = r.Type(App, "App", "B");
        r.Edge(m1, m2);
        r.Edge(m2, m1);
        r.Edge(m1, a, EdgeKind.References);
        r.Edge(m2, r.Method(b, "N"));

        var rel = r.Relations(m1);
        Assert.Equal(["A→B call 1 w=1"], EdgeShape(rel));
        Assert.Equal(["A:0", "B:1"], NodeShape(rel));
    }

    [Fact]
    public void Externals_OnlyInheritsAndImplementsTargets_InExternalComponentLast()
    {
        using var r = new RelationsRepo();
        r.Containers(("src/App", ["src/App"]));
        var a = r.Type(App, "App", "OrdersController");
        var get = r.Method(a, "Get");
        var controllerBase = r.External("Microsoft.AspNetCore.Mvc.ControllerBase");
        var handlerIface = r.External("MediatR.IRequestHandler<TRequest, TResponse>");
        var handleIface = r.External("MediatR.IRequestHandler<TRequest, TResponse>.Handle(TRequest, CancellationToken)");
        var log = r.External("Microsoft.Extensions.Logging.ILogger.LogInformation(string)");
        var loggerType = r.External("Microsoft.Extensions.Logging.ILogger");
        r.Edge(a, controllerBase, EdgeKind.Inherits);
        r.Edge(a, handlerIface, EdgeKind.Implements);
        r.Edge(get, handleIface, EdgeKind.Implements);  // 方法实现接口方法：和类型那条重复，不算
        r.Edge(get, log);                               // 外部调用：不画
        r.Edge(get, loggerType, EdgeKind.References);   // 外部类型引用：不画

        var rel = r.Relations(get);
        Assert.Equal(["OrdersController:0", "ControllerBase:1:ext", "IRequestHandler:1:ext"], NodeShape(rel));
        Assert.Equal(["OrdersController→ControllerBase inherits 1 w=5", "OrdersController→IRequestHandler implements 1 w=5"], EdgeShape(rel));
        Assert.Equal(["src/App:App:0", "external:外部:1"], rel.Components.Select(c => $"{c.Key}:{c.Label}:{c.Order}"));
        Assert.All(rel.Nodes.Where(n => n.External), n => Assert.Equal("external", n.ComponentKey));
    }

    [Fact]
    public void Depth2_ExpandsFirstRingOnly_AndConnectsKnownNodes()
    {
        using var r = new RelationsRepo();
        var a = r.Type(App, "App", "A");
        var b = r.Type(App, "App", "B");
        var c = r.Type(App, "App", "C");
        var d = r.Type(App, "App", "D");
        var e = r.Type(App, "App", "E");
        var am = r.Method(a, "M");
        var bm = r.Method(b, "M");
        var cm = r.Method(c, "M");
        var em = r.Method(e, "M");
        r.Edge(am, bm);
        r.Edge(am, em);
        r.Edge(bm, em);           // depth 1 节点彼此之间
        r.Edge(bm, cm);           // B → C：C 是 depth 2
        r.Edge(cm, r.Method(d, "M")); // C → D：depth 2 节点自己的邻居不展开

        var d1 = r.Relations(am);
        Assert.Equal(["A:0", "B:1", "E:1"], NodeShape(d1));
        Assert.Equal(["A→B call 1 w=1", "A→E call 1 w=1"], EdgeShape(d1)); // depth 1 不取 B → E

        var d2 = r.Relations(am, depth: 2);
        Assert.Equal(["A:0", "B:1", "E:1", "C:2"], NodeShape(d2));
        Assert.Equal(["A→B call 1 w=1", "A→E call 1 w=1", "B→C call 1 w=1", "B→E call 1 w=1"], EdgeShape(d2));
        Assert.False(d2.Truncated);
    }

    [Fact]
    public void Truncation_KeepsHeaviestFirstRing_ThenFillsDepth2()
    {
        using var r = new RelationsRepo();
        var a = r.Type(App, "App", "A");
        var am = r.Method(a, "M");
        var first = new List<long>();
        // 45 个直接邻居：N00 一条调用，N01 两条……权重各不相同
        for (var i = 0; i < 45; i++)
        {
            var n = r.Type(App, "App", $"N{i:00}");
            var nm = r.Method(n, "M");
            for (var k = 0; k <= i; k++) r.Edge(am, nm);
            first.Add(n);
        }
        var rel = r.Relations(am);
        Assert.True(rel.Truncated);
        Assert.Equal(RelationsBuilder.MaxNodes, rel.Nodes.Count);
        // 留下权重最大的 39 个（N06…N44），N00…N05 被截掉
        Assert.Equal(Enumerable.Range(6, 39).Select(i => $"N{i:00}").Reverse().Prepend("A"), rel.Nodes.Select(n => Label(rel, n.Unit.Id)));
        Assert.DoesNotContain(rel.Edges, e => e.To == first[5]);

        // depth 2：depth 1 只有 3 个、各连 20 个外层 → 36 个空位按与 depth 1 的权重填
        using var r2 = new RelationsRepo();
        var x = r2.Type(App, "App", "X");
        var xm = r2.Method(x, "M");
        for (var j = 0; j < 3; j++)
        {
            var mid = r2.Type(App, "App", $"Mid{j}");
            var midM = r2.Method(mid, "M");
            r2.Edge(xm, midM);
            for (var i = 0; i < 20; i++)
            {
                var o = r2.Type(App, "App", $"O{j}_{i:00}");
                var om = r2.Method(o, "M");
                for (var k = 0; k <= (i == 0 ? 5 : 0); k++) r2.Edge(midM, om); // 每组的 O?_00 权重 6，其余 1
            }
        }
        var rel2 = r2.Relations(xm, depth: 2);
        Assert.True(rel2.Truncated);
        Assert.Equal(40, rel2.Nodes.Count);
        Assert.Equal(1 + 3, rel2.Nodes.Count(n => n.Distance <= 1));
        Assert.Equal(36, rel2.Nodes.Count(n => n.Distance == 2));
        Assert.Equal(["O0_00", "O1_00", "O2_00"], rel2.Nodes.Where(n => n.Distance == 2).Take(3).Select(n => Label(rel2, n.Unit.Id)));
    }

    [Fact]
    public void Components_FollowDependencyOrder_SoftImplBreaksTies_CycleByName()
    {
        using var r = new RelationsRepo();
        r.Containers(("src/Api", ["src/Api", "src/Domain", "src/Infra"]));
        var handler = r.Type("src/Api/Handler.cs", "Shop.Api", "Handler");
        var handle = r.Method(handler, "Handle");
        var irepo = r.Type("src/Domain/IRepo.cs", "Shop.Domain", "IRepo", SymbolKind.Interface);
        var irepoAdd = r.Method(irepo, "Add");
        var order = r.Type("src/Domain/Order.cs", "Shop.Domain", "Order");
        var repo = r.Type("src/Infra/Repo.cs", "Shop.Infra", "Repo");
        var repoAdd = r.Method(repo, "Add");
        r.Edge(handle, irepoAdd);                     // Api → Domain（编译期）
        r.Edge(handle, r.Ctor(order));                // Api → Domain
        r.Edge(handle, repoAdd, conf: 0.9, via: "impl"); // Api → Infra 只有运行时去向
        r.Edge(repo, irepo, EdgeKind.Implements, file: "src/Infra/Repo.cs"); // Infra → Domain

        var rel = r.Relations(handle, depth: 2);
        Assert.Equal(["src/Domain:Domain:0", "src/Infra:Infra:1", "src/Api:Api:2"], rel.Components.Select(c => $"{c.Key}:{c.Label}:{c.Order}"));
        Assert.All(rel.Components, c => Assert.Equal("src/Api", c.ContainerKey));
        Assert.Equal("src/Infra", rel.Nodes.Single(n => n.Unit.Id == repo).ComponentKey);

        // 环：Q ↔ P 互相依赖（环内按键名 P、Q），A0 依赖 P（拓扑序在环之后，尽管名字最小）
        using var c = new RelationsRepo();
        c.Containers(("src/A0", ["src/A0", "src/P", "src/Q"]));
        var p = c.Type("src/P/P.cs", "X.P", "P");
        var q = c.Type("src/Q/Q.cs", "X.Q", "Q");
        var a0 = c.Type("src/A0/A0.cs", "X.A0", "A0");
        var pm = c.Method(p, "M");
        var qm = c.Method(q, "M");
        c.Edge(qm, pm);
        c.Edge(pm, qm);
        c.Edge(c.Method(a0, "M"), pm);
        Assert.Equal(["src/P", "src/Q", "src/A0"], c.Relations(qm, depth: 2).Components.OrderBy(x => x.Order).Select(x => x.Key));

        // 运行时去向与编译期依赖成环时不采用：A → B（编译期），B → A 只有 impl → 仍是 B、A（采用的话成环，按名字会变成 A、B）
        using var s = new RelationsRepo();
        s.Containers(("src/A", ["src/A", "src/B"]));
        var ta = s.Type("src/A/A.cs", "Y.A", "A");
        var tb = s.Type("src/B/B.cs", "Y.B", "B");
        var am = s.Method(ta, "M");
        var bm = s.Method(tb, "M");
        s.Edge(am, bm);
        s.Edge(bm, am, conf: 0.9, via: "impl");
        Assert.Equal(["src/B", "src/A"], s.Relations(am).Components.OrderBy(x => x.Order).Select(x => x.Key));
    }

    [Fact]
    public void Components_Fallback_AndMonolithDrillsIntoNamespaces()
    {
        // 不在任何容器成员下：路径上名字是命名空间前缀的目录（项目目录）
        Assert.Equal("tests/Shop.UnitTests", RelationsBuilder.FallbackKey(new RelationUnitPlace(1, "tests/Shop.UnitTests/Domain/OrderTests.cs", "Shop.UnitTests.Domain.OrderTests", "Shop.UnitTests.Domain")));
        Assert.Equal("lib/x", RelationsBuilder.FallbackKey(new RelationUnitPlace(1, "lib/x/a.ts", "lib/x/a.ts", null)));

        // 单体项目：都在 src/Shop 里 → 按主命名空间下一段分；主命名空间那一层标"（本级）"，漏前缀的标 ⚠
        using var r = new RelationsRepo();
        r.Containers(("src/Shop", ["src/Shop"]));
        var ctrl = r.Type("src/Shop/Controllers/OrdersController.cs", "Shop.Controllers", "OrdersController");
        var svc = r.Type("src/Shop/Services/OrderService.cs", "Shop.Services", "OrderService");
        var startup = r.Type("src/Shop/Startup.cs", "Shop", "Startup");
        var stray = r.Type("src/Shop/Legacy/Old.cs", "Legacy", "Old");
        var m = r.Method(ctrl, "Get");
        r.Edge(m, r.Method(svc, "Find"));
        r.Edge(m, r.Method(startup, "X"));
        r.Edge(m, r.Method(stray, "Y"));
        var rel = r.Relations(m);
        Assert.Equal(
            ["Legacy:⚠ Legacy", "Shop:（本级）", "Shop.Services:Services", "Shop.Controllers:Controllers"],
            rel.Components.OrderBy(c => c.Order).Select(c => $"{c.Key}:{c.Label}"));
        Assert.All(rel.Components, c => Assert.Equal("src/Shop", c.ContainerKey));
    }

    [Fact]
    public void Components_GoPackageMember_SplitsIntoPackageDirs()
    {
        var containers = new List<ArchNode> { new("svc", ArchNodeKind.Container, "go", "svc", "svc", ArchSource.Discovered, [], ["svc"], []) };
        var units = new List<RelationUnitPlace>
        {
            new(1, "svc/main.go", "svc/main.go", null),
            new(2, "svc/handlers/orders.go", "svc/handlers/orders.go", null),
            new(3, "svc/store/db/sql.go", "svc/store/db/sql.go", null),
        };
        var comps = RelationsBuilder.AssignComponents(units, 1, containers, _ => true, _ => throw new InvalidOperationException());
        Assert.Equal(["svc:svc", "svc/handlers:handlers", "svc/store:store"], units.Select(u => $"{comps[u.Id].Key}:{comps[u.Id].Label}"));
    }

    [Fact]
    public void Tests_AreFilteredUnlessRequested_OrCenterIsTest()
    {
        using var r = new RelationsRepo();
        var svc = r.Type(App, "App", "Svc");
        var find = r.Method(svc, "Find");
        var other = r.Type(App, "App", "Other");
        var t = r.Type("tests/App.Tests/SvcTests.cs", "App.Tests", "SvcTests");
        var tm = r.Method(t, "Finds");
        r.Edge(tm, find, file: "tests/App.Tests/SvcTests.cs");
        r.Edge(find, r.Method(other, "M"));

        Assert.Equal(["Svc:0", "Other:1"], NodeShape(r.Relations(find)));
        Assert.Equal(["Svc:0", "Other:1", "SvcTests:1"], NodeShape(r.Relations(find, tests: true)));
        Assert.Equal(["SvcTests:0", "Svc:1"], NodeShape(r.Relations(tm))); // 中心本身是测试：照常显示
        Assert.Equal("tests/App.Tests", r.Relations(tm).Nodes[0].ComponentKey); // 不在容器里：退到项目目录
    }

    [Fact]
    public void UnknownSymbol_ReturnsNull()
    {
        using var r = new RelationsRepo();
        Assert.Null(RelationsQuery.Build(r.Db, 987654321, new RelationsOptions()));
    }
}
