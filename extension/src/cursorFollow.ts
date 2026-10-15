// 侧边栏视图共用的"跟随光标"vscode 薄层：把编辑器事件喂给一个共享的 CursorSource（逻辑在 outlineFollow.ts），
// 各视图（调用大纲、影响范围）订阅它；跳转编辑器也走这里，好让"自己跳转引起的光标移动不跟随"对所有视图生效。
import { existsSync } from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { CursorSource, resolveCursor, type CursorPos, type FollowClient } from "./outlineFollow";
import { jumpPosition, type DocLike } from "./jumpModel";
import type { JumpTarget } from "./outlineModel";

export interface FollowHost {
  /** 文件归哪个服务（主根）。 */
  rootFor(uri: vscode.Uri): string | undefined;
  isSupported(filePath: string): boolean;
  hasServer(root: string): boolean;
  /** 拉起 / 复用服务，返回客户端；失败返回 null（错误已报）。 */
  connect(root: string): Promise<FollowClient | null>;
}

export class SharedFollow implements vscode.Disposable {
  readonly source: CursorSource;
  /** 已知有索引的根（health.files > 0），省得每次光标移动都问一遍；几个视图共用。 */
  readonly indexed = new Set<string>();
  private readonly disposables: vscode.Disposable[] = [];

  constructor(private readonly host: FollowHost) {
    this.source = new CursorSource({
      isSupported: (p) => host.isSupported(p),
      resolve: (pos) => resolveCursor(pos, {
        rootFor: (p) => host.rootFor(vscode.Uri.file(p)),
        hasServer: (r) => host.hasServer(r),
        indexExists: (r) => existsSync(path.join(r, ".codeatlas", "index.db")),
        connect: (r) => host.connect(r),
      }, this.indexed),
    });
    this.disposables.push(
      { dispose: () => this.source.dispose() },
      vscode.window.onDidChangeTextEditorSelection((e) => {
        if (e.textEditor !== vscode.window.activeTextEditor) return;
        const user = e.kind === vscode.TextEditorSelectionChangeKind.Keyboard || e.kind === vscode.TextEditorSelectionChangeKind.Mouse;
        this.source.notify(posOf(e.textEditor), user);
      }),
      vscode.window.onDidChangeActiveTextEditor((e) => this.source.notify(e ? posOf(e) : undefined, false)),
    );
  }

  /** 光标在一个归 CodeTrail Graph 管的支持文件里（刷新时据此决定"按光标重新解析"还是"只重取当前根"）。 */
  canFollow(pos: CursorPos | undefined): pos is CursorPos {
    return !!pos && this.host.isSupported(pos.filePath) && !!this.host.rootFor(vscode.Uri.file(pos.filePath));
  }

  /** 索引跑完了：忘掉"已知有索引"，按当前光标重新解析一次（各视图先各自清好缓存再调这里）。 */
  indexChanged(): void {
    this.indexed.clear();
    void this.source.refresh(currentPos());
  }

  /** 跳到仓库里某处：打开文件、设光标、滚到可见；期间引起的光标移动不跟随（侧边栏视图里点跳转用）。 */
  async jump(repoRoot: string, target: JumpTarget | null, preserveFocus: boolean): Promise<void> {
    await openAt(repoRoot, target, { preserveFocus, preview: true, beforeSelect: () => this.source.beginNavigation() });
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }
}

/**
 * 打开仓库里的文件并把光标放到目标处。光标通过 showTextDocument 的 selection 选项设（编辑器按"导航"记这次选区变化），
 * 所以会进 VS Code 的前进 / 后退历史（Alt+← 回去），同一文件里相距不到 10 行的跳转也会单独记一条。
 * 位置：pos 换算出的行与记录的行一致用 pos，否则按行（jumpModel.jumpPosition）。
 * beforeSelect：打开文档之前、设光标之前各调一次（侧边栏视图用它开"不跟随"宽限期）。
 */
export async function openAt(repoRoot: string, target: JumpTarget | null, opts: { preserveFocus: boolean; preview?: boolean; beforeSelect?: () => void }): Promise<void> {
  if (!target) return;
  const abs = path.isAbsolute(target.path) ? target.path : path.join(repoRoot, target.path);
  try {
    opts.beforeSelect?.();
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(abs));
    const p = jumpPosition(docLike(doc), target);
    const position = new vscode.Position(p.line, p.character);
    opts.beforeSelect?.(); // 打开文档本身可能花了一会儿，从设光标这一刻再算宽限期
    await vscode.window.showTextDocument(doc, { preserveFocus: opts.preserveFocus, preview: opts.preview, selection: new vscode.Range(position, position) });
  } catch (e) {
    void vscode.window.showWarningMessage(`打不开 ${target.path}：${e instanceof Error ? e.message : e}`);
  }
}

function docLike(doc: vscode.TextDocument): DocLike {
  return {
    lineCount: doc.lineCount,
    positionAt: (offset) => doc.positionAt(offset),
    firstNonWhitespace: (line) => doc.lineAt(line).firstNonWhitespaceCharacterIndex,
  };
}

function posOf(editor: vscode.TextEditor): CursorPos | undefined {
  if (editor.document.uri.scheme !== "file") return undefined;
  const a = editor.selection.active;
  return { filePath: editor.document.uri.fsPath, line: a.line + 1, col: a.character + 1 };
}

export function currentPos(): CursorPos | undefined {
  const e = vscode.window.activeTextEditor;
  return e ? posOf(e) : undefined;
}
