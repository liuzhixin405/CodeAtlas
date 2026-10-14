import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { IndexScheduler } from "../src/indexScheduler";

describe("IndexScheduler", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("debounces a burst of changes into one run", async () => {
    const runs: number[] = [];
    const s = new IndexScheduler(async () => { runs.push(Date.now()); }, 1500);
    for (let i = 0; i < 10; i++) { s.notifyChange(); await vi.advanceTimersByTimeAsync(100); }
    expect(runs).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1500);
    expect(runs).toHaveLength(1);
  });

  it("re-runs once after the current run when changes arrive while running", async () => {
    let release!: () => void;
    let runs = 0;
    const s = new IndexScheduler(() => { runs++; return new Promise<void>((r) => { release = r; }); }, 100);
    s.notifyChange();
    await vi.advanceTimersByTimeAsync(100);
    expect(runs).toBe(1);
    expect(s.running).toBe(true);

    s.notifyChange();
    s.notifyChange();
    await vi.advanceTimersByTimeAsync(500);
    expect(runs).toBe(1); // 运行中不并发

    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(runs).toBe(2); // 脏标记导致结束后立刻再跑一次
    release();
    await vi.advanceTimersByTimeAsync(1000);
    expect(runs).toBe(2);
  });

  it("runNow executes immediately and cancels a pending debounce", async () => {
    let runs = 0;
    const s = new IndexScheduler(async () => { runs++; }, 1500);
    s.notifyChange();
    await s.runNow();
    expect(runs).toBe(1);
    await vi.advanceTimersByTimeAsync(2000);
    expect(runs).toBe(1);
  });

  it("reports errors through onError instead of throwing", async () => {
    const errors: string[] = [];
    const s = new IndexScheduler(async () => { throw new Error("boom"); }, 10, (e) => errors.push(String(e)));
    s.notifyChange();
    await vi.advanceTimersByTimeAsync(10);
    expect(errors).toEqual(["Error: boom"]);
    expect(s.running).toBe(false);
  });
});
