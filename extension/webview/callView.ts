import { nodeLabel } from "./neighborRank";
import { RelationsHistory } from "./relationsState";
import type { FlowDto, SymbolDto } from "./types";

/**
 * 调用图模式的两种画法：
 * - seq（默认）：从焦点方法出发往下的时序图，和入口流程同一个渲染器（SequenceDiagram）；
 * - graph：原来的网状图（cytoscape + GraphState），能往上追调用者。
 * 这里是纯逻辑（不碰 DOM、不 import vscode）：记住用户的选择、把各种焦点来源分到两种画法、时序模式的焦点历史（面包屑）。
 */
export type CallViewMode = "seq" | "graph";

export const CallViewModeKey = "codeatlas.callViewMode";
export const DefaultCallViewMode: CallViewMode = "seq";

export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/** 读用户上次选的画法。webview 的 localStorage 可能不可用（访问就抛错），读不到就用默认。 */
export function loadCallViewMode(storage: () => StorageLike | null): CallViewMode {
  try {
    const v = storage()?.getItem(CallViewModeKey);
    return v === "seq" || v === "graph" ? v : DefaultCallViewMode;
  } catch {
    return DefaultCallViewMode;
  }
}

export function saveCallViewMode(storage: () => StorageLike | null, mode: CallViewMode): void {
  try { storage()?.setItem(CallViewModeKey, mode); } catch { /* 不记也能用 */ }
}

/** 时序区当前该画什么。 */
export type SeqViewState =
  | { status: "empty" }
  | { status: "loading"; symbol: SymbolDto }
  | { status: "ready"; symbol: SymbolDto; dto: FlowDto }
  /** 服务端给不出（旧服务没有 /api/symbols/{id}/flow，或符号已不在索引里）。 */
  | { status: "missing"; symbol: SymbolDto }
  | { status: "error"; symbol: SymbolDto; message: string };

export interface CallViewDeps {
  storage(): StorageLike | null;
  /** 取时序数据（FlowDto）；拿不到返回 null。 */
  loadFlow(symbol: SymbolDto): Promise<FlowDto | null>;
  /** 画时序区。 */
  showSeq(view: SeqViewState): void;
  /** 网状：以 symbol 为焦点重画（GraphState.setFocus + 自动向下）。 */
  focusGraph(symbol: SymbolDto): Promise<void>;
  /** 网状：从焦点沿调用者「↑ 到源头」。 */
  chainUpGraph(symbol: SymbolDto): Promise<void>;
  /** 网状当前的焦点符号；切画法时用来同步。 */
  graphFocus(): SymbolDto | null;
  /** 画法变了：切换显示的区域与工具栏。 */
  onModeChange(mode: CallViewMode): void;
  /** 时序模式的面包屑变了。 */
  onHistoryChange?(): void;
  /** 时序模式去了一个新焦点（推入了焦点历史）：面板的后退 / 前进时间线记一条。 */
  onVisit?(symbol: SymbolDto): void;
}

export interface FocusOptions {
  /**
   * 宿主 focus 消息带的 chain：「in」= 要看谁调了它（影响范围视图的「在面板中打开」）。
   * 时序图只表达往下的调用，所以切到网状并「↑ 到源头」；这次切换不算用户的选择，不记住。
   * 「out」与普通聚焦一样：时序图本来就一路往下，网状图默认自动向下。
   */
  chain?: "in" | "out";
}

/** 时序数据缓存的条数上限（面包屑来回走不必每次重取）。 */
const MaxCached = 40;

export class CallView {
  /** 时序模式的焦点历史；语义与模块关系的中心历史一样（新焦点丢弃前进历史，同一个符号不重复推）。 */
  readonly history = new RelationsHistory();
  private current: CallViewMode;
  private seqNo = 0;
  private navigationSeq = 0;
  private cache = new Map<number, FlowDto>();
  private view: SeqViewState = { status: "empty" };

  constructor(private readonly deps: CallViewDeps) {
    this.current = loadCallViewMode(deps.storage);
  }

  get mode(): CallViewMode {
    return this.current;
  }

  /** 时序区最近一次画的状态。 */
  get seqView(): SeqViewState {
    return this.view;
  }

  /** 当前画法下的焦点符号 id（跟随光标判重用）。 */
  focusId(): number | null {
    return this.current === "seq" ? this.history.current?.id ?? null : this.deps.graphFocus()?.id ?? null;
  }

  /**
   * 切画法。remember=true（用户点「时序 | 网状」）时记住选择；程序切换（chain=in）不记。
   * 两边焦点不一致时，以切换前那边的焦点为准把另一边画出来。
   */
  async setMode(mode: CallViewMode, remember = true): Promise<void> {
    if (remember) { this.navigationSeq++; saveCallViewMode(this.deps.storage, mode); }
    if (mode === this.current) return;
    this.seqNo++;
    const from = this.current === "seq" ? this.history.current : this.deps.graphFocus();
    this.current = mode;
    this.deps.onModeChange(mode);
    if (!from) return;
    if (mode === "graph") {
      if (this.deps.graphFocus()?.id !== from.id) await this.deps.focusGraph(from);
    } else if (this.history.current?.id !== from.id || this.view.status !== "ready") {
      await this.showSeqFor(from, "push");
    }
  }

  /** 新焦点（搜索、编辑器右键、侧边栏、跟随光标、模块关系、入口流程 / 地图里的链接、时序图里双击）。 */
  async focus(symbol: SymbolDto, opts: FocusOptions = {}): Promise<void> {
    const seq = ++this.navigationSeq;
    if (opts.chain === "in") {
      await this.setMode("graph", false);
      if (seq !== this.navigationSeq) return;
      // setMode 已经按时序焦点重画过网状图时不再重复取一遍
      if (this.deps.graphFocus()?.id !== symbol.id) await this.deps.focusGraph(symbol);
      if (seq !== this.navigationSeq) return;
      await this.deps.chainUpGraph(symbol);
      return;
    }
    if (this.current === "graph") { await this.deps.focusGraph(symbol); return; }
    await this.showSeqFor(symbol, "push");
  }

  /** 面包屑：回到第 index 个焦点。 */
  async goTo(index: number): Promise<void> {
    this.navigationSeq++;
    if (index === this.history.currentIndex) return;
    const s = this.history.goTo(index);
    if (s) await this.showSeqFor(s, "none");
  }

  async back(): Promise<void> {
    this.navigationSeq++;
    const s = this.history.back();
    if (s) await this.showSeqFor(s, "none");
  }

  /** 索引刷新后：缓存作废；时序模式下按新索引找回当前焦点重画（id 可能变了）。找不到返回 false。 */
  async refresh(resolve: (old: SymbolDto) => Promise<SymbolDto | null>): Promise<boolean> {
    this.cache.clear();
    const seq = ++this.seqNo;
    const old = this.history.current;
    if (!old) return true;
    const s = await resolve(old);
    if (seq !== this.seqNo || old !== this.history.current) return true;
    if (!s) return false;
    if (this.current === "seq") await this.showSeqFor(s, "replace");
    else this.history.replaceCurrent(s, nodeLabel(s));
    return true;
  }

  /** 切仓库：清空历史与缓存，画法保留（是用户的偏好）。 */
  reset(): void {
    this.navigationSeq++;
    this.history.reset();
    this.cache.clear();
    this.seqNo++;
    this.view = { status: "empty" };
    this.deps.showSeq(this.view);
    this.deps.onHistoryChange?.();
  }

  private show(view: SeqViewState): void {
    this.view = view;
    this.deps.showSeq(view);
  }

  private async showSeqFor(symbol: SymbolDto, how: "push" | "replace" | "none"): Promise<void> {
    if (how === "push") { this.history.visit(symbol, nodeLabel(symbol)); this.deps.onVisit?.(symbol); }
    else if (how === "replace") this.history.replaceCurrent(symbol, nodeLabel(symbol));
    this.deps.onHistoryChange?.();
    const seq = ++this.seqNo;
    const cached = this.cache.get(symbol.id);
    if (cached) { this.show({ status: "ready", symbol, dto: cached }); return; }
    this.show({ status: "loading", symbol });
    let dto: FlowDto | null;
    try {
      dto = await this.deps.loadFlow(symbol);
    } catch (e) {
      if (seq === this.seqNo) this.show({ status: "error", symbol, message: String(e) });
      return;
    }
    // 等待期间用户又换了焦点：只认最后一次
    if (seq !== this.seqNo) return;
    if (!dto) { this.show({ status: "missing", symbol }); return; }
    this.cache.set(symbol.id, dto);
    if (this.cache.size > MaxCached) this.cache.delete(this.cache.keys().next().value!);
    this.show({ status: "ready", symbol, dto });
  }
}
