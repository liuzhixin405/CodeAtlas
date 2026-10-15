using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Server.Tests;

/// <summary>
/// 快速跳转的内存库：符号逐个插入（带父子关系、显式行号），边用 IndexWriter 写。产品文件 src/App/App.cs 真的写到临时目录（判 Stale 要读磁盘），
/// 测试文件 tests/App.Tests/AppTests.cs 只在库里。
/// </summary>
internal sealed class LensRepo : IDisposable
{
    public const string AppPath = "src/App/App.cs";
    public const string TestPath = "tests/App.Tests/AppTests.cs";

    public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-lens-" + Guid.NewGuid().ToString("N"));
    public ProjectRoots Roots { get; }
    public string AppFullPath => Path.Combine(Root, "src", "App", "App.cs");
    private readonly IndexWriter _w;
    private readonly long _file;
    private readonly long _testFile;
    private int _line = 1;

    public LensRepo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppFullPath)!);
        var content = "namespace App; class Svc { }\n"u8.ToArray();
        File.WriteAllBytes(AppFullPath, content);
        Roots = ProjectRoots.Single(Root);
        _w = new IndexWriter(Db);
        _file = _w.UpsertFile(AppPath, "csharp", FileHasher.Sha256Hex(content), content.Length, FileStatus.Ok);
        _testFile = _w.UpsertFile(TestPath, "csharp", "h", 1, FileStatus.Ok);
    }

    public long Symbol(SymbolKind kind, string name, string qualifiedName, long? parent = null, bool test = false, int? line = null)
    {
        var start = line ?? (_line += 10);
        return Db.Scalar<long>("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, parent_id, source)
            VALUES ($f, $k, $n, $q, $l, 0, $e, 0, $p, 'roslyn') RETURNING id
            """, ("$f", test ? _testFile : _file), ("$k", DbNames.ToDb(kind)), ("$n", name), ("$q", qualifiedName), ("$l", start), ("$e", start + 8), ("$p", parent));
    }

    public long Type(string name, SymbolKind kind = SymbolKind.Class, bool test = false, int? line = null) => Symbol(kind, name, "App." + name, test: test, line: line);

    public long Method(long type, string name, bool test = false, int? line = null, SymbolKind kind = SymbolKind.Method)
    {
        var typeQName = Db.Query<string>("SELECT qualified_name FROM symbols WHERE id = $id", ("$id", type)).Single();
        return Symbol(kind, name, typeQName + "." + name + "()", type, test, line);
    }

    public long External(string name) => _w.GetOrCreateExternal(name);

    public void Edge(long from, long to, int line, int? pos = null, double conf = 1.0, string? via = null, EdgeKind kind = EdgeKind.Calls, bool test = false) =>
        _w.WriteEdges([new NewEdge(from, to, "x", kind, IndexSource.Roslyn, conf, test ? _testFile : _file, line, via, Pos: pos)]);

    public void Entry(long symbol) =>
        Db.Execute("INSERT INTO entry_points (symbol_id, container_key, rule_id, kind, file, line) VALUES ($s, 'app', 'test-rule', 'http', $f, 1)",
            ("$s", symbol), ("$f", AppPath));

    public FileLensDto Lens(string relative = AppPath) => LensQuery.Build(Db, Roots, relative, relative);

    public SymbolLensDto Of(long id) => Lens().Symbols.Single(s => s.Symbol.Id == id);

    public void Dispose()
    {
        Db.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

public class LensTests
{
    /// <summary>Host.Run / Host.Other 调 Svc.Find。</summary>
    private static (LensRepo R, long Host, long Svc, long Run, long Other, long Find) Repo()
    {
        var r = new LensRepo();
        var host = r.Type("Host");
        var run = r.Method(host, "Run");
        var other = r.Method(host, "Other");
        var svc = r.Type("Svc");
        var find = r.Method(svc, "Find");
        return (r, host, svc, run, other, find);
    }

    [Fact]
    public void Callers_CountsDistinctCallers_CallSitesCountsEverySite()
    {
        var (r, _, _, run, other, find) = Repo();
        using var _ = r;
        r.Edge(run, find, line: 12, pos: 100);
        r.Edge(run, find, line: 15, pos: 200);   // 同一调用方第二处：Callers 不加，CallSites 加
        r.Edge(other, find, line: 22, pos: 300);

        var lens = r.Of(find);
        Assert.Equal(2, lens.Callers);
        Assert.Equal(3, lens.CallSites);
        Assert.Equal(0, lens.TestCallers);
        Assert.Equal(1, r.Of(run).Callees);    // 两处相邻、同一目标：大纲合成一步（Repeat 2）
    }

    [Fact]
    public void Callers_FollowImpactRules_ConfidenceConduitImplRecursion()
    {
        var (r, host, _, run, other, find) = Repo();
        using var _ = r;
        var third = r.Method(host, "Third");
        var fourth = r.Method(host, "Fourth");
        r.Edge(run, find, line: 12, conf: 0.3);                // 0.3 档多候选：不到 0.4
        r.Edge(other, find, line: 22, via: "conduit");         // conduit：构造消息那一步，不是调用
        r.Edge(third, find, line: 32, via: "impl", conf: 0.9); // 接口调用派生到实现：算
        r.Edge(find, find, line: 52);                          // 递归：不算
        r.Edge(fourth, find, line: 42, conf: 0.4);             // 正好 0.4：算

        var lens = r.Of(find);
        Assert.Equal(2, lens.Callers);
        Assert.Equal(2, lens.CallSites);
    }

    [Fact]
    public void Callers_Dispatch_CountsDispatchViaOnly()
    {
        var r = new LensRepo();
        using var _ = r;
        var msg = r.Type("Msg");
        var handler = r.Type("Handler");
        var handle = r.Method(handler, "Handle");
        var host = r.Type("Host");
        var run = r.Method(host, "Run");
        var other = r.Method(host, "Other");
        r.Edge(run, handle, line: 5, kind: EdgeKind.Dispatches, via: "dispatch:App.Msg");
        r.Edge(other, handle, line: 6, kind: EdgeKind.Dispatches, via: "handles"); // 声明不是调用
        r.Edge(handler, msg, line: 3, kind: EdgeKind.Dispatches, via: "handles");

        Assert.Equal(1, r.Of(handle).Callers);
        Assert.Equal(1, r.Of(handle).CallSites);
    }

    [Fact]
    public void Callers_Constructor_CountsNewOfItsType()
    {
        var (r, _, svc, run, other, _) = Repo();
        using var _ = r;
        var ctor = r.Method(svc, "Svc");
        r.Edge(run, svc, line: 12);    // new Svc()：边落在类型上
        r.Edge(other, ctor, line: 22); // Roslyn 直接绑到构造函数
        var lens = r.Of(ctor);
        Assert.Equal(2, lens.Callers);
        Assert.Equal(2, lens.CallSites);
    }

    [Fact]
    public void Callers_ThroughInterfaceMethod_CountsAsImpl()
    {
        var (r, _, _, run, _, find) = Repo();
        using var _ = r;
        var iface = r.Type("ISvc", SymbolKind.Interface);
        var ifind = r.Method(iface, "Find");
        r.Edge(find, ifind, line: 40, kind: EdgeKind.Implements); // Roslyn 的方法级 implements
        r.Edge(run, ifind, line: 12);                             // 调的是接口方法，没有派生 impl 边（多实现 > 20 / 直接绑到接口）

        Assert.Equal(1, r.Of(find).Callers);
        Assert.Equal(1, r.Of(ifind).Callers);
    }

    [Fact]
    public void TestCode_CountsOnlyInTestCallers()
    {
        var (r, _, _, run, _, find) = Repo();
        using var _ = r;
        var tests = r.Type("HostTests", test: true);
        var t1 = r.Method(tests, "Finds", test: true);
        var t2 = r.Method(tests, "FindsTwice", test: true);
        r.Edge(t1, find, line: 3, test: true);
        r.Edge(t2, find, line: 4, test: true);
        r.Edge(t2, find, line: 5, test: true);
        r.Edge(run, find, line: 12);

        var lens = r.Of(find);
        Assert.Equal(1, lens.Callers);
        Assert.Equal(1, lens.CallSites);
        Assert.Equal(2, lens.TestCallers);
    }

    [Fact]
    public void Callees_EqualsOutlineStepCount_ForEveryMethod()
    {
        var (r, host, svc, run, other, find) = Repo();
        using var _ = r;
        var iface = r.Type("ISvc", SymbolKind.Interface);
        var ifind = r.Method(iface, "Find");
        var save = r.Method(svc, "Save");
        var helperTests = r.Type("Helpers", test: true);
        var testHelper = r.Method(helperTests, "Fake", test: true);
        var dup1 = r.Method(r.Type("Dup1"), "Go");
        var dup2 = r.Method(r.Type("Dup2"), "Go");
        var log = r.External("external:Microsoft.Extensions.Logging.ILogger.LogInformation(string)");
        var send = r.External("external:MediatR.IMediator.Send(object)");
        var msg = r.Type("Msg");
        var handle = r.Method(r.Type("Handler"), "Handle");

        // Run：相邻同目标合并、外部调用、接口调用挂 impl、0.3 多候选合成 ambiguous、conduit + dispatch、落到测试代码的目标（默认去掉）
        r.Edge(run, find, line: 12, pos: 100);
        r.Edge(run, find, line: 13, pos: 110);
        r.Edge(run, log, line: 14, pos: 120);
        r.Edge(run, ifind, line: 15, pos: 130);
        r.Edge(run, find, line: 15, pos: 130, via: "impl", conf: 0.9);
        r.Edge(run, dup1, line: 16, pos: 140, conf: 0.3);
        r.Edge(run, dup2, line: 16, pos: 140, conf: 0.3);
        r.Edge(run, msg, line: 17, pos: 150, via: "conduit");
        r.Edge(run, send, line: 17, pos: 155, via: "conduit");
        r.Edge(run, handle, line: 17, pos: 155, kind: EdgeKind.Dispatches, via: "dispatch:App.Msg");
        r.Edge(run, testHelper, line: 18, pos: 160);
        r.Edge(run, save, line: 19, pos: 170);
        // Other：没有 pos 的边、单个 0.3（不到阈值）
        r.Edge(other, save, line: 22);
        r.Edge(other, find, line: 23);
        r.Edge(other, save, line: 24);
        r.Edge(other, dup1, line: 25, conf: 0.3);
        r.Edge(find, save, line: 32);

        var lens = r.Lens();
        Assert.NotEmpty(lens.Symbols);
        foreach (var s in lens.Symbols)
        {
            var outline = OutlineQuery.Build(r.Db, s.Symbol.Id, new OutlineOptions())!;
            Assert.Equal(outline.Steps.Count, s.Callees);
        }
        Assert.Equal(6, r.Of(run).Callees);  // Find x2 | LogInformation | ISvc.Find[impl] | ambiguous | Send[dispatch] | Save
        Assert.Equal(3, r.Of(other).Callees);
        Assert.Equal(0, r.Of(save).Callees);
    }

    [Fact]
    public void Counts_MatchImpactQuery_ForEveryMethod()
    {
        // 主线整合核对：与影响范围接口（ImpactQuery.Build）的直接调用方逐个比
        var (r, host, svc, run, other, find) = Repo();
        using var _ = r;
        var ctor = r.Method(svc, "Svc");
        var iface = r.Type("ISvc", SymbolKind.Interface);
        var ifind = r.Method(iface, "Find");
        var tests = r.Type("HostTests", test: true);
        var t1 = r.Method(tests, "Finds", test: true);
        r.Edge(find, ifind, line: 40, kind: EdgeKind.Implements);
        r.Edge(run, ifind, line: 12, pos: 10);
        r.Edge(run, find, line: 13, pos: 20);
        r.Edge(other, find, line: 22, pos: 30);
        r.Edge(other, find, line: 23, pos: 40);
        r.Edge(other, svc, line: 24, pos: 50);
        r.Edge(t1, find, line: 3, test: true);
        r.Edge(t1, run, line: 4, test: true);
        r.Edge(run, other, line: 14, pos: 60, conf: 0.3);

        foreach (var s in r.Lens().Symbols)
        {
            var impact = ImpactQuery.Build(r.Db, s.Symbol.Id, new ImpactOptions(Entries: false))!;
            Assert.Equal(impact.Callers.Select(c => c.Caller.Id).Distinct().Count(), s.Callers);
            Assert.Equal(impact.Callers.Sum(c => c.Sites.Count), s.CallSites);
            Assert.Equal(impact.TestCallers, s.TestCallers);
        }
        Assert.Equal(2, r.Of(find).Callers);
        Assert.Equal(1, r.Of(ctor).Callers);
    }

    [Fact]
    public void IsEntry_FromEntryPoints()
    {
        var (r, _, _, run, other, _) = Repo();
        using var _ = r;
        r.Entry(run);
        Assert.True(r.Of(run).IsEntry);
        Assert.False(r.Of(other).IsEntry);
    }

    [Fact]
    public void Symbols_OnlyMethodsFunctionsConstructors_SortedByStartLine()
    {
        var r = new LensRepo();
        using var _ = r;
        var svc = r.Type("Svc", line: 1);
        var later = r.Method(svc, "Later", line: 50);
        var ctor = r.Method(svc, "Svc", line: 5);
        var run = r.Method(svc, "Run", line: 20);
        var local = r.Method(run, "inner", line: 22, kind: SymbolKind.Function); // 局部函数：父符号是方法
        r.Symbol(SymbolKind.Property, "Name", "App.Svc.Name", svc, line: 10);
        r.Symbol(SymbolKind.Field, "_x", "App.Svc._x", svc, line: 11);
        var fn = r.Symbol(SymbolKind.Function, "topLevel", "App.topLevel", line: 60);
        var tests = r.Type("HostTests", test: true);
        r.Method(tests, "InOtherFile", test: true, line: 3);

        var lens = r.Lens();
        Assert.Equal(LensRepo.AppPath, lens.Path);
        Assert.Equal([ctor, run, local, later, fn], lens.Symbols.Select(s => s.Symbol.Id));
        Assert.All(lens.Symbols, s => Assert.Equal(LensRepo.AppPath, s.Symbol.Path));
        Assert.Equal("method", lens.Symbols[0].Symbol.Kind);
        Assert.Equal("function", lens.Symbols[2].Symbol.Kind);
        Assert.All(lens.Symbols, s => Assert.Equal(0, s.Callers + s.CallSites + s.TestCallers + s.Callees));
    }

    [Fact]
    public void NotIndexed_ReturnsEmpty()
    {
        var r = new LensRepo();
        using var _ = r;
        var lens = r.Lens("src/App/Missing.cs");
        Assert.Equal("src/App/Missing.cs", lens.Path);
        Assert.Empty(lens.Symbols);
        Assert.False(lens.Stale);

        // 绝对路径不在任何根下：规范化得 null，原样回给调用方
        var outside = LensQuery.Build(r.Db, r.Roots, @"C:\elsewhere\x.cs", null);
        Assert.Equal("C:/elsewhere/x.cs", outside.Path);
        Assert.Empty(outside.Symbols);
    }

    [Fact]
    public void Stale_ComparesDiskHashWithIndex()
    {
        var (r, _, _, _, _, _) = Repo();
        using var _ = r;
        Assert.False(r.Lens().Stale);
        File.AppendAllText(r.AppFullPath, "// edited\n");
        var stale = r.Lens();
        Assert.True(stale.Stale);
        Assert.NotEmpty(stale.Symbols); // 过期也照样给计数，编辑器自己决定怎么显示
        File.Delete(r.AppFullPath);
        Assert.True(r.Lens().Stale);
    }
}
