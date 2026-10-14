import type { HealthDto, IndexStatusDto, SymbolDto } from "../webview/types";

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
