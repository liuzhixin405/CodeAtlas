using System.Diagnostics;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Diagnostics;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Architecture;

public sealed record ArchReport(int Containers, int Externals, int Edges, int OutboundTotal, int OutboundUnmatched, int Conflicts, TimeSpan Elapsed);

/// <summary>容器图的事实层：清单 → 容器 → 规则 → 匹配 → 声明合并 →（可选）写库。</summary>
public static class ArchitectureScanner
{
    public static IReadOnlyList<IDiscoveryRule> DefaultRules { get; } =
    [
        new PackageReferenceRule(),
        new LocalAssemblyRule(),
        new HttpClientRule(),
        new GrpcChannelRule(),
        new InfrastructurePatternRule(),
        new FrontendHttpRule(),
        new GoOutboundRule(),
        new PythonOutboundRule(),
        new PolyglotInfraRule(),
        new JavaOutboundRule(),
        new SpringCloudRule(),
        new IdentityProviderRule(),
        new AiEndpointRule(),
        new VsCodeExtensionRule(),
        new GrpcServiceRule(), // 放最后：要看前面各规则登记的 gRPC 通道，和它们去重
    ];

    public static ArchModel Scan(string root, IReadOnlyList<IDiscoveryRule>? rules = null) => Scan(ProjectRoots.Single(root), rules);

    /// <summary>多根：外部根的项目一样成为容器（键是相对主根的目录，如 ../backend/Api）；architecture.yaml 只读主根的。</summary>
    /// <param name="timings">给了就记细分阶段 arch.inventory / arch.containers / arch.rule.&lt;规则名&gt; / arch.shared / arch.match / arch.declarations。</param>
    /// <param name="sources">与之后的入口识别共用的源码缓存；不给就自建。规则要逐行扫的源码（测试文件除外）先并行读进来。</param>
    /// <param name="inventory">已经按同一组根加载好的清单（与入口识别共用）；不给就现加载。</param>
    public static ArchModel Scan(ProjectRoots roots, IReadOnlyList<IDiscoveryRule>? rules = null, PhaseTimings? timings = null, SourceTextCache? sources = null,
        ProjectInventory? inventory = null)
    {
        var root = roots.Main;
        var inv = inventory ?? Time(timings, "arch.inventory", () => ProjectInventory.Load(roots));
        var containers = Time(timings, "arch.containers", () => ContainerDiscovery.Discover(inv));
        sources ??= new SourceTextCache(root);
        Time(timings, "arch.read", () => { sources.Prefetch(inv.SourceFiles.Where(f => !RuleContext.IsTestFile(f))); return 0; });
        var ctx = new RuleContext(inv, containers, sources);
        foreach (var rule in rules ?? DefaultRules) Time(timings, "arch.rule." + rule.GetType().Name, () => { rule.Apply(ctx); return 0; });
        // 共享库里的出口只归真正用到它的容器（eShop：7 个容器引用 EventBusRabbitMQ，只有 4 个在发）
        Time(timings, "arch.shared", () => { SharedLibraryAttribution.Apply(ctx); return 0; });
        var (edges, resolved) = Time(timings, "arch.match", () => EdgeMatcher.Match(ctx));
        var referenced = edges.SelectMany(e => new[] { e.FromKey, e.ToKey }).ToHashSet(StringComparer.Ordinal);
        var nodes = containers.Concat(ctx.Externals.Values.Where(x => referenced.Contains(x.Key)))
            .OrderBy(n => n.Kind).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
        var model = new ArchModel(nodes, edges, resolved, []);
        var declarations = ArchitectureDeclarations.Load(root);
        return declarations is null ? model : Time(timings, "arch.declarations", () => declarations.Apply(model));
    }

    internal static T Time<T>(PhaseTimings? timings, string phase, Func<T> action) => timings is null ? action() : timings.Measure(phase, action);

    public static (ArchModel Model, ArchReport Report) ScanAndStore(string root, IndexDatabase db) => ScanAndStore(ProjectRoots.Single(root), db);

    public static (ArchModel Model, ArchReport Report) ScanAndStore(ProjectRoots roots, IndexDatabase db, PhaseTimings? timings = null, SourceTextCache? sources = null,
        ProjectInventory? inventory = null)
    {
        var sw = Stopwatch.StartNew();
        var model = Scan(roots, timings: timings, sources: sources, inventory: inventory);
        Time(timings, "arch.store", () => { ArchStorage.Replace(db, model); return 0; });
        // 未匹配 = 没有对上任何容器、只能挂到外部系统的 HTTP / gRPC 出口
        var unmatched = model.Outbound.Count(o => o.Protocol is "http" or "grpc" && (o.TargetKey is null || o.TargetKey.StartsWith("external:", StringComparison.Ordinal)));
        var report = new ArchReport(
            model.Nodes.Count(n => n.Kind == ArchNodeKind.Container), model.Nodes.Count(n => n.Kind == ArchNodeKind.External), model.Edges.Count,
            model.Outbound.Count, unmatched, model.Conflicts.Count, sw.Elapsed);
        return (model, report);
    }
}
