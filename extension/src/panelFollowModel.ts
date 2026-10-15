// 面板调用图"跟随光标"的纯逻辑：共享来源（outlineFollow.ts 的 CursorSource）分来的"光标所在方法"
// → 从最后一次光标移动算起 PanelFollowDebounceMs 没再动、且不是刚发过的同一方法 → 发给面板。
// 不 import vscode：光标移动由 panelFollow.ts 调 touch() 告知，时钟用 setTimeout / Date.now（测试里用假时钟）。
import type { FollowResult } from "./outlineFollow";

/** 从最后一次光标移动算起，多久没再动才让面板换焦点（面板重新布局比侧边栏重，比侧边栏的 300ms 多等一会儿）。 */
export const PanelFollowDebounceMs = 400;

/** 面板自己跳转编辑器（双击节点打开定义）之后多久内不跟随：否则焦点会被换成刚打开的那个方法，图就丢了。 */
export const PanelNavigationGraceMs = 1500;

export interface PanelFollowOptions {
  /** 把面板焦点设成这个方法。 */
  send(repoRoot: string, symbolId: number): void;
  /** 面板现在连着哪个仓库；别的仓库的方法不发。 */
  panelRepo(): string | null;
  debounceMs?: number;
  navigationGraceMs?: number;
  now?: () => number;
}

export class PanelFollow {
  private timer: ReturnType<typeof setTimeout> | undefined;
  private pending: { repoRoot: string; symbolId: number } | null = null;
  private lastSent: number | null = null;
  private lastActivity = Number.NEGATIVE_INFINITY;
  private ignoreUntil = Number.NEGATIVE_INFINITY;
  private readonly debounceMs: number;
  private readonly graceMs: number;
  private readonly now: () => number;

  constructor(private readonly opts: PanelFollowOptions) {
    this.debounceMs = opts.debounceMs ?? PanelFollowDebounceMs;
    this.graceMs = opts.navigationGraceMs ?? PanelNavigationGraceMs;
    this.now = opts.now ?? (() => Date.now());
  }

  /** 光标动了（不管换没换方法）：待发的往后推，保证从最后一次移动算起才发。 */
  touch(): void {
    this.lastActivity = this.now();
    if (this.pending) this.schedule();
  }

  /** 共享来源的结果（订阅回调）。不是方法（光标在类体 / 没索引 / 出错）时作废待发的，面板保持现状。 */
  accept(r: FollowResult): void {
    this.cancel();
    if (r.state !== "method") return;
    if (this.now() < this.ignoreUntil) return;
    this.pending = { repoRoot: r.repoRoot, symbolId: r.symbol.id };
    this.schedule();
  }

  /** 面板要跳转编辑器了：接下来一小会儿由此引起的"换了方法"不发。 */
  beginNavigation(): void {
    this.ignoreUntil = this.now() + this.graceMs;
    this.cancel();
  }

  /** 关掉跟随 / 面板关了 / 重新勾选：作废待发的，忘掉上次发过谁（重新勾选时当前方法要再发一次）。 */
  reset(): void {
    this.cancel();
    this.lastSent = null;
  }

  dispose(): void {
    this.reset();
  }

  private schedule(): void {
    clearTimeout(this.timer);
    const wait = Math.max(0, this.lastActivity + this.debounceMs - this.now());
    this.timer = setTimeout(() => this.fire(), wait);
  }

  private fire(): void {
    this.timer = undefined;
    const p = this.pending;
    this.pending = null;
    if (!p || p.symbolId === this.lastSent) return;
    if (this.opts.panelRepo() !== p.repoRoot) return;
    this.lastSent = p.symbolId;
    this.opts.send(p.repoRoot, p.symbolId);
  }

  private cancel(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.pending = null;
  }
}
