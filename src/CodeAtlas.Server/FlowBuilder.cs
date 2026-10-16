using System.Security.Cryptography;
using System.Text;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <param name="Kind">entry / call / dispatch / hop / http（没对上路由的 HTTP 出口，叶子）/ external-group / more / function。</param>
/// <param name="Via">边的来历：impl（接口 → 实现）、render（JSX 渲染组件）、dispatch:T（分派）；普通调用为 null。</param>
/// <param name="Count">相邻相同子树折叠后的次数（≥ 1）。</param>
/// <param name="Hidden">kind=more 时被折起来的分支数。</param>
/// <param name="Note">说明：hop / http 的去向；唯一实现合成一步的节点是"经接口 IXxx"；单独成行的关键外部调用是类别（数据库 / HTTP / 消息 / 云服务 / 外部进程 / 文件）；
/// "构造 N 个对象"（kind=call、没有符号 id）是构造的类型名列表；其余为 null。</param>
/// <param name="Path">可选，单击跳转用：调用处所在文件（call / dispatch / external-group 是调用方文件，hop / http 是出口所在文件）；根与续接进去的对端入口没有调用处，给定义所在文件；"还有 N 个"为 null。</param>
/// <param name="Line">可选：调用处的行（1 起；根与对端入口是定义的起始行）；不知道时为 null。</param>
/// <param name="Pos">可选：调用处在文件里的 UTF-16 偏移（edges.pos）；hop / http / 定义处没有，为 null。</param>
public sealed record FlowNodeDto(
    long? SymbolId, string Label, string QualifiedName, string Kind, string Actor, string? Via, double Confidence,
    int Count, int Depth, IReadOnlyList<FlowNodeDto> Children, int Hidden, string? Note,
    string? Path = null, int? Line = null, int? Pos = null);

/// <param name="Kind">project（容器内成员项目）/ container（另一个容器）/ external。</param>
public sealed record FlowActorDto(string Key, string Label, string Kind);

public sealed record FlowDto(
    long EntrySymbolId, string EntryLabel, string? Route, string ContainerKey, IReadOnlyList<FlowActorDto> Actors, FlowNodeDto Root,
    IReadOnlyList<string> Behaviors, int Nodes, bool Truncated, string? Note);

/// <summary>
/// 从一个入口出发的有序调用树 → 时序图数据。走运行时去向：calls（含 via=impl 的接口 → 实现派生边、via=render 的 JSX 组件渲染边）与 dispatch:T；
/// 不走 conduit / references / inherits / implements。深度 12、每节点 6 个分支、总 300 个节点；外部调用按列分组；相邻相同子树折叠；
/// 方法里对上另一容器路由的 HTTP 调用续接到对方入口；没对上的 HTTP 出口（容器图规则识别的，比如单独打开前端时后端不在索引里）画成 http 叶子。
/// 每层先按 rank 选进扇出、分名额（分派 &gt; 实现 &gt; hook &gt; 续接 &gt; 普通调用；能往下走的排在叶子前，没有构造逻辑的 new 最低），
/// 同档再按"分量"（<see cref="Weight"/>：子树规模、走不走到 I/O、被多少处调用）排，最后才按限定名；
/// 选进来的再按调用处的源码位置排（与调用大纲同一排序）；接口调用与唯一实现合成一步，多实现时实现挂在接口节点下。
/// 有别的调用时，没有构造逻辑的 new（两个以上）合成一行"构造 N 个对象"；外部调用按包 / 命名空间分组各成一行，数据库 / HTTP / 消息等关键类别单独成行（<see cref="ExternalCategory"/>）。
/// </summary>
public static partial class FlowBuilder
{
    public const int DefaultMaxDepth = 12;
    public const int DefaultFanout = 6;
    public const int DefaultMaxNodes = 300;
    private static readonly HashSet<string> DispatchViaSkip = ["handles", "conduit"];

    /// <summary>节点在源码里的位置（FlowNodeDto 的 Path / Line / Pos）。</summary>
    private readonly record struct Site(string? Path, int? Line, int? Pos);

    private static Site At(string path, int line, int? pos) => new(path, line > 0 ? line : null, pos);

    // 扇出排序档位（越大越先被选进扇出 / 先分名额；显示顺序另按调用处的源码位置排）
    internal const int RankDispatch = 100;
    internal const int RankImpl = 40;
    internal const int RankHook = 35;
    internal const int RankHopHere = 33;
    internal const int RankHopBelow = 32;
    internal const int RankCall = 30;
    /// <summary>目标只调框架（往下只剩"N 个框架调用"）：比能往下走的同档低一点，但不掉到下一档（只调 nuqs / react 的 hook 仍排在子组件前）。</summary>
    internal const int PenaltyExternalOnly = 2;
    /// <summary>目标是叶子（仓库里的方法但自己没有出边）：排在所有能往下走的普通调用之后。</summary>
    internal const int PenaltyLeaf = 20;
    /// <summary>构造一个没有构造逻辑的对象（DTO / 异常 / 结果）：最低档，只在扇出还有空位时出现。</summary>
    internal const int RankTrivialNew = 1;

    /// <summary>流程走的边：calls（置信度 ≥ 0.4、不是 conduit）与 dispatch:T 分派。</summary>
    internal static bool IsFlowEdge(MapEdge e) =>
        (e.Kind == EdgeKind.Calls && e.Confidence >= 0.4 && e.Via != "conduit")
        || (e.Kind == EdgeKind.Dispatches && e.Via is not null && !DispatchViaSkip.Contains(e.Via));

    public static FlowDto Build(
        IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, EntryPoint entry, IReadOnlyList<ArchNode> containers,
        IReadOnlyList<RouteMatch> matches, IReadOnlyList<EntryPoint> allEntries,
        int maxDepth = DefaultMaxDepth, int fanout = DefaultFanout, int maxNodes = DefaultMaxNodes,
        IReadOnlyList<Outbound>? outbound = null, IReadOnlyList<ArchNode>? externalNodes = null)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var childrenOf = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var outgoing = edges.Where(IsFlowEdge).ToLookup(e => e.FromId);
        // 续接按"当前所在容器"取出口：入口容器的，续接进别的容器后接着用那个容器的（frontend → checkout → payment）
        var matchesByFile = matches.ToLookup(m => m.File);
        // 本容器的 HTTP 出口里没对上路由的那些（对上的已经画成 hop）：按文件分组，展开节点时按行范围挂上去
        var matchedSites = matches.Where(m => m.FromContainerKey == entry.ContainerKey).Select(m => (m.File, m.Line)).ToHashSet();
        var unmatchedByFile = (outbound ?? []).Where(o => o.ContainerKey == entry.ContainerKey && o.Protocol == "http" && !matchedSites.Contains((o.File, o.Line)))
            .ToLookup(o => o.File);
        IEnumerable<Outbound> UnmatchedIn(MapSymbol s) => unmatchedByFile[s.Path].Where(o => o.Line >= s.StartLine && o.Line <= s.EndLine);
        IEnumerable<RouteMatch> HopsIn(MapSymbol s, ArchNode? home) =>
            matchesByFile[s.Path].Where(m => m.FromContainerKey == (home?.Key ?? entry.ContainerKey) && m.Line >= s.StartLine && m.Line <= s.EndLine);
        // 往下一层才续接（chooseAd → getAd → gRPC AdService）：排在普通调用前、直接续接的后面
        bool HopsBelow(MapSymbol s, ArchNode? home) =>
            outgoing[s.Id].Any(e => e.ToId != s.Id && byId.TryGetValue(e.ToId, out var t) && t.Kind != SymbolKind.External && HopsIn(t, home).Any());
        // gRPC 出口（容器图规则按客户端桩登记，行号是构造桩那一行）里没按方法对上入口的：对端是容器但没有入口（Node 服务没有符号）→ 停在 hop；
        // 对端是外部系统 → 出口叶子。同一方法里已有对到同一容器的 hop 就不重复
        var grpcByFile = (outbound ?? []).Where(o => o.Protocol == "grpc" && o.TargetKey is not null).ToLookup(o => o.File);
        IEnumerable<Outbound> GrpcExitsIn(MapSymbol s, ArchNode? home)
        {
            var hops = HopsIn(s, home).Select(m => m.ToContainerKey).ToHashSet(StringComparer.Ordinal);
            return grpcByFile[s.Path].Where(o => o.ContainerKey == (home?.Key ?? entry.ContainerKey) && o.Line >= s.StartLine && o.Line <= s.EndLine && !hops.Contains(o.TargetKey!))
                .DistinctBy(o => (o.TargetKey, o.ClientName));
        }
        // 目标往下还能走多远：能走到仓库里的别的方法 / 续接 / 出口 = 2；只调框架 = 1；什么都不调（叶子）= 0。
        // 测试代码与自己调自己不算（展开时也会被剪掉）
        int Reach(MapSymbol t, ArchNode? home)
        {
            if (t.Kind == SymbolKind.Module && childrenOf[t.Id].Any(c => c.Kind is SymbolKind.Function or SymbolKind.Class)) return 2;
            var external = false;
            foreach (var e in outgoing[t.Id])
            {
                if (e.ToId == t.Id || !byId.TryGetValue(e.ToId, out var x)) continue;
                if (x.Kind == SymbolKind.External) { external = true; continue; }
                if (!PipelineBuilder.IsTest(x.Path, x.QualifiedName)) return 2;
            }
            if (HopsIn(t, home).Any() || UnmatchedIn(t).Any() || GrpcExitsIn(t, home).Any()) return 2;
            return external ? 1 : 0;
        }
        bool IsTypeKind(SymbolKind k) => k is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Interface;
        bool IsConstructor(MapSymbol t) =>
            t.Kind is SymbolKind.Method or SymbolKind.Function && t.ParentId is { } pid && byId.TryGetValue(pid, out var owner) && IsTypeKind(owner.Kind)
            && (t.Name is "constructor" or "__init__" || MapBuilder.StripGenericArgs(t.Name) == MapBuilder.StripGenericArgs(owner.Name));
        // new 一个 DTO / 异常 / 结果对象：目标是类型本身（没有显式构造函数），或构造函数体里没有调到仓库里的别的东西
        bool IsTrivialNew(MapSymbol t, ArchNode? home) => (IsTypeKind(t.Kind) || IsConstructor(t)) && Reach(t, home) < 2;
        int RankOf(MapEdge e, MapSymbol t, ArchNode? home)
        {
            if (e.Kind == EdgeKind.Dispatches) return RankDispatch;
            if (IsTrivialNew(t, home)) return RankTrivialNew;
            // React hook（useXxx）是组件的数据 / 行为来源，排在子组件与局部函数前面，免得被扇出上限折进"还有 N 个"；
            // 自己就续接到另一个容器的方法（getProducts → gRPC）也排前面——跨服务的那一步是时序图最该看到的
            // 下一层才续接的（chooseAd → getAd → gRPC）紧随其后
            var rank = e.Via == "impl" ? RankImpl : IsHook(t.Name) ? RankHook : HopsIn(t, home).Any() ? RankHopHere : HopsBelow(t, home) ? RankHopBelow : RankCall;
            // "能往下走"加权：同档里能展开的排在叶子之前，叶子排在所有能展开的普通调用之后
            return rank - Reach(t, home) switch { 2 => 0, 1 => PenaltyExternalOnly, _ => PenaltyLeaf };
        }
        // new 的是哪个类型（目标是类型本身或它的构造函数）；不是构造返回 null
        string? ConstructedType(MapSymbol t) =>
            IsTypeKind(t.Kind) ? MapBuilder.StripGenericArgs(t.Name)
            : IsConstructor(t) && byId.TryGetValue(t.ParentId!.Value, out var owner) ? MapBuilder.StripGenericArgs(owner.Name) : null;
        string OwnerName(MapSymbol t) => t.ParentId is { } p && byId.TryGetValue(p, out var o) ? o.Name : t.Name;

        // new T(...)：边指向类型本身（构造函数有重载、解析不到是哪个）时，换成有调用的那个构造函数（调到仓库里的方法最多的，同样多取靠前的），
        // 构造函数里有逻辑（new Order(...) 里登记领域事件）就按普通调用排、能展开；都没有调用时仍是类型本身（没有构造逻辑的 new）
        var ctorMemo = new Dictionary<long, MapSymbol?>();
        MapSymbol? CtorWithCalls(MapSymbol type)
        {
            if (ctorMemo.TryGetValue(type.Id, out var c)) return c;
            // 先比调到仓库里的方法的边数（protected Order() 只 new 一个 List，public Order(...) 调 AddOrderStartedDomainEvent），再比全部出边
            c = childrenOf[type.Id].Where(IsConstructor)
                .Select(x => (Ctor: x, Repo: outgoing[x.Id].Count(e => e.ToId != x.Id && byId.TryGetValue(e.ToId, out var y) && y.Kind != SymbolKind.External),
                    Out: outgoing[x.Id].Count(e => e.ToId != x.Id)))
                .Where(x => x.Out > 0).OrderByDescending(x => x.Repo).ThenByDescending(x => x.Out).ThenBy(x => x.Ctor.StartLine).Select(x => x.Ctor).FirstOrDefault();
            return ctorMemo[type.Id] = c;
        }

        // 同档里的"分量"：先进扇出的应该是真正干活的那一步，而不是限定名字母序靠前的（IndexCommand.Run 的 IndexAsync 被 CommonOptions.DbPath 挤掉）。
        // = log2(1 + 往下 WeightDepth 层能走到的仓库方法数) + 走到 I/O 的加分 − 公共工具（被很多处调用）的减分，见 Weight；hook 另加名字相关度（NameAffinity）
        var callerMemo = new Dictionary<long, int>();
        var callersOf = edges.Where(IsFlowEdge).ToLookup(e => e.ToId);
        int Callers(MapSymbol t)
        {
            if (callerMemo.TryGetValue(t.Id, out var n)) return n;
            n = callersOf[t.Id].Select(e => e.FromId).Distinct().Count(f => f != t.Id && byId.TryGetValue(f, out var x) && !PipelineBuilder.IsTest(x.Path, x.QualifiedName));
            return callerMemo[t.Id] = n;
        }
        var weightMemo = new Dictionary<(long, string?), double>();
        double WeightOf(MapSymbol t, ArchNode? home)
        {
            var key = (t.Id, home?.Key);
            if (weightMemo.TryGetValue(key, out var w)) return w;
            var seen = new HashSet<long> { t.Id };
            var frontier = new List<MapSymbol> { t };
            var io = false;
            var directIo = false;
            for (var level = 0; level <= WeightDepth && frontier.Count > 0 && seen.Count <= WeightCap; level++)
            {
                var nextFrontier = new List<MapSymbol>();
                foreach (var x in frontier)
                {
                    if (!io && (HopsIn(x, home).Any() || UnmatchedIn(x).Any() || GrpcExitsIn(x, home).Any())) io = true;
                    foreach (var e in outgoing[x.Id])
                    {
                        if (!byId.TryGetValue(e.ToId, out var y)) continue;
                        if (e.Kind == EdgeKind.Dispatches) io = true;
                        if (y.Kind == SymbolKind.External) { if (!io && ExternalCategory(y.QualifiedName) is not null) io = true; continue; }
                        if (level == WeightDepth || PipelineBuilder.IsTest(y.Path, y.QualifiedName) || !seen.Add(y.Id)) continue;
                        nextFrontier.Add(y);
                    }
                }
                if (level == 0) directIo = io; // 第 0 层只有 t 自己：它直接做 I/O
                frontier = nextFrontier;
            }
            // 只数方法 / 函数 / 模块：new 出来的类型（没有显式构造函数的 DTO、适配器）不算干活
            var reach = seen.Count(id => id != t.Id && !IsTypeKind(byId[id].Kind));
            return weightMemo[key] = Weight(reach, io, Callers(t), directIo);
        }
        bool IsInterfaceMember(MapSymbol t) => t.ParentId is { } p && byId.TryGetValue(p, out var o) && o.Kind == SymbolKind.Interface;

        // 别的 .NET 容器也在索引里 → 后端在，只是路由没匹配上
        var unmatchedNote = containers.Any(c => c.Key != entry.ContainerKey && c.Kind == ArchNodeKind.Container && c.SubKind.StartsWith("dotnet", StringComparison.Ordinal))
            ? "未对上后端（路由没匹配上）" : "未对上后端（后端不在索引里，或路由没匹配上）";
        var entriesById = allEntries.GroupBy(e => e.SymbolId).ToDictionary(g => g.Key, g => g.First());
        var container = containers.FirstOrDefault(c => c.Key == entry.ContainerKey);
        var actors = new Dictionary<string, FlowActorDto>(StringComparer.Ordinal);
        var nodes = 0;
        var cut = new HashSet<FlowNodeDto>(ReferenceEqualityComparer.Instance);
        bool HasCut(FlowNodeDto n) => cut.Contains(n) || n.Children.Any(HasCut);

        // 方法显示成"类型.方法"：一屏里好几个 Handle / HandleAsync 时光看方法名分不清是谁的；构造函数只显示类型名。Java 的 enum 也能有方法
        string MemberLabel(MapSymbol s)
        {
            if (s.Kind is not (SymbolKind.Method or SymbolKind.Function)) return s.Name;
            // 局部函数（父符号是外层方法）只显示自己的名字；顶级语句显示 Program.Main；
            // 父符号是命名空间（分部类的方法有时这样入库）时，类型名从限定名里取
            var owner = s.ParentId is { } pid ? byId.GetValueOrDefault(pid) : null;
            if (owner is null || owner.Kind is not (SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum))
            {
                if (GoReceiver(s) is { } recv) return recv + "." + s.Name;
                if (MapBuilder.TopLevelLabel(s.QualifiedName) is { } top) return top;
                return owner?.Kind == SymbolKind.Namespace && MapBuilder.DeclaringTypeSegment(s.QualifiedName) is { } type && type != s.Name ? type + "." + s.Name : s.Name;
            }
            return owner.Name == s.Name ? s.Name : owner.Name + "." + s.Name;
        }

        string ActorOf(MapSymbol s, ArchNode? home)
        {
            if (s.Kind == SymbolKind.External)
            {
                var q = s.QualifiedName["external:".Length..];
                // tree-sitter 兜底的外部符号没有命名空间（external:Append），全部归到一个"外部"列，不然一列一个方法
                var first = q.Contains('.') ? q.Split('.')[0] : "";
                var extKey = first.Length > 0 ? "ext|" + first : "ext|";
                actors.TryAdd(extKey, new FlowActorDto(extKey, first.Length > 0 ? first : "外部 / 框架", "external"));
                return extKey;
            }
            var owner = home is not null && home.Members.Any(m => Under(s.Path, m)) ? home : containers.FirstOrDefault(c => c.Members.Any(m => Under(s.Path, m)));
            if (owner is null) { actors.TryAdd("unknown", new FlowActorDto("unknown", "（未归属）", "external")); return "unknown"; }
            var member = owner.Members.Where(m => Under(s.Path, m)).OrderByDescending(m => m.Length).First();
            var actorKey = owner.Key + "|" + member;
            var label = owner.Members.Count == 1 ? owner.Name : (owner.Key == entry.ContainerKey ? MapBuilder.LastSegment(member.Length == 0 ? owner.Name : member) : owner.Name + " / " + MapBuilder.LastSegment(member));
            actors.TryAdd(actorKey, new FlowActorDto(actorKey, label, owner.Key == entry.ContainerKey ? "project" : "container"));
            return actorKey;
        }

        // 出口的目标：外部系统按节点键成列（列名用外部节点名）；目标是容器时与 hop 同一列；没有目标归"外部 / 框架"
        string HttpActor(Outbound o)
        {
            if (o.TargetKey is not { } key) { actors.TryAdd("ext|", new FlowActorDto("ext|", "外部 / 框架", "external")); return "ext|"; }
            if (containers.FirstOrDefault(c => c.Key == key && c.Kind == ArchNodeKind.Container) is { } c)
            {
                var containerActor = c.Key + "|" + (c.Members.FirstOrDefault() ?? "");
                actors.TryAdd(containerActor, new FlowActorDto(containerActor, c.Name, "container"));
                return containerActor;
            }
            var name = externalNodes?.FirstOrDefault(n => n.Key == key)?.Name ?? key[(key.LastIndexOf(':') + 1)..];
            actors.TryAdd(key, new FlowActorDto(key, name, "external"));
            return key;
        }

        // 外部调用的行：原来一个节点里的全部外部调用并成一行、挂在第一个外部符号的列上（[Logger] 下藏着 JpaRepository.save，[jotai] 下是 react hooks）。
        // 现在：数据库 / HTTP / 消息 / 云服务 / 外部进程 / 文件类的外部调用单独成行（见 ExternalCategory）（同一列同一类别一行，Note 写类别）；其余按列（包 / 命名空间，与 ActorOf 同一键）分组，一组一行、挂在自己的列；
        // 组超过 MaxExternalGroups 个时，调用少的那几组并进"外部 / 框架"列一行。行按第一处调用的源码位置排，放在子节点最后
        IEnumerable<FlowNodeDto> ExternalRows(List<(MapEdge Edge, MapSymbol Target)> externals, ArchNode? home, int depth, Func<MapEdge, string> sitePath)
        {
            var items = externals.GroupBy(x => x.Target.Id)
                .Select(g => (Target: g.First().Target, Site: OutlineBuilder.SortBySite(g.Select(x => x.Edge), e => e.Line, e => e.Pos, _ => 0).First()))
                .ToList();
            var rows = new List<(MapEdge Site, FlowNodeDto Node)>();
            // 关键类别：同一列、同一类别并成一行（ArchStorage.Read 里 ExecuteReader / Read / GetString 是一行"4 个数据库调用"）
            var keyed = items.Select(x => (x.Target, x.Site, Category: ExternalCategory(x.Target.QualifiedName))).Where(x => x.Category is not null).ToList();
            var keyRows = keyed.GroupBy(x => (Actor: ActorOf(x.Target, home), x.Category)).Take(MaxKeyExternalRows).ToList();
            foreach (var g in keyRows)
            {
                var list = g.ToList();
                var siteEdge = OutlineBuilder.SortBySite(list.Select(x => x.Site), e => e.Line, e => e.Pos, _ => 0).First();
                var at = At(sitePath(siteEdge), siteEdge.Line, siteEdge.Pos);
                var labels = list.Select(x => OutlineBuilder.ExternalLabel(x.Target.QualifiedName)).Distinct(StringComparer.Ordinal).ToList();
                rows.Add((siteEdge, new FlowNodeDto(null, labels.Count == 1 ? labels[0] : $"{list.Count} 个{g.Key.Category}调用", list.Count == 1 ? list[0].Target.QualifiedName : string.Join(", ", labels.Take(8)),
                    "external-group", g.Key.Actor, null, 1.0, 1, depth + 1, [], 0, g.Key.Category, at.Path, at.Line, at.Pos)));
            }
            keyed = keyRows.SelectMany(g => g).ToList();
            var keyedIds = keyed.Select(x => x.Target.Id).ToHashSet();
            // 有包名的组在前（调用多的先），没有命名空间的（tree-sitter 兜底：setIsMounted、map）垫底——它们本来就在"外部 / 框架"列
            var groups = items.Where(x => !keyedIds.Contains(x.Target.Id)).GroupBy(x => ActorOf(x.Target, home), StringComparer.Ordinal)
                .Select(g => (Actor: g.Key, Items: g.ToList()))
                .OrderBy(g => g.Actor == "ext|").ThenByDescending(g => g.Items.Count).ThenBy(g => g.Items.Min(x => x.Site.Line)).ToList();
            if (groups.Count > MaxExternalGroups)
            {
                var rest = groups.Skip(MaxExternalGroups - 1).SelectMany(g => g.Items).ToList();
                groups = groups.Take(MaxExternalGroups - 1).ToList();
                var generic = groups.FindIndex(g => g.Actor == "ext|");
                actors.TryAdd("ext|", new FlowActorDto("ext|", "外部 / 框架", "external"));
                if (generic >= 0) groups[generic] = ("ext|", [.. groups[generic].Items, .. rest]);
                else groups.Add(("ext|", rest));
            }
            foreach (var (groupActor, list) in groups)
            {
                var siteEdge = OutlineBuilder.SortBySite(list.Select(x => x.Site), e => e.Line, e => e.Pos, _ => 0).First();
                var at = At(sitePath(siteEdge), siteEdge.Line, siteEdge.Pos);
                rows.Add((siteEdge, new FlowNodeDto(null, list.Count == 1 ? list[0].Target.Name : $"{list.Count} 个框架调用", string.Join(", ", list.Select(x => x.Target.Name).Take(8)),
                    "external-group", groupActor, null, 1.0, 1, depth + 1, [], 0, null, at.Path, at.Line, at.Pos)));
            }
            return OutlineBuilder.SortBySite(rows, r => r.Site.Line, r => r.Site.Pos, _ => 0).Select(r => r.Node);
        }

        // 同一符号（同一个所在容器）在一张图里只完整展开一次：以后再出现画成叶子（Note 写在上面 / 下面已展开），名额留给别的分支。
        // ccs-ui 每个 hook 经 useApi 都走到 new ApiClient() 的构造函数（拦截器那一串），不去重时同一棵子树重复十几遍。
        // expandedBy：键 → 登记它的那次展开（令牌）；regLog：登记流水，第二轮补名额重新展开某个分支前，先撤掉它上次展开时登记的
        var expandedBy = new Dictionary<(long, string?), object>();
        var regLog = new List<((long, string?) Key, object Token)>();
        void Unregister(int from, int to)
        {
            for (var i = from; i < to; i++)
                if (expandedBy.TryGetValue(regLog[i].Key, out var t) && ReferenceEquals(t, regLog[i].Token)) expandedBy.Remove(regLog[i].Key);
        }

        // budget：这棵子树（含自己）最多能用的节点数。名额在兄弟分支之间平均分，先展开的分支没用完的顺延给后面的——
        // 不然深度优先会让第一条分支吃光 300 个名额（组管理页合并后端后，一个服务那支续接进后端把另一支挤没了）。
        // site：这个节点在源码里的位置（调用处：调用方文件 + 边的行 / pos；根与续接进去的对端入口没有调用处，给定义处）。
        // note：节点说明（唯一实现合成一步时写"经接口 IXxx"）。extra：额外的出边（多实现时接口节点下挂的那几条 impl 边，调用处在 extraPath 文件里）
        FlowNodeDto Expand(MapSymbol s, string kind, string? via, double confidence, int depth, HashSet<long> ancestors, ArchNode? home, int budget, Site site,
            string? note = null, IReadOnlyList<(MapEdge Edge, MapSymbol Target)>? extra = null, string? extraPath = null)
        {
            nodes++;
            var actor = ActorOf(s, home);
            // 模块显示文件名（page.tsx），不能按 '.' 切成 "tsx"
            var label = s.Kind == SymbolKind.Module ? MapSymbol.FileName(s.Path) : MemberLabel(s);
            var dupKey = (s.Id, home?.Key);
            if (kind != "entry" && s.Kind != SymbolKind.External && depth < maxDepth && expandedBy.ContainsKey(dupKey))
                return new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, [], 0, note is null ? DupMarker : note + "；" + DupMarker,
                    site.Path, site.Line, site.Pos);
            if (depth >= maxDepth || budget <= 1 || s.Kind == SymbolKind.External)
            {
                var leaf = new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, [], 0, note, site.Path, site.Line, site.Pos);
                // 名额用完、其实还能往下走的节点记下来：截断标志按最终那棵树算（第二轮补名额后可能已经展开了）
                if (budget <= 1 && s.Kind != SymbolKind.External && depth < maxDepth
                    && (outgoing[s.Id].Any() || extra is { Count: > 0 } || HopsIn(s, home).Any() || UnmatchedIn(s).Any())) cut.Add(leaf);
                return leaf;
            }

            var token = new object();
            expandedBy[dupKey] = token;
            regLog.Add((dupKey, token));
            var children = new List<FlowNodeDto>();
            var next = new HashSet<long>(ancestors) { s.Id };
            // 待展开的子分支：先收齐，再按名额逐个展开（按 rank 的顺序分名额）；Line / Pos 是它在源码里的位置，最后按它排显示顺序
            var pending = new List<(Func<int, FlowNodeDto> Build, int Line, int? Pos)>();

            // 模块（前端页面）：子节点是模块里定义的函数
            if (s.Kind == SymbolKind.Module)
            {
                foreach (var f in childrenOf[s.Id].Where(c => c.Kind is SymbolKind.Function or SymbolKind.Class).OrderBy(c => c.StartLine).Take(fanout))
                    pending.Add((b => Expand(f, "function", null, 1.0, depth + 1, next, home, b, At(f.Path, f.StartLine, null)), f.StartLine, null));
            }

            // 出边：分派优先，再容器内调用；外部合并成一个节点
            var candidates = new List<(MapEdge Edge, MapSymbol Target)>();
            var externals = new List<(MapEdge Edge, MapSymbol Target)>();
            var extraEdges = extra is null ? null : new HashSet<MapEdge>(extra.Select(x => x.Edge), ReferenceEqualityComparer.Instance);
            string SitePath(MapEdge e) => extraEdges is not null && extraEdges.Contains(e) ? extraPath ?? s.Path : s.Path;
            // 这个方法里有 gRPC 续接时，生成的客户端桩（*.pb.go 里的 XxxClient.Method / NewXxxClient）就是那次 RPC 本身，由 hop 节点表示
            var grpcExits = GrpcExitsIn(s, home).ToList();
            var hasGrpcHop = grpcExits.Count > 0 || HopsIn(s, home).Any(m => m.Path.StartsWith(RouteLinker.GrpcPathPrefix, StringComparison.Ordinal));
            foreach (var e in extra is null ? outgoing[s.Id] : outgoing[s.Id].Concat(extra.Select(x => x.Edge)))
            {
                if (!byId.TryGetValue(e.ToId, out var t) || next.Contains(t.Id)) continue;
                if (t.Kind == SymbolKind.External) { externals.Add((e, t)); continue; }
                if (PipelineBuilder.IsTest(t.Path, t.QualifiedName)) continue;
                if (hasGrpcHop && IsGeneratedStub(t.Path)) continue;
                if (e.Kind == EdgeKind.Calls && IsTypeKind(t.Kind) && CtorWithCalls(t) is { } ctor)
                {
                    if (next.Contains(ctor.Id)) continue;
                    t = ctor;
                }
                candidates.Add((e, t));
            }
            // 接口调用与它的实现（via=impl 的派生边，同一处：同行且 pos 相同，没有 pos 时同行同名）：
            // 唯一实现（0.9）→ 合成一步，只画实现，note 写"经接口 IXxx"（接口方法没有方法体，单独一步只是个叶子，还占扇出名额）；
            // 多个候选实现（各 0.5）→ 保留接口那步，实现挂在它下面当子节点
            var implEdges = candidates.Where(c => c.Edge.Via == "impl").ToList();
            bool SameSite(MapEdge call, MapSymbol callee, (MapEdge Edge, MapSymbol Target) impl) =>
                impl.Edge.Line == call.Line && (impl.Edge.Pos is not null && call.Pos is not null ? impl.Edge.Pos == call.Pos : impl.Target.Name == callee.Name);
            var viaInterface = new Dictionary<long, string>();
            var nested = new Dictionary<long, List<(MapEdge Edge, MapSymbol Target)>>();
            var dropped = new HashSet<MapEdge>(ReferenceEqualityComparer.Instance);
            foreach (var c in candidates.Where(c => c.Edge.Kind == EdgeKind.Calls && c.Edge.Via is null && IsInterfaceMember(c.Target)))
            {
                var mine = implEdges.Where(i => SameSite(c.Edge, c.Target, i)).ToList();
                if (mine.Count == 0) continue;
                if (mine.Any(i => i.Edge.Confidence >= 0.9))
                {
                    dropped.Add(c.Edge);
                    foreach (var i in mine) viaInterface.TryAdd(i.Target.Id, "经接口 " + OwnerName(c.Target));
                }
                else
                {
                    if (!nested.TryGetValue(c.Target.Id, out var list)) nested[c.Target.Id] = list = [];
                    foreach (var i in mine)
                    {
                        if (list.All(x => x.Target.Id != i.Target.Id)) list.Add(i);
                        dropped.Add(i.Edge);
                    }
                }
            }
            candidates.RemoveAll(c => dropped.Contains(c.Edge));
            // 构造的就是这次分派的消息（new XxxCommand 再 Send）：分派那步已经代表它，构造不再单列
            var dispatched = candidates.Where(c => c.Edge.Kind == EdgeKind.Dispatches && c.Edge.Via!.StartsWith("dispatch:", StringComparison.Ordinal))
                .Select(c => MapBuilder.StripGenericArgs(c.Edge.Via!["dispatch:".Length..])).ToHashSet(StringComparer.Ordinal);
            if (dispatched.Count > 0) candidates.RemoveAll(c => c.Edge.Kind == EdgeKind.Calls && ConstructedType(c.Target) is { } ct && dispatched.Contains(ct));
            // 同一目标多条边：rank 取最高那条，位置取最早的调用处；挂着多个实现的接口节点按其中最好的那个实现排
            int RankWithNested(MapEdge e, MapSymbol t) =>
                nested.TryGetValue(t.Id, out var impls) ? impls.Max(i => RankOf(i.Edge, i.Target, home)) : RankOf(e, t, home);
            // hook 再加名字相关度：组件的主数据 hook 通常以组件的主题命名（MT4SymbolTable → useMT4Symbols）
            double WeightWithNested(MapSymbol t) =>
                nested.TryGetValue(t.Id, out var impls) ? impls.Max(i => WeightOf(i.Target, home))
                : WeightOf(t, home) + (IsHook(t.Name) ? NameAffinity(s.Name, t.Name) * WeightNameBonus + (ReadsData(t) ? WeightReadBonus : 0) : 0);
            // 取数据的 hook（自己调了 useQuery / useSWR 一类）是组件的主数据；只调 useMutation 的是动作（导入、导出……）
            bool ReadsData(MapSymbol t) => outgoing[t.Id].Any(e => byId.TryGetValue(e.ToId, out var y) && y.Kind == SymbolKind.External && IsDataReadApi(y.QualifiedName));
            // 同档按分量（子树规模、I/O、公共工具降权），分量也相同才按限定名（保证确定性）
            var ordered = candidates.GroupBy(c => c.Target.Id)
                .Select(g =>
                {
                    var best = g.Select(c => (c.Edge, c.Target, Rank: RankWithNested(c.Edge, c.Target))).OrderByDescending(r => r.Rank).First();
                    var siteEdge = OutlineBuilder.SortBySite(g.Select(c => c.Edge), e => e.Line, e => e.Pos, _ => 0).First();
                    return (best.Edge, best.Target, best.Rank, Site: siteEdge);
                })
                .OrderByDescending(r => r.Rank).ThenByDescending(r => WeightWithNested(r.Target)).ThenBy(r => r.Target.QualifiedName, StringComparer.Ordinal).ToList();
            // 没有构造逻辑的 new（DTO / 异常 / 结果对象）：还有别的调用时，两个以上合成一行"构造 N 个对象"，不占扇出名额
            // （ArchStorage.Read 6 个分支 5 个是 new DTO）；只有一个时照常（最低档，扇出有空位才出现）；全是 new 时照常逐个列
            (Func<int, FlowNodeDto> Build, int Line, int? Pos)? newRow = null;
            var trivial = ordered.Where(r => r.Rank == RankTrivialNew && r.Edge.Kind == EdgeKind.Calls).ToList();
            if (trivial.Count >= 2 && trivial.Count < ordered.Count)
            {
                ordered.RemoveAll(r => r.Rank == RankTrivialNew && r.Edge.Kind == EdgeKind.Calls);
                var sites = OutlineBuilder.SortBySite(trivial, r => r.Site.Line, r => r.Site.Pos, _ => 0).ToList();
                var first = sites[0];
                var names = sites.Select(r => ConstructedType(r.Target) ?? r.Target.Name).Distinct(StringComparer.Ordinal).ToList();
                var newSite = At(SitePath(first.Site), first.Site.Line, first.Site.Pos);
                newRow = (_ =>
                {
                    nodes++;
                    return new FlowNodeDto(null, $"构造 {trivial.Count} 个对象", string.Join(", ", names), "call", ActorOf(first.Target, home), null, 1.0, 1, depth + 1, [], 0,
                        string.Join(", ", names.Take(8)) + (names.Count > 8 ? " …" : ""), newSite.Path, newSite.Line, newSite.Pos);
                }, first.Site.Line, first.Site.Pos);
            }
            foreach (var (e, t, _, siteEdge) in ordered.Take(fanout))
            {
                var childKind = e.Kind == EdgeKind.Dispatches ? "dispatch" : "call";
                var childSite = At(SitePath(siteEdge), siteEdge.Line, siteEdge.Pos);
                var kids = nested.GetValueOrDefault(t.Id);
                var childNote = viaInterface.GetValueOrDefault(t.Id);
                pending.Add((b => Expand(t, childKind, e.Via, e.Confidence, depth + 1, next, home, b, childSite, childNote, kids, childSite.Path),
                    siteEdge.Line, siteEdge.Pos));
            }
            if (newRow is { } row) pending.Add(row);
            // 跨容器续接：这个方法里对上另一容器路由的 HTTP 调用（也参与分名额）
            foreach (var m in HopsIn(s, home))
            {
                var target = containers.FirstOrDefault(c => c.Key == m.ToContainerKey);
                var targetEntry = entriesById.GetValueOrDefault(m.EntrySymbolId);
                var hopActorKey = target is null ? "unknown" : target.Key + "|" + (target.Members.FirstOrDefault() ?? "");
                var hopLabel = m.Path.StartsWith(RouteLinker.GrpcPathPrefix, StringComparison.Ordinal) ? $"gRPC {m.Path[RouteLinker.GrpcPathPrefix.Length..]}" : $"HTTP {m.Path}";
                var hopSite = At(m.File, m.Line, null);
                // 参与者在真正展开时才登记，保持"按遍历中首次出现"排序
                void AddHopActor() { if (target is not null) actors.TryAdd(hopActorKey, new FlowActorDto(hopActorKey, target.Name, "container")); }
                if (targetEntry is not null && byId.TryGetValue(targetEntry.SymbolId, out var es) && !next.Contains(es.Id) && depth + 1 < maxDepth)
                    pending.Add((b => { AddHopActor(); return new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1,
                        [Expand(es, "call", null, 1.0, depth + 2, next, target, b, At(es.Path, es.StartLine, null))], 0, $"→ {target?.Name} {targetEntry.Route}",
                        hopSite.Path, hopSite.Line, hopSite.Pos); }, m.Line, null));
                else
                    pending.Add((_ => { AddHopActor(); return new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [], 0,
                        target is null ? "目标容器不存在" : $"→ {target.Name}（没有索引到符号，停在这里）", hopSite.Path, hopSite.Line, hopSite.Pos); }, m.Line, null));
            }
            foreach (var o in grpcExits)
            {
                var exitLabel = "gRPC " + (o.ClientName ?? o.TargetKey);
                var exitSite = At(o.File, o.Line, null);
                if (containers.FirstOrDefault(c => c.Key == o.TargetKey && c.Kind == ArchNodeKind.Container) is { } target)
                {
                    var hopActorKey = target.Key + "|" + (target.Members.FirstOrDefault() ?? "");
                    pending.Add((_ =>
                    {
                        nodes++;
                        actors.TryAdd(hopActorKey, new FlowActorDto(hopActorKey, target.Name, "container"));
                        return new FlowNodeDto(null, exitLabel, o.ClientName ?? "", "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [], 0, $"→ {target.Name}（没有索引到符号，停在这里）",
                            exitSite.Path, exitSite.Line, exitSite.Pos);
                    }, o.Line, null));
                }
                else
                    pending.Add((_ => { nodes++; return new FlowNodeDto(null, exitLabel, o.ClientName ?? "", "http", HttpActor(o), null, 0.5, 1, depth + 1, [], 0, "外部 gRPC 服务",
                        exitSite.Path, exitSite.Line, exitSite.Pos); }, o.Line, null));
            }
            // 没对上路由的 HTTP 出口：叶子节点，同一方法里相同 Label 的折成一条（Count 累加，位置取第一处）；分名额排在调用与续接之后
            var httpGroups = UnmatchedIn(s).OrderBy(o => o.Line).GroupBy(HttpLabel).ToList();
            foreach (var g in httpGroups.Take(fanout))
            {
                var o = g.First();
                var count = g.Count();
                var exitSite = At(o.File, o.Line, null);
                pending.Add((_ =>
                {
                    nodes++;
                    return new FlowNodeDto(null, g.Key, o.Url ?? o.ConfigKey ?? o.ClientName ?? "", "http", HttpActor(o), null, 0.5, count, depth + 1, [], 0, unmatchedNote,
                        exitSite.Path, exitSite.Line, exitSite.Pos);
                }, o.Line, null));
            }
            var hiddenCount = Math.Max(0, ordered.Count - fanout) + Math.Max(0, httpGroups.Count - fanout);
            // 两轮分名额：第一轮平均分（没用完的顺延给后面的兄弟）；第二轮把剩下的补给第一轮被截断的分支（通常是最深、最重要的那条）
            var remaining = budget - 1;
            var built = new (FlowNodeDto Node, int Share, int Used)[pending.Count];
            var regs = new (int From, int To)[pending.Count];
            for (var i = 0; i < pending.Count; i++)
            {
                var share = Math.Max(1, remaining / (pending.Count - i));
                var before = nodes;
                var mark = regLog.Count;
                var node = pending[i].Build(share);
                regs[i] = (mark, regLog.Count);
                built[i] = (node, share, nodes - before);
                remaining = Math.Max(0, remaining - built[i].Used);
            }
            // 第二轮只在前三层做：重新展开会递归，层层都做开销按层数翻倍；深层的平均分已经够用
            for (var round = 0; round < 2 && remaining > 0 && depth <= 2; round++)
            {
                // 被截断的分支都参与补名额。原来还要求"用满了自己的份额"：可它的子树在第三层以下按平均分时会剩下零头（没有第二轮），
                // 用量差一两个就不算"满"，最该补的那条（续接进别的服务的那条）反而拿不到剩下的名额
                var capped = Enumerable.Range(0, built.Length).Where(i => HasCut(built[i].Node)).ToList();
                if (capped.Count == 0) break;
                foreach (var i in capped)
                {
                    var extraShare = remaining / Math.Max(1, capped.Count - capped.IndexOf(i));
                    if (extraShare <= 0) break;
                    nodes -= built[i].Used;
                    var before = nodes;
                    // 新份额 = 它实际用掉的 + 补的：没用完的那部分第一轮已经还给 remaining 了，再按原份额加会重复计入、总数超过上限
                    var share = built[i].Used + extraShare;
                    Unregister(regs[i].From, regs[i].To); // 上次展开登记的作废，重新展开时重新登记
                    var mark = regLog.Count;
                    var node = pending[i].Build(share);
                    regs[i] = (mark, regLog.Count);
                    remaining = Math.Max(0, remaining + built[i].Used - (nodes - before));
                    built[i] = (node, share, nodes - before);
                }
            }
            // 选哪些、名额怎么分按 rank；显示按调用处在源码里的位置（时序图从上往下读）。hop 用对上路由的那一行，出口用出口行；
            // 位置相同（比如旧索引没有行号）时保持 rank 顺序。"还有 N 个"与"N 个框架调用"放最后
            children.AddRange(OutlineBuilder.SortBySite(Enumerable.Range(0, built.Length), i => pending[i].Line, i => pending[i].Pos, i => i).Select(i => built[i].Node));
            if (hiddenCount > 0)
                children.Add(new FlowNodeDto(null, $"还有 {hiddenCount} 个", "", "more", actor, null, 1.0, 1, depth + 1, [], hiddenCount, null));
            if (externals.Count > 0) children.AddRange(ExternalRows(externals, home, depth, SitePath));

            // 什么都没展开出来（只有自己）：不算"已展开"，别处再出现照常处理
            if (children.Count == 0 && ReferenceEquals(expandedBy.GetValueOrDefault(dupKey), token)) expandedBy.Remove(dupKey);
            return new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, Fold(children), 0, note, site.Path, site.Line, site.Pos);
        }

        if (!byId.TryGetValue(entry.SymbolId, out var entrySymbol))
            return new FlowDto(entry.SymbolId, entry.Route ?? "", entry.Route, entry.ContainerKey, [], new FlowNodeDto(null, "?", "", "call", "unknown", null, 0, 1, 0, [], 0, null), [], 0, false, "入口符号在索引里不存在（索引后被改掉了？）");

        var root = Expand(entrySymbol, "entry", null, 1.0, 0, [], container, maxNodes, At(entrySymbol.Path, entrySymbol.StartLine, null));
        var truncated = HasCut(root); // 先算：cut 按节点引用记，MarkRepeats 会换掉节点实例
        root = MarkRepeats(root);
        // 行为链：实现了 *Behavior 接口的类型（先按实现边建索引，不然是 类 × 边 的扫描，ccs-backend 上要好几秒）
        var behaviorImplementers = edges.Where(e => e.Kind == EdgeKind.Implements && byId.TryGetValue(e.ToId, out var i) && i.Name.EndsWith("Behavior", StringComparison.Ordinal))
            .Select(e => e.FromId).ToHashSet();
        var behaviors = behaviorImplementers.Select(id => byId.GetValueOrDefault(id)).Where(s => s is not null).Select(s => s!)
            .Where(s => s.Kind == SymbolKind.Class && !PipelineBuilder.IsTest(s.Path, s.QualifiedName) && container is not null && container.Members.Any(m => Under(s.Path, m)))
            .Select(s => s.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var entryLabel = entry.Route ?? entrySymbol.Name;
        // actor 顺序：入口所在 actor 第一，其余按最终那棵树里（显示顺序、先序）首次出现，外部最后。
        // 不再按展开时的登记顺序：展开按 rank、显示按源码位置，两者不同；第二轮补名额时丢掉的子树登记过的参与者也不再留空列
        var seen = new List<string>();
        var seenSet = new HashSet<string>(StringComparer.Ordinal);
        void Collect(FlowNodeDto n) { if (actors.ContainsKey(n.Actor) && seenSet.Add(n.Actor)) seen.Add(n.Actor); foreach (var c in n.Children) Collect(c); }
        Collect(root);
        var actorList = seen.Where(k => k == root.Actor)
            .Concat(seen.Where(k => k != root.Actor && actors[k].Kind != "external"))
            .Concat(seen.Where(k => k != root.Actor && actors[k].Kind == "external"))
            .Select(k => actors[k]).ToList();
        return new FlowDto(entry.SymbolId, entryLabel, entry.Route, entry.ContainerKey, actorList, root, behaviors, nodes, truncated, null);
    }

    private const string DupMarker = "dup";

    /// <summary>
    /// 重复出现的节点（展开时画成叶子、Note 带 <see cref="DupMarker"/>）按最终显示顺序（先序）写说明：完整展开的那个在它前面 →"上面已展开"，在后面 →"下面已展开"。
    /// 展开按 rank、显示按源码位置，所以完整展开的那个不一定在前面。键 = 符号 id + 列（同一符号在不同容器里展开的内容不同）。
    /// </summary>
    internal static FlowNodeDto MarkRepeats(FlowNodeDto root)
    {
        var order = 0;
        var fullAt = new Dictionary<(long, string), int>();
        void Index(FlowNodeDto n)
        {
            order++;
            if (n.SymbolId is { } id && n.Children.Count > 0) fullAt.TryAdd((id, n.Actor), order);
            foreach (var c in n.Children) Index(c);
        }
        Index(root);
        order = 0;
        FlowNodeDto Rewrite(FlowNodeDto n)
        {
            order++;
            if (n.Note is { } note && note.EndsWith(DupMarker, StringComparison.Ordinal))
            {
                var where = n.SymbolId is { } id && fullAt.TryGetValue((id, n.Actor), out var at) && at > order ? "下面" : "上面";
                return n with { Note = note[..^DupMarker.Length] + where + "已展开，这里不再重复" };
            }
            if (n.Children.Count == 0) return n;
            var kids = new List<FlowNodeDto>(n.Children.Count);
            foreach (var c in n.Children) kids.Add(Rewrite(c));
            return n with { Children = kids };
        }
        return Rewrite(root);
    }

    /// <summary>相邻且子树完全相同的兄弟合并，Count 累加。</summary>
    public static IReadOnlyList<FlowNodeDto> Fold(List<FlowNodeDto> children)
    {
        if (children.Count < 2) return children;
        var result = new List<FlowNodeDto>();
        foreach (var c in children)
        {
            if (result.Count > 0 && Digest(result[^1]) == Digest(c)) result[^1] = result[^1] with { Count = result[^1].Count + c.Count };
            else result.Add(c);
        }
        return result;
    }

    private static string Digest(FlowNodeDto n)
    {
        var sb = new StringBuilder();
        void Walk(FlowNodeDto x) { sb.Append(x.Kind).Append('|').Append(x.QualifiedName).Append('|').Append(x.Actor).Append('('); foreach (var c in x.Children) Walk(c); sb.Append(')'); }
        Walk(n);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static readonly HashSet<string> HttpVerbs = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    /// <summary>
    /// 出口节点的标签：前端出口（ClientName 是"方法[ 经代理 前缀]"）→ "GET /api/v5/mt5/groups"（后端看到的路径，剥掉代理前缀）；
    /// 其余（C# HttpClient 注册等，ClientName 是客户端名）→ "HTTP 地址或配置键"。
    /// </summary>
    private static string HttpLabel(Outbound o)
    {
        var (method, proxy) = FrontendHttpRule.ParseCall(o.ClientName);
        if (method is not null && HttpVerbs.Contains(method) && o.Url is not null) return $"{method} {FrontendHttpRule.BackendPath(o.Url, proxy)}";
        return "HTTP " + (o.Url ?? o.ConfigKey ?? o.ClientName ?? "?");
    }

    /// <summary>
    /// Go 方法的接收者类型名：接收者类型在别的文件时 ParentId 指向 Module，从限定名 &lt;pkgdir&gt;.&lt;RecvType&gt;.&lt;Method&gt; 的倒数第二段取。
    /// pkgdir 里可能有 '.'（v1.2），但方法的限定名最后两段总是 RecvType.Method；顶层函数（Kind Function）没有接收者。
    /// </summary>
    internal static string? GoReceiver(MapSymbol s)
    {
        if (s.Kind != SymbolKind.Method || !s.Path.EndsWith(".go", StringComparison.OrdinalIgnoreCase)) return null;
        var q = s.QualifiedName;
        var last = q.LastIndexOf('.');
        if (last <= 0) return null;
        var head = q[..last];
        var prev = Math.Max(head.LastIndexOf('.'), head.LastIndexOf('/'));
        var recv = head[(prev + 1)..].TrimStart('*');
        return recv.Length > 0 && (prev < 0 || head[prev] == '.') ? recv : null; // 根目录的包没有 pkgdir 段：T.M
    }

    // protoc 生成的桩：Go 的 *.pb.go / *_grpc.pb.go，Python 的 *_pb2.py / *_pb2_grpc.py
    private static bool IsGeneratedStub(string path) =>
        path.EndsWith(".pb.go", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_pb2.py", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_pb2_grpc.py", StringComparison.OrdinalIgnoreCase);

    private static bool IsHook(string name) => name.Length > 3 && name.StartsWith("use", StringComparison.Ordinal) && char.IsUpper(name[3]);

    // 与 RuleContext.Under 同一判据：成员路径为空（项目在主根）时不吞外部根的 ../… 文件
    private static bool Under(string rel, string dir) => Core.Architecture.Rules.RuleContext.Under(rel, dir);
}
