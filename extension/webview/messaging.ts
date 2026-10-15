import type { WebviewCommand } from "../src/aiServices";

/** 页面 → 扩展 */
export type OutMessage =
  | { type: "ready" }
  /** pos：UTF-16 字符偏移（document.positionAt）；有值时宿主应优先用它定位，没有再用 line / col。 */
  | { type: "open"; path: string; line: number; col: number; pos?: number | null }
  | { type: "requestIndex"; full: boolean }
  /** 只允许白名单命令，扩展侧再校验一遍。 */
  | { type: "command"; command: WebviewCommand }
  /** 工具栏"跟随光标"勾选 / 取消：勾上后宿主把编辑器光标所在方法发过来（cursorSymbol）。 */
  | { type: "followCursor"; enabled: boolean };

/** 扩展 → 页面 */
export type InMessage =
  | { type: "init"; apiBase: string; token?: string; repoRoot?: string }
  /** chain：聚焦后接着沿调用链展开（"in" = ↑ 到源头）。 */
  | { type: "focus"; symbolId: number; chain?: "in" | "out" }
  | { type: "indexChanged" }
  /** 跟随光标：编辑器光标所在方法（宿主已防抖、同一方法不重发）；页面只在调用图模式且勾了"跟随光标"时把它设为焦点。 */
  | { type: "cursorSymbol"; symbolId: number }
  /** 切到「模块关系」并以 symbolId 所属的单元为中心；depth 省略时沿用页面当前的深度。 */
  | { type: "showRelations"; symbolId: number; depth?: 1 | 2 };

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
