using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>一条 DI 注册（接口 → 实现），Order 是按（文件路径，行）排好的序号。</summary>
public sealed record MapDi(long InterfaceId, long ImplId, int Order);

public sealed record PipelineItemDto(string Name, string QualifiedName, long SymbolId, int Weight);

public sealed record PipelineCellDto(int Count, IReadOnlyList<PipelineItemDto> Items);

/// <param name="Orphans">没有任何非测试入口分发过的消息数。</param>
public sealed record PipelineMessagesDto(int Count, int Commands, int Queries, int Events, int Orphans);

public sealed record PipelineGroupDto(string Key, string Label, int Weight);

/// <param name="HasChildren">还能钻（命名空间行）；叶子行（消息行）为 false。</param>
/// <param name="SymbolId">叶子行对应的消息类型 id；命名空间行为 null。</param>
/// <param name="Nested">本行 Handler 发出的分发条数。</param>
/// <param name="Bypass">非 Handler、非测试代码直接调用本行 Handler 方法的条数（绕过管道）。</param>
public sealed record PipelineRowDto(
    string Key, string Label, bool HasChildren, long? SymbolId,
    PipelineCellDto Entries, PipelineMessagesDto Messages, PipelineCellDto Handlers,
    int Nested, int Bypass, int ExternalCalls, IReadOnlyList<PipelineGroupDto> Downstream);

/// <param name="Kind">nested（行 A 的 Handler 分发了行 B 的消息）或 bypass（行 A 的入口直接调了行 B 的 Handler）。</param>
public sealed record PipelineFlowDto(string From, string To, string Kind, int Weight);

public sealed record PipelineBehaviorDto(string Name, string QualifiedName, long SymbolId, int Order);

/// <param name="Root">实际使用的 root；传空时是自动推导的结果。没有分发数据时为空串且 Rows 为空。</param>
/// <param name="BehaviorsOrdered">每个 Behavior 都有 DI 注册记录时为 true；否则按名字排，顺序不可信。</param>
/// <param name="DefaultRoot">自动推导出的业务模块层（与传空时的 Root 相同）。面包屑以它为底：再往上只是项目 / 类库名，对管道没有意义。</param>
/// <param name="TestMessagesSkipped">测试代码里的消息（含其 Handler）被整体排除的个数。</param>
public sealed record PipelineDto(
    string Root, bool Leaf, bool BehaviorsOrdered, int TestEntriesSkipped, int MessagesOutsideRoot,
    IReadOnlyList<PipelineBehaviorDto> Behaviors, IReadOnlyList<PipelineRowDto> Rows, IReadOnlyList<PipelineFlowDto> Flows,
    string DefaultRoot = "", int TestMessagesSkipped = 0);

/// <summary>
/// 项目地图的管道视图：把符号按管道角色（入口 / 消息 / 行为 / Handler / 下游）分类，按消息所在命名空间分成泳道。
/// 只用现有的边：handles 定 Handler 与消息，dispatch:T 定入口与嵌套分发，implements 定行为，calls 定下游与绕过。
/// </summary>
public static partial class PipelineBuilder
{
    private const double AutoDescendShare = 0.9;
    private const int MaxItems = 20;
    private const int MaxDownstream = 5;
    private const string HandlesVia = "handles";
    private const string DispatchViaPrefix = "dispatch:";

    // Go：*_test.go；Python：test_*.py / *_test.py / conftest.py
    [GeneratedRegex(@"(^|[/.])(tests?|__tests__|spec|specs)([/.]|$)|\.(test|spec)\.[tj]sx?$|Tests?\.cs$|_test\.go$|(^|/)test_[^/]*\.py$|_test\.py$|(^|/)conftest\.py$", RegexOptions.IgnoreCase)]
    private static partial Regex TestPath();

    [GeneratedRegex(@"\.Tests?\.")]
    private static partial Regex TestQualifiedName();

    // Java：src/test/java/** 已由上面的 test 段规则覆盖；文件名 *Test.java / *Tests.java / *IT.java / *TestCase.java。
    // 单独一条区分大小写的规则、且后缀前须是小写字母或数字：Rabbit.java、Latest.java 不算（并进上面忽略大小写的那条会误伤它们）
    [GeneratedRegex(@"[a-z0-9_](Test|Tests|IT|TestCase)\.java$")]
    private static partial Regex JavaTestFile();

    [GeneratedRegex("Event|Notification|Consumer")]
    private static partial Regex EventInterface();

    /// <summary>与页面 isTestSymbol 同一条规则：路径或限定名里带 Tests/Test/Spec 段。</summary>
    public static bool IsTest(string path, string qualifiedName) =>
        TestPath().IsMatch(path.Replace('\\', '/')) || TestQualifiedName().IsMatch(qualifiedName) || JavaTestFile().IsMatch(path);

    public static PipelineDto Build(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, IReadOnlyList<MapDi> di, string root)
    {
        root = root.Trim().TrimEnd('.', '/');
        var byId = symbols.ToDictionary(s => s.Id);
        var keys = MapBuilder.GroupKeys(symbols);
        MapSymbol? TypeOf(long id) => byId.TryGetValue(id, out var s) ? MapBuilder.TypeAncestor(s, byId) : null;
        bool IsTestSymbol(MapSymbol s) => IsTest(s.Path, s.QualifiedName);

        // ---- Handler 与消息 ----
        var handlersOfMessage = new Dictionary<long, HashSet<long>>();   // 消息 → Handler 类型
        var messagesOfHandler = new Dictionary<long, HashSet<long>>();   // Handler 类型 → 消息
        var skippedTestMessages = new HashSet<long>();
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Dispatches || e.Via != HandlesVia) continue;
            if (TypeOf(e.FromId) is not { } handler || !byId.TryGetValue(e.ToId, out var message)) continue;
            // 测试项目里的夹具消息与 Handler 不是业务管道的一部分，整体排除，否则顶层会多出一条 Tests 泳道
            if (IsTestSymbol(handler) || IsTestSymbol(message)) { skippedTestMessages.Add(message.Id); continue; }
            Add(handlersOfMessage, e.ToId, handler.Id);
            Add(messagesOfHandler, handler.Id, e.ToId);
        }
        if (handlersOfMessage.Count == 0)
            return new PipelineDto(root, false, false, 0, 0, [], [], [], "", skippedTestMessages.Count);

        // ---- 接口：Handler 实现了什么、谁是 Behavior ----
        var interfacesOfType = new Dictionary<long, List<string>>();
        var behaviorTypes = new HashSet<long>();
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Implements || !byId.TryGetValue(e.ToId, out var iface)) continue;
            if (TypeOf(e.FromId) is not { } impl) continue;
            var ifaceName = StripGenerics(MapBuilder.LastSegment(StripGenerics(iface.QualifiedName)));
            if (!interfacesOfType.TryGetValue(impl.Id, out var list)) interfacesOfType[impl.Id] = list = [];
            list.Add(ifaceName);
            if (ifaceName.EndsWith("Behavior", StringComparison.Ordinal) && !IsTestSymbol(impl)) behaviorTypes.Add(impl.Id);
        }

        // ---- 分发：入口 / 嵌套 / 测试 ----
        var entryDispatches = new Dictionary<(long Entry, long Message), int>();
        var nestedDispatches = new List<(long Handler, long Message)>();
        var skippedTestTypes = new HashSet<long>();
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Dispatches || e.Via is null || !e.Via.StartsWith(DispatchViaPrefix, StringComparison.Ordinal)) continue;
            if (TypeOf(e.FromId) is not { } from || TypeOf(e.ToId) is not { } handler) continue;
            if (!messagesOfHandler.TryGetValue(handler.Id, out var handled)) continue;
            var messageName = e.Via[DispatchViaPrefix.Length..];
            var targets = handled.Where(m => MapBuilder.LastSegment(byId[m].QualifiedName) == messageName).ToList();
            if (targets.Count == 0) targets = handled.ToList();
            foreach (var m in targets)
            {
                if (messagesOfHandler.ContainsKey(from.Id)) nestedDispatches.Add((from.Id, m));
                else if (IsTestSymbol(from)) skippedTestTypes.Add(from.Id);
                else entryDispatches[(from.Id, m)] = entryDispatches.GetValueOrDefault((from.Id, m)) + 1;
            }
        }

        // ---- 行（泳道）----
        var messageKey = handlersOfMessage.Keys.ToDictionary(m => m, m => keys.GetValueOrDefault(m, ""));
        // 自动 root：公共前缀再按 ≥ 90% 下钻。下游分组截到与它同样的段数：root 是 Acme.App.Modules 时下游显示 Acme.App.Dtos / Acme.Infra.Repos 这一层
        var defaultRoot = AutoRoot(CommonPrefix(messageKey.Values), messageKey.Values);
        if (root.Length == 0) root = defaultRoot;
        var downstreamDepth = Math.Max(1, Segments(defaultRoot).Length);

        var rowOfMessage = new Dictionary<long, string>();
        var leaf = false;
        var outside = 0;
        foreach (var (m, key) in messageKey)
        {
            if (key == root) { rowOfMessage[m] = root; continue; }
            if (MapBuilder.ChildKey(root, key) is { } child) rowOfMessage[m] = child;
            else outside++;
        }
        if (rowOfMessage.Count > 0 && rowOfMessage.Values.All(k => k == root))
        {
            // root 就是消息所在的命名空间，没有更深一层：每个消息一行
            leaf = true;
            foreach (var m in rowOfMessage.Keys.ToList()) rowOfMessage[m] = byId[m].QualifiedName;
        }

        var rowKeys = rowOfMessage.Values.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var messagesOfRow = rowKeys.ToDictionary(k => k, _ => new List<long>());
        foreach (var (m, k) in rowOfMessage) messagesOfRow[k].Add(m);
        var rowsOfHandler = new Dictionary<long, HashSet<string>>();
        foreach (var (m, k) in rowOfMessage)
            foreach (var h in handlersOfMessage[m]) Add(rowsOfHandler, h, k);

        // ---- 绕过与下游（calls）----
        var bypassEdges = new List<(long FromType, long HandlerType)>();
        var downstreamOfHandler = new Dictionary<long, Dictionary<string, int>>();
        var externalOfHandler = new Dictionary<long, int>();
        foreach (var e in edges)
        {
            if (e.Kind != EdgeKind.Calls || e.Confidence < 0.4) continue;
            if (TypeOf(e.FromId) is not { } from) continue;
            if (!byId.TryGetValue(e.ToId, out var target)) continue;
            var fromIsHandler = messagesOfHandler.ContainsKey(from.Id);
            var targetType = target.Kind == SymbolKind.External ? null : TypeOf(e.ToId);
            if (targetType is not null && messagesOfHandler.ContainsKey(targetType.Id))
            {
                if (!fromIsHandler && !IsTestSymbol(from) && from.Id != targetType.Id) bypassEdges.Add((from.Id, targetType.Id));
                continue;
            }
            if (!fromIsHandler) continue;
            if (target.Kind == SymbolKind.External) { externalOfHandler[from.Id] = externalOfHandler.GetValueOrDefault(from.Id) + 1; continue; }
            if (targetType is null || !keys.TryGetValue(targetType.Id, out var ns)) continue;
            var group = Truncate(ns, downstreamDepth);
            if (!downstreamOfHandler.TryGetValue(from.Id, out var groups)) downstreamOfHandler[from.Id] = groups = new Dictionary<string, int>(StringComparer.Ordinal);
            groups[group] = groups.GetValueOrDefault(group) + 1;
        }

        // ---- 组装行 ----
        var rows = new List<PipelineRowDto>();
        foreach (var key in rowKeys)
        {
            var messages = messagesOfRow[key];
            var handlers = messages.SelectMany(m => handlersOfMessage[m]).Distinct().ToList();
            var messageSet = messages.ToHashSet();
            var handlerSet = handlers.ToHashSet();

            var entryWeights = new Dictionary<long, int>();
            foreach (var ((entry, m), n) in entryDispatches)
                if (messageSet.Contains(m)) entryWeights[entry] = entryWeights.GetValueOrDefault(entry) + n;
            var orphans = messages.Count(m => !entryDispatches.Keys.Any(k => k.Message == m));

            int commands = 0, queries = 0, events = 0;
            foreach (var m in messages)
            {
                switch (Category(byId[m], handlersOfMessage[m], interfacesOfType))
                {
                    case "query": queries++; break;
                    case "event": events++; break;
                    default: commands++; break;
                }
            }

            var handlerWeights = handlers.ToDictionary(h => h, h => messagesOfHandler[h].Count(messageSet.Contains));
            var nested = nestedDispatches.Count(n => handlerSet.Contains(n.Handler));
            var bypass = bypassEdges.Count(b => handlerSet.Contains(b.HandlerType));
            var downstream = new Dictionary<string, int>(StringComparer.Ordinal);
            var external = 0;
            foreach (var h in handlers)
            {
                external += externalOfHandler.GetValueOrDefault(h);
                if (downstreamOfHandler.TryGetValue(h, out var groups))
                    foreach (var (g, n) in groups) downstream[g] = downstream.GetValueOrDefault(g) + n;
            }

            var isLeaf = leaf;
            rows.Add(new PipelineRowDto(
                key, ShortName(key), !isLeaf, isLeaf ? messages[0] : null,
                Cell(entryWeights, byId), new PipelineMessagesDto(messages.Count, commands, queries, events, orphans), Cell(handlerWeights, byId),
                nested, bypass, external,
                downstream.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(MaxDownstream)
                    .Select(p => new PipelineGroupDto(p.Key, MapBuilder.LastSegment(p.Key), p.Value)).ToList()));
        }

        // ---- 跨行的流 ----
        var flows = new Dictionary<(string From, string To, string Kind), int>();
        foreach (var (handler, m) in nestedDispatches)
        {
            if (!rowsOfHandler.TryGetValue(handler, out var fromRows) || !rowOfMessage.TryGetValue(m, out var to)) continue;
            foreach (var from in fromRows) if (from != to) flows[(from, to, "nested")] = flows.GetValueOrDefault((from, to, "nested")) + 1;
        }
        var rowsOfEntry = new Dictionary<long, HashSet<string>>();
        foreach (var (entry, m) in entryDispatches.Keys)
            if (rowOfMessage.TryGetValue(m, out var r)) Add(rowsOfEntry, entry, r);
        foreach (var (fromType, handler) in bypassEdges)
        {
            if (!rowsOfEntry.TryGetValue(fromType, out var fromRows) || !rowsOfHandler.TryGetValue(handler, out var toRows)) continue;
            foreach (var from in fromRows) foreach (var to in toRows) if (from != to) flows[(from, to, "bypass")] = flows.GetValueOrDefault((from, to, "bypass")) + 1;
        }

        // ---- 行为链 ----
        var diOrder = di.Where(d => behaviorTypes.Contains(d.ImplId)).GroupBy(d => d.ImplId).ToDictionary(g => g.Key, g => g.Min(d => d.Order));
        var ordered = behaviorTypes.Count > 0 && behaviorTypes.All(diOrder.ContainsKey);
        var behaviors = behaviorTypes
            .OrderBy(b => diOrder.TryGetValue(b, out var o) ? o : int.MaxValue)
            .ThenBy(b => byId[b].QualifiedName, StringComparer.Ordinal)
            .Select((b, i) => new PipelineBehaviorDto(ShortName(byId[b].QualifiedName), byId[b].QualifiedName, b, i + 1))
            .ToList();

        return new PipelineDto(root, leaf, ordered, skippedTestTypes.Count, outside, behaviors, rows,
            flows.OrderBy(f => f.Key.Kind, StringComparer.Ordinal).ThenBy(f => f.Key.From, StringComparer.Ordinal).ThenBy(f => f.Key.To, StringComparer.Ordinal)
                .Select(f => new PipelineFlowDto(f.Key.From, f.Key.To, f.Key.Kind, f.Value)).ToList(),
            defaultRoot, skippedTestMessages.Count);
    }

    private static string Category(MapSymbol message, HashSet<long> handlers, Dictionary<long, List<string>> interfacesOfType)
    {
        var name = ShortName(message.QualifiedName);
        var ifaces = handlers.SelectMany(h => interfacesOfType.GetValueOrDefault(h) ?? []).ToList();
        if (ifaces.Any(i => i.Contains("Query", StringComparison.Ordinal)) || name.EndsWith("Query", StringComparison.Ordinal)) return "query";
        if (ifaces.Any(i => EventInterface().IsMatch(i)) || name.EndsWith("Event", StringComparison.Ordinal)) return "event";
        return "command";
    }

    private static PipelineCellDto Cell(Dictionary<long, int> weights, Dictionary<long, MapSymbol> byId) =>
        new(weights.Count, weights
            .OrderByDescending(p => p.Value).ThenBy(p => byId[p.Key].QualifiedName, StringComparer.Ordinal).Take(MaxItems)
            .Select(p => new PipelineItemDto(ShortName(byId[p.Key].QualifiedName), byId[p.Key].QualifiedName, p.Key, p.Value)).ToList());

    /// <summary>显示名：最后一段并去掉泛型参数（Roslyn 的限定名是 Ns.ValidationBehavior&lt;TRequest, TResponse&gt;）。</summary>
    private static string ShortName(string qualifiedName) => MapBuilder.LastSegment(StripGenerics(qualifiedName));

    /// <summary>消息命名空间的最长公共前缀（按段）。</summary>
    internal static string CommonPrefix(IEnumerable<string> keys)
    {
        string[]? prefix = null;
        var sep = '.';
        foreach (var k in keys)
        {
            var segs = Segments(k);
            if (prefix is null) { prefix = segs; sep = SeparatorOf(k); continue; }
            var n = 0;
            while (n < prefix.Length && n < segs.Length && prefix[n] == segs[n]) n++;
            prefix = prefix[..n];
        }
        return prefix is null ? "" : Join(prefix, sep);
    }

    /// <summary>从公共前缀出发，只要某一子行占了 ≥ 90% 的消息且它下面还有更深的消息命名空间，就继续下钻。</summary>
    private static string AutoRoot(string start, IEnumerable<string> messageKeys)
    {
        var keys = messageKeys.ToList();
        var root = start;
        while (true)
        {
            var children = new Dictionary<string, int>(StringComparer.Ordinal);
            var total = 0;
            foreach (var k in keys)
            {
                var child = k == root ? root : MapBuilder.ChildKey(root, k);
                if (child is null) continue;
                total++;
                children[child] = children.GetValueOrDefault(child) + 1;
            }
            if (total == 0) return root;
            var (big, n) = children.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            if (big == root || n < total * AutoDescendShare) return root;
            // 下面没有更深的消息命名空间了：钻进去只剩一列消息，停在这一层让人看到模块
            if (!keys.Any(k => MapBuilder.ChildKey(big, k) is not null)) return root;
            root = big;
        }
    }

    private static string Truncate(string key, int depth)
    {
        var segs = Segments(key);
        return segs.Length <= depth ? key : Join(segs[..depth], SeparatorOf(key));
    }

    // 目录名里的点（src/pkg/v1.2）不切，与 MapBuilder.ChildKey 同一判据
    private static string[] Segments(string key) => MapBuilder.KeySegments(key);

    /// <summary>脚本按目录分组的键用 '/'，C# 命名空间用 '.'。</summary>
    private static char SeparatorOf(string key) => key.Contains('/') ? '/' : '.';

    private static string Join(string[] segments, char sep) => string.Join(sep, segments);

    private static string StripGenerics(string name) => MapBuilder.StripGenericArgs(name);

    private static void Add<TKey, TValue>(Dictionary<TKey, HashSet<TValue>> dict, TKey key, TValue value) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var set)) dict[key] = set = [];
        set.Add(value);
    }
}
