using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;

namespace CodeAtlas.Core.Storage;

public sealed class IndexWriter(IndexDatabase db)
{
    public const string ExternalFilePath = "<external>";

    /// <param name="mtime">读到内容时文件的最后修改时间（UTC ticks）；null = 未知，下次增量照常读文件算哈希。</param>
    public long UpsertFile(string path, string language, string hash, long size, FileStatus status, long? mtime = null)
    {
        return db.Scalar<long>("""
            INSERT INTO files (path, language, hash, size, indexed_at, status, mtime)
            VALUES ($path, $language, $hash, $size, $now, $status, $mtime)
            ON CONFLICT(path) DO UPDATE SET
                language = excluded.language, hash = excluded.hash, size = excluded.size,
                indexed_at = excluded.indexed_at, status = excluded.status, mtime = excluded.mtime,
                roslyn_at = NULL
            RETURNING id;
            """,
            ("$path", path), ("$language", language), ("$hash", hash), ("$size", size),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$status", DbNames.ToDb(status)), ("$mtime", mtime));
    }

    /// <summary>内容没变（哈希一致）但 mtime 不同或旧库没有 mtime：只补写 mtime，下次就能不读文件直接跳过。</summary>
    public void SetMtime(long fileId, long mtime) =>
        db.Execute("UPDATE files SET mtime = $m WHERE id = $id", ("$m", mtime), ("$id", fileId));

    /// <summary>删除文件的符号、import 和从它出发的边（级联）；指向它的边变为悬空（to_id 置 NULL）。文件行保留。</summary>
    public void DeleteFileContents(long fileId)
    {
        db.Execute("""
            UPDATE files SET roslyn_at = NULL WHERE id IN (
                SELECT e.file_id FROM edges e JOIN symbols s ON s.id = e.to_id
                WHERE s.file_id = $f AND e.source = 'roslyn'
                UNION
                -- DI 注册（AddScoped<IRepo, Repo>()）没有指向实现的边，删符号会级联删掉注册行，注册所在文件要重走 Roslyn 才补得回来
                SELECT d.file_id FROM di_registrations d JOIN symbols s ON s.id = d.interface_id OR s.id = d.impl_id
                WHERE s.file_id = $f
            )
            """, ("$f", fileId));
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

    public void WriteReferences(IEnumerable<PendingReference> references)
    {
        using var cmd = db.CreateCommand("""
            INSERT INTO source_references (from_id, file_id, name, kind, line, via, receiver, import_source, imported_name, pos, ctx)
            VALUES ($from, $file, $name, $kind, $line, $via, $receiver, $source, $imported, $pos, $ctx)
            """);
        foreach (var name in new[] { "$from", "$file", "$name", "$kind", "$line", "$via", "$receiver", "$source", "$imported", "$pos", "$ctx" })
            cmd.Parameters.AddWithValue(name, DBNull.Value);
        foreach (var reference in references)
        {
            cmd.Parameters["$from"].Value = reference.FromSymbolId;
            cmd.Parameters["$file"].Value = reference.FileId;
            cmd.Parameters["$name"].Value = reference.Name;
            cmd.Parameters["$kind"].Value = DbNames.ToDb(reference.Kind);
            cmd.Parameters["$line"].Value = reference.Line;
            cmd.Parameters["$via"].Value = (object?)reference.Via ?? DBNull.Value;
            cmd.Parameters["$receiver"].Value = reference.Script is { } script ? (int)script.Receiver : DBNull.Value;
            cmd.Parameters["$source"].Value = (object?)reference.Script?.ImportSource ?? DBNull.Value;
            cmd.Parameters["$imported"].Value = (object?)reference.Script?.ImportedName ?? DBNull.Value;
            cmd.Parameters["$pos"].Value = reference.Pos;
            cmd.Parameters["$ctx"].Value = (object?)reference.Context ?? DBNull.Value;
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

    /// <summary>
    /// 同一调用处（from, kind, line, pos）既有悬空边又解析出了仓库内目标：删悬空边；只解析出 external：删 external、留悬空边等目标回来再挂。
    /// fileIds 给出时只处理这些文件里的边（增量只重解析了它们）。
    /// </summary>
    public void ReconcileResolvedReferences(IReadOnlyCollection<long>? fileIds = null)
    {
        var dangling = "";
        var fallback = "";
        if (fileIds is not null)
        {
            IndexReader.FillTempIds(db, "reconcile_files", fileIds);
            dangling = "AND dangling.file_id IN (SELECT id FROM temp.reconcile_files)";
            fallback = "AND fallback.file_id IN (SELECT id FROM temp.reconcile_files)";
        }
        db.Execute($"""
            DELETE FROM edges AS dangling WHERE dangling.to_id IS NULL AND dangling.kind <> 'contains' {dangling}
                AND EXISTS (
                    SELECT 1 FROM edges bound JOIN symbols target ON target.id = bound.to_id
                    WHERE bound.from_id = dangling.from_id AND bound.kind = dangling.kind
                        AND bound.line = dangling.line AND COALESCE(bound.pos, -1) = COALESCE(dangling.pos, -1)
                        AND target.kind <> 'external'
                );
            DELETE FROM edges AS fallback WHERE fallback.source = 'treesitter' {fallback}
                AND fallback.to_id IN (SELECT id FROM symbols WHERE kind = 'external')
                AND EXISTS (
                    SELECT 1 FROM edges dangling WHERE dangling.to_id IS NULL
                        AND dangling.from_id = fallback.from_id AND dangling.kind = fallback.kind
                        AND dangling.line = fallback.line AND COALESCE(dangling.pos, -1) = COALESCE(fallback.pos, -1)
                );
            """);
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
