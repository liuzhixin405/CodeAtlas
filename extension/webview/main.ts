import { Api, neighborKinds } from "./api";
import { SubKindLabels } from "./archLayout";
import { ArchRenderer } from "./archRenderer";
import { CallView, type CallViewMode, type SeqViewState } from "./callView";
import { reorderDsm } from "./dsmOrder";
import { DsmView, hotRevisionsText, type DsmCellModel } from "./dsmView";
import { findSameEntry, hasDsmContent, isRepoContainer, MaxDefaultEntryTries, NoServiceHint, pickDefaultEntry, repoContainerNode, RepoDsmKey, shouldOpenRepoDsm, soleContainer } from "./firstScreen";
import { appendFlowHeadExtras, EntryKindLabels, FlowView, entryTitle } from "./flowView";
import { frameworkJumpTarget, frameworkSummary, FrameworkTabTitle, pickFrameworkContainer, stageOfActor } from "./frameworkModel";
import { FrameworkView } from "./frameworkView";
import { NarrationCard, parseEdgeEvidence, type EvidenceRef } from "./narrationCard";
import { DefaultPreset, LayoutPresets, presetLabel } from "./presets";
import { GraphState, type ExpandOptions, type GraphEdge, type GraphNode } from "./graphState";
import { MapRenderer } from "./mapRenderer";
import { MapState, saveMapViewChoice } from "./mapState";
import { NavHistory, navTitle } from "./navHistory";
import { host } from "./messaging";
import { isTestSymbol, moduleOf, nodeLabel, qualifiedLabel, rankNeighbor } from "./neighborRank";
import { collapseImplementations } from "./neighborRules";
import { resolveRefreshedSymbol } from "./refresh";
import { fileName, kindsSummary, RelationKindLabels, relationToCenter, reverseNote, unitKindLabel, unitLabel } from "./relationsLayout";
import { RelationsRenderer } from "./relationsRenderer";
import { RelationsHistory } from "./relationsState";
import { Renderer } from "./renderer";
import { isFocusableNode, SequenceDiagram } from "./seqDiagram";
import type { ArchDto, ArchEdgeDto, ArchNodeDto, DsmComponentDto, DsmDto, EntryDto, EvidenceDto, FlowDto, FlowNodeDto, FrameworkFlowDto, MapEdgeDto, MapGroupDto, NarrativeKind, NeighborDto, RelationComponentDto, RelationEdgeDto, RelationNodeDto, RelationsDto, SymbolDto } from "./types";

// ---------- DOM 骨架 ----------

const app = document.getElementById("app")!;
const RelationsHint = "在编辑器里右键一个方法或类，或在调用图里选一个节点";
app.innerHTML = `
  <header>
    <span id="nav" class="seg"><button id="nav-back" class="tab" disabled title="没有可后退的（Alt+←）">←</button><button id="nav-fwd" class="tab" disabled title="没有可前进的（Alt+→）">→</button></span>
    <div id="mode"><button id="mode-graph" class="tab active">调用图</button><button id="mode-rel" class="tab" title="以一个类为中心看类与类之间的依赖，按组件分框">模块关系</button><button id="mode-map" class="tab">项目地图</button></div>
    <span id="call-view" class="seg"><button class="tab" data-view="seq" title="时序：从焦点方法往下，按调用顺序从上往下、按项目分列（和入口流程同一种图）">时序</button><button class="tab" data-view="graph" title="网状：焦点的调用与被调用画成一张网，可以往上追调用者">网状</button></span>
    <input id="search" placeholder="搜索符号（前缀，多个词用空格）" autocomplete="off" />
    <div id="dropdown"></div>
    <nav id="crumbs"></nav>
    <span id="rel-opts" style="display:none"><span id="rel-depth" class="seg" title="1 = 只看与中心直接相连的类；2 = 再往外一层"><span class="seg-label">深度</span><button class="tab active" data-depth="1">1</button><button class="tab" data-depth="2">2</button></span><span id="rel-trunc" class="warn" style="display:none" title="服务端按与中心之间的边权重保留了前若干个">节点过多，只显示最相关的 40 个</span></span>
    <label class="opt" title="聚焦时自动沿调用/分发边向下展开到叶子"><input type="checkbox" id="auto-chain" checked /> 自动向下</label>
    <label class="opt" title="每个节点自动展开几个最像主流程的分支（调用与分发合计），其余折成「还有 N 个」"><select id="branch-width"><option value="1">主线</option><option value="2" selected>2 分支</option><option value="3">3 分支</option><option value="5">5 分支</option><option value="30">全部</option></select></label>
    <button id="chain-down" class="secondary" title="从焦点沿调用链向下展开到底">↓ 到底</button>
    <button id="chain-up" class="secondary" title="从焦点沿调用者向上追到源头">↑ 到源头</button>
    <label class="opt" title="把继承 / 实现（类型之间的关系）也画进调用图。默认不画：它们不是「它调谁」；看类与类之间的关系请用「模块关系」视图"><input type="checkbox" id="show-types" /> 类型关系</label>
    <label class="opt" title="显示参数/字段等类型引用"><input type="checkbox" id="show-refs" /> 类型引用</label>
    <label class="opt" title="把测试项目里的调用者/被调用者也画出来"><input type="checkbox" id="show-tests" /> 测试代码</label>
    <label class="opt" title="显示分发管道本身：对 QueryAsync/SendAsync 的调用和消息对象的构造"><input type="checkbox" id="show-conduit" /> 分发细节</label>
    <label class="opt" title="编辑器光标移进哪个方法，调用图就以它为焦点（停下约 0.4 秒后换）"><input type="checkbox" id="follow-cursor" /> 跟随光标</label>
    <button id="reindex" class="secondary" title="增量索引">⟳ 索引</button>
  </header>
  <main id="cy"><div id="hint">在编辑器里右键「CodeTrail Graph → 在调用图中查看」，或在上面搜索一个符号。</div></main>
  <main id="seq" style="display:none"><div id="seq-diagram" class="flow-diagram"></div></main>
  <main id="rel" style="display:none"><div id="rel-cy"></div><div id="rel-hint">${RelationsHint}</div></main>
  <main id="map" style="display:none">
    <nav id="map-crumbs"><span id="map-crumb-list"></span><span id="map-view"><button id="view-arch" class="tab" title="容器图：可部署单元、外部系统与它们之间的边">容器</button><button id="view-framework" class="tab" title="${FrameworkTabTitle}">框架流程</button><button id="view-ns" class="tab" title="按命名空间 / 目录分组">命名空间</button></span><label class="opt" id="map-layout-wrap" title="版式预设：AI 的排版建议也只在这些里选" style="display:none"><span>版式</span><select id="map-layout"></select></label></nav>
    <div id="arch-cy" style="display:none"></div><div id="dsm-view" style="display:none"></div><div id="flow-view" style="display:none"></div><div id="fw-view" style="display:none"></div><div id="map-cy"></div>
    <aside id="narration"></aside>
  </main>
  <footer id="details"><span class="muted">未选中</span></footer>
  <div id="overlay"><div class="card">
    <h2 id="ov-title">正在连接服务…</h2>
    <p id="ov-text"></p>
    <div id="progress"><div></div></div>
    <button id="ov-index" style="display:none">建立索引</button>
  </div></div>
  <div id="chooser"><div class="dialog">
    <header id="ch-title">选择要加入图的目标</header>
    <div class="list" id="ch-list"></div>
    <div class="actions">
      <button class="secondary" id="ch-all">全选</button>
      <button class="secondary" id="ch-cancel">取消</button>
      <button id="ch-ok">加入</button>
    </div>
  </div></div>
  <div id="ctxmenu"></div>
  <div id="toast"></div>
`;

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const searchEl = $<HTMLInputElement>("search");
const dropdown = $("dropdown");
const crumbsEl = $("crumbs");
const detailsEl = $("details");
const overlay = $("overlay");
const chooser = $("chooser");
const ctxmenu = $("ctxmenu");
const toastEl = $("toast");
const hintEl = $("hint");

// ---------- 状态 ----------

const state = new GraphState();
const mapState = new MapState();
let api: Api | null = null;
let graphSeq = 0;
let symbolRequestSeq = 0;
let dsmSeq = 0;
let renderer: Renderer | null = null;
let mapRenderer: MapRenderer | null = null;
let archRenderer: ArchRenderer | null = null;
let archDto: ArchDto | null = null;
let dsmView: DsmView | null = null;
/** 当前 DSM 的容器与钻取路径（root 键列表，"" 是项目层）。 */
let dsmContainer: ArchNodeDto | null = null;
let dsmPath: string[] = [];
let dsmDto: DsmDto | null = null;
/** 入口流程视图（L3）：与 DSM 共用 dsmContainer；入口清单、选中的入口和它的流程。 */
let flowView: FlowView | null = null;
let flowContainerKey: string | null = null;
let flowEntries: EntryDto[] = [];
let flowEntry: EntryDto | null = null;
let flowDto: FlowDto | null = null;
/** 框架流程视图：看的容器、取到的数据、请求批次号；各容器 HTTP 入口数（挑默认容器用，按需取一次）。 */
let fwView: FrameworkView | null = null;
let fwContainer: ArchNodeDto | null = null;
let fwDto: FrameworkFlowDto | null = null;
let fwSeq = 0;
let httpCounts: Map<string, number> | null = null;
/** 实际画出来的 DSM（按预设重排过的）；证据定位按它的下标找行列。 */
let dsmShown: DsmDto | null = null;
/** 右侧叙述卡（M5d）与三类对象当前用的版式预设。 */
let card: NarrationCard | null = null;
const presets: Record<NarrativeKind, string> = { ...DefaultPreset };
type Mode = "graph" | "relations" | "map";
let mode: Mode = "graph";
/** 模块关系（以一个类为中心的类间依赖）：当前数据、中心历史、深度。 */
let relRenderer: RelationsRenderer | null = null;
let relDto: RelationsDto | null = null;
const relHistory = new RelationsHistory();
let relDepth: 1 | 2 = 1;
/** relDto 是按哪组参数取的；切回模块关系时参数变了（测试代码开关、索引刷新）就重取。 */
let relParams = "";
/** 上次从调用图带进模块关系的符号 id：调用图里的焦点 / 选中没变时，切回来不覆盖用户在模块关系里走到的中心。 */
let relSyncedFromGraph: number | null = null;
/** 请求批次号：用户连点时只认最后一次。 */
let relSeq = 0;
/** init 之前宿主发来的 showRelations。 */
let pendingRelations: { symbolId: number; depth?: 1 | 2 } | null = null;
type MapViewKind = "arch" | "dsm" | "flow" | "framework" | "ns";
/** 地图模式下看哪个视图；null = 还没问过服务。有容器就从容器图开始（用户上次在顶层点的是框架流程 / 命名空间就进那个），否则命名空间。 */
let mapView: MapViewKind | null = null;
/**
 * 第一屏「只有一个容器就直接进它的入口流程」还没判过。每个仓库只自动跳一次：
 * 判过之后用户点面包屑「项目」回容器图，不会再被跳回去；切仓库时重置。
 * 容器图为空（还没索引）时不算判过，索引完成后再判。
 */
let autoFlowPending = true;
let selectedNode: GraphNode | null = null;

/** 调用图的时序模式：渲染器与入口流程共用（SequenceDiagram）；单击行选中看详情，双击换焦点，点 ▸ 展开折叠。 */
const seqDiagram = new SequenceDiagram($("seq-diagram"), {
  header: seqHeader,
  toggleOnRowClick: false,
  onRowSelect: (node) => showSeqNodeDetails(node),
  onRowDoubleClick: (node) => focusSeqNode(node),
});
/** 时序图里选中的行，与查到的它的符号（定位、带进模块关系用）。 */
let seqSelected: FlowNodeDto | null = null;
let seqSelectedSymbol: SymbolDto | null = null;
/** 时序数据是退到入口流程接口拿到的（服务还没有 /api/symbols/{id}/flow）。 */
const seqFromEntryFlow = new Set<number>();

const callView = new CallView({
  storage: () => window.localStorage,
  loadFlow: loadSeqFlow,
  showSeq,
  focusGraph: (s) => focusSymbol(s),
  chainUpGraph: (s) => expandChain(GraphState.nodeIdFor(s), "in"),
  graphFocus: () => (state.focusNodeId ? state.getNode(state.focusNodeId)?.symbol ?? null : null),
  onModeChange: (m: CallViewMode) => { graphSeq++; symbolRequestSeq++; applyCallViewChrome(); showCallDetails(); if (m === "graph" && graphStale) void refreshFocus(); },
  onHistoryChange: () => renderCrumbs(),
  onVisit: (s) => recordCall(s, "seq"),
});

// ---------- 后退 / 前进（跨模式的一条时间线，见 navHistory.ts）----------

const nav = new NavHistory(() => renderNavButtons());

function renderNavButtons(): void {
  const back = document.getElementById("nav-back") as HTMLButtonElement | null;
  const fwd = document.getElementById("nav-fwd") as HTMLButtonElement | null;
  if (!back || !fwd) return;
  back.disabled = !nav.canBack || nav.isRestoring;
  fwd.disabled = !nav.canForward || nav.isRestoring;
  back.title = navTitle("back", nav.backTarget);
  fwd.title = navTitle("forward", nav.forwardTarget);
}

/** 调用图：焦点 + 画法（时序 / 网状）。 */
function recordCall(s: SymbolDto, view: CallViewMode): void {
  nav.record({ key: `call:${view}:${s.id}`, label: `调用图（${view === "seq" ? "时序" : "网状"}）· ${nodeLabel(s)}`, restore: () => restoreCall(s, view) });
}

async function restoreCall(s: SymbolDto, view: CallViewMode): Promise<void> {
  if (mode !== "graph") setMode("graph");
  if (callView.mode !== view) await callView.setMode(view, false);
  if (callView.focusId() !== s.id) await callView.focus(s);
}

/** 模块关系：中心单元。 */
function recordRel(center: SymbolDto): void {
  nav.record({ key: `rel:${center.id}`, label: `模块关系 · ${unitLabel(center)}`, restore: () => showRelationsFor(center.id) });
}

/** 项目地图：当前视图与它的上下文（容器、入口、框架流程的容器、命名空间路径）拍一张快照。 */
function recordMap(): void {
  if (mapView === null) return;
  const view = mapView;
  const container = dsmContainer;
  const entry = view === "flow" ? flowEntry : null;
  const fw = view === "framework" ? fwContainer : null;
  const roots = view === "ns" ? mapState.crumbs.slice(1).map((c) => c.root) : [];
  const key = `map:${view}:${view === "dsm" || view === "flow" ? container?.key ?? "" : ""}:${entry?.symbolId ?? ""}:${fw?.key ?? ""}:${roots.join(">")}`;
  const label = view === "arch" ? "项目地图 · 容器图"
    : view === "dsm" ? `组件矩阵 · ${container?.name ?? ""}`
    : view === "flow" ? (entry ? `入口流程 · ${entryTitle(entry)}` : `入口清单 · ${container?.name ?? ""}`)
    : view === "framework" ? `框架流程 · ${fw?.name ?? ""}`
    : `命名空间 · ${roots[roots.length - 1] ?? "项目"}`;
  nav.record({ key, label, restore: () => restoreMap(view, container, entry, fw, roots) });
}

async function restoreMap(view: MapViewKind, container: ArchNodeDto | null, entry: EntryDto | null, fw: ArchNodeDto | null, roots: string[]): Promise<void> {
  autoPickSeq++;
  if ((view === "dsm" || view === "flow") && container) dsmContainer = container;
  if (view === "dsm") dsmPath = [];
  if (view === "flow" && container) {
    if (flowContainerKey !== container.key) { flowContainerKey = container.key; flowEntries = []; }
    flowEntry = entry;
    flowDto = null;
  }
  if (view === "framework" && fw) fwContainer = fw;
  if (view === "ns") { mapState.reset(); for (const r of roots) mapState.drill(r); }
  if (view === "arch" && card && card.currentKind !== null && card.currentKind !== "container") card.clear();
  if ((view === "dsm" || view === "flow") && container) { if (isRepoContainer(container)) card?.clear(); else if (!entry) showContainerCard(container); }
  applyMapView(view);
  if (mode !== "map") setMode("map", true, false);
  await loadMapView();
  if (view === "flow" && entry) await selectFlowEntry(findSameEntry(flowEntries, entry) ?? entry);
}

function navBack(): void { if (!nav.isRestoring) void nav.back(); }
function navForward(): void { if (!nav.isRestoring) void nav.forward(); }

/** 链展开的安全阀：深度与每个节点的分支数。超过分支数就停在那个节点，让用户手动展开。 */
const ChainMaxDepth = 12;
const ChainMaxFanout = 6;

function branchWidth(): number {
  return Number($<HTMLSelectElement>("branch-width").value) || 2;
}

let focusModule = "";

/** 挑分支的打分见 neighborRank.rankNeighbor（纯函数，有单测）。 */
function rank(n: NeighborDto): number {
  return rankNeighbor(n, { focusModule });
}

function expandOptions(): ExpandOptions {
  return { limit: branchWidth(), rank };
}

/** 展开时记住每个节点是经哪条边进来的，用来选主干。 */
const arrivedBy = new Map<string, NeighborDto>();
function remember(neighbors: NeighborDto[]): void {
  for (const n of neighbors) if (n.other) arrivedBy.set(GraphState.nodeIdFor(n.other), n);
}

function currentKinds(): string {
  return neighborKinds({ references: $<HTMLInputElement>("show-refs").checked, typeRelations: $<HTMLInputElement>("show-types").checked });
}
function autoChainEnabled(): boolean {
  return $<HTMLInputElement>("auto-chain").checked;
}

/** 测试代码（neighborRank.isTestSymbol）：追源头时测试方法是最大的噪声，默认不画。 */
function filterNeighbors(neighbors: NeighborDto[]): NeighborDto[] {
  const showTests = $<HTMLInputElement>("show-tests").checked;
  const showConduit = $<HTMLInputElement>("show-conduit").checked;
  return collapseImplementations(neighbors.filter((n) => {
    // 管道细节：dispatcher.QueryAsync(...) 这类调用和 new 消息对象，默认用一条 dispatch 边代替
    if (!showConduit && n.via === "conduit") return false;
    if (!showTests && n.other && isTestSymbol(n.other)) return false;
    return true;
  }));
}

function toast(msg: string, ms = 2500): void {
  toastEl.textContent = msg;
  toastEl.classList.add("open");
  window.setTimeout(() => toastEl.classList.remove("open"), ms);
}

function render(): void {
  renderer?.render(state.toElements(), state.focusNodeId);
  renderCrumbs();
  hintEl.style.display = state.focusNodeId ? "none" : "block";
}

function renderCrumbs(): void {
  crumbsEl.innerHTML = "";
  if (mode === "relations") { renderRelationsCrumbs(); return; }
  if (mode === "graph" && callView.mode === "seq") { renderSeqCrumbs(); return; }
  state.breadcrumbs.forEach((b, i) => {
    if (i > 0) crumbsEl.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (i === state.breadcrumbIndex ? " current" : "");
    el.textContent = b.label;
    el.title = b.focusNodeId;
    el.onclick = () => { graphSeq++; symbolRequestSeq++; state.goTo(i); render(); };
    crumbsEl.appendChild(el);
  });
}

function edgeKindText(e: GraphEdge): string {
  if (e.kind === "dispatches") return e.label === "handles" ? "handles（Handler 处理该类型）" : `分发：${e.label} 交给它的 Handler 处理`;
  if (e.label === "conduit") return "calls（分发管道）";
  if (e.label === "impl") return e.viaInterface ? `calls（经接口 ${qualifiedLabel(e.viaInterface)} → 唯一实现，已合成一个节点）` : "calls（接口→实现）";
  return e.kind;
}

function confidenceText(c: number): string {
  if (c >= 1) return "确定（编译器 / 包含关系）";
  if (c >= 0.95) return "按 import 解析";
  if (c >= 0.9) return "按类型解析";
  if (c >= 0.7) return "名字唯一";
  if (c >= 0.5) return "外部库或弱匹配";
  return "多候选之一";
}

function showNodeDetails(n: GraphNode | null): void {
  selectedNode = n;
  if (!n) { detailsEl.innerHTML = '<span class="muted">未选中</span>'; return; }
  if (n.type === "symbol" && n.symbol) {
    const s = n.symbol;
    detailsEl.innerHTML = `<span class="kind">${s.kind}</span><span class="mono">${esc(s.qualifiedName)}</span>` +
      `<span class="muted">${esc(s.path)}:${s.startLine}</span>` +
      (s.kind !== "external" ? `<a id="open-link">在编辑器中打开</a>` : "") +
      (s.signature ? `<span class="muted mono" style="margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(s.signature)}</span>` : "");
    detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => openInEditor(s));
    if (n.expandedIn && state.chainTargets(n.id, "in").length === 0) detailsEl.insertAdjacentHTML("beforeend", '<span class="kind">入口?</span><span class="muted">没有找到调用者</span>');
  } else if (n.type === "ambiguous" && n.callers) {
    detailsEl.innerHTML = `<span class="kind">调用者</span><span>${esc(n.label)}：调用者默认不画（调用图先看它调谁），点一下胶囊加入图</span><span class="muted">谁调了它、一路影响到哪些入口：用「影响范围」视图</span>`;
  } else if (n.type === "ambiguous") {
    detailsEl.innerHTML = `<span class="kind">${n.externalGroup ? "框架调用" : n.overflow ? "已折叠" : "多候选"}</span><span>${n.label}，双击节点选择要加入的</span>`;
  } else {
    detailsEl.innerHTML = `<span class="kind">悬空</span><span class="mono">${esc(n.id.replace(/^dangling:/, ""))}</span><span class="muted">目标已不在索引中</span>`;
  }
}

function showEdgeDetails(e: GraphEdge | null): void {
  if (!e) return;
  const src = state.getNode(e.source)?.label ?? e.source;
  const dst = state.getNode(e.target)?.label ?? e.target;
  detailsEl.innerHTML = `<span class="kind">${esc(edgeKindText(e))}</span><span class="mono">${esc(src)} → ${esc(dst)}</span>` +
    `<span class="muted">confidence ${e.confidence.toFixed(2)}（${confidenceText(e.confidence)}）· 第 ${e.line} 行</span>`;
}

function esc(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
}

function openInEditor(s: SymbolDto): void {
  host.post({ type: "open", path: s.path, line: s.startLine, col: s.startCol });
  if (!host.isVsCode) toast(`（浏览器模式）打开 ${s.path}:${s.startLine}`);
}

// ---------- 核心动作 ----------

/**
 * 网状图：以 symbol 为焦点重画。取两侧邻居，但只画「它调谁」；调用者收进一个「被 N 处调用」胶囊，点开再加入。
 */
async function focusSymbol(symbol: SymbolDto, replace = false): Promise<void> {
  if (!api) return;
  const a = api;
  const seq = ++graphSeq;
  const neighbors = filterNeighbors(await a.neighbors(symbol.id, "both", currentKinds()));
  if (seq !== graphSeq || a !== api) return;
  focusModule = moduleOf(symbol.qualifiedName);
  arrivedBy.clear();
  remember(neighbors);
  const opts = { ...expandOptions(), callersCapsule: true };
  graphStale = false;
  if (replace) state.replaceFocus(symbol, neighbors, opts);
  else { state.setFocus(symbol, neighbors, opts); recordCall(symbol, "graph"); }
  render();
  showNodeDetails(state.getNode(GraphState.nodeIdFor(symbol)) ?? null);
  if (autoChainEnabled()) await expandChain(GraphState.nodeIdFor(symbol), "out");
}

/** 点开「被 N 处调用」胶囊。 */
function expandCallersCapsule(node: GraphNode): void {
  // 单击已经点开过时，双击带来的是旧胶囊：什么都不做
  if (!state.getNode(node.id)?.callers) return;
  state.expandCallers(node.id, expandOptions());
  render();
  showNodeDetails(node.anchor ? state.getNode(node.anchor.nodeId) ?? null : null);
}

/** 调用图里所有「设为焦点」的入口：按当前画法（时序 / 网状）重画；不在调用图模式就先切过来。 */
function focusCall(symbol: SymbolDto, chain?: "in" | "out"): Promise<void> {
  symbolRequestSeq++;
  if (mode !== "graph") setMode("graph");
  return callView.focus(symbol, { chain });
}

// ---------- 调用图 · 时序 ----------

/** 时序数据：新接口 /api/symbols/{id}/flow；旧服务没有（404）时退到入口流程接口——入口符号还能出图。 */
async function loadSeqFlow(symbol: SymbolDto): Promise<FlowDto | null> {
  if (!api) return null;
  const f = await api.symbolFlow(symbol.id);
  if (f) { seqFromEntryFlow.delete(symbol.id); return f; }
  const e = await api.flow(symbol.id);
  if (e) seqFromEntryFlow.add(symbol.id);
  return e;
}

function showSeq(view: SeqViewState): void {
  seqSelected = null;
  seqSelectedSymbol = null;
  hintEl.style.display = "none";
  switch (view.status) {
    case "empty":
      seqDiagram.clear("在编辑器里右键「CodeTrail Graph → 在调用图中查看」，或在上面搜索一个方法：从它出发往下，按调用顺序画时序图。");
      break;
    case "loading":
      seqDiagram.clear(`正在取 ${nodeLabel(view.symbol)} 的调用时序…`);
      break;
    case "missing": {
      seqDiagram.clear(`拿不到 ${nodeLabel(view.symbol)} 的调用时序：服务版本太旧（没有 /api/symbols/{id}/flow，重启服务或更新扩展；入口方法仍能出图），或符号已不在索引里。`);
      const b = document.createElement("button");
      b.className = "secondary flow-switch";
      b.textContent = "切到网状看";
      b.onclick = () => void callView.setMode("graph");
      $("seq-diagram").appendChild(b);
      break;
    }
    case "error":
      seqDiagram.clear(`取调用时序失败：${view.message}`);
      break;
    case "ready":
      seqDiagram.render(view.dto);
      break;
  }
  if (mode === "graph" && callView.mode === "seq") showSeqFocusDetails();
}

/** 时序图标题：焦点「类型.方法」、文件:行、节点数、是否截断；再加行为链与服务端提示。 */
function seqHeader(dto: FlowDto): HTMLElement {
  const v = callView.seqView;
  const s = v.status === "ready" ? v.symbol : null;
  const head = document.createElement("div");
  head.className = "flow-head";
  const label = s ? nodeLabel(s) : dto.entryLabel;
  const stats = [`${dto.nodes} 个节点`];
  if (dto.truncated) stats.push("已截断");
  const where = s ? `${s.path}:${s.startLine}` : "";
  head.innerHTML = `<div class="flow-title"><span class="kind">调用</span><span class="label" title="${esc(s?.qualifiedName ?? dto.entryLabel)}">${esc(label)}</span>` +
    (dto.route && dto.route !== label ? `<span class="muted">${esc(dto.route)}</span>` : "") +
    (where ? `<a class="flow-where" title="在编辑器中打开">${esc(where)}</a>` : "") +
    `<span class="muted">${esc(stats.join("，"))}</span></div>`;
  appendFlowHeadExtras(head, dto, (d) => void gotoFramework(d));
  head.insertAdjacentHTML("beforeend", (s && seqFromEntryFlow.has(s.id) ? `<div class="flow-note">服务还没有「任意方法的时序」接口，这里用的是入口流程的数据</div>` : "") +
    `<div class="muted flow-hint">单击行看详情，双击行以它为焦点重画，点 ▸ 展开 / 折叠；要看谁调了它，点「↑ 到源头」或用影响范围视图</div>`);
  if (s) head.querySelector<HTMLAnchorElement>(".flow-where")?.addEventListener("click", () => openInEditor(s));
  return head;
}

function renderSeqCrumbs(): void {
  callView.history.crumbs.forEach((c, i) => {
    if (i > 0) crumbsEl.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (i === callView.history.currentIndex ? " current" : "");
    el.textContent = c.label;
    el.title = c.symbol.qualifiedName;
    el.onclick = () => void callView.goTo(i);
    crumbsEl.appendChild(el);
  });
}

/** 详情条：时序焦点本身。 */
function showSeqFocusDetails(): void {
  const v = callView.seqView;
  if (v.status === "empty") { detailsEl.innerHTML = '<span class="muted">未选中</span>'; return; }
  const s = v.symbol;
  const stats = v.status === "ready" ? `<span class="muted">${v.dto.actors.length} 个参与者 · ${v.dto.nodes} 个节点${v.dto.truncated ? " · 已截断" : ""}</span>` : "";
  detailsEl.innerHTML = `<span class="kind">焦点</span><span class="mono" title="${esc(s.qualifiedName)}">${esc(s.qualifiedName)}</span><span class="muted">${esc(s.path)}:${s.startLine}</span>` +
    (s.kind !== "external" ? `<a id="open-link">在编辑器中打开</a>` : "") + stats;
  detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => openInEditor(s));
}

const FlowKindLabels: Record<string, string> = { entry: "焦点", call: "调用", dispatch: "分发", hop: "跨服务", "external-group": "框架调用", more: "已折叠", function: "函数", http: "接口调用", handlers: "Handler", controllers: "控制器" };

/**
 * 详情条：单击的那一行。限定名直接有；文件:行要按 symbolId 查（节点自己带了 path / line 就直接用）。
 * onFocus 是「设为焦点」做什么：调用图里换焦点；框架流程里传 focusInCallSeq（切到调用图时序模式），链接文字也跟着换。
 */
function showSeqNodeDetails(node: FlowNodeDto, onFocus: (node: FlowNodeDto) => void = focusSeqNode, kindLabel?: string): void {
  seqSelected = node;
  seqSelectedSymbol = null;
  const kind = kindLabel ?? FlowKindLabels[node.kind] ?? node.kind;
  const focusable = isFocusableNode(node);
  const loc = node.path ? `${node.path}${node.line ? ":" + node.line : ""}` : focusable ? "…" : "";
  detailsEl.innerHTML = `<span class="kind">${esc(kind)}</span><span class="mono" title="${esc(node.qualifiedName)}">${esc(node.qualifiedName || node.label)}</span>` +
    (loc ? `<span class="muted" id="seq-loc">${esc(loc)}</span>` : "") +
    (focusable ? `<a id="open-link">在编辑器中打开</a><a id="seq-focus">${onFocus === focusSeqNode ? "设为焦点" : "在调用图中查看"}</a>` : "") +
    (node.via ? `<span class="muted">via ${esc(node.via)}</span>` : "") +
    (node.note ? `<span class="muted">${esc(node.note)}</span>` : "") +
    (node.confidence < 1 ? `<span class="muted">confidence ${node.confidence.toFixed(2)}（${confidenceText(node.confidence)}）</span>` : "");
  detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => openSeqNode(node));
  detailsEl.querySelector<HTMLAnchorElement>("#seq-focus")?.addEventListener("click", () => onFocus(node));
  if (!focusable || node.path || node.symbolId === null || !api) return;
  const id = node.symbolId;
  void api.symbol(id).then((s) => {
    if (seqSelected !== node) return;
    seqSelectedSymbol = s;
    const el = document.getElementById("seq-loc");
    if (el) el.textContent = s ? `${s.path}:${s.startLine}` : "符号已不在索引里";
  });
}

/** 在编辑器中打开时序图里的一个节点：带 pos 时宿主优先用 pos；没有位置就按 symbolId 查符号。 */
async function openSeqNode(node: FlowNodeDto): Promise<void> {
  if (node.path) { openLocation(node.path, node.line ?? 1, 1, node.pos ?? null); return; }
  if (node.symbolId === null || !api) return;
  const s = seqSelected === node && seqSelectedSymbol ? seqSelectedSymbol : await api.symbol(node.symbolId);
  if (!s) { toast("符号在索引里找不到了"); return; }
  openLocation(s.path, s.startLine, s.startCol, node.pos ?? null);
}

/** 双击 / 「设为焦点」：以这一行为新焦点重画时序图（记进面包屑）。 */
function focusSeqNode(node: FlowNodeDto): void {
  if (!isFocusableNode(node) || node.symbolId === null) return;
  if (node.symbolId === callView.history.current?.id) { toast("已经是焦点"); return; }
  void focusSymbolById(node.symbolId);
}

/** 从焦点出发，每一步选分最高的孩子，直到叶子：这就是主干。 */
function highlightTrunk(): void {
  if (!state.focusNodeId) return;
  const path = [state.focusNodeId];
  const visited = new Set(path);
  for (;;) {
    const targets = state.chainTargets(path[path.length - 1]!, "out").filter((t) => !visited.has(t.id));
    if (targets.length === 0) break;
    targets.sort((a, b) => (arrivedBy.has(b.id) ? rank(arrivedBy.get(b.id)!) : 0) - (arrivedBy.has(a.id) ? rank(arrivedBy.get(a.id)!) : 0));
    const next = targets[0]!;
    visited.add(next.id);
    path.push(next.id);
  }
  state.markTrunk(path.length > 1 ? path : []);
}

/**
 * 沿一侧一路展开：向下走到叶子（外部库 / 没有下一跳），向上追到源头（没有调用者）。
 * 分支数超过 ChainMaxFanout 的节点停下不再自动继续，避免图爆炸。
 */
async function expandChain(startId: string, side: "out" | "in"): Promise<void> {
  if (!api) return;
  const a = api;
  const seq = graphSeq;
  const focus = state.focusNodeId;
  // 往上追：焦点的调用者还收在「被 N 处调用」胶囊里，先放出来
  if (side === "in") {
    const capsule = state.callersCapsule(startId);
    if (capsule) state.expandCallers(capsule.id, expandOptions());
    else {
      const start = state.getNode(startId);
      if (start?.symbol && !start.expandedIn) {
        const neighbors = filterNeighbors(await a.neighbors(start.symbol.id, "in", currentKinds()));
        if (seq !== graphSeq || a !== api || focus !== state.focusNodeId) return;
        remember(neighbors);
        state.expand(startId, neighbors, "in", expandOptions());
      }
    }
    render();
  }
  const visited = new Set<string>([startId]);
  let frontier = state.chainTargets(startId, side);
  const stoppedAt: string[] = [];
  for (let depth = 0; depth < ChainMaxDepth && frontier.length > 0; depth++) {
    const next: GraphNode[] = [];
    for (const node of frontier) {
      if (visited.has(node.id) || !node.symbol) continue;
      visited.add(node.id);
      const already = side === "out" ? node.expandedOut : node.expandedIn;
      if (!already) {
        const neighbors = filterNeighbors(await a.neighbors(node.symbol.id, side, currentKinds()));
        if (seq !== graphSeq || a !== api || focus !== state.focusNodeId) return;
        remember(neighbors);
        state.expand(node.id, neighbors, side, expandOptions());
      }
      const targets = state.chainTargets(node.id, side);
      if (targets.length > ChainMaxFanout) { stoppedAt.push(node.label); continue; }
      next.push(...targets.filter((t) => !visited.has(t.id)));
    }
    frontier = next;
    render();
  }
  if (side === "out") { highlightTrunk(); render(); }
  if (stoppedAt.length > 0) toast(`${stoppedAt.slice(0, 3).join("、")}${stoppedAt.length > 3 ? " 等" : ""} 分支超过 ${ChainMaxFanout} 个，已停止自动展开，双击可继续`, 5000);
}

/**
 * 展开一个节点。默认只展开「它调谁」（out）：调用图看的是往下走；「谁调它」用右键「展开调用者」或影响范围视图。
 * 已经展开过 out 时（再双击）就跳代码，见 Renderer 的双击回调。
 */
async function expandNode(node: GraphNode, side: "out" | "in" = "out"): Promise<void> {
  if (!api || node.type !== "symbol" || !node.symbol || !node.expandable) return;
  const a = api;
  const seq = graphSeq;
  const focus = state.focusNodeId;
  const neighbors = filterNeighbors(await a.neighbors(node.symbol.id, side, currentKinds()));
  if (seq !== graphSeq || a !== api || focus !== state.focusNodeId) return;
  remember(neighbors);
  // 超过每侧上限的邻居由 GraphState 自动折成"还有 N 个"胶囊，点开再选；没有邻居也算展开过，再双击就跳代码
  state.expand(node.id, neighbors, side, expandOptions());
  if (neighbors.length === 0) toast(side === "out" ? "它没有再调别的（仓库内）" : "没有找到调用者");
  render();
}

async function expandCapsule(node: GraphNode): Promise<void> {
  const members = state.getCapsuleMembers(node.id);
  const title = node.callers ? `${node.label}，选择要加入图的调用者` : node.overflow ? `${node.label}，选择要加入图的` : `${node.label}：这些同名符号都可能是真正的目标`;
  const chosen = await chooseNeighbors(title, members);
  if (!chosen || chosen.length === 0) return;
  state.expandAmbiguous(node.id, chosen.map((m) => m.edgeId));
  render();
  showNodeDetails(state.getNode(node.id) ?? null);
}

/** 清单弹层：按 kind 分组，带复选框。取消返回 null。 */
function chooseNeighbors(title: string, items: NeighborDto[]): Promise<NeighborDto[] | null> {
  return new Promise((resolve) => {
    $("ch-title").textContent = title;
    const list = $("ch-list");
    list.innerHTML = "";
    const byKind = new Map<string, NeighborDto[]>();
    for (const n of items) {
      const k = `${n.direction === "out" ? "→" : "←"} ${n.kind}`;
      (byKind.get(k) ?? byKind.set(k, []).get(k)!).push(n);
    }
    for (const [k, group] of byKind) {
      list.insertAdjacentHTML("beforeend", `<div class="group">${esc(k)} · ${group.length}</div>`);
      for (const n of group) {
        const label = document.createElement("label");
        const name = n.other?.qualifiedName ?? n.toName;
        label.innerHTML = `<input type="checkbox" data-edge="${n.edgeId}" /><span class="qname" title="${esc(name)}">${esc(name)}</span><span class="path">${n.confidence.toFixed(1)} · L${n.line}</span>`;
        list.appendChild(label);
      }
    }
    const boxes = () => [...list.querySelectorAll<HTMLInputElement>("input[type=checkbox]")];
    const done = (result: NeighborDto[] | null) => { chooser.classList.remove("open"); resolve(result); };
    $("ch-all").onclick = () => boxes().forEach((b) => (b.checked = true));
    $("ch-cancel").onclick = () => done(null);
    $("ch-ok").onclick = () => {
      const ids = new Set(boxes().filter((b) => b.checked).map((b) => Number(b.dataset["edge"])));
      done(items.filter((i) => ids.has(i.edgeId)));
    };
    chooser.classList.add("open");
  });
}

type MenuItem = { label: string; run?: () => void };

function showContextMenu(node: GraphNode, x: number, y: number): void {
  const items: MenuItem[] = [];
  if (node.type === "symbol" && node.symbol) {
    const s = node.symbol;
    items.push({ label: "设为焦点", run: () => void focusSymbol(s) });
    if (s.kind !== "external") items.push({ label: "在模块关系中查看", run: () => showRelationsFor(s.id) });
    if (node.expandable) {
      if (node.expandedOut || node.expandedIn) items.push({ label: "折叠邻居", run: () => { state.collapse(node.id); render(); } });
      if (!node.expandedOut) items.push({ label: "展开一层（它调谁）", run: () => void expandNode(node, "out") });
      const capsule = state.callersCapsule(node.id);
      if (capsule) items.push({ label: `加入调用者（${capsule.label}）`, run: () => expandCallersCapsule(capsule) });
      else if (!node.expandedIn) items.push({ label: "展开调用者（谁调它）", run: () => void expandNode(node, "in") });
    }
    if (node.id !== state.focusNodeId) items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
    if (s.kind !== "external") items.push({ label: "在编辑器中打开", run: () => openInEditor(s) });
  } else if (node.type === "ambiguous" && node.callers) {
    items.push({ label: "全部加入图", run: () => expandCallersCapsule(node) });
    items.push({ label: "选择要加入的调用者…", run: () => void expandCapsule(node) });
  } else if (node.type === "ambiguous") {
    items.push({ label: "选择要加入的目标…", run: () => void expandCapsule(node) });
    items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
  } else {
    items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
  }
  openMenu(items, x, y);
}

function openMenu(items: MenuItem[], x: number, y: number): void {
  ctxmenu.innerHTML = items.map((i, idx) => `<div class="mi${i.run ? "" : " disabled"}" data-i="${idx}">${esc(i.label)}</div>`).join("");
  ctxmenu.querySelectorAll<HTMLElement>(".mi").forEach((el) => {
    el.onclick = () => { ctxmenu.classList.remove("open"); items[Number(el.dataset["i"])]?.run?.(); };
  });
  ctxmenu.style.left = `${Math.min(x, window.innerWidth - 180)}px`;
  ctxmenu.style.top = `${Math.min(y, window.innerHeight - items.length * 28 - 10)}px`;
  ctxmenu.classList.add("open");
}
document.addEventListener("click", () => ctxmenu.classList.remove("open"));
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { ctxmenu.classList.remove("open"); chooser.classList.remove("open"); dropdown.classList.remove("open"); }
  if (e.key === "Enter" && mode === "graph" && document.activeElement !== searchEl) {
    if (callView.mode === "seq") { if (seqSelected) openSeqNode(seqSelected); }
    else if (selectedNode?.type === "symbol" && selectedNode.symbol) openInEditor(selectedNode.symbol);
  }
});

// ---------- 搜索 ----------

let searchTimer = 0;
let searchSeq = 0;
let activeIndex = -1;
let lastHits: SymbolDto[] = [];
searchEl.addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  const seq = ++searchSeq;
  const q = searchEl.value.trim();
  if (!q) { dropdown.classList.remove("open"); return; }
  searchTimer = window.setTimeout(async () => {
    if (!api) return;
    const a = api;
    const hits = await a.search(q, 20);
    if (seq !== searchSeq || a !== api || searchEl.value.trim() !== q) return;
    lastHits = hits;
    activeIndex = -1;
    dropdown.innerHTML = lastHits.length === 0
      ? '<div class="item"><span class="muted">没有匹配</span></div>'
      : lastHits.map((s, i) => `<div class="item" data-i="${i}"><span class="kind">${s.kind}</span><span class="qname" title="${esc(s.qualifiedName)}">${esc(s.qualifiedName)}</span><span class="path">${esc(s.path)}:${s.startLine}</span></div>`).join("");
    dropdown.querySelectorAll<HTMLElement>(".item[data-i]").forEach((el) => {
      el.onclick = () => pick(Number(el.dataset["i"]));
    });
    dropdown.classList.add("open");
  }, 300);
});
searchEl.addEventListener("keydown", (e) => {
  if (!dropdown.classList.contains("open")) return;
  if (e.key === "ArrowDown") { activeIndex = Math.min(activeIndex + 1, lastHits.length - 1); highlight(); e.preventDefault(); }
  if (e.key === "ArrowUp") { activeIndex = Math.max(activeIndex - 1, 0); highlight(); e.preventDefault(); }
  if (e.key === "Enter") { pick(activeIndex >= 0 ? activeIndex : 0); e.preventDefault(); }
});
function highlight(): void {
  dropdown.querySelectorAll<HTMLElement>(".item[data-i]").forEach((el, i) => el.classList.toggle("active", i === activeIndex));
}
function pick(i: number): void {
  searchSeq++;
  const s = lastHits[i];
  dropdown.classList.remove("open");
  if (!s) return;
  searchEl.value = "";
  // 在当前模式下打开：模块关系里以它为中心；调用图（以及项目地图，地图没有"符号焦点"）里聚焦它
  if (mode === "relations") { void openRelations(s.id, "push"); return; }
  void focusCall(s);
}

// ---------- 模块关系 ----------

function relTests(): boolean {
  return $<HTMLInputElement>("show-tests").checked;
}
function relParamKey(): string {
  return `${relDepth}|${relTests()}`;
}

/** 调用图里能带进模块关系的符号：选中的符号节点优先，其次焦点；外部 / 悬空 / 多候选不算。时序模式看选中的行与时序焦点。 */
function graphSymbolForRelations(): SymbolDto | null {
  if (callView.mode === "seq") {
    const s = seqSelectedSymbol ?? callView.history.current;
    return s && s.kind !== "external" ? s : null;
  }
  const pickable = (n: GraphNode | null | undefined): SymbolDto | null => (n && n.type === "symbol" && n.symbol && n.symbol.kind !== "external" ? n.symbol : null);
  return pickable(selectedNode) ?? pickable(state.focusNodeId ? state.getNode(state.focusNodeId) : null);
}

function ensureRelRenderer(): RelationsRenderer {
  relRenderer ??= new RelationsRenderer($("rel-cy"), {
    onUnitSelect: showRelUnitDetails,
    onUnitDoubleClick: (n) => {
      if (n.unit.id === relDto?.focus.id) { toast("已经是中心"); return; }
      void openRelations(n.unit, "push");
    },
    onUnitContext: showRelContextMenu,
    onComponentSelect: showRelComponentDetails,
    onEdgeSelect: showRelEdgeDetails,
    onClear: showRelationsSummary,
  });
  return relRenderer;
}

/** 进入模块关系模式。sync：从调用图带符号过来（见 setMode）；false 表示调用方马上会自己指定中心，这里只摆好界面。 */
function enterRelations(sync: boolean): void {
  ensureRelRenderer().resize();
  renderRelationsChrome();
  if (!sync) return;
  const s = graphSymbolForRelations();
  if (s && s.id !== relSyncedFromGraph) { relSyncedFromGraph = s.id; void openRelations(s, "push"); return; }
  const center = relHistory.current;
  // 离开期间切过测试代码开关 / 索引刷新过：按新参数重取
  if (center && (relDto === null || relParams !== relParamKey())) { void openRelations(center, "replace"); return; }
  showRelationsSummary();
}

/** 侧边栏 / 宿主消息 / 调用图右键：切到模块关系并以 symbolId 为中心。 */
function showRelationsFor(symbolId: number, depth?: 1 | 2): void {
  if (depth === 1 || depth === 2) relDepth = depth;
  // 调用图当前的选中 / 焦点算"已经带过来了"，之后切标签不会把这里指定的中心覆盖掉
  relSyncedFromGraph = graphSymbolForRelations()?.id ?? relSyncedFromGraph;
  setMode("relations", false);
  void openRelations(symbolId, "push");
}

/**
 * 取模块关系并画出来。target 可以是方法 / 属性 / 类型（服务端换成所属单元，中心是 dto.focus）。
 * push：推入中心历史（同一个单元不重复）；replace：替换当前这一格（面包屑回退、换深度、刷新）。
 * target 是旧符号且 404 时（增量索引后 id 变了）按位置 / 限定名找回再试一次。
 */
async function openRelations(target: number | SymbolDto, how: "push" | "replace"): Promise<void> {
  if (!api) return;
  const a = api;
  ensureRelRenderer();
  const seq = ++relSeq;
  const params = relParamKey();
  const depth = relDepth;
  const tests = relTests();
  let dto: RelationsDto | null;
  try {
    dto = await a.relations(typeof target === "number" ? target : target.id, depth, tests);
    if (dto === null && typeof target !== "number") {
      const s = await resolveRefreshedSymbol(a, target);
      if (s && s.id !== target.id) dto = await a.relations(s.id, depth, tests);
    }
  } catch (e) {
    if (seq === relSeq) toast(`取模块关系失败：${e}`, 5000);
    return;
  }
  if (seq !== relSeq) return;
  if (dto === null) {
    toast("拿不到模块关系：服务版本太旧（没有 /api/symbols/{id}/relations，重启服务或更新扩展），或符号已不在索引里", 6000);
    renderRelationsChrome();
    return;
  }
  relDto = dto;
  relParams = params;
  const label = unitLabel(dto.focus);
  if (how === "push") { relHistory.visit(dto.focus, label); recordRel(dto.focus); }
  else relHistory.replaceCurrent(dto.focus, label);
  relRenderer!.render(dto);
  renderRelationsChrome();
  showRelationsSummary();
}

/** 顶部：深度按钮、截断提示、面包屑；画布上的提示语。 */
function renderRelationsChrome(): void {
  $("rel-depth").querySelectorAll<HTMLButtonElement>("button[data-depth]").forEach((b) => b.classList.toggle("active", Number(b.dataset["depth"]) === relDepth));
  $("rel-trunc").style.display = relDto?.truncated ? "" : "none";
  $("rel-hint").style.display = relHistory.current ? "none" : "block";
  if (mode === "relations") renderCrumbs();
}

function renderRelationsCrumbs(): void {
  relHistory.crumbs.forEach((c, i) => {
    if (i > 0) crumbsEl.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (i === relHistory.currentIndex ? " current" : "");
    el.textContent = c.label;
    el.title = c.symbol.qualifiedName;
    el.onclick = () => {
      if (i === relHistory.currentIndex) return;
      const s = relHistory.goTo(i);
      renderCrumbs();
      if (s) void openRelations(s, "replace");
    };
    crumbsEl.appendChild(el);
  });
}

function relUnitName(id: number): string {
  const n = relDto?.nodes.find((x) => x.unit.id === id);
  return n ? unitLabel(n.unit) : String(id);
}

function relComponentLabel(key: string): string {
  return relDto?.components.find((c) => c.key === key)?.label ?? key;
}

function showRelationsSummary(): void {
  relSelectedEdge = null;
  if (mode !== "relations") return;
  const d = relDto;
  if (!d || !relHistory.current) { detailsEl.innerHTML = `<span class="muted">${esc(RelationsHint)}</span>`; return; }
  const units = d.nodes.length;
  const comps = new Set(d.nodes.map((n) => n.componentKey)).size;
  const reverse = d.edges.filter((e) => reverseNote(e, d) !== null).length;
  const body = units <= 1 ? "没有找到与其他类的依赖" : `${units} 个单元，${comps} 个组件，${d.edges.length} 条依赖${reverse > 0 ? `，其中 ${reverse} 条反向（红色描边）` : ""}`;
  detailsEl.innerHTML = `<span class="kind">${esc(unitKindLabel(d.focus))}</span><span class="mono" title="${esc(d.focus.qualifiedName)}">${esc(unitLabel(d.focus))}</span><span>${esc(body)}</span>` +
    `<span class="muted">组件按依赖序从左到右（被依赖的在左，外部最右）；单击边看示例，双击类换中心，右键更多</span>`;
}

function showRelUnitDetails(n: RelationNodeDto): void {
  if (!relDto) return;
  relSelectedEdge = null;
  const u = n.unit;
  const canOpen = !n.external && u.kind !== "external";
  detailsEl.innerHTML = `<span class="kind">${esc(unitKindLabel(u))}</span><span class="mono" title="${esc(u.path)}:${u.startLine}">${esc(u.qualifiedName)}</span>` +
    `<span class="muted">组件：${esc(relComponentLabel(n.componentKey))}${n.external ? " · 仓库外" : ""}</span>` +
    `<span>${esc(relationToCenter(relDto, u.id))}</span>` +
    (canOpen ? `<a id="rel-open">在编辑器中打开</a>` : "");
  detailsEl.querySelector<HTMLAnchorElement>("#rel-open")?.addEventListener("click", () => openInEditor(u));
}

function showRelComponentDetails(c: RelationComponentDto): void {
  relSelectedEdge = null;
  const count = relDto?.nodes.filter((n) => n.componentKey === c.key).length ?? 0;
  detailsEl.innerHTML = `<span class="kind">组件</span><span class="mono" title="${esc(c.key)}">${esc(c.label)}</span><span class="muted">依赖序 ${c.order}${c.containerKey ? ` · 容器 ${esc(c.containerKey)}` : ""} · 图里 ${count} 个单元</span>`;
}

let relSelectedEdge: RelationEdgeDto | null = null;

function showRelEdgeDetails(e: RelationEdgeDto): void {
  if (!relDto) return;
  relSelectedEdge = e;
  const rev = reverseNote(e, relDto);
  const samples = e.samples.map((x, i) =>
    `<span class="rel-sample"><a data-sample="${i}" title="${esc(x.file)}:${x.line}">${esc(x.fromLabel)} → ${esc(x.toLabel)}</a><span class="muted">${esc(RelationKindLabels[x.kind] ?? x.kind)} · ${esc(fileName(x.file))}:${x.line}${x.confidence < 0.7 ? ` · 置信 ${x.confidence.toFixed(1)}` : ""}</span></span>`).join("");
  detailsEl.innerHTML = `<span class="kind">依赖</span><span class="mono">${esc(relUnitName(e.from))} → ${esc(relUnitName(e.to))}</span><span>${esc(kindsSummary(e.kinds))}</span>` +
    (rev ? `<span class="warn">${esc(rev)}</span>` : "") +
    (samples ? `<span class="rel-samples"><span class="muted">示例：</span>${samples}</span>` : `<span class="muted">没有成员层面的示例</span>`);
}

/** 打开文件到行；pos（UTF-16 偏移）有值时宿主优先用它定位到调用名。 */
function openLocation(path: string, line: number, col: number, pos: number | null): void {
  host.post({ type: "open", path, line, col, pos });
  if (!host.isVsCode) toast(`（浏览器模式）打开 ${path}:${line}${pos !== null ? ` @${pos}` : ""}`);
}

detailsEl.addEventListener("click", (ev) => {
  const a = (ev.target as HTMLElement).closest("a[data-sample]") as HTMLElement | null;
  const x = a && relSelectedEdge ? relSelectedEdge.samples[Number(a.dataset["sample"])] : undefined;
  if (x) openLocation(x.file, x.line, 1, x.pos);
});

function showRelContextMenu(n: RelationNodeDto, x: number, y: number): void {
  const u = n.unit;
  const canOpen = !n.external && u.kind !== "external";
  const items: MenuItem[] = [
    { label: "在编辑器中打开", run: canOpen ? () => openInEditor(u) : undefined },
    { label: "在调用图中查看", run: canOpen ? () => void focusCall(u) : undefined },
    { label: "设为中心", run: u.id !== relDto?.focus.id ? () => void openRelations(u, "push") : undefined },
  ];
  openMenu(items, x, y);
}

$("rel-depth").querySelectorAll<HTMLButtonElement>("button[data-depth]").forEach((b) => {
  b.onclick = () => {
    const d = Number(b.dataset["depth"]) === 2 ? 2 : 1;
    if (d === relDepth) return;
    relDepth = d;
    renderRelationsChrome();
    const center = relHistory.current;
    if (center) void openRelations(center, "replace");
  };
});

/** 索引刷新后：中心符号的 id 可能变了，找回后在模块关系模式下重取；不在这个模式就等切回来再取。 */
async function refreshRelations(): Promise<void> {
  if (!api) return;
  const old = relHistory.current;
  if (!old) return;
  const s = await resolveRefreshedSymbol(api, old);
  if (!s) { if (mode === "relations") toast("模块关系的中心在新索引里找不到了"); return; }
  relHistory.replaceCurrent(s, unitLabel(s));
  relParams = "";
  if (mode === "relations") await openRelations(s, "replace");
}

// ---------- 项目地图 ----------

/**
 * 切模式。sync=true（用户点顶部的「模块关系」）时，调用图里有选中 / 焦点符号且和上次带过去的不同，就以它为中心打开；
 * 宿主的 showRelations、右键「在模块关系中查看」自己指定中心，传 false。
 */
/** load=false：只摆界面不取地图数据（后退恢复地图时自己接着取，免得先画一遍旧视图）。 */
function setMode(next: Mode, sync = true, load = true): void {
  if (next !== mode) { graphSeq++; symbolRequestSeq++; }
  mode = next;
  app.dataset["mode"] = next;
  $("rel").style.display = next === "relations" ? "" : "none";
  $("map").style.display = next === "map" ? "" : "none";
  $("mode-graph").classList.toggle("active", next === "graph");
  $("mode-rel").classList.toggle("active", next === "relations");
  $("mode-map").classList.toggle("active", next === "map");
  $("rel-opts").style.display = next === "relations" ? "" : "none";
  applyCallViewChrome();
  if (next === "map") { mapRenderer?.resize(); archRenderer?.resize(); if (load) void loadMapView(); recordMap(); }
  else if (next === "relations") { enterRelations(sync); if (relHistory.current) recordRel(relHistory.current); }
  else {
    showCallDetails();
    const s = callView.mode === "seq" ? callView.history.current : (state.focusNodeId ? state.getNode(state.focusNodeId)?.symbol ?? null : null);
    if (s) recordCall(s, callView.mode);
  }
}

/** 只对网状图有意义的工具栏控件：时序模式下隐藏（时序图由服务端按固定深度 / 分支数建好，这些开关改不了它）。 */
const GraphOnlyControls = ["auto-chain", "chain-down", "branch-width", "show-types", "show-refs", "show-conduit"];

/** 按「模式 + 时序 / 网状」摆好画布区与工具栏。 */
function applyCallViewChrome(): void {
  const graph = mode === "graph";
  const seq = graph && callView.mode === "seq";
  $("cy").style.display = graph && !seq ? "" : "none";
  $("seq").style.display = seq ? "" : "none";
  $("call-view").style.display = graph ? "" : "none";
  $("call-view").querySelectorAll<HTMLButtonElement>("button[data-view]").forEach((b) => b.classList.toggle("active", b.dataset["view"] === callView.mode));
  for (const id of GraphOnlyControls) {
    const el = $(id);
    (el.closest("label") ?? el).style.display = graph && !seq ? "" : "none";
  }
  // 「↑ 到源头」两种画法都留着：时序图只画往下的调用，在时序模式下点它 = 切到网状再往上追
  $("chain-up").style.display = graph ? "" : "none";
  $("chain-up").title = seq ? "切到网状图，从焦点沿调用者向上追到源头（时序图只画往下的调用）" : "从焦点沿调用者向上追到源头";
  const follow = document.getElementById("follow-cursor");
  if (follow) follow.closest("label")!.style.display = graph ? "" : "none";
  // 「测试代码」：网状图里过滤测试邻居，模块关系里是 tests=true/false；时序图不受它影响
  $("show-tests").closest("label")!.style.display = mode === "relations" || (graph && !seq) ? "" : "none";
  renderCrumbs();
}

/** 调用图模式下的详情条：网状显示选中节点，时序显示选中行或焦点。 */
function showCallDetails(): void {
  if (callView.mode === "graph") { renderer?.resize(); showNodeDetails(selectedNode); return; }
  if (seqSelected) showSeqNodeDetails(seqSelected);
  else showSeqFocusDetails();
}

/**
 * 第一次进地图模式先问服务：有容器就从容器图开始（只有一个容器时直接进它的入口流程）；
 * 一个容器都没有时看整库 DSM，再不行就命名空间视图。
 */
async function loadMapView(): Promise<void> {
  if (!api) return;
  if (mapView === null) {
    const a = await api.arch();
    archDto = a;
    const archAvailable = a !== null && a.nodes.length > 0;
    const hasContainers = a !== null && a.nodes.some((n) => n.kind === "container");
    $<HTMLButtonElement>("view-arch").disabled = !archAvailable;
    $("view-arch").title = a === null ? "服务版本太旧，没有 /api/arch/containers；重启服务或更新扩展" : archAvailable ? "容器图：可部署单元、外部系统与它们之间的边" : (a.note ?? "没有发现可部署单元");
    $<HTMLButtonElement>("view-framework").disabled = !hasContainers;
    $("view-framework").title = hasContainers ? FrameworkTabTitle : a === null ? "服务版本太旧，没有容器图；重启服务或更新扩展" : "没有发现可部署的容器，框架流程无从谈起";
    // 纯类库仓库（没有任何可部署容器）：直接看整库的模块依赖矩阵；DSM 没有组件（还没索引等）才退回命名空间视图
    if (autoFlowPending && shouldOpenRepoDsm(a) && hasDsmContent(await api.dsm(RepoDsmKey, ""))) {
      autoFlowPending = false;
      $<HTMLButtonElement>("view-arch").disabled = false;
      $("view-arch").title = NoServiceHint;
      toast(NoServiceHint);
      openDsm(repoContainerNode(repoDisplayName()));
      return;
    }
    // 不恢复"上次点的视图"：第一屏按用户定的规则走（只有一个容器直接进入口流程，否则容器图）
    if (archAvailable) {
      // 只有一个容器（单独打开的前端仓库等）：容器图没什么可看，直接进它的入口流程；没有入口时仍停在容器图
      const sole = autoFlowPending ? soleContainer(a) : null;
      autoFlowPending = false;
      if (sole && (await api.entries(sole.key)).length > 0) { openFlow(sole); return; }
      applyMapView("arch"); showArch(a!); return;
    }
    applyMapView("ns");
  }
  if (mapView === "arch") await loadArch();
  else if (mapView === "dsm") await loadDsm();
  else if (mapView === "flow") await loadFlowEntries();
  else if (mapView === "framework") await loadFramework();
  else await loadNamespaceMap();
}

function applyMapView(view: MapViewKind): void {
  mapView = view;
  // DSM / 入口流程都是容器图往下钻的一层，切换按钮上仍算"容器"
  $("view-arch").classList.toggle("active", view === "arch" || view === "dsm" || view === "flow");
  $("view-framework").classList.toggle("active", view === "framework");
  $("view-ns").classList.toggle("active", view === "ns");
  $("arch-cy").style.display = view === "arch" ? "" : "none";
  $("dsm-view").style.display = view === "dsm" ? "" : "none";
  $("flow-view").style.display = view === "flow" ? "" : "none";
  $("fw-view").style.display = view === "framework" ? "" : "none";
  $("map-cy").style.display = view === "ns" ? "" : "none";
  // 叙述卡只对容器 / 组件 / 入口流程有意义；框架流程（右侧有自己的清单）与命名空间视图不占右侧
  $("narration").style.display = layoutKindFor(view) ? "" : "none";
  renderLayoutSelect();
  if (mode === "map") recordMap();
}

// ---------- 版式预设（AI 只在这里面选）----------

function layoutKindFor(view: MapViewKind | null): NarrativeKind | null {
  return view === "arch" ? "container" : view === "dsm" ? "component" : view === "flow" ? "flow" : null;
}

/** 面包屑栏右侧的「版式」下拉：只在容器 / 组件矩阵 / 入口流程三个视图显示，选项随视图换。 */
function renderLayoutSelect(): void {
  const kind = layoutKindFor(mapView);
  const wrap = $("map-layout-wrap");
  wrap.style.display = kind ? "" : "none";
  if (!kind) return;
  const sel = $<HTMLSelectElement>("map-layout");
  sel.innerHTML = LayoutPresets[kind].map((p) => `<option value="${esc(p.id)}" title="${esc(p.title)}">${esc(p.label)}</option>`).join("");
  sel.value = presets[kind];
}
$("map-layout").onchange = () => {
  const kind = layoutKindFor(mapView);
  if (kind) applyPreset(kind, $<HTMLSelectElement>("map-layout").value);
};

/** 切预设：手动下拉和叙述卡的「按 AI 建议排版」都走这里。不认识的 id 忽略。 */
function applyPreset(kind: NarrativeKind, id: string): void {
  if (!LayoutPresets[kind].some((p) => p.id === id)) { toast(`不认识的版式 ${id}`); return; }
  const changed = presets[kind] !== id;
  presets[kind] = id;
  if (kind === "container") { if (archDto && archRenderer) archRenderer.render(archDto, id); }
  else if (kind === "component") { if (dsmDto && dsmView) { dsmShown = reorderDsm(dsmDto, id); dsmView.render(dsmShown); } }
  else flowView?.setPreset(id);
  renderLayoutSelect();
  card?.refresh();
  if (changed) toast(`版式：${presetLabel(kind, id)}`);
}

// ---------- 叙述卡（M5d）----------

function showContainerCard(c: ArchNodeDto): void {
  card?.show("container", c.key, c.name);
}

/** 点证据标签：node → 容器图选中；edge → 容器图选中边；entry / sym → 调用图聚焦；comp / cell → 组件矩阵里闪一下。 */
async function onEvidence(ref: EvidenceRef, raw: string): Promise<void> {
  const ensureArch = async () => {
    if (mapView !== "arch") { applyMapView("arch"); archRenderer?.resize(); await loadArch(); }
  };
  switch (ref.kind) {
    case "node": {
      await ensureArch();
      if (archRenderer?.selectNode(ref.ref)) {
        const n = archDto?.nodes.find((x) => x.key === ref.ref);
        if (n) showArchNodeDetails(n, false);
      } else toast(`容器图里没有 ${ref.ref}`);
      return;
    }
    case "edge": {
      const e = parseEdgeEvidence(ref.ref);
      if (!e) { toast(raw); return; }
      await ensureArch();
      if (archRenderer?.selectEdge(e.from, e.to, e.protocol)) {
        const edge = archDto?.edges.find((x) => x.from === e.from && x.to === e.to && (e.protocol === null || x.protocol === e.protocol));
        if (edge) showArchEdgeDetails(edge);
      } else toast(`容器图里没有这条边：${e.from} → ${e.to}`);
      return;
    }
    case "entry":
    case "sym": {
      const id = Number(ref.ref);
      if (Number.isInteger(id) && api) await focusSymbolById(id);
      else toast(raw);
      return;
    }
    case "comp": {
      if (mapView === "dsm" && highlightDsm(ref.ref, null)) return;
      toast(`组件 ${ref.ref}（在组件矩阵视图里可定位）`);
      return;
    }
    case "cell": {
      const arrow = ref.ref.indexOf("→");
      if (arrow > 0 && mapView === "dsm" && highlightDsm(ref.ref.slice(0, arrow), ref.ref.slice(arrow + 1))) return;
      toast(`依赖格 ${ref.ref}（在组件矩阵视图里可定位）`);
      return;
    }
    default:
      toast(raw);
  }
}

/**
 * 在当前 DSM 表格里让一个行头（colKey 为 null）或一个格子闪一下。
 * 依赖 dsmView 的表结构：tbody > tr（每行先一个 th 再 N 个 td），下标按 dsmShown 的组件顺序。
 */
function highlightDsm(rowKey: string, colKey: string | null): boolean {
  const comps = dsmShown?.components ?? [];
  const r = comps.findIndex((c) => c.key === rowKey);
  if (r < 0) return false;
  const tr = $("dsm-view").querySelectorAll<HTMLElement>("tbody tr")[r];
  if (!tr) return false;
  let target: HTMLElement | null;
  if (colKey === null) target = tr.querySelector("th");
  else {
    const c = comps.findIndex((x) => x.key === colKey);
    if (c < 0) return false;
    target = (tr.children[c + 1] as HTMLElement | undefined) ?? null;
  }
  if (!target) return false;
  target.scrollIntoView({ block: "nearest", inline: "nearest" });
  target.classList.remove("ca-flash");
  void target.offsetWidth; // 重新触发动画
  target.classList.add("ca-flash");
  const el = target;
  window.setTimeout(() => el.classList.remove("ca-flash"), 1700);
  return true;
}

function setMapView(view: MapViewKind): void {
  if (mapView === view) return;
  // 用户回到容器图时，组件 / 入口的叙述已不在画面上（证据定位走 applyMapView，不清卡片）
  if (view === "arch" && card && card.currentKind !== null && card.currentKind !== "container") card.clear();
  applyMapView(view);
  if (view === "arch") archRenderer?.resize(); else if (view === "ns") mapRenderer?.resize();
  void loadMapView();
}

// ---------- 组件 DSM（L2）----------

function openDsm(container: ArchNodeDto): void {
  dsmContainer = container;
  dsmPath = [];
  applyMapView("dsm");
  // 整库伪容器没有容器叙述
  if (isRepoContainer(container)) card?.clear(); else showContainerCard(container);
  void loadDsm();
}

/** 仓库目录名（整库 DSM 的标题）。 */
function repoDisplayName(): string {
  const root = (currentRepo ?? "").replace(/[\\/]+$/, "");
  return root.slice(Math.max(root.lastIndexOf("/"), root.lastIndexOf("\\")) + 1) || "仓库";
}

async function loadDsm(): Promise<void> {
  if (!api || !dsmContainer) { setMapView("arch"); return; }
  const a = api;
  const container = dsmContainer;
  const seq = ++dsmSeq;
  const root = dsmPath[dsmPath.length - 1] ?? "";
  const d = await a.dsm(container.key, root);
  if (seq !== dsmSeq || a !== api || container !== dsmContainer || root !== (dsmPath[dsmPath.length - 1] ?? "") || mapView !== "dsm") return;
  if (d === null) { toast("服务没有 /api/arch/dsm，或容器已不存在"); setMapView("arch"); return; }
  dsmDto = d;
  dsmView ??= new DsmView($("dsm-view"), {
    onCellSelect: showDsmCellDetails,
    onComponentSelect: showDsmComponentDetails,
    onComponentDoubleClick: (c) => {
      if (!c.hasChildren) { toast(`${c.label} 下面没有更深的层级`); return; }
      dsmPath.push(c.key);
      void loadDsm();
    },
  });
  // 版式预设只在页面这层套：服务端给的是依赖序，字母序由 reorderDsm 重排并同步 cells / coupling 下标
  dsmShown = reorderDsm(d, presets.component);
  dsmView.render(dsmShown);
  renderContainerCrumbs("dsm");
  const cyc = d.cycles.length > 0 ? `，${d.cycles.length} 个环（红框）` : "，没有环";
  const cpl = d.coupling.length > 0 ? `，${d.coupling.length} 对隐性耦合（虚框 ~）` : "";
  const git = d.gitAvailable ? "" : "；没有 git 统计";
  const hint = isRepoContainer(dsmContainer) ? `<span class="muted">${esc(NoServiceHint)}</span>` : "";
  detailsEl.innerHTML = `${hint}<span class="kind">DSM</span><span>${esc(dsmContainer.name)}${root ? " › " + esc(root) : ""}：${d.components.length} 个组件，${d.cells.length} 个依赖格${cyc}${cpl}${git}${d.note ? "；" + esc(d.note) : ""}</span><span class="muted">行依赖列；双击带下划线的行头钻入</span>`;
}

/**
 * 容器层的面包屑：项目 › 容器 [组件矩阵 | 入口流程 | 框架流程] › …。
 * DSM 后面跟钻取路径；入口流程后面只跟选中的入口——子标签已经写着「入口流程」，不再重复一段（用户反馈导航重复）。
 * 在入口流程里再点一次「入口流程」子标签 = 回到入口清单（原来那段面包屑的作用）。
 */
function renderContainerCrumbs(sub: "dsm" | "flow" | "framework"): void {
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = "";
  const add = (label: string, current: boolean, onClick: () => void) => {
    if (crumbs.childElementCount > 0) crumbs.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (current ? " current" : "");
    el.textContent = label;
    el.onclick = onClick;
    crumbs.appendChild(el);
  };
  const repo = isRepoContainer(dsmContainer);
  // 整库伪容器：没有容器图可回、也没有入口流程
  if (!repo) add("项目", false, () => setMapView("arch"));
  add(dsmContainer?.name ?? "容器", sub === "dsm" && dsmPath.length === 0, () => { if (dsmContainer) openDsm(dsmContainer); });
  const tabs = document.createElement("span");
  tabs.className = "subtabs";
  const tab = (label: string, title: string, active: boolean, onClick: () => void) => {
    const b = document.createElement("button");
    b.className = "tab" + (active ? " active" : "");
    b.textContent = label;
    b.title = title;
    b.onclick = onClick;
    tabs.appendChild(b);
  };
  tab("组件矩阵", "组件依赖矩阵（DSM）：行依赖列，带环与 git 耦合", sub === "dsm", () => { if (dsmContainer && mapView !== "dsm") openDsm(dsmContainer); });
  if (!repo) tab("入口流程", sub === "flow" && flowEntry ? "回到入口清单" : "从一个入口（路由 / 任务 / Handler）出发的调用时序", sub === "flow", () => {
    if (!dsmContainer) return;
    if (mapView !== "flow") openFlow(dsmContainer);
    else if (flowEntry) backToEntryList();
  });
  if (!repo) tab("框架流程", FrameworkTabTitle, sub === "framework", () => { if (dsmContainer && mapView !== "framework") openFramework(dsmContainer); });
  crumbs.appendChild(tabs);
  if (sub === "dsm") {
    dsmPath.forEach((key, i) => add(key.slice(Math.max(key.lastIndexOf("."), key.lastIndexOf("/")) + 1), i === dsmPath.length - 1, () => { dsmPath = dsmPath.slice(0, i + 1); void loadDsm(); }));
  } else if (sub === "flow") {
    if (flowEntry) add(entryTitle(flowEntry), true, () => {});
  }
}

/** 用户主动回到入口清单：不再自动挑入口（进行中的那次也作废）。 */
function backToEntryList(): void {
  autoPickSeq++;
  flowEntry = null;
  flowDto = null;
  recordMap();
  if (dsmContainer) showContainerCard(dsmContainer);
  void loadFlowEntries();
}

// ---------- 入口流程（L3）----------

/** 进入入口流程；还没选中入口时自动挑一个（见 firstScreen.pickDefaultEntry），右边不空着。 */
function openFlow(container: ArchNodeDto): void {
  if (flowContainerKey !== container.key) { flowContainerKey = container.key; flowEntries = []; flowEntry = null; flowDto = null; }
  dsmContainer = container;
  applyMapView("flow");
  if (flowEntry) card?.show("flow", String(flowEntry.symbolId), entryTitle(flowEntry));
  else showContainerCard(container);
  void loadFlowEntries(true);
}

/** 自动挑入口的批次号：用户中途自己点了入口、换了容器或离开视图，旧批次作废。 */
let autoPickSeq = 0;

async function autoSelectEntry(): Promise<void> {
  if (!api || flowEntries.length === 0 || flowEntry) return;
  const seq = ++autoPickSeq;
  const a = api;
  const container = flowContainerKey;
  const stale = () => seq !== autoPickSeq || flowEntry !== null || flowContainerKey !== container || mapView !== "flow";
  flowView?.clearFlow("正在挑一个入口…");
  const pick = await pickDefaultEntry(flowEntries, (e) => a.flow(e.symbolId, undefined, e), MaxDefaultEntryTries, stale);
  if (stale()) return;
  if (!pick) { flowView?.clearFlow(); return; }
  await selectFlowEntry(pick.entry, pick.flow);
}

async function loadFlowEntries(autoPick = false): Promise<void> {
  if (!api || !dsmContainer) { setMapView("arch"); return; }
  flowContainerKey = dsmContainer.key;
  flowEntries = await api.entries(dsmContainer.key);
  flowView ??= new FlowView($("flow-view"), {
    onEntrySelect: (e) => void selectFlowEntry(e),
    onNodeFocus: showFlowNodeDetails,
    onFrameworkJump: (dto) => void gotoFramework(dto),
  });
  flowView.renderEntries(flowEntries, flowEntry);
  if (flowEntry && flowDto) flowView.renderFlow(flowDto);
  else flowView.clearFlow();
  renderContainerCrumbs("flow");
  if (flowEntry && flowDto) { showFlowEntryDetails(flowEntry, flowDto); return; }
  if (autoPick && !flowEntry && flowEntries.length > 0) { await autoSelectEntry(); return; }
  const counts = new Map<string, number>();
  for (const e of flowEntries) counts.set(e.kind, (counts.get(e.kind) ?? 0) + 1);
  const parts = [...counts.entries()].map(([k, n]) => `${EntryKindLabels[k] ?? k} ${n}`).join("，");
  detailsEl.innerHTML = `<span class="kind">入口</span><span>${esc(dsmContainer.name)}：${flowEntries.length} 个入口${parts ? "（" + esc(parts) + "）" : ""}</span><span class="muted">${flowEntries.length ? "点一个入口看时序；点行展开 / 折叠，点名字切到调用图" : "没有发现入口，或服务没有 /api/arch/entries"}</span>`;
}

/** 选中一个入口并画它的时序图；preloaded 是自动挑选时已经拿到的流程，省一次请求。 */
async function selectFlowEntry(e: EntryDto, preloaded: FlowDto | null = null): Promise<void> {
  if (!api) return;
  flowEntry = e;
  flowDto = null;
  recordMap();
  flowView?.renderEntries(flowEntries, e);
  if (!preloaded) flowView?.clearFlow("正在加载…");
  renderContainerCrumbs("flow");
  // 流程叙述的 key 是入口 symbolId 的十进制字符串
  card?.show("flow", String(e.symbolId), entryTitle(e));
  const f = preloaded ?? await api.flow(e.symbolId, undefined, e);
  if (flowEntry !== e) return; // 等待期间用户又点了别的
  if (f === null) { toast("服务没有 /api/arch/flow，或入口已不在索引里"); flowView?.clearFlow("拿不到这个入口的流程"); return; }
  flowDto = f;
  flowView?.renderFlow(f);
  showFlowEntryDetails(e, f);
}

function showFlowEntryDetails(e: EntryDto, f: FlowDto | null): void {
  const kind = EntryKindLabels[e.kind] ?? e.kind;
  const where = `${e.file}:${e.line}`;
  const stats = f ? `<span class="muted">${f.actors.length} 个参与者 · ${f.nodes} 个节点${f.truncated ? " · 已截断" : ""}</span>` : "";
  detailsEl.innerHTML = `<span class="kind">${esc(kind)}</span><span class="mono" title="${esc(e.qualifiedName)}">${esc(entryTitle(e))}</span><span class="muted">${esc(where)}</span><a id="open-link">在编辑器中打开</a>${stats}`;
  detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => {
    host.post({ type: "open", path: e.file, line: e.line, col: 1 });
    if (!host.isVsCode) toast(`（浏览器模式）打开 ${where}`);
  });
}

function showFlowNodeDetails(node: FlowNodeDto): void {
  if (node.symbolId !== null) void focusSymbolById(node.symbolId);
}

function showDsmComponentDetails(c: DsmComponentDto): void {
  // 组件叙述的 key = <容器键>|<组件键>，钻入后的命名空间层也一样
  if (dsmContainer) card?.show("component", `${dsmContainer.key}|${c.key}`, c.label);
  const hot = c.hot ? `<span class="muted">git：${hotRevisionsText(c.hot)}，+${c.hot.added}/−${c.hot.deleted} 行，最近 ${c.hot.lastCommit}（${c.hot.ageMonths} 个月前）</span>` : "";
  const cycle = dsmDto?.cycles.find((cy) => cy.includes(c.key));
  const cyc = cycle ? `<span class="warn">环：${esc(cycle.map((k) => k.slice(Math.max(k.lastIndexOf("."), k.lastIndexOf("/")) + 1)).join(" ↔ "))}</span>` : "";
  detailsEl.innerHTML = `<span class="kind">组件</span><span class="mono">${esc(c.key)}</span><span class="muted">${c.symbols} 个符号 · ${c.files} 个文件${c.hasChildren ? " · 可钻入" : ""}</span>${hot}${cyc}`;
}

function showDsmCellDetails(cell: DsmCellModel, from: DsmComponentDto, to: DsmComponentDto): void {
  const kinds = cell.cell ? Object.entries(cell.cell.kinds).map(([k, n]) => `${k} ${n}`).join("，") : "没有静态依赖";
  const coupled = cell.coupled ? `<span class="warn">常一起改：共同修订 ${cell.coupled.shared} 次，耦合度 ${Math.round(cell.coupled.degree * 100)}%${cell.cell ? "" : "，却没有静态边"}</span>` : "";
  const cyc = cell.cycle ? `<span class="warn">环依赖</span>` : "";
  detailsEl.innerHTML = `<span class="kind">依赖</span><span class="mono">${esc(from.label)} → ${esc(to.label)}</span><span class="muted">${esc(kinds)}</span>${coupled}${cyc}`;
}

// ---------- 容器图（L1）----------

async function loadArch(): Promise<void> {
  if (!api) return;
  const a = await api.arch();
  archDto = a;
  if (a === null) { toast("服务没有 /api/arch/containers，切回命名空间视图"); setMapView("ns"); return; }
  showArch(a);
}

function showArch(a: ArchDto): void {
  archRenderer ??= new ArchRenderer($("arch-cy"), {
    onNodeSelect: showArchNodeDetails,
    onEdgeSelect: showArchEdgeDetails,
    onContainerDoubleClick: (n) => openDsm(n),
  });
  archRenderer.render(a, presets.container);
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = '<span class="crumb current">项目</span>';
  const containers = a.nodes.filter((n) => n.kind === "container").length;
  const externals = a.nodes.filter((n) => n.kind === "external").length;
  const firm = a.edges.filter((e) => e.confidence >= 0.7).length;
  const parts = [`${containers} 个容器`, `${externals} 个外部系统`, `${a.edges.length} 条边（其中 ${firm} 条按配置或路由对上）`];
  if (a.conflicts.length > 0) parts.push(`声明与发现冲突 ${a.conflicts.length} 处`);
  detailsEl.innerHTML = `<span class="kind">容器图</span><span>${esc(parts.join("，"))}</span><span class="muted">双击容器钻入；点边看证据</span>`;
}

function evidenceLinks(list: EvidenceDto[], max = 6): string {
  const items = list.slice(0, max).map((e) => `<span class="mono" title="${esc(e.note ?? "")}">${esc(e.file)}${e.line !== null ? ":" + e.line : ""}</span>`);
  if (list.length > max) items.push(`<span class="muted">…还有 ${list.length - max} 处</span>`);
  return items.join("，");
}

/** 单击容器：详情条照旧 + 叙述卡切到该容器（只 GET，不自动排队）。updateCard=false 用于证据定位，不动叙述卡。 */
function showArchNodeDetails(n: ArchNodeDto | null, updateCard = true): void {
  if (!n) return;
  if (updateCard) {
    if (n.kind === "container") showContainerCard(n);
    else card?.clear("外部系统 / 使用者没有叙述；单击一个容器看它的。");
  }
  const sub = SubKindLabels[n.subKind] ?? n.subKind;
  const extra = n.kind === "container"
    ? `<span class="muted">成员 ${n.members.length} 个项目${n.ports.length ? " · 端口 " + n.ports.join("/") : ""}${n.outbound ? " · " + n.outbound + " 处出口" : ""}</span>`
    : `<span class="muted">${n.source === "declared" ? "来自 architecture.yaml 声明" : "静态发现"}</span>`;
  detailsEl.innerHTML = `<span class="kind">${esc(sub)}</span><span class="mono">${esc(n.name)}</span>${extra}<span>证据：${evidenceLinks(n.evidence)}</span>`;
}

function showArchEdgeDetails(e: ArchEdgeDto | null): void {
  if (!e || !archDto) return;
  const name = (k: string) => archDto!.nodes.find((n) => n.key === k)?.name ?? k;
  const tier = e.source === "declared" ? "声明（1.0）" : e.confidence >= 0.9 ? "按路由对上（0.9）" : e.confidence >= 0.7 ? "按配置地址对上（0.7）" : "只知道客户端（0.5）";
  const addr = [e.configKey ? `配置 ${e.configKey}` : "", e.url ?? ""].filter(Boolean).join(" → ");
  detailsEl.innerHTML = `<span class="kind">${esc(e.protocol)}</span><span class="mono">${esc(name(e.from))} → ${esc(name(e.to))}</span><span class="muted">${esc(tier)}${addr ? " · " + esc(addr) : ""}</span><span>证据：${evidenceLinks(e.evidence)}</span>`;
}

// 没有容器的仓库：「容器」按钮回到整库 DSM
$("view-arch").onclick = () => {
  saveMapViewChoice(() => window.localStorage, "arch");
  if (archDto && shouldOpenRepoDsm(archDto)) { openDsm(repoContainerNode(repoDisplayName())); return; }
  setMapView("arch");
};

function renderMapCrumbs(s: MapState, reload: () => void): void {
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = "";
  s.crumbs.forEach((c, i) => {
    if (i > 0) crumbs.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (c.root === s.root ? " current" : "");
    el.textContent = c.label;
    el.onclick = () => { s.goTo(c.root); reload(); };
    crumbs.appendChild(el);
  });
}

async function loadNamespaceMap(): Promise<void> {
  if (!api) return;
  mapRenderer ??= new MapRenderer($("map-cy"), {
    onGroupDoubleClick: (g) => {
      if (g.symbolId !== null) { void focusSymbolById(g.symbolId); return; }
      mapState.drill(g.key);
      void loadNamespaceMap();
    },
    onGroupSelect: showMapGroupDetails,
    onEdgeSelect: showMapEdgeDetails,
  });
  if (mapView === "ns") recordMap();
  const map = await api.map(mapState.root);
  mapRenderer.render(map);
  // 面包屑回到"项目"时，有容器图就回容器图，而不是整库的命名空间顶层
  renderMapCrumbs(mapState, () => { if (mapState.root === "" && archDto && archDto.nodes.length > 0) setMapView("arch"); else void loadNamespaceMap(); });
  const total = map.groups.reduce((n, g) => n + g.symbols, 0);
  detailsEl.innerHTML = `<span class="kind">地图</span><span>${esc(mapState.root || "整个项目")}：${map.groups.length} 个${map.groups.some((g) => g.symbolId !== null) ? "节点" : "模块"}，${total} 个符号，${map.edges.length} 条依赖</span><span class="muted">双击模块钻入，双击类型切到调用图</span>`;
}

function focusSymbolById(id: number): Promise<void> {
  return api!.symbol(id).then((s) => { if (s) void focusCall(s); else toast("符号在索引里找不到了"); });
}

// ---------- 框架流程 ----------

/** 容器图里的容器（标题下拉的选项）。 */
function containerNodes(): ArchNodeDto[] {
  return (archDto?.nodes ?? []).filter((n) => n.kind === "container");
}

/** 各容器的 HTTP 入口数：挑默认容器用，每个仓库取一次（索引刷新后作废）。 */
async function loadHttpCounts(): Promise<Map<string, number>> {
  if (httpCounts) return httpCounts;
  const a = api!;
  const counts = new Map<string, number>();
  await Promise.all(containerNodes().map(async (c) => {
    try { counts.set(c.key, (await a.entries(c.key)).filter((e) => e.kind === "http").length); } catch { counts.set(c.key, 0); }
  }));
  httpCounts = counts;
  return counts;
}

/**
 * 顶层「框架流程」：看上次看的容器；没有就看当前 / 最近一次钻进的容器；再没有就挑 HTTP 入口最多的。
 * 标题里有容器下拉，随时换。
 */
async function openFrameworkDefault(): Promise<void> {
  if (!api) return;
  if (!archDto) archDto = await api.arch();
  const recent = fwContainer?.key ?? (dsmContainer && !isRepoContainer(dsmContainer) ? dsmContainer.key : null);
  const nodes = archDto?.nodes ?? [];
  const known = recent !== null && nodes.some((n) => n.kind === "container" && n.key === recent);
  const c = pickFrameworkContainer(nodes, recent, known ? null : await loadHttpCounts());
  if (!c) { toast("没有发现可部署的容器，框架流程无从谈起"); return; }
  openFramework(c);
}

function openFramework(container: ArchNodeDto): void {
  fwContainer = container;
  dsmContainer = container;
  // 框架流程没有叙述卡；回到容器的其他子视图时它们自己再 show
  applyMapView("framework");
  void loadFramework();
}

async function loadFramework(): Promise<void> {
  if (!api) return;
  if (!fwContainer) { await openFrameworkDefault(); return; }
  const c = fwContainer;
  const seq = ++fwSeq;
  fwView ??= new FrameworkView($("fw-view"), {
    onContainerChange: (key) => {
      const next = containerNodes().find((n) => n.key === key);
      if (next) openFramework(next);
    },
    onNodeSelect: (node) => showSeqNodeDetails(node, focusInCallSeq, frameworkNodeKind(node)),
    onNodeFocus: focusInCallSeq,
    onOpenEntry: (e) => void openEntryFlow(fwContainer?.key ?? "", e),
    onOpenHandler: (h) => { toast(`没有找到分派到 ${h.messageType ?? h.handler.name} 的入口，改在调用图里看这个 Handler`, 4000); void focusInCallSeqSymbol(h.handler.id); },
    loadEntries: (key) => (api ? api.entries(key) : Promise.resolve([])),
  });
  dsmContainer = c;
  renderContainerCrumbs("framework");
  if (fwView.current?.containerKey !== c.key) fwView.clear(`正在取 ${c.name} 的框架流程…`);
  let d: FrameworkFlowDto | null;
  try {
    d = await api.framework(c.key);
  } catch (e) {
    if (seq === fwSeq) { fwDto = null; fwView.unavailable(`取框架流程失败：${e}`, containerNodes(), c.key, c.name); detailsEl.innerHTML = `<span class="kind">框架流程</span><span class="warn">${esc(String(e))}</span>`; }
    return;
  }
  if (seq !== fwSeq || mapView !== "framework") return;
  if (d === null) {
    fwDto = null;
    fwView.unavailable(`拿不到 ${c.name} 的框架流程：服务版本太旧（没有 /api/arch/framework，重启服务或更新扩展），或容器已不在索引里。可以在上面换一个容器。`, containerNodes(), c.key, c.name);
    detailsEl.innerHTML = `<span class="kind">框架流程</span><span class="muted">服务没有 /api/arch/framework，或容器已不在索引里</span>`;
    return;
  }
  // 没变就不重画（索引刷新、切回来），免得把展开 / 清单的状态冲掉
  const unchanged = fwDto !== null && fwView.current !== null && JSON.stringify(fwDto) === JSON.stringify(d);
  fwDto = d;
  if (!unchanged) fwView.render(d, containerNodes());
  showFrameworkSummary(d);
}

function showFrameworkSummary(d: FrameworkFlowDto): void {
  detailsEl.innerHTML = `<span class="kind">框架流程</span><span>${esc(d.containerName)}：${esc(frameworkSummary(d.stages, d.handlers))}</span><span class="muted">点「N 个 Handler ▸」看清单；单击行看详情，双击在调用图里以它为焦点</span>`;
}

/** 框架流程里一行在详情条上的种类：它所在阶段的名字。 */
function frameworkNodeKind(node: FlowNodeDto): string | undefined {
  const actor = fwDto?.flow.actors.find((a) => a.key === node.actor);
  return actor && fwDto ? stageOfActor(actor, fwDto.stages)?.label : undefined;
}

/** 框架流程里双击 / 「在调用图中查看」：切到调用图的时序模式（不改用户记住的画法），以它为焦点。 */
function focusInCallSeq(node: FlowNodeDto): void {
  if (!isFocusableNode(node) || node.symbolId === null) return;
  void focusInCallSeqSymbol(node.symbolId);
}

async function focusInCallSeqSymbol(id: number): Promise<void> {
  if (!api) return;
  const s = await api.symbol(id);
  if (!s) { toast("符号在索引里找不到了"); return; }
  if (mode !== "graph") setMode("graph");
  if (callView.mode !== "seq") await callView.setMode("seq", false);
  await callView.focus(s);
}

/** Handler / 控制器清单里点一个入口：打开它所在容器的入口流程并选中它。 */
async function openEntryFlow(containerKey: string, entry: EntryDto): Promise<void> {
  const c = containerNodes().find((n) => n.key === containerKey) ?? fwContainer;
  if (!c) return;
  autoPickSeq++;
  if (flowContainerKey !== c.key) { flowContainerKey = c.key; flowEntries = []; }
  dsmContainer = c;
  flowEntry = entry;
  flowDto = null;
  applyMapView("flow");
  await loadFlowEntries();
  if (flowEntry !== entry) return; // 等待期间用户又点了别的
  // 换成入口清单里的那一个对象（高亮、索引刷新后找回都按清单里的）
  await selectFlowEntry(findSameEntry(flowEntries, entry) ?? entry);
}

/** 入口流程 / 调用图时序标题里的「经框架流程 ▸」：切到流程所在容器的框架流程。 */
async function gotoFramework(dto: FlowDto): Promise<void> {
  if (!api) return;
  if (!archDto) archDto = await api.arch();
  const c = frameworkJumpTarget(dto, archDto?.nodes ?? []);
  if (!c) toast(`容器图里找不到容器 ${dto.containerKey}，改看默认容器`);
  autoFlowPending = false;
  if (c) fwContainer = c;
  if (mode !== "map") {
    // 先定好视图再切模式：loadMapView 看到 mapView 已定，就不走第一屏的决策，直接取框架流程
    applyMapView("framework");
    setMode("map");
    return;
  }
  if (c) openFramework(c); else await openFrameworkDefault();
}

$("view-framework").onclick = () => {
  saveMapViewChoice(() => window.localStorage, "framework");
  if (mapView !== "framework") { autoFlowPending = false; void openFrameworkDefault(); }
};
$("view-ns").onclick = () => { saveMapViewChoice(() => window.localStorage, "ns"); setMapView("ns"); };

function showMapGroupDetails(g: MapGroupDto | null): void {
  if (!g) return;
  detailsEl.innerHTML = `<span class="kind">${g.symbolId !== null ? "类型" : "模块"}</span><span class="mono">${esc(g.key)}</span>` +
    `<span class="muted">${g.symbols} 个符号 · ${g.files} 个文件${g.hasChildren ? " · 可钻入" : ""}</span>`;
}

function showMapEdgeDetails(e: MapEdgeDto | null): void {
  if (!e) return;
  const kinds = Object.entries(e.kinds).map(([k, n]) => `${k} ${n}`).join("，");
  detailsEl.innerHTML = `<span class="kind">依赖</span><span class="mono">${esc(e.from)} → ${esc(e.to)}</span><span class="muted">${e.weight} 条：${esc(kinds)}</span>`;
}

$("mode-graph").onclick = () => setMode("graph");
$("nav-back").onclick = navBack;
$("nav-fwd").onclick = navForward;
// Alt+← / Alt+→（面板有焦点时；编辑器里的 Alt+← 是 VS Code 自己的导航历史）、鼠标侧键
window.addEventListener("keydown", (e) => {
  if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
  if (e.key === "ArrowLeft") { e.preventDefault(); navBack(); }
  else if (e.key === "ArrowRight") { e.preventDefault(); navForward(); }
});
window.addEventListener("mouseup", (e) => {
  if (e.button === 3) { e.preventDefault(); navBack(); }
  else if (e.button === 4) { e.preventDefault(); navForward(); }
});
$("mode-rel").onclick = () => { if (mode !== "relations") setMode("relations"); };
$("mode-map").onclick = () => setMode("map");

// ---------- 索引与连接 ----------

let statusTimer = 0;
let sawRunning = false;
async function checkHealth(): Promise<void> {
  if (!api) return;
  try {
    const h = await api.health();
    if (h.files === 0) {
      showOverlay("这个仓库还没有索引", `${h.repo}`, true);
    } else {
      overlay.classList.remove("open");
    }
    const st = await api.status();
    if (st.running) pollStatus();
  } catch (e) {
    showOverlay("连不上 CodeTrail Graph 服务", String(e), false);
  }
}
function showOverlay(title: string, text: string, showIndexButton: boolean): void {
  $("ov-title").textContent = title;
  $("ov-text").textContent = text;
  $<HTMLButtonElement>("ov-index").style.display = showIndexButton ? "inline-block" : "none";
  $("progress").style.display = "none";
  overlay.classList.add("open");
}
async function startIndex(full: boolean): Promise<void> {
  if (!api) return;
  // VS Code 里由扩展发起（它要更新状态栏）；浏览器调试模式下页面自己调服务
  if (host.isVsCode) host.post({ type: "requestIndex", full });
  else if ((await api.startIndex(full)) === "busy") toast("索引正在进行中");
  sawRunning = true;
  pollStatus();
}
function pollStatus(): void {
  window.clearTimeout(statusTimer);
  statusTimer = window.setTimeout(async () => {
    if (!api) return;
    const s = await api.status();
    if (s.running) {
      sawRunning = true;
      $("ov-title").textContent = "正在建立索引";
      $("ov-text").textContent = `${s.phase ?? ""} ${s.done}/${s.total}`;
      $("progress").style.display = "block";
      ($("progress").firstElementChild as HTMLElement).style.width = s.total ? `${Math.round((s.done / s.total) * 100)}%` : "10%";
      $<HTMLButtonElement>("ov-index").style.display = "none";
      overlay.classList.add("open");
      pollStatus();
    } else if (sawRunning) {
      // 只有真正看到过一次"进行中"才算一次索引结束，避免 init 时误关覆盖层、误弹"完成"
      sawRunning = false;
      overlay.classList.remove("open");
      if (s.error) toast(`索引失败：${s.error}`, 6000);
      else if (s.lastReport) toast(`索引完成：${s.lastReport.totalSymbols} 个符号，${s.lastReport.elapsedSeconds.toFixed(1)} 秒`);
      refreshMapAfterIndex();
      await refreshCallViews();
      await refreshRelations();
    }
  }, 500);
}

/** 索引完成后刷新项目地图；轮询结束与扩展的 indexChanged 可能前后脚到，合并成一次。 */
let mapRefresh: Promise<void> | null = null;
function refreshMapAfterIndex(): void {
  if (mapRefresh) return;
  mapRefresh = doRefreshMap().catch((e) => toast(`刷新项目地图失败：${e}`)).finally(() => { mapRefresh = null; });
}

async function doRefreshMap(): Promise<void> {
  if (!api) return;
  // 第一屏还没判过（索引前容器图是空的）：重新走一遍第一屏决策，只有一个容器就直接进入口流程
  if (autoFlowPending && mapView !== "dsm" && mapView !== "flow" && mapView !== "framework") {
    mapView = null;
    if (mode === "map") await loadMapView();
    return;
  }
  if (mode !== "map") return;
  if (mapView === "arch") {
    const a = await api.arch();
    // 没变就不重画，免得每次保存后的增量索引把用户的缩放 / 选中冲掉
    if (a && mapView === "arch" && JSON.stringify(a) !== JSON.stringify(archDto)) { archDto = a; showArch(a); }
  } else if (mapView === "flow") await reloadFlow();
  else if (mapView === "dsm") await loadDsm();
  else if (mapView === "ns") await loadNamespaceMap();
  else if (mapView === "framework") { httpCounts = null; await loadFramework(); }
}

/** 入口流程视图下索引完成：重取入口清单，按 id / 限定名找回原来的入口再重取流程；找不到就重新自动挑一个。 */
async function reloadFlow(): Promise<void> {
  if (!api || !dsmContainer) return;
  const container = dsmContainer.key;
  const old = flowEntry;
  const oldFlow = flowDto;
  const entries = await api.entries(container);
  if (mapView !== "flow" || dsmContainer?.key !== container || flowEntry !== old) return;
  const same = old ? findSameEntry(entries, old) : null;
  if (same) {
    const f = await api.flow(same.symbolId, undefined, same);
    if (mapView !== "flow" || flowEntry !== old) return;
    const unchanged = same.symbolId === old!.symbolId && JSON.stringify(entries) === JSON.stringify(flowEntries) && JSON.stringify(f) === JSON.stringify(oldFlow);
    if (unchanged) return;
    flowEntries = entries;
    await selectFlowEntry(same, f);
    return;
  }
  flowEntry = null;
  flowDto = null;
  await loadFlowEntries(old !== null);
}
/** 网状图的数据过时了（藏着的时候索引刷新过），切回网状时重建。 */
let graphStale = false;

/** 网状图按新开关 / 新索引重建当前焦点（不新增面包屑）。 */
async function refreshFocus(): Promise<void> {
  if (!api || !state.focusNodeId) return;
  const a = api;
  const seq = ++graphSeq;
  // 网状图藏着（时序模式）时不重画：cytoscape 在隐藏的容器里量不出尺寸；切回网状时再重建
  if (callView.mode !== "graph") { graphStale = true; return; }
  graphStale = false;
  const old = state.getNode(state.focusNodeId)?.symbol;
  if (!old) return;
  // 增量索引会重建被改文件的符号，id 会变：按 id → 原位置 → 限定名 依次找回
  const s = await resolveRefreshedSymbol(a, old);
  // 等待期间换了焦点（切回网状时按时序焦点重画了）：不要用旧焦点把它盖掉
  if (seq !== graphSeq || a !== api || state.focusNodeId !== GraphState.nodeIdFor(old)) return;
  if (s) await focusSymbol(s, true);
  else toast("焦点符号在新索引里找不到了");
}

/** 索引刷新后：网状与时序两边的焦点都按新索引找回重画。 */
async function refreshCallViews(): Promise<void> {
  await refreshFocus();
  if (!api) return;
  const a = api;
  if (!(await callView.refresh((old) => resolveRefreshedSymbol(a, old))) && callView.mode === "seq") toast("时序图的焦点在新索引里找不到了");
}
$("ov-index").onclick = () => void startIndex(false);
$("reindex").onclick = () => void startIndex(false);
$("chain-down").onclick = () => { if (state.focusNodeId) void expandChain(state.focusNodeId, "out"); };
$("chain-up").onclick = () => {
  // 时序模式：切到网状再往上追（时序图只画往下的调用）；这次切换不算用户的选择
  if (callView.mode === "seq") { const s = callView.history.current; if (s) void callView.focus(s, { chain: "in" }); else toast("先选一个焦点"); return; }
  if (state.focusNodeId) void expandChain(state.focusNodeId, "in");
};
$("show-refs").onchange = () => { void refreshFocus(); };
$("show-types").onchange = () => { void refreshFocus(); };
$("call-view").querySelectorAll<HTMLButtonElement>("button[data-view]").forEach((b) => {
  b.onclick = () => void callView.setMode(b.dataset["view"] === "graph" ? "graph" : "seq");
});
$("show-tests").onchange = () => {
  void refreshFocus();
  // 模块关系：tests=true/false；不在这个模式时 relParams 对不上，切回来会自动重取
  const center = relHistory.current;
  if (mode === "relations" && center) void openRelations(center, "replace");
};
$("show-conduit").onchange = () => { void refreshFocus(); };
$("branch-width").onchange = () => { void refreshFocus(); };

// 跟随光标（调用图）：勾上后宿主把编辑器光标所在方法发过来（已防抖、同一方法不重发）。浏览器调试模式没有编辑器，不显示
if (!host.isVsCode) $("follow-cursor").closest("label")!.remove();
else $<HTMLInputElement>("follow-cursor").onchange = (e) => host.post({ type: "followCursor", enabled: (e.target as HTMLInputElement).checked });

function onCursorSymbol(symbolId: number): void {
  const box = document.getElementById("follow-cursor") as HTMLInputElement | null;
  // 只在调用图模式下跟；别的模式（模块关系、项目地图）不打断
  if (!box?.checked || mode !== "graph" || !api) return;
  const a = api;
  const seq = ++symbolRequestSeq;
  // 时序 / 网状各看各的焦点：已经是它就不重画
  if (callView.focusId() === symbolId) return;
  void a.symbol(symbolId).then((s) => { if (s && seq === symbolRequestSeq && a === api && box.checked && mode === "graph" && callView.focusId() !== s.id) void focusCall(s); });
}

// ---------- 启动 ----------

let currentRepo: string | null = null;
function init(apiBase: string, token: string | null, repoRoot: string | null): void {
  graphSeq++;
  symbolRequestSeq++;
  dsmSeq++;
  searchSeq++;
  lastHits = [];
  dropdown.classList.remove("open");
  api = new Api(apiBase.replace(/\/$/, ""), token);
  if (repoRoot !== currentRepo) {
    // 切到另一个仓库：旧图里的符号 id 对新服务无意义
    currentRepo = repoRoot;
    state.reset();
    mapState.reset();
    mapView = null;
    autoFlowPending = true;
    autoPickSeq++;
    archDto = null;
    dsmContainer = null; dsmPath = []; dsmDto = null; dsmShown = null;
    flowContainerKey = null; flowEntries = []; flowEntry = null; flowDto = null;
    flowView?.renderEntries([], null); flowView?.clearFlow();
    fwContainer = null; fwDto = null; fwSeq++; httpCounts = null; fwView?.clear("");
    relHistory.reset(); relDto = null; relParams = ""; relSyncedFromGraph = null; relSeq++; relSelectedEdge = null;
    relRenderer?.clear(); renderRelationsChrome();
    card?.clear();
    callView.reset();
    nav.reset();
    graphStale = false;
    render();
    if (mode === "graph") showCallDetails();
  }
  // 叙述卡：数据与跳转都经回调回到这里；api 为空时一律当 404（服务版本太旧）
  card ??= new NarrationCard($("narration"), {
    fetch: (kind, key) => (api ? api.narrative(kind, key) : Promise.resolve(null)),
    generate: (kind, key, force) => (api ? api.narrate(kind, key, force) : Promise.resolve(null)),
    status: () => (api ? api.narrateStatus() : Promise.resolve(null)),
    control: (action) => (api ? api.narrateControl(action) : Promise.resolve(null)),
    onEvidence: (ref, raw) => void onEvidence(ref, raw),
    onApplyLayout: (kind, layout) => applyPreset(kind, layout),
    currentPreset: (kind) => presets[kind],
    onToggle: () => { archRenderer?.resize(); mapRenderer?.resize(); },
    runCommand: (command) => {
      if (host.isVsCode) host.post({ type: "command", command });
      else toast(`（浏览器模式）请在 VS Code 命令面板运行 ${command}`);
    },
  });
  renderer ??= new Renderer($("cy"), {
    onNodeDoubleClick: (n) => {
      if (n.callers) expandCallersCapsule(n);
      else if (n.type === "ambiguous") void expandCapsule(n);
      // 默认只展开「它调谁」：这一侧展开过（或没有可展开的）再双击就跳代码
      else if (n.type === "symbol" && n.symbol && (n.expandedOut || !n.expandable)) { if (n.symbol.kind !== "external") openInEditor(n.symbol); }
      else void expandNode(n, "out");
    },
    onNodeContext: showContextMenu,
    // 「被 N 处调用」胶囊点一下就加入调用者
    onNodeSelect: (n) => { if (n?.callers) expandCallersCapsule(n); else showNodeDetails(n); },
    onEdgeSelect: showEdgeDetails,
  });
  void checkHealth();
  if (pendingRelations) { const p = pendingRelations; pendingRelations = null; showRelationsFor(p.symbolId, p.depth); }
}

host.onMessage((msg) => {
  if (msg.type === "init") init(msg.apiBase, msg.token ?? null, msg.repoRoot ?? null);
  else if (msg.type === "focus") {
    if (mode !== "graph") setMode("graph");
    // chain = "in"（影响范围视图的「在面板中打开」，要看谁调了它）：时序图只画往下的调用，CallView 会切到网状并「↑ 到源头」
    const chain = msg.chain;
    const a = api;
    const seq = ++symbolRequestSeq;
    void a?.symbol(msg.symbolId).then((s) => { if (s && seq === symbolRequestSeq && a === api) void focusCall(s, chain); });
  }
  else if (msg.type === "cursorSymbol") onCursorSymbol(msg.symbolId);
  else if (msg.type === "indexChanged") { sawRunning = false; overlay.classList.remove("open"); refreshMapAfterIndex(); void refreshCallViews(); void refreshRelations(); }
  else if (msg.type === "showRelations") {
    if (api) showRelationsFor(msg.symbolId, msg.depth);
    else pendingRelations = { symbolId: msg.symbolId, depth: msg.depth };
  }
});
window.addEventListener("resize", () => { if (callView.mode === "graph") renderer?.resize(); relRenderer?.resize(); mapRenderer?.resize(); archRenderer?.resize(); });

applyCallViewChrome();
showSeq({ status: "empty" });

const params = new URLSearchParams(location.search);
const apiParam = params.get("api");
if (apiParam) init(apiParam, params.get("token"), "browser");
host.post({ type: "ready" });
