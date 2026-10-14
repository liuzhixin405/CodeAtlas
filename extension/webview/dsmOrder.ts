import type { DsmDto } from "./types";

/** DSM 的两个预设版式：服务端给的依赖序（原样）或按组件名字母序。 */
export type DsmPreset = "dependency-order" | "alphabetical";

function compareLabel(a: { label: string; key: string }, b: { label: string; key: string }): number {
  const la = a.label.toLowerCase(), lb = b.label.toLowerCase();
  if (la !== lb) return la < lb ? -1 : 1;
  return a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
}

/**
 * 按预设重排 DSM：`dependency-order` 原样返回同一个对象；`alphabetical` 按 label（忽略大小写，同名按 key）重排 components，
 * 并把 cells 的 row/col、coupling 的 a/b（以及有的话 violations 的 row/col）从旧下标映射到新下标。
 * cycles 是组件键列表，不含下标，不用动。
 * 纯函数，不改输入。
 */
export function reorderDsm(dto: DsmDto, preset: DsmPreset | string): DsmDto {
  if (preset !== "alphabetical") return dto;
  const order = dto.components.map((c, i) => ({ c, i })).sort((x, y) => compareLabel(x.c, y.c));
  const newIndex = new Array<number>(dto.components.length);
  order.forEach(({ i }, at) => { newIndex[i] = at; });
  const remap = (i: number): number => newIndex[i] ?? i;
  const result: DsmDto = {
    ...dto,
    components: order.map(({ c }) => c),
    cells: dto.cells.map((cell) => ({ ...cell, row: remap(cell.row), col: remap(cell.col) })).sort((a, b) => a.row - b.row || a.col - b.col),
    coupling: dto.coupling.map((cp) => ({ ...cp, a: remap(cp.a), b: remap(cp.b) })),
  };
  // 架构规则违规格子（M5e 的 violations，带 row/col 下标）：有就一起重映射；老服务没有这个字段就不加
  if (dto.violations) result.violations = dto.violations.map((v) => ({ ...v, row: remap(v.row), col: remap(v.col) }));
  return result;
}
