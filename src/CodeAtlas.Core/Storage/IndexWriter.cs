using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Storage;

public sealed class IndexWriter(IndexDatabase db)
{
    public const string ExternalFilePath = "<external>";

    public long UpsertFile(string path, string language, string hash, long size, FileStatus status)
    {
        return db.Scalar<long>("""
            INSERT INTO files (path, language, hash, size, indexed_at, status)
            VALUES ($path, $language, $hash, $size, $now, $status)
            ON CONFLICT(path) DO UPDATE SET
                language = excluded.language, hash = excluded.hash, size = excluded.size,
                indexed_at = excluded.indexed_at, status = excluded.status,
                roslyn_at = NULL
            RETURNING id;
            """,
            ("$path", path), ("$language", language), ("$hash", hash), ("$size", size),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$status", DbNames.ToDb(status)));
    }

    /// <summary>删除文件的符号、import 和从它出发的边（级联）；指向它的边变为悬空（to_id 置 NULL）。文件行保留。</summary>
    public void DeleteFileContents(long fileId)
    {
        db.Execute("DELETE FROM symbols WHERE file_id = $f", ("$f", fileId));
        db.Execute("DELETE FROM imports WHERE file_id = $f", ("$f", fileId));
    }

    public void DeleteFile(long fileId)
    {
        DeleteFileContents(fileId);
        db.Execute("DELETE FROM files WHERE id = $f", ("$f", fileId));
    }

    /// <summary>按顺序写入符号，返回与输入下标对应的数据库 id；同时写 contains 边。</summary>
    public IReadOnlyList<long> WriteSymbols(long fileId, IReadOnlyList<ParsedSymbol> symbols, IndexSource source)
    {
        var ids = new long[symbols.Count];
        using var insert = db.CreateCommand("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, signature, start_line, start_col, end_line, end_col, parent_id, visibility, source)
            VALUES ($file, $kind, $name, $qname, $sig, $sl, $sc, $el, $ec, $parent, $vis, $source)
            RETURNING id;
            """);
        var p = insert.Parameters;
        p.AddWithValue("$file", fileId); p.AddWithValue("$kind", ""); p.AddWithValue("$name", ""); p.AddWithValue("$qname", "");
        p.AddWithValue("$sig", DBNull.Value); p.AddWithValue("$sl", 0); p.AddWithValue("$sc", 0); p.AddWithValue("$el", 0); p.AddWithValue("$ec", 0);
        p.AddWithValue("$parent", DBNull.Value); p.AddWithValue("$vis", DBNull.Value); p.AddWithValue("$source", DbNames.ToDb(source));

        var containsEdges = new List<NewEdge>();
        for (var i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (s.ParentIndex is { } pi && (pi < 0 || pi >= i))
                throw new InvalidOperationException($"符号 {s.QualifiedName} 的 ParentIndex={pi} 必须指向更早的符号。");
            p["$kind"].Value = DbNames.ToDb(s.Kind);
            p["$name"].Value = s.Name;
            p["$qname"].Value = s.QualifiedName;
            p["$sig"].Value = (object?)s.Signature ?? DBNull.Value;
            p["$sl"].Value = s.StartLine; p["$sc"].Value = s.StartCol; p["$el"].Value = s.EndLine; p["$ec"].Value = s.EndCol;
            p["$parent"].Value = s.ParentIndex is { } pidx ? ids[pidx] : DBNull.Value;
            p["$vis"].Value = (object?)s.Visibility ?? DBNull.Value;
            ids[i] = (long)insert.ExecuteScalar()!;
            if (s.ParentIndex is { } parentIdx)
                containsEdges.Add(new NewEdge(ids[parentIdx], ids[i], s.QualifiedName, EdgeKind.Contains, source, 1.0, fileId, s.StartLine));
        }
        WriteEdges(containsEdges);
        return ids;
    }

    public void WriteImports(long fileId, IReadOnlyList<ParsedImport> imports)
    {
        using var cmd = db.CreateCommand("INSERT INTO imports (file_id, target, is_path, line) VALUES ($f, $t, $p, $l)");
        cmd.Parameters.AddWithValue("$f", fileId); cmd.Parameters.AddWithValue("$t", ""); cmd.Parameters.AddWithValue("$p", 0); cmd.Parameters.AddWithValue("$l", 0);
        foreach (var imp in imports)
        {
            cmd.Parameters["$t"].Value = imp.Target; cmd.Parameters["$p"].Value = imp.IsPath ? 1 : 0; cmd.Parameters["$l"].Value = imp.Line;
            cmd.ExecuteNonQuery();
        }
    }

    public void WriteEdges(IEnumerable<NewEdge> edges)
    {
        using var cmd = db.CreateCommand("""
            INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line, pos, ctx)
            VALUES ($from, $to, $toName, $kind, $source, $conf, $file, $line, $via, $ev, $pos, $ctx)
            """);
        var p = cmd.Parameters;
        foreach (var n in new[] { "$from", "$to", "$toName", "$kind", "$source", "$conf", "$file", "$line", "$via", "$ev", "$pos", "$ctx" }) p.AddWithValue(n, DBNull.Value);
        foreach (var e in edges)
        {
            p["$from"].Value = e.FromId; p["$to"].Value = (object?)e.ToId ?? DBNull.Value; p["$toName"].Value = e.ToName;
            p["$kind"].Value = DbNames.ToDb(e.Kind); p["$source"].Value = DbNames.ToDb(e.Source); p["$conf"].Value = e.Confidence;
            p["$file"].Value = e.FileId; p["$line"].Value = e.Line; p["$via"].Value = (object?)e.Via ?? DBNull.Value;
            p["$ev"].Value = (object?)e.EvidenceLine ?? DBNull.Value;
            p["$pos"].Value = e.Pos is >= 0 ? e.Pos : DBNull.Value; p["$ctx"].Value = (object?)e.Ctx ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>外部符号（仓库外的目标）。按名字去重，挂在伪文件 &lt;external&gt; 下。</summary>
    public long GetOrCreateExternal(string name)
    {
        var qname = "external:" + name;
        var existing = db.Query<long>("SELECT id FROM symbols WHERE kind = 'external' AND qualified_name = $q LIMIT 1", ("$q", qname));
        if (existing.Count > 0) return existing[0];
        var fileRows = db.Query<long>("SELECT id FROM files WHERE path = $p", ("$p", ExternalFilePath));
        var fileId = fileRows.Count > 0 ? fileRows[0] : UpsertFile(ExternalFilePath, "*", "", 0, FileStatus.Ok);
        return db.Scalar<long>("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, source)
            VALUES ($f, 'external', $n, $q, 0, 0, 0, 0, 'treesitter') RETURNING id;
            """, ("$f", fileId), ("$n", name), ("$q", qname));
    }

    /// <summary>把悬空边按 to_name 重新挂到唯一匹配的符号上。返回重挂数量。</summary>
    public int ReattachDanglingEdges()
    {
        return db.Execute("""
            UPDATE edges SET to_id = (
                SELECT s.id FROM symbols s WHERE s.qualified_name = edges.to_name
            )
            WHERE to_id IS NULL
              AND (SELECT COUNT(*) FROM symbols s WHERE s.qualified_name = edges.to_name) = 1
            """);
    }
}
