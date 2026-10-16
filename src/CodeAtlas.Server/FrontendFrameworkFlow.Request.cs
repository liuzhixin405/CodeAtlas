using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>前端框架流程：路由（TanStack Router / React Router）、请求封装（axios / 生成的 OpenAPI 客户端 / fetch 封装）、收尾（响应拦截、全局 onError），以及守卫与出错处理的文字说明。</summary>
public static partial class FrontendFrameworkFlow
{
    private static readonly HashSet<string> TanStackRouteFunctions = new(StringComparer.Ordinal)
        { "createFileRoute", "createLazyFileRoute", "createRootRoute", "createRootRouteWithContext", "createRoute" };
    private static readonly HashSet<string> ReactRouterFactories = new(StringComparer.Ordinal) { "createBrowserRouter", "createHashRouter", "createMemoryRouter" };

    [GeneratedRegex(@"(?<![\w$.])(?<fn>createFileRoute|createLazyFileRoute|createRootRouteWithContext|createRootRoute|createRoute)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex TanStackRouteCall();

    [GeneratedRegex(@"(?<![\w$.])(?<fn>createBrowserRouter|createHashRouter|createMemoryRouter)\s*\(")]
    private static partial Regex ReactRouterFactory();

    [GeneratedRegex(@"(Auth|Guard|Protected|Private|Require|Permission)")]
    private static partial Regex GuardName();

    [GeneratedRegex(@"(?<recv>[A-Za-z_$][\w$]*(?:\s*\.\s*[A-Za-z_$][\w$]*)*?)\s*\.\s*interceptors\s*\.\s*(?<k>request|response)\s*\.\s*use\s*\(")]
    private static partial Regex InterceptorUse();

    [GeneratedRegex(@"(?<![\w$.])axios\s*\.\s*create\s*\(")]
    private static partial Regex AxiosCreate();

    [GeneratedRegex(@"(?<![\w$])(?<recv>[A-Za-z_$][\w$]*)\s*\.\s*setConfig\s*\(")]
    private static partial Regex SetConfigCall();

    [GeneratedRegex(@"export\s+const\s+(?<n>[A-Za-z_$][\w$]*)\s*=\s*createClient\s*\(")]
    private static partial Regex HeyApiClient();

    [GeneratedRegex(@"(request|http|api|client|axios|fetch|interceptor|service)", RegexOptions.IgnoreCase)]
    private static partial Regex HttpishFileName();

    private sealed record TsRoute(string Id, string Fn, JsFile File, int Pos, List<(string Key, int KeyPos, int ValueStart, int ValueEnd)> Options)
    {
        public bool IsRoot => Fn.StartsWith("createRoot", StringComparison.Ordinal);
        public (string Key, int KeyPos, int ValueStart, int ValueEnd)? Opt(string key) => Options.FirstOrDefault(o => o.Key == key) is { Key: not null } o ? o : null;
    }

    private sealed record RrRoute(string? Path, JsFile File, int Pos, JsxNode? Element, (int Start, int End)? Loader, string? ErrorElement, List<RrRoute> Children);

    private sealed partial class Run
    {
        // ================= 路由 =================

        private void BuildRoutes()
        {
            var wrappers = routeRows.ToList();
            routeRows.Clear();
            var tanstack = deps.ContainsKey("@tanstack/react-router") || routeTags.Any(t => t.El.Name == "RouterProvider" && LibraryOf(t.File, "RouterProvider")?.Contains("tanstack", StringComparison.Ordinal) == true);
            var reactRouter = deps.ContainsKey("react-router-dom") || deps.ContainsKey("react-router") || routeTags.Any(t => t.El.Name is "Routes" or "Switch" || RouterWrappers.Contains(t.El.Name));
            routeRows.AddRange(wrappers);
            if (tanstack) BuildTanStackRoutes();
            if (reactRouter) BuildReactRouterRoutes();
            if (routeRows.Count == 0)
                notes.Add(tanstack || reactRouter
                    ? "路由：依赖里有路由库，但没找到路由定义（createRootRoute / createFileRoute / createBrowserRouter / <Routes>）"
                    : "路由：没认出路由库（TanStack Router / React Router）");
        }

        private IEnumerable<string> FilesCalling(IReadOnlySet<string> names)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (from, list) in callsFrom)
                foreach (var e in list)
                    if (byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External && names.Contains(t.Name) && byId.TryGetValue(from, out var fs)) paths.Add(fs.Path);
            return paths;
        }

        private void BuildTanStackRoutes()
        {
            var routes = new List<TsRoute>();
            foreach (var path in FilesCalling(TanStackRouteFunctions))
            {
                if (File(path) is not { } f) continue;
                foreach (Match m in TanStackRouteCall().Matches(f.S))
                {
                    var fn = m.Groups["fn"].Value;
                    var open = m.Index + m.Length - 1;
                    var close = MatchAny(f.S, open);
                    if (close < 0) continue;
                    string? id = null;
                    var optsOpen = -1;
                    if (fn is "createFileRoute" or "createLazyFileRoute" or "createRootRouteWithContext")
                    {
                        if (fn != "createRootRouteWithContext") id = f.StringAt(open + 1);
                        var next = FrameworkFlowBuilder.SkipWs(f.S, close + 1);
                        if (next < f.S.Length && f.S[next] == '(') optsOpen = next;
                    }
                    else optsOpen = open;
                    if (optsOpen < 0) continue;
                    var brace = FrameworkFlowBuilder.SkipWs(f.S, optsOpen + 1);
                    var entries = brace < f.S.Length && f.S[brace] == '{' && MatchAny(f.S, brace) is var bc and > 0 ? ObjectEntries(f.S, brace, bc) : [];
                    if (fn.StartsWith("createRoot", StringComparison.Ordinal)) id = "__root__";
                    else if (fn == "createRoute") id = entries.FirstOrDefault(e => e.Key == "path") is { Key: not null } p ? f.StringAt(p.ValueStart) : null;
                    routes.Add(new TsRoute(id ?? "?", fn, f, m.Index, entries));
                }
            }
            if (routes.Count == 0) return;

            static string Trim(string id) => id.Length > 1 ? id.TrimEnd('/') : id;
            // 文件路由的 id：/_layout 是布局，/_layout/ 是它下面的 index 页面（结尾的 / 表示 index，不是布局）
            List<TsRoute> ChildrenOf(TsRoute r) => r.IsRoot ? routes.Where(x => !x.IsRoot).ToList()
                : routes.Where(x => !x.IsRoot && x != r && x.Id.Length > Trim(r.Id).Length && x.Id.StartsWith(Trim(r.Id) + "/", StringComparison.Ordinal)).ToList();
            bool IsLayout(TsRoute r) => r.IsRoot || r.Id != "/" && !r.Id.EndsWith('/') && r.Id != "?"
                && (ChildrenOf(r).Count > 0 || r.Id.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.StartsWith('_') == true);

            var layouts = routes.Where(IsLayout).OrderBy(r => r.IsRoot ? 0 : 1).ThenBy(r => r.Id.Count(c => c == '/')).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
            foreach (var r in layouts)
            {
                var f = r.File;
                var kids = new List<FlowNodeDto>();
                var parts = new List<string>();
                var children = ChildrenOf(r);
                if (r.IsRoot) parts.Add($"根路由（{Where(f, r.Pos)}）：所有页面的外壳");
                else
                {
                    var pathless = r.Id.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.StartsWith('_') == true;
                    parts.Add((pathless ? "无路径段的布局路由" : "布局路由") + $"（{Where(f, r.Pos)}）");
                    if (children.Count > 0) parts.Add($"包住 {children.Count} 个子路由：{string.Join("、", children.Take(5).Select(c => c.Id))}" + (children.Count > 5 ? "…" : ""));
                }
                foreach (var key in new[] { "beforeLoad", "loader" })
                {
                    if (r.Opt(key) is not { } o) continue;
                    var (rows, guard) = HookRows(f, o.ValueStart, o.ValueEnd, StageRoute);
                    var what = key == "beforeLoad" ? "beforeLoad（守卫）" : "loader（进路由前取数）";
                    kids.Add(Node(null, what, key, "external-group", StageRoute, guard ?? (key == "beforeLoad" ? "进路由前执行：可 throw redirect 拦下" : "进路由前取数，取完才渲染"), Cap(rows, StageRoute), f.Path, f.LineOf(o.KeyPos)));
                }
                if (r.Opt("component") is { } comp)
                {
                    var text = f.S[comp.ValueStart..comp.ValueEnd].Trim();
                    if (Regex.IsMatch(text, @"^[A-Za-z_$][\w$]*$") && Resolve(f, text) is { } sym)
                    {
                        var own = ComponentJsx(sym) is { } cj ? TagNames(cj.Jsx).ToHashSet(StringComparer.Ordinal) : [];
                        var renders = ComponentJsx(sym) is { } cj2 ? TagNames(cj2.Jsx).Where(n => !char.IsLower(n[0])).Distinct().Take(8).ToList() : [];
                        kids.Add(Row(StageRoute, sym, Label(sym), (r.IsRoot ? "根组件" : "布局组件") + (renders.Count > 0 ? $"：渲染 {string.Join("、", renders)}" : "") + "（<Outlet/> 处放子路由）",
                            CallRows(sym, StageRoute, own), sym.Path, sym.StartLine));
                    }
                    else
                    {
                        var tags = JsxIn(f.S, comp.ValueStart, comp.ValueEnd).SelectMany(TagNames).Where(n => !char.IsLower(n[0])).Distinct().Take(8).ToList();
                        if (tags.Count > 0) parts.Add($"component 渲染 {string.Join("、", tags)}");
                    }
                }
                foreach (var key in new[] { "errorComponent", "notFoundComponent", "pendingComponent" })
                {
                    if (r.Opt(key) is not { } o) continue;
                    var text = f.S[o.ValueStart..o.ValueEnd].Trim();
                    var name = Regex.IsMatch(text, @"^[A-Za-z_$][\w$]*$") ? text : JsxIn(f.S, o.ValueStart, o.ValueEnd).FirstOrDefault()?.Name;
                    if (name is null) continue;
                    if (key == "errorComponent")
                    {
                        var sym = Resolve(f, name);
                        AddTail("route-error:" + f.Path + ":" + o.KeyPos, Row(StageTail, sym, $"errorComponent → {name}",
                            $"路由出错时显示（{Where(f, o.KeyPos)}）：beforeLoad / loader / 渲染抛错都落到这里", [], sym?.Path ?? f.Path, sym?.StartLine ?? f.LineOf(o.KeyPos), sym is null ? ExternalQn(f, name) : null));
                    }
                    else parts.Add($"{key} → {name}");
                }
                var module = modulesByPath.GetValueOrDefault(f.Path);
                var label = r.IsRoot ? $"{FileName(f.Path)}（根路由）" : $"{Trim(r.Id)}（布局路由）";
                routeRows.Add(Row(StageRoute, module, label, string.Join("；", parts), kids, f.Path, f.LineOf(r.Pos)));
            }
            var leafHooks = routes.Where(r => !IsLayout(r) && (r.Opt("beforeLoad") is not null || r.Opt("loader") is not null)).ToList();
            notes.Add($"路由：TanStack Router，{routes.Count} 个路由定义（{routes.Select(r => r.File.Path).Distinct().Count()} 个文件）；画了根路由与 {layouts.Count(l => !l.IsRoot)} 个布局路由（按路径层级由外到内）"
                      + (leafHooks.Count > 0 ? $"；另有 {leafHooks.Count} 个页面路由自带 beforeLoad / loader（只管自己，没画）" : ""));
        }

        /// <summary>beforeLoad / loader 的值：函数名就展开那个函数，内联箭头函数就取这段里的调用；附带守卫说明。</summary>
        private (List<FlowNodeDto> Rows, string? Guard) HookRows(JsFile f, int s, int e, string stage)
        {
            var text = f.S[s..e].Trim();
            if (Regex.IsMatch(text, @"^[A-Za-z_$][\w$]*$") && Resolve(f, text) is { } fn && File(fn.Path) is { } ff)
            {
                var (fs, fe) = ff.Span(fn.StartLine, fn.EndLine);
                return ([Row(stage, fn, Label(fn), null, CallRows(fn, stage), fn.Path, fn.StartLine)], GuardNote(ff, fs, fe));
            }
            return (SpanRows(f, s, e, stage), GuardNote(f, s, e));
        }

        private void BuildReactRouterRoutes()
        {
            var tables = new List<(string Label, JsFile File, int Pos, List<RrRoute> Routes)>();
            foreach (var path in FilesCalling(ReactRouterFactories))
            {
                if (File(path) is not { } f) continue;
                foreach (Match m in ReactRouterFactory().Matches(f.S))
                {
                    var open = m.Index + m.Length - 1;
                    var bracket = FrameworkFlowBuilder.SkipWs(f.S, open + 1);
                    if (bracket >= f.S.Length || f.S[bracket] != '[') continue;
                    tables.Add((m.Groups["fn"].Value, f, m.Index, RouteArray(f, bracket)));
                }
            }
            var tags = routeTags.Where(t => t.El.Name is "Routes" or "Switch").ToList();
            if (tags.Count == 0 && tables.Count == 0)
                foreach (var path in FilesCalling(new HashSet<string>(StringComparer.Ordinal) { "Routes" }))
                    if (File(path) is { } f)
                        foreach (var n in JsxIn(f.S, 0, f.S.Length).SelectMany(Descendants).Where(n => n.Name == "Routes"))
                            tags.Add((f, n));
            foreach (var (f, el) in tags.DistinctBy(t => (t.File.Path, t.El.Pos)))
                tables.Add((el.Name, f, el.Pos, RouteElements(f, el)));

            foreach (var (label, f, pos, routes) in tables)
            {
                var all = routes.SelectMany(AllRoutes).ToList();
                var layoutRoutes = new List<(RrRoute Route, int Depth)>();
                void Collect(RrRoute r, int depth) { if (r.Children.Count > 0 && depth <= 2) layoutRoutes.Add((r, depth)); foreach (var c in r.Children) Collect(c, depth + 1); }
                foreach (var r in routes) Collect(r, 0);
                var kids = layoutRoutes.Select(x => LayoutRouteRow(x.Route)).ToList();
                var guardedLeaves = all.Where(r => r.Children.Count == 0 && r.Element is { } el && TagNames(el).Any(n => GuardName().IsMatch(n))).ToList();
                foreach (var r in routes.Where(r => r.ErrorElement is not null))
                {
                    var sym = Resolve(r.File, r.ErrorElement!);
                    AddTail("rr-error:" + r.File.Path + ":" + r.Pos, Row(StageTail, sym, $"errorElement → {r.ErrorElement}", $"路由出错时显示（{Where(r.File, r.Pos)}）：loader / action / 渲染抛错都落到这里",
                        [], sym?.Path ?? r.File.Path, sym?.StartLine ?? r.File.LineOf(r.Pos), sym is null ? ExternalQn(r.File, r.ErrorElement!) : null));
                }
                var note = $"React Router 路由表（{Where(f, pos)}）：{all.Count} 条路由，{layoutRoutes.Count} 个布局路由"
                           + (guardedLeaves.Count > 0 ? $"；另有 {guardedLeaves.Count} 条路由的 element 套了守卫组件（{string.Join("、", guardedLeaves.SelectMany(r => TagNames(r.Element!)).Where(n => GuardName().IsMatch(n)).Distinct().Take(3))}）" : "");
                routeRows.Add(Row(StageRoute, null, label, note, kids, f.Path, f.LineOf(pos), ExternalQn(f, label)));
            }
            if (tables.Count > 0) notes.Add($"路由：React Router，{tables.Count} 张路由表；画了前三层里带子路由的布局路由，element 里的守卫组件单独成行");
        }

        private static IEnumerable<JsxNode> Descendants(JsxNode n) => n.Children.SelectMany(Descendants).Prepend(n);

        private static IEnumerable<RrRoute> AllRoutes(RrRoute r) => r.Children.SelectMany(AllRoutes).Prepend(r);

        /// <summary>createBrowserRouter([{ path, element, loader, errorElement, children: [...] }, …])。</summary>
        private List<RrRoute> RouteArray(JsFile f, int bracket)
        {
            var close = MatchAny(f.S, bracket);
            var list = new List<RrRoute>();
            if (close < 0) return list;
            for (var i = bracket + 1; i < close; i++)
            {
                if (f.S[i] != '{') { if (f.S[i] is '(' or '[') i = Math.Max(i, MatchAny(f.S, i)); continue; }
                var oc = MatchAny(f.S, i);
                if (oc < 0) break;
                var entries = ObjectEntries(f.S, i, oc);
                string? path = entries.FirstOrDefault(e => e.Key == "path") is { Key: not null } p ? f.StringAt(p.ValueStart) : null;
                var element = entries.FirstOrDefault(e => e.Key == "element") is { Key: not null } el ? JsxIn(f.S, el.ValueStart, el.ValueEnd).FirstOrDefault() : null;
                (int, int)? loader = entries.FirstOrDefault(e => e.Key == "loader") is { Key: not null } l ? (l.ValueStart, l.ValueEnd) : null;
                string? error = entries.FirstOrDefault(e => e.Key is "errorElement" or "ErrorBoundary") is { Key: not null } er
                    ? JsxIn(f.S, er.ValueStart, er.ValueEnd).FirstOrDefault()?.Name ?? f.S[er.ValueStart..er.ValueEnd].Trim() : null;
                var kids = entries.FirstOrDefault(e => e.Key == "children") is { Key: not null } c && FrameworkFlowBuilder.SkipWs(f.S, c.ValueStart) is var cb && cb < f.S.Length && f.S[cb] == '['
                    ? RouteArray(f, cb) : [];
                list.Add(new RrRoute(path, f, i, element, loader, error, kids));
                i = oc;
            }
            return list;
        }

        /// <summary>&lt;Routes&gt;&lt;Route path element loader errorElement&gt;…&lt;/Route&gt;&lt;/Routes&gt;。</summary>
        private List<RrRoute> RouteElements(JsFile f, JsxNode parent)
        {
            var list = new List<RrRoute>();
            foreach (var c in parent.Children)
            {
                if (c.Name != "Route") { if (c.IsHost) list.AddRange(RouteElements(f, c)); continue; }
                var path = Attr(f, c, "path")?.Text;
                JsxNode? element = Attr(f, c, "element") is { } el ? JsxIn(f.S, el.Start, el.End).FirstOrDefault() : null;
                (int, int)? loader = Attr(f, c, "loader") is { } l ? (l.Start, l.End) : null;
                var error = Attr(f, c, "errorElement") is { } er ? JsxIn(f.S, er.Start, er.End).FirstOrDefault()?.Name : null;
                list.Add(new RrRoute(path, f, c.Pos, element, loader, error, RouteElements(f, c)));
            }
            return list;
        }

        private FlowNodeDto LayoutRouteRow(RrRoute r)
        {
            var f = r.File;
            var kids = new List<FlowNodeDto>();
            if (r.Element is { } el)
                foreach (var name in TagNames(el).Where(n => !char.IsLower(n[0]) && n is not ("Outlet" or "Suspense")).Distinct())
                {
                    var sym = Resolve(f, name);
                    string? guard = null;
                    if (sym is not null && File(sym.Path) is { } sf) { var (s, e) = sf.Span(sym.StartLine, sym.EndLine); guard = GuardNote(sf, s, e); }
                    var isGuard = guard is not null || GuardName().IsMatch(name);
                    kids.Add(Row(StageRoute, sym, name, isGuard ? guard ?? "守卫组件：条件不满足时不渲染子路由" : "布局组件：<Outlet/> 处放子路由",
                        sym is not null && sym.Kind is SymbolKind.Function or SymbolKind.Method ? CallRows(sym, StageRoute, ComponentJsx(sym) is { } cj ? TagNames(cj.Jsx).ToHashSet(StringComparer.Ordinal) : null) : [],
                        sym?.Path ?? f.Path, sym?.StartLine ?? f.LineOf(r.Pos), sym is null ? ExternalQn(f, name) : null));
                }
            if (r.Loader is { } l)
            {
                var (rows, guard) = HookRows(f, l.Start, l.End, StageRoute);
                kids.Add(Node(null, "loader（进路由前取数）", "loader", "external-group", StageRoute, guard ?? "进路由前取数，取完才渲染", Cap(rows, StageRoute), f.Path, f.LineOf(l.Start)));
            }
            var children = r.Children.Select(c => c.Path ?? "（index）").ToList();
            return Node(null, "布局路由 " + (r.Path ?? "（无路径）"), "", "external-group", StageRoute,
                $"包住 {r.Children.Count} 个子路由：{string.Join("、", children.Take(5))}" + (children.Count > 5 ? "…" : "") + $"（{Where(f, r.Pos)}）", Cap(kids, StageRoute), f.Path, f.LineOf(r.Pos));
        }

        // ================= 请求封装与收尾 =================

        private void BuildRequest()
        {
            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (from, list) in callsFrom)
                foreach (var e in list)
                    if (byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External
                        && (t.QualifiedName.StartsWith("external:axios", StringComparison.Ordinal) || t.Name is "use" or "create" or "setConfig" or "createClient")
                        && byId.TryGetValue(from, out var fs)) candidates.Add(fs.Path);
            foreach (var e in input.Edges)
                if (e.Kind == EdgeKind.Imports && inside.Contains(e.FromId) && byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External
                    && (t.QualifiedName.StartsWith("external:axios", StringComparison.Ordinal) || t.QualifiedName.StartsWith("external:@hey-api", StringComparison.Ordinal)) && byId.TryGetValue(e.FromId, out var fs))
                    candidates.Add(fs.Path);
            foreach (var p in modulesByPath.Keys)
                if (HttpishFileName().IsMatch(FileName(p)) || p.Contains(".gen.", StringComparison.Ordinal) || FileName(p).StartsWith("OpenAPI.", StringComparison.Ordinal)) candidates.Add(p);

            var instances = new List<FlowNodeDto>();
            var serverFiles = new List<string>();
            var baseUrls = new List<(JsFile File, int Pos, string Expr)>();
            var apiRoutes = input.Entries.Where(e => e.ContainerKey == container.Key && e.Kind == "api-route").Select(e => e.File).ToHashSet(StringComparer.Ordinal);
            bool ServerOnly(string path) => apiRoutes.Contains(path) || path.Contains("/app/api/", StringComparison.Ordinal) || path.Contains("/pages/api/", StringComparison.Ordinal)
                                            || Regex.IsMatch(FileName(path), @"^(middleware|proxy)\.");
            var requestInterceptors = new List<FlowNodeDto>();
            var interceptorCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var generated = new List<(JsFile File, string Generator)>();
            var generatedAxios = new List<string>();
            foreach (var path in candidates)
            {
                if (!ScriptExtensions.Any(ext => path.EndsWith(ext, StringComparison.Ordinal)) || path.EndsWith(".d.ts", StringComparison.Ordinal) || File(path) is not { } f) continue;
                var gen = Generator(f);
                if (gen is not null) generated.Add((f, gen));
                foreach (Match m in AxiosCreate().Matches(f.S))
                {
                    if (gen is not null) { generatedAxios.Add(Where(f, m.Index)); continue; }
                    if (ServerOnly(f.Path)) { serverFiles.Add(f.Path); continue; }
                    var close = MatchAny(f.S, m.Index + m.Length - 1);
                    if (close > 0 && Regex.Match(f.Raw[m.Index..close], @"\bbaseURL\s*:\s*(?<v>[^,}\r\n]+)") is { Success: true } bm) baseUrls.Add((f, m.Index, bm.Groups["v"].Value.Trim()));
                    var owner = Owner(f, m.Index, m.Index + 1);
                    var keys = close > 0 ? ObjectKeys(f, m.Index + m.Length, close) : null;
                    instances.Add(Row(StageRequest, owner, owner is null ? "axios.create" : Label(owner), $"axios 实例（{Where(f, m.Index)}）" + (keys is null ? "" : "：" + keys), [], f.Path, f.LineOf(m.Index), owner is null ? "external:axios.create" : null));
                }
                if (gen is not null || ServerOnly(f.Path)) continue;
                foreach (Match m in InterceptorUse().Matches(f.S))
                {
                    var open = m.Index + m.Length - 1;
                    var close = MatchAny(f.S, open);
                    if (close < 0) continue;
                    var recv = Regex.Replace(m.Groups["recv"].Value, @"\s+", "");
                    var kind = m.Groups["k"].Value;
                    var args = ArgSpans(f.S, open + 1, close);
                    if (kind == "request")
                    {
                        interceptorCount[recv] = interceptorCount.GetValueOrDefault(recv) + 1;
                        var (s, e) = args.Count > 0 ? args[0] : (open + 1, close);
                        var headers = HeadersSet(f.Raw[s..e]);
                        requestInterceptors.Add(Node(null, $"{recv}.interceptors.request.use", "interceptors.request.use", "external-group", StageRequest,
                            $"请求拦截器（{Where(f, m.Index)}）：每个请求发出前执行" + (headers is null ? "" : "；" + headers), Cap(SpanRows(f, s, e, StageRequest), StageRequest), f.Path, f.LineOf(m.Index)));
                    }
                    else
                    {
                        if (args.Count > 0)
                        {
                            var (s, e) = args[0];
                            var unwrap = Regex.IsMatch(f.Raw[s..e], @"(?:return\s+|=>\s*)\w+\s*\.\s*data\b") ? "；成功时把 response.data 拆出来给调用方" : "";
                            AddTail($"resp-ok:{f.Path}:{m.Index}", Node(null, $"{recv}.interceptors.response.use（成功）", "interceptors.response.use", "external-group", StageTail,
                                $"响应拦截器（{Where(f, m.Index)}）：响应回来先过这里" + unwrap, Cap(SpanRows(f, s, e, StageTail), StageTail), f.Path, f.LineOf(s)));
                        }
                        if (args.Count > 1)
                        {
                            var (s, e) = args[1];
                            var desc = DescribeErrors(ReachableTexts(f, s, e));
                            AddTail($"resp-err:{f.Path}:{m.Index}", Node(null, $"{recv}.interceptors.response.use（出错）", "interceptors.response.use", "external-group", StageTail,
                                $"响应出错时（{Where(f, s)}）" + (desc is null ? "：统一出错处理" : "：" + desc), Cap(ExpandOnce(SpanRows(f, s, e, StageTail), StageTail), StageTail), f.Path, f.LineOf(s)));
                        }
                    }
                }
                foreach (Match m in SetConfigCall().Matches(f.S))
                {
                    var where = Where(f, m.Index);
                    if (clientConfigSites.Any(c => c.StartsWith(where + " ", StringComparison.Ordinal))) continue;
                    var close = MatchAny(f.S, m.Index + m.Length - 1);
                    clientConfigSites.Add($"{where} {m.Groups["recv"].Value}.setConfig（{(close > 0 ? ObjectKeys(f, m.Index + m.Length, close) : null) ?? "…"}）");
                }
                foreach (Match m in GlobalAssign().Matches(f.S))
                {
                    var where = Where(f, m.Index);
                    if (!clientConfigSites.Any(c => c.StartsWith(where + " ", StringComparison.Ordinal))) clientConfigSites.Add($"{where} {Regex.Replace(m.Groups["lhs"].Value, @"\s+", "")}");
                }
            }

            // 生成的 OpenAPI 客户端：每个生成器一行
            foreach (var (gen, group) in generated.GroupBy(g => g.Generator).Select(g => (g.Key, g.ToList())))
            {
                JsFile? at = null;
                var pos = 0;
                string? name = null;
                foreach (var (f, _) in group)
                {
                    if (HeyApiClient().Match(f.S) is { Success: true } hm) { at = f; pos = hm.Index; name = hm.Groups["n"].Value; break; }
                    if (FileName(f.Path).StartsWith("OpenAPI.", StringComparison.Ordinal) && Regex.Match(f.S, @"export\s+const\s+OpenAPI\b") is { Success: true } om) { at = f; pos = om.Index; name = "OpenAPI"; break; }
                }
                if (at is null) { at = group[0].File; name = gen; }
                var sym = name is null ? null : symbolsByPath[at.Path].FirstOrDefault(s => s.Name == name && s.Kind is SymbolKind.Variable or SymbolKind.Function);
                var parts = new List<string> { $"{gen} 生成的 OpenAPI 客户端（{Where(at, pos)}）" };
                if (generatedAxios.Count > 0) parts.Add($"底层 axios（{generatedAxios[0]} axios.create）");
                if (clientConfigSites.Count > 0) parts.Add("全局配置：" + string.Join("；", clientConfigSites.Take(4)));
                if (clientConfigSites.Any(c => Regex.IsMatch(c, @"\bauth\b|TOKEN"))) parts.Add("令牌：配置里的 auth / TOKEN 在每个请求发出时带上");
                if (requestInterceptors.Count == 0) parts.Add("没有注册 interceptors");
                requestRows.Add(Row(StageRequest, sym, $"{name ?? gen}（生成的客户端）", string.Join("；", parts), [], at.Path, at.LineOf(pos), sym is null ? name : null));
            }
            requestRows.AddRange(instances);
            requestRows.AddRange(requestInterceptors);
            // 浏览器端实例的 baseURL 指向本应用自己的服务端代理路由（Next.js app/api/…/route.ts 再用服务端 axios 转发到后端）：代理这一跳也在每个请求的路径上
            var proxy = ProxyHop(baseUrls);
            if (proxy is { } ph)
            {
                requestRows.Add(ph.Row);
                serverFiles.RemoveAll(p => p == ph.File);
            }
            var serverSide = serverFiles.Count;
            if (interceptorCount.Values.Any(n => n > 1))
            {
                notes.Add("请求封装：同一实例注册了多个请求拦截器——axios 后注册的先执行，响应拦截器按注册顺序执行");
            }

            // fetch 封装：被 3 处以上调用、自己调了 fetch 的函数（服务端路由与中间件里的不算）
            if (requestRows.Count == 0 || requestInterceptors.Count == 0)
            {
                var wrappers = new List<MapSymbol>();
                foreach (var (from, list) in callsFrom)
                    if (byId.TryGetValue(from, out var fs) && fs.Kind is SymbolKind.Function or SymbolKind.Method && !ServerOnly(fs.Path) && list.Any(e => byId.TryGetValue(e.ToId, out var t) && t.Kind == SymbolKind.External && t.QualifiedName == "external:fetch"))
                        wrappers.Add(fs);
                if (wrappers.Count > 0)
                {
                    var ids = wrappers.Select(w => w.Id).ToHashSet();
                    var callers = new Dictionary<long, HashSet<long>>();
                    foreach (var e in input.Edges)
                        if (e.Kind == EdgeKind.Calls && ids.Contains(e.ToId) && inside.Contains(e.FromId) && e.FromId != e.ToId)
                            (callers.TryGetValue(e.ToId, out var set) ? set : callers[e.ToId] = []).Add(e.FromId);
                    foreach (var w in wrappers.Where(w => callers.GetValueOrDefault(w.Id)?.Count >= 3).OrderByDescending(w => callers[w.Id].Count).ThenBy(w => w.QualifiedName, StringComparer.Ordinal).Take(3))
                        requestRows.Add(Row(StageRequest, w, Label(w), $"fetch 封装（被 {callers[w.Id].Count} 处调用）", CallRows(w, StageRequest), w.Path, w.StartLine));
                }
            }
            if (proxy is { } pn) notes.Add(pn.Note);
            if (requestRows.Count == 0 && serverSide > 0)
                notes.Add($"没认出请求封装：页面这边没有 axios.create / interceptors / 生成的客户端 / fetch 封装；另有 {serverSide} 个 axios 实例在服务端 API 路由 / 中间件里（不在页面的请求路径上，没画）");
            if (requestRows.Count > 0)
                notes.Add("请求封装：" + string.Join("；", new[]
                {
                    generated.Count > 0 ? $"生成的 OpenAPI 客户端（{string.Join("、", generated.Select(g => g.Generator).Distinct())}）" : null,
                    instances.Count > 0 ? $"{instances.Count} 个 axios 实例" : null,
                    requestInterceptors.Count > 0 ? $"{requestInterceptors.Count} 个请求拦截器" : "没有请求拦截器",
                    tailIndex.Keys.Count(k => k.StartsWith("resp-", StringComparison.Ordinal)) is var r and > 0 ? $"响应拦截 {r} 段（进收尾）" : null,
                    serverSide > 0 ? $"另有 {serverSide} 个 axios 实例在服务端 API 路由 / 中间件里（不在页面的请求路径上，没画）" : null,
                }.Where(x => x is not null)));
        }

        /// <summary>
        /// 浏览器端 axios 的 baseURL（字面量，或 export const X = "…" 这种常量）以 / 开头、落在本容器的 API 路由（api-route 入口）上：
        /// 请求先进这个代理路由（服务端）再转发到后端。画成请求封装里的一行：路由处理函数的关键调用，Note 写它做了什么（转发用的 axios 实例、Bearer、401 刷新重试）。
        /// </summary>
        private (FlowNodeDto Row, string File, string Note)? ProxyHop(List<(JsFile File, int Pos, string Expr)> baseUrls)
        {
            var routes = input.Entries.Where(e => e.ContainerKey == container.Key && e.Kind == "api-route" && e.Route is not null).ToList();
            if (routes.Count == 0) return null;
            foreach (var (f, pos, expr) in baseUrls)
            {
                string? value = null;
                var lit = Regex.Match(expr, @"^[""'`]([^""'`$]*)[""'`]");
                if (lit.Success) value = lit.Groups[1].Value;
                else if (Regex.IsMatch(expr, @"^[A-Za-z_$][\w$]*$"))
                {
                    var decl = new Regex(@"\b(?:const|let|var)\s+" + Regex.Escape(expr) + @"\s*(?::\s*\w+\s*)?=\s*[""'`]([^""'`$]*)[""'`]");
                    foreach (var path in new[] { f.Path }.Concat(modulesByPath.Keys.OrderBy(p => p, StringComparer.Ordinal)))
                    {
                        string? raw;
                        try { raw = src.ReadFile(path); } catch { raw = null; }
                        if (raw is null || !raw.Contains(expr, StringComparison.Ordinal)) continue;
                        if (decl.Match(raw) is { Success: true } dm) { value = dm.Groups[1].Value; break; }
                    }
                }
                if (value is null || !value.StartsWith('/')) continue;
                var prefix = value.TrimEnd('/');
                var hit = routes.FirstOrDefault(e => e.Route!.StartsWith(prefix + "/", StringComparison.Ordinal) || e.Route == prefix);
                if (hit is null || File(hit.File) is not { } rf) continue;
                // 路由的处理函数：export const GET = handleX / export async function GET(…)
                MapSymbol? handler = null;
                if (Regex.Match(rf.S, @"export\s+const\s+(?:GET|POST|PUT|PATCH|DELETE)\s*=\s*([A-Za-z_$][\w$]*)") is { Success: true } em)
                    handler = symbolsByPath[rf.Path].FirstOrDefault(x => x.Name == em.Groups[1].Value && x.Kind is SymbolKind.Function or SymbolKind.Variable);
                handler ??= symbolsByPath[rf.Path].FirstOrDefault(x => x.Name is "GET" or "POST" && x.Kind is SymbolKind.Function or SymbolKind.Variable);
                var what = new List<string>();
                if (AxiosCreate().Match(rf.S) is { Success: true } am)
                    what.Add($"服务端 axios 实例（{Where(rf, am.Index)}{(Owner(rf, am.Index, am.Index + 1) is { } ao ? " " + Label(ao) : "")}）转发到后端");
                if (Regex.IsMatch(rf.Raw, @"Bearer\b")) what.Add("注入 Authorization: Bearer 令牌");
                if (Regex.Match(rf.Raw, @"\b(\w*[Rr]efresh\w*)\s*(?:<[^>]*>)?\s*\(") is { Success: true } refresh) what.Add($"令牌过期 / 401 时刷新重试（{refresh.Groups[1].Value}）");
                else if (rf.Raw.Contains("401", StringComparison.Ordinal)) what.Add("处理 401");
                var where = $"{FileName(f.Path)}:{f.LineOf(pos)}";
                var note = $"代理路由（服务端，{Rel(hit.File)}）：浏览器端 axios 的 baseURL = {(lit.Success ? "" : expr + " = ")}\"{value}\"（{where}），每个请求先到这里" + (what.Count > 0 ? "；" + string.Join("、", what) : "");
                var kids = handler is not null && handler.Kind == SymbolKind.Function ? CallRows(handler, StageRequest) : [];
                var row = Row(StageRequest, handler, $"代理 {hit.Route}", note, kids, hit.File, handler?.StartLine ?? hit.Line);
                return (row, hit.File, $"请求封装：浏览器端的 baseURL 指向本应用的代理路由 {hit.Route}（{FileName(hit.File)}），再由服务端转发到后端——代理这一跳画在请求封装里");
            }
            return null;
        }

        /// <summary>生成的客户端：文件头写了生成器（@hey-api/openapi-ts / openapi-typescript-codegen / orval），或文件名带 .gen.。</summary>
        private static string? Generator(JsFile f)
        {
            var head = f.Raw[..Math.Min(f.Raw.Length, 600)];
            if (head.Contains("@hey-api", StringComparison.Ordinal)) return "@hey-api/openapi-ts";
            if (head.Contains("openapi-typescript-codegen", StringComparison.Ordinal)) return "openapi-typescript-codegen";
            if (head.Contains("orval", StringComparison.OrdinalIgnoreCase)) return "orval";
            if (Regex.IsMatch(head, @"(auto-?generated|@generated|generated by|Code generated)", RegexOptions.IgnoreCase) && Regex.IsMatch(head + f.Path, "openapi|swagger", RegexOptions.IgnoreCase)) return "OpenAPI 生成器";
            return null;
        }

        /// <summary>调用实参 [start, end) 按顶层逗号切开的各段（去掉首尾空白）。</summary>
        private static List<(int Start, int End)> ArgSpans(string s, int start, int end)
        {
            var spans = new List<(int, int)>();
            var depth = 0;
            var a = start;
            for (var i = start; i <= end; i++)
            {
                if (i < end && s[i] is '(' or '[' or '{') depth++;
                else if (i < end && s[i] is ')' or ']' or '}') depth--;
                else if (i == end || s[i] == ',' && depth == 0)
                {
                    var b = FrameworkFlowBuilder.SkipWs(s, a);
                    var e = i;
                    while (e > b && char.IsWhiteSpace(s[e - 1])) e--;
                    if (e > b) spans.Add((b, e));
                    a = i + 1;
                }
            }
            return spans;
        }

        /// <summary>请求拦截器里设置的请求头（config.headers["X"] = / headers.X = / headers.set("X")）与令牌。</summary>
        private static string? HeadersSet(string raw)
        {
            var names = new List<string>();
            foreach (Match m in Regex.Matches(raw, @"headers\s*(?:\[\s*(?:[""'`](?<q>[^""'`]+)[""'`]|(?<c>[A-Za-z_$][\w$]*))\s*\]|\.\s*(?<d>[A-Za-z_$][\w$]*))\s*=(?!=)"))
                names.Add(m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["c"].Success ? m.Groups["c"].Value : m.Groups["d"].Value);
            foreach (Match m in Regex.Matches(raw, @"headers\s*\.\s*set\s*\(\s*[""'`](?<q>[^""'`]+)")) names.Add(m.Groups["q"].Value);
            var token = Regex.IsMatch(raw, @"Bearer|[Aa]uthorization|[Tt]oken");
            if (names.Count == 0 && !token) return null;
            return (names.Count > 0 ? "设置请求头 " + string.Join("、", names.Distinct().Take(6)) : "") + (token && !names.Any(n => n.Contains("uthorization", StringComparison.Ordinal)) ? (names.Count > 0 ? "，" : "") + "带上令牌" : "");
        }

        // ---- TanStack Query 的 QueryClient ----

        /// <summary>QueryClientProvider 的 client：同文件的 new QueryClient，或 client={x} 的 x = getQueryClient() 一路往下（两层），再不行取容器里任意一处 new QueryClient。</summary>
        private List<FlowNodeDto> QueryClientRows(JsFile f, JsxNode el)
        {
            var site = FindQueryClient(f, Attr(f, el, "client")?.Text);
            if (site is null) return [];
            var (qf, pos) = site.Value;
            var open = qf.S.IndexOf('(', pos);
            var close = open < 0 ? -1 : MatchAny(qf.S, open);
            var parts = new List<string> { $"查询客户端（{Where(qf, pos)}）" };
            var handlers = new List<(string What, int Start, int End)>();
            var brace = open < 0 ? -1 : FrameworkFlowBuilder.SkipWs(qf.S, open + 1);
            if (close > 0 && brace < close && qf.S[brace] == '{' && MatchAny(qf.S, brace) is var bc and > 0)
            {
                foreach (var (key, _, vs, ve) in ObjectEntries(qf.S, brace, bc))
                {
                    if (key is "queryCache" or "mutationCache")
                    {
                        var inner = qf.S.IndexOf('{', vs);
                        if (inner < 0 || inner >= ve) continue;
                        var ic = MatchAny(qf.S, inner);
                        foreach (var h in ObjectEntries(qf.S, inner, ic).Where(x => x.Key is "onError" or "onSettled"))
                            handlers.Add((key == "queryCache" ? "QueryCache." + h.Key : "MutationCache." + h.Key, h.ValueStart, h.ValueEnd));
                    }
                    else if (key == "defaultOptions" && qf.S[vs] == '{')
                    {
                        foreach (var (sub, _, ss, se) in ObjectEntries(qf.S, vs, MatchAny(qf.S, vs)))
                        {
                            if (qf.S[ss] != '{') continue;
                            var opts = ObjectEntries(qf.S, ss, MatchAny(qf.S, ss));
                            parts.Add($"defaultOptions.{sub}：{string.Join("、", opts.Select(o => o.Key + (o.Key == "retry" && Regex.IsMatch(qf.S[o.ValueStart..o.ValueEnd], @"=>|function") ? "（自定义函数）" : "")).Take(8))}");
                            foreach (var h in opts.Where(o => o.Key == "onError"))
                                handlers.Add(($"defaultOptions.{sub}.onError", h.ValueStart, h.ValueEnd));
                        }
                    }
                }
            }
            if (handlers.Count > 0) parts.Add($"{string.Join("、", handlers.Select(h => h.What).Distinct())} 见收尾");
            foreach (var h in handlers) AddQueryErrorTail(qf, h.What, h.Start, h.End);
            var owner = Owner(qf, pos, pos + 1);
            return [Row(StageProvider, owner, owner is null ? "new QueryClient" : $"{Label(owner)} → new QueryClient", string.Join("；", parts), [], qf.Path, qf.LineOf(pos), owner is null ? "external:@tanstack/react-query.QueryClient" : null)];
        }

        private (JsFile File, int Pos)? FindQueryClient(JsFile f, string? ident)
        {
            var rx = new Regex(@"\bnew\s+QueryClient\s*\(");
            if (rx.Match(f.S) is { Success: true } here) return (f, here.Index);
            if (ident is not null && Regex.IsMatch(ident, @"^[A-Za-z_$][\w$]*$"))
            {
                var call = new Regex(@"(?<![\w$.])" + Regex.Escape(ident) + @"\s*(?::[^=]+)?=\s*(?:await\s+)?(?<fn>[A-Za-z_$][\w$]*)\s*\(").Match(f.S);
                var start = call.Success ? Resolve(f, call.Groups["fn"].Value) : null;
                var frontier = start is null ? new List<MapSymbol>() : [start];
                var seen = new HashSet<long>();
                for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
                {
                    var next = new List<MapSymbol>();
                    foreach (var fn in frontier)
                    {
                        if (!seen.Add(fn.Id) || File(fn.Path) is not { } ff) continue;
                        var (s, e) = ff.Span(fn.StartLine, fn.EndLine);
                        var m = rx.Match(ff.S, s, e - s);
                        if (m.Success) return (ff, m.Index);
                        next.AddRange(RepoCallees(ff, s, e));
                    }
                    frontier = next;
                }
            }
            foreach (var path in FilesCalling(new HashSet<string>(StringComparer.Ordinal) { "QueryClient" }))
                if (File(path) is { } ff && rx.Match(ff.S) is { Success: true } m) return (ff, m.Index);
            return null;
        }

        /// <summary>QueryCache / MutationCache 的 onError：函数名就挂那个函数（同一个函数只画一行，名字合并），内联的取这段里的调用。</summary>
        private void AddQueryErrorTail(JsFile f, string what, int s, int e)
        {
            var text = f.S[s..e].Trim();
            MapSymbol? fn = Regex.IsMatch(text, @"^[A-Za-z_$][\w$]*$") ? Resolve(f, text) : null;
            var trigger = what.StartsWith("Mutation", StringComparison.Ordinal) ? "提交（mutation）出错时" : "查询出错时";
            if (fn is not null && File(fn.Path) is { } ff)
            {
                var key = "qc:" + fn.Id;
                if (tailIndex.TryGetValue(key, out var at))
                {
                    var old = tailRows[at];
                    var whats = old.Label[..old.Label.IndexOf(" → ", StringComparison.Ordinal)];
                    tailRows[at] = old with { Label = $"{whats} / {what} → {fn.Name}", Note = old.Note?.Replace("查询出错时", "查询 / 提交（mutation）出错时", StringComparison.Ordinal) };
                    return;
                }
                var (fs, fe) = ff.Span(fn.StartLine, fn.EndLine);
                var desc = DescribeErrors(ReachableTexts(ff, fs, fe));
                AddTail(key, Row(StageTail, fn, $"{what} → {fn.Name}", $"TanStack Query 全局：{trigger}" + (desc is null ? "" : "：" + desc), ExpandOnce(CallRows(fn, StageTail), StageTail), fn.Path, fn.StartLine));
                return;
            }
            var d = DescribeErrors(ReachableTexts(f, s, e));
            AddTail($"qc:{f.Path}:{s}", Node(null, what, what, "external-group", StageTail, $"TanStack Query 全局：{trigger}" + (d is null ? "" : "：" + d),
                Cap(ExpandOnce(SpanRows(f, s, e, StageTail), StageTail), StageTail), f.Path, f.LineOf(s)));
        }

        // ================= 守卫与出错处理的说明 =================

        /// <summary>跳转：redirect / NextResponse.redirect / 自己包的 xxxRedirect(new URL(...)) / &lt;Navigate to&gt; / navigate() / location.href / router.push。t = 字面量目标，v = 变量目标。</summary>
        private static readonly Regex[] RedirectPatterns =
        [
            new(@"(?<how>throw\s+redirect|redirect)\s*\(\s*\{[^}]*?\bto\s*:\s*[""'`](?<t>[^""'`]+)"),
            new(@"(?<how>(?:[\w$]+\s*\.\s*)?[\w$]*[Rr]edirect[\w$]*)\s*\(\s*(?:new\s+URL\s*\(\s*)?[""'`](?<t>[^""'`]+)"),
            new(@"<(?<how>Navigate)\b[^>]*?\bto\s*=\s*\{?\s*[""'`](?<t>[^""'`]+)"),
            new(@"(?<how>navigate)\s*\(\s*[""'`](?<t>[^""'`]+)"),
            new(@"(?<how>(?:window\s*\.\s*)?location\s*\.\s*href)\s*=\s*[""'`](?<t>[^""'`]+)"),
            new(@"(?<how>router\s*\.\s*(?:push|replace))\s*\(\s*[""'`](?<t>[^""'`]+)"),
        ];

        private static readonly Regex VariableRedirect = new(@"(?<how>NextResponse\s*\.\s*redirect)\s*\(\s*(?<v>[A-Za-z_$][\w$.]*)");

        [GeneratedRegex(@"login|signin|sign-in|auth", RegexOptions.IgnoreCase)]
        private static partial Regex LoginTarget();

        /// <summary>
        /// 一段源码里的守卫：跳登录页的优先、其中外面包的 if 最少的那处（主守卫，不是"已在登录页"之类的分支）；条件是包住它的各层 if（由外到内）。
        /// 没有字面量目标的只认 NextResponse.redirect(变量)。
        /// </summary>
        internal static string? GuardNote(JsFile f, int s, int e)
        {
            var hits = new List<(int Pos, string How, string Target, List<string> Conds)>();
            e = Math.Min(e, f.Raw.Length);
            foreach (var rx in RedirectPatterns)
                for (var m = rx.Match(f.Raw, s, e - s); m.Success; m = m.NextMatch())
                    if (f.S[m.Index] != ' ' && !IsDefinition(f.S, m.Index)) hits.Add((m.Index, Regex.Replace(m.Groups["how"].Value, @"\s+", " "), m.Groups["t"].Value, EnclosingIfs(f, s, m.Index)));
            if (hits.Count == 0)
                for (var m = VariableRedirect.Match(f.Raw, s, e - s); m.Success; m = m.NextMatch())
                    if (f.S[m.Index] != ' ') hits.Add((m.Index, Regex.Replace(m.Groups["how"].Value, @"\s+", " "), m.Groups["v"].Value, EnclosingIfs(f, s, m.Index)));
            if (hits.Count == 0) return null;
            var best = hits.OrderBy(h => LoginTarget().IsMatch(h.Target) ? 0 : 1).ThenBy(h => h.Conds.Count).ThenBy(h => h.Pos).First();
            return "守卫：" + (best.Conds.Count == 0 ? "" : string.Join(" 且 ", best.Conds) + " 时") + $"跳 {best.Target}（{best.How}）";
        }

        /// <summary>pos 处是函数声明的名字（function noCacheRedirect(…)），不是调用。</summary>
        private static bool IsDefinition(string s, int pos) => Regex.IsMatch(s[Math.Max(0, pos - 12)..pos], @"function\s+$");

        /// <summary>包住 pos 的各层 if（由外到内）的条件原文；if (x) 后面不带花括号的，管到这一行结束。</summary>
        private static List<string> EnclosingIfs(JsFile f, int s, int pos)
        {
            var conds = new List<string>();
            foreach (Match m in Regex.Matches(f.S[s..pos], @"\bif\s*\("))
            {
                var open = s + m.Index + m.Length - 1;
                var close = MatchAny(f.S, open);
                if (close < 0 || close >= pos) continue;
                var body = FrameworkFlowBuilder.SkipWs(f.S, close + 1);
                var end = body < f.S.Length && f.S[body] == '{' ? MatchAny(f.S, body) : f.S.IndexOf('\n', body) is var nl and >= 0 ? nl : f.S.Length;
                if (end > pos) conds.Add(f.Snippet(open + 1, close, 40));
            }
            return conds;
        }

        /// <summary>出错处理做了什么：涉及的状态码 + 动作（清令牌、跳登录、广播事件、弹提示、刷新令牌、再抛给调用方）。</summary>
        internal static string? DescribeErrors(IEnumerable<string> texts)
        {
            var all = string.Join("\n", texts);
            var statuses = Regex.Matches(all, @"(?<![\w.])(401|403|404|419|429|500|502|503)(?![\w.])").Select(m => m.Value).Distinct().ToList();
            var actions = new List<string>();
            foreach (Match m in Regex.Matches(all, @"removeItem\s*\(\s*[""'`](?<k>[^""'`]+)")) actions.Add("清 " + m.Groups["k"].Value);
            foreach (var rx in RedirectPatterns)
                foreach (Match m in rx.Matches(all))
                    if (m.Groups["t"].Success) actions.Add("跳 " + m.Groups["t"].Value);
            if (Regex.IsMatch(all, @"\bsignOut\s*\(")) actions.Add("登出");
            if (Regex.IsMatch(all, @"\brefresh\w*\s*\(", RegexOptions.IgnoreCase)) actions.Add("刷新令牌");
            foreach (Match m in Regex.Matches(all, @"dispatchEvent\s*\(\s*new\s+(?:Custom)?Event\s*\(\s*[""'`](?<e>[^""'`]+)")) actions.Add($"广播事件 {m.Groups["e"].Value}");
            if (Regex.IsMatch(all, @"\btoast\b|enqueueSnackbar|message\s*\.\s*error|notification\s*\.\s*error|show\w*Error\w*\s*\(|\bnotify\w*\s*\(")) actions.Add("弹出错提示");
            if (Regex.IsMatch(all, @"Promise\s*\.\s*reject|\bthrow\b")) actions.Add("再抛给调用方");
            if (statuses.Count == 0 && actions.Count == 0) return null;
            return (statuses.Count > 0 ? "涉及 " + string.Join("/", statuses) : "") + (actions.Count > 0 ? (statuses.Count > 0 ? "；" : "") + string.Join("、", actions.Distinct()) : "");
        }
    }
}
