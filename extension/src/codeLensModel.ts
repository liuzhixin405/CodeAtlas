// 编辑器 CodeLens（方法上方"被 N 处调用 · 调用 M 个 · 影响范围"）的纯逻辑：文案、行号漂移时隐藏、按文件缓存。
// 不 import vscode，方便单测；codeLens.ts 把这里算出的 LensSpec 原样搬进 vscode.CodeLens。
import type { FileLensDto, SymbolDto, SymbolLensDto } from "../webview/types";

export type LensKind = "entry" | "callers" | "tests" | "callees" | "impact";

export interface LensSpec {
  kind: LensKind;
  title: string;
  tooltip: string;
  /** 点了有动作；false 时只是一行字（入口、没有调用方、调用 0 个）。 */
  clickable: boolean;
}

/** 一个方法上方的一行 CodeLens。 */
export interface MethodLenses {
  symbol: SymbolDto;
  /** 放在哪一行（1 起，= symbol.startLine）。 */
  line: number;
  lenses: LensSpec[];
}

export const StaleSuffix = "（索引已过期）";
const StaleTooltip = "文件在索引之后改过，数字和行号可能对不上；保存后会增量索引。";

/** "被 N 处调用" 那一条的标题：调用方个数和处数不同时两个都写；都是 0 写"没有调用方"（找死代码用）。 */
export function callersTitle(callers: number, callSites: number): string {
  const sites = Math.max(callers, callSites);
  if (sites === 0) return "没有调用方";
  if (callers > 0 && callers !== sites) return `被 ${callers} 个方法调用 ${sites} 处`;
  return `被 ${sites} 处调用`;
}

/**
 * 一个方法的 CodeLens，从左到右：入口 · 被 N 处调用 · 测试 K · 调用 M 个 · 影响范围。
 * 过期（stale）时只在调用计数那一条后面加"（索引已过期）"，一行只加一次。
 */
export function lensSpecs(item: SymbolLensDto, stale: boolean): LensSpec[] {
  const out: LensSpec[] = [];
  if (item.isEntry) out.push({ kind: "entry", title: "入口", tooltip: "这个方法本身是入口（HTTP / 任务 / 命令 / 消息处理…），由框架调用。", clickable: false });

  const sites = Math.max(item.callers, item.callSites);
  const callersTip = sites === 0
    ? `索引里没有找到调用方（不含测试）${item.isEntry ? "；它是入口，由框架调用" : "：可能是死代码，也可能经反射 / 依赖注入 / 动态调用"}。`
    : `直接调用方 ${item.callers} 个，共 ${sites} 处（不含测试）。点开列出调用方，选一个跳到调用处。`;
  out.push({
    kind: "callers",
    title: callersTitle(item.callers, item.callSites) + (stale ? StaleSuffix : ""),
    tooltip: stale ? `${callersTip}\n${StaleTooltip}` : callersTip,
    clickable: sites > 0,
  });

  if (item.testCallers > 0) {
    out.push({ kind: "tests", title: `测试 ${item.testCallers}`, tooltip: `测试代码里有 ${item.testCallers} 个调用方。点开列出全部调用方（含测试）。`, clickable: true });
  }

  out.push({
    kind: "callees",
    title: `调用 ${item.callees} 个`,
    tooltip: item.callees > 0 ? `按源码顺序调用了 ${item.callees} 个（调用大纲第一层，含框架调用）。点开选一个跳到调用处。` : "这个方法里没有找到调用。",
    clickable: item.callees > 0,
  });

  out.push({ kind: "impact", title: "影响范围", tooltip: "在侧边栏「影响范围」里以这个方法为根：谁调用了它、一路影响到哪些入口。", clickable: true });
  return out;
}

/** 声明行往下最多看几行找名字（C# 特性 / Java 注解 / TS、Python 装饰器让 startLine 落在声明上面几行）。 */
export const DeclarationLookahead = 3;

/** 特性 / 注解 / 装饰器行：`[HttpPost]`、`@Override`、`@Injectable()`。 */
function isDecoration(text: string): boolean {
  const t = text.trimStart();
  return t.startsWith("[") || t.startsWith("@");
}

/**
 * 声明还在原处吗：startLine 那一行包含方法名；或者 startLine 起是特性 / 注解 / 装饰器行，
 * 紧接着（最多往下看 DeclarationLookahead 行、不超过 endLine）有一行包含方法名。
 * 文件改过没保存 / 索引过期时用：行号已漂走的那条就不显示，免得挂在别的地方。lineText 取不到（超出文档）返回 undefined。
 */
export function declarationIntact(symbol: SymbolDto, lineText: (line: number) => string | undefined): boolean {
  if (!symbol.name) return true;
  const last = Math.max(symbol.startLine, Math.min(symbol.endLine || symbol.startLine, symbol.startLine + DeclarationLookahead));
  for (let line = symbol.startLine; line <= last; line++) {
    const text = lineText(line);
    if (text === undefined) return false;
    if (text.includes(symbol.name)) return true;
    if (!isDecoration(text)) return false;
  }
  return false;
}

/**
 * 整个文件的 CodeLens。checkLines：文件在编辑没保存或索引过期时为 true，逐条核对声明行，对不上的不显示；
 * 文件干净且索引新鲜时行号可信，不核对（构造函数名、装饰器等不会被误杀）。
 */
export function fileLenses(dto: FileLensDto, opts: { checkLines: boolean; lineText: (line: number) => string | undefined }): MethodLenses[] {
  const out: MethodLenses[] = [];
  for (const item of dto.symbols) {
    const s = item.symbol;
    if (!(s.startLine >= 1)) continue;
    if (opts.checkLines && !declarationIntact(s, opts.lineText)) continue;
    out.push({ symbol: s, line: s.startLine, lenses: lensSpecs(item, dto.stale) });
  }
  return out;
}

// ---------- 缓存 ----------

/**
 * 按文件缓存 lens 接口的结果（一次请求整个文件），再按文档版本缓存算好的 CodeLens：
 * - 数据只在保存这个文件 / 索引完成时作废（invalidate），编辑中不重新请求——服务端的数字只在索引后才会变；
 * - 同一文档版本重复调 provideCodeLenses 直接返回上次算的结果；版本变了（在编辑）只重算行号核对，不发请求。
 * 请求失败不缓存，下次再试。
 */
export class LensCache<R> {
  private readonly data = new Map<string, DataEntry>();
  private readonly memo = new Map<string, { entry: DataEntry; version: number; r: R | null }>();

  constructor(private readonly fetch: (filePath: string) => Promise<FileLensDto | null>) {}

  /** 文件的 lens 数据（缓存）。 */
  get(filePath: string): Promise<FileLensDto | null> {
    return this.entry(filePath).p;
  }

  /** 这个文档版本的 CodeLens（缓存）；compute 拿到数据算一次。数据为 null（服务不可用 / 没索引）结果也是 null。 */
  async lenses(filePath: string, version: number, compute: (dto: FileLensDto) => R): Promise<R | null> {
    const entry = this.entry(filePath);
    const m = this.memo.get(filePath);
    if (m && m.entry === entry && m.version === version) return m.r;
    const dto = await entry.p;
    const r = dto ? compute(dto) : null;
    // 期间作废过（数据换了）：结果照样返回，但不记
    if (this.data.get(filePath) === entry) this.memo.set(filePath, { entry, version, r });
    return r;
  }

  /** 作废一个文件（保存）或全部（索引完成、服务重启）。 */
  invalidate(filePath?: string): void {
    if (filePath === undefined) {
      this.data.clear();
      this.memo.clear();
      return;
    }
    this.data.delete(filePath);
    this.memo.delete(filePath);
  }

  private entry(filePath: string): DataEntry {
    const hit = this.data.get(filePath);
    if (hit) return hit;
    const entry: DataEntry = { p: this.fetch(filePath) };
    this.data.set(filePath, entry);
    entry.p.catch(() => {
      if (this.data.get(filePath) !== entry) return;
      this.data.delete(filePath);
      this.memo.delete(filePath);
    });
    return entry;
  }
}

interface DataEntry {
  p: Promise<FileLensDto | null>;
}
