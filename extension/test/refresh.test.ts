import { describe, expect, it } from "vitest";
import { resolveRefreshedSymbol, type SymbolLookup } from "../webview/refresh";
import type { SymbolDto } from "../webview/types";

const old: SymbolDto = { id: 2, kind: "function", name: "a", qualifiedName: "a.ts:a", signature: null, path: "a.ts", startLine: 1, startCol: 17, endLine: 1, endCol: 24, visibility: null };
const renewed: SymbolDto = { ...old, id: 7, startLine: 2 };

function fake(overrides: Partial<SymbolLookup>): SymbolLookup {
  return {
    symbol: async () => null,
    symbolAt: async () => null,
    search: async () => [],
    ...overrides,
  };
}

describe("resolveRefreshedSymbol", () => {
  it("returns the symbol by id when it still exists", async () => {
    const r = await resolveRefreshedSymbol(fake({ symbol: async (id) => (id === 2 ? old : null) }), old);
    expect(r?.id).toBe(2);
  });

  it("falls back to the symbol at the old position when the id is gone (file was re-indexed)", async () => {
    const r = await resolveRefreshedSymbol(fake({ symbolAt: async (p, l, c) => (p === "a.ts" && l === 1 && c === 17 ? renewed : null) }), old);
    expect(r?.id).toBe(7);
  });

  it("falls back to an exact qualifiedName match from search when the position moved", async () => {
    const r = await resolveRefreshedSymbol(fake({ search: async () => [{ ...renewed, qualifiedName: "a.ts:other" }, renewed] }), old);
    expect(r?.id).toBe(7);
  });

  it("returns null when nothing matches", async () => {
    expect(await resolveRefreshedSymbol(fake({}), old)).toBeNull();
  });
});
