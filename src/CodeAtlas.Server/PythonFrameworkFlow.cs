using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Python（python-service）：由 <see cref="FrameworkFlowBuilder.Build"/> 按容器 SubKind 分派进来。认三种框架（源码文本 + 调用大纲）：
/// <list type="number">
/// <item>FastAPI / Starlette（<c>app = FastAPI(...)</c>）：中间件 = <c>add_middleware</c> 与 <c>@app.middleware("http")</c>，按注册倒序（后加的在最外层先执行），
/// 构造参数 <c>middleware=[Middleware(X)]</c> 在它们里面；自研中间件在 <c>call_next</c> / <c>self.app(...)</c> 处拆前后，后置进收尾（洋葱倒序）。
/// 依赖 = 路由参数里的 <c>Depends(...)</c>（别名 <c>X = Annotated[T, Depends(f)]</c>、内联 Annotated、默认值 <c>= Depends(f)</c>）、
/// 装饰器 <c>dependencies=[...]</c>、<c>APIRouter(dependencies=)</c>、<c>include_router(..., dependencies=)</c>、<c>FastAPI(dependencies=)</c>；
/// 按被多少路由用到排序（作用于全部路由的在最前），依赖函数自己的依赖在它下面、再是它的调用；<c>yield</c> 依赖 yield 之后的部分进收尾。
/// <c>exception_handler</c> → 收尾"出错时"。见 PythonFrameworkFlow.FastApi.cs。</item>
/// <item>Django（含 DRF）：settings 的 <c>MIDDLEWARE</c> 列表顺序；仓库里的中间件拆 <c>get_response</c> 前后，或 MiddlewareMixin 的 process_request / process_view（前）
/// 与 process_response / process_exception（收尾，倒序）；DRF 的 <c>REST_FRAMEWORK</c>：认证 / 权限 / 限流类（视图 initial() 里）、
/// 渲染器与 <c>EXCEPTION_HANDLER</c>（收尾）。见 PythonFrameworkFlow.Django.cs。</item>
/// <item>Flask（<c>app = Flask(...)</c>）：<c>app.wsgi_app = X(app.wsgi_app)</c> 中间件、before_request（应用级在前、蓝图在后）、
/// errorhandler / register_error_handler、after_request 与 teardown_request（蓝图在前、各自倒序）。见 PythonFrameworkFlow.Flask.cs。</item>
/// </list>
/// 注释（#）与三引号字符串先换成空格再扫（偏移与行号不变）；普通字符串保留（MIDDLEWARE 列表、@app.middleware("http") 要读它）。
/// 入口占位 Kind = "controllers"（页面点它列出容器的 HTTP 入口），Handlers 清单恒为空。
/// </summary>
public static partial class PythonFrameworkFlow
{
    public static FrameworkFlowDto Build(FrameworkInput input)
    {
        var x = new Py(input);
        var fastApi = FindFastApiApps(x);
        var django = FindDjangoSettings(x);
        var flask = FindFlaskApps(x);

        // 一个容器里认出多种时：按各自规则认出的 HTTP 入口数挑（同数按 FastAPI → Django → Flask）
        int Rule(params string[] ids) => x.Http.Count(e => ids.Contains(e.RuleId));
        var candidates = new List<(string Name, int Score)>();
        if (fastApi.Count > 0) candidates.Add(("FastAPI", Rule("python.fastapi")));
        if (django is not null) candidates.Add(("Django", Rule("python.django", "python.drf")));
        if (flask.Count > 0) candidates.Add(("Flask", Rule("python.flask")));
        // 纯 gRPC 服务（grpc.server + add_XxxServicer_to_server）：画 gRPC 这条链（拦截器 → 服务方法），与别的语言一致
        if (candidates.Count == 0) return FrameworkFlowBuilder.GrpcOnly(input, "python", []) ?? NonWeb(x);
        var pick = candidates.OrderByDescending(c => c.Score).First().Name;
        var others = candidates.Where(c => c.Name != pick).Select(c => c.Name).ToList();
        var notes = new List<string>();
        var flow = pick switch
        {
            "FastAPI" => BuildFastApi(x, fastApi, notes),
            "Django" => BuildDjango(x, django!, notes),
            _ => BuildFlask(x, flask, notes),
        };
        if (others.Count > 0) notes.Add($"这个容器里还认出了 {string.Join("、", others)}，按 HTTP 入口多的 {pick} 画");
        var nonHttp = x.Entries.Count - x.Http.Count;
        if (nonHttp > 0) notes.Add($"另有 {nonHttp} 个非 HTTP 入口（任务 / 命令行 / 常驻进程），没画在这条请求链上");
        return flow;
    }

    /// <summary>没认出 Web 框架：有入口就画一个"入口"占位，否则只有根。</summary>
    private static FrameworkFlowDto NonWeb(Py x)
    {
        var notes = new List<string> { "没认出 Python Web 框架（FastAPI 的 app = FastAPI(...)、Django settings 的 MIDDLEWARE、Flask 的 app = Flask(...)）" };
        if (x.Entries.Count == 0) { notes.Add("没有发现入口"); return Empty(x.Container, notes); }
        notes.Add("按非 Web 容器画：入口是任务 / 命令行 / 常驻进程");
        var entry = Node(null, "入口（任务 / 命令行 / 常驻进程）", "", "controllers", StageController, null, [], null, null, x.Entries.Count);
        var root = Node(null, "入口", "", "entry", StageController, null, [entry], null, null);
        var stages = new List<FrameworkStageDto> { new(StageController, "入口", true, x.EntrySymbols(x.Entries)) };
        return Assemble(x.Container, "入口", root, stages, [], notes);
    }

    // ================= 共用：容器、符号、调用 =================

    /// <summary>一次框架流程计算的上下文：容器里的 Python 文件、符号索引、去注释后的源码缓存、调用展开。</summary>
    private sealed class Py
    {
        public readonly ArchNode Container;
        public readonly FrameworkSources Src;
        public readonly Dictionary<long, MapSymbol> ById = [];
        public readonly ILookup<long, MapSymbol> ChildrenOf;
        /// <summary>容器里的 .py 文件（不含测试），浅的在前、再按路径。</summary>
        public readonly List<string> Files;
        public readonly List<EntryPoint> Entries;
        public readonly List<EntryPoint> Http;
        private readonly Dictionary<string, List<MapSymbol>> callablesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MapSymbol>> symbolsByFile = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> texts = new(StringComparer.Ordinal);
        private readonly Dictionary<long, List<FrameworkCall>> kept = [];

        public Py(FrameworkInput input)
        {
            Container = input.Container;
            Src = input.Sources;
            foreach (var s in input.Symbols) ById[s.Id] = s;
            ChildrenOf = input.Symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            foreach (var s in input.Symbols)
            {
                if (s.Kind == SymbolKind.External || !IsPy(s.Path) || !InContainer(s)) continue;
                if (!symbolsByFile.TryGetValue(s.Path, out var list)) symbolsByFile[s.Path] = list = [];
                list.Add(s);
                if (s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Method)
                {
                    if (!callablesByName.TryGetValue(s.Name, out var named)) callablesByName[s.Name] = named = [];
                    named.Add(s);
                }
            }
            Files = symbolsByFile.Keys.OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).ToList();
            Entries = input.Entries.Where(e => e.ContainerKey == Container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
            Http = Entries.Where(e => e.Kind == "http").OrderBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ToList();
        }

        public bool InContainer(MapSymbol s) => s.Kind != SymbolKind.External && Container.Members.Any(m => Under(s.Path, m)) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);

        /// <summary>去掉 # 注释与三引号字符串后的文件文本（偏移、行号不变）；读不到为 null。</summary>
        public string? Text(string path)
        {
            if (texts.TryGetValue(path, out var t)) return t;
            string? raw;
            try { raw = Src.ReadFile(path); } catch { raw = null; }
            return texts[path] = raw is null ? null : StripPython(raw);
        }

        public IEnumerable<(string Path, string Text)> Texts(Func<string, bool> quick)
        {
            foreach (var f in Files)
                if (Text(f) is { } t && quick(t)) yield return (f, t);
        }

        public static string FileName(string path) => MapSymbol.FileName(path);
        public static string Where(string path, string text, int pos) => $"{FileName(path)}:{LineAt(text, pos)}";

        /// <summary>方法写成"类.方法"，函数就是函数名。</summary>
        public string Label(MapSymbol s) =>
            s.Kind == SymbolKind.Method && s.ParentId is { } p && ById.TryGetValue(p, out var o) && OutlineBuilder.IsType(o.Kind) ? o.Name + "." + s.Name : s.Name;

        public SymbolDto Dto(MapSymbol s) => Src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);

        public List<SymbolDto> Dtos(IEnumerable<MapSymbol> symbols) => symbols.DistinctBy(s => s.Id).Select(Dto).ToList();

        public List<SymbolDto> EntrySymbols(IEnumerable<EntryPoint> entries) =>
            Dtos(entries.Select(e => ById.GetValueOrDefault(e.SymbolId)).Where(s => s is not null)!);

        public MapSymbol? Method(MapSymbol cls, params string[] names)
        {
            var methods = ChildrenOf[cls.Id].Where(c => c.Kind is SymbolKind.Method or SymbolKind.Function).ToList();
            foreach (var n in names) if (methods.FirstOrDefault(m => m.Name == n) is { } hit) return hit;
            return null;
        }

        /// <summary>容器里叫这个名字的函数 / 类（不含方法）：同文件优先，再按路径。</summary>
        public MapSymbol? Callable(string name, string? preferFile)
        {
            name = LastSegment(name);
            if (!callablesByName.TryGetValue(name, out var list)) return null;
            var c = list.Where(s => s.Kind != SymbolKind.Method && !(s.ParentId is { } p && ById.TryGetValue(p, out var o) && o.Kind is SymbolKind.Function or SymbolKind.Method)).ToList();
            return c.FirstOrDefault(s => s.Path == preferFile)
                   ?? c.OrderBy(s => s.Path.Count(ch => ch == '/')).ThenBy(s => s.Path, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>文件里 afterLine 行（含）之后最近的叫这个名字的函数 / 方法（装饰器挂到下一个 def 上）。</summary>
        public MapSymbol? DefinedAt(string path, string name, int afterLine) =>
            symbolsByFile.GetValueOrDefault(path)?.Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method && s.Name == name && s.StartLine >= afterLine)
                .OrderBy(s => s.StartLine).FirstOrDefault()
            ?? Callable(name, path);

        /// <summary>点分路径（settings 里的 'acme.middleware.TimingMiddleware'）→ 仓库里的类 / 函数；ModuleFound = 模块在仓库里（但可能没这个名字）。</summary>
        public (MapSymbol? Symbol, bool ModuleFound) ResolveDotted(string dotted)
        {
            var dot = dotted.LastIndexOf('.');
            if (dot <= 0) return (Callable(dotted, null), false);
            var module = dotted[..dot];
            var name = dotted[(dot + 1)..];
            var files = Files.Where(f => ModuleName(f) == module || ModuleName(f).EndsWith("." + module, StringComparison.Ordinal)).ToList();
            foreach (var f in files)
                if (symbolsByFile.GetValueOrDefault(f)?.Where(s => s.Name == name && s.Kind is SymbolKind.Class or SymbolKind.Function)
                        .OrderBy(s => s.ParentId is { } p && ById.TryGetValue(p, out var o) && o.Kind != SymbolKind.Module ? 1 : 0).FirstOrDefault() is { } hit)
                    return (hit, true);
            return (null, files.Count > 0);
        }

        /// <summary>函数里的关键调用（源码顺序，<see cref="KeepPy"/> 过滤）。</summary>
        public List<FrameworkCall> KeptCalls(long id)
        {
            if (kept.TryGetValue(id, out var k)) return k;
            return kept[id] = Ordered(Src.Calls(id)).Where(KeepPy).ToList();
        }

        /// <summary>一个函数画成一行：子节点是它的关键调用（仓库里的函数再进一层）。</summary>
        public FlowNodeDto FunctionNode(MapSymbol fn, string actor, string? note, string? path = null, int? line = null, List<FlowNodeDto>? prefix = null) =>
            Node(fn.Id, Label(fn), fn.QualifiedName, "call", actor, note, Cap([.. prefix ?? [], .. Expand(KeptCalls(fn.Id), fn, actor, null)], actor), path ?? fn.Path, line ?? fn.StartLine);

        /// <summary>调用画成行；调到仓库里的函数 / 方法再列它一层调用（不递归到自己）。</summary>
        public List<FlowNodeDto> Expand(IEnumerable<FrameworkCall> calls, MapSymbol owner, string actor, string? note, int depth = 0)
        {
            var nodes = new List<FlowNodeDto>();
            foreach (var c in calls)
            {
                List<FlowNodeDto>? inner = null;
                if (depth < 1 && c.Kind == "call" && c.Children.Count == 0 && c.TargetId is { } tid && tid != owner.Id
                    && ById.TryGetValue(tid, out var t) && t.Kind is SymbolKind.Function or SymbolKind.Method && InContainer(t))
                    inner = Expand(KeptCalls(t.Id), t, actor, null, depth + 1);
                nodes.Add(PyCallNode(c, owner.Path, actor, note, inner));
            }
            return Cap(nodes, actor);
        }

        /// <summary>
        /// 在"下一层"调用处把函数拆成前后两段：call_next(request) / self.get_response(request) / self.app(scope…) / yield。
        /// 先在调用大纲里按名字找（主调用取第一个不在 if 里的），找不到再在函数源码里按 textPivot 找行号（同一行算前置）。
        /// </summary>
        public PySplit SplitAt(MapSymbol fn, IReadOnlyCollection<string> nextNames, Regex? textPivot)
        {
            var all = Ordered(Src.Calls(fn.Id)).ToList();
            var nexts = all.Where(c => nextNames.Contains(LastSegment(c.Label)) || nextNames.Contains(LastSegment(c.QualifiedName))).ToList();
            var main = nexts.FirstOrDefault(c => c.Ctx != CallContext.Cond) ?? nexts.LastOrDefault();
            var conditional = nexts.Any(c => c.Ctx == CallContext.Cond && !ReferenceEquals(c, main));
            var pivotLine = main?.Line ?? -1;
            if (main is null && textPivot is not null && Text(fn.Path) is { } text)
            {
                var (s, e) = LineSpan(text, fn.StartLine, fn.EndLine);
                var m = textPivot.Match(text[s..e]);
                if (m.Success) pivotLine = LineAt(text, s + m.Index);
            }
            var pre = new List<FrameworkCall>();
            var post = new List<FrameworkCall>();
            foreach (var c in all)
            {
                if (nexts.Contains(c) || !KeepPy(c)) continue;
                var after = pivotLine >= 0 && (main is not null
                    ? c.Line > main.Line || c.Line == main.Line && (c.Pos ?? 0) > (main.Pos ?? 0)
                    : c.Line > pivotLine);
                (after ? post : pre).Add(c);
            }
            return new PySplit(pre, post, pivotLine >= 0, conditional, pivotLine);
        }
    }

    private sealed record PySplit(List<FrameworkCall> Pre, List<FrameworkCall> Post, bool Found, bool Conditional, int PivotLine);

    /// <summary>与 <see cref="FrameworkFlowBuilder.CallNode"/> 同样的行，只是子节点用 <see cref="KeepPy"/> 过滤，并可接上展开的下一层。</summary>
    private static FlowNodeDto PyCallNode(FrameworkCall c, string? path, string actor, string? note, List<FlowNodeDto>? inner)
    {
        var kind = c.Kind switch { "external" or "unresolved" => "external-group", "dispatch" => "dispatch", _ => "call" };
        var ctx = CtxNote(c.Ctx);
        var fullNote = note is null ? ctx : ctx is null ? note : note + " · " + ctx;
        var kids = c.Children.Where(KeepPy).Select(k => PyCallNode(k, path, actor, null, null)).ToList();
        if (inner is not null) kids.AddRange(inner);
        return new FlowNodeDto(kind == "external-group" ? null : c.TargetId, c.Label, c.QualifiedName, kind, actor, c.Kind == "impl" ? "impl" : null, 1.0, 1, 0,
            Cap(kids, actor), 0, fullNote, path, c.Line > 0 ? c.Line : null, c.Pos);
    }

    /// <summary>这些标准库模块里的调用不算关键调用。</summary>
    private static readonly string[] NoiseModules =
        ["typing.", "collections.", "itertools.", "functools.", "dataclasses.", "enum.", "abc.", "copy.", "math.", "re.", "string.", "operator.", "builtins."];

    /// <summary>
    /// Python 的关键调用：去掉没解析到的；外部调用只留带模块的（jwt.decode、sqlmodel.Session）——
    /// 不带点的是内置函数或接收者不明的方法（len、str、split、lower），标准库的工具模块也不要。构造（new）都留：Python 里实例化常有校验逻辑（pydantic）。
    /// </summary>
    public static bool KeepPy(FrameworkCall c)
    {
        if (c.Kind == "unresolved") return false;
        if (c.Kind != "external") return true;
        var q = c.QualifiedName.StartsWith("external:", StringComparison.Ordinal) ? c.QualifiedName["external:".Length..] : c.QualifiedName;
        var paren = q.IndexOf('(');
        if (paren >= 0) q = q[..paren];
        return q.Contains('.') && !NoiseModules.Any(p => q.StartsWith(p, StringComparison.Ordinal));
    }

    // ================= 共用：Python 源码文本 =================

    private static bool IsPy(string path) => path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>a/b/c.py → a.b.c；a/b/__init__.py → a.b。</summary>
    internal static string ModuleName(string path)
    {
        var m = path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        if (m.EndsWith("/__init__", StringComparison.Ordinal)) m = m[..^"/__init__".Length];
        return m.Replace('/', '.');
    }

    /// <summary>"a.b.c" / "self.app" / "external:jwt.decode" → 最后一段（去掉参数列表）。</summary>
    internal static string LastSegment(string name)
    {
        var q = name.StartsWith("external:", StringComparison.Ordinal) ? name["external:".Length..] : name;
        var paren = q.IndexOf('(');
        if (paren >= 0) q = q[..paren];
        var dot = q.LastIndexOf('.');
        return dot >= 0 ? q[(dot + 1)..] : q;
    }

    internal static int LineAt(string text, int pos)
    {
        var n = 1;
        for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') n++;
        return n;
    }

    /// <summary>
    /// # 注释与三引号字符串（docstring）换成空格（换行保留，偏移与行号不变）；普通单行字符串原样保留，但其中的 # 不当注释。
    /// </summary>
    public static string StripPython(string text)
    {
        var chars = text.ToCharArray();
        void Blank(int from, int to) { for (var k = from; k < to && k < chars.Length; k++) if (chars[k] is not ('\n' or '\r')) chars[k] = ' '; }
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '#')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(i, e); i = e; continue;
            }
            if (ch is '"' or '\'')
            {
                if (i + 2 < text.Length && text[i + 1] == ch && text[i + 2] == ch)
                {
                    var close = i + 3;
                    while (close < text.Length && !(text[close] == ch && close + 2 < text.Length && text[close + 1] == ch && text[close + 2] == ch))
                        close += text[close] == '\\' ? 2 : 1;
                    var e = Math.Min(text.Length, close + 3);
                    Blank(i, e); i = e; continue;
                }
                i = SkipString(text, i); continue;
            }
            i++;
        }
        return new string(chars);
    }

    /// <summary>text[i] 是单行字符串的开引号，返回闭引号之后的位置。</summary>
    private static int SkipString(string text, int i)
    {
        var q = text[i];
        var j = i + 1;
        while (j < text.Length && text[j] != q && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
        return Math.Min(text.Length, j + 1);
    }

    /// <summary>text[open] 是 ( [ {，返回配对的闭括号位置（字符串里的括号不算）；没配上返回 -1。</summary>
    internal static int MatchPy(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '"' or '\'') { i = SkipString(text, i) - 1; continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>按顶层逗号切（括号、字符串里的逗号不切），返回（片段，片段在 s 里的起点）。</summary>
    internal static List<(string Part, int Start)> SplitPy(string s)
    {
        var parts = new List<(string, int)>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is '"' or '\'') { i = SkipString(s, i) - 1; continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == ',' && depth == 0) { parts.Add((s[start..i], start)); start = i + 1; }
        }
        if (s[start..].Trim().Length > 0) parts.Add((s[start..], start));
        return parts;
    }

    /// <summary>顶层第一个单独的 =（不是 == / &lt;= / >= / !=）的位置；没有为 -1。</summary>
    internal static int TopLevelAssign(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch is '"' or '\'') { i = SkipString(s, i) - 1; continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == '=' && depth == 0 && (i + 1 >= s.Length || s[i + 1] != '=') && (i == 0 || s[i - 1] is not ('=' or '<' or '>' or '!' or ':'))) return i;
        }
        return -1;
    }

    /// <summary>参数 / 实参列表里 name=值 的值（顶层）及它在 args 里的起点；没有为 null。</summary>
    internal static (string Value, int Start)? Kwarg(string args, string name)
    {
        foreach (var (part, start) in SplitPy(args))
        {
            var eq = TopLevelAssign(part);
            if (eq > 0 && part[..eq].Trim() == name) return (part[(eq + 1)..], start + eq + 1);
        }
        return null;
    }

    /// <summary>顶层第一个位置实参（不是 name=值 的）。</summary>
    internal static string? FirstPositional(string args)
    {
        var parts = SplitPy(args);
        if (parts.Count == 0) return null;
        var p = parts[0].Part;
        var eq = TopLevelAssign(p);
        return eq > 0 && Regex.IsMatch(p[..eq].Trim(), @"^\w+$") ? null : p.Trim();
    }

    [GeneratedRegex(@"(?<q>['""])(?<v>(?:\\.|(?!\k<q>)[^\\\n])*)\k<q>")]
    private static partial Regex StringLiteral();

    /// <summary>s 里的字符串字面量（值，起点）。</summary>
    internal static List<(string Value, int Pos)> StringLiterals(string s) =>
        StringLiteral().Matches(s).Select(m => (m.Groups["v"].Value, m.Index)).ToList();

    /// <summary>
    /// def 行（1 起）上面紧挨着的装饰器块（@ 开头、与 def 同缩进，及其续行）的 [起，止) 偏移；没有装饰器返回 null。
    /// </summary>
    internal static (int Start, int End)? DecoratorBlock(string text, int defLine)
    {
        var lines = text.Split('\n');
        if (defLine < 2 || defLine > lines.Length) return null;
        static int Indent(string l) { var n = 0; while (n < l.Length && l[n] is ' ' or '\t') n++; return n; }
        var defIndent = Indent(lines[defLine - 1]);
        var top = -1;
        for (var i = defLine - 2; i >= 0; i--)
        {
            var l = lines[i].TrimEnd('\r');
            if (l.Trim().Length == 0) break;
            var ind = Indent(l);
            var trimmed = l.TrimStart();
            if (ind == defIndent && trimmed.StartsWith('@')) { top = i; continue; }
            if (ind > defIndent || ind == defIndent && trimmed[0] is ')' or ']') continue;
            break;
        }
        if (top < 0) return null;
        var (s, _) = LineSpan(text, top + 1, top + 1);
        var (e, _) = LineSpan(text, defLine, defLine);
        return (s, e);
    }

    [GeneratedRegex(@"^(?:async\s+)?def\s+(\w+)")]
    private static partial Regex DefLine();

    /// <summary>装饰器（闭括号在 start 前）下面紧跟着的 def / async def 的名字与行（中间可隔空行、别的单行装饰器）；不是紧跟着的为 null。</summary>
    internal static (string Name, int Line)? DefAfter(string text, int start)
    {
        var lines = text.Split('\n');
        var from = LineAt(text, start); // 装饰器所在行（1 起）= 下一行的 0 起下标
        for (var i = from; i < lines.Length && i < from + 30; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('@') || l.StartsWith(')')) continue;
            var m = DefLine().Match(l);
            return m.Success ? (m.Groups[1].Value, i + 1) : null;
        }
        return null;
    }
}
