import { PassThrough } from "node:stream";
import { describe, expect, it } from "vitest";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import * as path from "node:path";
import { aiArgs, ApiKeyEnvVar, buildServeLaunch, currentTarget, ensureExecutable, packageTargetMismatch, parsePortLine, projectArgs, redactSecrets, resolveProjects, ServerManager, waitForPortLine, type AiOptions, type ProcessLike } from "../src/serverManager";

describe("aiArgs", () => {
  it("turns codeatlas.ai.* into serve flags", () => {
    expect(aiArgs(null)).toEqual([]);
    expect(aiArgs({ enabled: false, endpoint: "http://127.0.0.1:8080/v1", model: "qwen3.6-27b" })).toEqual(["--no-ai"]);
    expect(aiArgs({ enabled: true, endpoint: " http://127.0.0.1:8080/v1 ", model: "qwen3.6-27b" })).toEqual(["--ai-endpoint", "http://127.0.0.1:8080/v1", "--ai-model", "qwen3.6-27b"]);
    // 空值不传，让服务用自己的默认
    expect(aiArgs({ enabled: true, endpoint: "", model: "  " })).toEqual([]);
  });

  it("adds provider / concurrency / timeout in a fixed order", () => {
    const ai: AiOptions = { enabled: true, provider: "anthropic", endpoint: "https://api.anthropic.com/v1", model: "claude-sonnet-5-5", concurrency: 3, timeoutSeconds: 60, includeSource: true };
    expect(aiArgs(ai)).toEqual([
      "--ai-provider", "anthropic",
      "--ai-endpoint", "https://api.anthropic.com/v1",
      "--ai-model", "claude-sonnet-5-5",
      "--ai-concurrency", "3",
      "--ai-timeout", "60",
    ]);
  });

  it("includeSource=false adds --ai-no-source; undefined does not", () => {
    const base: AiOptions = { enabled: true, endpoint: "https://api.deepseek.com/v1", model: "deepseek-chat" };
    expect(aiArgs({ ...base, includeSource: false })).toContain("--ai-no-source");
    expect(aiArgs({ ...base, includeSource: true })).not.toContain("--ai-no-source");
    expect(aiArgs(base)).not.toContain("--ai-no-source");
  });

  it("normalizes odd values: unknown provider → openai, concurrency clamped to 1–8, timeout rounded, non-positive timeout dropped", () => {
    const base: AiOptions = { enabled: true, endpoint: "", model: "" };
    expect(aiArgs({ ...base, provider: "gemini" })).toEqual(["--ai-provider", "openai"]);
    expect(aiArgs({ ...base, concurrency: 0 })).toEqual(["--ai-concurrency", "1"]);
    expect(aiArgs({ ...base, concurrency: 99 })).toEqual(["--ai-concurrency", "8"]);
    expect(aiArgs({ ...base, concurrency: 2.7 })).toEqual(["--ai-concurrency", "2"]);
    expect(aiArgs({ ...base, concurrency: Number.NaN })).toEqual([]);
    expect(aiArgs({ ...base, timeoutSeconds: 59.6 })).toEqual(["--ai-timeout", "60"]);
    expect(aiArgs({ ...base, timeoutSeconds: 0 })).toEqual([]);
  });

  it("disabled AI ignores every other setting", () => {
    expect(aiArgs({ enabled: false, provider: "anthropic", endpoint: "https://api.anthropic.com/v1", model: "m", concurrency: 4, timeoutSeconds: 60, includeSource: false })).toEqual(["--no-ai"]);
  });
});

describe("buildServeLaunch", () => {
  const key = "sk-test-0123456789abcdef";
  const token = "tok-abcdef0123456789";
  const remote: AiOptions = { enabled: true, provider: "openai", endpoint: "https://api.deepseek.com/v1", model: "deepseek-chat", concurrency: 2, timeoutSeconds: 60, includeSource: false };

  it("puts the API key only into the child env, never into args", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, key, { PATH: "C:/bin" });
    expect(l.env[ApiKeyEnvVar]).toBe(key);
    expect(l.env["PATH"]).toBe("C:/bin");
    expect(l.args.join(" ")).not.toContain(key);
    expect(l.args).toEqual([
      "serve", "D:/repo", "--port", "0", "--parent-pid", "42", "--token", token,
      "--ai-provider", "openai", "--ai-endpoint", "https://api.deepseek.com/v1", "--ai-model", "deepseek-chat",
      "--ai-concurrency", "2", "--ai-timeout", "60", "--ai-no-source",
    ]);
  });

  it("log line masks token and key and says how the key was passed", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, key, {});
    expect(l.logLine).not.toContain(key);
    expect(l.logLine).not.toContain(token);
    expect(l.logLine).toContain("--token ***");
    expect(l.logLine).toContain("--ai-no-source");
    expect(l.logLine).toContain(`已设置，经环境变量 ${ApiKeyEnvVar} 传入`);
  });

  it("masks the key even if it leaked into another setting (e.g. pasted into model)", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, { ...remote, model: key }, key, {});
    expect(l.logLine).not.toContain(key);
  });

  it("without a stored key keeps an external CODEATLAS_AI_API_KEY (CI usage) and does not log it", () => {
    const external = "sk-external-9876543210";
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, undefined, { [ApiKeyEnvVar]: external, PATH: "x" });
    expect(l.env[ApiKeyEnvVar]).toBe(external);
    expect(l.logLine).not.toContain(external);
    expect(l.logLine).toContain("沿用外部环境变量");
  });

  it("without any key sets nothing; blank key counts as none", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, "   ", { PATH: "x" });
    expect(ApiKeyEnvVar in l.env).toBe(false);
    expect(l.logLine).toContain("API Key：未设置");
  });

  it("stored key wins over an external one", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, key, { [ApiKeyEnvVar]: "sk-external-9876543210" });
    expect(l.env[ApiKeyEnvVar]).toBe(key);
  });

  it("disabled AI does not inject the key and does not mention it", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, { ...remote, enabled: false }, key, {});
    expect(ApiKeyEnvVar in l.env).toBe(false);
    expect(l.args).toContain("--no-ai");
    expect(l.logLine).not.toContain("API Key");
  });

  it("does not mutate the base env", () => {
    const base = { PATH: "x" };
    buildServeLaunch("codeatlas.exe", "D:/repo", token, 42, remote, key, base);
    expect(base).toEqual({ PATH: "x" });
  });
});

describe("resolveProjects", () => {
  it("other workspace folders first, then codeatlas.projects resolved against the primary folder", () => {
    expect(resolveProjects("D:\\a\\ccs-ui", ["D:\\a\\backend"], ["../shared", "E:\\libs\\pay"], "win32")).toEqual([
      "D:\\a\\backend", "D:\\a\\shared", "E:\\libs\\pay",
    ]);
  });

  it("drops blanks, the primary itself and duplicates (case, slash direction and trailing separator ignored on Windows)", () => {
    expect(resolveProjects("D:\\a\\ui", ["d:/A/backend/", "D:\\a\\ui"], ["  ", "../backend", "D:\\A\\BACKEND", ".", "..\\ui\\"], "win32")).toEqual(["d:\\A\\backend"]);
  });

  it("is case-sensitive on Linux", () => {
    expect(resolveProjects("/w/ui", [], ["../Api", "../api", "/w/api/"], "linux")).toEqual(["/w/Api", "/w/api"]);
  });
});

describe("projectArgs", () => {
  it("one --project pair per root, --no-nested only when nested repos are off, nothing for null", () => {
    expect(projectArgs(null)).toEqual([]);
    expect(projectArgs({ projects: [], includeNested: true })).toEqual([]);
    expect(projectArgs({ projects: ["D:\\a\\backend", "D:\\a\\shared"], includeNested: false })).toEqual([
      "--project", "D:\\a\\backend", "--project", "D:\\a\\shared", "--no-nested",
    ]);
  });
});

describe("buildServeLaunch with projects", () => {
  it("puts project flags before the AI flags and shows them in the log line", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/a/ccs-ui", "tok-abcdef0123456789", 7, { enabled: false, endpoint: "", model: "" }, undefined, {},
      { projects: ["D:/a/backend"], includeNested: false });
    expect(l.args).toEqual([
      "serve", "D:/a/ccs-ui", "--port", "0", "--parent-pid", "7", "--token", "tok-abcdef0123456789",
      "--project", "D:/a/backend", "--no-nested", "--no-ai",
    ]);
    expect(l.logLine).toContain("--project D:/a/backend --no-nested --no-ai");
  });

  it("without project options the args are unchanged (old callers)", () => {
    const l = buildServeLaunch("codeatlas.exe", "D:/repo", "tok-abcdef0123456789", 7, null, undefined, {});
    expect(l.args).toEqual(["serve", "D:/repo", "--port", "0", "--parent-pid", "7", "--token", "tok-abcdef0123456789"]);
  });
});

describe("ServerManager.stop", () => {
  it("stops only that repo's process; the next ensure() spawns a fresh one", async () => {
    const spawned: { root: string; stopped: boolean }[] = [];
    const m = new ServerManager(() => "codeatlas.exe", { info: () => {}, error: () => {} }, (_exe, root) => {
      const rec = { root, stopped: false };
      spawned.push(rec);
      const p: ProcessLike = {
        get alive() { return !rec.stopped; },
        get apiBase() { return rec.stopped ? null : `http://127.0.0.1:${spawned.indexOf(rec) + 1}`; },
        start: () => Promise.resolve(`http://127.0.0.1:${spawned.indexOf(rec) + 1}`),
        stop: () => { rec.stopped = true; },
      };
      return p;
    });
    await m.ensure("D:/a/old-primary");
    await m.ensure("D:/a/other");
    m.stop("D:/a/old-primary");
    expect(spawned.map((s) => [s.root, s.stopped])).toEqual([["D:/a/old-primary", true], ["D:/a/other", false]]);
    expect(m.repos()).toEqual(["D:/a/other"]);
    expect(await m.ensure("D:/a/old-primary")).toBe("http://127.0.0.1:3");
  });
});

describe("redactSecrets", () => {
  it("replaces every occurrence; ignores empty and very short values", () => {
    expect(redactSecrets("a sk-12345678 b sk-12345678", ["sk-12345678"])).toBe("a *** b ***");
    expect(redactSecrets("abc", [undefined, "", "ab"])).toBe("abc");
  });
});

describe("parsePortLine", () => {
  it("reads the port from the JSON line the server prints", () => {
    expect(parsePortLine('{"port":62448}')).toBe(62448);
    expect(parsePortLine('  {"port":80}\r')).toBe(80);
  });

  it("returns null for anything else", () => {
    expect(parsePortLine("")).toBeNull();
    expect(parsePortLine("提示：建议把 .codeatlas/ 加入 .gitignore")).toBeNull();
    expect(parsePortLine('{"other":1}')).toBeNull();
    expect(parsePortLine('{"port":"abc"}')).toBeNull();
  });
});

describe("waitForPortLine", () => {
  it("resolves with the port when the first matching line arrives, even split across chunks", async () => {
    const s = new PassThrough();
    const p = waitForPortLine(s, 1000);
    s.write('{"po');
    s.write('rt":5173}\nignored\n');
    await expect(p).resolves.toBe(5173);
  });

  it("skips non-port lines before the port line", async () => {
    const s = new PassThrough();
    const p = waitForPortLine(s, 1000);
    s.write("warming up\n{\"port\":9}\n");
    await expect(p).resolves.toBe(9);
  });

  it("rejects on timeout", async () => {
    const s = new PassThrough();
    await expect(waitForPortLine(s, 30)).rejects.toThrow(/超时/);
  });

  it("rejects when the stream ends without a port line", async () => {
    const s = new PassThrough();
    const p = waitForPortLine(s, 1000);
    s.end("nothing useful\n");
    await expect(p).rejects.toThrow(/结束/);
  });
});

describe("ensureExecutable", () => {
  it("does nothing on Windows and never throws for a missing file", () => {
    // 真正的 chmod 只有 Linux / macOS 上有意义，在 Linux 容器里实测（见 docs/acceptance.md）
    expect(ensureExecutable("C:/nope/codeatlas.exe", "win32")).toBe(false);
    expect(ensureExecutable("/definitely/missing/codeatlas", "linux")).toBe(false);
  });
});

describe("packageTargetMismatch", () => {
  const dir = mkdtempSync(path.join(tmpdir(), "ca-target-"));
  it("names the package to install when the bundled server is for another platform", () => {
    writeFileSync(path.join(dir, "target.json"), JSON.stringify({ target: "darwin-x64" }));
    // 用户实测：Windows 上装了 darwin-x64 的包，原来只报"找不到路径"
    const msg = packageTargetMismatch(dir, "0.1.0", "win32", "x64");
    expect(msg).toContain("macOS（Intel 芯片）（darwin-x64）");
    expect(msg).toContain("Windows x64（win32-x64）");
    expect(msg).toContain("codetrail-graph-win32-x64-0.1.0.vsix");
  });
  it("is null when the platform matches or there is no target.json (dev build)", () => {
    writeFileSync(path.join(dir, "target.json"), JSON.stringify({ target: "linux-arm64" }));
    expect(packageTargetMismatch(dir, "0.1.0", "linux", "arm64")).toBeNull();
    expect(currentTarget("darwin", "arm64")).toBe("darwin-arm64");
    expect(packageTargetMismatch(path.join(dir, "missing"), "0.1.0", "win32", "x64")).toBeNull();
  });
});
