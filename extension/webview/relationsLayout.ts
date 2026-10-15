import type { RelationComponentDto, RelationEdgeDto, RelationKind, RelationNodeDto, RelationsDto, SymbolDto } from "./types";

// ---- 模块关系视图的纯版式：组件成列（依赖序从左到右，外部最右），列内单元按距离 / 与中心的权重 / 名字排 ----

/** 单元框的宽高（px）。 */
export const UnitWidth = 172;
export const UnitHeight = 34;
/** 列内相邻单元的中心距。 */
export const RelRowHeight = 50;
/** 组件列之间留给边和边标签的空隙。 */
export const RelColumnGap = 170;
/** 同一组件内折成多小列时，小列之间的空隙。 */
export const RelSubColumnGap = 18;
/** 一个组件超过这么多单元就折成多小列（最多 RelMaxSubColumns 列），免得一列拉得很长。 */
export const RelWrapAt = 10;
export const RelMaxSubColumns = 3;

/** 关系的中文名；新增 RelationKind 时这里必须补（测试会检查覆盖）。 */
export const RelationKindLabels: Record<RelationKind, string> = {
  call: "调用",
  new: "构造",
  dispatch: "分派",
  inherits: "继承",
  implements: "实现",
  reference: "引用",
};

/** 汇总标签里的先后：先成员层面的调用，再结构关系，最后类型引用。 */
export const RelationKindOrder: readonly RelationKind[] = ["call", "new", "dispatch", "inherits", "implements", "reference"];

/** 结构关系（继承 / 实现）在类型之间通常只有一条，标签里不写次数。 */
const Structural: ReadonlySet<RelationKind> = new Set<RelationKind>(["inherits", "implements"]);

/** 单元种类的中文名（详情条用）；abstract 是页面按签名判出来的抽象类。 */
export const UnitKindLabels: Record<string, string> = {
  class: "类",
  abstract: "抽象类",
  interface: "接口",
  struct: "结构",
  enum: "枚举",
  module: "模块",
  type_alias: "类型别名",
  function: "函数",
  external: "外部类型",
};

export function unitKindLabel(unit: SymbolDto): string {
  const k = isAbstract(unit) ? "abstract" : unit.kind;
  return UnitKindLabels[k] ?? unit.kind;
}

/** "调用 12 · 构造 2 · 实现"：按 RelationKindOrder，只列 > 0 的；继承 / 实现不写次数。 */
export function kindsSummary(kinds: Partial<Record<RelationKind, number>>): string {
  const parts: string[] = [];
  for (const k of RelationKindOrder) {
    const n = kinds[k] ?? 0;
    if (n <= 0) continue;
    parts.push(Structural.has(k) ? RelationKindLabels[k] : `${RelationKindLabels[k]} ${n}`);
  }
  // 服务端以后多出的、页面还不认识的种类：原样附在后面，不丢
  for (const [k, n] of Object.entries(kinds)) {
    if (!(k in RelationKindLabels) && (n ?? 0) > 0) parts.push(`${k} ${n}`);
  }
  return parts.join(" · ");
}

/** 边的主关系，决定画法：继承 / 实现 > 调用 / 构造 > 分派 > 引用。 */
export type EdgePrimary = "inherits" | "implements" | "call" | "dispatch" | "reference";

export function primaryRelation(kinds: Partial<Record<RelationKind, number>>): EdgePrimary {
  const has = (k: RelationKind) => (kinds[k] ?? 0) > 0;
  if (has("inherits")) return "inherits";
  if (has("implements")) return "implements";
  if (has("call") || has("new")) return "call";
  if (has("dispatch")) return "dispatch";
  return "reference";
}

/** 线宽三档：< 3 细、< 10 中、≥ 10 粗（Weight 的口径见 RelationEdgeDto：调用 / 构造按条数，继承 / 实现各 5，引用按条数 / 2）。 */
export type WeightTier = 1 | 2 | 3;
export const WeightTierBounds = [3, 10] as const;

export function weightTier(weight: number): WeightTier {
  if (weight >= WeightTierBounds[1]) return 3;
  if (weight >= WeightTierBounds[0]) return 2;
  return 1;
}

/** 每档的线宽（px），渲染器与测试共用。 */
export const TierWidths: Record<WeightTier, number> = { 1: 1.2, 2: 2.2, 3: 3.6 };

// ---- 短名 ----

/** 去掉泛型参数（`Repository<T>`、``List`1``）和命名空间前缀。 */
export function stripGenerics(name: string): string {
  let s = name;
  const lt = s.indexOf("<");
  if (lt > 0) s = s.slice(0, lt);
  const tick = s.indexOf("`");
  if (tick > 0) s = s.slice(0, tick);
  const dot = s.lastIndexOf(".");
  const colons = s.lastIndexOf("::");
  const cut = Math.max(dot >= 0 ? dot + 1 : 0, colons >= 0 ? colons + 2 : 0);
  if (cut > 0 && cut < s.length) s = s.slice(cut);
  return s.trim() || name;
}

export function fileName(path: string): string {
  const i = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return i < 0 ? path : path.slice(i + 1);
}

export function isAbstract(unit: SymbolDto): boolean {
  return unit.kind === "class" && /\babstract\b/.test(unit.signature ?? "");
}

/** 单元框上的字：模块用文件名，接口前加 ◇，其余用去泛型的类型名。 */
export function unitLabel(unit: SymbolDto): string {
  if (unit.kind === "module") return fileName(unit.path) || stripGenerics(unit.name);
  const base = stripGenerics(unit.name || unit.qualifiedName);
  return unit.kind === "interface" ? `◇ ${base}` : base;
}

// ---- 组件与方向 ----

/** 外部组件：服务端放"仓库外类型"的那个组件（其中的单元都是 External）。 */
export function externalComponentKeys(dto: RelationsDto): Set<string> {
  const all = new Map<string, boolean>();
  for (const n of dto.nodes) all.set(n.componentKey, (all.get(n.componentKey) ?? true) && n.external);
  return new Set([...all].filter(([, ext]) => ext).map(([k]) => k));
}

/** 图里实际用到的组件，按列序排好：非外部按 Order、再按键；外部组件放最右。节点引用了 components 里没有的键时补一个同名组件。 */
export function orderedComponents(dto: RelationsDto): RelationComponentDto[] {
  const used = new Set(dto.nodes.map((n) => n.componentKey));
  const byKey = new Map<string, RelationComponentDto>();
  for (const c of dto.components) if (used.has(c.key) && !byKey.has(c.key)) byKey.set(c.key, c);
  const maxOrder = Math.max(-1, ...dto.components.map((c) => c.order));
  for (const k of [...used].sort()) if (!byKey.has(k)) byKey.set(k, { key: k, label: k, order: maxOrder + 1, containerKey: null });
  const ext = externalComponentKeys(dto);
  return [...byKey.values()].sort((a, b) => Number(ext.has(a.key)) - Number(ext.has(b.key)) || a.order - b.order || cmp(a.key, b.key));
}

/**
 * 反向依赖：From 所在组件的 Order 小于 To 所在组件的 Order——被依赖的（左边的基础组件）反过来依赖了依赖它的组件。
 * 同组件内、或任一端在外部组件的边不算。
 */
export function isReverseDependency(edge: RelationEdgeDto, dto: RelationsDto): boolean {
  return componentResolver(dto).reverse(edge);
}

/** 反向依赖的说明文字；不是反向依赖返回 null。 */
export function reverseNote(edge: RelationEdgeDto, dto: RelationsDto): string | null {
  const r = componentResolver(dto);
  if (!r.reverse(edge)) return null;
  const [from, to] = r.pair(edge)!;
  return `反向依赖：${from.label} 组件依赖了 ${to.label} 组件`;
}

/** 单元 → 组件的查表，一次建好给整张图的边用。 */
function componentResolver(dto: RelationsDto): {
  pair(edge: RelationEdgeDto): [RelationComponentDto, RelationComponentDto] | null;
  reverse(edge: RelationEdgeDto): boolean;
} {
  const compOfUnit = new Map(dto.nodes.map((n) => [n.unit.id, n.componentKey]));
  const comps = new Map(orderedComponents(dto).map((c) => [c.key, c]));
  const ext = externalComponentKeys(dto);
  const pair = (edge: RelationEdgeDto): [RelationComponentDto, RelationComponentDto] | null => {
    const ka = compOfUnit.get(edge.from);
    const kb = compOfUnit.get(edge.to);
    const a = ka === undefined ? undefined : comps.get(ka);
    const b = kb === undefined ? undefined : comps.get(kb);
    return a && b ? [a, b] : null;
  };
  return {
    pair,
    reverse(edge) {
      const p = pair(edge);
      if (!p) return false;
      const [from, to] = p;
      if (from.key === to.key || ext.has(from.key) || ext.has(to.key)) return false;
      return from.order < to.order;
    },
  };
}

/** 单元与中心之间的边权重（两个方向相加）；排列内顺序用。 */
export function weightToCenter(dto: RelationsDto): Map<number, number> {
  const center = dto.focus.id;
  const w = new Map<number, number>();
  for (const e of dto.edges) {
    if (e.from === center && e.to !== center) w.set(e.to, (w.get(e.to) ?? 0) + e.weight);
    else if (e.to === center && e.from !== center) w.set(e.from, (w.get(e.from) ?? 0) + e.weight);
  }
  return w;
}

/** 详情条："依赖中心：调用 3；被中心依赖：实现"。中心自己、或与中心没有直接边时给相应说明。 */
export function relationToCenter(dto: RelationsDto, unitId: number): string {
  const center = dto.focus.id;
  if (unitId === center) return "这是中心";
  const out = dto.edges.find((e) => e.from === unitId && e.to === center);
  const inc = dto.edges.find((e) => e.from === center && e.to === unitId);
  const parts: string[] = [];
  if (inc) parts.push(`中心依赖它：${kindsSummary(inc.kinds)}`);
  if (out) parts.push(`它依赖中心：${kindsSummary(out.kinds)}`);
  if (parts.length > 0) return parts.join("；");
  const d = dto.nodes.find((n) => n.unit.id === unitId)?.distance;
  return d !== undefined && d >= 2 ? "与中心没有直接关系（隔一层）" : "与中心没有直接关系";
}

// ---- cytoscape 元素 ----

export interface RelComponentElement {
  data: { id: string; label: string; componentKey: string; payload: RelationComponentDto };
  classes: string;
}

export interface RelUnitElement {
  data: {
    id: string;
    parent: string;
    label: string;
    unitId: number;
    componentKey: string;
    distance: number;
    payload: RelationNodeDto;
  };
  position: { x: number; y: number };
  classes: string;
}

export interface RelEdgeElement {
  data: {
    id: string;
    source: string;
    target: string;
    label: string;
    primary: EdgePrimary;
    tier: WeightTier;
    reverse: boolean;
    payload: RelationEdgeDto;
  };
  classes: string;
}

export interface RelationsLayout {
  /** 组件框（compound 父节点），按列序。 */
  components: RelComponentElement[];
  /** 单元，按列序、列内顺序。 */
  units: RelUnitElement[];
  /** 边，按 id 排。 */
  edges: RelEdgeElement[];
  /** 列序里每个组件的单元 id，测试与调试用。 */
  columns: { componentKey: string; unitIds: number[] }[];
}

export const unitElementId = (id: number) => `u:${id}`;
export const componentElementId = (key: string) => `c:${key}`;
export const edgeElementId = (from: number, to: number) => `e:${from}->${to}`;

/**
 * 把 RelationsDto 变成带坐标的 cytoscape 元素。纯函数：同样的 DTO 永远同样的元素与坐标。
 * - 组件成列：orderedComponents 的顺序（Order 小的、被依赖的在左，外部组件最右）；
 * - 列内：Distance 升序 → 与中心的边权重降序 → 短名 → 限定名 → id；
 * - 一列超过 RelWrapAt 个单元折成多小列；每列垂直居中；
 * - 中心单元带 center 类；边的类：主关系（structural / call / dispatch / reference）、线宽档 w1-w3、reverse、near（连着中心，默认显示标签）、intra（同组件内）。
 */
/**
 * 组件标题节点的位置：骑在框的上边线正中（像图例标签）。框的范围由 cytoscape 按子节点 + padding 算出，渲染后才知道，所以单独成函数。
 * 标题不画在框（compound 父节点）自己的 label 上：父节点先画、线后画，线上的文字会压住它。
 */
export function componentTitlePosition(box: { x1: number; y1: number; w: number }): { x: number; y: number } {
  return { x: box.x1 + box.w / 2, y: box.y1 };
}

/** 组件标题节点的元素 id。 */
export function componentTitleId(componentId: string): string {
  return `${componentId}::title`;
}

export function layoutRelations(dto: RelationsDto): RelationsLayout {
  const comps = orderedComponents(dto);
  const ext = externalComponentKeys(dto);
  const toCenter = weightToCenter(dto);
  const resolver = componentResolver(dto);
  const center = dto.focus.id;

  const byComp = new Map<string, RelationNodeDto[]>();
  const seen = new Set<number>();
  for (const n of dto.nodes) {
    if (seen.has(n.unit.id)) continue;
    seen.add(n.unit.id);
    (byComp.get(n.componentKey) ?? byComp.set(n.componentKey, []).get(n.componentKey)!).push(n);
  }
  const distanceOf = (n: RelationNodeDto) => (n.unit.id === center ? 0 : Math.max(n.distance, 1));
  for (const list of byComp.values()) {
    list.sort((a, b) =>
      distanceOf(a) - distanceOf(b)
      || (toCenter.get(b.unit.id) ?? 0) - (toCenter.get(a.unit.id) ?? 0)
      || cmp(unitLabel(a.unit), unitLabel(b.unit))
      || cmp(a.unit.qualifiedName, b.unit.qualifiedName)
      || a.unit.id - b.unit.id);
  }

  const subColumns = (count: number) => Math.min(RelMaxSubColumns, Math.max(1, Math.ceil(count / RelWrapAt)));
  const rows = (count: number) => Math.ceil(count / subColumns(count));
  const tallest = Math.max(1, ...comps.map((c) => rows(byComp.get(c.key)?.length ?? 0)));

  const components: RelComponentElement[] = [];
  const units: RelUnitElement[] = [];
  const columns: RelationsLayout["columns"] = [];
  let x = 0;
  for (const c of comps) {
    const list = byComp.get(c.key) ?? [];
    const cols = subColumns(list.length);
    const offset = ((tallest - rows(list.length)) * RelRowHeight) / 2;
    const isExt = ext.has(c.key);
    components.push({
      data: { id: componentElementId(c.key), label: c.label, componentKey: c.key, payload: c },
      classes: ["component", isExt ? "external" : "", list.some((n) => n.unit.id === center) ? "has-center" : ""].filter(Boolean).join(" "),
    });
    list.forEach((n, i) => {
      const u = n.unit;
      const isCenter = u.id === center;
      const classes = ["unit", `kind-${u.kind}`];
      if (isAbstract(u)) classes.push("abstract");
      if (u.kind === "interface" || isAbstract(u)) classes.push("italic");
      if (n.external) classes.push("external");
      classes.push(isCenter ? "center" : `d${Math.min(distanceOf(n), 2)}`);
      units.push({
        data: { id: unitElementId(u.id), parent: componentElementId(c.key), label: unitLabel(u), unitId: u.id, componentKey: c.key, distance: isCenter ? 0 : distanceOf(n), payload: n },
        position: { x: x + (i % cols) * (UnitWidth + RelSubColumnGap), y: offset + Math.floor(i / cols) * RelRowHeight },
        classes: classes.join(" "),
      });
    });
    columns.push({ componentKey: c.key, unitIds: list.map((n) => n.unit.id) });
    x += cols * UnitWidth + (cols - 1) * RelSubColumnGap + RelColumnGap;
  }

  const present = new Set(units.map((u) => u.data.unitId));
  const compOf = new Map(units.map((u) => [u.data.unitId, u.data.componentKey]));
  const edges: RelEdgeElement[] = [];
  const edgeSeen = new Set<string>();
  for (const e of dto.edges) {
    if (e.from === e.to || !present.has(e.from) || !present.has(e.to)) continue;
    const id = edgeElementId(e.from, e.to);
    if (edgeSeen.has(id)) continue;
    edgeSeen.add(id);
    const primary = primaryRelation(e.kinds);
    const tier = weightTier(e.weight);
    const reverse = resolver.reverse(e);
    const classes = [primary === "inherits" || primary === "implements" ? "structural" : primary, `w${tier}`];
    if (reverse) classes.push("reverse");
    if (e.from === center || e.to === center) classes.push("near");
    // 同一组件内的边两端上下相邻，直连太短：渲染器让它往右鼓出去
    if (compOf.get(e.from) === compOf.get(e.to)) classes.push("intra");
    edges.push({
      data: { id, source: unitElementId(e.from), target: unitElementId(e.to), label: kindsSummary(e.kinds), primary, tier, reverse, payload: e },
      classes: classes.join(" "),
    });
  }
  edges.sort((a, b) => cmp(a.data.id, b.data.id));
  return { components, units, edges, columns };
}

function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
