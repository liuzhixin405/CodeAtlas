using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server.Narration;

/// <summary>一次同步生成的结果：DTO + 端点统计（CLI 打印耗时与 tok/s）。</summary>
public sealed record GenerationResult(NarrativeDto Dto, ChatResult? Chat, Facts? Facts, int Attempts);

/// <summary>
/// AI 叙述：事实子图 → 提示词 → 模型 → 校验 → 缓存。后台队列（Concurrency 个 worker，可停可续）。
/// 缓存（NarrativeCache，单独的 narratives.db）按 (kind, 稳定键) 存一条：
/// · 重建索引不丢、符号 id 变了也认得；换模型不作废（本地模型几分钟一条，结果很宝贵）；
/// · 过期按"相关文件内容哈希"和"架构事实指纹"判：过期的照样返回（Stale = true + 原因），不自动重算，用户点"更新"（force）才算；
/// · 没有任何缓存时打开叙述卡会自动排队（只保留最近打开的一个）。
/// 终态（ready / failed）落库；排队 / 运行中 / unavailable 只在内存。
/// </summary>
public sealed class NarrationService : IDisposable
{
    public const string Ready = "ready";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";

    /// <summary>端点连不上的结论保留多久：过了再打开叙述卡就重新试，不用用户手动点。</summary>
    public TimeSpan UnavailableTtl { get; init; } = TimeSpan.FromSeconds(60);

    private readonly NarrationOptions _options;
    private readonly IChatProvider _provider;
    private readonly IFactsSource _facts;
    private readonly NarrativeCache _cache;
    private readonly bool _ownsCache;
    private readonly object _lock = new();
    private readonly List<(string Kind, string Key, bool Force, bool Auto)> _queue = [];
    private readonly Dictionary<(string, string), (NarrativeDto Dto, DateTime At)> _transient = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _workers;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private bool _paused;
    private int _done;
    private int _failed;
    private string? _lastError;
    private bool? _reachable;

    public NarrationService(NarrationOptions options, IChatProvider provider, IFactsSource facts, NarrativeCache cache, bool ownsCache)
    {
        _options = options;
        _provider = provider;
        _facts = facts;
        _cache = cache;
        _ownsCache = ownsCache;
        // 每个并发槽一个后台 worker：本地 1 个；联网服务可以 2–4 个，provider 里的并发门同数
        _workers = options.Enabled ? Enumerable.Range(0, Math.Max(1, options.Concurrency)).Select(_ => Task.Run(WorkerAsync)).ToArray() : [];
    }

    /// <summary>服务端 / CLI 用：缓存放在索引库旁边的 narratives.db。</summary>
    public static NarrationService Create(ServerOptions options, IndexService index, IChatProvider? provider = null)
    {
        var n = options.Narration;
        return new NarrationService(n, provider ?? ChatProviders.Create(n), new IndexFactsSource(index, n.IncludeSource),
            NarrativeCache.Open(NarrativeCache.PathBeside(options.DbPath)), ownsCache: true);
    }

    public NarrationOptions Options => _options;

    /// <summary>
    /// 读叙述。有缓存就返回缓存（过期的带 Stale 与原因，不自动重算）；正在重算时返回旧内容 + pending / running。
    /// 没有缓存时自动排队（只保留最近读的那一个，见 Enqueue 的 auto）。
    /// </summary>
    public NarrativeDto Get(string kind, string key)
    {
        if (!_options.Enabled) return Empty(kind, key, Unavailable, "已用 --no-ai 关闭 AI 叙述");
        var facts = SafeResolve(kind, key, out var resolveError);
        if (facts is null) return Empty(kind, key, Failed, resolveError ?? $"没有这个对象：{kind} {key}");
        var cached = Cached(facts, key);
        if (Transient(kind, key) is { } t)
            return cached is null ? t : cached with { Status = t.Status, Error = t.Error ?? cached.Error };
        if (cached is not null)
        {
            // 失败过、而且代码在那之后又变了：值得再试一次
            if (cached.Status == Failed && cached.Stale) { Enqueue(kind, key, force: false, auto: true); return cached with { Status = Pending }; }
            return cached;
        }
        var queued = Enqueue(kind, key, force: false, auto: true);
        return queued;
    }

    /// <summary>
    /// 入队。非 force 时缓存新鲜（ready 且没过期）直接返回，不入队。
    /// auto = 由"打开叙述卡"触发的自动排队：新的自动项会撤掉队列里其他还没开始的自动项——27B 模型一条两三分钟，
    /// 随手点十个容器不该排出半小时的活；用户点"生成 / 更新"（POST）的项不受影响。
    /// </summary>
    public NarrativeDto Enqueue(string kind, string key, bool force, bool auto = false)
    {
        if (!_options.Enabled) return Empty(kind, key, Unavailable, "已用 --no-ai 关闭 AI 叙述");
        var facts = SafeResolve(kind, key, out var resolveError);
        if (facts is null) return Empty(kind, key, Failed, resolveError ?? $"没有这个对象：{kind} {key}");
        var cached = Cached(facts, key);
        if (!force && cached is { Status: Ready, Stale: false }) return cached;
        lock (_lock)
        {
            if (_running.Contains($"{kind}:{key}")) return Merge(cached, _transient.GetValueOrDefault((kind, key)).Dto ?? Empty(kind, key, Running, null));
            if (auto)
                foreach (var old in _queue.Where(q => q.Auto && !(q.Kind == kind && q.Key == key)).ToList())
                {
                    _queue.Remove(old);
                    _transient.Remove((old.Kind, old.Key));
                }
            var idx = _queue.FindIndex(q => q.Kind == kind && q.Key == key);
            if (idx >= 0) _queue[idx] = (kind, key, _queue[idx].Force || force, _queue[idx].Auto && auto);
            else _queue.Add((kind, key, force, auto));
            var dto = Empty(kind, key, Pending, _paused ? "队列已暂停，恢复后生成" : null);
            _transient[(kind, key)] = (dto, DateTime.UtcNow);
            _signal.Release();
            return Merge(cached, dto);
        }
    }

    public NarrationStatusDto Status()
    {
        lock (_lock)
            return new NarrationStatusDto(_options.Enabled, _options.Endpoint, _options.Model, _reachable, _queue.Count, _running.Count == 0 ? null : string.Join(", ", _running.Order(StringComparer.Ordinal)),
                _done, _failed, _lastError, _options.RemoteWarning, _paused,
                _options.Provider, Math.Max(1, _options.Concurrency), _options.IncludeSource, !string.IsNullOrEmpty(_options.ApiKey));
    }

    /// <summary>暂停：正在生成的那条做完，队列里的不再取。</summary>
    public NarrationStatusDto Stop()
    {
        lock (_lock)
        {
            _paused = true;
            foreach (var (kind, key, _, _) in _queue) _transient[(kind, key)] = (Empty(kind, key, Pending, "队列已暂停，恢复后生成"), DateTime.UtcNow);
        }
        return Status();
    }

    public NarrationStatusDto Resume()
    {
        lock (_lock)
        {
            _paused = false;
            foreach (var (kind, key, _, _) in _queue) _transient[(kind, key)] = (Empty(kind, key, Pending, null), DateTime.UtcNow);
        }
        _signal.Release(Math.Max(1, _workers.Length)); // 每个 worker 都叫醒，并发槽一起开工
        return Status();
    }

    /// <summary>同步生成（CLI 用）：不经队列，直接算并写缓存。非 force 时缓存新鲜就直接返回（Attempts = 0）。</summary>
    public async Task<GenerationResult> GenerateAsync(string kind, string key, bool force, CancellationToken ct)
    {
        if (!_options.Enabled) return new GenerationResult(Empty(kind, key, Unavailable, "已用 --no-ai 关闭 AI 叙述"), null, null, 0);
        var facts = SafeResolve(kind, key, out var resolveError);
        if (facts is null) return new GenerationResult(Empty(kind, key, Failed, resolveError ?? $"没有这个对象：{kind} {key}"), null, null, 0);
        if (!force && Cached(facts, key) is { Status: Ready, Stale: false } hit) return new GenerationResult(hit, null, facts, 0);
        return await RunAsync(facts, key, ct);
    }

    /// <summary>缓存里这个对象的状态（批量预生成用）：null = 没有；否则带 Stale。</summary>
    public NarrativeDto? Peek(string kind, string key)
    {
        var facts = SafeResolve(kind, key, out _);
        return facts is null ? null : Cached(facts, key);
    }

    /// <summary>测试用：等队列清空且没有在跑的。</summary>
    public async Task WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock) if (_running.Count == 0 && (_queue.Count == 0 || _paused)) return;
            await Task.Delay(20);
        }
    }

    private async Task WorkerAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            while (!_stop.IsCancellationRequested)
            {
                (string Kind, string Key, bool Force, bool Auto) item;
                lock (_lock)
                {
                    if (_paused || _queue.Count == 0) break;
                    item = _queue[0];
                    _queue.RemoveAt(0);
                    _running.Add($"{item.Kind}:{item.Key}");
                    _transient[(item.Kind, item.Key)] = (Empty(item.Kind, item.Key, Running, null), DateTime.UtcNow);
                }
                try
                {
                    var facts = SafeResolve(item.Kind, item.Key, out var resolveError);
                    if (facts is null) { Finish(item.Kind, item.Key, Empty(item.Kind, item.Key, Failed, resolveError ?? $"没有这个对象：{item.Kind} {item.Key}")); continue; }
                    if (!item.Force && Cached(facts, item.Key) is { Status: Ready, Stale: false })
                    {
                        Finish(item.Kind, item.Key, null);
                        continue;
                    }
                    var result = await RunAsync(facts, item.Key, _stop.Token);
                    Finish(item.Kind, item.Key, result.Dto.Status is Ready ? null : result.Dto);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Finish(item.Kind, item.Key, Empty(item.Kind, item.Key, Failed, "生成时出错：" + ex.Message));
                }
            }
        }
    }

    /// <summary>收尾：清 running；ready / failed 已落库，从内存里拿掉；unavailable 留在内存（UnavailableTtl 后过期）。</summary>
    private void Finish(string kind, string key, NarrativeDto? keepTransient)
    {
        lock (_lock)
        {
            _running.Remove($"{kind}:{key}");
            if (keepTransient is null || keepTransient.Status is Ready or Failed) _transient.Remove((kind, key));
            else _transient[(kind, key)] = (keepTransient, DateTime.UtcNow);
            if (keepTransient is null || keepTransient.Status == Ready) _done++;
            else { _failed++; _lastError = keepTransient.Error; }
        }
    }

    /// <summary>调模型、校验、落库；失败（非 JSON / 超时 / HTTP 错）重试一次；连不上 / Key 不对直接 unavailable 不重试。</summary>
    private async Task<GenerationResult> RunAsync(Facts facts, string requestKey, CancellationToken ct)
    {
        var (system, user) = Prompts.Build(facts);
        ChatResult? chat = null;
        string? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                chat = await _provider.CompleteAsync(system, user, ct);
                lock (_lock) _reachable = true;
                var body = NarrativeSchema.Parse(chat.Content, facts.Kind, facts.Ids);
                var dto = new NarrativeDto(facts.Kind, requestKey, Ready, _provider.Model, Now(), facts.Fingerprint, body.Summary, body.KeyPoints, body.Risks, body.Layout, null,
                    GitCommit: Persist(facts, Ready, _provider.Model, body, null));
                return new GenerationResult(dto, chat, facts, attempt);
            }
            catch (ChatUnavailableException ex)
            {
                lock (_lock) _reachable = false;
                var dto = Empty(facts.Kind, requestKey, Unavailable, ex.Message) with { Hash = facts.Fingerprint, Model = _provider.Model };
                return new GenerationResult(dto, chat, facts, attempt);
            }
            catch (Exception ex) when (ex is ChatFailedException or NarrativeParseException)
            {
                if (ex is ChatFailedException) lock (_lock) _reachable = true;
                lastError = ex.Message;
            }
        }
        var error = lastError + "（已重试一次）";
        // 失败只在没有可用旧结果时落库：不能拿一次超时把本地模型之前算好的叙述盖掉
        string? git = null;
        if (_cache.Get(facts.Kind, facts.StableKey) is not { Status: Ready })
            git = Persist(facts, Failed, _provider.Model, new NarrativeBody("", [], [], null), error);
        var failed = new NarrativeDto(facts.Kind, requestKey, Failed, _provider.Model, Now(), facts.Fingerprint, null, [], [], null, error, GitCommit: git);
        return new GenerationResult(failed, chat, facts, 2);
    }

    /// <summary>落库：evidence 里会随索引变的 id 换成稳定形式；记下事实指纹、相关文件哈希、git HEAD。返回记下的 commit。</summary>
    private string? Persist(Facts facts, string status, string model, NarrativeBody body, string? error)
    {
        IReadOnlyList<NarrativePointDto> ToStable(IReadOnlyList<NarrativePointDto> points) =>
            points.Select(p => p with { Evidence = p.Evidence.Select(e => facts.StableIds.GetValueOrDefault(e, e)).ToList() }).ToList();
        var stableBody = body with { KeyPoints = ToStable(body.KeyPoints), Risks = ToStable(body.Risks) };
        var git = _facts.GitHead();
        _cache.Upsert(new NarrativeRow(facts.Kind, facts.StableKey, model, status,
            JsonSerializer.Serialize(stableBody, NarrativeSchema.Json), error, Now(),
            facts.Fingerprint, JsonSerializer.Serialize(facts.Sources), git));
        return git;
    }

    /// <summary>缓存里的这条（evidence 换回当前 id，算好过期状态）；没有为 null。</summary>
    private NarrativeDto? Cached(Facts facts, string requestKey)
    {
        var row = _cache.Get(facts.Kind, facts.StableKey);
        if (row is null) return null;
        NarrativeBody? body = null;
        try { body = JsonSerializer.Deserialize<NarrativeBody>(row.Json, NarrativeSchema.Json); } catch (JsonException) { /* 坏行：当没有正文 */ }
        var current = facts.StableIds.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal); // 稳定 → 当前
        IReadOnlyList<NarrativePointDto> ToCurrent(IReadOnlyList<NarrativePointDto>? points) =>
            (points ?? []).Select(p => p with { Evidence = p.Evidence.Select(e => current.GetValueOrDefault(e, e)).ToList() }).ToList();

        Dictionary<string, string> then;
        try { then = JsonSerializer.Deserialize<Dictionary<string, string>>(row.SourcesJson) ?? []; } catch (JsonException) { then = []; }
        var changed = then.Count(p => !facts.Sources.TryGetValue(p.Key, out var h) || h != p.Value)
                      + facts.Sources.Keys.Count(k => !then.ContainsKey(k));
        var factsChanged = row.Fingerprint != facts.Fingerprint;
        var reasons = new List<string>();
        if (changed > 0) reasons.Add($"生成后 {changed} 个相关文件有改动");
        if (factsChanged) reasons.Add("架构事实有变化");
        return new NarrativeDto(facts.Kind, requestKey, row.Status, row.Model, row.GeneratedAt, row.Fingerprint,
            string.IsNullOrEmpty(body?.Summary) ? null : body!.Summary, ToCurrent(body?.KeyPoints), ToCurrent(body?.Risks), body?.Layout, row.Error,
            Stale: reasons.Count > 0, StaleReason: reasons.Count == 0 ? null : string.Join("；", reasons), ChangedFiles: changed, GitCommit: row.GitCommit);
    }

    /// <summary>内存里的排队 / 运行 / 不可用状态；不可用超过 UnavailableTtl 视为过期（让下次打开重新试）。</summary>
    private NarrativeDto? Transient(string kind, string key)
    {
        lock (_lock)
        {
            if (!_transient.TryGetValue((kind, key), out var t)) return null;
            if (t.Dto.Status == Unavailable && DateTime.UtcNow - t.At > UnavailableTtl) { _transient.Remove((kind, key)); return null; }
            return t.Dto;
        }
    }

    /// <summary>有旧内容时带着旧内容报新状态：页面继续显示、只转圈。</summary>
    private static NarrativeDto Merge(NarrativeDto? cached, NarrativeDto state) =>
        cached is null || cached.Status != Ready ? state : cached with { Status = state.Status, Error = state.Error };

    private Facts? SafeResolve(string kind, string key, out string? error)
    {
        error = null;
        try { return _facts.Resolve(kind, key); }
        catch (Exception ex) { error = "取事实失败：" + ex.Message; return null; }
    }

    private static NarrativeDto Empty(string kind, string key, string status, string? error) => new(kind, key, status, null, null, null, null, [], [], null, error);

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _stop.Cancel();
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(5)); } catch { /* 正在等模型：放弃 */ }
        if (_ownsCache) _cache.Dispose();
        (_provider as IDisposable)?.Dispose();
        _signal.Dispose();
        _stop.Dispose();
    }
}

/// <summary>提示词。叙述用简体中文；模型只能引用给定 id、只能从预设版式里选。</summary>
public static class Prompts
{
    public static (string System, string User) Build(Facts facts)
    {
        var kindName = facts.Kind switch { NarrativePresets.Container => "容器（可部署单元）", NarrativePresets.Component => "组件（容器内的项目 / 命名空间）", _ => "入口流程（从一个入口出发的调用时序）" };
        var presets = string.Join("\n", NarrativePresets.For(facts.Kind).Select(p => $"  - \"{p.Id}\"：{p.Description}"));
        var system = $$"""
            你是软件架构分析助手，用简体中文为代码库的一个{{kindName}}写一段简短叙述。
            规则：
            1. 只依据下面给出的事实，不要编造事实里没有的组件、调用或技术；不确定的话写"事实里没有体现"。
            2. 每条 keyPoint / risk 必须引用事实 id（方括号里的，如 node:x、edge:a→b:http、entry:12、sym:34、comp:k、cell:a→b）；引用的 id 必须原样来自事实列表。
            3. summary 不超过 120 个汉字，说清这个对象是什么、与谁打交道、最值得注意的一点。
            4. keyPoints 3 到 5 条，每条一句话；risks 0 到 3 条，只写事实能支撑的风险（环、反向依赖、低置信度的边、热点、隐性耦合、没有对上的出口等），没有就给空数组。
            5. layout 必须从下面的预设 id 里选一个最适合展示这个对象的，不要自己发明：
            {{presets}}
            6. 只输出一个 JSON 对象，不要 markdown 围栏、不要解释。形状：
            {"summary": "…", "keyPoints": [{"text": "…", "evidence": ["id", "…"]}], "risks": [{"text": "…", "evidence": ["id"]}], "layout": "预设id"}
            """;
        var user = new StringBuilder();
        user.Append("事实（每行一条，方括号里是 id）：\n\n").Append(facts.Text);
        user.Append("\n请按规则输出 JSON。");
        return (system, user.ToString());
    }
}
