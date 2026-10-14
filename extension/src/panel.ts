import * as crypto from "node:crypto";
import * as vscode from "vscode";
import { isAllowedWebviewCommand } from "./aiServices";

export interface PanelCallbacks {
  /** 页面请求跳转：path 是相对仓库根的路径。 */
  open(repoRoot: string, relativePath: string, line: number, col: number): void;
  requestIndex(repoRoot: string, full: boolean): void;
}

/** 承载 2b 页面的 Webview 面板。单例；切换工作区文件夹时重新 init。 */
export class PanelHost {
  private panel: vscode.WebviewPanel | null = null;
  private apiBase: string | null = null;
  private token: string | null = null;
  private repoRoot: string | null = null;
  private ready = false;
  private pendingFocus: number | null = null;

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
      this.panel = vscode.window.createWebviewPanel("codeatlas.panel", "CodeAtlas", { viewColumn: column, preserveFocus: true }, {
        enableScripts: true,
        retainContextWhenHidden: true,
        localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, "media"), vscode.Uri.joinPath(this.context.extensionUri, "webview")],
      });
      this.panel.iconPath = vscode.Uri.joinPath(this.context.extensionUri, "webview", "icon.svg");
      this.panel.webview.html = this.html(this.panel.webview);
      this.panel.webview.onDidReceiveMessage((msg: { type: string; path?: string; line?: number; col?: number; full?: boolean; command?: unknown }) => {
        if (msg.type === "ready") {
          this.ready = true;
          this.sendInit();
          if (this.pendingFocus !== null) { this.focus(this.pendingFocus); this.pendingFocus = null; }
        } else if (msg.type === "open" && this.repoRoot && msg.path) {
          this.cb.open(this.repoRoot, msg.path, msg.line ?? 1, msg.col ?? 1);
        } else if (msg.type === "requestIndex" && this.repoRoot) {
          this.cb.requestIndex(this.repoRoot, Boolean(msg.full));
        } else if (msg.type === "command" && isAllowedWebviewCommand(msg.command)) {
          // 页面只能触发白名单里的命令（设置 API Key / 选择 AI 服务），其他一律忽略
          void vscode.commands.executeCommand(msg.command);
        }
      });
      this.panel.onDidDispose(() => { this.panel = null; this.ready = false; });
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

  focus(symbolId: number): void {
    if (!this.panel) return;
    if (!this.ready) { this.pendingFocus = symbolId; return; }
    void this.panel.webview.postMessage({ type: "focus", symbolId });
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
  <title>CodeAtlas</title>
</head>
<body>
  <div id="app"></div>
  <script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
  }
}
