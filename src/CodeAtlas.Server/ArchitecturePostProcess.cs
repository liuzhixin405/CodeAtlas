using System.Diagnostics;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Server;

/// <param name="EntriesByKind">入口数按 kind（http / hosted / command / job / hub / grpc / handler / page / api-route）。</param>
/// <param name="Matches">出口路径对上另一容器入口的处数。</param>
/// <param name="Rules">声明规则数（NetArchTest 还原 + architecture.yaml）。</param>
/// <param name="RulesByKind">规则数按 kind（forbid-dependency / require-dependency / placement）。</param>
/// <param name="RulesSkipped">认不出、跳过的谓词数（BeSealed、OnlyHaveDependencyOn …）。</param>
public sealed record PostProcessReport(IReadOnlyDictionary<string, int> EntriesByKind, int Matches, TimeSpan Elapsed,
    int Rules = 0, IReadOnlyDictionary<string, int>? RulesByKind = null, int RulesSkipped = 0)
{
    public int Entries => EntriesByKind.Values.Sum();

    /// <summary>一行：rules: 9 (forbid 4, placement 5, skipped 0)。</summary>
    public string FormatRules()
    {
        var by = RulesByKind ?? new Dictionary<string, int>();
        return $"rules: {Rules} (forbid {by.GetValueOrDefault(ArchRuleKind.ForbidDependency)}, require {by.GetValueOrDefault(ArchRuleKind.RequireDependency)}, placement {by.GetValueOrDefault(ArchRuleKind.Placement)}, skipped {RulesSkipped})";
    }
}

/// <summary>
/// 容器图扫描之后的第二步：用索引里的符号与边找入口（控制器动作、常驻服务、分发处理器、前端页面），
/// 把出口路径对到别的容器的入口，再把对上的升级成 0.9 的容器边。结果写回 arch_* 与 entry_points / route_matches。
/// 第三步：从测试项目还原 NetArchTest 规则（加 architecture.yaml 的 layers / rules）写进 arch_rules，DSM 叠加用。
/// </summary>
public static class ArchitecturePostProcess
{
    public static PostProcessReport Run(string repoRoot, IndexDatabase db) => Run(ProjectRoots.Single(repoRoot), db);

    /// <summary>多根：外部根的文件路径是 ../other/…，MakeReadLines 用 Path.Combine(主根, 相对路径) 照样读得到；清单要扫全部根。</summary>
    public static PostProcessReport Run(ProjectRoots roots, IndexDatabase db)
    {
        var repoRoot = roots.Main;
        var sw = Stopwatch.StartNew();
        var model = ArchStorage.Read(db);
        var (symbols, edges) = MapData.Load(new IndexReader(db));
        var containers = model.Nodes.Where(n => n.Kind == ArchNodeKind.Container).ToList();
        var readLines = MakeReadLines(repoRoot);

        var entries = EntryDiscovery.Discover(symbols, edges, containers, readLines);
        var matches = RouteLinker.Match(model.Outbound, entries, containers, symbols, readLines);
        var upgraded = RouteLinker.Upgrade(model, matches);

        ArchStorage.Replace(db, upgraded);
        EntryStorage.Replace(db, entries, matches);

        var inventory = ProjectInventory.Load(roots);
        var rules = ArchRuleScanner.Scan(inventory, new SymbolTypeResolver(symbols, inventory), readLines, ArchitectureDeclarations.Load(repoRoot));
        ArchRuleStorage.Replace(db, rules.Rules);

        var byKind = entries.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rulesByKind = rules.Rules.GroupBy(r => r.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new PostProcessReport(byKind, matches.Count, sw.Elapsed, rules.Rules.Count, rulesByKind, rules.SkippedPredicates);
    }

    /// <summary>仓库相对路径 → 源码行；读不到（文件被删、编码错）返回空数组，规则按"没有特性"处理。外部根的 ../other/… 也是相对主根，直接拼接即可。</summary>
    public static Func<string, string[]> MakeReadLines(string repoRoot)
    {
        var cache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        return rel =>
        {
            if (cache.TryGetValue(rel, out var cached)) return cached;
            string[] lines;
            try { lines = File.ReadAllLines(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar))); }
            catch { lines = []; }
            return cache[rel] = lines;
        };
    }

    /// <summary>
    /// 用索引里的类型符号解析 <c>typeof(X)</c>：X 的命名空间 = 限定名去掉最后一段；X 的"程序集" = X 所在 csproj 目录下命名空间的多数派公共前缀。
    /// 同名类型多个时优先非测试项目的；仍不唯一（不同命名空间）→ null。
    /// </summary>
    public sealed class SymbolTypeResolver : IArchTypeResolver
    {
        private static readonly HashSet<SymbolKind> TypeKinds = [SymbolKind.Class, SymbolKind.Interface, SymbolKind.Struct, SymbolKind.Enum];
        private readonly Dictionary<string, List<MapSymbol>> _byName = new(StringComparer.Ordinal);
        private readonly List<CsProject> _projects;
        private readonly Dictionary<string, string> _rootOfProject = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<MapSymbol> _symbols;

        public SymbolTypeResolver(IReadOnlyList<MapSymbol> symbols, ProjectInventory inventory)
        {
            _symbols = symbols;
            _projects = inventory.Projects.OrderByDescending(p => p.Dir.Length).ToList();
            foreach (var s in symbols)
            {
                if (!TypeKinds.Contains(s.Kind)) continue;
                if (!_byName.TryGetValue(s.Name, out var list)) _byName[s.Name] = list = [];
                list.Add(s);
            }
        }

        public string? NamespaceOf(string typeName)
        {
            var candidates = Candidates(typeName).Select(s => NamespaceOfQualified(s.QualifiedName)).Distinct(StringComparer.Ordinal).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        public string? AssemblyRootOf(string typeName)
        {
            var dirs = Candidates(typeName).Select(s => ProjectOf(s.Path)).Where(p => p is not null).Select(p => p!.Dir).Distinct(StringComparer.Ordinal).ToList();
            if (dirs.Count != 1) return null;
            var dir = dirs[0];
            if (_rootOfProject.TryGetValue(dir, out var cached)) return cached;
            var namespaces = _symbols.Where(s => s.Kind == SymbolKind.Namespace && Rules.Under(s.Path, dir)).Select(s => s.QualifiedName).Distinct().ToList();
            return _rootOfProject[dir] = namespaces.Count == 0 ? NamespaceOfQualified(Candidates(typeName).First().QualifiedName) : DsmBuilder.DominantPrefix(namespaces, dir);
        }

        private List<MapSymbol> Candidates(string typeName)
        {
            if (!_byName.TryGetValue(typeName, out var list)) return [];
            var nonTest = list.Where(s => ProjectOf(s.Path) is { IsTest: false }).ToList();
            return nonTest.Count > 0 ? nonTest : list;
        }

        private CsProject? ProjectOf(string path) => _projects.FirstOrDefault(p => Rules.Under(path, p.Dir));

        /// <summary>限定名去掉泛型参数与最后一段：A.B.ICommand&lt;T&gt; → A.B；没有点 → ""。</summary>
        private static string NamespaceOfQualified(string qualified)
        {
            qualified = MapBuilder.StripGenericArgs(qualified);
            var dot = qualified.LastIndexOf('.');
            return dot < 0 ? "" : qualified[..dot];
        }

        private static class Rules
        {
            public static bool Under(string rel, string dir) => Core.Architecture.Rules.RuleContext.Under(rel, dir);
        }
    }
}
