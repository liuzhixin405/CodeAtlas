import type { NeighborDto } from "./types";

/** collapseImplementations 合并后留下的实现边：记着它替掉的是哪个接口方法，详情条里说明"经接口 X"。 */
export type MergedNeighbor = NeighborDto & { viaInterface?: string };

function methodName(n: NeighborDto): string {
  return n.other?.name ?? n.toName.replace(/\(.*$/, "").split(/[.:/]/).pop() ?? n.toName;
}

/**
 * 接口调用 → 实现 的派生边（via = "impl"，服务端按代码里的 implements 关系推导，与原调用同一行、同一 pos）：
 * 唯一实现（置信度 ≥ 0.9）时，把指向接口方法的那条调用边去掉，只留实现边——图上接口调用和它的实现是同一个节点，
 * 链路直接走到实现。配对规则（由准到粗）：
 * 1. 两条边都有 pos（调用名的字符偏移）且相等：就是同一个调用点；
 * 2. 没有 pos 的旧索引：同一行里与实现同名的那条普通调用（IRepo.Find → Repo.Find）；
 * 3. 还配不上：同一行只有一条普通调用时就是它。
 * 多个候选（0.5）时不动，候选和接口一起显示。只处理出边，入边两种都是真实调用者。
 */
export function collapseImplementations(neighbors: NeighborDto[]): MergedNeighbor[] {
  const drop = new Set<number>();
  const merged = new Map<number, string>();
  const byLine = new Map<number, NeighborDto[]>();
  for (const n of neighbors) {
    if (n.direction !== "out" || n.kind !== "calls") continue;
    const list = byLine.get(n.line) ?? [];
    list.push(n);
    byLine.set(n.line, list);
  }
  for (const list of byLine.values()) {
    const impls = list.filter((n) => n.via === "impl");
    if (impls.length === 0) continue;
    const plain = list.filter((n) => n.via === null);
    const byPos = (impl: NeighborDto) => impl.pos != null ? plain.filter((p) => p.pos != null && p.pos === impl.pos) : [];
    // 同一调用点（pos 相同）的实现候选只有在都 ≥ 0.9 时才合并；没有 pos 的旧数据整行一起判
    for (const impl of impls) {
      if (impl.confidence < 0.9) continue;
      let target: NeighborDto | undefined;
      const samePos = byPos(impl);
      if (samePos.length === 1) target = samePos[0];
      else if (impl.pos == null || samePos.length === 0) {
        // 旧索引没有 pos：同一行有多个实现候选（0.5）说明不唯一，不合并
        if (impls.some((x) => x.confidence < 0.9 && x.pos == impl.pos)) continue;
        const unpaired = plain.filter((p) => !drop.has(p.edgeId) && (p.pos == null || impl.pos == null));
        const sameName = unpaired.filter((p) => methodName(p) === methodName(impl));
        target = sameName.length === 1 ? sameName[0] : unpaired.length === 1 ? unpaired[0] : undefined;
      }
      if (!target || drop.has(target.edgeId)) continue;
      drop.add(target.edgeId);
      merged.set(impl.edgeId, target.other?.qualifiedName ?? target.toName);
    }
  }
  if (drop.size === 0) return neighbors;
  return neighbors.filter((n) => !drop.has(n.edgeId)).map((n) => (merged.has(n.edgeId) ? { ...n, viaInterface: merged.get(n.edgeId)! } : n));
}
