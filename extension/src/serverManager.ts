import { spawn, type ChildProcess } from "node:child_process";
import { accessSync, chmodSync, constants, readFileSync, statSync } from "node:fs";
import * as path from "node:path";
import type { Readable } from "node:stream";

/** 服务启动后向 stdout 打印的第一行形如 {"port":12345}。 */
export function parsePortLine(line: string): number | null {
  const trimmed = line.trim();
  if (!trimmed.startsWith("{")) return null;
  try {
    const obj = JSON.parse(trimmed) as { port?: unknown };
    return typeof obj.port === "number" && Number.isInteger(obj.port) && obj.port > 0 ? obj.port : null;
  } catch {
    return null;
  }
}

/** 从流里逐行读，直到读到端口行；超时或流结束则拒绝。 */
export function waitForPortLine(stream: Readable, timeoutMs: number): Promise<number> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    let done = false;
    const finish = (fn: () => void) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      stream.off("data", onData);
      stream.off("end", onEnd);
      stream.off("error", onError);
      fn();
    };
    const timer = setTimeout(() => finish(() => reject(new Error(`等待服务端口超时（${timeoutMs}ms）`))), timeoutMs);
    const onData = (chunk: Buffer | string) => {
      buffer += chunk.toString();
      let nl: number;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, nl);
        buffer = buffer.slice(nl + 1);
        const port = parsePortLine(line);
        if (port !== null) { finish(() => resolve(port)); return; }
      }
    };
    const onEnd = () => finish(() => reject(new Error("服务输出已结束，未读到端口行")));
    const onError = (e: Error) => finish(() => reject(e));
    stream.on("data", onData);
    stream.on("end", onEnd);
    stream.on("error", onError);
  });
}

/** VS Code 平台名（vsce --target）：win32-x64、linux-arm64、darwin-arm64 … */
export function currentTarget(platform: NodeJS.Platform = process.platform, arch: string = process.arch): string {
  return `${platform}-${arch}`;
}

const TargetLabels: Record<string, string> = {
  "win32-x64": "Windows x64",
  "win32-arm64": "Windows ARM64",
  "linux-x64": "Linux x64",
  "linux-arm64": "Linux ARM64",
  "darwin-x64": "macOS（Intel 芯片）",
  "darwin-arm64": "macOS（Apple 芯片）",
};

const targetLabel = (t: string) => (TargetLabels[t] ? `${TargetLabels[t]}（${t}）` : t);

/**
 * 自带服务是给哪个平台打的（打包时写进 server/target.json）。和当前系统不一致时返回给用户看的提示，一致或没有 target.json（开发环境）返回 null。
 * 本地 VSIX 安装不拦平台不匹配的包，不检查的话用户只会看到"找不到路径"。
 */
export function packageTargetMismatch(bundledDir: string, version: string, platform: NodeJS.Platform = process.platform, arch: string = process.arch): string | null {
  let target: unknown;
  try {
    target = (JSON.parse(readFileSync(path.join(bundledDir, "target.json"), "utf8")) as { target?: unknown }).target;
  } catch {
    return null;
  }
  const current = currentTarget(platform, arch);
  if (typeof target !== "string" || target === current) return null;
  return `CodeTrail Graph：这个安装包是给 ${targetLabel(target)} 的，当前系统是 ${targetLabel(current)}，服务无法启动。`
    + `请完全退出 VS Code 后安装 codetrail-graph-${current}-${version}.vsix。`;
}

/** 查找顺序：扩展自带的 server/ 目录 → 用户设置 → PATH。返回 null 表示都没有。 */
export function findServerExecutable(bundledDir: string | null, configured: string | null, envPath: string | undefined): string | null {
  const exe = process.platform === "win32" ? "codeatlas.exe" : "codeatlas";
  const candidates: string[] = [];
  if (bundledDir) candidates.push(path.join(bundledDir, exe));
  if (configured && configured.trim()) candidates.push(configured.trim());
  for (const dir of (envPath ?? "").split(path.delimiter)) if (dir) candidates.push(path.join(dir, exe));
  for (const c of candidates) {
    try { accessSync(c, constants.F_OK); return c; } catch { /* 下一个 */ }
  }
  return null;
}

/**
 * Linux / macOS：VSIX 解压后不一定保留可执行位（在 Windows 上打的包里没有 Unix 权限），启动前补上 u+x、g+x、o+x。
 * 返回是否改过；Windows 上什么都不做。
 */
export function ensureExecutable(file: string, platform: NodeJS.Platform = process.platform): boolean {
  if (platform === "win32") return false;
  try {
    accessSync(file, constants.X_OK);
    return false;
  } catch {
    try {
      chmodSync(file, statSync(file).mode | 0o111);
      return true;
    } catch {
      return false; // 改不了就照常启动，spawn 的报错会进输出面板
    }
  }
}

export interface ServerLogger {
  info(line: string): void;
  error(line: string): void;
}

/** ServerManager 管理的进程抽象，测试里用假实现。 */
export interface ProcessLike {
  readonly alive: boolean;
  readonly apiBase: string | null;
  start(): Promise<string>;
  stop(): void;
}

export type ProcessFactory = (exe: string, repoRoot: string) => ProcessLike;

/**
 * AI 叙述的设置（codeatlas.ai.*），启动 serve 时变成命令行参数。
 * provider / concurrency / timeoutSeconds / includeSource 缺省表示不传，让服务用自己的默认。
 */
export interface AiOptions {
  enabled: boolean;
  endpoint: string;
  model: string;
  /** "openai" | "anthropic"。 */
  provider?: string;
  /** 同时在跑的叙述请求数，1–8。 */
  concurrency?: number;
  /** 单条叙述的超时（秒）。 */
  timeoutSeconds?: number;
  /** false 时加 --ai-no-source：只发事实子图，不发入口方法源码。 */
  includeSource?: boolean;
}

/** 服务进程读 API Key 的环境变量名。Key 绝不进命令行参数（进程列表可见）。 */
export const ApiKeyEnvVar = "CODEATLAS_AI_API_KEY";

export const MinAiConcurrency = 1;
export const MaxAiConcurrency = 8;

/**
 * 设置 → serve 参数：
 * 启用时 `--ai-provider <p> --ai-endpoint <url> --ai-model <name> --ai-concurrency <n> --ai-timeout <秒> [--ai-no-source]`，
 * 关掉时 `--no-ai`；null 表示不传（老服务）。
 */
export function aiArgs(ai: AiOptions | null): string[] {
  if (!ai) return [];
  if (!ai.enabled) return ["--no-ai"];
  const args: string[] = [];
  if (ai.provider !== undefined) args.push("--ai-provider", ai.provider === "anthropic" ? "anthropic" : "openai");
  if (ai.endpoint.trim()) args.push("--ai-endpoint", ai.endpoint.trim());
  if (ai.model.trim()) args.push("--ai-model", ai.model.trim());
  if (ai.concurrency !== undefined && Number.isFinite(ai.concurrency)) {
    const n = Math.min(MaxAiConcurrency, Math.max(MinAiConcurrency, Math.floor(ai.concurrency)));
    args.push("--ai-concurrency", String(n));
  }
  if (ai.timeoutSeconds !== undefined && Number.isFinite(ai.timeoutSeconds) && ai.timeoutSeconds > 0) {
    args.push("--ai-timeout", String(Math.max(1, Math.round(ai.timeoutSeconds))));
  }
  if (ai.includeSource === false) args.push("--ai-no-source");
  return args;
}

/**
 * 多项目设置：主根之外一起索引的目录（多根工作区的其余 folder + codeatlas.projects），以及是否纳入嵌套仓库。
 * projects 已是绝对路径（resolveProjects 算好）。
 */
export interface ProjectOptions {
  projects: string[];
  /** false 时加 --no-nested：带独立 .git 的子目录（克隆 / 子模块）不索引。 */
  includeNested: boolean;
}

/**
 * 算外部项目根：多根工作区里第一个 folder 是主根，其余 folder 依次在前；再接 codeatlas.projects（相对主根或绝对路径）。
 * 去掉空项、主根本身与重复项（Windows 上不分大小写、不分 / 与 \、忽略结尾分隔符）。
 */
export function resolveProjects(primary: string, otherFolders: readonly string[], configured: readonly string[], platform: NodeJS.Platform = process.platform): string[] {
  const p = platform === "win32" ? path.win32 : path.posix;
  // 去掉结尾分隔符，但盘符根 D:\ 与 / 保留
  const trim = (s: string) => (s.length > p.parse(s).root.length ? s.replace(/[\\/]+$/, "") : s);
  const fold = platform === "win32" || platform === "darwin";
  const key = (s: string) => (fold ? trim(p.normalize(s)).toLowerCase() : trim(p.normalize(s)));
  const seen = new Set<string>([key(primary)]);
  const out: string[] = [];
  const add = (raw: string) => {
    const t = raw.trim();
    if (!t) return;
    const abs = trim(p.isAbsolute(t) ? p.normalize(t) : p.resolve(primary, t));
    const k = key(abs);
    if (seen.has(k)) return;
    seen.add(k);
    out.push(abs);
  };
  for (const f of otherFolders) add(f);
  for (const c of configured) add(c);
  return out;
}

/** 多项目设置 → serve 参数：每个外部根一对 `--project <路径>`，关掉嵌套仓库时再加 `--no-nested`；null 表示不传。 */
export function projectArgs(projects: ProjectOptions | null): string[] {
  if (!projects) return [];
  const args: string[] = [];
  for (const p of projects.projects) args.push("--project", p);
  if (!projects.includeNested) args.push("--no-nested");
  return args;
}

/** 把日志行里出现的密钥换成 ***（防御：比如用户把 key 误填进了 model 设置）。 */
export function redactSecrets(line: string, secrets: readonly (string | undefined)[]): string {
  let out = line;
  // 太短的串替换会误伤正常文字；真实的 key / token 都远长于 8
  for (const s of secrets) if (s && s.length >= 8) out = out.split(s).join("***");
  return out;
}

export interface ServeLaunch {
  args: string[];
  env: NodeJS.ProcessEnv;
  /** 写进输出面板的那一行；token 与 key 都已脱敏。 */
  logLine: string;
}

/**
 * 拼 serve 的参数、环境变量和日志行。
 * - API Key 只放进 env 的 CODEATLAS_AI_API_KEY；
 * - 没有 key 时原样继承外部环境（外部已有 CODEATLAS_AI_API_KEY 就沿用，作为 CI / 高级用法）；
 * - AI 关掉时不注入 key。
 */
export function buildServeLaunch(
  exe: string,
  repoRoot: string,
  token: string,
  parentPid: number,
  ai: AiOptions | null,
  apiKey: string | undefined,
  baseEnv: NodeJS.ProcessEnv,
  projects: ProjectOptions | null = null,
): ServeLaunch {
  const extra = [...projectArgs(projects), ...aiArgs(ai)];
  const args = ["serve", repoRoot, "--port", "0", "--parent-pid", String(parentPid), "--token", token, ...extra];
  const key = ai?.enabled && apiKey && apiKey.trim() ? apiKey.trim() : undefined;
  const env: NodeJS.ProcessEnv = key ? { ...baseEnv, [ApiKeyEnvVar]: key } : { ...baseEnv };
  let note = "";
  if (ai?.enabled) {
    if (key) note = `（API Key：已设置，经环境变量 ${ApiKeyEnvVar} 传入）`;
    else if (baseEnv[ApiKeyEnvVar]) note = `（API Key：沿用外部环境变量 ${ApiKeyEnvVar}）`;
    else note = "（API Key：未设置）";
  }
  const raw = `启动服务：${exe} serve ${repoRoot} --port 0 --parent-pid ${parentPid} --token *** ${extra.join(" ")}`.trimEnd() + note;
  return { args, env, logLine: redactSecrets(raw, [token, key, baseEnv[ApiKeyEnvVar]]) };
}

/** 一个工作区文件夹对应一个服务进程。token 由扩展生成，服务要求每个 /api 请求都带它。 */
export class ServerProcess implements ProcessLike {
  private child: ChildProcess | null = null;
  apiBase: string | null = null;

  /**
   * @param getApiKey 读当前 AI 服务的 API Key（扩展里来自 SecretStorage）；测试与老调用方可不传。
   */
  constructor(
    readonly exe: string,
    readonly repoRoot: string,
    readonly token: string,
    private readonly log: ServerLogger,
    private readonly ai: AiOptions | null = null,
    private readonly getApiKey: () => Promise<string | undefined> = () => Promise.resolve(undefined),
    private readonly projects: ProjectOptions | null = null,
  ) {}

  get alive(): boolean {
    return this.child !== null && this.child.exitCode === null && !this.child.killed;
  }

  /** 首次启动可能被杀毒软件扫描程序集拖慢，给 20 秒。 */
  async start(timeoutMs = 20000): Promise<string> {
    let apiKey: string | undefined;
    if (this.ai?.enabled) {
      try { apiKey = await this.getApiKey(); } catch (e) { this.log.error(`读取 AI API Key 失败，按未设置处理：${e instanceof Error ? e.message : String(e)}`); }
    }
    const launch = buildServeLaunch(this.exe, this.repoRoot, this.token, process.pid, this.ai, apiKey, process.env, this.projects);
    const secrets = [this.token, apiKey?.trim(), launch.env[ApiKeyEnvVar]];
    const scrub = (s: string) => redactSecrets(s, secrets);
    this.log.info(launch.logLine);
    if (ensureExecutable(this.exe)) this.log.info(`已给 ${this.exe} 加上可执行权限`);
    const child = spawn(this.exe, launch.args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: true, env: launch.env });
    this.child = child;
    child.stderr?.on("data", (d: Buffer) => this.log.error(scrub(d.toString().trimEnd())));
    child.on("error", (e) => this.log.error(`服务进程错误：${scrub(e.message)}`));
    child.on("exit", (code) => {
      this.log.info(`服务退出，code=${code}`);
      this.apiBase = null;
    });
    try {
      const port = await waitForPortLine(child.stdout!, timeoutMs);
      this.apiBase = `http://127.0.0.1:${port}`;
      child.stdout!.on("data", (d: Buffer) => this.log.info(scrub(d.toString().trimEnd())));
      return this.apiBase;
    } catch (e) {
      this.stop();
      throw e;
    }
  }

  stop(): void {
    if (this.child && this.child.exitCode === null) {
      try { this.child.kill(); } catch { /* 已退出 */ }
    }
    this.child = null;
    this.apiBase = null;
  }
}

/**
 * 按仓库根目录管理服务进程。
 * - 并发 ensure() 共享同一个启动 Promise，不会拉起两个进程。
 * - recover() 在请求失败后最多自动重启一次；之后拒绝，直到用户显式 restart()。
 */
export class ServerManager {
  private readonly servers = new Map<string, ProcessLike>();
  private readonly starting = new Map<string, Promise<string>>();
  private readonly restartBudget = new Map<string, number>();

  constructor(
    private readonly resolveExe: () => string | null,
    private readonly log: ServerLogger,
    private readonly createProcess: ProcessFactory,
  ) {}

  /** 该仓库是否已有本会话拉起且仍存活的服务。 */
  has(repoRoot: string): boolean {
    const p = this.servers.get(repoRoot);
    return p !== undefined && p.alive && p.apiBase !== null;
  }

  /** 本会话里有存活服务的仓库；设置变化时逐个重启用。 */
  repos(): string[] {
    return [...this.servers.keys()].filter((r) => this.has(r));
  }

  ensure(repoRoot: string): Promise<string> {
    const existing = this.servers.get(repoRoot);
    if (existing?.alive && existing.apiBase) return Promise.resolve(existing.apiBase);
    const inFlight = this.starting.get(repoRoot);
    if (inFlight) return inFlight;

    const exe = this.resolveExe();
    if (!exe) return Promise.reject(new ServerNotFoundError());
    const proc = this.createProcess(exe, repoRoot);
    this.servers.set(repoRoot, proc);
    const p = proc.start().finally(() => this.starting.delete(repoRoot));
    this.starting.set(repoRoot, p);
    return p;
  }

  /** 请求失败后的自动恢复：每个仓库只允许一次。 */
  recover(repoRoot: string): Promise<string> {
    const used = this.restartBudget.get(repoRoot) ?? 0;
    if (used >= 1) return Promise.reject(new Error("CodeTrail Graph 服务已重启过一次仍不可用，请用命令「CodeTrail Graph: 重启服务」手动重启"));
    this.restartBudget.set(repoRoot, used + 1);
    this.servers.get(repoRoot)?.stop();
    this.servers.delete(repoRoot);
    return this.ensure(repoRoot);
  }

  /** 用户显式重启：重置自动重启预算。 */
  restart(repoRoot: string): Promise<string> {
    this.restartBudget.delete(repoRoot);
    this.servers.get(repoRoot)?.stop();
    this.servers.delete(repoRoot);
    return this.ensure(repoRoot);
  }

  /** 停掉一个仓库的服务（多根工作区换了主根时用）；下次 ensure 会重新拉起。 */
  stop(repoRoot: string): void {
    this.servers.get(repoRoot)?.stop();
    this.servers.delete(repoRoot);
    this.starting.delete(repoRoot);
    this.restartBudget.delete(repoRoot);
  }

  stopAll(): void {
    for (const p of this.servers.values()) p.stop();
    this.servers.clear();
    this.starting.clear();
  }
}

export class ServerNotFoundError extends Error {
  constructor() {
    super("找不到 codeatlas 可执行文件：请在设置 codeatlas.serverPath 中指定，或把它加入 PATH。");
    this.name = "ServerNotFoundError";
  }
}
