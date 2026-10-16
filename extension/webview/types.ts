// 与 CodeAtlas.Server 的 DTO（Dtos.cs）保持一致。

export interface SymbolDto {
  id: number;
  kind: string;
  name: string;
  qualifiedName: string;
  signature: string | null;
  path: string;
  startLine: number;
  startCol: number;
  endLine: number;
  endCol: number;
  visibility: string | null;
  /** 被多少处调用；只在 neighbors 结果里有。 */
  callers?: number | null;
}

export interface NeighborDto {
  edgeId: number;
  kind: string;
  direction: "in" | "out";
  confidence: number;
  line: number;
  ambiguousGroup: string | null;
  other: SymbolDto | null;
  toName: string;
  /** dispatches 边：handles（Handler 处理某类型）或 dispatch（调用点分发到 Handler） */
  via: string | null;
  evidenceLine: number | null;
  /** 调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致）；旧索引为 null。 */
  pos?: number | null;
  /** 调用所在控制结构：loop / cond / try / catch / callback。 */
  ctx?: OutlineCtx | null;
}

// ---- 框架流程（GET /api/arch/framework?container=），与 src/CodeAtlas.Server/Dtos.cs 的 FrameworkFlowDto 对应 ----

/**
 * .NET 的六个阶段之外，各语言有自己的阶段（interceptor / aspect / dependency / provider / route / page / request ……），
 * 列名与顺序都以服务端为准，页面不按键分支（只有 handler 用来数 Handler 总数）。
 */
export type FrameworkStageKey = "middleware" | "controller" | "dispatcher" | "behavior" | "handler" | "tail" | (string & {});

export interface FrameworkStageDto {
  key: FrameworkStageKey;
  /** 中间件 / 控制器 / 分派器 / 行为链 / Handler / 收尾 */
  label: string;
  /** 阶段内部的先后有没有依据（中间件：app.UseXxx 书写顺序；行为：DI 注册顺序）。 */
  orderKnown: boolean;
  symbols: SymbolDto[];
}

export interface FrameworkHandlerDto {
  /** Handler 的入口方法。 */
  handler: SymbolDto;
  messageType: string | null;
  /** command / query / event */
  messageKind: string | null;
  /** 会分派到它的入口；点一个打开它的入口流程。 */
  entries: EntryDto[];
}

export interface FrameworkHandlerGroupDto {
  module: string;
  items: FrameworkHandlerDto[];
}

export interface FrameworkFlowDto {
  containerKey: string;
  containerName: string;
  /** 骨架时序图：actors 是阶段（kind = "stage"），handlers / controllers 是占位节点。 */
  flow: FlowDto;
  stages: FrameworkStageDto[];
  handlers: FrameworkHandlerGroupDto[];
  /** 识别不到的阶段、顺序未知的原因等。 */
  notes: string[];
}

// ---- 快速跳转（GET /api/files/lens），与 src/CodeAtlas.Server/Dtos.cs 的 FileLensDto 对应 ----

export interface SymbolLensDto {
  /** 方法 / 函数 / 构造函数。 */
  symbol: SymbolDto;
  /** 直接调用方个数（去重，不含测试）。 */
  callers: number;
  /** 被调用的处数（不含测试）。 */
  callSites: number;
  testCallers: number;
  /** 调用大纲第一层步骤数。 */
  callees: number;
  /** 经分派到的 Handler 个数（Send / Publish 一类，不是直接引用）；老服务没有这个字段。 */
  dispatches?: number;
  /** 本身是入口。 */
  isEntry: boolean;
}

export interface FileLensDto {
  path: string;
  symbols: SymbolLensDto[];
  /** 文件在索引之后改过，行号可能对不上。 */
  stale: boolean;
}

// ---- 模块关系（GET /api/symbols/{id}/relations），与 src/CodeAtlas.Server/Dtos.cs 的 RelationsDto 对应 ----

export type RelationKind = "call" | "new" | "dispatch" | "inherits" | "implements" | "reference";

export interface RelationComponentDto {
  /** 组件键（与 DSM 同一套）。 */
  key: string;
  label: string;
  /** 依赖序（0 起）：被依赖的在前，版式从左到右按它排。 */
  order: number;
  containerKey: string | null;
}

export interface RelationNodeDto {
  /** 单元：类 / 接口 / … / 模块。 */
  unit: SymbolDto;
  componentKey: string;
  /** 0 = 中心，1 = 直接相连，2 = 再外一层。 */
  distance: number;
  /** 仓库外类型，放在"外部"组件。 */
  external: boolean;
}

export interface RelationSampleDto {
  fromLabel: string;
  toLabel: string;
  kind: RelationKind;
  file: string;
  line: number;
  pos: number | null;
  confidence: number;
}

export interface RelationEdgeDto {
  /** From 依赖 To（单元符号 id）。 */
  from: number;
  to: number;
  /** 各关系条数，只列 > 0 的。 */
  kinds: Partial<Record<RelationKind, number>>;
  weight: number;
  /** 成员层面的示例，最多 5 条。 */
  samples: RelationSampleDto[];
}

export interface RelationsDto {
  focus: SymbolDto;
  components: RelationComponentDto[];
  nodes: RelationNodeDto[];
  edges: RelationEdgeDto[];
  truncated: boolean;
}

// ---- 影响范围（GET /api/symbols/{id}/impact），与 src/CodeAtlas.Server/Dtos.cs 的 ImpactDto 对应 ----

export type ImpactCallerKind = "call" | "impl" | "dispatch" | "new" | "hop";

export interface ImpactSiteDto {
  line: number;
  /** 调用名的 UTF-16 字符偏移；null 时跳到行首。 */
  pos: number | null;
  ctx: OutlineCtx | null;
}

export interface ImpactEntryDto {
  entry: EntryDto;
  containerKey: string;
  containerName: string;
  /** 往上走了几层到达（0 = 符号自己就是入口）。 */
  depth: number;
}

export interface ImpactCallerDto {
  /** call 直接调用 / impl 经接口 / dispatch 经分派 / new 构造 / hop 跨服务（前端 fetch、别的服务的客户端调用）。 */
  kind: ImpactCallerKind;
  /** 调用方显示名：类型.方法。 */
  label: string;
  caller: SymbolDto;
  /** 调用方里每一处调用，按位置排。 */
  sites: ImpactSiteDto[];
  confidence: number;
  /** 调用方自己还有调用方。 */
  expandable: boolean;
  /** 调用方本身是入口时的入口信息。 */
  asEntry: ImpactEntryDto[];
}

export interface ImpactDto {
  symbol: SymbolDto;
  callers: ImpactCallerDto[];
  /** entries=true 的根请求才有：往上能走到的全部入口。 */
  entries: ImpactEntryDto[] | null;
  entriesTruncated: boolean;
  /** 测试代码里的直接调用方个数（tests=false 时只计数不列出）。 */
  testCallers: number;
  truncated: boolean;
}

// ---- 调用大纲（GET /api/symbols/{id}/outline），与 src/CodeAtlas.Server/Dtos.cs 的 OutlineDto 对应 ----

export type OutlineCtx = "loop" | "cond" | "try" | "catch" | "callback";
export type OutlineStepKind = "call" | "new" | "dispatch" | "impl" | "external" | "ambiguous";

export interface OutlineStepDto {
  /** 本层序号（1 起，按调用名的字符偏移排）。 */
  order: number;
  kind: OutlineStepKind;
  /** 显示名：类型.方法。 */
  label: string;
  /** 被调符号；ambiguous 为 null。 */
  target: SymbolDto | null;
  /** 调用发生在当前方法里的行（单击跳到这里）。 */
  line: number;
  /** 调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致）；null 时跳到行首。 */
  pos: number | null;
  ctx: OutlineCtx | null;
  confidence: number;
  /** 能展开：展开时请求 target 的大纲。 */
  expandable: boolean;
  /** 相邻同一调用合并后的次数。 */
  repeat: number;
  /** 就地子项：dispatch 目标、impl 实现、ambiguous 候选。 */
  children: OutlineStepDto[];
}

export interface OutlineDto {
  symbol: SymbolDto;
  steps: OutlineStepDto[];
  truncated: boolean;
}

export interface HealthDto {
  repo: string;
  dbPath: string;
  files: number;
  symbols: number;
  indexedAt: string | null;
  /** 库是旧版本解析规则建的，需要全量重建。 */
  needsRebuild: boolean;
  /** 最近一次 Roslyn 写回时间；没跑过为 null。 */
  roslynAt?: string | null;
  /** 改过但还没被 Roslyn 覆盖的 C# 文件数。 */
  roslynStaleFiles?: number;
}

/** 项目地图的一个节点：命名空间/目录分组，或钻到底后的类型。 */
export interface MapGroupDto {
  key: string;
  label: string;
  symbols: number;
  files: number;
  hasChildren: boolean;
  /** 类型节点才有，可直接切到调用图聚焦。 */
  symbolId: number | null;
}

export interface MapEdgeDto {
  from: string;
  to: string;
  weight: number;
  kinds: Record<string, number>;
}

export interface MapDto {
  root: string;
  groups: MapGroupDto[];
  edges: MapEdgeDto[];
}

// ---- 容器图 L1（ArchDto）----

export interface EvidenceDto {
  file: string;
  line: number | null;
  note: string | null;
}

export interface ArchNodeDto {
  key: string;
  /** container / external / person */
  kind: string;
  /** dotnet-web / dotnet-worker / dotnet-exe / frontend / database / cache / broker / http-service / grpc-service / native-sdk / storage / observability / other */
  subKind: string;
  name: string;
  path: string | null;
  /** discovered / declared */
  source: string;
  evidence: EvidenceDto[];
  members: string[];
  ports: number[];
  /** 钻到命名空间地图用的根；前端是目录。 */
  namespaceRoots: string[];
  outbound: number;
}

export interface ArchEdgeDto {
  from: string;
  to: string;
  protocol: string;
  /** 0.9 路由对上；0.7 配置地址对上；0.5 只知道客户端；1.0 声明 */
  confidence: number;
  source: string;
  evidence: EvidenceDto[];
  configKey: string | null;
  url: string | null;
}

export interface ArchConflictDto {
  kind: string;
  message: string;
}

export interface ArchDto {
  nodes: ArchNodeDto[];
  edges: ArchEdgeDto[];
  conflicts: ArchConflictDto[];
  note: string | null;
}

// ---- 组件 DSM L2（DsmDto）----

export interface DsmHotDto {
  revisions: number;
  added: number;
  deleted: number;
  ageMonths: number;
  lastCommit: string;
  /** 涉及的提交数（去重）；库里没存提交号时为空，此时 revisions 是各文件修订之和。 */
  commits?: number | null;
}

export interface DsmComponentDto {
  key: string;
  label: string;
  symbols: number;
  files: number;
  hasChildren: boolean;
  scc: number;
  hot: DsmHotDto | null;
}

export interface DsmCellDto {
  row: number;
  col: number;
  count: number;
  kinds: Record<string, number>;
}

export interface DsmCouplingDto {
  a: number;
  b: number;
  shared: number;
  degree: number;
}

export interface DsmDto {
  container: string;
  root: string;
  components: DsmComponentDto[];
  cells: DsmCellDto[];
  cycles: string[][];
  coupling: DsmCouplingDto[];
  gitAvailable: boolean;
  note: string | null;
  /** 声明规则（M5e，DsmRuleDto）；老服务没有此字段。 */
  rules?: DsmRuleDto[];
  /** 这一层判出的违规格子（M5e，DsmViolationDto）；老服务没有此字段。 */
  violations?: DsmViolationDto[];
}

// ---- 入口流程 L3（EntryDiscovery.cs / FlowBuilder.cs）----

export interface EntryDto {
  symbolId: number;
  /** http / hosted / command / job / hub / grpc / handler / page / api-route */
  kind: string;
  ruleId: string;
  route: string | null;
  httpMethod: string | null;
  name: string;
  qualifiedName: string;
  file: string;
  line: number;
}

export interface FlowActorDto {
  key: string;
  label: string;
  /** stage：框架流程（/api/arch/framework）里的阶段列，key 是阶段 key。 */
  kind: "project" | "container" | "external" | "stage";
}

export interface FlowNodeDto {
  symbolId: number | null;
  label: string;
  qualifiedName: string;
  /**
   * http：前端 hook 里没对上后端路由的接口调用（label 如 "GET /api/v5/mt5/groups"，qualifiedName 是原始 URL，
   * actor 指向 kind=external 的列，没有 symbolId 与 children，note 说明为什么没对上）。
   */
  /** handlers / controllers：框架流程里的占位（count = 个数），只在 /api/arch/framework 的结果里出现。 */
  kind: "entry" | "call" | "dispatch" | "hop" | "external-group" | "more" | "function" | "http" | "handlers" | "controllers";
  /** 所属 actor 的 key。 */
  actor: string;
  via: string | null;
  confidence: number;
  count: number;
  depth: number;
  children: FlowNodeDto[];
  /** 被折掉没下发的孩子数。 */
  hidden: number;
  note: string | null;
  // 以下为 /api/symbols/{id}/flow 可能带的可选字段；没有时页面按 symbolId 查符号再定位
  /** 符号所在文件（仓库相对路径）。 */
  path?: string | null;
  /** 1 起的行号。 */
  line?: number | null;
  /** UTF-16 字符偏移；有值时打开编辑器优先用它。 */
  pos?: number | null;
}

export interface FlowDto {
  entrySymbolId: number;
  entryLabel: string;
  route: string | null;
  containerKey: string;
  /** 已排好序：入口所在 actor 在前，再按首次出现，外部最后。 */
  actors: FlowActorDto[];
  root: FlowNodeDto;
  behaviors: string[];
  nodes: number;
  truncated: boolean;
  note: string | null;
}

export interface IndexReportDto {
  filesScanned: number;
  filesParsed: number;
  filesFailed: number;
  filesSkipped: number;
  filesRemoved: number;
  filesUnchanged: number;
  totalSymbols: number;
  totalEdges: number;
  elapsedSeconds: number;
  roslyn?: RoslynReportDto | null;
  /** 各阶段耗时，按发生顺序（ts.* / roslyn[解决方案].* / roslyn.finish.* / arch / git / entries）。 */
  timings?: { phase: string; seconds: number }[] | null;
}

export interface RoslynReportDto {
  files: number;
  filesSkipped: number;
  symbolsMerged: number;
  symbolsAdded: number;
  edges: number;
  diRegistrations: number;
  errors: number;
  elapsedSeconds: number;
  /** 没跑成的原因；成功为 null。 */
  error: string | null;
  /** 本机缺 .NET 10 运行时或 SDK：C# 用 tree-sitter 的结果，其他语言不受影响。 */
  unavailable?: boolean;
}

export interface IndexStatusDto {
  running: boolean;
  phase: string | null;
  done: number;
  total: number;
  lastReport: IndexReportDto | null;
  error: string | null;
}

// ---- 本地 AI 叙述 M5d（NarrativeDto / NarrationStatusDto）----

/** 叙述对象的三种粒度；key 分别是 容器键 / `<容器键>|<组件键>` / 入口 symbolId 的十进制字符串。 */
export type NarrativeKind = "container" | "component" | "flow";

export type NarrativeStatus = "ready" | "pending" | "running" | "failed" | "unavailable";

export interface NarrativePointDto {
  text: string;
  /** 事实 id：node:<容器键> / edge:<from>→<to>:<protocol> / entry:<symbolId> / sym:<symbolId> / comp:<组件键> / cell:<行键>→<列键> */
  evidence: string[];
  /** 引用对不上输入子图，模型自己推的。 */
  inferred: boolean;
}

export interface NarrativeDto {
  kind: NarrativeKind;
  key: string;
  status: NarrativeStatus;
  model: string | null;
  generatedAt: string | null;
  hash: string | null;
  summary: string | null;
  keyPoints: NarrativePointDto[];
  risks: NarrativePointDto[];
  /** AI 在预设版式里选的一个；null = 没有建议。 */
  layout: string | null;
  error: string | null;
  // 以下为缓存过期判断新增；老服务没有这些字段
  /** 缓存过期：生成后相关文件或架构事实变了。内容仍可看，status 仍是 ready；点「更新」（force）才重算。 */
  stale?: boolean;
  /** 过期原因（中文），如「生成后 3 个相关文件有改动；架构事实有变化」。 */
  staleReason?: string | null;
  /** 生成后有改动的相关文件数。 */
  changedFiles?: number;
  /** 生成时仓库 HEAD 的短哈希；不是 git 仓库为 null。 */
  gitCommit?: string | null;
}

export interface NarrationStatusDto {
  enabled: boolean;
  endpoint: string;
  model: string;
  /** null = 还没探测过。 */
  reachable: boolean | null;
  queued: number;
  running: string | null;
  done: number;
  failed: number;
  lastError: string | null;
  /** 端点不是回环地址时的提示（事实子图 / 源码会发出去）。 */
  remoteWarning: string | null;
  paused: boolean;
  // 以下为联网 AI 新增；老服务没有这些字段
  /** "openai"（OpenAI 兼容协议）| "anthropic"。 */
  provider?: string;
  concurrency?: number;
  /** 是否把入口方法源码发给模型。 */
  includeSource?: boolean;
  /** 服务进程拿到了 CODEATLAS_AI_API_KEY。 */
  hasApiKey?: boolean;
}

// ---- DSM 声明规则（M5e，DsmBuilder.cs 的 DsmRuleDto / DsmViolationDto）----

export type DsmRuleKind = "forbid-dependency" | "require-dependency" | "placement";

export interface DsmRuleDto {
  /** 规则表里的下标；violations 用它引用。 */
  id: number;
  kind: DsmRuleKind | string;
  /** 主语范围：命名空间模式；placement 时是接口名；"*" = 全部。 */
  from: string;
  /** prefix / contains / exact。 */
  fromMatch: string;
  to: string | null;
  toMatch: string | null;
  /** 仓库相对路径。 */
  file: string;
  /** 1 起；yaml 规则为 0。 */
  line: number;
  testName: string | null;
  /** netarchtest / yaml；括号里是无法精确还原的说明。 */
  source: string;
}

export interface DsmViolationDto {
  row: number;
  col: number;
  ruleId: number;
  /** forbid：违规边数；require 未满足：0。 */
  count: number;
}
