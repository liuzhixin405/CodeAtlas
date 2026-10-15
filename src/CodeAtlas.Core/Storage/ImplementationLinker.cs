namespace CodeAtlas.Core.Storage;

/// <summary>
/// 调用接口成员的边，补一条到实现的派生边（via = "impl"），这样"一路向下"不会停在接口上。
/// 只按代码里的 implements 关系推导，不看 DI 注册：唯一实现 0.9；2..20 个实现各 0.5（候选）；更多则不推导。
/// 分发链路的 conduit 边不参与（dispatches 边已经指向 Handler）。可重复执行，不会产生重复边。
/// </summary>
public sealed class ImplementationLinker(IndexDatabase db)
{
    public const string Via = "impl";
    public const int MaxImplementations = 20;

    /// <summary>返回新增的派生边数量。</summary>
    public int Link()
    {
        return db.Execute("""
            INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line, pos, ctx)
            SELECT c.from_id, impl.from_id, impl_sym.qualified_name, 'calls', c.source,
                   CASE WHEN cnt.n = 1 THEN 0.9 ELSE 0.5 END, c.file_id, c.line, $via, c.line, c.pos, c.ctx
            FROM edges c
            JOIN symbols t ON t.id = c.to_id
            JOIN symbols p ON p.id = t.parent_id AND p.kind = 'interface'
            JOIN (SELECT to_id, COUNT(*) AS n FROM edges WHERE kind = 'implements' GROUP BY to_id) cnt ON cnt.to_id = t.id
            JOIN edges impl ON impl.to_id = t.id AND impl.kind = 'implements'
            JOIN symbols impl_sym ON impl_sym.id = impl.from_id
            WHERE c.kind = 'calls' AND c.via IS NULL AND cnt.n <= $max
              AND NOT EXISTS (
                  SELECT 1 FROM edges d
                  WHERE d.from_id = c.from_id AND d.to_id = impl.from_id AND d.kind = 'calls' AND d.line = c.line AND d.via = $via)
            """, ("$via", Via), ("$max", MaxImplementations));
    }
}
