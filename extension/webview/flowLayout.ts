import type { FlowActorDto, FlowDto, FlowNodeDto } from "./types";

/** 深度小于这个值的节点默认展开，再深的默认折起来，点一下再看。 */
export const DefaultCollapseDepth = 3;

export interface FlowRow {
  /** 从根开始的孩子下标路径，根是 "0"，如 "0.2.1"。 */
  id: string;
  node: FlowNodeDto;
  depth: number;
  /** 父节点所在的 actor 列；根用自己的列。 */
  fromCol: number;
  toCol: number;
  direction: "right" | "left" | "self";
  /** node.label，重复多次时带 ×N。 */
  label: string;
  hasChildren: boolean;
  /** 有孩子但没展开。 */
  collapsed: boolean;
  /** 边来历的样式（如 JSX 渲染边）；普通调用为 null。 */
  via: ViaStyle | null;
}

/** 箭头按来历区分的样式：cls 加到行上，tag 显示在标签旁。 */
export interface ViaStyle {
  cls: string;
  tag: string;
}

/** 只给调用箭头区分来历：via=render 是 JSX 里渲染组件（<X />），不是函数调用。hop 的 via 是协议，另有显示。 */
export function viaStyle(node: FlowNodeDto): ViaStyle | null {
  if (node.kind !== "call") return null;
  if (node.via === "render") return { cls: "via-render", tag: "渲染" };
  // 作为实参 / 属性值传出去的函数（stream.on("data", onData)、mutationFn: login）：框架回头调它
  if (node.via === "callback") return { cls: "via-render", tag: "回调" };
  return null;
}

export interface FlowLayout {
  columns: FlowActorDto[];
  rows: FlowRow[];
}

/** 默认展开规则 + expanded 集合里的 id 取反。 */
export function isExpanded(node: FlowNodeDto, id: string, expanded: Set<string>): boolean {
  const byDefault = node.depth < DefaultCollapseDepth;
  return expanded.has(id) ? !byDefault : byDefault;
}

/** 翻转一个节点的展开状态；返回新集合，不改原来的。 */
export function toggle(expanded: Set<string>, id: string): Set<string> {
  const next = new Set(expanded);
  if (next.has(id)) next.delete(id); else next.add(id);
  return next;
}

/** 时序图的预设版式：参与者按首次出现各占一列（first-seen），或所有外部系统合并成一列（grouped-external）。 */
export type FlowPreset = "first-seen" | "grouped-external";

/** 合并后的外部列的 key。 */
export const ExternalGroupKey = "ext|*";

/**
 * 按预设决定列：first-seen 直接用服务端排好的 actors；grouped-external 把所有 kind=external 的 actor
 * 合并成最后一列「外部」（没有外部 actor 就不加）。返回列表和 actor key → 列号 的映射。
 */
export function flowColumns(dto: FlowDto, preset: FlowPreset | string): { columns: FlowActorDto[]; colOf: Map<string, number> } {
  const colOf = new Map<string, number>();
  if (preset !== "grouped-external") {
    dto.actors.forEach((a, i) => colOf.set(a.key, i));
    return { columns: dto.actors, colOf };
  }
  const columns: FlowActorDto[] = dto.actors.filter((a) => a.kind !== "external");
  columns.forEach((a, i) => colOf.set(a.key, i));
  const externals = dto.actors.filter((a) => a.kind === "external");
  if (externals.length > 0) {
    const label = externals.length === 1 ? externals[0]!.label : `外部（${externals.length}）`;
    columns.push({ key: ExternalGroupKey, label, kind: "external" });
    for (const a of externals) colOf.set(a.key, columns.length - 1);
  }
  return { columns, colOf };
}

/**
 * 把流程树摊成时序图的行：先序遍历可见节点，每行记下从哪列画到哪列。
 * 纯函数，不碰 DOM。preset 见 flowColumns，默认 first-seen。
 */
export function layoutFlow(dto: FlowDto, expanded: Set<string>, preset: FlowPreset | string = "first-seen"): FlowLayout {
  const { columns, colOf } = flowColumns(dto, preset);
  const rows: FlowRow[] = [];
  const visit = (node: FlowNodeDto, id: string, parentCol: number | null): void => {
    // actor 不在列表里（不该发生）就画在父列上，免得整行丢掉
    const toCol = colOf.get(node.actor) ?? parentCol ?? 0;
    const fromCol = parentCol ?? toCol;
    // 未对上后端的接口调用（kind=http）是叶子：服务端不给孩子，万一给了也不展开
    const hasChildren = node.kind !== "http" && node.children.length > 0;
    const open = isExpanded(node, id, expanded);
    rows.push({
      id, node, depth: node.depth, fromCol, toCol,
      direction: toCol > fromCol ? "right" : toCol < fromCol ? "left" : "self",
      label: node.label + (node.count > 1 ? ` ×${node.count}` : ""),
      hasChildren,
      collapsed: hasChildren && !open,
      via: viaStyle(node),
    });
    if (open && hasChildren) node.children.forEach((c, i) => visit(c, `${id}.${i}`, toCol));
  };
  visit(dto.root, "0", null);
  return { columns, rows };
}
