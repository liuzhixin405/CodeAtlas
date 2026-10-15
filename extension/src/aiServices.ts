// AI 叙述服务的预设、SecretStorage 键名、端点工具函数。
// 纯函数、不依赖 vscode / node，扩展宿主（src/）和页面（webview/）共用，测试直接导入。

/** serve 的 --ai-provider：openai = OpenAI 兼容协议（llama-server / DeepSeek / 通义 / OpenAI 等）；anthropic = Claude Messages API。 */
export type AiProvider = "openai" | "anthropic";

export const AiProviders: readonly AiProvider[] = ["openai", "anthropic"];

/** 设置里读到的任意值 → 合法 provider；不认识的按默认 openai。 */
export function normalizeProvider(v: unknown): AiProvider {
  return v === "anthropic" ? "anthropic" : "openai";
}

export interface AiServicePreset {
  /** 稳定 id，测试与「当前服务」判断用。 */
  id: string;
  /** QuickPick 上的名字。 */
  label: string;
  provider: AiProvider;
  endpoint: string;
  /** 默认模型名，可在设置 codeatlas.ai.model 里改。 */
  model: string;
}

/** 「CodeTrail Graph: 选择 AI 服务」里的预设。模型名只是默认值。 */
export const AiServicePresets: readonly AiServicePreset[] = [
  { id: "local", label: "本地 llama-server", provider: "openai", endpoint: "http://127.0.0.1:8080/v1", model: "qwen3.6-27b" },
  { id: "deepseek", label: "DeepSeek", provider: "openai", endpoint: "https://api.deepseek.com/v1", model: "deepseek-chat" },
  { id: "dashscope", label: "通义千问（阿里云百炼）", provider: "openai", endpoint: "https://dashscope.aliyuncs.com/compatible-mode/v1", model: "qwen-plus" },
  { id: "openai", label: "OpenAI", provider: "openai", endpoint: "https://api.openai.com/v1", model: "gpt-4o-mini" },
  { id: "anthropic", label: "Anthropic Claude", provider: "anthropic", endpoint: "https://api.anthropic.com/v1", model: "claude-sonnet-5-5" },
];

/** 已知联网服务的主机 → 叙述卡上的友好名。 */
const KnownHosts: Record<string, string> = {
  "api.deepseek.com": "DeepSeek",
  "dashscope.aliyuncs.com": "通义千问",
  "api.openai.com": "OpenAI",
  "api.anthropic.com": "Anthropic Claude",
};

function tryParseUrl(endpoint: string): URL | null {
  try { return new URL(endpoint.trim()); } catch { return null; }
}

/** 端点的主机（含非默认端口，小写）；解析不了就返回去掉首尾空白的原串。 */
export function endpointHost(endpoint: string): string {
  const u = tryParseUrl(endpoint);
  return u && u.host ? u.host.toLowerCase() : endpoint.trim();
}

/** 端点是不是本机回环地址（127.x / localhost / ::1）。解析不了按「非回环」处理，宁可多提示。 */
export function isLoopbackEndpoint(endpoint: string): boolean {
  const u = tryParseUrl(endpoint);
  if (!u) return false;
  const h = u.hostname.toLowerCase().replace(/^\[|\]$/g, "");
  return h === "localhost" || h.endsWith(".localhost") || h === "::1" || /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(h);
}

/** SecretStorage 键名前缀；监听 secrets.onDidChange 时用它过滤。 */
export const ApiKeySecretPrefix = "codeatlas.ai.apiKey:";

/** API Key 在 SecretStorage 里的键：按 provider + 端点主机区分，切换服务不用重输。 */
export function apiKeySecretKey(provider: string, endpoint: string): string {
  return `${ApiKeySecretPrefix}${normalizeProvider(provider)}:${endpointHost(endpoint)}`;
}

/** 叙述卡上的服务名：已知主机给友好名，回环给「本地」，其余显示主机。 */
export function serviceName(provider: string | undefined, endpoint: string): string {
  if (isLoopbackEndpoint(endpoint)) return "本地";
  const host = endpointHost(endpoint);
  const known = KnownHosts[host];
  if (known) return known;
  return provider === "anthropic" ? `${host}（Anthropic 协议）` : host;
}

/** 找出与当前设置一致的预设（provider + 端点主机 + 路径都一样）；没有返回 null。 */
export function matchPreset(provider: string, endpoint: string): AiServicePreset | null {
  const norm = (e: string) => e.trim().replace(/\/+$/, "").toLowerCase();
  return AiServicePresets.find((p) => p.provider === normalizeProvider(provider) && norm(p.endpoint) === norm(endpoint)) ?? null;
}

/** 页面允许让扩展执行的命令（白名单）。 */
export const WebviewCommands = ["codeatlas.ai.setApiKey", "codeatlas.ai.selectService"] as const;

export type WebviewCommand = (typeof WebviewCommands)[number];

export function isAllowedWebviewCommand(c: unknown): c is WebviewCommand {
  return typeof c === "string" && (WebviewCommands as readonly string[]).includes(c);
}
