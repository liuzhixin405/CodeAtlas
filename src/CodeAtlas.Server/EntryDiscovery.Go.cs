using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Go 入口（源码文本 + 符号的 Path / 行范围 / 名字定位，不依赖限定名细节）：
/// net/http 与 gorilla/mux 的 HandleFunc / Handle（方法取 .Methods(...) 或 Go 1.22 的 "GET /x" 模式）、gin / echo 的 GET / POST…（Group 前缀链，
/// 含 <c>users.UsersRegister(v1.Group("/users"))</c> 这种把 group 传进别的函数再注册的写法）、chi 的 Get / Post…（r.Route("/x", func(r chi.Router){…}) 闭包前缀）、
/// gRPC 的 <c>pb.RegisterXxxServer(srv, impl)</c>（impl 类型的导出方法、首参 context.Context，有生成的 XxxServer 接口时取交集）、main 包的 main 函数（hosted）。
/// 路由入口的 File / Line 是注册处（与 Minimal API 一致）；gRPC 与 main 是方法本身的位置。
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(HandleFunc|Handle)\s*\(")]
    private static partial Regex GoNetHttp();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS|Any)\s*\(")]
    private static partial Regex GoUpperVerb();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(Get|Post|Put|Delete|Patch|Head|Options)\s*\(")]
    private static partial Regex GoChiVerb();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*Route\s*\(")]
    private static partial Regex GoChiRoute();
    // v1 := r.Group("/api")   api := r.PathPrefix("/api").Subrouter()
    [GeneratedRegex(@"\b(\w+)\s*(?::=|=)\s*(\w+)\s*\.\s*(Group|PathPrefix)\s*\(")]
    private static partial Regex GoGroupDecl();
    // users.UsersRegister(v1.Group("/users"))
    [GeneratedRegex(@"(\w+)\s*\(\s*(\w+)\s*\.\s*Group\s*\(")]
    private static partial Regex GoGroupPass();
    [GeneratedRegex(@"\b\w+\s*\.\s*Register(\w+)Server\s*\(")]
    private static partial Regex GoGrpcRegister();
    [GeneratedRegex(@"^\s*package\s+main\b")]
    private static partial Regex GoPackageMain();
    [GeneratedRegex(@"\bMethod(Get|Post|Put|Delete|Patch|Head|Options)\b|""(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)""")]
    private static partial Regex GoMethodToken();
    [GeneratedRegex(@"^(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+(/\S*)$")]
    private static partial Regex GoPatternWithMethod();
    [GeneratedRegex(@"^&?\s*(?:\w+\.)?([A-Za-z_]\w*)\s*\{")]
    private static partial Regex GoCompositeLiteral();
    [GeneratedRegex(@"^new\s*\(\s*(?:\w+\.)?(\w+)\s*\)")]
    private static partial Regex GoNewCall();
    [GeneratedRegex(@"^\s*([A-Z]\w*)\s*\(")]
    private static partial Regex GoInterfaceMethod();
    [GeneratedRegex(@"(\w+)\s*\)*\s*$")]
    private static partial Regex GoTrailingIdent();

    private static readonly HashSet<string> ChiReceiverSkip = new(StringComparer.Ordinal) { "http", "client", "resty", "req", "request", "header", "Header", "q", "query", "values" };

    internal static bool IsGo(string path) => path.EndsWith(".go", StringComparison.OrdinalIgnoreCase);

    private static void DiscoverGo(Scan scan)
    {
        var goSymbols = scan.Symbols.Where(s => IsGo(s.Path) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)).ToList();
        if (goSymbols.Count == 0) return;
        var files = goSymbols.Select(s => s.Path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var funcs = goSymbols.Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method).ToList();
        var funcsByFile = funcs.ToLookup(s => s.Path);
        var funcsByName = funcs.ToLookup(s => s.Name, StringComparer.Ordinal);

        MapSymbol? Enclosing(string file, int line) =>
            funcsByFile[file].Where(s => s.StartLine <= line && line <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault();

        // 按名字找处理函数：同目录（同一个包）优先，再同容器
        MapSymbol? FindFunc(string name, string fromFile)
        {
            var candidates = funcsByName[name].ToList();
            if (candidates.Count == 0) return null;
            var dir = MapBuilder.DirectoryOf(fromFile);
            var owners = scan.ContainersOf(fromFile).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            return candidates.OrderBy(s => MapBuilder.DirectoryOf(s.Path) == dir ? 0 : scan.ContainersOf(s.Path).Any(c => owners.Contains(c.Key)) ? 1 : 2)
                .ThenBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine).First();
        }

        // 每个文件的 group 变量：名字 → (接收者, 段)；PathPrefix 只有接 .Subrouter() 才算
        var groupsCache = new Dictionary<string, Dictionary<string, (string Parent, string Segment)>>(StringComparer.Ordinal);
        Dictionary<string, (string Parent, string Segment)> GroupsOf(string file)
        {
            if (groupsCache.TryGetValue(file, out var cached)) return cached;
            var groups = new Dictionary<string, (string Parent, string Segment)>(StringComparer.Ordinal);
            var lines = scan.ReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match g in GoGroupDecl().Matches(lines[i]))
                {
                    if (RuleContext.InCommentOrString(lines[i], g.Index)) continue;
                    var (args, close) = ArgParser.SplitArgs(lines[i], g.Index + g.Length - 1);
                    if (args.Count == 0 || ArgParser.LastLiteral(args[0]) is not { } seg) continue;
                    if (g.Groups[3].Value == "PathPrefix" && (close < 0 || !Regex.IsMatch(lines[i][(close + 1)..], @"^\s*\.\s*Subrouter\s*\("))) continue;
                    groups[g.Groups[1].Value] = (g.Groups[2].Value, seg);
                }
            }
            return groupsCache[file] = groups;
        }

        static (string Prefix, string Root) LocalPrefix(Dictionary<string, (string Parent, string Segment)> groups, string ident)
        {
            var prefix = "";
            for (var depth = 0; depth < 8 && groups.TryGetValue(ident, out var g); depth++) { prefix = JoinRoute(g.Segment, prefix); ident = g.Parent; }
            return (prefix, ident);
        }

        bool IsParam(MapSymbol fn, string ident)
        {
            var decl = DeclarationText(scan.ReadLines(fn.Path), fn);
            return Regex.IsMatch(decl, $@"[(,]\s*{Regex.Escape(ident)}\s+[\w*.\[\]]");
        }

        // 跨函数传 group：被调函数名 → 调用处（测试文件不算）
        var passed = new List<(string Callee, string File, int Line, string Receiver, string Segment)>();
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".Group", StringComparison.Ordinal)) continue;
                foreach (Match m in GoGroupPass().Matches(lines[i]))
                {
                    if (RuleContext.InCommentOrString(lines[i], m.Index)) continue;
                    var (args, _) = ArgParser.SplitArgs(lines[i], m.Index + m.Length - 1);
                    if (args.Count == 0 || ArgParser.LastLiteral(args[0]) is not { } seg) continue;
                    passed.Add((m.Groups[1].Value, file, i + 1, m.Groups[2].Value, seg));
                }
            }
        }
        var prefixMemo = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> FunctionPrefixes(string name, int depth)
        {
            if (prefixMemo.TryGetValue(name, out var memo)) return memo;
            if (depth > 6) return [];
            var result = new List<string>();
            foreach (var p in passed.Where(p => p.Callee == name))
            {
                var (local, root) = LocalPrefix(GroupsOf(p.File), p.Receiver);
                var outer = new List<string> { "" };
                if (Enclosing(p.File, p.Line) is { } enc && enc.Name != name && IsParam(enc, root) && FunctionPrefixes(enc.Name, depth + 1) is { Count: > 0 } fp) outer = fp;
                foreach (var o in outer) result.Add(JoinRoute(o, local, p.Segment));
            }
            return prefixMemo[name] = result.Distinct(StringComparer.Ordinal).ToList();
        }

        // ── HTTP 路由 ──
        foreach (var file in files)
        {
            var owners = scan.ContainersOf(file);
            if (owners.Count == 0) continue;
            var lines = scan.ReadLines(file);
            var dir = MapBuilder.DirectoryOf(file);
            var isEcho = Array.Exists(lines, l => l.Contains("labstack/echo", StringComparison.Ordinal))
                || files.Where(f => MapBuilder.DirectoryOf(f) == dir).Any(f => Array.Exists(scan.ReadLines(f), l => l.Contains("labstack/echo", StringComparison.Ordinal)));
            var groups = GroupsOf(file);
            var scopes = new List<(string Prefix, int Depth)>(); // chi 的 r.Route 闭包
            var braces = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                var depthBefore = braces;
                var chunk = string.Join('\n', lines[i..Math.Min(lines.Length, i + 8)]); // 实参可能跨行

                void Register(Match m, string ruleId, string? verb)
                {
                    if (RuleContext.InCommentOrString(line, m.Index)) return;
                    var receiver = m.Groups[1].Value;
                    var (args, close) = ArgParser.SplitArgs(chunk, m.Index + m.Length - 1);
                    if (args.Count < 1 || ArgParser.LastLiteral(args[0]) is not { } path) return;
                    string? method = verb;
                    if (ruleId == "go.nethttp")
                    {
                        if (GoPatternWithMethod().Match(path) is { Success: true } pm) { method = pm.Groups[1].Value; path = pm.Groups[2].Value; }
                        else if (close >= 0 && Regex.Match(chunk[(close + 1)..], @"^\s*\.\s*Methods\s*\(([^)]*)\)") is { Success: true } mm && GoMethodToken().Match(mm.Groups[1].Value) is { Success: true } tok)
                            method = (tok.Groups[1].Success ? tok.Groups[1].Value : tok.Groups[2].Value).ToUpperInvariant();
                    }
                    else if (ruleId == "go.chi" && (!path.StartsWith('/') || ChiReceiverSkip.Contains(receiver))) return;
                    if (ruleId == "go.nethttp" && !path.StartsWith('/') && path.Length > 0) return;

                    var enc = Enclosing(file, i + 1);
                    if (enc is null) return;
                    var owner = enc;
                    if (args.Count >= 2)
                    {
                        var h = args[^1];
                        if (!h.StartsWith("func", StringComparison.Ordinal) && GoTrailingIdent().Match(h) is { Success: true } id && FindFunc(id.Groups[1].Value, file) is { } handler)
                            owner = handler;
                    }
                    var (local, root) = LocalPrefix(groups, receiver);
                    var outer = new List<string> { "" };
                    if (scopes.Count == 0 && IsParam(enc, root) && FunctionPrefixes(enc.Name, 0) is { Count: > 0 } fp) outer = fp;
                    var scopePrefix = string.Concat(scopes.Select(s => "/" + s.Prefix.Trim('/')));
                    foreach (var o in outer)
                    {
                        var route = JoinRoute(o, scopePrefix, local, path);
                        foreach (var c in owners) scan.Add(new EntryPoint(owner.Id, c.Key, ruleId, "http", route, method, file, i + 1));
                    }
                }

                if (line.Contains("Handle", StringComparison.Ordinal))
                    foreach (Match m in GoNetHttp().Matches(line)) Register(m, "go.nethttp", null);
                foreach (Match m in GoUpperVerb().Matches(line))
                    Register(m, isEcho ? "go.echo" : "go.gin", m.Groups[2].Value == "Any" ? null : m.Groups[2].Value);
                foreach (Match m in GoChiVerb().Matches(line))
                    Register(m, "go.chi", m.Groups[2].Value.ToUpperInvariant());

                braces += BraceDelta(line);
                foreach (Match m in GoChiRoute().Matches(line))
                {
                    if (RuleContext.InCommentOrString(line, m.Index)) continue;
                    var (args, _) = ArgParser.SplitArgs(chunk, m.Index + m.Length - 1);
                    if (args.Count >= 2 && ArgParser.Literal(args[0]) is { } seg && args[1].StartsWith("func", StringComparison.Ordinal) && braces > depthBefore)
                        scopes.Add((seg, depthBefore));
                }
                scopes.RemoveAll(s => braces <= s.Depth);
            }
        }

        // ── gRPC 服务端：pb.RegisterXxxServer(srv, impl) ──
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            if (!Array.Exists(lines, l => l.Contains("Register", StringComparison.Ordinal) && l.Contains("Server", StringComparison.Ordinal))) continue;
            var owners = scan.ContainersOf(file);
            if (owners.Count == 0) continue;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = GoGrpcRegister().Match(lines[i]);
                if (!m.Success || RuleContext.InCommentOrString(lines[i], m.Index) || lines[i].TrimStart().StartsWith("func ", StringComparison.Ordinal)) continue;
                var chunk = string.Join('\n', lines[i..Math.Min(lines.Length, i + 4)]);
                var (args, _) = ArgParser.SplitArgs(chunk, m.Index + m.Length - 1);
                if (args.Count < 2 || GoImplType(args[1], lines, i) is not { } typeName) continue;
                var service = m.Groups[1].Value;
                var dir = MapBuilder.DirectoryOf(file);
                var ownerKeys = owners.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                var methods = funcs.Where(s => s.Kind == SymbolKind.Method && ReceiverOf(s, scan.ById) == typeName && s.Name.Length > 0 && char.IsUpper(s.Name[0])
                                               && scan.ContainersOf(s.Path).Any(c => ownerKeys.Contains(c.Key))).ToList();
                if (methods.Any(s => MapBuilder.DirectoryOf(s.Path) == dir)) methods = methods.Where(s => MapBuilder.DirectoryOf(s.Path) == dir).ToList();
                methods = methods.Where(s => FirstParamIsContext(scan.ReadLines(s.Path), s)).ToList();
                // 生成的 XxxServer 接口在：只要接口里声明的方法（同一类型上顺手实现的 Health Check 之类不算）
                var declared = ServiceInterfaceMethods(scan, service + "Server", ownerKeys);
                if (declared is not null) methods = methods.Where(s => declared.Contains(s.Name)).ToList();
                foreach (var s in methods.OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
                    foreach (var c in owners) scan.Add(new EntryPoint(s.Id, c.Key, "go.grpc", "grpc", service + "/" + s.Name, null, s.Path, s.StartLine));
            }
        }

        DiscoverGoCli(scan, files, FindFunc, Enclosing);

        // ── main 包的 main 函数 ──
        foreach (var main in funcs.Where(s => s.Kind == SymbolKind.Function && s.Name == "main"))
        {
            if (!Array.Exists(scan.ReadLines(main.Path), l => GoPackageMain().IsMatch(l))) continue;
            var dir = MapBuilder.DirectoryOf(main.Path);
            foreach (var c in scan.ContainersOf(main.Path))
                scan.Add(new EntryPoint(main.Id, c.Key, "go.main", "hosted", dir.Length == 0 ? c.Name : MapBuilder.KeyLabel(dir), null, main.Path, main.StartLine));
        }
    }

    /// <summary>Go 方法的接收者类型名：ParentId 指向类型时取类型名，否则从限定名取（接收者类型在别的文件）。</summary>
    private static string? ReceiverOf(MapSymbol s, IReadOnlyDictionary<long, MapSymbol> byId) =>
        s.ParentId is { } p && byId.TryGetValue(p, out var owner) && owner.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface ? owner.Name : FlowBuilder.GoReceiver(s);

    /// <summary>注册实参 → 实现类型名：&amp;T{} / T{} / new(T)，或在注册行之前赋值过的变量（svc := &amp;T{} / svc := new(T)）。外部构造（health.NewServer()）返回 null。</summary>
    private static string? GoImplType(string expr, string[] lines, int line)
    {
        expr = expr.Trim();
        if (GoCompositeLiteral().Match(expr) is { Success: true } lit) return lit.Groups[1].Value;
        if (GoNewCall().Match(expr) is { Success: true } nw) return nw.Groups[1].Value;
        if (!Regex.IsMatch(expr, @"^\w+$")) return null;
        for (var i = line; i >= 0; i--)
        {
            var m = Regex.Match(lines[i], $@"\b(?:var\s+)?{Regex.Escape(expr)}\s*(?::=|=)\s*(.+)$");
            if (m.Success)
            {
                var rhs = m.Groups[1].Value.Trim();
                if (GoCompositeLiteral().Match(rhs) is { Success: true } l2) return l2.Groups[1].Value;
                if (GoNewCall().Match(rhs) is { Success: true } n2) return n2.Groups[1].Value;
                return null;
            }
            var decl = Regex.Match(lines[i], $@"\bvar\s+{Regex.Escape(expr)}\s+\*?(?:\w+\.)?(\w+)\s*$");
            if (decl.Success) return decl.Groups[1].Value;
        }
        return null;
    }

    /// <summary>方法的第一个参数是不是 context.Context（func (s *T) M(ctx context.Context, …) 或未命名的 M(context.Context, …)）。</summary>
    private static bool FirstParamIsContext(string[] lines, MapSymbol m)
    {
        var decl = DeclarationText(lines, m);
        var at = decl.IndexOf(m.Name + "(", StringComparison.Ordinal);
        if (at < 0) return false;
        var rest = decl[(at + m.Name.Length + 1)..];
        var end = rest.IndexOfAny([',', ')']);
        return (end < 0 ? rest : rest[..end]).Contains("context.Context", StringComparison.Ordinal);
    }

    /// <summary>生成代码里的 type XxxServer interface { … } 声明的方法名；找不到接口返回 null。</summary>
    private static HashSet<string>? ServiceInterfaceMethods(Scan scan, string interfaceName, IReadOnlySet<string> containerKeys)
    {
        HashSet<string>? names = null;
        foreach (var iface in scan.Symbols.Where(s => s.Kind == SymbolKind.Interface && IsGo(s.Path) && s.Name == interfaceName))
        {
            if (!scan.ContainersOf(iface.Path).Any(c => containerKeys.Contains(c.Key))) continue;
            var lines = scan.ReadLines(iface.Path);
            names ??= new HashSet<string>(StringComparer.Ordinal);
            for (var i = Math.Max(0, iface.StartLine); i < Math.Min(lines.Length, iface.EndLine); i++)
                if (GoInterfaceMethod().Match(lines[i]) is { Success: true } m) names.Add(m.Groups[1].Value);
        }
        return names;
    }

    /// <summary>一行里 { 与 } 的差（跳过字符串与行注释）。</summary>
    private static int BraceDelta(string line)
    {
        var delta = 0;
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote is not null)
            {
                if (ch == '\\' && quote != '`') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (ch is '"' or '`' or '\'') quote = ch;
            else if (ch == '{') delta++;
            else if (ch == '}') delta--;
        }
        return delta;
    }

    /// <summary>路由段拼接：各段去掉首尾 /，空段跳过，统一以 / 开头、不留尾斜杠（与 Minimal API 一致）。</summary>
    internal static string JoinRoute(params string[] parts) =>
        "/" + string.Join('/', parts.Select(p => p.Trim().Trim('/')).Where(p => p.Length > 0));
}
