using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 出口路径 ↔ 入口路由匹配。出口有这几种：前端 HTTP 调用（容器图扫描已登记，经代理的按 ClientName 里的前缀剥掉、按其中的方法挑入口）；
/// 前端生成的 SDK（hey-api / openapi 生成器的 <c>client.get({ url: '/api/…' })</c>，容器图规则认不出接收者是表达式的调用）；
/// C# / Go / Python 里同一方法既调了 HTTP 客户端又出现了路径字面量；Go / Python / Java 的 gRPC 客户端调用（<c>NewXxxClient(conn).Method(</c> / <c>XxxStub(channel)</c> /
/// <c>XxxGrpc.newBlockingStub(channel)</c>）；Java 的 RestTemplate / WebClient / @FeignClient（见 RouteLinker.Java.cs，主机名点名目标服务）；
/// 后端容器里的静态脚本（不在前端容器里）以 /api 开头的调用实参。
/// 直接对不上时试 Spring Cloud Gateway 路由（网关容器 application.yml 的 Path 谓词 + StripPrefix / PrefixPath → 目标服务），见 <see cref="ServiceDirectory"/>。
/// 对上就是跨容器的 0.9 边，也是时序图续接的依据。gRPC 的匹配 Path 记成 <c>grpc:Service/Method</c>。
/// </summary>
public static partial class RouteLinker
{
    /// <summary>gRPC 匹配的 Path 前缀：grpc:ProductCatalogService/ListProducts。</summary>
    public const string GrpcPathPrefix = "grpc:";

    [GeneratedRegex(@"\.(GetAsync|PostAsync|PutAsync|DeleteAsync|PatchAsync|SendAsync|PostAsJsonAsync|PutAsJsonAsync|GetFromJsonAsync|GetStringAsync|GetStreamAsync)\s*[<(]")]
    private static partial Regex HttpCall();
    // Go：net/http 的包级函数与 NewRequest，或 client.Do(req)
    [GeneratedRegex(@"\bhttp\.(?:Get|Post|Head|PostForm|NewRequest|NewRequestWithContext)\s*\(|\.Do\s*\(")]
    private static partial Regex GoHttpCall();
    // Python：requests / httpx / aiohttp 与名字像会话 / 客户端的变量
    [GeneratedRegex(@"\b(?:requests|httpx|aiohttp|\w*[sS]ession|\w*[cC]lient)\s*\.\s*(?:get|post|put|patch|delete|head|options|request)\s*\(")]
    private static partial Regex PyHttpCall();
    [GeneratedRegex(@"(\$?)""([^""]*)""")]
    private static partial Regex StringLiteral();
    // Go 的解释串与原始串（反引号）
    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""|`([^`]*)`")]
    private static partial Regex GoStringLiteral();
    // Python：可带 r / b / f / u 前缀（大小写、组合）的单 / 双引号串
    [GeneratedRegex(@"(?<!\w)([rRbBfFuU]{0,2})(['""])((?:(?!\2)[^\\]|\\.)*)\2")]
    private static partial Regex PyStringLiteral();
    // private readonly string remoteServiceBaseUrl = "api/catalog/";  const string Base = "/api/orders";
    [GeneratedRegex(@"\bstring\s+(\w+)\s*=\s*\$?""([^""]*/[^""]*)""\s*;")]
    private static partial Regex PathField();
    // const apiBase = "/api/v1"   apiBase := "/api/v1"
    [GeneratedRegex(@"\b(\w+)\s*(?::=|=)\s*""([^""]*/[^""]*)""\s*$")]
    private static partial Regex GoPathField();
    // API = "/api/v1"   API_PREFIX: str = '/api'
    [GeneratedRegex(@"^\s*(\w+)\s*(?::\s*[\w\[\]., ]+)?=\s*[rRuU]?(['""])([^'""]*/[^'""]*)\2\s*(?:#.*)?$")]
    private static partial Regex PyPathField();
    // 已知路径常量 + 字面量：apiBase + "/orders"
    [GeneratedRegex(@"\b(\w+)\s*\+\s*[rRuU]?(['""])([^'""]*)\2")]
    private static partial Regex FieldPlusLiteral();
    [GeneratedRegex(@"^/(api|v\d+|graphql|hubs)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiPath();
    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Hole();
    // Go 的格式化动词：%s %v %d %q %x
    [GeneratedRegex(@"%[-+# 0-9.]*[svdqx]")]
    private static partial Regex GoVerb();
    [GeneratedRegex(@"\b(Post|Put|Delete|Patch|Get)(?:As|Async|FromJson|String|Stream)|HttpMethod\.(Post|Put|Delete|Patch|Get)\b")]
    private static partial Regex MethodHint();
    [GeneratedRegex(@"\bhttp\.Method(Post|Put|Delete|Patch|Get)\b|""(POST|PUT|DELETE|PATCH|GET)""|\bhttp\.(Post|Get|Head)\s*\(")]
    private static partial Regex GoMethodHint();
    [GeneratedRegex(@"\.\s*(post|put|delete|patch|get)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex PyMethodHint();
    // 生成的前端 SDK：(options.client ?? client).post<…>({ url: '/api/v1/login/access-token' })
    [GeneratedRegex(@"\.\s*(get|post|put|patch|delete)\s*[<(]")]
    private static partial Regex TsVerbCall();
    [GeneratedRegex(@"\burl\s*:\s*(['""`])(/[^'""`]*)\1")]
    private static partial Regex TsUrlProperty();
    // gRPC 客户端：Go 的 pb.NewXxxClient(conn)，Python 的 demo_pb2_grpc.XxxStub(channel)
    [GeneratedRegex(@"\bNew(\w+)Client\s*\(")]
    private static partial Regex GoClientCtor();
    [GeneratedRegex(@"(?:\b\w+\s*\.\s*)?(\w+)\s*(?::=|=)\s*(?:&\s*)?(?:\w+\.)?New(\w+)Client\s*\(")]
    private static partial Regex GoClientVar();
    [GeneratedRegex(@"\b(\w+)Stub\s*\(")]
    private static partial Regex PyStubCtor();
    [GeneratedRegex(@"(?:\bself\s*\.\s*)?(\w+)\s*=\s*(?:[\w.]+\.)?(\w+)Stub\s*\(")]
    private static partial Regex PyStubVar();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*([A-Za-z]\w*)\s*\(")]
    private static partial Regex MemberCall();

    /// <summary>
    /// 路由模板是否匹配一条具体路径。段数相同；模板的 {x} / :x / *（Spring 的单段通配）匹配任意一段，{*x} / **（Spring）匹配其余；
    /// 路径里的 ${…} 与 {…} 也匹配任意模板段。忽略查询串、大小写与尾斜杠。
    /// </summary>
    public static bool PathMatches(string template, string path)
    {
        var t = Segments(template, stripQuery: false); // 模板里的 {id?} 不是查询串
        var p = Segments(path, stripQuery: true);
        for (var i = 0; i < t.Length; i++)
        {
            var seg = t[i];
            if (seg.StartsWith("{*", StringComparison.Ordinal) || seg == "**") return p.Length >= i;
            if (i >= p.Length) return seg.EndsWith("?}", StringComparison.Ordinal); // 可选参数
            if (seg.StartsWith('{') || seg.StartsWith(':') || seg == "*" || p[i].Contains("${", StringComparison.Ordinal) || IsPlaceholder(p[i])) continue;
            if (!string.Equals(seg, p[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return t.Length == p.Length;
    }

    // 参数段数：{id} / {id:int}（ASP.NET）、:slug（gin / express）与 Spring 的 * / **
    private static int ParamSegments(string route) => route.Count(ch => ch == '{') + route.Split('/').Count(s => s.StartsWith(':') || s is "*" or "**");

    // 生成的 SDK 里路径参数写成 {id}（与后端的 {item_id} 名字不一定相同）
    private static bool IsPlaceholder(string seg) => seg.Length > 2 && seg[0] == '{' && seg[^1] == '}';

    private static string[] Segments(string s, bool stripQuery)
    {
        var q = stripQuery ? s.IndexOf('?') : -1;
        if (q >= 0) s = s[..q];
        return s.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private enum Lang { CSharp, Go, Python, Java }

    private static Lang? LangOf(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? Lang.CSharp
        : path.EndsWith(".go", StringComparison.OrdinalIgnoreCase) ? Lang.Go
        : path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? Lang.Python
        : path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ? Lang.Java
        : null;

    // protoc 生成的桩：Go 的 *.pb.go，Python 的 *_pb2(_grpc).py，Java 的 XxxGrpc.java
    private static bool IsGeneratedStub(string path) =>
        path.EndsWith(".pb.go", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_pb2_grpc.py", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_pb2.py", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("Grpc.java", StringComparison.Ordinal);

    private static bool IsScript(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);

    public static List<RouteMatch> Match(
        IReadOnlyList<Outbound> outbound, IReadOnlyList<EntryPoint> entries, IReadOnlyList<ArchNode> containers,
        IReadOnlyList<MapSymbol> symbols, Func<string, string[]> readLines)
    {
        var httpEntries = entries.Where(e => e.Kind == "http" && e.Route is not null).ToList();
        var grpcEntries = entries.Where(e => e.Kind == "grpc" && e.Route is not null).ToLookup(e => e.Route!, StringComparer.OrdinalIgnoreCase);
        var matches = new List<RouteMatch>();
        var services = new ServiceDirectory(containers, readLines);

        // target：只在这个容器里找（Java 出口的主机名 / Feign 的 name 已经点名了服务）
        RouteMatch? ResolveIn(string fromContainer, string file, int line, string path, string? method, string? target)
        {
            var hits = MostSpecific(httpEntries.Where(e => e.ContainerKey != fromContainer && (target is null || e.ContainerKey == target) && PathMatches(e.Route!, path)).ToList());
            var targetContainers = hits.Select(h => h.ContainerKey).Distinct().ToList();
            if (targetContainers.Count != 1) return null; // 对到两个容器不猜
            var best = hits.OrderBy(h => method is not null && h.HttpMethod is not null && h.HttpMethod != method ? 1 : 0) // 同一路径有 GET / POST 两个入口时按调用方式挑
                .ThenBy(h => ParamSegments(h.Route!)).ThenBy(h => h.Route, StringComparer.Ordinal).First(); // 字面量多的模板更具体
            return new RouteMatch(fromContainer, file, line, path, best.SymbolId, best.ContainerKey);
        }

        // 直接对不上时试网关：路径落在某个网关路由的 Path 谓词里 → 剥前缀段（StripPrefix）/ 补前缀（PrefixPath）后到路由目标服务里找。
        // 匹配记录的 Path 仍是调用方写的原路径（时序图 hop 显示它，注释里是对端入口的路由）
        RouteMatch? Resolve(string fromContainer, string file, int line, string path, string? method = null, string? target = null)
        {
            if (ResolveIn(fromContainer, file, line, path, method, target) is { } direct) return direct;
            if (target is not null) return null;
            foreach (var route in services.GatewayRoutes)
            {
                if (route.Rewrite(path) is not { } rewritten) continue;
                var to = services.ContainerOf(route.TargetHost);
                if (to is null || to.Key == fromContainer) return null; // 第一个命中的路由说了算（与网关按顺序匹配一致）
                return ResolveIn(fromContainer, file, line, rewritten, method, to.Key) is { } via ? via with { Path = path } : null;
            }
            return null;
        }

        RouteMatch? ResolveGrpc(string fromContainer, string file, int line, string service, string method)
        {
            var hits = MostSpecific(grpcEntries[service + "/" + method].Where(e => e.ContainerKey != fromContainer).ToList());
            if (hits.Select(h => h.ContainerKey).Distinct().Count() != 1) return null;
            var best = hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).First();
            return new RouteMatch(fromContainer, file, line, GrpcPathPrefix + best.Route, best.SymbolId, best.ContainerKey);
        }

        // 同一批入口符号因为容器嵌套（成员 "" 包住 backend/）出现在几个容器里：不是歧义，留成员目录最具体的容器。
        // 各容器的入口符号不同（两个服务真有同一路由）仍然不猜
        var byKey = containers.ToDictionary(c => c.Key, StringComparer.Ordinal);
        List<EntryPoint> MostSpecific(List<EntryPoint> hits)
        {
            var groups = hits.GroupBy(h => h.ContainerKey).ToList();
            if (groups.Count < 2) return hits;
            var first = groups[0].Select(h => h.SymbolId).ToHashSet();
            if (!groups.All(g => g.Select(h => h.SymbolId).ToHashSet().SetEquals(first))) return hits;
            int Specificity(EntryPoint h) => byKey.TryGetValue(h.ContainerKey, out var c) ? c.Members.Where(m => RuleContext.Under(h.File, m)).Select(m => m.Length).DefaultIfEmpty(-1).Max() : -1;
            var best = hits.Max(Specificity);
            var kept = hits.Where(h => Specificity(h) == best).ToList();
            return kept.Select(h => h.ContainerKey).Distinct().Count() == 1 ? kept : hits;
        }

        IReadOnlyList<ArchNode> OwnersOf(string file) => containers.Where(c => c.Members.Any(m => RuleContext.Under(file, m))).ToList();

        // 1. 前端出口：ClientName 记着 HTTP 方法与代理前缀（FrontendHttpRule.FormatCall），经代理的先剥掉前缀再对后端路由
        foreach (var o in outbound.Where(o => o.RuleId == "frontend-http" && o.Url is not null && o.Url.StartsWith('/')))
        {
            var (method, proxy) = FrontendHttpRule.ParseCall(o.ClientName);
            if (Resolve(o.ContainerKey, o.File, o.Line, FrontendHttpRule.BackendPath(o.Url!, proxy), method) is { } m) matches.Add(m);
        }

        var methodsByFile = symbols.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function && !PipelineBuilder.IsTest(s.Path, "")).ToLookup(s => s.Path);

        // 2. C# / Go / Python：有 HTTP 客户端调用的方法里的路径——字面量、插值串 / f-string / Sprintf（孔用文件里的路径常量展开）、直接当参数的常量名
        foreach (var group in methodsByFile)
        {
            var file = group.Key;
            if (LangOf(file) is not { } lang || lang == Lang.Java) continue; // Java 按调用实参取 URL，见第 5 段
            var owners = OwnersOf(file);
            if (owners.Count == 0) continue;
            var lines = readLines(file);
            var call = lang switch { Lang.Go => GoHttpCall(), Lang.Python => PyHttpCall(), _ => HttpCall() };
            if (!Array.Exists(lines, l => call.IsMatch(l))) continue;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                var f = lang switch { Lang.Go => GoPathField().Match(l), Lang.Python => PyPathField().Match(l), _ => PathField().Match(l) };
                if (!f.Success || (lang != Lang.Python && RuleContext.InCommentOrString(l, f.Index))) continue;
                fields[f.Groups[1].Value] = lang == Lang.Python ? f.Groups[3].Value : f.Groups[2].Value;
            }
            foreach (var method in group)
            {
                var start = Math.Max(0, method.StartLine - 1);
                var end = Math.Min(lines.Length, method.EndLine);
                if (end <= start) continue;
                var body = lines[start..end];
                if (!Array.Exists(body, l => call.IsMatch(l))) continue;
                var hint = MethodHintOf(lang, body);
                for (var i = 0; i < body.Length; i++)
                {
                    var candidates = lang switch { Lang.Go => GoCandidatePaths(body[i], fields), Lang.Python => PyCandidatePaths(body[i], fields), _ => CandidatePaths(body[i], fields) };
                    foreach (var path in candidates)
                        foreach (var c in owners)
                            if (Resolve(c.Key, file, start + i + 1, path, hint) is { } m) matches.Add(m);
                }
            }
        }

        // 3. 前端生成的 SDK：方法体里有 .get / .post…(…) 调用与 url: '/api/…' 属性；方法取 url 之前最近的那次调用
        var frontends = containers.Where(c => c.SubKind == "frontend").ToList();
        if (frontends.Count > 0)
        {
            foreach (var group in methodsByFile.Where(g => IsScript(g.Key)))
            {
                var file = group.Key;
                var owners = frontends.Where(c => c.Members.Any(m => RuleContext.Under(file, m))).ToList();
                if (owners.Count == 0) continue;
                var lines = readLines(file);
                if (!Array.Exists(lines, l => TsUrlProperty().IsMatch(l))) continue;
                var registered = outbound.Where(o => o.RuleId == "frontend-http" && o.File == file).Select(o => o.Line).ToList();
                foreach (var method in group)
                {
                    var start = Math.Max(0, method.StartLine - 1);
                    var end = Math.Min(lines.Length, method.EndLine);
                    if (registered.Any(l => l > start && l <= end)) continue; // 容器图规则已经登记了这个方法里的调用（第 1 段已匹配），不重复
                    string? verb = null;
                    for (var i = start; i < end; i++)
                    {
                        if (TsVerbCall().Match(lines[i]) is { Success: true } v) verb = v.Groups[1].Value.ToUpperInvariant();
                        var u = TsUrlProperty().Match(lines[i]);
                        if (!u.Success || verb is null) continue;
                        if (NormalizePath(u.Groups[2].Value, allowLeadingHole: false) is not { } path) continue;
                        foreach (var c in owners)
                            if (Resolve(c.Key, file, i + 1, path, verb) is { } m) matches.Add(m);
                    }
                }
            }
        }

        // 4. gRPC 客户端调用：Go 的 NewXxxClient(conn).Method( / 存进变量后 x.Method(，Python 的 XxxStub(channel) 赋给变量后 x.Method(
        if (grpcEntries.Count > 0)
        {
            foreach (var group in methodsByFile)
            {
                var file = group.Key;
                if (LangOf(file) is not { } lang || lang == Lang.CSharp || IsGeneratedStub(file)) continue; // 生成的桩里是客户端的定义，不是调用
                var owners = OwnersOf(file);
                if (owners.Count == 0) continue;
                var lines = readLines(file);
                var ctor = lang switch { Lang.Go => GoClientCtor(), Lang.Java => JavaStubCtor(), _ => PyStubCtor() };
                if (!Array.Exists(lines, l => ctor.IsMatch(l))) continue;
                // 文件里的客户端变量（局部变量与结构体字段 / self 属性 / Java 字段）→ 服务名
                var clients = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var l in lines)
                {
                    var v = (lang switch { Lang.Go => GoClientVar(), Lang.Java => JavaStubVar(), _ => PyStubVar() }).Match(l);
                    if (!v.Success) continue;
                    // resp, err := pb.NewXxxClient(conn).Method(…)：构造完直接链式调用，左边的变量不是客户端
                    var close = ArgParser.MatchClose(l, v.Index + v.Length - 1);
                    if (close >= 0 && Regex.IsMatch(l[(close + 1)..], @"^\s*\.")) continue;
                    if (close < 0 && l.TrimEnd().EndsWith('.')) continue;
                    clients[v.Groups[1].Value] = v.Groups[2].Value;
                }
                foreach (var method in group)
                {
                    var start = Math.Max(0, method.StartLine - 1);
                    var end = Math.Min(lines.Length, method.EndLine);
                    if (end <= start) continue;
                    foreach (var (line, service, rpc) in GrpcCalls(lines, start, end, ctor, clients))
                        foreach (var c in owners)
                            if (ResolveGrpc(c.Key, file, line, service, rpc) is { } m) matches.Add(m);
                }
            }
        }

        // 5. Java：RestTemplate / WebClient 调用的 URL 实参（主机名点名目标服务），@FeignClient 接口方法
        foreach (var group in methodsByFile.Where(g => LangOf(g.Key) == Lang.Java))
        {
            var owners = OwnersOf(group.Key);
            if (owners.Any(c => c.SubKind != "frontend")) owners = owners.Where(c => c.SubKind != "frontend").ToList();
            foreach (var c in owners)
                foreach (var call in JavaCalls(group.Key, group, symbols, readLines))
                {
                    string? target = null;
                    if (call.Host is not null)
                    {
                        if (services.ContainerOf(call.Host) is not { } t) continue; // 外部主机（api.github.com）
                        target = t.Key;
                    }
                    if (Resolve(c.Key, group.Key, call.Line, call.Path, call.Method, target) is { } m) matches.Add(m);
                }
        }

        // 6. 后端容器里的静态脚本（Spring 的 src/main/resources/static 之类，不在任何前端容器成员目录下，容器图规则没扫）：
        // 调用的第一个实参是以 /api、/v1 … 开头的字面量 / 模板串（request('/api/customer/owners')、fetch(`/api/x/${id}`)），常经网关路由转发
        foreach (var file in symbols.Where(s => IsScript(s.Path) && !PipelineBuilder.IsTest(s.Path, "")).Select(s => s.Path).Distinct(StringComparer.Ordinal))
        {
            if (frontends.Any(c => c.Members.Any(m => RuleContext.Under(file, m)))) continue;
            var owners = OwnersOf(file).Where(c => c.SubKind != "frontend").ToList();
            if (owners.Count == 0) continue;
            var registered = outbound.Where(o => o.File == file).Select(o => o.Line).ToHashSet();
            foreach (var (line, path, method) in ScriptApiCalls(readLines(file)))
            {
                if (registered.Contains(line)) continue;
                foreach (var c in owners)
                    if (Resolve(c.Key, file, line, path, method) is { } m) matches.Add(m);
            }
        }
        return matches.DistinctBy(m => (m.FromContainerKey, m.File, m.Line, m.EntrySymbolId)).OrderBy(m => m.FromContainerKey, StringComparer.Ordinal).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line).ToList();
    }

    /// <summary>方法体 [start, end) 里的 gRPC 调用：链式（构造客户端后紧跟 .Method(，可以跨行）与经变量的 x.Method(。行号是方法名所在行。</summary>
    private static IEnumerable<(int Line, string Service, string Method)> GrpcCalls(string[] lines, int start, int end, Regex ctor, IReadOnlyDictionary<string, string> clients)
    {
        var text = string.Join('\n', lines[start..end]);
        int LineAt(int index) => start + 1 + text.AsSpan(0, index).Count('\n');
        foreach (Match m in ctor.Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = ArgParser.MatchClose(text, open);
            if (close < 0) continue;
            var after = Regex.Match(text[(close + 1)..], @"^\s*\.\s*(\w+)\s*\(");
            if (after.Success) yield return (LineAt(close + 1 + after.Groups[1].Index), m.Groups[1].Value, after.Groups[1].Value);
        }
        if (clients.Count == 0) yield break;
        foreach (Match m in MemberCall().Matches(text))
        {
            if (!clients.TryGetValue(m.Groups[1].Value, out var service)) continue;
            yield return (LineAt(m.Groups[2].Index), service, m.Groups[2].Value);
        }
    }

    private static string? MethodHintOf(Lang lang, string[] body)
    {
        foreach (var l in body)
        {
            var m = (lang switch { Lang.Go => GoMethodHint(), Lang.Python => PyMethodHint(), _ => MethodHint() }).Match(l);
            if (!m.Success) continue;
            var g = m.Groups.Cast<Group>().Skip(1).First(x => x.Success);
            return g.Value.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>一行代码里可能是请求路径的东西：字符串字面量（插值孔 {field} 用已知字段展开，其余孔变成 ${x}）、单独出现的路径字段名。只保留以 /api、/v1、/graphql、/hubs 开头的。</summary>
    public static IEnumerable<string> CandidatePaths(string line, IReadOnlyDictionary<string, string> fields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match lit in StringLiteral().Matches(line))
        {
            var text = lit.Groups[2].Value;
            if (lit.Groups[1].Value.Length > 0)
                text = Hole().Replace(text, h => fields.TryGetValue(h.Groups[1].Value.Trim(), out var v) ? v : "${" + h.Groups[1].Value + "}");
            if (NormalizePath(text, allowLeadingHole: false) is { } p && seen.Add(p)) yield return p;
        }
        var outside = StringLiteral().Replace(line, " ");
        foreach (var (name, value) in fields)
            if (Regex.IsMatch(outside, $@"\b{Regex.Escape(name)}\b") && NormalizePath(value, allowLeadingHole: false) is { } p && seen.Add(p)) yield return p;
    }

    /// <summary>Go：解释串 / 原始串；Sprintf 的 %s 等动词变成 ${x}，开头的那个（主机地址）丢掉；已知常量 + 字面量拼起来；单独出现的常量名。</summary>
    public static IEnumerable<string> GoCandidatePaths(string line, IReadOnlyDictionary<string, string> fields)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match lit in GoStringLiteral().Matches(line))
        {
            var text = lit.Groups[1].Success ? lit.Groups[1].Value : lit.Groups[2].Value;
            text = GoVerb().Replace(text, "${x}");
            if (NormalizePath(text, allowLeadingHole: true) is { } p && seen.Add(p)) yield return p;
        }
        foreach (var p in FieldConcatenations(line, fields, GoStringLiteral())) if (seen.Add(p)) yield return p;
    }

    /// <summary>Python：各种前缀的串；f-string 的孔用模块里的路径常量展开，其余变成 ${x}，开头的孔（主机地址）丢掉；已知常量 + 字面量；单独出现的常量名。</summary>
    public static IEnumerable<string> PyCandidatePaths(string line, IReadOnlyDictionary<string, string> fields)
    {
        var hash = PyCommentStart(line);
        if (hash >= 0) line = line[..hash];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match lit in PyStringLiteral().Matches(line))
        {
            var text = lit.Groups[3].Value;
            if (lit.Groups[1].Value.Contains('f', StringComparison.OrdinalIgnoreCase))
                text = Hole().Replace(text, h => fields.TryGetValue(h.Groups[1].Value.Trim(), out var v) ? v : "${" + h.Groups[1].Value + "}");
            if (NormalizePath(text, allowLeadingHole: true) is { } p && seen.Add(p)) yield return p;
        }
        foreach (var p in FieldConcatenations(line, fields, PyStringLiteral())) if (seen.Add(p)) yield return p;
    }

    private static IEnumerable<string> FieldConcatenations(string line, IReadOnlyDictionary<string, string> fields, Regex literal)
    {
        foreach (Match m in FieldPlusLiteral().Matches(line))
            if (fields.TryGetValue(m.Groups[1].Value, out var v) && NormalizePath(v.TrimEnd('/') + "/" + m.Groups[3].Value.TrimStart('/'), allowLeadingHole: false) is { } p)
                yield return p;
        var outside = literal.Replace(line, " ");
        foreach (var (name, value) in fields)
            if (Regex.IsMatch(outside, $@"\b{Regex.Escape(name)}\b(?!\s*(?::=|=[^=]))") && NormalizePath(value, allowLeadingHole: false) is { } p) yield return p;
    }

    /// <summary>
    /// 原始串 → 请求路径；不像 API 路径返回 null。allowLeadingHole：开头的 ${x} 后面紧跟 '/' 时当主机地址丢掉（Go / Python 惯用 base + "/api/…"；
    /// C# 的写法里开头的未知字段多半是相对路径前缀，拼不出就不猜）。
    /// </summary>
    private static string? NormalizePath(string raw, bool allowLeadingHole)
    {
        if (raw.Length == 0 || raw.Contains("://", StringComparison.Ordinal) || raw.Contains(' ')) return null;
        var q = raw.IndexOf('?');
        if (q >= 0) raw = raw[..q];
        if (allowLeadingHole)
        {
            var hole = Regex.Match(raw, @"^(?:\$\{[^}]*\})+(?=/)");
            if (hole.Success) raw = raw[hole.Length..];
        }
        var p = "/" + raw.Trim('/');
        if (p.StartsWith("/$", StringComparison.Ordinal)) return null; // 以未知变量开头的拼不出路径
        return ApiPath().IsMatch(p) ? p : null;
    }

    /// <summary>Python 行内 # 注释的起点（不在字符串里）；没有返回 -1。</summary>
    internal static int PyCommentStart(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote is not null)
            {
                if (ch == '\\') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            if (ch is '\'' or '"') quote = ch;
            else if (ch == '#') return i;
        }
        return -1;
    }

    /// <summary>把匹配结果叠到容器图上：加 0.9 的 http / grpc 边（已有同协议的边升到 0.9、证据追加）；"API /xxx" 外部节点的出口全部对上时去掉它。</summary>
    public static ArchModel Upgrade(ArchModel model, IReadOnlyList<RouteMatch> matches)
    {
        if (matches.Count == 0) return model;
        var edges = model.Edges.ToList();
        var nodes = model.Nodes.ToList();
        foreach (var g in matches.GroupBy(m => (m.FromContainerKey, m.ToContainerKey, Protocol: m.Path.StartsWith(GrpcPathPrefix, StringComparison.Ordinal) ? "grpc" : "http")))
        {
            var evidence = g.Select(m => new Evidence(m.File, m.Line, m.Path)).DistinctBy(e => (e.File, e.Line)).Take(20).ToList();
            var existing = edges.FindIndex(e => e.FromKey == g.Key.FromContainerKey && e.ToKey == g.Key.ToContainerKey && e.Protocol == g.Key.Protocol);
            if (existing >= 0)
            {
                var e = edges[existing];
                edges[existing] = e with { Confidence = Math.Max(e.Confidence, EdgeMatcher.ByRoute), Evidence = [.. e.Evidence, .. evidence.Where(x => !e.Evidence.Any(y => y.File == x.File && y.Line == x.Line))] };
            }
            else edges.Add(new ArchEdge(g.Key.FromContainerKey, g.Key.ToContainerKey, g.Key.Protocol, EdgeMatcher.ByRoute, ArchSource.Discovered, evidence));
        }
        // 前端出口对上了的那些，从"API /xxx"外部节点的边里拿掉；外部节点没人指了就删
        var matchedSites = matches.Select(m => (m.File, m.Line)).ToHashSet();
        var apiTargets = model.Outbound.Where(o => o.RuleId == "frontend-http" && o.TargetKey is not null && o.TargetKey.StartsWith("external:http-service:api-", StringComparison.Ordinal))
            .GroupBy(o => (o.ContainerKey, o.TargetKey!));
        foreach (var g in apiTargets)
        {
            var total = g.Count();
            var matched = g.Count(o => matchedSites.Contains((o.File, o.Line)));
            if (matched == 0) continue;
            var idx = edges.FindIndex(e => e.FromKey == g.Key.ContainerKey && e.ToKey == g.Key.Item2);
            if (idx < 0) continue;
            if (matched == total) edges.RemoveAt(idx);
            else edges[idx] = edges[idx] with { Evidence = [.. edges[idx].Evidence, new Evidence(ArchitectureDeclarations.RelativePath, null, $"{matched}/{total} 处已对上路由")] };
        }
        var referenced = edges.SelectMany(e => new[] { e.FromKey, e.ToKey }).ToHashSet(StringComparer.Ordinal);
        nodes.RemoveAll(n => n.Kind == ArchNodeKind.External && !referenced.Contains(n.Key));
        return model with { Nodes = nodes, Edges = edges.OrderBy(e => e.FromKey, StringComparer.Ordinal).ThenBy(e => e.ToKey, StringComparer.Ordinal).ThenBy(e => e.Protocol, StringComparer.Ordinal).ToList() };
    }
}
