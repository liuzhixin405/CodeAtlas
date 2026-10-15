using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Indexing.TreeSitter;

public sealed class TreeSitterIndexer(IndexDatabase db, IReadOnlyList<ILanguageAdapter> adapters, IEdgeResolver resolver, IndexOptions? options = null)
{
    private readonly IndexOptions _options = options ?? new IndexOptions();

    private sealed record ParseResult(ScannedFile File, ParsedFile? Parsed, string Hash, FileStatus Status, string? Error, bool Unchanged = false);

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
        if (removed.Count > 0)
        {
            using var batch = db.BeginBatch();
            foreach (var f in removed) writer.DeleteFile(f.Id);
            batch.Commit();
        }

        // 2. 并行：读、哈希、比较、解析
        var results = new ConcurrentBag<ParseResult>();
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(scanned, parallel, async (file, token) =>
        {
            try
            {
                if (file.Size > _options.MaxFileBytes)
                {
                    results.Add(new ParseResult(file, null, "", FileStatus.Skipped, null));
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(file.FullPath, token);
                var hash = FileHasher.Sha256Hex(bytes);
                if (!_options.Full && existing.TryGetValue(file.RelativePath, out var old) && old.Hash == hash && old.Status == FileStatus.Ok)
                {
                    results.Add(new ParseResult(file, null, hash, FileStatus.Ok, null, Unchanged: true));
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

        // 3. 串行分批写入
        var pending = new List<PendingReference>();
        var toWrite = results.Where(r => !r.Unchanged).OrderBy(r => r.File.RelativePath, StringComparer.Ordinal).ToList();
        var written = 0;
        foreach (var chunk in toWrite.Chunk(_options.WriteBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            using var batch = db.BeginBatch();
            foreach (var r in chunk)
            {
                if (existing.TryGetValue(r.File.RelativePath, out var old)) writer.DeleteFileContents(old.Id);
                var fileId = writer.UpsertFile(r.File.RelativePath, r.File.Language, r.Hash, r.File.Size, r.Status);
                if (r.Parsed is null) continue;
                var ids = writer.WriteSymbols(fileId, r.Parsed.Symbols, IndexSource.TreeSitter);
                writer.WriteImports(fileId, r.Parsed.Imports);
                foreach (var reference in r.Parsed.References)
                    pending.Add(new PendingReference(ids[reference.FromIndex], fileId, r.File.RelativePath, r.File.Language, reference.Name, reference.Kind, reference.Line, reference.Via, reference.Script,
                        reference.Pos, reference.Context));
            }
            batch.Commit();
            written += chunk.Length;
            progress?.Report(new IndexProgress("write", written, toWrite.Count, null));
        }

        // 4. 解析引用并重挂悬空边
        progress?.Report(new IndexProgress("resolve", 0, pending.Count, null));
        using (var batch = db.BeginBatch())
        {
            // tsconfig / jsconfig 的 paths 别名每次都按全部扫描到的文件重新找（增量时未变文件的配置也要算进来）
            // go.mod 同理（Go 的 import 路径按最近的 go.mod 解析）
            var goModules = GoModuleResolver.Discover(scanned.Where(f => f.Language == "go").Select(f => (f.RelativePath, f.FullPath)));
            await resolver.ResolveAsync(new ResolveContext(db, pending, TsConfigReader.Discover(scanned)) { GoModules = goModules }, ct);
            writer.ReattachDanglingEdges();
            batch.Commit();
        }
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
            Errors: list.Where(r => r.Error is not null).Select(r => (r.File.RelativePath, r.Error!)).ToList());
    }

    /// <summary>按 BOM 识别编码，默认 UTF-8；无效字节用替换字符，不抛异常。</summary>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
