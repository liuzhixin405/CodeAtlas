using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexReaderTests
{
    private static (IndexDatabase Db, long FileA, long FileB, IReadOnlyList<long> A, IReadOnlyList<long> B) Seed()
    {
        var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/order/OrderService.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/repo.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a,
        [
            new(SymbolKind.Module, "OrderService.ts", "src/order/OrderService.ts", null, 1, 1, 50, 1, null, null),
            new(SymbolKind.Class, "OrderService", "src/order/OrderService.ts:OrderService", "class OrderService", 2, 1, 40, 2, 0, "public"),
            new(SymbolKind.Method, "findOrder", "src/order/OrderService.ts:OrderService.findOrder", "findOrder(id)", 3, 3, 10, 4, 1, null),
            new(SymbolKind.Method, "saveOrder", "src/order/OrderService.ts:OrderService.saveOrder", "saveOrder(o)", 11, 3, 20, 4, 1, null),
        ], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b,
        [
            new(SymbolKind.Module, "repo.ts", "src/repo.ts", null, 1, 1, 50, 1, null, null),
            new(SymbolKind.Function, "load", "src/repo.ts:load", "load(id)", 2, 1, 5, 1, 0, null),
        ], IndexSource.TreeSitter);
        w.WriteEdges(
        [
            new NewEdge(aIds[2], bIds[1], "src/repo.ts:load", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 5),
            new NewEdge(aIds[3], aIds[2], "src/order/OrderService.ts:OrderService.findOrder", EdgeKind.Calls, IndexSource.TreeSitter, 0.9, a, 13),
            new NewEdge(aIds[3], null, "src/gone.ts:vanished", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 14),
        ]);
        return (db, a, b, aIds, bIds);
    }

    [Fact]
    public void SearchSymbols_PrefixMatchesName_CaseInsensitive()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            var hits = r.SearchSymbols("order");
            Assert.Contains(hits, s => s.Id == a[1]);
            Assert.Contains(hits, s => s.Id == a[2]);
            Assert.DoesNotContain(hits, s => s.Name == "load");
        }
    }

    [Fact]
    public void SearchSymbols_ExcludesExternalAndModuleSymbols()
    {
        var (db, _, _, _, _) = Seed();
        using (db)
        {
            new IndexWriter(db).GetOrCreateExternal("OrderBase");
            var hits = new IndexReader(db).SearchSymbols("order");
            Assert.DoesNotContain(hits, s => s.Kind == SymbolKind.External);
            Assert.DoesNotContain(hits, s => s.Kind == SymbolKind.Module);
        }
    }

    [Fact]
    public void SearchSymbols_MultiWord_RequiresAllTerms()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var hits = new IndexReader(db).SearchSymbols("order save");
            Assert.Single(hits);
            Assert.Equal(a[3], hits[0].Id);
        }
    }

    [Fact]
    public void GetNeighbors_Outgoing_ReturnsTargetsIncludingDangling()
    {
        var (db, _, _, a, b) = Seed();
        using (db)
        {
            var n = new IndexReader(db).GetNeighbors(a[3], Direction.Outgoing);
            Assert.Equal(2, n.Count);
            Assert.Contains(n, x => x.Other?.Id == a[2] && x.Edge.Confidence == 0.9);
            Assert.Contains(n, x => x.Other is null && x.Edge.ToName == "src/gone.ts:vanished");
        }
    }

    [Fact]
    public void GetNeighbors_Incoming_ExcludesContainsByDefaultFilter()
    {
        var (db, _, _, a, b) = Seed();
        using (db)
        {
            var callers = new IndexReader(db).GetNeighbors(a[2], Direction.Incoming, new HashSet<EdgeKind> { EdgeKind.Calls });
            Assert.Single(callers);
            Assert.Equal(a[3], callers[0].Other!.Id);

            var all = new IndexReader(db).GetNeighbors(a[2], Direction.Incoming);
            Assert.Equal(2, all.Count); // calls + contains
        }
    }

    [Fact]
    public void FindByName_And_FindByQualifiedName_And_GetSymbol()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            Assert.Single(r.FindByName("findOrder"));
            Assert.Equal(a[2], r.FindByQualifiedName("src/order/OrderService.ts:OrderService.findOrder").Single().Id);
            Assert.Equal("saveOrder", r.GetSymbol(a[3])!.Name);
            Assert.Null(r.GetSymbol(999999));
        }
    }

    [Fact]
    public void GetSymbolsInFile_OrderedByStartLine_AndGetFile()
    {
        var (db, fileA, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            var syms = r.GetSymbolsInFile(fileA);
            Assert.Equal(4, syms.Count);
            Assert.Equal(a[0], syms[0].Id);
            Assert.Equal("typescript", r.GetFile("src/order/OrderService.ts")!.Language);
            Assert.Null(r.GetFile("nope.ts"));
            Assert.Equal(2, r.GetAllFiles().Count);
        }
    }

    [Fact]
    public void GetSymbolAt_ReturnsInnermost_OrNull()
    {
        var (db, fileA, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            Assert.Equal(a[2], r.GetSymbolAt(fileA, 5, 10)!.Id);   // 方法体内
            Assert.Equal(a[1], r.GetSymbolAt(fileA, 2, 7)!.Id);    // 类名上
            Assert.Equal(a[1], r.GetSymbolAt(fileA, 10, 5)!.Id);   // findOrder 结束列之后、仍在类内
            Assert.Equal(a[2], r.GetSymbolAt(fileA, 10, 4)!.Id);   // 恰好在 findOrder 结束位置
            Assert.Null(r.GetSymbolAt(fileA, 45, 1));              // 类之后的空行，只剩 module
            Assert.Null(r.GetSymbolAt(999, 1, 1));                 // 文件不存在
        }
    }

    [Fact]
    public void GetDanglingEdges_ReturnsOnlyNullTargets()
    {
        var (db, _, _, _, _) = Seed();
        using (db)
        {
            var dangling = new IndexReader(db).GetDanglingEdges();
            Assert.Single(dangling);
            Assert.Equal("src/gone.ts:vanished", dangling[0].ToName);
        }
    }

    [Fact]
    public void GetEdgeTriples_SkipsDanglingAndContains_AndCarriesVia()
    {
        var (db, fileA, _, a, b) = Seed();
        using (db)
        {
            new IndexWriter(db).WriteEdges(
            [
                new NewEdge(a[1], b[1], "src/repo.ts:load", EdgeKind.Dispatches, IndexSource.TreeSitter, 0.7, fileA, 2, Via: "handles"),
            ]);
            var triples = new IndexReader(db).GetEdgeTriples();
            // Seed：2 条已解析 calls（悬空的那条不算）+ WriteSymbols 产生的 contains 不算 + 这条 dispatches
            Assert.Equal(3, triples.Count);
            var handles = Assert.Single(triples, t => t.Kind == EdgeKind.Dispatches);
            Assert.Equal((a[1], b[1], 0.7, "handles"), (handles.FromId, handles.ToId, handles.Confidence, handles.Via));
            Assert.All(triples.Where(t => t.Kind == EdgeKind.Calls), t => Assert.Null(t.Via));
        }
    }

    [Fact]
    public void GetDiRegistrations_ReturnsResolvedPairsInFileLineOrder_SkippingHalfResolved()
    {
        var (db, fileA, fileB, a, b) = Seed();
        using (db)
        {
            // 两条完整注册故意按行号倒序插入；一条只解析出实现没有接口的要跳过
            db.Execute("INSERT INTO di_registrations (interface_id, impl_id, file_id, line, lifetime, conditional) VALUES ($i, $m, $f, $l, 'scoped', 0)",
                ("$i", a[1]), ("$m", b[1]), ("$f", fileB), ("$l", 9));
            db.Execute("INSERT INTO di_registrations (interface_id, impl_id, file_id, line, lifetime, conditional) VALUES ($i, $m, $f, $l, 'scoped', 0)",
                ("$i", a[1]), ("$m", a[2]), ("$f", fileB), ("$l", 3));
            db.Execute("INSERT INTO di_registrations (interface_id, impl_id, file_id, line, lifetime, conditional) VALUES (NULL, $m, $f, $l, 'scoped', 0)",
                ("$m", a[3]), ("$f", fileA), ("$l", 1));
            var di = new IndexReader(db).GetDiRegistrations();
            Assert.Equal(2, di.Count);
            Assert.Equal((a[1], a[2], fileB, 3), (di[0].InterfaceId, di[0].ImplId, di[0].FileId, di[0].Line));
            Assert.Equal((a[1], b[1], fileB, 9), (di[1].InterfaceId, di[1].ImplId, di[1].FileId, di[1].Line));
        }
    }
}
