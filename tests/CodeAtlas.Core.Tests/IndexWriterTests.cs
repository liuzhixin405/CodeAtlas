using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexWriterTests
{
    private static ParsedSymbol Module(string path) =>
        new(SymbolKind.Module, Path.GetFileName(path), path, null, 1, 1, 100, 1, null, null);

    [Fact]
    public void UpsertFile_InsertsThenUpdatesSameRow()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);

        var id1 = w.UpsertFile("src/a.ts", "typescript", "h1", 10, FileStatus.Ok);
        var id2 = w.UpsertFile("src/a.ts", "typescript", "h2", 12, FileStatus.Ok);

        Assert.Equal(id1, id2);
        Assert.Equal("h2", db.Scalar<string>("SELECT hash FROM files WHERE id = $id", ("$id", id1)));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public void WriteSymbols_AssignsIds_ParentLinks_AndContainsEdges()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var fileId = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var symbols = new List<ParsedSymbol>
        {
            Module("src/a.ts"),
            new(SymbolKind.Class, "Svc", "src/a.ts:Svc", "class Svc", 2, 1, 10, 2, 0, "public"),
            new(SymbolKind.Method, "find", "src/a.ts:Svc.find", "find(id: number)", 3, 3, 5, 4, 1, null),
        };

        var ids = w.WriteSymbols(fileId, symbols, IndexSource.TreeSitter);

        Assert.Equal(3, ids.Count);
        Assert.Equal(ids[0], db.Scalar<long>("SELECT parent_id FROM symbols WHERE id = $id", ("$id", ids[1])));
        Assert.Equal(ids[1], db.Scalar<long>("SELECT parent_id FROM symbols WHERE id = $id", ("$id", ids[2])));
        Assert.Equal("type_alias", DbNames.ToDb(SymbolKind.TypeAlias));
        Assert.Equal(2L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'contains'"));
        Assert.Equal(1L, db.Scalar<long>(
            "SELECT COUNT(*) FROM edges WHERE kind = 'contains' AND from_id = $p AND to_id = $c AND confidence = 1.0",
            ("$p", ids[1]), ("$c", ids[2])));
    }

    [Fact]
    public void DeleteFileContents_RemovesSymbolsImportsAndOutgoingEdges_DanglesIncoming()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/b.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b, [Module("src/b.ts"), new(SymbolKind.Function, "helper", "src/b.ts:helper", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        w.WriteImports(a, [new ParsedImport("./b", true, 1)]);
        // a.main -> b.helper
        w.WriteEdges([new NewEdge(aIds[1], bIds[1], "src/b.ts:helper", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 3)]);
        // b.helper -> a.main
        w.WriteEdges([new NewEdge(bIds[1], aIds[1], "src/a.ts:main", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, b, 3)]);

        w.DeleteFileContents(a);

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM symbols WHERE file_id = $f", ("$f", a)));
        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM imports WHERE file_id = $f", ("$f", a)));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM files WHERE id = $f", ("$f", a)));
        // b.helper -> a.main 变为悬空，保留 to_name
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE from_id = $b AND to_id IS NULL AND to_name = 'src/a.ts:main'", ("$b", bIds[1])));
        // a.main -> b.helper 随 a 的符号级联删除
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'calls'"));
    }

    [Fact]
    public void ReattachDanglingEdges_RelinksByQualifiedName_WhenUnique()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/b.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b, [Module("src/b.ts"), new(SymbolKind.Function, "helper", "src/b.ts:helper", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        w.WriteEdges([new NewEdge(bIds[1], aIds[1], "src/a.ts:main", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, b, 3)]);
        w.DeleteFileContents(a);
        var newAIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 5, 1, 9, 1, 0, null)], IndexSource.TreeSitter);

        var reattached = w.ReattachDanglingEdges();

        Assert.Equal(1, reattached);
        Assert.Equal(newAIds[1], db.Scalar<long>("SELECT to_id FROM edges WHERE from_id = $b", ("$b", bIds[1])));
    }

    [Fact]
    public void GetOrCreateExternal_ReturnsSameIdForSameName()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);

        var id1 = w.GetOrCreateExternal("console.log");
        var id2 = w.GetOrCreateExternal("console.log");
        var id3 = w.GetOrCreateExternal("fetch");

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Equal("external", db.Scalar<string>("SELECT kind FROM symbols WHERE id = $id", ("$id", id1)));
        Assert.Equal("<external>", db.Scalar<string>("SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id = $id", ("$id", id1)));
    }

    [Fact]
    public void DeleteFile_RemovesFileRow()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        w.WriteSymbols(a, [Module("src/a.ts")], IndexSource.TreeSitter);

        w.DeleteFile(a);

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM symbols"));
    }
}
