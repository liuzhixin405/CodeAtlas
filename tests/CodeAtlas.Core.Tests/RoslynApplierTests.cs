using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class RoslynApplierTests
{
    private static ParsedSymbol Module(string path) => new(SymbolKind.Module, Path.GetFileName(path), path, null, 1, 1, 200, 1, null, null);

    /// <summary>预置 tree-sitter 版索引：A.cs 里 Ns.C.Find / Ns.C.Other，B.cs 里 Ns.D.Run 调 Find（按不带参数的旧限定名挂接）。</summary>
    private static (IndexDatabase Db, long FileA, long FileB, IReadOnlyList<long> A, IReadOnlyList<long> B) Seed()
    {
        var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("A.cs", "csharp", "h", 1, FileStatus.Ok);
        var b = w.UpsertFile("B.cs", "csharp", "h", 1, FileStatus.Ok);
        var aIds = w.WriteSymbols(a,
        [
            Module("A.cs"),
            new(SymbolKind.Namespace, "Ns", "Ns", null, 1, 1, 200, 1, 0, null),
            new(SymbolKind.Class, "C", "Ns.C", "class C", 3, 1, 20, 2, 1, "public"),
            new(SymbolKind.Method, "Find", "Ns.C.Find", "Item Find(int id)", 5, 5, 5, 40, 2, "public"),
            new(SymbolKind.Method, "Other", "Ns.C.Other", "void Other()", 7, 5, 7, 30, 2, "public"),
        ], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b,
        [
            Module("B.cs"),
            new(SymbolKind.Namespace, "Ns", "Ns", null, 1, 1, 200, 1, 0, null),
            new(SymbolKind.Class, "D", "Ns.D", "class D", 3, 1, 20, 2, 1, "public"),
            new(SymbolKind.Method, "Run", "Ns.D.Run", "void Run()", 5, 5, 5, 40, 2, "public"),
        ], IndexSource.TreeSitter);
        w.WriteImports(a, [new ParsedImport("System", false, 1)]);
        w.WriteEdges(
        [
            new NewEdge(aIds[3], aIds[4], "Ns.C.Other", EdgeKind.Calls, IndexSource.TreeSitter, 0.9, a, 5),           // tree-sitter 猜的，要被覆盖掉
            new NewEdge(aIds[3], null, "external:ToList", EdgeKind.Calls, IndexSource.TreeSitter, 0.5, a, 5),
            new NewEdge(aIds[0], null, "external:System", EdgeKind.Imports, IndexSource.TreeSitter, 0.5, a, 1),     // imports 保留
            new NewEdge(bIds[3], aIds[3], "Ns.C.Find", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, b, 5),           // B 指向 A 的旧限定名
        ]);
        return (db, a, b, aIds, bIds);
    }

    private static RoslynApplyReport ApplyAll(IndexDatabase db, params RoslynRecord[] records)
    {
        var applier = new RoslynApplier(db);
        foreach (var r in records) applier.Apply(r);
        return applier.Finish();
    }

    [Fact]
    public void OverrideRules_DeleteHeuristicEdges_KeepContainsAndImports_MergeSymbols_RewriteIncomingNames()
    {
        var (db, fileA, fileB, a, b) = Seed();
        using (db)
        {
            var report = ApplyAll(db,
                new FileRec("A.cs"),
                new SymbolRec("A.cs", "namespace", "Ns", "Ns", "namespace Ns", 1, 1, 200, 1, null, null),
                new SymbolRec("A.cs", "class", "C", "Ns.C", "class C", 3, 1, 20, 2, "Ns", "public"),
                new SymbolRec("A.cs", "method", "Find", "Ns.C.Find(int)", "Item Find(int id)", 5, 5, 5, 40, "Ns.C", "public"),
                new SymbolRec("A.cs", "method", "Other", "Ns.C.Other()", "void Other()", 7, 5, 7, 30, "Ns.C", "public"),
                new EdgeRec("Ns.C.Find(int)", "Ns.IRepo.Find(int)", false, "calls", 1.0, "A.cs", 5),                  // 目标不在库里 → 悬空
                new EdgeRec("Ns.C.Find(int)", "System.Linq.Enumerable.ToList<TSource>(IEnumerable<TSource>)", true, "calls", 1.0, "A.cs", 5),
                new DoneRec(1, 1, 0));

            var reader = new IndexReader(db);
            var find = reader.GetSymbol(a[3])!;
            Assert.Equal("Ns.C.Find(int)", find.QualifiedName);
            Assert.Equal(IndexSource.Roslyn, find.Source);
            Assert.Equal(IndexSource.Roslyn, reader.GetSymbol(a[2])!.Source);
            Assert.Equal(5, reader.GetSymbolsInFile(fileA).Count); // 没有多出符号

            var outFind = reader.GetNeighbors(a[3], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
            Assert.DoesNotContain(outFind, n => n.Other?.Id == a[4]);                                    // 启发式边删了
            Assert.DoesNotContain(outFind, n => n.Edge.ToName == "external:ToList");
            Assert.Contains(outFind, n => n.Other is null && n.Edge.ToName == "Ns.IRepo.Find(int)" && n.Edge.Source == IndexSource.Roslyn);
            Assert.Contains(outFind, n => n.Other?.Kind == SymbolKind.External && n.Edge.ToName.StartsWith("external:System.Linq.Enumerable.ToList"));

            Assert.Equal(4L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'contains' AND file_id = $f", ("$f", fileA)));
            Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'imports' AND file_id = $f", ("$f", fileA)));

            // B.cs 没被 Roslyn 处理，但它指向 Find 的边要跟着改名并保持挂接
            var runOut = reader.GetNeighbors(b[3], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).Single();
            Assert.Equal(a[3], runOut.Other!.Id);
            Assert.Equal("Ns.C.Find(int)", runOut.Edge.ToName);

            Assert.NotNull(db.Query<string>("SELECT roslyn_at FROM files WHERE id = $f", ("$f", fileA)).Single());
            Assert.Equal(1L, db.Scalar<long>("SELECT roslyn_at IS NULL FROM files WHERE id = $f", ("$f", fileB))); // 未处理文件不写
            Assert.Equal((1, 4, 0, 2), (report.Files, report.SymbolsMerged, report.SymbolsAdded, report.Edges));
        }
    }

    [Fact]
    public void UpsertFile_OnChangedFile_ClearsRoslynAt_SoItCountsAsStale()
    {
        var (db, fileA, _, _, _) = Seed();
        using (db)
        {
            ApplyAll(db, new FileRec("A.cs"), new DoneRec(1, 1, 0));
            Assert.Equal(0L, db.Scalar<long>("SELECT roslyn_at IS NULL FROM files WHERE id = $f", ("$f", fileA)));

            new IndexWriter(db).UpsertFile("A.cs", "csharp", "h2", 2, FileStatus.Ok); // 文件变了，tree-sitter 重新写入
            Assert.Equal(1L, db.Scalar<long>("SELECT roslyn_at IS NULL FROM files WHERE id = $f", ("$f", fileA)));
        }
    }

    [Fact]
    public void UnmatchedSymbol_IsInsertedUnderItsParent_WithContainsEdge()
    {
        var (db, fileA, _, a, _) = Seed();
        using (db)
        {
            ApplyAll(db,
                new FileRec("A.cs"),
                new SymbolRec("A.cs", "class", "C", "Ns.C", "class C", 3, 1, 20, 2, "Ns", "public"),
                new SymbolRec("A.cs", "property", "Count", "Ns.C.Count", "int Count { get; }", 9, 5, 9, 30, "Ns.C", "public"),
                new DoneRec(1, 1, 0));

            var reader = new IndexReader(db);
            var count = reader.FindByQualifiedName("Ns.C.Count").Single();
            Assert.Equal(a[2], count.ParentId);
            Assert.Equal(IndexSource.Roslyn, count.Source);
            Assert.Contains(reader.GetNeighbors(a[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Contains }), n => n.Other?.Id == count.Id);
        }
    }

    [Fact]
    public void PartialType_InTwoFiles_MergesEachFilesSymbol_AndEdgesResolveToOneId()
    {
        var db = IndexDatabase.OpenInMemory();
        using (db)
        {
            var w = new IndexWriter(db);
            var f1 = w.UpsertFile("P1.cs", "csharp", "h", 1, FileStatus.Ok);
            var f2 = w.UpsertFile("P2.cs", "csharp", "h", 1, FileStatus.Ok);
            var ids1 = w.WriteSymbols(f1, [Module("P1.cs"), new(SymbolKind.Class, "Repo", "Repo", null, 1, 1, 10, 1, 0, null), new(SymbolKind.Method, "A", "Repo.A", null, 2, 5, 2, 20, 1, null)], IndexSource.TreeSitter);
            var ids2 = w.WriteSymbols(f2, [Module("P2.cs"), new(SymbolKind.Class, "Repo", "Repo", null, 1, 1, 10, 1, 0, null), new(SymbolKind.Method, "B", "Repo.B", null, 2, 5, 2, 20, 1, null)], IndexSource.TreeSitter);

            ApplyAll(db,
                new FileRec("P1.cs"),
                new SymbolRec("P1.cs", "class", "Repo", "Repo", "class Repo", 1, 1, 10, 1, null, "public"),
                new SymbolRec("P1.cs", "method", "A", "Repo.A()", "void A()", 2, 5, 2, 20, "Repo", "public"),
                new EdgeRec("Repo.A()", "Repo.B()", false, "calls", 1.0, "P1.cs", 2),
                new FileRec("P2.cs"),
                new SymbolRec("P2.cs", "class", "Repo", "Repo", "class Repo", 1, 1, 10, 1, null, "public"),
                new SymbolRec("P2.cs", "method", "B", "Repo.B()", "void B()", 2, 5, 2, 20, "Repo", "public"),
                new DoneRec(1, 2, 0));

            var reader = new IndexReader(db);
            Assert.Equal(2, reader.FindByQualifiedName("Repo").Count);                 // 两个文件各一个，没有第三个
            Assert.All(reader.FindByQualifiedName("Repo"), s => Assert.Equal(IndexSource.Roslyn, s.Source));
            var edge = reader.GetNeighbors(ids1[2], Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).Single();
            Assert.Equal(ids2[2], edge.Other!.Id);                                     // A() → B() 跨文件挂接成功
        }
    }
}
