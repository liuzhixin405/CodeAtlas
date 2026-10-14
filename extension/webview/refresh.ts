import type { SymbolDto } from "./types";

/** 刷新焦点时需要的最小查询面，便于测试。 */
export interface SymbolLookup {
  symbol(id: number): Promise<SymbolDto | null>;
  symbolAt(path: string, line: number, col: number): Promise<SymbolDto | null>;
  search(q: string, limit?: number): Promise<SymbolDto[]>;
}

/**
 * 增量索引会重建被修改文件的全部符号，id 随之变化。
 * 依次尝试：原 id → 原位置 → 按限定名精确匹配。都找不到返回 null。
 */
export async function resolveRefreshedSymbol(api: SymbolLookup, old: SymbolDto): Promise<SymbolDto | null> {
  const byId = await api.symbol(old.id);
  if (byId) return byId;
  const byPos = await api.symbolAt(old.path, old.startLine, old.startCol);
  if (byPos && byPos.qualifiedName === old.qualifiedName) return byPos;
  const hits = await api.search(old.name, 50);
  return hits.find((s) => s.qualifiedName === old.qualifiedName) ?? byPos ?? null;
}
