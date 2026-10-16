using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · 前端（frontend）：打开一个页面要经过的那套框架。由 <see cref="FrameworkFlowBuilder.Build"/> 按容器 SubKind 分派进来。纯函数，只看符号、边与源码文本。
/// 阶段（列）与识别规则：
/// <list type="number">
/// <item>mount（挂载）：React SPA 是入口文件（main.tsx / index.tsx…，或调了 react-dom 的 createRoot / hydrateRoot / render 的文件）里的挂载调用；
/// 挂载之前的模块顶层调用（client.setConfig、new QueryClient、createRouter、i18n.init…）、副作用导入、OpenAPI.BASE / TOKEN 赋值作为它的子节点。
/// Next.js App Router 没有 createRoot：画包住至少一半页面的那几层 app/**/layout.tsx（由外到内）；Pages Router 画 pages/_app。</item>
/// <item>provider：挂载的 JSX（或布局返回的 JSX）里由外到内包住页面的组件（QueryClientProvider、Redux Provider、ThemeProvider、自定义 *Provider……）。
/// 仓库里的包装组件（含根组件 App）进去接着找，最多两层，里面的标"经 X"；组件自己的 Context.Provider 不算。
/// QueryClientProvider 挂 new QueryClient 的配置（defaultOptions、queryCache / mutationCache 的 onError，onError 进收尾）。
/// 同层不包东西的组件（Toaster、Devtools）写进 Note，不画。</item>
/// <item>route（路由）：TanStack Router（createRootRoute / createFileRoute(...)({ beforeLoad, loader, component })：画根路由与布局路由，守卫写清条件与跳转）、
/// React Router（createBrowserRouter([...]) / &lt;Routes&gt;&lt;Route&gt;：画布局路由、守卫组件、loader）、Next.js 的 middleware.ts / proxy.ts（排在最前，请求先过它）。</item>
/// <item>controller（页面）：占位 Kind = "controllers"，Count = 容器里 kind = page 的入口数，点开列入口。</item>
/// <item>request（请求封装）：axios.create 的实例、interceptors.request.use（请求拦截器）、生成的 OpenAPI 客户端（@hey-api/openapi-ts / openapi-typescript-codegen）、
/// 被 3 处以上调用的 fetch 封装函数。</item>
/// <item>tail（收尾）：interceptors.response.use 的成功 / 出错两段、TanStack Query 全局 onError、出错边界（ErrorBoundary、路由的 errorComponent / errorElement、Next 的 error.tsx）。
/// 出错处理写清状态码与动作（401 → 清 token、跳 /login、弹提示……）。</item>
/// </list>
/// Vue / Angular / Svelte 还不认，只给 Note。找不到的阶段不画，Notes 里说为什么。
/// </summary>
public static partial class FrontendFrameworkFlow
{
    /// <summary>前端：挂载（createRoot(...).render / 根布局）。</summary>
    public const string StageMount = "mount";

    private const int MaxExpandLevel = 2;

    public static FrameworkFlowDto Build(FrameworkInput input)
    {
        try { return new Run(input).Build(); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or RegexMatchTimeoutException)
        {
            // 文本扫描遇到没料到的写法时不拖垮整个视图：给一句说明（带异常类型，方便回报）
            return Empty(input.Container, [$"前端框架流程分析源码时出错（{e.GetType().Name}：{e.Message}），这个容器先不画"]);
        }
    }

    private static readonly string[] ScriptExtensions = [".tsx", ".ts", ".jsx", ".js", ".mjs", ".cjs", ".mts", ".cts"];
    private static readonly string[] EntryFileNames = ["main.tsx", "main.jsx", "main.ts", "main.js", "index.tsx", "index.jsx", "index.js", "index.ts", "entry-client.tsx", "client.tsx"];
    private static readonly HashSet<string> MountFunctions = new(StringComparer.Ordinal) { "createRoot", "hydrateRoot", "render", "hydrate" };

    /// <summary>外部调用大多不是"关键调用"（TS 的外部符号没有参数表，<see cref="FrameworkFlowBuilder.Keep"/> 全滤掉），这些留下：跳转、提示、令牌读写、配置。</summary>
    private static readonly HashSet<string> KeyExternals = new(StringComparer.Ordinal)
    {
        "redirect", "navigate", "notFound", "toast", "enqueueSnackbar", "dispatchEvent", "signOut", "signIn", "getSession",
        "removeItem", "setConfig", "fetch", "captureException", "rewrite", "next",
    };

    private static readonly (string Dep, string Name)[] KnownLibraries =
    [
        ("next", "Next.js"), ("react-router-dom", "React Router"), ("react-router", "React Router"), ("@tanstack/react-router", "TanStack Router"),
        ("@tanstack/react-query", "TanStack Query"), ("react-query", "React Query"), ("swr", "SWR"), ("@apollo/client", "Apollo Client"),
        ("@reduxjs/toolkit", "Redux Toolkit"), ("react-redux", "Redux"), ("zustand", "Zustand"), ("jotai", "Jotai"), ("mobx-react-lite", "MobX"), ("mobx-react", "MobX"),
        ("axios", "axios"), ("ky", "ky"), ("@hey-api/openapi-ts", "@hey-api/openapi-ts"), ("openapi-typescript-codegen", "openapi-typescript-codegen"), ("orval", "orval"),
        ("openapi-fetch", "openapi-fetch"), ("i18next", "i18next"), ("next-intl", "next-intl"), ("next-auth", "NextAuth"),
    ];

    internal static bool FrontKeep(FrameworkCall c) =>
        Keep(c) || c.Kind == "external" && KeyExternals.Contains(OwnerAndMember(c.QualifiedName).Member);

    [GeneratedRegex(@"(?<![\w$.])(?:(?<ns>[A-Za-z_$][\w$]*)\s*\.\s*)?(?<fn>createRoot|hydrateRoot)\s*\(")]
    private static partial Regex ModernMount();

    [GeneratedRegex(@"(?<![\w$.])(?:(?<ns>[A-Za-z_$][\w$]*)\s*\.\s*)?(?<fn>render|hydrate)\s*\(\s*<")]
    private static partial Regex LegacyMount();

    [GeneratedRegex(@"(?m)^[ \t]*import\s+(?<q>[""'])")]
    private static partial Regex SideEffectImport();

    [GeneratedRegex(@"(?<![\w$.])(?<lhs>OpenAPI\s*\.\s*[A-Z_]+|axios\s*\.\s*defaults(?:\s*\.\s*[\w$]+)+)\s*=(?![=>])")]
    private static partial Regex GlobalAssign();

    /// <summary>组件库里也算框架外壳的名字（其余组件库组件如 Box / Stack 是界面，穿过去）。</summary>
    // 组件里的跳转：router.replace / router.push / redirect( / navigate(（守卫与跳转组件，不是 Provider）
    [GeneratedRegex(@"\b(?:router\s*\.\s*(?:replace|push)|redirect|navigate)\s*\(")]
    private static partial Regex NavigateCall();

    [GeneratedRegex(@"(Provider|Boundary|Adapter|Router|Suspense|StrictMode|Root|Gate|Guard|Context)$")]
    private static partial Regex ProviderLike();

    [GeneratedRegex(@"(?:getElementById|querySelector)\s*\(\s*[""'`](?<id>[^""'`]+)")]
    private static partial Regex MountTarget();

    [GeneratedRegex(@"\.(css|scss|sass|less|styl|svg|png)$")]
    private static partial Regex StyleImport();

    private sealed partial class Run
    {
        private readonly FrameworkInput input;
        private readonly ArchNode container;
        private readonly FrameworkSources src;
        private readonly string home;
        private readonly Dictionary<long, MapSymbol> byId = new();
        private readonly ILookup<long, MapSymbol> childrenOf;
        private readonly ILookup<string, MapSymbol> symbolsByPath;
        private readonly Dictionary<string, MapSymbol> modulesByPath = new(StringComparer.Ordinal);
        private readonly HashSet<long> inside = new();
        private readonly Dictionary<long, List<MapEdge>> callsFrom = new();
        private readonly Dictionary<string, JsFile?> files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, (string Spec, bool Default, string Imported)>> importCache = new(StringComparer.Ordinal);
        private readonly List<string> notes = [];
        private readonly Dictionary<string, List<MapSymbol>> stageSymbols = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> orderKnown = new(StringComparer.Ordinal);
        private Dictionary<string, string> deps = new(StringComparer.Ordinal);

        private readonly List<FlowNodeDto> mountRows = [];
        private readonly List<FlowNodeDto> providerRows = [];
        private readonly List<FlowNodeDto> routeRows = [];
        private readonly List<FlowNodeDto> requestRows = [];
        private readonly List<FlowNodeDto> tailRows = [];
        private readonly Dictionary<string, int> tailIndex = new(StringComparer.Ordinal);
        /// <summary>Provider 树里遇到的路由标签（RouterProvider / Routes / BrowserRouter…）。</summary>
        private readonly List<(JsFile File, JsxNode El)> routeTags = [];
        /// <summary>挂载前配置里出现过的 client.setConfig / OpenAPI.X 赋值（请求封装的 Note 引用）。</summary>
        private readonly List<string> clientConfigSites = [];
        private string? providerSource;

        public Run(FrameworkInput input)
        {
            this.input = input;
            container = input.Container;
            src = input.Sources;
            home = (container.Path ?? container.Members.FirstOrDefault() ?? "").Trim('/');
            foreach (var s in input.Symbols) byId[s.Id] = s;
            childrenOf = input.Symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            var mine = input.Symbols.Where(s => s.Kind != SymbolKind.External && InContainer(s.Path)).ToList();
            foreach (var s in mine)
            {
                inside.Add(s.Id);
                if (s.Kind == SymbolKind.Module) modulesByPath[s.Path] = s;
            }
            symbolsByPath = mine.ToLookup(s => s.Path, StringComparer.Ordinal);
            foreach (var e in input.Edges)
            {
                if (e.Kind != EdgeKind.Calls || !inside.Contains(e.FromId)) continue;
                if (!callsFrom.TryGetValue(e.FromId, out var list)) callsFrom[e.FromId] = list = [];
                list.Add(e);
            }
        }

        private bool InContainer(string path) =>
            container.Members.Any(m => Under(path, m)) && !PipelineBuilder.IsTest(path, "") && !path.Contains("/node_modules/", StringComparison.Ordinal);

        public FrameworkFlowDto Build()
        {
            deps = ReadPackage();
            var hasMountEdge = MountEdgeFiles().Count > 0;
            var react = deps.ContainsKey("react") || deps.ContainsKey("preact") || hasMountEdge;
            var next = deps.ContainsKey("next");
            if (!react && !next)
            {
                var other = deps.ContainsKey("vue") || deps.ContainsKey("nuxt") ? "Vue" : deps.ContainsKey("@angular/core") ? "Angular"
                    : deps.ContainsKey("svelte") ? "Svelte" : deps.ContainsKey("solid-js") ? "Solid" : null;
                return Empty(container, [other is null
                    ? "没认出前端框架：package.json 里没有 react / next（或没找到 package.json），也没有 react-dom 的挂载调用"
                    : $"{other} 的框架流程还不认（package.json 里有 {other.ToLowerInvariant()}）：先只做了 React / Next.js"]);
            }
            notes.Add(RecognizedNote());

            var rootKids = new List<FlowNodeDto>();
            if (next) BuildNext(rootKids);
            else BuildSpa(rootKids);

            BuildRequest();
            return Assemble(rootKids, next);
        }

        // ================= 组装 =================

        private FrameworkFlowDto Assemble(List<FlowNodeDto> rootKids, bool next)
        {
            var pages = input.Entries.Where(e => e.ContainerKey == container.Key && e.Kind == "page" && !PipelineBuilder.IsTest(e.File, "")).ToList();
            var pageNote = pages.Count > 0
                ? $"{pages.Count} 个页面入口" + (next ? "（app/**/page.tsx 与 pages/）" : "") + "，点开看清单"
                : "入口识别没有登记页面（只认 Next.js 的 app/**/page.tsx 与 pages/；SPA 的路由表还不登记成入口），清单是空的";
            // 收尾的先后：响应拦截（每个请求回来就过）→ TanStack Query 全局 onError → 出错边界（渲染抛错才到）
            static int Rank(string key) => key.StartsWith("resp-", StringComparison.Ordinal) ? 0 : key.StartsWith("qc:", StringComparison.Ordinal) ? 1 : 2;
            var tail = tailIndex.OrderBy(p => Rank(p.Key)).ThenBy(p => p.Value).Select(p => tailRows[p.Value]).ToList();
            var pageNode = Node(null, "页面", "", "controllers", StageController, pageNote, [.. requestRows, .. tail], null, null, pages.Count);
            rootKids.Add(pageNode);
            if (pages.Count == 0) notes.Add("页面：" + pageNote);
            if (requestRows.Count == 0 && !notes.Any(n => n.StartsWith("没认出请求封装", StringComparison.Ordinal))) notes.Add("没认出请求封装：没有 axios.create / interceptors / 生成的 OpenAPI 客户端，也没有被 3 处以上调用的 fetch 封装函数");
            if (tailRows.Count == 0) notes.Add("没发现收尾：没有响应拦截器、TanStack Query 全局 onError、出错边界（ErrorBoundary / errorComponent / error.tsx）");

            var root = Node(null, "页面加载", "", "entry", rootKids[0].Actor, null, rootKids, null, null);

            var order = new List<string>();
            void Walk(FlowNodeDto n) { if (!order.Contains(n.Actor)) order.Add(n.Actor); foreach (var c in n.Children) Walk(c); }
            foreach (var c in rootKids) Walk(c);
            var stages = new List<FrameworkStageDto>();
            foreach (var key in order)
            {
                var symbols = key == StageController
                    ? pages.Select(e => byId.GetValueOrDefault(e.SymbolId)).Where(s => s is not null).Select(s => s!)
                    : stageSymbols.GetValueOrDefault(key) ?? [];
                stages.Add(new FrameworkStageDto(key, StageLabel(key), orderKnown.GetValueOrDefault(key, true), symbols.DistinctBy(s => s.Id).Select(Dto).ToList()));
            }
            return FrameworkFlowBuilder.Assemble(container, "页面加载", root, stages, [], notes, StageLabel(root.Actor));
        }

        private static string StageLabel(string key) => key switch
        {
            StageMount => "挂载",
            StageProvider => "Provider",
            StageRoute => "路由",
            StageController => "页面",
            StageRequest => "请求封装",
            StageTail => "收尾",
            _ => key,
        };

        private SymbolDto Dto(MapSymbol s) => src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);

        private void Record(string stage, MapSymbol s)
        {
            if (!stageSymbols.TryGetValue(stage, out var list)) stageSymbols[stage] = list = [];
            list.Add(s);
        }

        /// <summary>一行：仓库里的符号画成 call（可跳转、记进阶段的符号），没有符号的画成 external-group。</summary>
        private FlowNodeDto Row(string stage, MapSymbol? sym, string label, string? note, List<FlowNodeDto> kids, string? path, int? line, string? qn = null)
        {
            if (sym is not null && sym.Kind != SymbolKind.External) Record(stage, sym);
            var repo = sym is not null && sym.Kind != SymbolKind.External;
            return Node(repo ? sym!.Id : null, label, qn ?? sym?.QualifiedName ?? "", repo ? "call" : "external-group", stage, note, Cap(kids, stage), path, line);
        }

        private void AddTail(string key, FlowNodeDto row)
        {
            if (tailIndex.ContainsKey(key)) return;
            tailIndex[key] = tailRows.Count;
            tailRows.Add(row);
        }

        // ================= 取数小工具 =================

        private JsFile? File(string path)
        {
            if (files.TryGetValue(path, out var f)) return f;
            string? raw;
            try { raw = src.ReadFile(path); } catch { raw = null; }
            return files[path] = raw is null ? null : new JsFile(path, raw);
        }

        private Dictionary<string, (string Spec, bool Default, string Imported)> ImportsOf(JsFile f)
        {
            if (!importCache.TryGetValue(f.Path, out var map)) importCache[f.Path] = map = Imports(f);
            return map;
        }

        private string Join(string rel) => home.Length == 0 ? rel : home + "/" + rel;

        private string Rel(string path) => home.Length > 0 && path.StartsWith(home + "/", StringComparison.Ordinal) ? path[(home.Length + 1)..] : path;

        private static string FileName(string path) => MapSymbol.FileName(path);

        private string Where(JsFile f, int pos) => $"{FileName(f.Path)}:{f.LineOf(pos)}";

        private string Label(MapSymbol s) =>
            s.ParentId is { } p && byId.TryGetValue(p, out var o) && OutlineBuilder.IsType(o.Kind) ? o.Name + "." + s.Name : s.Name;

        private MapSymbol? DefaultExport(MapSymbol module) =>
            childrenOf[module.Id].Where(s => s.Visibility == "export default" && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable)
                .OrderBy(s => s.StartLine).FirstOrDefault();

        /// <summary>函数的调用大纲第一层（关键调用），exclude 里的名字（组件自己渲染的 JSX 标签）不要。</summary>
        private List<FlowNodeDto> CallRows(MapSymbol fn, string stage, IReadOnlySet<string>? exclude = null) =>
            Ordered(src.Calls(fn.Id)).Where(FrontKeep)
                .Where(c => exclude is null || !exclude.Contains(OwnerAndMember(c.QualifiedName).Member) && !exclude.Contains(OwnerAndMember(c.Label).Member))
                .Select(c => CallNode(c, fn.Path, stage, null)).ToList();

        /// <summary>包住 [s, e) 的最小函数 / 方法；没有返回 null（模块顶层）。</summary>
        private MapSymbol? Owner(JsFile f, int s, int e)
        {
            var a = f.LineOf(s);
            var b = f.LineOf(Math.Max(s, e - 1));
            return symbolsByPath[f.Path].Where(x => x.Kind is SymbolKind.Function or SymbolKind.Method && x.StartLine <= a && x.EndLine >= b)
                .OrderBy(x => x.EndLine - x.StartLine).FirstOrDefault();
        }

        /// <summary>[s, e) 里的关键调用：在函数里就取函数的调用大纲按位置截，在模块顶层就取模块的调用边。</summary>
        private List<FlowNodeDto> SpanRows(JsFile f, int s, int e, string stage)
        {
            var a = f.LineOf(s);
            var b = f.LineOf(Math.Max(s, e - 1));
            bool In(int line, int? pos) => pos is { } p ? p >= s && p < e : line >= a && line <= b;
            if (Owner(f, s, e) is { } owner)
                return Ordered(src.Calls(owner.Id)).Where(c => In(c.Line, c.Pos)).Where(FrontKeep).Select(c => CallNode(c, f.Path, stage, null)).ToList();
            if (!modulesByPath.TryGetValue(f.Path, out var module)) return [];
            var rows = new List<FlowNodeDto>();
            foreach (var edge in (callsFrom.GetValueOrDefault(module.Id) ?? []).Where(x => In(x.Line, x.Pos)).OrderBy(x => x.Line).ThenBy(x => x.Pos ?? 0))
            {
                if (!byId.TryGetValue(edge.ToId, out var t)) continue;
                if (t.Kind == SymbolKind.External)
                {
                    if (!KeyExternals.Contains(OwnerAndMember(t.QualifiedName).Member)) continue;
                    rows.Add(Node(null, t.Name, t.QualifiedName, "external-group", stage, null, [], f.Path, edge.Line));
                }
                else if (t.Kind is SymbolKind.Function or SymbolKind.Method or SymbolKind.Class)
                    rows.Add(Node(t.Id, Label(t), t.QualifiedName, "call", stage, null, [], f.Path, edge.Line));
            }
            return rows;
        }

        /// <summary>[s, e) 里调到的仓库函数（去重、按位置）。</summary>
        private List<MapSymbol> RepoCallees(JsFile f, int s, int e)
        {
            var a = f.LineOf(s);
            var b = f.LineOf(Math.Max(s, e - 1));
            bool In(int line, int? pos) => pos is { } p ? p >= s && p < e : line >= a && line <= b;
            IEnumerable<long> ids = Owner(f, s, e) is { } owner
                ? Flatten(src.Calls(owner.Id)).Where(c => In(c.Line, c.Pos) && c.TargetId is not null).Select(c => c.TargetId!.Value)
                : modulesByPath.TryGetValue(f.Path, out var module)
                    ? (callsFrom.GetValueOrDefault(module.Id) ?? []).Where(x => In(x.Line, x.Pos)).OrderBy(x => x.Line).ThenBy(x => x.Pos ?? 0).Select(x => x.ToId)
                    : [];
            return ids.Distinct().Select(id => byId.GetValueOrDefault(id)).Where(t => t is not null && inside.Contains(t.Id) && t.Kind is SymbolKind.Function or SymbolKind.Method).Select(t => t!).ToList();
        }

        /// <summary>
        /// 行的子节点里指向仓库函数、自己又没有子节点、而且里面确实在做出错处理（认得出状态码 / 跳转 / 清令牌 / 提示）的，再展开一层它的调用
        /// （出错拦截器里的 handleError → 401 分支）；记日志一类的辅助函数不展开。
        /// </summary>
        private List<FlowNodeDto> ExpandOnce(List<FlowNodeDto> rows, string stage) =>
            rows.Select(r => r.Children.Count == 0 && r.SymbolId is { } id && inside.Contains(id) && byId.TryGetValue(id, out var t) && t.Kind is SymbolKind.Function or SymbolKind.Method
                             && File(t.Path) is { } tf && DescribeErrors(ReachableTexts(tf, tf.Span(t.StartLine, t.EndLine).Start, tf.Span(t.StartLine, t.EndLine).End, 1)) is { } d && d != "再抛给调用方"
                ? r with { Children = Cap(CallRows(t, stage), stage) } : r).ToList();

        /// <summary>函数（或一段源码）以及它往下两层内调到的同仓库函数的原文：认出错处理的状态码与动作用。</summary>
        private List<string> ReachableTexts(JsFile f, int s, int e, int depth = 2)
        {
            var texts = new List<string> { f.Raw[s..e] };
            var seen = new HashSet<long>();
            var frontier = RepoCallees(f, s, e);
            for (var level = 0; level < depth && frontier.Count > 0 && seen.Count < 12; level++)
            {
                var next = new List<MapSymbol>();
                foreach (var t in frontier)
                {
                    if (!seen.Add(t.Id) || File(t.Path) is not { } tf) continue;
                    var (ts, te) = tf.Span(t.StartLine, t.EndLine);
                    texts.Add(tf.Raw[ts..te]);
                    next.AddRange(RepoCallees(tf, ts, te));
                }
                frontier = next;
            }
            return texts;
        }

        /// <summary>按名字找仓库里的组件 / 函数：本文件里调用边的目标 → 本文件里的定义 → import 进来的模块。</summary>
        private MapSymbol? Resolve(JsFile f, string name)
        {
            if (name.Length == 0 || name.Contains('.')) return null;
            foreach (var s in symbolsByPath[f.Path])
                foreach (var e in callsFrom.GetValueOrDefault(s.Id) ?? [])
                    if (byId.TryGetValue(e.ToId, out var t) && t.Kind != SymbolKind.External && inside.Contains(t.Id) && t.Name == name && t.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable or SymbolKind.Method)
                        return t;
            if (symbolsByPath[f.Path].FirstOrDefault(s => s.Name == name && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable && s.ParentId is { } p && byId.TryGetValue(p, out var o) && o.Kind == SymbolKind.Module) is { } local)
                return local;
            if (!ImportsOf(f).TryGetValue(name, out var imp) || ResolveModule(f.Path, imp.Spec) is not { } module) return null;
            return imp.Default ? DefaultExport(module) : childrenOf[module.Id].FirstOrDefault(s => s.Name == imp.Imported && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable);
        }

        /// <summary>import 说明符 → 仓库里的模块：相对路径、@/ 与 ~/（指 src/ 或容器根）。包名返回 null。</summary>
        private MapSymbol? ResolveModule(string fromPath, string spec)
        {
            var bases = new List<string>();
            if (spec.StartsWith('.'))
            {
                var dir = fromPath.Contains('/') ? fromPath[..fromPath.LastIndexOf('/')] : "";
                var parts = (dir.Length == 0 ? [] : dir.Split('/')).ToList();
                foreach (var seg in spec.Split('/'))
                {
                    if (seg == ".") continue;
                    if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                    parts.Add(seg);
                }
                bases.Add(string.Join('/', parts));
            }
            else if (spec.StartsWith("@/", StringComparison.Ordinal) || spec.StartsWith("~/", StringComparison.Ordinal))
            {
                bases.Add(Join("src/" + spec[2..]));
                bases.Add(Join(spec[2..]));
            }
            else return null;
            foreach (var b in bases)
            {
                if (modulesByPath.TryGetValue(b, out var exact)) return exact;
                foreach (var ext in ScriptExtensions)
                {
                    if (modulesByPath.TryGetValue(b + ext, out var m)) return m;
                    if (modulesByPath.TryGetValue(b + "/index" + ext, out var idx)) return idx;
                }
            }
            return null;
        }

        /// <summary>本文件里调用边指向的外部符号（JSX 标签的限定名：external:@tanstack/react-query.QueryClientProvider）。</summary>
        private string ExternalQn(JsFile f, string name)
        {
            foreach (var s in symbolsByPath[f.Path])
                foreach (var e in callsFrom.GetValueOrDefault(s.Id) ?? [])
                    if (byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External && t.Name == name) return t.QualifiedName;
            return ImportsOf(f).TryGetValue(name, out var imp) ? $"external:{imp.Spec}.{imp.Imported}" : name;
        }

        private string? LibraryOf(JsFile f, string name) => ImportsOf(f).TryGetValue(name.Split('.')[0], out var imp) && !imp.Spec.StartsWith('.') && !imp.Spec.StartsWith("@/", StringComparison.Ordinal) && !imp.Spec.StartsWith("~/", StringComparison.Ordinal) ? imp.Spec : null;

        // ================= package.json 与识别 =================

        private Dictionary<string, string> ReadPackage()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var dir in new[] { home }.Concat(container.Members).Distinct())
            {
                string? raw;
                try { raw = src.ReadFile(dir.Length == 0 ? "package.json" : dir.Trim('/') + "/package.json"); } catch { raw = null; }
                if (raw is null) continue;
                foreach (Match m in Regex.Matches(raw, @"""(?:dependencies|devDependencies|peerDependencies)""\s*:\s*\{"))
                {
                    var open = m.Index + m.Length - 1;
                    var close = raw.IndexOf('}', open);
                    if (close < 0) continue;
                    foreach (Match d in Regex.Matches(raw[open..close], @"""(?<n>[^""]+)""\s*:\s*""(?<v>[^""]*)"""))
                        result.TryAdd(d.Groups["n"].Value, d.Groups["v"].Value);
                }
                if (result.Count > 0) break;
            }
            return result;
        }

        private string RecognizedNote()
        {
            var parts = new List<string>();
            if (deps.TryGetValue("react", out var rv) && Regex.Match(rv, @"\d+") is { Success: true } major) parts.Add($"React {major.Value}（react {rv}）");
            else if (deps.ContainsKey("react")) parts.Add("React");
            foreach (var (dep, name) in KnownLibraries)
                if (deps.ContainsKey(dep) && !parts.Contains(name)) parts.Add(name);
            return parts.Count == 0 ? "没读到 package.json 的依赖：只按源码认" : $"认出：{string.Join("、", parts)}（package.json）";
        }

        private string ReactVersion() => deps.TryGetValue("react", out var v) && Regex.Match(v, @"\d+") is { Success: true } m ? " " + m.Value : "";

        /// <summary>调了 react-dom 的 createRoot / hydrateRoot / render 的文件。</summary>
        private List<string> MountEdgeFiles()
        {
            var paths = new List<string>();
            foreach (var (from, list) in callsFrom)
                foreach (var e in list)
                    if (byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External && t.QualifiedName.StartsWith("external:react-dom", StringComparison.Ordinal)
                        && MountFunctions.Contains(t.Name) && byId.TryGetValue(from, out var fs) && !paths.Contains(fs.Path))
                        paths.Add(fs.Path);
            return paths;
        }

        // ================= React SPA：挂载 → Provider → 路由 =================

        private void BuildSpa(List<FlowNodeDto> rootKids)
        {
            var mount = FindMount();
            if (mount is null)
                notes.Add("没找到挂载点：入口文件（main.tsx / index.tsx…）里没有 createRoot(...).render / hydrateRoot / ReactDOM.render");
            else
            {
                var (f, mountPos, label, api, jsxAt) = mount.Value;
                var kids = PreMountRows(f, StatementStart(f.S, mountPos));
                var target = MountTargetOf(f, mountPos);
                kids.Add(Node(null, label, "external:react-dom." + api, "external-group", StageMount, "挂载：从这里开始渲染下面的 Provider 树", [], f.Path, f.LineOf(mountPos)));
                var module = modulesByPath.GetValueOrDefault(f.Path);
                var apiNote = api switch
                {
                    "createRoot" => "createRoot（React 18+）",
                    "hydrateRoot" => "hydrateRoot（服务端渲染后水合）",
                    _ => $"ReactDOM.{api}（React 17 及以前的旧 API）",
                };
                mountRows.Add(Row(StageMount, module, FileName(f.Path), $"应用入口：React{ReactVersion()} {apiNote}（{Where(f, mountPos)}）" + (target is null ? "" : $"挂到 {target}"), kids, f.Path, module is null ? null : 1));

                if (jsxAt >= 0 && ParseJsx(f.S, jsxAt) is { } jsx)
                {
                    providerSource = FileName(f.Path);
                    Visit(f, jsx, true, 0, null, false);
                }
                else notes.Add($"挂载调用（{Where(f, mountPos)}）的参数里没解析出 JSX");
            }
            if (providerRows.Count > 0)
                notes.Add($"Provider 顺序 = {providerSource} 里挂载的 JSX 由外到内的嵌套；仓库里的包装组件进去接着找（最多 {MaxExpandLevel} 层），里面的标「经 X」");
            else if (mount is not null) notes.Add("挂载的 JSX 里没有包住页面的 Provider");

            BuildRoutes();
            rootKids.AddRange(mountRows);
            rootKids.AddRange(providerRows);
            rootKids.AddRange(routeRows);
        }

        /// <summary>入口文件与挂载调用：(文件, 挂载调用的位置, 行的名字, API, JSX 的 '&lt;' 位置)。</summary>
        private (JsFile File, int Pos, string Label, string Api, int JsxAt)? FindMount()
        {
            var candidates = MountEdgeFiles()
                .Concat(modulesByPath.Keys.Where(p => EntryFileNames.Contains(FileName(p))).OrderBy(p => p.Contains("/src/", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(p => p.Count(c => c == '/')).ThenBy(p => Array.IndexOf(EntryFileNames, FileName(p))))
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (var path in candidates)
            {
                if (File(path) is not { } f) continue;
                var modern = ModernMount().Match(f.S);
                if (modern.Success)
                {
                    var api = modern.Groups["fn"].Value;
                    var open = modern.Index + modern.Length - 1;
                    var close = MatchAny(f.S, open);
                    if (close < 0) continue;
                    var target = MountTargetOf(f, modern.Index);
                    if (api == "hydrateRoot")
                        return (f, modern.Index, $"hydrateRoot({target ?? "…"})", api, JsxIn(f.S, open + 1, close).FirstOrDefault()?.Pos - 1 ?? -1);
                    // createRoot(el).render(<App />) 或 const root = createRoot(el); root.render(<App />)
                    var render = Regex.Match(f.S[(close + 1)..], @"^\s*\.\s*render\s*\(");
                    var renderOpen = render.Success ? close + 1 + render.Length - 1 : -1;
                    if (renderOpen < 0 && AssignedVariable(f.S, modern.Index) is { } v)
                    {
                        var m = new Regex(@"(?<![\w$.])" + Regex.Escape(v) + @"\s*\.\s*render\s*\(").Match(f.S, close);
                        if (m.Success) renderOpen = m.Index + m.Length - 1;
                    }
                    var jsxAt = -1;
                    if (renderOpen >= 0 && MatchAny(f.S, renderOpen) is var rc and > 0 && JsxIn(f.S, renderOpen + 1, rc).FirstOrDefault() is { } j) jsxAt = j.Pos - 1;
                    return (f, modern.Index, $"createRoot({target ?? "…"}).render", api, jsxAt);
                }
                var legacy = LegacyMount().Match(f.S);
                if (legacy.Success && (legacy.Groups["ns"].Success || ImportsOf(f).TryGetValue(legacy.Groups["fn"].Value, out var imp) && imp.Spec.StartsWith("react-dom", StringComparison.Ordinal)))
                {
                    var lt = f.S.IndexOf('<', legacy.Index);
                    var api = legacy.Groups["fn"].Value;
                    return (f, legacy.Index, $"ReactDOM.{api}", api, lt);
                }
            }
            return null;
        }

        private static string? AssignedVariable(string s, int pos)
        {
            var lineStart = s.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
            var m = Regex.Match(s[lineStart..pos], @"(?:const|let|var)\s+(?<n>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*$");
            return m.Success ? m.Groups["n"].Value : null;
        }

        /// <summary>挂载调用实参里的 getElementById("root") / querySelector("#app")（旧 API 的元素在最后一个实参）。</summary>
        private static string? MountTargetOf(JsFile f, int pos)
        {
            var open = f.S.IndexOf('(', pos);
            var close = open < 0 ? -1 : MatchAny(f.S, open);
            var end = close < 0 ? Math.Min(f.Raw.Length, pos + 200) : close;
            var m = MountTarget().Match(f.Raw, pos, end - pos);
            if (!m.Success) return null;
            var id = m.Groups["id"].Value;
            return id.StartsWith('#') || id.StartsWith('.') ? id : "#" + id;
        }

        /// <summary>pos 所在语句的起点（行首，跳过前面的 const x = ）：挂载前的配置只算这之前的。</summary>
        private static int StatementStart(string s, int pos) => s.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;

        /// <summary>挂载之前：副作用导入、模块顶层调用、OpenAPI.X / axios.defaults 赋值（按位置）。</summary>
        private List<FlowNodeDto> PreMountRows(JsFile f, int before)
        {
            var rows = new List<(int Pos, FlowNodeDto Node)>();
            foreach (Match m in SideEffectImport().Matches(f.S))
            {
                if (m.Index >= before || f.StringAt(m.Groups["q"].Index) is not { } spec || StyleImport().IsMatch(spec)) continue;
                var module = ResolveModule(f.Path, spec);
                rows.Add((m.Index, Row(StageMount, module, $"import \"{spec}\"", "副作用导入：模块加载时就执行（i18n 初始化、polyfill、全局配置……）", [], f.Path, f.LineOf(m.Index), module is null ? "external:" + spec : null)));
            }
            foreach (var tc in TopLevelCalls(f.S, 0, before))
            {
                var name = tc.Callee.Replace("new ", "").Split('.')[^1];
                if (MountFunctions.Contains(name)) continue;
                var line = f.LineOf(tc.Pos);
                var target = modulesByPath.TryGetValue(f.Path, out var module)
                    ? (callsFrom.GetValueOrDefault(module.Id) ?? []).Where(e => e.Line == line).Select(e => byId.GetValueOrDefault(e.ToId)).FirstOrDefault(t => t is not null && t.Name == name)
                    : null;
                var note = TopCallNote(f, tc);
                if (name == "setConfig" || tc.Callee.StartsWith("OpenAPI", StringComparison.Ordinal)) clientConfigSites.Add($"{Where(f, tc.Pos)} {tc.Callee}（{ObjectKeys(f, tc.ArgStart, tc.ArgEnd) ?? "…"}）");
                var repo = target is not null && target.Kind != SymbolKind.External && inside.Contains(target.Id);
                rows.Add((tc.Pos, Row(StageMount, repo ? target : null, tc.Callee, note, repo && target!.Kind is SymbolKind.Function or SymbolKind.Method ? CallRows(target, StageMount) : [],
                    f.Path, line, repo ? null : target?.QualifiedName ?? tc.Callee)));
            }
            foreach (Match m in GlobalAssign().Matches(f.S))
            {
                if (m.Index >= before) continue;
                var lhs = Regex.Replace(m.Groups["lhs"].Value, @"\s+", "");
                clientConfigSites.Add($"{Where(f, m.Index)} {lhs}");
                rows.Add((m.Index, Node(null, lhs + " = …", lhs, "external-group", StageMount,
                    lhs.StartsWith("OpenAPI", StringComparison.Ordinal) ? "生成的 OpenAPI 客户端全局配置：之后每个请求都带上" : "axios 全局默认值：之后每个请求都带上", [], f.Path, f.LineOf(m.Index))));
            }
            return rows.OrderBy(r => r.Pos).Select(r => r.Node).ToList();
        }

        private string TopCallNote(JsFile f, TopCall tc)
        {
            var name = tc.Callee.Replace("new ", "").Split('.')[^1];
            var what = name switch
            {
                "setConfig" => "请求客户端全局配置",
                "QueryClient" => "TanStack Query 客户端",
                "createRouter" => "TanStack Router 路由实例",
                "createBrowserRouter" or "createHashRouter" or "createMemoryRouter" => "React Router 路由表",
                "init" when Regex.IsMatch(tc.Callee, "i18n", RegexOptions.IgnoreCase) => "i18n 初始化",
                "init" => "初始化",
                "configureStore" or "createStore" => "Redux store",
                "start" when tc.Callee.Contains("worker", StringComparison.Ordinal) => "MSW 模拟接口",
                _ => null,
            };
            var keys = ObjectKeys(f, tc.ArgStart, tc.ArgEnd);
            var parts = new List<string?> { what, tc.AssignedTo is null ? null : $"{tc.AssignedTo} = …", keys is null ? null : "配置：" + keys, "挂载前执行" };
            return string.Join("；", parts.Where(p => p is not null));
        }

        /// <summary>实参是对象字面量时的顶层键（baseURL、auth）。</summary>
        private static string? ObjectKeys(JsFile f, int argStart, int argEnd)
        {
            var open = FrameworkFlowBuilder.SkipWs(f.S, argStart);
            if (open >= argEnd || f.S[open] != '{') return null;
            var close = MatchAny(f.S, open);
            if (close < 0) return null;
            var keys = ObjectEntries(f.S, open, close).Select(e => e.Key).ToList();
            return keys.Count == 0 ? null : string.Join("、", keys.Take(8)) + (keys.Count > 8 ? "…" : "");
        }

        // ---- Provider 树 ----

        private static readonly HashSet<string> RouterWrappers = new(StringComparer.Ordinal) { "BrowserRouter", "HashRouter", "MemoryRouter", "Router", "StaticRouter", "unstable_HistoryRouter" };
        private static readonly HashSet<string> RouteTables = new(StringComparer.Ordinal) { "RouterProvider", "Routes", "Switch" };
        private readonly HashSet<long> expanded = new();

        private static bool AppLike(string name) => name is "App" or "Root" or "Main" || name.EndsWith("App", StringComparison.Ordinal) || name.EndsWith("Root", StringComparison.Ordinal);

        private static bool IsErrorBoundary(string name) => name == "ErrorBoundary" || name.EndsWith("ErrorBoundary", StringComparison.Ordinal) && !name.Contains("Provider", StringComparison.Ordinal);

        /// <summary>
        /// JSX 元素：宿主元素、片段与组件库的 UI 组件（Box / Stack）穿过去；路由标签记下来给路由阶段；包着东西的 Provider 类组件（名字像 Provider / Boundary / Adapter…，或仓库里的组件）
        /// 画一行，仓库里的再进去（最多两层）；不包东西的组件只有"独子或像根组件（App）且在仓库里"才进去。子元素里有带 {children} 的就只走那条路（其余写进 Note）。
        /// ownImpl：在展开的仓库组件里，它自己的 X.Provider / XContext 不算。
        /// </summary>
        private void Visit(JsFile f, JsxNode el, bool sole, int level, string? via, bool ownImpl)
        {
            var name = el.Name;
            var pass = el.IsHost || ownImpl && (name.EndsWith(".Provider", StringComparison.Ordinal) || name.EndsWith("Context", StringComparison.Ordinal));
            MapSymbol? sym = null;
            if (!pass)
            {
                if (RouteTables.Contains(name)) { routeTags.Add((f, el)); return; }
                if (RouterWrappers.Contains(name))
                {
                    routeTags.Add((f, el));
                    routeRows.Add(Row(StageRoute, null, name, $"React Router 路由容器（{Where(f, el.Pos)}）：提供 history 与路由上下文" + (via is null ? "" : $"；经 {via}"), [], f.Path, f.LineOf(el.Pos), ExternalQn(f, name)));
                    VisitChildren(f, el, level, via, ownImpl);
                    return;
                }
                sym = Resolve(f, name);
                if (sym is not null && !(inside.Contains(sym.Id) && sym.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable)) sym = null;
                if (el.Wraps && sym is null && !ProviderLike().IsMatch(name)) pass = true;
            }
            if (pass) { VisitChildren(f, el, level, via, ownImpl); return; }
            if (el.Wraps)
            {
                var path = OnPath(el);
                var siblings = el.Children.Where(c => !c.IsHost && !RouteTables.Contains(c.Name) && !RouterWrappers.Contains(c.Name)
                        && (!path.Contains(c) || !c.Wraps && !WouldExpand(f, c, path.Count == 1, level)))
                    .Select(c => c.Name).Distinct().ToList();
                AddProvider(f, el, sym, level, via, siblings);
                if (sym is not null && level < MaxExpandLevel && expanded.Add(sym.Id)) Expand(sym, level + 1, name, true);
                foreach (var c in path) Visit(f, c, path.Count == 1, level, via, ownImpl);
                return;
            }
            if (sym is not null && WouldExpand(f, el, sole, level) && expanded.Add(sym.Id))
            {
                AddProvider(f, el, sym, level, via, [], "根组件");
                Expand(sym, level + 1, name, false);
            }
        }

        private void VisitChildren(JsFile f, JsxNode el, int level, string? via, bool ownImpl)
        {
            var path = OnPath(el);
            var sole = path.Count(c => !c.IsHost) <= 1;
            foreach (var c in path) Visit(f, c, sole, level, via, ownImpl);
        }

        /// <summary>子元素里有带 {children} 的就只走那几个（页面走的那条路），否则全走。</summary>
        private static List<JsxNode> OnPath(JsxNode el) => el.Children.Any(c => c.HasChildrenSlot) ? el.Children.Where(c => c.HasChildrenSlot).ToList() : el.Children;

        private bool WouldExpand(JsFile f, JsxNode el, bool sole, int level) =>
            !el.IsHost && level < MaxExpandLevel && (sole || AppLike(el.Name)) && Resolve(f, el.Name) is { } s && inside.Contains(s.Id) && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable;

        /// <summary>仓库里的组件：找它返回的 JSX（类组件找 render 方法）接着走。包装组件（requireSlot）只在 JSX 里有 {children} 时才走——没有说明页面不在它的 JSX 里（ErrorBoundary 的兜底界面）。</summary>
        private void Expand(MapSymbol sym, int level, string via, bool requireSlot)
        {
            if (ComponentJsx(sym) is not { } cj || requireSlot && !cj.Jsx.HasChildrenSlot) return;
            Visit(cj.File, cj.Jsx, true, level, via, true);
        }

        private (JsFile File, JsxNode Jsx)? ComponentJsx(MapSymbol sym)
        {
            if (File(sym.Path) is not { } f) return null;
            var body = sym;
            if (sym.Kind == SymbolKind.Class && childrenOf[sym.Id].FirstOrDefault(c => c.Name == "render") is { } render) body = render;
            var (s, e) = f.Span(body.StartLine, body.EndLine);
            return ReturnedJsx(f, s, e) is { } jsx ? (f, jsx) : null;
        }

        private static IEnumerable<string> TagNames(JsxNode n) => n.Children.SelectMany(TagNames).Prepend(n.Name).Where(x => x.Length > 0);

        private void AddProvider(JsFile f, JsxNode el, MapSymbol? sym, int level, string? via, List<string> siblings, string? role = null)
        {
            var name = el.Name;
            var repo = sym is not null && inside.Contains(sym.Id);
            var lib = repo ? null : LibraryOf(f, name);
            var parts = new List<string?> { role, Describe(name, lib, AttrNames(f, el)) };
            foreach (var a in new[] { "client", "store", "router", "theme", "session", "messages", "value", "i18n", "config" })
                if (Attr(f, el, a) is { } v && v.Text.Length > 0) parts.Add($"{a} = {Short(v.Text)}");
            if (lib is not null) parts.Add(lib);
            if (via is not null) parts.Add($"经 {via}");
            parts.Add(Where(f, el.Pos));
            if (siblings.Count > 0) parts.Add($"里面还挂着 {string.Join("、", siblings.Take(5))}（不包页面的全局组件，没画）");
            var kids = new List<FlowNodeDto>();
            if (name == "QueryClientProvider") kids.AddRange(QueryClientRows(f, el));
            if (repo && sym!.Kind is SymbolKind.Function or SymbolKind.Method)
            {
                var own = ComponentJsx(sym) is { } cj ? TagNames(cj.Jsx).ToHashSet(StringComparer.Ordinal) : [];
                kids.AddRange(CallRows(sym, StageProvider, own));
            }
            if (IsErrorBoundary(name))
                AddTail("eb:" + f.Path + ":" + el.Pos, Row(StageTail, repo ? sym : null, name, $"渲染出错时：{name}（{Where(f, el.Pos)}）显示兜底界面", [], f.Path, f.LineOf(el.Pos), repo ? null : ExternalQn(f, name)));
            // 仓库里的组件、名字不像 Provider、自己也不渲染 Provider / Context：不是 Provider——会跳转的是守卫（路由列），其余是布局容器（挂载列）
            var stage = StageProvider;
            if (repo && role is null && !ProviderLike().IsMatch(name) && sym!.Kind is SymbolKind.Function or SymbolKind.Method or SymbolKind.Class && !RendersProvider(sym))
            {
                var guard = File(sym.Path) is { } sf && sf.Span(sym.StartLine, sym.EndLine) is var (gs, ge)
                    ? GuardNote(sf, gs, ge) ?? (NavigateCall().Match(sf.Raw, gs, Math.Max(0, Math.Min(sf.Raw.Length, ge) - gs)) is { Success: true } nm ? $"守卫 / 跳转：条件不满足时 {Regex.Replace(nm.Value, @"\s+", "")}…（{Where(sf, nm.Index)}），否则渲染子树" : null)
                    : null;
                if (guard is not null || GuardName().IsMatch(name)) { stage = StageRoute; parts.Insert(0, (guard ?? "守卫组件") + "（不是 Provider：不渲染 Context.Provider）"); }
                else { stage = StageMount; parts.Insert(0, "布局容器（不是 Provider：不渲染 Context.Provider）"); }
                kids = kids.Select(k => k with { Actor = k.Actor == StageProvider ? stage : k.Actor }).ToList();
            }
            providerRows.Add(Row(stage, repo ? sym : null, name, string.Join("；", parts.Where(p => !string.IsNullOrEmpty(p))), kids, f.Path, f.LineOf(el.Pos), repo ? null : ExternalQn(f, name)));
        }

        /// <summary>组件返回的 JSX 里有没有 Provider 一类的标签（X.Provider、*Provider、*Context）——真 Provider 自己会渲染一个。</summary>
        private bool RendersProvider(MapSymbol sym) =>
            ComponentJsx(sym) is { } cj && TagNames(cj.Jsx).Skip(1).Concat(cj.Jsx.Name.Length > 0 ? [cj.Jsx.Name] : [])
                .Any(n => n != sym.Name && (n.EndsWith(".Provider", StringComparison.Ordinal) || n.EndsWith("Provider", StringComparison.Ordinal) || n.EndsWith("Context", StringComparison.Ordinal)));

        private static string Short(string text, int max = 40)
        {
            var t = Regex.Replace(text, @"\s+", " ").Trim();
            return t.Length <= max ? t : t[..(max - 1)] + "…";
        }

        /// <summary>常见 Provider 的说明（名字 + 来自的库）。</summary>
        private static string? Describe(string name, string? lib, List<string> attrs) => name switch
        {
            "StrictMode" or "React.StrictMode" => "React 严格模式（只在开发期生效：组件渲染两次、检查过时 API）",
            "Suspense" or "React.Suspense" => "Suspense：懒加载 / 数据没就绪时先显示 fallback",
            "QueryClientProvider" => "TanStack Query：提供查询缓存",
            "Provider" when lib?.Contains("redux", StringComparison.Ordinal) == true || attrs.Contains("store") => "Redux：提供 store",
            "Provider" when lib == "jotai" => "Jotai：原子状态的 store",
            "ThemeProvider" or "MantineProvider" or "ChakraProvider" or "CssVarsProvider" => "主题",
            "ConfigProvider" => "组件库全局配置",
            "I18nextProvider" or "IntlProvider" or "NextIntlClientProvider" => "国际化（文案 / 语言）",
            "SessionProvider" => "登录会话",
            "SnackbarProvider" or "ToastProvider" or "NotificationsProvider" => "消息提示",
            "HelmetProvider" => "页面 head 管理",
            "LocalizationProvider" => "日期组件本地化",
            "AppRouterCacheProvider" => "MUI 样式缓存（Next App Router）",
            "ApolloProvider" => "Apollo GraphQL 客户端",
            "NuqsAdapter" => "URL 查询参数状态（nuqs）",
            _ when IsErrorBoundary(name) => "出错边界：子树渲染抛错时显示兜底界面（见收尾）",
            _ when name.EndsWith(".Provider", StringComparison.Ordinal) => "Context 提供值",
            _ => null,
        };

        // ================= Next.js：中间件 → 布局 → Provider =================

        private void BuildNext(List<FlowNodeDto> rootKids)
        {
            // 中间件（middleware.ts；Next 16 改名 proxy.ts）
            foreach (var rel in new[] { "middleware", "proxy", "src/middleware", "src/proxy" })
            {
                var module = ScriptExtensions.Select(ext => modulesByPath.GetValueOrDefault(Join(rel + ext))).FirstOrDefault(m => m is not null);
                if (module is null || File(module.Path) is not { } f) continue;
                var fn = childrenOf[module.Id].FirstOrDefault(s => s.Kind == SymbolKind.Function && (s.Visibility == "export default" || s.Visibility == "export" && s.Name is "middleware" or "proxy"));
                var matcher = Regex.Match(f.Raw, @"matcher\s*:\s*(?<m>\[[^\]]*\]|[""'][^""']*[""'])");
                var parts = new List<string> { $"Next 中间件（{FileName(f.Path)}）：每个请求先在服务端过它，再渲染布局" };
                if (matcher.Success) parts.Add("matcher = " + Short(Regex.Replace(matcher.Groups["m"].Value, @"//[^\n]*|/\*[\s\S]*?\*/", " "), 60));
                if (fn is not null)
                {
                    var (s, e) = f.Span(fn.StartLine, fn.EndLine);
                    if (GuardNote(f, s, e) is { } g) parts.Add(g);
                }
                else if (Regex.Match(f.S, @"export\s+default\s+(?<fn>[A-Za-z_$][\w$]*)\s*\(") is { Success: true } wrapped) parts.Add($"默认导出 {wrapped.Groups["fn"].Value}(…) 包出来的中间件");
                routeRows.Add(Row(StageRoute, fn ?? module, fn is null ? FileName(f.Path) : $"{fn.Name}（{FileName(f.Path)}）", string.Join("；", parts), fn is null ? [] : CallRows(fn, StageRoute), f.Path, fn?.StartLine ?? 1));
                notes.Add($"路由：Next.js 中间件 {Rel(f.Path)} 排在最前（服务端，渲染布局之前执行）");
                break;
            }

            // 布局链：App Router 的 layout，Pages Router 的 _app
            var appRoot = new[] { "app", "src/app" }.Select(Join).FirstOrDefault(d => ScriptExtensions.Any(ext => modulesByPath.ContainsKey(d + "/layout" + ext)));
            var pages = input.Entries.Where(e => e.ContainerKey == container.Key && e.Kind == "page").Select(e => e.File).ToList();
            var layoutChain = new List<MapSymbol>();
            if (appRoot is not null)
            {
                var layouts = modulesByPath.Values.Where(m => Under(m.Path, appRoot) && Regex.IsMatch(FileName(m.Path), @"^layout\.(tsx|jsx|ts|js)$")).ToList();
                var dirOf = layouts.ToDictionary(m => m.Id, m => m.Path[..m.Path.LastIndexOf('/')]);
                var cover = layouts.ToDictionary(m => m.Id, m => pages.Count(p => Under(p, dirOf[m.Id])));
                layoutChain = layouts.Where(m => dirOf[m.Id] == appRoot || pages.Count > 0 && cover[m.Id] * 2 >= pages.Count)
                    .OrderBy(m => dirOf[m.Id].Count(c => c == '/')).ToList();
                var others = layouts.Count - layoutChain.Count;
                notes.Add($"挂载：Next.js App Router 没有 createRoot，框架按 {Rel(appRoot)}/ 下的 layout 由外到内渲染；画了包住至少一半页面的 {layoutChain.Count} 层布局"
                          + (others > 0 ? $"（另有 {others} 个局部布局只包少数页面，没画）" : ""));
                // 没画的局部布局里套着守卫（<AdminPermissionGuard category=… permission=…> 包住 children）：看不见就以为那些页面没有权限检查，列出来
                var deepest = layoutChain.Count > 0 ? dirOf[layoutChain[^1].Id] : appRoot;
                var guarded = new List<(string Prefix, string Guard, int Pages)>();
                foreach (var l in layouts.Except(layoutChain).OrderBy(m => m.Path, StringComparer.Ordinal))
                    if (LocalLayoutGuard(l) is { } g)
                    {
                        var dir = dirOf[l.Id];
                        var prefix = Under(dir, deepest) && dir != deepest ? "…/" + dir[(deepest.Length + 1)..] : Rel(dir);
                        guarded.Add((prefix, g, cover[l.Id]));
                    }
                if (guarded.Count > 0)
                    notes.Add($"守卫：{guarded.Count} 个局部布局带守卫，共包住 {guarded.Sum(g => g.Pages)} 个页面（只管它下面的路由，没画进骨架）："
                              + string.Join("；", guarded.Select(g => $"{g.Prefix}（{g.Guard}，{g.Pages} 个页面）")));
                foreach (var layout in layoutChain) AddLayout(layout, pages, dirOf[layout.Id]);
                // 出错边界：链上目录里的 error / global-error / not-found
                var chainDirs = layoutChain.Select(l => dirOf[l.Id]).ToList();
                foreach (var m in modulesByPath.Values.Where(m => Under(m.Path, appRoot)).OrderBy(m => m.Path.Count(c => c == '/')).ThenBy(m => m.Path, StringComparer.Ordinal))
                {
                    var file = FileName(m.Path);
                    var stem = file.Contains('.') ? file[..file.IndexOf('.')] : file;
                    if (stem is not ("error" or "global-error" or "not-found")) continue;
                    var dir = m.Path[..m.Path.LastIndexOf('/')];
                    if (!chainDirs.Contains(dir)) continue;
                    var fn = DefaultExport(m) ?? m;
                    var what = stem switch
                    {
                        "global-error" => "根布局自己也抛错时的兜底（替换整个 html）",
                        "error" => "这一层以下的页面 / 布局渲染抛错时显示（Next 出错边界）",
                        _ => "notFound() 或没匹配到路由时显示",
                    };
                    AddTail("next:" + m.Path, Row(StageTail, fn, $"{file}（{Rel(dir)}）", what, fn.Kind == SymbolKind.Module ? [] : CallRows(fn, StageTail), m.Path, fn.StartLine));
                }
            }
            else
            {
                var app = new[] { "pages/_app", "src/pages/_app" }.SelectMany(p => ScriptExtensions.Select(ext => Join(p + ext))).Select(p => modulesByPath.GetValueOrDefault(p)).FirstOrDefault(m => m is not null);
                if (app is not null)
                {
                    notes.Add($"挂载：Next.js Pages Router，每个页面都包在 {Rel(app.Path)} 里");
                    AddLayout(app, pages, app.Path[..app.Path.LastIndexOf('/')]);
                }
                else notes.Add("挂载：Next.js 项目里没找到 app/layout 或 pages/_app");
            }
            if (mountRows.Any(r => r.Actor == StageProvider)) notes.Add($"Provider 顺序 = 各层布局返回的 JSX 由外到内的嵌套（夹在布局之间：下一层布局渲染在上一层的 Provider 里面）；仓库里的包装组件进去接着找（最多 {MaxExpandLevel} 层），里面的标「经 X」");
            if (routeRows.Count == 0) notes.Add("路由：Next.js 按 app/ 目录约定路由，没有 middleware.ts / proxy.ts");
            rootKids.AddRange(routeRows);
            rootKids.AddRange(mountRows);
        }

        /// <summary>局部布局里的守卫：JSX 里名字像守卫的组件（AdminPermissionGuard category="Profile" permission="Read"），或函数体里的跳转守卫；没有返回 null。</summary>
        private string? LocalLayoutGuard(MapSymbol module)
        {
            if (File(module.Path) is not { } f || DefaultExport(module) is not { } fn) return null;
            if (ComponentJsx(fn) is { } cj)
            {
                JsxNode? Find(JsxNode n) => !n.IsHost && n.Name.Length > 0 && GuardName().IsMatch(n.Name) && !n.Name.EndsWith("Provider", StringComparison.Ordinal) ? n : n.Children.Select(Find).FirstOrDefault(x => x is not null);
                if (Find(cj.Jsx) is { } g)
                {
                    var attrs = new List<string>();
                    foreach (var a in AttrNames(cj.File, g).Take(4))
                        if (Attr(cj.File, g, a) is { } v) attrs.Add($"{a}={Short(v.Text.Trim('"', '\'', '{', '}'), 24)}");
                    return $"<{g.Name}{(attrs.Count > 0 ? " " + string.Join(" ", attrs) : "")}>";
                }
            }
            var (s, e) = f.Span(fn.StartLine, fn.EndLine);
            return GuardNote(f, s, e);
        }

        /// <summary>一层布局：布局行（挂载列，子节点是它的关键调用，守卫画进路由列），后面跟着它 JSX 里的 Provider。</summary>
        private void AddLayout(MapSymbol module, List<string> pages, string dir)
        {
            if (File(module.Path) is not { } f) return;
            var fn = DefaultExport(module);
            var covered = pages.Count(p => Under(p, dir));
            var note = $"布局 {Rel(module.Path)}" + (pages.Count > 0 ? $"：包住 {covered}/{pages.Count} 个页面" : "");
            var kids = new List<FlowNodeDto>();
            var cj = fn is null ? null : ComponentJsx(fn);
            if (fn is not null)
            {
                var (s, e) = f.Span(fn.StartLine, fn.EndLine);
                if (GuardNote(f, s, e) is { } g)
                    kids.Add(Node(null, $"守卫（{fn.Name}）", "", "external-group", StageRoute, g, [], f.Path, fn.StartLine));
                var own = cj is { } c ? TagNames(c.Jsx).ToHashSet(StringComparer.Ordinal) : [];
                kids.AddRange(CallRows(fn, StageMount, own));
            }
            mountRows.Add(Row(StageMount, fn ?? module, fn?.Name ?? FileName(module.Path), note, kids, f.Path, fn?.StartLine ?? 1));
            // Provider 接在这一层布局后面（下一层布局渲染在这些 Provider 里面）
            var before = providerRows.Count;
            if (cj is { } jsx)
            {
                if (fn is not null) expanded.Add(fn.Id);
                Visit(jsx.File, jsx.Jsx, true, 0, null, false);
            }
            mountRows.AddRange(providerRows.Skip(before));
            providerRows.RemoveRange(before, providerRows.Count - before);
        }
    }
}
