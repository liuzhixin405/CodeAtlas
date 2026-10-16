/**
 * 面板的后退 / 前进（工具栏 ← →、Alt+← / Alt+→、鼠标侧键）：调用图换焦点、模块关系换中心、项目地图里切视图 / 钻进容器 / 选入口……
 * 每去一个地方记一条（key 标识"那个画面"，restore 把它重新画出来）。各模式自己的面包屑照旧，这里是跨模式的一条时间线。
 *
 * - 记录时：当前这条之后的"前进"分支丢掉；与当前这条 key 相同时只替换（同一画面刷新 / 重复点不算一步）。
 * - 后退 / 前进时执行目标的 restore；restore 期间（可能是异步的）发生的记录一律忽略，免得恢复动作把时间线改掉。
 * - 最多 MaxNavEntries 条，超出丢最老的。
 */
export interface NavEntry {
  key: string;
  /** 按钮提示里的"后退到：…"。 */
  label: string;
  restore: () => Promise<void> | void;
}

export const MaxNavEntries = 100;

export class NavHistory {
  private entries: NavEntry[] = [];
  private index = -1;
  private restoring = 0;

  constructor(private readonly onChange: () => void = () => {}) {}

  get canBack(): boolean { return this.index > 0; }
  get canForward(): boolean { return this.index >= 0 && this.index < this.entries.length - 1; }
  get current(): NavEntry | null { return this.entries[this.index] ?? null; }
  /** 后退会去的那一条；没有为 null。 */
  get backTarget(): NavEntry | null { return this.canBack ? this.entries[this.index - 1]! : null; }
  get forwardTarget(): NavEntry | null { return this.canForward ? this.entries[this.index + 1]! : null; }
  get isRestoring(): boolean { return this.restoring > 0; }
  get length(): number { return this.entries.length; }

  record(entry: NavEntry): void {
    if (this.restoring > 0) return;
    const cur = this.entries[this.index];
    if (cur && cur.key === entry.key) { this.entries[this.index] = entry; this.onChange(); return; }
    this.entries = this.entries.slice(0, this.index + 1);
    this.entries.push(entry);
    if (this.entries.length > MaxNavEntries) this.entries.splice(0, this.entries.length - MaxNavEntries);
    this.index = this.entries.length - 1;
    this.onChange();
  }

  async back(): Promise<boolean> {
    if (!this.canBack) return false;
    this.index--;
    await this.run(this.entries[this.index]!);
    return true;
  }

  async forward(): Promise<boolean> {
    if (!this.canForward) return false;
    this.index++;
    await this.run(this.entries[this.index]!);
    return true;
  }

  /** 切仓库：时间线清空。 */
  reset(): void {
    this.entries = [];
    this.index = -1;
    this.onChange();
  }

  private async run(e: NavEntry): Promise<void> {
    this.restoring++;
    this.onChange();
    try { await e.restore(); }
    finally { this.restoring--; this.onChange(); }
  }
}

/** 按钮提示："后退到：OrderService.Place（Alt+←）"；没有可去的写"没有可后退的"。 */
export function navTitle(direction: "back" | "forward", target: NavEntry | null): string {
  const key = direction === "back" ? "Alt+←" : "Alt+→";
  if (!target) return direction === "back" ? `没有可后退的（${key}）` : `没有可前进的（${key}）`;
  return `${direction === "back" ? "后退到" : "前进到"}：${target.label}（${key}）`;
}
