import { PassThrough } from "node:stream";
import { describe, expect, it } from "vitest";
import { aiArgs, ApiKeyEnvVar, buildServeLaunch, parsePortLine, redactSecrets, waitForPortLine, type AiOptions } from "../src/serverManager";

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
