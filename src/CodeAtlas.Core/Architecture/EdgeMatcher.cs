using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 把出口变成边。档位：按路由对上 0.9（5c 做）、gRPC 客户端桩与服务端注册按服务名对上 0.9（<see cref="GrpcServiceRule"/>）、
/// URL 的 host:port 对上另一容器的监听端口 0.7、只知道客户端 0.5（挂到外部系统）。同一对节点同一协议只留一条边，置信度取最高。
/// 消费类协议（*-consume）方向反过来：外部系统 → 容器。
/// </summary>
public static class EdgeMatcher
{
    public const double ByRoute = 0.9;
    public const double ByServiceName = 0.9;
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
            // 出口在共享库里：证据注明"来自共享库"，先列本容器自己用到它的那一行；本容器没找到调用的降置信度
            ctx.SharedUses.TryGetValue(o, out var shared);
            var evidence = new Evidence(o.File, o.Line, SharedNote(Note(o), shared));
            void AddEdge(string from, string to, string protocol, double confidence, string? configKey, string? url)
            {
                if (shared?.Usage is { } usage) Add(from, to, protocol, confidence, usage with { Note = $"经共享库 {shared.Library} 的 {shared.Token}：{usage.Note}" }, configKey, url);
                Add(from, to, protocol, shared is { Certain: false } ? Math.Min(confidence, SharedLibraryAttribution.Unsure) : confidence, evidence, configKey, url);
            }
            if (o.TargetKey is not null)
            {
                var byName = o.RuleId == GrpcServiceRule.RuleId && containers.Any(c => c.Key == o.TargetKey);
                // VS Code 扩展拉起的进程按可执行文件名对上，与按配置地址对上同档
                var byExecutable = o.RuleId == VsCodeExtensionRule.RuleId && containers.Any(c => c.Key == o.TargetKey);
                AddEdge(o.ContainerKey, o.TargetKey, o.Protocol, byName ? ByServiceName : byExecutable ? ByConfig : ClientOnly, o.ConfigKey, o.Url);
                resolved.Add(o);
                continue;
            }

            var url = o.Url ?? (o.ConfigKey is not null ? ResolveConfig(ctx, o) : null);
            // gRPC 地址常写成 host:port（Go / Python 的 Dial、insecure_channel），补上方案才能按 URI 解析
            if (url is not null && o.Protocol == "grpc" && !url.Contains("://", StringComparison.Ordinal)) url = "http://" + (url.StartsWith("dns:///", StringComparison.Ordinal) ? url[7..] : url);
            var target = url is not null ? ContainerByUrl(containers, o.ContainerKey, url) : null;
            if (target is not null)
            {
                AddEdge(o.ContainerKey, target.Key, o.Protocol, ByConfig, o.ConfigKey, url);
                resolved.Add(o with { TargetKey = target.Key, Url = url });
                continue;
            }
            // 身份提供方地址解析不出：本仓库恰有一个容器自己就是 OIDC 服务端（引用了 Duende.IdentityServer 这类"本进程内"的身份服务）→ 连它
            if (o.Protocol == "oidc" && IdentityHost(ctx, o.ContainerKey) is { } idp)
            {
                AddEdge(o.ContainerKey, idp.Key, o.Protocol, ClientOnly, o.ConfigKey, url);
                resolved.Add(o with { TargetKey = idp.Key, Url = url });
                continue;
            }
            var subKind = o.Protocol switch { "grpc" => "grpc-service", "oidc" => "identity", _ => "http-service" };
            var name = ExternalName(o, url);
            var key = ctx.External(subKind, name, evidence);
            AddEdge(o.ContainerKey, key, o.Protocol, ClientOnly, o.ConfigKey, url);
            resolved.Add(o with { TargetKey = key, Url = url });
        }

        // 包引用的泛协议边（kafka）在同一对节点上有更具体的代码边（kafka-produce）时去掉
        var list = edges.Select(p => new ArchEdge(p.Key.From, p.Key.To, p.Key.Protocol, p.Value.Confidence, ArchSource.Discovered, p.Value.Evidence, p.Value.ConfigKey, p.Value.Url)).ToList();
        list = MergeSameNamedExternals(ctx, list, resolved);
        list.RemoveAll(e => PackageReferenceRule.GenericProtocols.Contains(e.Protocol)
            && list.Any(o => o.Protocol != e.Protocol && o.Protocol.StartsWith(e.Protocol + "-", StringComparison.Ordinal)
                             && ((o.FromKey == e.FromKey && o.ToKey == e.ToKey) || (o.FromKey == e.ToKey && o.ToKey == e.FromKey))));
        return (list.OrderBy(e => e.FromKey, StringComparer.Ordinal).ThenBy(e => e.ToKey, StringComparer.Ordinal).ThenBy(e => e.Protocol, StringComparer.Ordinal).ToList(), resolved);
    }

    /// <summary>
    /// 同名外部系统按种类登记成了两个节点（前端调 /realms 的 http-service「Keycloak」、后端 JWT Authority 的 identity「Keycloak」）：合成一个。
    /// 只合并"笼统种类"（http-service / grpc-service / other）进具体种类的那个；两个都具体（database 与 cache 同名）不猜。
    /// 边改指向留下的节点，同一对节点同一协议的边并成一条（置信度取高、证据合并）；出口的 TargetKey 一并改。
    /// </summary>
    private static List<ArchEdge> MergeSameNamedExternals(RuleContext ctx, List<ArchEdge> edges, List<Outbound> resolved)
    {
        static bool Generic(ArchNode n) => n.SubKind is "http-service" or "grpc-service" or "other";
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in ctx.Externals.Values.GroupBy(n => RuleContext.Slug(n.Name), StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var specific = g.Where(n => !Generic(n)).ToList();
            if (specific.Count != 1) continue;
            var keep = specific[0];
            foreach (var other in g.Where(n => n.Key != keep.Key))
            {
                rename[other.Key] = keep.Key;
                var merged = keep.Evidence.Concat(other.Evidence.Where(e => !keep.Evidence.Any(k => k.File == e.File && k.Line == e.Line))).ToList();
                keep = keep with { Evidence = merged };
                ctx.Externals.Remove(other.Key);
            }
            ctx.Externals[keep.Key] = keep;
        }
        if (rename.Count == 0) return edges;
        string Map(string key) => rename.TryGetValue(key, out var k) ? k : key;
        for (var i = 0; i < resolved.Count; i++)
            if (resolved[i].TargetKey is { } t && rename.ContainsKey(t)) resolved[i] = resolved[i] with { TargetKey = Map(t) };
        return edges.Select(e => e with { FromKey = Map(e.FromKey), ToKey = Map(e.ToKey) })
            .GroupBy(e => (e.FromKey, e.ToKey, e.Protocol))
            .Select(g => g.Skip(1).Aggregate(g.First(), (a, b) => a with
            {
                Confidence = Math.Max(a.Confidence, b.Confidence),
                Evidence = a.Evidence.Concat(b.Evidence.Where(e => !a.Evidence.Any(x => x.File == e.File && x.Line == e.Line))).ToList(),
                ConfigKey = a.ConfigKey ?? b.ConfigKey, Url = a.Url ?? b.Url,
            }))
            .ToList();
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
        // 服务发现名（Aspire / compose / k8s / Spring Cloud 的 lb://服务名 与 Eureka）：主机名去掉分隔符后与容器名或目录名相同（catalog-api ↔ Catalog.API）
        var host = Normalize(u.Host);
        var byName = containers.Where(c => c.Key != selfKey && (Normalize(c.Name) == host || Normalize(LastSegment(c.Key)) == host)).ToList();
        // Java 容器名常是带项目前缀的 artifactId（spring-petclinic-config-server），compose / k8s 的主机名是去掉前缀的 config-server
        if (byName.Count == 0 && u.Host.Length >= 4)
            byName = containers.Where(c => c.Key != selfKey && c.SubKind == "java-service" && c.Name.EndsWith("-" + u.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string LastSegment(string key) { var i = key.LastIndexOf('/'); return i < 0 ? key : key[(i + 1)..]; }

    /// <summary>自己就是 OIDC 服务端的唯一容器（包引用边指向"本进程内"的 identity 外部系统）；不是 selfKey。</summary>
    private static ArchNode? IdentityHost(RuleContext ctx, string selfKey)
    {
        var hosts = ctx.DirectEdges.Where(d => d.ExternalKey.StartsWith("external:identity:", StringComparison.Ordinal)
                                               && ctx.Externals.TryGetValue(d.ExternalKey, out var x) && x.Name.Contains("本进程内", StringComparison.Ordinal))
            .Select(d => d.ContainerKey).Distinct(StringComparer.Ordinal).ToList();
        return hosts.Count == 1 && hosts[0] != selfKey ? ctx.Containers.FirstOrDefault(c => c.Key == hosts[0]) : null;
    }

    private static string? SharedNote(string? note, SharedLibraryUse? shared) => shared switch
    {
        null => note,
        { Certain: true } => $"{note}（来自共享库 {shared.Library}）".TrimStart(),
        _ => $"{note}（来自共享库 {shared.Library}，本容器里没找到调用，可能经分派 / 反射）".TrimStart(),
    };

    /// <summary>边证据的说明：前端出口的 ClientName 存的是"方法 [经代理 前缀]"，拼成"GET /api/proxy-gateway/api/v5/x"；其余照旧取客户端名 / 配置键 / URL。</summary>
    private static string? Note(Outbound o)
    {
        if (o.RuleId != "frontend-http") return o.ClientName ?? o.ConfigKey ?? o.Url;
        var (method, _) = FrontendHttpRule.ParseCall(o.ClientName);
        return method is null ? o.Url : $"{method} {o.Url}";
    }

    /// <summary>
    /// 外部系统名：配置节名（Billing:Url → Billing）> 客户端名 > URL 主机名。
    /// 节名只是配置类名（LarkBotConfig、EmailOptions、SmtpSettings）时说明不了是什么系统：改用 URL 认出的服务名（open.larksuite.com → Lark Open API），
    /// 认不出用主机名；没有 URL 时去掉 Config / Options / Settings 后缀。
    /// </summary>
    internal static string ExternalName(Outbound o, string? url)
    {
        // 身份提供方：认出的产品名（Keycloak）优先于配置节名（AuthServer）
        if (o.Protocol == "oidc")
        {
            if (!string.IsNullOrWhiteSpace(o.ClientName)) return o.ClientName;
            if (o.ConfigKey is not null && o.ConfigKey.Split(':')[0] is { Length: > 0 } s) return s;
            return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var h) ? h.Host : "身份认证服务";
        }
        Uri? parsed = url is not null && Uri.TryCreate(url.StartsWith("https+http://", StringComparison.OrdinalIgnoreCase) ? "http://" + url[13..] : url, UriKind.Absolute, out var u) ? u : null;
        if (o.ConfigKey is not null)
        {
            var section = o.ConfigKey.Split(':')[0];
            if (section.Length > 0 && ConfigClassSuffix.Match(section) is { Success: true } cls)
            {
                if (parsed is not null) return KnownService(parsed.Host) ?? parsed.Host;
                if (cls.Groups[1].Value.Length > 0) return cls.Groups[1].Value;
            }
            else if (section.Length > 0) return section;
        }
        if (!string.IsNullOrWhiteSpace(o.ClientName)) return o.ClientName;
        if (parsed is not null) return KnownService(parsed.Host) ?? parsed.Host;
        return o.Protocol == "grpc" ? "gRPC 服务" : "HTTP 服务";
    }

    /// <summary>配置类名式的节名：XxxConfig / XxxConfiguration / XxxOptions / XxxSettings（第 1 组是去掉后缀的部分）。</summary>
    private static readonly System.Text.RegularExpressions.Regex ConfigClassSuffix = new(@"^(\w*?)(?:Config|Configuration|Options|Settings)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>认得出的公网服务主机 → 服务名；*.amazonaws.com 取服务段（ses.us-east-1.amazonaws.com → AWS SES）。认不出返回 null。</summary>
    internal static string? KnownService(string host)
    {
        host = host.ToLowerInvariant();
        bool Is(string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (Is("larksuite.com") || Is("feishu.cn")) return "Lark Open API";
        if (Is("sendgrid.com") || Is("sendgrid.net")) return "SendGrid";
        if (Is("mailgun.net")) return "Mailgun";
        if (Is("slack.com")) return "Slack";
        if (Is("api.telegram.org")) return "Telegram Bot API";
        if (Is("graph.microsoft.com")) return "Microsoft Graph";
        if (Is("api.github.com")) return "GitHub API";
        if (Is("dingtalk.com")) return "钉钉开放平台";
        if (Is("qyapi.weixin.qq.com")) return "企业微信 API";
        if (Is("amazonaws.com"))
        {
            var service = host.Split('.')[0];
            if (service is "email" or "email-smtp") service = "ses";
            return "AWS " + service.ToUpperInvariant();
        }
        return null;
    }
}
