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
    this.goToPathFrom("", root);
  }

  /**
   * 以 floor 为底展开到 root：floor 本身是第一级，再往上的层级不出现在面包屑里。
   * 管道视图用：floor 是自动算出的业务模块层，它上面只是项目 / 类库名，对管道没有意义。
   * root 不在 floor 之下时退化为 goToPath(root)。
   */
  goToPathFrom(floor: string, root: string): void {
    this.path = [];
    if (root === "") return;
    const under = floor !== "" && (root === floor || root.startsWith(floor) && /[./]/.test(root[floor.length] ?? ""));
    const start = under ? floor.length + 1 : 0;
    if (under) this.path.push(floor);
    const re = /[./]/g;
    re.lastIndex = start;
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
