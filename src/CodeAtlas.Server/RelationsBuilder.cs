using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <param name="Depth">1：中心与直接相连的单元；2：再对 depth 1 的单元各做一次（只连已知节点，不展开 depth 2 节点自己的邻居）。</param>
/// <param name="Tests">保留测试代码里的单元。中心本身在测试代码里时不论这个开关都保留（看测试类依赖了谁）。</param>
public sealed record RelationsOptions(int Depth = 1, bool Tests = false);

/// <summary>成员层面的一条边（RelationsQuery 取来，RelationsBuilder 汇总到单元之间）。File 是边所在文件的相对路径。</summary>
public sealed record RelationRawEdge(long Id, long FromId, long ToId, EdgeKind Kind, double Confidence, string? Via, string File, int Line, int? Pos);

/// <summary>一个成员（方法 / 属性 / 字段 / 单元自身……）。</summary>
/// <param name="Unit">所属单元的符号 id。</param>
/// <param name="Label">显示名：类型.方法；单元自身与构造函数是类型名；模块里的顶层函数是函数名。</param>
/// <param name="Constructs">调到它算"构造"（new）：它是类 / 结构本身或构造函数。</param>
public sealed record RelationMember(long Id, long Unit, string Label, bool Constructs);

/// <param name="Label">单元的短名（类型名 / 模块文件名 / 外部类型名）。</param>
/// <param name="Test">测试代码里的单元（PipelineBuilder.IsTest）。外部单元恒为 false。</param>
public sealed record RelationUnit(SymbolDto Symbol, string Label, bool External, bool Test);

/// <summary>单元所属的组件。</summary>
public sealed record RelationComponentRef(string Key, string Label, string? ContainerKey);

/// <summary>算组件用的单元位置。Namespace：C# 单元所在的命名空间（沿父链找到的第一个 namespace 符号），其余为 null。</summary>
public sealed record RelationUnitPlace(long Id, string Path, string QualifiedName, string? Namespace);

/// <summary>
/// 模块关系（GET /api/symbols/{id}/relations）：以一个"单元"（类 / 接口 / 结构 / 枚举；没有类型的顶层函数用所在文件的模块）为中心，
/// 把成员之间的边汇总成单元之间的依赖，按组件分框、组件按依赖序排。纯函数，数据由 <see cref="RelationsQuery"/> 取。
/// <list type="number">
/// <item>边：calls（≥ 0.4，含 via=impl / conduit / render；落到类 / 结构或构造函数的算 new，其余算 call）、dispatches（via = dispatch:*）、
/// references、inherits、implements，置信度都要 ≥ 0.4（0.3 档多候选只是"可能"）。两端映射到单元，同单元内部的不要。</item>
/// <item>外部符号只保留 inherits / implements 的目标（ControllerBase、IRequestHandler 这类），外部调用不画。</item>
/// <item>按（From 单元，To 单元）汇总：Kinds 计数；Weight = call / new / dispatch 各 1，inherits / implements 各 5，reference 各 0.5；
/// Samples 取置信度最高的 <see cref="MaxSamples"/> 条。</item>
/// <item>节点上限 <see cref="MaxNodes"/>：中心必留；depth 1 按与中心之间（两个方向）的 Weight 之和排序先留；depth 2 的候选按与已留的 depth 1 节点之间的 Weight 之和
/// 填剩下的位置。留不下的丢掉并置 Truncated。</item>
/// <item>组件：见 <see cref="AssignComponents"/>；外部单元放组件 "external"。组件顺序按这张子图里组件之间的编译期依赖（call 里去掉 via=impl 的派生边、不算 dispatch，
/// 与 DSM 同口径）做拓扑序，被依赖的在前，环内按键名；编译期依赖定不了先后的，再用运行时去向（impl / dispatch）补依赖，只要不和已有依赖成环；外部放最后。</item>
/// <item>同一成员同一行已经有到同一单元的调用（new X() / X.Static()）时，那一行对 X 的类型引用不再算；继承 / 实现只算类型对类型（方法实现接口方法的边是重复）。</item>
/// </list>
/// </summary>
public static class RelationsBuilder
{
    public const int MaxNodes = 40;
    public const int MaxSamples = 5;
    public const string ExternalComponentKey = "external";
    public const string ExternalComponentLabel = "外部";
    public const double MinConfidence = 0.4;

    /// <summary>Kinds 的固定顺序（也是同置信度样例的先后）。</summary>
    internal static readonly string[] KindOrder = ["call", "new", "dispatch", "inherits", "implements", "reference"];

    internal sealed class Agg
    {
        public readonly Dictionary<string, int> Kinds = new(StringComparer.Ordinal);
        public double Weight;
        /// <summary>编译期依赖的条数（排组件顺序用）：不含 via=impl 的派生调用与 dispatch。</summary>
        public int Compile;
        /// <summary>运行时去向的条数（via=impl 的派生调用、dispatch）：只在不与编译期依赖成环时用来给组件定先后。</summary>
        public int Soft;
        public readonly List<RelationSampleDto> Samples = [];
    }

    /// <summary>一条边算哪种关系；不算的返回 null。target 是终点成员（判断 new）。</summary>
    public static string? Classify(RelationRawEdge e, RelationMember target)
    {
        if (e.Confidence < MinConfidence) return null;
        return e.Kind switch
        {
            EdgeKind.Calls => target.Constructs ? "new" : "call",
            EdgeKind.Dispatches => e.Via is { } v && v.StartsWith("dispatch:", StringComparison.Ordinal) ? "dispatch" : null,
            EdgeKind.References => "reference",
            EdgeKind.Inherits => "inherits",
            EdgeKind.Implements => "implements",
            _ => null,
        };
    }

    public static double KindWeight(string kind) => kind switch
    {
        "inherits" or "implements" => 5,
        "reference" => 0.5,
        _ => 1,
    };

    /// <summary>成员边 → 单元之间的汇总（同一条边 id 只算一次）。</summary>
    internal static Dictionary<(long From, long To), Agg> Aggregate(
        IEnumerable<RelationRawEdge> edges, IReadOnlyDictionary<long, RelationMember> members, IReadOnlyDictionary<long, RelationUnit> units, bool keepTests)
    {
        var agg = new Dictionary<(long, long), Agg>();
        var seen = new HashSet<long>();
        var unique = edges.Where(e => seen.Add(e.Id)).ToList();
        // new X(…) / X.Static(…) 在同一行还会带一条对 X 的类型引用：同一成员、同一行已经有到同一单元的调用时不再算引用
        var callSites = new HashSet<(long From, long ToUnit, string File, int Line)>();
        foreach (var e in unique)
            if (e.Kind == EdgeKind.Calls && members.TryGetValue(e.ToId, out var callee)) callSites.Add((e.FromId, callee.Unit, e.File, e.Line));
        foreach (var e in unique)
        {
            if (!members.TryGetValue(e.FromId, out var from) || !members.TryGetValue(e.ToId, out var to) || from.Unit == to.Unit) continue;
            if (!units.TryGetValue(from.Unit, out var fu) || !units.TryGetValue(to.Unit, out var tu)) continue;
            if (Classify(e, to) is not { } kind) continue;
            // 继承 / 实现只算类型对类型；方法对方法的（Handle 实现 IRequestHandler.Handle）是同一件事的重复
            if (kind is "inherits" or "implements" && (from.Id != from.Unit || to.Id != to.Unit)) continue;
            if (kind == "reference" && callSites.Contains((e.FromId, to.Unit, e.File, e.Line))) continue;
            if (fu.External) continue; // 外部符号没有出边；保险
            if (tu.External && kind is not ("inherits" or "implements")) continue;
            if (!keepTests && (fu.Test || tu.Test)) continue;
            if (!agg.TryGetValue((from.Unit, to.Unit), out var a)) agg[(from.Unit, to.Unit)] = a = new Agg();
            a.Kinds[kind] = a.Kinds.GetValueOrDefault(kind) + 1;
            a.Weight += KindWeight(kind);
            if (kind != "dispatch" && e.Via != "impl") a.Compile++;
            else a.Soft++;
            a.Samples.Add(new RelationSampleDto(from.Label, to.Label, kind, e.File, e.Line, e.Pos, e.Confidence));
        }
        return agg;
    }

    /// <summary>与中心直接相连的单元，按两个方向的 Weight 之和降序（同分按短名、id）。depth 2 时 RelationsQuery 只展开其中留得下的前 MaxNodes - 1 个。</summary>
    public static List<long> FirstRing(long focus, IEnumerable<RelationRawEdge> edges, IReadOnlyDictionary<long, RelationMember> members,
        IReadOnlyDictionary<long, RelationUnit> units, bool keepTests) =>
        Ring(focus, Aggregate(edges, members, units, keepTests || units[focus].Test), units);

    private static List<long> Ring(long focus, Dictionary<(long From, long To), Agg> agg, IReadOnlyDictionary<long, RelationUnit> units)
    {
        var weight = new Dictionary<long, double>();
        foreach (var ((from, to), a) in agg)
        {
            if (from == focus) weight[to] = weight.GetValueOrDefault(to) + a.Weight;
            else if (to == focus) weight[from] = weight.GetValueOrDefault(from) + a.Weight;
        }
        return Ranked(weight, units);
    }

    private static List<long> Ranked(Dictionary<long, double> weight, IReadOnlyDictionary<long, RelationUnit> units) =>
        weight.OrderByDescending(p => p.Value).ThenBy(p => units[p.Key].Label, StringComparer.Ordinal).ThenBy(p => p.Key).Select(p => p.Key).ToList();

    /// <param name="focus">中心单元的符号 id（units 里必须有）。</param>
    /// <param name="edges">中心各成员的出入边；depth 2 时再加上被展开的 depth 1 单元各成员的出入边。</param>
    /// <param name="members">边两端的成员（含单元自身）。</param>
    /// <param name="componentsOf">给留下的仓库内单元（不含外部）算组件；一般是 <see cref="AssignComponents"/> 包一层取数。</param>
    public static RelationsDto Build(
        long focus, IReadOnlyList<RelationRawEdge> edges, IReadOnlyDictionary<long, RelationMember> members, IReadOnlyDictionary<long, RelationUnit> units,
        RelationsOptions options, Func<IReadOnlyList<long>, IReadOnlyDictionary<long, RelationComponentRef>> componentsOf, int maxNodes = MaxNodes)
    {
        var keepTests = options.Tests || units[focus].Test;
        var agg = Aggregate(edges, members, units, keepTests);

        // ---- 选节点 ----
        var distance = new Dictionary<long, int> { [focus] = 0 };
        var rankWeight = new Dictionary<long, double>();
        var ring = Ring(focus, agg, units);
        var truncated = ring.Count > maxNodes - 1;
        var firstRing = ring.Take(maxNodes - 1).ToHashSet();
        foreach (var u in firstRing) distance[u] = 1;
        foreach (var ((from, to), a) in agg)
        {
            if (from == focus && firstRing.Contains(to)) rankWeight[to] = rankWeight.GetValueOrDefault(to) + a.Weight;
            else if (to == focus && firstRing.Contains(from)) rankWeight[from] = rankWeight.GetValueOrDefault(from) + a.Weight;
        }

        var allRing = ring.ToHashSet();
        if (options.Depth >= 2)
        {
            // depth 2 候选：与已留的 depth 1 节点相连、又不是中心或 depth 1（含被截掉的 depth 1——它们其实和中心直接相连，不能冒充第二层）
            var second = new Dictionary<long, double>();
            foreach (var ((from, to), a) in agg)
            {
                if (firstRing.Contains(from) && to != focus && !allRing.Contains(to)) second[to] = second.GetValueOrDefault(to) + a.Weight;
                else if (firstRing.Contains(to) && from != focus && !allRing.Contains(from)) second[from] = second.GetValueOrDefault(from) + a.Weight;
            }
            var ranked = Ranked(second, units);
            var slots = Math.Max(0, maxNodes - distance.Count);
            if (ranked.Count > slots) truncated = true;
            foreach (var u in ranked.Take(slots)) { distance[u] = 2; rankWeight[u] = second[u]; }
        }

        // ---- 边：两端都留下；depth 1 只要碰到中心的，depth 2 再加碰到 depth 1 节点的（depth 2 节点彼此之间、与更外层的不取） ----
        bool Expanded(long u) => u == focus || (options.Depth >= 2 && firstRing.Contains(u));
        var keptEdges = agg.Where(p => distance.ContainsKey(p.Key.From) && distance.ContainsKey(p.Key.To) && (Expanded(p.Key.From) || Expanded(p.Key.To)))
            .ToList();

        // ---- 组件 ----
        var inRepo = distance.Keys.Where(u => !units[u].External).OrderBy(u => u).ToList();
        var comps = componentsOf(inRepo);
        string CompOf(long u) => units[u].External ? ExternalComponentKey : comps[u].Key;
        var compKeys = inRepo.Select(CompOf).Distinct(StringComparer.Ordinal).ToList();
        var deps = compKeys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var ((from, to), a) in keptEdges)
        {
            if (a.Compile == 0 || units[from].External || units[to].External) continue;
            var (cf, ct) = (CompOf(from), CompOf(to));
            if (cf != ct) deps[cf].Add(ct);
        }
        // 运行时去向（接口调用落到的实现、分派到的 Handler）：编译期依赖定不了先后的组件，按它补一条依赖——只要不和已有的成环。
        // 例：Handler（API）调 IOrderRepository（Domain），实现在 Infrastructure；编译期 API 只依赖 Domain，补上 API → Infrastructure 后 API 排在最后
        var soft = new Dictionary<(string, string), int>();
        foreach (var ((from, to), a) in keptEdges)
        {
            if (a.Soft == 0 || units[from].External || units[to].External) continue;
            var (cf, ct) = (CompOf(from), CompOf(to));
            if (cf != ct && !deps[cf].Contains(ct)) soft[(cf, ct)] = soft.GetValueOrDefault((cf, ct)) + a.Soft;
        }
        foreach (var ((cf, ct), _) in soft.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2, StringComparer.Ordinal))
            if (!Reaches(deps, ct, cf)) deps[cf].Add(ct);
        var (ordered, _, _) = DsmBuilder.DependencyOrder(compKeys, deps, _ => 0); // 同时可放的、环内的都按键名
        var components = new List<RelationComponentDto>();
        foreach (var k in ordered)
        {
            var r = comps.Values.First(c => c.Key == k);
            components.Add(new RelationComponentDto(k, r.Label, components.Count, r.ContainerKey));
        }
        if (distance.Keys.Any(u => units[u].External))
            components.Add(new RelationComponentDto(ExternalComponentKey, ExternalComponentLabel, components.Count, null));

        var nodes = distance.Keys
            .OrderBy(u => distance[u]).ThenByDescending(u => rankWeight.GetValueOrDefault(u)).ThenBy(u => units[u].Label, StringComparer.Ordinal).ThenBy(u => u)
            .Select(u => new RelationNodeDto(units[u].Symbol, CompOf(u), distance[u], units[u].External)).ToList();

        var edgeDtos = keptEdges
            .Select(p => new RelationEdgeDto(p.Key.From, p.Key.To, OrderedKinds(p.Value.Kinds), p.Value.Weight, TopSamples(p.Value.Samples)))
            .OrderByDescending(e => e.Weight).ThenBy(e => units[e.From].Label, StringComparer.Ordinal).ThenBy(e => units[e.To].Label, StringComparer.Ordinal)
            .ThenBy(e => e.From).ThenBy(e => e.To).ToList();

        return new RelationsDto(units[focus].Symbol, components, nodes, edgeDtos, truncated);
    }

    private static bool Reaches(Dictionary<string, HashSet<string>> deps, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == to) return true;
            foreach (var n in deps[cur]) if (seen.Add(n)) stack.Push(n);
        }
        return false;
    }

    private static Dictionary<string, int> OrderedKinds(Dictionary<string, int> kinds)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var k in KindOrder) if (kinds.GetValueOrDefault(k) > 0) result[k] = kinds[k];
        return result;
    }

    /// <summary>置信度降序，同分按 Kind 的固定顺序、文件、行、偏移；同一处同一对成员的重复只留一条。</summary>
    private static List<RelationSampleDto> TopSamples(List<RelationSampleDto> samples) =>
        samples.DistinctBy(s => (s.FromLabel, s.ToLabel, s.Kind, s.File, s.Line, s.Pos))
            .OrderByDescending(s => s.Confidence).ThenBy(s => Array.IndexOf(KindOrder, s.Kind))
            .ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Pos ?? -1)
            .Take(MaxSamples).ToList();

    // ---- 组件 ----

    /// <summary>
    /// 单元 → 组件，与 DSM 容器层同一套键：
    /// <list type="number">
    /// <item>落在某个容器成员目录下（取最长的那个成员）：C# / Java / 前端的组件就是成员项目目录；Go / Python 的成员（多数文件是 .go / .py，<paramref name="isPackageMember"/>）
    /// 按 DSM 的做法摊成包目录（成员目录本身 + 它下面的顶层子目录）。ContainerKey 优先取中心所在的容器，其次按键名第一个包含该成员的容器。</item>
    /// <item>不在任何成员下（测试项目、没有可部署容器的纯类库仓库）：C# 取路径上最深的、名字是命名空间前缀的目录（tests/Ordering.UnitTests 这类项目目录），
    /// 找不到用文件所在目录；其余语言用文件所在目录。ContainerKey 为 null。</item>
    /// <item>仓库内的单元都落进同一个成员组件（单体项目）时往下钻一层，和 DSM 钻进项目后一样：C# 按主命名空间（<paramref name="namespaceBase"/>）下的下一段命名空间分，
    /// 前端 / 脚本按成员目录下的子目录分；钻下去仍只有一个组件就不钻。</item>
    /// </list>
    /// </summary>
    public static Dictionary<long, RelationComponentRef> AssignComponents(
        IReadOnlyList<RelationUnitPlace> units, long focus, IReadOnlyList<ArchNode> containers, Func<string, bool> isPackageMember, Func<string, string> namespaceBase)
    {
        var members = containers.SelectMany(c => c.Members).Distinct(StringComparer.Ordinal).OrderByDescending(m => m.Length).ThenBy(m => m, StringComparer.Ordinal).ToList();
        string? MemberOf(string path) => members.FirstOrDefault(m => Core.Architecture.Rules.RuleContext.Under(path, m));
        List<ArchNode> ContainersOf(string member) => containers.Where(c => c.Members.Contains(member)).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();

        var focusPlace = units.FirstOrDefault(u => u.Id == focus);
        var focusContainers = focusPlace is not null && MemberOf(focusPlace.Path) is { } fm ? ContainersOf(fm).Select(c => c.Key).ToHashSet(StringComparer.Ordinal) : [];
        ArchNode ContainerFor(string member)
        {
            var list = ContainersOf(member);
            return list.FirstOrDefault(c => focusContainers.Contains(c.Key)) ?? list[0];
        }

        var packageMember = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsPackage(string m) => packageMember.TryGetValue(m, out var p) ? p : packageMember[m] = isPackageMember(m);

        var result = new Dictionary<long, RelationComponentRef>();
        var memberOfUnit = new Dictionary<long, string>();
        foreach (var u in units)
        {
            if (MemberOf(u.Path) is not { } m)
            {
                var key = FallbackKey(u);
                result[u.Id] = new RelationComponentRef(key, key.Length == 0 ? "（根目录）" : MapBuilder.KeyLabel(key), null);
                continue;
            }
            var container = ContainerFor(m);
            memberOfUnit[u.Id] = m;
            if (IsPackage(m))
            {
                var dir = MapBuilder.DirectoryOf(u.Path);
                var comp = dir == m ? m : MapBuilder.ChildKey(m, dir) ?? m;
                result[u.Id] = new RelationComponentRef(comp, comp.Length == 0 ? container.Name : MapBuilder.KeyLabel(comp), container.Key);
            }
            else result[u.Id] = new RelationComponentRef(m, m.Length == 0 ? container.Name : MapBuilder.KeyLabel(m), container.Key);
        }

        // 单体项目：都在同一个（非包语言的）成员组件里 → 钻一层
        if (units.Count >= 2 && result.Values.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == 1
            && memberOfUnit.Count == units.Count && memberOfUnit.Values.Distinct(StringComparer.Ordinal).Count() == 1)
        {
            var m = memberOfUnit.Values.First();
            if (!IsPackage(m))
            {
                var containerKey = result.Values.First().ContainerKey;
                var drilled = new Dictionary<long, RelationComponentRef>();
                var basis = units.Any(u => u.Namespace is not null) ? namespaceBase(m) : m;
                foreach (var u in units)
                {
                    if (u.Namespace is { } ns)
                    {
                        string comp; string label;
                        if (ns == basis) { comp = basis; label = DsmBuilder.SelfLabel; }
                        else if (MapBuilder.ChildKey(basis, ns) is { } child) { comp = child; label = MapBuilder.KeyLabel(child); }
                        else
                        {
                            // 漏写前缀的命名空间（或全局命名空间）：和 DSM 一样单独成组件、标 ⚠
                            comp = MapBuilder.ChildKey("", ns) ?? ns;
                            label = comp.Length == 0 ? DsmBuilder.GlobalLabel : DsmBuilder.StrayPrefix + comp;
                        }
                        drilled[u.Id] = new RelationComponentRef(comp, label, containerKey);
                    }
                    else
                    {
                        var dir = MapBuilder.DirectoryOf(u.Path);
                        var comp = dir == m ? m : MapBuilder.ChildKey(m, dir) ?? m;
                        drilled[u.Id] = new RelationComponentRef(comp, comp == m ? DsmBuilder.SelfLabel : MapBuilder.KeyLabel(comp), containerKey);
                    }
                }
                if (drilled.Values.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() > 1) return drilled;
            }
        }
        return result;
    }

    /// <summary>不在容器成员下的单元：C# 取路径上最深的、名字是命名空间前缀的目录（项目目录通常以根命名空间命名），否则文件所在目录。</summary>
    public static string FallbackKey(RelationUnitPlace u)
    {
        var dir = MapBuilder.DirectoryOf(u.Path);
        if (u.Namespace is not { Length: > 0 } ns) return dir;
        var segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (ns == s || ns.StartsWith(s + ".", StringComparison.Ordinal)) return string.Join("/", segments.Take(i + 1));
        }
        return dir;
    }
}
