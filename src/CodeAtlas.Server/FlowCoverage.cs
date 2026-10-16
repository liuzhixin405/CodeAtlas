namespace CodeAtlas.Server;

/// <param name="EntryId">抽到的 HTTP 入口。</param>
/// <param name="RootId">时序图的根：入口分派到的 Handler，没有分派就是入口本身。</param>
/// <param name="Targets">根的"业务调用"目标个数（见 <see cref="FlowCoverage"/>）。</param>
/// <param name="Seen">其中出现在时序图前 <see cref="FlowCoverageReport.Levels"/> 层的个数。</param>
/// <param name="Hit">Seen ≥ 1。</param>
/// <param name="NextRootId">下一层的根：根的第一个业务调用目标（按大纲顺序，通常是 Service / 仓储方法，不在入口表里）；没有可判定的为 null。</param>
/// <param name="NextHit">以 NextRootId 为根的时序图前几层里出现了它自己的业务调用目标；NextRootId 为 null 时为 null。</param>
public sealed record FlowCoverageItem(long EntryId, string? Route, long RootId, int Targets, int Seen, bool Hit, long? NextRootId, bool? NextHit);

/// <param name="Entries">容器里的 HTTP 入口总数。</param>
/// <param name="Skipped">抽样途中跳过的入口：根没有业务调用（只调框架 / 只 new 对象），无从判定。</param>
/// <param name="Ratio">Hits / Sampled：前 levels 层至少看到一个业务调用的比例（验收线 90%）。</param>
/// <param name="Recall">每个根"看到的业务调用 / 全部业务调用"的平均：比 Ratio 严，看排序把多少业务调用留在了第一屏。</param>
/// <param name="NextSampled">下一层根（任意方法，多数不是入口）参与判定的个数；NextHits / NextRatio 同 Hits / Ratio。</param>
public sealed record FlowCoverageReport(
    string ContainerKey, int Entries, int Sampled, int Skipped, int Hits, int Levels, double Ratio, double Recall,
    int NextSampled, int NextHits, double NextRatio, IReadOnlyList<FlowCoverageItem> Items);

/// <summary>
/// "任意方法时序图第一屏能不能看到业务调用"的度量（scripts/flow-metric.py 是同一算法的 HTTP 版，回归脚本用 CLI 的 arch --flow-coverage）：
/// <list type="number">
/// <item>容器的 HTTP 入口按 <see cref="SampleKey"/>（id 的乘法散列，确定性的"随机"）排序，依次取；</item>
/// <item>根 = 入口调用大纲里第一个 dispatch 步骤（含挂在分发调用下的）的目标；没有分派就是入口本身；</item>
/// <item>业务调用 = 根的调用大纲里 kind 为 call / dispatch 且 expandable 的步骤目标，加上各步 children 里 kind 为 impl / dispatch 且 expandable 的目标；
/// 一个都没有的入口跳过（不计入分母），直到凑够 sample 个；</item>
/// <item>命中 = 根的 /api/symbols/{id}/flow 里深度 1..levels 的节点出现至少一个业务调用目标；</item>
/// <item>下一层：根的第一个业务调用目标再当一次根，同样判定（它自己没有业务调用时不计入）——这才是"从任意方法出发"，Handler 本身在入口表里。</item>
/// </list>
/// </summary>
public static class FlowCoverage
{
    public const int DefaultSample = 30;
    public const int DefaultLevels = 2;

    /// <summary>Knuth 乘法散列的低 32 位：Python 里是 (id * 2654435761) &amp; 0xffffffff，两边抽到同一批入口。</summary>
    public static long SampleKey(long id) => unchecked(id * 2654435761L) & 0xffffffffL;

    public static FlowCoverageReport Measure(IndexService s, string containerKey, int sample = DefaultSample, int levels = DefaultLevels)
    {
        var entries = s.Entries(containerKey, "http", null).OrderBy(e => SampleKey(e.SymbolId)).ThenBy(e => e.SymbolId).ToList();
        var items = new List<FlowCoverageItem>();
        var skipped = 0;
        var options = new OutlineOptions();
        foreach (var e in entries)
        {
            if (items.Count >= sample) break;
            var entryOutline = s.Outline(e.SymbolId, options);
            var rootId = entryOutline is null ? e.SymbolId : FirstDispatch(entryOutline.Steps) ?? e.SymbolId;
            var targets = BusinessTargets(rootId == e.SymbolId ? entryOutline : s.Outline(rootId, options));
            if (targets.Count == 0) { skipped++; continue; }
            var seen = Seen(s, rootId, targets, levels);
            long? nextRoot = null;
            bool? nextHit = null;
            var nextTargets = BusinessTargets(s.Outline(targets[0], options));
            if (nextTargets.Count > 0)
            {
                nextRoot = targets[0];
                nextHit = Seen(s, targets[0], nextTargets, levels) > 0;
            }
            items.Add(new FlowCoverageItem(e.SymbolId, e.Route, rootId, targets.Count, seen, seen > 0, nextRoot, nextHit));
        }
        var hits = items.Count(i => i.Hit);
        var next = items.Where(i => i.NextHit is not null).ToList();
        var nextHits = next.Count(i => i.NextHit == true);
        return new FlowCoverageReport(containerKey, entries.Count, items.Count, skipped, hits, levels,
            items.Count == 0 ? 0 : (double)hits / items.Count,
            items.Count == 0 ? 0 : items.Average(i => (double)i.Seen / i.Targets),
            next.Count, nextHits, next.Count == 0 ? 0 : (double)nextHits / next.Count, items);
    }

    private static int Seen(IndexService s, long rootId, IReadOnlyList<long> targets, int levels)
    {
        var flow = s.SymbolFlow(rootId, null);
        var seen = new HashSet<long>();
        if (flow is not null) Walk(flow.Root, n => { if (n.Depth >= 1 && n.Depth <= levels && n.SymbolId is { } id) seen.Add(id); });
        return targets.Count(seen.Contains);
    }

    internal static long? FirstDispatch(IReadOnlyList<OutlineStepDto> steps)
    {
        foreach (var st in steps)
        {
            if (st.Kind == "dispatch" && st.Target is { } t) return t.Id;
            foreach (var c in st.Children)
                if (c.Kind == "dispatch" && c.Target is { } ct) return ct.Id;
        }
        return null;
    }

    /// <summary>业务调用目标，按大纲顺序去重。</summary>
    internal static List<long> BusinessTargets(OutlineDto? outline)
    {
        var list = new List<long>();
        if (outline is null) return list;
        void Add(long id) { if (!list.Contains(id)) list.Add(id); }
        foreach (var st in outline.Steps)
        {
            if (st.Kind is "call" or "dispatch" && st.Expandable && st.Target is { } t) Add(t.Id);
            foreach (var c in st.Children)
                if (c.Kind is "impl" or "dispatch" && c.Expandable && c.Target is { } ct) Add(ct.Id);
        }
        return list;
    }

    private static void Walk(FlowNodeDto n, Action<FlowNodeDto> visit)
    {
        visit(n);
        foreach (var c in n.Children) Walk(c, visit);
    }
}
