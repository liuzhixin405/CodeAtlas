// 面板调用图"跟随光标"：vscode 薄层。订阅侧边栏共用的 CursorSource（光标 → 当前方法只解析一次），
// 勾选时才算"要结果"；防抖（从最后一次光标移动算起 400ms）与去重在 panelFollowModel.ts。
// 面板自己发起的跳转（双击节点打开定义）之后一小会儿不跟随，否则焦点会被换成刚打开的那个方法，图就丢了。
import * as vscode from "vscode";
import { currentPos, type SharedFollow } from "./cursorFollow";
import type { FollowSubscription } from "./outlineFollow";
import { PanelFollow } from "./panelFollowModel";

export interface PanelFollowHost {
  /** 面板现在连着哪个仓库；面板没开为 null。 */
  panelRepo(): string | null;
  /** 让面板以这个方法为焦点（宿主 → 面板 cursorSymbol 消息）。 */
  send(symbolId: number): void;
}

export class PanelCursorFollow implements vscode.Disposable {
  private readonly model: PanelFollow;
  private readonly sub: FollowSubscription;
  private enabled = false;
  private readonly disposables: vscode.Disposable[] = [];

  constructor(follow: SharedFollow, host: PanelFollowHost) {
    this.model = new PanelFollow({ panelRepo: () => host.panelRepo(), send: (_root, id) => host.send(id) });
    this.sub = follow.source.subscribe((r) => this.model.accept(r));
    // 防抖从最后一次光标移动算起：同一方法里继续移动（来源不会再分结果）也要把待发的往后推
    const touch = () => { if (this.enabled) this.model.touch(); };
    this.disposables.push(
      vscode.window.onDidChangeTextEditorSelection((e) => { if (e.textEditor === vscode.window.activeTextEditor) touch(); }),
      vscode.window.onDidChangeActiveTextEditor(touch),
    );
  }

  /** 页面勾选 / 取消"跟随光标"，或面板关了（false）。勾选时按当前光标立刻跟一次。 */
  setEnabled(enabled: boolean): void {
    this.model.reset();
    if (!enabled && !this.enabled) return;
    this.enabled = enabled;
    if (enabled) {
      this.sub.reset(); // 重新勾选：当前方法哪怕和上次一样也要再发一次（面板焦点可能已经被用户换过）
      this.sub.setActive(true, currentPos());
    } else {
      this.sub.setActive(false);
    }
  }

  /** 面板要跳转编辑器了：接下来由此引起的光标移动不让面板换焦点。 */
  beginNavigation(): void {
    this.model.beginNavigation();
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
    this.model.dispose();
    this.sub.dispose();
  }
}
