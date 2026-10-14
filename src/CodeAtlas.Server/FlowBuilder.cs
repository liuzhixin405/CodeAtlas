using System.Security.Cryptography;
using System.Text;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <param name="Kind">entry / call / dispatch / hop / external-group / more / function。</param>
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
/// 从一个入口出发的有序调用树 → 时序图数据。走运行时去向：calls（含 via=impl 的接口 → 实现派生边）与 dispatch:T；
/// 不走 conduit / references / inherits / implements。深度 12、每节点 6 个分支、总 300 个节点；外部调用合并；相邻相同子树折叠；
/// 方法里对上另一容器路由的 HTTP 调用续接到对方入口。
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
        int maxDepth = DefaultMaxDepth, int fanout = DefaultFanout, int maxNodes = DefaultMaxNodes)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var childrenOf = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var outgoing = edges.Where(e => (e.Kind == EdgeKind.Calls && e.Confidence >= 0.4 && e.Via != "conduit")
                                      || (e.Kind == EdgeKind.Dispatches && e.Via is not null && !DispatchViaSkip.Contains(e.Via)))
            .ToLookup(e => e.FromId);
        var matchesByFile = matches.Where(m => m.FromContainerKey == entry.ContainerKey).ToLookup(m => m.File);
        var entriesById = allEntries.GroupBy(e => e.SymbolId).ToDictionary(g => g.Key, g => g.First());
        var container = containers.FirstOrDefault(c => c.Key == entry.ContainerKey);
        var actors = new Dictionary<string, FlowActorDto>(StringComparer.Ordinal);
        var nodes = 0;
        var truncated = false;

        // 方法显示成"类型.方法"：一屏里好几个 Handle / HandleAsync 时光看方法名分不清是谁的；构造函数只显示类型名
        string MemberLabel(MapSymbol s)
        {
            if (s.Kind is not (SymbolKind.Method or SymbolKind.Function) || s.ParentId is not { } pid || !byId.TryGetValue(pid, out var owner)) return s.Name;
            if (owner.Kind is not (SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface)) return s.Name;
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

        FlowNodeDto Expand(MapSymbol s, string kind, string? via, double confidence, int depth, HashSet<long> ancestors, ArchNode? home)
        {
            nodes++;
            var actor = ActorOf(s, home);
            var label = s.Kind == SymbolKind.Module ? MapBuilder.LastSegment(s.Path) : MemberLabel(s);
            if (depth >= maxDepth || nodes >= maxNodes || s.Kind == SymbolKind.External)
            {
                if (nodes >= maxNodes) truncated = true;
                return new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, [], 0, null);
            }

            var children = new List<FlowNodeDto>();
            var next = new HashSet<long>(ancestors) { s.Id };

            // 模块（前端页面）：子节点是模块里定义的函数
            if (s.Kind == SymbolKind.Module)
            {
                foreach (var f in childrenOf[s.Id].Where(c => c.Kind is SymbolKind.Function or SymbolKind.Class).OrderBy(c => c.StartLine).Take(fanout))
                    children.Add(Expand(f, "function", null, 1.0, depth + 1, next, home));
            }

            // 出边：分派优先，再容器内调用；外部合并成一个节点
            var ranked = new List<(MapEdge Edge, MapSymbol Target, int Rank)>();
            var externals = new List<MapSymbol>();
            foreach (var e in outgoing[s.Id])
            {
                if (!byId.TryGetValue(e.ToId, out var t) || next.Contains(t.Id)) continue;
                if (t.Kind == SymbolKind.External) { externals.Add(t); continue; }
                if (PipelineBuilder.IsTest(t.Path, t.QualifiedName)) continue;
                var rank = e.Kind == EdgeKind.Dispatches ? 100 : e.Via == "impl" ? 40 : 30;
                ranked.Add((e, t, rank));
            }
            var ordered = ranked.GroupBy(r => r.Target.Id).Select(g => g.OrderByDescending(r => r.Rank).First())
                .OrderByDescending(r => r.Rank).ThenBy(r => r.Target.QualifiedName, StringComparer.Ordinal).ToList();
            foreach (var (e, t, _) in ordered.Take(fanout))
            {
                var childKind = e.Kind == EdgeKind.Dispatches ? "dispatch" : "call";
                children.Add(Expand(t, childKind, e.Via, e.Confidence, depth + 1, next, home));
            }
            if (ordered.Count > fanout)
                children.Add(new FlowNodeDto(null, $"还有 {ordered.Count - fanout} 个", "", "more", actor, null, 1.0, 1, depth + 1, [], ordered.Count - fanout, null));
            if (externals.Count > 0)
            {
                var distinct = externals.DistinctBy(x => x.Id).ToList();
                var extActor = ActorOf(distinct[0], home);
                children.Add(new FlowNodeDto(null, distinct.Count == 1 ? distinct[0].Name : $"{distinct.Count} 个框架调用", string.Join(", ", distinct.Select(x => x.Name).Take(8)), "external-group", extActor, null, 1.0, 1, depth + 1, [], 0, null));
            }

            // 跨容器续接：这个方法里对上另一容器路由的 HTTP 调用
            foreach (var m in matchesByFile[s.Path].Where(m => m.Line >= s.StartLine && m.Line <= s.EndLine))
            {
                var target = containers.FirstOrDefault(c => c.Key == m.ToContainerKey);
                var targetEntry = entriesById.GetValueOrDefault(m.EntrySymbolId);
                var hopActorKey = target is null ? "unknown" : target.Key + "|" + (target.Members.FirstOrDefault() ?? "");
                if (target is not null) actors.TryAdd(hopActorKey, new FlowActorDto(hopActorKey, target.Name, "container"));
                var hopLabel = $"HTTP {m.Path}";
                if (targetEntry is not null && byId.TryGetValue(targetEntry.SymbolId, out var es) && !next.Contains(es.Id) && depth + 1 < maxDepth)
                {
                    var inner = Expand(es, "call", null, 1.0, depth + 2, next, target);
                    children.Add(new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [inner], 0, $"→ {target?.Name} {targetEntry.Route}"));
                }
                else children.Add(new FlowNodeDto(null, hopLabel, m.Path, "hop", hopActorKey, null, EdgeMatcher.ByRoute, 1, depth + 1, [], 0, target is null ? "目标容器不存在" : $"→ {target.Name}（没有索引到符号，停在这里）"));
            }

            return new FlowNodeDto(s.Id, label, s.QualifiedName, kind, actor, via, confidence, 1, depth, Fold(children), 0, null);
        }

        if (!byId.TryGetValue(entry.SymbolId, out var entrySymbol))
            return new FlowDto(entry.SymbolId, entry.Route ?? "", entry.Route, entry.ContainerKey, [], new FlowNodeDto(null, "?", "", "call", "unknown", null, 0, 1, 0, [], 0, null), [], 0, false, "入口符号在索引里不存在（索引后被改掉了？）");

        var root = Expand(entrySymbol, "entry", null, 1.0, 0, [], container);
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

    private static bool Under(string rel, string dir) => dir.Length == 0 || rel == dir || rel.StartsWith(dir + "/", StringComparison.Ordinal);
}
