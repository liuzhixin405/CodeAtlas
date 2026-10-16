using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server;

/// <param name="Scc">强连通分量编号；同编号且分量成员 > 1 的组件互为环。</param>
/// <param name="Hot">git 热点；没有 git 数据为 null。</param>
public sealed record DsmComponentDto(string Key, string Label, int Symbols, int Files, bool HasChildren, int Scc, DsmHotDto? Hot);

/// <param name="Revisions">组件内各文件修订数之和（文件修订合计：一次提交改了 N 个文件算 N 次），不是提交数。</param>
/// <param name="AgeMonths">最近一次提交距今的月数。</param>
/// <param name="Commits">涉及的提交数（按提交去重）；git 统计里没有提交号（从库里读回的）时为 null，页面只能写"文件修订合计"。</param>
public sealed record DsmHotDto(int Revisions, int Added, int Deleted, int AgeMonths, string LastCommit, int? Commits = null);

/// <summary>行 Row 依赖列 Col 的边数与种类。</summary>
public sealed record DsmCellDto(int Row, int Col, int Count, Dictionary<string, int> Kinds);

/// <summary>两个组件常一起改、却没有静态边（隐性耦合）。A / B 是组件下标。</summary>
public sealed record DsmCouplingDto(int A, int B, int Shared, double Degree);

/// <summary>一条声明规则（NetArchTest 还原或 architecture.yaml）。Id = 规则表里的下标，Violations 用它引用。</summary>
/// <param name="From">主语范围：命名空间模式；placement 时是接口名；* = 全部。</param>
/// <param name="FromMatch">prefix / contains / exact。</param>
public sealed record DsmRuleDto(int Id, string Kind, string From, string FromMatch, string? To, string? ToMatch, string File, int Line, string? TestName, string Source);

/// <summary>行 Row 依赖列 Col 违反了规则 RuleId：forbid 时 Count 是违规边数；require 未满足时 Count = 0。</summary>
public sealed record DsmViolationDto(int Row, int Col, int RuleId, int Count);

/// <param name="Root">"" = 容器内的项目层；项目目录 = 该项目的子命名空间；再深 = 命名空间 / 目录键。</param>
/// <param name="Cycles">每个环里的组件键。</param>
/// <param name="Rules">全部声明规则（含只展示的 placement）。</param>
/// <param name="Violations">这一层判出的违规格子；比规则粗的组件不判。</param>
/// <remarks>Rules / Violations 可省略（老调用方按 8 个参数构造），省略时为空表而不是 null。</remarks>
public sealed record DsmDto(
    string Container, string Root, IReadOnlyList<DsmComponentDto> Components, IReadOnlyList<DsmCellDto> Cells,
    IReadOnlyList<IReadOnlyList<string>> Cycles, IReadOnlyList<DsmCouplingDto> Coupling, bool GitAvailable, string? Note,
    IReadOnlyList<DsmRuleDto>? Rules = null, IReadOnlyList<DsmViolationDto>? Violations = null)
{
    public IReadOnlyList<DsmRuleDto> Rules { get; init; } = Rules ?? [];
    public IReadOnlyList<DsmViolationDto> Violations { get; init; } = Violations ?? [];
}

/// <summary>
/// 容器内的依赖结构矩阵：组件按层级切（项目 → 命名空间 → 子命名空间），行依赖列；Tarjan 找环，分量间按"被依赖者在前"排成下三角。
/// 叠加 git 热点与隐性耦合。与 MapBuilder 用同一份符号与边、同一套分组键。
/// </summary>
public static class DsmBuilder
{
    /// <summary>
    /// 只算编译期依赖：calls / references / implements / inherits。排除 dispatches（运行时分派）与 via=impl 的派生边
    /// （接口调用 → 实现，里程碑 3 为了调用链加的）——它们表达的是运行时去向，算进去会让 Application "依赖" Infrastructure，与项目引用矛盾。
    /// </summary>
    private static readonly HashSet<EdgeKind> DependencyKinds = [EdgeKind.Calls, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References];
    /// <summary>
    /// 没有任何可部署容器（纯类库仓库）时的整库伪容器键：DSM 仍可按项目 / 模块看依赖。
    /// 成员 = 清单里的非测试项目目录（csproj、go.mod、Python 项目、Java 模块、前端）；一个都没有就是整个仓库（""）。
    /// </summary>
    public const string RepoKey = "*";

    public static ArchNode RepoContainer(CodeAtlas.Core.Workspace.ProjectRoots roots)
    {
        var inv = ProjectInventory.Load(roots);
        // 测试树下的项目（tests/…/Fixtures 里的 Go 模块、夹具 csproj）与测试项目不是架构的一部分：与容器发现同一条测试路径规则
        var dirs = inv.Projects.Where(p => !p.IsTest).Select(p => p.Dir)
            .Concat(inv.GoModules.Select(m => m.Dir))
            .Concat(inv.PythonProjects.Where(p => !p.IsTest).Select(p => p.Dir))
            .Concat(inv.JavaModules.Where(m => !m.IsTest).Select(m => m.Dir))
            .Concat(inv.Frontends.Select(f => f.Dir))
            .Concat(inv.NodeServices.Select(n => n.Dir))
            .Where(d => !ContainerDiscovery.InTestTree(d))
            .Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (dirs.Count == 0) dirs = [""];
        var name = System.IO.Path.GetFileName(roots.Main.TrimEnd('/', '\\'));
        return new ArchNode(RepoKey, ArchNodeKind.Container, "repo", name.Length == 0 ? "仓库" : name, "", ArchSource.Discovered, [], dirs, []);
    }

    public const string SelfLabel = "（本级）";
    public const string GlobalLabel = "（全局命名空间）";
    public const string StrayPrefix = "⚠ ";

    /// <summary>库里的声明规则；给 Build 的 rules 参数。</summary>
    public static IReadOnlyList<ArchRule> LoadRules(IndexDatabase db) => ArchRuleStorage.Read(db);

    /// <param name="rules">声明规则（LoadRules）；null 或空 = 不叠加。</param>
    public static DsmDto Build(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, ArchNode container, string root, GitHistoryResult? git, DateOnly? today = null, IReadOnlyList<ArchRule>? rules = null)
    {
        root = root.Trim().TrimEnd('.', '/');
        var ruleDtos = (rules ?? []).Select((r, i) => new DsmRuleDto(i, r.Kind, r.FromPattern, r.FromMatch, r.ToPattern, r.ToMatch, r.File, r.Line, r.TestName, r.Source)).ToList();
        var members = container.Members.OrderByDescending(m => m.Length).ToList();
        string? MemberOf(string path) => members.FirstOrDefault(m => Rules.Under(path, m));

        // module 符号是"每个文件一个"的容器节点，没有命名空间，会把所有文件堆进一个空键组件；不参与。
        // Java 的测试代码（Maven / Gradle 布局在同一个模块目录的 src/test/java 里）不是架构的一部分，也不参与——C# 的测试项目本来就不是容器成员
        // 生成代码（protoc 的 *.pb.go / *_pb2.py、*.g.cs …）也不参与：microservices-demo 的 genproto 会成一个 618 符号的大组件，全是桩
        var scoped = symbols.Where(s => s.Kind is not (SymbolKind.External or SymbolKind.Module) && MemberOf(s.Path) is not null
                                        && !(MapBuilder.IsJava(s) && PipelineBuilder.IsTest(s.Path, "")) && !ProjectInventory.IsGeneratedSource(s.Path)).ToList();
        var byId = symbols.ToDictionary(s => s.Id);
        var keys = MapBuilder.GroupKeys(scoped);

        // 每个成员项目的"主命名空间"：该目录下分组键的多数派公共前缀；脚本按目录
        var baseOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            var ks = scoped.Where(s => MemberOf(s.Path) == m && keys.ContainsKey(s.Id)).Select(s => keys[s.Id]).ToList();
            baseOf[m] = ks.Count == 0 ? m : DominantPrefix(ks, m);
        }

        // Go / Python 的成员（多数符号在 .go / .py 里）：组件是包目录。容器层不是"一个成员一个组件"，而是成员目录本身（直接放在里面的文件）
        // 加它下面的顶层子目录——Go 模块 / Python 项目通常只有一个成员目录（go.mod / pyproject 所在），一个组件什么也看不出
        var packageMembers = members.Where(m =>
        {
            var inMember = scoped.Where(s => MemberOf(s.Path) == m).ToList();
            return inMember.Count > 0 && inMember.Count(s => IsPackageLanguage(s.Path)) * 2 > inMember.Count;
        }).ToHashSet(StringComparer.Ordinal);

        var compOf = new Dictionary<long, string>();
        var hasChildren = new Dictionary<string, bool>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in scoped)
        {
            if (!keys.TryGetValue(s.Id, out var key)) continue;
            var member = MemberOf(s.Path)!;
            string? comp;
            if (root.Length == 0 && packageMembers.Contains(member))
            {
                comp = key == member ? member : MapBuilder.ChildKey(member, key) ?? member;
                labels[comp] = comp != member ? MapBuilder.KeyLabel(comp) : member.Length == 0 ? container.Name : MapBuilder.KeyLabel(member);
                // 本级的子目录已经摊在这一层，本级组件不再往下钻
                if (comp != member && MapBuilder.ChildKey(comp, key) is not null) hasChildren[comp] = true;
                else hasChildren.TryAdd(comp, false);
            }
            else if (root.Length == 0)
            {
                comp = member;
                labels[comp] = member.Length == 0 ? container.Name : MapBuilder.LastSegment(member);
                var b = baseOf[member];
                if (MapBuilder.ChildKey(b, key) is not null) hasChildren[comp] = true;
                else hasChildren.TryAdd(comp, false);
            }
            else
            {
                var basis = baseOf.TryGetValue(root, out var b2) ? b2 : root;    // root 是项目目录 → 用它的主命名空间；否则 root 本身就是命名空间 / 目录
                if (key == basis) comp = basis;
                else comp = MapBuilder.ChildKey(basis, key);
                var stray = false;
                if (comp is null)
                {
                    // 项目里漏写了前缀的命名空间（比如少了公司名）或全局命名空间：在项目层单独成一个组件露出来，标全名；更深层则不属于这里
                    if (baseOf.ContainsKey(root) && member == root) { comp = MapBuilder.ChildKey("", key) ?? key; stray = true; }
                    else continue;
                }
                // 漏前缀的命名空间标 ⚠ 加顶层段，和同名的正常组件区分开（RolePermissionLibrary vs ⚠ RolePermissionLibrary）
                labels[comp] = comp == basis ? SelfLabel : stray ? (comp.Length == 0 ? GlobalLabel : StrayPrefix + comp) : MapBuilder.KeyLabel(comp);
                if (comp != basis && MapBuilder.ChildKey(comp, key) is not null) hasChildren[comp] = true;
                else hasChildren.TryAdd(comp, false);
            }
            compOf[s.Id] = comp;
        }

        if (compOf.Count == 0)
            return new DsmDto(container.Key, root, [], [], [], [], git is { Files.Count: > 0 }, root.Length == 0 ? "容器成员目录下没有索引到符号（子仓库没被索引？）" : $"{root} 下没有符号", ruleDtos, []);

        var compKeys = compOf.Values.Distinct().ToList();
        var symbolCount = compKeys.ToDictionary(k => k, k => compOf.Count(p => p.Value == k), StringComparer.Ordinal);
        var filesOf = compKeys.ToDictionary(k => k, k => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (id, comp) in compOf) filesOf[comp].Add(byId[id].Path);

        // 依赖：行 → 列
        var cellMap = new Dictionary<(string, string), (int Count, Dictionary<string, int> Kinds)>();
        foreach (var e in edges)
        {
            if (!DependencyKinds.Contains(e.Kind) || e.Confidence < 0.4 || e.Via == "impl") continue;
            if (!compOf.TryGetValue(e.FromId, out var from) || !compOf.TryGetValue(e.ToId, out var to) || from == to) continue;
            if (!cellMap.TryGetValue((from, to), out var c)) cellMap[(from, to)] = c = (0, new Dictionary<string, int>());
            var kind = DbNames.ToDb(e.Kind);
            c.Kinds[kind] = c.Kinds.GetValueOrDefault(kind) + 1;
            cellMap[(from, to)] = (c.Count + 1, c.Kinds);
        }
        var deps = compKeys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (from, to) in cellMap.Keys) deps[from].Add(to);

        // Tarjan + 分量间拓扑（被依赖者在前）
        var (ordered, sccOf, sccMembers) = DependencyOrder(compKeys, deps, k => symbolCount[k]);
        var index = ordered.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal);
        var sccIndex = new Dictionary<int, int>();
        foreach (var k in ordered) sccIndex.TryAdd(sccOf[k], sccIndex.Count);

        var todayValue = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var statsByPath = git?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var components = ordered.Select(k =>
        {
            DsmHotDto? hot = null;
            if (statsByPath is not null)
            {
                var stats = filesOf[k].Select(f => statsByPath.GetValueOrDefault(f)).Where(s => s is not null).Select(s => s!).ToList();
                if (stats.Count > 0)
                {
                    var last = stats.Max(s => s.LastCommit);
                    int? commits = stats.All(s => s.Commits is not null) ? stats.SelectMany(s => s.Commits!).Distinct(StringComparer.Ordinal).Count() : null;
                    hot = new DsmHotDto(stats.Sum(s => s.Revisions), stats.Sum(s => s.Added), stats.Sum(s => s.Deleted), Math.Max(0, (todayValue.DayNumber - last.DayNumber) / 30), last.ToString("yyyy-MM-dd"), commits);
                }
            }
            return new DsmComponentDto(k, labels[k], symbolCount[k], filesOf[k].Count, hasChildren.GetValueOrDefault(k), sccIndex[sccOf[k]], hot);
        }).ToList();

        var cells = cellMap.Select(p => new DsmCellDto(index[p.Key.Item1], index[p.Key.Item2], p.Value.Count, p.Value.Kinds)).OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
        var cycles = sccMembers.Values.Where(m => m.Count > 1).Select(m => (IReadOnlyList<string>)m.OrderBy(k => index[k]).ToList()).OrderBy(m => index[m[0]]).ToList();

        // 隐性耦合：文件对 → 组件对，只保留没有静态边的
        var coupling = new List<DsmCouplingDto>();
        if (git is not null)
        {
            var compOfFile = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, files) in filesOf) foreach (var f in files) compOfFile[f] = k;
            var agg = new Dictionary<(int, int), (int Shared, double Degree)>();
            foreach (var c in git.Coupling)
            {
                if (!compOfFile.TryGetValue(c.PathA, out var a) || !compOfFile.TryGetValue(c.PathB, out var b) || a == b) continue;
                if (cellMap.ContainsKey((a, b)) || cellMap.ContainsKey((b, a))) continue;
                var pair = index[a] < index[b] ? (index[a], index[b]) : (index[b], index[a]);
                var cur = agg.GetValueOrDefault(pair);
                agg[pair] = (cur.Shared + c.Shared, Math.Max(cur.Degree, c.Degree));
            }
            coupling = agg.Select(p => new DsmCouplingDto(p.Key.Item1, p.Key.Item2, p.Value.Shared, p.Value.Degree)).OrderByDescending(c => c.Shared).ThenBy(c => c.A).ThenBy(c => c.B).ToList();
        }

        // 声明规则叠加：组件按命名空间与规则模式比对。项目层的组件键是目录，用它的主命名空间
        var nsOf = ordered.ToDictionary(k => k, k => root.Length == 0 ? baseOf.GetValueOrDefault(k, k) : k, StringComparer.Ordinal);
        var violations = Violations(rules ?? [], ordered, nsOf, cellMap);

        return new DsmDto(container.Key, root, components, cells, cycles, coupling, git is { Files.Count: > 0 }, null, ruleDtos, violations);
    }

    /// <summary>
    /// 规则 → 违规格子。forbid：行在 from 内、列在 to 内且有依赖；require：行在 from 内，to 的列在矩阵里却一条依赖都没有（Count 0）。
    /// 组件比规则粗（规则说 A.App.Modules，组件是 A.App）时这一层不判，钻进去才判——否则整个项目会因为一个子命名空间的规则被标红。
    /// </summary>
    public static List<DsmViolationDto> Violations(IReadOnlyList<ArchRule> rules, IReadOnlyList<string> ordered, IReadOnlyDictionary<string, string> nsOf, IReadOnlyDictionary<(string, string), (int Count, Dictionary<string, int> Kinds)> cellMap)
    {
        var violations = new List<DsmViolationDto>();
        var index = ordered.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal);
        for (var id = 0; id < rules.Count; id++)
        {
            var rule = rules[id];
            if (rule.Kind is not (ArchRuleKind.ForbidDependency or ArchRuleKind.RequireDependency) || rule.ToPattern is null) continue;
            var toMatch = rule.ToMatch ?? ArchRuleMatch.Prefix;
            bool In(string comp, string pattern, string match) => ArchRule.Matches(nsOf[comp], pattern, match) && !ArchRule.IsCoarserThan(nsOf[comp], pattern, match);
            var rows = ordered.Where(k => In(k, rule.FromPattern, rule.FromMatch)).ToList();
            var cols = ordered.Where(k => In(k, rule.ToPattern, toMatch)).ToList();
            foreach (var row in rows)
            {
                var targets = cols.Where(c => c != row).ToList();
                if (targets.Count == 0) continue;
                if (rule.Kind == ArchRuleKind.ForbidDependency)
                {
                    foreach (var col in targets)
                        if (cellMap.TryGetValue((row, col), out var cell) && cell.Count > 0) violations.Add(new DsmViolationDto(index[row], index[col], id, cell.Count));
                }
                else if (!targets.Any(col => cellMap.TryGetValue((row, col), out var cell) && cell.Count > 0))
                {
                    // 一条都没依赖：标在 to 的第一列上
                    violations.Add(new DsmViolationDto(index[row], index[targets[0]], id, 0));
                }
            }
        }
        return violations.OrderBy(v => v.Row).ThenBy(v => v.Col).ThenBy(v => v.RuleId).ToList();
    }

    private static bool IsPackageLanguage(string path) =>
        path.EndsWith(".go", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>多数派的公共前缀：按第一段分组取最大组，再求组内公共前缀；全失败回退 fallback。</summary>
    public static string DominantPrefix(IReadOnlyList<string> keys, string fallback)
    {
        var dominant = keys.Where(k => MapBuilder.KeySegments(k).Length > 0).GroupBy(k => MapBuilder.KeySegments(k)[0]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        if (dominant is null) return fallback;
        var prefix = PipelineBuilder.CommonPrefix(dominant);
        return prefix.Length == 0 ? dominant.Key : prefix;
    }

    /// <summary>
    /// 组件的依赖序（DSM 与模块关系共用）：Tarjan 找强连通分量，分量之间拓扑排序、被依赖者在前；同时可放的分量按 size 之和降序、再按分量里最小的键；
    /// 分量（环）内按 size 降序、再按键。deps[k] 是 k 依赖的组件（必须含 keys 里每个键）。
    /// </summary>
    internal static (List<string> Ordered, Dictionary<string, int> SccOf, Dictionary<int, List<string>> SccMembers) DependencyOrder(
        List<string> keys, Dictionary<string, HashSet<string>> deps, Func<string, int> size)
    {
        var sccOf = Tarjan(keys, deps);
        var sccMembers = sccOf.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToList());
        var sccDeps = sccMembers.Keys.ToDictionary(id => id, id => sccMembers[id].SelectMany(k => deps[k]).Select(k => sccOf[k]).Where(d => d != id).ToHashSet());
        var ordered = new List<string>();
        var placed = new HashSet<int>();
        int SccSize(int id) => sccMembers[id].Sum(size);
        while (placed.Count < sccMembers.Count)
        {
            var ready = sccMembers.Keys.Where(id => !placed.Contains(id) && sccDeps[id].All(placed.Contains))
                .OrderByDescending(SccSize).ThenBy(id => sccMembers[id].Min(StringComparer.Ordinal), StringComparer.Ordinal).ToList();
            if (ready.Count == 0) ready = [sccMembers.Keys.First(id => !placed.Contains(id))]; // 理论上不会：分量间无环
            var id = ready[0];
            placed.Add(id);
            ordered.AddRange(sccMembers[id].OrderByDescending(size).ThenBy(k => k, StringComparer.Ordinal));
        }
        return (ordered, sccOf, sccMembers);
    }

    private static Dictionary<string, int> Tarjan(List<string> nodes, Dictionary<string, HashSet<string>> adj)
    {
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var counter = 0;
        var sccCount = 0;

        void Visit(string v)
        {
            indexOf[v] = low[v] = counter++;
            stack.Push(v); onStack.Add(v);
            foreach (var w in adj[v].OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!indexOf.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], indexOf[w]);
            }
            if (low[v] != indexOf[v]) return;
            var id = sccCount++;
            string x;
            do { x = stack.Pop(); onStack.Remove(x); result[x] = id; } while (x != v);
        }
        foreach (var n in nodes.OrderBy(x => x, StringComparer.Ordinal)) if (!indexOf.ContainsKey(n)) Visit(n);
        return result;
    }

    private static class Rules
    {
        // 多项目：成员目录 "" 是主根，不含外部根的 ../… 文件（见 RuleContext.Under）
        public static bool Under(string rel, string dir) => Core.Architecture.Rules.RuleContext.Under(rel, dir);
    }
}
