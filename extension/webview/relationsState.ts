import type { SymbolDto } from "./types";

export interface RelationsCrumb {
  symbol: SymbolDto;
  label: string;
}

/**
 * 模块关系的中心历史（面包屑），语义与调用图的焦点历史一致：
 * 新中心推入并丢弃当前位置之后的历史；连续以同一个单元为中心不重复推入；回退只移动位置。纯状态，便于测试。
 */
export class RelationsHistory {
  private items: RelationsCrumb[] = [];
  private index = -1;

  get crumbs(): readonly RelationsCrumb[] {
    return this.items;
  }

  get currentIndex(): number {
    return this.index;
  }

  get current(): SymbolDto | null {
    return this.items[this.index]?.symbol ?? null;
  }

  /** 以 symbol 为新中心。与当前中心是同一个单元时原地替换（数据可能刷新过）。 */
  visit(symbol: SymbolDto, label = symbol.name): void {
    if (this.current?.id === symbol.id) { this.items[this.index] = { symbol, label }; return; }
    this.items = this.items.slice(0, this.index + 1);
    this.items.push({ symbol, label });
    this.index = this.items.length - 1;
  }

  /** 用刷新后的符号（索引重建后 id 可能变）替换当前中心，不新增面包屑。没有中心时等同 visit。 */
  replaceCurrent(symbol: SymbolDto, label = symbol.name): void {
    if (this.index < 0) { this.visit(symbol, label); return; }
    this.items[this.index] = { symbol, label };
  }

  goTo(index: number): SymbolDto | null {
    if (index < 0 || index >= this.items.length) return null;
    this.index = index;
    return this.current;
  }

  back(): SymbolDto | null {
    return this.index > 0 ? this.goTo(this.index - 1) : null;
  }

  reset(): void {
    this.items = [];
    this.index = -1;
  }
}
