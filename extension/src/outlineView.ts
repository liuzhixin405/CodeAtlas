// 侧边栏「调用大纲」树视图：vscode 薄层。逻辑在 outlineModel.ts（步骤 → 条目）与 outlineFollow.ts（跟随光标）；
// 光标 → 当前方法由 cursorFollow.ts 的共享来源解析（与「影响范围」共用一次 symbolAt），这里只订阅。
import * as vscode from "vscode";
import type { OutlineDto } from "../webview/types";
import { currentPos, type SharedFollow } from "./cursorFollow";
import type { FollowResult, FollowSubscription, OutlineClient, OutlineState } from "./outlineFollow";
import {
  childSource, clickTarget, definitionTarget, expandedAncestors, inPlaceChildren, itemSpec, outlineChildren, rootAncestors,
  type JumpTarget, type OutlineNode, type RootNode,
} from "./outlineModel";

export const OutlineViewId = "codeatlas.callOutline";
const DecorationScheme = "codeatlas-outline";
const ExternalsStateKey = "codeatlas.outline.externals";

export interface OutlineHost {
  /** 拉起 / 复用服务，返回客户端；失败返回 null（错误已报）。 */
  connect(root: string): Promise<OutlineClient | null>;
  /** 在 CodeTrail Graph 面板里聚焦这个符号。 */
  /** how：graph（调用图聚焦）/ callers（聚焦后"↑ 到源头"）/ relations（模块关系）。 */
  openPanel(root: string, symbolId: number, how?: "graph" | "callers" | "relations"): Promise<void>;
  log(line: string): void;
}

export interface CallOutline extends vscode.Disposable {
  /** 索引跑完了：清缓存、忘掉当前根（随后 SharedFollow.indexChanged 统一重新解析光标；固定时只重取）。 */
  indexChanged(): void;
}

export function registerCallOutline(context: vscode.ExtensionContext, host: OutlineHost, follow: SharedFollow): CallOutline {
  const view = new CallOutlineController(context, host, follow);
  context.subscriptions.push(view);
  return view;
}

class CallOutlineController implements vscode.TreeDataProvider<OutlineNode>, CallOutline {
  private readonly changed = new vscode.EventEmitter<OutlineNode | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly tree: vscode.TreeView<OutlineNode>;
  private readonly follower: FollowSubscription;
  private readonly disposables: vscode.Disposable[] = [];
  /** 本轮的大纲缓存：符号 id → 结果。换根 / 刷新 / 切换框架调用 / 索引变化时清空。 */
  private cache = new Map<number, Promise<OutlineDto | null>>();
  private root: RootNode | null = null;
  private state: OutlineState = "noMethod";
  private externals: boolean;

  constructor(private readonly context: vscode.ExtensionContext, private readonly host: OutlineHost, private readonly follow: SharedFollow) {
    this.externals = context.globalState.get<boolean>(ExternalsStateKey, true);
    this.tree = vscode.window.createTreeView(OutlineViewId, { treeDataProvider: this, showCollapseAll: true });
    this.follower = follow.source.subscribe((r) => this.apply(r));

    const decorations = new vscode.EventEmitter<vscode.Uri | vscode.Uri[] | undefined>();
    this.disposables.push(
      this.tree, this.changed, decorations, this.follower,
      // 框架调用的文字置灰：TreeItem 带 resourceUri，再用 FileDecoration 给它上 disabledForeground
      vscode.window.registerFileDecorationProvider({
        onDidChangeFileDecorations: decorations.event,
        provideFileDecoration: (uri) => uri.scheme === DecorationScheme && uri.path.startsWith("/external")
          ? new vscode.FileDecoration(undefined, "框架 / 第三方调用", new vscode.ThemeColor("disabledForeground"))
          : undefined,
      }),
      this.tree.onDidChangeVisibility((e) => this.follower.setActive(e.visible, currentPos())),
      vscode.commands.registerCommand("codeatlas.outline.pin", () => this.setPinned(true)),
      vscode.commands.registerCommand("codeatlas.outline.unpin", () => this.setPinned(false)),
      vscode.commands.registerCommand("codeatlas.outline.showExternals", () => this.setExternals(true)),
      vscode.commands.registerCommand("codeatlas.outline.hideExternals", () => this.setExternals(false)),
      vscode.commands.registerCommand("codeatlas.outline.refresh", () => void this.refresh()),
      vscode.commands.registerCommand("codeatlas.outline.openInPanel", () => void this.openInPanel()),
      vscode.commands.registerCommand("codeatlas.outline.openRelations", () => void this.openInPanel("relations")),
      vscode.commands.registerCommand("codeatlas.outline.open", (node?: OutlineNode) => { if (node) void this.jump(clickTarget(node), true); }),
      vscode.commands.registerCommand("codeatlas.outline.goToDefinition", (node?: OutlineNode) => { if (node) void this.jump(definitionTarget(node), false); }),
    );
    void this.setState("noMethod");
    this.syncPinned();
    void vscode.commands.executeCommand("setContext", "codeatlas.outline.externals", this.externals);
    if (this.tree.visible) this.follower.setActive(true, currentPos());
  }

  // ---------- TreeDataProvider ----------

  getTreeItem(node: OutlineNode): vscode.TreeItem {
    const spec = itemSpec(node);
    const state = spec.collapsible === "expanded" ? vscode.TreeItemCollapsibleState.Expanded
      : spec.collapsible === "collapsed" ? vscode.TreeItemCollapsibleState.Collapsed
        : vscode.TreeItemCollapsibleState.None;
    const item = new vscode.TreeItem(spec.label, state);
    item.description = spec.description;
    item.tooltip = spec.tooltip;
    item.iconPath = new vscode.ThemeIcon(spec.icon, spec.iconColor ? new vscode.ThemeColor(spec.iconColor) : undefined);
    item.contextValue = spec.contextValue;
    if (spec.dim && node.type === "step") item.resourceUri = vscode.Uri.from({ scheme: DecorationScheme, path: `/external/${node.step.order}` });
    if (spec.clickable) item.command = { command: "codeatlas.outline.open", title: "跳到调用处", arguments: [node] };
    return item;
  }

  async getChildren(node?: OutlineNode): Promise<OutlineNode[]> {
    if (!node) return this.root && this.state === "method" ? [this.root] : [];
    if (node.type === "root") return this.load(node.symbol.id, node.repoRoot, rootAncestors(node));
    if (node.type === "step") {
      const source = childSource(node);
      if (source === "inPlace") return inPlaceChildren(node);
      if (source === "fetch" && node.step.target && this.root) return this.load(node.step.target.id, this.root.repoRoot, expandedAncestors(node));
    }
    return [];
  }

  private async load(symbolId: number, repoRoot: string, ancestors: ReadonlySet<number>): Promise<OutlineNode[]> {
    try {
      let p = this.cache.get(symbolId);
      if (!p) {
        const cache = this.cache;
        const fresh = this.fetchOutline(repoRoot, symbolId);
        p = fresh;
        cache.set(symbolId, fresh);
        // 失败的不缓存，再展开会重试（缓存期间被整个换掉了就不用管）
        fresh.catch(() => { if (cache.get(symbolId) === fresh) cache.delete(symbolId); });
      }
      const outline = await p;
      if (!outline) return [{ type: "info", text: "取不到这个方法的大纲", tooltip: "符号不在索引里了（重新索引过？），或服务版本过旧还没有 outline 接口。", icon: "warning" }];
      return outlineChildren(outline, ancestors);
    } catch (e) {
      this.host.log(`调用大纲加载失败：${e}`);
      return [{ type: "info", text: "加载失败，点刷新重试", tooltip: String(e), icon: "error" }];
    }
  }

  private async fetchOutline(repoRoot: string, symbolId: number): Promise<OutlineDto | null> {
    const client = await this.host.connect(repoRoot);
    if (!client) throw new Error("CodeTrail Graph 服务不可用");
    return client.outline(symbolId, { externals: this.externals, tests: false });
  }

  // ---------- 跟随 ----------

  private apply(r: FollowResult): void {
    if (r.state === "method") {
      this.root = { type: "root", symbol: r.symbol, repoRoot: r.repoRoot };
      this.cache = new Map();
    } else {
      this.root = null;
      if (r.state === "error") this.host.log(`调用大纲：${r.message}`);
    }
    void this.setState(r.state);
    this.changed.fire(undefined);
  }

  private async setState(state: OutlineState): Promise<void> {
    this.state = state;
    await vscode.commands.executeCommand("setContext", "codeatlas.outline.state", state);
  }

  // ---------- 命令 ----------

  private setPinned(pinned: boolean): void {
    this.follower.setPinned(pinned, currentPos());
    this.syncPinned();
  }

  private syncPinned(): void {
    void vscode.commands.executeCommand("setContext", "codeatlas.outline.pinned", this.follower.pinned);
    const tags = [this.follower.pinned ? "已固定" : "", this.externals ? "" : "不含框架调用"].filter(Boolean);
    this.tree.description = tags.length ? tags.join(" · ") : undefined;
  }

  private setExternals(on: boolean): void {
    this.externals = on;
    void this.context.globalState.update(ExternalsStateKey, on);
    void vscode.commands.executeCommand("setContext", "codeatlas.outline.externals", on);
    this.syncPinned();
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
    // 固定时（或光标不在支持的文件里）只重取当前根的大纲
    this.changed.fire(undefined);
  }

  indexChanged(): void {
    this.cache = new Map();
    // 固定时根不变，只重取；没固定的由共享来源重新解析光标后回调 apply
    if (this.follower.pinned) this.changed.fire(undefined);
  }

  private async openInPanel(how: "graph" | "relations" = "graph"): Promise<void> {
    if (!this.root) {
      void vscode.window.showInformationMessage("调用大纲里还没有方法：把光标放进一个方法。");
      return;
    }
    await this.host.openPanel(this.root.repoRoot, this.root.symbol.id, how);
  }

  private async jump(target: JumpTarget | null, preserveFocus: boolean): Promise<void> {
    if (this.root) await this.follow.jump(this.root.repoRoot, target, preserveFocus);
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }
}
