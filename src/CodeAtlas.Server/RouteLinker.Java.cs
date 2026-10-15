using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Java 出口与服务目录：
/// RestTemplate（getForObject / postForObject / exchange(…, HttpMethod.X) …，接收者名字带 rest / template）与 WebClient / RestClient 的 .uri(…) 的 URL 实参
/// （字面量、字段 / 常量拼接，未知变量成 ${x} 孔；相对路径用文件里唯一的 .baseUrl("…") 补全）；@FeignClient 接口方法（name / url 定目标服务，path 与映射注解拼路径，
/// 行号是接口方法的声明行——时序图走到这个接口方法就在这里续接）；gRPC 的 XxxGrpc.newBlockingStub(channel)。
/// 绝对地址（http://customers-service/…、lb://…）的主机名按 <see cref="ServiceDirectory"/> 对容器：对不上就是外部系统，不匹配。
/// </summary>
public static partial class RouteLinker
{
    [GeneratedRegex(@"\.\s*uri\s*\(")]
    private static partial Regex JavaUriCall();
    [GeneratedRegex(@"\b\w*(?:[Rr]est|[Tt]emplate)\w*\s*\.\s*(getForObject|getForEntity|postForObject|postForEntity|postForLocation|put|delete|patchForObject|exchange|execute)\s*\(")]
    private static partial Regex JavaRestTemplateCall();
    [GeneratedRegex(@"\.\s*(get|post|put|delete|patch|head|options)\s*\(\s*\)")]
    private static partial Regex JavaWebClientVerb();
    [GeneratedRegex(@"\bHttpMethod\s*\.\s*(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b")]
    private static partial Regex JavaHttpMethod();
    [GeneratedRegex(@"\.\s*baseUrl\s*\(\s*""([^""]+)""|\bWebClient\s*\.\s*create\s*\(\s*""([^""]+)""")]
    private static partial Regex JavaBaseUrl();
    [GeneratedRegex(@"^(?:lb|https?)://([^/:?#]+)(?::\d+)?(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrl();
    // gRPC 客户端：AdServiceGrpc.newBlockingStub(channel) / newFutureStub / newStub
    [GeneratedRegex(@"\b(\w+)Grpc\s*\.\s*new(?:Blocking|Future)?Stub\s*\(")]
    private static partial Regex JavaStubCtor();
    [GeneratedRegex(@"(?:\bthis\s*\.\s*)?(\w+)\s*=\s*(?:[\w.]+\.)?(\w+)Grpc\s*\.\s*new(?:Blocking|Future)?Stub\s*\(")]
    private static partial Regex JavaStubVar();

    // 脚本里的调用：第一个实参是 '…' / "…" / `…` 串
    [GeneratedRegex(@"([A-Za-z_$][\w$]*)\s*\(\s*(['""`])(/[^'""`\s]*)\2")]
    private static partial Regex ScriptPathCall();
    [GeneratedRegex(@"['""`](GET|POST|PUT|DELETE|PATCH)['""`]", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptVerbLiteral();
    private static readonly HashSet<string> ScriptVerbFunctions = new(StringComparer.OrdinalIgnoreCase) { "get", "post", "put", "delete", "patch" };
    private static readonly HashSet<string> ScriptNonCalls = new(StringComparer.Ordinal) { "import", "require", "if", "while", "for", "switch", "return" };

    /// <summary>脚本文件里第一个实参是 API 路径串的调用：(行号, 路径, 方法)。方法取函数名（post / put …）或后面实参里的 'PUT' 之类字面量，都没有算 GET；注释行跳过。</summary>
    private static IEnumerable<(int Line, string Path, string? Method)> ScriptApiCalls(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*') || t.StartsWith("/*", StringComparison.Ordinal)) continue;
            foreach (Match m in ScriptPathCall().Matches(lines[i]))
            {
                if (ScriptNonCalls.Contains(m.Groups[1].Value)) continue;
                var comment = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0 && comment < m.Index && !lines[i][..comment].Contains("://", StringComparison.Ordinal)) continue;
                if (NormalizePath(m.Groups[3].Value, allowLeadingHole: false) is not { } path) continue;
                var chunk = string.Join('\n', lines[i..Math.Min(lines.Length, i + 6)]);
                var open = chunk.IndexOf('(', m.Index + m.Groups[1].Length);
                var (args, _) = ArgParser.SplitArgs(chunk, open);
                // 没写方法 = GET（fetch 与常见包装的缺省，与容器图规则对 fetch 的处理一致）
                var method = ScriptVerbFunctions.Contains(m.Groups[1].Value) ? m.Groups[1].Value.ToUpperInvariant()
                    : args.Skip(1).Select(a => ScriptVerbLiteral().Match(a)).FirstOrDefault(x => x.Success)?.Groups[1].Value.ToUpperInvariant() ?? "GET";
                yield return (i + 1, path, method);
            }
        }
    }

    private static readonly Dictionary<string, string?> RestTemplateVerbs = new(StringComparer.Ordinal)
    {
        ["getForObject"] = "GET", ["getForEntity"] = "GET", ["postForObject"] = "POST", ["postForEntity"] = "POST", ["postForLocation"] = "POST",
        ["put"] = "PUT", ["delete"] = "DELETE", ["patchForObject"] = "PATCH", ["exchange"] = null, ["execute"] = null,
    };

    /// <param name="Host">URL 里的主机名（目标服务）；相对路径为 null。</param>
    private sealed record JavaCall(int Line, string Path, string? Method, string? Host);

    private static IEnumerable<JavaCall> JavaCalls(string file, IEnumerable<MapSymbol> methods, IReadOnlyList<MapSymbol> symbols, Func<string, string[]> readLines)
    {
        var lines = readLines(file);
        var whole = string.Join('\n', lines);
        var hasClient = JavaUriCall().IsMatch(whole) || JavaRestTemplateCall().IsMatch(whole);
        var hasFeign = whole.Contains("@FeignClient", StringComparison.Ordinal);
        if (!hasClient && !hasFeign) yield break;
        var fields = JavaSource.StringConstants(lines);
        string? Field(string name) => fields.GetValueOrDefault(name);

        if (hasClient)
        {
            var bases = JavaBaseUrl().Matches(whole).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Distinct(StringComparer.Ordinal).ToList();
            var baseUrl = bases.Count == 1 ? bases[0] : null; // 多个不同的 baseUrl 不猜
            foreach (var method in methods.Where(m => m.Kind is SymbolKind.Method or SymbolKind.Function))
            {
                var start = Math.Max(0, method.StartLine - 1);
                var end = Math.Min(lines.Length, method.EndLine);
                if (end <= start) continue;
                var body = string.Join('\n', lines[start..end]);
                int LineAt(int index) => start + 1 + body.AsSpan(0, index).Count('\n');
                foreach (Match m in JavaUriCall().Matches(body))
                {
                    var (args, _) = ArgParser.SplitArgs(body, m.Index + m.Length - 1);
                    if (args.Count == 0 || JavaSource.Eval(args[0], Field, holes: true) is not { } url || ParseJavaUrl(url, baseUrl) is not { } target) continue;
                    yield return new JavaCall(LineAt(m.Index), target.Path, WebClientVerb(body, m.Index), target.Host);
                }
                foreach (Match m in JavaRestTemplateCall().Matches(body))
                {
                    var (args, _) = ArgParser.SplitArgs(body, m.Index + m.Length - 1);
                    if (args.Count == 0 || JavaSource.Eval(args[0], Field, holes: true) is not { } url || ParseJavaUrl(url, baseUrl) is not { } target) continue;
                    var verb = RestTemplateVerbs[m.Groups[1].Value] ?? (args.Count > 1 && JavaHttpMethod().Match(args[1]) is { Success: true } hm ? hm.Groups[1].Value : null);
                    yield return new JavaCall(LineAt(m.Index), target.Path, verb, target.Host);
                }
            }
        }

        if (hasFeign)
        {
            var annotations = JavaSource.Scan(lines);
            string? Constant(string name) => fields.GetValueOrDefault(name);
            foreach (var iface in symbols.Where(s => s.Path == file && s.Kind == SymbolKind.Interface))
            {
                var typeAnnotations = annotations.Of(iface.StartLine);
                if (typeAnnotations.FirstOrDefault(a => a.Name == "FeignClient") is not { } feign) continue;
                var name = JavaSource.Values(feign.Args, Constant, "name", "value", "serviceId").Select(v => v.Value).FirstOrDefault();
                var prefix = JavaSource.Keyword(feign.Args, "path") is { } p ? JavaSource.Eval(p, Constant) ?? "" : "";
                string? host = name;
                if (JavaSource.Keyword(feign.Args, "url") is { } u && JavaSource.Eval(u, Constant) is { } url && ParseJavaUrl(url, null) is { Host: not null } abs)
                {
                    host = abs.Host;
                    prefix = EntryDiscovery.JoinRoute(abs.Path, prefix);
                }
                if (host is null || host.Contains("${", StringComparison.Ordinal)) continue;
                var classPrefix = typeAnnotations.FirstOrDefault(a => a.Name == "RequestMapping") is { } rm
                    ? JavaSource.Values(rm.Args, Constant, "value", "path").Select(v => v.Value).FirstOrDefault() ?? "" : "";
                foreach (var m in symbols.Where(s => s.ParentId == iface.Id && s.Kind == SymbolKind.Method).OrderBy(s => s.StartLine))
                    foreach (var a in annotations.Of(m.StartLine).Where(JavaSource.IsSpringMapping))
                        foreach (var (path, verb) in JavaSource.SpringMapping(a, Constant))
                            yield return new JavaCall(m.StartLine, EntryDiscovery.JoinRoute(prefix, classPrefix, path), verb, host);
            }
        }
    }

    /// <summary>WebClient 链上 .uri( 之前、同一语句里最近的 .get() / .post() …；或 .method(HttpMethod.X)。</summary>
    private static string? WebClientVerb(string body, int uriIndex)
    {
        var stmt = Math.Max(body.LastIndexOf(';', Math.Max(0, uriIndex - 1)), body.LastIndexOf('{', Math.Max(0, uriIndex - 1)));
        var segment = body[(stmt + 1)..uriIndex];
        var verbs = JavaWebClientVerb().Matches(segment);
        if (verbs.Count > 0) return verbs[^1].Groups[1].Value.ToUpperInvariant();
        return JavaHttpMethod().Match(segment) is { Success: true } hm ? hm.Groups[1].Value : null;
    }

    /// <summary>
    /// 求值后的 URL → (主机, 路径)：http(s):// / lb:// 取主机名（去端口）；开头的 ${x} 孔（不知道的基地址）后跟 / 时丢掉；相对路径有 baseUrl 时拼上；
    /// 去掉查询串与片段。主机或路径以未知变量开头 → null。
    /// </summary>
    private static (string? Host, string Path)? ParseJavaUrl(string url, string? baseUrl)
    {
        url = url.Trim();
        var abs = AbsoluteUrl().Match(url);
        string? host = null;
        string path;
        if (abs.Success) { host = abs.Groups[1].Value; path = abs.Groups[2].Value; }
        else if (Regex.Match(url, @"^(?:\$\{[^}]*\})+(?=/)") is { Success: true } hole) path = url[hole.Length..];
        else if (baseUrl is not null && !url.Contains("://", StringComparison.Ordinal)) return ParseJavaUrl(baseUrl.TrimEnd('/') + "/" + url.TrimStart('/'), null);
        else if (url.StartsWith('/')) path = url;
        else return null;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        path = "/" + path.Trim('/');
        if (path.StartsWith("/${", StringComparison.Ordinal) || host?.Contains('$') == true) return null;
        return (host, path);
    }

    /// <summary>
    /// 服务目录：主机名 / 服务名 → 容器（容器名、容器键 / 目录的最后一段、"-服务名"后缀如 spring-petclinic-customers-service、成员项目
    /// src/main/resources 下 application / bootstrap 配置里的 spring.application.name），以及这些配置里的 Spring Cloud Gateway 路由表。都在第一次用到时才读文件。
    /// </summary>
    internal sealed class ServiceDirectory(IReadOnlyList<ArchNode> containers, Func<string, string[]> readLines)
    {
        private static readonly string[] ConfigFiles = ["application.yml", "application.yaml", "application.properties", "bootstrap.yml", "bootstrap.yaml", "bootstrap.properties"];
        private Dictionary<string, HashSet<string>>? _appNames;
        private List<GatewayRoute>? _routes;
        private readonly Dictionary<string, ArchNode?> _byHost = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GatewayRoute> GatewayRoutes { get { Load(); return _routes!; } }

        public ArchNode? ContainerOf(string host)
        {
            if (_byHost.TryGetValue(host, out var cached)) return cached;
            Load();
            static string Last(string s) => s.TrimEnd('/')[(s.TrimEnd('/').LastIndexOf('/') + 1)..];
            var list = containers.Where(c => c.Kind == ArchNodeKind.Container).ToList();
            var exact = list.Where(c => c.Name.Equals(host, StringComparison.OrdinalIgnoreCase) || Last(c.Key).Equals(host, StringComparison.OrdinalIgnoreCase)
                                        || (c.Path is not null && Last(c.Path).Equals(host, StringComparison.OrdinalIgnoreCase)) || _appNames![c.Key].Contains(host)).ToList();
            var found = exact.Count == 1 ? exact[0] : null;
            if (exact.Count == 0)
            {
                var suffix = list.Where(c => new[] { c.Name, Last(c.Key), c.Path is null ? "" : Last(c.Path) }
                    .Any(n => n.EndsWith("-" + host, StringComparison.OrdinalIgnoreCase) || n.EndsWith("_" + host, StringComparison.OrdinalIgnoreCase) || n.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))).ToList();
                found = suffix.Count == 1 ? suffix[0] : null;
            }
            return _byHost[host] = found;
        }

        private void Load()
        {
            if (_appNames is not null) return;
            _appNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            _routes = [];
            foreach (var c in containers)
            {
                var names = _appNames[c.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (c.Kind != ArchNodeKind.Container) continue;
                foreach (var member in c.Members.Distinct(StringComparer.Ordinal))
                    foreach (var f in ConfigFiles)
                    {
                        var path = (member.Length == 0 ? "" : member.TrimEnd('/') + "/") + "src/main/resources/" + f;
                        var lines = readLines(path);
                        if (lines.Length == 0) continue;
                        var properties = f.EndsWith(".properties", StringComparison.Ordinal);
                        if (SpringConfig.ApplicationName(lines, properties) is { } n) names.Add(n);
                        _routes.AddRange(SpringConfig.GatewayRoutes(lines, properties, c.Key));
                    }
            }
        }
    }

    /// <param name="Patterns">Path 谓词（/api/customer/**），可以多个。</param>
    /// <param name="TargetHost">uri 的主机名（lb://customers-service → customers-service）。</param>
    internal sealed record GatewayRoute(string ContainerKey, IReadOnlyList<string> Patterns, string TargetHost, int StripPrefix, string? PrefixPath)
    {
        /// <summary>路径落在某个 Path 谓词里 → 网关转发给目标服务的路径（剥 StripPrefix 段，补 PrefixPath）；不在 → null。</summary>
        public string? Rewrite(string path)
        {
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            if (!Patterns.Any(p => PathMatches(p, path))) return null;
            var rest = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(StripPrefix));
            return PrefixPath is null ? rest : EntryDiscovery.JoinRoute(PrefixPath, rest);
        }
    }

    /// <summary>Spring Boot 配置文件（application.yml / .properties）里认得的两样：spring.application.name 与 Spring Cloud Gateway 的 routes。YAML 只按缩进跟踪键路径，不做完整解析。</summary>
    internal static partial class SpringConfig
    {
        [GeneratedRegex(@"^(\s*)([\w.\-]+)\s*:\s*(.*?)\s*$")]
        private static partial Regex YamlKey();
        [GeneratedRegex(@"(?<![\w])Path\s*=\s*(.+?)\s*$")]
        private static partial Regex PathPredicate();
        [GeneratedRegex(@"\bStripPrefix\s*=\s*(\d+)")]
        private static partial Regex StripPrefix();
        [GeneratedRegex(@"\bPrefixPath\s*=\s*(\S+)")]
        private static partial Regex PrefixPath();
        [GeneratedRegex(@"^\s*uri\s*:\s*(\S+)")]
        private static partial Regex UriKey();
        [GeneratedRegex(@"^\s*spring\.cloud\.gateway\.(?:server\.\w+\.)?routes\[(\d+)\]\.(uri|predicates\[\d+\]|filters\[\d+\])\s*[=:]\s*(.+?)\s*$")]
        private static partial Regex PropertiesRoute();

        public static string? ApplicationName(string[] lines, bool properties)
        {
            if (properties)
            {
                foreach (var l in lines)
                    if (Regex.Match(l, @"^\s*spring\.application\.name\s*[=:]\s*(\S+)\s*$") is { Success: true } m && !m.Groups[1].Value.Contains('$')) return m.Groups[1].Value;
                return null;
            }
            foreach (var (path, value, _) in YamlScalars(lines))
                if (path == "spring.application.name" && value.Length > 0 && !value.Contains('$')) return value;
            return null;
        }

        /// <summary>YAML 的 (键路径, 标量值, 行号)：按缩进维护键栈；列表项（- …）不进路径；--- 文档分隔重置。</summary>
        private static IEnumerable<(string Path, string Value, int Line)> YamlScalars(string[] lines)
        {
            var stack = new List<(int Indent, string Key)>();
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.TrimStart().StartsWith('#') || l.Trim().Length == 0) continue;
                if (l.StartsWith("---", StringComparison.Ordinal)) { stack.Clear(); continue; }
                var m = YamlKey().Match(l);
                if (!m.Success || l.TrimStart().StartsWith('-')) continue;
                var indent = m.Groups[1].Value.Length;
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                var key = m.Groups[2].Value;
                var full = string.Join('.', stack.Select(s => s.Key).Append(key));
                var value = m.Groups[3].Value.Trim().Trim('"', '\'');
                if (value.Length == 0) stack.Add((indent, key));
                else yield return (full, value, i + 1);
            }
        }

        public static IEnumerable<GatewayRoute> GatewayRoutes(string[] lines, bool properties, string containerKey)
        {
            if (properties)
            {
                var byIndex = new SortedDictionary<int, (string? Uri, List<string> Patterns, int Strip, string? Prefix)>();
                foreach (var l in lines)
                {
                    var m = PropertiesRoute().Match(l);
                    if (!m.Success) continue;
                    var idx = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var r = byIndex.TryGetValue(idx, out var cur) ? cur : (null, [], 0, null);
                    var value = m.Groups[3].Value;
                    if (m.Groups[2].Value == "uri") r.Uri = value;
                    else Collect(value, ref r.Patterns, ref r.Strip, ref r.Prefix);
                    byIndex[idx] = r;
                }
                foreach (var r in byIndex.Values)
                    if (Make(containerKey, r.Uri, r.Patterns, r.Strip, r.Prefix) is { } route) yield return route;
                yield break;
            }

            // YAML：键路径里有 gateway 的 routes: 列表；列表项缩进相同的 "- " 开新路由
            var stack = new List<(int Indent, string Key)>();
            var routesIndent = -1;
            var itemIndent = -1;
            string? uri = null;
            var patterns = new List<string>();
            int strip = 0;
            string? prefix = null;
            var result = new List<GatewayRoute>();
            void Flush()
            {
                if (Make(containerKey, uri, patterns, strip, prefix) is { } route) result.Add(route);
                uri = null; patterns = []; strip = 0; prefix = null;
            }
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var indent = raw.Length - raw.TrimStart().Length;
                if (routesIndent >= 0)
                {
                    var ended = raw.StartsWith("---", StringComparison.Ordinal) || indent < routesIndent || (indent == routesIndent && !t.StartsWith('-'));
                    if (!ended)
                    {
                        if (t.StartsWith('-') && (itemIndent < 0 || indent == itemIndent))
                        {
                            if (itemIndent >= 0) Flush();
                            itemIndent = indent;
                            t = t[1..].Trim();
                        }
                        else if (t.StartsWith('-')) t = t[1..].Trim();
                        if (UriKey().Match(t) is { Success: true } u) uri = u.Groups[1].Value;
                        else Collect(t, ref patterns, ref strip, ref prefix);
                        continue;
                    }
                    Flush();
                    routesIndent = itemIndent = -1;
                }
                if (raw.StartsWith("---", StringComparison.Ordinal)) { stack.Clear(); continue; }
                var m = YamlKey().Match(raw);
                if (!m.Success || t.StartsWith('-')) continue;
                while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
                var key = m.Groups[2].Value;
                if (m.Groups[3].Value.Length > 0) continue;
                if (key == "routes" && stack.Any(s => s.Key == "gateway" || s.Key.EndsWith(".gateway", StringComparison.Ordinal) || s.Key.Contains(".gateway.", StringComparison.Ordinal))) { routesIndent = indent; itemIndent = -1; continue; }
                stack.Add((indent, key));
            }
            if (routesIndent >= 0) Flush();
            foreach (var r in result) yield return r;
        }

        private static void Collect(string text, ref List<string> patterns, ref int strip, ref string? prefix)
        {
            if (PathPredicate().Match(text) is { Success: true } p)
                patterns.AddRange(p.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Trim('"', '\'')));
            else if (StripPrefix().Match(text) is { Success: true } s) strip = int.Parse(s.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else if (PrefixPath().Match(text) is { Success: true } pp) prefix = pp.Groups[1].Value.Trim('"', '\'');
        }

        private static GatewayRoute? Make(string containerKey, string? uri, List<string> patterns, int strip, string? prefix)
        {
            if (uri is null || patterns.Count == 0 || AbsoluteUrl().Match(uri.Trim('"', '\'')) is not { Success: true } u) return null;
            return new GatewayRoute(containerKey, patterns.ToList(), u.Groups[1].Value, strip, prefix);
        }
    }
}
