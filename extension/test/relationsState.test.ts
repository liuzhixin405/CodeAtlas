import { describe, expect, it } from "vitest";
import { RelationsHistory } from "../webview/relationsState";
import type { SymbolDto } from "../webview/types";

const s = (id: number, name = `T${id}`): SymbolDto => ({
  id, kind: "class", name, qualifiedName: `Acme.${name}`, signature: null, path: `${name}.cs`, startLine: 1, startCol: 1, endLine: 2, endCol: 1, visibility: null,
});

describe("RelationsHistory", () => {
  it("pushes new centres and ignores repeats of the current one", () => {
    const h = new RelationsHistory();
    expect(h.current).toBeNull();
    h.visit(s(1));
    h.visit(s(2));
    h.visit(s(2, "T2-refreshed"));
    expect(h.crumbs.map((c) => c.label)).toEqual(["T1", "T2-refreshed"]);
    expect(h.currentIndex).toBe(1);
  });

  it("goes back without losing forward history until a new centre is visited", () => {
    const h = new RelationsHistory();
    [1, 2, 3].forEach((id) => h.visit(s(id)));
    expect(h.goTo(0)?.id).toBe(1);
    expect(h.crumbs).toHaveLength(3);
    expect(h.back()).toBeNull();
    h.goTo(1);
    expect(h.back()?.id).toBe(1);
    h.visit(s(9));
    expect(h.crumbs.map((c) => c.symbol.id)).toEqual([1, 9]);
    expect(h.goTo(5)).toBeNull();
    expect(h.current?.id).toBe(9);
  });

  it("replaces the current crumb after a refresh (ids change) and resets", () => {
    const h = new RelationsHistory();
    h.replaceCurrent(s(1));
    expect(h.crumbs).toHaveLength(1);
    h.visit(s(2));
    h.goTo(0);
    h.replaceCurrent(s(11, "T1"), "T1 new");
    expect(h.crumbs.map((c) => [c.symbol.id, c.label])).toEqual([[11, "T1 new"], [2, "T2"]]);
    h.reset();
    expect(h.crumbs).toHaveLength(0);
    expect(h.current).toBeNull();
  });
});
