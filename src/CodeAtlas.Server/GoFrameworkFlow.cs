using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Go（go-service）：gin / echo / chi / gorilla mux / net/http。由 <see cref="FrameworkFlowBuilder.Build"/> 按容器 SubKind 分派进来。
/// 中间件注册读源码文本（剥过注释与字符串，含反引号原始字符串），中间件函数本体的调用用 <see cref="FrameworkSources.Calls"/>。测试文件（*_test.go）不算。
/// <list type="number">
/// <item>路由器：r := gin.Default() / gin.New() / echo.New() / chi.NewRouter() / chi.NewMux() / mux.NewRouter() / http.NewServeMux()（包名按 import 表认，别名也行）。
/// 扫描范围是创建它的那个函数；有好几个路由器时取认出东西最多的那个，别的写进 Notes。一个都没有但有 HTTP 入口：按 DefaultServeMux 找 ListenAndServe 的包装。</item>
/// <item>中间件（阶段 middleware，列名"中间件"）：执行顺序 = 手工包装的 http.Handler（最后包的最先执行）→ echo 的 e.Pre → gin.Default() 内置的 Logger、Recovery → r.Use 的书写顺序。
/// 手工包装：handler = &amp;logHandler{next: handler}、handler = ensureSessionID(handler)、http.ListenAndServe(addr, a(b(r)))、Server{Handler: …}；
/// 实参是仓库里的函数 / 方法 → 挂它的符号，子节点是 next 之前的调用（前置），之后的（含 defer 里的）进收尾（Note "Xxx 后置"，洋葱倒序）；不在仓库里的画成 external-group 行。</item>
/// <item>路由组中间件（阶段 group，列名"路由组中间件"）：g := r.Group("/api", mw…) 再 g.Use(…)（gin / echo）、r.Route("/api", func(r chi.Router) { r.Use(…) }) 与 r.Group(func…)（chi）、
/// s := r.PathPrefix("/api").Subrouter(); s.Use(…)（gorilla）。把路由器 / 组传给仓库里的注册函数（users.Register(v1.Group("/users"))）就进去接着找（3 层内）。
/// 每行 Note 写清只作用于哪个前缀；gin 的 Use 只对之后注册的路由生效，之前建的组在 Note 里点名。</item>
/// <item>next：gin 的 c.Next()、net/http 的 next.ServeHTTP / h.next.ServeHTTP、echo / chi 风格的 next(c)（签名里类型以 Handler / HandlerFunc / Handle 结尾的参数）。
/// 中间件函数返回闭包（return func(c *gin.Context) / return http.HandlerFunc(func…)）时只看闭包里的调用，闭包之外的是构造时执行一次的，不画。
/// gin 不调 c.Next() 也会接着往下走：调用全算前置；c.Abort* 标"可能中断"；net/http / echo 在 next 之前有 return 标"可能中断"。</item>
/// <item>路由（阶段 controller，列名"路由"）：占位 Kind = "controllers"，Count = 容器的 HTTP 入口数；NoRoute / NoMethod / NotFound / NotFoundHandler 挂在它下面。</item>
/// <item>收尾（阶段 tail）：各中间件 next 之后与 defer 里的调用（按洋葱倒序）、gin.Recovery / echo Recover / chi Recoverer 的 panic 恢复、Logger 的访问日志、echo 的 e.HTTPErrorHandler。</item>
/// </list>
/// 路由级中间件（r.GET(path, mw, handler)、chi 的 r.With(mw)）不画。
/// </summary>
public static partial class GoFrameworkFlow
{
    /// <summary>路由组中间件的阶段键。</summary>
    public const string StageGroup = "group";

    /// <summary>每层最多进几层注册函数（users.Register(v1.Group(...)) 里再调别的）。</summary>
    private const int MaxRegisterDepth = 3;

    private enum Fw { Gin, Echo, Chi, Gorilla, NetHttp }

    private static string FwName(Fw f) => f switch { Fw.Gin => "gin", Fw.Echo => "echo", Fw.Chi => "chi", Fw.Gorilla => "gorilla/mux", _ => "net/http" };

    /// <summary>源码里的一个路由器 / 路由组变量：变量名、框架、是不是根、前缀。</summary>
    private sealed record Router(string Var, Fw Fw, bool Root, string Prefix);

    /// <summary>一个认出的中间件：时序图里的一行、它的后置（收尾列）、阶段里登记的符号。</summary>
    private sealed record Mw(FlowNodeDto Node, List<FlowNodeDto> Post, MapSymbol? Symbol);

    /// <summary>一个路由器扫出来的东西。</summary>
    private sealed class RouterScan(Fw fw, string var, string path, int line, string ctor)
    {
        public Fw Fw { get; } = fw;
        public string Var { get; } = var;
        public string Path { get; } = path;
        public int Line { get; } = line;
        public string Ctor { get; } = ctor;
        public List<Mw> Wraps { get; } = [];
        public List<Mw> Pre { get; } = [];
        public List<Mw> Global { get; } = [];
        public List<Mw> Groups { get; } = [];
        public List<FlowNodeDto> RouteChildren { get; } = [];
        public List<FlowNodeDto> ErrorTail { get; } = [];
        public SortedSet<string> UseFiles { get; } = new(StringComparer.Ordinal);
        public List<string> WrapSites { get; } = [];
        public List<string> GroupPrefixes { get; } = [];
        public int Routes { get; set; }
        public int Score => Wraps.Count + Pre.Count + Global.Count + Groups.Count + RouteChildren.Count + ErrorTail.Count + Routes;
    }

    public static FrameworkFlowDto Build(FrameworkInput input) => new Scanner(input).Run();

    // ---- 正则 ----

    // r := gin.Default() / e := echo.New() / r := chi.NewRouter() / r := mux.NewRouter() / mux := http.NewServeMux()（包名再按 import 表核对）
    [GeneratedRegex(@"(?<![\w.])(?<v>[A-Za-z_][\w.]*)\s*(?::=|=)\s*(?<pkg>[A-Za-z_]\w*)\s*\.\s*(?<fn>Default|New|NewRouter|NewMux|NewServeMux)\s*\(\s*\)")]
    private static partial Regex Ctor();

    // 任意调用点：callee 是 a / a.b / a.b.c
    [GeneratedRegex(@"(?<![\w.])(?<callee>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(")]
    private static partial Regex CallSite();

    // e.HTTPErrorHandler = … / r.NotFoundHandler = …
    [GeneratedRegex(@"(?<![\w.])(?<recv>[A-Za-z_][\w.]*)\.(?<p>HTTPErrorHandler|NotFoundHandler|MethodNotAllowedHandler)\s*=(?!=)\s*")]
    private static partial Regex PropAssign();

    [GeneratedRegex(@"(?<lhs>[A-Za-z_][\w.]*)\s*(?::=|=)\s*$")]
    private static partial Regex LhsBefore();

    // 手工包装：handler = X(handler) / var handler http.Handler = r
    [GeneratedRegex(@"(?m)^[ \t]*(?:var[ \t]+)?(?<lhs>[A-Za-z_][\w.]*)(?:[ \t]+[\w.*\[\]]+)?[ \t]*(?::=|=)(?!=)[ \t]*")]
    private static partial Regex Assign();

    [GeneratedRegex(@"(?<![\w.])(?:[A-Za-z_]\w*\.)?(?<fn>ListenAndServe|ListenAndServeTLS)\s*\(")]
    private static partial Regex Serve();

    [GeneratedRegex(@"(?<![\w.])Handler\s*:\s*")]
    private static partial Regex HandlerField();

    // return func(…) / return http.HandlerFunc(func(…) / return gin.HandlerFunc(func(…)
    [GeneratedRegex(@"\breturn\s+(?:[A-Za-z_][\w.]*\s*\(\s*)?(?<f>func)\s*\(")]
    private static partial Regex ReturnFunc();

    [GeneratedRegex(@"(?<![\w.])defer\b")]
    private static partial Regex Defer();

    [GeneratedRegex(@"(?<![\w.])func\s*\(")]
    private static partial Regex FuncLiteral();

    [GeneratedRegex(@"(?<![\w.])return\b")]
    private static partial Regex ReturnKeyword();

    [GeneratedRegex(@"\.\s*ServeHTTP\s*\(")]
    private static partial Regex ServeHttpCall();

    [GeneratedRegex(@"^&?\s*(?<callee>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*(?<open>[({])")]
    private static partial Regex LeadingCallee();

    [GeneratedRegex(@"^&?\s*(?<callee>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)")]
    private static partial Regex LeadingName();

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "func", "if", "for", "switch", "select", "return", "go", "defer", "range", "case", "make", "new", "len", "cap", "append", "panic", "recover", "copy", "delete", "close", "print", "println" };

    private static readonly HashSet<string> RouteMethods = new(StringComparer.Ordinal)
        { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "Any", "Handle", "HandleFunc", "Get", "Post", "Put", "Delete", "Patch", "Head", "Options", "Add", "Match", "Static", "StaticFS", "File", "Method", "MethodFunc", "Connect", "Trace" };

    /// <summary>Go 的外部调用里不算"关键调用"的：context / strings / 错误包装这类，以及 .String() / .Context() 一类取值。</summary>
    private static readonly string[] GoNoisePrefixes =
        ["external:context.", "external:strings.", "external:strconv.", "external:errors.", "external:sync.", "external:bytes.", "external:sort.", "external:math.",
         "external:unicode.", "external:fmt.Sprint", "external:fmt.Errorf", "external:utf8."];

    private static readonly HashSet<string> GoNoiseMembers = new(StringComparer.Ordinal)
        { "String", "Context", "WithContext", "Value", "Len", "Lock", "Unlock", "RLock", "RUnlock" };

    /// <summary>Go 的关键调用：Keep 的 Go 版（Go 的外部调用限定名不带参数表，共用的 Keep 会全丢）。</summary>
    internal static bool GoKeep(FrameworkCall c) => c.Kind switch
    {
        "unresolved" => false,
        "external" => !GoNoisePrefixes.Any(p => c.QualifiedName.StartsWith(p, StringComparison.Ordinal)) && !GoNoiseMembers.Contains(OwnerAndMember(c.QualifiedName).Member),
        "new" => c.Expandable,
        _ => true,
    };

    /// <summary>框架 / 第三方的常见中间件在 next 之后做的事（收尾列的一行）。</summary>
    private static string? KnownPost(string name) => name switch
    {
        "Logger" or "LoggerWithConfig" or "LoggerWithFormatter" or "LoggerWithWriter" or "RequestLogger" or "RequestLoggerWithConfig" => "请求完成后写访问日志（耗时、状态码）",
        "Recovery" or "RecoveryWithWriter" or "CustomRecovery" or "CustomRecoveryWithWriter" or "Recover" or "RecoverWithConfig" or "Recoverer" => "panic 时恢复并回 500",
        _ => null,
    };

    private sealed class Scanner
    {
        private readonly FrameworkInput input;
        private readonly ArchNode container;
        private readonly FrameworkSources src;
        private readonly Dictionary<long, MapSymbol> byId = new();
        private readonly Dictionary<string, List<MapSymbol>> functionsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MapSymbol>> typesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MapSymbol>> callablesByPath = new(StringComparer.Ordinal);
        private readonly List<string> goFiles;
        private readonly Dictionary<string, GoText?> texts = new(StringComparer.Ordinal);
        private RouterScan cur = new(Fw.NetHttp, "", "", 0, "");

        public Scanner(FrameworkInput input)
        {
            this.input = input;
            container = input.Container;
            src = input.Sources;
            foreach (var s in input.Symbols) byId[s.Id] = s;
            var files = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var s in input.Symbols)
            {
                if (!InContainer(s) || !s.Path.EndsWith(".go", StringComparison.Ordinal)) continue;
                files.Add(s.Path);
                if (s.Kind is SymbolKind.Function or SymbolKind.Method)
                {
                    Add(functionsByName, s.Name, s);
                    Add(callablesByPath, s.Path, s);
                }
                else if (s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface) Add(typesByName, s.Name, s);
            }
            goFiles = files.ToList();
        }

        private bool InContainer(MapSymbol s) => s.Kind != SymbolKind.External && container.Members.Any(m => Under(s.Path, m)) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);

        private static void Add<T>(Dictionary<string, List<T>> d, string k, T v)
        {
            if (!d.TryGetValue(k, out var l)) d[k] = l = [];
            l.Add(v);
        }

        private GoText? Text(string path)
        {
            if (texts.TryGetValue(path, out var t)) return t;
            string? raw;
            try { raw = src.ReadFile(path); } catch { raw = null; }
            return texts[path] = raw is null ? null : new GoText(raw);
        }

        private SymbolDto Dto(MapSymbol s) => src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);

        /// <summary>Go 符号的短标签：方法 = 接收者类型.方法，函数 = 函数名。</summary>
        private static string GoLabel(MapSymbol s)
        {
            var q = s.QualifiedName;
            var slash = q.LastIndexOf('/');
            if (slash >= 0) q = q[(slash + 1)..];
            var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return s.Name;
            return s.Kind == SymbolKind.Method && parts.Length >= 2 ? parts[^2] + "." + parts[^1] : parts[^1];
        }

        /// <summary>写在"Xxx 后置"里的名字：ServeHTTP 方法用接收者类型名，其余用函数名。</summary>
        private static string OwnerName(MapSymbol s)
        {
            var label = GoLabel(s);
            return s.Kind == SymbolKind.Method && s.Name == "ServeHTTP" && label.Contains('.') ? label[..label.IndexOf('.')] : label.Contains('.') ? label[(label.LastIndexOf('.') + 1)..] : label;
        }

        private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

        private static string LastDirSegment(string path) { var d = Dir(path); return d.Contains('/') ? d[(d.LastIndexOf('/') + 1)..] : d; }

        private MapSymbol? Enclosing(string path, int line) =>
            (callablesByPath.GetValueOrDefault(path) ?? []).Where(f => f.StartLine <= line && line <= f.EndLine)
                .OrderBy(f => f.EndLine - f.StartLine).FirstOrDefault();

        /// <summary>
        /// 调用点的名字落到仓库里的函数：pkg.F（pkg 在 import 表里，导入路径的末段与函数所在目录的末段相同）、F（同目录）、
        /// x.M（x 不是导入名：按方法名找，同目录优先）。都对不上返回 null（外部包）。
        /// </summary>
        private MapSymbol? ResolveFunc(string? qualifier, string name, string fromPath, GoText fromText)
        {
            if (!functionsByName.TryGetValue(name, out var list)) return null;
            var dir = Dir(fromPath);
            if (string.IsNullOrEmpty(qualifier))
                return list.Where(f => f.Kind == SymbolKind.Function && Dir(f.Path) == dir).OrderBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
            if (fromText.Imports.TryGetValue(qualifier, out var importPath))
            {
                var segs = importPath.Split('/');
                return list.Where(f => f.Kind == SymbolKind.Function && LastDirSegment(f.Path) is { Length: > 0 } last && last == segs[^1] || f.Kind == SymbolKind.Function && segs.Length > 1 && LastDirSegment(f.Path) == segs[^2] && Regex.IsMatch(segs[^1], @"^v\d+$"))
                    .OrderByDescending(f => CommonSuffix(Dir(f.Path).Split('/'), segs)).ThenBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
            }
            // 变量上的方法（amw.Middleware）：按方法名，同目录优先
            var methods = list.Where(f => f.Kind == SymbolKind.Method).ToList();
            return methods.OrderBy(f => Dir(f.Path) == dir ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal).FirstOrDefault();
        }

        private static int CommonSuffix(string[] a, string[] b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && a[^(n + 1)] == b[^(n + 1)]) n++;
            return n;
        }

        private MapSymbol? ResolveType(string? qualifier, string name, string fromPath, GoText fromText)
        {
            if (!typesByName.TryGetValue(name, out var list)) return null;
            var dir = Dir(fromPath);
            if (string.IsNullOrEmpty(qualifier)) return list.FirstOrDefault(t => Dir(t.Path) == dir);
            if (!fromText.Imports.TryGetValue(qualifier, out var importPath)) return null;
            var last = importPath.Split('/')[^1];
            return list.FirstOrDefault(t => LastDirSegment(t.Path) == last);
        }

        private static (string? Qualifier, string Name) SplitCallee(string callee)
        {
            var dot = callee.LastIndexOf('.');
            return dot < 0 ? (null, callee) : (callee[..dot], callee[(dot + 1)..]);
        }

        private static Fw? FrameworkOf(GoText t, string pkg, string fn)
        {
            if (!t.Imports.TryGetValue(pkg, out var path)) return null;
            Fw? fw = path.Contains("gin-gonic/gin", StringComparison.Ordinal) ? Fw.Gin
                : path.Contains("labstack/echo", StringComparison.Ordinal) ? Fw.Echo
                : path.Contains("go-chi/chi", StringComparison.Ordinal) ? Fw.Chi
                : path.Contains("gorilla/mux", StringComparison.Ordinal) ? Fw.Gorilla
                : path == "net/http" ? Fw.NetHttp : null;
            return fw switch
            {
                Fw.Gin when fn is "Default" or "New" => fw,
                Fw.Echo when fn == "New" => fw,
                Fw.Chi when fn is "NewRouter" or "NewMux" => fw,
                Fw.Gorilla when fn == "NewRouter" => fw,
                Fw.NetHttp when fn == "NewServeMux" => fw,
                _ => null,
            };
        }

        // ================= 主流程 =================

        public FrameworkFlowDto Run()
        {
            var entries = input.Entries.Where(e => e.ContainerKey == container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
            var httpEntries = entries.Where(e => e.Kind == "http").ToList();

            var scans = new List<RouterScan>();
            foreach (var path in goFiles)
            {
                if (Text(path) is not { } t) continue;
                foreach (Match m in Ctor().Matches(t.Code))
                {
                    if (FrameworkOf(t, m.Groups["pkg"].Value, m.Groups["fn"].Value) is not { } fw) continue;
                    var line = t.LineAt(m.Index);
                    var scopeFn = Enclosing(path, line);
                    var (_, end) = scopeFn is null ? (0, t.Code.Length) : t.Span(scopeFn.StartLine, scopeFn.EndLine);
                    var v = m.Groups["v"].Value;
                    cur = new RouterScan(fw, v, path, line, $"{m.Groups["pkg"].Value}.{m.Groups["fn"].Value}()");
                    if (fw == Fw.Gin && m.Groups["fn"].Value == "Default") AddGinDefaults(path, line);
                    ScanScope(path, t, m.Index + m.Length, end, new Dictionary<string, Router>(StringComparer.Ordinal) { [v] = new Router(v, fw, true, "") }, scopeFn, 0, []);
                    ScanWraps(path, t, m.Index + m.Length, end, v);
                    scans.Add(cur);
                }
            }
            // 没有显式路由器：http.HandleFunc 注册到 DefaultServeMux，看 ListenAndServe(addr, wrap(http.DefaultServeMux)) 有没有包装
            if (scans.Count == 0 && httpEntries.Count > 0)
            {
                foreach (var path in goFiles)
                {
                    if (Text(path) is not { } t || !t.Code.Contains("ListenAndServe", StringComparison.Ordinal)) continue;
                    var m = Serve().Match(t.Code);
                    if (!m.Success) continue;
                    var line = t.LineAt(m.Index);
                    var scopeFn = Enclosing(path, line);
                    var (s, e) = scopeFn is null ? (0, t.Code.Length) : t.Span(scopeFn.StartLine, scopeFn.EndLine);
                    cur = new RouterScan(Fw.NetHttp, "http.DefaultServeMux", path, line, "http.DefaultServeMux");
                    ScanWraps(path, t, s, e, "http.DefaultServeMux");
                    scans.Add(cur);
                    break;
                }
            }

            var notes = new List<string>();
            var others = entries.Count - httpEntries.Count;
            if (scans.Count == 0 && httpEntries.Count == 0)
            {
                notes.Add("这个容器没有 HTTP 入口，也没认出 Go Web 框架（gin.Default / gin.New / echo.New / chi.NewRouter / mux.NewRouter / http.NewServeMux）：框架流程只画 HTTP 请求链");
                // 纯 gRPC 服务（grpc.NewServer + RegisterXxxServer）：画 gRPC 这条链（拦截器 / StatsHandler → 服务方法），与别的语言一致
                if (FrameworkFlowBuilder.GrpcOnly(input, "go", []) is { } grpcFlow) return grpcFlow;
                if (others > 0) notes.Add($"容器里有 {others} 个非 HTTP 入口（{string.Join("、", entries.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}）");
                return Empty(container, notes);
            }

            var scan = scans.OrderByDescending(s => s.Score).ThenBy(s => s.Fw == Fw.NetHttp ? 1 : 0).ThenBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.Line).FirstOrDefault();
            return Assemble(scan, scans, httpEntries, others, notes);
        }

        private FrameworkFlowDto Assemble(RouterScan? scan, List<RouterScan> scans, List<EntryPoint> httpEntries, int others, List<string> notes)
        {
            var global = new List<Mw>();
            var groups = new List<Mw>();
            if (scan is not null)
            {
                global.AddRange(scan.Wraps);
                global.AddRange(scan.Pre);
                global.AddRange(scan.Global);
                groups.AddRange(scan.Groups);
            }

            var routeNote = scan?.Fw switch
            {
                Fw.Gin => "gin 路由树：按方法 + 路径找到处理函数（路由组的中间件在注册时已拼进各条路由的处理链）",
                Fw.Echo => "echo 路由：按方法 + 路径找到处理函数",
                Fw.Chi => "chi 路由树：按方法 + 路径找到处理函数",
                Fw.Gorilla => "gorilla/mux：按注册顺序逐条匹配（路径、方法……），第一条匹配上的处理函数",
                Fw.NetHttp => "http.ServeMux：按路径（最长前缀）找到处理函数",
                _ => "路由：按方法 + 路径找到处理函数",
            };
            var routeNode = Node(null, "路由", "", "controllers", StageController, routeNote, Cap(scan?.RouteChildren ?? [], StageController), null, null, httpEntries.Count);

            // 收尾：路由组的后置 → 全局的后置（洋葱倒序）；echo 的 HTTPErrorHandler 在 echo 自己的链之后、外层包装之前
            var tail = new List<FlowNodeDto>();
            foreach (var g in Enumerable.Reverse(groups)) tail.AddRange(g.Post);
            if (scan is not null)
            {
                foreach (var g in Enumerable.Reverse(scan.Global)) tail.AddRange(g.Post);
                foreach (var g in Enumerable.Reverse(scan.Pre)) tail.AddRange(g.Post);
                tail.AddRange(scan.ErrorTail);
                foreach (var g in Enumerable.Reverse(scan.Wraps)) tail.AddRange(g.Post);
            }

            var rootChildren = new List<FlowNodeDto>();
            rootChildren.AddRange(global.Select(m => m.Node));
            rootChildren.AddRange(groups.Select(m => m.Node));
            rootChildren.Add(routeNode);
            rootChildren.AddRange(tail);

            var stages = new List<FrameworkStageDto>();
            if (global.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "中间件", true, Symbols(global)));
            if (groups.Count > 0) stages.Add(new FrameworkStageDto(StageGroup, "路由组中间件", true, Symbols(groups)));
            var bySymbol = input.Symbols.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            stages.Add(new FrameworkStageDto(StageController, "路由", true,
                httpEntries.Select(e => bySymbol.GetValueOrDefault(e.SymbolId)).Where(s => s is not null).DistinctBy(s => s!.Id).Select(s => Dto(s!)).ToList()));
            if (tail.Count > 0)
            {
                var tailSymbols = tail.Where(n => n.SymbolId is not null).Select(n => bySymbol.GetValueOrDefault(n.SymbolId!.Value)).Where(s => s is not null && s.Kind != SymbolKind.External)
                    .DistinctBy(s => s!.Id).Select(s => Dto(s!)).ToList();
                stages.Add(new FrameworkStageDto(StageTail, "收尾", true, tailSymbols));
            }

            // ---- Notes ----
            if (scan is null)
                notes.Add("没找到路由器的创建（gin.Default / gin.New / echo.New / chi.NewRouter / mux.NewRouter / http.NewServeMux），中间件没认出；只画路由占位");
            else
            {
                var where = $"{MapSymbol.FileName(scan.Path)}:{scan.Line}";
                notes.Add(scan.Var == "http.DefaultServeMux"
                    ? $"没有显式的路由器：处理函数注册在 http.DefaultServeMux 上（{where} 起服务）"
                    : $"认出 {FwName(scan.Fw)}（{where} {scan.Var} := {scan.Ctor}）");
                if (scans.Count > 1)
                    notes.Add($"另有 {scans.Count - 1} 个路由器没画：{string.Join("、", scans.Where(s => !ReferenceEquals(s, scan)).Select(s => $"{FwName(s.Fw)} {MapSymbol.FileName(s.Path)}:{s.Line}"))}");
                if (scan.Fw == Fw.Gin)
                    notes.Add(scan.Ctor == "gin.Default()" ? "gin.Default() 自带 Logger、Recovery 两个中间件，排在最前；它们的后置（访问日志、panic 恢复）在收尾列" : "gin.New() 不带内置中间件");
                if (scan.Wraps.Count > 0)
                    notes.Add($"{string.Join("、", scan.WrapSites.Distinct())} 手工包装 http.Handler：最后包的最先执行，实际顺序 {string.Join(" → ", scan.Wraps.Select(w => w.Node.Label))} → 路由器");
                if (scan.Pre.Count > 0) notes.Add($"{scan.Var}.Pre 的 {scan.Pre.Count} 个在路由匹配之前执行，排在 Use 前面");
                if (scan.Global.Count(m => m.Node.Note?.Contains("gin.Default()", StringComparison.Ordinal) != true) is var uses && uses > 0)
                    notes.Add($"全局中间件按 {string.Join("、", scan.UseFiles.Select(MapSymbol.FileName))} 里 {scan.Var}.Use 的书写顺序"
                              + (scan.Fw == Fw.Gorilla ? "；gorilla/mux 的 Use 在路由匹配上之后、处理函数之前执行（没匹配上的请求不经过）" : "")
                              + (scan.Fw == Fw.Chi ? "；chi 要求 Use 写在所有路由之前，对整个路由器生效" : ""));
                else if (scan.Global.Count > 0)
                    notes.Add($"没有 {scan.Var}.Use：全局中间件只有 gin.Default() 内置的 Logger、Recovery");
                else if (scan.Wraps.Count == 0 && scan.Pre.Count == 0)
                    notes.Add(scan.Var == "http.DefaultServeMux" ? "没有发现全局中间件（ListenAndServe 的 handler 没有包装）" : $"没有发现全局中间件（没有 {scan.Var}.Use，也没有包装 http.Handler）");
                if (groups.Count > 0)
                    notes.Add($"路由组中间件 {groups.Count} 个，只作用于各自的前缀（{string.Join("、", scan.GroupPrefixes.Distinct())}），见每行说明"
                              + (scan.Fw == Fw.Gin ? "；gin 的 Use 只对这行之后注册的路由生效" : ""));
            }
            if (httpEntries.Count == 0) notes.Add("没有发现 HTTP 入口（处理函数注册）：路由占位的数量是 0");
            if (others > 0) notes.Add($"另有 {others} 个非 HTTP 入口（main / 任务 / 消息消费……），没画在这条请求链上");
            notes.Add(tail.Count > 0 ? "收尾 = 各中间件 next 之后（含 defer）的调用与框架的兜底，按洋葱倒序（后注册的先回来）" : "没有发现收尾（中间件 next 之后 / defer 里没有调用）");
            notes.Add("路由级中间件（r.GET(path, mw, handler)、chi 的 r.With）没画");

            var rootActor = stages[0].Key;
            var root = Node(null, "HTTP 请求", "", "entry", rootActor, null, rootChildren, null, null);
            return FrameworkFlowBuilder.Assemble(container, "HTTP 请求", root, stages, [], notes, "入口");
        }

        private List<SymbolDto> Symbols(IEnumerable<Mw> mws) => mws.Where(m => m.Symbol is not null).Select(m => m.Symbol!).DistinctBy(s => s.Id).Select(Dto).ToList();

        private void AddGinDefaults(string path, int line)
        {
            var where = $"{MapSymbol.FileName(path)}:{line}";
            foreach (var name in new[] { "Logger", "Recovery" })
            {
                var label = $"gin.{name}()";
                var post = new List<FlowNodeDto> { Node(null, label, "external:gin." + name, "external-group", StageTail, $"gin.{name} 后置：{KnownPost(name)}（gin.Default() 内置）", [], path, line) };
                cur.Global.Add(new Mw(Node(null, label, "external:gin." + name, "external-group", StageMiddleware, $"gin.Default() 内置（{where}）", [], path, line), post, null));
            }
        }

        // ================= 注册扫描 =================

        /// <summary>
        /// [start, end) 里路由器变量上的调用（Use / Pre / Group / Route / PathPrefix…Subrouter / NoRoute / 路由注册）与属性赋值，按源码顺序；
        /// 把路由器 / 组传给仓库里的函数就进去接着找。chi 的 Route / Group 闭包递归扫，闭包范围从本层排除。
        /// </summary>
        private void ScanScope(string path, GoText t, int start, int end, Dictionary<string, Router> vars, MapSymbol? scopeFn, int depth, HashSet<string> visited)
        {
            var code = t.Code;
            end = Math.Min(end, code.Length);
            var events = new List<(int Pos, Match M, bool Prop)>();
            for (var m = CallSite().Match(code, start); m.Success && m.Index < end; m = m.NextMatch()) events.Add((m.Index, m, false));
            for (var m = PropAssign().Match(code, start); m.Success && m.Index < end; m = m.NextMatch()) events.Add((m.Index, m, true));
            events.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            var excluded = new List<(int S, int E)>();
            var created = new Dictionary<string, List<(int Pos, string Prefix)>>(StringComparer.Ordinal);
            var routesBefore = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (var (pos, m, prop) in events)
            {
                if (excluded.Any(x => pos >= x.S && pos <= x.E)) continue;
                var line = t.LineAt(pos);
                var where = $"{MapSymbol.FileName(path)}:{line}";
                if (prop)
                {
                    if (!vars.TryGetValue(m.Groups["recv"].Value, out var pr)) continue;
                    var rhsStart = m.Index + m.Length;
                    var rhsEnd = StatementEnd(code, rhsStart, end);
                    HandlerRow(t, path, rhsStart, rhsEnd, pr, m.Groups["p"].Value, where, line);
                    continue;
                }
                var callee = m.Groups["callee"].Value;
                if (Keywords.Contains(callee)) continue;
                var open = m.Index + m.Length - 1;
                var close = MatchPair(code, open, '(', ')');
                if (close < 0 || close > end) continue;
                var args = SplitArgs(code, open + 1, close);
                var (recv, member) = SplitCallee(callee);
                if (recv is not null && vars.TryGetValue(recv, out var router))
                {
                    switch (member)
                    {
                        case "Use" when router.Fw != Fw.NetHttp:
                            foreach (var (s, e) in args)
                                AddMiddleware(t, path, s, e, router, scopeFn, $"{recv}.Use", where, line, false, EarlierOf(router, pos, created, routesBefore, t, path, end));
                            if (router.Root) cur.UseFiles.Add(path);
                            break;
                        case "Pre" when router.Fw == Fw.Echo:
                            foreach (var (s, e) in args) AddMiddleware(t, path, s, e, router, scopeFn, $"{recv}.Pre", where, line, true, null);
                            break;
                        case "Group" when router.Fw is Fw.Gin or Fw.Echo && args.Count > 0:
                        {
                            var prefix = JoinPrefix(router.Prefix, LiteralOrExpr(t, args[0].Start, args[0].End));
                            var group = new Router(LhsOf(t, m.Index) ?? $"{recv}.Group", router.Fw, false, prefix);
                            if (LhsOf(t, m.Index) is { } lhs) vars[lhs] = group;
                            Add(created, router.Var, (pos, prefix));
                            foreach (var (s, e) in args.Skip(1))
                                AddMiddleware(t, path, s, e, group, scopeFn, $"{recv}.Group 的参数", where, line, false, null);
                            break;
                        }
                        case "Group" when router.Fw == Fw.Chi && args.Count > 0 && code[args[0].Start..].StartsWith("func", StringComparison.Ordinal):
                            ChiClosure(t, path, args[0].Start, router.Prefix, true, scopeFn, depth, visited, excluded);
                            break;
                        case "Route" when router.Fw == Fw.Chi && args.Count > 1 && code[args[1].Start..].StartsWith("func", StringComparison.Ordinal):
                            ChiClosure(t, path, args[1].Start, JoinPrefix(router.Prefix, LiteralOrExpr(t, args[0].Start, args[0].End)), false, scopeFn, depth, visited, excluded);
                            break;
                        case "PathPrefix" when router.Fw == Fw.Gorilla && args.Count > 0:
                        {
                            var after = Regex.Match(code[(close + 1)..Math.Min(code.Length, close + 64)], @"^\s*\.\s*Subrouter\s*\(\s*\)");
                            if (!after.Success) break;
                            var prefix = JoinPrefix(router.Prefix, LiteralOrExpr(t, args[0].Start, args[0].End));
                            if (LhsOf(t, m.Index) is { } lhs) vars[lhs] = new Router(lhs, Fw.Gorilla, false, prefix);
                            Add(created, router.Var, (pos, prefix));
                            break;
                        }
                        case "NoRoute" or "NoMethod" when router.Fw == Fw.Gin:
                        case "NotFound" or "MethodNotAllowed" when router.Fw == Fw.Chi:
                            foreach (var (s, e) in args) HandlerRow(t, path, s, e, router, member, where, line);
                            break;
                        default:
                            if (RouteMethods.Contains(member)) { cur.Routes++; Add(routesBefore, router.Var, pos); }
                            break;
                    }
                    continue;
                }
                if (depth < MaxRegisterDepth) Descend(t, path, callee, args, vars, depth, visited);
            }
        }

        /// <summary>
        /// gin：Use 只对之后从这个路由器 / 组建的子组与注册的路由生效（Group 建组时把当时的处理链拷进去）。说明按注册顺序点名：
        /// 之前建的子组（它是哪个注册函数的参数：UsersRegister(v1.Group("/users"))，那个函数里注册了几条路由）不经过，之后建的经过——
        /// 同一个前缀可能前后各建一次（/api/articles 先匿名注册、后要鉴权），只说前缀会说反。
        /// </summary>
        private string? EarlierOf(Router router, int pos, Dictionary<string, List<(int Pos, string Prefix)>> created, Dictionary<string, List<int>> routes, GoText t, string path, int end)
        {
            if (router.Fw != Fw.Gin) return null;
            var before = (created.GetValueOrDefault(router.Var) ?? []).Where(g => g.Pos < pos).ToList();
            var direct = (routes.GetValueOrDefault(router.Var) ?? []).Count(p => p < pos);
            // 之后建的组：同一作用域里这行之后的 Var.Group("…")
            var after = new List<(int Pos, string Prefix)>();
            var code = t.Code;
            for (var m = Regex.Match(code[..Math.Min(end, code.Length)], @"(?<![\w.])" + Regex.Escape(router.Var) + @"\s*\.\s*Group\s*\(", RegexOptions.None, TimeSpan.FromSeconds(1)); m.Success; m = m.NextMatch())
            {
                if (m.Index <= pos) continue;
                var open = m.Index + m.Length - 1;
                var close = MatchPair(code, open, '(', ')');
                if (close < 0) continue;
                var args = SplitArgs(code, open + 1, close);
                if (args.Count > 0) after.Add((m.Index, JoinPrefix(router.Prefix, LiteralOrExpr(t, args[0].Start, args[0].End))));
            }
            if (before.Count == 0 && direct == 0) return null;
            var parts = before.Select(g => DescribeGroup(t, path, g.Pos, g.Prefix)).ToList();
            if (direct > 0) parts.Add($"{direct} 条直接注册在 {router.Var} 上的路由");
            var text = $"gin 的 Use 只对这行之后建的组 / 注册的路由生效：之前的 {string.Join("、", parts)} 不经过";
            if (after.Count > 0) text += $"；之后的 {string.Join("、", after.Select(g => DescribeGroup(t, path, g.Pos, g.Prefix)))} 经过";
            return text;
        }

        /// <summary>一个子组：是注册函数的参数时写"函数（前缀，N 条路由）"（路由数 = 入口里落在那个函数体内的 HTTP 入口），否则只写前缀。</summary>
        private string DescribeGroup(GoText t, string path, int groupPos, string prefix)
        {
            var lineStart = t.Code.LastIndexOf('\n', Math.Max(0, groupPos - 1)) + 1;
            var m = Regex.Match(t.Code[lineStart..groupPos], @"(?<fn>[\w.]+)\s*\(\s*$");
            if (!m.Success || Keywords.Contains(m.Groups["fn"].Value)) return prefix;
            var callee = m.Groups["fn"].Value;
            var (qualifier, name) = SplitCallee(callee);
            if (ResolveFunc(qualifier, name, path, t) is not { } fn) return $"{name}（{prefix}）";
            var n = input.Entries.Count(e => e.ContainerKey == container.Key && e.Kind == "http" && e.File == fn.Path && e.Line >= fn.StartLine && e.Line <= fn.EndLine);
            return n > 0 ? $"{name}（{prefix}，{n} 条路由）" : $"{name}（{prefix}）";
        }

        private static string? LhsOf(GoText t, int pos)
        {
            var lineStart = t.Code.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
            var m = LhsBefore().Match(t.Code[lineStart..pos]);
            return m.Success ? m.Groups["lhs"].Value : null;
        }

        /// <summary>chi：r.Route("/api", func(r chi.Router) { … }) / r.Group(func(r chi.Router) { … })：闭包体按新前缀递归扫，闭包范围从外层排除。</summary>
        private void ChiClosure(GoText t, string path, int funcPos, string prefix, bool anonymous, MapSymbol? scopeFn, int depth, HashSet<string> visited, List<(int S, int E)> excluded)
        {
            if (ParseFunc(t.Code, funcPos) is not { } f) return;
            excluded.Add((f.Start, f.BodyClose));
            var param = ParamList(t.Code[(f.ParamsOpen + 1)..f.ParamsClose]).FirstOrDefault().Name;
            if (string.IsNullOrEmpty(param)) return;
            var group = new Router(param, Fw.Chi, false, anonymous && prefix.Length == 0 ? "" : prefix);
            ScanScope(path, t, f.BodyOpen + 1, f.BodyClose, new Dictionary<string, Router>(StringComparer.Ordinal) { [param] = group }, scopeFn, depth, visited);
        }

        /// <summary>F(r) / F(v1.Group("/users")) / F(r.PathPrefix("/x").Subrouter())：F 是仓库里的函数，把对应的形参当成路由器 / 组进去接着扫。</summary>
        private void Descend(GoText t, string path, string callee, List<(int Start, int End)> args, Dictionary<string, Router> vars, int depth, HashSet<string> visited)
        {
            var code = t.Code;
            var mapped = new List<(int Index, Router Router)>();
            for (var i = 0; i < args.Count; i++)
            {
                var a = code[args[i].Start..args[i].End];
                if (vars.TryGetValue(a, out var r)) { mapped.Add((i, r)); continue; }
                var g = Regex.Match(a, @"^(?<v>[A-Za-z_][\w.]*)\s*\.\s*(?<m>Group|PathPrefix)\s*\(");
                if (!g.Success || !vars.TryGetValue(g.Groups["v"].Value, out var parent)) continue;
                var gOpen = args[i].Start + g.Index + g.Length - 1;
                var gClose = MatchPair(code, gOpen, '(', ')');
                if (gClose < 0) continue;
                var inner = SplitArgs(code, gOpen + 1, gClose);
                if (inner.Count == 0) continue;
                var isGroup = g.Groups["m"].Value == "Group" && parent.Fw is Fw.Gin or Fw.Echo;
                var isSub = g.Groups["m"].Value == "PathPrefix" && parent.Fw == Fw.Gorilla && code[(gClose + 1)..args[i].End].Contains("Subrouter", StringComparison.Ordinal);
                if (!isGroup && !isSub) continue;
                mapped.Add((i, new Router("", parent.Fw, false, JoinPrefix(parent.Prefix, LiteralOrExpr(t, inner[0].Start, inner[0].End)))));
            }
            if (mapped.Count == 0) return;
            var (q, n) = SplitCallee(callee);
            if (ResolveFunc(q, n, path, t) is not { } fn || Text(fn.Path) is not { } ft) return;
            if (!visited.Add(fn.Id + ":" + string.Join(",", mapped.Select(x => x.Index + "=" + x.Router.Prefix)))) return;
            var (fs, fe) = ft.Span(fn.StartLine, fn.EndLine);
            var funcPos = ft.Code.IndexOf("func", fs, StringComparison.Ordinal);
            if (funcPos < 0 || funcPos >= fe || ParseFunc(ft.Code, funcPos) is not { } decl) return;
            var ps = ParamList(ft.Code[(decl.ParamsOpen + 1)..decl.ParamsClose]);
            var newVars = new Dictionary<string, Router>(StringComparer.Ordinal);
            foreach (var (i, r) in mapped)
                if (i < ps.Count && ps[i].Name.Length > 0) newVars[ps[i].Name] = r with { Var = ps[i].Name };
            if (newVars.Count == 0) return;
            ScanScope(fn.Path, ft, decl.BodyOpen + 1, decl.BodyClose, newVars, fn, depth + 1, visited);
        }

        /// <summary>NoRoute / NotFound / HTTPErrorHandler 这类兜底处理函数：路由占位下的一行（错误处理进收尾）。</summary>
        private void HandlerRow(GoText t, string path, int s, int e, Router router, string member, string where, int line)
        {
            var code = t.Code;
            var expr = code[s..e].Trim();
            var isError = member == "HTTPErrorHandler";
            var stage = isError ? StageTail : StageController;
            var note = isError ? $"处理函数返回 error 时（{router.Var}.HTTPErrorHandler，{where}）" : $"没匹配上路由时（{router.Var}.{member}，{where}）";
            FlowNodeDto node;
            var lc = LeadingCallee().Match(expr);
            // http.HandlerFunc(notFound) 是类型转换：看里面那个
            if (lc.Success && lc.Groups["callee"].Value is "http.HandlerFunc" or "http.Handler" && lc.Groups["open"].Value == "(")
            {
                var open = s + (expr.Length - expr.TrimStart().Length) + lc.Groups["open"].Index;
                var close = MatchPair(code, open, '(', ')');
                if (close > open) { var inner = SplitArgs(code, open + 1, close); if (inner.Count == 1) { s = inner[0].Start; e = inner[0].End; expr = code[s..e]; lc = LeadingCallee().Match(expr); } }
            }
            var name = lc.Success ? lc.Groups["callee"].Value : Collapse(t.Raw[s..e]);
            var (q, n) = SplitCallee(name);
            if (ResolveFunc(q, n, path, t) is { } fn)
            {
                var calls = Ordered(src.Calls(fn.Id)).Where(GoKeep).ToList();
                node = Node(fn.Id, GoLabel(fn), fn.QualifiedName, "call", stage, note, Cap(calls.Select(c => CallNode(c, fn.Path, stage, null)).ToList(), stage), path, line);
            }
            else node = Node(null, Shorten(t.Raw[s..e], name), name, "external-group", stage, note, [], path, line);
            (isError ? cur.ErrorTail : cur.RouteChildren).Add(node);
        }

        private static string Shorten(string raw, string callee)
        {
            var c = Collapse(raw);
            return c.Length <= 40 ? c : callee + "(…)";
        }

        // ================= 手工包装 =================

        /// <summary>
        /// 包在路由器外面的 http.Handler：从路由器变量出发，handler = X(handler) / &amp;T{next: handler} 每次包一层（最后包的在最外层、最先执行），
        /// 别名（var handler http.Handler = r）跟着走；最后 ListenAndServe(addr, …) / Handler: … 里再包的也算。
        /// </summary>
        private void ScanWraps(string path, GoText t, int start, int end, string rootVar)
        {
            var code = t.Code;
            end = Math.Min(end, code.Length);
            var handlerVars = new HashSet<string>(StringComparer.Ordinal) { rootVar };
            var layers = new List<(string Callee, bool Struct, int Pos, int Line)>();
            var lines = new List<int>();
            for (var m = Assign().Match(code, start); m.Success && m.Index < end; m = m.NextMatch())
            {
                var rhsStart = m.Index + m.Length;
                var rhsEnd = StatementEnd(code, rhsStart, end);
                var rhs = code[rhsStart..rhsEnd].Trim();
                var lhs = m.Groups["lhs"].Value;
                if (handlerVars.Contains(rhs)) { handlerVars.Add(lhs); continue; }
                var wrapped = ParseWrap(t, rhsStart, rhsEnd, handlerVars);
                if (wrapped.Count == 0) continue;
                layers.InsertRange(0, wrapped);
                lines.Add(t.LineAt(rhsStart));
                handlerVars.Add(lhs);
            }
            // ListenAndServe(addr, wrap(handler)) / ListenAndServeTLS(addr, cert, key, wrap(handler)) / &http.Server{Handler: wrap(handler)}
            for (var m = Serve().Match(code, start); m.Success && m.Index < end; m = m.NextMatch())
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPair(code, open, '(', ')');
                if (close < 0) continue;
                var args = SplitArgs(code, open + 1, close);
                var idx = m.Groups["fn"].Value == "ListenAndServe" ? 1 : 3;
                if (args.Count <= idx) continue;
                var wrapped = ParseWrap(t, args[idx].Start, args[idx].End, handlerVars);
                if (wrapped.Count == 0) continue;
                layers.InsertRange(0, wrapped);
                lines.Add(t.LineAt(m.Index));
            }
            for (var m = HandlerField().Match(code, start); m.Success && m.Index < end; m = m.NextMatch())
            {
                var s = m.Index + m.Length;
                var e = s;
                var depth = 0;
                for (; e < end; e++)
                {
                    if (code[e] is '(' or '[' or '{') depth++;
                    else if (code[e] is ')' or ']' or '}') { if (--depth < 0) break; }
                    else if (code[e] is ',' or '\n' && depth == 0) break;
                }
                var wrapped = ParseWrap(t, s, e, handlerVars);
                if (wrapped.Count == 0) continue;
                layers.InsertRange(0, wrapped);
                lines.Add(t.LineAt(m.Index));
            }
            if (layers.Count == 0) return;
            lines.Sort();
            cur.WrapSites.Add($"{MapSymbol.FileName(path)}:{(lines[0] == lines[^1] ? lines[0].ToString() : $"{lines[0]}-{lines[^1]}")}");
            foreach (var (callee, isStruct, _, line) in layers)
            {
                var where = $"{MapSymbol.FileName(path)}:{line}";
                var (q, n) = SplitCallee(callee);
                var note = $"包在路由器外面的 http.Handler（{where}）";
                MapSymbol? fn = null;
                if (isStruct)
                {
                    if (ResolveType(q, n, path, t) is { } type)
                        fn = (functionsByName.GetValueOrDefault("ServeHTTP") ?? []).FirstOrDefault(f => f.QualifiedName == type.QualifiedName + ".ServeHTTP");
                }
                else fn = ResolveFunc(q, n, path, t);
                if (fn is not null) cur.Wraps.Add(AnalyzeFunc(fn, GoLabel(fn), note, path, line, StageMiddleware));
                else cur.Wraps.Add(External(isStruct ? $"&{callee}{{…}}" : callee, callee, note, path, line, StageMiddleware));
            }
        }

        /// <summary>
        /// 一个包装表达式里包了几层（外层在前）：&amp;T{… v …} → T；f(v, …) / pkg.F(v, …) → F，再看包着 v 的那个实参；http.HandlerFunc(x) 这类类型转换不算一层。
        /// 表达式里没有出现 handlerVars 里的变量（作为独立的名字）就不是包装。
        /// </summary>
        private static List<(string Callee, bool Struct, int Pos, int Line)> ParseWrap(GoText t, int start, int end, HashSet<string> handlerVars)
        {
            var code = t.Code;
            var result = new List<(string, bool, int, int)>();
            var s = start;
            var e = end;
            for (var guard = 0; guard < 16; guard++)
            {
                while (s < e && char.IsWhiteSpace(code[s])) s++;
                while (e > s && char.IsWhiteSpace(code[e - 1])) e--;
                var expr = code[s..e];
                if (handlerVars.Contains(expr)) return result;
                var m = LeadingCallee().Match(expr);
                if (!m.Success) return [];
                var callee = m.Groups["callee"].Value;
                if (handlerVars.Contains(callee) || handlerVars.Any(v => callee.StartsWith(v + ".", StringComparison.Ordinal))) return []; // r.PathPrefix(...)：路由器自己的方法
                var open = s + m.Groups["open"].Index;
                var isStruct = code[open] == '{';
                var close = MatchPair(code, open, code[open], isStruct ? '}' : ')');
                if (close < 0) return [];
                var args = SplitArgs(code, open + 1, close);
                var holder = args.FirstOrDefault(a => handlerVars.Any(v => Regex.IsMatch(code[a.Start..a.End], @"(?<![\w.])" + Regex.Escape(v) + @"(?![\w])")));
                if (holder == default) return [];
                var line = t.LineAt(s);
                if (isStruct)
                {
                    // &http.Server{Handler: …} 是服务配置不是一层包装（Handler 字段另行扫描）
                    if (callee.EndsWith("Server", StringComparison.Ordinal)) return [];
                    result.Add((callee, true, s, line));
                    return result;
                }
                if (callee is not ("http.HandlerFunc" or "http.Handler")) result.Add((callee, false, s, line));
                // 结构体字段 next: handler 的形式在 { } 里：上面已处理；这里接着看包着变量的那个实参
                s = holder.Start;
                e = holder.End;
                var colon = Regex.Match(code[s..e], @"^[A-Za-z_]\w*\s*:\s*");
                if (colon.Success) s += colon.Length;
            }
            return result;
        }

        // ================= 中间件分析 =================

        /// <summary>Use / Pre / Group 的一个实参：内联 func 字面量、仓库里的函数（调用或函数值）、外部的。</summary>
        private void AddMiddleware(GoText t, string path, int s, int e, Router router, MapSymbol? scopeFn, string how, string where, int line, bool pre, string? earlier)
        {
            var code = t.Code;
            var regNote = pre ? $"{how}（{where}），路由匹配之前执行"
                : router.Root ? $"{how}（{where}）"
                : $"路由组 {(router.Prefix.Length == 0 ? "（无前缀分组）" : router.Prefix)}（{how}，{where}）：只作用于这个前缀下的路由";
            if (earlier is not null) regNote += "；" + earlier;
            var stage = router.Root || pre ? StageMiddleware : StageGroup;
            Mw mw;
            var expr = code[s..e];
            if (expr.StartsWith("func", StringComparison.Ordinal) && ParseFunc(code, s) is { } lit)
            {
                var calls = scopeFn is null ? new List<FrameworkCall>()
                    : Ordered(src.Calls(scopeFn.Id)).Where(c => { var p = t.PosOf(c); return p > lit.BodyOpen && p < lit.BodyClose; }).ToList();
                var ps = ParamList(code[(lit.ParamsOpen + 1)..lit.ParamsClose]);
                mw = AnalyzeBody(calls, scopeFn?.Path ?? path, t, lit, lit, ps, 0, null, "内联中间件", "", regNote, path, line, stage, "内联中间件");
            }
            else
            {
                // 调用（RequestID()）或函数值（audit、amw.Middleware）
                var lc = LeadingName().Match(expr);
                var callee = lc.Success ? lc.Groups["callee"].Value : Collapse(expr);
                var label = Shorten(t.Raw[s..e], callee);
                var (q, n) = SplitCallee(callee);
                mw = (lc.Success ? ResolveFunc(q, n, path, t) : null) is { } fn
                    ? AnalyzeFunc(fn, label, regNote, path, line, stage)
                    : External(label, callee, regNote, path, line, stage);
            }
            if (pre) cur.Pre.Add(mw);
            else if (router.Root) cur.Global.Add(mw);
            else
            {
                cur.Groups.Add(mw);
                cur.GroupPrefixes.Add(router.Prefix.Length == 0 ? "（无前缀分组）" : router.Prefix);
            }
        }

        private static Mw External(string label, string callee, string regNote, string? path, int? line, string stage)
        {
            var name = SplitCallee(callee).Name;
            var known = KnownPost(name);
            var node = Node(null, label, callee.Contains('.') ? "external:" + callee : callee, "external-group", stage, regNote + "；框架 / 第三方中间件", [], path, line);
            var post = known is null ? new List<FlowNodeDto>() : [Node(null, label, "external:" + callee, "external-group", StageTail, $"{callee} 后置：{known}", [], path, line)];
            return new Mw(node, post, null);
        }

        /// <summary>仓库里的中间件函数 / 方法：返回闭包的只看闭包里的调用（闭包外是构造时执行一次的）。</summary>
        private Mw AnalyzeFunc(MapSymbol fn, string label, string regNote, string? regPath, int? regLine, string stage)
        {
            if (Text(fn.Path) is not { } t) return new Mw(Node(fn.Id, label, fn.QualifiedName, "call", stage, regNote, [], regPath ?? fn.Path, regLine ?? fn.StartLine), [], fn);
            var code = t.Code;
            var (fs, fe) = t.Span(fn.StartLine, fn.EndLine);
            var funcPos = code.IndexOf("func", fs, StringComparison.Ordinal);
            if (funcPos < 0 || funcPos >= fe || ParseFunc(code, funcPos) is not { } decl)
                return new Mw(Node(fn.Id, label, fn.QualifiedName, "call", stage, regNote, [], regPath ?? fn.Path, regLine ?? fn.StartLine), [], fn);
            var calls = Ordered(src.Calls(fn.Id)).ToList();
            var ps = ParamList(code[(decl.ParamsOpen + 1)..decl.ParamsClose]);
            var body = decl;
            var setup = 0;
            var ret = ReturnFunc().Match(code, decl.BodyOpen);
            if (ret.Success && ret.Index < decl.BodyClose && ParseFunc(code, ret.Groups["f"].Index) is { } closure && closure.BodyClose <= decl.BodyClose)
            {
                body = closure;
                ps.AddRange(ParamList(code[(closure.ParamsOpen + 1)..closure.ParamsClose]));
                setup = calls.Count(c => t.PosOf(c) < closure.Start && GoKeep(c));
                calls = calls.Where(c => { var p = t.PosOf(c); return p > closure.BodyOpen && p < closure.BodyClose; }).ToList();
            }
            return AnalyzeBody(calls, fn.Path, t, decl, body, ps, setup, fn, label, fn.QualifiedName, regNote, regPath, regLine, stage, OwnerName(fn));
        }

        /// <summary>
        /// 中间件本体：找主 next（gin 的 c.Next()、ServeHTTP、调 Handler 类型的参数），之前的是前置子节点，之后的与 defer 里的进收尾；
        /// c.Abort* / next 之前的 return 标"可能中断"。闭包本身带来的"回调 / lambda 里"去掉，嵌套字面量里的保留。
        /// </summary>
        private Mw AnalyzeBody(List<FrameworkCall> calls, string callsPath, GoText t, FuncSpan decl, FuncSpan body, List<(string Name, string Type)> ps, int setup,
            MapSymbol? fn, string label, string qn, string regNote, string? regPath, int? regLine, string stage, string owner)
        {
            var code = t.Code;
            var nextNames = ps.Where(p => p.Name.Length > 0 && Regex.IsMatch(p.Type, @"(Handler|HandlerFunc|Handle)$")).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var ginContexts = ps.Where(p => p.Name.Length > 0 && p.Type.Contains("gin.Context", StringComparison.Ordinal)).Select(p => p.Name).ToList();
            var isGin = ginContexts.Count > 0;

            // 函数体里的嵌套字面量（defer func 单独算）与 defer 语句
            var defers = new List<(int S, int E)>();
            for (var m = Defer().Match(code, body.BodyOpen); m.Success && m.Index < body.BodyClose; m = m.NextMatch())
            {
                var after = FrameworkFlowBuilder.SkipWs(code, m.Index + m.Length);
                if (code[after..].StartsWith("func", StringComparison.Ordinal) && ParseFunc(code, after) is { } df)
                {
                    var callOpen = FrameworkFlowBuilder.SkipWs(code, df.BodyClose + 1);
                    var callEnd = callOpen < code.Length && code[callOpen] == '(' ? MatchPair(code, callOpen, '(', ')') : df.BodyClose;
                    defers.Add((m.Index, Math.Max(callEnd, df.BodyClose)));
                }
                else defers.Add((m.Index, StatementEnd(code, after, body.BodyClose)));
            }
            var nested = new List<(int S, int E)>();
            for (var m = FuncLiteral().Match(code, body.BodyOpen + 1); m.Success && m.Index < body.BodyClose; m = m.NextMatch())
            {
                if (defers.Any(d => m.Index >= d.S && m.Index <= d.E)) continue;
                if (ParseFunc(code, m.Index) is { } lit) nested.Add((lit.Start, lit.BodyClose));
            }
            bool InNested(int p) => nested.Any(x => p >= x.S && p <= x.E);
            int DeferIndex(int p) => defers.FindIndex(d => p >= d.S && p <= d.E);

            // next：调用大纲里认出的优先（有 ctx，能判断在不在分支里）；认不出时退回源码文本
            bool IsNext(FrameworkCall c)
            {
                var (_, member) = OwnerAndMember(c.QualifiedName);
                if (member == "ServeHTTP") return true;
                if (member == "Next" && isGin && c.Kind is "external" or "unresolved") return true;
                if (nextNames.Contains(member) && c.Kind is "external" or "unresolved") return true;
                var (_, labelMember) = OwnerAndMember(c.Label);
                return nextNames.Contains(labelMember) && c.Kind is "external" or "unresolved";
            }
            var textNext = new List<(int S, int E)>();
            void TextNext(int at)
            {
                var open = code.IndexOf('(', at);
                if (open < 0 || open >= body.BodyClose || InNested(at)) return;
                var close = MatchPair(code, open, '(', ')');
                textNext.Add((at, close < 0 ? open : close));
            }
            for (var m = ServeHttpCall().Match(code, body.BodyOpen); m.Success && m.Index < body.BodyClose; m = m.NextMatch()) TextNext(m.Index);
            foreach (var cn in ginContexts)
                for (var m = Regex.Match(code[..body.BodyClose], @"(?<![\w.])" + Regex.Escape(cn) + @"\s*\.\s*Next\s*\(\s*\)"); m.Success; m = m.NextMatch())
                    if (m.Index > body.BodyOpen) TextNext(m.Index);
            foreach (var name in nextNames)
                for (var m = Regex.Match(code[..body.BodyClose], @"(?<![\w.])" + Regex.Escape(name) + @"\s*\(", RegexOptions.None, TimeSpan.FromSeconds(1)); m.Success; m = m.NextMatch())
                    if (m.Index > body.BodyOpen) TextNext(m.Index);
            textNext.Sort((a, b) => a.S.CompareTo(b.S));

            var nextCalls = calls.Where(IsNext).ToList();
            var main = nextCalls.FirstOrDefault(c => c.Ctx != CallContext.Cond) ?? nextCalls.LastOrDefault();
            var conditional = nextCalls.Any(c => c.Ctx == CallContext.Cond) || textNext.Count > 1;
            (int S, int E)? nextSpan = null;
            if (main is not null)
            {
                var p = t.PosOf(main);
                nextSpan = textNext.Cast<(int S, int E)?>().FirstOrDefault(x => p >= x!.Value.S && p <= x.Value.E) ?? (p, t.LineStart(main.Line + 1) - 1);
            }
            else if (textNext.Count > 0) nextSpan = textNext[0];

            var pre = new List<FrameworkCall>();
            var post = new List<FrameworkCall>();
            var deferred = new List<(int Index, FrameworkCall Call)>();
            var aborts = new List<string>();
            foreach (var c in calls)
            {
                if (IsNext(c)) continue;
                var p = t.PosOf(c);
                var (_, member) = OwnerAndMember(c.QualifiedName);
                if (member.StartsWith("Abort", StringComparison.Ordinal) && !aborts.Contains(member)) aborts.Add(member);
                if (!GoKeep(c)) continue;
                var adjusted = c.Ctx == CallContext.Callback && !InNested(p) ? c with { Ctx = null } : c;
                if (DeferIndex(p) is var di and >= 0) { deferred.Add((di, adjusted with { Ctx = null })); continue; }
                if (nextSpan is { } ns && p > ns.E) post.Add(adjusted);
                else pre.Add(adjusted);
            }
            // 早退：next 之前有 return（不是返回闭包那一句、不在嵌套字面量里）
            var earlyReturn = false;
            if (!isGin && nextSpan is { } span)
                for (var m = ReturnKeyword().Match(code, body.BodyOpen); m.Success && m.Index < span.S; m = m.NextMatch())
                {
                    if (InNested(m.Index) || t.LineAt(m.Index) == t.LineAt(span.S)) continue;
                    if (Regex.IsMatch(code[(m.Index + 6)..Math.Min(code.Length, m.Index + 64)], @"^\s*(?:[A-Za-z_][\w.]*\s*\(\s*)?func\s*\(")) continue;
                    earlyReturn = true;
                    break;
                }

            var note = regNote;
            if (aborts.Count > 0) note += $"；可能中断（{string.Join("、", aborts.Select(a => "c." + a))} 之后不再执行后面的处理器）";
            if (earlyReturn) note += "；可能中断（next 之前有 return，请求不往下传）";
            if (nextSpan is null)
                note += isGin ? "；没调 c.Next()：它返回后 gin 接着执行后面的处理器，调用都算前置" : "；没找到 next 调用";
            else if (conditional) note += "；有条件调用 next";
            if (setup > 0) note += $"；构造时的 {setup} 个调用（启动时执行一次）不画";

            var kids = Dedup(pre).Select(c => CallNode(c, callsPath, stage, null)).ToList();
            var postNodes = Dedup(post).Select(c => CallNode(c, callsPath, StageTail, $"{owner} 后置")).ToList();
            // defer 按后进先出执行
            foreach (var group in deferred.GroupBy(d => d.Index).OrderByDescending(g => g.Key))
                postNodes.AddRange(Dedup(group.Select(g => g.Call).ToList()).Select(c => CallNode(c, callsPath, StageTail, $"{owner} 后置（defer）")));
            var node = Node(fn?.Id, label, qn, fn is null ? "external-group" : "call", stage, note, Cap(kids, stage), regPath ?? fn?.Path, regLine ?? fn?.StartLine);
            return new Mw(node, postNodes, fn);
        }

        /// <summary>同一个外部调用在一段里只留第一次（Go 的外部调用按包名猜，r.Context() / w.Header() 这类会重复很多次）。</summary>
        private static List<FrameworkCall> Dedup(List<FrameworkCall> calls)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return calls.Where(c => c.Kind != "external" || seen.Add(c.QualifiedName)).ToList();
        }
    }
}
