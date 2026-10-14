namespace CodeAtlas.Server;

/// <param name="RoslynAt">最近一次 Roslyn 写回时间；从没跑过为 null。</param>
/// <param name="RoslynStaleFiles">tree-sitter 重新解析过、还没被 Roslyn 覆盖的 C# 文件数。</param>
public sealed record HealthDto(string Repo, string DbPath, long Files, long Symbols, string? IndexedAt, bool NeedsRebuild, string? RoslynAt = null, long RoslynStaleFiles = 0);

/// <param name="Roslyn">null 自动（全量或从未跑过时跑）；true 强制跑；false 不跑。</param>
public sealed record IndexRequest(bool Full, bool? Roslyn = null);

public sealed record IndexReportDto(
    int FilesScanned, int FilesParsed, int FilesFailed, int FilesSkipped, int FilesRemoved, int FilesUnchanged,
    long TotalSymbols, long TotalEdges, double ElapsedSeconds, RoslynReportDto? Roslyn = null);

/// <param name="Error">没跑成的原因（找不到 worker / 解决方案、worker 崩溃）；成功为 null。</param>
public sealed record RoslynReportDto(
    int Files, int FilesSkipped, int SymbolsMerged, int SymbolsAdded, int Edges, int DiRegistrations, int Errors, double ElapsedSeconds, string? Error);

public sealed record IndexStatusDto(bool Running, string? Phase, int Done, int Total, IndexReportDto? LastReport, string? Error);

// ---- 容器图（L1）----

/// <param name="NamespaceRoots">容器成员项目里符号的根命名空间（或前端目录），双击容器钻到命名空间地图时用。</param>
/// <param name="Outbound">这个容器登记的出口数。</param>
public sealed record ArchNodeDto(
    string Key, string Kind, string SubKind, string Name, string? Path, string Source,
    IReadOnlyList<CodeAtlas.Core.Architecture.Evidence> Evidence, IReadOnlyList<string> Members, IReadOnlyList<int> Ports,
    IReadOnlyList<string> NamespaceRoots, int Outbound);

public sealed record ArchEdgeDto(
    string From, string To, string Protocol, double Confidence, string Source,
    IReadOnlyList<CodeAtlas.Core.Architecture.Evidence> Evidence, string? ConfigKey, string? Url);

/// <param name="Note">没有容器时的说明（纯类库 / 没扫描过）。</param>
public sealed record ArchDto(IReadOnlyList<ArchNodeDto> Nodes, IReadOnlyList<ArchEdgeDto> Edges, IReadOnlyList<CodeAtlas.Core.Architecture.ArchConflict> Conflicts, string? Note);

// ---- 入口与时序（L3）----

/// <param name="Kind">http / hosted / job / hub / handler / page / api-route。</param>
/// <param name="Route">HTTP 路由模板、页面路径、消息类型名或 null。</param>
/// <param name="Name">入口符号的短名（方法名 / 文件名）。</param>
public sealed record EntryDto(long SymbolId, string Kind, string RuleId, string? Route, string? HttpMethod, string Name, string QualifiedName, string File, int Line);

public sealed record SymbolDto(
    long Id, string Kind, string Name, string QualifiedName, string? Signature, string Path,
    int StartLine, int StartCol, int EndLine, int EndCol, string? Visibility,
    /// <summary>被多少处调用（calls 入边数）。只在 neighbors 里填，用来识别到处被调的工具方法。</summary>
    int? Callers = null);

/// <param name="Direction">"out" 表示从查询的符号出发，"in" 表示指向它。</param>
/// <param name="AmbiguousGroup">confidence 为 0.3 的多候选边带 "<kind>:<line>"，页面按它折叠。</param>
/// <param name="Other">对端符号；悬空边为 null，此时看 ToName。</param>
public sealed record NeighborDto(
    long EdgeId, string Kind, string Direction, double Confidence, int Line, string? AmbiguousGroup, SymbolDto? Other, string ToName,
    string? Via = null, int? EvidenceLine = null);

// ---- 本地 AI 叙述（里程碑 5d）----

/// <param name="Evidence">模型引用的事实 id（node:… / edge:… / entry:… / sym:… / comp:… / cell:…）。</param>
/// <param name="Inferred">引用里有不在输入事实集合里的 id：这条是模型推断的，页面要标出来。</param>
public sealed record NarrativePointDto(string Text, IReadOnlyList<string> Evidence, bool Inferred);

/// <param name="Kind">container / component / flow。</param>
/// <param name="Key">container = 容器键；component = "&lt;容器键&gt;|&lt;组件键&gt;"；flow = 入口符号 id。</param>
/// <param name="Status">ready / pending / running / failed / unavailable（--no-ai 或端点连不上，Error 写原因）。</param>
/// <param name="Model">生成这条叙述的模型（可能不是当前服务的模型：换模型不让旧叙述作废）。</param>
/// <param name="Hash">生成时的架构事实指纹（与模型无关，符号 id 已换成稳定形式）。</param>
/// <param name="Layout">模型从预设版式里选的一个 id；没选或选了不存在的为 null。</param>
/// <param name="Stale">缓存可能过期：相关文件内容或架构事实在生成后变了。内容照样返回，不自动重算。</param>
/// <param name="StaleReason">中文原因，如"生成后 3 个相关文件有改动；架构事实有变化"。</param>
/// <param name="ChangedFiles">生成后内容变了（含新增、删除）的相关文件数。</param>
/// <param name="GitCommit">生成时仓库 HEAD 的短哈希；不是 git 仓库为 null。</param>
public sealed record NarrativeDto(
    string Kind, string Key, string Status, string? Model, string? GeneratedAt, string? Hash,
    string? Summary, IReadOnlyList<NarrativePointDto> KeyPoints, IReadOnlyList<NarrativePointDto> Risks, string? Layout, string? Error,
    bool Stale = false, string? StaleReason = null, int ChangedFiles = 0, string? GitCommit = null);

/// <param name="Reachable">端点是否连得上；还没试过为 null。</param>
/// <param name="Running">正在生成的对象 "kind:key"；空闲为 null。</param>
/// <param name="RemoteWarning">端点不是回环地址时的提示："事实子图（与入口源码）将发送到 &lt;host&gt;"。</param>
/// <param name="Provider">协议：openai（OpenAI 兼容，含本地 llama-server）| anthropic。</param>
/// <param name="HasApiKey">服务进程拿到了 API Key（只报有没有，不回传值）。</param>
public sealed record NarrationStatusDto(
    bool Enabled, string Endpoint, string Model, bool? Reachable, int Queued, string? Running, int Done, int Failed,
    string? LastError, string? RemoteWarning, bool Paused,
    string Provider = "openai", int Concurrency = 1, bool IncludeSource = true, bool HasApiKey = false);

/// <param name="Force">true 时忽略缓存重新生成。</param>
public sealed record NarrateRequest(string Kind, string Key, bool? Force = null);
