using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>Node 框架流程 · Fastify：addHook 按生命周期排，register 的仓库插件在注册处展开，第三方插件画成一行。</summary>
public static partial class NodeFrameworkFlow
{
    private static readonly string[] FastifyPreHooks = ["onRequest", "preParsing", "preValidation", "preHandler"];
    private static readonly string[] FastifyPostHooks = ["preSerialization", "onSend", "onResponse"];
    private static readonly string[] FastifyErrorHooks = ["onError", "onTimeout", "onRequestAbort"];

    private sealed class FastifyAcc
    {
        public readonly List<(string Hook, int Seq, FlowNodeDto Node)> Hooks = [];
        public readonly List<FlowNodeDto> Plugins = [];
        public readonly List<FlowNodeDto> ErrorHandlers = [];
        public readonly List<FlowNodeDto> NotFound = [];
        public readonly List<MapSymbol> PreSymbols = [];
        public readonly List<MapSymbol> TailSymbols = [];
        public readonly List<string> RepoPlugins = [];
        public readonly List<string> ExternalPlugins = [];
        public int Seq;
    }

    private static FrameworkFlowDto BuildFastify(Ctx x, AppDecl app)
    {
        var t = x.Text(app.File)!;
        var acc = new FastifyAcc();
        ScanFastify(x, app.File, app.Pos, t.Code.Length, app.Var, null, "", 0, [], acc);

        int Phase(string hook, string[] order) => Array.IndexOf(order, hook) is var i and >= 0 ? i : order.Length;
        var pre = acc.Hooks.Where(h => FastifyPreHooks.Contains(h.Hook)).OrderBy(h => Phase(h.Hook, FastifyPreHooks)).ThenBy(h => h.Seq).Select(h => h.Node).ToList();
        var post = acc.Hooks.Where(h => FastifyPostHooks.Contains(h.Hook)).OrderBy(h => Phase(h.Hook, FastifyPostHooks)).ThenBy(h => h.Seq).Select(h => h.Node).ToList();
        var errors = acc.Hooks.Where(h => FastifyErrorHooks.Contains(h.Hook)).OrderBy(h => h.Seq).Select(h => h.Node).ToList();
        var middle = new List<FlowNodeDto>([.. acc.Plugins, .. pre]);
        var tail = new List<FlowNodeDto>([.. post, .. acc.NotFound, .. errors, .. acc.ErrorHandlers]);
        var httpCount = x.Entries.Count(e => e.Kind == "http");
        var placeholder = httpCount > 0 ? Node(null, "路由", "", "controllers", StageController, null, tail, null, null, httpCount) : null;
        var children = new List<FlowNodeDto>(middle);
        if (placeholder is not null) children.Add(placeholder); else children.AddRange(tail);
        var root = Node(null, "HTTP 请求", "", "entry", middle.Count > 0 ? StageMiddleware : StageController, null, children, null, null);

        var stages = new List<FrameworkStageDto>();
        if (middle.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "钩子", acc.Plugins.Count == 0, acc.PreSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));
        if (placeholder is not null) stages.Add(new FrameworkStageDto(StageController, "路由", true, x.EntrySymbols("http")));
        if (tail.Count > 0) stages.Add(new FrameworkStageDto(StageTail, "收尾", true, acc.TailSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));

        var notes = new List<string>
        {
            $"认出 Fastify（{x.Where(app.File, app.Pos)} 的 {app.Var} = Fastify(…)）",
            "钩子按 Fastify 生命周期排：onRequest → preParsing → preValidation → preHandler →（处理函数）→ preSerialization → onSend → onResponse；"
            + "同一钩子按注册顺序（register 的插件在注册处展开）；onError / setErrorHandler 是出错时",
        };
        if (acc.RepoPlugins.Count > 0) notes.Add($"仓库插件：{string.Join("、", acc.RepoPlugins)}");
        if (acc.ExternalPlugins.Count > 0)
            notes.Add($"第三方插件 {string.Join("、", acc.ExternalPlugins)}：内部挂的钩子看不到，画在钩子列最前（按注册顺序），这一列的先后不完全可信");
        if (acc.Hooks.Count == 0) notes.Add($"没找到 {app.Var}.addHook(...) 注册的钩子");
        if (errors.Count == 0 && acc.ErrorHandlers.Count == 0) notes.Add("没找到 setErrorHandler / onError 钩子：出错时走 Fastify 默认的错误处理（回 500 JSON）");
        if (httpCount == 0) notes.Add("没有发现 HTTP 路由入口");
        return Assemble(x.Container, "HTTP 请求", root, stages, [], notes, "入口");
    }

    /// <summary>[start, end) 里 receiver 上的 addHook / register / setErrorHandler / setNotFoundHandler；仓库插件进去接着找（4 层，receiver 换成插件函数的第一个形参）。</summary>
    private static void ScanFastify(Ctx x, string file, int start, int end, string receiver, string? scope, string prefix, int depth, HashSet<long> visited, FastifyAcc acc)
    {
        if (x.Text(file) is not { } t) return;
        foreach (var c in ReceiverCalls(t.Code, receiver, start, end))
        {
            var (args, _) = SplitArgsAt(t.Code, c.Open);
            string? Lit(int k) => k < args.Count ? ArgParser.Literal(t.NoComments[args[k].Start..args[k].End]) : null;
            var where = x.Where(file, c.Pos);
            var line = x.LineOf(file, c.Pos);
            var scopeNote = scope is null ? "" : "；" + scope;
            switch (c.Name)
            {
                case "addHook" when args.Count >= 2 && Lit(0) is { } hook:
                {
                    var actor = FastifyPreHooks.Contains(hook) ? StageMiddleware : StageTail;
                    var arg = Classify(x, file, args[1].Start, args[1].End, "fastify");
                    var note = (FastifyErrorHooks.Contains(hook) ? "出错时：" : "") + $"{hook} 钩子（{where}）" + scopeNote;
                    acc.Hooks.Add((hook, acc.Seq++, HookNode(x, hook, arg, file, line, actor, note, actor == StageMiddleware ? acc.PreSymbols : acc.TailSymbols)));
                    break;
                }
                case "setErrorHandler" when args.Count >= 1:
                    acc.ErrorHandlers.Add(HookNode(x, "setErrorHandler", Classify(x, file, args[0].Start, args[0].End, "fastify"), file, line, StageTail,
                        $"出错时（setErrorHandler，{where}）" + scopeNote, acc.TailSymbols));
                    break;
                case "setNotFoundHandler" when args.Count >= 1:
                    acc.NotFound.Add(HookNode(x, "setNotFoundHandler", Classify(x, file, args[^1].Start, args[^1].End, "fastify"), file, line, StageTail,
                        $"没有路由匹配时（setNotFoundHandler，{where}）" + scopeNote, acc.TailSymbols));
                    break;
                case "register" when args.Count >= 1:
                {
                    var optPrefix = args.Count > 1 && Regex.Match(t.NoComments[args[1].Start..args[1].End], @"\bprefix\s*:\s*['""`]([^'""`]*)['""`]") is { Success: true } pm ? pm.Groups[1].Value : "";
                    var full = JoinRoute(prefix, optPrefix);
                    var (fn, fnSymbol, label, package, wrapped) = PluginTarget(x, file, args[0].Start, args[0].End);
                    if (fn is null)
                    {
                        acc.ExternalPlugins.Add(label);
                        acc.Plugins.Add(Node(null, label, "", "external-group", StageMiddleware,
                            $"{(package is null ? "插件（没找到源码）" : $"第三方插件（{package}）")}（{where}）：内部挂的钩子看不到" + (optPrefix.Length > 0 ? $"；前缀 {full}" : "") + scopeNote, [], file, line));
                        break;
                    }
                    if (fnSymbol is not null && !visited.Add(fnSymbol.Id) || depth >= 4 || fn.Params.Count == 0 || fn.Params[0] == "{}") break;
                    var pluginScope = wrapped
                        ? $"经插件 {label}（fastify-plugin 包过，作用于外层）"
                        : $"插件 {label} 里注册：封装作用域，只对它注册的路由生效" + (full != "/" ? $"（前缀 {full}）" : "");
                    acc.RepoPlugins.Add(label + (wrapped ? "（fastify-plugin 包过，钩子作用于外层）" : "（封装作用域" + (full != "/" ? $"，前缀 {full}" : "") + "）"));
                    ScanFastify(x, fn.Path, fn.BodyStart, fn.End, fn.Params[0], scope is null ? pluginScope : scope + "；" + pluginScope, full, depth + 1, visited, acc);
                    break;
                }
            }
        }
    }

    /// <summary>register 的第一个实参 → 插件函数：仓库函数 / require('./x') 的默认导出 / fp(fn) 包过的 / 内联函数；第三方返回 fn = null 与包名。</summary>
    private static (JsFunction? Fn, MapSymbol? Symbol, string Label, string? Package, bool Wrapped) PluginTarget(Ctx x, string file, int s, int e)
    {
        var t = x.Text(file)!;
        var code = t.Code[s..e].Trim();
        // fp(fn) / fastifyPlugin(fn)
        var wrapCall = Regex.Match(code, @"^([\w$]+)\s*\(");
        if (wrapCall.Success && x.Imports(file).TryGetValue(wrapCall.Groups[1].Value, out var wrapImp) && wrapImp.Spec == "fastify-plugin")
        {
            var (inner, _) = SplitArgsAt(t.Code, s + t.Code[s..e].IndexOf('('));
            if (inner.Count > 0)
            {
                var r = PluginTarget(x, file, inner[0].Start, inner[0].End);
                return (r.Fn, r.Symbol, r.Label, r.Package, true);
            }
        }
        var arg = Classify(x, file, s, e, "fastify");
        switch (arg)
        {
            case InlineArg i:
                return (i.Fn, null, "（内联插件）", null, false);
            case FnArg f when x.FunctionOf(f.Symbol) is { } fn:
                return (fn, f.Symbol, x.Label(f.Symbol), null, WrappedByPlugin(x, f.Symbol));
            case ExternalArg ext:
                return (null, null, ext.Package ?? ext.Label, ext.Package, false);
            default:
                return (null, null, t.NoComments[s..e].Trim(), null, false);
        }
    }

    /// <summary>插件函数所在文件有没有用 fastify-plugin 包它（module.exports = fp(fn) / export default fp(fn) / fp(async function …)）。</summary>
    private static bool WrappedByPlugin(Ctx x, MapSymbol fn)
    {
        if (x.Text(fn.Path) is not { } t) return false;
        foreach (var (local, imp) in x.Imports(fn.Path))
            if (imp.Spec == "fastify-plugin" && Regex.IsMatch(t.Code, @"(?<![\w$.])" + Regex.Escape(local) + @"\s*\(\s*(?:" + Regex.Escape(fn.Name) + @"\b|async\b|function\b)"))
                return true;
        return false;
    }

    /// <summary>钩子 / 错误处理函数 → 一行，子节点是它的关键调用（回调风格的 done() 不算）。</summary>
    private static FlowNodeDto HookNode(Ctx x, string hook, UseArg arg, string file, int line, string actor, string note, List<MapSymbol> symbols)
    {
        switch (arg)
        {
            case InlineArg i:
            {
                var calls = x.CallsInSpan(file, i.Fn.Start, i.Fn.End, i.Fn.BodyStart);
                return Node(null, $"{hook}（内联）", "", "external-group", actor, note, HookRows(x, i.Fn, calls, file, actor), file, line);
            }
            case FnArg f:
            {
                symbols.Add(f.Symbol);
                var fn = x.FunctionOf(f.Symbol);
                var calls = x.CallsOf(f.Symbol);
                return Node(f.Symbol.Id, x.Label(f.Symbol), f.Symbol.QualifiedName, "call", actor, note,
                    fn is null ? Rows(calls.Where(JsKeep), f.Symbol.Path, actor, null) : HookRows(x, fn, calls, f.Symbol.Path, actor), file, line);
            }
            case ExternalArg ext:
                return Node(null, ext.Label, "", "external-group", actor, note + (ext.Package is { } p ? $"；第三方（{p}）" : ""), [], file, line);
            default:
                return Node(null, hook, "", "external-group", actor, note + "；没认出处理函数", [], file, line);
        }
    }

    private static List<FlowNodeDto> HookRows(Ctx x, JsFunction fn, List<FrameworkCall> calls, string path, string actor)
    {
        var done = fn.Params.FirstOrDefault(p => p is "done" or "next" or "cb" or "callback");
        var split = SplitAtNext(x, fn, calls, done);
        return Rows(split.Pre.Concat(split.Post).Concat(split.Finish), path, actor, null);
    }
}
