import { describe, expect, it } from "vitest";
import {
  ApiKeyHint, describeProvenance, describeQueue, describeService, describeStatus, evidenceKind, evidenceLabel, generateButtonState, hasContent,
  isAuthError, missingApiKey, needsApiKeyAction, parseEdgeEvidence, serviceTooltip, staleBanner,
} from "../webview/narrationCard";
import { DefaultPreset, isKnownPreset, LayoutPresets, presetLabel } from "../webview/presets";
import type { NarrationStatusDto, NarrativeDto } from "../webview/types";

function dto(over: Partial<NarrativeDto>): NarrativeDto {
  return { kind: "container", key: "Acme.Api", status: "ready", model: null, generatedAt: null, hash: null, summary: null, keyPoints: [], risks: [], layout: null, error: null, ...over };
}

function status(over: Partial<NarrationStatusDto> = {}): NarrationStatusDto {
  return { enabled: true, endpoint: "http://127.0.0.1:8080/v1", model: "qwen3.6-27b", reachable: true, queued: 0, running: null, done: 0, failed: 0, lastError: null, remoteWarning: null, paused: false, ...over };
}

describe("describeStatus", () => {
  it("404 (no narrative route) reads as an old server", () => {
    expect(describeStatus(null, null)).toBe("服务版本太旧，没有叙述接口；重启服务或更新扩展");
  });

  it("ready shows provenance: model and local time", () => {
    // 用本地时间构造，避免时区把分钟数挪走
    const at = new Date(2026, 9, 14, 9, 5).toISOString();
    expect(describeStatus(dto({ status: "ready", model: "qwen3.6-27b", generatedAt: at }), status())).toBe("由 qwen3.6-27b 生成 · 2026-10-14 09:05");
    expect(describeStatus(dto({ status: "ready" }), null)).toBe("由 未知模型 生成 · 时间未知");
  });

  it("content + pending / running keeps the old text and says 正在更新", () => {
    const old = { summary: "旧摘要", model: "qwen3.6-27b", stale: true, staleReason: "架构事实有变化" };
    expect(describeStatus(dto({ ...old, status: "running" }), status())).toBe("正在更新…");
    expect(describeStatus(dto({ ...old, status: "pending" }), status({ queued: 2 }))).toBe("正在更新…");
    // 只有关键点也算有内容
    expect(describeStatus(dto({ status: "running", keyPoints: [{ text: "x", evidence: [], inferred: false }] }), status())).toBe("正在更新…");
    expect(hasContent(dto({ summary: "旧摘要" }))).toBe(true);
    expect(hasContent(dto({}))).toBe(false);
    expect(hasContent(null)).toBe(false);
  });

  it("pending / running", () => {
    expect(describeStatus(dto({ status: "pending" }), status({ queued: 3 }))).toBe("排队中（队列 3）…");
    expect(describeStatus(dto({ status: "pending" }), null)).toBe("排队中…");
    expect(describeStatus(dto({ status: "running" }), status({ running: "container:Acme.Api" }))).toBe("生成中…");
  });

  it("failed carries the reason", () => {
    expect(describeStatus(dto({ status: "failed", error: "模型 120 秒没回" }), status())).toBe("生成失败：模型 120 秒没回");
    expect(describeStatus(dto({ status: "failed" }), status())).toBe("生成失败：未知原因");
  });

  it("unavailable explains why: disabled → unreachable → server error → generic", () => {
    const u = dto({ status: "unavailable", error: "服务端说的" });
    expect(describeStatus(u, status({ enabled: false, reachable: false }))).toBe("AI 未启用（设置 codeatlas.ai.enabled）");
    expect(describeStatus(u, status({ reachable: false, endpoint: "http://10.0.0.5:8080/v1" }))).toBe("端点连不上：http://10.0.0.5:8080/v1");
    expect(describeStatus(u, status({ reachable: null }))).toBe("服务端说的");
    expect(describeStatus(dto({ status: "unavailable" }), null)).toBe("AI 不可用");
  });
});

describe("describeProvenance", () => {
  const at = new Date(2026, 9, 14, 20, 40).toISOString();

  it("same model: model · time · commit", () => {
    expect(describeProvenance(dto({ model: "qwen3.6-27b", generatedAt: at, gitCommit: "a1b2c3d" }), status())).toBe("由 qwen3.6-27b 生成 · 2026-10-14 20:40 · commit a1b2c3d");
  });

  it("different model names the current service but does not count as stale", () => {
    const d = dto({ model: "qwen3.6-27b", generatedAt: at, gitCommit: "a1b2c3d" });
    expect(describeProvenance(d, status({ model: "deepseek-chat", endpoint: "https://api.deepseek.com/v1" })))
      .toBe("由 qwen3.6-27b 生成 · 2026-10-14 20:40 · commit a1b2c3d（当前服务：deepseek-chat）");
    expect(staleBanner(d)).toBeNull();
  });

  it("no commit (not a git repo) drops the commit part", () => {
    expect(describeProvenance(dto({ model: "qwen3.6-27b", generatedAt: at, gitCommit: null }), status())).toBe("由 qwen3.6-27b 生成 · 2026-10-14 20:40");
  });

  it("old server without the new fields / without status", () => {
    // 老服务：没有 stale / gitCommit 等字段
    expect(describeProvenance(dto({ model: "qwen3.6-27b", generatedAt: at }), status())).toBe("由 qwen3.6-27b 生成 · 2026-10-14 20:40");
    expect(describeProvenance(dto({ model: "qwen3.6-27b", generatedAt: at }), null)).toBe("由 qwen3.6-27b 生成 · 2026-10-14 20:40");
    // 模型未知时不比较
    expect(describeProvenance(dto({ generatedAt: at }), status({ model: "deepseek-chat" }))).toBe("由 未知模型 生成 · 2026-10-14 20:40");
  });
});

describe("staleBanner", () => {
  it("null when not stale or the field is missing", () => {
    expect(staleBanner(dto({ stale: false, staleReason: "x" }))).toBeNull();
    expect(staleBanner(dto({}))).toBeNull();
    expect(staleBanner(null)).toBeNull();
    expect(staleBanner(undefined)).toBeNull();
  });

  it("stale shows the server's reason", () => {
    expect(staleBanner(dto({ stale: true, staleReason: "生成后 3 个相关文件有改动；架构事实有变化", changedFiles: 3 })))
      .toEqual({ text: "可能过期：生成后 3 个相关文件有改动；架构事实有变化" });
  });

  it("empty reason falls back to changed-file count, then to 相关代码有变化", () => {
    expect(staleBanner(dto({ stale: true }))).toEqual({ text: "可能过期：相关代码有变化" });
    expect(staleBanner(dto({ stale: true, staleReason: null }))).toEqual({ text: "可能过期：相关代码有变化" });
    expect(staleBanner(dto({ stale: true, staleReason: "  ", changedFiles: 0 }))).toEqual({ text: "可能过期：相关代码有变化" });
    expect(staleBanner(dto({ stale: true, staleReason: "", changedFiles: 2 }))).toEqual({ text: "可能过期：生成后 2 个相关文件有改动" });
  });

  it("hidden while the update is queued / running (status row says 正在更新)", () => {
    expect(staleBanner(dto({ stale: true, status: "running", summary: "旧摘要" }))).toBeNull();
    expect(staleBanner(dto({ stale: true, status: "pending", summary: "旧摘要" }))).toBeNull();
  });
});

describe("service line and API key hints", () => {
  const deepseek = { provider: "openai", endpoint: "https://api.deepseek.com/v1", model: "deepseek-chat" };

  it("describeService: friendly name for known hosts, 本地 for loopback, host otherwise", () => {
    expect(describeService(null)).toBe("");
    expect(describeService(status(deepseek))).toBe("DeepSeek · deepseek-chat");
    expect(describeService(status({ provider: "anthropic", endpoint: "https://api.anthropic.com/v1", model: "claude-sonnet-5-5" }))).toBe("Anthropic Claude · claude-sonnet-5-5");
    expect(describeService(status({ provider: "openai", endpoint: "https://dashscope.aliyuncs.com/compatible-mode/v1", model: "qwen-plus" }))).toBe("通义千问 · qwen-plus");
    expect(describeService(status())).toBe("本地 · qwen3.6-27b");
    expect(describeService(status({ endpoint: "https://llm.corp.example/v1", model: "x" }))).toBe("llm.corp.example · x");
    // 老服务没有 provider 字段
    expect(describeService(status({ endpoint: "https://api.openai.com/v1", model: "gpt-4o-mini" }))).toBe("OpenAI · gpt-4o-mini");
  });

  it("serviceTooltip names the protocol and full endpoint", () => {
    expect(serviceTooltip(status({ ...deepseek, concurrency: 3, includeSource: false }))).toBe("OpenAI 兼容协议 · https://api.deepseek.com/v1\n并发 3 · 不发送源码\n点击切换 AI 服务");
    expect(serviceTooltip(status({ provider: "anthropic", endpoint: "https://api.anthropic.com/v1" }))).toBe("Anthropic Messages API · https://api.anthropic.com/v1\n点击切换 AI 服务");
  });

  it("missingApiKey only for an enabled remote endpoint whose server reports hasApiKey=false", () => {
    expect(missingApiKey(status({ ...deepseek, hasApiKey: false }))).toBe(true);
    expect(missingApiKey(status({ ...deepseek, hasApiKey: true }))).toBe(false);
    expect(missingApiKey(status({ ...deepseek }))).toBe(false); // 老服务没有字段
    expect(missingApiKey(status({ hasApiKey: false }))).toBe(false); // 本机回环不需要 key
    expect(missingApiKey(status({ ...deepseek, hasApiKey: false, enabled: false }))).toBe(false);
    expect(missingApiKey(null)).toBe(false);
  });

  it("isAuthError spots HTTP 401 / 403 but not other numbers", () => {
    expect(isAuthError("API Key 无效或没有权限（HTTP 401）")).toBe(true);
    expect(isAuthError("HTTP 403 Forbidden")).toBe(true);
    expect(isAuthError("HTTP 500")).toBe(false);
    expect(isAuthError("模型 4010 秒没回")).toBe(false);
    expect(isAuthError(null)).toBe(false);
  });

  it("unavailable without a key shows the command hint; 401/403 errors get the same command appended", () => {
    const u = dto({ status: "unavailable", error: "端点要求 API Key" });
    expect(describeStatus(u, status({ ...deepseek, hasApiKey: false }))).toBe(ApiKeyHint);
    expect(ApiKeyHint).toBe("未设置 API Key（命令面板：CodeTrail Graph: 设置 AI API Key）");
    expect(describeStatus(dto({ status: "unavailable", error: "API Key 无效或没有权限（HTTP 401）" }), status({ ...deepseek, hasApiKey: true })))
      .toBe("API Key 无效或没有权限（HTTP 401）（命令面板：CodeTrail Graph: 设置 AI API Key）");
    expect(describeStatus(dto({ status: "failed", error: "HTTP 403" }), status(deepseek))).toBe("生成失败：HTTP 403（命令面板：CodeTrail Graph: 设置 AI API Key）");
    // 连不上优先于缺 key
    expect(describeStatus(u, status({ ...deepseek, hasApiKey: false, reachable: false }))).toBe("端点连不上：https://api.deepseek.com/v1");
  });

  it("needsApiKeyAction decides whether the status row gets a 设置 API Key button", () => {
    expect(needsApiKeyAction(dto({ status: "unavailable" }), status({ ...deepseek, hasApiKey: false }))).toBe(true);
    expect(needsApiKeyAction(dto({ status: "unavailable", error: "HTTP 401" }), status(deepseek))).toBe(true);
    expect(needsApiKeyAction(dto({ status: "failed", error: "HTTP 403" }), status(deepseek))).toBe(true);
    expect(needsApiKeyAction(dto({ status: "failed", error: "超时" }), status(deepseek))).toBe(false);
    expect(needsApiKeyAction(dto({ status: "ready" }), status({ ...deepseek, hasApiKey: false }))).toBe(false);
    expect(needsApiKeyAction(null, status({ ...deepseek, hasApiKey: false }))).toBe(false);
    expect(needsApiKeyAction(undefined, null)).toBe(false);
  });
});

describe("describeQueue", () => {
  it("joins the non-zero counters", () => {
    expect(describeQueue(null)).toBe("");
    // 服务名单独一行（describeService），这里只说就绪
    expect(describeQueue(status())).toBe("就绪");
    expect(describeQueue(status({ enabled: false }))).toBe("AI 未启用");
    expect(describeQueue(status({ paused: true, queued: 2, running: "Acme.Api|Acme.Orders", done: 5, failed: 1 }))).toBe("已暂停 · 队列 2 · 正在生成 Orders · 完成 5 · 失败 1");
  });
});

describe("generateButtonState", () => {
  it("ready regenerates with force; busy is disabled; 404 hides", () => {
    expect(generateButtonState(dto({ status: "ready" }))).toEqual({ label: "重新生成", force: true, disabled: false, visible: true });
    expect(generateButtonState(dto({ status: "pending" }))).toEqual({ label: "生成中", force: false, disabled: true, visible: true });
    expect(generateButtonState(dto({ status: "running" })).disabled).toBe(true);
    expect(generateButtonState(dto({ status: "failed" }))).toEqual({ label: "生成", force: false, disabled: false, visible: true });
    expect(generateButtonState(dto({ status: "unavailable" })).label).toBe("生成");
    expect(generateButtonState(null).visible).toBe(false);
  });

  it("old content: failed still offers 重新生成 (force); updating is disabled", () => {
    expect(generateButtonState(dto({ status: "failed", summary: "旧摘要" }))).toEqual({ label: "重新生成", force: true, disabled: false, visible: true });
    expect(generateButtonState(dto({ status: "running", summary: "旧摘要" }))).toEqual({ label: "生成中", force: false, disabled: true, visible: true });
  });
});

describe("evidenceKind", () => {
  it("splits known prefixes at the first colon", () => {
    expect(evidenceKind("node:Acme.Api")).toEqual({ kind: "node", ref: "Acme.Api" });
    expect(evidenceKind("node:external:database:postgresql")).toEqual({ kind: "node", ref: "external:database:postgresql" });
    expect(evidenceKind("edge:web→Acme.Api:http")).toEqual({ kind: "edge", ref: "web→Acme.Api:http" });
    expect(evidenceKind("entry:1234")).toEqual({ kind: "entry", ref: "1234" });
    expect(evidenceKind("sym:56")).toEqual({ kind: "sym", ref: "56" });
    expect(evidenceKind("comp:Acme.Orders")).toEqual({ kind: "comp", ref: "Acme.Orders" });
    expect(evidenceKind("cell:Acme.Orders→Acme.Billing")).toEqual({ kind: "cell", ref: "Acme.Orders→Acme.Billing" });
  });

  it("anything else is unknown with the whole id", () => {
    expect(evidenceKind("Acme.Api")).toEqual({ kind: "unknown", ref: "Acme.Api" });
    expect(evidenceKind("file:src/a.cs")).toEqual({ kind: "unknown", ref: "file:src/a.cs" });
    expect(evidenceKind(":x")).toEqual({ kind: "unknown", ref: ":x" });
    expect(evidenceKind("")).toEqual({ kind: "unknown", ref: "" });
  });

  it("edge refs keep colons inside external keys; protocol is after the last colon", () => {
    expect(parseEdgeEvidence("Acme.Api→external:database:postgresql:sql")).toEqual({ from: "Acme.Api", to: "external:database:postgresql", protocol: "sql" });
    expect(parseEdgeEvidence("external:broker:kafka→Acme.Worker:kafka-consume")).toEqual({ from: "external:broker:kafka", to: "Acme.Worker", protocol: "kafka-consume" });
    expect(parseEdgeEvidence("a→b")).toEqual({ from: "a", to: "b", protocol: null });
    expect(parseEdgeEvidence("no-arrow")).toBeNull();
  });

  it("chip labels are short", () => {
    expect(evidenceLabel("node:external:database:postgresql")).toBe("postgresql");
    expect(evidenceLabel("edge:Acme.Api→external:database:postgresql:sql")).toBe("Api → postgresql");
    expect(evidenceLabel("entry:12")).toBe("入口 #12");
    expect(evidenceLabel("cell:Acme.Orders→Acme.Billing")).toBe("Orders → Billing");
    expect(evidenceLabel("comp:src/features/orders")).toBe("orders");
  });
});

describe("presets", () => {
  it("defaults are the existing layouts and the AI can only pick listed ids", () => {
    expect(DefaultPreset).toEqual({ container: "columns", component: "dependency-order", flow: "first-seen" });
    expect(LayoutPresets.container.map((p) => p.id)).toEqual(["columns", "topological"]);
    expect(LayoutPresets.component.map((p) => p.id)).toEqual(["dependency-order", "alphabetical"]);
    expect(LayoutPresets.flow.map((p) => p.id)).toEqual(["first-seen", "grouped-external"]);
    expect(isKnownPreset("container", "topological")).toBe(true);
    expect(isKnownPreset("container", "alphabetical")).toBe(false);
    expect(isKnownPreset("flow", null)).toBe(false);
    expect(presetLabel("component", "alphabetical")).toBe("字母序");
    expect(presetLabel("component", "free-form")).toBe("free-form");
  });
});
