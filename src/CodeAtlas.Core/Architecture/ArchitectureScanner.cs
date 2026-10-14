using System.Diagnostics;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Storage;

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
    ];

    public static ArchModel Scan(string root, IReadOnlyList<IDiscoveryRule>? rules = null)
    {
        var inv = ProjectInventory.Load(root);
        var containers = ContainerDiscovery.Discover(inv);
        var ctx = new RuleContext(inv, containers);
        foreach (var rule in rules ?? DefaultRules) rule.Apply(ctx);
        var (edges, resolved) = EdgeMatcher.Match(ctx);
        var referenced = edges.SelectMany(e => new[] { e.FromKey, e.ToKey }).ToHashSet(StringComparer.Ordinal);
        var nodes = containers.Concat(ctx.Externals.Values.Where(x => referenced.Contains(x.Key)))
            .OrderBy(n => n.Kind).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
        var model = new ArchModel(nodes, edges, resolved, []);
        var declarations = ArchitectureDeclarations.Load(root);
        return declarations is null ? model : declarations.Apply(model);
    }

    public static (ArchModel Model, ArchReport Report) ScanAndStore(string root, IndexDatabase db)
    {
        var sw = Stopwatch.StartNew();
        var model = Scan(root);
        ArchStorage.Replace(db, model);
        // 未匹配 = 没有对上任何容器、只能挂到外部系统的 HTTP / gRPC 出口
        var unmatched = model.Outbound.Count(o => o.Protocol is "http" or "grpc" && (o.TargetKey is null || o.TargetKey.StartsWith("external:", StringComparison.Ordinal)));
        var report = new ArchReport(
            model.Nodes.Count(n => n.Kind == ArchNodeKind.Container), model.Nodes.Count(n => n.Kind == ArchNodeKind.External), model.Edges.Count,
            model.Outbound.Count, unmatched, model.Conflicts.Count, sw.Elapsed);
        return (model, report);
    }
}
