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
public sealed record FlowNodeDto(
    long? SymbolId, string Label, string QualifiedName, string Kind, string Actor, string? Via, double Confidence,
    int Count, int Depth, IReadOnlyList<FlowNodeDto> Children, int Hidden, string? Note);

/// <param name="Kind">project（容器内成员项目）/ container（另一个容器）/ external。</param>
public sealed record FlowActorDto(string Key, string Label, string Kind);

public sealed record FlowDto(
    long EntrySymbolId, string EntryLabel, string? Route, string ContainerKey, IReadOnlyList<FlowActorDto> Actors, FlowNodeDto Root,
    IReadOnlyList<string> Behaviors, int Nodes, bool Truncated, string? Note);

/// <summary>
/// 从一个入口出发的有序调用树 → 时序图数据。走运行时去向：calls（含 via=impl 的接口 → 实现派生边、via=render 的 JSX 组件渲染边）与 dispatch:T；
/// 不走 conduit / references / inherits / implements。深度 12、每节点 6 个分支、总 300 个节点；外部调用合并；相邻相同子树折叠；
/// 方法里对上另一容器路由的 HTTP 调用续接到对方入口；没对上的 HTTP 出口（容器图规则识别的，比如单独打开前端时后端不在索引里）画成 http 叶子。
/// </summary>
public static class FlowBuilder
{
    public const int DefaultMaxDepth = 12;
    public const int DefaultFanout = 6;
    public const int DefaultMaxNodes = 300;
    private static readonly HashSet<string> DispatchViaSkip = ["handles", "conduit"];

    public static FlowDto Build(
        IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, EntryPoint entry, IReadOnlyList<ArchNode> containers,
        IReadOnlyList<RouteMatch> matches, IReadOnlyList<EntryPoint> allEntries,
        int maxDepth = DefaultMaxDepth, int fanout = DefaultFanout, int maxNodes = DefaultMaxNodes,
        IReadOnlyList<Outbound>? outbound = null, IReadOnlyList<ArchNode>? externalNodes = null)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var childrenOf = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var outgoing = edges.Where(e => (e.Kind == EdgeKind.Calls && e.Confidence >= 0.4 && e.Via != "conduit")
                                      || (e.Kind == EdgeKind.Dispatches && e.Via is not null && !DispatchViaSkip.Contains(e.Via)))
            .ToLookup(e => e.FromId);
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
            if (s.ParentId is not { } pid || !byId.TryGetValue(pid, out var owner) || owner.Kind is not (SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum))
                return GoReceiver(s) is { } recv ? recv + "." + s.Name : s.Name;
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

        // budget：这棵子树（含自己）最多能用的节点数。名额在兄弟分支之间平均分，先展开的分支没用完的顺延给后面的——
        // 不然深度优先会让第一条分支吃光 300 个名额（组管理页合并后端后，MT4 那支续接进后端把 MT5 那支挤没了）
        FlowNodeDto Expand(MapSymbol s, string kind, string? via, double confidence, int depth, HashSet<long> ancestors, ArchNode? home, int budget)
        {
            nodes++;
            var actor = ActorOf(s, home);
            // 模块显示文件名（page.tsx），不能按 '.' 切成 "tsx"
            var label = s.Kind == SymbolKind.Module ? MapSymbol.FileName(s.Path) : MemberLabel(s);
            if (depth >= maxDepth || budget <= 1 || s.Kind == SymbolKind.External)
            {
                var leaf = new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, [], 0, null);
                // 名额用完、其实还能往下走的节点记下来：截断标志按最终那棵树算（第二轮补名额后可能已经展开了）
                if (budget <= 1 && s.Kind != SymbolKind.External && depth < maxDepth && (outgoing[s.Id].Any() || HopsIn(s, home).Any() || UnmatchedIn(s).Any())) cut.Add(leaf);
                return leaf;
            }

            var children = new List<FlowNodeDto>();
            var next = new HashSet<long>(ancestors) { s.Id };
            // 待展开的子分支：先收齐，再按名额逐个展开
            var pending = new List<Func<int, FlowNodeDto>>();

            // 模块（前端页面）：子节点是模块里定义的函数
            if (s.Kind == SymbolKind.Module)
            {
                foreach (var f in childrenOf[s.Id].Where(c => c.Kind is SymbolKind.Function or SymbolKind.Class).OrderBy(c => c.StartLine).Take(fanout))
                    pending.Add(b => Expand(f, "function", null, 1.0, depth + 1, next, home, b));
            }

            // 出边：分派优先，再容器内调用；外部合并成一个节点
            var ranked = new List<(MapEdge Edge, MapSymbol Target, int Rank)>();
            var externals = new List<MapSymbol>();
            // 这个方法里有 gRPC 续接时，生成的客户端桩（*.pb.go 里的 XxxClient.Method / NewXxxClient）就是那次 RPC 本身，由 hop 节点表示
            var grpcExits = GrpcExitsIn(s, home).ToList();
            var hasGrpcHop = grpcExits.Count > 0 || HopsIn(s, home).Any(m => m.Path.StartsWith(RouteLinker.GrpcPathPrefix, StringComparison.Ordinal));
            foreach (var e in outgoing[s.Id])
            {
                if (!byId.TryGetValue(e.ToId, out var t) || next.Contains(t.Id)) continue;
                if (t.Kind == SymbolKind.External) { externals.Add(t); continue; }
                if (PipelineBuilder.IsTest(t.Path, t.QualifiedName)) continue;
                if (hasGrpcHop && IsGeneratedStub(t.Path)) continue;
                // React hook（useXxx）是组件的数据 / 行为来源，排在子组件与局部函数前面，免得被扇出上限折进"还有 N 个"；
                // 自己就续接到另一个容器的方法（getProducts → gRPC）也排前面——跨服务的那一步是时序图最该看到的
                // 下一层才续接的（chooseAd → getAd → gRPC）紧随其后
                var rank = e.Kind == EdgeKind.Dispatches ? 100 : e.Via == "impl" ? 40 : IsHook(t.Name) ? 35 : HopsIn(t, home).Any() ? 33 : HopsBelow(t, home) ? 32 : 30;
                ranked.Add((e, t, rank));
            }
            var ordered = ranked.GroupBy(r => r.Target.Id).Select(g => g.OrderByDescending(r => r.Rank).First())
                .OrderByDescending(r => r.Rank).ThenBy(r => r.Target.QualifiedName, StringComparer.Ordinal).ToList();
            foreach (var (e, t, _) in ordered.Take(fanout))
            {
                var childKind = e.Kind == EdgeKind.Dispatches ? "dispatch" : "call";
                pending.Add(b => Expand(t, childKind, e.Via, e.Confidence, depth + 1, next, home, b));
            }
            // 跨容器续接：这个方法里对上另一容器路由的 HTTP 调用（也参与分名额）
            foreach (var m in HopsIn(s, home))
            {
                var target = containers.FirstOrDefault(c => c.Key == m.ToContainerKey);
                var targetEntry = entriesById.GetValueOrDefault(m.EntrySymbolId);
                var hopActorKey = target is null ? "unknown" : target.Key + "|" + (target.Members.FirstOrDefault() ?? "");
                var hopLabel = m.Path.StartsWith(RouteLinker.GrpcPathPrefix, StringComparison.Ordinal) ? $"gRPC {m.Path[RouteLinker.GrpcPathPrefix.Length..]}" : $"HTTP {m.Path}";
                // 参与者在真正展开时才登记，保持"按遍历中首次出现"排序
                void AddHopActor() { if (target is not null) actors.TryAdd(hopActorKey, new FlowActorDto(hopActorKey, target.Name, "container")); }
                if (targetEntry is not null && byId.TryGetValue(targetEntry.SymbolId, out var es) && !next.Contains(es.Id) && depth + 1 < maxDepth)
                    pending.Add(b => { AddHopActor(); return new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1,
                        [Expand(es, "call", null, 1.0, depth + 2, next, target, b)], 0, $"→ {target?.Name} {targetEntry.Route}"); });
                else
                    pending.Add(_ => { AddHopActor(); return new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [], 0,
                        target is null ? "目标容器不存在" : $"→ {target.Name}（没有索引到符号，停在这里）"); });
            }
            foreach (var o in grpcExits)
            {
                var exitLabel = "gRPC " + (o.ClientName ?? o.TargetKey);
                if (containers.FirstOrDefault(c => c.Key == o.TargetKey && c.Kind == ArchNodeKind.Container) is { } target)
                {
                    var hopActorKey = target.Key + "|" + (target.Members.FirstOrDefault() ?? "");
                    pending.Add(_ =>
                    {
                        nodes++;
                        actors.TryAdd(hopActorKey, new FlowActorDto(hopActorKey, target.Name, "container"));
                        return new FlowNodeDto(null, exitLabel, o.ClientName ?? "", "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [], 0, $"→ {target.Name}（没有索引到符号，停在这里）");
                    });
                }
                else
                    pending.Add(_ => { nodes++; return new FlowNodeDto(null, exitLabel, o.ClientName ?? "", "http", HttpActor(o), null, 0.5, 1, depth + 1, [], 0, "外部 gRPC 服务"); });
            }
            // 没对上路由的 HTTP 出口：叶子节点，同一方法里相同 Label 的折成一条（Count 累加）；排在调用与续接之后，也参与分名额
            var httpGroups = UnmatchedIn(s).OrderBy(o => o.Line).GroupBy(HttpLabel).ToList();
            foreach (var g in httpGroups.Take(fanout))
            {
                var o = g.First();
                var count = g.Count();
                pending.Add(_ =>
                {
                    nodes++;
                    return new FlowNodeDto(null, g.Key, o.Url ?? o.ConfigKey ?? o.ClientName ?? "", "http", HttpActor(o), null, 0.5, count, depth + 1, [], 0, unmatchedNote);
                });
            }
            var hiddenCount = Math.Max(0, ordered.Count - fanout) + Math.Max(0, httpGroups.Count - fanout);
            // 两轮分名额：第一轮平均分（没用完的顺延给后面的兄弟）；第二轮把剩下的补给第一轮被截断的分支（通常是最深、最重要的那条）
            var remaining = budget - 1;
            var built = new (FlowNodeDto Node, int Share, int Used)[pending.Count];
            for (var i = 0; i < pending.Count; i++)
            {
                var share = Math.Max(1, remaining / (pending.Count - i));
                var before = nodes;
                var node = pending[i](share);
                built[i] = (node, share, nodes - before);
                remaining = Math.Max(0, remaining - built[i].Used);
            }
            // 第二轮只在前三层做：重新展开会递归，层层都做开销按层数翻倍；深层的平均分已经够用
            for (var round = 0; round < 2 && remaining > 0 && depth <= 2; round++)
            {
                var capped = Enumerable.Range(0, built.Length).Where(i => built[i].Used >= built[i].Share && HasCut(built[i].Node)).ToList();
                if (capped.Count == 0) break;
                foreach (var i in capped)
                {
                    var extra = remaining / Math.Max(1, capped.Count - capped.IndexOf(i));
                    if (extra <= 0) break;
                    nodes -= built[i].Used;
                    var before = nodes;
                    var share = built[i].Share + extra;
                    var node = pending[i](share);
                    remaining = Math.Max(0, remaining + built[i].Used - (nodes - before));
                    built[i] = (node, share, nodes - before);
                }
            }
            children.AddRange(built.Select(b => b.Node));
            if (hiddenCount > 0)
                children.Add(new FlowNodeDto(null, $"还有 {hiddenCount} 个", "", "more", actor, null, 1.0, 1, depth + 1, [], hiddenCount, null));
            if (externals.Count > 0)
            {
                var distinct = externals.DistinctBy(x => x.Id).ToList();
                var extActor = ActorOf(distinct[0], home);
                children.Add(new FlowNodeDto(null, distinct.Count == 1 ? distinct[0].Name : $"{distinct.Count} 个框架调用", string.Join(", ", distinct.Select(x => x.Name).Take(8)), "external-group", extActor, null, 1.0, 1, depth + 1, [], 0, null));
            }

            return new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, Fold(children), 0, null);
        }

        if (!byId.TryGetValue(entry.SymbolId, out var entrySymbol))
            return new FlowDto(entry.SymbolId, entry.Route ?? "", entry.Route, entry.ContainerKey, [], new FlowNodeDto(null, "?", "", "call", "unknown", null, 0, 1, 0, [], 0, null), [], 0, false, "入口符号在索引里不存在（索引后被改掉了？）");

        var root = Expand(entrySymbol, "entry", null, 1.0, 0, [], container, maxNodes);
        var truncated = HasCut(root);
        // 行为链：实现了 *Behavior 接口的类型（先按实现边建索引，不然是 类 × 边 的扫描，ccs-backend 上要好几秒）
        var behaviorImplementers = edges.Where(e => e.Kind == EdgeKind.Implements && byId.TryGetValue(e.ToId, out var i) && i.Name.EndsWith("Behavior", StringComparison.Ordinal))
            .Select(e => e.FromId).ToHashSet();
        var behaviors = behaviorImplementers.Select(id => byId.GetValueOrDefault(id)).Where(s => s is not null).Select(s => s!)
            .Where(s => s.Kind == SymbolKind.Class && !PipelineBuilder.IsTest(s.Path, s.QualifiedName) && container is not null && container.Members.Any(m => Under(s.Path, m)))
            .Select(s => s.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var entryLabel = entry.Route ?? entrySymbol.Name;
        // actor 顺序：入口所在 actor 第一，其余按首次出现（字典插入序），外部最后
        var actorList = actors.Values.Where(a => a.Kind != "external").Concat(actors.Values.Where(a => a.Kind == "external")).ToList();
        return new FlowDto(entry.SymbolId, entryLabel, entry.Route, entry.ContainerKey, actorList, root, behaviors, nodes, truncated, null);
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
