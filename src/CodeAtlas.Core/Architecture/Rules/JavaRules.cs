using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// Java 表达式的地址来源：字符串字面量（http / https / lb://）、"前缀" + 变量 / 变量 + "后缀" 的拼接、URI.create / new URI、String.format、
/// System.getenv / getOrDefault（环境变量名记成配置键）、env.getProperty("键")、@Value("${键:缺省}") 注解的字段 / 参数（键在模块的 Spring 配置里找值）、
/// 字段 / 局部变量的赋值、无参方法的 return 表达式、discoveryClient.getInstances("服务名")。只做文本回溯，找不到返回空。
/// </summary>
public static class JavaAddress
{
    private static readonly Regex Literal = new(@"^""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex UriWrap = new(@"^(?:URI\s*\.\s*create|new\s+URI|URI\s*\.\s*of|UriComponentsBuilder\s*\.\s*fromHttpUrl|UriComponentsBuilder\s*\.\s*fromUriString)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Format = new(@"^String\s*\.\s*format\s*\(", RegexOptions.Compiled);
    private static readonly Regex Getenv = new(@"^System\s*\.\s*getenv\s*\(\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex GetenvDefault = new(@"^System\s*\.\s*getenv\s*\(\s*\)\s*\.\s*getOrDefault\s*\(\s*""([^""]+)""\s*,\s*""([^""]*)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex GetProperty = new(@"^\w+\s*\.\s*(?:getProperty|getRequiredProperty)\s*\(\s*""([^""]+)""\s*(?:,\s*""([^""]*)""\s*)?(?:,[^)]*)?\)", RegexOptions.Compiled);
    private static readonly Regex Instances = new(@"\.\s*(?:getInstances|choose)\s*\(\s*""([\w.\-]+)""", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^(?:this\s*\.\s*)?([A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex Concat = new(@"^\s*\+\s*(.+)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <param name="setting">Spring 配置键 → 值（已去掉 ${…:缺省} 占位）；找不到返回 null。</param>
    public static AddressResolver.Address Resolve(string expr, string[] lines, int lineIndex, Func<string, string?> setting, int depth = 0)
    {
        var none = new AddressResolver.Address(null, null);
        expr = expr.Trim();
        if (expr.Length == 0 || depth > 4) return none;
        var (head, tail) = SplitConcat(expr);
        var address = ResolveTerm(head, lines, lineIndex, setting, depth);
        // "http://" + host：字面量只有方案，地址在后面的变量里
        if (address.Url is { } u && u.EndsWith("://", StringComparison.Ordinal) && tail is not null)
        {
            var (next, rest) = SplitConcat(tail);
            var inner = ResolveTerm(next, lines, lineIndex, setting, depth + 1, bare: true);
            address = new(inner.Url is null ? null : inner.Url.Contains("://", StringComparison.Ordinal) ? inner.Url : u + inner.Url, inner.ConfigKey);
            tail = rest;
        }
        // 基址 + "/路径"：后面的字面量拼上去（变量就不拼了）
        if (address.Url is { } baseUrl && tail is not null && Literal.Match(tail.TrimStart()) is { Success: true } lit)
            address = address with { Url = JoinPath(baseUrl, lit.Groups[1].Value) };
        return address.Url is not null && !IsAbsolute(address.Url) ? address with { Url = null } : address;
    }

    public static bool IsAbsolute(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("lb://", StringComparison.OrdinalIgnoreCase);

    public static string JoinPath(string baseUrl, string path)
    {
        if (path.Length == 0) return baseUrl;
        if (baseUrl.EndsWith('/') && path.StartsWith('/')) return baseUrl + path[1..];
        if (!baseUrl.EndsWith('/') && !path.StartsWith('/') && !path.StartsWith('?')) return baseUrl + "/" + path;
        return baseUrl + path;
    }

    /// <summary>expr 拆成第一项与 "+ 之后" 的剩余（同层的第一个 +，引号与括号里的不算）。</summary>
    private static (string Head, string? Tail) SplitConcat(string expr)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '+' && depth == 0) return (expr[..i].Trim(), expr[(i + 1)..].Trim());
        }
        return (expr, null);
    }

    /// <param name="bare">"http://" + x 里的 x：字面量可以是不带方案的 host:port。</param>
    private static AddressResolver.Address ResolveTerm(string term, string[] lines, int lineIndex, Func<string, string?> setting, int depth, bool bare = false)
    {
        var none = new AddressResolver.Address(null, null);
        term = term.Trim();
        while (term.StartsWith('(') && term.EndsWith(')')) term = term[1..^1].Trim();
        if (Literal.Match(term) is { Success: true } lit)
        {
            var v = Regex.Unescape(lit.Groups[1].Value);
            return new(IsAbsolute(v) || v.EndsWith("://", StringComparison.Ordinal) || bare ? v : null, null);
        }
        if (UriWrap.Match(term) is { Success: true } wrap)
        {
            var args = PolyText.SplitArgs(term, wrap.Index + wrap.Length - 1);
            return args.Count > 0 ? Resolve(args[0], lines, lineIndex, setting, depth + 1) : none;
        }
        if (Format.Match(term) is { Success: true } fm)
        {
            var args = PolyText.SplitArgs(term, fm.Index + fm.Length - 1);
            if (args.Count == 0 || Literal.Match(args[0]) is not { Success: true } f) return none;
            var format = f.Groups[1].Value;
            if (format.StartsWith("%s", StringComparison.Ordinal) && args.Count > 1)
            {
                var first = Resolve(args[1], lines, lineIndex, setting, depth + 1);
                var cut = format.IndexOf('%', 2);
                return first.Url is null ? first : first with { Url = JoinPath(first.Url, cut >= 0 ? format[2..cut] : format[2..]) };
            }
            var pct = format.IndexOf('%');
            return new(pct >= 0 ? format[..pct] : format, null);
        }
        if (GetenvDefault.Match(term) is { Success: true } gd) return new(IsAbsolute(gd.Groups[2].Value) ? gd.Groups[2].Value : null, gd.Groups[1].Value);
        if (Getenv.Match(term) is { Success: true } ge) return new(null, ge.Groups[1].Value);
        if (GetProperty.Match(term) is { Success: true } gp)
        {
            var key = gp.Groups[1].Value;
            var value = setting(key) ?? (gp.Groups[2].Success ? gp.Groups[2].Value : null);
            return new(value is not null && IsAbsolute(value) ? value : null, key);
        }
        if (Instances.Match(term) is { Success: true } inst) return new("http://" + inst.Groups[1].Value, null);
        var id = Identifier.Match(term);
        if (!id.Success) return none;
        var name = id.Groups[1].Value;
        if (name is "null" or "true" or "false" or "new") return none;
        var after = term[id.Length..].TrimStart();
        // 无参方法调用 getCustomerServiceUri()：取方法体里的 return 表达式
        if (after.StartsWith("()", StringComparison.Ordinal))
        {
            var body = ReturnOf(name, lines);
            return body is null ? none : Resolve(body.Value.Expr, lines, body.Value.Line, setting, depth + 1);
        }
        if (after.Length > 0 && !after.StartsWith('.')) return none;
        // @Value("${键:缺省}") 标在字段 / 构造参数上
        var valueAnn = new Regex(@"@Value\(\s*""\$\{([^}:]+)(?::([^}]*))?\}""\s*\)\s*(?:(?:private|protected|public|final|static)\s+)*(?:[\w.<>\[\]]+\s+)?" + Regex.Escape(name) + @"\b");
        for (var i = 0; i < lines.Length; i++)
        {
            var stmt = i + 1 < lines.Length ? lines[i] + " " + lines[i + 1].Trim() : lines[i];
            if (valueAnn.Match(stmt) is not { Success: true } va) continue;
            var key = va.Groups[1].Value;
            var value = setting(key) ?? (va.Groups[2].Success ? va.Groups[2].Value : null);
            return new(value is not null && IsAbsolute(value) ? value : null, key);
        }
        // 赋值：先往回找（局部变量），再全文件找（字段）
        var assign = new Regex(@"(?:^|[\s(,;])(?:this\s*\.\s*)?" + Regex.Escape(name) + @"\s*=(?!=)\s*(.+?);");
        foreach (var i in Enumerable.Range(0, lineIndex + 1).Reverse().Concat(Enumerable.Range(lineIndex + 1, Math.Max(0, lines.Length - lineIndex - 1))))
        {
            if (i >= lines.Length) continue;
            var line = lines[i];
            if (!line.Contains(name, StringComparison.Ordinal)) continue;
            var stmt = PolyText.Statement(lines, i, 3);
            if (assign.Match(stmt) is not { Success: true } am) continue;
            var r = Resolve(am.Groups[1].Value, lines, i, setting, depth + 1);
            if (r.Url is not null || r.ConfigKey is not null) return r;
        }
        return none;
    }

    /// <summary>无参方法 name() 的 return 表达式（方法头之后 12 行内的第一条 return）。</summary>
    private static (string Expr, int Line)? ReturnOf(string name, string[] lines)
    {
        var header = new Regex(@"\b" + Regex.Escape(name) + @"\s*\(\s*\)\s*(?:throws\s+[\w.,\s]+)?\{?\s*$");
        for (var i = 0; i < lines.Length; i++)
        {
            if (!header.IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("return", StringComparison.Ordinal) || !Regex.IsMatch(lines[i], @"^\s*(?:(?:private|protected|public|static|final)\s+)*[\w.<>\[\]]+\s+" + Regex.Escape(name))) continue;
            for (var k = i; k < lines.Length && k <= i + 12; k++)
                if (Regex.Match(PolyText.Statement(lines, k, 3), @"^\s*return\s+(.+?);") is { Success: true } r) return (r.Groups[1].Value, k);
        }
        return null;
    }
}

/// <summary>
/// Java 的 HTTP 出口：RestTemplate 的 getForObject / postForEntity / exchange …、WebClient.create(url) / .baseUrl(url) / .uri(url)、RestClient、
/// java.net.http 的 HttpRequest.newBuilder(URI) / .uri(URI)、OkHttp 的 .url(url)、@FeignClient 接口（每个映射方法一条，name 是目标服务名）、
/// DiscoveryClient.getInstances("服务名") / LoadBalancerClient.choose("服务名")。地址里的主机是服务名（lb://x、http://x/…）时由 EdgeMatcher 按容器名对上。
/// </summary>
public sealed class JavaOutboundRule : IDiscoveryRule
{
    public const string RuleId = "java-outbound";
    public string Id => RuleId;
    public string Description => "Java 的 RestTemplate / WebClient / RestClient / HttpClient / OkHttp 调用、@FeignClient 接口、DiscoveryClient 服务名查找";

    /// <param name="FileMustContain">文件里得出现的类型名（.uri( / .url( 这种通用调用只在用了对应客户端的文件里算）。</param>
    private sealed record Call(Regex Regex, string[]? FileMustContain, bool ByServiceName = false);

    private static readonly Call[] Calls =
    [
        new(new(@"\bWebClient\s*\.\s*create\s*\(\s*(?=\S)(?!\))", RegexOptions.Compiled), null),
        new(new(@"\.\s*baseUrl\s*\(", RegexOptions.Compiled), ["WebClient", "RestClient"]),
        new(new(@"\bHttpRequest\s*\.\s*newBuilder\s*\(\s*(?!\))", RegexOptions.Compiled), null),
        new(new(@"\b\w*[Tt]emplate\w*\s*\.\s*(?:getForObject|getForEntity|postForObject|postForEntity|postForLocation|put|delete|exchange|patchForObject|headForHeaders|optionsForAllow|execute)\s*\(", RegexOptions.Compiled), ["RestTemplate", "RestOperations"]),
        new(new(@"\.\s*uri\s*\(", RegexOptions.Compiled), ["WebClient", "RestClient", "HttpRequest", "RouteLocator"]),
        new(new(@"\.\s*url\s*\(", RegexOptions.Compiled), ["okhttp3"]),
        new(new(@"\.\s*(?:getInstances|choose)\s*\(\s*""[\w.\-]+""", RegexOptions.Compiled), ["DiscoveryClient", "LoadBalancerClient"], ByServiceName: true),
    ];

    private static readonly Regex FeignClient = new(@"@FeignClient\s*\(", RegexOptions.Compiled);
    private static readonly Regex Mapping = new(@"@(Get|Post|Put|Delete|Patch|Request)Mapping\b(\s*\()?", RegexOptions.Compiled);
    private static readonly string[] QuickKeywords = ["Template", "WebClient", "RestClient", "HttpRequest", "okhttp3", "DiscoveryClient", "LoadBalancerClient", "@FeignClient", "RouteLocator"];

    /// <summary>认出是 HTTP 调用但地址解析不出来的地方（诊断用）。</summary>
    public List<(string File, int Line)> Unresolved { get; } = [];

    public void Apply(RuleContext ctx)
    {
        Unresolved.Clear();
        foreach (var file in ctx.SourceFilesOf(".java"))
        {
            var lines = ctx.Lines(file);
            var text = string.Join('\n', lines);
            if (!QuickKeywords.Any(k => text.Contains(k, StringComparison.Ordinal))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var setting = SettingLookup(ctx, file);
            if (text.Contains("@FeignClient", StringComparison.Ordinal)) { Feign(ctx, file, lines, containers, setting); continue; }
            var calls = Calls.Where(c => c.FileMustContain is null || c.FileMustContain.Any(k => text.Contains(k, StringComparison.Ordinal))).ToList();
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith("import ", StringComparison.Ordinal)) continue;
                var matched = false;
                AddressResolver.Address? found = null;
                foreach (var call in calls)
                {
                    var m = call.Regex.Match(lines[i]);
                    if (!m.Success || PolyText.InCommentOrString(lines[i], m.Index, SourceLang.Java)) continue;
                    matched = true;
                    var stmt = PolyText.Statement(lines, i, 3);
                    var address = call.ByServiceName
                        ? JavaAddress.Resolve(lines[i][m.Index..], lines, i, setting)
                        : JavaAddress.Resolve(FirstArg(stmt, stmt.IndexOf('(', m.Index)), lines, i, setting);
                    if (address.Url is null && address.ConfigKey is null) continue;
                    found = address;
                    break; // 一行只登记一个调用：WebClient.create(地址).get().uri("/相对") 取前者
                }
                if (found is { } a) foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, "http", null, a.ConfigKey, a.Url, file, i + 1, null));
                else if (matched) Unresolved.Add((file, i + 1));
            }
        }
    }

    private static string FirstArg(string stmt, int open) => open < 0 ? "" : PolyText.SplitArgs(stmt, open).FirstOrDefault() ?? "";

    /// <summary>文件所在 Java 模块的 Spring 配置查值（主配置优先，再任一 profile），去掉占位符。</summary>
    internal static Func<string, string?> SettingLookup(RuleContext ctx, string file)
    {
        var module = ctx.Inventory.JavaModules.Where(m => RuleContext.Under(file, m.Dir)).OrderByDescending(m => m.Dir.Length).FirstOrDefault();
        return key =>
        {
            if (module is null) return null;
            var hits = module.Config.Where(c => SpringConfigEntry.SameKey(c.Key, key)).ToList();
            return SpringConfigReader.Resolve((hits.FirstOrDefault(c => c.Profile is null) ?? hits.FirstOrDefault())?.Value);
        };
    }

    /// <summary>@FeignClient(name / value = "服务", url = "…", path = "…") 接口：每个 @XxxMapping 方法一条出口；没有映射方法时在注解处登记一条。</summary>
    private void Feign(RuleContext ctx, string file, string[] lines, IReadOnlyList<ArchNode> containers, Func<string, string?> setting)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var fm = FeignClient.Match(lines[i]);
            if (!fm.Success || PolyText.InCommentOrString(lines[i], fm.Index, SourceLang.Java)) continue;
            var args = PolyText.SplitArgs(PolyText.Statement(lines, i, 4), lines[i].IndexOf('(', fm.Index));
            var name = Attr(args, "name") ?? Attr(args, "value") ?? args.Select(a => Regex.Match(a, @"^""([^""]*)""$")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
            var urlAttr = Attr(args, "url");
            var classPath = Attr(args, "path") ?? "";
            string? configKey = null;
            string? baseUrl = null;
            if (urlAttr is { Length: > 0 })
            {
                var ph = Regex.Match(urlAttr, @"^\$\{([^}:]+)(?::([^}]*))?\}$");
                if (ph.Success) { configKey = ph.Groups[1].Value; baseUrl = setting(configKey) ?? (ph.Groups[2].Success ? ph.Groups[2].Value : null); }
                else baseUrl = urlAttr;
                if (baseUrl is not null && !JavaAddress.IsAbsolute(baseUrl)) baseUrl = "http://" + baseUrl;
            }
            else if (name is { Length: > 0 } && !name.StartsWith("${", StringComparison.Ordinal)) baseUrl = "http://" + name;
            if (baseUrl is null && configKey is null) { Unresolved.Add((file, i + 1)); continue; }
            baseUrl = baseUrl is null ? null : JavaAddress.JoinPath(baseUrl, classPath);
            var end = Array.FindIndex(lines, i + 1, l => FeignClient.IsMatch(l));
            var any = false;
            for (var k = i + 1; k < (end < 0 ? lines.Length : end); k++)
            {
                var mm = Mapping.Match(lines[k]);
                if (!mm.Success || PolyText.InCommentOrString(lines[k], mm.Index, SourceLang.Java)) continue;
                var path = "";
                if (mm.Groups[2].Success)
                {
                    var margs = PolyText.SplitArgs(PolyText.Statement(lines, k, 3), lines[k].IndexOf('(', mm.Index));
                    path = Attr(margs, "value") ?? Attr(margs, "path") ?? margs.Select(a => Regex.Match(a, @"^\{?\s*""([^""]*)""")).FirstOrDefault(x => x.Success)?.Groups[1].Value ?? "";
                }
                any = true;
                foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, "http", name, configKey, baseUrl is null ? null : JavaAddress.JoinPath(baseUrl, path), file, k + 1, null));
            }
            if (!any) foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, "http", name, configKey, baseUrl, file, i + 1, null));
        }
    }

    /// <summary>注解实参 name = "x" / name = {"x", …}。</summary>
    private static string? Attr(IEnumerable<string> args, string name)
    {
        foreach (var a in args)
            if (Regex.Match(a, @"^\s*" + Regex.Escape(name) + @"\s*=\s*\{?\s*""([^""]*)""") is { Success: true } m) return m.Groups[1].Value;
        return null;
    }
}

/// <summary>
/// Spring Cloud 的服务间关系（不经业务代码就能看出的）：
/// Gateway 路由（spring.cloud.gateway[.server.webflux|.mvc].routes[i].uri = lb://服务 / http://…）→ 出口，ConfigKey 记路由 id、ClientName 记 Path 断言；
/// Eureka 客户端（spring-cloud-starter-netflix-eureka-client）→ 仓库里的 Eureka 服务端容器（依赖 eureka-server 或标了 @EnableEurekaServer），
/// 有 eureka.client.service-url.defaultZone 就按地址对；仓库里没有服务端就挂"Eureka 服务注册中心"外部系统；
/// 配置中心客户端（spring.config.import 的 configserver:地址 / spring.cloud.config.uri / spring-cloud-starter-config）→ 同理对 Config Server 容器或"Spring Cloud Config 配置中心"。
/// </summary>
public sealed class SpringCloudRule : IDiscoveryRule
{
    public const string RuleId = "spring-cloud";
    public string Id => RuleId;
    public string Description => "Spring Cloud Gateway 路由、Eureka 注册、配置中心客户端";

    private static readonly Regex RouteUri = new(@"^(spring\.cloud\.gateway\.(?:[\w\-]+\.)*routes)\[(\d+)\]\.uri$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string EurekaClient = "org.springframework.cloud:spring-cloud-starter-netflix-eureka-client";
    private const string EurekaServer = "org.springframework.cloud:spring-cloud-starter-netflix-eureka-server";
    private const string ConfigServer = "org.springframework.cloud:spring-cloud-config-server";
    private static readonly string[] ConfigClients = ["org.springframework.cloud:spring-cloud-starter-config", "org.springframework.cloud:spring-cloud-config-client"];

    public void Apply(RuleContext ctx)
    {
        var modules = ctx.Inventory.JavaModules.ToDictionary(m => m.Dir, StringComparer.Ordinal);
        var java = ctx.Containers.Where(c => c.SubKind == "java-service" && c.Path is not null && modules.ContainsKey(c.Path)).ToList();
        var registries = java.Where(c => modules[c.Path!].HasDependency(EurekaServer) || modules[c.Path!].Markers.Any(m => m.Note == "@EnableEurekaServer")).ToList();
        var configServers = java.Where(c => modules[c.Path!].HasDependency(ConfigServer) || modules[c.Path!].Markers.Any(m => m.Note == "@EnableConfigServer")).ToList();
        foreach (var c in java)
        {
            var module = modules[c.Path!];
            // 成员里的模块（自己 + 依赖的库模块）的依赖都算这个容器的
            var memberModules = ctx.Inventory.JavaModules.Where(m => !m.IsTest && c.Members.Any(x => x == m.Dir || RuleContext.Under(x, m.Dir)) && (m.Dir == module.Dir || module.ModuleRefs.Contains(m.Dir) || Reachable(module, m.Dir, modules))).ToList();
            Routes(ctx, c, module);
            var eureka = memberModules.SelectMany(m => m.Dependencies).FirstOrDefault(d => d.Name.Equals(EurekaClient, StringComparison.OrdinalIgnoreCase));
            if (eureka is not null && !registries.Any(r => r.Key == c.Key))
            {
                var zone = Setting(module, "eureka.client.service-url.defaultZone") ?? Setting(module, "eureka.client.serviceUrl.defaultZone");
                var url = zone is null ? null : SpringConfigReader.Resolve(zone.Value)?.Split(',')[0].Trim();
                if (url is not null && JavaAddress.IsAbsolute(url)) ctx.Outbound.Add(new Outbound(c.Key, Id, "http", "Eureka", zone!.Key, url, zone.File, zone.Line, null));
                else ctx.Outbound.Add(new Outbound(c.Key, Id, "http", "Eureka", null, null, eureka.File, eureka.Line,
                    Target(ctx, registries, c, "service-registry", "Eureka 服务注册中心", new Evidence(eureka.File, eureka.Line, eureka.Name))));
            }
            if (configServers.Any(s => s.Key == c.Key)) continue;
            var import = ConfigImport(module);
            if (import is not null) { ctx.Outbound.Add(new Outbound(c.Key, Id, "http", "Spring Cloud Config", import.Value.Entry.Key, import.Value.Url, import.Value.Entry.File, import.Value.Entry.Line, null)); continue; }
            var client = memberModules.SelectMany(m => m.Dependencies).FirstOrDefault(d => ConfigClients.Any(x => d.Name.Equals(x, StringComparison.OrdinalIgnoreCase)));
            if (client is not null)
                ctx.Outbound.Add(new Outbound(c.Key, Id, "http", "Spring Cloud Config", null, null, client.File, client.Line,
                    Target(ctx, configServers, c, "config-center", "Spring Cloud Config 配置中心", new Evidence(client.File, client.Line, client.Name))));
        }
    }

    private static bool Reachable(JavaModule from, string dir, Dictionary<string, JavaModule> modules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(from.ModuleRefs);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (!seen.Add(d)) continue;
            if (d == dir) return true;
            if (modules.TryGetValue(d, out var m)) foreach (var r in m.ModuleRefs) queue.Enqueue(r);
        }
        return false;
    }

    /// <summary>仓库里唯一的服务端容器（不是自己）→ 它的键；没有 / 多个 → 外部系统。</summary>
    private static string Target(RuleContext ctx, List<ArchNode> servers, ArchNode self, string subKind, string name, Evidence evidence)
    {
        var others = servers.Where(s => s.Key != self.Key).ToList();
        return others.Count == 1 ? others[0].Key : ctx.External(subKind, name, evidence);
    }

    private static SpringConfigEntry? Setting(JavaModule module, string key) =>
        module.Setting(key) ?? module.Config.FirstOrDefault(c => SpringConfigEntry.SameKey(c.Key, key));

    /// <summary>spring.config.import 里的 configserver:地址（主配置优先，再任一 profile），或 spring.cloud.config.uri。</summary>
    private static (SpringConfigEntry Entry, string Url)? ConfigImport(JavaModule module)
    {
        foreach (var e in module.Config.Where(c => SpringConfigEntry.SameKey(c.Key, "spring.config.import")).OrderBy(c => c.Profile is null ? 0 : 1))
            foreach (var part in SplitTopLevel(e.Value))
            {
                var p = part.Trim();
                if (p.StartsWith("optional:", StringComparison.OrdinalIgnoreCase)) p = p["optional:".Length..];
                if (!p.StartsWith("configserver:", StringComparison.OrdinalIgnoreCase)) continue;
                var url = SpringConfigReader.Resolve(p["configserver:".Length..]);
                if (url is not null && JavaAddress.IsAbsolute(url)) return (e, url);
            }
        if (Setting(module, "spring.cloud.config.uri") is { } uri && SpringConfigReader.Resolve(uri.Value) is { } u && JavaAddress.IsAbsolute(u)) return (uri, u);
        return null;
    }

    /// <summary>按不在 ${…} 里的逗号切分。</summary>
    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '{') depth++;
            else if (value[i] == '}') depth--;
            else if (value[i] == ',' && depth == 0) { yield return value[start..i]; start = i + 1; }
        }
        yield return value[start..];
    }

    /// <summary>网关路由：每条 routes[i].uri 一个出口（forward: / no:// 之类不出网的跳过）。各 profile 的同 id 路由只记一次。</summary>
    private void Routes(RuleContext ctx, ArchNode container, JavaModule module)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in module.Config.OrderBy(c => c.Profile is null ? 0 : 1))
        {
            var m = RouteUri.Match(e.Key);
            if (!m.Success) continue;
            var url = SpringConfigReader.Resolve(e.Value);
            if (url is null || !JavaAddress.IsAbsolute(url)) continue;
            var prefix = $"{m.Groups[1].Value}[{m.Groups[2].Value}]";
            var doc = module.Config.Where(c => c.File == e.File && c.Profile == e.Profile).ToList();
            var id = doc.FirstOrDefault(c => c.Key.Equals(prefix + ".id", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!seen.Add(id ?? url)) continue;
            var path = doc.FirstOrDefault(c => c.Key.StartsWith(prefix + ".predicates[", StringComparison.OrdinalIgnoreCase) && c.Value.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))?.Value;
            ctx.Outbound.Add(new Outbound(container.Key, Id, "http", path, id, url, e.File, e.Line, null));
        }
    }
}
