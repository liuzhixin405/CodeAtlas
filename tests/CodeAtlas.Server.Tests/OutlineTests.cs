using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Tests;

/// <summary>手写的内存库：符号逐个插入（带父子关系），边用 IndexWriter 写（带 pos / ctx）。</summary>
internal sealed class OutlineRepo : IDisposable
{
    public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
    private readonly IndexWriter _w;
    private readonly long _file;
    private readonly long _testFile;
    private int _line = 1;

    public OutlineRepo()
    {
        _w = new IndexWriter(Db);
        _file = _w.UpsertFile("src/App/App.cs", "csharp", "h", 1, FileStatus.Ok);
        _testFile = _w.UpsertFile("tests/App.Tests/AppTests.cs", "csharp", "h", 1, FileStatus.Ok);
    }

    /// <param name="file">文件 id（<see cref="File"/> 建的）；null 时按 test 落到主文件或测试文件。</param>
    /// <param name="source">roslyn / treesitter（影响范围对 tree-sitter 符号按名字找接口方法）。</param>
    public long Symbol(SymbolKind kind, string name, string qualifiedName, long? parent = null, bool test = false, long? file = null, string source = "roslyn")
    {
        var line = _line += 10;
        return Db.Scalar<long>("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, parent_id, source)
            VALUES ($f, $k, $n, $q, $l, 0, $e, 0, $p, $s) RETURNING id
            """, ("$f", file ?? (test ? _testFile : _file)), ("$k", DbNames.ToDb(kind)), ("$n", name), ("$q", qualifiedName), ("$l", line), ("$e", line + 8), ("$p", parent), ("$s", source));
    }

    /// <summary>再建一个文件（跨容器的前端文件等）。</summary>
    public long File(string path) => _w.UpsertFile(path, path.EndsWith(".cs", StringComparison.Ordinal) ? "csharp" : "typescript", "h", 1, FileStatus.Ok);

    /// <summary>符号的起始行（手造符号各占 9 行：start..start+8）。</summary>
    public int StartLine(long id) => Db.Scalar<int>("SELECT start_line FROM symbols WHERE id = $id", ("$id", id));

    public long Type(string name, SymbolKind kind = SymbolKind.Class, bool test = false, long? file = null, string source = "roslyn") =>
        Symbol(kind, name, "App." + name, test: test, file: file, source: source);

    /// <summary>类型下的方法；限定名按 Roslyn 的形状带参数列表（parameters 是括号里的文本）。</summary>
    public long Method(long type, string name, bool test = false, string parameters = "", string source = "roslyn")
    {
        var typeQName = Db.Query<string>("SELECT qualified_name FROM symbols WHERE id = $id", ("$id", type)).Single();
        var fileId = Db.Scalar<long>("SELECT file_id FROM symbols WHERE id = $id", ("$id", type));
        return Symbol(SymbolKind.Method, name, typeQName + "." + name + "(" + parameters + ")", type, test, fileId, source);
    }

    public long External(string name) => _w.GetOrCreateExternal(name);

    public void Edge(long from, long to, int line, int? pos = null, string? ctx = null, double conf = 1.0, string? via = null, EdgeKind kind = EdgeKind.Calls) =>
        _w.WriteEdges([new NewEdge(from, to, "x", kind, IndexSource.Roslyn, conf, _file, line, via, Pos: pos, Ctx: ctx)]);

    public OutlineDto Outline(long id, bool externals = true, bool tests = false) =>
        OutlineQuery.Build(Db, id, new OutlineOptions(externals, tests)) ?? throw new InvalidOperationException("符号不存在");

    public void Dispose() => Db.Dispose();
}

public class OutlineTests
{
    private static string Shape(IEnumerable<OutlineStepDto> steps) =>
        string.Join(" | ", steps.Select(s => $"{s.Order}:{s.Kind}:{s.Label}" + (s.Repeat > 1 ? $"x{s.Repeat}" : "") +
                                             (s.Children.Count > 0 ? " [" + Shape(s.Children) + "]" : "")));

    /// <summary>根方法 + 若干目标方法（都在 App.Svc 上）。</summary>
    private static (OutlineRepo R, long Root, long Svc) Repo()
    {
        var r = new OutlineRepo();
        var host = r.Type("Host");
        var root = r.Method(host, "Run");
        var svc = r.Type("Svc");
        return (r, root, svc);
    }

    [Fact]
    public void Sort_ByPos_WhenEveryStepHasPos()
    {
        var (r, root, svc) = Repo();
        using var _ = r;
        // 链式调用：三个调用都记在链开头那一行（10），只有 pos 分得出先后；边的插入顺序故意打乱
        r.Edge(root, r.Method(svc, "Third"), line: 10, pos: 300);
        r.Edge(root, r.Method(svc, "First"), line: 10, pos: 100);
        r.Edge(root, r.Method(svc, "Second"), line: 10, pos: 200);
        r.Edge(root, r.Method(svc, "Fourth"), line: 9, pos: 400); // pos 优先于行：都有 pos 时不看行号

        Assert.Equal("1:call:Svc.First | 2:call:Svc.Second | 3:call:Svc.Third | 4:call:Svc.Fourth", Shape(r.Outline(root).Steps));
    }

    [Fact]
    public void Sort_ByLineThenEdgeId_WhenNoPos()
    {
        var (r, root, svc) = Repo();
        using var _ = r;
        r.Edge(root, r.Method(svc, "Late"), line: 12);
        r.Edge(root, r.Method(svc, "SameLineFirstWritten"), line: 10);
        r.Edge(root, r.Method(svc, "SameLineSecondWritten"), line: 10);

        Assert.Equal("1:call:Svc.SameLineFirstWritten | 2:call:Svc.SameLineSecondWritten | 3:call:Svc.Late", Shape(r.Outline(root).Steps));
    }

    [Fact]
    public void Sort_Mixed_LineFirstThenPosWithinLine_NullPosLastInItsLine()
    {
        var (r, root, svc) = Repo();
        using var _ = r;
        r.Edge(root, r.Method(svc, "NoPos10"), line: 10);
        r.Edge(root, r.Method(svc, "Pos10"), line: 10, pos: 105);
        r.Edge(root, r.Method(svc, "Line9"), line: 9, pos: 500);
        r.Edge(root, r.Method(svc, "Line11"), line: 11, pos: 50); // 单按 pos 会排第一

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:call:Svc.Line9 | 2:call:Svc.Pos10 | 3:call:Svc.NoPos10 | 4:call:Svc.Line11", Shape(steps));
        Assert.Equal([9, 10, 10, 11], steps.Select(s => s.Line));
        Assert.Equal([500, 105, null, 50], steps.Select(s => s.Pos));
    }

    [Fact]
    public void Impl_UniqueImplementation_HangsUnderInterfaceCall_AndIsTheExpandableOne()
    {
        var (r, root, svc) = Repo();
        using var _ = r;
        var iface = r.Type("IRepo", SymbolKind.Interface);
        var ifaceAdd = r.Method(iface, "Add");
        var repo = r.Type("Repo");
        var repoAdd = r.Method(repo, "Add");
        r.Edge(repoAdd, r.External("Microsoft.EntityFrameworkCore.DbSet.Add(object)"), line: 200); // 实现自己有调用
        r.Edge(root, r.Method(svc, "Before"), line: 19, pos: 350);
        r.Edge(root, ifaceAdd, line: 20, pos: 400, conf: 0.9);
        r.Edge(root, repoAdd, line: 20, pos: 400, conf: 0.9, via: "impl");

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:call:Svc.Before | 2:call:IRepo.Add [1:impl:Repo.Add]", Shape(steps));
        var call = steps[1];
        Assert.False(call.Expandable); // 接口方法没有方法体
        var impl = Assert.Single(call.Children);
        Assert.Equal(repoAdd, impl.Target!.Id);
        Assert.True(impl.Expandable);
        Assert.Equal(0.9, impl.Confidence);
    }

    [Fact]
    public void Impl_MultipleImplementations_MatchedBySameLineAndMethodName_WhenPosMissing()
    {
        var (r, root, _) = Repo();
        using var __ = r;
        var iface = r.Type("IStore", SymbolKind.Interface);
        var save = r.Method(iface, "Save");
        var sql = r.Method(r.Type("SqlStore"), "Save");
        var mem = r.Method(r.Type("MemoryStore"), "Save");
        r.Edge(root, save, line: 30, pos: 700);
        r.Edge(root, sql, line: 30, conf: 0.5, via: "impl");
        r.Edge(root, mem, line: 30, conf: 0.5, via: "impl");
        // 找不到接口调用那一步的 impl：单独成一步
        var orphan = r.Method(r.Type("Lonely"), "Go");
        r.Edge(root, orphan, line: 31, conf: 0.9, via: "impl");

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:call:IStore.Save [1:impl:SqlStore.Save | 2:impl:MemoryStore.Save] | 2:impl:Lonely.Go", Shape(steps));
        Assert.All(steps[0].Children, c => Assert.False(c.Expandable)); // 两个实现都没有调用
    }

    [Fact]
    public void Dispatch_HangsUnderExternalSend_AndIsPromotedWhenExternalsOff()
    {
        var (r, root, _) = Repo();
        using var __ = r;
        var command = r.Type("CreateOrderCommand");
        var handler = r.Type("CreateOrderCommandHandler");
        var handle = r.Method(handler, "Handle");
        r.Edge(handle, r.Method(r.Type("Order"), "Place"), line: 300);
        var send = r.External("MediatR.IMediator.Send<TResponse>(IRequest<TResponse>, CancellationToken)");
        r.Edge(root, command, line: 40, pos: 820, via: "conduit");          // 构造消息：管道细节，不成步
        r.Edge(root, send, line: 40, pos: 805, via: "conduit");             // 分发调用本身就是那一步
        r.Edge(root, handle, line: 40, pos: 805, via: "dispatch:CreateOrderCommand", kind: EdgeKind.Dispatches);
        r.Edge(root, handler, line: 40, via: "handles", kind: EdgeKind.Dispatches); // handles 不要

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:external:IMediator.Send [1:dispatch:CreateOrderCommandHandler.Handle]", Shape(steps));
        Assert.False(steps[0].Expandable);
        Assert.True(steps[0].Children[0].Expandable);

        var noExt = r.Outline(root, externals: false).Steps;
        Assert.Equal("1:dispatch:CreateOrderCommandHandler.Handle", Shape(noExt));
        Assert.Equal(handle, noExt[0].Target!.Id);
        Assert.Equal(40, noExt[0].Line);
    }

    [Fact]
    public void Dispatch_WithoutCallOnItsLine_IsItsOwnStep_AndTreeSitterLineOffByOneStillAttaches()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var h1 = r.Method(r.Type("PingHandler"), "Handle");
        var h2 = r.Method(r.Type("PongHandler"), "Handle");
        r.Edge(root, r.Method(svc, "Prepare"), line: 50);
        r.Edge(root, h1, line: 51, via: "dispatch:Ping", kind: EdgeKind.Dispatches, conf: 0.4); // 这一行没有调用
        // tree-sitter：分派边记在 new Pong(...) 那一行（60），mediator.Send 在下一行且被标成 conduit
        r.Edge(root, r.External("Send"), line: 61, via: "conduit", conf: 0.5);
        r.Edge(root, h2, line: 60, via: "dispatch:Pong", kind: EdgeKind.Dispatches, conf: 0.4);

        Assert.Equal("1:call:Svc.Prepare | 2:dispatch:PingHandler.Handle | 3:external:Send [1:dispatch:PongHandler.Handle]", Shape(r.Outline(root).Steps));
    }

    [Fact]
    public void Ambiguous_LowConfidenceCandidatesAtSameSite_MergeIntoOneStep_LoneOneDropped()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var d1 = r.Method(r.Type("Dup1"), "helper2");
        var d2 = r.Method(r.Type("Dup2"), "helper2");
        r.Edge(root, d1, line: 70, pos: 900, conf: 0.3);
        r.Edge(root, d2, line: 70, pos: 900, conf: 0.3);
        r.Edge(root, r.Method(svc, "Alone"), line: 71, conf: 0.3); // 只有一个 0.3 候选：不到阈值
        r.Edge(root, r.Method(svc, "Weak"), line: 72, conf: 0.2);

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:ambiguous:2 个可能目标：helper2 [1:call:Dup1.helper2 | 2:call:Dup2.helper2]", Shape(steps));
        Assert.Null(steps[0].Target);
        Assert.False(steps[0].Expandable);
        Assert.Equal([d1, d2], steps[0].Children.Select(c => c.Target!.Id));
    }

    [Fact]
    public void External_LabelIsLastTwoSegments_NotExpandable_AndHiddenWhenExternalsOff()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        r.Edge(root, r.External("Microsoft.Extensions.Logging.ILogger.LogInformation(string, params object[])"), line: 80, conf: 0.5);
        r.Edge(root, r.External("System.Linq.Enumerable.Select<TSource, TResult>(IEnumerable<TSource>, Func<TSource, TResult>)"), line: 81);
        r.Edge(root, r.External("gorm.io/gorm.Where"), line: 82);
        r.Edge(root, r.External("Append"), line: 83);
        r.Edge(root, r.Method(svc, "Mine"), line: 84);

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:external:ILogger.LogInformation | 2:external:Enumerable.Select | 3:external:gorm.Where | 4:external:Append | 5:call:Svc.Mine", Shape(steps));
        Assert.All(steps.Take(4), s => Assert.False(s.Expandable));
        Assert.Equal("external:gorm.io/gorm.Where", steps[2].Target!.QualifiedName);

        Assert.Equal("1:call:Svc.Mine", Shape(r.Outline(root, externals: false).Steps));
    }

    [Fact]
    public void New_TypeTarget_LabelIsShortTypeName_UniqueConstructorBecomesTarget()
    {
        var (r, root, _) = Repo();
        using var __ = r;
        var order = r.Type("Order");
        var ctor = r.Method(order, "Order");
        r.Edge(ctor, r.Method(order, "Validate"), line: 400); // 构造函数体里有调用
        var box = r.Symbol(SymbolKind.Class, "Box", "App.Box<T>");
        r.Method(box, "Box"); r.Method(box, "Box"); // 两个重载：不知道是哪个，Target 留在类型上
        var put = r.Symbol(SymbolKind.Method, "Put", "App.Box<T>.Put(T)", box);
        r.Edge(root, order, line: 90, pos: 1000);
        r.Edge(root, box, line: 91, pos: 1100);
        r.Edge(root, put, line: 92, pos: 1200);

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:new:Order | 2:new:Box | 3:call:Box.Put", Shape(steps));
        Assert.Equal(ctor, steps[0].Target!.Id);
        Assert.True(steps[0].Expandable);
        Assert.Equal(box, steps[1].Target!.Id);
        Assert.False(steps[1].Expandable);
    }

    [Fact]
    public void Repeat_MergesOnlyAdjacentSameTargetSameCtx()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var a = r.Method(svc, "A");
        var b = r.Method(svc, "B");
        r.Edge(root, a, line: 10, ctx: CallContext.Loop);
        r.Edge(root, a, line: 11, ctx: CallContext.Loop);
        r.Edge(root, b, line: 12);
        r.Edge(root, a, line: 13);                       // 不相邻：照常重复
        r.Edge(root, a, line: 14, ctx: CallContext.Cond); // 相邻但 ctx 不同：不合并

        var steps = r.Outline(root).Steps;
        Assert.Equal("1:call:Svc.Ax2 | 2:call:Svc.B | 3:call:Svc.A | 4:call:Svc.A", Shape(steps));
        Assert.Equal(["loop", null, null, "cond"], steps.Select(s => s.Ctx));
        Assert.Equal(10, steps[0].Line); // 合并后的位置取第一次
    }

    [Fact]
    public void Tests_TargetsInTestCodeExcludedByDefault_KeptWithFlagOrWhenRootIsTest()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var fake = r.Method(r.Type("FakeClock", test: true), "Now", test: true);
        r.Edge(root, fake, line: 10);
        r.Edge(root, r.Method(svc, "Real"), line: 11);

        Assert.Equal("1:call:Svc.Real", Shape(r.Outline(root).Steps));
        Assert.Equal("1:call:FakeClock.Now | 2:call:Svc.Real", Shape(r.Outline(root, tests: true).Steps));

        var testRoot = r.Method(r.Type("AppTests", test: true), "Works", test: true);
        r.Edge(testRoot, fake, line: 20);
        Assert.Equal("1:call:FakeClock.Now", Shape(r.Outline(testRoot).Steps));
    }

    [Fact]
    public void Expandable_OnlyWhenTargetHasQualifyingOutgoingEdge()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var withCall = r.Method(svc, "WithCall");
        r.Edge(withCall, r.Method(svc, "Leaf0"), line: 500);
        var leaf = r.Method(svc, "Leaf");
        var onlyRefs = r.Method(svc, "OnlyRefs");
        r.Edge(onlyRefs, r.Type("Dto"), line: 510, kind: EdgeKind.References);
        var onlyWeak = r.Method(svc, "OnlyWeak");
        r.Edge(onlyWeak, r.Method(svc, "Guess"), line: 520, conf: 0.3);
        var onlyExternal = r.Method(svc, "OnlyExternal");
        r.Edge(onlyExternal, r.External("System.Console.WriteLine(string)"), line: 530);
        var onlyDispatch = r.Method(svc, "OnlyDispatch");
        r.Edge(onlyDispatch, r.Method(r.Type("XHandler"), "Handle"), line: 540, via: "dispatch:X", kind: EdgeKind.Dispatches);
        foreach (var (t, line) in new[] { (withCall, 10), (leaf, 11), (onlyRefs, 12), (onlyWeak, 13), (onlyExternal, 14), (onlyDispatch, 15) }) r.Edge(root, t, line);

        Assert.Equal([true, false, false, false, true, true], r.Outline(root).Steps.Select(s => s.Expandable));
        // 不要外部调用时，只调外部的方法展开后是空的：不能展开
        Assert.Equal([true, false, false, false, false, true], r.Outline(root, externals: false).Steps.Select(s => s.Expandable));
    }

    [Fact]
    public void Filter_DropsNonCallKinds_AndConduitConstruction()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        var dto = r.Type("Dto");
        r.Edge(root, dto, line: 10, kind: EdgeKind.References);
        r.Edge(root, dto, line: 11, kind: EdgeKind.Inherits);
        r.Edge(root, dto, line: 12, kind: EdgeKind.Implements);
        r.Edge(root, dto, line: 13, via: "conduit");
        r.Edge(root, r.Method(svc, "Kept"), line: 14);

        Assert.Equal("1:call:Svc.Kept", Shape(r.Outline(root).Steps));
    }

    [Fact]
    public void Truncate_AtMaxSteps()
    {
        var (r, root, svc) = Repo();
        using var __ = r;
        for (var i = 0; i < OutlineBuilder.MaxSteps + 5; i++) r.Edge(root, r.Method(svc, "M" + i), line: 10 + i);

        var o = r.Outline(root);
        Assert.True(o.Truncated);
        Assert.Equal(OutlineBuilder.MaxSteps, o.Steps.Count);
        Assert.Equal(OutlineBuilder.MaxSteps, o.Steps[^1].Order);
        Assert.Equal("Svc.M299", o.Steps[^1].Label);

        var (r2, root2, svc2) = Repo();
        using var ___ = r2;
        r2.Edge(root2, r2.Method(svc2, "Only"), line: 10);
        Assert.False(r2.Outline(root2).Truncated);
    }

    [Fact]
    public void UnknownSymbol_ReturnsNull()
    {
        using var r = new OutlineRepo();
        Assert.Null(OutlineQuery.Build(r.Db, 12345, new OutlineOptions()));
    }
}
