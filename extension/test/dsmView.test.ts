import { describe, expect, it } from "vitest";
import { bucket, buildModel, cellClass, cellText, heat, hotRevisionsText, ruleLine, rulesSummary } from "../webview/dsmView";
import type { DsmDto } from "../webview/types";

const dto: DsmDto = {
  container: "Acme.Api", root: "", gitAvailable: true, note: null,
  components: [
    { key: "Acme.App", label: "App", symbols: 9, files: 3, hasChildren: true, scc: 0, hot: { revisions: 21, added: 1, deleted: 1, ageMonths: 1, lastCommit: "2026-09-01" } },
    { key: "Acme.Infra", label: "Infra", symbols: 3, files: 1, hasChildren: false, scc: 0, hot: { revisions: 3, added: 1, deleted: 1, ageMonths: 8, lastCommit: "2026-02-01" } },
    { key: "Acme.Api", label: "Api", symbols: 3, files: 1, hasChildren: false, scc: 1, hot: null },
    { key: "Acme.Util", label: "Util", symbols: 1, files: 1, hasChildren: false, scc: 2, hot: null },
  ],
  cells: [
    { row: 0, col: 1, count: 1, kinds: { calls: 1 } },
    { row: 1, col: 0, count: 1, kinds: { calls: 1 } },
    { row: 2, col: 0, count: 4, kinds: { calls: 4 } },
    { row: 2, col: 1, count: 1, kinds: { calls: 1 } },
  ],
  cycles: [["Acme.App", "Acme.Infra"]],
  coupling: [{ a: 2, b: 3, shared: 7, degree: 0.6 }],
};

describe("dsm model", () => {
  it("buckets counts relative to the max and heat relative to max revisions", () => {
    expect([bucket(0, 4), bucket(1, 4), bucket(2, 4), bucket(3, 4), bucket(4, 4)]).toEqual([0, 1, 2, 3, 4]);
    expect(bucket(5, 0)).toBe(0);
    expect([heat(0, 21), heat(3, 21), heat(10, 21), heat(21, 21)]).toEqual([0, 1, 2, 3]);
  });

  it("marks cycle cells only inside multi-member components and coupled cells symmetrically", () => {
    const m = buildModel(dto);
    expect(m.maxCount).toBe(4);
    expect(m.maxRevisions).toBe(21);
    expect([...m.cycleSccs]).toEqual([0]);
    expect(m.rows[0]![1]!.cycle).toBe(true);   // App → Infra
    expect(m.rows[1]![0]!.cycle).toBe(true);   // Infra → App
    expect(m.rows[2]![0]!.cycle).toBe(false);  // Api → App 不是环
    expect(m.rows[2]![0]!.bucket).toBe(4);
    expect(m.rows[2]![1]!.bucket).toBe(1);
    expect(m.rows[0]![0]!.count).toBe(0);      // 对角线
    expect(m.rows[2]![3]!.coupled?.shared).toBe(7);
    expect(m.rows[3]![2]!.coupled?.shared).toBe(7);
    expect(m.rows[3]![0]!.coupled).toBeNull();
    // 没有 rules / violations 字段（老服务）：不显示统计，格子没有规则标记
    expect(m.ruleCount).toBe(0);
    expect(rulesSummary(m)).toBeNull();
    expect(m.rows.flat().every((c) => c.violations.length === 0)).toBe(true);
    expect(cellClass(m.rows[2]![3]!)).toBe("b0 coupled");
    expect(cellText(m.rows[2]![3]!)).toBe("~");
  });
});

describe("dsm rules overlay", () => {
  const rule = (id: number, kind: string, from: string, to: string, testName: string | null, line: number, source = "netarchtest") =>
    ({ id, kind, from, fromMatch: "prefix", to, toMatch: "prefix", file: line > 0 ? "Tests/Arch/ArchTests.cs" : ".codeatlas/architecture.yaml", line, testName, source });
  const withRules: DsmDto = {
    ...dto,
    rules: [
      rule(0, "forbid-dependency", "Acme.App", "Acme.Infra", "App_NotInfra", 29, "netarchtest（主语限于实现 ICommandHandler 的类型）"),
      rule(1, "require-dependency", "Acme.Util", "Acme.App", "Util_UsesApp", 72),
      rule(2, "placement", "ICommand", "Modules", "Cmd_InModules", 15),
      rule(3, "forbid-dependency", "*", "Acme.Infra", null, 0, "yaml"),
    ],
    // (0,1)：App → Infra 1 条边同时违反规则 0 与 3（故意倒序给，模型要按 id 排）；(3,0)：Util 一条都没依赖 App
    violations: [
      { row: 0, col: 1, ruleId: 3, count: 1 },
      { row: 0, col: 1, ruleId: 0, count: 1 },
      { row: 2, col: 1, ruleId: 3, count: 1 },
      { row: 3, col: 0, ruleId: 1, count: 0 },
      { row: 1, col: 1, ruleId: 0, count: 1 },   // 对角线：忽略
      { row: 2, col: 0, ruleId: 99, count: 4 },  // 找不到的规则：忽略
    ],
  };

  it("collects violations per cell, counts violated cells, and summarises", () => {
    const m = buildModel(withRules);
    expect(m.ruleCount).toBe(4);
    // 违规格子 = (0,1) (2,1) (3,0) 三处；(1,1) 对角线、ruleId 99 都不算
    expect(m.violationCells).toBe(3);
    expect(rulesSummary(m)).toBe("声明规则 4 条，违规 3 处");
    expect(m.rows[0]![1]!.violations.map((v) => v.rule.id)).toEqual([0, 3]);
    expect(m.rows[2]![0]!.violations).toEqual([]);
    expect(m.rows[1]![1]!.violations).toEqual([]);
  });

  it("forbid cells get a solid red frame, unmet require gets a dashed frame with !", () => {
    const m = buildModel(withRules);
    const appInfra = m.rows[0]![1]!;
    expect([appInfra.forbidden, appInfra.requireUnmet]).toEqual([true, false]);
    expect(cellClass(appInfra)).toBe("b1 cycle violation");   // 1 / max 4 → 第 1 档；App↔Infra 是环
    expect(cellText(appInfra)).toBe("1");
    const utilApp = m.rows[3]![0]!;
    expect([utilApp.forbidden, utilApp.requireUnmet, utilApp.count]).toEqual([false, true, 0]);
    expect(cellClass(utilApp)).toBe("b0 require-unmet");
    expect(cellText(utilApp)).toBe("!");
    expect(cellClass(m.rows[0]![0]!)).toBe("diag");
  });

  it("rule lines name the test and file:line; yaml rules have no line", () => {
    const m = buildModel(withRules);
    const [r0, r3] = m.rows[0]![1]!.violations;
    expect(ruleLine(r0!)).toBe("禁止依赖 Acme.App → Acme.Infra：App_NotInfra（Tests/Arch/ArchTests.cs:29），1 条边");
    expect(ruleLine(r3!)).toBe("禁止依赖 * → Acme.Infra：.codeatlas/architecture.yaml，1 条边");
    expect(ruleLine(m.rows[3]![0]!.violations[0]!)).toBe("必须依赖 Acme.Util → Acme.App：Util_UsesApp（Tests/Arch/ArchTests.cs:72），一条依赖都没有");
  });

  it("rules without violations still show the count; reorderDsm keeps violations on the same components", async () => {
    const quiet = buildModel({ ...withRules, violations: [] });
    expect(rulesSummary(quiet)).toBe("声明规则 4 条，违规 0 处");
    const { reorderDsm } = await import("../webview/dsmOrder");
    // 字母序：Api(0) App(1) Infra(2) Util(3)；原 (0,1) App→Infra → (1,2)，原 (3,0) Util→App → (3,1)
    const m = buildModel(reorderDsm(withRules, "alphabetical"));
    expect(m.rows[1]![2]!.violations.map((v) => v.rule.id)).toEqual([0, 3]);
    expect(m.rows[3]![1]!.requireUnmet).toBe(true);
    expect(m.violationCells).toBe(3);
  });
});

describe("组件修订数文案", () => {
  it("有去重后的提交数写「N 次提交」，没有就照实写「文件修订合计 N」（同一提交改几个文件算几次）", () => {
    const hot = { revisions: 38, added: 1, deleted: 1, ageMonths: 0, lastCommit: "2026-10-16" };
    expect(hotRevisionsText({ ...hot, commits: 18 })).toBe("18 次提交");
    expect(hotRevisionsText(hot)).toBe("文件修订合计 38");
    expect(hotRevisionsText({ ...hot, commits: null })).toBe("文件修订合计 38");
  });
});
