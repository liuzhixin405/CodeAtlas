using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

public static partial class SpringFrameworkFlow
{
    /// <summary>Ordered.HIGHEST_PRECEDENCE / LOWEST_PRECEDENCE（Integer.MIN_VALUE / MAX_VALUE）。用 long 免得 HIGHEST_PRECEDENCE - 1 溢出。</summary>
    public const long Highest = int.MinValue;
    public const long Lowest = int.MaxValue;
    /// <summary>Spring Security 过滤器链在 Servlet / WebFlux 过滤器里的默认 order（SecurityProperties.DEFAULT_FILTER_ORDER、WebFilterChainProxy）。</summary>
    public const long SecurityOrder = -100;

    /// <summary>这些外部类型上的调用（字符串、集合、Optional、Reactor 拼装……）不是框架流程里的"关键调用"。</summary>
    private static readonly HashSet<string> NoiseTypes = new(StringComparer.Ordinal)
    {
        "String", "Objects", "Math", "Integer", "Long", "Boolean", "Double", "Float", "Character", "Byte", "Short",
        "List", "Map", "Set", "HashMap", "ArrayList", "LinkedList", "HashSet", "LinkedHashMap", "TreeMap", "ConcurrentHashMap", "Arrays", "Collections",
        "Optional", "Stream", "Collectors", "IntStream", "StringBuilder", "StringBuffer", "Iterator", "Mono", "Flux", "Duration", "Instant",
        "LocalDateTime", "LocalDate", "UUID", "CollectionUtils", "StringUtils", "ObjectUtils", "Assert", "Pattern", "Matcher", "Enum",
    };

    /// <summary>
    /// Stream / Optional / Reactor 的链式方法：tree-sitter 把链上每一环都记成链头接收者的类型（e.getBindingResult().getFieldErrors().stream().map(…)
    /// 记成 MethodArgumentNotValidException.stream / .map），按类型滤不掉，按方法名滤。
    /// </summary>
    private static readonly HashSet<string> NoiseMembers = new(StringComparer.Ordinal)
    {
        "stream", "map", "flatMap", "filter", "collect", "forEach", "ifPresent", "ifPresentOrElse", "orElse", "orElseGet", "orElseThrow", "findFirst", "findAny",
        "anyMatch", "allMatch", "noneMatch", "sorted", "distinct", "limit", "skip", "toList", "toArray", "reduce", "peek", "mapToObj", "mapToInt", "boxed",
        "then", "thenReturn", "doOnNext", "doOnError", "doFinally", "switchIfEmpty", "defaultIfEmpty", "onErrorResume", "subscribe", "block",
    };

    /// <summary>
    /// Java 的关键调用：tree-sitter 给 Java 外部调用的限定名是 external:类型.方法（没有参数表），共用的 <see cref="FrameworkFlowBuilder.Keep"/> 会把它们全滤掉，所以另写一份：
    /// 去掉没解析到的、只有方法名没有类型的（orElseThrow）、外部类型的构造（new SessionLocaleResolver()）、<see cref="NoiseTypes"/> 上的调用、没有构造逻辑的 new。
    /// </summary>
    public static bool JavaKeep(FrameworkCall c) => c.Kind switch
    {
        "unresolved" => false,
        "external" => OwnerAndMember(c.QualifiedName) is var (type, member) && type.Length > 0 && member.Length > 0 && !NoiseTypes.Contains(type) && !NoiseMembers.Contains(member),
        "new" => c.Expandable,
        _ => true,
    };

    private static readonly Regex SuperClause = new(@"\b(?:extends|implements)\s+(.+?)(?=\bimplements\b|\bpermits\b|$)", RegexOptions.Singleline);
    private static readonly Regex AnnotationText = new(@"@[\w.]+(?:\s*\([^)]*\))?");
    private static readonly Regex NewExpr = new(@"^new\s+([\w.]+)\s*(?:<[^()]*>)?\s*\(");
    private static readonly Regex CallExpr = new(@"^(?:this\s*\.\s*)?(\w+)\s*\(\s*\)$");
    private static readonly Regex IdentExpr = new(@"^(?:this\s*\.\s*)?(\w+)$");
    private static readonly Regex StringLiteral = new(@"""((?:[^""\\]|\\.)*)""");

    /// <summary>
    /// 一个 Java 容器的符号、源码文本与注解的索引（框架流程按需查）：容器内（不含测试）的类型与方法、按短名找类型、
    /// 类型的直接父类型（implements / inherits 边 ∪ 源码里 extends / implements 子句）、注解（<see cref="JavaSource.Scan"/>）、签名、方法体。
    /// </summary>
    private sealed class World
    {
        public readonly ArchNode Container;
        public readonly FrameworkSources Src;
        public readonly Dictionary<long, MapSymbol> ById = new();
        public readonly ILookup<long, MapSymbol> ChildrenOf;
        /// <summary>容器里的 Java 类型与方法（扫注解用）。</summary>
        public readonly List<MapSymbol> Declared;
        /// <summary>容器里的 Java 类与接口，按限定名排。</summary>
        public readonly List<MapSymbol> Types;
        public readonly Dictionary<string, MapSymbol> TypeByName;
        /// <summary>容器里的 Java 文件（相对仓库根），按路径排。</summary>
        public readonly List<string> Files;
        private readonly Dictionary<long, List<string>> edgeSupers = new();
        private readonly Dictionary<string, (string Raw, string Code, string[] Lines)?> texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JavaSource.FileAnnotations> annotations = new(StringComparer.Ordinal);

        public World(FrameworkInput input)
        {
            Container = input.Container;
            Src = input.Sources;
            foreach (var s in input.Symbols) ById[s.Id] = s;
            ChildrenOf = input.Symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            Declared = input.Symbols.Where(s => InContainer(s) && (OutlineBuilder.IsType(s.Kind) || s.Kind is SymbolKind.Method or SymbolKind.Function)).ToList();
            Types = Declared.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Interface).OrderBy(s => s.QualifiedName, StringComparer.Ordinal).ToList();
            TypeByName = Types.GroupBy(ShortName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Kind == SymbolKind.Class ? 0 : 1).First(), StringComparer.Ordinal);
            Files = input.Symbols.Where(InContainer).Select(s => s.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            foreach (var e in input.Edges)
                if (e.Kind is EdgeKind.Implements or EdgeKind.Inherits && ById.TryGetValue(e.ToId, out var t))
                {
                    if (!edgeSupers.TryGetValue(e.FromId, out var list)) edgeSupers[e.FromId] = list = [];
                    list.Add(ShortName(t));
                }
        }

        public bool InContainer(MapSymbol s) =>
            s.Kind != SymbolKind.External && JavaSource.IsJava(s.Path) && Container.Members.Any(m => Under(s.Path, m)) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);

        public static string ShortName(MapSymbol s) => MapBuilder.StripGenericArgs(s.Name);

        public string MemberLabel(MapSymbol m) =>
            m.ParentId is { } p && ById.TryGetValue(p, out var o) && OutlineBuilder.IsType(o.Kind) ? ShortName(o) + "." + ShortName(m) : ShortName(m);

        public SymbolDto Dto(MapSymbol s) => Src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);

        public MapSymbol? Owner(MapSymbol m) => m.ParentId is { } p && ById.TryGetValue(p, out var o) && OutlineBuilder.IsType(o.Kind) ? o : null;

        // ---------------- 源码文本 ----------------

        /// <summary>原文、去掉注释与字符串的代码（偏移与行号不变）、按行切的原文（注解扫描用）；读不到为 null。</summary>
        public (string Raw, string Code, string[] Lines)? Text(string path)
        {
            if (texts.TryGetValue(path, out var t)) return t;
            string? raw;
            try { raw = Src.ReadFile(path); } catch { raw = null; }
            return texts[path] = raw is null ? null : (raw, StripCommentsAndStrings(raw), raw.Split('\n'));
        }

        public IReadOnlyList<JavaSource.Annotation> Annotations(MapSymbol s)
        {
            if (!annotations.TryGetValue(s.Path, out var a))
                annotations[s.Path] = a = Text(s.Path) is { } t ? JavaSource.Scan(t.Lines) : JavaSource.FileAnnotations.Empty;
            return a.Of(s.StartLine);
        }

        public JavaSource.Annotation? Annotation(MapSymbol s, params string[] names) => Annotations(s).FirstOrDefault(a => names.Contains(a.Name));

        public static int LineAt(string text, int pos)
        {
            var n = 1;
            for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }

        public static string FileName(string path) => MapSymbol.FileName(path);

        /// <summary>方法体（声明行到结束行）在文件里的 [起，止)。</summary>
        public (int Start, int End)? Span(MapSymbol s) => Text(s.Path) is { } t ? LineSpan(t.Code, s.StartLine, s.EndLine) : null;

        /// <summary>code[pos] 开始的调用名后面那对括号里的原文（字符串保留）：ScanReceiver 给的 Args 是去掉字符串的。</summary>
        public static string RawArgs(string code, string raw, int namePos)
        {
            var open = code.IndexOf('(', namePos);
            if (open < 0) return "";
            var close = MatchBracket(code, open, '(', ')');
            return close < 0 ? "" : raw[(open + 1)..close];
        }

        /// <summary>原文片段里的字符串字面量（"/api/**"）。</summary>
        public static List<string> Literals(string raw) => StringLiteral.Matches(raw).Select(m => m.Groups[1].Value).ToList();

        // ---------------- 类型与继承 ----------------

        /// <summary>直接父类型的短名：implements / inherits 边 ∪ 源码里 extends / implements 子句（tree-sitter 不一定都记边）。</summary>
        public List<string> DirectSupers(MapSymbol type)
        {
            var list = new List<string>(edgeSupers.GetValueOrDefault(type.Id) ?? []);
            if (Text(type.Path) is { } t)
            {
                var header = JavaSource.TypeHeader(t.Lines, type.StartLine);
                foreach (Match m in SuperClause.Matches(header))
                    foreach (var part in SplitTopLevel(m.Groups[1].Value))
                        if (TypeName(part) is { Length: > 0 } n) list.Add(n);
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>所有父类型短名（沿仓库里的父类型往上，最多 4 层）。</summary>
        public HashSet<string> Supers(MapSymbol type, int depth = 0)
        {
            var set = new HashSet<string>(DirectSupers(type), StringComparer.Ordinal);
            if (depth < 4)
                foreach (var n in set.ToList())
                    if (TypeByName.TryGetValue(n, out var b) && b.Id != type.Id) set.UnionWith(Supers(b, depth + 1));
            return set;
        }

        public bool IsAbstract(MapSymbol type) =>
            type.Kind == SymbolKind.Interface || Text(type.Path) is { } t && Regex.IsMatch(JavaSource.TypeHeader(t.Lines, type.StartLine), @"\babstract\b");

        public IEnumerable<MapSymbol> Methods(MapSymbol type) => ChildrenOf[type.Id].Where(c => c.Kind is SymbolKind.Method or SymbolKind.Function).OrderBy(c => c.StartLine);

        /// <summary>类型自己或仓库里的父类（最多 3 层）上第一个叫这些名字之一的方法（模板方法写在抽象基类里）。</summary>
        public MapSymbol? MethodIn(MapSymbol type, params string[] names)
        {
            var visited = new HashSet<long>();
            for (MapSymbol? cur = type; cur is not null && visited.Add(cur.Id) && visited.Count <= 4;)
            {
                var methods = Methods(cur).ToList();
                foreach (var n in names) if (methods.FirstOrDefault(m => ShortName(m) == n) is { } hit) return hit;
                cur = DirectSupers(cur).Select(s => TypeByName.GetValueOrDefault(s)).FirstOrDefault(b => b is not null && b.Kind == SymbolKind.Class);
            }
            return null;
        }

        public static string TypeName(string text)
        {
            var p = AnnotationText.Replace(text, " ").Trim();
            p = MapBuilder.StripGenericArgs(p).Trim();
            if (p.EndsWith("[]", StringComparison.Ordinal)) p = p[..^2];
            return MapBuilder.LastSegment(p.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
        }

        // ---------------- 方法签名 ----------------

        public string? Signature(MapSymbol m) =>
            Src.Signature(m.Id) ?? (Text(m.Path) is { } t ? JavaSource.Signature(t.Lines, m.StartLine, ShortName(m)) : null);

        /// <summary>参数（类型短名，参数名）。</summary>
        public List<(string Type, string Name)> Params(MapSymbol m)
        {
            var result = new List<(string, string)>();
            if (Signature(m) is not { } sig) return result;
            var at = Regex.Match(sig, $@"\b{Regex.Escape(ShortName(m))}\s*\(");
            if (!at.Success) return result;
            var (args, _) = ArgParser.SplitArgs(sig, at.Index + at.Length - 1);
            foreach (var a in args)
            {
                var p = AnnotationText.Replace(a, " ");
                p = Regex.Replace(p, @"\bfinal\b", " ");
                p = MapBuilder.StripGenericArgs(p);
                var words = p.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) continue;
                result.Add((MapBuilder.LastSegment(words[^2].Replace("...", "").Replace("[]", "")), words[^1]));
            }
            return result;
        }

        /// <summary>返回类型短名（public LocaleChangeInterceptor localeChangeInterceptor() → LocaleChangeInterceptor）；认不出为 null。</summary>
        public string? ReturnType(MapSymbol m)
        {
            if (Signature(m) is not { } sig) return null;
            var head = AnnotationText.Replace(sig, " ");
            var at = Regex.Match(head, $@"\b{Regex.Escape(ShortName(m))}\s*\(");
            if (!at.Success) return null;
            var before = MapBuilder.StripGenericArgs(head[..at.Index]).Replace("[]", "").Trim();
            var words = before.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;
            var type = MapBuilder.LastSegment(words[^1]);
            return type is "void" or "public" or "protected" or "private" or "static" or "final" ? null : type;
        }

        /// <summary>
        /// 一段表达式的类型短名：new X(…)（后面紧跟 { 是匿名类）、同文件里的无参方法 x()（取返回类型，@Bean 方法常见）、
        /// 字段 / 参数名 x（在文件里找 "Type x" 的声明）。认不出为 null。
        /// </summary>
        public (string? Type, bool Anonymous) ExprType(string expr, string path)
        {
            var e = expr.Trim();
            if (NewExpr.Match(e) is { Success: true } n)
            {
                var open = e.IndexOf('(', n.Index);
                var close = open < 0 ? -1 : MatchBracket(e, open, '(', ')');
                var anonymous = close > 0 && e[(close + 1)..].TrimStart().StartsWith('{');
                return (MapBuilder.LastSegment(n.Groups[1].Value), anonymous);
            }
            if (CallExpr.Match(e) is { Success: true } c)
            {
                var name = c.Groups[1].Value;
                var method = Declared.FirstOrDefault(s => s.Path == path && s.Kind == SymbolKind.Method && ShortName(s) == name);
                return (method is null ? null : ReturnType(method), false);
            }
            if (IdentExpr.Match(e) is { Success: true } i && Text(path) is { } t)
            {
                var decl = Regex.Match(t.Code, $@"\b([A-Z][\w.]*)\s*(?:<[^;=(){{}}]*>)?\s+{Regex.Escape(i.Groups[1].Value)}\s*[;=,)]");
                return (decl.Success ? MapBuilder.LastSegment(decl.Groups[1].Value) : null, false);
            }
            return (null, false);
        }

        // ---------------- 调用 ----------------

        public List<FrameworkCall> Calls(MapSymbol m) => Ordered(Src.Calls(m.Id)).ToList();

        public List<FlowNodeDto> CallNodes(IEnumerable<FrameworkCall> calls, string path, string actor, string? note = null) =>
            Cap(calls.Where(JavaKeep).Select(c => CallNode(c, path, actor, note)).ToList(), actor);

        /// <summary>
        /// 在"往下传"的那次调用（chain.doFilter / chain.filter / pjp.proceed）处把方法里的调用切成前置 / 后置。
        /// 链参数是签名里类型为 chainTypes 之一的参数；调用的类型是链类型 / 参数名，或没有类型（lambda 参数）都算。
        /// 主调用取第一个不在 if 分支里的（都在分支里取最后一个）；另有分支里的 → Conditional。调用大纲里没有时退到源码文本里找"参数名.方法("。
        /// </summary>
        public ChainSplit SplitAtChain(MapSymbol method, IReadOnlyCollection<string> chainTypes, IReadOnlyCollection<string> members)
        {
            var calls = Calls(method);
            var names = Params(method).Where(p => chainTypes.Contains(p.Type)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            int Score(FrameworkCall c)
            {
                var (type, member) = OwnerAndMember(c.QualifiedName);
                if (!members.Contains(member)) (type, member) = OwnerAndMember(c.Label);
                if (!members.Contains(member)) return 0;
                return chainTypes.Contains(type) || names.Contains(type) ? 2 : type.Length == 0 ? 1 : 0;
            }
            var scored = calls.Select(c => (Call: c, Score: Score(c))).Where(x => x.Score > 0).ToList();
            var best = scored.Count == 0 ? 0 : scored.Max(x => x.Score);
            var chainCalls = scored.Where(x => x.Score == best).Select(x => x.Call).ToList();
            var main = chainCalls.FirstOrDefault(c => c.Ctx != CallContext.Cond) ?? chainCalls.LastOrDefault();
            var conditional = chainCalls.Any(c => c.Ctx == CallContext.Cond);
            int? splitPos = main?.Pos;
            var splitLine = main?.Line ?? 0;
            if (main is null && Text(method.Path) is { } t && names.Count > 0)
            {
                // 调用大纲里没有（没解析到的调用不进大纲）：源码文本里找 chain.doFilter(
                var (s, e) = LineSpan(t.Code, method.StartLine, method.EndLine);
                var rx = new Regex(@"(?<![\w.])(?:" + string.Join("|", names.Select(Regex.Escape)) + @")\s*\.\s*(?:" + string.Join("|", members.Select(Regex.Escape)) + @")\s*\(");
                var hits = rx.Matches(t.Code[s..e]).ToList();
                if (hits.Count > 0)
                {
                    splitPos = s + hits[0].Index;
                    splitLine = LineAt(t.Code, splitPos.Value);
                    conditional = hits.Count > 1;
                }
            }
            var pre = new List<FrameworkCall>();
            var post = new List<FrameworkCall>();
            var hasChain = main is not null || splitPos is not null;
            foreach (var c in calls)
            {
                if (chainCalls.Contains(c) || !JavaKeep(c)) continue;
                var after = hasChain && (c.Pos is { } p && splitPos is { } sp ? p > sp : c.Line > splitLine);
                (after ? post : pre).Add(c);
            }
            return new ChainSplit(pre, post, conditional, hasChain);
        }

        // ---------------- 顺序 ----------------

        /// <summary>
        /// @Order / setOrder / getOrder 的值：整数、Ordered.HIGHEST_PRECEDENCE / LOWEST_PRECEDENCE、Integer.MIN_VALUE / MAX_VALUE、
        /// SecurityProperties.DEFAULT_FILTER_ORDER（-100），以及同文件里 static final int 常量，可带 + / - 整数。求不出为 null。
        /// </summary>
        public long? EvalOrder(string? expr, string? path, int depth = 0)
        {
            if (expr is null || depth > 2) return null;
            var s = Regex.Replace(expr, @"\s+", "");
            if (s.StartsWith("value=", StringComparison.Ordinal)) s = s[6..];
            while (s.StartsWith('(') && s.EndsWith(')')) s = s[1..^1];
            if (!Regex.IsMatch(s, @"^[+-]?[\w.]+(?:[+-][\w.]+)*$")) return null;
            long total = 0;
            foreach (Match m in Regex.Matches(s, @"([+-]?)([\w.]+)"))
            {
                var v = Term(m.Groups[2].Value);
                if (v is null) return null;
                total += m.Groups[1].Value == "-" ? -v.Value : v.Value;
            }
            return total;

            long? Term(string term)
            {
                if (long.TryParse(term.TrimEnd('L', 'l'), out var n)) return n;
                var name = MapBuilder.LastSegment(term);
                switch (name)
                {
                    case "HIGHEST_PRECEDENCE" or "MIN_VALUE": return Highest;
                    case "LOWEST_PRECEDENCE" or "MAX_VALUE": return Lowest;
                    case "DEFAULT_FILTER_ORDER": return SecurityOrder;
                    case "REQUEST_WRAPPER_FILTER_MAX_ORDER": return 0;
                }
                if (path is null || Text(path) is not { } t) return null;
                var decl = Regex.Match(t.Code, $@"\bfinal\s+(?:int|long|Integer|Long)\s+{Regex.Escape(name)}\s*=\s*([^;]+);");
                return decl.Success ? EvalOrder(decl.Groups[1].Value, path, depth + 1) : null;
            }
        }

        /// <summary>类型上声明的顺序：@Order(n)，或实现 Ordered 的 getOrder() 里 return 的值。没有为 null。</summary>
        public (long? Order, string? How) TypeOrder(MapSymbol type)
        {
            if (Annotation(type, "Order", "Priority") is { } a)
            {
                var v = EvalOrder(JavaSource.Keyword(a.Args, "value") ?? a.Args, type.Path);
                if (v is not null) return (v, $"@{a.Name}({OrderText(v.Value)})");
            }
            if (MethodIn(type, "getOrder") is { } g && Span(g) is { } span && Text(g.Path) is { } t)
            {
                var ret = Regex.Match(t.Code[span.Start..span.End], @"\breturn\s+([^;]+);");
                if (ret.Success && EvalOrder(ret.Groups[1].Value, g.Path) is { } v) return (v, $"getOrder() = {OrderText(v)}");
            }
            return (null, null);
        }
    }

    internal sealed record ChainSplit(List<FrameworkCall> Pre, List<FrameworkCall> Post, bool Conditional, bool HasChain);

    /// <summary>顺序值的显示：HIGHEST_PRECEDENCE + 10、LOWEST_PRECEDENCE、-100。</summary>
    public static string OrderText(long v) => v switch
    {
        Highest => "HIGHEST_PRECEDENCE",
        Lowest => "LOWEST_PRECEDENCE",
        < Highest + 100000 => $"HIGHEST_PRECEDENCE + {v - Highest}",
        > Lowest - 100000 => $"LOWEST_PRECEDENCE - {Lowest - v}",
        _ => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
