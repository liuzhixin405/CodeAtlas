import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import ts from "typescript";
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

  it("queues full ahead of incremental and waits until the queued run completes", async () => {
    const runs: boolean[] = [];
    const releases: (() => void)[] = [];
    const scheduler = new IndexScheduler((full: boolean) => {
      runs.push(full);
      return new Promise<void>(resolve => releases.push(resolve));
    }, 100);
    const initial = scheduler.runNow();
    await vi.advanceTimersByTimeAsync(0);
    scheduler.notifyChange();
    let finished = false;
    const full = scheduler.runNow(true).then(() => { finished = true; });
    releases[0]!();
    await vi.advanceTimersByTimeAsync(0);
    expect(runs).toEqual([false, true]);
    expect(finished).toBe(false);
    releases[1]!();
    await Promise.all([initial, full]);
    expect(scheduler.running).toBe(false);
  });
});

it("retries an index demand after the server's busy run has finished", async () => {
  const source = ts.createSourceFile("extension.ts", readFileSync(new URL("../src/extension.ts", import.meta.url), "utf8"), ts.ScriptTarget.Latest, true);
  const declaration = source.statements.filter(ts.isFunctionDeclaration).find(node => node.name?.text === "doIndex")!;
  const requests: boolean[] = [];
  let waits = 0;
  const client = {
    startIndex: async (full: boolean) => { requests.push(full); return requests.length === 1 ? "busy" : "started"; },
    waitForIndex: async () => { waits++; return { error: null, lastReport: null }; },
  };
  const deps = {
    withServer: async (_root: string, action: (value: typeof client) => Promise<unknown>) => action(client),
    log: () => {}, status: { text: "" }, ensureServer: async () => null,
    panel: { indexChanged: () => {} }, outline: null, impact: null, follow: null, lens: null,
  };
  const compiled = ts.transpileModule(declaration.getText(source), { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;
  const run = new Function(...Object.keys(deps), compiled + "\nreturn doIndex;")(...Object.values(deps)) as (root: string, full: boolean) => Promise<void>;
  await run("repo", true);
  expect(requests).toEqual([true, true]);
  expect(waits).toBe(2);
});
