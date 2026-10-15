using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Server;

/// <summary>
/// 服务的核心：一个写连接给索引器、一个读连接给查询（WAL 模式下互不阻塞）。
/// 索引在后台串行运行；所有查询在读锁下串行执行（毫秒级，够用）。
/// </summary>
public sealed class IndexService : IDisposable
{
    private readonly ServerOptions _options;
    private readonly IndexDatabase _writeDb;
    private readonly IndexDatabase _readDb;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly List<ILanguageAdapter> _adapters = LanguageAdapters.Create();
    private readonly object _stateLock = new();
    private Task? _indexTask;
    private IndexProgress? _progress;
    private IndexReportDto? _lastReport;
    private string? _lastError;

    public IndexService(ServerOptions options)
    {
        _options = options;
        var dir = Path.GetDirectoryName(options.DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writeDb = IndexDatabase.Open(options.DbPath);
        _readDb = IndexDatabase.Open(options.DbPath);
        _roots = options.LoadRoots();
    }

    public string RepoRoot => _options.RepoRoot;

    /// <summary>当前的根集合（主根 + 外部根）；每次索引开始时按 --project 与 .codeatlas/config.json 重新读。</summary>
    private ProjectRoots _roots;
    public ProjectRoots Roots { get { lock (_stateLock) return _roots; } }

    public HealthDto Health() => Read(r =>
    {
        var files = _readDb.Scalar<long>("SELECT COUNT(*) FROM files WHERE path <> $ext", ("$ext", IndexWriter.ExternalFilePath));
        var symbols = _readDb.Scalar<long>("SELECT COUNT(*) FROM symbols WHERE kind <> 'external'");
        var indexedAt = _readDb.Query<string>("SELECT MAX(indexed_at) FROM files WHERE path <> $ext", ("$ext", IndexWriter.ExternalFilePath)).FirstOrDefault();
        var roslynAt = _readDb.Query<string>("SELECT COALESCE(MAX(roslyn_at), '') FROM files").FirstOrDefault();
        var stale = _readDb.Scalar<long>("SELECT COUNT(*) FROM files WHERE language = 'csharp' AND roslyn_at IS NULL");
        return new HealthDto(_options.RepoRoot, _options.DbPath, files, symbols, string.IsNullOrEmpty(indexedAt) ? null : indexedAt, _writeDb.NeedsRebuild,
            string.IsNullOrEmpty(roslynAt) ? null : roslynAt, stale);
    });

    /// <summary>启动一次索引；已在跑返回 false。tree-sitter 之后按 roslyn 参数决定是否接着跑 Roslyn（见 IndexRequest）。</summary>
    public bool TryStartIndex(bool full, bool? roslyn = null)
    {
        lock (_stateLock)
        {
            if (_indexTask is { IsCompleted: false }) return false;
            _progress = new IndexProgress("scan", 0, 0, null);
            _lastError = null;
            var roots = _roots = _options.LoadRoots();
            var indexer = new TreeSitterIndexer(_writeDb, _adapters, new HeuristicEdgeResolver(), new IndexOptions { Full = full });
            var progress = new Progress<IndexProgress>(p => _progress = p);
            _indexTask = Task.Run(async () =>
            {
                try
                {
                    var r = await indexer.IndexAsync(roots, progress);
                    var report = new IndexReportDto(r.FilesScanned, r.FilesParsed, r.FilesFailed, r.FilesSkipped, r.FilesRemoved, r.FilesUnchanged,
                        r.TotalSymbols, r.TotalEdges, r.Elapsed.TotalSeconds);
                    lock (_stateLock) _lastReport = report;
                    if (ShouldRunRoslyn(full, roslyn))
                    {
                        var roslynReport = await RunRoslynAsync(roots);
                        lock (_stateLock) _lastReport = report with { Roslyn = roslynReport };
                    }
                    // 容器图事实：几秒钟的文件与文本扫描；失败只记错误不影响索引
                    try { lock (_stateLock) _progress = new IndexProgress("arch", 0, 0, null); ArchitectureScanner.ScanAndStore(roots, _writeDb); }
                    catch (Exception ex) { lock (_stateLock) _lastError = "容器图扫描失败：" + ex.Message; }
                    // git 行为统计：没有 git 时 Error 里写原因，DSM 的叠加层为空
                    try { lock (_stateLock) _progress = new IndexProgress("git", 0, 0, null); GitStorage.Replace(_writeDb, GitHistory.Run(roots)); }
                    catch (Exception ex) { lock (_stateLock) _lastError = "git 统计失败：" + ex.Message; }
                    // 入口与路由匹配：靠索引里的符号和边，所以放在最后；失败同样只记错误
                    try { lock (_stateLock) _progress = new IndexProgress("entries", 0, 0, null); ArchitecturePostProcess.Run(roots, _writeDb); }
                    catch (Exception ex) { lock (_stateLock) _lastError = "入口识别失败：" + ex.Message; }
                }
                catch (Exception ex)
                {
                    lock (_stateLock) _lastError = ex.Message;
                }
                finally
                {
                    lock (_stateLock) _progress = null;
                    lock (_mapCache) { _mapCache.Clear(); _pipelineCache.Clear(); _archCache = null; _dsmCache.Clear(); _entriesCache.Clear(); _flowCache.Clear(); _mapData = null; _fileHashes = null; }
                }
            });
            return true;
        }
    }

    private bool ShouldRunRoslyn(bool full, bool? requested)
    {
        if (!_options.Roslyn || requested == false) return false;
        if (requested == true || full) return true;
        // 自动：从没跑过 Roslyn 的库（首次索引）才跑；保存文件触发的增量不跑，等下次全量
        return _writeDb.Scalar<long>("SELECT COUNT(*) FROM files WHERE roslyn_at IS NOT NULL") == 0
            && _writeDb.Scalar<long>("SELECT COUNT(*) FROM files WHERE language = 'csharp'") > 0;
    }

    /// <summary>主根、外部根、被纳入的嵌套仓库各找一个解决方案，依次跑 worker 写回同一个库；部分失败不影响其余（失败原因进 error）。</summary>
    private async Task<RoslynReportDto> RunRoslynAsync(ProjectRoots roots)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var worker = RoslynRunner.FindWorker();
        if (worker is null) return new RoslynReportDto(0, 0, 0, 0, 0, 0, 0, 0, $"没有找到 {RoslynRunner.WorkerName}（应与 codeatlas 同目录），跳过 Roslyn 精确语义。");
        var solutions = RoslynRunner.DiscoverSolutions(roots);
        if (solutions.Count == 0) return new RoslynReportDto(0, 0, 0, 0, 0, 0, 0, 0, "仓库里没有找到 .sln/.slnx，跳过 Roslyn 精确语义。");
        if (await RoslynRunner.ProbeAsync(worker) is { } unavailable) return new RoslynReportDto(0, 0, 0, 0, 0, 0, 0, 0, unavailable, Unavailable: true);
        lock (_stateLock) _progress = new IndexProgress("roslyn", 0, 0, null);
        try
        {
            var r = await RoslynRunner.RunAndApplyAsync(_writeDb, worker, solutions, roots,
                p => { lock (_stateLock) _progress = new IndexProgress("roslyn", p.Done, p.Total, p.Current); },
                null, CancellationToken.None);
            var partial = r.FailedSolutions is { Count: > 0 } failed ? $"{failed.Count}/{r.Solutions} 个解决方案失败：" + string.Join("；", failed) : null;
            return new RoslynReportDto(r.Files, r.FilesSkipped, r.SymbolsMerged, r.SymbolsAdded, r.Edges, r.DiRegistrations, r.Errors, sw.Elapsed.TotalSeconds, partial);
        }
        catch (Exception ex)
        {
            return new RoslynReportDto(0, 0, 0, 0, 0, 0, 0, sw.Elapsed.TotalSeconds, ex.Message);
        }
    }

    public IndexStatusDto Status()
    {
        lock (_stateLock)
        {
            var running = _indexTask is { IsCompleted: false };
            var p = running ? _progress : null;
            return new IndexStatusDto(running, p?.Phase, p?.Done ?? 0, p?.Total ?? 0, _lastReport, _lastError);
        }
    }

    /// <summary>测试用：等待当前索引结束。</summary>
    public Task WaitForIndexAsync()
    {
        Task? t;
        lock (_stateLock) t = _indexTask;
        return t ?? Task.CompletedTask;
    }

    public IReadOnlyList<SymbolDto> Search(string query, int limit) => Read(r =>
    {
        var files = new Dictionary<long, string>();
        return r.SearchSymbols(query, limit).Select(s => ToDto(r, s, files)).ToList();
    });

    public SymbolDto? GetSymbol(long id) => Read(r => r.GetSymbol(id) is { } s ? ToDto(r, s, new Dictionary<long, string>()) : null);

    public SymbolDto? SymbolAt(string path, int line, int col) => Read(r =>
    {
        var relative = NormalizeToRelative(path);
        if (relative is null) return null;
        var file = r.GetFile(relative);
        if (file is null) return null;
        var s = r.GetSymbolAt(file.Id, line, col);
        return s is null ? null : ToDto(r, s, new Dictionary<long, string> { [file.Id] = file.Path });
    });

    /// <summary>符号不存在返回 null。</summary>
    public IReadOnlyList<NeighborDto>? Neighbors(long id, Direction direction, IReadOnlySet<EdgeKind>? kinds) => Read(r =>
    {
        if (r.GetSymbol(id) is null) return null;
        var files = new Dictionary<long, string>();
        var seen = new HashSet<long>();
        var list = new List<NeighborDto>();
        foreach (var n in r.GetNeighbors(id, direction, kinds))
        {
            if (!seen.Add(n.Edge.Id)) continue;
            var dir = n.Edge.FromId == id ? "out" : "in";
            var ambiguous = Math.Abs(n.Edge.Confidence - 0.3) < 0.001 ? $"{DbNames.ToDb(n.Edge.Kind)}:{n.Edge.Line}" : null;
            SymbolDto? other = null;
            if (n.Other is { } o)
            {
                other = ToDto(r, o, files);
                if (o.Kind is SymbolKind.Method or SymbolKind.Function)
                    other = other with { Callers = (int)r.CountIncoming(o.Id, EdgeKind.Calls) };
            }
            list.Add(new NeighborDto(n.Edge.Id, DbNames.ToDb(n.Edge.Kind), dir, n.Edge.Confidence, n.Edge.Line, ambiguous,
                other, n.Edge.ToName, n.Edge.Via, n.Edge.EvidenceLine, n.Edge.Pos, n.Edge.Ctx));
        }
        return list;
    });

    /// <summary>调用大纲：这个方法按源码顺序的一层调用（见 OutlineBuilder）。只查它自己的出边与相关符号，不走整库缓存。符号不存在返回 null。</summary>
    public OutlineDto? Outline(long id, OutlineOptions options) => Read(_ => OutlineQuery.Build(_readDb, id, options));

    /// <summary>模块关系：以这个符号所属的类（或模块）为中心，类与类之间的依赖（见 RelationsBuilder）。按需查，不走整库缓存。符号不存在返回 null。</summary>
    public RelationsDto? Relations(long id, RelationsOptions options) => Read(_ => RelationsQuery.Build(_readDb, id, options));

    /// <summary>快速跳转：文件里每个方法的调用计数（见 LensQuery）。path 相对仓库根或绝对路径；不在索引里返回空 Symbols。</summary>
    public FileLensDto Lens(string path) => Read(_ => LensQuery.Build(_readDb, Roots, path, NormalizeToRelative(path)));

    /// <summary>影响范围：这个符号的直接调用方，entries=true 时另附往上能走到的入口（见 ImpactBuilder）。按需查入边，不走整库缓存。符号不存在返回 null。</summary>
    public ImpactDto? Impact(long id, ImpactOptions options) => Read(_ => ImpactQuery.Build(_readDb, id, options));

    private readonly Dictionary<string, MapDto> _mapCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PipelineDto> _pipelineCache = new(StringComparer.Ordinal);

    /// <summary>项目地图。整库符号与边加载一次后按 root 缓存；索引结束时清空。</summary>
    public MapDto Map(string root) => Read(r =>
    {
        lock (_mapCache)
        {
            if (_mapCache.TryGetValue(root, out var cached)) return cached;
            var (symbols, edges) = Data(r);
            var map = MapBuilder.Build(symbols, edges, root);
            _mapCache[root] = map;
            return map;
        }
    });

    /// <summary>项目地图的管道视图；root 为空表示自动推导。缓存与清空策略同 Map。</summary>
    public PipelineDto Pipeline(string root) => Read(r =>
    {
        lock (_mapCache)
        {
            if (_pipelineCache.TryGetValue(root, out var cached)) return cached;
            var (symbols, edges) = Data(r);
            var di = r.GetDiRegistrations().Select((d, i) => new MapDi(d.InterfaceId, d.ImplId, i)).ToList();
            var pipeline = PipelineBuilder.Build(symbols, edges, di, root);
            _pipelineCache[root] = pipeline;
            return pipeline;
        }
    });

    private ArchDto? _archCache;

    /// <summary>L1 容器图：索引时写好的事实 + 每个容器的根命名空间（钻取用）。</summary>
    public ArchDto Architecture() => Read(r =>
    {
        lock (_mapCache)
        {
            if (_archCache is not null) return _archCache;
            var model = ArchStorage.Read(_readDb);
            var outboundCount = model.Outbound.GroupBy(o => o.ContainerKey).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var nodes = model.Nodes.Select(n => new ArchNodeDto(n.Key, n.Kind.ToString().ToLowerInvariant(), n.SubKind, n.Name, n.Path, n.Source.ToString().ToLowerInvariant(),
                n.Evidence, n.Members, n.Ports, n.Kind == ArchNodeKind.Container ? NamespaceRoots(n) : [], outboundCount.GetValueOrDefault(n.Key))).ToList();
            var edges = model.Edges.Select(e => new ArchEdgeDto(e.FromKey, e.ToKey, e.Protocol, e.Confidence, e.Source.ToString().ToLowerInvariant(), e.Evidence, e.ConfigKey, e.Url)).ToList();
            string? note = null;
            if (nodes.Count == 0)
                note = _readDb.Scalar<long>("SELECT COUNT(*) FROM files") == 0 ? "还没有索引" : "没有发现可部署单元（可执行项目、前端应用）：这个仓库只有类库，显示整库的模块依赖（组件矩阵）";
            return _archCache = new ArchDto(nodes, edges, model.Conflicts, note);
        }
    });

    /// <summary>容器成员目录下 namespace 符号的最长公共前缀；没有 C# 符号（前端）时用容器目录本身（脚本按目录分组）。</summary>
    private List<string> NamespaceRoots(ArchNode container)
    {
        var namespaces = new List<string>();
        foreach (var dir in container.Members)
        {
            var like = dir.Length == 0 ? "%" : dir.Replace("%", "\\%").Replace("_", "\\_") + "/%";
            // 成员 "" 是主根：外部根的 ../… 文件不算（多项目时主根上的前端容器不能吞掉后端的命名空间）
            namespaces.AddRange(_readDb.Query<string>(
                "SELECT DISTINCT s.qualified_name FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.kind = 'namespace' AND f.path LIKE $p ESCAPE '\\' AND ($p <> '%' OR f.path NOT LIKE '../%')", ("$p", like)));
        }
        if (namespaces.Count == 0) return container.Path is null ? [] : [container.Path];
        // 取"多数派"的公共前缀：一两个漏写前缀的命名空间（或全局命名空间）不能把根拉成空串
        var dominant = namespaces.Where(n => n.Length > 0).GroupBy(n => n.Split('.')[0]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        if (dominant is null) return container.Path is null ? [] : [container.Path];
        var prefix = PipelineBuilder.CommonPrefix(dominant);
        return [prefix.Length == 0 ? dominant.Key : prefix];
    }

    private readonly Dictionary<(string, string), DsmDto> _dsmCache = new();

    /// <summary>L2：某个容器内的组件 DSM。容器不存在返回 null；键是 <see cref="DsmBuilder.RepoKey"/> 时按整个仓库算（纯类库仓库的第一屏）。</summary>
    public DsmDto? Dsm(string containerKey, string root) => Read(r =>
    {
        lock (_mapCache)
        {
            if (_dsmCache.TryGetValue((containerKey, root), out var cached)) return cached;
            var container = ArchStorage.Read(_readDb).Nodes.FirstOrDefault(n => n.Key == containerKey && n.Kind == ArchNodeKind.Container)
                            ?? (containerKey == DsmBuilder.RepoKey ? DsmBuilder.RepoContainer(Roots) : null); // 纯类库仓库：整库伪容器
            if (container is null) return null;
            var (symbols, edges) = Data(r);
            var git = GitStorage.Read(_readDb);
            var dsm = DsmBuilder.Build(symbols, edges, container, root, git.Files.Count > 0 ? git : null, rules: DsmBuilder.LoadRules(_readDb));
            if (git.Files.Count == 0 && git.Error is not null) dsm = dsm with { Note = (dsm.Note is null ? "" : dsm.Note + "；") + "git 统计不可用：" + git.Error };
            return _dsmCache[(containerKey, root)] = dsm;
        }
    });

    private readonly Dictionary<string, List<EntryDto>> _entriesCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(long, int), FlowDto?> _flowCache = new();

    /// <summary>L3：某容器的入口清单。容器不存在或没有入口都返回空表；kind / q 在缓存的全表上过滤。</summary>
    public IReadOnlyList<EntryDto> Entries(string containerKey, string? kind, string? q) => Read(r =>
    {
        List<EntryDto>? all;
        lock (_mapCache)
        {
            if (!_entriesCache.TryGetValue(containerKey, out all))
            {
                var (entries, _) = EntryStorage.Read(_readDb);
                var mine = entries.Where(e => e.ContainerKey == containerKey).ToList();
                var files = new Dictionary<long, string>();
                all = [];
                foreach (var e in mine)
                {
                    var s = r.GetSymbol(e.SymbolId);
                    if (s is null) continue; // 索引后符号被改掉了：跳过，下次索引会重算
                    var dto = ToDto(r, s, files);
                    all.Add(new EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, dto.Name, dto.QualifiedName, e.File, e.Line));
                }
                all = all.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
                _entriesCache[containerKey] = all;
            }
        }
        IEnumerable<EntryDto> result = all;
        if (!string.IsNullOrEmpty(kind)) result = result.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
            result = result.Where(e => (e.Route?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                                    || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                    || e.QualifiedName.Contains(q, StringComparison.OrdinalIgnoreCase));
        return result.ToList();
    });

    /// <summary>L3：从一个入口出发的时序树。没有这个入口返回 null。depth 夹到 1..20，默认 12。</summary>
    public FlowDto? Flow(long symbolId, int? depth) => Read(r =>
    {
        var maxDepth = Math.Clamp(depth ?? FlowBuilder.DefaultMaxDepth, 1, 20);
        lock (_mapCache)
        {
            if (_flowCache.TryGetValue((symbolId, maxDepth), out var cached)) return cached;
            var (entries, matches) = EntryStorage.Read(_readDb);
            var entry = entries.FirstOrDefault(e => e.SymbolId == symbolId);
            if (entry is null) return _flowCache[(symbolId, maxDepth)] = null;
            var arch = ArchStorage.Read(_readDb);
            var containers = arch.Nodes.Where(n => n.Kind == ArchNodeKind.Container).ToList();
            var externals = arch.Nodes.Where(n => n.Kind == ArchNodeKind.External).ToList();
            var (symbols, edges) = Data(r);
            // 出口：没对上路由的 HTTP 调用画成 http 叶子（单独打开前端时后端不在索引里）
            return _flowCache[(symbolId, maxDepth)] = FlowBuilder.Build(symbols, edges, entry, containers, matches, entries, maxDepth,
                outbound: arch.Outbound, externalNodes: externals);
        }
    });

    private IReadOnlyDictionary<string, string>? _fileHashes;

    /// <summary>全部已索引文件的内容哈希（path → hash）：叙述缓存按它判断"相关代码变没变"，比 git commit 准（未提交的改动也算）。</summary>
    public IReadOnlyDictionary<string, string> FileHashes() => Read(r =>
    {
        lock (_mapCache)
            return _fileHashes ??= r.GetAllFiles().ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal);
    });

    /// <summary>
    /// DSM 组件覆盖的文件：容器成员目录里，限定名落在组件键（命名空间前缀）下、或路径落在组件键（目录）下的符号所在文件。
    /// 组件键为空（DSM 根层"全局命名空间"一类）时返回空。
    /// </summary>
    public IReadOnlyCollection<string> ComponentFiles(IReadOnlyList<string> members, string componentKey) => Read(r =>
    {
        if (componentKey.Length == 0) return (IReadOnlyCollection<string>)[];
        lock (_mapCache)
        {
            var (symbols, _) = Data(r);
            var ns = componentKey + ".";
            var dir = componentKey.TrimEnd('/') + "/";
            return symbols
                .Where(s => s.Kind != SymbolKind.External && members.Any(m => Core.Architecture.Rules.RuleContext.Under(s.Path, m)))
                .Where(s => s.QualifiedName == componentKey || s.QualifiedName.StartsWith(ns, StringComparison.Ordinal) || s.Path.StartsWith(dir, StringComparison.Ordinal))
                .Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        }
    });

    internal static (List<MapSymbol> Symbols, List<MapEdge> Edges) LoadAll(IndexReader r) => MapData.Load(r);

    private (List<MapSymbol> Symbols, List<MapEdge> Edges)? _mapData;

    /// <summary>整库符号与边只加载一次（ccs-backend 约 6 万符号 / 70 万边要两三秒），地图 / 管道 / DSM / 流程共用；索引结束时随其他缓存一起清。调用方持有 _mapCache 锁。</summary>
    private (List<MapSymbol> Symbols, List<MapEdge> Edges) Data(IndexReader r) => _mapData ??= MapData.Load(r);

    /// <summary>绝对路径：落在主根或某个外部根之下时转成入库用的相对路径（外部根是 ../other/…），否则 null；相对路径原样（去掉开头的 /）。</summary>
    private string? NormalizeToRelative(string path)
    {
        var p = path.Replace('\\', '/');
        if (Path.IsPathRooted(path)) return Roots.ToRelative(path);
        return p.TrimStart('/');
    }

    private static SymbolDto ToDto(IndexReader r, SymbolRecord s, Dictionary<long, string> files)
    {
        if (!files.TryGetValue(s.FileId, out var path))
        {
            path = r.GetFileById(s.FileId)?.Path ?? "?";
            files[s.FileId] = path;
        }
        return new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, s.Signature, path,
            s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Visibility);
    }

    private T Read<T>(Func<IndexReader, T> query)
    {
        _readLock.Wait();
        try { return query(new IndexReader(_readDb)); }
        finally { _readLock.Release(); }
    }

    public void Dispose()
    {
        try { WaitForIndexAsync().Wait(TimeSpan.FromSeconds(30)); } catch { /* 索引失败已记录在 _lastError */ }
        _readDb.Dispose();
        _writeDb.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
    }
}

/// <summary>整库符号与边 → 聚合视图的轻量输入（命名空间地图、管道、DSM 共用）。</summary>
public static class MapData
{
    public static (List<MapSymbol> Symbols, List<MapEdge> Edges) Load(IndexReader r)
    {
        var files = r.GetAllFiles().ToDictionary(f => f.Id, f => f.Path);
        var symbols = r.GetAllSymbols()
            .Select(s => new MapSymbol(s.Id, s.Kind, s.QualifiedName, files.GetValueOrDefault(s.FileId, IndexWriter.ExternalFilePath), s.ParentId, s.StartLine, s.EndLine, s.Visibility))
            .ToList();
        var edges = r.GetEdgeTriples().Select(t => new MapEdge(t.FromId, t.ToId, t.Kind, t.Confidence, t.Via)).ToList();
        return (symbols, edges);
    }
}
