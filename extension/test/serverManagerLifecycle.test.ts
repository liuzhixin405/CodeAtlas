import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { findServerExecutable, ServerManager, ServerNotFoundError, type ProcessLike } from "../src/serverManager";

class FakeProcess implements ProcessLike {
  static spawned = 0;
  alive = false;
  apiBase: string | null = null;
  private resolveStart!: (v: string) => void;
  readonly started: Promise<string>;
  constructor(readonly exe: string, readonly repoRoot: string) {
    FakeProcess.spawned++;
    this.started = new Promise<string>((r) => { this.resolveStart = r; });
  }
  start(): Promise<string> {
    this.alive = true;
    return this.started.then((base) => { this.apiBase = base; return base; });
  }
  finishStart(port: number): void { this.resolveStart(`http://127.0.0.1:${port}`); }
  die(): void { this.alive = false; this.apiBase = null; }
  stop(): void { this.alive = false; this.apiBase = null; }
}

const log = { info: () => {}, error: () => {} };

describe("ServerManager lifecycle", () => {
  afterEach(() => { FakeProcess.spawned = 0; });

  it("concurrent ensure() for the same repo spawns exactly one process", async () => {
    const procs: FakeProcess[] = [];
    const m = new ServerManager(() => "codeatlas.exe", log, (exe, root) => { const p = new FakeProcess(exe, root); procs.push(p); return p; });
    const a = m.ensure("D:/repo");
    const b = m.ensure("D:/repo");
    expect(FakeProcess.spawned).toBe(1);
    procs[0]!.finishStart(1234);
    expect(await a).toBe("http://127.0.0.1:1234");
    expect(await b).toBe("http://127.0.0.1:1234");
    expect(m.has("D:/repo")).toBe(true);
    expect(m.has("D:/other")).toBe(false);
  });

  it("recover() restarts once per repo, then refuses until restart() resets the budget", async () => {
    const procs: FakeProcess[] = [];
    const m = new ServerManager(() => "codeatlas.exe", log, (exe, root) => { const p = new FakeProcess(exe, root); procs.push(p); return p; });
    const first = m.ensure("D:/repo"); procs[0]!.finishStart(1); await first;

    procs[0]!.die();
    const second = m.recover("D:/repo"); procs[1]!.finishStart(2);
    expect(await second).toBe("http://127.0.0.1:2");

    procs[1]!.die();
    await expect(m.recover("D:/repo")).rejects.toThrow(/已重启过一次/);
    expect(FakeProcess.spawned).toBe(2);

    const third = m.restart("D:/repo"); procs[2]!.finishStart(3);
    expect(await third).toBe("http://127.0.0.1:3");
  });

  it("ensure() throws ServerNotFoundError when no executable can be found and spawns nothing", async () => {
    const m = new ServerManager(() => null, log, () => { throw new Error("should not spawn"); });
    await expect(m.ensure("D:/repo")).rejects.toBeInstanceOf(ServerNotFoundError);
  });
});

describe("findServerExecutable", () => {
  const dirs: string[] = [];
  const mk = (name: string, withExe: boolean) => {
    const d = mkdtempSync(path.join(tmpdir(), `ca-${name}-`));
    dirs.push(d);
    if (withExe) writeFileSync(path.join(d, process.platform === "win32" ? "codeatlas.exe" : "codeatlas"), "");
    return d;
  };
  afterEach(() => { for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true }); });

  it("prefers bundled, then configured, then PATH", () => {
    const bundled = mk("bundled", true);
    const configured = path.join(mk("configured", true), process.platform === "win32" ? "codeatlas.exe" : "codeatlas");
    const onPath = mk("path", true);
    expect(findServerExecutable(bundled, configured, onPath)).toBe(path.join(bundled, path.basename(configured)));
    expect(findServerExecutable(mk("empty", false), configured, onPath)).toBe(configured);
    expect(findServerExecutable(null, "", onPath)).toBe(path.join(onPath, path.basename(configured)));
    expect(findServerExecutable(null, null, mk("nothing", false))).toBeNull();
  });
});
