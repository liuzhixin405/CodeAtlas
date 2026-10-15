import { describe, expect, it } from "vitest";
import { clampListWidth, entryKey, MinListWidth } from "../webview/flowView";

describe("clampListWidth", () => {
  it("keeps the entry list between 180px and 60% of the panel", () => {
    expect(clampListWidth(100, 1000)).toBe(MinListWidth);
    expect(clampListWidth(420, 1000)).toBe(420);
    expect(clampListWidth(900, 1000)).toBe(600);
    expect(clampListWidth(250, 200)).toBe(MinListWidth); // 面板比下限还窄：只保证下限
    expect(clampListWidth(5000, 0)).toBe(5000);         // 面板宽度未知：不限上限
  });
});

describe("entryKey", () => {
  it("tells apart entries that share one symbol (top-level statements)", () => {
    const post = { symbolId: 9, kind: "http", route: "/api/brands/refresh", httpMethod: "POST" };
    const get = { symbolId: 9, kind: "http", route: "/api/versions/version", httpMethod: "GET" };
    const main = { symbolId: 9, kind: "hosted", route: null, httpMethod: null };
    expect(new Set([entryKey(post), entryKey(get), entryKey(main)]).size).toBe(3);
  });
});
