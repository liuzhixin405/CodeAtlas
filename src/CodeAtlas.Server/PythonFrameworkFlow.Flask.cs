using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Flask。阶段：中间件（WSGI）→ 请求钩子 → 路由（占位）→ 收尾。
/// WSGI 中间件 = app.wsgi_app = X(app.wsgi_app, …)，后包的在最外层；before_request：应用级在前、蓝图在后，各按注册顺序（返回非 None 直接响应）；
/// 收尾：errorhandler / register_error_handler（蓝图的先查）→ after_request（蓝图在前，各自倒序）→ teardown_request（同上）→ 中间件后置（倒序）。
/// 钩子认装饰器 @app.before_request 与调用 app.before_request(func) 两种；*_app_request / app_errorhandler 挂在蓝图上也是应用级。
/// </summary>
public static partial class PythonFrameworkFlow
{
    [GeneratedRegex(@"^[ \t]*(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:[\w.]+\.)?Flask\s*\(", RegexOptions.Multiline)]
    private static partial Regex FlaskDecl();

    [GeneratedRegex(@"^[ \t]*(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:[\w.]+\.)?Blueprint\s*\(", RegexOptions.Multiline)]
    private static partial Regex BlueprintDecl();

    [GeneratedRegex(@"^[ \t]*@\s*(\w+)\s*\.\s*(before_request|before_app_request|after_request|after_app_request|teardown_request|teardown_app_request|errorhandler|app_errorhandler)\b", RegexOptions.Multiline)]
    private static partial Regex FlaskHookDecorator();

    [GeneratedRegex(@"(?<![\w.@])(\w+)\s*\.\s*(before_request|before_app_request|after_request|after_app_request|teardown_request|teardown_app_request)\s*\(\s*(\w+)\s*\)")]
    private static partial Regex FlaskHookCall();

    [GeneratedRegex(@"(?<![\w.])(\w+)\s*\.\s*register_error_handler\s*\(")]
    private static partial Regex FlaskErrorCall();

    [GeneratedRegex(@"(?<![\w.])(\w+)\s*\.\s*wsgi_app\s*=\s*([\w.]+)\s*\(")]
    private static partial Regex FlaskWsgi();

    [GeneratedRegex(@"(?<![\w.])\w+\s*\.\s*register_blueprint\s*\(")]
    private static partial Regex RegisterBlueprint();

    private sealed record FlaskHook(string Kind, bool AppLevel, string Receiver, string Name, MapSymbol? Fn, string Path, int Pos, string How, string? Exc);

    private static List<PyApp> FindFlaskApps(Py x) => FindApps(x, "Flask", FlaskDecl());

    private static FrameworkFlowDto BuildFlask(Py x, List<PyApp> apps, List<string> notes)
    {
        var app = apps[0];
        var appText = x.Text(app.Path)!;
        notes.Add($"认出 Flask：{Py.Where(app.Path, appText, app.Pos)} {app.Var} = Flask(...)"
                  + (apps.Count > 1 ? $"；另有 {apps.Count - 1} 个 Flask 实例，按第一个画" : ""));

        // ---- 蓝图与钩子（全部文件，文件顺序 + 源码顺序）----
        var blueprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in x.Texts(t => t.Contains("Blueprint", StringComparison.Ordinal)))
            foreach (Match m in BlueprintDecl().Matches(text)) blueprints.Add(m.Groups[1].Value);
        var hooks = new List<FlaskHook>();
        var registered = 0;
        static string KindOf(string method) => method switch
        {
            "before_request" or "before_app_request" => "before",
            "after_request" or "after_app_request" => "after",
            "teardown_request" or "teardown_app_request" => "teardown",
            _ => "error",
        };
        bool AppLevel(string receiver, string method) => !blueprints.Contains(receiver) || method.Contains("_app_", StringComparison.Ordinal) || method == "app_errorhandler";
        foreach (var (path, text) in x.Texts(t => t.Contains("_request", StringComparison.Ordinal) || t.Contains("errorhandler", StringComparison.Ordinal)
                                                   || t.Contains("register_error_handler", StringComparison.Ordinal) || t.Contains("register_blueprint", StringComparison.Ordinal)))
        {
            registered += RegisterBlueprint().Count(text);
            var found = new List<FlaskHook>();
            foreach (Match m in FlaskHookDecorator().Matches(text))
            {
                var (receiver, method) = (m.Groups[1].Value, m.Groups[2].Value);
                var end = m.Index + m.Length;
                string? exc = null;
                var i = end;
                while (i < text.Length && text[i] is ' ' or '\t') i++;
                if (i < text.Length && text[i] == '(' && MatchPy(text, i) is var close and > 0) { exc = FirstPositional(text[(i + 1)..close]); end = close + 1; }
                if (DefAfter(text, end) is not { } def) continue;
                found.Add(new FlaskHook(KindOf(method), AppLevel(receiver, method), receiver, def.Name, x.DefinedAt(path, def.Name, def.Line), path, m.Index, $"@{receiver}.{method}", exc?.Trim()));
            }
            foreach (Match m in FlaskHookCall().Matches(text))
            {
                var (receiver, method, fn) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                found.Add(new FlaskHook(KindOf(method), AppLevel(receiver, method), receiver, fn, x.Callable(fn, path), path, m.Index, $"{receiver}.{method}({fn})", null));
            }
            foreach (Match m in FlaskErrorCall().Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var close = MatchPy(text, open);
                if (close < 0) continue;
                var parts = SplitPy(text[(open + 1)..close]);
                if (parts.Count < 2) continue;
                var h = parts[1].Part.Trim();
                found.Add(new FlaskHook("error", !blueprints.Contains(m.Groups[1].Value), m.Groups[1].Value, h, x.Callable(h, path), path, m.Index, $"{m.Groups[1].Value}.register_error_handler", parts[0].Part.Trim()));
            }
            hooks.AddRange(found.OrderBy(h => h.Pos));
        }

        // ---- WSGI 中间件：后包的在最外层 ----
        var wraps = FlaskWsgi().Matches(appText).Where(m => m.Groups[1].Value == app.Var).ToList();
        var middlewareNodes = new List<FlowNodeDto>();
        var middlewarePosts = new List<List<FlowNodeDto>>();
        var middlewareSymbols = new List<MapSymbol>();
        foreach (var m in Enumerable.Reverse(wraps))
        {
            var expr = m.Groups[2].Value;
            var name = LastSegment(expr);
            var where = Py.Where(app.Path, appText, m.Index);
            var line = LineAt(appText, m.Index);
            if (x.Callable(name, app.Path) is { Kind: SymbolKind.Class } cls && x.Method(cls, "__call__") is { } call)
            {
                middlewareSymbols.Add(cls);
                var (node, post) = SplitNode(x, call, ["app", "wsgi_app"], new Regex(@"\bself\s*\.\s*(?:app|wsgi_app)\s*\("), StageMiddleware, $"自研 WSGI 中间件（{app.Var}.wsgi_app = …，{where}）", app.Path, line);
                middlewareNodes.Add(node);
                middlewarePosts.Add(post);
            }
            else
            {
                middlewareNodes.Add(Node(null, name, expr, "external-group", StageMiddleware, $"WSGI 中间件（框架 / 第三方，{where}）", [], app.Path, line));
                middlewarePosts.Add([]);
            }
        }

        // ---- 钩子节点 ----
        var tailSymbols = new List<MapSymbol>();
        var hookSymbols = new List<MapSymbol>();
        FlowNodeDto HookNode(FlaskHook h, string actor, string note)
        {
            var where = Py.Where(h.Path, x.Text(h.Path)!, h.Pos);
            var scope = h.AppLevel ? "" : $"；只对蓝图 {h.Receiver} 的路由";
            if (h.Fn is null) return Node(null, h.Name, h.Name, "external-group", actor, $"{note}（{h.How}，{where}）{scope}；没找到函数定义", [], h.Path, LineAt(x.Text(h.Path)!, h.Pos));
            (actor == StageTail ? tailSymbols : hookSymbols).Add(h.Fn);
            return x.FunctionNode(h.Fn, actor, $"{note}（{h.How}，{where}）{scope}");
        }
        var before = hooks.Where(h => h.Kind == "before").OrderBy(h => h.AppLevel ? 0 : 1).ToList();
        var beforeNodes = before.Select(h => HookNode(h, StageInterceptor, "before_request：返回非 None 就直接作为响应，不进视图")).ToList();
        IEnumerable<FlaskHook> Reversed(string kind) =>
            hooks.Where(h => h.Kind == kind && !h.AppLevel).Reverse().Concat(hooks.Where(h => h.Kind == kind && h.AppLevel).Reverse());
        var errorNodes = hooks.Where(h => h.Kind == "error").OrderBy(h => h.AppLevel ? 1 : 0)
            .Select(h => HookNode(h, StageTail, $"出错时：{h.Exc ?? "?"}")).ToList();
        var afterNodes = Reversed("after").Select(h => HookNode(h, StageTail, "after_request：拿到响应、可改写后返回（倒序）")).ToList();
        var teardownNodes = Reversed("teardown").Select(h => HookNode(h, StageTail, "teardown_request：请求结束总会执行（出错也执行，拿到异常）")).ToList();

        // ---- 组装 ----
        var routeNode = x.Http.Count > 0 ? Node(null, "路由", "", "controllers", StageController, null, [], null, null, x.Http.Count) : null;
        var rootKids = new List<FlowNodeDto>(middlewareNodes);
        rootKids.AddRange(beforeNodes);
        if (routeNode is not null) rootKids.Add(routeNode);
        rootKids.AddRange(errorNodes);
        rootKids.AddRange(afterNodes);
        rootKids.AddRange(teardownNodes);
        for (var i = middlewarePosts.Count - 1; i >= 0; i--) rootKids.AddRange(middlewarePosts[i]);
        foreach (var n in middlewarePosts.SelectMany(p => p)) if (n.SymbolId is { } id && x.ById.TryGetValue(id, out var ps)) tailSymbols.Add(ps);
        var rootActor = middlewareNodes.Count > 0 ? StageMiddleware : beforeNodes.Count > 0 ? StageInterceptor : StageController;
        var root = Node(null, "HTTP 请求", "", "entry", rootActor, null, rootKids, null, null);
        var stages = new List<FrameworkStageDto>();
        if (middlewareNodes.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "中间件", true, x.Dtos(middlewareSymbols)));
        if (beforeNodes.Count > 0) stages.Add(new FrameworkStageDto(StageInterceptor, "请求钩子", true, x.Dtos(hookSymbols)));
        if (routeNode is not null) stages.Add(new FrameworkStageDto(StageController, "路由", true, x.EntrySymbols(x.Http)));
        var hasTail = errorNodes.Count + afterNodes.Count + teardownNodes.Count > 0 || middlewarePosts.Any(p => p.Count > 0);
        if (hasTail) stages.Add(new FrameworkStageDto(StageTail, "收尾", true, x.Dtos(tailSymbols)));

        // ---- Notes ----
        notes.Add(wraps.Count > 0
            ? $"WSGI 中间件按 {app.Var}.wsgi_app = X({app.Var}.wsgi_app) 倒序排：后包的在最外层、请求先经过它"
            : $"没有 {app.Var}.wsgi_app = X(...)：没有 WSGI 中间件");
        if (blueprints.Count > 0) notes.Add($"{blueprints.Count} 个蓝图（{string.Join("、", blueprints.Take(6))}{(blueprints.Count > 6 ? " …" : "")}），register_blueprint {registered} 处；蓝图上的钩子只对它自己的路由");
        if (hooks.Count == 0) notes.Add("没有 before_request / after_request / teardown_request / errorhandler 钩子");
        else notes.Add("before_request 应用级在前、蓝图在后，各按注册顺序；收尾先是 errorhandler（蓝图的先查），再 after_request、teardown_request（蓝图在前，各自倒序）");
        if (routeNode is null) notes.Add("没有发现 HTTP 入口（@app.route / @bp.get …）");
        return Assemble(x.Container, "HTTP 请求", root, stages, [], notes,
            rootActor == StageMiddleware ? "中间件" : rootActor == StageInterceptor ? "请求钩子" : "路由");
    }
}
