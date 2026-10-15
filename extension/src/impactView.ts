// 侧边栏「影响范围」树视图（反向：改它会影响谁）：vscode 薄层。逻辑在 impactModel.ts（ImpactDto → 条目）；
// 光标 → 当前方法与调用大纲共用 cursorFollow.ts 的共享来源（一次 symbolAt），固定状态各管各的。
import * as vscode from "vscode";
import type { ImpactDto, SymbolDto } from "../webview/types";
import { currentPos, type SharedFollow } from "./cursorFollow";
import {
  callerChildren, canExpand, containerChildren, entriesChildren, expandedAncestors, impactClick, impactDefinition, impactItemSpec, rootChildren,
  type ImpactNode, type ImpactRootNode,
} from "./impactModel";
import type { FollowResult, FollowSubscription, ImpactClient, OutlineState } from "./outlineFollow";
import type { JumpTarget } from "./outlineModel";

export const ImpactViewId = "codeatlas.impact";
const TestsStateKey = "codeatlas.impact.tests";

export interface ImpactHost {
  /** 拉起 / 复用服务，返回客户端；失败返回 null（错误已报）。 */
  connect(root: string): Promise<ImpactClient | null>;
  /** 在 CodeTrail Graph 面板里聚焦这个符号。 */
  /** how：graph（调用图聚焦）/ callers（聚焦后"↑ 到源头"）/ relations（模块关系）。 */
  openPanel(root: string, symbolId: number, how?: "graph" | "callers" | "relations"): Promise<void>;
  log(line: string): void;
  /** showFor 只给了符号 id、没给仓库时用哪个根（主根）。 */
  defaultRoot?(): string | undefined;
}

export interface ImpactView extends vscode.Disposable {
  /** 索引跑完了：清缓存（随后 SharedFollow.indexChanged 统一重新解析光标；固定时只重取）。 */
  indexChanged(): void;
  /** 以这个方法为根并固定（不再跟随光标），打开侧边栏聚焦这个视图。CodeLens「影响范围」用。 */
  showFor(symbol: SymbolDto, repoRoot: string): Promise<void>;
}

export function registerImpactView(context: vscode.ExtensionContext, host: ImpactHost, follow: SharedFollow): ImpactView {
  const view = new ImpactController(context, host, follow);
  context.subscriptions.push(view);
  return view;
}

class ImpactController implements vscode.TreeDataProvider<ImpactNode>, ImpactView {
  private readonly changed = new vscode.EventEmitter<ImpactNode | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly tree: vscode.TreeView<ImpactNode>;
  private readonly follower: FollowSubscription;
  private readonly disposables: vscode.Disposable[] = [];
  /** 本轮的结果缓存：`符号 id|entries` → 结果。换根 / 刷新 / 切换测试 / 索引变化时清空。 */
  private cache = new Map<string, Promise<ImpactDto | null>>();
  private root: ImpactRootNode | null = null;
  private state: OutlineState = "noMethod";
  private tests: boolean;

  constructor(private readonly context: vscode.ExtensionContext, private readonly host: ImpactHost, private readonly follow: SharedFollow) {
    this.tests = context.globalState.get<boolean>(TestsStateKey, false);
    this.tree = vscode.window.createTreeView(ImpactViewId, { treeDataProvider: this, showCollapseAll: true });
    this.follower = follow.source.subscribe((r) => this.apply(r));
    this.disposables.push(
      this.tree, this.changed, this.follower,
      this.tree.onDidChangeVisibility((e) => this.follower.setActive(e.visible, currentPos())),
      vscode.commands.registerCommand("codeatlas.impact.pin", () => this.setPinned(true)),
      vscode.commands.registerCommand("codeatlas.impact.unpin", () => this.setPinned(false)),
      vscode.commands.registerCommand("codeatlas.impact.includeTests", () => this.setTests(true)),
      vscode.commands.registerCommand("codeatlas.impact.excludeTests", () => this.setTests(false)),
      vscode.commands.registerCommand("codeatlas.impact.refresh", () => void this.refresh()),
      vscode.commands.registerCommand("codeatlas.impact.openInPanel", () => void this.openInPanel()),
      vscode.commands.registerCommand("codeatlas.impact.openRelations", () => void this.openRelations()),
      vscode.commands.registerCommand("codeatlas.impact.open", (node?: ImpactNode) => { if (node) void this.click(node); }),
      vscode.commands.registerCommand("codeatlas.impact.goToDefinition", (node?: ImpactNode) => { if (node) void this.jump(impactDefinition(node), false); }),
      // 参数：SymbolDto 或符号 id，可选仓库根（不给用主根）
      vscode.commands.registerCommand("codeatlas.impact.showFor", (arg?: SymbolDto | number, repoRoot?: string) => this.showForArg(arg, repoRoot)),
    );
    void this.setState("noMethod");
    this.syncTags();
    void vscode.commands.executeCommand("setContext", "codeatlas.impact.tests", this.tests);
    if (this.tree.visible) this.follower.setActive(true, currentPos());
  }

  // ---------- TreeDataProvider ----------

  getTreeItem(node: ImpactNode): vscode.TreeItem {
    const spec = impactItemSpec(node);
    const state = spec.collapsible === "expanded" ? vscode.TreeItemCollapsibleState.Expanded
      : spec.collapsible === "collapsed" ? vscode.TreeItemCollapsibleState.Collapsed
        : vscode.TreeItemCollapsibleState.None;
    const item = new vscode.TreeItem(spec.label, state);
    item.description = spec.description;
    item.tooltip = spec.tooltip;
    item.iconPath = new vscode.ThemeIcon(spec.icon, spec.iconColor ? new vscode.ThemeColor(spec.iconColor) : undefined);
    item.contextValue = spec.contextValue;
    if (spec.clickable) item.command = { command: "codeatlas.impact.open", title: node.type === "tests" ? "包含测试" : "跳转", arguments: [node] };
    return item;
  }

  async getChildren(node?: ImpactNode): Promise<ImpactNode[]> {
    if (!node) return this.root && this.state === "method" ? [this.root] : [];
    switch (node.type) {
      case "root": {
        const root = node;
        return this.load(root.symbol.id, root.repoRoot, true, (impact) => rootChildren(impact, root, this.tests));
      }
      case "entries": return entriesChildren(node);
      case "container": return containerChildren(node);
      case "callers": return node.children;
      case "caller": {
        if (!canExpand(node) || !this.root) return [];
        const ancestors = expandedAncestors(node);
        return this.load(node.caller.caller.id, this.root.repoRoot, false, (impact) => callerChildren(impact, ancestors, this.tests));
      }
      default: return [];
    }
  }

  private async load(symbolId: number, repoRoot: string, entries: boolean, map: (impact: ImpactDto) => ImpactNode[]): Promise<ImpactNode[]> {
    const key = `${symbolId}|${entries}`;
    try {
      let p = this.cache.get(key);
      if (!p) {
        const cache = this.cache;
        const fresh = this.fetchImpact(repoRoot, symbolId, entries);
        p = fresh;
        cache.set(key, fresh);
        // 失败的不缓存，再展开会重试（缓存期间被整个换掉了就不用管）
        fresh.catch(() => { if (cache.get(key) === fresh) cache.delete(key); });
      }
      const impact = await p;
      if (!impact) return [{ type: "info", text: "取不到这个方法的影响范围", tooltip: "符号不在索引里了（重新索引过？），或服务版本过旧还没有 impact 接口。", icon: "warning" }];
      return map(impact);
    } catch (e) {
      this.host.log(`影响范围加载失败：${e}`);
      return [{ type: "info", text: "加载失败，点刷新重试", tooltip: String(e), icon: "error" }];
    }
  }

  private async fetchImpact(repoRoot: string, symbolId: number, entries: boolean): Promise<ImpactDto | null> {
    const client = await this.host.connect(repoRoot);
    if (!client) throw new Error("CodeTrail Graph 服务不可用");
    return client.impact(symbolId, { entries, tests: this.tests });
  }

  // ---------- 跟随 ----------

  private apply(r: FollowResult): void {
    if (r.state === "method") {
      this.root = { type: "root", symbol: r.symbol, repoRoot: r.repoRoot };
      this.cache = new Map();
    } else {
      this.root = null;
      if (r.state === "error") this.host.log(`影响范围：${r.message}`);
    }
    void this.setState(r.state);
    this.changed.fire(undefined);
  }

  private async setState(state: OutlineState): Promise<void> {
    this.state = state;
    await vscode.commands.executeCommand("setContext", "codeatlas.impact.state", state);
  }

  // ---------- 命令 ----------

  private setPinned(pinned: boolean): void {
    this.follower.setPinned(pinned, currentPos());
    this.syncTags();
  }

  private syncTags(): void {
    void vscode.commands.executeCommand("setContext", "codeatlas.impact.pinned", this.follower.pinned);
    const tags = [this.follower.pinned ? "已固定" : "", this.tests ? "含测试" : ""].filter(Boolean);
    this.tree.description = tags.length ? tags.join(" · ") : undefined;
  }

  private setTests(on: boolean): void {
    if (this.tests === on) return;
    this.tests = on;
    void this.context.globalState.update(TestsStateKey, on);
    void vscode.commands.executeCommand("setContext", "codeatlas.impact.tests", on);
    this.syncTags();
    this.cache = new Map();
    this.changed.fire(undefined);
  }

  private async refresh(): Promise<void> {
    this.cache = new Map();
    this.follow.indexed.clear();
    const pos = currentPos();
    // 没固定且光标在一个归 CodeTrail Graph 管的文件里：按光标重新解析（欢迎页上的"重试"也走这里，用户点的，可以拉起服务）
    if (!this.follower.pinned && this.follow.canFollow(pos)) {
      await this.follower.resolveNow(pos);
      return;
    }
    // 固定时（或光标不在支持的文件里）只重取当前根
    this.changed.fire(undefined);
  }

  indexChanged(): void {
    this.cache = new Map();
    if (this.follower.pinned) this.changed.fire(undefined);
  }

  async showFor(symbol: SymbolDto, repoRoot: string, prefetched?: ImpactDto): Promise<void> {
    // 先固定再聚焦：聚焦让视图变可见，固定着就不会被光标跟随换掉；忘掉"上次跟随的是谁"，取消固定时按光标重新跟
    this.follower.reset();
    this.follower.setPinned(true);
    this.syncTags();
    this.root = { type: "root", symbol, repoRoot };
    this.cache = new Map();
    if (prefetched) this.cache.set(`${symbol.id}|true`, Promise.resolve(prefetched));
    await this.setState("method");
    this.changed.fire(undefined);
    await vscode.commands.executeCommand(`${ImpactViewId}.focus`);
  }

  private async showForArg(arg: SymbolDto | number | undefined, repoRoot: string | undefined): Promise<void> {
    const root = repoRoot ?? this.host.defaultRoot?.();
    if (arg === undefined || !root) return;
    if (typeof arg === "object") { await this.showFor(arg, root); return; }
    // 只有 id：取一次根的影响范围拿到符号（顺手放进缓存，展开根时不再请求）
    try {
      const impact = await this.fetchImpact(root, arg, true);
      if (!impact) { void vscode.window.showInformationMessage("这个方法不在索引里了（重新索引过？）。"); return; }
      await this.showFor(impact.symbol, root, impact);
    } catch (e) {
      this.host.log(`影响范围加载失败：${e}`);
      void vscode.window.showWarningMessage(`影响范围加载失败：${e instanceof Error ? e.message : e}`);
    }
  }

  private async openRelations(): Promise<void> {
    if (!this.root) {
      void vscode.window.showInformationMessage("影响范围里还没有方法：把光标放进一个方法。");
      return;
    }
    await this.host.openPanel(this.root.repoRoot, this.root.symbol.id, "relations");
  }

  private async openInPanel(): Promise<void> {
    if (!this.root) {
      void vscode.window.showInformationMessage("影响范围里还没有方法：把光标放进一个方法。");
      return;
    }
    // 影响范围看的是"谁调了它"：面板里直接沿调用链往上展开
    await this.host.openPanel(this.root.repoRoot, this.root.symbol.id, "callers");
  }

  private async click(node: ImpactNode): Promise<void> {
    const c = impactClick(node);
    if (!c) return;
    if (c.kind === "includeTests") { this.setTests(true); return; }
    await this.jump(c.target, true);
  }

  private async jump(target: JumpTarget | null, preserveFocus: boolean): Promise<void> {
    if (this.root) await this.follow.jump(this.root.repoRoot, target, preserveFocus);
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }
}
