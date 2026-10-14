using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 出口路径 ↔ 入口路由匹配。两种出口：前端 fetch / axios 的路径字面量（容器图扫描已登记），
/// C# 里同一方法既调了 HttpClient 又出现了路径字面量。对上就是跨容器的 0.9 边，也是时序图续接的依据。
/// </summary>
public static partial class RouteLinker
{
    [GeneratedRegex(@"\.(GetAsync|PostAsync|PutAsync|DeleteAsync|PatchAsync|SendAsync|PostAsJsonAsync|PutAsJsonAsync|GetFromJsonAsync|GetStringAsync|GetStreamAsync)\s*[<(]")]
    private static partial Regex HttpCall();
    [GeneratedRegex(@"(\$?)""([^""]*)""")]
    private static partial Regex StringLiteral();
    // private readonly string remoteServiceBaseUrl = "api/catalog/";  const string Base = "/api/orders";
    [GeneratedRegex(@"\bstring\s+(\w+)\s*=\s*\$?""([^""]*/[^""]*)""\s*;")]
    private static partial Regex PathField();
    [GeneratedRegex(@"^/(api|v\d+|graphql|hubs)(/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiPath();
    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Hole();
    [GeneratedRegex(@"\b(Post|Put|Delete|Patch|Get)(?:As|Async|FromJson|String|Stream)|HttpMethod\.(Post|Put|Delete|Patch|Get)\b")]
    private static partial Regex MethodHint();

    /// <summary>路由模板是否匹配一条具体路径。段数相同；模板的 {x} / :x 匹配任意一段，{*x} 匹配其余；路径里的 ${…} 也匹配任意模板段。忽略查询串与大小写。</summary>
    public static bool PathMatches(string template, string path)
    {
        var t = Segments(template, stripQuery: false); // 模板里的 {id?} 不是查询串
        var p = Segments(path, stripQuery: true);
        for (var i = 0; i < t.Length; i++)
        {
            var seg = t[i];
            if (seg.StartsWith("{*", StringComparison.Ordinal)) return p.Length >= i;
            if (i >= p.Length) return seg.EndsWith("?}", StringComparison.Ordinal); // 可选参数
            if (seg.StartsWith('{') || seg.StartsWith(':') || p[i].Contains("${", StringComparison.Ordinal)) continue;
            if (!string.Equals(seg, p[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return t.Length == p.Length;
    }

    private static string[] Segments(string s, bool stripQuery)
    {
        var q = stripQuery ? s.IndexOf('?') : -1;
        if (q >= 0) s = s[..q];
        return s.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public static List<RouteMatch> Match(
        IReadOnlyList<Outbound> outbound, IReadOnlyList<EntryPoint> entries, IReadOnlyList<ArchNode> containers,
        IReadOnlyList<MapSymbol> symbols, Func<string, string[]> readLines)
    {
        var httpEntries = entries.Where(e => e.Kind == "http" && e.Route is not null).ToList();
        var matches = new List<RouteMatch>();

        RouteMatch? Resolve(string fromContainer, string file, int line, string path, string? method = null)
        {
            var hits = httpEntries.Where(e => e.ContainerKey != fromContainer && PathMatches(e.Route!, path)).ToList();
            var targetContainers = hits.Select(h => h.ContainerKey).Distinct().ToList();
            if (targetContainers.Count != 1) return null; // 对到两个容器不猜
            var best = hits.OrderBy(h => method is not null && h.HttpMethod is not null && h.HttpMethod != method ? 1 : 0) // 同一路径有 GET / POST 两个入口时按调用方式挑
                .ThenBy(h => h.Route!.Count(ch => ch == '{')).ThenBy(h => h.Route, StringComparer.Ordinal).First(); // 字面量多的模板更具体
            return new RouteMatch(fromContainer, file, line, path, best.SymbolId, best.ContainerKey);
        }

        // 1. 前端出口
        foreach (var o in outbound.Where(o => o.RuleId == "frontend-http" && o.Url is not null && o.Url.StartsWith('/')))
            if (Resolve(o.ContainerKey, o.File, o.Line, o.Url!) is { } m) matches.Add(m);

        // 2. C#：有 HttpClient 调用的方法里的路径——字面量、插值串（{field} 用文件里的 string 字段展开）、直接当参数的字段名
        var methodsByFile = symbols.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function && s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToLookup(s => s.Path);
        foreach (var group in methodsByFile)
        {
            var file = group.Key;
            if (PipelineBuilder.IsTest(file, "")) continue;
            var owners = containers.Where(c => c.Members.Any(m => RuleContext.Under(file, m))).ToList();
            if (owners.Count == 0) continue;
            var lines = readLines(file);
            if (!Array.Exists(lines, l => HttpCall().IsMatch(l))) continue;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                var f = PathField().Match(l);
                if (f.Success && !RuleContext.InCommentOrString(l, f.Index)) fields[f.Groups[1].Value] = f.Groups[2].Value;
            }
            foreach (var method in group)
            {
                var start = Math.Max(0, method.StartLine - 1);
                var end = Math.Min(lines.Length, method.EndLine);
                if (end <= start) continue;
                var body = lines[start..end];
                if (!Array.Exists(body, l => HttpCall().IsMatch(l))) continue;
                var hintMatch = body.Select(l => MethodHint().Match(l)).FirstOrDefault(x => x.Success);
                var hint = hintMatch is null ? null : (hintMatch.Groups[1].Success ? hintMatch.Groups[1].Value : hintMatch.Groups[2].Value).ToUpperInvariant();
                for (var i = 0; i < body.Length; i++)
                {
                    foreach (var path in CandidatePaths(body[i], fields))
                        foreach (var c in owners)
                            if (Resolve(c.Key, file, start + i + 1, path, hint) is { } m) matches.Add(m);
                }
            }
        }
        return matches.DistinctBy(m => (m.FromContainerKey, m.File, m.Line, m.EntrySymbolId)).OrderBy(m => m.FromContainerKey, StringComparer.Ordinal).ThenBy(m => m.File, StringComparer.Ordinal).ThenBy(m => m.Line).ToList();
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
            if (Normalize(text) is { } p && seen.Add(p)) yield return p;
        }
        var outside = StringLiteral().Replace(line, " ");
        foreach (var (name, value) in fields)
            if (Regex.IsMatch(outside, $@"\b{Regex.Escape(name)}\b") && Normalize(value) is { } p && seen.Add(p)) yield return p;

        static string? Normalize(string raw)
        {
            if (raw.Length == 0 || raw.Contains("://", StringComparison.Ordinal) || raw.Contains(' ')) return null;
            var q = raw.IndexOf('?');
            if (q >= 0) raw = raw[..q];
            var p = "/" + raw.Trim('/');
            if (p.StartsWith("/$", StringComparison.Ordinal)) return null; // 以未知变量开头的拼不出路径
            return ApiPath().IsMatch(p) ? p : null;
        }
    }

    /// <summary>把匹配结果叠到容器图上：加 0.9 的 http 边；"API /xxx" 外部节点的出口全部对上时去掉它。</summary>
    public static ArchModel Upgrade(ArchModel model, IReadOnlyList<RouteMatch> matches)
    {
        if (matches.Count == 0) return model;
        var edges = model.Edges.ToList();
        var nodes = model.Nodes.ToList();
        foreach (var g in matches.GroupBy(m => (m.FromContainerKey, m.ToContainerKey)))
        {
            var evidence = g.Select(m => new Evidence(m.File, m.Line, m.Path)).DistinctBy(e => (e.File, e.Line)).Take(20).ToList();
            var existing = edges.FindIndex(e => e.FromKey == g.Key.FromContainerKey && e.ToKey == g.Key.ToContainerKey && e.Protocol == "http");
            if (existing >= 0)
            {
                var e = edges[existing];
                edges[existing] = e with { Confidence = Math.Max(e.Confidence, EdgeMatcher.ByRoute), Evidence = [.. e.Evidence, .. evidence.Where(x => !e.Evidence.Any(y => y.File == x.File && y.Line == x.Line))] };
            }
            else edges.Add(new ArchEdge(g.Key.FromContainerKey, g.Key.ToContainerKey, "http", EdgeMatcher.ByRoute, ArchSource.Discovered, evidence));
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
