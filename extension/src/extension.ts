import * as crypto from "node:crypto";
import { promises as fsp } from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { AiServicePresets, apiKeySecretKey, endpointHost, isLoopbackEndpoint, matchPreset, normalizeProvider, serviceName, type AiServicePreset } from "./aiServices";
import { detectBackendsToSuggest, multiSuggestionMessage, PromptedStateKey, projectsWithCandidate, readRepoConfigProjects, suggestionMessage, type BackendCandidate, type DetectFs } from "./backendDetect";
import { ServerClient } from "./client";
import { SharedFollow } from "./cursorFollow";
import { registerCodeLens, type CodeAtlasLens } from "./codeLens";
import { registerImpactView, type ImpactView } from "./impactView";
import { registerJumpCommands } from "./jumpCommands";
import { PanelCursorFollow } from "./panelFollow";
import { IndexScheduler } from "./indexScheduler";
import { registerCallOutline, type CallOutline } from "./outlineView";
import { PanelHost } from "./panel";
import { findServerExecutable, MaxAiConcurrency, MinAiConcurrency, packageTargetMismatch, resolveProjects, ServerManager, ServerNotFoundError, ServerProcess, type AiOptions, type ProjectOptions } from "./serverManager";

const SupportedExtensions = new Set([".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".cs", ".go", ".py", ".java"]);

let servers: ServerManager;
let panel: PanelHost;
let outline: CallOutline | undefined;
let impact: ImpactView | undefined;
let follow: SharedFollow | undefined;
let lens: CodeAtlasLens | undefined;
let panelFollow: PanelCursorFollow | undefined;
let status: vscode.StatusBarItem;
let output: vscode.OutputChannel;
/** API Key 存在 VS Code SecretStorage，不进 settings.json。 */
let secrets: vscode.SecretStorage;
/** 工作区级的小状态（比如「旁边的后端问过了」）。 */
let workspaceState: vscode.Memento;
/** 本扩展宿主进程生成一次，所有服务进程共用；页面通过 init 消息拿到。 */
let token: string;
const schedulers = new Map<string, IndexScheduler>();

/** 一次「选择服务」会连写好几项设置，合并成一次重启。 */
const AiRestartDebounceMs = 400;
let aiRestartTimer: ReturnType<typeof setTimeout> | undefined;
/** 自带服务的平台与当前系统不一致时的提示（null = 一致或开发环境）。 */
let packageMismatch: string | null = null;
/** 本会话已提示过"缺 .NET，C# 用 tree-sitter"。 */
let roslynUnavailableShown = false;
/** >0 时暂不重启（选择服务的流程还没走完，比如正等用户输 key）。 */
let aiRestartSuppressed = 0;

export function activate(context: vscode.ExtensionContext): void {
  secrets = context.secrets;
  workspaceState = context.workspaceState;
  token =crypto.randomBytes(24).toString("hex");
  output = vscode.window.createOutputChannel("CodeTrail Graph");
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.command = "codeatlas.openPanel";
  status.text = "$(type-hierarchy) CodeTrail Graph";
  status.tooltip = "打开 CodeTrail Graph 面板";
  status.show();

  const log = { info: (l: string) => output.appendLine(l), error: (l: string) => output.appendLine(`[stderr] ${l}`) };
  packageMismatch = packageTargetMismatch(path.join(context.extensionPath, "server"), String(context.extension.packageJSON.version ?? "0.0.0"));
  if (packageMismatch) {
    log.error(packageMismatch);
    status.text = "$(error) CodeTrail Graph · 安装包平台不对";
    status.tooltip = packageMismatch;
    void vscode.window.showErrorMessage(packageMismatch);
  }
  servers = new ServerManager(
    () => findServerExecutable(
      path.join(context.extensionPath, "server"),
      vscode.workspace.getConfiguration("codeatlas").get<string>("serverPath") ?? null,
      process.env["PATH"]),
    log,
    (exe, root) => new ServerProcess(exe, root, token, log, aiOptions(), () => currentApiKey(), projectOptions(root)));

  panel = new PanelHost(context, {
    open: (repoRoot, rel, line, col, pos) => { panelFollow?.beginNavigation(); void openInEditor(repoRoot, rel, line, col, pos); },
    requestIndex: (repoRoot, full) => void runIndex(repoRoot, full),
    followCursor: (enabled) => panelFollow?.setEnabled(enabled),
  });

  // 侧边栏视图共用的跟随光标：「调用大纲」（光标所在方法按顺序调了什么）与「影响范围」（改它会影响谁）订阅同一个来源，
  // 光标每次移动只解析一次。服务规则同保存时增量：只在已有索引库时才拉起服务
  const connect = async (root: string): Promise<ServerClient | null> => {
    // 已在跑就直接拿地址（光标每动一次都会走到这里，不必每次刷状态栏）
    const apiBase = servers.has(root) ? await servers.ensure(root) : await ensureServer(root);
    return apiBase ? new ServerClient(apiBase, token) : null;
  };
  const viewHost = {
    connect,
    openPanel: async (root: string, symbolId: number, how: "graph" | "callers" | "relations" = "graph") => {
      const apiBase = await ensureServer(root);
      if (!apiBase) return;
      panel.show(root, apiBase, token);
      if (how === "relations") panel.showRelations(symbolId);
      else panel.focus(symbolId, how === "callers" ? "in" : undefined);
    },
    log: (line: string) => output.appendLine(`[${new Date().toLocaleTimeString()}] ${line}`),
    defaultRoot: () => primaryFolder()?.uri.fsPath,
  };
  follow = new SharedFollow({
    rootFor: (uri) => serverRootFor(uri),
    isSupported: (filePath) => SupportedExtensions.has(path.extname(filePath).toLowerCase()),
    hasServer: (root) => servers.has(root),
    connect,
  });
  context.subscriptions.push(follow);
  outline = registerCallOutline(context, viewHost, follow);
  impact = registerImpactView(context, viewHost, follow);
  // 快速跳转：面板调用图跟随光标（订阅同一个来源）、转到调用方 / 被调方 / 入口命令、方法上方的 CodeLens
  panelFollow = new PanelCursorFollow(follow, { panelRepo: () => (panel.isOpen ? panel.currentRepo : null), send: (id) => panel.cursorSymbol(id) });
  context.subscriptions.push(panelFollow);
  registerJumpCommands(context, viewHost, follow);
  lens = registerCodeLens(context, {
    rootFor: (uri) => serverRootFor(uri),
    isSupported: (filePath) => SupportedExtensions.has(path.extname(filePath).toLowerCase()),
    hasServer: (root) => servers.has(root),
    connect,
    log: viewHost.log,
  }, SupportedExtensions);

  context.subscriptions.push(
    output, status, { dispose: () => panel.dispose() }, { dispose: () => servers.stopAll() },
    vscode.commands.registerCommand("codeatlas.showSymbol", () => void showSymbolAtCursor()),
    vscode.commands.registerCommand("codeatlas.openPanel", () => void openPanel()),
    vscode.commands.registerCommand("codeatlas.showRelations", () => void showSymbolAtCursor("relations")),
    vscode.commands.registerCommand("codeatlas.indexWorkspace", () => void indexCurrentWorkspace(false)),
    vscode.commands.registerCommand("codeatlas.indexWorkspaceFull", () => void indexCurrentWorkspace(true)),
    vscode.commands.registerCommand("codeatlas.restartServer", () => void restartServer()),
    vscode.commands.registerCommand("codeatlas.ai.selectService", () => void selectAiService()),
    vscode.commands.registerCommand("codeatlas.ai.setApiKey", () => void setApiKeyCommand()),
    vscode.commands.registerCommand("codeatlas.ai.clearApiKey", () => void clearApiKeyCommand()),
    vscode.workspace.onDidSaveTextDocument((doc) => onSaved(doc)),
    // AI 设置是 serve 的启动参数（含 provider / concurrency / timeoutSeconds / includeSource），改了只能重启服务才生效
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("codeatlas.ai")) scheduleAiRestart();
      // 多项目设置同样是启动参数（--project / --no-nested）
      if (e.affectsConfiguration("codeatlas.projects") || e.affectsConfiguration("codeatlas.includeNestedRepos")) scheduleProjectsRestart();
    }),
    // 多根工作区增删 folder：其余 folder 就是外部根，变了要重启
    vscode.workspace.onDidChangeWorkspaceFolders(() => scheduleProjectsRestart()),
    { dispose: () => clearTimeout(projectsRestartTimer) },
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

// ---------- 多项目 ----------

/**
 * 主根（服务的 repoRoot）：多根工作区里固定是第一个 folder，其余 folder 作为 --project 一起索引——
 * 一个服务、一个索引库，前端 folder 的调用才能连到后端 folder 的控制器。
 */
function primaryFolder(): vscode.WorkspaceFolder | undefined {
  return vscode.workspace.workspaceFolders?.[0];
}

/** 外部根：其余 folder + codeatlas.projects（相对主根或绝对路径），已去重。 */
function externalRoots(primary: string): string[] {
  const others = (vscode.workspace.workspaceFolders ?? []).slice(1).map((f) => f.uri.fsPath);
  const configured = vscode.workspace.getConfiguration("codeatlas").get<string[]>("projects", []) ?? [];
  return resolveProjects(primary, others, Array.isArray(configured) ? configured.filter((s): s is string => typeof s === "string") : []);
}

/** codeatlas.projects / codeatlas.includeNestedRepos + 其余 folder → serve 的 --project / --no-nested。每次拉进程时现读。 */
function projectOptions(primary: string): ProjectOptions {
  return {
    projects: externalRoots(primary),
    includeNested: vscode.workspace.getConfiguration("codeatlas").get<boolean>("includeNestedRepos", true),
  };
}

/** 文件归哪个服务：在任一工作区 folder 或外部根之下 → 主根；否则 undefined。 */
function serverRootFor(uri: vscode.Uri): string | undefined {
  const primary = primaryFolder()?.uri.fsPath;
  if (!primary) return undefined;
  if (vscode.workspace.getWorkspaceFolder(uri)) return primary;
  const file = uri.fsPath;
  const under = (root: string) => {
    const rel = path.relative(root, file);
    return rel !== "" && !rel.startsWith("..") && !path.isAbsolute(rel);
  };
  return externalRoots(primary).some(under) ? primary : undefined;
}

const ProjectsRestartDebounceMs = 400;
let projectsRestartTimer: ReturnType<typeof setTimeout> | undefined;

function scheduleProjectsRestart(): void {
  clearTimeout(projectsRestartTimer);
  projectsRestartTimer = setTimeout(() => { projectsRestartTimer = undefined; void onProjectsChanged(); }, ProjectsRestartDebounceMs);
}

/**
 * 外部根 / 嵌套仓库设置变了：重启本会话拉起过的服务，再跑一次增量索引——新加的根要进库、去掉的根要删掉，
 * 不然地图和设置对不上。主根变了（第一个 folder 换了）旧服务停掉，等下次用到时按新主根启动。
 */
async function onProjectsChanged(): Promise<void> {
  const repos = servers.repos();
  if (repos.length === 0) return;
  const primary = primaryFolder()?.uri.fsPath;
  for (const root of repos) {
    if (root !== primary) {
      log(`主根已变为 ${primary ?? "（无）"}，停掉 ${root} 的服务`);
      servers.stop(root);
      continue;
    }
    const p = projectOptions(root);
    // 自动检测加进来的后端要跑 Roslyn，只有全量重建才跑，这里直接全量
    const full = fullIndexAfterProjectsChange.delete(root);
    log(`多项目设置变化（外部根 ${p.projects.length ? p.projects.join("、") : "无"}；嵌套仓库 ${p.includeNested ? "纳入" : "不纳入"}），重启服务并${full ? "全量重建" : "增量索引"}`);
    try {
      const apiBase = await servers.restart(root);
      if (panel.isOpen && panel.currentRepo === root) panel.show(root, apiBase, token);
      await runIndex(root, full);
    } catch (e) {
      reportServerError(e);
    }
  }
}

// ---------- 自动检测旁边的后端 ----------

/** 本会话里已经检测过的主根：检测只在第一次为它启动服务时做一次。 */
const backendChecked = new Set<string>();
/** 下一次多项目设置变化后跑全量重建（而不是增量）的主根。 */
const fullIndexAfterProjectsChange = new Set<string>();

const nodeFs: DetectFs = {
  async readDir(dir) {
    try {
      // 符号链接 isDirectory() 为 false，不会顺着链接绕圈
      return (await fsp.readdir(dir, { withFileTypes: true })).map((d) => ({ name: d.name, isDirectory: d.isDirectory() }));
    } catch {
      return [];
    }
  },
  async readFile(file) {
    try { return await fsp.readFile(file, "utf8"); } catch { return null; }
  },
};

/** 为主根启动服务前调：不阻塞启动，检测与提示在后台走。 */
function maybeSuggestBackend(repoRoot: string): void {
  if (repoRoot !== primaryFolder()?.uri.fsPath || backendChecked.has(repoRoot)) return;
  backendChecked.add(repoRoot);
  void suggestBackend(repoRoot).catch((e) => log(`检测旁边的后端失败：${e}`));
}

/**
 * 单独打开前端仓库时，旁边（上级目录或同级目录）有 .NET 后端就问一次要不要一起索引。
 * 条件见 backendDetect.detectBackendsToSuggest；选「一起索引」或「不用」后本工作区不再问，直接关掉通知下次会话再问。
 */
async function suggestBackend(primary: string): Promise<void> {
  const configured = vscode.workspace.getConfiguration("codeatlas").get<string[]>("projects", []) ?? [];
  const candidates = await detectBackendsToSuggest(nodeFs, primary, {
    configuredProjects: Array.isArray(configured) ? configured.filter((s): s is string => typeof s === "string") : [],
    repoConfigProjects: await readRepoConfigProjects(nodeFs, primary),
    otherFolders: Math.max(0, (vscode.workspace.workspaceFolders?.length ?? 1) - 1),
    alreadyAsked: workspaceState.get<boolean>(PromptedStateKey, false),
  });
  if (candidates.length === 0) return;
  log(`检测到旁边的 .NET 后端：${candidates.map((c) => `${c.dir}（${c.sln}）`).join("、")}`);
  const first = candidates[0]!;
  const pick = await vscode.window.showInformationMessage(candidates.length === 1 ? suggestionMessage(first) : multiSuggestionMessage(candidates), "一起索引", "不用");
  if (pick === undefined) return;
  if (pick === "不用") {
    await workspaceState.update(PromptedStateKey, true);
    log("用户选择不一起索引旁边的后端，本工作区不再询问");
    return;
  }
  let chosen: BackendCandidate = first;
  if (candidates.length > 1) {
    const item = await vscode.window.showQuickPick(
      candidates.map((c) => ({ label: c.name, description: c.sln, detail: `${c.dir} · 写入 codeatlas.projects："${c.relative}"`, candidate: c })),
      { title: "CodeTrail Graph: 选择一起索引的 .NET 后端", placeHolder: "选一个；以后可在设置 codeatlas.projects 里增删", ignoreFocusOut: true, matchOnDetail: true },
    );
    if (!item) return;
    chosen = item.candidate;
  }
  await workspaceState.update(PromptedStateKey, true);
  await addBackendProject(primary, chosen);
}

/**
 * 把后端写进工作区级 codeatlas.projects（单文件夹工作区即 .vscode/settings.json）。
 * 设置变化会让服务带着新的 --project 重启；新加的外部根要全量重建才跑 Roslyn，所以标记这一次走全量。
 */
async function addBackendProject(primary: string, c: BackendCandidate): Promise<void> {
  // 先等正在启动的服务起来：设置变化触发的重启只作用于已在运行的服务，这样新进程才带上 --project
  await ensureServer(primary);
  fullIndexAfterProjectsChange.add(primary);
  const cfg = vscode.workspace.getConfiguration("codeatlas");
  const current = cfg.inspect<string[]>("projects")?.workspaceValue;
  await cfg.update("projects", projectsWithCandidate(Array.isArray(current) ? current : [], c), vscode.ConfigurationTarget.Workspace);
  log(`已把 ${c.relative}（${c.dir}）写进工作区设置 codeatlas.projects，重启服务后全量重建`);
  status.text = "$(sync~spin) CodeTrail Graph · 一起索引后端…";
  void vscode.window.showInformationMessage(`正在一起索引后端 ${c.name}（全量重建，含 Roslyn），约 2–3 分钟。完成后项目地图能显示前端页面到后端控制器的调用链。`);
  if (!servers.has(primary)) {
    // 服务没起来，设置变化不会触发重启：直接跑（会按新设置拉起服务）
    fullIndexAfterProjectsChange.delete(primary);
    await runIndex(primary, true);
  }
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

/** 以光标处符号打开面板：graph = 调用图聚焦；relations = 模块关系（以它所属的类为中心）。 */
async function showSymbolAtCursor(target: "graph" | "relations" = "graph"): Promise<void> {
  const editor = vscode.window.activeTextEditor;
  if (!editor) { flash("没有打开的编辑器"); return; }
  // 多根工作区 / 外部根里的文件都归主根的服务：服务按绝对路径换算成 ../other/… 去查
  const repoRoot = serverRootFor(editor.document.uri);
  if (!repoRoot) { flash("文件不在任何工作区文件夹或 codeatlas.projects 里"); return; }
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
    const pick = await vscode.window.showInformationMessage("这个工作区还没有 CodeTrail Graph 索引。", "建立索引");
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
  if (target === "relations") panel.showRelations(symbol.id);
  else panel.focus(symbol.id);
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
    flash("CodeTrail Graph 服务已重启");
    void refreshStatus(new ServerClient(apiBase, token));
  } catch (e) {
    reportServerError(e);
  }
}

// ---------- AI 服务 ----------

type ServiceItem = vscode.QuickPickItem & { preset?: AiServicePreset; custom?: true };

/** 「CodeTrail Graph: 选择 AI 服务」：选预设 → 写 provider / endpoint / model → 联网服务没 key 就紧接着让用户输。 */
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
    title: "CodeTrail Graph: 选择 AI 服务",
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
        void vscode.window.showWarningMessage(`已切换到 ${p.label}，但还没有 API Key，叙述会不可用。稍后可运行「CodeTrail Graph: 设置 AI API Key」。`);
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

/** 「CodeTrail Graph: 设置 AI API Key」：给当前 provider + 端点主机存 key，然后重启服务。 */
async function setApiKeyCommand(): Promise<void> {
  const ai = aiOptions();
  if (!(await promptApiKey(ai.provider ?? "openai", ai.endpoint))) return;
  flash("API Key 已保存，服务重启中");
  scheduleAiRestart();
}

/** 「CodeTrail Graph: 清除 AI API Key」：删掉当前服务的 key，然后重启服务。 */
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
  const repoRoot = serverRootFor(doc.uri);
  if (!repoRoot) return;
  // 只对本会话里已经用过 CodeTrail Graph 的文件夹做增量；绝不因为一次保存就静默拉起服务、往仓库写索引
  if (!servers.has(repoRoot)) return;
  schedulerFor(repoRoot).notifyChange();
}

function schedulerFor(repoRoot: string): IndexScheduler {
  let s = schedulers.get(repoRoot);
  if (!s) {
    s = new IndexScheduler((full) => doIndex(repoRoot, full), 1500, (e) => log(`索引失败：${e}`));
    schedulers.set(repoRoot, s);
  }
  return s;
}

async function runIndex(repoRoot: string, full: boolean): Promise<void> {
  try {
    await schedulerFor(repoRoot).runNow(full);
  } catch (e) {
    reportServerError(e);
  }
}

async function doIndex(repoRoot: string, full: boolean): Promise<void> {
  let started = await withServer(repoRoot, (c) => c.startIndex(full));
  while (started === "busy") {
    log("索引已在进行中，等待结束后执行本次请求");
    if (await withServer(repoRoot, (c) => c.waitForIndex(() => {})) === undefined) return;
    started = await withServer(repoRoot, (c) => c.startIndex(full));
  }
  if (started === undefined) return;
  status.text = "$(sync~spin) CodeTrail Graph · 索引中";
  const final = await withServer(repoRoot, (c) => c.waitForIndex((s) => {
    if (!s.running) return;
    if (s.phase === "roslyn") status.text = s.total ? `$(sync~spin) CodeTrail Graph · Roslyn ${s.done}/${s.total}` : "$(sync~spin) CodeTrail Graph · Roslyn 编译中";
    else if (s.total) status.text = `$(sync~spin) CodeTrail Graph · ${s.phase} ${s.done}/${s.total}`;
  }));
  if (final === undefined) return;
  if (final.error) {
    status.text = "$(warning) CodeTrail Graph · 索引失败";
    log(`索引失败：${final.error}`);
    return;
  }
  const r = final.lastReport;
  if (r) log(`索引完成：${r.filesScanned} 文件，${r.totalSymbols} 符号，${r.totalEdges} 边，${r.elapsedSeconds.toFixed(1)}s`);
  if (r?.roslyn) {
    if (r.roslyn.error) log(`Roslyn 未运行：${r.roslyn.error}`);
    // 缺 .NET 只提示一次：地图照常能用，只是 C# 的调用精度低一些
    if (r.roslyn.unavailable && !roslynUnavailableShown) {
      roslynUnavailableShown = true;
      void vscode.window.showInformationMessage(`CodeTrail Graph：${r.roslyn.error ?? "C# 精确语义不可用"}`);
    }
    else log(`Roslyn 完成：${r.roslyn.files} 文件，合并 ${r.roslyn.symbolsMerged} 符号，${r.roslyn.edges} 条精确边，${r.roslyn.elapsedSeconds.toFixed(1)}s`);
  }
  if (r?.timings?.length) log(`阶段耗时：${r.timings.map((t) => `${t.phase} ${t.seconds.toFixed(1)}s`).join("  ")}`);
  const apiBase = await ensureServer(repoRoot);
  if (apiBase) await refreshStatus(new ServerClient(apiBase, token));
  panel.indexChanged();
  outline?.indexChanged();
  impact?.indexChanged();
  follow?.indexChanged();
  lens?.indexChanged();
}

async function refreshStatus(client: ServerClient): Promise<void> {
  try {
    const h = await client.health();
    status.text = h.files === 0 ? "$(type-hierarchy) CodeTrail Graph · 未索引" : `$(type-hierarchy) CodeTrail Graph · ${formatCount(h.symbols)} 符号`;
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
    if (!servers.has(repoRoot)) {
      status.text = "$(sync~spin) CodeTrail Graph · 启动服务…";
      maybeSuggestBackend(repoRoot);
    }
    const apiBase = await servers.ensure(repoRoot);
    void refreshStatus(new ServerClient(apiBase, token));
    return apiBase;
  } catch (e) {
    reportServerError(e);
    return null;
  }
}

function reportServerError(e: unknown): void {
  // 装错平台的包时，"找不到服务"的真正原因是平台不对，直接说该装哪个
  if (packageMismatch) {
    void vscode.window.showErrorMessage(packageMismatch);
    return;
  }
  if (e instanceof ServerNotFoundError) {
    void vscode.window.showErrorMessage(e.message, "打开设置").then((pick) => {
      if (pick) void vscode.commands.executeCommand("workbench.action.openSettings", "codeatlas.serverPath");
    });
    return;
  }
  status.text = "$(error) CodeTrail Graph · 服务不可用";
  log(`服务错误：${e}`);
  output.show(true);
}

// ---------- 工具 ----------

async function openInEditor(repoRoot: string, relativePath: string, line: number, col: number, charPos?: number | null): Promise<void> {
  const uri = vscode.Uri.file(path.join(repoRoot, relativePath));
  const doc = await vscode.workspace.openTextDocument(uri);
  // 有字符偏移且换算出的行与记录的行一致时用它（落在调用名上）；文件改过、对不上时退回行列
  let pos = new vscode.Position(Math.max(0, line - 1), Math.max(0, col - 1));
  if (typeof charPos === "number" && charPos >= 0) {
    const p = doc.positionAt(charPos);
    if (p.line === line - 1) pos = p;
  }
  // selection 选项走"导航"记录：Alt+← 能回到跳转前（同一文件相隔很近也单独记一条）
  await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.One, preserveFocus: false, selection: new vscode.Range(pos, pos) });
}

/** 面板 / 索引 / 重启命令作用的文件夹：多根工作区里固定是主根（第一个 folder），其余 folder 由它的服务一起索引。 */
function currentFolder(): vscode.WorkspaceFolder | undefined {
  return primaryFolder();
}

function flash(message: string): void {
  vscode.window.setStatusBarMessage(`CodeTrail Graph：${message}`, 3000);
}

function log(line: string): void {
  output.appendLine(`[${new Date().toLocaleTimeString()}] ${line}`);
}

function formatCount(n: number): string {
  return n >= 1000 ? `${(n / 1000).toFixed(n >= 10000 ? 0 : 1)}k` : String(n);
}
