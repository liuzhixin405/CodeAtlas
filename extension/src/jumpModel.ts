// 快速跳转（CodeLens 与"转到调用方 / 被调方 / 受影响的入口"命令）的纯逻辑：服务结果 → QuickPick 项，跳转目标 → 编辑器位置。
// 不 import vscode，方便单测；jumpCommands.ts 把这里算出的项原样搬进 vscode.QuickPickItem。
// 名字、ctx 文案、跳转目标与调用大纲 / 影响范围共用 outlineModel.ts、impactModel.ts。
import type { ImpactCallerDto, ImpactCallerKind, ImpactDto, ImpactEntryDto, OutlineDto, OutlineStepDto } from "../webview/types";
import { entryLabel } from "./impactModel";
import { hasDefinition, stepDescription, trustPos, type JumpTarget } from "./outlineModel";

/** 一个 QuickPick 项（vscode.QuickPickItem 的子集）+ 选中后跳到哪。 */
export interface JumpPickItem {
  label: string;
  description: string;
  detail: string;
  /** 选中：跳到这里（调用处 / 入口）。 */
  target: JumpTarget;
  /** 行内按钮"转到定义"跳到这里；null = 没有按钮（外部符号）。 */
  definition: JumpTarget | null;
}

/** QuickPick 里调用方式的中文（直接调用也写出来，和别的方式并列时好区分）。 */
export const CallerKindPickLabels: Record<ImpactCallerKind, string> = {
  call: "调用",
  impl: "经接口",
  dispatch: "经分派",
  new: "构造",
  hop: "跨服务",
};

/** 行号列表最多写几个，再多写"等 N 处"。 */
export const MaxPickLines = 6;

export function linesText(lines: number[]): string {
  if (lines.length === 0) return "";
  const shown = lines.slice(0, MaxPickLines).map((l) => `L${l}`).join(", ");
  return lines.length > MaxPickLines ? `${shown} 等 ${lines.length} 处` : shown;
}

/** 调用方 → 项：label 调用方显示名，description 调用方式 + 各处行号，detail 文件路径；选中跳到第一处调用。 */
export function callerPickItem(c: ImpactCallerDto): JumpPickItem {
  const kind = CallerKindPickLabels[c.kind] ?? c.kind;
  const lines = linesText(c.sites.map((s) => s.line));
  const first = c.sites[0];
  const definition: JumpTarget = { path: c.caller.path, line: c.caller.startLine, col: c.caller.startCol, pos: null };
  return {
    label: c.label,
    description: lines ? `${kind} · ${lines}` : kind,
    detail: c.caller.path,
    // 没有调用处（老服务）就退回调用方的定义
    target: first ? { path: c.caller.path, line: first.line, col: null, pos: first.pos ?? null } : definition,
    definition,
  };
}

export function callerPickItems(impact: ImpactDto): JumpPickItem[] {
  return impact.callers.map(callerPickItem);
}

/** 大纲第一层的一步 → 项：label 被调方（同调用大纲），description 种类 / ctx / 行号（同调用大纲），detail 定义处；选中跳到调用处。 */
export function calleePickItem(step: OutlineStepDto, containerPath: string): JumpPickItem {
  const t = step.target;
  const def = hasDefinition(t) && step.kind !== "external" ? { path: t.path, line: t.startLine, col: t.startCol, pos: null } : null;
  return {
    label: `${step.label}${step.repeat > 1 ? ` ×${step.repeat}` : ""}`,
    description: stepDescription(step, false),
    detail: def ? `${def.path}:${def.line}` : step.kind === "ambiguous" ? `${step.children.length} 个候选` : "仓库外",
    target: { path: containerPath, line: step.line, col: null, pos: step.pos ?? null },
    definition: def,
  };
}

/**
 * "调用 N 个"的列表：第一层每一步一项；分发调用（Send / Publish）下的 dispatch 子步骤、接口调用下的实现（impl）各补一项
 * 「↳ Handler · 经分派」「↳ 实现 · 经接口」——它们不是直接引用，选中直接跳到 Handler / 实现的定义（跳到调用处只会停在 Send 那一行）。
 * 第一层本身就是 dispatch 的（没经过分发调用的分派边）同样跳到定义。
 */
export function calleePickItems(outline: OutlineDto): JumpPickItem[] {
  const out: JumpPickItem[] = [];
  for (const s of outline.steps) {
    const item = calleePickItem(s, outline.symbol.path);
    out.push(s.kind === "dispatch" && item.definition ? { ...item, target: item.definition } : item);
    for (const c of s.children) {
      if (c.kind !== "dispatch" && c.kind !== "impl") continue;
      const sub = calleePickItem(c, outline.symbol.path);
      if (!sub.definition) continue;
      out.push({
        ...sub,
        label: `↳ ${sub.label}`,
        description: `${c.kind === "dispatch" ? "经分派" : "经接口"} · ${sub.description}`,
        target: sub.definition,
      });
    }
  }
  return out;
}

/** 受影响的入口 → 项：label 入口（同影响范围），description 所在容器 · 经几层，detail 入口文件；选中跳到入口方法。 */
export function entryPickItem(e: ImpactEntryDto): JumpPickItem {
  const target: JumpTarget = { path: e.entry.file, line: e.entry.line, col: null, pos: null };
  return {
    label: entryLabel(e.entry),
    description: `${e.containerName} · ${e.depth === 0 ? "它自己就是入口" : `往上 ${e.depth} 层`}`,
    detail: `${e.entry.file}:${e.entry.line}`,
    target,
    definition: null,
  };
}

/** 入口按层数从近到远，同层按名字。 */
export function entryPickItems(entries: ImpactEntryDto[]): JumpPickItem[] {
  return [...entries]
    .sort((a, b) => a.depth - b.depth || entryLabel(a.entry).localeCompare(entryLabel(b.entry)))
    .map(entryPickItem);
}

// ---------- 跳转位置 ----------

/** 文档的最小接口（vscode.TextDocument 的子集），0 起的行列。 */
export interface DocLike {
  readonly lineCount: number;
  positionAt(offset: number): { line: number; character: number };
  firstNonWhitespace(line: number): number;
}

/**
 * 跳转目标 → 编辑器位置（0 起）：有 pos 且换算出的行与记录的行一致就用 pos（落在调用名上）；
 * 对不上（文件改过还没重新索引）退回按行：有列用列，否则行首第一个非空白。行号超出文档就夹到最后一行。
 */
export function jumpPosition(doc: DocLike, t: JumpTarget): { line: number; character: number } {
  const lineIndex = Math.min(Math.max(0, t.line - 1), Math.max(0, doc.lineCount - 1));
  if (t.pos !== null && t.pos >= 0) {
    const p = doc.positionAt(t.pos);
    if (trustPos(p.line + 1, t.line)) return p;
  }
  if (t.col !== null) return { line: lineIndex, character: Math.max(0, t.col - 1) };
  return { line: lineIndex, character: doc.firstNonWhitespace(lineIndex) };
}
