import { describe, expect, it } from "vitest";
import { reorderDsm } from "../webview/dsmOrder";
import type { DsmComponentDto, DsmDto } from "../webview/types";

function comp(key: string, label: string): DsmComponentDto {
  return { key, label, symbols: 1, files: 1, hasChildren: false, scc: 0, hot: null };
}

/** 服务端依赖序：Zeta(0) alpha(1) Mid(2)；字母序（忽略大小写）应是 alpha Mid Zeta，旧 → 新下标 [2, 0, 1]。 */
const dto: DsmDto = {
  container: "Acme.Api",
  root: "",
  components: [comp("z.Zeta", "Zeta"), comp("a.alpha", "alpha"), comp("m.Mid", "Mid")],
  cells: [
    { row: 0, col: 1, count: 4, kinds: { calls: 4 } },
    { row: 2, col: 0, count: 1, kinds: { references: 1 } },
    { row: 1, col: 2, count: 2, kinds: { calls: 2 } },
  ],
  cycles: [["z.Zeta", "a.alpha"]],
  coupling: [{ a: 0, b: 2, shared: 7, degree: 0.6 }],
  gitAvailable: true,
  note: null,
};

describe("reorderDsm", () => {
  it("dependency-order returns the server order untouched (same object)", () => {
    expect(reorderDsm(dto, "dependency-order")).toBe(dto);
    expect(reorderDsm(dto, "whatever")).toBe(dto);
  });

  it("alphabetical sorts by label ignoring case and remaps cell / coupling indices", () => {
    const r = reorderDsm(dto, "alphabetical");
    expect(r.components.map((c) => c.key)).toEqual(["a.alpha", "m.Mid", "z.Zeta"]);
    // (0,1)→(2,0)；(2,0)→(1,2)；(1,2)→(0,1)，再按 row/col 排
    expect(r.cells.map((c) => [c.row, c.col, c.count])).toEqual([[0, 1, 2], [1, 2, 1], [2, 0, 4]]);
    expect(r.coupling).toEqual([{ a: 2, b: 1, shared: 7, degree: 0.6 }]);
    // 环是键列表，不随下标变
    expect(r.cycles).toEqual([["z.Zeta", "a.alpha"]]);
    expect(r.container).toBe("Acme.Api");
  });

  it("does not mutate the input", () => {
    const before = JSON.stringify(dto);
    reorderDsm(dto, "alphabetical");
    expect(JSON.stringify(dto)).toBe(before);
  });

  it("same label falls back to key order", () => {
    const d: DsmDto = { ...dto, components: [comp("b.Core", "Core"), comp("a.Core", "core")], cells: [{ row: 0, col: 1, count: 1, kinds: {} }], coupling: [] };
    const r = reorderDsm(d, "alphabetical");
    expect(r.components.map((c) => c.key)).toEqual(["a.Core", "b.Core"]);
    expect(r.cells.map((c) => [c.row, c.col])).toEqual([[1, 0]]);
  });

  it("remaps violations row/col when the field exists (M5e), leaves other fields alone", () => {
    const withViolations: DsmDto = { ...dto, violations: [{ row: 0, col: 1, ruleId: 4, count: 3 }, { row: 1, col: 2, ruleId: 0, count: 0 }] };
    const r = reorderDsm(withViolations, "alphabetical");
    expect(r.violations).toEqual([{ row: 2, col: 0, ruleId: 4, count: 3 }, { row: 0, col: 1, ruleId: 0, count: 0 }]);
    expect(withViolations.violations![0]!.row).toBe(0);
    expect("violations" in reorderDsm(dto, "alphabetical")).toBe(false);
  });
});
