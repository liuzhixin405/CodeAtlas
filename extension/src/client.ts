import type { FileLensDto, HealthDto, ImpactDto, IndexStatusDto, OutlineDto, SymbolDto } from "../webview/types";

/** 扩展侧用的服务客户端（Node 18+ 自带 fetch）。 */
export class ServerClient {
  constructor(readonly apiBase: string, private readonly token: string) {}

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return { "X-CodeAtlas-Token": this.token, ...extra };
  }

  health(): Promise<HealthDto> {
    return this.json<HealthDto>("/api/health");
  }

  async symbolAt(absolutePath: string, line: number, col: number): Promise<SymbolDto | null> {
    const r = await fetch(`${this.apiBase}/api/symbols/at?path=${encodeURIComponent(absolutePath)}&line=${line}&col=${col}`, { headers: this.headers() });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`symbols/at → ${r.status}`);
    return (await r.json()) as SymbolDto;
  }

  /** 调用大纲：一个方法按源码顺序的调用步骤（只有一层）。符号不存在返回 null。 */
  async outline(id: number, opts: { externals: boolean; tests: boolean }): Promise<OutlineDto | null> {
    const q = `externals=${opts.externals ? "true" : "false"}&tests=${opts.tests ? "true" : "false"}`;
    const r = await fetch(`${this.apiBase}/api/symbols/${id}/outline?${q}`, { headers: this.headers() });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`symbols/${id}/outline → ${r.status}`);
    return (await r.json()) as OutlineDto;
  }

  /**
   * 影响范围：谁调用了它（一层），entries=true 时还带往上能走到的全部入口。
   * 符号不存在（或服务版本过旧还没有 impact 接口）返回 null。
   */
  async impact(id: number, opts: { entries: boolean; tests: boolean }): Promise<ImpactDto | null> {
    const q = `entries=${opts.entries ? "true" : "false"}&tests=${opts.tests ? "true" : "false"}`;
    const r = await fetch(`${this.apiBase}/api/symbols/${id}/impact?${q}`, { headers: this.headers() });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`symbols/${id}/impact → ${r.status}`);
    return (await r.json()) as ImpactDto;
  }

  /**
   * 快速跳转：整个文件里每个方法的调用计数（CodeLens 用）。文件不在索引里时服务返回空 symbols；
   * 服务版本过旧还没有这个接口（404）返回 null。
   */
  async fileLens(absolutePath: string): Promise<FileLensDto | null> {
    const r = await fetch(`${this.apiBase}/api/files/lens?path=${encodeURIComponent(absolutePath)}`, { headers: this.headers() });
    if (r.status === 404) return null;
    if (!r.ok) throw new Error(`files/lens → ${r.status}`);
    return (await r.json()) as FileLensDto;
  }

  async startIndex(full: boolean): Promise<"started" | "busy"> {
    const r = await fetch(`${this.apiBase}/api/index`, {
      method: "POST",
      headers: this.headers({ "content-type": "application/json" }),
      body: JSON.stringify({ full }),
    });
    if (r.status === 409) return "busy";
    if (!r.ok) throw new Error(`POST /api/index → ${r.status}`);
    return "started";
  }

  status(): Promise<IndexStatusDto> {
    return this.json<IndexStatusDto>("/api/index/status");
  }

  /** 轮询直到索引结束；每次状态变化回调一次。 */
  async waitForIndex(onProgress: (s: IndexStatusDto) => void, intervalMs = 500): Promise<IndexStatusDto> {
    for (;;) {
      const s = await this.status();
      onProgress(s);
      if (!s.running) return s;
      await new Promise((r) => setTimeout(r, intervalMs));
    }
  }

  private async json<T>(path: string): Promise<T> {
    const r = await fetch(this.apiBase + path, { headers: this.headers() });
    if (!r.ok) throw new Error(`GET ${path} → ${r.status}`);
    return (await r.json()) as T;
  }
}
