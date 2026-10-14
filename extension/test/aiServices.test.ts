import { readFileSync } from "node:fs";
import * as path from "node:path";
import { describe, expect, it } from "vitest";
import {
  AiProviders, AiServicePresets, apiKeySecretKey, ApiKeySecretPrefix, endpointHost, isAllowedWebviewCommand,
  isLoopbackEndpoint, matchPreset, normalizeProvider, serviceName, WebviewCommands,
} from "../src/aiServices";

describe("AiServicePresets", () => {
  it("lists the agreed services in order", () => {
    expect(AiServicePresets.map((p) => [p.label, p.provider, p.endpoint, p.model])).toEqual([
      ["本地 llama-server", "openai", "http://127.0.0.1:8080/v1", "qwen3.6-27b"],
      ["DeepSeek", "openai", "https://api.deepseek.com/v1", "deepseek-chat"],
      ["通义千问（阿里云百炼）", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"],
      ["OpenAI", "openai", "https://api.openai.com/v1", "gpt-4o-mini"],
      ["Anthropic Claude", "anthropic", "https://api.anthropic.com/v1", "claude-sonnet-5-5"],
    ]);
  });

  it("every preset has all fields, a known provider, a unique id and a valid http(s) endpoint", () => {
    const ids = new Set<string>();
    for (const p of AiServicePresets) {
      expect(p.id.trim()).not.toBe("");
      expect(p.label.trim()).not.toBe("");
      expect(p.model.trim()).not.toBe("");
      expect(AiProviders).toContain(p.provider);
      const u = new URL(p.endpoint);
      expect(["http:", "https:"]).toContain(u.protocol);
      expect(u.hostname).not.toBe("");
      ids.add(p.id);
    }
    expect(ids.size).toBe(AiServicePresets.length);
  });

  it("only the local preset is loopback; online ones use https", () => {
    for (const p of AiServicePresets) {
      if (p.id === "local") expect(isLoopbackEndpoint(p.endpoint)).toBe(true);
      else { expect(isLoopbackEndpoint(p.endpoint)).toBe(false); expect(new URL(p.endpoint).protocol).toBe("https:"); }
    }
  });

  it("matchPreset finds the preset for current settings, tolerating case and a trailing slash", () => {
    expect(matchPreset("openai", "https://api.deepseek.com/v1/")?.id).toBe("deepseek");
    expect(matchPreset("anthropic", "HTTPS://API.ANTHROPIC.COM/v1")?.id).toBe("anthropic");
    // 同一端点换了协议就不是那个预设
    expect(matchPreset("anthropic", "https://api.deepseek.com/v1")).toBeNull();
    expect(matchPreset("openai", "http://10.0.0.5:8080/v1")).toBeNull();
  });
});

describe("apiKeySecretKey", () => {
  it("is provider + endpoint host", () => {
    expect(apiKeySecretKey("anthropic", "https://api.anthropic.com/v1")).toBe("codeatlas.ai.apiKey:anthropic:api.anthropic.com");
    expect(apiKeySecretKey("openai", "https://api.deepseek.com/v1")).toBe("codeatlas.ai.apiKey:openai:api.deepseek.com");
    expect(apiKeySecretKey("openai", "https://api.deepseek.com/v1").startsWith(ApiKeySecretPrefix)).toBe(true);
  });

  it("ignores path, case and default port; keeps a non-default port", () => {
    expect(apiKeySecretKey("openai", "https://API.DeepSeek.com:443/v1/")).toBe("codeatlas.ai.apiKey:openai:api.deepseek.com");
    expect(apiKeySecretKey("openai", "http://127.0.0.1:8080/v1")).toBe("codeatlas.ai.apiKey:openai:127.0.0.1:8080");
    expect(apiKeySecretKey("openai", "http://127.0.0.1:8081/v1")).not.toBe(apiKeySecretKey("openai", "http://127.0.0.1:8080/v1"));
  });

  it("differs by provider for the same host, and unknown providers fall back to openai", () => {
    expect(apiKeySecretKey("anthropic", "https://gw.example.com/v1")).not.toBe(apiKeySecretKey("openai", "https://gw.example.com/v1"));
    expect(apiKeySecretKey("whatever", "https://gw.example.com/v1")).toBe(apiKeySecretKey("openai", "https://gw.example.com/v1"));
  });

  it("falls back to the trimmed string for an unparsable endpoint", () => {
    expect(apiKeySecretKey("openai", "  not a url ")).toBe("codeatlas.ai.apiKey:openai:not a url");
  });
});

describe("endpoint helpers", () => {
  it("endpointHost", () => {
    expect(endpointHost("https://dashscope.aliyuncs.com/compatible-mode/v1")).toBe("dashscope.aliyuncs.com");
    expect(endpointHost("http://localhost:8080/v1")).toBe("localhost:8080");
  });

  it("isLoopbackEndpoint", () => {
    expect(isLoopbackEndpoint("http://127.0.0.1:8080/v1")).toBe(true);
    expect(isLoopbackEndpoint("http://127.1.2.3/v1")).toBe(true);
    expect(isLoopbackEndpoint("http://localhost:8080/v1")).toBe(true);
    expect(isLoopbackEndpoint("http://[::1]:8080/v1")).toBe(true);
    expect(isLoopbackEndpoint("http://10.0.0.5:8080/v1")).toBe(false);
    expect(isLoopbackEndpoint("https://api.openai.com/v1")).toBe(false);
    expect(isLoopbackEndpoint("garbage")).toBe(false);
  });

  it("normalizeProvider", () => {
    expect(normalizeProvider("anthropic")).toBe("anthropic");
    expect(normalizeProvider("openai")).toBe("openai");
    expect(normalizeProvider(undefined)).toBe("openai");
    expect(normalizeProvider("Anthropic")).toBe("openai");
  });

  it("serviceName: known hosts get friendly names, loopback is 本地, others show the host", () => {
    expect(serviceName("openai", "https://api.deepseek.com/v1")).toBe("DeepSeek");
    expect(serviceName("openai", "https://dashscope.aliyuncs.com/compatible-mode/v1")).toBe("通义千问");
    expect(serviceName("openai", "https://api.openai.com/v1")).toBe("OpenAI");
    expect(serviceName("anthropic", "https://api.anthropic.com/v1")).toBe("Anthropic Claude");
    expect(serviceName("openai", "http://127.0.0.1:8080/v1")).toBe("本地");
    expect(serviceName("openai", "https://llm.corp.example:9000/v1")).toBe("llm.corp.example:9000");
    expect(serviceName("anthropic", "https://gw.example.com/v1")).toBe("gw.example.com（Anthropic 协议）");
    expect(serviceName(undefined, "https://gw.example.com/v1")).toBe("gw.example.com");
  });
});

describe("package.json contributions", () => {
  const pkg = JSON.parse(readFileSync(path.join(__dirname, "..", "package.json"), "utf8")) as {
    contributes: { commands: { command: string; title: string }[]; configuration: { properties: Record<string, { default?: unknown; enum?: string[]; minimum?: number; maximum?: number }> } };
  };
  const props = pkg.contributes.configuration.properties;

  it("declares the AI settings with the agreed defaults", () => {
    expect(props["codeatlas.ai.provider"]?.enum).toEqual([...AiProviders]);
    expect(props["codeatlas.ai.provider"]?.default).toBe("openai");
    expect(props["codeatlas.ai.concurrency"]).toMatchObject({ default: 1, minimum: 1, maximum: 8 });
    expect(props["codeatlas.ai.timeoutSeconds"]?.default).toBe(300);
    expect(props["codeatlas.ai.includeSource"]?.default).toBe(true);
    // 默认值与「本地 llama-server」预设一致
    const local = AiServicePresets.find((p) => p.id === "local")!;
    expect(props["codeatlas.ai.endpoint"]?.default).toBe(local.endpoint);
    expect(props["codeatlas.ai.model"]?.default).toBe(local.model);
  });

  it("declares the three AI commands, including the webview whitelist", () => {
    const ids = pkg.contributes.commands.map((c) => c.command);
    for (const c of ["codeatlas.ai.selectService", "codeatlas.ai.setApiKey", "codeatlas.ai.clearApiKey", ...WebviewCommands]) expect(ids).toContain(c);
  });
});

describe("webview command whitelist", () => {
  it("only allows the two AI commands", () => {
    expect([...WebviewCommands]).toEqual(["codeatlas.ai.setApiKey", "codeatlas.ai.selectService"]);
    expect(isAllowedWebviewCommand("codeatlas.ai.setApiKey")).toBe(true);
    expect(isAllowedWebviewCommand("codeatlas.ai.selectService")).toBe(true);
    expect(isAllowedWebviewCommand("codeatlas.ai.clearApiKey")).toBe(false);
    expect(isAllowedWebviewCommand("workbench.action.terminal.new")).toBe(false);
    expect(isAllowedWebviewCommand(undefined)).toBe(false);
    expect(isAllowedWebviewCommand(42)).toBe(false);
  });
});
