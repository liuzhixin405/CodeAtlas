import { describe, expect, it } from "vitest";
import { collapseImplementations } from "../webview/neighborRules";
import type { NeighborDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string, kind = "method"): SymbolDto {
  const name = qualifiedName.split(".").pop()!.replace(/\(.*$/, "");
  return { id, kind, name, qualifiedName, signature: null, path: "A.cs", startLine: 1, startCol: 1, endLine: 1, endCol: 1, visibility: null };
}
function edge(id: number, other: SymbolDto, opts: Partial<NeighborDto> = {}): NeighborDto {
  return { edgeId: id, kind: "calls", direction: "out", confidence: 1.0, line: 12, ambiguousGroup: null, other, toName: other.qualifiedName, via: null, evidenceLine: null, ...opts };
}

const iface = sym(1, "Ns.IRepo.Find(int)");
const impl = sym(2, "Ns.Repo.Find(int)");
const other = sym(3, "Ns.Logger.Log(string)");

describe("collapseImplementations", () => {
  it("唯一实现（0.9）时，同一行的接口调用边被实现边替代", () => {
    const out = collapseImplementations([edge(10, iface), edge(11, impl, { via: "impl", confidence: 0.9 })]);
    expect(out.map((n) => n.edgeId)).toEqual([11]);
  });

  it("多个候选实现（0.5）时保留接口边，候选一起显示", () => {
    const impl2 = sym(4, "Ns.CachedRepo.Find(int)");
    const out = collapseImplementations([
      edge(10, iface),
      edge(11, impl, { via: "impl", confidence: 0.5 }),
      edge(12, impl2, { via: "impl", confidence: 0.5 }),
    ]);
    expect(out.map((n) => n.edgeId)).toEqual([10, 11, 12]);
  });

  it("同一行有多条普通调用、没有 pos 时按方法名配对：IRepo.Find 与 Repo.Find 合并，另一条调用不动", () => {
    const out = collapseImplementations([edge(10, iface), edge(13, other), edge(11, impl, { via: "impl", confidence: 0.9 })]);
    expect(out.map((n) => n.edgeId)).toEqual([13, 11]);
  });

  it("同一行多条普通调用、名字也对不上时分不清哪条是接口调用，不折叠", () => {
    const renamed = sym(5, "Ns.SqlRepo.Lookup(int)");
    const out = collapseImplementations([edge(10, iface), edge(13, other), edge(11, renamed, { via: "impl", confidence: 0.9 })]);
    expect(out.map((n) => n.edgeId)).toEqual([10, 13, 11]);
  });

  it("有 pos 时按调用点精确配对：同一行两个接口调用各自换成自己的实现", () => {
    const iface2 = sym(6, "Ns.IUnitOfWork.Save()");
    const impl2 = sym(7, "Ns.Db.Save()");
    const out = collapseImplementations([
      edge(10, iface, { pos: 100 }), edge(20, iface2, { pos: 130 }),
      edge(11, impl, { via: "impl", confidence: 0.9, pos: 100 }), edge(21, impl2, { via: "impl", confidence: 0.9, pos: 130 }),
    ]);
    expect(out.map((n) => n.edgeId)).toEqual([11, 21]);
  });

  it("合并后的实现边记着被替掉的接口方法（详情条说明「经接口」）", () => {
    const out = collapseImplementations([edge(10, iface, { pos: 5 }), edge(11, impl, { via: "impl", confidence: 0.9, pos: 5 })]);
    expect(out[0]).toMatchObject({ edgeId: 11, viaInterface: "Ns.IRepo.Find(int)" });
  });

  it("入边方向不折叠（向上追源头时接口边与实现边都是真实调用者）", () => {
    const out = collapseImplementations([
      edge(10, iface, { direction: "in" }),
      edge(11, impl, { direction: "in", via: "impl", confidence: 0.9 }),
    ]);
    expect(out).toHaveLength(2);
  });
});
