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
/// <param name="Solutions">跑了几个解决方案（多项目索引时主根、嵌套仓库、外部根各一个）。</param>
/// <param name="FailedSolutions">失败的解决方案及原因；其余解决方案的结果照常写回。</param>
/// <param name="HeuristicKept">Roslyn 在那一行一条调用都没绑上（引用没还原、编译错误），保留下来的 tree-sitter 调用边。</param>
/// <param name="HeuristicTypeEdgesKept">Roslyn 对某个类型一条继承 / 实现边都没给（基类型在没还原的包里，是错误类型），保留下来的 tree-sitter 继承 / 实现边。</param>
/// <param name="Incremental">只走了待更新的文件（见 RoslynRunner.RunAndApplyAsync 的 pendingFiles）。</param>
/// <param name="Misses">Roslyn 试过、不在任何（成功的）解决方案输出里的 C# 文件，记了 roslyn_miss，内容不变就不再算待更新。</param>
public sealed record RoslynApplyReport(
    int Files, int FilesSkipped, int SymbolsMerged, int SymbolsAdded, int Edges, int EdgesDropped, int DiRegistrations, int Errors, int ImplementationEdges = 0,
    int Solutions = 1, IReadOnlyList<string>? FailedSolutions = null, int HeuristicKept = 0, int HeuristicTypeEdgesKept = 0,
    IReadOnlyList<CodeAtlas.Core.Diagnostics.PhaseTiming>? Timings = null, bool Incremental = false, int Misses = 0);

/// <summary>
/// 把 Roslyn worker 的记录流写回索引库，覆盖 tree-sitter 的启发式结果。
/// 规则：
/// <list type="bullet">
/// <item>每个文件：删掉该文件里的 calls/references/inherits/implements/dispatches 边与 DI 行，contains/imports 保留；</item>
/// <item>例外二：tree-sitter 的继承 / 实现边，若 Roslyn 对同一个类型同一种边（inherits 或 implements）一条都没给，保留下来——
/// 包没还原时 Roslyn 认不出 ControllerBase 这类基类，删掉启发式边就认不出控制器入口；</item>
/// <item>例外：tree-sitter 指向仓库内符号的调用边，若原置信度不低于 HeuristicKeptMin 且 Roslyn 在同一行一条 calls 都没给（语义模型绑不上），保留下来，置信度封顶 HeuristicKeptCap；</item>
/// <item>符号先按 (start_line, start_col, kind) 合并，再按 (kind, name, start_line)，都不匹配则新插入；合并时更新限定名/签名/可见性并标为 roslyn；</item>
/// <item>限定名变化时，指向该符号的所有边的 to_name 同步改写（例如 Ns.C.Find → Ns.C.Find(int)），其他文件的 tree-sitter 边继续挂在正确 id 上；</item>
/// <item>边按限定名挂接：目标已知立即挂，仓库外目标记为 external，其他先悬空，Finish 时用全部已知符号再挂一次；</item>
/// <item>处理完一个文件写 files.roslyn_at 与 roslyn_sln（当前解决方案，见 <see cref="Solution"/>），清 roslyn_miss；Finish 时补接口调用 → 实现 的派生边。</item>
/// <item>增量模式（incremental）：只有一部分文件重走。开始时把库里全部 Roslyn 符号的限定名 → id 预加载进已知表，
/// 没重走的文件里的符号照样能被挂接、做父级、做 DI 的两端；Finish 时 Roslyn 的 impl 派生边全部删掉重推（实现个数变了，置信度要跟着变）。</item>
/// </list>
/// 整个过程是一个事务（几十万条记录，预编译语句复用）；调用方按顺序 Apply 每条记录，最后 Finish。实例不可重用。
/// </summary>
public sealed class RoslynApplier(IndexDatabase db, bool incremental = false) : IDisposable
{
    private const string OverriddenEdgeKinds = "('calls','references','inherits','implements','dispatches')";
    /// <summary>保留的启发式调用边置信度上限：Roslyn 绑不上说明类型信息缺失，按类型解析（0.9）的结论也不可信。</summary>
    public const double HeuristicKeptCap = 0.7;
    /// <summary>只保留原本就有把握的启发式边：多候选同名猜测（0.3）在引用没还原的测试项目里占绝大多数，几乎都是错的（如 Assert.Equal 猜成仓库里的 Equal）。</summary>
    public const double HeuristicKeptMin = 0.7;

    private readonly IndexWriter _writer = new(db);
    private readonly IndexReader _reader = new(db);

    /// <summary>所有已处理文件里的 Roslyn 限定名 → 符号 id（partial 类型会有多个，取先出现的）。</summary>
    private readonly Dictionary<string, long> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _externals = new(StringComparer.Ordinal);
    private readonly List<DiRec> _di = [];
    private readonly HashSet<string> _processedFiles = new(StringComparer.Ordinal);

    /// <summary>接下来 Apply 的记录来自哪个解决方案（相对主根、'/' 分隔），写进 files.roslyn_sln；每个解决方案开始前由调用方设置。</summary>
    public string? Solution { get; set; }

    /// <summary>Roslyn 输出过的文件（相对主根），含库里没有、被跳过的。</summary>
    public IReadOnlySet<string> ProcessedFiles => _processedFiles;

    private IndexDatabase.Batch? _batch;
    private SqliteCommand? _insertEdge, _updateSymbol, _renameEdges, _insertSymbol, _deleteEdges, _deleteDi, _stampFile, _heuristicCalls, _insertHeuristic;

    // 当前文件
    private long? _fileId;
    private string? _fileSolution;
    private readonly Dictionary<string, long> _fileSymbols = new(StringComparer.Ordinal);
    private Dictionary<(int Line, int Col, SymbolKind Kind), SymbolRecord> _byPosition = new();
    private Dictionary<(SymbolKind Kind, string Name, int Line), SymbolRecord> _byNameLine = new();
    private readonly HashSet<long> _matched = [];
    private long? _moduleId;
    private readonly List<HeuristicEdge> _heuristic = [];
    private readonly HashSet<int> _roslynCallLines = [];
    private readonly HashSet<(long FromId, EdgeKind Kind)> _roslynTypeEdges = [];

    private int _files, _filesSkipped, _merged, _added, _edges, _edgesDropped, _errors, _kept, _typeKept, _misses;
    private bool _finished;

    public void Apply(RoslynRecord record)
    {
        if (_finished) throw new InvalidOperationException("Finish 之后不能再 Apply。");
        EnsureStarted();
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

    /// <summary>
    /// 记下 Roslyn 试过但没输出的文件：roslyn_miss = 当前哈希（只对仍未被覆盖的文件）。内容不变就不再算待更新，
    /// 免得解决方案外的文件（没进任何项目的测试代码、脚本）让每次增量都整跑 Roslyn。
    /// </summary>
    public void MarkMisses(IEnumerable<string> paths)
    {
        if (_finished) throw new InvalidOperationException("Finish 之后不能再 MarkMisses。");
        EnsureStarted();
        FinishFile();
        using var cmd = db.CreateCommand("UPDATE files SET roslyn_miss = hash WHERE path = $p AND roslyn_at IS NULL AND language = 'csharp'", ("$p", ""));
        foreach (var path in paths)
        {
            cmd.Parameters["$p"].Value = path;
            _misses += cmd.ExecuteNonQuery();
        }
    }

    private void EnsureStarted()
    {
        if (_batch is not null) return;
        _batch = db.BeginBatch();
        if (!incremental) return;
        // 按 id 升序：partial 类型取最早入库的那个，与全量时"先出现的"大体一致
        using var cmd = db.CreateCommand("SELECT qualified_name, id FROM symbols WHERE source = 'roslyn' ORDER BY id");
        using var r = cmd.ExecuteReader();
        while (r.Read()) _known.TryAdd(r.GetString(0), r.GetInt64(1));
    }

    public RoslynApplyReport Finish()
    {
        if (_finished) throw new InvalidOperationException("已经 Finish 过。");
        _finished = true;
        var timings = new CodeAtlas.Core.Diagnostics.PhaseTimings();
        EnsureStarted();
        FinishFile();
        ResolveDanglingRoslynEdges();
        timings.Lap("roslyn.finish.dangling");
        var di = WriteDiRegistrations();
        timings.Lap("roslyn.finish.di");
        _writer.ReattachDanglingEdges();
        timings.Lap("roslyn.finish.reattach");
        // 增量：没重走的文件里也有 Roslyn 调用边，它们的派生边按新的实现集合重推（新增实现后唯一实现 0.9 要变成各 0.5）
        if (incremental) db.Execute("DELETE FROM edges WHERE source = 'roslyn' AND via = $via", ("$via", ImplementationLinker.Via));
        var impl = new ImplementationLinker(db).Link();
        timings.Lap("roslyn.finish.impl");
        DisposeCommands();
        _batch.Commit();
        _batch = null;
        timings.Lap("roslyn.finish.commit");
        return new RoslynApplyReport(_files, _filesSkipped, _merged, _added, _edges, _edgesDropped, di, _errors, impl, HeuristicKept: _kept, HeuristicTypeEdgesKept: _typeKept,
            Timings: timings.Items, Incremental: incremental, Misses: _misses);
    }

    private void BeginFile(string path)
    {
        FinishFile();
        // 多个解决方案都包含同一个文件（跨仓库 ProjectReference）：第一次写回为准，后面的整文件跳过
        if (!_processedFiles.Add(path)) return;
        var file = _reader.GetFile(path);
        if (file is null) { _filesSkipped++; return; }
        _fileId = file.Id;
        _fileSolution = Solution;

        // 删之前记下可能要放回去的 tree-sitter 边，FinishFile 时按 Roslyn 给了什么决定：
        // 指向仓库内符号、有把握的调用边（按行），继承 / 实现边（按类型，目标可以是外部或悬空）
        var heuristic = Prepare(ref _heuristicCalls, """
            SELECT e.from_id, e.to_id, e.to_name, e.confidence, e.line, e.via, e.evidence_line, e.kind, e.pos, e.ctx
            FROM edges e LEFT JOIN symbols s ON s.id = e.to_id
            WHERE e.file_id = $f AND e.source <> 'roslyn'
              AND ((e.kind = 'calls' AND s.kind IS NOT NULL AND s.kind <> 'external' AND e.confidence >= $min) OR e.kind IN ('inherits', 'implements'))
            """, [("$f", file.Id), ("$min", HeuristicKeptMin)]);
        using (var r = heuristic.ExecuteReader())
        {
            while (r.Read())
            {
                if (!DbNames.TryParseEdgeKind(r.GetString(7), out var kind)) continue;
                _heuristic.Add(new HeuristicEdge(kind, r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetDouble(3), r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9)));
            }
        }

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
        foreach (var h in _heuristic)
        {
            if (h.Kind == EdgeKind.Calls ? _roslynCallLines.Contains(h.Line) : _roslynTypeEdges.Contains((h.FromId, h.Kind))) continue;
            Run(ref _insertHeuristic, """
                INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line, pos, ctx)
                VALUES ($from, $to, $toName, $kind, 'treesitter', $conf, $file, $line, $via, $ev, $pos, $ctx)
                """,
                ("$from", h.FromId), ("$to", h.ToId), ("$toName", h.ToName), ("$kind", DbNames.ToDb(h.Kind)), ("$conf", Math.Min(h.Confidence, HeuristicKeptCap)),
                ("$file", fileId), ("$line", h.Line), ("$via", h.Via), ("$ev", h.EvidenceLine), ("$pos", h.Pos), ("$ctx", h.Ctx));
            if (h.Kind == EdgeKind.Calls) _kept++; else _typeKept++;
        }
        _heuristic.Clear();
        _roslynCallLines.Clear();
        _roslynTypeEdges.Clear();
        Run(ref _stampFile, "UPDATE files SET roslyn_at = $now, roslyn_sln = $sln, roslyn_miss = NULL WHERE id = $f",
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$sln", _fileSolution), ("$f", fileId));
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

        InsertEdge(fromId, toId, toName, kind, rec.Confidence, _fileId!.Value, rec.Line, rec.Via, rec.EvidenceLine, rec.Pos, rec.Ctx);
        if (kind == EdgeKind.Calls) _roslynCallLines.Add(rec.Line);
        else if (kind is EdgeKind.Inherits or EdgeKind.Implements) _roslynTypeEdges.Add((fromId, kind));
        _edges++;
    }

    private void InsertEdge(long fromId, long? toId, string toName, EdgeKind kind, double confidence, long fileId, int line, string? via, int? evidenceLine,
        int? pos = null, string? ctx = null) =>
        Run(ref _insertEdge, """
            INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line, pos, ctx)
            VALUES ($from, $to, $toName, $kind, 'roslyn', $conf, $file, $line, $via, $ev, $pos, $ctx)
            """,
            ("$from", fromId), ("$to", toId), ("$toName", toName), ("$kind", DbNames.ToDb(kind)), ("$conf", confidence),
            ("$file", fileId), ("$line", line), ("$via", via), ("$ev", evidenceLine), ("$pos", pos is >= 0 ? pos : null), ("$ctx", ctx));

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

    private sealed record HeuristicEdge(EdgeKind Kind, long FromId, long? ToId, string ToName, double Confidence, int Line, string? Via, int? EvidenceLine, int? Pos, string? Ctx);

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
        foreach (var c in new[] { _insertEdge, _updateSymbol, _renameEdges, _insertSymbol, _deleteEdges, _deleteDi, _stampFile, _heuristicCalls, _insertHeuristic }) c?.Dispose();
        _insertEdge = _updateSymbol = _renameEdges = _insertSymbol = _deleteEdges = _deleteDi = _stampFile = _heuristicCalls = _insertHeuristic = null;
    }

    public void Dispose()
    {
        DisposeCommands();
        _batch?.Dispose(); // 没 Finish 就 Dispose：回滚
        _batch = null;
    }
}
