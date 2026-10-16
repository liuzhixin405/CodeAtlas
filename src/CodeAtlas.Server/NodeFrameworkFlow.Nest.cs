using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>Node 框架流程 · NestJS：main.ts 的全局注册、模块里的 APP_* provider 与 configure(consumer)，按 Nest 固定的请求生命周期排列。</summary>
public static partial class NodeFrameworkFlow
{
    /// <summary>一个全局增强器：种类（guard / interceptor / pipe / filter）、仓库里的类（没有为 null）、显示名、来源说明、注册处。</summary>
    private sealed record Enhancer(string Kind, MapSymbol? Class, string Label, string Source, string File, int Pos);

    private static readonly Dictionary<string, string> GlobalEnhancerKinds = new(StringComparer.Ordinal)
    {
        ["useGlobalGuards"] = "guard", ["useGlobalInterceptors"] = "interceptor", ["useGlobalPipes"] = "pipe", ["useGlobalFilters"] = "filter",
    };

    private static readonly Dictionary<string, string> AppProviderKinds = new(StringComparer.Ordinal)
    {
        ["APP_GUARD"] = "guard", ["APP_INTERCEPTOR"] = "interceptor", ["APP_PIPE"] = "pipe", ["APP_FILTER"] = "filter",
    };

    private static FrameworkFlowDto BuildNest(Ctx x, AppDecl app)
    {
        var t = x.Text(app.File)!;
        var code = t.Code;
        var acc = new Acc();
        var enhancers = new List<Enhancer>();
        string? globalPrefix = null;
        var events = new List<AppEvent>();
        var decl = NestDecl().Match(code, app.Pos);
        var createArgs = decl.Success ? SplitArgsAt(code, decl.Index + decl.Length - 1).Args : [];
        var moduleName = createArgs.Count > 0 ? code[createArgs[0].Start..createArgs[0].End].Trim() : "?";

        // NestFactory.create(AppModule, { cors: true }) / 选项是变量
        if (createArgs.Count > 1)
        {
            var opt = t.NoComments[createArgs[1].Start..createArgs[1].End].Trim();
            if (Regex.IsMatch(opt, @"^[\w$]+$") && Regex.Match(t.NoComments, @"(?<![\w$.])(?:const|let|var)\s+" + Regex.Escape(opt) + @"\s*(?::[^=]+)?=\s*(\{[^;]*\})") is { Success: true } ov)
                opt = ov.Groups[1].Value;
            if (Regex.IsMatch(opt, @"\bcors\s*:\s*(?:true|\{)"))
                events.Add(new AppEvent("use", app.File, decl.Index, null, [new ExternalArg("cors（create 选项）", "@nestjs/core", true, createArgs[1].Start, createArgs[1].End)], null, app.Var, null, false));
        }
        foreach (var c in ReceiverCalls(code, app.Var, app.Pos, code.Length))
        {
            var (args, _) = SplitArgsAt(code, c.Open);
            switch (c.Name)
            {
                case "use":
                    if (EventOf(x, app.File, c, app.Var, null, app.Pos, "nest") is { } ev) events.Add(ev);
                    break;
                case "enableCors":
                    events.Add(new AppEvent("use", app.File, c.Pos, null, [new ExternalArg("enableCors", "@nestjs/core", true, c.Pos, c.Open)], null, app.Var, null, false));
                    break;
                case "setGlobalPrefix" when args.Count > 0:
                    globalPrefix = ArgParser.Literal(t.NoComments[args[0].Start..args[0].End]);
                    break;
                default:
                    if (GlobalEnhancerKinds.TryGetValue(c.Name, out var kind))
                        foreach (var a in args)
                            enhancers.Add(EnhancerOf(x, kind, app.File, a.Start, a.End, $"{c.Name}（{x.Where(app.File, c.Pos)}）"));
                    break;
            }
        }
        var appNodes = ProcessScope(x, events, "", false, acc, "nest", 0, []).Select(p => p.Node).ToList();

        // APP_GUARD / APP_INTERCEPTOR / APP_PIPE / APP_FILTER provider
        var providerFiles = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in x.ScriptFiles)
        {
            if (x.Text(file) is not { } ft || !ft.Code.Contains("APP_", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(ft.Code, @"\bprovide\s*:\s*(APP_(?:GUARD|INTERCEPTOR|PIPE|FILTER))\b"))
            {
                var open = UnmatchedOpenBefore(ft.Code, m.Index, '{', '}');
                var close = open < 0 ? -1 : MatchClose(ft.Code, open);
                if (close < 0) continue;
                var obj = ft.Code[open..close];
                var kind = AppProviderKinds[m.Groups[1].Value];
                var source = $"{m.Groups[1].Value} provider（{x.Where(file, m.Index)}）";
                if (Regex.Match(obj, @"\buse(?:Class|Existing)\s*:\s*([\w$]+)") is { Success: true } uc)
                    enhancers.Add(new Enhancer(kind, x.ClassNamed(uc.Groups[1].Value, file), uc.Groups[1].Value, source, file, m.Index));
                else if (Regex.Match(obj, @"\buseValue\s*:\s*new\s+([\w$]+)") is { Success: true } uv)
                    enhancers.Add(new Enhancer(kind, x.ClassNamed(uv.Groups[1].Value, file), uv.Groups[1].Value, source, file, m.Index));
                else enhancers.Add(new Enhancer(kind, null, m.Groups[1].Value + "（useFactory）", source, file, m.Index));
                if (!providerFiles.TryGetValue(kind, out var set)) providerFiles[kind] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(file);
            }
        }

        // configure(consumer) { consumer.apply(A, B).exclude(…).forRoutes(…) }
        var moduleMw = new List<(string File, FlowNodeDto Node)>();
        foreach (var file in x.ScriptFiles)
        {
            if (x.Text(file) is not { } ft) continue;
            foreach (Match m in Regex.Matches(ft.Code, @"(?<![\w$.])configure\s*\(\s*([\w$]+)\s*(?::\s*[\w$.]+\s*)?\)\s*(?::\s*[\w$<>]+\s*)?\{"))
            {
                var bodyOpen = m.Index + m.Length - 1;
                var bodyClose = MatchClose(ft.Code, bodyOpen);
                if (bodyClose < 0) continue;
                var chain = ReceiverCalls(ft.Code, m.Groups[1].Value, bodyOpen, bodyClose);
                for (var i = 0; i < chain.Count; i++)
                {
                    if (chain[i].Name != "apply") continue;
                    var apply = chain[i];
                    var routes = new List<string>();
                    var excludes = 0;
                    for (var j = i + 1; j < chain.Count && chain[j].Name != "apply"; j++)
                    {
                        var (ra, _) = SplitArgsAt(ft.Code, chain[j].Open);
                        if (chain[j].Name == "forRoutes") routes.AddRange(ra.Select(r => RouteSpec(ft, r.Start, r.End)));
                        else if (chain[j].Name == "exclude") excludes += ra.Count;
                    }
                    var forRoutes = routes.Count == 0 ? "没写 forRoutes" : $"forRoutes {string.Join("、", routes.Take(3))}{(routes.Count > 3 ? $" 等 {routes.Count} 条" : "")}";
                    var where = x.Where(file, apply.Pos);
                    var (applied, _) = SplitArgsAt(ft.Code, apply.Open);
                    foreach (var a in applied)
                    {
                        var name = ft.Code[a.Start..a.End].Trim();
                        UseArg arg = Regex.IsMatch(name, @"^[\w$]+$") && x.ClassNamed(name, file) is { } cls
                            ? (x.Method(cls, "use") is { } use ? new FnArg(use, a.Start, a.End) : new UnknownArg(name, a.Start, a.End))
                            : Classify(x, file, a.Start, a.End, "nest");
                        var note = $"模块中间件（{where}，{forRoutes}{(excludes > 0 ? $"，exclude {excludes} 条" : "")}）";
                        if (arg is ExternalArg ext)
                        {
                            moduleMw.Add((file, Node(null, ext.Label, "", "external-group", StageMiddleware, note + (ext.Package is { } p ? $"；第三方（{p}）" : ""), [], file, x.LineOf(file, apply.Pos))));
                            continue;
                        }
                        if (AnalyzeMw(x, arg, "nest", "consumer") is not { } mw)
                        {
                            moduleMw.Add((file, Node(null, name, "", "external-group", StageMiddleware, note + "；没找到这个中间件类", [], file, x.LineOf(file, apply.Pos))));
                            continue;
                        }
                        if (mw.Symbol is { } ms) acc.MwSymbols.Add(ms);
                        var extra = mw.Notes.Count == 0 ? "" : "；" + string.Join("；", mw.Notes);
                        moduleMw.Add((file, Node(mw.Symbol?.Id, mw.Label, mw.Qn, mw.Kind, StageMiddleware, note + extra, Rows(mw.Split.Pre, mw.Path, StageMiddleware, null), file, x.LineOf(file, apply.Pos))));
                        acc.Posts.Add(Rows(mw.Split.Post, mw.Path, StageTail, $"{mw.Label} 后置"));
                        acc.Finishes.AddRange(Rows(mw.Split.Finish, mw.Path, StageTail, $"{mw.Label}：响应发完时（res.on('finish')）"));
                    }
                }
            }
        }
        // 根模块（create 的实参）所在文件的排前面，其余按路径
        var rootModuleFile = x.ClassNamed(moduleName, app.File)?.Path;
        var moduleFiles = moduleMw.Select(m => m.File).Distinct(StringComparer.Ordinal).ToList();
        var middleware = new List<FlowNodeDto>(appNodes);
        middleware.AddRange(moduleMw.OrderBy(m => m.File == rootModuleFile ? 0 : 1).ThenBy(m => m.File, StringComparer.Ordinal).Select(m => m.Node));

        // 守卫 / 拦截器 / 管道 / 过滤器
        var guards = new List<FlowNodeDto>();
        var interceptors = new List<FlowNodeDto>();
        var pipes = new List<FlowNodeDto>();
        var filters = new List<FlowNodeDto>();
        var interceptorPosts = new List<List<FlowNodeDto>>();
        var symbolsOf = new Dictionary<string, List<MapSymbol>>(StringComparer.Ordinal);
        foreach (var e in enhancers.OrderBy(e => e.File == app.File ? 0 : 1).ThenBy(e => e.File == app.File ? "" : e.File, StringComparer.Ordinal).ThenBy(e => e.Pos))
        {
            var (actor, method) = e.Kind switch
            {
                "guard" => (StageAspect, "canActivate"),
                "interceptor" => (StageInterceptor, "intercept"),
                "pipe" => (StagePipe, "transform"),
                _ => (StageTail, "catch"),
            };
            var target = e.Kind switch { "guard" => guards, "interceptor" => interceptors, "pipe" => pipes, _ => filters };
            var line = x.LineOf(e.File, e.Pos);
            var m = e.Class is { } cls ? x.Method(cls, method) : null;
            if (m is null)
            {
                var what = e.Class is null ? "Nest 内置 / 第三方" : $"仓库里的类，没找到 {method} 方法";
                target.Add(Node(e.Class?.Id, e.Label, e.Class?.QualifiedName ?? "", e.Class is null ? "external-group" : "call", actor,
                    (e.Kind == "filter" ? "出错时：" : "") + $"{what}（{e.Source}）", [], e.File, line));
                continue;
            }
            if (!symbolsOf.TryGetValue(e.Kind, out var syms)) symbolsOf[e.Kind] = syms = [];
            syms.Add(m);
            var calls = x.CallsOf(m);
            switch (e.Kind)
            {
                case "interceptor":
                {
                    var fn = x.FunctionOf(m);
                    var split = fn is null ? new BodySplit(calls.Where(JsKeep).ToList(), [], [], false, false) : SplitAtNext(x, fn, calls, fn.Params.ElementAtOrDefault(1) ?? "next", handle: true);
                    interceptors.Add(Node(m.Id, x.Label(m), m.QualifiedName, "call", actor, e.Source + (split.HasNext ? "" : "；没找到 next.handle()") + (split.Conditional ? "；有条件（有分支不调 next.handle()）" : ""),
                        Rows(split.Pre, m.Path, actor, null), e.File, line));
                    interceptorPosts.Add(Rows(split.Post, m.Path, StageTail, $"{x.Label(e.Class!)} 后置（next.handle() 之后）"));
                    break;
                }
                case "filter":
                {
                    var types = CatchTypes(x, e.Class!);
                    filters.Add(Node(m.Id, x.Label(m), m.QualifiedName, "call", actor,
                        $"出错时（异常过滤器，{e.Source}）" + (types is null ? "" : types.Length == 0 ? "；@Catch() 接全部异常" : $"；只接 {types}"),
                        Rows(calls.Where(JsKeep), m.Path, actor, null), e.File, line));
                    break;
                }
                default:
                    target.Add(Node(m.Id, x.Label(m), m.QualifiedName, "call", actor, e.Source, Rows(calls.Where(JsKeep), m.Path, actor, null), e.File, line));
                    break;
            }
        }

        // ---- 组装 ----
        var tail = new List<FlowNodeDto>();
        foreach (var g in Enumerable.Reverse(interceptorPosts)) tail.AddRange(g);
        tail.AddRange(filters);
        foreach (var g in Enumerable.Reverse(acc.Posts)) tail.AddRange(g);
        tail.AddRange(acc.AfterRoutes);
        tail.AddRange(acc.Errors);
        tail.AddRange(acc.Finishes);
        var httpCount = x.Entries.Count(e => e.Kind == "http");
        var placeholder = httpCount > 0 ? Node(null, "控制器", "", "controllers", StageController, null, tail, null, null, httpCount) : null;
        var children = new List<FlowNodeDto>([.. middleware, .. guards, .. interceptors, .. pipes]);
        if (placeholder is not null) children.Add(placeholder); else children.AddRange(tail);
        var rootActor = children.FirstOrDefault()?.Actor ?? StageController;
        var root = Node(null, "HTTP 请求", "", "entry", rootActor == StageTail ? StageController : rootActor, null, children, null, null);

        var tailSymbols = new List<MapSymbol>(acc.TailSymbols);
        tailSymbols.AddRange(symbolsOf.GetValueOrDefault("filter") ?? []);
        bool Known(string kind) => !(providerFiles.TryGetValue(kind, out var f) && f.Count > 1);
        var stages = new List<FrameworkStageDto>();
        if (middleware.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "中间件", moduleFiles.Count <= 1, acc.MwSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));
        if (guards.Count > 0) stages.Add(new FrameworkStageDto(StageAspect, "守卫", Known("guard"), (symbolsOf.GetValueOrDefault("guard") ?? []).Select(x.Dto).ToList()));
        if (interceptors.Count > 0) stages.Add(new FrameworkStageDto(StageInterceptor, "拦截器", Known("interceptor"), (symbolsOf.GetValueOrDefault("interceptor") ?? []).Select(x.Dto).ToList()));
        if (pipes.Count > 0) stages.Add(new FrameworkStageDto(StagePipe, "管道", Known("pipe"), (symbolsOf.GetValueOrDefault("pipe") ?? []).Select(x.Dto).ToList()));
        if (placeholder is not null) stages.Add(new FrameworkStageDto(StageController, "控制器", true, x.EntrySymbols("http")));
        if (tail.Count > 0) stages.Add(new FrameworkStageDto(StageTail, "收尾", Known("interceptor"), tailSymbols.DistinctBy(s => s.Id).Select(x.Dto).ToList()));

        // ---- Notes ----
        var notes = new List<string>
        {
            $"认出 NestJS（{x.Where(app.File, app.Pos)} 的 {app.Var} = await NestFactory.create({moduleName})）" + (globalPrefix is null ? "" : $"；全局前缀 {JoinRoute(globalPrefix)}"),
            "顺序按 Nest 固定的请求生命周期：中间件 → 守卫 → 拦截器（前置）→ 管道 → 控制器 → 拦截器（后置，next.handle() 之后，倒序）→ 异常过滤器（出错时）；"
            + "同一类里 main.ts 的 useGlobalXxx 按书写顺序在前，模块里的 APP_XXX provider 在后",
        };
        if (moduleFiles.Count > 1)
            notes.Add($"模块中间件来自 {moduleFiles.Count} 个模块（{string.Join("、", moduleFiles.Select(MapSymbol.FileName))}），各自只对 forRoutes 的路由生效；模块之间的先后取决于导入顺序，没排");
        foreach (var (kind, files) in providerFiles.Where(p => p.Value.Count > 1))
            notes.Add($"{kind} 的 APP_ provider 分在 {files.Count} 个模块里，模块之间的先后没排");
        var missing = new List<string>();
        if (guards.Count == 0) missing.Add("守卫");
        if (interceptors.Count == 0) missing.Add("拦截器");
        if (pipes.Count == 0) missing.Add("管道");
        if (missing.Count > 0) notes.Add($"没找到全局的{string.Join("、", missing)}（useGlobalXxx / APP_XXX provider）");
        if (filters.Count == 0) notes.Add("没有全局异常过滤器：出错时走 Nest 内置的 BaseExceptionFilter（HttpException 按状态码回，其余 500）");
        if (middleware.Count == 0) notes.Add("没有中间件（main.ts 的 app.use、configure(consumer) 的 consumer.apply）");
        var scoped = ControllerEnhancers(x);
        if (scoped.Count > 0)
            notes.Add($"控制器 / 方法级：{string.Join("，", scoped.Select(s => $"@{s.Decorator} {s.Count} 处（{string.Join("、", s.Names)}）"))}——只对所在控制器 / 方法生效，没画进骨架");
        if (httpCount == 0) notes.Add("没有发现 HTTP 入口（@Controller + @Get / @Post…）");
        return Assemble(x.Container, "HTTP 请求", root, stages, [], notes, "入口");
    }

    /// <summary>全局注册的一个实参：new X(…) / X / app.get(X) → 仓库里的类或外部的名字。</summary>
    private static Enhancer EnhancerOf(Ctx x, string kind, string file, int s, int e, string source)
    {
        var t = x.Text(file)!;
        var text = t.Code[s..e].Trim();
        var m = Regex.Match(text, @"^new\s+([\w$]+)") is { Success: true } n ? n : Regex.Match(text, @"^(?:[\w$]+\s*\.\s*get\s*\(\s*)?([\w$]+)");
        var name = m.Success ? m.Groups[1].Value : text;
        return new Enhancer(kind, x.ClassNamed(name, file), name, source, file, s);
    }

    /// <summary>pos 往前第一个没配对的开括号 open。</summary>
    private static int UnmatchedOpenBefore(string code, int pos, char open, char close)
    {
        var depth = 0;
        for (var i = pos - 1; i >= 0; i--)
        {
            if (code[i] == close) depth++;
            else if (code[i] == open) { if (depth == 0) return i; depth--; }
        }
        return -1;
    }

    /// <summary>forRoutes 的一个实参的简写：'*' / OrdersController / { path: 'user', method: RequestMethod.GET } → GET user。</summary>
    private static string RouteSpec(JsSource t, int s, int e)
    {
        var nc = t.NoComments[s..e].Trim();
        if (ArgParser.Literal(nc) is { } lit) return lit;
        if (nc.StartsWith('{'))
        {
            var path = Regex.Match(nc, @"\bpath\s*:\s*['""`]([^'""`]*)['""`]");
            var method = Regex.Match(nc, @"\bmethod\s*:\s*(?:RequestMethod\s*\.\s*)?([\w$]+)");
            return (method.Success ? method.Groups[1].Value + " " : "") + (path.Success ? path.Groups[1].Value : "?");
        }
        return Regex.Replace(nc, @"\s+", " ");
    }

    /// <summary>类声明前的 @Catch(A, B)：返回 "A、B"；@Catch() 返回 ""；没有装饰器返回 null。</summary>
    private static string? CatchTypes(Ctx x, MapSymbol cls)
    {
        if (x.Text(cls.Path) is not { } t) return null;
        var (s, e) = t.Span(Math.Max(1, cls.StartLine - 4), cls.StartLine);
        var m = Regex.Match(t.Code[s..e], @"@\s*Catch\s*\(([^()]*)\)");
        if (!m.Success) return null;
        return string.Join("、", m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>控制器 / 方法上的 @UseGuards / @UseInterceptors / @UsePipes / @UseFilters：每种的处数与用到的名字。</summary>
    private static List<(string Decorator, int Count, List<string> Names)> ControllerEnhancers(Ctx x)
    {
        var stats = new Dictionary<string, (int Count, List<string> Names)>(StringComparer.Ordinal);
        foreach (var file in x.ScriptFiles)
        {
            if (x.Text(file) is not { } t || !t.Code.Contains("@Use", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(t.Code, @"@\s*(UseGuards|UseInterceptors|UsePipes|UseFilters)\s*\("))
            {
                var (args, _) = SplitArgsAt(t.Code, m.Index + m.Length - 1);
                var dec = m.Groups[1].Value;
                if (!stats.TryGetValue(dec, out var st)) st = (0, []);
                st.Count++;
                foreach (var a in args)
                {
                    var text = t.Code[a.Start..a.End].Trim();
                    var n = Regex.Match(text, @"^(?:new\s+)?([\w$]+)");
                    if (n.Success && !st.Names.Contains(n.Groups[1].Value)) st.Names.Add(n.Groups[1].Value);
                }
                stats[dec] = st;
            }
        }
        return new[] { "UseGuards", "UseInterceptors", "UsePipes", "UseFilters" }.Where(stats.ContainsKey).Select(d => (d, stats[d].Count, stats[d].Names)).ToList();
    }
}
