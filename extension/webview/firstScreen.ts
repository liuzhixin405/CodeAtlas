import type { ArchDto, ArchNodeDto, DsmDto, EntryDto, FlowDto, FlowNodeDto } from "./types";

/**
 * 项目地图第一屏的决策（纯函数，便于测试）。
 * 只有一个可部署容器时（典型：单独打开一个前端仓库），容器图只剩一个方块加几个占位外部系统，
 * 没什么可看；直接进它的「入口流程」并选好一个入口，右边一打开就是时序图。
 */

/** 容器图里唯一的非外部容器；人 / 外部系统不算。零个或两个及以上返回 null。 */
export function soleContainer(arch: ArchDto | null): ArchNodeDto | null {
  if (!arch) return null;
  const containers = arch.nodes.filter((n) => n.kind === "container");
  return containers.length === 1 ? containers[0]! : null;
}

/** 第一屏是否直接进入唯一容器的入口流程。 */
export function shouldAutoOpenFlow(arch: ArchDto | null): boolean {
  return soleContainer(arch) !== null;
}

/** 整库伪容器的键（服务端 DsmBuilder.RepoKey）：纯类库仓库没有容器，DSM 按整个仓库的项目 / 模块算。 */
export const RepoDsmKey = "*";

/** 纯类库仓库第一屏的提示。 */
export const NoServiceHint = "未发现可部署的服务，显示模块依赖";

/**
 * 第一屏是否直接打开整库 DSM：服务给了容器图（不是旧服务的 null）但一个容器都没有——只有类库，
 * 容器图是空的、入口流程无从谈起，模块之间谁依赖谁才是这类仓库该看的。
 * 还没索引时也没有容器；调用方再用 hasDsmContent 判 DSM 是否真有组件，没有就退回原来的管道 / 命名空间视图。
 */
export function shouldOpenRepoDsm(arch: ArchDto | null): boolean {
  return arch !== null && !arch.nodes.some((n) => n.kind === "container");
}

/** DSM 有没有可看的组件（服务没有 /api/arch/dsm 或成员目录下没有符号时为否）。 */
export function hasDsmContent(d: DsmDto | null): boolean {
  return d !== null && d.components.length > 0;
}

/** 整库伪容器的节点：面包屑与 DSM 标题用。name 一般是仓库目录名。 */
export function repoContainerNode(name: string): ArchNodeDto {
  return { key: RepoDsmKey, kind: "container", subKind: "repo", name: name || "仓库", path: "", source: "discovered", evidence: [], members: [], ports: [], namespaceRoots: [], outbound: 0 };
}

/** 是不是整库伪容器（没有入口流程、没有容器叙述）。 */
export function isRepoContainer(c: ArchNodeDto | null): boolean {
  return c?.key === RepoDsmKey;
}

/** 少于这么多节点的流程算「太薄」（入口自己 + 一个调用），右边几乎是空的。 */
export const ThinFlowNodes = 3;

/** 最多看几个候选入口的流程（每个一次 /api/arch/flow 请求，服务端有缓存；ccs-ui 52 个页面约 1–2 秒）。 */
export const MaxDefaultEntryTries = 60;

/** 同时在飞的流程请求数。 */
export const DefaultEntryConcurrency = 4;

/**
 * 默认入口的候选顺序（也是同分时的先后）：
 * 1. 前端页面（kind=page），路由越短越靠前（`/` 最先），同长按字典序；
 * 2. HTTP 路由，保持服务端给的顺序；
 * 3. 命令行命令（kind=command），保持原顺序——命令行工具的入口是命令，排在启动入口（hosted 的 Main）前面；
 * 4. 其余入口，保持原顺序。
 */
export function rankDefaultEntries(entries: readonly EntryDto[]): EntryDto[] {
  const routeOf = (e: EntryDto) => e.route ?? e.name;
  const pages = entries.filter((e) => e.kind === "page")
    .map((e, i) => ({ e, i }))
    .sort((a, b) => routeOf(a.e).length - routeOf(b.e).length || (routeOf(a.e) < routeOf(b.e) ? -1 : routeOf(a.e) > routeOf(b.e) ? 1 : a.i - b.i))
    .map((x) => x.e);
  const http = entries.filter((e) => e.kind === "http");
  const commands = entries.filter((e) => e.kind === "command");
  const rest = entries.filter((e) => e.kind !== "page" && e.kind !== "http" && e.kind !== "command");
  return [...pages, ...http, ...commands, ...rest];
}

/** 流程太薄：节点数不到 ThinFlowNodes。 */
export function isThinFlow(f: FlowDto): boolean {
  return f.nodes < ThinFlowNodes;
}

/** 流程里的接口调用数：续接进后端的 hop + 没对上后端的 http。 */
export function interfaceCalls(f: FlowDto): number {
  let n = 0;
  const walk = (x: FlowNodeDto) => { if (x.kind === "hop" || x.kind === "http") n += x.count || 1; x.children.forEach(walk); };
  walk(f.root);
  return n;
}

export interface DefaultEntryPick {
  entry: EntryDto;
  /** 挑选时已经拿到的流程；null = 拿不到（旧服务 / 入口失效），调用方自己再请求或提示。 */
  flow: FlowDto | null;
}

/**
 * 挑一条最能说明这个项目在干什么的流程：前 maxTries 个候选都取流程，按 接口调用数 → 节点数 → 候选顺序 取最好的。
 * 用户第一次反馈"前端地图光秃秃"后，第一版按"路由最短且不太薄"挑，落到了登录页（没有任何接口调用）；
 * 业务页面（组管理、订单…）路由更长，但调用了后端接口，才是用户想看的那种链。
 * 都拿不到流程时用第一个候选。没有入口返回 null。cancelled() 为真时提前停（用户等待期间自己点了入口）。
 */
export async function pickDefaultEntry(
  entries: readonly EntryDto[],
  loadFlow: (e: EntryDto) => Promise<FlowDto | null>,
  maxTries = MaxDefaultEntryTries,
  cancelled: () => boolean = () => false,
  concurrency = DefaultEntryConcurrency,
): Promise<DefaultEntryPick | null> {
  const ranked = rankDefaultEntries(entries);
  if (ranked.length === 0) return null;
  const candidates = ranked.slice(0, Math.max(1, maxTries));
  const flows: (FlowDto | null)[] = new Array(candidates.length).fill(null);
  let next = 0;
  const worker = async () => {
    while (next < candidates.length && !cancelled()) {
      const i = next++;
      try { flows[i] = await loadFlow(candidates[i]!); } catch { flows[i] = null; }
    }
  };
  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, candidates.length)) }, worker));
  if (cancelled()) return null;
  let best = -1;
  const better = (i: number, j: number) => {
    const a = flows[i]!, b = flows[j]!;
    const ta = isThinFlow(a) ? 0 : 1, tb = isThinFlow(b) ? 0 : 1;
    if (ta !== tb) return ta > tb;
    const ca = interfaceCalls(a), cb = interfaceCalls(b);
    if (ca !== cb) return ca > cb;
    return a.nodes > b.nodes; // 同分取靠前的（i 更大才走到这里，严格大于才换）
  };
  for (let i = 0; i < candidates.length; i++) {
    if (!flows[i]) continue;
    if (best < 0 || better(i, best)) best = i;
  }
  return best < 0 ? { entry: candidates[0]!, flow: null } : { entry: candidates[best]!, flow: flows[best] ?? null };
}
/** 刷新后在新入口清单里找回原来选中的入口（索引重建后 symbolId 会变）：先按 id，再按 种类 + 限定名 + 路由。 */
export function findSameEntry(entries: readonly EntryDto[], old: EntryDto): EntryDto | null {
  return entries.find((e) => e.symbolId === old.symbolId && e.qualifiedName === old.qualifiedName)
    ?? entries.find((e) => e.kind === old.kind && e.qualifiedName === old.qualifiedName && e.route === old.route && e.httpMethod === old.httpMethod)
    ?? null;
}
