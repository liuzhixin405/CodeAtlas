using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class HeuristicEdgeResolverTests
{
    private sealed class Fixture : IDisposable
    {
        public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
        public IndexWriter Writer { get; }
        public IndexReader Reader { get; }
        public Fixture() { Writer = new IndexWriter(Db); Reader = new IndexReader(Db); }

        public (long FileId, IReadOnlyList<long> Ids) File(string path, string language, params (SymbolKind Kind, string Name, int? Parent)[] symbols)
        {
            var fileId = Writer.UpsertFile(path, language, "h", 1, FileStatus.Ok);
            var prefix = language == "csharp" ? "" : path + ":";
            var list = new List<ParsedSymbol> { new(SymbolKind.Module, System.IO.Path.GetFileName(path), path, null, 1, 1, 999, 1, null, null) };
            var line = 2;
            foreach (var (kind, name, parent) in symbols)
            {
                var parentIdx = parent ?? 0;
                var parentQ = parentIdx == 0 ? prefix : list[parentIdx].QualifiedName + ".";
                list.Add(new ParsedSymbol(kind, name, parentQ + name, null, line, 1, line + 1, 1, parentIdx, null));
                line += 2;
            }
            return (fileId, Writer.WriteSymbols(fileId, list, IndexSource.TreeSitter));
        }

        public async Task Resolve(params PendingReference[] refs)
        {
            await new HeuristicEdgeResolver().ResolveAsync(new ResolveContext(Db, refs), CancellationToken.None);
        }

        public List<(long? ToId, string ToName, double Confidence)> EdgesFrom(long fromId, EdgeKind kind) =>
            Reader.GetNeighbors(fromId, Direction.Outgoing, new HashSet<EdgeKind> { kind })
                .Select(n => (n.Edge.ToId, n.Edge.ToName, n.Edge.Confidence)).ToList();

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task SameFileUnique_Gets09()
    {
        using var f = new Fixture();
        var (fileId, ids) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null), (SymbolKind.Function, "helper", null));
        f.File("src/b.ts", "typescript", (SymbolKind.Function, "helper", null));

        await f.Resolve(new PendingReference(ids[1], fileId, "src/a.ts", "typescript", "helper", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(ids[1], EdgeKind.Calls);
        Assert.Single(edges);
        Assert.Equal((ids[2], "src/a.ts:helper", 0.9), edges[0]);
    }

    [Fact]
    public async Task ImportScopeUnique_PathImport_Gets07()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/app/main.ts", "typescript", (SymbolKind.Function, "run", null));
        var (_, bIds) = f.File("src/lib/svc.ts", "typescript", (SymbolKind.Class, "Svc", null));
        f.File("src/other/svc.ts", "typescript", (SymbolKind.Class, "Svc", null));
        f.Writer.WriteImports(aFile, [new ParsedImport("../lib/svc", true, 1)]);

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/app/main.ts", "typescript", "Svc", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls);
        Assert.Single(edges);
        Assert.Equal((bIds[1], "src/lib/svc.ts:Svc", 0.7), edges[0]);
    }

    [Fact]
    public async Task ImportScopeUnique_IndexFile_Resolves()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/main.ts", "typescript", (SymbolKind.Function, "run", null));
        var (_, bIds) = f.File("src/lib/index.ts", "typescript", (SymbolKind.Function, "boot", null));
        f.File("src/x/index.ts", "typescript", (SymbolKind.Function, "boot", null));
        f.Writer.WriteImports(aFile, [new ParsedImport("./lib", true, 1)]);

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/main.ts", "typescript", "boot", EdgeKind.Calls, 3));

        Assert.Equal((bIds[1], "src/lib/index.ts:boot", 0.7), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
    }

    [Fact]
    public async Task CSharp_UsingNamespace_AndOwnNamespace_AreImportScope()
    {
        using var f = new Fixture();
        var (svcFile, svcIds) = f.File("Svc.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Orders", null), (SymbolKind.Class, "OrderService", 1), (SymbolKind.Method, "Find", 2));
        var (_, repoIds) = f.File("Repo.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Repo", null), (SymbolKind.Class, "OrderRepo", 1));
        f.File("Other.cs", "csharp", (SymbolKind.Namespace, "Legacy", null), (SymbolKind.Class, "OrderRepo", 1));
        var (_, valIds) = f.File("Validator.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Orders", null), (SymbolKind.Class, "Validator", 1));
        f.File("Other2.cs", "csharp", (SymbolKind.Namespace, "Legacy", null), (SymbolKind.Class, "Validator", 1));
        f.Writer.WriteImports(svcFile, [new ParsedImport("Acme.Repo", false, 1)]);

        await f.Resolve(
            new PendingReference(svcIds[3], svcFile, "Svc.cs", "csharp", "OrderRepo", EdgeKind.Calls, 5),
            new PendingReference(svcIds[3], svcFile, "Svc.cs", "csharp", "Validator", EdgeKind.Calls, 6));

        var edges = f.EdgesFrom(svcIds[3], EdgeKind.Calls);
        Assert.Equal(2, edges.Count);
        Assert.Contains((repoIds[2], "Acme.Repo.OrderRepo", 0.7), edges);
        Assert.Contains((valIds[2], "Acme.Orders.Validator", 0.7), edges);
    }

    [Fact]
    public async Task RepoUnique_Gets05()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, bIds) = f.File("src/deep/b.ts", "typescript", (SymbolKind.Function, "helper", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "helper", EdgeKind.Calls, 3));

        Assert.Equal((bIds[1], "src/deep/b.ts:helper", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
    }

    [Fact]
    public async Task NoCandidate_CreatesExternal_05()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "fetch", EdgeKind.Calls, 3));

        var e = f.EdgesFrom(aIds[1], EdgeKind.Calls).Single();
        Assert.Equal(0.5, e.Confidence);
        Assert.Equal("external:fetch", e.ToName);
        Assert.Equal(SymbolKind.External, f.Reader.GetSymbol(e.ToId!.Value)!.Kind);
    }

    [Fact]
    public async Task Resolve_ManyCandidates_CapsAt20Edges()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        for (var i = 0; i < 25; i++)
            f.File($"src/h{i}.ts", "typescript", (SymbolKind.Function, "Handle", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Handle", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls);
        Assert.Equal(HeuristicEdgeResolver.MaxCandidates, edges.Count);
        Assert.All(edges, e => Assert.Equal(0.3, e.Confidence));
    }

    [Fact]
    public async Task MultipleCandidates_UsesNarrowestNonEmptyLevel_NotWholeRepo()
    {
        using var f = new Fixture();
        // import 范围内有两个同名候选（类 + 函数），仓库别处还有三个：应只对范围内的两个出 0.3 边
        var (aFile, aIds) = f.File("src/main.ts", "typescript", (SymbolKind.Function, "run", null));
        var (_, libIds) = f.File("src/lib.ts", "typescript", (SymbolKind.Class, "Handle", null), (SymbolKind.Function, "Handle", null));
        f.File("src/a1.ts", "typescript", (SymbolKind.Function, "Handle", null));
        f.File("src/a2.ts", "typescript", (SymbolKind.Function, "Handle", null));
        f.File("src/a3.ts", "typescript", (SymbolKind.Function, "Handle", null));
        f.Writer.WriteImports(aFile, [new ParsedImport("./lib", true, 1)]);

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/main.ts", "typescript", "Handle", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls);
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(0.3, e.Confidence));
        Assert.Contains(edges, e => e.ToId == libIds[1]);
        Assert.Contains(edges, e => e.ToId == libIds[2]);
    }

    [Fact]
    public async Task MultipleCandidates_SameFileOverloads_StayInFile()
    {
        using var f = new Fixture();
        // 同文件两个同名（C# 重载很常见），别的文件也有同名：只连同文件的两个
        var (aFile, aIds) = f.File("Svc.cs", "csharp",
            (SymbolKind.Class, "Svc", null), (SymbolKind.Method, "Find", 1), (SymbolKind.Method, "Find", 1), (SymbolKind.Method, "Run", 1));
        f.File("Other.cs", "csharp", (SymbolKind.Class, "Other", null), (SymbolKind.Method, "Find", 1));

        await f.Resolve(new PendingReference(aIds[4], aFile, "Svc.cs", "csharp", "Find", EdgeKind.Calls, 8));

        var edges = f.EdgesFrom(aIds[4], EdgeKind.Calls);
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(0.3, e.Confidence));
        Assert.All(edges, e => Assert.Contains(e.ToId!.Value, new[] { aIds[2], aIds[3] }));
    }

    [Fact]
    public async Task KindMismatch_IsNotACandidate()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Class, "C", null), (SymbolKind.Field, "count", 1), (SymbolKind.Method, "run", 1));

        await f.Resolve(new PendingReference(aIds[3], aFile, "src/a.ts", "typescript", "count", EdgeKind.Calls, 5));

        Assert.Equal("external:count", f.EdgesFrom(aIds[3], EdgeKind.Calls).Single().ToName);
    }

    [Fact]
    public async Task LanguageFamily_TsCanResolveIntoTsx_ButNotCSharp()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, tsxIds) = f.File("src/View.tsx", "tsx", (SymbolKind.Function, "View", null));
        f.File("View.cs", "csharp", (SymbolKind.Class, "View", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "View", EdgeKind.Calls, 3));

        Assert.Equal((tsxIds[1], "src/View.tsx:View", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
        Assert.Equal("js", HeuristicEdgeResolver.LanguageFamily("tsx"));
        Assert.Equal("csharp", HeuristicEdgeResolver.LanguageFamily("csharp"));
    }

    [Fact]
    public async Task Imports_PathResolvesToModule_NamespaceResolvesToNamespaces_UnknownToExternal()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, bIds) = f.File("src/b.ts", "typescript", (SymbolKind.Function, "x", null));
        var (csFile, csIds) = f.File("Svc.cs", "csharp", (SymbolKind.Namespace, "Acme.Orders", null));
        var (_, ns1) = f.File("N1.cs", "csharp", (SymbolKind.Namespace, "Acme.Repo", null));
        var (_, ns2) = f.File("N2.cs", "csharp", (SymbolKind.Namespace, "Acme.Repo", null));

        await f.Resolve(
            new PendingReference(aIds[0], aFile, "src/a.ts", "typescript", "./b", EdgeKind.Imports, 1),
            new PendingReference(aIds[0], aFile, "src/a.ts", "typescript", "react", EdgeKind.Imports, 2),
            new PendingReference(csIds[0], csFile, "Svc.cs", "csharp", "Acme.Repo", EdgeKind.Imports, 1));

        var tsImports = f.EdgesFrom(aIds[0], EdgeKind.Imports);
        Assert.Contains((bIds[0], "src/b.ts", 1.0), tsImports);
        Assert.Contains(tsImports, e => e.ToName == "external:react" && e.Confidence == 0.5);
        var csImports = f.EdgesFrom(csIds[0], EdgeKind.Imports);
        Assert.Equal(2, csImports.Count);
        Assert.Contains((ns1[1], "Acme.Repo", 1.0), csImports);
        Assert.Contains((ns2[1], "Acme.Repo", 1.0), csImports);
    }

    [Fact]
    public async Task Dispatch_MessageArgument_LinksToHandlerMethod_ViaHandlesEdge()
    {
        using var f = new Fixture();
        var (ctlFile, ctl) = f.File("Ctl.cs", "csharp", (SymbolKind.Class, "Ctl", null), (SymbolKind.Method, "List", 1));
        var (_, q) = f.File("ListQuery.cs", "csharp", (SymbolKind.Class, "ListQuery", null));
        var (hFile, h) = f.File("Handler.cs", "csharp", (SymbolKind.Class, "ListQueryHandler", null), (SymbolKind.Method, "HandleAsync", 1), (SymbolKind.Method, "Helper", 1));

        await f.Resolve(
            new PendingReference(h[1], hFile, "Handler.cs", "csharp", "ListQuery", EdgeKind.Dispatches, 2, Via: "handles"),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "QueryAsync", EdgeKind.Calls, 9, Pos: 100),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "ListQuery", EdgeKind.Calls, 9, Via: "dispatch-site:100", Pos: 110));

        // 1. handles 边：Handler 类 → Query 类
        var handles = f.Reader.GetNeighbors(h[1], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Dispatches }).Single();
        Assert.Equal(q[1], handles.Other!.Id);
        Assert.Equal("handles", handles.Edge.Via);

        // 2. 调用点 → HandleAsync 的 dispatches 边，证据行是 new ListQuery 那一行
        var dispatch = f.Reader.GetNeighbors(ctl[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Dispatches }).Single();
        Assert.Equal(h[2], dispatch.Other!.Id);
        Assert.Equal("dispatch:ListQuery", dispatch.Edge.Via);
        Assert.Equal(0.4, dispatch.Edge.Confidence);
        Assert.Equal(9, dispatch.Edge.EvidenceLine);
        // 原来的构造调用边仍在
        Assert.Contains(f.Reader.GetNeighbors(ctl[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }), n => n.Other?.Id == q[1]);
    }

    [Fact]
    public async Task Dispatch_ConduitCalls_AreMarked_AndDispatchEdgeNamesTheMessageType()
    {
        using var f = new Fixture();
        // => dispatcher.QueryAsync(new ListQuery(id), ct)  —— QueryAsync 与 new ListQuery 在同一行
        var (ctlFile, ctl) = f.File("Ctl.cs", "csharp", (SymbolKind.Class, "Ctl", null), (SymbolKind.Method, "List", 1));
        var (_, disp) = f.File("IDispatcher.cs", "csharp", (SymbolKind.Interface, "IDispatcher", null), (SymbolKind.Method, "QueryAsync", 1));
        var (_, q) = f.File("ListQuery.cs", "csharp", (SymbolKind.Class, "ListQuery", null));
        var (hFile, h) = f.File("Handler.cs", "csharp", (SymbolKind.Class, "ListQueryHandler", null), (SymbolKind.Method, "HandleAsync", 1));

        await f.Resolve(
            new PendingReference(h[1], hFile, "Handler.cs", "csharp", "ListQuery", EdgeKind.Dispatches, 2, Via: "handles"),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "QueryAsync", EdgeKind.Calls, 9, Pos: 100),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "ListQuery", EdgeKind.Calls, 9, Via: "dispatch-site:100", Pos: 110));

        var outgoing = f.Reader.GetNeighbors(ctl[2], Direction.Outgoing);
        var toDispatcher = outgoing.Single(n => n.Other?.Id == disp[2]);
        var toCtor = outgoing.Single(n => n.Other?.Id == q[1]);
        var toHandler = outgoing.Single(n => n.Other?.Id == h[2]);
        Assert.Equal("conduit", toDispatcher.Edge.Via);   // 管道方法调用
        Assert.Equal("conduit", toCtor.Edge.Via);         // 消息类型的构造
        Assert.Equal("dispatch:ListQuery", toHandler.Edge.Via); // 带消息类型名，页面用来标注
    }

    [Fact]
    public async Task Dispatch_UnrelatedCallOnSameLine_IsNotConduit()
    {
        using var f = new Fixture();
        var (ctlFile, ctl) = f.File("Ctl.cs", "csharp", (SymbolKind.Class, "Ctl", null), (SymbolKind.Method, "List", 1), (SymbolKind.Method, "Log", 1));
        f.File("ListQuery.cs", "csharp", (SymbolKind.Class, "ListQuery", null));
        var (hFile, h) = f.File("Handler.cs", "csharp", (SymbolKind.Class, "ListQueryHandler", null), (SymbolKind.Method, "HandleAsync", 1));

        await f.Resolve(
            new PendingReference(h[1], hFile, "Handler.cs", "csharp", "ListQuery", EdgeKind.Dispatches, 2, Via: "handles"),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "Log", EdgeKind.Calls, 9),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "ListQuery", EdgeKind.Calls, 9));

        var toLog = f.Reader.GetNeighbors(ctl[2], Direction.Outgoing).Single(n => n.Other?.Id == ctl[3]);
        Assert.Null(toLog.Edge.Via); // Log 不是分发方法名，不折叠
        Assert.Empty(f.Reader.GetNeighbors(ctl[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Dispatches }));
    }

    [Fact]
    public async Task Dispatch_HandlerWithoutHandleMethod_FallsBackToClass()
    {
        using var f = new Fixture();
        var (ctlFile, ctl) = f.File("Ctl.cs", "csharp", (SymbolKind.Class, "Ctl", null), (SymbolKind.Method, "Run", 1));
        f.File("Cmd.cs", "csharp", (SymbolKind.Class, "Cmd", null));
        var (hFile, h) = f.File("H.cs", "csharp", (SymbolKind.Class, "CmdConsumer", null));

        await f.Resolve(
            new PendingReference(h[1], hFile, "H.cs", "csharp", "Cmd", EdgeKind.Dispatches, 2, Via: "handles"),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "Send", EdgeKind.Calls, 5, Pos: 50),
            new PendingReference(ctl[2], ctlFile, "Ctl.cs", "csharp", "Cmd", EdgeKind.Calls, 5, Via: "dispatch-site:50", Pos: 60));

        var dispatch = f.Reader.GetNeighbors(ctl[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Dispatches }).Single();
        Assert.Equal(h[1], dispatch.Other!.Id);
    }

    [Fact]
    public async Task Calls_InterfaceMethodAndImplementationBothMatch_PrefersImplementation()
    {
        using var f = new Fixture();
        // repo.ListAsync(...)：IFeedRepository.ListAsync（接口）与 FeedRepository.ListAsync（类）同名，连到类
        var (ctlFile, ctl) = f.File("Svc.cs", "csharp", (SymbolKind.Class, "Svc", null), (SymbolKind.Method, "Run", 1));
        f.File("IFeedRepository.cs", "csharp", (SymbolKind.Interface, "IFeedRepository", null), (SymbolKind.Method, "ListAsync", 1));
        var (_, impl) = f.File("FeedRepository.cs", "csharp", (SymbolKind.Class, "FeedRepository", null), (SymbolKind.Method, "ListAsync", 1));

        await f.Resolve(new PendingReference(ctl[2], ctlFile, "Svc.cs", "csharp", "ListAsync", EdgeKind.Calls, 4));

        var e = f.EdgesFrom(ctl[2], EdgeKind.Calls).Single();
        Assert.Equal(impl[2], e.ToId);
        Assert.Equal(0.5, e.Confidence);
    }

    [Fact]
    public async Task Calls_TwoImplementationsAndOneInterface_StillAmbiguousBetweenImplementations()
    {
        using var f = new Fixture();
        var (ctlFile, ctl) = f.File("Svc.cs", "csharp", (SymbolKind.Class, "Svc", null), (SymbolKind.Method, "Run", 1));
        f.File("IRepo.cs", "csharp", (SymbolKind.Interface, "IRepo", null), (SymbolKind.Method, "ListAsync", 1));
        var (_, a) = f.File("RepoA.cs", "csharp", (SymbolKind.Class, "RepoA", null), (SymbolKind.Method, "ListAsync", 1));
        var (_, b) = f.File("RepoB.cs", "csharp", (SymbolKind.Class, "RepoB", null), (SymbolKind.Method, "ListAsync", 1));

        await f.Resolve(new PendingReference(ctl[2], ctlFile, "Svc.cs", "csharp", "ListAsync", EdgeKind.Calls, 4));

        var edges = f.EdgesFrom(ctl[2], EdgeKind.Calls);
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal(0.3, e.Confidence));
        Assert.Equal(new[] { a[2], b[2] }.OrderBy(x => x), edges.Select(e => e.ToId!.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task Calls_CommonBclName_OnlyResolvesWithinFileOrImportScope()
    {
        using var f = new Fixture();
        // 仓库里恰好有一个叫 ToList 的扩展方法；别处调用 .ToList() 大概率是 System.Linq，不应连过去
        var (ctlFile, ctl) = f.File("Svc.cs", "csharp", (SymbolKind.Namespace, "Acme.App", null), (SymbolKind.Class, "Svc", 1), (SymbolKind.Method, "Run", 2));
        var (_, ext) = f.File("Ext.cs", "csharp", (SymbolKind.Namespace, "Acme.Util", null), (SymbolKind.Class, "Ext", 1), (SymbolKind.Method, "ToList", 2));
        var (_, same) = f.File("Local.cs", "csharp", (SymbolKind.Namespace, "Acme.App", null), (SymbolKind.Class, "Local", 1), (SymbolKind.Method, "Where", 2));

        await f.Resolve(
            new PendingReference(ctl[3], ctlFile, "Svc.cs", "csharp", "ToList", EdgeKind.Calls, 4),
            new PendingReference(ctl[3], ctlFile, "Svc.cs", "csharp", "Where", EdgeKind.Calls, 5));

        var edges = f.EdgesFrom(ctl[3], EdgeKind.Calls);
        Assert.Contains(edges, e => e.ToName == "external:ToList" && e.Confidence == 0.5);   // 全仓库唯一但名字太常见 → 外部
        Assert.Contains(edges, e => e.ToId == same[3] && e.Confidence == 0.7);               // 同命名空间（import 范围）命中仍然保留
        Assert.DoesNotContain(edges, e => e.ToId == ext[3]);
    }

    [Fact]
    public async Task Script_UnboundCall_IsExternal_NotRepoUniqueGuess_WhileLegacyReferenceStillGuesses()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        f.File("src/app/error.tsx", "tsx", (SymbolKind.Function, "Error", null));

        await f.Resolve(
            new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Error", EdgeKind.Calls, 3, Script: new ScriptBinding(ScriptReceiver.None)),
            new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Error", EdgeKind.Calls, 4)); // 没有绑定信息：照旧按全仓唯一

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls).OrderBy(e => e.Confidence).ToList();
        Assert.Equal(2, edges.Count);
        Assert.Equal(("external:Error", 0.5), (edges[0].ToName, edges[0].Confidence));
        Assert.Equal(("src/app/error.tsx:Error", 0.5), (edges[1].ToName, edges[1].Confidence));
        Assert.NotEqual(edges[0].ToId, edges[1].ToId);
    }

    [Fact]
    public async Task Script_NamedImportThroughTsconfigAlias_ResolvesToExport_095()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("web/src/page.tsx", "tsx", (SymbolKind.Function, "Page", null));
        var (_, hook) = f.File("web/src/hooks/useG.ts", "typescript", (SymbolKind.Function, "useG", null));
        f.File("web/src/legacy/useG.ts", "typescript", (SymbolKind.Function, "useG", null));
        var configs = new[] { new ScriptPathConfig("web", null, [new("@/*", new[] { "web/src/*" })]) };

        await new HeuristicEdgeResolver().ResolveAsync(new ResolveContext(f.Db,
        [
            new PendingReference(aIds[1], aFile, "web/src/page.tsx", "tsx", "useG", EdgeKind.Calls, 3, Script: new ScriptBinding(ScriptReceiver.None, "@/hooks/useG", "useG")),
            new PendingReference(aIds[0], aFile, "web/src/page.tsx", "tsx", "@/hooks/useG", EdgeKind.Imports, 1),
        ], configs), CancellationToken.None);

        Assert.Equal((hook[1], "web/src/hooks/useG.ts:useG", 0.95), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
        Assert.Equal((hook[0], "web/src/hooks/useG.ts", 1.0), f.EdgesFrom(aIds[0], EdgeKind.Imports).Single());
    }

    [Fact]
    public async Task Inherits_PrefersTypes_NotMethods()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Class, "Child", null), (SymbolKind.Method, "Base", 1));
        var (_, bIds) = f.File("src/b.ts", "typescript", (SymbolKind.Class, "Base", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Base", EdgeKind.Inherits, 2));

        Assert.Equal((bIds[1], "src/b.ts:Base", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Inherits).Single());
    }
}
