// 侧边栏视图（调用大纲、影响范围）共用的"跟随光标"逻辑：光标 → 当前方法只解析一次，几个视图订阅同一个来源。
// 来源（CursorSource）管防抖、同一行不重复请求、非支持文件忽略、自己跳转引起的光标移动不跟随；
// 每个视图一个订阅（FollowSubscription），各管各的固定 / 可见 / "同一方法不重复刷新"。
// 不 import vscode：光标事件由 cursorFollow.ts 转成 CursorPos 喂进来，时钟用 setTimeout / Date.now（测试里用假时钟）。
import type { ImpactDto, OutlineDto, SymbolDto } from "../webview/types";
import { isOutlineSymbol } from "./outlineModel";

/** 光标位置；line / col 1 起（与 /api/symbols/at 一致）。 */
export interface CursorPos {
  filePath: string;
  line: number;
  col: number;
}

/** 视图状态，同时是 context key codeatlas.outline.state / codeatlas.impact.state 的值（viewsWelcome 按它显示提示）。 */
export type OutlineState = "method" | "noMethod" | "noIndex" | "noServer" | "error";

export type ResolveResult =
  | { state: "method"; symbol: SymbolDto; repoRoot: string }
  | { state: "noMethod" | "noIndex" | "noServer" }
  | { state: "error"; message: string }
  /** 不归 CodeTrail Graph 管（不在工作区 / 外部根里）：保持现状。 */
  | { state: "ignore" };

export type FollowResult = Exclude<ResolveResult, { state: "ignore" }>;

/** 跟随光标用到的服务接口（ServerClient 的子集；测试里用假的）。 */
export interface FollowClient {
  health(): Promise<{ files: number }>;
  symbolAt(absolutePath: string, line: number, col: number): Promise<SymbolDto | null>;
}

export interface OutlineClient extends FollowClient {
  outline(id: number, opts: { externals: boolean; tests: boolean }): Promise<OutlineDto | null>;
}

export interface ImpactClient extends FollowClient {
  impact(id: number, opts: { entries: boolean; tests: boolean }): Promise<ImpactDto | null>;
}

export interface ResolveDeps {
  /** 文件归哪个服务（主根）；不归任何服务为 undefined。 */
  rootFor(filePath: string): string | undefined;
  /** 本会话已经为这个根拉起过服务。 */
  hasServer(root: string): boolean;
  /** 仓库里已经有索引库（.codeatlas/index.db）。 */
  indexExists(root: string): boolean;
  /** 确保服务在跑并返回客户端；起不来返回 null。 */
  connect(root: string): Promise<FollowClient | null>;
}

/**
 * 光标 → 视图的根方法（调用大纲 / 影响范围共用）。
 * 服务没起来时只在仓库已有索引库时拉起（拉起不会往仓库写新东西）；没有索引库就提示建立索引，不静默拉服务、不建库。
 * 至少一个视图可见且没固定时才会调到这里（用户打开了视图），所以"已有索引就拉起"不会在用户没看视图时发生。
 */
export async function resolveCursor(pos: CursorPos, deps: ResolveDeps, indexed: Set<string>): Promise<ResolveResult> {
  const root = deps.rootFor(pos.filePath);
  if (!root) return { state: "ignore" };
  if (!deps.hasServer(root) && !deps.indexExists(root)) return { state: "noIndex" };
  try {
    const client = await deps.connect(root);
    if (!client) return { state: "noServer" };
    if (!indexed.has(root)) {
      const h = await client.health();
      if (h.files === 0) return { state: "noIndex" };
      indexed.add(root);
    }
    const sym = await client.symbolAt(pos.filePath, pos.line, pos.col);
    if (!sym || !isOutlineSymbol(sym)) return { state: "noMethod" };
    return { state: "method", symbol: sym, repoRoot: root };
  } catch (e) {
    return { state: "error", message: e instanceof Error ? e.message : String(e) };
  }
}

export interface SourceOptions {
  resolve(pos: CursorPos): Promise<ResolveResult>;
  isSupported(filePath: string): boolean;
  debounceMs?: number;
  /** 跳转后多久内的非用户光标事件（打开文档、程序设光标）不跟随。 */
  navigationGraceMs?: number;
  now?: () => number;
}

export const FollowDebounceMs = 300;
export const NavigationGraceMs = 1000;

/**
 * 光标 → 当前方法的共享来源。几个视图订阅它，一次光标移动只解析一次（一次 symbolAt），
 * 结果分给"可见且没固定"的订阅；没有这样的订阅时什么都不做（不请求）。
 */
export class CursorSource {
  private timer: ReturnType<typeof setTimeout> | undefined;
  private seq = 0;
  /** 上次解析过的 文件:行 及结果：同一行不再请求（同一行几乎不会换方法），后变可见 / 取消固定的视图直接拿这份。 */
  private last: { key: string; result: FollowResult } | null = null;
  private navigatingUntil = 0;
  private readonly subs = new Set<FollowSubscription>();
  private readonly debounceMs: number;
  private readonly graceMs: number;
  private readonly now: () => number;

  constructor(private readonly opts: SourceOptions) {
    this.debounceMs = opts.debounceMs ?? FollowDebounceMs;
    this.graceMs = opts.navigationGraceMs ?? NavigationGraceMs;
    this.now = opts.now ?? (() => Date.now());
  }

  /** 订阅；onResult 只在这个订阅"换了方法 / 状态变了"时调。 */
  subscribe(onResult: (r: FollowResult) => void): FollowSubscription {
    const sub = new FollowSubscription(this, onResult);
    this.subs.add(sub);
    return sub;
  }

  /** 有没有可见且没固定的订阅。 */
  get wanted(): boolean {
    for (const s of this.subs) if (s.eligible) return true;
    return false;
  }

  isSupported(filePath: string): boolean {
    return this.opts.isSupported(filePath);
  }

  /**
   * 光标动了 / 换了编辑器。userInitiated：键盘或鼠标引起的（VS Code 的 TextEditorSelectionChangeKind.Keyboard / Mouse）。
   * 没有订阅要结果时什么都不做；某个视图刚跳转过时，非用户引起的事件忽略。
   */
  notify(pos: CursorPos | undefined, userInitiated: boolean): void {
    if (!this.wanted) return;
    if (this.navigatingUntil > 0) {
      if (userInitiated) this.navigatingUntil = 0;
      else if (this.now() < this.navigatingUntil) return;
      else this.navigatingUntil = 0;
    }
    if (!pos || !this.opts.isSupported(pos.filePath)) return;
    clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.timer = undefined; void this.run(pos); }, this.debounceMs);
  }

  /**
   * 某个视图要跳转编辑器了：接下来由此引起的光标移动不跟随（否则点一个深层步骤，根就被换成那一层的方法）。
   * 对所有订阅生效：在一个视图里点跳转，另一个视图也不跟着换根，等用户在编辑器里再点 / 打字。
   */
  beginNavigation(): void {
    this.navigatingUntil = this.now() + this.graceMs;
    clearTimeout(this.timer);
    this.timer = undefined;
  }

  /**
   * 立刻要光标所在方法（"转到调用方…"等命令用）：同一行有缓存就直接用，否则解析一次并记住（不分给订阅、不影响待处理的防抖）。
   * 不是支持的文件返回 ignore。
   */
  async resolveFor(pos: CursorPos): Promise<ResolveResult> {
    if (!this.opts.isSupported(pos.filePath)) return { state: "ignore" };
    const key = `${pos.filePath}:${pos.line}`;
    if (this.last && this.last.key === key) return this.last.result;
    const r = await this.opts.resolve(pos);
    if (r.state === "method" || r.state === "noMethod" || r.state === "noIndex") this.last = { key, result: r };
    return r;
  }

  /** 忘掉上次解析的位置和结果：下次事件一定重新请求。 */
  invalidate(): void {
    this.last = null;
  }

  /** 索引变了：忘掉所有订阅的"上次是谁"和缓存，按 pos 立刻重新解析一次（只请求一次，分给所有要结果的订阅）。 */
  async refresh(pos: CursorPos | undefined): Promise<void> {
    this.cancel();
    this.invalidate();
    for (const s of this.subs) s.forget();
    if (!this.wanted || !pos || !this.opts.isSupported(pos.filePath)) return;
    await this.run(pos);
  }

  dispose(): void {
    this.cancel();
    this.subs.clear();
  }

  /** @internal 某个订阅不再要结果了（固定 / 藏起）：没人要了就作废待处理的和在途的。 */
  subscriptionChanged(): void {
    if (!this.wanted) this.cancel();
  }

  /** @internal 某个订阅的"立刻解析"（刷新按钮）：不防抖、不用缓存。 */
  async resolveNow(pos: CursorPos): Promise<void> {
    this.cancel();
    this.invalidate();
    await this.run(pos);
  }

  /** @internal */
  unsubscribe(sub: FollowSubscription): void {
    this.subs.delete(sub);
    this.subscriptionChanged();
  }

  private cancel(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.seq++; // 在途的解析作废
  }

  private async run(pos: CursorPos): Promise<void> {
    const key = `${pos.filePath}:${pos.line}`;
    if (this.last && this.last.key === key) { this.deliver(this.last.result); return; }
    const my = ++this.seq;
    const r = await this.opts.resolve(pos);
    // 期间又有新事件 / 都固定了 / 都藏了：这次结果作废
    if (my !== this.seq || !this.wanted) return;
    if (r.state === "ignore") return;
    // 出错 / 服务没起来时不记住位置，下一次光标事件还会再试
    this.last = r.state === "method" || r.state === "noMethod" || r.state === "noIndex" ? { key, result: r } : null;
    this.deliver(r);
  }

  private deliver(r: FollowResult): void {
    for (const s of [...this.subs]) if (s.eligible) s.accept(r);
  }
}

/** 一个视图对 CursorSource 的订阅：自己的固定 / 可见，自己去重；固定一个视图不影响别的视图。 */
export class FollowSubscription {
  private rootId: number | null = null;
  private lastState: string | null = null;
  private _pinned = false;
  private _active = false;

  constructor(private readonly source: CursorSource, private readonly onResult: (r: FollowResult) => void) {}

  get pinned(): boolean { return this._pinned; }
  get active(): boolean { return this._active; }
  get currentRootId(): number | null { return this.rootId; }
  /** 可见且没固定：要跟随结果。 */
  get eligible(): boolean { return this._active && !this._pinned; }

  /** 固定 / 取消固定；取消时按当前光标重新跟一次（同一行的结果直接用来源的缓存，不再请求）。 */
  setPinned(pinned: boolean, current?: CursorPos): void {
    this._pinned = pinned;
    if (pinned) { this.source.subscriptionChanged(); return; }
    this.source.notify(current, true);
  }

  /** 视图可见性；变可见时按当前光标跟一次。 */
  setActive(active: boolean, current?: CursorPos): void {
    this._active = active;
    if (!active) { this.source.subscriptionChanged(); return; }
    this.source.notify(current, true);
  }

  /** 忘掉"上次是谁"，并让来源忘掉缓存：下次事件一定重新解析并回调。 */
  reset(): void {
    this.forget();
    this.source.invalidate();
  }

  /** 立刻按 pos 解析（刷新按钮）：不防抖，忽略固定以外的去重。别的订阅也会拿到这次结果（它们照常去重）。 */
  async resolveNow(pos: CursorPos | undefined): Promise<void> {
    if (this._pinned || !pos || !this.source.isSupported(pos.filePath)) return;
    this.forget();
    await this.source.resolveNow(pos);
  }

  dispose(): void {
    this.source.unsubscribe(this);
  }

  /** @internal 只忘掉自己的去重状态。 */
  forget(): void {
    this.rootId = null;
    this.lastState = null;
  }

  /** @internal 来源分来的结果：换了方法或状态变了才回调。 */
  accept(r: FollowResult): void {
    if (r.state === "method") {
      if (r.symbol.id === this.rootId) return;
      this.rootId = r.symbol.id;
      this.lastState = "method";
      this.onResult(r);
      return;
    }
    const stateKey = r.state === "error" ? `error:${r.message}` : r.state;
    if (this.rootId === null && this.lastState === stateKey) return;
    this.rootId = null;
    this.lastState = stateKey;
    this.onResult(r);
  }
}
