/**
 * 把一连串"文件保存"事件防抖成一次索引；索引进行中再有变更则置脏，结束后再跑一次。
 * 不依赖 vscode，便于用假计时器测试。
 */
export class IndexScheduler {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private dirty = false;
  private current: Promise<void> | null = null;

  constructor(
    private readonly run: () => Promise<void>,
    private readonly delayMs: number,
    private readonly onError: (e: unknown) => void = () => {},
  ) {}

  get running(): boolean {
    return this.current !== null;
  }

  notifyChange(): void {
    if (this.current) { this.dirty = true; return; }
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.timer = null; void this.execute(); }, this.delayMs);
  }

  /** 立即执行（例如用户点了"索引"按钮）；若正在跑则置脏并等待当前这次。 */
  runNow(): Promise<void> {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    if (this.current) { this.dirty = true; return this.current; }
    return this.execute();
  }

  private async execute(): Promise<void> {
    this.dirty = false;
    this.current = (async () => {
      try { await this.run(); }
      catch (e) { this.onError(e); }
    })();
    try { await this.current; }
    finally {
      this.current = null;
      if (this.dirty) void this.execute();
    }
  }
}
