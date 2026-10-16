import type { ArchDto, DsmDto, EntryDto, FlowDto, FrameworkFlowDto, HealthDto, IndexStatusDto, MapDto, NarrationStatusDto, NarrativeDto, NarrativeKind, NeighborDto, RelationsDto, SymbolDto } from "./types";

/**
 * 网状调用图默认只取调用链上的边：调用与分发。
 * 继承 / 实现（类型关系）不是"它调谁"，默认不取，工具栏「类型关系」开关打开（看类型之间的关系主要用模块关系视图）；
 * 类型引用（参数类型、字段类型）同理，「类型引用」开关打开。
 */
export const DefaultKinds = "calls,dispatches";
export const TypeRelationKinds = "implements,inherits";
export const KindsWithReferences = DefaultKinds + ",references";

/** 按工具栏开关拼 neighbors 的 kinds 参数。 */
export function neighborKinds(opts: { references?: boolean; typeRelations?: boolean } = {}): string {
  let k = DefaultKinds;
  if (opts.typeRelations) k += "," + TypeRelationKinds;
  if (opts.references) k += ",references";
  return k;
}

/** CodeAtlas.Server 的薄封装。404 返回 null，其他非 2xx 抛错。 */
export class Api {
  constructor(public readonly base: string, private readonly token: string | null = null) {}

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return this.token ? { "X-CodeAtlas-Token": this.token, ...extra } : extra;
  }

  health(): Promise<HealthDto> {
    return this.get<HealthDto>("/api/health") as Promise<HealthDto>;
  }

  async startIndex(full: boolean): Promise<"started" | "busy"> {
    const r = await fetch(`${this.base}/api/index`, {
      method: "POST",
      headers: this.headers({ "content-type": "application/json" }),
      body: JSON.stringify({ full }),
    });
    if (r.status === 409) return "busy";
    if (!r.ok) throw new Error(`POST /api/index → ${r.status}`);
    return "started";
  }

  status(): Promise<IndexStatusDto> {
    return this.get<IndexStatusDto>("/api/index/status") as Promise<IndexStatusDto>;
  }

  async search(q: string, limit = 20): Promise<SymbolDto[]> {
    return (await this.get<SymbolDto[]>(`/api/search?q=${encodeURIComponent(q)}&limit=${limit}`)) ?? [];
  }

  map(root: string): Promise<MapDto> {
    return this.get<MapDto>(`/api/map?root=${encodeURIComponent(root)}`) as Promise<MapDto>;
  }

  /** L1 容器图。旧版本服务没有这个接口（404）时返回 null。 */
  arch(): Promise<ArchDto | null> {
    return this.get<ArchDto>("/api/arch/containers");
  }

  /** L2 组件 DSM；容器不存在或旧服务返回 null。 */
  dsm(container: string, root: string): Promise<DsmDto | null> {
    return this.get<DsmDto>(`/api/arch/dsm?container=${encodeURIComponent(container)}&root=${encodeURIComponent(root)}`);
  }

  /** 容器的入口清单（L3）；旧服务 404 时返回空数组。 */
  async entries(container: string, kind?: string, q?: string): Promise<EntryDto[]> {
    let path = `/api/arch/entries?container=${encodeURIComponent(container)}`;
    if (kind) path += `&kind=${encodeURIComponent(kind)}`;
    if (q) path += `&q=${encodeURIComponent(q)}`;
    return (await this.get<EntryDto[]>(path)) ?? [];
  }

  /**
   * 一个入口的调用流程（时序图）；入口不存在或旧服务返回 null。
   * pick：同一个方法上挂着几条路由时选哪一条（入口清单里的那一项）；旧服务忽略这两个参数。
   */
  flow(symbolId: number, depth?: number, pick?: { route: string | null; httpMethod: string | null }): Promise<FlowDto | null> {
    const extra = (pick?.route ? `&route=${encodeURIComponent(pick.route)}` : "") + (pick?.httpMethod ? `&method=${encodeURIComponent(pick.httpMethod)}` : "");
    return this.get<FlowDto>(`/api/arch/flow?entry=${symbolId}${depth !== undefined ? `&depth=${depth}` : ""}${extra}`);
  }

  /**
   * 从任意一个方法出发的调用流程（调用图的时序模式），数据形状与入口流程一样（FlowDto）。
   * 旧服务没有这个接口、或符号不在索引里时返回 null；调用方可以再试 flow()（入口符号能出图）。
   */
  symbolFlow(symbolId: number, depth?: number): Promise<FlowDto | null> {
    return this.get<FlowDto>(`/api/symbols/${symbolId}/flow${depth !== undefined ? `?depth=${depth}` : ""}`);
  }

  /** 一个容器的框架流程（中间件 → 控制器 → 分派器 → 行为链 → Handler → 收尾）；容器不存在或旧服务（404）返回 null。 */
  framework(container: string): Promise<FrameworkFlowDto | null> {
    return this.get<FrameworkFlowDto>(`/api/arch/framework?container=${encodeURIComponent(container)}`);
  }

  symbol(id: number): Promise<SymbolDto | null> {
    return this.get<SymbolDto>(`/api/symbols/${id}`);
  }

  symbolAt(path: string, line: number, col: number): Promise<SymbolDto | null> {
    return this.get<SymbolDto>(`/api/symbols/at?path=${encodeURIComponent(path)}&line=${line}&col=${col}`);
  }

  async neighbors(id: number, direction: "in" | "out" | "both" = "both", kinds = DefaultKinds): Promise<NeighborDto[]> {
    return (await this.get<NeighborDto[]>(`/api/symbols/${id}/neighbors?direction=${direction}&kinds=${encodeURIComponent(kinds)}`)) ?? [];
  }

  /** 模块关系：以 id 所属的单元（类 / 接口 / … / 模块）为中心。旧服务没有这个接口或符号不在索引里时返回 null。 */
  relations(id: number, depth: 1 | 2, tests: boolean): Promise<RelationsDto | null> {
    return this.get<RelationsDto>(`/api/symbols/${id}/relations?depth=${depth}&tests=${tests}`);
  }

  // ---- 本地 AI 叙述（M5d）。老服务没有这些路由 → 404 → null，页面按 unavailable 处理 ----

  /** 读缓存里的叙述；没算过服务端也会返回一个 status=pending/unavailable 的壳。 */
  narrative(kind: NarrativeKind, key: string): Promise<NarrativeDto | null> {
    return this.get<NarrativeDto>(`/api/arch/narrative?kind=${encodeURIComponent(kind)}&key=${encodeURIComponent(key)}`);
  }

  /** 把对象排进生成队列；force 忽略缓存重算。返回 202 的当前状态。 */
  narrate(kind: NarrativeKind, key: string, force = false): Promise<NarrativeDto | null> {
    return this.post<NarrativeDto>("/api/arch/narrate", { kind, key, force });
  }

  narrateStatus(): Promise<NarrationStatusDto | null> {
    return this.get<NarrationStatusDto>("/api/arch/narrate/status");
  }

  narrateControl(action: "stop" | "resume"): Promise<NarrationStatusDto | null> {
    return this.post<NarrationStatusDto>(`/api/arch/narrate/${action}`, {});
  }

  private async get<T>(path: string): Promise<T | null> {
    const r = await fetch(this.base + path, { headers: this.headers() });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`GET ${path} → ${r.status}`);
    return (await r.json()) as T;
  }

  private async post<T>(path: string, body: unknown): Promise<T | null> {
    const r = await fetch(this.base + path, { method: "POST", headers: this.headers({ "content-type": "application/json" }), body: JSON.stringify(body) });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`POST ${path} → ${r.status}`);
    return (await r.json()) as T;
  }
}
