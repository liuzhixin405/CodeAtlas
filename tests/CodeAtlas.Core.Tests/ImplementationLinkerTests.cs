using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class ImplementationLinkerTests
{
    private static ParsedSymbol Module(string path) => new(SymbolKind.Module, Path.GetFileName(path), path, null, 1, 1, 200, 1, null, null);

    /// <summary>Caller.Run 调接口 IRepo.Find(int)；implCount 个实现类各自 implements 该接口方法。</summary>
    private static (IndexDatabase Db, long Caller, long IfaceMethod, List<long> Impls) Seed(int implCount)
    {
        var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var f = w.UpsertFile("A.cs", "csharp", "h", 1, FileStatus.Ok);
        var ids = w.WriteSymbols(f,
        [
            Module("A.cs"),
            new(SymbolKind.Interface, "IRepo", "IRepo", null, 2, 1, 5, 1, 0, "public"),
            new(SymbolKind.Method, "Find", "IRepo.Find(int)", null, 3, 5, 3, 30, 1, "public"),
            new(SymbolKind.Class, "Caller", "Caller", null, 10, 1, 15, 1, 0, "public"),
            new(SymbolKind.Method, "Run", "Caller.Run()", null, 11, 5, 11, 30, 3, "public"),
        ], IndexSource.Roslyn);
        var impls = new List<long>();
        var edges = new List<NewEdge> { new(ids[4], ids[2], "IRepo.Find(int)", EdgeKind.Calls, IndexSource.Roslyn, 1.0, f, 12) };
        for (var i = 0; i < implCount; i++)
        {
            var line = 20 + i * 10;
            var implIds = w.WriteSymbols(f,
            [
                new(SymbolKind.Class, $"Repo{i}", $"Repo{i}", null, line, 1, line + 5, 1, null, "public"),
                new(SymbolKind.Method, "Find", $"Repo{i}.Find(int)", null, line + 1, 5, line + 1, 30, 0, "public"),
            ], IndexSource.Roslyn);
            impls.Add(implIds[1]);
            edges.Add(new NewEdge(implIds[0], ids[1], "IRepo", EdgeKind.Implements, IndexSource.Roslyn, 1.0, f, line));
            edges.Add(new NewEdge(implIds[1], ids[2], "IRepo.Find(int)", EdgeKind.Implements, IndexSource.Roslyn, 1.0, f, line + 1));
        }
        w.WriteEdges(edges);
        return (db, ids[4], ids[2], impls);
    }

    private static List<Neighbor> CallsFrom(IndexDatabase db, long id) =>
        new IndexReader(db).GetNeighbors(id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).ToList();

    [Fact]
    public void SingleImplementation_AddsHighConfidenceEdgeToImpl_KeepsInterfaceEdge()
    {
        var (db, caller, iface, impls) = Seed(1);
        using (db)
        {
            var added = new ImplementationLinker(db).Link();
            Assert.Equal(1, added);
            var calls = CallsFrom(db, caller);
            Assert.Equal(2, calls.Count);
            Assert.Contains(calls, n => n.Other!.Id == iface && n.Edge.Confidence == 1.0 && n.Edge.Via is null);
            var impl = Assert.Single(calls, n => n.Other!.Id == impls[0]);
            Assert.Equal(0.9, impl.Edge.Confidence);
            Assert.Equal("impl", impl.Edge.Via);
            Assert.Equal(12, impl.Edge.Line);
            Assert.Equal(IndexSource.Roslyn, impl.Edge.Source);
        }
    }

    [Fact]
    public void MultipleImplementations_AddsCandidateEdges_AtHalfConfidence()
    {
        var (db, caller, _, impls) = Seed(3);
        using (db)
        {
            Assert.Equal(3, new ImplementationLinker(db).Link());
            var implEdges = CallsFrom(db, caller).Where(n => n.Edge.Via == "impl").ToList();
            Assert.Equal(impls.OrderBy(x => x), implEdges.Select(n => n.Other!.Id).OrderBy(x => x));
            Assert.All(implEdges, n => Assert.Equal(0.5, n.Edge.Confidence));
        }
    }

    [Fact]
    public void Link_IsIdempotent_AndIgnoresCallsToNonInterfaceMembers()
    {
        var (db, caller, _, impls) = Seed(1);
        using (db)
        {
            var w = new IndexWriter(db);
            // 直接调实现类的方法：不是接口成员，不派生
            w.WriteEdges([new NewEdge(caller, impls[0], "Repo0.Find(int)", EdgeKind.Calls, IndexSource.Roslyn, 1.0, 1, 13)]);
            var linker = new ImplementationLinker(db);
            Assert.Equal(1, linker.Link());
            Assert.Equal(0, linker.Link()); // 再跑一遍不重复
            Assert.Equal(3, CallsFrom(db, caller).Count); // 接口 + 直接调用 + 1 条派生
        }
    }
}
