using CodeAtlas.Core.Model;
using CodeAtlas.Core.Roslyn;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <param name="Files">Roslyn 处理过并写回的文件数。</param>
/// <param name="FilesSkipped">Roslyn 输出了但索引库里没有的文件（例如 obj/ 下生成的代码），整文件忽略。</param>
/// <param name="SymbolsMerged">按位置匹配到 tree-sitter 符号并升级为 roslyn 的数量。</param>
/// <param name="SymbolsAdded">tree-sitter 没识别出、由 Roslyn 新插入的符号。</param>
/// <param name="Edges">写入的 Roslyn 边（含最终仍悬空的）。</param>
/// <param name="EdgesDropped">起点在库里找不到而丢弃的边。</param>
/// <param name="ImplementationEdges">接口调用 → 实现 的派生边（见 ImplementationLinker）。</param>
public sealed record RoslynApplyReport(
    int Files, int FilesSkipped, int SymbolsMerged, int SymbolsAdded, int Edges, int EdgesDropped, int DiRegistrations, int Errors, int ImplementationEdges = 0);

/// <summary>
/// 把 Roslyn worker 的记录流写回索引库，覆盖 tree-sitter 的启发式结果。
/// 规则：
/// <list type="bullet">
/// <item>每个文件：删掉该文件里的 calls/references/inherits/implements/dispatches 边与 DI 行，contains/imports 保留；</item>
/// <item>符号先按 (start_line, start_col, kind) 合并，再按 (kind, name, start_line)，都不匹配则新插入；合并时更新限定名/签名/可见性并标为 roslyn；</item>
/// <item>限定名变化时，指向该符号的所有边的 to_name 同步改写（例如 Ns.C.Find → Ns.C.Find(int)），其他文件的 tree-sitter 边继续挂在正确 id 上；</item>
/// <item>边按限定名挂接：目标已知立即挂，仓库外目标记为 external，其他先悬空，Finish 时用全部已知符号再挂一次；</item>
/// <item>处理完一个文件写 files.roslyn_at；Finish 时补接口调用 → 实现 的派生边。</item>
/// </list>
/// 整个过程是一个事务（几十万条记录，预编译语句复用）；调用方按顺序 Apply 每条记录，最后 Finish。实例不可重用。
/// </summary>
public sealed class RoslynApplier(IndexDatabase db) : IDisposable
{
    private const string OverriddenEdgeKinds = "('calls','references','inherits','implements','dispatches')";

    private readonly IndexWriter _writer = new(db);
    private readonly IndexReader _reader = new(db);

    /// <summary>所有已处理文件里的 Roslyn 限定名 → 符号 id（partial 类型会有多个，取先出现的）。</summary>
    private readonly Dictionary<string, long> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _externals = new(StringComparer.Ordinal);
    private readonly List<DiRec> _di = [];

    private IndexDatabase.Batch? _batch;
    private SqliteCommand? _insertEdge, _updateSymbol, _renameEdges, _insertSymbol, _deleteEdges, _deleteDi, _stampFile;

    // 当前文件
    private long? _fileId;
    private readonly Dictionary<string, long> _fileSymbols = new(StringComparer.Ordinal);
    private Dictionary<(int Line, int Col, SymbolKind Kind), SymbolRecord> _byPosition = new();
    private Dictionary<(SymbolKind Kind, string Name, int Line), SymbolRecord> _byNameLine = new();
    private readonly HashSet<long> _matched = [];
    private long? _moduleId;

    private int _files, _filesSkipped, _merged, _added, _edges, _edgesDropped, _errors;
    private bool _finished;

    public void Apply(RoslynRecord record)
    {
        if (_finished) throw new InvalidOperationException("Finish 之后不能再 Apply。");
        _batch ??= db.BeginBatch();
        switch (record)
        {
            case FileRec f: BeginFile(f.Path); break;
            case SymbolRec s: if (_fileId is not null) ApplySymbol(s); break;
            case EdgeRec e: if (_fileId is not null) ApplyEdge(e); break;
            case DiRec d: if (_fileId is not null) _di.Add(d); break;
            case ErrorRec: _errors++; break;
            default: break; // progress / done
        }
    }

    public RoslynApplyReport Finish()
    {
        if (_finished) throw new InvalidOperationException("已经 Finish 过。");
        _finished = true;
        _batch ??= db.BeginBatch();
        FinishFile();
        ResolveDanglingRoslynEdges();
        var di = WriteDiRegistrations();
        _writer.ReattachDanglingEdges();
        var impl = new ImplementationLinker(db).Link();
        DisposeCommands();
        _batch.Commit();
        _batch = null;
        return new RoslynApplyReport(_files, _filesSkipped, _merged, _added, _edges, _edgesDropped, di, _errors, impl);
    }

    private void BeginFile(string path)
    {
        FinishFile();
        var file = _reader.GetFile(path);
        if (file is null) { _filesSkipped++; return; }
        _fileId = file.Id;

        Run(ref _deleteEdges, $"DELETE FROM edges WHERE file_id = $f AND kind IN {OverriddenEdgeKinds}", ("$f", file.Id));
        Run(ref _deleteDi, "DELETE FROM di_registrations WHERE file_id = $f", ("$f", file.Id));

        var existing = _reader.GetSymbolsInFile(file.Id);
        _byPosition = new Dictionary<(int, int, SymbolKind), SymbolRecord>();
        _byNameLine = new Dictionary<(SymbolKind, string, int), SymbolRecord>();
        _moduleId = null;
        foreach (var s in existing)
        {
            if (s.Kind == SymbolKind.Module) { _moduleId ??= s.Id; continue; }
            _byPosition.TryAdd((s.StartLine, s.StartCol, s.Kind), s);
            _byNameLine.TryAdd((s.Kind, s.Name, s.StartLine), s);
        }
    }

    private void FinishFile()
    {
        if (_fileId is not { } fileId) return;
        Run(ref _stampFile, "UPDATE files SET roslyn_at = $now WHERE id = $f", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$f", fileId));
        _files++;
        _fileId = null;
        _fileSymbols.Clear();
        _matched.Clear();
    }

    private void ApplySymbol(SymbolRec rec)
    {
        if (!DbNames.TryParseSymbolKind(rec.Kind, out var kind)) return;
        var fileId = _fileId!.Value;

        SymbolRecord? hit = null;
        if (_byPosition.TryGetValue((rec.StartLine, rec.StartCol, kind), out var byPos) && _matched.Add(byPos.Id)) hit = byPos;
        else if (_byNameLine.TryGetValue((kind, rec.Name, rec.StartLine), out var byName) && _matched.Add(byName.Id)) hit = byName;

        long id;
        if (hit is not null)
        {
            id = hit.Id;
            Run(ref _updateSymbol, """
                UPDATE symbols SET name = $n, qualified_name = $q, signature = $sig, end_line = $el, end_col = $ec,
                    visibility = COALESCE($vis, visibility), source = 'roslyn'
                WHERE id = $id
                """,
                ("$n", rec.Name), ("$q", rec.QualifiedName), ("$sig", rec.Signature), ("$el", rec.EndLine), ("$ec", rec.EndCol),
                ("$vis", rec.Visibility), ("$id", id));
            if (!string.Equals(hit.QualifiedName, rec.QualifiedName, StringComparison.Ordinal))
                Run(ref _renameEdges, "UPDATE edges SET to_name = $q WHERE to_id = $id", ("$q", rec.QualifiedName), ("$id", id));
            _merged++;
        }
        else
        {
            var parentId = ResolveParent(rec.ParentQualifiedName);
            id = (long)Scalar(ref _insertSymbol, """
                INSERT INTO symbols (file_id, kind, name, qualified_name, signature, start_line, start_col, end_line, end_col, parent_id, visibility, source)
                VALUES ($f, $k, $n, $q, $sig, $sl, $sc, $el, $ec, $p, $vis, 'roslyn') RETURNING id;
                """,
                ("$f", fileId), ("$k", DbNames.ToDb(kind)), ("$n", rec.Name), ("$q", rec.QualifiedName), ("$sig", rec.Signature),
                ("$sl", rec.StartLine), ("$sc", rec.StartCol), ("$el", rec.EndLine), ("$ec", rec.EndCol), ("$p", parentId), ("$vis", rec.Visibility))!;
            if (parentId is { } pid)
                InsertEdge(pid, id, rec.QualifiedName, EdgeKind.Contains, 1.0, fileId, rec.StartLine, null, null);
            _added++;
        }

        _fileSymbols.TryAdd(rec.QualifiedName, id);
        _known.TryAdd(rec.QualifiedName, id);
    }

    private long? ResolveParent(string? parentQualifiedName)
    {
        if (parentQualifiedName is not null)
        {
            if (_fileSymbols.TryGetValue(parentQualifiedName, out var local)) return local;
            if (_known.TryGetValue(parentQualifiedName, out var global)) return global;
        }
        return _moduleId;
    }

    private void ApplyEdge(EdgeRec rec)
    {
        if (!DbNames.TryParseEdgeKind(rec.Kind, out var kind)) return;
        if (!_fileSymbols.TryGetValue(rec.FromQualifiedName, out var fromId) && !_known.TryGetValue(rec.FromQualifiedName, out fromId))
        {
            _edgesDropped++;
            return;
        }

        long? toId;
        string toName;
        if (rec.ToIsExternal)
        {
            if (!_externals.TryGetValue(rec.ToQualifiedName, out var ext))
            {
                ext = _writer.GetOrCreateExternal(rec.ToQualifiedName);
                _externals[rec.ToQualifiedName] = ext;
            }
            toId = ext;
            toName = "external:" + rec.ToQualifiedName;
        }
        else
        {
            toName = rec.ToQualifiedName;
            toId = _fileSymbols.TryGetValue(toName, out var local) ? local
                : _known.TryGetValue(toName, out var global) ? global
                : null;
        }

        InsertEdge(fromId, toId, toName, kind, rec.Confidence, _fileId!.Value, rec.Line, rec.Via, rec.EvidenceLine);
        _edges++;
    }

    private void InsertEdge(long fromId, long? toId, string toName, EdgeKind kind, double confidence, long fileId, int line, string? via, int? evidenceLine) =>
        Run(ref _insertEdge, """
            INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line)
            VALUES ($from, $to, $toName, $kind, 'roslyn', $conf, $file, $line, $via, $ev)
            """,
            ("$from", fromId), ("$to", toId), ("$toName", toName), ("$kind", DbNames.ToDb(kind)), ("$conf", confidence),
            ("$file", fileId), ("$line", line), ("$via", via), ("$ev", evidenceLine));

    /// <summary>目标文件在后面才处理到的边，这里用全部已知符号补挂。</summary>
    private void ResolveDanglingRoslynEdges()
    {
        var dangling = new List<(long Id, string ToName)>();
        using (var cmd = db.CreateCommand("SELECT id, to_name FROM edges WHERE source = 'roslyn' AND to_id IS NULL"))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) dangling.Add((r.GetInt64(0), r.GetString(1)));
        }
        if (dangling.Count == 0) return;
        using var update = db.CreateCommand("UPDATE edges SET to_id = $to WHERE id = $id");
        update.Parameters.AddWithValue("$to", 0L);
        update.Parameters.AddWithValue("$id", 0L);
        foreach (var (id, toName) in dangling)
        {
            if (!_known.TryGetValue(toName, out var toId)) continue;
            update.Parameters["$to"].Value = toId;
            update.Parameters["$id"].Value = id;
            update.ExecuteNonQuery();
        }
    }

    private int WriteDiRegistrations()
    {
        var written = 0;
        foreach (var d in _di)
        {
            var file = _reader.GetFile(d.Path);
            if (file is null) continue;
            var ifaceId = _known.TryGetValue(d.InterfaceQualifiedName, out var i) ? i : (long?)null;
            var implId = _known.TryGetValue(d.ImplQualifiedName, out var m) ? m : (long?)null;
            if (ifaceId is null && implId is null) continue;
            db.Execute("""
                INSERT INTO di_registrations (interface_id, impl_id, file_id, line, lifetime, conditional)
                VALUES ($i, $m, $f, $l, $lt, $c)
                """, ("$i", ifaceId), ("$m", implId), ("$f", file.Id), ("$l", d.Line), ("$lt", d.Lifetime), ("$c", d.Conditional ? 1 : 0));
            written++;
        }
        return written;
    }

    // ---- 预编译语句复用：几十万条记录逐条 CreateCommand 的开销占了写回时间的大头 ----

    private SqliteCommand Prepare(ref SqliteCommand? slot, string sql, (string Name, object? Value)[] parameters)
    {
        if (slot is null)
        {
            slot = db.CreateCommand(sql);
            foreach (var (name, _) in parameters) slot.Parameters.AddWithValue(name, DBNull.Value);
        }
        foreach (var (name, value) in parameters) slot.Parameters[name].Value = value ?? DBNull.Value;
        return slot;
    }

    private void Run(ref SqliteCommand? slot, string sql, params (string Name, object? Value)[] parameters) =>
        Prepare(ref slot, sql, parameters).ExecuteNonQuery();

    private object? Scalar(ref SqliteCommand? slot, string sql, params (string Name, object? Value)[] parameters) =>
        Prepare(ref slot, sql, parameters).ExecuteScalar();

    private void DisposeCommands()
    {
        foreach (var c in new[] { _insertEdge, _updateSymbol, _renameEdges, _insertSymbol, _deleteEdges, _deleteDi, _stampFile }) c?.Dispose();
        _insertEdge = _updateSymbol = _renameEdges = _insertSymbol = _deleteEdges = _deleteDi = _stampFile = null;
    }

    public void Dispose()
    {
        DisposeCommands();
        _batch?.Dispose(); // 没 Finish 就 Dispose：回滚
        _batch = null;
    }
}
