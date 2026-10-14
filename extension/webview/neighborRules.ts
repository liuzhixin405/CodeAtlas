import type { NeighborDto } from "./types";

/**
 * 接口调用 → 实现 的派生边（via = "impl"，服务端按代码里的 implements 关系推导）：
 * 唯一实现（置信度 ≥ 0.9）时，把同一行上那条指向接口方法的调用边换成实现边，链路直接走到实现；
 * 多个候选（0.5）或同一行有多条普通调用（分不清哪条是接口调用）时不动。只处理出边，入边两种都是真实调用者。
 */
export function collapseImplementations(neighbors: NeighborDto[]): NeighborDto[] {
  const drop = new Set<number>();
  const byLine = new Map<number, NeighborDto[]>();
  for (const n of neighbors) {
    if (n.direction !== "out" || n.kind !== "calls") continue;
    const list = byLine.get(n.line) ?? [];
    list.push(n);
    byLine.set(n.line, list);
  }
  for (const list of byLine.values()) {
    const impls = list.filter((n) => n.via === "impl");
    if (impls.length === 0 || impls.some((n) => n.confidence < 0.9)) continue;
    const plain = list.filter((n) => n.via === null);
    const only = plain.length === 1 ? plain[0] : undefined;
    if (only) drop.add(only.edgeId);
  }
  return drop.size === 0 ? neighbors : neighbors.filter((n) => !drop.has(n.edgeId));
}
