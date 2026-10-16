/**
 * 把一连串"文件保存"事件防抖成一次索引；索引进行中再有变更则置脏，结束后再跑一次。
 * 不依赖 vscode，便于用假计时器测试。
 */
export class IndexScheduler {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private dirty = false;
  private fullRequested = false;
  private current: Promise<void> | null = null;

  constructor(
    private readonly run: (full: boolean) => Promise<void>,
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

  runNow(full = false): Promise<void> {
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    this.fullRequested ||= full;
    if (this.current) { this.dirty = true; return this.current; }
    return this.execute();
  }

  private execute(): Promise<void> {
    this.current = Promise.resolve().then(async () => {
      do {
        const full = this.fullRequested;
        this.fullRequested = false;
        this.dirty = false;
        try { await this.run(full); }
        catch (e) { this.onError(e); }
      } while (this.dirty);
    }).finally(() => {
      this.current = null;
    });
    return this.current;
  }
}
