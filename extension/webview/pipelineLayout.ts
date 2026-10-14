import type { PipelineBehaviorDto, PipelineDto, PipelineFlowDto, PipelineRowDto } from "./types";

/** 泳道图的列：行标签 + 管道五段。 */
export type CellKind = "label" | "entry" | "message" | "behaviors" | "handler" | "downstream";
export type NodeKind = CellKind | "header";

export const Columns: readonly CellKind[] = ["label", "entry", "message", "behaviors", "handler", "downstream"];
export const ColumnTitles: Record<CellKind, string> = { label: "", entry: "入口", message: "消息", behaviors: "行为链", handler: "Handler", downstream: "下游" };

export const ColWidth = 240;
export const RowHeight = 96;
export const HeaderY = 0;
export const FirstRowY = 60;
export const CellWidth = 200;
export const CellHeight = 64;

export interface LayoutNode {
  data: {
    id: string;
    label: string;
    kind: NodeKind;
    /** 所属行的 key；列标题为 null。 */
    row: string | null;
    /** 需要警示色边框：孤儿消息、顺序未知的行为链、被绕过的 Handler。 */
    warn: boolean;
    payload: PipelineRowDto | PipelineBehaviorDto[] | null;
  };
  position: { x: number; y: number };
  classes: string;
}

export interface LayoutEdge {
  data: {
    id: string;
    source: string;
    target: string;
    kind: "lane" | "nested" | "bypass";
    label: string;
    weight: number;
    payload: PipelineFlowDto | null;
  };
  classes: string;
}

export function cellId(row: string, kind: CellKind): string {
  return `${row}|${kind}`;
}

function x(kind: CellKind): number {
  return Columns.indexOf(kind) * ColWidth;
}

function join(parts: (string | null)[]): string {
  return parts.filter((p): p is string => p !== null && p !== "").join("\n");
}

/** 把服务端的管道 DTO 变成带坐标的 cytoscape 元素。纯函数：同样的 DTO 永远同样的图。 */
export function layoutPipeline(dto: PipelineDto): { nodes: LayoutNode[]; edges: LayoutEdge[] } {
  const nodes: LayoutNode[] = [];
  const edges: LayoutEdge[] = [];

  for (const col of Columns) {
    if (col === "label") continue;
    nodes.push({
      data: { id: `header|${col}`, label: ColumnTitles[col], kind: "header", row: null, warn: false, payload: null },
      position: { x: x(col), y: HeaderY },
      classes: "header",
    });
  }

  const behaviors = dto.behaviors;
  const first = behaviors[0];
  const last = behaviors[behaviors.length - 1];
  const behaviorsLabel = first === undefined || last === undefined
    ? "行为链 ×0"
    : join([
        `行为链 ×${behaviors.length}`,
        behaviors.length === 1 ? first.name : `${first.name} … ${last.name}`,
        !dto.behaviorsOrdered && behaviors.length > 1 ? "顺序未知" : null,
      ]);
  const behaviorsWarn = !dto.behaviorsOrdered && behaviors.length > 1;

  dto.rows.forEach((row, i) => {
    const y = FirstRowY + i * RowHeight;
    const add = (kind: CellKind, label: string, warn: boolean, payload: PipelineRowDto | PipelineBehaviorDto[]) =>
      nodes.push({ data: { id: cellId(row.key, kind), label, kind, row: row.key, warn, payload }, position: { x: x(kind), y }, classes: kind + (warn ? " warn" : "") });

    add("label", row.label, false, row);
    add("entry", join([`入口 ${row.entries.count}`, row.entries.items[0]?.name ?? null]), false, row);
    const m = row.messages;
    add("message", join([
      `消息 ${m.count}`,
      row.symbolId !== null ? row.label : `命令 ${m.commands} · 查询 ${m.queries} · 事件 ${m.events}`,
      m.orphans > 0 ? `孤儿 ${m.orphans}` : null,
    ]), m.orphans > 0, row);
    add("behaviors", behaviorsLabel, behaviorsWarn, behaviors);
    const onlyHandler = row.handlers.items.length === 1 ? row.handlers.items[0] : undefined;
    add("handler", join([
      `Handler ${row.handlers.count}`,
      onlyHandler?.name ?? null,
      row.nested > 0 ? `嵌套分发 ${row.nested}` : null,
      row.bypass > 0 ? `绕过 ${row.bypass}` : null,
    ]), row.bypass > 0, row);
    const total = row.downstream.reduce((n, g) => n + g.weight, 0);
    add("downstream", join([
      `下游 ${total}`,
      ...row.downstream.slice(0, 2).map((g) => `${g.label} ${g.weight}`),
      row.externalCalls > 0 ? `框架调用 ${row.externalCalls}` : null,
    ]), false, row);

    const chain: readonly [CellKind, CellKind][] = [["entry", "message"], ["message", "behaviors"], ["behaviors", "handler"], ["handler", "downstream"]];
    for (const [from, to] of chain) {
      const source = cellId(row.key, from);
      const target = cellId(row.key, to);
      edges.push({ data: { id: `${source}=>${target}`, source, target, kind: "lane", label: "", weight: 0, payload: null }, classes: "lane" });
    }
  });

  for (const f of dto.flows) {
    const source = f.kind === "nested" ? cellId(f.from, "handler") : cellId(f.from, "entry");
    const target = f.kind === "nested" ? cellId(f.to, "message") : cellId(f.to, "handler");
    edges.push({ data: { id: `${f.kind}:${f.from}=>${f.to}`, source, target, kind: f.kind, label: String(f.weight), weight: f.weight, payload: f }, classes: f.kind });
  }

  nodes.sort((a, b) => (a.data.id < b.data.id ? -1 : a.data.id > b.data.id ? 1 : 0));
  edges.sort((a, b) => (a.data.id < b.data.id ? -1 : a.data.id > b.data.id ? 1 : 0));
  return { nodes, edges };
}
