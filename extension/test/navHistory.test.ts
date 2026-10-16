import { describe, expect, it } from "vitest";
import { MaxNavEntries, NavHistory, navTitle, type NavEntry } from "../webview/navHistory";

/** 一条记录：restore 时把 key 写进 shown（模拟"画出那个画面"），可选在恢复途中再记录一条（模拟恢复动作调了会记录的导航函数）。 */
function entry(key: string, shown: string[], h?: NavHistory, recordDuringRestore?: string): NavEntry {
  return {
    key,
    label: key.toUpperCase(),
    restore: async () => {
      await Promise.resolve();
      if (h && recordDuringRestore) h.record(entry(recordDuringRestore, shown));
      shown.push(key);
    },
  };
}

describe("NavHistory：面板的后退 / 前进", () => {
  it("后退、前进按记录的顺序恢复；两头不能再走", async () => {
    const shown: string[] = [];
    const h = new NavHistory();
    expect(h.canBack).toBe(false);
    h.record(entry("a", shown));
    h.record(entry("b", shown));
    h.record(entry("c", shown));
    expect(h.backTarget?.key).toBe("b");
    expect(await h.back()).toBe(true);
    expect(await h.back()).toBe(true);
    expect(await h.back()).toBe(false);
    expect(shown).toEqual(["b", "a"]);
    expect(h.current?.key).toBe("a");
    expect(await h.forward()).toBe(true);
    expect(shown).toEqual(["b", "a", "b"]);
    expect(h.forwardTarget?.key).toBe("c");
  });

  it("后退以后再去新地方：前进分支丢掉", async () => {
    const shown: string[] = [];
    const h = new NavHistory();
    for (const k of ["a", "b", "c"]) h.record(entry(k, shown));
    await h.back();
    h.record(entry("d", shown));
    expect(h.canForward).toBe(false);
    expect(h.backTarget?.key).toBe("b");
    expect(h.length).toBe(3);
  });

  it("与当前同一个画面（同 key）只替换，不算一步", () => {
    const h = new NavHistory();
    h.record(entry("a", []));
    h.record(entry("b", []));
    h.record({ key: "b", label: "B2", restore: () => {} });
    expect(h.length).toBe(2);
    expect(h.current?.label).toBe("B2");
  });

  it("恢复途中（含异步之后）调到的导航不记录，时间线不被改", async () => {
    const shown: string[] = [];
    const h = new NavHistory();
    h.record(entry("a", shown, undefined));
    h.record(entry("b", shown));
    // 让 a 的恢复里再"记录"一次 x：应当被忽略
    const a = h.backTarget!;
    a.restore = entry("a", shown, h, "x").restore;
    await h.back();
    expect(h.current?.key).toBe("a");
    expect(h.length).toBe(2);
    expect(h.forwardTarget?.key).toBe("b");
    expect(h.isRestoring).toBe(false);
  });

  it("超过上限丢最老的；reset 清空", () => {
    const h = new NavHistory();
    for (let i = 0; i < MaxNavEntries + 5; i++) h.record(entry(`k${i}`, []));
    expect(h.length).toBe(MaxNavEntries);
    expect(h.current?.key).toBe(`k${MaxNavEntries + 4}`);
    h.reset();
    expect(h.length).toBe(0);
    expect(h.canBack).toBe(false);
  });

  it("每次变化都通知（按钮跟着更新）", async () => {
    let n = 0;
    const h = new NavHistory(() => n++);
    h.record(entry("a", []));
    h.record(entry("b", []));
    const before = n;
    await h.back();
    expect(n).toBeGreaterThan(before);
  });

  it("按钮提示写去哪、快捷键", () => {
    expect(navTitle("back", { key: "x", label: "OrderService.Place", restore: () => {} })).toBe("后退到：OrderService.Place（Alt+←）");
    expect(navTitle("forward", null)).toBe("没有可前进的（Alt+→）");
  });
});
