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

// ---- 管道视图（PipelineBuilder.cs）----

export interface PipelineItemDto {
  name: string;
  qualifiedName: string;
  symbolId: number;
  weight: number;
}

export interface PipelineCellDto {
  count: number;
  items: PipelineItemDto[];
}

export interface PipelineMessagesDto {
  count: number;
  commands: number;
  queries: number;
  events: number;
  /** 没有任何非测试入口分发过的消息数。 */
  orphans: number;
}

export interface PipelineGroupDto {
  key: string;
  label: string;
  weight: number;
}

export interface PipelineRowDto {
  key: string;
  label: string;
  /** 命名空间行可钻；叶子行（消息行）为 false。 */
  hasChildren: boolean;
  /** 叶子行对应的消息类型 id。 */
  symbolId: number | null;
  entries: PipelineCellDto;
  messages: PipelineMessagesDto;
  handlers: PipelineCellDto;
  /** 本行 Handler 发出的分发条数。 */
  nested: number;
  /** 非 Handler、非测试代码直接调用本行 Handler 方法的条数（绕过管道）。 */
  bypass: number;
  externalCalls: number;
  downstream: PipelineGroupDto[];
}

export interface PipelineFlowDto {
  from: string;
  to: string;
  kind: "nested" | "bypass";
  weight: number;
}

export interface PipelineBehaviorDto {
  name: string;
  qualifiedName: string;
  symbolId: number;
  order: number;
}

export interface PipelineDto {
  /** 实际使用的 root；没有分发数据时为空串且 rows 为空。 */
  root: string;
  leaf: boolean;
  /** 每个 Behavior 都有 DI 注册记录时为 true；否则按名字排，顺序不可信。 */
  behaviorsOrdered: boolean;
  testEntriesSkipped: number;
  messagesOutsideRoot: number;
  behaviors: PipelineBehaviorDto[];
  rows: PipelineRowDto[];
  flows: PipelineFlowDto[];
  /** 自动推导出的业务模块层；面包屑以它为底。 */
  defaultRoot: string;
  /** 测试代码里的消息（含其 Handler）被整体排除的个数。 */
  testMessagesSkipped: number;
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
  /** http / hosted / job / hub / handler / page / api-route */
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
  kind: "project" | "container" | "external";
}

export interface FlowNodeDto {
  symbolId: number | null;
  label: string;
  qualifiedName: string;
  kind: "entry" | "call" | "dispatch" | "hop" | "external-group" | "more" | "function";
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
