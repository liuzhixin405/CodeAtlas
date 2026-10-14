import * as crypto from "node:crypto";
import * as path from "node:path";
import * as vscode from "vscode";
import { AiServicePresets, apiKeySecretKey, endpointHost, isLoopbackEndpoint, matchPreset, normalizeProvider, serviceName, type AiServicePreset } from "./aiServices";
import { ServerClient } from "./client";
import { IndexScheduler } from "./indexScheduler";
import { PanelHost } from "./panel";
import { findServerExecutable, MaxAiConcurrency, MinAiConcurrency, ServerManager, ServerNotFoundError, ServerProcess, type AiOptions } from "./serverManager";

const SupportedExtensions = new Set([".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".cs"]);

let servers: ServerManager;
let panel: PanelHost;
let status: vscode.StatusBarItem;
let output: vscode.OutputChannel;
/** API Key 存在 VS Code SecretStorage，不进 settings.json。 */
let secrets: vscode.SecretStorage;
/** 本扩展宿主进程生成一次，所有服务进程共用；页面通过 init 消息拿到。 */
let token: string;
const schedulers = new Map<string, IndexScheduler>();

/** 一次「选择服务」会连写好几项设置，合并成一次重启。 */
const AiRestartDebounceMs = 400;
let aiRestartTimer: ReturnType<typeof setTimeout> | undefined;
/** >0 时暂不重启（选择服务的流程还没走完，比如正等用户输 key）。 */
let aiRestartSuppressed = 0;

export function activate(context: vscode.ExtensionContext): void {
  secrets = context.secrets;
  token = crypto.randomBytes(24).toString("hex");
  output = vscode.window.createOutputChannel("CodeAtlas");
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.command = "codeatlas.openPanel";
  status.text = "$(type-hierarchy) CodeAtlas";
  status.tooltip = "打开 CodeAtlas 面板";
  status.show();

  const log = { info: (l: string) => output.appendLine(l), error: (l: string) => output.appendLine(`[stderr] ${l}`) };
  servers = new ServerManager(
    () => findServerExecutable(
      path.join(context.extensionPath, "server"),
      vscode.workspace.getConfiguration("codeatlas").get<string>("serverPath") ?? null,
      process.env["PATH"]),
    log,
    (exe, root) => new ServerProcess(exe, root, token, log, aiOptions(), () => currentApiKey()));

  panel = new PanelHost(context, {
    open: (repoRoot, rel, line, col) => void openInEditor(repoRoot, rel, line, col),
    requestIndex: (repoRoot, full) => void runIndex(repoRoot, full),
  });

  context.subscriptions.push(
    output, status, { dispose: () => panel.dispose() }, { dispose: () => servers.stopAll() },
    vscode.commands.registerCommand("codeatlas.showSymbol", () => void showSymbolAtCursor()),
    vscode.commands.registerCommand("codeatlas.openPanel", () => void openPanel()),
    vscode.commands.registerCommand("codeatlas.indexWorkspace", () => void indexCurrentWorkspace(false)),
    vscode.commands.registerCommand("codeatlas.indexWorkspaceFull", () => void indexCurrentWorkspace(true)),
    vscode.commands.registerCommand("codeatlas.restartServer", () => void restartServer()),
    vscode.commands.registerCommand("codeatlas.ai.selectService", () => void selectAiService()),
    vscode.commands.registerCommand("codeatlas.ai.setApiKey", () => void setApiKeyCommand()),
    vscode.commands.registerCommand("codeatlas.ai.clearApiKey", () => void clearApiKeyCommand()),
    vscode.workspace.onDidSaveTextDocument((doc) => onSaved(doc)),
    // AI 设置是 serve 的启动参数（含 provider / concurrency / timeoutSeconds / includeSource），改了只能重启服务才生效
    vscode.workspace.onDidChangeConfiguration((e) => { if (e.affectsConfiguration("codeatlas.ai")) scheduleAiRestart(); }),
    // 当前服务的 key 在别的窗口改了也要重启，key 才能进新进程的环境变量
    context.secrets.onDidChange((e) => { if (e.key === currentApiKeyName()) scheduleAiRestart(); }),
    { dispose: () => clearTimeout(aiRestartTimer) },
  );
}

/** codeatlas.ai.* 设置 → serve 参数。每次拉进程时现读，所以重启就是生效。 */
function aiOptions(): AiOptions {
  const cfg = vscode.workspace.getConfiguration("codeatlas.ai");
  const concurrency = cfg.get<number>("concurrency", 1);
  const timeoutSeconds = cfg.get<number>("timeoutSeconds", 300);
  return {
    enabled: cfg.get<boolean>("enabled", true),
    provider: normalizeProvider(cfg.get<string>("provider", "openai")),
    endpoint: cfg.get<string>("endpoint", "http://127.0.0.1:8080/v1"),
    model: cfg.get<string>("model", "qwen3.6-27b"),
    concurrency: Number.isFinite(concurrency) ? Math.min(MaxAiConcurrency, Math.max(MinAiConcurrency, Math.floor(concurrency))) : 1,
    timeoutSeconds: Number.isFinite(timeoutSeconds) && timeoutSeconds > 0 ? timeoutSeconds : 300,
    includeSource: cfg.get<boolean>("includeSource", true),
  };
}

/** 当前 provider + 端点主机对应的 SecretStorage 键。 */
function currentApiKeyName(): string {
  const ai = aiOptions();
  return apiKeySecretKey(ai.provider ?? "openai", ai.endpoint);
}

/** 拉服务进程时调：读当前服务的 key；AI 关掉时不读。 */
async function currentApiKey(): Promise<string | undefined> {
  if (!aiOptions().enabled) return undefined;
  return (await secrets.get(currentApiKeyName())) || undefined;
}

/** 防抖地重启服务；选择服务流程进行中先不重启，结束时统一来一次。 */
function scheduleAiRestart(): void {
  if (aiRestartSuppressed > 0) return;
  clearTimeout(aiRestartTimer);
  aiRestartTimer = setTimeout(() => { aiRestartTimer = undefined; void onAiSettingsChanged(); }, AiRestartDebounceMs);
}

/** AI 设置变了：把本会话拉起过的服务挨个重启，面板指向的那个重新 init。 */
async function onAiSettingsChanged(): Promise<void> {
  const repos = servers.repos();
  if (repos.length === 0) return;
  const ai = aiOptions();
  const desc = ai.enabled
    ? `${ai.provider} · ${ai.endpoint} · ${ai.model} · 并发 ${ai.concurrency} · 超时 ${ai.timeoutSeconds}s${ai.includeSource ? "" : " · 不发源码"}`
    : "已关闭";
  log(`AI 设置变化（${desc}），重启 ${repos.length} 个服务`);
  for (const root of repos) {
    try {
      const apiBase = await servers.restart(root);
      if (panel.isOpen && panel.currentRepo === root) panel.show(root, apiBase, token);
      void refreshStatus(new ServerClient(apiBase, token));
    } catch (e) {
      reportServerError(e);
    }
  }
  flash("AI 设置已更新，服务已重启");
}

export function deactivate(): void {
  servers?.stopAll();
}

// ---------- 命令 ----------

async function showSymbolAtCursor(): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) { flash("没有打开的编辑器"); return; }
  const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
  if (!folder) { flash("文件不在任何工作区文件夹里"); return; }
  const repoRoot = folder.uri.fsPath;
  const filePath = editor.document.uri.fsPath;
  const pos = editor.selection.active;

  const health = await withServer(repoRoot, (c) => c.health());
  if (health === undefined) return;
  if (health.files > 0 && health.needsRebuild) {
    // 索引是旧版本解析规则建的（比如还没有分发边），自动全量重建一次
    flash("索引格式已更新，正在全量重建…");
    log("索引版本过旧，自动全量重建");
    await runIndex(repoRoot, true);
  }
  if (health.files === 0) {
    const pick = await vscode.window.showInformationMessage("这个工作区还没有 CodeAtlas 索引。", "建立索引");
    if (!pick) return;
    await runIndex(repoRoot, false);
    // 索引完直接接着定位，不让用户再右键一次
  }

  const symbol = await withServer(repoRoot, (c) => c.symbolAt(filePath, pos.line + 1, pos.character + 1));
  if (symbol === undefined) return;
  if (!symbol) { flash("光标处没有已索引的符号"); return; }
  const apiBase = await ensureServer(repoRoot);
  if (!apiBase) return;
  panel.show(repoRoot, apiBase, token);
  panel.focus(symbol.id);
}

async function openPanel(): Promise<void> {
  const folder = currentFolder();
  if (!folder) { flash("没有打开的工作区文件夹"); return; }
  const apiBase = await ensureServer(folder.uri.fsPath);
  if (apiBase) panel.show(folder.uri.fsPath, apiBase, token);
}

async function indexCurrentWorkspace(full: boolean): Promise<void> {
  const folder = currentFolder();
  if (!folder) { flash("没有打开的工作区文件夹"); return; }
  await runIndex(folder.uri.fsPath, full);
}

async function restartServer(): Promise<void> {
  const folder = currentFolder();
  if (!folder) return;
  try {
    const apiBase = await servers.restart(folder.uri.fsPath);
    if (panel.isOpen) panel.show(folder.uri.fsPath, apiBase, token);
    flash("CodeAtlas 服务已重启");
    void refreshStatus(new ServerClient(apiBase, token));
  } catch (e) {
    reportServerError(e);
  }
}

// ---------- AI 服务 ----------

type ServiceItem = vscode.QuickPickItem & { preset?: AiServicePreset; custom?: true };

/** 「CodeAtlas: 选择 AI 服务」：选预设 → 写 provider / endpoint / model → 联网服务没 key 就紧接着让用户输。 */
async function selectAiService(): Promise<void> {
  const ai = aiOptions();
  const current = ai.enabled ? matchPreset(ai.provider ?? "openai", ai.endpoint) : null;
  const items: ServiceItem[] = AiServicePresets.map((p) => ({
    label: p.label,
    description: `${current?.id === p.id ? "当前 · " : ""}${p.model} · ${endpointHost(p.endpoint)}`,
    detail: isLoopbackEndpoint(p.endpoint)
      ? "本机运行，代码不出机器；27B 模型约 2–4 分钟一条。模型名可在设置里改。"
      : `联网服务，需要 API Key；事实子图${ai.includeSource ? "与入口源码" : ""}会发送到 ${endpointHost(p.endpoint)}。模型名可在设置里改。`,
    preset: p,
  }));
  items.push({ label: "自定义…", detail: "打开设置页 codeatlas.ai，手动填协议 / 端点 / 模型", custom: true });

  const pick = await vscode.window.showQuickPick(items, {
    title: "CodeAtlas: 选择 AI 服务",
    placeHolder: "叙述用哪个模型服务（预设里的模型名只是默认值）",
    matchOnDescription: true,
    matchOnDetail: true,
  });
  if (!pick) return;
  if (pick.custom || !pick.preset) {
    await vscode.commands.executeCommand("workbench.action.openSettings", "codeatlas.ai");
    return;
  }
  const p = pick.preset;

  aiRestartSuppressed++;
  try {
    const cfg = vscode.workspace.getConfiguration("codeatlas.ai");
    const target = writeTarget(cfg, ["provider", "endpoint", "model"]);
    await cfg.update("provider", p.provider, target);
    await cfg.update("endpoint", p.endpoint, target);
    await cfg.update("model", p.model, target);
    // 显式选了服务就是要用 AI；之前关掉的顺手打开
    if (!cfg.get<boolean>("enabled", true)) await cfg.update("enabled", true, writeTarget(cfg, ["enabled"]));

    if (!isLoopbackEndpoint(p.endpoint)) {
      const has = Boolean(await secrets.get(apiKeySecretKey(p.provider, p.endpoint)));
      if (!has && !(await promptApiKey(p.provider, p.endpoint))) {
        void vscode.window.showWarningMessage(`已切换到 ${p.label}，但还没有 API Key，叙述会不可用。稍后可运行「CodeAtlas: 设置 AI API Key」。`);
      }
      const sends = aiOptions().includeSource ? "事实子图与入口方法源码" : "事实子图";
      void vscode.window.showInformationMessage(`已切换到 ${p.label}（${p.model}）。叙述会把${sends}发送到 ${endpointHost(p.endpoint)}，请确认符合公司数据政策。`);
    } else {
      flash(`AI 服务已切换为 ${p.label}`);
    }
  } finally {
    aiRestartSuppressed--;
    scheduleAiRestart();
  }
}

/**
 * 写到哪一层：默认 Global（跨工作区共用）；工作区里已经覆盖了这些键时写 Workspace，否则写了也不生效。
 * 只有文件夹级（多根工作区）覆盖时仍写 Workspace，提示用户那一层会盖过它。
 */
function writeTarget(cfg: vscode.WorkspaceConfiguration, keys: string[]): vscode.ConfigurationTarget {
  let workspace = false;
  for (const k of keys) {
    const i = cfg.inspect(k);
    if (i?.workspaceValue !== undefined) workspace = true;
    if (i?.workspaceFolderValue !== undefined) {
      workspace = true;
      log(`提示：codeatlas.ai.${k} 在文件夹级设置里有覆盖值，它会盖过这次写入的工作区值`);
    }
  }
  return workspace ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
}

/** 输入框（密码模式）收 key 存进 SecretStorage；用户取消返回 false。 */
async function promptApiKey(provider: string, endpoint: string): Promise<boolean> {
  const name = serviceName(provider, endpoint);
  const key = apiKeySecretKey(provider, endpoint);
  const value = await vscode.window.showInputBox({
    title: `设置 AI API Key：${name}（${endpointHost(endpoint)}）`,
    prompt: "保存在 VS Code SecretStorage，不写进 settings.json；按协议 + 主机分别保存，切换服务不用重输。",
    placeHolder: "粘贴 API Key",
    password: true,
    ignoreFocusOut: true,
    validateInput: (v) => (v.trim() ? null : "API Key 不能为空"),
  });
  if (value === undefined || !value.trim()) return false;
  await secrets.store(key, value.trim());
  log(`已保存 ${name} 的 API Key（SecretStorage 键 ${key}）`);
  return true;
}

/** 「CodeAtlas: 设置 AI API Key」：给当前 provider + 端点主机存 key，然后重启服务。 */
async function setApiKeyCommand(): Promise<void> {
  const ai = aiOptions();
  if (!(await promptApiKey(ai.provider ?? "openai", ai.endpoint))) return;
  flash("API Key 已保存，服务重启中");
  scheduleAiRestart();
}

/** 「CodeAtlas: 清除 AI API Key」：删掉当前服务的 key，然后重启服务。 */
async function clearApiKeyCommand(): Promise<void> {
  const ai = aiOptions();
  const name = serviceName(ai.provider, ai.endpoint);
  const key = currentApiKeyName();
  if (!(await secrets.get(key))) {
    flash(`${name}（${endpointHost(ai.endpoint)}）没有已保存的 API Key`);
    return;
  }
  await secrets.delete(key);
  log(`已清除 ${name} 的 API Key（SecretStorage 键 ${key}）`);
  flash("API Key 已清除，服务重启中");
  scheduleAiRestart();
}

// ---------- 索引 ----------

function onSaved(doc: vscode.TextDocument): void {
  if (!vscode.workspace.getConfiguration("codeatlas").get<boolean>("indexOnSave", true)) return;
  if (!SupportedExtensions.has(path.extname(doc.fileName).toLowerCase())) return;
  const folder = vscode.workspace.getWorkspaceFolder(doc.uri);
  if (!folder) return;
  // 只对本会话里已经用过 CodeAtlas 的文件夹做增量；绝不因为一次保存就静默拉起服务、往仓库写索引
  if (!servers.has(folder.uri.fsPath)) return;
  schedulerFor(folder.uri.fsPath).notifyChange();
}

function schedulerFor(repoRoot: string): IndexScheduler {
  let s = schedulers.get(repoRoot);
  if (!s) {
    s = new IndexScheduler(() => doIndex(repoRoot, false), 1500, (e) => log(`索引失败：${e}`));
    schedulers.set(repoRoot, s);
  }
  return s;
}

async function runIndex(repoRoot: string, full: boolean): Promise<void> {
  try {
    if (full) await doIndex(repoRoot, true);
    else await schedulerFor(repoRoot).runNow();
  } catch (e) {
    reportServerError(e);
  }
}

async function doIndex(repoRoot: string, full: boolean): Promise<void> {
  const started = await withServer(repoRoot, (c) => c.startIndex(full));
  if (started === undefined) return;
  if (started === "busy") log("索引已在进行中，等待其结束");
  status.text = "$(sync~spin) CodeAtlas · 索引中";
  const final = await withServer(repoRoot, (c) => c.waitForIndex((s) => {
    if (!s.running) return;
    if (s.phase === "roslyn") status.text = s.total ? `$(sync~spin) CodeAtlas · Roslyn ${s.done}/${s.total}` : "$(sync~spin) CodeAtlas · Roslyn 编译中";
    else if (s.total) status.text = `$(sync~spin) CodeAtlas · ${s.phase} ${s.done}/${s.total}`;
  }));
  if (final === undefined) return;
  if (final.error) {
    status.text = "$(warning) CodeAtlas · 索引失败";
    log(`索引失败：${final.error}`);
    return;
  }
  const r = final.lastReport;
  if (r) log(`索引完成：${r.filesScanned} 文件，${r.totalSymbols} 符号，${r.totalEdges} 边，${r.elapsedSeconds.toFixed(1)}s`);
  if (r?.roslyn) {
    if (r.roslyn.error) log(`Roslyn 未运行：${r.roslyn.error}`);
    else log(`Roslyn 完成：${r.roslyn.files} 文件，合并 ${r.roslyn.symbolsMerged} 符号，${r.roslyn.edges} 条精确边，${r.roslyn.elapsedSeconds.toFixed(1)}s`);
  }
  const apiBase = await ensureServer(repoRoot);
  if (apiBase) await refreshStatus(new ServerClient(apiBase, token));
  panel.indexChanged();
}

async function refreshStatus(client: ServerClient): Promise<void> {
  try {
    const h = await client.health();
    status.text = h.files === 0 ? "$(type-hierarchy) CodeAtlas · 未索引" : `$(type-hierarchy) CodeAtlas · ${formatCount(h.symbols)} 符号`;
    status.tooltip = `${h.files} 个文件 · ${h.symbols} 个符号\n${h.repo}`;
  } catch { /* 状态栏保持原样 */ }
}

// ---------- 服务 ----------

/**
 * 对服务发请求；连接失败时自动重启一次再试（spec §4），再失败则报错并返回 undefined。
 */
async function withServer<T>(repoRoot: string, fn: (client: ServerClient) => Promise<T>): Promise<T | undefined> {
  const apiBase = await ensureServer(repoRoot);
  if (!apiBase) return undefined;
  try {
    return await fn(new ServerClient(apiBase, token));
  } catch (first) {
    log(`请求失败，尝试重启服务一次：${first}`);
    try {
      const recovered = await servers.recover(repoRoot);
      if (panel.isOpen) panel.show(repoRoot, recovered, token);
      return await fn(new ServerClient(recovered, token));
    } catch (second) {
      reportServerError(second);
      return undefined;
    }
  }
}

async function ensureServer(repoRoot: string): Promise<string | null> {
  try {
    if (!servers.has(repoRoot)) status.text = "$(sync~spin) CodeAtlas · 启动服务…";
    const apiBase = await servers.ensure(repoRoot);
    void refreshStatus(new ServerClient(apiBase, token));
    return apiBase;
  } catch (e) {
    reportServerError(e);
    return null;
  }
}

function reportServerError(e: unknown): void {
  if (e instanceof ServerNotFoundError) {
    void vscode.window.showErrorMessage(e.message, "打开设置").then((pick) => {
      if (pick) void vscode.commands.executeCommand("workbench.action.openSettings", "codeatlas.serverPath");
    });
    return;
  }
  status.text = "$(error) CodeAtlas · 服务不可用";
  log(`服务错误：${e}`);
  output.show(true);
}

// ---------- 工具 ----------

async function openInEditor(repoRoot: string, relativePath: string, line: number, col: number): Promise<void> {
  const uri = vscode.Uri.file(path.join(repoRoot, relativePath));
  const doc = await vscode.workspace.openTextDocument(uri);
  const editor = await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.One, preserveFocus: false });
  const pos = new vscode.Position(Math.max(0, line - 1), Math.max(0, col - 1));
  editor.selection = new vscode.Selection(pos, pos);
  editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
}

function currentFolder(): vscode.WorkspaceFolder | undefined {
  const active = vscode.window.activeTextEditor?.document.uri;
  return (active && vscode.workspace.getWorkspaceFolder(active)) ?? vscode.workspace.workspaceFolders?.[0];
}

function flash(message: string): void {
  vscode.window.setStatusBarMessage(`CodeAtlas：${message}`, 3000);
}

function log(line: string): void {
  output.appendLine(`[${new Date().toLocaleTimeString()}] ${line}`);
}

function formatCount(n: number): string {
  return n >= 1000 ? `${(n / 1000).toFixed(n >= 10000 ? 0 : 1)}k` : String(n);
}
