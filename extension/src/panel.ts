import * as crypto from "node:crypto";
import * as vscode from "vscode";
import { isAllowedWebviewCommand } from "./aiServices";

export interface PanelCallbacks {
  /** 页面请求跳转：path 是相对仓库根的路径；pos（UTF-16 字符偏移）有值时优先用它定位。 */
  open(repoRoot: string, relativePath: string, line: number, col: number, pos?: number | null): void;
  requestIndex(repoRoot: string, full: boolean): void;
  /** 页面勾选 / 取消工具栏的"跟随光标"；页面重新加载或面板关闭时为 false。 */
  followCursor?(enabled: boolean): void;
}

/** 承载 2b 页面的 Webview 面板。单例；切换工作区文件夹时重新 init。 */
export class PanelHost {
  private panel: vscode.WebviewPanel | null = null;
  private apiBase: string | null = null;
  private token: string | null = null;
  private repoRoot: string | null = null;
  private ready = false;
  /** 页面还没 ready 时先存着，ready 后再发（聚焦调用图 / 打开模块关系）。 */
  private pendingFocus: { symbolId: number; chain?: "in" | "out"; relations?: boolean } | null = null;

  constructor(private readonly context: vscode.ExtensionContext, private readonly cb: PanelCallbacks) {}

  get isOpen(): boolean {
    return this.panel !== null;
  }

  /** 面板当前连着哪个仓库的服务；没开过为 null。 */
  get currentRepo(): string | null {
    return this.repoRoot;
  }

  show(repoRoot: string, apiBase: string, token: string): void {
    const column = vscode.ViewColumn.Beside;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel("codeatlas.panel", "CodeTrail Graph", { viewColumn: column, preserveFocus: true }, {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, "media"), vscode.Uri.joinPath(this.context.extensionUri, "webview")],
      });
      this.panel.iconPath = vscode.Uri.joinPath(this.context.extensionUri, "webview", "icon.svg");
      this.panel.webview.html = this.html(this.panel.webview);
      this.panel.webview.onDidReceiveMessage((msg: { type: string; path?: string; line?: number; col?: number; pos?: number | null; full?: boolean; command?: unknown; enabled?: unknown }) => {
        if (msg.type === "ready") {
          this.ready = true;
          this.cb.followCursor?.(false); // 页面（重新）加载时复选框是空的，勾上会再发
          this.sendInit();
          if (this.pendingFocus !== null) {
            const p = this.pendingFocus;
            this.pendingFocus = null;
            if (p.relations) this.showRelations(p.symbolId); else this.focus(p.symbolId, p.chain);
          }
        } else if (msg.type === "open" && this.repoRoot && msg.path) {
          this.cb.open(this.repoRoot, msg.path, msg.line ?? 1, msg.col ?? 1, typeof msg.pos === "number" ? msg.pos : null);
        } else if (msg.type === "requestIndex" && this.repoRoot) {
          this.cb.requestIndex(this.repoRoot, Boolean(msg.full));
        } else if (msg.type === "followCursor") {
          this.cb.followCursor?.(msg.enabled === true);
        } else if (msg.type === "command" && isAllowedWebviewCommand(msg.command)) {
          // 页面只能触发白名单里的命令（设置 API Key / 选择 AI 服务），其他一律忽略
          void vscode.commands.executeCommand(msg.command);
        }
      });
      this.panel.onDidDispose(() => { this.panel = null; this.ready = false; this.cb.followCursor?.(false); });
    } else {
      this.panel.reveal(column, true);
    }
    if (this.repoRoot !== repoRoot || this.apiBase !== apiBase || this.token !== token) {
      this.repoRoot = repoRoot;
      this.apiBase = apiBase;
      this.token = token;
      if (this.ready) this.sendInit();
    }
  }

  /** 调用图聚焦；chain = "in" 时聚焦后接着"↑ 到源头"（影响范围视图的"在面板中打开"）。 */
  focus(symbolId: number, chain?: "in" | "out"): void {
    if (!this.panel) return;
    if (!this.ready) { this.pendingFocus = { symbolId, chain }; return; }
    void this.panel.webview.postMessage({ type: "focus", symbolId, chain });
  }

  /** 切到「模块关系」，以 symbolId 所属的类为中心。 */
  showRelations(symbolId: number): void {
    if (!this.panel) return;
    if (!this.ready) { this.pendingFocus = { symbolId, relations: true }; return; }
    void this.panel.webview.postMessage({ type: "showRelations", symbolId });
  }

  /** 跟随光标：编辑器光标所在方法（页面只在调用图模式且勾了"跟随光标"时换焦点）。 */
  cursorSymbol(symbolId: number): void {
    if (this.panel && this.ready) void this.panel.webview.postMessage({ type: "cursorSymbol", symbolId });
  }

  indexChanged(): void {
    void this.panel?.webview.postMessage({ type: "indexChanged" });
  }

  dispose(): void {
    this.panel?.dispose();
  }

  private sendInit(): void {
    if (this.panel && this.apiBase) void this.panel.webview.postMessage({ type: "init", apiBase: this.apiBase, token: this.token, repoRoot: this.repoRoot });
  }

  private html(webview: vscode.Webview): string {
    const nonce = crypto.randomBytes(16).toString("hex");
    const script = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "media", "panel.js"));
    const style = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, "webview", "panel.css"));
    const csp = [
      "default-src 'none'",
      `style-src ${webview.cspSource} 'unsafe-inline'`,
      `script-src 'nonce-${nonce}'`,
      `font-src ${webview.cspSource}`,
      `img-src ${webview.cspSource} data:`,
      "connect-src http://127.0.0.1:* http://localhost:*",
    ].join("; ");
    return `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <link rel="stylesheet" href="${style}" />
  <title>CodeTrail Graph</title>
</head>
<body>
  <div id="app"></div>
  <script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }
}
