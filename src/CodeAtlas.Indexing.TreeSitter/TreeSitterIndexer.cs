using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodeAtlas.Core.Diagnostics;
using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Indexing.TreeSitter;

public sealed class TreeSitterIndexer(IndexDatabase db, IReadOnlyList<ILanguageAdapter> adapters, IEdgeResolver resolver, IndexOptions? options = null)
{
    private readonly IndexOptions _options = options ?? new IndexOptions();

    /// <param name="BackfillMtime">内容没变但库里的 mtime 不同或为空：补写 mtime，下次直接跳过不读。</param>
    private sealed record ParseResult(ScannedFile File, ParsedFile? Parsed, string Hash, FileStatus Status, string? Error, bool Unchanged = false, bool BackfillMtime = false);

    /// <summary>单根索引：纳入嵌套的独立克隆与子模块，不读 .codeatlas/config.json（那是 CLI / 服务用 ProjectRoots.Load 做的事）。</summary>
    public Task<IndexReport> IndexAsync(string root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default) =>
        IndexAsync(ProjectRoots.Single(root), progress, ct);

    /// <summary>
    /// 多根索引：主根 + 外部根的文件都以相对主根的路径入库。
    /// 删除检测：库里有、这次没扫到的文件删掉——但不存在的外部根（盘没挂上、目录改名）下的文件保留，免得一次误配置清空它们。
    /// </summary>
    public async Task<IndexReport> IndexAsync(ProjectRoots roots, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        var reader = new IndexReader(db);
        var writer = new IndexWriter(db);
        var adapterByLanguage = adapters.ToDictionary(a => a.LanguageId);
        var extensionToLanguage = adapters
            .SelectMany(a => a.Extensions.Select(e => (Extension: e, a.LanguageId)))
            .ToDictionary(x => x.Extension, x => x.LanguageId, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new IndexProgress("scan", 0, 0, null));
        var scanned = new RepositoryScanner(extensionToLanguage, _options.ExcludedDirectories).Scan(roots).ToList();
        var existing = reader.GetAllFiles().ToDictionary(f => f.Path, StringComparer.Ordinal);

        // 1. 磁盘上已不存在的文件（不存在的外部根下的除外）
        var scannedPaths = scanned.Select(s => s.RelativePath).ToHashSet(StringComparer.Ordinal);
        var removed = existing.Values.Where(f => !scannedPaths.Contains(f.Path) && !roots.IsUnderMissingRoot(f.Path)).ToList();
        timings.Lap("ts.scan");

        // 2. 并行：读、哈希、比较、解析。增量时大小与 mtime 都和库里一致就直接算没变，不读文件、不算哈希
        var incremental = !_options.Full && !db.NeedsRebuild;
        var results = new ConcurrentBag<ParseResult>();
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(scanned, parallel, async (file, token) =>
        {
            try
            {
                var old = incremental ? existing.GetValueOrDefault(file.RelativePath) : null;
                if (file.Size > _options.MaxFileBytes)
                {
                    var same = old is { Status: FileStatus.Skipped } && old.Size == file.Size;
                    results.Add(new ParseResult(file, null, "", FileStatus.Skipped, null, Unchanged: same));
                    return;
                }
                if (old is { Status: FileStatus.Ok, Mtime: { } mtime } && file.Mtime != 0 && mtime == file.Mtime && old.Size == file.Size)
                {
                    results.Add(new ParseResult(file, null, old.Hash, FileStatus.Ok, null, Unchanged: true));
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(file.FullPath, token);
                var hash = FileHasher.Sha256Hex(bytes);
                if (old is not null && old.Hash == hash && old.Status == FileStatus.Ok)
                {
                    results.Add(new ParseResult(file, null, hash, FileStatus.Ok, null, Unchanged: true, BackfillMtime: file.Mtime != 0 && old.Mtime != file.Mtime));
                    return;
                }
                var parsed = adapterByLanguage[file.Language].Parse(file.RelativePath, Decode(bytes));
                results.Add(new ParseResult(file, parsed, hash, FileStatus.Ok, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new ParseResult(file, null, "", FileStatus.ParseFailed, ex.Message));
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(new IndexProgress("parse", d, scanned.Count, file.RelativePath));
            }
        });
        timings.Lap("ts.parse");

        // 3. 串行分批写入
        var toWrite = results.Where(r => !r.Unchanged).OrderBy(r => r.File.RelativePath, StringComparer.Ordinal).ToList();
        // 只重解析受影响文件的引用：库里已有内容、规则没变、tsconfig / go.mod 没改过时；否则全部重解析
        var scope = incremental && existing.Count > 0 && !_options.ResolveAllReferences && !ConfigsChangedSinceLastWrite(roots, scanned)
            ? new AffectedScope(db) : null;
        var written = 0;
        using var batch = db.BeginBatch();
        scope?.CaptureBefore(toWrite.Where(r => existing.ContainsKey(r.File.RelativePath)).Select(r => existing[r.File.RelativePath])
            .Concat(removed).ToList());
        foreach (var file in removed) writer.DeleteFile(file.Id);
        foreach (var r in results.Where(r => r.BackfillMtime)) writer.SetMtime(existing[r.File.RelativePath].Id, r.File.Mtime);
        foreach (var chunk in toWrite.Chunk(_options.WriteBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var r in chunk)
            {
                if (existing.TryGetValue(r.File.RelativePath, out var old)) writer.DeleteFileContents(old.Id);
                var fileId = writer.UpsertFile(r.File.RelativePath, r.File.Language, r.Hash, r.File.Size, r.Status,
                    r.Status == FileStatus.ParseFailed || r.File.Mtime == 0 ? null : r.File.Mtime);
                scope?.Written(fileId, r.File.RelativePath, existing.ContainsKey(r.File.RelativePath), r.Parsed);
                if (r.Parsed is null) continue;
                var ids = writer.WriteSymbols(fileId, r.Parsed.Symbols, IndexSource.TreeSitter);
                writer.WriteImports(fileId, r.Parsed.Imports);
                writer.WriteReferences(r.Parsed.References.Select(reference => new PendingReference(ids[reference.FromIndex], fileId,
                    r.File.RelativePath, r.File.Language, reference.Name, reference.Kind, reference.Line, reference.Via, reference.Script,
                    reference.Pos, reference.Context)));
            }
            written += chunk.Length;
            progress?.Report(new IndexProgress("write", written, toWrite.Count, null));
        }
        timings.Lap("ts.write");

        // 4. 解析引用并重挂悬空边（增量：只删、只解析、只核对受影响文件的边）
        var affected = scope?.Compute();
        if (affected is not null) IndexReader.FillTempIds(db, "ts_affected", affected);
        db.Execute($"""
            DELETE FROM edges WHERE source = 'treesitter' AND kind <> 'contains' AND to_id IS NOT NULL
                AND file_id IN (SELECT id FROM files WHERE roslyn_at IS NULL AND path <> '<external>'
                    {(affected is null ? "" : "AND id IN (SELECT id FROM temp.ts_affected)")})
            """);
        var pending = reader.GetPendingReferences(affected);
        IReadOnlyList<PendingReference> support = affected is null || pending.Count == 0 ? [] : reader.GetSupportReferences(affected);
        progress?.Report(new IndexProgress("resolve", 0, pending.Count, null));
        timings.Lap($"ts.resolve.load({pending.Count})");
        if (affected is null || pending.Count > 0)
        {
            // tsconfig / jsconfig 的 paths 别名每次都按全部扫描到的文件重新找（增量时未变文件的配置也要算进来）
            // go.mod 同理（Go 的 import 路径按最近的 go.mod 解析）
            var goModules = GoModuleResolver.Discover(scanned.Where(f => f.Language == "go").Select(f => (f.RelativePath, f.FullPath)));
            await resolver.ResolveAsync(new ResolveContext(db, pending, TsConfigReader.Discover(scanned))
                { GoModules = goModules, SupportReferences = support }, ct);
        }
        timings.Lap("ts.resolve.match");
        writer.ReconcileResolvedReferences(affected);
        timings.Lap("ts.resolve.reconcile");
        writer.ReattachDanglingEdges();
        timings.Lap("ts.resolve.reattach");
        ct.ThrowIfCancellationRequested();
        batch.Commit();
        timings.Lap("ts.commit");
        progress?.Report(new IndexProgress("resolve", pending.Count, pending.Count, null));
        if (_options.Full || !db.NeedsRebuild) db.MarkCurrentVersion();

        var list = results.ToList();
        return new IndexReport(
            FilesScanned: scanned.Count,
            FilesParsed: list.Count(r => r.Parsed is not null),
            FilesFailed: list.Count(r => r.Status == FileStatus.ParseFailed),
            FilesSkipped: list.Count(r => r.Status == FileStatus.Skipped),
            FilesRemoved: removed.Count,
            FilesUnchanged: list.Count(r => r.Unchanged),
            TotalSymbols: db.Scalar<long>("SELECT COUNT(*) FROM symbols"),
            TotalEdges: db.Scalar<long>("SELECT COUNT(*) FROM edges"),
            Elapsed: stopwatch.Elapsed,
            Errors: list.Where(r => r.Error is not null).Select(r => (r.File.RelativePath, r.Error!)).ToList(),
            Timings: timings.Items);
    }

    private static readonly string[] ConfigFileNames = ["tsconfig.json", "jsconfig.json", "go.mod"];

    /// <summary>
    /// tsconfig / jsconfig（paths 别名）或 go.mod（模块路径）比索引库上次写入新：它们不是被索引的源文件，改了不会让任何文件"变"，
    /// 却改变全部 import 的解析，得全部重解析。以库文件（及 -wal）的修改时间为"上次写入"；内存库无从比较，当作没改。
    /// 已知盲区：删掉一个配置文件看不出来（要 --full）。
    /// </summary>
    private bool ConfigsChangedSinceLastWrite(ProjectRoots roots, IReadOnlyList<ScannedFile> scanned)
    {
        var dbPath = db.Connection.DataSource;
        if (string.IsNullOrEmpty(dbPath) || dbPath == ":memory:" || !File.Exists(dbPath)) return false;
        var lastWrite = new[] { dbPath, dbPath + "-wal" }.Where(File.Exists).Max(File.GetLastWriteTimeUtc);
        var rootSet = roots.All.Select(Path.GetFullPath).ToHashSet(ProjectRoots.PathComparer);
        var seen = new HashSet<string>(ProjectRoots.PathComparer);
        foreach (var file in scanned)
        {
            if (file.Language is not ("typescript" or "tsx" or "javascript" or "go")) continue;
            for (var dir = Path.GetDirectoryName(file.FullPath); dir is not null && seen.Add(dir); dir = Path.GetDirectoryName(dir))
            {
                foreach (var name in ConfigFileNames)
                    if (File.GetLastWriteTimeUtc(Path.Combine(dir, name)) > lastWrite) return true; // 不存在时是 1601 年
                if (rootSet.Contains(dir)) break;
            }
        }
        return false;
    }

    /// <summary>
    /// 增量时这次要重解析引用的文件集合。改动 / 删除的文件在写入前记下"之前"的样子，写入后与新解析结果比较：
    /// <list type="bullet">
    /// <item>写入（新增 / 改动）的文件本身；</item>
    /// <item>依赖方：有边指向改动 / 删除文件里符号的文件（任何来源）；</item>
    /// <item>名字变化：增删或签名变了的符号（按限定名 + 种类 + 签名比）的名字与限定名，以及前后 handles 的目标名——引用了这些名字的文件；</item>
    /// <item>父类型变了：新旧父类型的名字与它们的成员名、父类型变了的类型名——引用了这些名字的文件；</item>
    /// <item>新增 / 删除，或 import 变了（重导出）的文件：文件名（去扩展名）与所在目录名，name / import_source / imported_name / import 目标里含这些词的文件。</item>
    /// </list>
    /// 其余文件的引用解析只依赖符号表（全量加载）、自己的 import 与别的文件的父类型等事实（SupportReferences 补齐），结果不变。
    /// </summary>
    private sealed class AffectedScope(IndexDatabase db)
    {
        private sealed record Facts(List<(string Name, string Key)> Symbols, HashSet<string> Supers, HashSet<string> Handles, HashSet<string> Imports);

        private readonly Dictionary<string, Facts> _before = new(StringComparer.Ordinal);
        private readonly HashSet<string> _written = new(StringComparer.Ordinal);
        private readonly HashSet<long> _files = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly HashSet<string> _superNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _words = new(StringComparer.Ordinal);

        /// <summary>写入前调用：记下改动与删除的文件现在的符号、父类型、handles、import，以及依赖它们的文件。</summary>
        public void CaptureBefore(IReadOnlyList<FileRecord> files)
        {
            if (files.Count == 0) return;
            var byId = files.ToDictionary(f => f.Id);
            IndexReader.FillTempIds(db, "ts_changed", byId.Keys);
            // 依赖方：指向这些文件里符号的边所在的文件（DeleteFileContents 之后这些边就悬空、查不到来源了）
            _files.UnionWith(db.Query<long>("""
                SELECT DISTINCT e.file_id FROM edges e JOIN symbols s ON s.id = e.to_id
                WHERE s.file_id IN (SELECT id FROM temp.ts_changed)
                """));
            foreach (var f in files) _before[f.Path] = new Facts([], [], [], []);
            using (var cmd = db.CreateCommand("""
                SELECT file_id, name, qualified_name || '|' || kind || '|' || COALESCE(signature, '') FROM symbols
                WHERE file_id IN (SELECT id FROM temp.ts_changed)
                """))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) _before[byId[r.GetInt64(0)].Path].Symbols.Add((r.GetString(1), r.GetString(2)));
            using (var cmd = db.CreateCommand("""
                SELECT r.file_id, s.qualified_name, r.name, r.kind, COALESCE(r.via, ''), COALESCE(r.import_source, ''), COALESCE(r.imported_name, '')
                FROM source_references r JOIN symbols s ON s.id = r.from_id
                WHERE r.file_id IN (SELECT id FROM temp.ts_changed)
                  AND (r.via = 'handles' OR r.kind IN ('inherits', 'implements', 'imports') OR r.import_source IS NOT NULL)
                """))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    AddReference(_before[byId[r.GetInt64(0)].Path], r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));
            using (var cmd = db.CreateCommand("SELECT file_id, target FROM imports WHERE file_id IN (SELECT id FROM temp.ts_changed)"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) _before[byId[r.GetInt64(0)].Path].Imports.Add("import:" + r.GetString(1));
        }

        /// <summary>写入一个文件之后调用：与之前比较，记下受影响的名字与词。</summary>
        public void Written(long fileId, string path, bool existed, ParsedFile? parsed)
        {
            _files.Add(fileId);
            _written.Add(path);
            var after = new Facts([], [], [], []);
            if (parsed is not null)
            {
                foreach (var s in parsed.Symbols) after.Symbols.Add((s.Name, s.QualifiedName + "|" + DbNames.ToDb(s.Kind) + "|" + (s.Signature ?? "")));
                foreach (var r in parsed.References)
                    if (r.Via == "handles" || r.Kind is EdgeKind.Inherits or EdgeKind.Implements or EdgeKind.Imports || r.Script?.ImportSource is not null)
                        AddReference(after, parsed.Symbols[r.FromIndex].QualifiedName, r.Name, DbNames.ToDb(r.Kind), r.Via ?? "",
                            r.Script?.ImportSource ?? "", r.Script?.ImportedName ?? "");
                foreach (var i in parsed.Imports) after.Imports.Add("import:" + i.Target);
            }
            if (existed && _before.TryGetValue(path, out var before)) Compare(path, before, after);
            else AddedOrRemoved(path, after);
        }

        private static void AddReference(Facts facts, string fromQualified, string name, string kind, string via, string importSource, string importedName)
        {
            if (via == "handles") facts.Handles.Add(name);
            if (kind is "inherits" or "implements") facts.Supers.Add(fromQualified + "|" + kind + "|" + name);
            if (kind == "imports" || importSource.Length > 0) facts.Imports.Add(kind + "|" + name + "|" + importSource + "|" + importedName);
        }

        private void AddedOrRemoved(string path, Facts facts)
        {
            foreach (var (name, key) in facts.Symbols) AddName(name, key);
            _names.UnionWith(facts.Handles);
            AddWords(path);
        }

        private void Compare(string path, Facts before, Facts after)
        {
            var oldKeys = before.Symbols.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            var newKeys = after.Symbols.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, key) in before.Symbols.Where(s => !newKeys.Contains(s.Key))) AddName(name, key);
            foreach (var (name, key) in after.Symbols.Where(s => !oldKeys.Contains(s.Key))) AddName(name, key);
            // 新增 Handler 时发送方要补 dispatch 边、删掉时要去掉：handles 的目标名前后都算
            _names.UnionWith(before.Handles);
            _names.UnionWith(after.Handles);
            if (!before.Supers.SetEquals(after.Supers))
            {
                var typeNames = before.Symbols.Concat(after.Symbols).GroupBy(s => s.Key[..s.Key.IndexOf('|')], StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
                foreach (var super in before.Supers.Concat(after.Supers))
                {
                    var parts = super.Split('|');
                    var written = parts[2];
                    var bare = written[(written.LastIndexOf('.') + 1)..];
                    if (bare.IndexOf('<') is var lt and > 0) bare = bare[..lt];
                    _superNames.Add(bare);
                    _names.Add(written);
                    _names.Add(bare);
                    if (typeNames.TryGetValue(parts[0], out var typeName)) _names.Add(typeName);
                }
            }
            if (!before.Imports.SetEquals(after.Imports)) AddWords(path);
        }

        /// <summary>名字与限定名都记：TS 等按名字引用，C# using / Python import 写的是限定名。</summary>
        private void AddName(string name, string key)
        {
            _names.Add(name);
            _names.Add(key[..key.IndexOf('|')]);
        }

        /// <summary>文件名（去扩展名；index / __init__ / mod / package-info 不算）与所在目录名，小写。</summary>
        private void AddWords(string path)
        {
            var slash = path.LastIndexOf('/');
            var dir = slash >= 0 ? path[..slash] : "";
            var dirName = dir[(dir.LastIndexOf('/') + 1)..];
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length > 0 && stem is not ("index" or "__init__" or "mod" or "package-info")) _words.Add(stem.ToLowerInvariant());
            if (dirName is not ("" or "." or "..")) _words.Add(dirName.ToLowerInvariant());
        }

        /// <summary>写完之后调用：删除的文件（没有"之后"）名字全算变化；查出引用了变化名字 / 词的文件，并上写入的与依赖方。</summary>
        public IReadOnlyCollection<long> Compute()
        {
            foreach (var (path, before) in _before)
                if (!_written.Contains(path)) AddedOrRemoved(path, before);
            if (_superNames.Count > 0)
            {
                // 父类型变了：经它们继承来的成员名（x.Inherited() 的解析可能变）
                IndexReader.FillTempNames(db, "ts_super_names", _superNames);
                _names.UnionWith(db.Query<string>("""
                    SELECT DISTINCT c.name FROM symbols p JOIN symbols c ON c.parent_id = p.id
                    WHERE p.name IN (SELECT name FROM temp.ts_super_names)
                    """));
            }
            if (_names.Count > 0)
            {
                IndexReader.FillTempNames(db, "ts_names", _names);
                _files.UnionWith(db.Query<long>("SELECT DISTINCT file_id FROM source_references WHERE name IN (SELECT name FROM temp.ts_names)"));
            }
            foreach (var word in _words)
                _files.UnionWith(db.Query<long>("""
                    SELECT DISTINCT file_id FROM source_references
                    WHERE instr(lower(name), $w) > 0 OR instr(lower(import_source), $w) > 0 OR instr(lower(imported_name), $w) > 0
                    UNION SELECT file_id FROM imports WHERE instr(lower(target), $w) > 0
                    """, ("$w", word)));
            return _files;
        }
    }

    /// <summary>按 BOM 识别编码，默认 UTF-8；无效字节用替换字符，不抛异常。</summary>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
