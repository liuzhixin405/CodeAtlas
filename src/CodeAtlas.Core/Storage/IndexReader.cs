using CodeAtlas.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

public sealed record Neighbor(EdgeRecord Edge, SymbolRecord? Other);

public sealed class IndexReader(IndexDatabase db)
{
    private const string SymbolColumns = "s.id, s.file_id, s.kind, s.name, s.qualified_name, s.signature, s.start_line, s.start_col, s.end_line, s.end_col, s.parent_id, s.visibility, s.source";
    private const string EdgeColumns = "e.id, e.from_id, e.to_id, e.to_name, e.kind, e.source, e.confidence, e.file_id, e.line, e.via, e.evidence_line";

    public IReadOnlyList<SymbolRecord> SearchSymbols(string query, int limit = 50)
    {
        var terms = query.Split([' ', '\t', '.', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*");
        var match = string.Join(" AND ", terms);
        if (match.Length == 0) return [];
        return ReadSymbols($"""
            SELECT {SymbolColumns} FROM symbols_fts f JOIN symbols s ON s.id = f.rowid
            WHERE symbols_fts MATCH $m AND s.kind NOT IN ('module', 'external')
            ORDER BY bm25(symbols_fts), length(s.name) LIMIT $limit
            """, ("$m", match), ("$limit", limit));
    }

    public SymbolRecord? GetSymbol(long id) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<SymbolRecord> FindByName(string name) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.name = $n ORDER BY s.qualified_name", ("$n", name));

    public IReadOnlyList<SymbolRecord> FindByQualifiedName(string qualifiedName) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.qualified_name = $q", ("$q", qualifiedName));

    public IReadOnlyList<SymbolRecord> GetSymbolsInFile(long fileId) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.file_id = $f ORDER BY s.start_line, s.start_col", ("$f", fileId));

    /// <summary>范围包含 (line, col) 且起点最靠后的非 module 符号；没有则 null。</summary>
    public SymbolRecord? GetSymbolAt(long fileId, int line, int col) =>
        ReadSymbols($"""
            SELECT {SymbolColumns} FROM symbols s
            WHERE s.file_id = $f AND s.kind <> 'module'
              AND (s.start_line < $l OR (s.start_line = $l AND s.start_col <= $c))
              AND (s.end_line > $l OR (s.end_line = $l AND s.end_col >= $c))
            ORDER BY s.start_line DESC, s.start_col DESC LIMIT 1
            """, ("$f", fileId), ("$l", line), ("$c", col)).FirstOrDefault();

    public long CountIncoming(long symbolId, EdgeKind kind) =>
        db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE to_id = $id AND kind = $k", ("$id", symbolId), ("$k", DbNames.ToDb(kind)));

    public IReadOnlyList<SymbolRecord> GetChildren(long parentId) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.parent_id = $p ORDER BY s.start_line", ("$p", parentId));

    /// <summary>处理某个消息/命令/查询类型的类：即有 dispatches/handles 边指向该类型的符号。</summary>
    public IReadOnlyList<SymbolRecord> GetDispatchHandlers(long typeId) =>
        ReadSymbols($"""
            SELECT {SymbolColumns} FROM edges e JOIN symbols s ON s.id = e.from_id
            WHERE e.to_id = $t AND e.kind = 'dispatches' AND e.via = 'handles'
            """, ("$t", typeId));

    public IReadOnlyList<SymbolRecord> GetAllSymbols() =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s");

    public IReadOnlyList<Neighbor> GetNeighbors(long symbolId, Direction direction, IReadOnlySet<EdgeKind>? kinds = null)
    {
        var kindFilter = kinds is { Count: > 0 }
            ? " AND e.kind IN (" + string.Join(",", kinds.Select(k => "'" + DbNames.ToDb(k) + "'")) + ")"
            : "";
        var result = new List<Neighbor>();
        if (direction is Direction.Outgoing or Direction.Both)
            result.AddRange(ReadNeighbors($"""
                SELECT {EdgeColumns}, {SymbolColumns} FROM edges e LEFT JOIN symbols s ON s.id = e.to_id
                WHERE e.from_id = $id{kindFilter} ORDER BY e.line
                """, symbolId));
        if (direction is Direction.Incoming or Direction.Both)
            result.AddRange(ReadNeighbors($"""
                SELECT {EdgeColumns}, {SymbolColumns} FROM edges e JOIN symbols s ON s.id = e.from_id
                WHERE e.to_id = $id{kindFilter} ORDER BY s.qualified_name, e.line
                """, symbolId));
        return result;
    }

    public FileRecord? GetFileById(long id) =>
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at FROM files WHERE id = $id", ("$id", id)).FirstOrDefault();

    public FileRecord? GetFile(string path) =>
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at FROM files WHERE path = $p", ("$p", path)).FirstOrDefault();

    public IReadOnlyList<FileRecord> GetAllFiles() =>
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at FROM files WHERE path <> $ext ORDER BY path", ("$ext", IndexWriter.ExternalFilePath));

    public IReadOnlyList<(long FileId, string Target, bool IsPath)> GetAllImports()
    {
        using var cmd = db.CreateCommand("SELECT file_id, target, is_path FROM imports");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, bool)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1));
        return list;
    }

    /// <summary>全部已解析的非 contains 边的 (from, to, kind, confidence, via)，给聚合视图用。</summary>
    public IReadOnlyList<(long FromId, long ToId, EdgeKind Kind, double Confidence, string? Via)> GetEdgeTriples()
    {
        using var cmd = db.CreateCommand("SELECT from_id, to_id, kind, confidence, via FROM edges WHERE to_id IS NOT NULL AND kind <> 'contains'");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long, EdgeKind, double, string?)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), DbNames.ParseEdgeKind(r.GetString(2)), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>接口与实现都解析到了的 DI 注册，按（文件路径，行）排序：注册顺序就是管道行为的执行顺序。</summary>
    public IReadOnlyList<(long InterfaceId, long ImplId, long FileId, int Line)> GetDiRegistrations()
    {
        using var cmd = db.CreateCommand("""
            SELECT d.interface_id, d.impl_id, d.file_id, d.line FROM di_registrations d JOIN files f ON f.id = d.file_id
            WHERE d.interface_id IS NOT NULL AND d.impl_id IS NOT NULL ORDER BY f.path, d.line
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long, long, int)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3)));
        return list;
    }

    public IReadOnlyList<EdgeRecord> GetDanglingEdges()
    {
        using var cmd = db.CreateCommand($"SELECT {EdgeColumns} FROM edges e WHERE e.to_id IS NULL");
        using var r = cmd.ExecuteReader();
        var list = new List<EdgeRecord>();
        while (r.Read()) list.Add(ReadEdge(r, 0));
        return list;
    }

    private List<SymbolRecord> ReadSymbols(string sql, params (string, object?)[] p)
    {
        using var cmd = db.CreateCommand(sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<SymbolRecord>();
        while (r.Read()) list.Add(ReadSymbol(r, 0)!);
        return list;
    }

    private List<Neighbor> ReadNeighbors(string sql, long id)
    {
        using var cmd = db.CreateCommand(sql, ("$id", id));
        using var r = cmd.ExecuteReader();
        var list = new List<Neighbor>();
        while (r.Read()) list.Add(new Neighbor(ReadEdge(r, 0), ReadSymbol(r, 11)));
        return list;
    }

    private List<FileRecord> ReadFiles(string sql, params (string, object?)[] p)
    {
        using var cmd = db.CreateCommand(sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read())
            list.Add(new FileRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                DateTimeOffset.Parse(r.GetString(5)), DbNames.ParseStatus(r.GetString(6)),
                r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7))));
        return list;
    }

    private static SymbolRecord? ReadSymbol(SqliteDataReader r, int o)
    {
        if (r.IsDBNull(o)) return null;
        return new SymbolRecord(
            r.GetInt64(o), r.GetInt64(o + 1), DbNames.ParseSymbolKind(r.GetString(o + 2)), r.GetString(o + 3), r.GetString(o + 4),
            r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
            r.GetInt32(o + 6), r.GetInt32(o + 7), r.GetInt32(o + 8), r.GetInt32(o + 9),
            r.IsDBNull(o + 10) ? null : r.GetInt64(o + 10),
            r.IsDBNull(o + 11) ? null : r.GetString(o + 11),
            DbNames.ParseSource(r.GetString(o + 12)));
    }

    private static EdgeRecord ReadEdge(SqliteDataReader r, int o) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.IsDBNull(o + 2) ? null : r.GetInt64(o + 2), r.GetString(o + 3),
        DbNames.ParseEdgeKind(r.GetString(o + 4)), DbNames.ParseSource(r.GetString(o + 5)), r.GetDouble(o + 6),
        r.GetInt64(o + 7), r.GetInt32(o + 8), r.IsDBNull(o + 9) ? null : r.GetString(o + 9), r.IsDBNull(o + 10) ? null : r.GetInt32(o + 10));
}
