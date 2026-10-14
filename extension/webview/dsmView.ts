import type { DsmCellDto, DsmComponentDto, DsmCouplingDto, DsmDto, DsmRuleDto } from "./types";

/** 格子着色分档：按与最大值的比例分 4 档，0 不画。 */
export function bucket(count: number, max: number): 0 | 1 | 2 | 3 | 4 {
  if (count <= 0 || max <= 0) return 0;
  const r = count / max;
  return r > 0.75 ? 4 : r > 0.5 ? 3 : r > 0.25 ? 2 : 1;
}

/** 热度分档：按修订数相对最大值。 */
export function heat(revisions: number, max: number): 0 | 1 | 2 | 3 {
  if (revisions <= 0 || max <= 0) return 0;
  const r = revisions / max;
  return r > 0.66 ? 3 : r > 0.33 ? 2 : 1;
}

export interface DsmCellModel {
  row: number;
  col: number;
  count: number;
  bucket: 0 | 1 | 2 | 3 | 4;
  /** 行列在同一个多成员分量里（环）。 */
  cycle: boolean;
  /** 常一起改却没有静态边。 */
  coupled: DsmCouplingDto | null;
  cell: DsmCellDto | null;
  /** 这一格命中的声明规则（按规则 id 排序）；没有为空数组。 */
  violations: DsmCellViolation[];
  /** 有 forbid 违规（行依赖了不该依赖的列）。 */
  forbidden: boolean;
  /** 有 require 未满足（行该依赖列所在的范围却一条都没有）。 */
  requireUnmet: boolean;
}

export interface DsmCellViolation {
  rule: DsmRuleDto;
  /** forbid：违规边数；require 未满足：0。 */
  count: number;
}

export interface DsmModel {
  components: DsmComponentDto[];
  rows: DsmCellModel[][];
  maxCount: number;
  maxRevisions: number;
  cycleSccs: Set<number>;
  /** 声明规则总数（含只展示的 placement）。 */
  ruleCount: number;
  /** 有违规的格子数（同一格多条规则算一处）。 */
  violationCells: number;
}

/** 表头统计：`声明规则 N 条，违规 M 处`；没有规则返回 null（不显示）。 */
export function rulesSummary(model: Pick<DsmModel, "ruleCount" | "violationCells">): string | null {
  if (model.ruleCount === 0) return null;
  return `声明规则 ${model.ruleCount} 条，违规 ${model.violationCells} 处`;
}

const KIND_LABEL: Record<string, string> = {
  "forbid-dependency": "禁止依赖",
  "require-dependency": "必须依赖",
  placement: "放置位置",
};

/** 一条命中规则的说明：`禁止依赖 A → B：测试名（文件:行），3 条边`。yaml 规则没有测试名与行号。 */
export function ruleLine(v: DsmCellViolation): string {
  const r = v.rule;
  const where = r.line > 0 ? `${r.file}:${r.line}` : r.file;
  const who = r.testName ? `${r.testName}（${where}）` : where;
  const what = v.count > 0 ? `，${v.count} 条边` : r.kind === "require-dependency" ? "，一条依赖都没有" : "";
  return `${KIND_LABEL[r.kind] ?? r.kind} ${r.from} → ${r.to ?? "?"}：${who}${what}`;
}

/** 把 DTO 变成逐格的渲染模型。纯函数，便于测试。 */
export function buildModel(dto: DsmDto): DsmModel {
  const n = dto.components.length;
  const cellAt = new Map<string, DsmCellDto>();
  for (const c of dto.cells) cellAt.set(`${c.row}:${c.col}`, c);
  const coupledAt = new Map<string, DsmCouplingDto>();
  for (const c of dto.coupling) { coupledAt.set(`${c.a}:${c.b}`, c); coupledAt.set(`${c.b}:${c.a}`, c); }
  const sccSize = new Map<number, number>();
  for (const c of dto.components) sccSize.set(c.scc, (sccSize.get(c.scc) ?? 0) + 1);
  const cycleSccs = new Set([...sccSize.entries()].filter(([, size]) => size > 1).map(([scc]) => scc));
  const maxCount = Math.max(0, ...dto.cells.map((c) => c.count));
  const maxRevisions = Math.max(0, ...dto.components.map((c) => c.hot?.revisions ?? 0));
  // 违规按格子收拢；规则 id 引用 dto.rules（找不到的忽略，不让页面崩）
  const ruleById = new Map<number, DsmRuleDto>();
  for (const rule of dto.rules ?? []) ruleById.set(rule.id, rule);
  const violationsAt = new Map<string, DsmCellViolation[]>();
  for (const v of dto.violations ?? []) {
    const rule = ruleById.get(v.ruleId);
    if (!rule || v.row === v.col) continue;
    const key = `${v.row}:${v.col}`;
    const list = violationsAt.get(key) ?? [];
    list.push({ rule, count: v.count });
    violationsAt.set(key, list);
  }
  for (const list of violationsAt.values()) list.sort((a, b) => a.rule.id - b.rule.id);
  const rows: DsmCellModel[][] = [];
  let violationCells = 0;
  for (let r = 0; r < n; r++) {
    const row: DsmCellModel[] = [];
    for (let c = 0; c < n; c++) {
      const cell = cellAt.get(`${r}:${c}`) ?? null;
      const sameScc = dto.components[r]!.scc === dto.components[c]!.scc;
      const violations = violationsAt.get(`${r}:${c}`) ?? [];
      if (violations.length > 0) violationCells++;
      row.push({
        row: r, col: c, count: cell?.count ?? 0, bucket: bucket(cell?.count ?? 0, maxCount),
        cycle: r !== c && sameScc && cycleSccs.has(dto.components[r]!.scc),
        coupled: r !== c ? coupledAt.get(`${r}:${c}`) ?? null : null,
        cell,
        violations,
        forbidden: violations.some((v) => v.rule.kind !== "require-dependency"),
        requireUnmet: violations.some((v) => v.rule.kind === "require-dependency"),
      });
    }
    rows.push(row);
  }
  return { components: dto.components, rows, maxCount, maxRevisions, cycleSccs, ruleCount: dto.rules?.length ?? 0, violationCells };
}

/** 格子的 class：分档 + 环 + 隐性耦合 + 规则违规（violation 红实框 / require-unmet 红虚框）。对角线是 diag。 */
export function cellClass(cell: DsmCellModel): string {
  if (cell.row === cell.col) return "diag";
  return `b${cell.bucket}` + (cell.cycle ? " cycle" : "") + (cell.coupled ? " coupled" : "") +
    (cell.forbidden ? " violation" : "") + (cell.requireUnmet ? " require-unmet" : "");
}

/** 格子里的字：边数；没有边时 require 未满足写 `!`，隐性耦合写 `~`。 */
export function cellText(cell: DsmCellModel): string {
  if (cell.row === cell.col) return "";
  if (cell.count > 0) return String(cell.count);
  if (cell.requireUnmet) return "!";
  if (cell.coupled) return "~";
  return "";
}

export interface DsmViewCallbacks {
  onCellSelect(cell: DsmCellModel, from: DsmComponentDto, to: DsmComponentDto): void;
  onComponentSelect(component: DsmComponentDto): void;
  onComponentDoubleClick(component: DsmComponentDto): void;
}

/** 用一张 HTML 表格画 DSM：行依赖列；行头带符号数与 git 热度条。 */
export class DsmView {
  constructor(private readonly container: HTMLElement, private readonly cb: DsmViewCallbacks) {}

  render(dto: DsmDto): void {
    const model = buildModel(dto);
    this.container.innerHTML = "";
    if (model.components.length === 0) {
      const p = document.createElement("p");
      p.className = "dsm-empty";
      p.textContent = dto.note ?? "没有组件";
      this.container.appendChild(p);
      return;
    }
    const summary = rulesSummary(model);
    if (summary) {
      const p = document.createElement("div");
      p.className = "dsm-rules-summary" + (model.violationCells > 0 ? " has-violations" : "");
      p.textContent = summary;
      p.title = "规则来自测试项目里的 NetArchTest 链与 .codeatlas/architecture.yaml；红框 = 违反禁止依赖，红虚框 ! = 必须依赖却没有；组件比规则粗时这一层不判，钻进去才判";
      this.container.appendChild(p);
    }
    // 格子命中的规则列在表格下方（详情栏由 main.ts 写，规则另起一块，点格子时刷新）
    const ruleDetail = document.createElement("div");
    ruleDetail.className = "dsm-rule-detail";
    ruleDetail.style.display = "none";
    const table = document.createElement("table");
    table.className = "dsm";
    const thead = document.createElement("thead");
    const headRow = document.createElement("tr");
    headRow.appendChild(document.createElement("th")).className = "corner";
    model.components.forEach((c, i) => {
      const th = document.createElement("th");
      th.className = "col" + (model.cycleSccs.has(c.scc) ? " cycle" : "");
      th.textContent = String(i + 1);
      th.title = c.label;
      headRow.appendChild(th);
    });
    thead.appendChild(headRow);
    table.appendChild(thead);
    const tbody = document.createElement("tbody");
    model.rows.forEach((row, r) => {
      const comp = model.components[r]!;
      const tr = document.createElement("tr");
      const th = document.createElement("th");
      th.className = "row" + (model.cycleSccs.has(comp.scc) ? " cycle" : "") + (comp.hasChildren ? " drill" : "");
      const heatLevel = heat(comp.hot?.revisions ?? 0, model.maxRevisions);
      th.innerHTML = `<span class="idx">${r + 1}</span><span class="name" title="${esc(comp.key)}">${esc(comp.label)}</span><span class="count">${comp.symbols}</span>` +
        (comp.hot ? `<span class="heat h${heatLevel}" title="修订 ${comp.hot.revisions} 次，最近 ${comp.hot.lastCommit}"></span>` : "");
      th.onclick = () => this.cb.onComponentSelect(comp);
      th.ondblclick = () => this.cb.onComponentDoubleClick(comp);
      tr.appendChild(th);
      row.forEach((cell) => {
        const td = document.createElement("td");
        td.className = cellClass(cell);
        if (cell.row !== cell.col) {
          td.textContent = cellText(cell);
          const to = model.components[cell.col]!;
          td.title = `${comp.label} → ${to.label}` + (cell.count ? `：${cell.count}` : "") + (cell.coupled ? `；常一起改 ${cell.coupled.shared} 次` : "") +
            cell.violations.map((v) => "\n⚠ " + ruleLine(v)).join("");
          td.onclick = () => {
            this.cb.onCellSelect(cell, comp, to);
            showRuleDetail(ruleDetail, cell, comp, to);
          };
        }
        tr.appendChild(td);
      });
      tbody.appendChild(tr);
    });
    table.appendChild(tbody);
    this.container.appendChild(table);
    this.container.appendChild(ruleDetail);
  }
}

/** 点格子：有命中规则就列出来（测试名 + 文件:行 + 还原说明），没有就收起。 */
function showRuleDetail(el: HTMLElement, cell: DsmCellModel, from: DsmComponentDto, to: DsmComponentDto): void {
  if (cell.violations.length === 0) { el.style.display = "none"; el.innerHTML = ""; return; }
  el.style.display = "";
  el.innerHTML = `<div class="head">${esc(from.label)} → ${esc(to.label)}：命中 ${cell.violations.length} 条声明规则</div>` +
    cell.violations.map((v) =>
      `<div class="rule ${v.rule.kind === "require-dependency" ? "require" : "forbid"}">${esc(ruleLine(v))}` +
      (v.rule.source !== "netarchtest" && v.rule.source !== "yaml" ? `<span class="muted">${esc(v.rule.source)}</span>` : "") + "</div>").join("");
}

function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
