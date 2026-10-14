import type { WebviewCommand } from "../src/aiServices";

/** 页面 → 扩展 */
export type OutMessage =
  | { type: "ready" }
  | { type: "open"; path: string; line: number; col: number }
  | { type: "requestIndex"; full: boolean }
  /** 只允许白名单命令，扩展侧再校验一遍。 */
  | { type: "command"; command: WebviewCommand };

/** 扩展 → 页面 */
export type InMessage =
  | { type: "init"; apiBase: string; token?: string; repoRoot?: string }
  | { type: "focus"; symbolId: number }
  | { type: "indexChanged" };

interface VsCodeApi {
  postMessage(msg: unknown): void;
}

declare function acquireVsCodeApi(): VsCodeApi;

const vscode: VsCodeApi | null = typeof acquireVsCodeApi === "function" ? acquireVsCodeApi() : null;

export const host = {
  /** 在 VS Code 里运行。浏览器调试模式下为 false。 */
  isVsCode: vscode !== null,

  post(msg: OutMessage): void {
    if (vscode) vscode.postMessage(msg);
    else console.info("[codeatlas → host]", msg);
  },

  onMessage(handler: (msg: InMessage) => void): void {
    window.addEventListener("message", (e: MessageEvent) => {
      const data = e.data as InMessage | undefined;
      if (data && typeof data === "object" && "type" in data) handler(data);
    });
  },
};
