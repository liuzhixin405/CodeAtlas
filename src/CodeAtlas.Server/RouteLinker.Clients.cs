using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// C# 后端经已注册的 HttpClient 调另一个容器。容器图的 http-client 出口记着注册（<c>AddHttpClient(名字, c => c.BaseAddress = 配置里的地址)</c>）
/// 与它对到的容器；这里只在能确定一处调用用的是哪个注册时才匹配，并且只在那个注册的目标容器里找入口：
/// <list type="bullet">
/// <item>方法里 <c>CreateClient(名字)</c> 拿到的客户端上的 HttpClient 调用（GetAsync / SendAsync …）；类型化客户端（注册名就是类名）自己文件里的 HttpClient 调用。</item>
/// <item>封装类：在类一级点名了注册的文件（<c>: base(http, ConstConfig.X)</c>、<c>const string ClientName = "XClient"</c>，不算方法里的 CreateClient）里声明的类，
/// 以及它们实现的、仓库里声明的接口。注入了这些类型的字段 / 参数上的调用（<c>_helper.PostJsonAsync(url, body)</c>）、派生类里对基类方法的调用
/// （<c>SendRequestAsync(id, HttpMethod.Post, "gateway/x", …)</c>）算对这个注册的调用。路径实参按封装方法声明里名字像 url / uri / endpoint / path
/// 的 string 参数取（声明找不到就不算），方法按 HttpMethod 参数、否则按方法名前缀（GetAsync / PostJsonAsync …）取。</item>
/// </list>
/// 路径表达式见 <see cref="CSharpSource"/>：字面量、插值串、拼接、string.Format、new Uri(基, 相对)、方法里的局部变量、本类字段与别的类的 const。
/// 相对路径接在注册的基地址路径后（BaseAddress …/api/ + "gateway/x" → /api/gateway/x），开头求不出的孔当基地址丢掉。
/// 先前只认 HttpClient 自己的方法、要求路径以 /api 开头、不解析别的文件里的常量，封装出来的调用全部续接不上。
/// </summary>
public static partial class RouteLinker
{
    private sealed record ClientRegistration(string FromContainer, string Name, string? Value, string TargetKey, string BasePath);

    /// <param name="MethodStart">调用所在方法的 [起, 止] 行，去重用（同一方法里第 2 段已经按字面量对上同一入口的不再记）。</param>
    private sealed record BoundCall(string File, int Line, string Path, string? Method, string Target, int MethodStart, int MethodEnd);

    [GeneratedRegex(@"\bCreateClient\s*\(")]
    private static partial Regex CreateClientCall();
    [GeneratedRegex(@"^(Get|Post|Put|Delete|Patch)(?![a-z])")]
    private static partial Regex VerbPrefix();
    [GeneratedRegex(@"\bHttpMethod\s*\.\s*(Get|Post|Put|Delete|Patch)\b")]
    private static partial Regex HttpMethodArg();
    [GeneratedRegex(@"(?i)(url|uri|endpoint|path|route|address)$")]
    private static partial Regex UrlParameterName();

    /// <summary>注册的基地址 → 路径部分：http://localhost:5003/api/ → /api/；没有地址或不是绝对地址 → /。</summary>
    public static string BasePathOf(string? url)
    {
        if (url is null) return "/";
        var m = Regex.Match(url.Trim(), @"^[A-Za-z][\w+.-]*://[^/?#]*(/[^?#]*)?");
        if (!m.Success || !m.Groups[1].Success) return "/";
        return "/" + m.Groups[1].Value.Trim('/') + (m.Groups[1].Value.Trim('/').Length > 0 ? "/" : "");
    }

    /// <summary>
    /// 求值后的路径 → 请求路径：开头求不出的孔（{managerUrl}、{resolver.BaseUrl(id)}）当基地址丢掉并接到基地址路径后；
    /// 没有孔、以 / 开头的是绝对路径（HttpClient 的 BaseAddress 只换路径）；其余接到基地址路径后。绝对 URL、含空格、去掉查询串后为空 → null。
    /// </summary>
    public static string? BoundPath(string value, string basePath)
    {
        var v = value.Trim();
        var hole = Regex.Match(v, @"^(?:\$\{[^}]*\})+");
        var relative = hole.Success;
        if (hole.Success) v = v[hole.Length..];
        if (v.Contains("://", StringComparison.Ordinal) || v.Contains(' ')) return null;
        var cut = v.IndexOfAny(['?', '#']);
        if (cut >= 0) v = v[..cut];
        if (v.Trim('/').Length == 0) return null;
        var p = !relative && v.StartsWith('/') ? v : basePath.TrimEnd('/') + "/" + v.TrimStart('/');
        return "/" + p.Trim('/');
    }

    private static IEnumerable<(string From, BoundCall Call)> RegisteredClientCalls(
        IReadOnlyList<Outbound> outbound, IReadOnlyList<ArchNode> containers, IReadOnlyList<MapSymbol> symbols,
        ILookup<string, MapSymbol> methodsByFile, Func<string, string[]> readLines, Func<string, IReadOnlyList<ArchNode>> ownersOf)
    {
        var containerKeys = containers.Where(c => c.Kind == ArchNodeKind.Container).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var raw = outbound.Where(o => o.RuleId == "http-client" && o.ClientName is { Length: > 0 } && o.TargetKey is { } t && containerKeys.Contains(t) && t != o.ContainerKey).ToList();
        if (raw.Count == 0) yield break;
        var cs = new CSharpSource(symbols, readLines);
        var regs = raw.Select(o => new ClientRegistration(o.ContainerKey, o.ClientName!, cs.ClientNameValue(o.File, o.Line, o.ClientName!), o.TargetKey!, BasePathOf(o.Url)))
            .DistinctBy(r => (r.FromContainer, r.Name)).ToList();
        var byContainer = regs.ToLookup(r => r.FromContainer, StringComparer.Ordinal);
        var clientNames = regs.Select(r => r.Name).Distinct(StringComparer.Ordinal).ToList();
        var valueOf = regs.GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => r.Value).FirstOrDefault(v => v is not null), StringComparer.Ordinal);

        // 实参 / 记号是不是点名了某个注册：标识符（X、Cls.X）或字面量（值或名字，不分大小写）
        string? ClientOf(string arg, string file)
        {
            var a = arg.Trim();
            var lit = CSharpSource.LiteralContent(a) ?? cs.Eval(a, file, "", 0);
            foreach (var n in clientNames)
            {
                if (Regex.IsMatch(a, $@"^(?:[\w.]+\.)?{Regex.Escape(n)}$")) return n;
                if (lit is not null && (string.Equals(lit, n, StringComparison.OrdinalIgnoreCase) || (valueOf[n] is { } v && string.Equals(lit, v, StringComparison.OrdinalIgnoreCase)))) return n;
            }
            return null;
        }

        var csFiles = methodsByFile.Where(g => LangOf(g.Key) == Lang.CSharp && ownersOf(g.Key).Any(c => byContainer[c.Key].Any())).ToList();

        // 封装文件：在 CreateClient(…) 之外点名了恰好一个注册（类一级的 base(…, ConstConfig.X) / const 字段）；类型化客户端的类自己的文件
        var classes = symbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Struct).ToList();
        var classesByFile = classes.ToLookup(s => s.Path, StringComparer.Ordinal);
        var typeNames = classes.Select(s => SimpleName(s.QualifiedName)).ToHashSet(StringComparer.Ordinal);
        var wrapperFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        // 封装类可能只有构造函数（不一定有方法符号）：按声明了类的文件找
        foreach (var file in classesByFile.Select(g => g.Key).Where(f => LangOf(f) == Lang.CSharp && !PipelineBuilder.IsTest(f, "") && ownersOf(f).Any(c => byContainer[c.Key].Any())))
        {
            var text = CSharpSource.StripComments(string.Join('\n', readLines(file)));
            var outside = CSharpSource.BlankCalls(text, CreateClientCall());
            var named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in clientNames)
            {
                if (typeNames.Contains(n)) continue; // 类型化客户端按类名认，不按记号
                if (Regex.IsMatch(outside, $@"(?<![\w""]){Regex.Escape(n)}\b")) named.Add(n);
                foreach (var lit in new[] { n, valueOf[n] }.OfType<string>())
                    if (outside.Contains('"' + lit + '"', StringComparison.OrdinalIgnoreCase)) named.Add(n);
            }
            if (named.Count == 1) wrapperFiles[file] = named.First();
        }
        foreach (var n in clientNames.Where(typeNames.Contains))
            foreach (var t in classes.Where(s => SimpleName(s.QualifiedName) == n))
                wrapperFiles.TryAdd(t.Path, n);

        // 封装类型 → 注册：封装文件里声明的类，加上它们基类列表里仓库声明的接口
        var interfaceNames = symbols.Where(s => s.Kind == SymbolKind.Interface).Select(s => SimpleName(s.QualifiedName)).ToHashSet(StringComparer.Ordinal);
        var boundTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Bind(string type, string client) => boundTypes[type] = boundTypes.TryGetValue(type, out var had) && had != client ? null : client; // 一个类型对两个注册：不猜
        foreach (var (file, client) in wrapperFiles)
            foreach (var t in classesByFile[file])
            {
                var name = SimpleName(t.QualifiedName);
                Bind(name, client);
                foreach (var b in cs.BaseTypes(t).Where(interfaceNames.Contains)) Bind(b, client);
            }
        var bound = boundTypes.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
        foreach (var g in csFiles)
        {
            var file = g.Key;
            var owners = ownersOf(file).Where(c => byContainer[c.Key].Any()).ToList();
            var lines = readLines(file);
            var text = string.Join('\n', lines);
            wrapperFiles.TryGetValue(file, out var fileClient);
            // 字段 / 构造参数 / 主构造参数的类型是封装类型 → 变量名 → (注册, 类型)
            var receivers = new Dictionary<string, (string Client, string Type)>(StringComparer.Ordinal);
            foreach (var (type, client) in bound)
            {
                if (!text.Contains(type, StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(text, $@"(?<![\w.]){Regex.Escape(type)}\??\s+(@?\w+)\s*(?=[,;=)])"))
                    receivers.TryAdd(m.Groups[1].Value, (client, type));
            }
            // 文件里的类派生自封装类型 → (注册, 基类型)
            var inherited = classesByFile[file]
                .SelectMany(t => cs.BaseTypes(t)).Where(bound.ContainsKey).Distinct(StringComparer.Ordinal).Select(b => (Client: bound[b], Type: b)).ToList();
            if (fileClient is null && receivers.Count == 0 && inherited.Count == 0 && !CreateClientCall().IsMatch(text)) continue;

            foreach (var method in g)
            {
                var start = Math.Max(0, method.StartLine - 1);
                var end = Math.Min(lines.Length, method.EndLine);
                if (end <= start) continue;
                var body = string.Join('\n', lines[start..end]);
                int LineAt(int index) => start + 1 + body.AsSpan(0, index).Count('\n');
                var calls = new List<(int Index, string Client, string? Endpoint, string? Verb)>();

                // CreateClient(名字) 拿到的客户端 / 类型化客户端文件里的 HttpClient 调用
                var created = CreateClientCall().Matches(body).Select(m => CSharpSource.Args(body, m.Index + m.Length - 1).FirstOrDefault())
                    .Select(a => a is null ? null : ClientOf(a, file)).Distinct().ToList();
                var direct = created.Count == 1 && created[0] is not null ? created[0] : created.Count == 0 ? fileClient : null;
                if (direct is not null)
                    foreach (Match m in HttpCall().Matches(body))
                    {
                        var open = CSharpSource.OpenParen(body, m.Index + m.Length - 1);
                        if (open < 0) continue;
                        var args = CSharpSource.Args(body, open);
                        if (args.Count == 0) continue;
                        calls.Add((m.Groups[1].Index, direct, args[0], VerbPrefix().Match(m.Groups[1].Value) is { Success: true } v ? v.Value.ToUpperInvariant() : null));
                    }

                // 封装类型变量上的调用、派生类里对基类方法的调用：路径 / 方法实参按封装方法的声明取
                void Wrapper(int nameIndex, string name, int open, string client, string type)
                {
                    if (cs.FindMethod(type, name) is not { } decl) return;
                    var args = CSharpSource.Args(body, open);
                    string? Arg(int i, string pname)
                    {
                        var named = args.FirstOrDefault(a => Regex.IsMatch(a, $@"^{Regex.Escape(pname)}\s*:(?!:)"));
                        if (named is not null) return named[(named.IndexOf(':') + 1)..].Trim();
                        return i < args.Count && !Regex.IsMatch(args[i], @"^\w+\s*:(?!:)") ? args[i] : null;
                    }
                    var ui = decl.FindIndex(p => p.Type.TrimEnd('?') is "string" or "Uri" && UrlParameterName().IsMatch(p.Name));
                    if (ui < 0) return;
                    var endpoint = Arg(ui, decl[ui].Name);
                    if (endpoint is null) return;
                    var mi = decl.FindIndex(p => p.Type.TrimEnd('?') == "HttpMethod");
                    var verb = mi >= 0 && Arg(mi, decl[mi].Name) is { } ma && HttpMethodArg().Match(ma) is { Success: true } hm ? hm.Groups[1].Value.ToUpperInvariant()
                        : mi < 0 && VerbPrefix().Match(name) is { Success: true } vp ? vp.Value.ToUpperInvariant() : null;
                    calls.Add((nameIndex, client, endpoint, verb));
                }
                foreach (var (recv, (client, type)) in receivers)
                    foreach (Match m in Regex.Matches(body, $@"(?<![\w.])(?:this\s*\.\s*)?{Regex.Escape(recv)}\s*\??\.\s*(\w+)\s*(?=[<(])"))
                    {
                        var open = CSharpSource.OpenParen(body, m.Index + m.Length);
                        if (open >= 0) Wrapper(m.Groups[1].Index, m.Groups[1].Value, open, client, type);
                    }
                foreach (var (client, type) in inherited)
                    foreach (Match m in Regex.Matches(body, @"(?<![\w.]|\.\s*)(?:(?:this|base)\s*\.\s*)?(\w+)\s*(?=[<(])"))
                    {
                        if (LineAt(m.Groups[1].Index) == method.StartLine) continue; // 签名行里的方法名不是调用
                        var open = CSharpSource.OpenParen(body, m.Index + m.Length);
                        if (open >= 0) Wrapper(m.Groups[1].Index, m.Groups[1].Value, open, client, type);
                    }

                foreach (var (index, client, endpoint, verb) in calls)
                {
                    if (endpoint is null) continue;
                    var (value, requestVerb) = cs.Endpoint(endpoint, file, body, index);
                    if (value is null) continue;
                    foreach (var c in owners)
                    {
                        var reg = byContainer[c.Key].FirstOrDefault(r => r.Name == client);
                        if (reg is null || BoundPath(value, reg.BasePath) is not { } path) continue;
                        yield return (c.Key, new BoundCall(file, LineAt(index), path, verb ?? requestVerb, reg.TargetKey, method.StartLine, method.EndLine));
                    }
                }
            }
        }
    }

    private static string SimpleName(string qualifiedName)
    {
        var s = qualifiedName;
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var generic = s.IndexOf('<');
        if (generic >= 0) s = s[..generic];
        return s[(s.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// C# 源码里的字符串求值（只为路由续接）：字面量 / 逐字串 / 插值串（含嵌套串）、顶层 + 拼接、string.Format（{0} → 孔）、new Uri(基, 相对)、
    /// new HttpRequestMessage(方法, 地址)、方法里在调用之前的局部变量赋值、本文件的字段 / const、别的类的 const（类名.名字，按索引里的类型找文件）。
    /// 求不出的部分写成 ${名字} 孔；整个都求不出 → null。
    /// </summary>
    public sealed partial class CSharpSource(IReadOnlyList<MapSymbol> symbols, Func<string, string[]> readLines)
    {
        private readonly ILookup<string, MapSymbol> _types = symbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface)
            .ToLookup(s => SimpleName(s.QualifiedName), StringComparer.Ordinal);
        private readonly ILookup<long, MapSymbol> _children = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        private readonly ILookup<string, MapSymbol> _methodsByFile = symbols.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function).ToLookup(s => s.Path, StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _fields = new(StringComparer.Ordinal);

        [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|readonly|const|new|required)\s+)*string\??\s+(\w+)\s*=\s*(.+?);\s*(?://.*)?$")]
        private static partial Regex FieldDeclaration();
        [GeneratedRegex(@"^(?:[A-Za-z_]\w*\s*\.\s*)*[A-Za-z_]\w*$")]
        private static partial Regex DottedName();

        /// <summary>注册名是常量标识符时它的值：从登记那一行找 Cls.Name / Name 再查常量（ConstConfig.MT → "MtClient"）。</summary>
        public string? ClientNameValue(string file, int line, string name)
        {
            var lines = readLines(file);
            var text = line >= 1 && line <= lines.Length ? lines[line - 1] : "";
            var m = Regex.Match(text, $@"(?:(\w+)\s*\.\s*)?\b{Regex.Escape(name)}\b(?!\s*"")");
            if (!m.Success || text[..m.Index].TrimEnd().EndsWith('"')) return null;
            return Constant(m.Groups[1].Success ? m.Groups[1].Value : null, name, file, 0);
        }

        /// <summary>类型声明头里基类 / 接口的简名（主构造参数、泛型实参、where 约束去掉）。</summary>
        public IEnumerable<string> BaseTypes(MapSymbol type)
        {
            var header = JavaSource.TypeHeader(readLines(type.Path), type.StartLine);
            var name = SimpleName(type.QualifiedName);
            var at = Regex.Match(header, $@"\b(?:class|struct|interface|record)\s+{Regex.Escape(name)}\b");
            if (!at.Success) yield break;
            var rest = header[(at.Index + at.Length)..];
            var i = 0;
            while (i < rest.Length)
            {
                var ch = rest[i];
                if (ch is '<' or '(')
                {
                    var close = ch == '(' ? ArgParser.MatchClose(rest, i) : MatchAngle(rest, i);
                    if (close < 0) yield break;
                    i = close + 1;
                    continue;
                }
                if (ch == ':') break;
                if (!char.IsWhiteSpace(ch)) yield break;
                i++;
            }
            if (i >= rest.Length) yield break;
            var list = rest[(i + 1)..];
            var where = Regex.Match(list, @"\bwhere\b");
            if (where.Success) list = list[..where.Index];
            foreach (var part in SplitTopLevel(list, ','))
            {
                var p = part.Trim();
                var lt = p.IndexOf('<');
                if (lt >= 0) p = p[..lt];
                p = p[(p.LastIndexOf('.') + 1)..].Trim();
                if (p.Length > 0) yield return p;
            }
        }

        /// <summary>类型（含基类 / 接口链，最多 4 层）里名为 name 的方法的形参表 (类型, 名字)；找不到 → null。</summary>
        public List<(string Type, string Name)>? FindMethod(string typeName, string name, int depth = 0)
        {
            if (depth > 4) return null;
            foreach (var t in _types[typeName])
            {
                foreach (var m in _children[t.Id].Where(c => c.Kind is SymbolKind.Method && SimpleName(c.QualifiedName) == name))
                {
                    var signature = JavaSource.Signature(readLines(m.Path), m.StartLine, name);
                    var at = Regex.Match(signature, $@"\b{Regex.Escape(name)}\s*(?=[<(])");
                    if (!at.Success) continue;
                    var open = OpenParen(signature, at.Index + at.Length);
                    if (open < 0) continue;
                    var close = ArgParser.MatchClose(signature, open);
                    if (close < 0) continue;
                    var ps = new List<(string, string)>();
                    foreach (var a in Parameters(signature[(open + 1)..close]).Where(a => a.Length > 0))
                    {
                        var p = Regex.Replace(a, @"^\s*(?:\[[^\]]*\]\s*)*", "");
                        p = Regex.Replace(p, @"^(?:this|params|ref|out|in|scoped)\s+", "");
                        var eq = SplitTopLevel(p, '=')[0].Trim();
                        var sp = eq.LastIndexOf(' ');
                        if (sp < 0) { ps.Add(("", eq)); continue; }
                        ps.Add((eq[..sp].Trim(), eq[(sp + 1)..].Trim()));
                    }
                    return ps;
                }
                foreach (var b in BaseTypes(t))
                    if (FindMethod(b, name, depth + 1) is { } found) return found;
            }
            return null;
        }

        /// <summary>
        /// 调用的路径实参 → (值, 请求方法)：顺着局部变量找到初始化表达式；是 new HttpRequestMessage(HttpMethod.X, 地址) 时取地址与方法。
        /// body 是方法体文本，before 是调用在 body 里的位置（只看它之前的赋值）。
        /// </summary>
        public (string? Value, string? Verb) Endpoint(string expr, string file, string body, int before)
        {
            var e = expr.Trim();
            for (var hop = 0; hop < 4 && DottedName().IsMatch(e) && !e.Contains('.'); hop++)
            {
                if (LocalInitializer(e, body, before) is not { } init) break;
                e = init.Expr;
                before = init.At;
            }
            var req = Regex.Match(e, @"^new\s+(?:System\.Net\.Http\.)?HttpRequestMessage\s*\(");
            if (req.Success)
            {
                var args = Args(e, req.Index + req.Length - 1);
                if (args.Count < 2) return (null, null);
                var verb = HttpMethodArg().Match(args[0]) is { Success: true } hm ? hm.Groups[1].Value.ToUpperInvariant() : null;
                return (Eval(args[1], file, body, before), verb);
            }
            return (Eval(e, file, body, before), null);
        }

        /// <summary>
        /// 表达式求值（见类说明）。body / before 给局部变量用；depth 防常量互相引用转圈；
        /// bound 是内联本文件小方法时形参 → 已求出的实参值（<c>Endpoint(UrlConsts.X, id)</c> 这类 string.Format 包装）。
        /// </summary>
        public string? Eval(string expr, string file, string body, int before, int depth = 0, IReadOnlyDictionary<string, string>? bound = null)
        {
            if (depth > 6) return null;
            var e = expr.Trim();
            e = Regex.Replace(e, @"\.\s*ToString\s*\(\s*\)$", "");
            var coalesce = SplitTopLevel(e, '?', "??");
            if (coalesce.Count > 1) e = coalesce[0].Trim();
            var parts = SplitTopLevel(e, '+');
            if (parts.Count > 1)
            {
                var sb = new StringBuilder();
                var known = false;
                foreach (var part in parts)
                {
                    var v = Eval(part, file, body, before, depth + 1, bound);
                    known |= v is not null;
                    sb.Append(v ?? Hole(part));
                }
                return known ? sb.ToString() : null;
            }
            if (e.StartsWith('(') && ArgParser.MatchClose(e, 0) == e.Length - 1) return Eval(e[1..^1], file, body, before, depth + 1, bound);
            if (StringLiteral(e) is { } lit) return Interpolate(lit, file, body, before, depth, bound);
            var fmt = Regex.Match(e, @"^(?:string|String|System\.String)\s*\.\s*Format\s*\(");
            if (fmt.Success)
            {
                var args = Args(e, fmt.Index + fmt.Length - 1);
                var first = args.Count > 0 && Regex.IsMatch(args[0], @"^\w+\s*\.\s*InvariantCulture|CultureInfo") ? 1 : 0; // string.Format(CultureInfo.InvariantCulture, fmt, …)
                if (args.Count <= first || Eval(args[first], file, body, before, depth + 1, bound) is not { } f) return null;
                return Regex.Replace(f, @"\{\d+(?:[,:][^{}]*)?\}", "$${x}");
            }
            var uri = Regex.Match(e, @"^new\s+(?:System\.)?Uri\s*\(");
            if (uri.Success)
            {
                var args = Args(e, uri.Index + uri.Length - 1);
                if (args.Count == 0) return null;
                // new Uri(相对, UriKind.Relative) / new Uri(new Uri(基), 相对)：相对部分才是路径
                var rel = args.Count >= 2 && !args[1].TrimStart().StartsWith("UriKind", StringComparison.Ordinal) ? args[1] : args[0];
                return Eval(rel, file, body, before, depth + 1, bound);
            }
            var call = Regex.Match(e, @"^(\w+)\s*\(");
            if (call.Success && ArgParser.MatchClose(e, call.Length - 1) == e.Length - 1)
                return Inline(call.Groups[1].Value, Args(e, call.Length - 1).Select(a => Eval(a, file, body, before, depth + 1, bound) ?? Hole(a)).ToList(), file, depth + 1);
            if (!DottedName().IsMatch(e)) return null;
            var name = Regex.Replace(e, @"\s+", "");
            if (name.StartsWith("this.", StringComparison.Ordinal)) name = name[5..];
            var dot = name.LastIndexOf('.');
            if (dot < 0)
            {
                if (bound is not null && bound.TryGetValue(name, out var arg)) return arg;
                if (body.Length > 0 && LocalInitializer(name, body, before) is { } init) return Eval(init.Expr, file, body, init.At, depth + 1, bound);
                return Constant(null, name, file, depth + 1);
            }
            var owner = name[..dot];
            return Constant(owner[(owner.LastIndexOf('.') + 1)..], name[(dot + 1)..], file, depth + 1);
        }

        /// <summary>
        /// 本文件里名为 name 的方法（表达式体 <c>=> …;</c>，或块体里最后一个 <c>return …;</c>）按实参值内联求值；同名重载按形参个数挑，挑不出来不猜。
        /// </summary>
        private string? Inline(string name, List<string> args, string file, int depth)
        {
            if (depth > 6) return null;
            var lines = readLines(file);
            var candidates = new List<(List<string> Params, bool Variadic, string Body, string Expr, int At)>();
            foreach (var m in _methodsByFile[file].Where(m => SimpleName(m.QualifiedName) == name && m.EndLine >= m.StartLine))
            {
                var text = string.Join('\n', lines[Math.Max(0, m.StartLine - 1)..Math.Min(lines.Length, m.EndLine)]);
                var at = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*(?=[<(])");
                if (!at.Success) continue;
                var open = OpenParen(text, at.Index + at.Length);
                var close = open < 0 ? -1 : ArgParser.MatchClose(text, open);
                if (close < 0) continue;
                var raw = Parameters(text[(open + 1)..close]).Where(x => x.Length > 0).ToList();
                var ps = raw.Select(x => SplitTopLevel(x, '=')[0].Trim()).Select(x => x[(x.LastIndexOf(' ') + 1)..]).ToList();
                var variadic = raw.Count > 0 && raw[^1].StartsWith("params ", StringComparison.Ordinal);
                var rest = text[(close + 1)..];
                var arrow = Regex.Match(rest, @"^\s*=>");
                string expr;
                int exprAt;
                if (arrow.Success)
                {
                    var from = close + 1 + arrow.Index + arrow.Length;
                    var end = StatementEnd(text, from);
                    if (end < 0) continue;
                    (expr, exprAt) = (text[from..end], from);
                }
                else
                {
                    var ret = Regex.Matches(text, @"\breturn\s+").LastOrDefault();
                    if (ret is null) continue;
                    var end = StatementEnd(text, ret.Index + ret.Length);
                    if (end < 0) continue;
                    (expr, exprAt) = (text[(ret.Index + ret.Length)..end], ret.Index);
                }
                candidates.Add((ps, variadic, text, expr, exprAt));
            }
            var fit = candidates.Where(c => c.Params.Count == args.Count || (c.Variadic && args.Count >= c.Params.Count - 1)).ToList();
            if (fit.Count != 1) return null;
            var (pars, _, body, e, before) = fit[0];
            var bound = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < pars.Count && i < args.Count; i++) bound[pars[i]] = args[i];
            return Eval(e, file, body, before, depth + 1, bound);
        }

        private string? Interpolate((string Content, bool Interpolated, bool Verbatim) lit, string file, string body, int before, int depth, IReadOnlyDictionary<string, string>? bound)
        {
            if (!lit.Interpolated) return lit.Content;
            var s = lit.Content;
            var sb = new StringBuilder();
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '{' && i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i++; continue; }
                if (ch == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i++; continue; }
                if (ch != '{') { sb.Append(ch); continue; }
                var close = MatchHole(s, i);
                if (close < 0) return null;
                var hole = s[(i + 1)..close];
                // 对齐 / 格式说明：{x,10} {x:N0}（不切 ?: 三元与 :: 别名）
                var spec = Regex.Match(hole, @"^([\w.\s]+?)\s*[,:](?!:)[^{}]*$");
                var holeExpr = spec.Success ? spec.Groups[1].Value : hole;
                sb.Append(Eval(holeExpr, file, body, before, depth + 1, bound) ?? Hole(holeExpr));
                i = close;
            }
            return sb.ToString();
        }

        private static string Hole(string expr)
        {
            var m = Regex.Match(expr, @"(\w+)\s*(?:\(.*\))?\s*$", RegexOptions.Singleline);
            return "${" + (m.Success ? m.Groups[1].Value : "x") + "}";
        }

        /// <summary>body[..before] 里 name 最后一次声明 / 赋值（var / string / const string name = …; 或 name = …;）的右边与其位置；+= 不算（多是拼查询串）。</summary>
        private static (string Expr, int At)? LocalInitializer(string name, string body, int before)
        {
            var scope = body[..Math.Min(before, body.Length)];
            Match? last = null;
            foreach (Match m in Regex.Matches(scope, $@"(?:\b(?:var|string\??|const\s+string|Uri|HttpRequestMessage)\s+|(?<![\w.]))@?{Regex.Escape(name)}\s*=(?![=>])"))
                last = m;
            if (last is null) return null;
            var from = last.Index + last.Length;
            var end = StatementEnd(body, from);
            return end < 0 ? null : (body[from..end].Trim(), last.Index);
        }

        /// <summary>从 from 起到顶层 ';' 的位置（跳过字符串与括号）；没有 → -1。</summary>
        private static int StatementEnd(string text, int from)
        {
            var depth = 0;
            for (var i = from; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"' || (ch is '$' or '@' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '@'))
                {
                    var end = StringEnd(text, i);
                    if (end < 0) return -1;
                    i = end;
                    continue;
                }
                if (ch == '\'') { var q = text.IndexOf('\'', i + 1 + (i + 1 < text.Length && text[i + 1] == '\\' ? 1 : 0)); if (q < 0) return -1; i = q; continue; }
                if (ch is '(' or '[' or '{') depth++;
                else if (ch is ')' or ']' or '}') depth--;
                else if (ch == ';' && depth <= 0) return i;
            }
            return -1;
        }

        /// <summary>本文件（不在方法体里）的字段 / 常量，或 owner 类型所在文件里的；值按声明处的上下文递归求。</summary>
        private string? Constant(string? owner, string name, string file, int depth)
        {
            if (owner is null)
                return Fields(file).TryGetValue(name, out var raw) ? Eval(raw, file, "", 0, depth + 1) : null;
            string? found = null;
            foreach (var t in _types[owner].Select(t => t.Path).Distinct(StringComparer.Ordinal))
            {
                if (!Fields(t).TryGetValue(name, out var raw) || Eval(raw, t, "", 0, depth + 1) is not { } v) continue;
                if (found is not null && found != v) return null; // 同名类型的同名常量值不同：不猜
                found = v;
            }
            return found;
        }

        private Dictionary<string, string> Fields(string file)
        {
            if (_fields.TryGetValue(file, out var cached)) return cached;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var bodies = _methodsByFile[file].Where(m => m.EndLine > m.StartLine).Select(m => (m.StartLine, m.EndLine)).ToList();
            var lines = readLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = i + 1;
                if (bodies.Any(b => line > b.StartLine && line <= b.EndLine)) continue; // 方法里的局部变量不是字段
                var m = FieldDeclaration().Match(lines[i]);
                if (m.Success) map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }
            return _fields[file] = map;
        }

        // ───── 词法小工具 ─────

        /// <summary>整个表达式是一个字符串字面量（"…"、@"…"、$"…"、$@"…"）：(内容, 是否插值, 是否逐字)；否则 null。三引号原始串不认。</summary>
        internal static (string Content, bool Interpolated, bool Verbatim)? StringLiteral(string e)
        {
            var i = 0;
            var interpolated = false;
            var verbatim = false;
            while (i < e.Length && e[i] is '$' or '@') { if (e[i] == '$') interpolated = true; else verbatim = true; i++; }
            if (i >= e.Length || e[i] != '"' || e.AsSpan(i).StartsWith("\"\"\"")) return null;
            var end = StringEnd(e, 0);
            if (end != e.Length - 1) return null;
            var content = e[(i + 1)..end];
            content = verbatim ? content.Replace("\"\"", "\"") : Regex.Replace(content, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });
            return (content, interpolated, verbatim);
        }

        /// <summary>整个实参是普通 / 逐字字面量时的内容（不插值）；否则 null。</summary>
        public static string? LiteralContent(string e) => StringLiteral(e.Trim()) is { Interpolated: false } l ? l.Content : null;

        /// <summary>text[start] 起的字符串字面量（可带 $ / @ 前缀）的右引号位置；插值孔里的嵌套串与括号跳过。没闭合 → -1。</summary>
        internal static int StringEnd(string text, int start)
        {
            var i = start;
            var interpolated = false;
            var verbatim = false;
            while (i < text.Length && text[i] is '$' or '@') { if (text[i] == '$') interpolated = true; else verbatim = true; i++; }
            if (i >= text.Length || text[i] != '"') return -1;
            for (i++; i < text.Length; i++)
            {
                var ch = text[i];
                if (!verbatim && ch == '\\') { i++; continue; }
                if (ch == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    return i;
                }
                if (interpolated && ch == '{')
                {
                    if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
                    var close = MatchHole(text, i);
                    if (close < 0) return -1;
                    i = close;
                }
                else if (interpolated && ch == '}' && i + 1 < text.Length && text[i + 1] == '}') i++;
            }
            return -1;
        }

        /// <summary>插值孔 text[open] == '{' 的右括号（孔里的串、括号嵌套跳过）；没有 → -1。</summary>
        private static int MatchHole(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"' || (ch is '$' or '@' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '@'))
                {
                    var end = StringEnd(text, i);
                    if (end < 0) return -1;
                    i = end;
                    continue;
                }
                if (ch is '{' or '(' or '[') depth++;
                else if (ch is '}' or ')' or ']')
                {
                    depth--;
                    if (depth == 0) return ch == '}' ? i : -1;
                }
            }
            return -1;
        }

        /// <summary>顶层（不在括号 / 字符串里）切开；token 给出时按多字符记号切（"??"），否则按 sep。'+' 不切 ++ / += 之类。</summary>
        internal static List<string> SplitTopLevel(string text, char sep, string? token = null)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"' || (ch is '$' or '@' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '@'))
                {
                    var end = StringEnd(text, i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }
                if (ch == '\'') { var q = text.IndexOf('\'', i + 1 + (i + 1 < text.Length && text[i + 1] == '\\' ? 1 : 0)); if (q < 0) break; i = q; continue; }
                if (ch is '(' or '[' or '{') { depth++; continue; }
                if (ch is ')' or ']' or '}') { depth--; continue; }
                if (depth != 0) continue;
                if (token is not null)
                {
                    if (text.AsSpan(i).StartsWith(token)) { parts.Add(text[start..i]); i += token.Length - 1; start = i + 1; }
                    continue;
                }
                if (ch != sep) continue;
                if (sep == '+' && ((i + 1 < text.Length && text[i + 1] is '+' or '=') || (i > 0 && text[i - 1] == '+'))) continue;
                parts.Add(text[start..i]);
                start = i + 1;
            }
            parts.Add(text[start..]);
            return parts;
        }

        /// <summary>text[open] == '(' 的调用实参（顶层逗号切开、去空白）；插值串里的嵌套引号与括号跳过。不配对时切到文本末尾。</summary>
        internal static List<string> Args(string text, int open)
        {
            if (open < 0 || open >= text.Length || text[open] != '(') return [];
            var depth = 0;
            var start = open + 1;
            var args = new List<string>();
            for (var i = open; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"' || (ch is '$' or '@' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '@'))
                {
                    var end = StringEnd(text, i);
                    if (end < 0) break;
                    i = end;
                    continue;
                }
                if (ch == '\'') { var q = text.IndexOf('\'', i + 1 + (i + 1 < text.Length && text[i + 1] == '\\' ? 1 : 0)); if (q < 0) break; i = q; continue; }
                if (ch is '(' or '[' or '{') depth++;
                else if (ch is ')' or ']' or '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        args.Add(text[start..i].Trim());
                        return args.Count == 1 && args[0].Length == 0 ? [] : args;
                    }
                }
                else if (ch == ',' && depth == 1) { args.Add(text[start..i].Trim()); start = i + 1; }
            }
            args.Add(text[start..].Trim());
            return args;
        }

        /// <summary>形参表文本按顶层逗号切开（泛型尖括号里的逗号不切：Dictionary&lt;string, string&gt;? headers）。</summary>
        private static List<string> Parameters(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"') { var end = StringEnd(text, i); if (end < 0) break; i = end; continue; }
                if (ch is '(' or '[' or '{' or '<') depth++;
                else if (ch is ')' or ']' or '}' or '>') depth--;
                else if (ch == ',' && depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
            }
            parts.Add(text[start..].Trim());
            return parts;
        }

        /// <summary>from 处（方法名之后）跳过空白与泛型实参 &lt;…&gt; 后的 '(' 位置；不是调用 → -1。</summary>
        internal static int OpenParen(string text, int from)
        {
            var i = from;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] == '<')
            {
                var close = MatchAngle(text, i);
                if (close < 0) return -1;
                i = close + 1;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }
            return i < text.Length && text[i] == '(' ? i : -1;
        }

        private static int MatchAngle(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '<') depth++;
                else if (ch == '>') { depth--; if (depth == 0) return i; }
                else if (ch is ';' or '{' or '}' or '"' or '=' && !(ch == '=' && i > 0 && text[i - 1] == '>')) return -1;
            }
            return -1;
        }

        /// <summary>去掉 // 与 /* */ 注释（字符串里的不动）。</summary>
        public static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '"' || (ch is '$' or '@' && i + 1 < text.Length && text[i + 1] is '"' or '$' or '@'))
                {
                    var end = StringEnd(text, i);
                    if (end < 0) { sb.Append(text[i..]); break; }
                    sb.Append(text, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/') { var nl = text.IndexOf('\n', i); if (nl < 0) break; i = nl - 1; continue; }
                if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*') { var c = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (c < 0) break; i = c + 1; continue; }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>把 call 匹配到的调用（到右括号）整段换成空白，看调用之外还点名了什么。</summary>
        public static string BlankCalls(string text, Regex call)
        {
            var chars = text.ToCharArray();
            foreach (Match m in call.Matches(text))
            {
                var close = ArgParser.MatchClose(text, m.Index + m.Length - 1);
                for (var i = m.Index; i <= (close < 0 ? m.Index + m.Length - 1 : close); i++) if (chars[i] != '\n') chars[i] = ' ';
            }
            return new string(chars);
        }
    }
}
