import { describe, expect, it, vi } from "vitest";
import { callersTitle, declarationIntact, fileLenses, LensCache, lensSpecs, StaleSuffix } from "../src/codeLensModel";
import type { FileLensDto, SymbolDto, SymbolLensDto } from "../webview/types";

function sym(id: number, name: string, startLine: number, endLine = startLine + 5): SymbolDto {
  return { id, kind: "method", name, qualifiedName: `Acme.Shop.OrderService.${name}`, signature: null, path: "src/OrderService.cs", startLine, startCol: 5, endLine, endCol: 6, visibility: "public" };
}

function lens(over: Partial<SymbolLensDto> = {}): SymbolLensDto {
  return { symbol: sym(1, "PlaceOrder", 10), callers: 2, callSites: 2, testCallers: 0, callees: 4, isEntry: false, ...over };
}

const titles = (item: SymbolLensDto, stale = false) => lensSpecs(item, stale).map((l) => l.title);

describe("CodeLens 文案", () => {
  it("普通方法：被 N 处调用 · 调用 M 个 · 影响范围", () => {
    expect(titles(lens())).toEqual(["被 2 处调用", "调用 4 个", "影响范围"]);
  });

  it("调用方个数 ≠ 处数：两个都写", () => {
    expect(callersTitle(3, 5)).toBe("被 3 个方法调用 5 处");
    expect(titles(lens({ callers: 3, callSites: 5 }))[0]).toBe("被 3 个方法调用 5 处");
    expect(callersTitle(1, 1)).toBe("被 1 处调用");
  });

  it("0 处调用：没有调用方，不能点", () => {
    const specs = lensSpecs(lens({ callers: 0, callSites: 0 }), false);
    expect(specs[0]).toMatchObject({ kind: "callers", title: "没有调用方", clickable: false });
    expect(specs[0]!.tooltip).toContain("死代码");
  });

  it("入口：最前面加「入口」，0 调用方的提示说明由框架调用", () => {
    const specs = lensSpecs(lens({ isEntry: true, callers: 0, callSites: 0 }), false);
    expect(specs.map((s) => s.title)).toEqual(["入口", "没有调用方", "调用 4 个", "影响范围"]);
    expect(specs[0]!.clickable).toBe(false);
    expect(specs[1]!.tooltip).toContain("框架");
    expect(specs[1]!.tooltip).not.toContain("死代码");
  });

  it("测试调用方 > 0：加「测试 K」，放在调用计数后面，能点", () => {
    const specs = lensSpecs(lens({ testCallers: 3 }), false);
    expect(specs.map((s) => s.title)).toEqual(["被 2 处调用", "测试 3", "调用 4 个", "影响范围"]);
    expect(specs[1]).toMatchObject({ kind: "tests", clickable: true });
    expect(titles(lens({ testCallers: 0 }))).not.toContain("测试 0");
  });

  it("只有测试调用：没有调用方 + 测试 K", () => {
    expect(titles(lens({ callers: 0, callSites: 0, testCallers: 2 }))).toEqual(["没有调用方", "测试 2", "调用 4 个", "影响范围"]);
  });

  it("调用 0 个：显示但不能点", () => {
    const specs = lensSpecs(lens({ callees: 0 }), false);
    expect(specs.find((s) => s.kind === "callees")).toMatchObject({ title: "调用 0 个", clickable: false });
  });

  it("索引过期：只在调用计数那一条后面加一次", () => {
    const specs = lensSpecs(lens({ isEntry: true, testCallers: 1 }), true);
    expect(specs.map((s) => s.title)).toEqual(["入口", `被 2 处调用${StaleSuffix}`, "测试 1", "调用 4 个", "影响范围"]);
    expect(specs.filter((s) => s.title.includes(StaleSuffix))).toHaveLength(1);
    expect(specs[1]!.tooltip).toContain("索引之后改过");
    expect(titles(lens({ callers: 0, callSites: 0 }), true)[0]).toBe(`没有调用方${StaleSuffix}`);
  });
});

describe("行号漂移时隐藏", () => {
  const text = (lines: string[]) => (line: number) => lines[line - 1];

  it("声明行包含方法名：显示", () => {
    expect(declarationIntact(sym(1, "PlaceOrder", 2), text(["class A {", "  public void PlaceOrder() {"]))).toBe(true);
  });

  it("那一行已不像声明（没有方法名）：隐藏", () => {
    expect(declarationIntact(sym(1, "PlaceOrder", 2, 2), text(["class A {", "  // 新插了一行", "  public void PlaceOrder() {"]))).toBe(false);
  });

  it("特性 / 注解 / 装饰器让 startLine 落在声明上方几行：跳过它们往下最多看 3 行（不超过方法末行）", () => {
    const lines = ["  [HttpPost]", "  [Authorize]", "  public IActionResult PlaceOrder() {", "  }"];
    expect(declarationIntact(sym(1, "PlaceOrder", 1, 4), text(lines))).toBe(true);
    expect(declarationIntact(sym(1, "placeOrder", 1, 4), text(["  @Post()", "  async placeOrder() {", "  }"]))).toBe(true);
    // 方法只有 1 行：不越过末行去找
    expect(declarationIntact(sym(1, "PlaceOrder", 1, 1), text(lines))).toBe(false);
    // 特性太多，超出往下看的行数
    expect(declarationIntact(sym(1, "PlaceOrder", 1, 9), text(["[A]", "[B]", "[C]", "[D]", "void PlaceOrder() {"]))).toBe(false);
  });

  it("上面插了普通代码 / 注释（不是特性）：不往下找，隐藏", () => {
    expect(declarationIntact(sym(1, "PlaceOrder", 1, 9), text(["  var x = 1;", "  void PlaceOrder() {"]))).toBe(false);
  });

  it("行号超出文档：隐藏", () => {
    expect(declarationIntact(sym(1, "PlaceOrder", 9, 9), text(["a", "b"]))).toBe(false);
  });

  const dto: FileLensDto = {
    path: "src/OrderService.cs",
    stale: false,
    symbols: [lens({ symbol: sym(1, "PlaceOrder", 2) }), lens({ symbol: sym(2, "CancelOrder", 4) })],
  };
  const doc = ["class OrderService {", "  void PlaceOrder() {}", "  // 编辑中插入的一行", "  void CancelOrder() {}"];
  const drifted = ["class OrderService {", "  // 编辑中插入的一行", "  void PlaceOrder() {}", "  void CancelOrder() {}"];

  it("文件干净且索引新鲜：不核对，全部显示", () => {
    const out = fileLenses(dto, { checkLines: false, lineText: text(drifted) });
    expect(out.map((m) => m.symbol.id)).toEqual([1, 2]);
  });

  it("在编辑没保存：只隐藏对不上的那一条", () => {
    const ok = fileLenses(dto, { checkLines: true, lineText: text(doc) });
    expect(ok.map((m) => [m.symbol.id, m.line])).toEqual([[1, 2], [2, 4]]);
    const out = fileLenses(dto, { checkLines: true, lineText: text(drifted) });
    expect(out.map((m) => m.symbol.id)).toEqual([2]);
  });

  it("stale 标记带进每个方法的标题", () => {
    const out = fileLenses({ ...dto, stale: true }, { checkLines: false, lineText: text(doc) });
    expect(out[0]!.lenses[0]!.title).toBe(`被 2 处调用${StaleSuffix}`);
  });
});

describe("LensCache：按文件缓存数据、按文档版本缓存结果", () => {
  const dto = (n: number): FileLensDto => ({ path: "a.cs", stale: false, symbols: [lens({ callers: n, callSites: n })] });

  function setup() {
    let n = 0;
    const fetch = vi.fn(async (_p: string) => dto(++n));
    const compute = vi.fn((d: FileLensDto) => d.symbols[0]!.callers);
    return { cache: new LensCache<number>(fetch), fetch, compute };
  }

  it("同一文件只请求一次；同一版本只算一次；版本变了只重算不请求", async () => {
    const { cache, fetch, compute } = setup();
    expect(await cache.lenses("/r/a.cs", 1, compute)).toBe(1);
    expect(await cache.lenses("/r/a.cs", 1, compute)).toBe(1);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(compute).toHaveBeenCalledTimes(1);
    expect(await cache.lenses("/r/a.cs", 2, compute)).toBe(1);
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(compute).toHaveBeenCalledTimes(2);
  });

  it("并发的两次请求共用一个", async () => {
    const { cache, fetch, compute } = setup();
    await Promise.all([cache.lenses("/r/a.cs", 1, compute), cache.lenses("/r/a.cs", 1, compute)]);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("保存（作废一个文件）后重新请求；别的文件不受影响", async () => {
    const { cache, fetch, compute } = setup();
    await cache.lenses("/r/a.cs", 1, compute);
    await cache.lenses("/r/b.cs", 1, compute);
    cache.invalidate("/r/a.cs");
    expect(await cache.lenses("/r/a.cs", 1, compute)).toBe(3);
    expect(await cache.lenses("/r/b.cs", 1, compute)).toBe(2);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it("索引完成（全部作废）后全部重新请求", async () => {
    const { cache, fetch, compute } = setup();
    await cache.lenses("/r/a.cs", 1, compute);
    await cache.lenses("/r/b.cs", 1, compute);
    cache.invalidate();
    await cache.lenses("/r/a.cs", 1, compute);
    await cache.lenses("/r/b.cs", 1, compute);
    expect(fetch).toHaveBeenCalledTimes(4);
  });

  it("请求失败不缓存，下次再试", async () => {
    const fetch = vi.fn<(p: string) => Promise<FileLensDto | null>>().mockRejectedValueOnce(new Error("down")).mockResolvedValue(dto(7));
    const cache = new LensCache<number>(fetch);
    await expect(cache.lenses("/r/a.cs", 1, (d) => d.symbols[0]!.callers)).rejects.toThrow("down");
    expect(await cache.lenses("/r/a.cs", 1, (d) => d.symbols[0]!.callers)).toBe(7);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("请求在途时被作废：这次的结果不记，下次重新请求", async () => {
    let release!: (d: FileLensDto) => void;
    const fetch = vi.fn<(p: string) => Promise<FileLensDto | null>>()
      .mockImplementationOnce(() => new Promise((r) => { release = r; }))
      .mockResolvedValue(dto(9));
    const cache = new LensCache<number>(fetch);
    const first = cache.lenses("/r/a.cs", 1, (d) => d.symbols[0]!.callers);
    cache.invalidate();
    release(dto(1));
    expect(await first).toBe(1);
    expect(await cache.lenses("/r/a.cs", 1, (d) => d.symbols[0]!.callers)).toBe(9);
  });

  it("服务没有 lens 接口（null）：结果为 null", async () => {
    const cache = new LensCache<number>(async () => null);
    expect(await cache.lenses("/r/a.cs", 1, () => 1)).toBeNull();
  });
});
