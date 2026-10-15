import * as path from "node:path";

/**
 * 单独打开一个前端仓库时，自动发现旁边的 .NET 后端并提示「一起索引」。
 * 全是纯逻辑：文件系统经 DetectFs 回调进来（扩展里是 node:fs，测试里是假的），不碰 vscode。
 */

export interface DirEntry {
  name: string;
  isDirectory: boolean;
}

export interface DetectFs {
  /** 列目录；不存在或读不了返回空数组。 */
  readDir(dir: string): Promise<DirEntry[]>;
  /** 读文本文件；不存在或读不了返回 null。 */
  readFile(file: string): Promise<string | null>;
}

export interface BackendCandidate {
  /** 后端目录的绝对路径。 */
  dir: string;
  /** 目录名，提示里用。 */
  name: string;
  /** 目录根下的解决方案文件名（多个时取字典序第一个）。 */
  sln: string;
  /** 写进 codeatlas.projects 的值：相对主根、用 / 分隔，如 ".." 或 "../ccs-backend"。 */
  relative: string;
  /** parent = 主根的上级目录；sibling = 与主根同级的目录。 */
  relation: "parent" | "sibling";
}

/** 判断「像前端」的依赖名：package.json 的 dependencies / devDependencies / peerDependencies 里有其一。 */
export const FrontendDependencies = ["next", "react", "vue", "@angular/core", "vite"] as const;

/** 找 package.json 往下看几层（主根本身是第 0 层）。 */
export const FrontendScanDepth = 3;
/** 判断主根自己是不是 .NET 后端时，找 *.sln / *.slnx 往下看几层。 */
export const SolutionScanDepth = 2;
/** 同级目录候选最多列几个。 */
export const MaxSiblingCandidates = 3;

/** workspaceState 里记「本工作区问过了」的键。 */
export const PromptedStateKey = "codeatlas.backendPrompted";

/** 扫描时跳过的目录：依赖、构建产物、版本库与各种以点开头的工具目录（.git、.claude/worktrees 等）。 */
const SkipDirs = new Set(["node_modules", "bin", "obj", "dist", "build", "out", "target", "coverage"]);
function skipDir(name: string): boolean {
  return name.startsWith(".") || SkipDirs.has(name.toLowerCase());
}

function isSolutionFile(name: string): boolean {
  const n = name.toLowerCase();
  return n.endsWith(".sln") || n.endsWith(".slnx");
}

/** package.json 文本里是否声明了前端框架依赖；解析不了按否。 */
export function hasFrontendDependency(packageJson: string): boolean {
  let obj: unknown;
  try { obj = JSON.parse(packageJson); } catch { return false; }
  if (!obj || typeof obj !== "object") return false;
  const rec = obj as Record<string, unknown>;
  for (const field of ["dependencies", "devDependencies", "peerDependencies"]) {
    const deps = rec[field];
    if (deps && typeof deps === "object" && FrontendDependencies.some((d) => Object.prototype.hasOwnProperty.call(deps, d))) return true;
  }
  return false;
}

/** 广度优先遍历 root 下 0..maxDepth 层目录（跳过 skipDir），visit 返回 true 时停。 */
async function walk(fs: DetectFs, p: path.PlatformPath, root: string, maxDepth: number, visit: (dir: string, entries: DirEntry[]) => Promise<boolean> | boolean): Promise<boolean> {
  let level = [root];
  for (let depth = 0; depth <= maxDepth && level.length > 0; depth++) {
    const next: string[] = [];
    for (const dir of level) {
      const entries = await fs.readDir(dir);
      if (await visit(dir, entries)) return true;
      if (depth < maxDepth) for (const e of entries) if (e.isDirectory && !skipDir(e.name)) next.push(p.join(dir, e.name));
    }
    level = next;
  }
  return false;
}

/** 主根下 FrontendScanDepth 层内有声明前端框架依赖的 package.json。 */
export function looksLikeFrontend(fs: DetectFs, root: string, p: path.PlatformPath = path): Promise<boolean> {
  return walk(fs, p, root, FrontendScanDepth, async (dir, entries) => {
    if (!entries.some((e) => !e.isDirectory && e.name === "package.json")) return false;
    const text = await fs.readFile(p.join(dir, "package.json"));
    return text !== null && hasFrontendDependency(text);
  });
}

/** 主根下 SolutionScanDepth 层内有 *.sln / *.slnx（主根自己就是 .NET 仓库）。 */
export function containsSolution(fs: DetectFs, root: string, p: path.PlatformPath = path): Promise<boolean> {
  return walk(fs, p, root, SolutionScanDepth, (_dir, entries) => entries.some((e) => !e.isDirectory && isSolutionFile(e.name)));
}

/** 目录根下（不往下找）的解决方案文件名，取字典序第一个；没有返回 null。 */
export async function solutionAtRoot(fs: DetectFs, dir: string): Promise<string | null> {
  const slns = (await fs.readDir(dir)).filter((e) => !e.isDirectory && isSolutionFile(e.name)).map((e) => e.name).sort();
  return slns[0] ?? null;
}

/** codeatlas.projects 的写法：相对主根、统一用 /。 */
export function relativeProjectPath(primary: string, dir: string, p: path.PlatformPath = path): string {
  const rel = p.relative(primary, dir);
  return (rel === "" ? "." : rel).split(p.sep).join("/");
}

/**
 * 候选后端目录：
 * - 上级目录根下有 *.sln / *.slnx → 只给它一个（它已经包含了所有同级目录，再列同级是重复）；
 * - 否则同级目录里根下有 *.sln / *.slnx 的，按名字排序最多 MaxSiblingCandidates 个。
 * 只看上级目录根与它的直接子目录根，不深扫。
 */
export async function findBackendCandidates(fs: DetectFs, primary: string, p: path.PlatformPath = path, platform: NodeJS.Platform = process.platform): Promise<BackendCandidate[]> {
  const parent = p.dirname(primary);
  if (parent === primary) return [];
  const parentSln = await solutionAtRoot(fs, parent);
  if (parentSln) return [{ dir: parent, name: p.basename(parent) || parent, sln: parentSln, relative: relativeProjectPath(primary, parent, p), relation: "parent" }];

  // 排除主根自己：Windows / macOS 文件系统不分大小写（fsPath 的大小写可能和磁盘上不一样），Linux 上 web 与 Web 是两个目录
  const fold = platform === "win32" || platform === "darwin";
  const nameKey = (s: string) => (fold ? s.toLowerCase() : s);
  const self = nameKey(p.basename(primary));
  const siblings = (await fs.readDir(parent))
    .filter((e) => e.isDirectory && !skipDir(e.name) && nameKey(e.name) !== self)
    .map((e) => e.name)
    .sort();
  const out: BackendCandidate[] = [];
  for (const name of siblings) {
    if (out.length >= MaxSiblingCandidates) break;
    const dir = p.join(parent, name);
    const sln = await solutionAtRoot(fs, dir);
    if (sln) out.push({ dir, name, sln, relative: relativeProjectPath(primary, dir, p), relation: "sibling" });
  }
  return out;
}

/** 提示前要满足的工作区条件（由扩展从 vscode API 读出来）。 */
export interface PromptContext {
  /** codeatlas.projects 的当前值（各层合并后）。 */
  configuredProjects: readonly string[];
  /** 仓库 .codeatlas/config.json 里的 projects（没有文件为空）。 */
  repoConfigProjects: readonly string[];
  /** 多根工作区里除主根外的文件夹个数。 */
  otherFolders: number;
  /** 本工作区已经问过（workspaceState）。 */
  alreadyAsked: boolean;
}

/** 读仓库的 .codeatlas/config.json 里的 projects；没有或格式不对返回空数组。 */
export async function readRepoConfigProjects(fs: DetectFs, primary: string, p: path.PlatformPath = path): Promise<string[]> {
  const text = await fs.readFile(p.join(primary, ".codeatlas", "config.json"));
  if (text === null) return [];
  try {
    const obj = JSON.parse(text) as { projects?: unknown };
    return Array.isArray(obj.projects) ? obj.projects.filter((s): s is string => typeof s === "string" && s.trim() !== "") : [];
  } catch {
    return [];
  }
}

/**
 * 要不要提示、提示哪些候选。返回空数组 = 不提示。条件全部满足才提示：
 * 没问过；codeatlas.projects 与 .codeatlas/config.json 都没配外部根；不是多根工作区；
 * 主根像前端（3 层内 package.json 有 next / react / vue / @angular/core / vite）；
 * 主根自己不是 .NET 后端（2 层内没有 *.sln / *.slnx）；旁边找得到带解决方案的目录。
 * 便宜的条件先判，文件系统扫描放后面。
 */
export async function detectBackendsToSuggest(fs: DetectFs, primary: string, ctx: PromptContext, p: path.PlatformPath = path, platform: NodeJS.Platform = process.platform): Promise<BackendCandidate[]> {
  if (ctx.alreadyAsked) return [];
  if (ctx.configuredProjects.some((s) => s.trim() !== "")) return [];
  if (ctx.repoConfigProjects.length > 0) return [];
  if (ctx.otherFolders > 0) return [];
  if (!(await looksLikeFrontend(fs, primary, p))) return [];
  if (await containsSolution(fs, primary, p)) return [];
  return findBackendCandidates(fs, primary, p, platform);
}

/** 单个候选的提示文字。 */
export function suggestionMessage(c: BackendCandidate): string {
  return `检测到旁边的 .NET 后端 ${c.name}（${c.sln}）。一起索引后，项目地图能显示前端页面到后端控制器的完整调用链。是否一起索引？`;
}

/** 多个候选时的提示文字（点「一起索引」后再用 QuickPick 选一个）。 */
export function multiSuggestionMessage(cs: readonly BackendCandidate[]): string {
  return `检测到旁边有 ${cs.length} 个 .NET 后端（${cs.map((c) => c.name).join("、")}）。一起索引后，项目地图能显示前端页面到后端控制器的完整调用链。是否一起索引？`;
}

/** 选定后写回的 codeatlas.projects：原值后面追加，已有（同写法）不重复加。 */
export function projectsWithCandidate(current: readonly string[], c: BackendCandidate): string[] {
  const list = current.filter((s) => s.trim() !== "");
  return list.includes(c.relative) ? list : [...list, c.relative];
}
