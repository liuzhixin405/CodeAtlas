// 编辑器 CodeLens：每个方法 / 函数 / 构造函数上方"入口 · 被 N 处调用 · 测试 K · 调用 M 个 · 影响范围"。vscode 薄层；
// 文案、行号漂移时隐藏、缓存在 codeLensModel.ts。一次请求整个文件（GET /api/files/lens），保存 / 索引完成后刷新。
// 服务规则同保存时增量：服务没起来且仓库没有 .codeatlas/index.db 时什么都不显示，不拉服务、不建库。
import { existsSync } from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import type { FileLensDto } from "../webview/types";
import { fileLenses, LensCache, type LensSpec, type MethodLenses } from "./codeLensModel";
import type { JumpArgs } from "./jumpCommands";

export interface LensClient {
  fileLens(absolutePath: string): Promise<FileLensDto | null>;
}

export interface LensHost {
  /** 文件归哪个服务（主根）；不归任何服务为 undefined。 */
  rootFor(uri: vscode.Uri): string | undefined;
  isSupported(filePath: string): boolean;
  /** 本会话已经为这个根拉起过服务。 */
  hasServer(root: string): boolean;
  /** 拉起 / 复用服务，返回客户端；失败返回 null（错误已报）。 */
  connect(root: string): Promise<LensClient | null>;
  log(line: string): void;
}

export interface CodeAtlasLens extends vscode.Disposable {
  /** 索引跑完了 / 服务重启：作废全部缓存并让编辑器重新要 CodeLens。 */
  indexChanged(): void;
}

const EnabledKey = "codeLens.enabled";

export function registerCodeLens(context: vscode.ExtensionContext, host: LensHost, extensions: Iterable<string>): CodeAtlasLens {
  const provider = new LensProvider(host);
  const pattern = `**/*.{${[...extensions].map((e) => e.replace(/^\./, "")).join(",")}}`;
  context.subscriptions.push(provider, vscode.languages.registerCodeLensProvider({ scheme: "file", pattern }, provider));
  return provider;
}

class LensProvider implements vscode.CodeLensProvider, CodeAtlasLens {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeCodeLenses = this.changed.event;
  private readonly disposables: vscode.Disposable[] = [];
  private readonly cache: LensCache<MethodLenses[]>;
  /** 每个根只记一次"lens 请求失败"，免得每次编辑都刷日志。 */
  private readonly loggedFailure = new Set<string>();

  constructor(private readonly host: LensHost) {
    this.cache = new LensCache((filePath) => this.fetch(filePath));
    this.disposables.push(
      this.changed,
      vscode.workspace.onDidSaveTextDocument((doc) => {
        if (!this.host.isSupported(doc.uri.fsPath)) return;
        // 保存后文件不再"没保存"，过期标记也要重新问服务；索引跑完还会再全部刷新一次
        this.cache.invalidate(doc.uri.fsPath);
        this.changed.fire();
      }),
      vscode.workspace.onDidChangeConfiguration((e) => { if (e.affectsConfiguration(`codeatlas.${EnabledKey}`)) this.changed.fire(); }),
    );
  }

  indexChanged(): void {
    this.cache.invalidate();
    this.loggedFailure.clear();
    this.changed.fire();
  }

  async provideCodeLenses(doc: vscode.TextDocument): Promise<vscode.CodeLens[]> {
    if (!vscode.workspace.getConfiguration("codeatlas").get<boolean>(EnabledKey, true)) return [];
    if (doc.uri.scheme !== "file" || !this.host.isSupported(doc.uri.fsPath)) return [];
    const root = this.host.rootFor(doc.uri);
    if (!root) return [];
    // 同保存时增量：只对本会话用过 CodeTrail Graph、或已有索引库的文件夹
    if (!this.host.hasServer(root) && !existsSync(path.join(root, ".codeatlas", "index.db"))) return [];
    try {
      const methods = await this.cache.lenses(doc.uri.fsPath, doc.version, (dto) => fileLenses(dto, {
        checkLines: doc.isDirty || dto.stale,
        lineText: (line) => (line >= 1 && line <= doc.lineCount ? doc.lineAt(line - 1).text : undefined),
      }));
      return methods ? toCodeLenses(methods, root, doc) : [];
    } catch (e) {
      if (!this.loggedFailure.has(root)) {
        this.loggedFailure.add(root);
        this.host.log(`CodeLens 取数失败（这个仓库后面的失败不再记）：${e}`);
      }
      return [];
    }
  }

  private async fetch(filePath: string): Promise<FileLensDto | null> {
    const root = this.host.rootFor(vscode.Uri.file(filePath));
    if (!root) return null;
    const client = await this.host.connect(root);
    // 服务起不来：抛出去不缓存，下次再试
    if (!client) throw new Error("CodeTrail Graph 服务不可用");
    return client.fileLens(filePath);
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }
}

function toCodeLenses(methods: MethodLenses[], repoRoot: string, doc: vscode.TextDocument): vscode.CodeLens[] {
  const out: vscode.CodeLens[] = [];
  for (const m of methods) {
    const line = Math.min(m.line, doc.lineCount) - 1;
    if (line < 0) continue;
    const range = new vscode.Range(line, 0, line, 0);
    for (const spec of m.lenses) out.push(new vscode.CodeLens(range, lensCommand(spec, { repoRoot, symbol: m.symbol })));
  }
  return out;
}

function lensCommand(spec: LensSpec, args: JumpArgs): vscode.Command {
  // 不能点的只是一行字：command 为空串时 VS Code 显示成不带链接的文字
  if (!spec.clickable) return { title: spec.title, tooltip: spec.tooltip, command: "" };
  switch (spec.kind) {
    case "callers": return { title: spec.title, tooltip: spec.tooltip, command: "codeatlas.gotoCallers", arguments: [args] };
    case "tests": return { title: spec.title, tooltip: spec.tooltip, command: "codeatlas.gotoCallers", arguments: [{ ...args, tests: true }] };
    case "callees": return { title: spec.title, tooltip: spec.tooltip, command: "codeatlas.gotoCallees", arguments: [args] };
    case "impact": return { title: spec.title, tooltip: spec.tooltip, command: "codeatlas.impact.showFor", arguments: [args.symbol, args.repoRoot] };
    default: return { title: spec.title, tooltip: spec.tooltip, command: "" };
  }
}
