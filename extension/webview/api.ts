import type { ArchDto, DsmDto, EntryDto, FlowDto, HealthDto, IndexStatusDto, MapDto, NarrationStatusDto, NarrativeDto, NarrativeKind, NeighborDto, PipelineDto, SymbolDto } from "./types";

/** 默认不含 references：类型引用（参数类型、字段类型）不是调用链的一部分，按需打开。 */
export const DefaultKinds = "calls,implements,inherits,dispatches";
export const KindsWithReferences = DefaultKinds + ",references";

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

  /** 一个入口的调用流程（时序图）；入口不存在或旧服务返回 null。 */
  flow(symbolId: number, depth?: number): Promise<FlowDto | null> {
    return this.get<FlowDto>(`/api/arch/flow?entry=${symbolId}${depth !== undefined ? `&depth=${depth}` : ""}`);
  }

  /** root 为空 = 服务端自动推导。旧版本服务没有这个接口（404）时返回 null，页面退回命名空间视图。 */
  pipeline(root: string): Promise<PipelineDto | null> {
    return this.get<PipelineDto>(`/api/pipeline?root=${encodeURIComponent(root)}`);
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
