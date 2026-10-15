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
/// <param name="Unavailable">本机缺 .NET 10 运行时或 SDK，Roslyn 没跑（Error 是给用户看的原因）；C# 用 tree-sitter 的结果。</param>
public sealed record RoslynReportDto(
    int Files, int FilesSkipped, int SymbolsMerged, int SymbolsAdded, int Edges, int DiRegistrations, int Errors, double ElapsedSeconds, string? Error,
    bool Unavailable = false);

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

/// <param name="Kind">http / hosted / command / job / hub / grpc / handler / page / api-route。</param>
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
    string? Via = null, int? EvidenceLine = null, int? Pos = null, string? Ctx = null);

// ---- 快速跳转（编辑器 CodeLens 与"转到调用方 / 被调方"）----

/// <summary>
/// GET /api/files/lens?path=&lt;相对仓库根或绝对路径&gt; 的结果：这个文件里每个方法 / 函数 / 构造函数的调用计数，编辑器在方法上方显示
/// "被 N 处调用 · 调用 M 个"。一次给整个文件，不逐个方法请求。文件不在索引里时返回空 Symbols（不是 404）。
/// </summary>
/// <param name="Path">规范化后的相对路径（与 SymbolDto.Path 同一套）。</param>
/// <param name="Stale">文件在索引之后改过（内容哈希与库里不一致）：行号可能对不上，编辑器可以把 CodeLens 标成"索引已过期"。</param>
public sealed record FileLensDto(string Path, IReadOnlyList<SymbolLensDto> Symbols, bool Stale);

/// <param name="Symbol">方法 / 函数 / 构造函数（局部函数也算）。</param>
/// <param name="Callers">直接调用方的个数（去重后的调用方方法数，规则与影响范围的直接调用方一致，不含测试代码）。</param>
/// <param name="CallSites">被调用的处数（同一调用方多处各算一处，不含测试代码）。</param>
/// <param name="TestCallers">测试代码里的调用方个数。</param>
/// <param name="Callees">调用大纲第一层的步骤数（规则与调用大纲一致，含框架调用）。</param>
/// <param name="IsEntry">它本身是入口（HTTP / 任务 / 命令 / 消息处理…）。</param>
public sealed record SymbolLensDto(SymbolDto Symbol, int Callers, int CallSites, int TestCallers, int Callees, bool IsEntry);

// ---- 模块关系（面板「模块关系」模式）：以一个类为中心，类与类之间的依赖，按组件分框 ----

/// <summary>
/// GET /api/symbols/{id}/relations?depth=1|2&amp;tests=false|true 的结果。id 可以是方法 / 属性 / 类型：服务端换成它所属的"单元"
/// （类、接口、结构、记录、枚举；没有类的语言用所在文件的模块符号）作为中心。成员之间的边（calls / dispatches / references / inherits / implements）
/// 汇总到单元与单元之间。
/// </summary>
/// <param name="Focus">中心单元。</param>
/// <param name="Components">图里出现的组件（框）。Order 是依赖序：被依赖的在前（与 DSM 的排序一致），版式从左到右按它排。</param>
/// <param name="Nodes">单元：中心（Distance 0）、与中心直接相连的（1），depth=2 时再往外一层（2）。</param>
/// <param name="Edges">单元之间的汇总边（有向：From 依赖 To）。</param>
/// <param name="Truncated">节点超过上限（RelationsBuilder.MaxNodes）被截断：按与中心之间的边权重保留前若干个。</param>
public sealed record RelationsDto(SymbolDto Focus, IReadOnlyList<RelationComponentDto> Components, IReadOnlyList<RelationNodeDto> Nodes, IReadOnlyList<RelationEdgeDto> Edges, bool Truncated);

/// <param name="Key">组件键（与 DSM 的组件键同一套：容器成员项目 / 命名空间段 / 前端目录）。</param>
/// <param name="Label">显示名。</param>
/// <param name="Order">依赖序（0 起）：被依赖的组件在前。</param>
/// <param name="ContainerKey">所在容器的键（跨容器时用来区分同名组件），不在任何容器里为 null。</param>
public sealed record RelationComponentDto(string Key, string Label, int Order, string? ContainerKey);

/// <param name="Unit">单元符号（类 / 接口 / … / 模块）。</param>
/// <param name="Distance">离中心的步数：0 = 中心，1 = 直接相连，2 = 再外一层。</param>
/// <param name="External">仓库外的类型（框架 / 第三方，比如继承的 ControllerBase）：放在单独的"外部"组件里。</param>
public sealed record RelationNodeDto(SymbolDto Unit, string ComponentKey, int Distance, bool External);

/// <summary>From 依赖 To 的汇总边。</summary>
/// <param name="Kinds">各种关系的条数：call（调用，含 impl 派生）/ new（构造）/ dispatch（分派）/ inherits / implements / reference（类型引用）。只列 &gt; 0 的。</param>
/// <param name="Weight">用于排版与截断的权重（调用与构造按条数，继承 / 实现各算 5，引用按条数 / 2）。</param>
/// <param name="Samples">成员层面的示例（最多 5 条，按置信度降序）：哪个方法调了哪个方法、在哪一行。</param>
public sealed record RelationEdgeDto(long From, long To, IReadOnlyDictionary<string, int> Kinds, double Weight, IReadOnlyList<RelationSampleDto> Samples);

/// <param name="FromLabel">起点成员显示名（类型.方法）；继承 / 实现时是类型名。</param>
/// <param name="ToLabel">终点成员显示名。</param>
/// <param name="Kind">call / new / dispatch / inherits / implements / reference。</param>
/// <param name="File">发生的文件（相对仓库根）。</param>
/// <param name="Line">行。</param>
/// <param name="Pos">UTF-16 字符偏移，未知为 null。</param>
public sealed record RelationSampleDto(string FromLabel, string ToLabel, string Kind, string File, int Line, int? Pos, double Confidence);

// ---- 影响范围（侧边栏「影响范围」树视图）：反向看谁调了它、一路影响到哪些入口 ----

/// <summary>
/// GET /api/symbols/{id}/impact?entries=true|false&amp;tests=false|true 的结果：这个符号的直接调用方（一层，展开某个调用方时再对它取一次），
/// 根请求（entries=true）另附从它往上能走到的全部入口。
/// </summary>
/// <param name="Callers">直接调用方，同一个调用方的多处调用合成一项；排序：先是本身是入口的调用方，再按置信度降序、Label。</param>
/// <param name="Entries">entries=true 时：往上（沿 calls / impl / dispatch 的反向）能走到的入口，按 Depth、Kind、Route 排；entries=false 时为 null。</param>
/// <param name="EntriesTruncated">往上走超过 ImpactBuilder 的步数 / 节点上限被截断，入口可能不全。</param>
/// <param name="TestCallers">被多少个测试代码里的方法直接调用（tests=false 时它们不出现在 Callers 里，只计数）。</param>
/// <param name="Truncated">直接调用方超过上限被截断。</param>
public sealed record ImpactDto(SymbolDto Symbol, IReadOnlyList<ImpactCallerDto> Callers, IReadOnlyList<ImpactEntryDto>? Entries, bool EntriesTruncated, int TestCallers, bool Truncated);

/// <summary>
/// 一个直接调用方。
/// Kind：call（直接调用）/ impl（调用的是这个符号实现的接口方法，经 via=impl 派生边）/ dispatch（分派的消息由这个符号处理）/ new（构造这个类型或构造函数）/
/// hop（跨服务：别的容器里发起 HTTP / gRPC 调用、经路由匹配对上这个入口的方法，比如前端页面里的 fetch）。
/// </summary>
/// <param name="Label">调用方显示名：类型.方法。</param>
/// <param name="Caller">调用方符号（单击跳到它里面的调用处，展开时对它再取一次影响范围）。</param>
/// <param name="Sites">调用方里每一处调用（按位置排）：行、字符偏移、所在控制结构。</param>
/// <param name="Expandable">调用方自己还有调用方（满足同样规则的入边）。</param>
/// <param name="AsEntry">调用方本身是入口时的入口信息（可能多个：同一方法既是 http 又被注册成别的），否则为空。</param>
public sealed record ImpactCallerDto(
    string Kind, string Label, SymbolDto Caller, IReadOnlyList<ImpactSiteDto> Sites, double Confidence, bool Expandable, IReadOnlyList<ImpactEntryDto> AsEntry);

/// <param name="Pos">调用名的 UTF-16 字符偏移；null 时跳到 Line 行首。</param>
/// <param name="Ctx">loop / cond / try / catch / callback，见 CodeAtlas.Core.Model.CallContext。</param>
public sealed record ImpactSiteDto(int Line, int? Pos, string? Ctx);

/// <summary>受影响的入口。</summary>
/// <param name="ContainerKey">入口所在容器的键；ContainerName 是显示名（容器图里的名字）。</param>
/// <param name="Depth">从这个符号往上走了几层到达（直接调用方是入口 = 1；符号自己就是入口 = 0）。</param>
public sealed record ImpactEntryDto(EntryDto Entry, string ContainerKey, string ContainerName, int Depth);

// ---- 调用大纲（侧边栏「调用大纲」树视图）----

/// <summary>GET /api/symbols/{id}/outline 的结果：一个方法按源码顺序的调用步骤，只有一层（展开某一步时再对它的 Target 取一次）。</summary>
/// <param name="Truncated">步骤超过上限（OutlineBuilder.MaxSteps）被截断。</param>
public sealed record OutlineDto(SymbolDto Symbol, IReadOnlyList<OutlineStepDto> Steps, bool Truncated);

/// <summary>
/// 大纲里的一步。
/// Kind：
/// <list type="bullet">
/// <item>call：调用仓库里的方法 / 函数；</item>
/// <item>new：构造（new T()、T(...) 落到类型）；</item>
/// <item>dispatch：分派到 Handler（mediator.Send 那一步下面挂的目标，或单独一步）；</item>
/// <item>impl：接口调用落到的实现（挂在接口调用那一步下面）；</item>
/// <item>external：框架 / 第三方调用，不能展开；Label 是"类型.方法"或包名.方法；</item>
/// <item>ambiguous：多个候选，Children 是各个候选（kind = call）。</item>
/// </list>
/// </summary>
/// <param name="Order">在本层的序号（1 起，按调用名的字符偏移排；偏移未知时按行）；Children 里的项也各自从 1 编号。</param>
/// <param name="Label">显示名：类型.方法（前端不再拼）。</param>
/// <param name="Target">被调的符号；external 也有（外部符号），ambiguous 为 null。</param>
/// <param name="Line">调用发生在当前方法里的哪一行（单击跳到这里）。</param>
/// <param name="Pos">调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致），未知为 null；扩展用它把光标放到调用名上。</param>
/// <param name="Ctx">所在控制结构：loop / cond / try / catch / callback（见 CodeAtlas.Core.Model.CallContext），不在结构里为 null。</param>
/// <param name="Expandable">Target 是仓库里的方法且它自己还有调用：可以展开（展开时请求 Target 的大纲）。</param>
/// <param name="Repeat">相邻的同一调用合并后的次数（≥1）。</param>
/// <param name="Children">就地挂上的子项：dispatch 目标、impl 实现、ambiguous 候选；普通调用为空（要展开才取）。</param>
public sealed record OutlineStepDto(
    int Order, string Kind, string Label, SymbolDto? Target, int Line, int? Pos, string? Ctx, double Confidence,
    bool Expandable, int Repeat, IReadOnlyList<OutlineStepDto> Children);

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
