using System.Diagnostics;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Diagnostics;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Server;

/// <param name="EntriesByKind">入口数按 kind（http / hosted / command / job / hub / grpc / handler / page / api-route）。</param>
/// <param name="Matches">出口路径对上另一容器入口的处数。</param>
/// <param name="Rules">声明规则数（NetArchTest 还原 + architecture.yaml）。</param>
/// <param name="RulesByKind">规则数按 kind（forbid-dependency / require-dependency / placement）。</param>
/// <param name="RulesSkipped">认不出、跳过的谓词数（BeSealed、OnlyHaveDependencyOn …）。</param>
/// <param name="Skipped">输入（源码 / 配置 / 索引内容）与上次相同，没有重算，数字是库里沿用的结果。</param>
public sealed record PostProcessReport(IReadOnlyDictionary<string, int> EntriesByKind, int Matches, TimeSpan Elapsed,
    int Rules = 0, IReadOnlyDictionary<string, int>? RulesByKind = null, int RulesSkipped = 0, bool Skipped = false)
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
/// 每次索引（全量与增量）之后的三步后处理：容器图（arch）→ git 行为统计（git）→ 入口 / 路由匹配 / 声明规则（entries）。按这个顺序各调一次，
/// 每步可以单独抛异常（调用方各自记错误，后面的步骤照跑）：
/// <code>
/// var post = new PostIndexSteps(roots, db, timings);
/// var arch = post.Arch();       // null = 输入没变，跳过
/// var gitRan = post.Git();      // false = 各仓库 ref 没变，跳过
/// var entries = post.Entries(); // entries.Skipped = 跟着 arch 一起跳过
/// </code>
/// 省时的办法：一份清单、一份并行预读的源码缓存三步共用；输入指纹（代码版本 + 清单遍历到的全部文件的路径 / 大小 / 修改时间 + architecture.yaml
/// + 索引内容校验）与上次成功时相同就整段跳过 arch 与 entries（两者一起：entries 会把升级后的容器图写回 arch_*，只重跑 entries 会在已升级的图上再升级一次）。
/// 改了任何参与的文件就整段重算——结果与全量一致，测试 PostIndexStepsTests 对比两者的表内容。
/// </summary>
public sealed class PostIndexSteps(ProjectRoots roots, IndexDatabase db, PhaseTimings? timings = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly SourceTextCache _sources = new(roots.Main);
    private ProjectInventory? _inventory;
    private string? _fingerprint;
    private bool _skip;

    /// <summary>规则代码本身的版本：换了 CodeAtlas 版本（规则改了）就算输入变了。</summary>
    private static readonly string CodeVersion =
        typeof(ArchitectureScanner).Assembly.ManifestModule.ModuleVersionId + "|" + typeof(EntryDiscovery).Assembly.ManifestModule.ModuleVersionId;

    private T Time<T>(string phase, Func<T> action) => timings is null ? action() : timings.Measure(phase, action);

    /// <summary>容器图扫描并写库；输入与上次成功的后处理相同时跳过，返回 null。</summary>
    public ArchReport? Arch()
    {
        _fingerprint = null;
        _skip = false;
        var fingerprint = Time("arch.fingerprint", () => ArchFingerprintStorage.Combine(CodeVersion, ProjectInventory.InputFingerprint(roots), ArchFingerprintStorage.IndexContent(db)));
        if (ArchFingerprintStorage.Get(db, ArchFingerprintStorage.Post)?.Value == fingerprint)
        {
            _skip = true;
            _fingerprint = fingerprint;
            return null;
        }
        _inventory = Time("arch.inventory", () => ProjectInventory.Load(roots));
        var (_, report) = ArchitectureScanner.ScanAndStore(roots, db, timings, _sources, _inventory);
        _fingerprint = fingerprint;
        return report;
    }

    /// <summary>git 行为统计；各仓库 ref 与 .mailmap 都没变时跳过，返回 false。</summary>
    public bool Git() => GitStorage.RefreshIfChanged(db, roots);

    /// <summary>入口识别、路由匹配、声明规则并写库；Arch 跳过时也跳过（Skipped，数字取库里的）。Arch 成功后这一步也成功才记下指纹。</summary>
    public PostProcessReport Entries()
    {
        if (_skip && ArchFingerprintStorage.Get(db, ArchFingerprintStorage.Post) is { Info: { } info } && System.Text.Json.JsonSerializer.Deserialize<StoredReport>(info, Json) is { } stored)
            return new PostProcessReport(stored.EntriesByKind, stored.Matches, TimeSpan.Zero, stored.Rules, stored.RulesByKind, stored.RulesSkipped, Skipped: true);
        var fingerprint = _fingerprint;
        _fingerprint = null;
        _skip = false;
        var report = ArchitecturePostProcess.Run(roots, db, timings, _sources, _inventory);
        if (fingerprint is not null)
        {
            var summary = new StoredReport(report.EntriesByKind, report.Matches, report.Rules, report.RulesByKind ?? new Dictionary<string, int>(), report.RulesSkipped);
            ArchFingerprintStorage.Set(db, ArchFingerprintStorage.Post, fingerprint, System.Text.Json.JsonSerializer.Serialize(summary, Json));
        }
        return report;
    }

    private sealed record StoredReport(IReadOnlyDictionary<string, int> EntriesByKind, int Matches, int Rules, IReadOnlyDictionary<string, int> RulesByKind, int RulesSkipped);
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
    /// <param name="sources">容器图扫描时读过的源码缓存（同一次后处理里传同一个，省掉重读）；不给就自建。</param>
    /// <param name="inventory">容器图扫描用过的清单（同一组根）；不给就现加载。</param>
    public static PostProcessReport Run(ProjectRoots roots, IndexDatabase db, PhaseTimings? timings = null, SourceTextCache? sources = null, ProjectInventory? inventory = null)
    {
        T Time<T>(string phase, Func<T> action) => timings is null ? action() : timings.Measure(phase, action);
        var repoRoot = roots.Main;
        var sw = Stopwatch.StartNew();
        var model = Time("entries.arch-read", () => ArchStorage.Read(db));
        var (symbols, edges) = Time("entries.load", () => MapData.Load(new IndexReader(db)));
        var containers = model.Nodes.Where(n => n.Kind == ArchNodeKind.Container).ToList();
        sources ??= new SourceTextCache(repoRoot);
        Time("entries.read", () => { sources.Prefetch(symbols.Where(s => s.Path != IndexWriter.ExternalFilePath).Select(s => s.Path).Where(p => !RuleContext.IsTestFile(p))); return 0; });
        Func<string, string[]> readLines = sources.Lines;

        var entries = Time("entries.discover", () => EntryDiscovery.Discover(symbols, edges, containers, readLines, timings));
        var matches = Time("entries.routes", () => RouteLinker.Match(model.Outbound, entries, containers, symbols, readLines));
        var upgraded = RouteLinker.Upgrade(model, matches);

        Time("entries.store", () => { ArchStorage.Replace(db, upgraded); EntryStorage.Replace(db, entries, matches); return 0; });

        inventory ??= Time("entries.inventory", () => ProjectInventory.Load(roots));
        var rules = Time("entries.rules", () => ArchRuleScanner.Scan(inventory, new SymbolTypeResolver(symbols, inventory), sources, ArchitectureDeclarations.Load(repoRoot)));
        Time("entries.rules-store", () => { ArchRuleStorage.Replace(db, rules.Rules); return 0; });

        var byKind = entries.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rulesByKind = rules.Rules.GroupBy(r => r.Kind, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new PostProcessReport(byKind, matches.Count, sw.Elapsed, rules.Rules.Count, rulesByKind, rules.SkippedPredicates);
    }

    /// <summary>仓库相对路径 → 源码行；读不到（文件被删、编码错）返回空数组，规则按"没有特性"处理。外部根的 ../other/… 也是相对主根，直接拼接即可。</summary>
    public static Func<string, string[]> MakeReadLines(string repoRoot) => new SourceTextCache(repoRoot).Lines;

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
