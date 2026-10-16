using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

public sealed record Neighbor(EdgeRecord Edge, SymbolRecord? Other);

public sealed class IndexReader(IndexDatabase db)
{
    private const string SymbolColumns = "s.id, s.file_id, s.kind, s.name, s.qualified_name, s.signature, s.start_line, s.start_col, s.end_line, s.end_col, s.parent_id, s.visibility, s.source";
    private const string EdgeColumns = "e.id, e.from_id, e.to_id, e.to_name, e.kind, e.source, e.confidence, e.file_id, e.line, e.via, e.evidence_line, e.pos, e.ctx";
    /// <summary>EdgeColumns 的列数：同一行里边后面接符号列时，符号从这个下标开始。</summary>
    private const int EdgeColumnCount = 13;

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
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at, mtime FROM files WHERE id = $id", ("$id", id)).FirstOrDefault();

    public FileRecord? GetFile(string path) =>
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at, mtime FROM files WHERE path = $p", ("$p", path)).FirstOrDefault();

    public IReadOnlyList<FileRecord> GetAllFiles() =>
        ReadFiles("SELECT id, path, language, hash, size, indexed_at, status, roslyn_at, mtime FROM files WHERE path <> $ext ORDER BY path", ("$ext", IndexWriter.ExternalFilePath));

    /// <summary>
    /// "待 Roslyn"的唯一定义：C# 文件、还没有被 Roslyn 覆盖，且不是"上次试过、不在任何解决方案里、内容没变"（roslyn_miss = hash）。
    /// 改过的文件（UpsertFile 清 roslyn_at）与依赖它的文件（DeleteFileContents 把有 Roslyn 边指向它的文件清 roslyn_at）都在里面。
    /// </summary>
    public const string RoslynPendingWhere = "language = 'csharp' AND roslyn_at IS NULL AND COALESCE(roslyn_miss, '') <> hash";

    /// <summary>待 Roslyn 的文件路径（相对主根，'/' 分隔），按路径排序。</summary>
    public IReadOnlyList<string> GetRoslynPendingFiles() =>
        db.Query<string>($"SELECT path FROM files WHERE {RoslynPendingWhere} ORDER BY path");

    public long CountRoslynPendingFiles() => db.Scalar<long>($"SELECT COUNT(*) FROM files WHERE {RoslynPendingWhere}");

    /// <summary>
    /// 待解析的引用（Roslyn 没覆盖的文件里、Roslyn 没绑上的那些）。fileIds 给出时只取这些文件的（增量只重解析受影响的文件）。
    /// </summary>
    public IReadOnlyList<PendingReference> GetPendingReferences(IReadOnlyCollection<long>? fileIds = null)
    {
        var filter = "";
        if (fileIds is not null)
        {
            FillTempIds(db, "pending_files", fileIds);
            filter = "AND r.file_id IN (SELECT id FROM temp.pending_files)";
        }
        return ReadPendingReferences(filter);
    }

    /// <summary>
    /// 增量时不在 exceptFileIds 里、却是别的文件解析要用的待解析引用：父类型（inherits / implements，含 Go 内嵌）、
    /// 函数返回值提示（via=returns）、Python 的类属性 / 类型别名（references）与 import 绑定、Go 的 import 别名。
    /// 解析器只拿它们建索引，不解析、不写边——这样子集解析时这些跨文件事实与全量解析时一样来自同一份引用。
    /// </summary>
    public IReadOnlyList<PendingReference> GetSupportReferences(IReadOnlyCollection<long> exceptFileIds)
    {
        FillTempIds(db, "support_except", exceptFileIds);
        // 条件只用 r 自己的列（语言经 file_id 子查询判断）：OR 里带 f.language 时 SQLite 没法先剪枝，
        // 后面的 NOT EXISTS 会对全部引用各跑一遍（ccs-backend 43 万条：4 秒 → 0.1 秒）
        return ReadPendingReferences("""
            AND r.file_id NOT IN (SELECT id FROM temp.support_except)
            AND (r.kind IN ('inherits', 'implements') OR r.via = 'returns'
                 OR ((r.kind = 'references' OR r.import_source IS NOT NULL)
                     AND r.file_id IN (SELECT id FROM files WHERE language IN ('python', 'go'))))
            """);
    }

    private List<PendingReference> ReadPendingReferences(string filter)
    {
        using var cmd = db.CreateCommand($"""
            SELECT r.from_id, r.file_id, f.path, f.language, r.name, r.kind, r.line, r.via,
                   r.receiver, r.import_source, r.imported_name, r.pos, r.ctx
            FROM source_references r JOIN files f ON f.id = r.file_id
            WHERE f.roslyn_at IS NULL {filter} AND NOT EXISTS (
                                SELECT 1 FROM edges e WHERE e.from_id = r.from_id
                                      AND e.to_id IS NOT NULL AND e.source = 'roslyn'
                                    AND e.kind = r.kind AND e.line = r.line
                  AND COALESCE(e.pos, -1) = r.pos
            )
            ORDER BY r.id
            """);
        using var rows = cmd.ExecuteReader();
        var references = new List<PendingReference>();
        while (rows.Read())
        {
            var script = rows.IsDBNull(8) ? null : new ScriptBinding((ScriptReceiver)rows.GetInt32(8),
                rows.IsDBNull(9) ? null : rows.GetString(9), rows.IsDBNull(10) ? null : rows.GetString(10));
            references.Add(new PendingReference(rows.GetInt64(0), rows.GetInt64(1), rows.GetString(2), rows.GetString(3),
                rows.GetString(4), DbNames.ParseEdgeKind(rows.GetString(5)), rows.GetInt32(6), rows.IsDBNull(7) ? null : rows.GetString(7),
                script, rows.GetInt32(11), rows.IsDBNull(12) ? null : rows.GetString(12)));
        }
        return references;
    }

    /// <summary>把一组 id 放进连接级临时表 temp.&lt;table&gt;(id)，供 IN (SELECT id FROM temp.&lt;table&gt;) 用——比拼几千个参数的 IN 列表稳。</summary>
    public static void FillTempIds(IndexDatabase db, string table, IEnumerable<long> ids)
    {
        db.Execute($"CREATE TEMP TABLE IF NOT EXISTS {table} (id INTEGER PRIMARY KEY); DELETE FROM temp.{table};");
        using var insert = db.CreateCommand($"INSERT OR IGNORE INTO temp.{table} (id) VALUES ($id)");
        insert.Parameters.AddWithValue("$id", 0L);
        foreach (var id in ids)
        {
            insert.Parameters["$id"].Value = id;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>同 <see cref="FillTempIds"/>，放名字：temp.&lt;table&gt;(name)。</summary>
    public static void FillTempNames(IndexDatabase db, string table, IEnumerable<string> names)
    {
        db.Execute($"CREATE TEMP TABLE IF NOT EXISTS {table} (name TEXT PRIMARY KEY); DELETE FROM temp.{table};");
        using var insert = db.CreateCommand($"INSERT OR IGNORE INTO temp.{table} (name) VALUES ($n)");
        insert.Parameters.AddWithValue("$n", "");
        foreach (var name in names)
        {
            insert.Parameters["$n"].Value = name;
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(long FileId, string Target, bool IsPath)> GetAllImports()
    {
        using var cmd = db.CreateCommand("SELECT file_id, target, is_path FROM imports");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, bool)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1));
        return list;
    }

    /// <summary>全部已解析的非 contains 边的 (from, to, kind, confidence, via, line, pos)，按边 id 排序，给聚合视图用（line / pos 给流程排源码顺序）。</summary>
    public IReadOnlyList<(long FromId, long ToId, EdgeKind Kind, double Confidence, string? Via, int Line, int? Pos)> GetEdgeTriples()
    {
        using var cmd = db.CreateCommand("SELECT from_id, to_id, kind, confidence, via, line, pos FROM edges WHERE to_id IS NOT NULL AND kind <> 'contains' ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, long, EdgeKind, double, string?, int, int?)>();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetInt64(1), DbNames.ParseEdgeKind(r.GetString(2)), r.GetDouble(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? 0 : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6)));
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
        while (r.Read()) list.Add(new Neighbor(ReadEdge(r, 0), ReadSymbol(r, EdgeColumnCount)));
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
                r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)), r.IsDBNull(8) ? null : r.GetInt64(8)));
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
        r.GetInt64(o + 7), r.GetInt32(o + 8), r.IsDBNull(o + 9) ? null : r.GetString(o + 9), r.IsDBNull(o + 10) ? null : r.GetInt32(o + 10),
        r.IsDBNull(o + 11) ? null : r.GetInt32(o + 11), r.IsDBNull(o + 12) ? null : r.GetString(o + 12));
}
