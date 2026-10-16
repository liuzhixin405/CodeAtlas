import type { NeighborDto, SymbolDto } from "./types";

/**
 * 网状调用图挑分支的规则（纯函数）：每个节点只自动展开打分最高的几个邻居，其余折成「还有 N 个」。
 *
 * 2026-10 修正（用户反馈"复杂项目里第一屏看不到想看的调用"）：
 * - 去掉"被调用多就扣分"：服务层核心方法（被几十处调用）恰恰是主流程，原来会被扣到 DTO 构造之下；
 * - 构造（new 一个类 / 结构 / 记录，或调用构造函数）降到普通调用之下：new DTO 不是"它做了什么"；
 * - 保留：分发最像主流程；测试代码、校验 / 日志 / 判断类名字降权；与焦点同模块的略加分。
 */

/** 名字一看就是校验/日志/判断类的辅助方法，不太可能是主流程。 */
export const UtilityName = /^(Validate|Ensure|Check|Format|Log[A-Z]|Is[A-Z]|Has[A-Z]|Can[A-Z]|Try[A-Z]|ThrowIf|Assert|Guard|Normalize|Sanitize)/;

/** 日志类：ILogger / XxxLogger / LoggerExtensions 上的方法。 */
const LoggerType = /^I?\w*Log(ger)?(Extensions)?$/;

/** 测试代码判定：路径或限定名里带 Tests/Test/Spec 段。 */
const TestPathPattern = /(^|[\/.])(tests?|__tests__|spec|specs)([\/.]|$)|\.(test|spec)\.[tj]sx?$|Tests?\.cs$/i;

export function isTestSymbol(s: SymbolDto): boolean {
  return TestPathPattern.test(s.path) || /\.Tests?\./.test(s.qualifiedName);
}

/** 限定名的前两段，粗略当"模块"：Acme.Shop.Orders.X → Acme.Shop。 */
export function moduleOf(qname: string): string {
  return qname.split(/[.:/]/).slice(0, 2).join(".");
}

/**
 * 把限定名拆成段：去掉 external: 前缀、参数表、泛型实参（里面的点不算分隔）；
 * 脚本语言的限定名是「文件路径:类.方法」，只取冒号后面那段（文件路径里的点不是类型）。
 * "Ns.Foo<Bar.Baz>.Handle(A, B)" → ["Ns", "Foo", "Handle"]；"src/cart.ts:Cart.add" → ["Cart", "add"]。
 */
export function qualifiedSegments(qname: string): string[] {
  let s = qname.startsWith("external:") ? qname.slice("external:".length) : qname;
  const paren = s.indexOf("(");
  if (paren >= 0) s = s.slice(0, paren);
  let out = "";
  let depth = 0;
  for (const ch of s) {
    if (ch === "<") depth++;
    else if (ch === ">") depth = Math.max(0, depth - 1);
    else if (depth === 0) out += ch;
  }
  const colon = out.lastIndexOf(":");
  if (colon >= 0) out = out.slice(colon + 1);
  return out.split(/[./#]/).filter(Boolean);
}

const TypeKinds = new Set(["class", "struct", "record", "interface", "enum"]);
const MemberKinds = new Set(["method", "property", "field"]);

function stripGeneric(name: string): string {
  const i = name.indexOf("<");
  return i >= 0 ? name.slice(0, i) : name;
}

/** 构造函数符号：constructor / __init__ / .ctor，或方法名与所属类型同名（C# / Java）。 */
export function isConstructorSymbol(s: SymbolDto): boolean {
  if (s.kind !== "method") return false;
  if (s.name === "constructor" || s.name === "__init__" || s.name === ".ctor") return true;
  const seg = qualifiedSegments(s.qualifiedName);
  return seg.length >= 2 && stripGeneric(seg[seg.length - 2]!) === stripGeneric(s.name);
}

/** 这条边是"构造一个对象"：调用的目标是类型本身（new X()），或是构造函数，或 via 标着 new。 */
export function isConstruction(n: NeighborDto): boolean {
  if (n.kind !== "calls") return false;
  if (n.via === "new" || n.via === "ctor") return true;
  const o = n.other;
  return o !== null && (TypeKinds.has(o.kind) || isConstructorSymbol(o));
}

/**
 * 节点显示名一律「类型.方法」（入口流程时序图同样的写法）：
 * 方法 / 属性 / 字段带所属类型；构造函数只写类型名；外部符号取限定名最后两段；类型、顶层函数、模块就是名字。
 */
export function nodeLabel(s: SymbolDto): string {
  const seg = qualifiedSegments(s.qualifiedName);
  if (s.kind === "external") return seg.length >= 2 ? `${seg[seg.length - 2]}.${seg[seg.length - 1]}` : s.name;
  if (!MemberKinds.has(s.kind)) return s.name;
  if (isConstructorSymbol(s)) return seg.length >= 2 ? seg[seg.length - 2]! : s.name;
  // 显式接口实现的名字本身带点（Ns.IHandler.Handle）：方法名取最后一段，类型是名字前面那一段
  const nameSeg = qualifiedSegments(s.name);
  const method = nameSeg[nameSeg.length - 1] ?? s.name;
  const type = seg[seg.length - 1 - Math.max(1, nameSeg.length)];
  return type ? `${type}.${method}` : method;
}

/** 只有限定名时的「类型.方法」：最后两段。 */
export function qualifiedLabel(qname: string): string {
  const seg = qualifiedSegments(qname);
  return seg.slice(-2).join(".") || qname;
}

export interface RankContext {
  /** 焦点所在模块（moduleOf(焦点限定名)）；同模块的邻居略加分。 */
  focusModule: string;
}

/**
 * 一条边像不像主流程（分高的先展开）：分发 > 普通调用 > 构造 / 类型关系；测试、校验 / 日志类降权。
 * 不看"被调用多少处"。外部调用在 GraphState 里单独折叠，这里不用管。
 */
export function rankNeighbor(n: NeighborDto, ctx: RankContext = { focusModule: "" }): number {
  let s = 0;
  const construction = isConstruction(n);
  if (n.kind === "dispatches") s += 100;
  else if (n.kind === "calls") s += construction ? -20 : 30;
  else if (n.kind === "implements" || n.kind === "inherits") s += -25;
  // 接口调用换成唯一实现的边（via=impl 0.9）就是原来那次调用，不因为"经过接口"比直接调用低一档
  s += (n.via === "impl" && n.confidence >= 0.9 ? 1 : n.confidence) * 20;
  const o = n.other;
  if (o) {
    if (isTestSymbol(o)) s -= 200;
    if (UtilityName.test(o.name)) s -= 50;
    const seg = qualifiedSegments(o.qualifiedName);
    if (seg.length >= 2 && LoggerType.test(seg[seg.length - 2]!)) s -= 50;
    if (ctx.focusModule && moduleOf(o.qualifiedName) === ctx.focusModule) s += 15;
  }
  return s;
}
