using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>按行扫描时区分的源码语言：注释记号与字符串引号不同。</summary>
public enum SourceLang { CSharp, Go, Python, JavaScript, Java }

/// <summary>Go / Python / JS 源码的行级小工具：注释与字符串判定、实参切分、跨行拼语句。</summary>
public static class PolyText
{
    public static SourceLang? LangOf(string rel)
    {
        var ext = System.IO.Path.GetExtension(rel).ToLowerInvariant();
        return ext switch
        {
            ".cs" => SourceLang.CSharp,
            ".go" => SourceLang.Go,
            ".py" => SourceLang.Python,
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts" => SourceLang.JavaScript,
            ".java" => SourceLang.Java,
            _ => null,
        };
    }

    /// <summary>line[index] 是否处在注释里或字符串里（Go / C# / JS 的 //，Python 的 #；引号按语言）。</summary>
    public static bool InCommentOrString(string line, int index, SourceLang lang)
    {
        char? quote = null;
        for (var i = 0; i < index && i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == '\\' && quote != '`') { i++; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (lang == SourceLang.Python ? c == '#' : c == '/' && i + 1 < line.Length && line[i + 1] == '/') return true;
            if (c == '"' || (c == '\'' && lang != SourceLang.CSharp) || (c == '`' && lang is SourceLang.Go or SourceLang.JavaScript)) quote = c;
        }
        return quote is not null;
    }

    /// <summary>从第 i 行起拼最多 extra 行（去掉后续行的缩进），给跨行的调用用。</summary>
    public static string Statement(string[] lines, int i, int extra = 5)
    {
        var s = lines[i];
        for (var k = i + 1; k < lines.Length && k <= i + extra; k++) s += " " + lines[k].Trim();
        return s;
    }

    /// <summary>text[open] 是 "("：按同层逗号切出实参（引号、括号嵌套都跳过）；到配对的 ")" 为止，没配上就到文本末尾。</summary>
    public static List<string> SplitArgs(string text, int open)
    {
        var args = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = open + 1;
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\' && quote != '`') { i++; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) { var last = text[start..i].Trim(); if (last.Length > 0 || args.Count > 0) args.Add(last); return args; }
                depth--;
            }
            else if (c == ',' && depth == 0) { args.Add(text[start..i].Trim()); start = i + 1; }
        }
        if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest) args.Add(rest);
        return args;
    }

    /// <summary>关键字实参 name=…（Python 的 base_url= / url=）。</summary>
    public static string? Keyword(IEnumerable<string> args, string name)
    {
        foreach (var a in args)
        {
            var m = Regex.Match(a, @"^\s*" + Regex.Escape(name) + @"\s*=\s*(.+)$", RegexOptions.Singleline);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>非关键字的第 k 个实参（Python 里关键字实参不占位置）。</summary>
    public static string? Positional(List<string> args, int k, SourceLang lang)
    {
        var positional = lang == SourceLang.Python ? args.Where(a => !Regex.IsMatch(a, @"^\s*\w+\s*=[^=]")).ToList() : args;
        return k < positional.Count ? positional[k] : null;
    }
}

/// <summary>
/// Go / Python 的地址来源：URL 字面量（gRPC 还认 host:port）、环境变量（os.Getenv / os.environ.get / os.getenv，名字记成配置键，缺省值是 URL 就记成地址）、
/// flag / viper / settings.X 之类的配置键，变量 → 在同一函数里往回找赋值（再不行找包 / 模块级的赋值）。只做文本回溯，找不到返回空。
/// </summary>
public static class PolyAddress
{
    private static readonly Regex GoEnv = new(@"\bos\.(?:Getenv|LookupEnv)\(\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex GoViper = new(@"\bviper\.Get\w*\(\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex GoFlag = new(@"\bflag\.(?:String|StringVar)\(\s*(?:&?[\w.]+\s*,\s*)?""([^""]+)""\s*,\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex GoSprintf = new(@"^fmt\.Sprintf\(\s*""([^""]*)""\s*(?:,\s*(.+))?\)$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PyEnv = new(@"\bos\.(?:environ\.get|getenv)\(\s*['""]([^'""]+)['""]\s*(?:,\s*(.+?))?\s*\)", RegexOptions.Compiled);
    private static readonly Regex PyEnvIndex = new(@"\bos\.environ\[\s*['""]([^'""]+)['""]\s*\]", RegexOptions.Compiled);
    private static readonly Regex PySettings = new(@"\bsettings\.([A-Z][A-Z0-9_]+)\b", RegexOptions.Compiled);
    private static readonly Regex PyDecouple = new(@"\b(?:config|env)\(\s*['""]([A-Z][A-Z0-9_]+)['""]", RegexOptions.Compiled);
    private static readonly Regex Literal = new(@"^(?:[frbuFRBU]{1,2})?(?:""([^""]*)""|'([^']*)'|`([^`]*)`)", RegexOptions.Compiled);
    private static readonly Regex HostPort = new(@"^(?:dns:///)?(?:[\w.\-]+|\[[:\w]*\]):\d{2,5}$", RegexOptions.Compiled);
    private static readonly Regex LeadingName = new(@"^(?:self\.)?([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex GoSelector = new(@"^&?([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)", RegexOptions.Compiled);

    /// <param name="siblings">同一个 Go 包（同目录）里其他文件的行：包级变量、字段的赋值可能在那边。</param>
    public static AddressResolver.Address Resolve(string expr, string[] lines, int lineIndex, SourceLang lang, bool grpc, int depth = 0, IReadOnlyList<string[]>? siblings = null)
    {
        var none = new AddressResolver.Address(null, null);
        expr = expr.Trim();
        if (expr.Length == 0 || depth > 3) return none;
        if (lang == SourceLang.Go)
        {
            if (GoEnv.Match(expr) is { Success: true } e) return new(null, e.Groups[1].Value);
            if (GoViper.Match(expr) is { Success: true } v) return new(null, v.Groups[1].Value);
            if (GoFlag.Match(expr) is { Success: true } f) return new(AsAddress(f.Groups[2].Value, grpc), f.Groups[1].Value);
            if (GoSprintf.Match(expr) is { Success: true } s)
            {
                var format = s.Groups[1].Value;
                if (format.StartsWith('%') && s.Groups[2].Success) return Resolve(PolyText.SplitArgs("(" + s.Groups[2].Value + ")", 0).FirstOrDefault() ?? "", lines, lineIndex, lang, grpc, depth + 1, siblings);
                var cut = format.IndexOf('%');
                return new(AsAddress(cut >= 0 ? format[..cut] : format, grpc), null);
            }
        }
        else
        {
            if (PyEnv.Match(expr) is { Success: true } e)
            {
                string? url = null;
                if (e.Groups[2].Success && Literal.Match(e.Groups[2].Value.Trim()) is { Success: true } d) url = AsAddress(LiteralValue(d), grpc);
                return new(url, e.Groups[1].Value);
            }
            if (PyEnvIndex.Match(expr) is { Success: true } ix) return new(null, ix.Groups[1].Value);
            if (PySettings.Match(expr) is { Success: true } st) return new(null, st.Groups[1].Value);
            if (PyDecouple.Match(expr) is { Success: true } dc) return new(null, dc.Groups[1].Value);
        }
        var lit = Literal.Match(expr);
        if (lit.Success)
        {
            var value = LiteralValue(lit);
            // f"{BASE}/x"：开头是占位的 f-string，按占位里的变量解析
            if (lang == SourceLang.Python && value.StartsWith('{') && value.IndexOf('}') is var close and > 1)
                return Resolve(value[1..close], lines, lineIndex, lang, grpc, depth + 1, siblings);
            // "http://" + addr：字面量只有方案，地址在后面拼的变量里
            if (value.EndsWith("://", StringComparison.Ordinal) && Regex.Match(expr[lit.Length..], @"^\s*\+\s*(.+)$", RegexOptions.Singleline) is { Success: true } rest)
            {
                var inner = Resolve(rest.Groups[1].Value, lines, lineIndex, lang, grpc: true, depth + 1, siblings);
                return new(inner.Url is null ? null : inner.Url.Contains("://", StringComparison.Ordinal) ? inner.Url : value + inner.Url, inner.ConfigKey);
            }
            var brace = value.IndexOfAny(['{', '%']);
            return new(AsAddress(brace > 0 && lang == SourceLang.Python ? value[..brace] : value, grpc), null);
        }
        // Go 的字段 svc.productCatalogSvcAddr：在同一包里找 mustMapEnv(&svc.productCatalogSvcAddr, "PRODUCT_CATALOG_SERVICE_ADDR") 这种
        // "把环境变量读进字段"的辅助调用，或 x.field = … 的赋值
        if (lang == SourceLang.Go && GoSelector.Match(expr) is { Success: true } sel && !expr[sel.Length..].TrimStart().StartsWith('('))
        {
            var field = sel.Groups[1].Value[(sel.Groups[1].Value.LastIndexOf('.') + 1)..];
            if (EnvSetter(field, lines, siblings) is { } fieldEnv) return new(null, fieldEnv);
            var assign = new Regex(@"\.\b" + Regex.Escape(field) + @"\s*=(?!=)\s*(.+)$");
            foreach (var src in new[] { lines }.Concat(siblings ?? []))
                for (var i = 0; i < src.Length; i++)
                    if (assign.Match(src[i]) is { Success: true } a2 && Resolve(a2.Groups[1].Value, src, i, lang, grpc, depth + 1, siblings) is { } r && (r.Url is not null || r.ConfigKey is not null)) return r;
            return none;
        }
        // 变量（可能带拼接 base + "/x" 或属性 self.base_url）：回溯赋值
        var name = LeadingName.Match(expr);
        if (!name.Success) return none;
        var ident = name.Groups[1].Value;
        if (ident is "nil" or "None" or "true" or "false" or "True" or "False") return none;
        var rhs = FindAssignment(ident, expr.StartsWith("self.", StringComparison.Ordinal), lines, lineIndex, lang, out var at);
        if (rhs is not null && Resolve(rhs, lines, at, lang, grpc, depth + 1, siblings) is { } local && (local.Url is not null || local.ConfigKey is not null)) return local;
        if (lang != SourceLang.Go) return none;
        // 同一个 Go 包里别的文件的包级变量（init() 里赋值的也算）
        foreach (var src in siblings ?? [])
            if (FindAssignment(ident, false, src, 0, lang, out var at2) is { } rhs2) return Resolve(rhs2, src, at2, lang, grpc, depth + 1, siblings);
        // var collectorAddr string; mustMapEnv(&collectorAddr, "COLLECTOR_SERVICE_ADDR")
        return EnvSetter(ident, lines, siblings) is { } env ? new(null, env) : none;
    }

    /// <summary>"把环境变量读进变量 / 字段"的辅助调用：f(&amp;x, "ENV_NAME") / f(&amp;svc.x, "ENV_NAME")。返回环境变量名。</summary>
    private static string? EnvSetter(string name, string[] lines, IReadOnlyList<string[]>? siblings)
    {
        var setter = new Regex(@"\b\w+\(\s*&\s*(?:\w+\.)*" + Regex.Escape(name) + @"\s*,\s*""([A-Z][A-Z0-9_]*)""\s*\)");
        foreach (var src in new[] { lines }.Concat(siblings ?? []))
            foreach (var l in src)
                if (setter.Match(l) is { Success: true } m) return m.Groups[1].Value;
        return null;
    }

    /// <summary>
    /// 表达式是不是外层 Go 函数的形参（func mustConnGRPC(ctx context.Context, conn **grpc.ClientConn, addr string)）：是就返回函数名与形参位置。
    /// 通道地址常这样经辅助函数传进来，规则再到调用处取对应实参解析。
    /// </summary>
    public static (string Func, int Index)? GoParameter(string expr, string[] lines, int lineIndex)
    {
        var name = LeadingName.Match(expr.Trim());
        if (!name.Success || expr.Trim().Length != name.Length) return null;
        for (var i = lineIndex; i >= 0; i--)
        {
            if (!lines[i].StartsWith("func ", StringComparison.Ordinal)) continue;
            var m = Regex.Match(lines[i], @"^func\s+(?:\([^)]*\)\s*)?(\w+)\s*(?:\[[^\]]*\])?\(([^)]*)\)");
            if (!m.Success) return null;
            // 形参名：逗号分隔，每段第一个词（a, b string 这种合写的类型也对）
            var names = m.Groups[2].Value.Split(',').Select(p => p.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToList();
            var idx = names.IndexOf(name.Groups[1].Value);
            return idx < 0 ? null : (m.Groups[1].Value, idx);
        }
        return null;
    }

    private static string LiteralValue(Match m) => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

    /// <summary>字面量能当地址：http(s):// 开头；gRPC 还认 host:port（含 dns:///、[::]:port）。相对路径不算。</summary>
    private static string? AsAddress(string value, bool grpc)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        if (grpc && HostPort.IsMatch(value)) return value;
        return null;
    }

    /// <summary>往回找 name 的赋值（到函数边界为止），找不到再找包 / 模块级的赋值；返回右边的表达式与所在行。</summary>
    private static string? FindAssignment(string name, bool selfAttr, string[] lines, int lineIndex, SourceLang lang, out int at)
    {
        var target = (selfAttr ? @"self\." : "") + Regex.Escape(name);
        var local = lang == SourceLang.Go
            ? new Regex(@"(?:^|[\s(,;])(?:var\s+)?" + target + @"(?:\s+[\w.*\[\]]+)?\s*(?::=|=)(?!=)\s*(.+)$")
            : new Regex(@"^\s*" + target + @"\s*(?::\s*[^=]+)?=(?!=)\s*(.+)$");
        for (var i = lineIndex - 1; i >= 0 && i >= lineIndex - 60; i--)
        {
            var line = lines[i];
            if (lang == SourceLang.Go && line.StartsWith("func ", StringComparison.Ordinal)) break;
            if (lang == SourceLang.Python && Regex.IsMatch(line, @"^\s*(?:async\s+)?def\s") && !selfAttr) break;
            var m = local.Match(line);
            if (m.Success && !Regex.IsMatch(line, @"\b" + Regex.Escape(name) + @"\s*,\s*\w+\s*:=")) { at = i; return m.Groups[1].Value.Trim(); }
        }
        // 包级 const / var（Go：顶格或 const ( … ) 块里一层缩进），模块级常量（Python：顶格）
        var top = lang == SourceLang.Go
            ? new Regex(@"^(?:(?:var|const)\s+)?\t?" + target + @"(?:\s+[\w.*\[\]]+)?\s*=(?!=)\s*(.+)$")
            : new Regex(@"^" + target + @"\s*(?::\s*[^=]+)?=(?!=)\s*(.+)$");
        for (var i = 0; i < lines.Length; i++)
        {
            var m = top.Match(lines[i]);
            if (m.Success) { at = i; return m.Groups[1].Value.Trim(); }
        }
        at = lineIndex;
        return null;
    }
}

/// <summary>Go 的出口：net/http 的 http.Get / Post / NewRequest、resty 的 SetBaseURL / R().Get(…)、grpc.Dial / DialContext / NewClient。</summary>
public sealed class GoOutboundRule : IDiscoveryRule
{
    public string Id => "go-outbound";
    public string Description => "Go 的 HTTP 调用（net/http、resty）与 gRPC 通道（grpc.Dial / NewClient），地址来自字面量、环境变量或 flag";

    private sealed record Call(Regex Regex, string Protocol, Func<Match, int> ArgIndex);

    private static readonly Call[] Calls =
    [
        new(new(@"\bhttp\.(?:Get|Post|Head|PostForm)\(", RegexOptions.Compiled), "http", _ => 0),
        new(new(@"\bhttp\.NewRequest(WithContext)?\(", RegexOptions.Compiled), "http", m => m.Groups[1].Success ? 2 : 1),
        new(new(@"\.SetBaseURL\(", RegexOptions.Compiled), "http", _ => 0),
        new(new(@"\.R\(\)(?:\.\w+\([^()]*\))*\.(?:Get|Post|Put|Patch|Delete|Head)\(", RegexOptions.Compiled), "http", _ => 0),
        new(new(@"\bgrpc\.(Dial|DialContext|NewClient)\(", RegexOptions.Compiled), "grpc", m => m.Groups[1].Value == "DialContext" ? 1 : 0),
    ];

    /// <summary>认出是 HTTP 调用但地址解析不出来的地方（诊断用）。</summary>
    public List<(string File, int Line)> Unresolved { get; } = [];

    public void Apply(RuleContext ctx)
    {
        Unresolved.Clear();
        var goFiles = ctx.SourceFilesOf(".go").ToList();
        var byDir = goFiles.GroupBy(f => f.Contains('/') ? f[..f.LastIndexOf('/')] : "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var file in goFiles)
        {
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => l.Contains("http.", StringComparison.Ordinal) || l.Contains("grpc.", StringComparison.Ordinal) || l.Contains(".R()", StringComparison.Ordinal) || l.Contains("SetBaseURL", StringComparison.Ordinal))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var package = byDir[file.Contains('/') ? file[..file.LastIndexOf('/')] : ""];
            var siblings = package.Where(f => f != file).Select(ctx.Lines).ToList();
            for (var i = 0; i < lines.Length; i++)
                foreach (var call in Calls)
                {
                    var m = call.Regex.Match(lines[i]);
                    if (!m.Success || PolyText.InCommentOrString(lines[i], m.Index, SourceLang.Go)) continue;
                    var args = PolyText.SplitArgs(PolyText.Statement(lines, i), m.Index + m.Length - 1);
                    var k = call.ArgIndex(m);
                    var grpc = call.Protocol == "grpc";
                    var address = k < args.Count ? PolyAddress.Resolve(args[k], lines, i, SourceLang.Go, grpc, 0, siblings) : new AddressResolver.Address(null, null);
                    if (address.Url is null && address.ConfigKey is null && k < args.Count && PolyAddress.GoParameter(args[k], lines, i) is { } param
                        && ViaCallSites(ctx, package, param, call.Protocol) is { Count: > 0 } sites)
                    {
                        // 地址是辅助函数的形参：每个调用处一个出口，地址取那里的实参（mustConnGRPC(ctx, &conn, svc.cartSvcAddr)）
                        ctx.Outbound.AddRange(sites);
                        continue;
                    }
                    if (!grpc && address.Url is null && address.ConfigKey is null) { Unresolved.Add((file, i + 1)); continue; }
                    foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, call.Protocol, null, address.ConfigKey, address.Url, file, i + 1, null));
                }
        }
    }

    /// <summary>同一个包里对 param.Func 的调用：取第 param.Index 个实参解析，解析出地址的才登记。</summary>
    private List<Outbound> ViaCallSites(RuleContext ctx, List<string> package, (string Func, int Index) param, string protocol)
    {
        var found = new List<Outbound>();
        var call = new Regex(@"(?<![\w.])" + Regex.Escape(param.Func) + @"\(");
        foreach (var f in package)
        {
            var lines = ctx.Lines(f);
            var containers = ctx.ContainersOf(f);
            var siblings = package.Where(x => x != f).Select(ctx.Lines).ToList();
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("func ", StringComparison.Ordinal)) continue;
                var m = call.Match(lines[i]);
                if (!m.Success || PolyText.InCommentOrString(lines[i], m.Index, SourceLang.Go)) continue;
                var args = PolyText.SplitArgs(PolyText.Statement(lines, i), m.Index + m.Length - 1);
                if (param.Index >= args.Count) continue;
                var a = PolyAddress.Resolve(args[param.Index], lines, i, SourceLang.Go, protocol == "grpc", 0, siblings);
                if (a.Url is null && a.ConfigKey is null) continue;
                foreach (var c in containers) found.Add(new Outbound(c.Key, Id, protocol, null, a.ConfigKey, a.Url, f, i + 1, null));
            }
        }
        return found;
    }
}

/// <summary>
/// Python 的出口：requests / httpx 的模块级调用与 request(method, url)、httpx.Client(base_url=…) / aiohttp.ClientSession(base_url=…)、
/// 会话 / 客户端变量上的 .get / .post…（只认解析出绝对地址或环境变量的）、grpc.insecure_channel / secure_channel（含 grpc.aio）。
/// </summary>
public sealed class PythonOutboundRule : IDiscoveryRule
{
    public string Id => "python-outbound";
    public string Description => "Python 的 HTTP 调用（requests、httpx、aiohttp）与 gRPC 通道（grpc.insecure_channel），地址来自字面量、环境变量或 settings";

    private sealed record Call(Regex Regex, string Protocol, int Arg, string? Keyword, bool Strict = false);

    private static readonly Call[] Calls =
    [
        new(new(@"\b(?:requests|httpx)\.(?:get|post|put|patch|delete|head|options)\(", RegexOptions.Compiled), "http", 0, "url"),
        new(new(@"\b(?:requests|httpx)\.request\(", RegexOptions.Compiled), "http", 1, "url"),
        new(new(@"\bhttpx\.(?:Async)?Client\(", RegexOptions.Compiled), "http", -1, "base_url"),
        new(new(@"\baiohttp\.ClientSession\(", RegexOptions.Compiled), "http", -1, "base_url"),
        new(new(@"\b(?!requests\b|httpx\b)\w*(?:session|client)\w*\.(?:get|post|put|patch|delete)\(", RegexOptions.Compiled | RegexOptions.IgnoreCase), "http", 0, "url", Strict: true),
        new(new(@"\bgrpc\.(?:aio\.)?(?:insecure|secure)_channel\(", RegexOptions.Compiled), "grpc", 0, "target"),
    ];

    public List<(string File, int Line)> Unresolved { get; } = [];

    public void Apply(RuleContext ctx)
    {
        Unresolved.Clear();
        foreach (var file in ctx.SourceFilesOf(".py"))
        {
            var lines = ctx.Lines(file);
            var http = Array.Exists(lines, l => Regex.IsMatch(l, @"^\s*(?:import|from)\s+(?:requests|httpx|aiohttp)\b"));
            var grpc = Array.Exists(lines, l => l.Contains("grpc.", StringComparison.Ordinal));
            if (!http && !grpc) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            for (var i = 0; i < lines.Length; i++)
                foreach (var call in Calls)
                {
                    if (call.Protocol == "http" && !http) continue;
                    var m = call.Regex.Match(lines[i]);
                    if (!m.Success || PolyText.InCommentOrString(lines[i], m.Index, SourceLang.Python)) continue;
                    var args = PolyText.SplitArgs(PolyText.Statement(lines, i), m.Index + m.Length - 1);
                    var expr = (call.Keyword is not null ? PolyText.Keyword(args, call.Keyword) : null) ?? (call.Arg >= 0 ? PolyText.Positional(args, call.Arg, SourceLang.Python) : null);
                    var address = expr is not null ? PolyAddress.Resolve(expr, lines, i, SourceLang.Python, call.Protocol == "grpc") : new AddressResolver.Address(null, null);
                    if (call.Protocol == "http" && address.Url is null && address.ConfigKey is null)
                    {
                        if (!call.Strict) Unresolved.Add((file, i + 1));
                        continue;
                    }
                    foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, call.Protocol, null, address.ConfigKey, address.Url, file, i + 1, null));
                    break; // 一行只登记一个调用
                }
        }
    }
}

/// <summary>
/// Go / Python 里直接说明外部系统的代码模式：gorm.Open(驱动.Open(…)) / sql.Open("驱动") / Django settings 的 ENGINE / create_engine("方案://") → 数据库；
/// Kafka / RabbitMQ 的生产与消费（给出方向，替掉包引用的泛协议边）；Redis / MongoDB 客户端；smtplib → 邮件；boto3.client("s3") → AWS 服务。
/// </summary>
public sealed class PolyglotInfraRule : IDiscoveryRule
{
    public string Id => "polyglot-infra";
    public string Description => "Go / Python 的数据库打开、Kafka / RabbitMQ 生产消费、Redis / MongoDB 客户端、smtplib、boto3 等代码模式";

    /// <param name="NameFrom">名字从第 1 个捕获组经映射表得到；映射不到就不算。</param>
    /// <param name="FileMustContain">文件里必须出现的文本（.Publish( 这种通用调用只在用了 amqp 的文件里算）。</param>
    private sealed record Pattern(SourceLang Lang, Regex Regex, string SubKind, string Name, string Protocol, IReadOnlyDictionary<string, string>? NameFrom = null, string? FileMustContain = null);

    private static readonly Dictionary<string, string> SqlDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "PostgreSQL", ["postgresql"] = "PostgreSQL", ["pgx"] = "PostgreSQL", ["postgresql_psycopg2"] = "PostgreSQL", ["postgis"] = "PostgreSQL",
        ["mysql"] = "MySQL", ["sqlite"] = PackageReferenceRule.SQLite, ["sqlite3"] = PackageReferenceRule.SQLite, ["spatialite"] = PackageReferenceRule.SQLite,
        ["sqlserver"] = "SQL Server", ["mssql"] = "SQL Server", ["oracle"] = "Oracle", ["clickhouse"] = "ClickHouse",
    };

    private static readonly Dictionary<string, string> AwsServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s3"] = "AWS S3", ["sqs"] = "AWS SQS", ["sns"] = "AWS SNS", ["dynamodb"] = "AWS DynamoDB", ["ses"] = "AWS SES", ["kinesis"] = "AWS Kinesis", ["lambda"] = "AWS Lambda",
    };

    private static readonly Pattern[] Patterns =
    [
        new(SourceLang.Go, new(@"\bgorm\.Open\(\s*(\w+)\.Open\(", RegexOptions.Compiled), "database", "", "sql", SqlDrivers),
        new(SourceLang.Go, new(@"\bsqlx?\.(?:Open|Connect)\(\s*""(\w+)""", RegexOptions.Compiled), "database", "", "sql", SqlDrivers),
        new(SourceLang.Go, new(@"\bpgxpool\.(?:New|Connect|NewWithConfig)\(|\bpgx\.Connect\(", RegexOptions.Compiled), "database", "PostgreSQL", "sql"),
        new(SourceLang.Go, new(@"\bredis\.(?:NewClient|NewClusterClient|NewUniversalClient|NewFailoverClient)\(", RegexOptions.Compiled), "cache", "Redis", "redis"),
        new(SourceLang.Go, new(@"\bkafka\.NewWriter\(|&kafka\.Writer\{|\bsarama\.New(?:Sync|Async)Producer\(", RegexOptions.Compiled), "broker", "Kafka", "kafka-produce"),
        new(SourceLang.Go, new(@"\bkafka\.NewReader\(|\bsarama\.NewConsumer(?:Group)?\(", RegexOptions.Compiled), "broker", "Kafka", "kafka-consume"),
        new(SourceLang.Go, new(@"\.Publish(?:WithContext)?\(", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-produce", FileMustContain: "amqp"),
        new(SourceLang.Go, new(@"\.Consume\(", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-consume", FileMustContain: "amqp"),
        new(SourceLang.Go, new(@"\bmongo\.(?:Connect|NewClient)\(", RegexOptions.Compiled), "database", "MongoDB", "mongo"),
        new(SourceLang.Python, new(@"['""]ENGINE['""]\s*:\s*['""]django\.(?:contrib\.gis\.)?db\.backends\.(\w+)['""]", RegexOptions.Compiled), "database", "", "sql", SqlDrivers),
        new(SourceLang.Python, new(@"\bcreate_(?:async_)?engine\(\s*[fr]?['""](\w+)(?:\+\w+)?://", RegexOptions.Compiled), "database", "", "sql", SqlDrivers),
        new(SourceLang.Python, new(@"\bsmtplib\.SMTP(?:_SSL)?\(", RegexOptions.Compiled), "email", "SMTP 邮件服务", "smtp"),
        new(SourceLang.Python, new(@"\.basic_publish\(", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-produce"),
        new(SourceLang.Python, new(@"\.basic_consume\(", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-consume"),
        new(SourceLang.Python, new(@"\b(?:AIO)?KafkaProducer\(", RegexOptions.Compiled), "broker", "Kafka", "kafka-produce"),
        new(SourceLang.Python, new(@"\b(?:AIO)?KafkaConsumer\(", RegexOptions.Compiled), "broker", "Kafka", "kafka-consume"),
        new(SourceLang.Python, new(@"\bredis\.(?:Redis|StrictRedis|from_url)\(", RegexOptions.Compiled), "cache", "Redis", "redis"),
        new(SourceLang.Python, new(@"\bMongoClient\(", RegexOptions.Compiled), "database", "MongoDB", "mongo", FileMustContain: "mongo"),
        new(SourceLang.Python, new(@"\bboto3\.(?:client|resource)\(\s*['""](\w+)['""]", RegexOptions.Compiled), "storage", "", "http", AwsServices),
    ];

    private static readonly string[] QuickKeywords = ["gorm.Open", "sql.Open", "sqlx.", "pgx", "redis.", "kafka.", "sarama.", ".Publish", ".Consume(", "mongo.", "ENGINE", "create_engine", "create_async_engine", "smtplib", "basic_publish", "basic_consume", "KafkaProducer", "KafkaConsumer", "MongoClient", "boto3."];

    public void Apply(RuleContext ctx)
    {
        foreach (var file in ctx.SourceFilesOf(".go").Concat(ctx.SourceFilesOf(".py")))
        {
            var lang = file.EndsWith(".go", StringComparison.OrdinalIgnoreCase) ? SourceLang.Go : SourceLang.Python;
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => QuickKeywords.Any(k => l.Contains(k, StringComparison.Ordinal)))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var text = string.Join('\n', lines);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!QuickKeywords.Any(k => lines[i].Contains(k, StringComparison.Ordinal))) continue;
                foreach (var p in Patterns)
                {
                    if (p.Lang != lang || (p.FileMustContain is not null && !text.Contains(p.FileMustContain, StringComparison.Ordinal))) continue;
                    var m = p.Regex.Match(lines[i]);
                    if (!m.Success || PolyText.InCommentOrString(lines[i], m.Index, lang)) continue;
                    var name = p.Name;
                    var subKind = p.SubKind;
                    if (ReferenceEquals(p.NameFrom, AwsServices))
                    {
                        // boto3.client("s3")：映射表里没有的服务也记，名字就是 "AWS 服务名"
                        var svc = m.Groups[1].Value.ToLowerInvariant();
                        name = AwsServices.TryGetValue(svc, out var aws) ? aws : "AWS " + svc;
                        subKind = svc switch { "s3" => "storage", "sqs" or "sns" or "kinesis" => "broker", "dynamodb" => "database", "ses" => "email", _ => "http-service" };
                    }
                    else if (p.NameFrom is not null)
                    {
                        if (!p.NameFrom.TryGetValue(m.Groups[1].Value, out var mapped)) continue;
                        name = mapped;
                    }
                    var evidence = new Evidence(file, i + 1, lines[i].Trim());
                    var key = ctx.External(subKind, name, evidence);
                    foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, p.Protocol, name, null, null, file, i + 1, key));
                }
            }
        }
    }
}
