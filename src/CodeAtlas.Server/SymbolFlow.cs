using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 从任意符号出发的时序树（GET /api/symbols/{id}/flow、CLI query --flow）：复用 <see cref="FlowBuilder"/>，只是根不必在入口表里。
/// <list type="bullet">
/// <item>根本身是入口：直接用它的入口信息，结果与 /api/arch/flow 相同。</item>
/// <item>方法 / 函数 / 模块（以及有出边的属性、字段、变量——属性初始化器、箭头函数）：临时构造入口：容器 = 成员目录最长匹配的那个容器，
/// kind = method，route / httpMethod 为空，file / line 取符号的。</item>
/// <item>不在任何容器里：容器键为空串。给了 <c>repoContainer</c>（整库伪容器，成员 = 仓库里的各个项目目录）时把它当作键为空的"本容器"，
/// 参与者按项目分列；没给时 home 为空，仓库内符号归"（未归属）"一列。两种情况下 hop 与出口都不画：RouteMatch / Outbound 按调用处所在容器的键登记，
/// 空键对不上任何一条（FlowBuilder 里 HopsIn / UnmatchedIn / GrpcExitsIn 都按 home?.Key ?? entry.ContainerKey 过滤）。</item>
/// <item>类型：换成它的构造函数（有多个时取出边最多的那个，同样多取靠前的）；没有带调用的构造函数时返回只有根的流程，Note 说明。</item>
/// <item>其余（命名空间、接口、枚举、外部符号、没有出边的字段……）：只有根的流程，Note 说明为什么没有可展开的调用。
/// 不返回 400：面板把"调用图"默认成时序图，用户在类或字段上打开时一行说明比一个错误更好处理。</item>
/// </list>
/// </summary>
public static class SymbolFlow
{
    public const string RuleId = "symbol";
    public const string EntryKind = "method";

    public static FlowDto Build(
        IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, MapSymbol symbol, IReadOnlyList<ArchNode> containers,
        IReadOnlyList<RouteMatch> matches, IReadOnlyList<EntryPoint> allEntries, int maxDepth = FlowBuilder.DefaultMaxDepth,
        IReadOnlyList<Outbound>? outbound = null, IReadOnlyList<ArchNode>? externalNodes = null, Func<ArchNode?>? repoContainer = null)
    {
        FlowDto Run(EntryPoint entry, IReadOnlyList<ArchNode> list, int depth) =>
            FlowBuilder.Build(symbols, edges, entry, list, matches, allEntries, depth, outbound: outbound, externalNodes: externalNodes);

        if (allEntries.FirstOrDefault(e => e.SymbolId == symbol.Id) is { } asEntry) return Run(asEntry, containers, maxDepth);

        var root = symbol;
        string? note = null;
        var hasOut = edges.Where(e => e.FromId == symbol.Id).Any(FlowBuilder.IsFlowEdge);
        if (IsType(symbol.Kind))
        {
            var ctors = symbols.Where(c => c.ParentId == symbol.Id && c.Kind is SymbolKind.Method or SymbolKind.Function && IsConstructorName(c, symbol)).ToList();
            var ids = ctors.Select(c => c.Id).ToHashSet();
            var outCount = edges.Where(e => ids.Contains(e.FromId) && FlowBuilder.IsFlowEdge(e)).GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.Count());
            var best = ctors.Where(c => outCount.ContainsKey(c.Id)).OrderByDescending(c => outCount[c.Id]).ThenBy(c => c.StartLine).FirstOrDefault();
            if (best is null)
                return Leaf(symbol, ctors.Count == 0
                    ? $"{symbol.Name} 是类型，没有显式构造函数，没有可展开的调用；在它的方法上打开时序图"
                    : $"{symbol.Name} 的构造函数里没有调用；在它的方法上打开时序图");
            if (allEntries.FirstOrDefault(e => e.SymbolId == best.Id) is { } ctorEntry) return Run(ctorEntry, containers, maxDepth) with { Note = $"从 {symbol.Name} 的构造函数展开" };
            root = best;
            note = ctors.Count > 1 ? $"{symbol.Name} 有 {ctors.Count} 个构造函数，展开的是调用最多的那个（第 {best.StartLine} 行）" : $"从 {symbol.Name} 的构造函数展开";
        }
        else if (!(symbol.Kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Module
                   || (symbol.Kind is SymbolKind.Property or SymbolKind.Field or SymbolKind.Variable && hasOut)))
        {
            return Leaf(symbol, symbol.Kind switch
            {
                SymbolKind.External => $"{symbol.Name} 是框架 / 第三方符号，索引里没有它的实现",
                SymbolKind.Property or SymbolKind.Field or SymbolKind.Variable => $"{symbol.Name} 是{KindText(symbol.Kind)}，没有调用可展开",
                _ => $"{symbol.Name} 是{KindText(symbol.Kind)}，不是可执行的方法；在方法上打开时序图",
            });
        }

        var (entry, list) = Synthesize(root, containers, repoContainer);
        var flow = Run(entry, list, maxDepth);
        // 标题用"类型.方法"（与根节点同一个标签）：一屏好几个 Handle 时光看方法名分不清
        return flow with { EntryLabel = flow.Root.Label, Note = flow.Note ?? note };

        FlowDto Leaf(MapSymbol s, string why)
        {
            var (e, l) = Synthesize(s, containers, repoContainer);
            var f = FlowBuilder.Build(symbols, edges, e, l, matches, allEntries, maxDepth: 0, outbound: outbound, externalNodes: externalNodes);
            return f with { EntryLabel = f.Root.Label, Note = why };
        }
    }

    /// <summary>
    /// 临时入口：容器取成员目录最长匹配的那个（多个容器都收了同一个类库时，取收得最"近"的）；都不在时键为空串，
    /// 有整库伪容器就把它以空键加进容器表，充当 home。
    /// </summary>
    internal static (EntryPoint Entry, IReadOnlyList<ArchNode> Containers) Synthesize(MapSymbol s, IReadOnlyList<ArchNode> containers, Func<ArchNode?>? repoContainer)
    {
        var home = ContainerOf(s.Path, containers);
        IReadOnlyList<ArchNode> list = containers;
        if (home is null && repoContainer?.Invoke() is { } repo)
        {
            home = repo with { Key = "" };
            list = [.. containers, home];
        }
        return (new EntryPoint(s.Id, home?.Key ?? "", RuleId, EntryKind, null, null, s.Path, s.StartLine), list);
    }

    /// <summary>包含这个文件的容器：成员目录最长匹配；同样长时按容器表顺序取第一个。判据与 FlowBuilder / RuleContext.Under 相同。</summary>
    public static ArchNode? ContainerOf(string path, IReadOnlyList<ArchNode> containers)
    {
        ArchNode? best = null;
        var bestLen = -1;
        foreach (var c in containers)
        {
            if (c.Kind != ArchNodeKind.Container) continue;
            foreach (var m in c.Members)
                if (m.Length > bestLen && Core.Architecture.Rules.RuleContext.Under(path, m)) { best = c; bestLen = m.Length; }
        }
        return best;
    }

    private static bool IsType(SymbolKind k) => k is SymbolKind.Class or SymbolKind.Struct;

    private static bool IsConstructorName(MapSymbol method, MapSymbol owner) =>
        method.Name is "constructor" or "__init__" || MapBuilder.StripGenericArgs(method.Name) == MapBuilder.StripGenericArgs(owner.Name);

    private static string KindText(SymbolKind k) => k switch
    {
        SymbolKind.Namespace => "命名空间", SymbolKind.Interface => "接口", SymbolKind.Enum => "枚举", SymbolKind.TypeAlias => "类型别名",
        SymbolKind.Property => "属性", SymbolKind.Field => "字段", SymbolKind.Variable => "变量", _ => DbNames.ToDb(k),
    };
}
