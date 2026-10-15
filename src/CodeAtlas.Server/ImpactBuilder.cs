using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <param name="Entries">附上入口汇总（从这个符号往上 BFS）；展开某个调用方时不需要，传 false 省掉整趟遍历。</param>
/// <param name="Tests">列出测试代码里的调用方。false 时它们只计入 TestCallers。被查询的符号本身在测试代码里时不论这个开关都列出。</param>
public sealed record ImpactOptions(bool Entries = true, bool Tests = false);

/// <summary>入边的目标与被查询符号的关系（见 <see cref="ImpactBuilder"/> 第 1 条）。</summary>
public enum ImpactRelation
{
    /// <summary>边直接指向被查询的符号。</summary>
    Self,
    /// <summary>被查询的是构造函数，边指向它所属的类型（new T 的边落在类型上）。</summary>
    ConstructedType,
    /// <summary>边指向被查询方法实现 / 重写的接口方法或基类方法（调用方调的是接口 / 基类，运行时可能落到这里）。</summary>
    BaseMember,
}

/// <summary>反向的一条影响：Caller 经 Kind 影响 Target（被查询或 BFS 走到的符号）。Line / Pos / Ctx 是调用方里那一处调用。</summary>
public readonly record struct ImpactLink(long TargetId, long CallerId, string Kind, double Confidence, int Line, int? Pos, string? Ctx);

/// <summary>
/// 影响范围：反向看谁调了这个符号、一路影响到哪些入口（GET /api/symbols/{id}/impact）。纯函数，数据由 <see cref="ImpactQuery"/> 取。
/// <list type="number">
/// <item>直接调用方（<see cref="Classify"/>）：
/// calls 入边（confidence ≥ 0.4，via 不是 conduit）：via=impl 的是 impl（调用方调的是接口方法、派生到了这里），被查询的是类型 / 构造函数时是 new，其余 call；
/// dispatches 入边里 via 以 dispatch: 开头的是 dispatch（handles 不要：那是"类型处理什么消息"的声明，不是调用）；
/// 被查询的是构造函数时，指向它所属类型的 calls 入边也算 new；
/// 指向被查询方法实现 / 重写的接口方法、基类方法的 calls 入边算 impl，置信度取原边与 0.5 的较小值——
/// 这是"多实现 &gt; 20 没派生 impl 边"、"基类虚方法（派生边只给接口）"时的补充；同一处已有 impl 派生边时两条落在同一个 Site 上，合并后取大。
/// 自己调自己（递归）不算调用方。</item>
/// <item>同一个调用方的多处调用合成一项（Kind 不同时分开），Sites 按位置排，Confidence 取各处最大。</item>
/// <item>测试代码里的调用方（<see cref="PipelineBuilder.IsTest"/>）只计数，tests=true 时才列出。</item>
/// <item>排序：本身是入口的在前，再按置信度降序、Label；超过 <see cref="MaxCallers"/> 截断。</item>
/// <item>入口汇总（<see cref="Reach"/>）：沿同样的规则往上 BFS，到达的入口取最小深度。
/// Confidence &lt; 0.5 的边不走：0.3 档的多候选（同名方法猜不准是哪个）一旦参与，几层之后就会把影响面放大到全仓库，
/// 结果是"什么都影响"，等于没有信息；0.5 档（多实现、接口 / 基类补充）保留，宁可多报几个入口也不漏。
/// 测试代码不走。跨容器的 route_matches（前端 fetch → 后端控制器）也反向走：被对上的入口往上接到发起调用的那个方法。
/// 深度 <see cref="MaxDepth"/>、节点 <see cref="MaxNodes"/> 封顶，超了 EntriesTruncated。</item>
/// </list>
/// </summary>
public static class ImpactBuilder
{
    public const int MaxCallers = 300;
    /// <summary>跨服务调用方：别的容器里发起 HTTP / gRPC 调用、经 route_matches 对上这个入口的方法。</summary>
    public const string HopKind = "hop";
    public const int MaxDepth = 15;
    public const int MaxNodes = 5000;
    /// <summary>直接调用方列表收的最低置信度（与调用大纲一致）。</summary>
    public const double ListThreshold = 0.4;
    /// <summary>入口汇总往上走的最低置信度。</summary>
    public const double WalkThreshold = 0.5;
    /// <summary>经接口 / 基类方法补充进来的调用方的置信度上限。</summary>
    public const double BaseMemberCap = 0.5;

    /// <summary>
    /// 入边 → 影响链接。
    /// </summary>
    /// <param name="edges">指向 <paramref name="relations"/> 里各个目标的入边（可以多给，不相干的会被过滤掉）。</param>
    /// <param name="relations">边的目标 id → (被查询的符号, 关系)；同一个目标可能对应多个被查询的符号（两个实现共享一个接口方法）。</param>
    /// <param name="constructs">被查询的符号里是类型或构造函数的（它们的直接 calls 入边算 new）。</param>
    /// <param name="minConfidence">最低置信度（经接口 / 基类补充的先封顶再比较）。</param>
    public static List<ImpactLink> Classify(
        IReadOnlyList<EdgeRecord> edges, ILookup<long, (long Origin, ImpactRelation Relation)> relations, IReadOnlySet<long> constructs, double minConfidence = ListThreshold)
    {
        var links = new List<ImpactLink>();
        foreach (var e in edges)
        {
            if (e.ToId is not { } to) continue;
            foreach (var (origin, relation) in relations[to])
            {
                if (e.FromId == origin) continue; // 递归
                string kind;
                var confidence = e.Confidence;
                if (e.Kind == EdgeKind.Dispatches)
                {
                    // 分派只认直接落到这里的；落到基类 / 所属类型的分派不推（Roslyn 的基类分派已经按子类型发了边）
                    if (relation != ImpactRelation.Self || e.Via is not { } dv || !dv.StartsWith("dispatch:", StringComparison.Ordinal)) continue;
                    kind = "dispatch";
                }
                else if (e.Kind == EdgeKind.Calls)
                {
                    if (e.Via == "conduit") continue;
                    switch (relation)
                    {
                        case ImpactRelation.Self:
                            kind = e.Via == Core.Storage.ImplementationLinker.Via ? "impl" : constructs.Contains(origin) ? "new" : "call";
                            break;
                        case ImpactRelation.ConstructedType:
                            kind = "new";
                            break;
                        default:
                            // 同一处已有派生到这里的 impl 边时，两条落在同一个 Site 上，合并时置信度取大（派生边的 0.9 更准），不用另外去重
                            kind = "impl";
                            confidence = Math.Min(confidence, BaseMemberCap);
                            break;
                    }
                }
                else continue;
                if (confidence < minConfidence) continue;
                links.Add(new ImpactLink(origin, e.FromId, kind, confidence, e.Line, e.Pos, e.Ctx));
            }
        }
        return links;
    }

    /// <summary>
    /// 直接调用方列表（第 2–4 条）。
    /// </summary>
    /// <param name="links">被查询符号的影响链接（TargetId 不是它的忽略）。</param>
    /// <param name="symbols">调用方与它们的父符号（拼"类型.方法"用）。</param>
    /// <param name="paths">文件 id → 路径。</param>
    /// <param name="expandable">自己还有调用方的调用方 id。</param>
    /// <param name="entriesOf">符号 id → 它作为入口的信息（Depth 已填 1）；不是入口返回空。</param>
    public static (List<ImpactCallerDto> Callers, int TestCallers, bool Truncated) Callers(
        SymbolDto root, IReadOnlyList<ImpactLink> links, IReadOnlyDictionary<long, SymbolRecord> symbols, IReadOnlyDictionary<long, string> paths,
        IReadOnlySet<long> expandable, Func<long, IReadOnlyList<ImpactEntryDto>> entriesOf, ImpactOptions options, int maxCallers = MaxCallers)
    {
        var keepTests = options.Tests || PipelineBuilder.IsTest(root.Path, root.QualifiedName);
        string PathOf(SymbolRecord s) => paths.GetValueOrDefault(s.FileId, Core.Storage.IndexWriter.ExternalFilePath);
        var testCallers = new HashSet<long>();
        var callers = new List<ImpactCallerDto>();
        foreach (var g in links.Where(l => l.TargetId == root.Id).GroupBy(l => (l.CallerId, l.Kind)))
        {
            if (!symbols.TryGetValue(g.Key.CallerId, out var caller)) continue;
            if (PipelineBuilder.IsTest(PathOf(caller), caller.QualifiedName))
            {
                testCallers.Add(caller.Id);
                if (!keepTests) continue;
            }
            var sites = SortSites(g.Select(l => new ImpactSiteDto(l.Line, l.Pos, l.Ctx)).Distinct());
            callers.Add(new ImpactCallerDto(g.Key.Kind, OutlineBuilder.SymbolLabel(caller, symbols, paths), ToDto(caller, PathOf(caller)), sites,
                g.Max(l => l.Confidence), expandable.Contains(caller.Id), entriesOf(caller.Id)));
        }
        var ordered = callers
            .OrderBy(c => c.AsEntry.Count > 0 ? 0 : 1)
            .ThenByDescending(c => c.Confidence)
            .ThenBy(c => c.Label, StringComparer.Ordinal)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Caller.Id)
            .ToList();
        var truncated = ordered.Count > maxCallers;
        if (truncated) ordered = ordered.Take(maxCallers).ToList();
        return (ordered, testCallers.Count, truncated);
    }

    /// <summary>都有 pos 时按 pos；否则按行、同一行内 pos（没有 pos 的排在该行后面）。与调用大纲的排序一致。</summary>
    internal static List<ImpactSiteDto> SortSites(IEnumerable<ImpactSiteDto> sites)
    {
        var list = sites.ToList();
        if (list.All(s => s.Pos is not null)) return list.OrderBy(s => s.Pos!.Value).ThenBy(s => s.Line).ToList();
        return list.OrderBy(s => s.Line).ThenBy(s => s.Pos ?? int.MaxValue).ToList();
    }

    /// <summary>
    /// 第 5 条：从 root 往上逐层 BFS。<paramref name="predecessors"/> 一次拿一整层的上一层（已按走边规则过滤：置信度、测试代码、route_matches 续接），
    /// 返回每个走到的符号的最小深度（root = 0）。超过 <paramref name="maxDepth"/> 层或 <paramref name="maxNodes"/> 个节点时停下，Truncated = true。
    /// 已访问的不再入队，环不会死循环。
    /// </summary>
    public static (Dictionary<long, int> Depths, bool Truncated) Reach(
        long rootId, Func<IReadOnlyCollection<long>, IEnumerable<long>> predecessors, int maxDepth = MaxDepth, int maxNodes = MaxNodes)
    {
        var depths = new Dictionary<long, int> { [rootId] = 0 };
        var frontier = new List<long> { rootId };
        var truncated = false;
        for (var depth = 0; frontier.Count > 0 && !truncated; depth++)
        {
            var next = new List<long>();
            foreach (var p in predecessors(frontier).Distinct().Order())
            {
                if (depths.ContainsKey(p)) continue;
                // 还有没走到的上一层，但已到深度 / 节点上限：入口可能不全
                if (depth + 1 > maxDepth || depths.Count >= maxNodes) { truncated = true; break; }
                depths[p] = depth + 1;
                next.Add(p);
            }
            frontier = next;
        }
        return (depths, truncated);
    }

    /// <summary>入口汇总的排序：深度、kind、路由、容器名、入口名。</summary>
    public static List<ImpactEntryDto> SortEntries(IEnumerable<ImpactEntryDto> entries) => entries
        .OrderBy(e => e.Depth)
        .ThenBy(e => e.Entry.Kind, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.Route ?? "", StringComparer.Ordinal)
        .ThenBy(e => e.ContainerName, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.Name, StringComparer.Ordinal)
        .ThenBy(e => e.Entry.SymbolId)
        .ToList();

    internal static SymbolDto ToDto(SymbolRecord s, string path) => new(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, s.Signature, path,
        s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Visibility);
}
