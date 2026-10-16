using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

public static partial class SpringFrameworkFlow
{
    private static readonly Regex MavenArtifact = new(@"<artifactId>\s*([\w.\-]+)\s*</artifactId>");
    // implementation 'org.springframework.boot:spring-boot-starter-web'   implementation("group:artifact:1.0")
    private static readonly Regex GradleDependency = new(@"^\s*(\w+)\s*\(?\s*['""]([\w.\-]+):([\w.\-]+)(?::[^'""]*)?['""]", RegexOptions.Multiline);

    /// <summary>
    /// 容器构建文件（pom.xml / build.gradle / build.gradle.kts，在容器目录下）里的依赖 artifactId：用来判断 Spring MVC / WebFlux / Gateway / Security。
    /// 去掉 *-test 与 test* 配置里的依赖。读不到给空表。
    /// </summary>
    public static List<string> Dependencies(Func<string, string?> readFile, string containerPath)
    {
        string Rel(string name) => containerPath.Length == 0 ? name : containerPath.TrimEnd('/') + "/" + name;
        string? Read(string name) { try { return readFile(Rel(name)); } catch { return null; } }
        var result = new List<string>();
        if (Read("pom.xml") is { } pom)
            foreach (Match m in MavenArtifact.Matches(pom))
                if (!m.Groups[1].Value.EndsWith("-test", StringComparison.Ordinal)) result.Add(m.Groups[1].Value);
        foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
            if (Read(name) is { } gradle)
                foreach (Match m in GradleDependency.Matches(gradle))
                    if (!m.Groups[1].Value.StartsWith("test", StringComparison.OrdinalIgnoreCase) && !m.Groups[3].Value.EndsWith("-test", StringComparison.Ordinal))
                        result.Add(m.Groups[3].Value);
        return result;
    }

    /// <summary>网关配置里的一个过滤器：名字（CircuitBreaker / StripPrefix）、参数原文、所在文件与行。</summary>
    public sealed record GatewayFilterConfig(string Name, string Args, string File, int Line);

    /// <summary>网关的一条路由：id、Path 断言、uri、过滤器。</summary>
    public sealed record GatewayRoute(string Id, string? Path, string? Uri, List<GatewayFilterConfig> Filters, string File, int Line);

    /// <summary>
    /// Spring Cloud Gateway 的配置（application.yml / .yaml / .properties、bootstrap.yml，容器 src/main/resources 下）：
    /// default-filters 列表（按书写顺序，网关给它们编号 1、2…）与 routes（id、Path=、uri、filters）。只认常见写法：
    /// YAML 里 "- name: X" / "- X=参数" / "- X"，properties 里 spring.cloud.gateway…default-filters[0]=X=参数、routes[0].id=…。
    /// </summary>
    public static (List<GatewayFilterConfig> Defaults, List<GatewayRoute> Routes) GatewayConfig(Func<string, string?> readFile, string containerPath)
    {
        var defaults = new List<GatewayFilterConfig>();
        var routes = new List<GatewayRoute>();
        var dir = (containerPath.Length == 0 ? "" : containerPath.TrimEnd('/') + "/") + "src/main/resources/";
        foreach (var name in new[] { "application.yml", "application.yaml", "bootstrap.yml", "bootstrap.yaml", "application.properties" })
        {
            string? text;
            try { text = readFile(dir + name); } catch { text = null; }
            if (text is null) continue;
            var file = dir + name;
            if (name.EndsWith(".properties", StringComparison.Ordinal)) ReadProperties(text, file, defaults, routes);
            else ReadYaml(text, file, defaults, routes);
        }
        return (defaults, routes);
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static GatewayFilterConfig? FilterItem(string item, string file, int line, IReadOnlyList<string> lines, int index)
    {
        var t = item.Trim();
        if (t.StartsWith("name:", StringComparison.Ordinal))
        {
            // - name: CircuitBreaker  后面 args: 的缩进块原样不读，只记名字
            var n = t[5..].Trim().Trim('"', '\'');
            return n.Length == 0 ? null : new GatewayFilterConfig(n, "", file, line);
        }
        t = t.Trim('"', '\'');
        var eq = t.IndexOf('=');
        var fname = (eq < 0 ? t : t[..eq]).Trim();
        if (fname.Length == 0 || !fname.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')) return null;
        return new GatewayFilterConfig(fname, eq < 0 ? "" : t[(eq + 1)..].Trim(), file, line);
    }

    /// <summary>key: 之后缩进更深的那块里，第一层 "- " 列表项（起始行下标，项文本）。</summary>
    private static List<(int Index, string Item)> ListItems(string[] lines, int keyIndex)
    {
        var items = new List<(int, string)>();
        var keyIndent = Indent(lines[keyIndex]);
        int? itemIndent = null;
        for (var i = keyIndex + 1; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var ind = Indent(l);
            if (ind < keyIndent || ind == keyIndent && !t.StartsWith("- ", StringComparison.Ordinal)) break;
            if (!t.StartsWith("- ", StringComparison.Ordinal)) continue;
            itemIndent ??= ind;
            if (ind == itemIndent) items.Add((i, t[2..]));
            else if (ind < itemIndent) break;
        }
        return items;
    }

    private static void ReadYaml(string text, string file, List<GatewayFilterConfig> defaults, List<GatewayRoute> routes)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t is "default-filters:" or "defaultFilters:")
            {
                foreach (var (index, item) in ListItems(lines, i))
                    if (FilterItem(item, file, index + 1, lines, index) is { } f) defaults.Add(f);
            }
            else if (t == "routes:" && IsGatewayRoutes(lines, i))
            {
                var items = ListItems(lines, i);
                for (var k = 0; k < items.Count; k++)
                {
                    var (start, first) = items[k];
                    var end = k + 1 < items.Count ? items[k + 1].Index : NextOutdent(lines, i);
                    string? id = null, path = null, uri = null;
                    var filters = new List<GatewayFilterConfig>();
                    for (var j = start; j < end; j++)
                    {
                        var lt = (j == start ? first : lines[j].Trim()).TrimEnd('\r');
                        if (lt.StartsWith("id:", StringComparison.Ordinal)) id = lt[3..].Trim().Trim('"', '\'');
                        else if (lt.StartsWith("uri:", StringComparison.Ordinal)) uri = lt[4..].Trim().Trim('"', '\'');
                        else if (lt.StartsWith("- Path=", StringComparison.Ordinal)) path ??= lt[7..].Trim().Trim('"', '\'');
                        else if (lt == "filters:")
                            foreach (var (fi, fitem) in ListItems(lines, j))
                                if (fi < end && FilterItem(fitem, file, fi + 1, lines, fi) is { } f) filters.Add(f);
                    }
                    if (id is not null || uri is not null) routes.Add(new GatewayRoute(id ?? uri!, path, uri, filters, file, start + 1));
                }
            }
        }
    }

    /// <summary>routes: 是网关的（祖先键里有 gateway），不是别的框架的 routes。</summary>
    private static bool IsGatewayRoutes(string[] lines, int index)
    {
        var indent = Indent(lines[index]);
        for (var i = index - 1; i >= 0 && indent > 0; i--)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var ind = Indent(l);
            if (ind >= indent) continue;
            if (t.StartsWith("gateway", StringComparison.Ordinal)) return true;
            indent = ind;
        }
        return false;
    }

    private static int NextOutdent(string[] lines, int keyIndex)
    {
        var keyIndent = Indent(lines[keyIndex]);
        for (var i = keyIndex + 1; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (Indent(lines[i].TrimEnd('\r')) <= keyIndent && !t.StartsWith("- ", StringComparison.Ordinal)) return i;
        }
        return lines.Length;
    }

    private static readonly Regex PropDefaultFilter = new(@"^\s*spring\.cloud\.gateway(?:\.server\.webflux)?\.default-filters\[(\d+)\]\s*[=:]\s*(.+)$", RegexOptions.Multiline);
    private static readonly Regex PropRoute = new(@"^\s*spring\.cloud\.gateway(?:\.server\.webflux)?\.routes\[(\d+)\]\.(id|uri|predicates\[\d+\]|filters\[\d+\])\s*[=:]\s*(.+)$", RegexOptions.Multiline);

    private static void ReadProperties(string text, string file, List<GatewayFilterConfig> defaults, List<GatewayRoute> routes)
    {
        int LineOf(int pos) => text[..pos].Count(c => c == '\n') + 1;
        foreach (Match m in PropDefaultFilter.Matches(text).OrderBy(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            if (FilterItem(m.Groups[2].Value.TrimEnd('\r'), file, LineOf(m.Index), [], 0) is { } f) defaults.Add(f);
        var byIndex = new SortedDictionary<int, (string? Id, string? Path, string? Uri, List<GatewayFilterConfig> Filters, int Line)>();
        foreach (Match m in PropRoute.Matches(text))
        {
            var idx = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var r = byIndex.TryGetValue(idx, out var old) ? old : (null, null, null, new List<GatewayFilterConfig>(), LineOf(m.Index));
            var v = m.Groups[3].Value.TrimEnd('\r').Trim();
            var key = m.Groups[2].Value;
            if (key == "id") r.Id = v;
            else if (key == "uri") r.Uri = v;
            else if (key.StartsWith("predicates", StringComparison.Ordinal) && v.StartsWith("Path=", StringComparison.Ordinal)) r.Path ??= v[5..];
            else if (key.StartsWith("filters", StringComparison.Ordinal) && FilterItem(v, file, LineOf(m.Index), [], 0) is { } f) r.Filters.Add(f);
            byIndex[idx] = r;
        }
        foreach (var r in byIndex.Values)
            if (r.Id is not null || r.Uri is not null) routes.Add(new GatewayRoute(r.Id ?? r.Uri!, r.Path, r.Uri, r.Filters, file, r.Line));
    }
}
