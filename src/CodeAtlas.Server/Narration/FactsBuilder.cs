using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeAtlas.Server.Narration;

/// <param name="Title">给人看的对象名（容器名 / 组件标签 / 入口路由）。</param>
/// <param name="Text">给模型的事实文本，每条事实一行，带 id 的以 "[id] " 开头。</param>
/// <param name="Ids">文本里出现的全部事实 id：模型的 evidence 必须落在这里面。</param>
/// <param name="Omitted">超预算被省掉的事实条数。</param>
/// <remarks>
/// 缓存相关的三样（重建索引、换模型都不该让本地慢慢算出来的叙述作废）：
/// StableKey —— 跨索引不变的对象键（流程用入口方法全名，不用会变的符号 id）；
/// Fingerprint —— 架构事实的指纹：id 换成稳定形式、git 计数之类每次提交都变的数字不进来；
/// Sources —— 对象覆盖的文件 → 内容哈希，用来说"生成后 N 个相关文件有改动"。
/// </remarks>
public sealed record Facts(string Kind, string Key, string Title, string Text, IReadOnlySet<string> Ids, int Omitted)
{
    /// <summary>算指纹用的文本：默认同 Text；FactWriter 会把易变的片段换掉。</summary>
    public string FingerprintText { get; init; } = Text;

    public string StableKey { get; init; } = Key;

    /// <summary>当前 id → 稳定 id（sym:123 → sym@Acme.Ns.Type.Method(int)）。只收会随索引变的 entry: / sym:。</summary>
    public IReadOnlyDictionary<string, string> StableIds { get; init; } = new Dictionary<string, string>();

    /// <summary>对象覆盖的文件 → 内容哈希（索引里的 files.hash）。</summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>架构事实指纹：与模型无关；当前 id 先换成稳定 id，免得全量重建索引后符号 id 变了就判过期。</summary>
    public string Fingerprint
    {
        get
        {
            var text = FingerprintText;
            if (StableIds.Count > 0)
                text = IdToken().Replace(text, m => StableIds.TryGetValue(m.Groups[1].Value, out var s) ? "[" + s + "]" : m.Value);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Kind + "\n" + StableKey + "\n" + text)));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex IdTokenRegex = new(@"\[((?:sym|entry):\d+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static System.Text.RegularExpressions.Regex IdToken() => IdTokenRegex;
}

/// <summary>
/// 三种叙述对象的事实子图 → 给模型的紧凑文本。不给源码全文（入口流程只给入口方法本身，最多 80 行）。
/// 预算约 6000 token（按 3.3 字符 / token 估成 20000 字符）：每条事实带优先级，超预算先丢低优先级的，末尾注明省略数。
/// </summary>
public static class FactsBuilder
{
    public const int BudgetChars = 20_000;
    public const int MaxEntries = 20;
    public const int MaxFlowDepth = 4;
    public const int MaxSourceLines = 80;

    /// <summary>容器：节点本身、它的边（协议 / 置信度 / 证据文件）、入口计数与前 20 条入口、成员项目。</summary>
    public static Facts Container(ArchDto arch, ArchNodeDto node, IReadOnlyList<EntryDto> entries)
    {
        var w = new FactWriter();
        var names = arch.Nodes.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);
        w.Line(10, $"对象：容器 {node.Name}（键 {node.Key}）", null);
        w.Line(10, $"类型 {node.Kind}/{node.SubKind}，来源 {node.Source}，路径 {node.Path ?? "-"}，端口 [{string.Join(",", node.Ports)}]，成员项目 {node.Members.Count} 个，出口登记 {node.Outbound} 处", $"node:{node.Key}");
        foreach (var m in node.Members) w.Line(6, $"  成员项目目录：{(m.Length == 0 ? "（仓库根）" : m)}", null);
        foreach (var e in node.Evidence.Take(3)) w.Line(4, $"  判定证据：{e.File}{(e.Line is { } l ? ":" + l : "")}{(e.Note is null ? "" : " " + e.Note)}", null);

        var outgoing = arch.Edges.Where(e => e.From == node.Key).ToList();
        var incoming = arch.Edges.Where(e => e.To == node.Key).ToList();
        w.Line(9, $"出边 {outgoing.Count} 条（本容器调用别人），入边 {incoming.Count} 条（别人调用本容器）", null);
        foreach (var e in outgoing.OrderByDescending(e => e.Confidence).ThenBy(e => e.To, StringComparer.Ordinal))
            w.Line(8, EdgeLine("→", e, names.GetValueOrDefault(e.To)), $"edge:{e.From}→{e.To}:{e.Protocol}");
        foreach (var e in incoming.OrderByDescending(e => e.Confidence).ThenBy(e => e.From, StringComparer.Ordinal))
            w.Line(8, EdgeLine("←", e, names.GetValueOrDefault(e.From)), $"edge:{e.From}→{e.To}:{e.Protocol}");
        // 对端节点：模型要提到它们时有 id 可引
        foreach (var other in outgoing.Select(e => e.To).Concat(incoming.Select(e => e.From)).Distinct(StringComparer.Ordinal).Where(k => k != node.Key))
            if (names.TryGetValue(other, out var o)) w.Line(5, $"相关节点 {o.Name}：{o.Kind}/{o.SubKind}，来源 {o.Source}", $"node:{o.Key}");

        var byKind = entries.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        w.Line(9, $"入口 {entries.Count} 个（{string.Join("，", byKind)}）", null);
        foreach (var e in entries.Take(MaxEntries))
            w.Line(7, $"  入口 {e.Kind} {e.HttpMethod ?? ""} {e.Route ?? e.Name}  方法 {e.QualifiedName}  {e.File}:{e.Line}", $"entry:{e.SymbolId}");
        if (entries.Count > MaxEntries) w.Line(7, $"  （其余 {entries.Count - MaxEntries} 个入口略）", null);
        foreach (var c in arch.Conflicts.Where(c => c.Message.Contains(node.Name, StringComparison.Ordinal) || c.Message.Contains(node.Key, StringComparison.Ordinal)))
            w.Line(6, $"声明与发现的冲突 [{c.Kind}]：{c.Message}", null);
        return w.Build(NarrativePresets.Container, node.Key, node.Name);
    }

    private static string EdgeLine(string arrow, ArchEdgeDto e, ArchNodeDto? other)
    {
        var otherName = other?.Name ?? (arrow == "→" ? e.To : e.From);
        var first = e.Evidence.Count > 0 ? e.Evidence[0] : null;
        return $"  {arrow} {otherName}  协议 {e.Protocol}  置信度 {e.Confidence.ToString("0.0", CultureInfo.InvariantCulture)}  来源 {e.Source}"
             + (e.ConfigKey is null ? "" : $"  配置键 {e.ConfigKey}") + (e.Url is null ? "" : $"  地址 {e.Url}")
             + (first is null ? "" : $"  证据 {first.File}{(first.Line is { } l ? ":" + l : "")}");
    }

    /// <summary>组件：该 DSM 行 / 列的依赖数、是否在环里、热点、隐性耦合。key 形如 "&lt;容器键&gt;|&lt;组件键&gt;"。</summary>
    public static Facts Component(DsmDto dsm, DsmComponentDto comp, string key, string containerName)
    {
        var w = new FactWriter();
        var index = dsm.Components.ToList().IndexOf(comp);
        var inCycle = dsm.Cycles.FirstOrDefault(c => c.Contains(comp.Key));
        w.Line(10, $"对象：组件 {comp.Label}（键 {comp.Key}），属于容器 {containerName}，层级 root=\"{dsm.Root}\"，同层共 {dsm.Components.Count} 个组件，依赖序第 {index + 1} 位（越靠前被依赖越多）", null);
        w.Line(10, $"符号 {comp.Symbols} 个，文件 {comp.Files} 个，{(comp.HasChildren ? "还能往下钻" : "没有更深的层级")}，{(inCycle is null ? "不在环里" : "在环里")}"
                 + (comp.Hot is null ? "，没有 git 统计" : $"，git：修订 {comp.Hot.Revisions} 次，+{comp.Hot.Added}/-{comp.Hot.Deleted} 行，最近提交 {comp.Hot.LastCommit}（{comp.Hot.AgeMonths} 个月前）"), $"comp:{comp.Key}",
            // 指纹里不放 git 计数：每次提交都变，会让叙述动不动就"过期"；代码真改了由 Sources 判
            fingerprint: $"符号 {comp.Symbols} 个，文件 {comp.Files} 个，{(comp.HasChildren ? "还能往下钻" : "没有更深的层级")}，{(inCycle is null ? "不在环里" : "在环里")}");

        var outCells = dsm.Cells.Where(c => c.Row == index).OrderByDescending(c => c.Count).ToList();
        var inCells = dsm.Cells.Where(c => c.Col == index).OrderByDescending(c => c.Count).ToList();
        w.Line(9, $"依赖出去 {outCells.Count} 个组件（共 {outCells.Sum(c => c.Count)} 条边），被 {inCells.Count} 个组件依赖（共 {inCells.Sum(c => c.Count)} 条边）", null);
        foreach (var c in outCells)
        {
            var to = dsm.Components[c.Col];
            w.Line(8, $"  依赖 → {to.Label}  {c.Count} 条（{Kinds(c.Kinds)}）{(c.Col > index ? "  ⚠ 反向：依赖了排在自己后面的组件" : "")}", $"cell:{comp.Key}→{to.Key}");
        }
        foreach (var c in inCells)
        {
            var from = dsm.Components[c.Row];
            w.Line(7, $"  被依赖 ← {from.Label}  {c.Count} 条（{Kinds(c.Kinds)}）", $"cell:{from.Key}→{comp.Key}");
        }
        if (inCycle is not null)
            w.Line(9, $"环成员：{string.Join(" ↔ ", inCycle.Select(k => dsm.Components.FirstOrDefault(x => x.Key == k)?.Label ?? k))}", null);
        foreach (var cp in dsm.Coupling.Where(cp => cp.A == index || cp.B == index).OrderByDescending(cp => cp.Shared))
        {
            var other = dsm.Components[cp.A == index ? cp.B : cp.A];
            w.Line(7, $"隐性耦合 ~ {other.Label}：共同修订 {cp.Shared} 次，耦合度 {cp.Degree.ToString("0.00", CultureInfo.InvariantCulture)}，但没有静态依赖边", $"comp:{other.Key}",
                fingerprint: $"隐性耦合 ~ {other.Label}");
        }
        // 提到的邻居组件给个简介，让模型能引
        foreach (var n in outCells.Select(c => dsm.Components[c.Col]).Concat(inCells.Select(c => dsm.Components[c.Row])).DistinctBy(c => c.Key))
            w.Line(5, $"邻居组件 {n.Label}：符号 {n.Symbols}，文件 {n.Files}{(n.Hot is null ? "" : $"，修订 {n.Hot.Revisions} 次")}{(dsm.Cycles.Any(c => c.Contains(n.Key)) ? "，在环里" : "")}", $"comp:{n.Key}",
                fingerprint: $"邻居组件 {n.Label}：符号 {n.Symbols}，文件 {n.Files}{(dsm.Cycles.Any(c => c.Contains(n.Key)) ? "，在环里" : "")}");
        if (dsm.Note is not null) w.Line(4, $"备注：{dsm.Note}", null);
        if (!dsm.GitAvailable) w.Line(4, "git 统计不可用（热点与隐性耦合为空）", null);
        return w.Build(NarrativePresets.Component, key, comp.Label);
    }

    private static string Kinds(Dictionary<string, int> kinds) => string.Join("、", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));

    /// <summary>流程：FlowDto 树（深度 ≤ 4）、参与者、行为链、入口方法源码（最多 80 行）。</summary>
    public static Facts Flow(FlowDto flow, EntryDto? entry, string containerName, IReadOnlyList<string>? sourceLines, int sourceStartLine)
    {
        var w = new FactWriter();
        var actors = flow.Actors.ToDictionary(a => a.Key, a => a, StringComparer.Ordinal);
        var key = flow.EntrySymbolId.ToString(CultureInfo.InvariantCulture);
        w.Line(10, $"对象：入口流程 {flow.EntryLabel}，容器 {containerName}" + (entry is null ? "" : $"，类型 {entry.Kind}{(entry.HttpMethod is null ? "" : " " + entry.HttpMethod)}，方法 {entry.QualifiedName}，位置 {entry.File}:{entry.Line}"), $"entry:{key}");
        w.Line(9, $"调用树节点 {flow.Nodes} 个{(flow.Truncated ? "（已按预算截断）" : "")}，参与者 {flow.Actors.Count} 个：{string.Join("，", flow.Actors.Select(a => $"{a.Label}[{a.Kind}]"))}", null);
        if (flow.Behaviors.Count > 0) w.Line(8, $"管道行为（每次分派都会经过）：{string.Join("、", flow.Behaviors)}", null);
        if (flow.Note is not null) w.Line(8, $"备注：{flow.Note}", null);

        w.Line(9, "调用树（缩进 = 深度；[actor] 是所在参与者；via 是边的依据；数字是置信度）：", null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(FlowNodeDto n)
        {
            if (n.Depth > MaxFlowDepth) return;
            var pad = new string(' ', 2 + n.Depth * 2);
            var actor = actors.GetValueOrDefault(n.Actor)?.Label ?? n.Actor;
            var via = n.Via is null ? "" : $" via {n.Via}";
            var count = n.Count > 1 ? $" ×{n.Count}" : "";
            var note = n.Note is null ? "" : $"  {n.Note}";
            // 根节点就是入口本身，已有 entry:<id>，不再给 sym:<id>，免得模型把同一个东西当两条证据
            var id = n.SymbolId is { } sid && n.Depth > 0 && n.Kind is not ("external-group" or "more" or "hop") ? $"sym:{sid}" : null;
            // 同一符号在树里出现多次只给一次 id（文本里重复的 id 会让集合去重，但别让模型以为是两条事实）
            if (id is not null && !seen.Add(id)) id = null;
            var label = n.Kind == "more" ? $"（还有 {n.Hidden} 个分支折叠）" : n.Kind == "external-group" ? $"外部 / 框架调用：{n.QualifiedName}" : $"{n.Label}  {n.QualifiedName}";
            var priority = n.Depth switch { 0 => 9, 1 => 8, 2 => 7, 3 => 6, _ => 5 };
            w.Line(priority, $"{pad}{label}  [{actor}]  {n.Kind}{via} {n.Confidence.ToString("0.0", CultureInfo.InvariantCulture)}{count}{note}", id);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(flow.Root);
        if (Deeper(flow.Root)) w.Line(5, $"  （深度超过 {MaxFlowDepth} 的分支略）", null);

        if (sourceLines is { Count: > 0 })
        {
            var shown = sourceLines.Take(MaxSourceLines).ToList();
            w.Line(6, $"入口方法源码（{(entry?.File ?? "")} 第 {sourceStartLine} 行起，共 {sourceLines.Count} 行{(sourceLines.Count > MaxSourceLines ? $"，只给前 {MaxSourceLines} 行" : "")}）：", null);
            w.Block(3, "```\n" + string.Join("\n", shown) + "\n```");
        }
        return w.Build(NarrativePresets.Flow, key, flow.EntryLabel);
    }

    private static bool Deeper(FlowNodeDto n) => n.Depth > MaxFlowDepth || n.Children.Any(Deeper);

    /// <summary>按优先级装进预算：高优先级先占位，然后按原顺序输出。</summary>
    private sealed class FactWriter
    {
        private readonly List<(int Priority, string Text, string? Id, int Order, string Fingerprint)> _lines = [];

        /// <param name="fingerprint">算缓存指纹时用的文本（去掉 git 计数这类每次提交都变的数字）；null 时同 text。</param>
        public void Line(int priority, string text, string? id, string? fingerprint = null)
        {
            var shown = id is null ? text : $"[{id}] {text}";
            _lines.Add((priority, shown, id, _lines.Count, fingerprint is null ? shown : (id is null ? fingerprint : $"[{id}] {fingerprint}")));
        }

        public void Block(int priority, string text) => _lines.Add((priority, text, null, _lines.Count, text));

        public Facts Build(string kind, string key, string title)
        {
            var included = new HashSet<int>();
            var used = 0;
            foreach (var l in _lines.OrderByDescending(l => l.Priority).ThenBy(l => l.Order))
            {
                var cost = l.Text.Length + 1;
                if (used + cost > BudgetChars && included.Count > 0) continue;
                included.Add(l.Order);
                used += cost;
            }
            var omitted = _lines.Count - included.Count;
            var sb = new StringBuilder();
            var fp = new StringBuilder();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in _lines.Where(l => included.Contains(l.Order)).OrderBy(l => l.Order))
            {
                sb.Append(l.Text).Append('\n');
                fp.Append(l.Fingerprint).Append('\n');
                if (l.Id is not null) ids.Add(l.Id);
            }
            if (omitted > 0) sb.Append($"（事实超出预算，已按重要性省略 {omitted} 条）\n");
            return new Facts(kind, key, title, sb.ToString(), ids, omitted) { FingerprintText = fp.ToString() };
        }
    }
}
