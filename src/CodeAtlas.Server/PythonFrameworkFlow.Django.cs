using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Django（含 DRF）。阶段：中间件 → 视图（占位）→ DRF 认证 / 权限 → 收尾。
/// 中间件 = settings 里 MIDDLEWARE 列表的顺序（请求从上往下，响应倒序往回）：仓库里的类拆 get_response(...) 前后（新式 __call__），
/// 或 MiddlewareMixin 的 process_request / process_view（前置）与 process_response / process_exception（收尾，倒序）；函数式中间件拆里层函数；
/// 其余（django.* / 第三方）画 external-group 行。DRF 的 REST_FRAMEWORK：DEFAULT_AUTHENTICATION_CLASSES / DEFAULT_PERMISSION_CLASSES /
/// DEFAULT_THROTTLE_CLASSES 在视图 initial() 里（没配就画 DRF 默认值），DEFAULT_RENDERER_CLASSES 与 EXCEPTION_HANDLER 在收尾。
/// </summary>
public static partial class PythonFrameworkFlow
{
    private sealed record DjangoSettings(string Path, string Text, int Pos, int Open, int Close, List<string> OtherFiles);

    [GeneratedRegex(@"^MIDDLEWARE(?:_CLASSES)?\s*(?::[^=\n]+)?=\s*([\[(])", RegexOptions.Multiline)]
    private static partial Regex DjangoMiddlewareList();

    [GeneratedRegex(@"^[ \t]*MIDDLEWARE(?:_CLASSES)?\s*(?:\+=|=|\.append\s*\(|\.insert\s*\(|\.extend\s*\(|\.remove\s*\()", RegexOptions.Multiline)]
    private static partial Regex DjangoMiddlewareTouch();

    [GeneratedRegex(@"^REST_FRAMEWORK\s*(?::[^=\n]+)?=\s*(\{)", RegexOptions.Multiline)]
    private static partial Regex DrfSettings();

    [GeneratedRegex(@"^INSTALLED_APPS\s*(?::[^=\n]+)?=\s*([\[(])", RegexOptions.Multiline)]
    private static partial Regex InstalledApps();

    [GeneratedRegex(@"^ROOT_URLCONF\s*=\s*['""]([\w.]+)['""]", RegexOptions.Multiline)]
    private static partial Regex RootUrlConf();

    [GeneratedRegex(@"^[ \t]+(authentication_classes|permission_classes|renderer_classes|throttle_classes)\s*=", RegexOptions.Multiline)]
    private static partial Regex DrfViewOverride();

    [GeneratedRegex(@"@\s*(?:\w+\.)?(authentication_classes|permission_classes|renderer_classes|throttle_classes)\s*\(")]
    private static partial Regex DrfDecoratorOverride();

    private static readonly Dictionary<string, string> KnownDjangoMiddleware = new(StringComparer.Ordinal)
    {
        ["SecurityMiddleware"] = "HTTPS 重定向与安全响应头",
        ["SessionMiddleware"] = "从 Cookie 取会话，响应时保存",
        ["CommonMiddleware"] = "APPEND_SLASH / PREPEND_WWW、拦截 DISALLOWED_USER_AGENTS",
        ["CsrfViewMiddleware"] = "CSRF 校验（process_view，视图之前）",
        ["AuthenticationMiddleware"] = "给 request.user 挂上会话里的用户（惰性）",
        ["MessageMiddleware"] = "消息框架",
        ["XFrameOptionsMiddleware"] = "X-Frame-Options 响应头",
        ["CorsMiddleware"] = "跨域响应头",
        ["LocaleMiddleware"] = "按请求选语言",
        ["GZipMiddleware"] = "响应压缩",
        ["WhiteNoiseMiddleware"] = "静态文件",
        ["ConditionalGetMiddleware"] = "ETag / Last-Modified 条件请求",
    };

    private static readonly Dictionary<string, string> KnownDrfClasses = new(StringComparer.Ordinal)
    {
        ["SessionAuthentication"] = "用 Django 会话认证（非安全方法要过 CSRF）",
        ["BasicAuthentication"] = "HTTP Basic 认证",
        ["TokenAuthentication"] = "Authorization: Token …",
        ["JWTAuthentication"] = "Authorization: Bearer <JWT>",
        ["AllowAny"] = "不限制",
        ["IsAuthenticated"] = "没登录就 401 / 403",
        ["IsAuthenticatedOrReadOnly"] = "没登录只能读",
        ["IsAdminUser"] = "只许 is_staff",
        ["DjangoModelPermissions"] = "按模型权限",
        ["JSONRenderer"] = "JSON",
        ["BrowsableAPIRenderer"] = "可浏览的 API 页面",
        ["AnonRateThrottle"] = "匿名用户限流",
        ["UserRateThrottle"] = "登录用户限流",
    };

    /// <summary>定义了 MIDDLEWARE = [...] 的 settings：settings.py 优先，再 base.py / common.py，再浅的；其余改了 MIDDLEWARE 的文件记下来。</summary>
    private static DjangoSettings? FindDjangoSettings(Py x)
    {
        var found = new List<(string Path, string Text, Match M)>();
        var touched = new List<string>();
        foreach (var (path, text) in x.Texts(t => t.Contains("MIDDLEWARE", StringComparison.Ordinal)))
        {
            if (DjangoMiddlewareList().Match(text) is { Success: true } m) found.Add((path, text, m));
            else if (DjangoMiddlewareTouch().IsMatch(text)) touched.Add(path);
        }
        if (found.Count == 0) return null;
        static int Rank(string p) => Py.FileName(p) switch { "settings.py" => 0, "base.py" or "common.py" or "defaults.py" => 1, _ => 2 };
        var pick = found.OrderBy(f => Rank(f.Path)).ThenBy(f => f.Path.Count(c => c == '/')).ThenBy(f => f.Path, StringComparer.Ordinal).First();
        var open = pick.M.Groups[1].Index;
        var close = MatchPy(pick.Text, open);
        if (close < 0) return null;
        var others = found.Where(f => f.Path != pick.Path).Select(f => f.Path).Concat(touched).Distinct(StringComparer.Ordinal).ToList();
        return new DjangoSettings(pick.Path, pick.Text, pick.M.Index, open, close, others);
    }

    /// <summary>settings 字典（REST_FRAMEWORK）的键 → （值，值在全文里的起点）。</summary>
    private static Dictionary<string, (string Value, int Pos)> SettingsDict(string text, int open, int close)
    {
        var dict = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        var inner = text[(open + 1)..close];
        foreach (var (part, start) in SplitPy(inner))
        {
            var key = Regex.Match(part, @"^\s*['""](\w+)['""]\s*:");
            if (!key.Success) continue;
            dict[key.Groups[1].Value] = (part[key.Length..], open + 1 + start + key.Length);
        }
        return dict;
    }

    private static FrameworkFlowDto BuildDjango(Py x, DjangoSettings s, List<string> notes)
    {
        var text = s.Text;
        var file = Py.FileName(s.Path);
        var items = StringLiterals(text[(s.Open + 1)..s.Close]).Select(l => (Dotted: l.Value, Pos: s.Open + 1 + l.Pos)).Where(l => l.Dotted.Length > 0).ToList();

        // ---- 中间件 ----
        var middlewareNodes = new List<FlowNodeDto>();
        var middlewareSymbols = new List<MapSymbol>();
        var posts = new List<List<FlowNodeDto>>();
        var errors = new List<List<FlowNodeDto>>();
        var tailSymbols = new List<MapSymbol>();
        foreach (var (dotted, pos) in items)
        {
            var where = Py.Where(s.Path, text, pos);
            var line = LineAt(text, pos);
            var post = new List<FlowNodeDto>();
            var error = new List<FlowNodeDto>();
            posts.Add(post);
            errors.Add(error);
            var (sym, moduleFound) = x.ResolveDotted(dotted);
            if (sym is { Kind: SymbolKind.Class } cls)
            {
                middlewareSymbols.Add(cls);
                var nth = $"MIDDLEWARE 第 {middlewareNodes.Count + 1} 个，{where}";
                var view = x.Method(cls, "process_view");
                FlowNodeDto node;
                if (x.Method(cls, "__call__") is { } call)
                {
                    var (n, p) = SplitNode(x, call, ["get_response"], new Regex(@"\bget_response\s*\("), StageMiddleware, $"自研中间件（{nth}）", s.Path, line);
                    node = n;
                    post.AddRange(p);
                }
                else if (x.Method(cls, "process_request") is { } req)
                    node = x.FunctionNode(req, StageMiddleware, $"自研中间件 process_request（MiddlewareMixin，{nth}）", s.Path, line);
                else if (view is not null)
                    node = x.FunctionNode(view, StageMiddleware, $"自研中间件，只有 process_view：URL 解析之后、视图之前（{nth}）", s.Path, line);
                else
                    node = Node(cls.Id, cls.Name, cls.QualifiedName, "call", StageMiddleware, $"自研中间件（{nth}），没有 __call__ / process_request / process_view", [], s.Path, line);
                // process_view 在 URL 解析之后、视图之前：挂在这个中间件下面最后一行
                if (view is not null && node.SymbolId != view.Id)
                    node = node with { Children = [.. node.Children, x.FunctionNode(view, StageMiddleware, "process_view：URL 解析之后、视图之前")] };
                middlewareNodes.Add(node);
                if (x.Method(cls, "process_response") is { } resp)
                {
                    tailSymbols.Add(resp);
                    post.Add(x.FunctionNode(resp, StageTail, $"{cls.Name} 的 process_response（响应往回走，倒序）"));
                }
                if (x.Method(cls, "process_exception") is { } exc)
                {
                    tailSymbols.Add(exc);
                    error.Add(x.FunctionNode(exc, StageTail, $"出错时：{cls.Name}.process_exception（视图抛异常，倒序）"));
                }
                continue;
            }
            if (sym is { Kind: SymbolKind.Function } factory)
            {
                // 函数式中间件：def tenant_middleware(get_response): def middleware(request): … get_response(request) …
                middlewareSymbols.Add(factory);
                var inner = x.ChildrenOf[factory.Id].Where(c => c.Kind == SymbolKind.Function).OrderBy(c => c.StartLine).FirstOrDefault();
                var target = inner ?? factory;
                var next = Params(x, factory).FirstOrDefault()?.Name ?? "get_response";
                var (n, p) = SplitNode(x, target, [next], new Regex(@"\b" + Regex.Escape(next) + @"\s*\("), StageMiddleware,
                    $"自研函数中间件（MIDDLEWARE 第 {middlewareNodes.Count + 1} 个，{where}）", s.Path, line, factory.Name);
                middlewareNodes.Add(n);
                post.AddRange(p);
                continue;
            }
            var name = LastSegment(dotted);
            var origin = dotted.StartsWith("django.", StringComparison.Ordinal) ? "Django 内置"
                : moduleFound ? $"仓库里的模块，但没找到 {name}"
                : $"第三方 {dotted.Split('.')[0]}";
            var desc = KnownDjangoMiddleware.GetValueOrDefault(name);
            middlewareNodes.Add(Node(null, name, dotted, "external-group", StageMiddleware, $"{origin}（{where}）" + (desc is null ? "" : "：" + desc), [], s.Path, line));
        }
        foreach (var n in posts.SelectMany(p => p)) if (n.SymbolId is { } id && x.ById.TryGetValue(id, out var ps)) tailSymbols.Add(ps);

        // ---- DRF ----
        var installed = InstalledApps().Match(text) is { Success: true } ia && MatchPy(text, ia.Groups[1].Index) is var iaEnd and > 0
            ? StringLiterals(text[ia.Groups[1].Index..iaEnd]).Select(l => l.Value).ToList() : [];
        var drfMatch = DrfSettings().Match(text);
        var drfConf = drfMatch.Success && MatchPy(text, drfMatch.Groups[1].Index) is var drfEnd and > 0
            ? SettingsDict(text, drfMatch.Groups[1].Index, drfEnd) : null;
        var isDrf = drfConf is not null || installed.Contains("rest_framework") || x.Http.Any(e => e.RuleId == "python.drf");
        var interceptors = new List<FlowNodeDto>();
        var interceptorSymbols = new List<MapSymbol>();
        var drfTail = new List<FlowNodeDto>();
        if (isDrf)
        {
            List<(string Dotted, int Pos)>? Classes(string key) =>
                drfConf is not null && drfConf.TryGetValue(key, out var v) ? StringLiterals(v.Value).Select(l => (l.Value, v.Pos + l.Pos)).ToList() : null;
            void AddClasses(string key, string what, string method, string actor, List<(string Dotted, int Pos)> defaults, List<FlowNodeDto> into, string? extra = null)
            {
                var configured = Classes(key);
                foreach (var (dotted, pos) in configured ?? defaults)
                {
                    var src = configured is null ? $"{what}（DRF 默认 {key}）" : $"{what}（{key}，{Py.Where(s.Path, text, pos)}）";
                    src += extra ?? "";
                    var name = LastSegment(dotted);
                    int? line = configured is null ? null : LineAt(text, pos);
                    if (x.ResolveDotted(dotted).Symbol is { Kind: SymbolKind.Class } cls)
                    {
                        if (x.Method(cls, method) is { } m)
                        {
                            (actor == StageTail ? tailSymbols : interceptorSymbols).Add(m);
                            into.Add(x.FunctionNode(m, actor, src, configured is null ? null : s.Path, line));
                        }
                        else
                        {
                            (actor == StageTail ? tailSymbols : interceptorSymbols).Add(cls);
                            into.Add(Node(cls.Id, cls.Name, cls.QualifiedName, "call", actor, src + $"；没有 {method}，用基类的", [], configured is null ? null : s.Path, line));
                        }
                        continue;
                    }
                    var desc = KnownDrfClasses.GetValueOrDefault(name);
                    into.Add(Node(null, name, dotted, "external-group", actor, src + (desc is null ? "" : "：" + desc), [], configured is null ? null : s.Path, line));
                }
            }
            AddClasses("DEFAULT_AUTHENTICATION_CLASSES", "认证", "authenticate", StageInterceptor,
                [("rest_framework.authentication.SessionAuthentication", 0), ("rest_framework.authentication.BasicAuthentication", 0)], interceptors,
                "；按顺序试，第一个返回 (user, auth) 的生效");
            AddClasses("DEFAULT_PERMISSION_CLASSES", "权限", "has_permission", StageInterceptor, [("rest_framework.permissions.AllowAny", 0)], interceptors,
                "；全部通过才进视图方法");
            if (Classes("DEFAULT_THROTTLE_CLASSES") is not null)
                AddClasses("DEFAULT_THROTTLE_CLASSES", "限流", "allow_request", StageInterceptor, [], interceptors);
            // 收尾：出错时的 EXCEPTION_HANDLER，再是渲染器（错误响应也要渲染）
            if (drfConf is not null && drfConf.TryGetValue("EXCEPTION_HANDLER", out var eh) && StringLiterals(eh.Value).FirstOrDefault() is { Value.Length: > 0 } ehv)
            {
                var where = Py.Where(s.Path, text, eh.Pos + ehv.Pos);
                if (x.ResolveDotted(ehv.Value).Symbol is { Kind: SymbolKind.Function } fn)
                {
                    tailSymbols.Add(fn);
                    drfTail.Add(x.FunctionNode(fn, StageTail, $"出错时：DRF EXCEPTION_HANDLER（{where}），把异常变成错误响应", s.Path, LineAt(text, eh.Pos)));
                }
                else drfTail.Add(Node(null, LastSegment(ehv.Value), ehv.Value, "external-group", StageTail, $"出错时：DRF EXCEPTION_HANDLER（{where}）", [], s.Path, LineAt(text, eh.Pos)));
            }
            if (Classes("DEFAULT_RENDERER_CLASSES") is not null)
                AddClasses("DEFAULT_RENDERER_CLASSES", "渲染响应", "render", StageTail, [], drfTail, "；按 Accept 协商选一个");
            else
                drfTail.Add(Node(null, "JSONRenderer / BrowsableAPIRenderer", "rest_framework.renderers", "external-group", StageTail, "渲染响应（DRF 默认 DEFAULT_RENDERER_CLASSES，按 Accept 协商）", [], null, null));
        }

        // ---- 组装 ----
        var viewKids = new List<FlowNodeDto>(interceptors);
        viewKids.AddRange(drfTail);
        var viewNode = x.Http.Count > 0 ? Node(null, "视图", "", "controllers", StageController, null, viewKids, null, null, x.Http.Count) : null;
        var rootKids = new List<FlowNodeDto>(middlewareNodes);
        if (viewNode is not null) rootKids.Add(viewNode);
        else rootKids.AddRange(viewKids);
        for (var i = errors.Count - 1; i >= 0; i--) rootKids.AddRange(errors[i]);
        for (var i = posts.Count - 1; i >= 0; i--) rootKids.AddRange(posts[i]);
        var rootActor = middlewareNodes.Count > 0 ? StageMiddleware : StageController;
        var root = Node(null, "HTTP 请求", "", "entry", rootActor, null, rootKids, null, null);
        var stages = new List<FrameworkStageDto>();
        if (middlewareNodes.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "中间件", true, x.Dtos(middlewareSymbols)));
        if (viewNode is not null) stages.Add(new FrameworkStageDto(StageController, "视图", true, x.EntrySymbols(x.Http)));
        if (interceptors.Count > 0) stages.Add(new FrameworkStageDto(StageInterceptor, "DRF 认证 / 权限", true, x.Dtos(interceptorSymbols)));
        if (drfTail.Count > 0 || posts.Any(p => p.Count > 0) || errors.Any(e => e.Count > 0)) stages.Add(new FrameworkStageDto(StageTail, "收尾", true, x.Dtos(tailSymbols)));

        // ---- Notes ----
        notes.Add($"认出 Django：{Py.Where(s.Path, text, s.Pos)} MIDDLEWARE（{items.Count} 个）；中间件按列表顺序，请求从上往下经过，响应倒序往回"
                  + "（get_response 之后的部分、process_response 放在收尾，process_exception 在收尾「出错时」，都倒序）");
        if (s.OtherFiles.Count > 0)
            notes.Add($"另有 {string.Join("、", s.OtherFiles.Select(Py.FileName).Distinct())} 也定义 / 改了 MIDDLEWARE（按环境覆盖或追加），没画；以 {file} 为准");
        if (RootUrlConf().Match(text) is { Success: true } urls) notes.Add($"路由：ROOT_URLCONF = {urls.Groups[1].Value}，{x.Http.Count} 个 HTTP 入口");
        if (viewNode is null) notes.Add("没有发现 HTTP 入口（urls.py 里的 path / re_path、DRF router.register）");
        if (isDrf)
        {
            notes.Add((drfMatch.Success ? $"认出 DRF（{Py.Where(s.Path, text, drfMatch.Index)} REST_FRAMEWORK）" : "认出 DRF（INSTALLED_APPS 里有 rest_framework，没配 REST_FRAMEWORK）")
                      + "：认证 / 权限 / 限流在 APIView.initial() 里执行（URL 路由之后、视图方法之前），EXCEPTION_HANDLER 与渲染器在收尾");
            var missing = new[] { "DEFAULT_AUTHENTICATION_CLASSES", "DEFAULT_PERMISSION_CLASSES", "DEFAULT_RENDERER_CLASSES", "EXCEPTION_HANDLER" }
                .Where(k => drfConf is null || !drfConf.ContainsKey(k)).ToList();
            if (missing.Count > 0) notes.Add($"没配 {string.Join("、", missing)}：画的是 DRF 默认值");
            var overrides = DrfOverrides(x);
            if (overrides.Count > 0)
                notes.Add("视图自己设的（覆盖上面的默认）：" + string.Join("、", overrides.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key} {o.Value} 个")));
        }
        else notes.Add("没用 DRF：视图函数 / 类视图直接处理请求");
        return Assemble(x.Container, "HTTP 请求", root, stages, [], notes, rootActor == StageMiddleware ? "中间件" : "视图");
    }

    /// <summary>HTTP 入口所在的视图类（或函数视图的装饰器）里自己设了 xxx_classes 的个数。</summary>
    private static Dictionary<string, int> DrfOverrides(Py x)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var owners = new HashSet<long>();
        foreach (var e in x.Http)
        {
            if (!x.ById.TryGetValue(e.SymbolId, out var s)) continue;
            var owner = s.Kind == SymbolKind.Method && s.ParentId is { } p && x.ById.TryGetValue(p, out var c) && c.Kind == SymbolKind.Class ? c : s;
            if (!owners.Add(owner.Id) || x.Text(owner.Path) is not { } text) continue;
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (owner.Kind == SymbolKind.Class)
            {
                var (start, end) = LineSpan(text, owner.StartLine, owner.EndLine);
                foreach (Match m in DrfViewOverride().Matches(text[start..end])) found.Add(m.Groups[1].Value);
            }
            else if (DecoratorBlock(text, owner.StartLine) is { } block)
                foreach (Match m in DrfDecoratorOverride().Matches(text[block.Start..block.End])) found.Add(m.Groups[1].Value);
            foreach (var f in found) counts[f] = counts.GetValueOrDefault(f) + 1;
        }
        return counts;
    }
}
