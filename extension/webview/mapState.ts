/** 项目地图的钻取路径：根 → 子命名空间/目录 → … 纯状态，便于测试。 */
export class MapState {
  private path: string[] = [];

  /** 当前 root；空串是顶层。 */
  get root(): string {
    return this.path[this.path.length - 1] ?? "";
  }

  /** 面包屑：顶层 + 每一级。 */
  get crumbs(): { label: string; root: string }[] {
    const list = [{ label: "项目", root: "" }];
    for (const p of this.path) list.push({ label: lastSegment(p), root: p });
    return list;
  }

  drill(key: string): void {
    this.path.push(key);
  }

  goTo(root: string): void {
    if (root === "") { this.path = []; return; }
    const i = this.path.indexOf(root);
    if (i >= 0) this.path = this.path.slice(0, i + 1);
    else this.path = [root];
  }

  /** 服务端自动推导出的 root（如 Acme.App.Modules）：按分隔符拆成逐级路径，面包屑每一级都能点。 */
  goToPath(root: string): void {
    this.path = [];
    if (root === "") return;
    const re = /[./]/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(root)) !== null) this.path.push(root.slice(0, m.index));
    if (this.path[this.path.length - 1] !== root) this.path.push(root);
  }

  up(): void {
    this.path.pop();
  }

  reset(): void {
    this.path = [];
  }
}

export function lastSegment(key: string): string {
  const i = Math.max(key.lastIndexOf("/"), key.lastIndexOf("."));
  return i < 0 ? key : key.slice(i + 1);
}

// ---------- 项目地图顶层视图的选择（记在本机）----------

/** 顶层切换「容器 | 框架流程 | 命名空间」用户上次点的哪个。组件矩阵 / 入口流程是容器下面的子视图，不单独记。 */
export type MapViewChoice = "arch" | "framework" | "ns";

export const MapViewKey = "codeatlas.mapView";

/**
 * 读回存的值。旧版本的「管道」视图（"pipeline"）已换成框架流程，读到时映射为 "framework"；
 * 不认识的值（含空）返回 null，由第一屏的默认规则决定。
 */
export function parseMapViewChoice(v: string | null | undefined): MapViewChoice | null {
  if (v === "pipeline") return "framework";
  return v === "arch" || v === "framework" || v === "ns" ? v : null;
}

export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

/** webview 的 localStorage 可能不可用（访问就抛错），读不到返回 null。旧值 "pipeline" 读到时顺手改写成 "framework"。 */
export function loadMapViewChoice(storage: () => StorageLike | null): MapViewChoice | null {
  try {
    const s = storage();
    const raw = s?.getItem(MapViewKey) ?? null;
    const v = parseMapViewChoice(raw);
    if (v !== null && raw !== v) s?.setItem(MapViewKey, v);
    return v;
  } catch {
    return null;
  }
}

export function saveMapViewChoice(storage: () => StorageLike | null, v: MapViewChoice): void {
  try { storage()?.setItem(MapViewKey, v); } catch { /* 不记也能用 */ }
}
