using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 把出口变成边。三档：按路由对上 0.9（5c 做）、URL 的 host:port 对上另一容器的监听端口 0.7、只知道客户端 0.5（挂到外部系统）。
/// 消费类协议（*-consume）方向反过来：外部系统 → 容器。
/// </summary>
public static class EdgeMatcher
{
    public const double ByRoute = 0.9;
    public const double ByConfig = 0.7;
    public const double ClientOnly = 0.5;

    public static (List<ArchEdge> Edges, List<Outbound> Resolved) Match(RuleContext ctx)
    {
        var containers = ctx.Containers;
        var resolved = new List<Outbound>();
        var edges = new Dictionary<(string From, string To, string Protocol), (double Confidence, List<Evidence> Evidence, string? ConfigKey, string? Url)>();

        void Add(string from, string to, string protocol, double confidence, Evidence evidence, string? configKey, string? url)
        {
            if (protocol.EndsWith("-consume", StringComparison.Ordinal)) (from, to) = (to, from);
            var key = (from, to, protocol);
            if (!edges.TryGetValue(key, out var e)) edges[key] = (confidence, [evidence], configKey, url);
            else
            {
                if (!e.Evidence.Any(x => x.File == evidence.File && x.Line == evidence.Line)) e.Evidence.Add(evidence);
                edges[key] = (Math.Max(e.Confidence, confidence), e.Evidence, e.ConfigKey ?? configKey, e.Url ?? url);
            }
        }

        foreach (var d in ctx.DirectEdges) Add(d.ContainerKey, d.ExternalKey, d.Protocol, ClientOnly, d.Evidence, null, null);

        foreach (var o in ctx.Outbound)
        {
            var evidence = new Evidence(o.File, o.Line, o.ClientName ?? o.ConfigKey ?? o.Url);
            if (o.TargetKey is not null) { Add(o.ContainerKey, o.TargetKey, o.Protocol, ClientOnly, evidence, o.ConfigKey, o.Url); resolved.Add(o); continue; }

            var url = o.Url ?? (o.ConfigKey is not null ? ResolveConfig(ctx, o) : null);
            var target = url is not null ? ContainerByUrl(containers, o.ContainerKey, url) : null;
            if (target is not null)
            {
                Add(o.ContainerKey, target.Key, o.Protocol, ByConfig, evidence, o.ConfigKey, url);
                resolved.Add(o with { TargetKey = target.Key, Url = url });
                continue;
            }
            var subKind = o.Protocol == "grpc" ? "grpc-service" : "http-service";
            var name = ExternalName(o, url);
            var key = ctx.External(subKind, name, evidence);
            Add(o.ContainerKey, key, o.Protocol, ClientOnly, evidence, o.ConfigKey, url);
            resolved.Add(o with { TargetKey = key, Url = url });
        }

        // 包引用的泛协议边（kafka）在同一对节点上有更具体的代码边（kafka-produce）时去掉
        var list = edges.Select(p => new ArchEdge(p.Key.From, p.Key.To, p.Key.Protocol, p.Value.Confidence, ArchSource.Discovered, p.Value.Evidence, p.Value.ConfigKey, p.Value.Url)).ToList();
        list.RemoveAll(e => PackageReferenceRule.GenericProtocols.Contains(e.Protocol)
            && list.Any(o => o.Protocol != e.Protocol && o.Protocol.StartsWith(e.Protocol + "-", StringComparison.Ordinal)
                             && ((o.FromKey == e.FromKey && o.ToKey == e.ToKey) || (o.FromKey == e.ToKey && o.ToKey == e.FromKey))));
        return (list.OrderBy(e => e.FromKey, StringComparer.Ordinal).ThenBy(e => e.ToKey, StringComparer.Ordinal).ThenBy(e => e.Protocol, StringComparer.Ordinal).ToList(), resolved);
    }

    /// <summary>配置键先在出口所在项目的 appsettings 找，再在容器宿主项目找。</summary>
    private static string? ResolveConfig(RuleContext ctx, Outbound o)
    {
        var dirs = new List<string>();
        var own = ctx.ProjectDirOf(o.File);
        if (own is not null) dirs.Add(own);
        var container = ctx.Containers.FirstOrDefault(c => c.Key == o.ContainerKey);
        if (container?.Path is not null) dirs.Add(container.Path);
        foreach (var d in dirs.Distinct())
        {
            var v = ctx.Inventory.Config(d, o.ConfigKey!);
            if (v is not null) return v;
        }
        return null;
    }

    private static ArchNode? ContainerByUrl(IReadOnlyList<ArchNode> containers, string selfKey, string url)
    {
        // Aspire 的 https+http:// 不是合法 URI 方案，先换成 http:// 再解析
        var normalized = url.StartsWith("https+http://", StringComparison.OrdinalIgnoreCase) ? "http://" + url["https+http://".Length..] : url;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var u)) return null;
        if (u.IsLoopback || u.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            var port = u.IsDefaultPort ? (u.Scheme == "https" ? 443 : 80) : u.Port;
            var hits = containers.Where(c => c.Key != selfKey && c.Ports.Contains(port)).ToList();
            return hits.Count == 1 ? hits[0] : null; // 两个容器声明同一端口时不猜
        }
        // 服务发现名（Aspire / compose / k8s）：主机名去掉分隔符后与容器名或目录名相同（catalog-api ↔ Catalog.API）
        var host = Normalize(u.Host);
        var byName = containers.Where(c => c.Key != selfKey && (Normalize(c.Name) == host || Normalize(LastSegment(c.Key)) == host)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string LastSegment(string key) { var i = key.LastIndexOf('/'); return i < 0 ? key : key[(i + 1)..]; }

    /// <summary>外部系统名：配置节名（Billing:Url → Billing）> 客户端名 > URL 主机名。</summary>
    private static string ExternalName(Outbound o, string? url)
    {
        if (o.ConfigKey is not null)
        {
            var section = o.ConfigKey.Split(':')[0];
            if (section.Length > 0) return section;
        }
        if (!string.IsNullOrWhiteSpace(o.ClientName)) return o.ClientName;
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.Host;
        return o.Protocol == "grpc" ? "gRPC 服务" : "HTTP 服务";
    }
}
