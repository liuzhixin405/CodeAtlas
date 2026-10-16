using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Python 入口（符号的 StartLine 是 def 行，装饰器在它上面，按文件顺扫把装饰器挂到下一个 def / class 上）：
/// FastAPI（@app.get / @router.post…，APIRouter(prefix=…) 与 include_router(…, prefix=…) 的前缀链，常量 settings.API_V1_STR 在仓库里找唯一赋值）、
/// Flask（@app.route(methods=[…]) / @bp.get，Blueprint(url_prefix=…)，register_blueprint 的 url_prefix 覆盖它）、
/// Django urls.py（path / re_path / url，include('a.urls') 前缀链，类视图 X.as_view() 的 get / post / list … 方法，函数视图）、
/// DRF router.register('articles', ViewSet)（list / create / retrieve / update / partial_update / destroy）、
/// gRPC add_XxxServicer_to_server(Impl(), server)、Celery @app.task / @shared_task、if __name__ == "__main__"（hosted）。
/// FastAPI / Flask / Django 的路由按框架原样拼接，保留尾斜杠（它们区分 /items 与 /items/）；Flask 的 &lt;int:id&gt; 与 Django 的 (?P&lt;id&gt;…) 写成 {id}。
/// 入口的 File / Line 是处理函数（方法）的位置。
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"^\s*(?:async\s+)?def\s+(\w+)|^\s*class\s+(\w+)")]
    private static partial Regex PyDefOrClass();
    [GeneratedRegex(@"^\s*from\s+(\.*)([\w.]*)\s+import\s+(.+)$")]
    private static partial Regex PyFromImport();
    [GeneratedRegex(@"^\s*import\s+([\w.]+)(?:\s+as\s+(\w+))?\s*$")]
    private static partial Regex PyImport();
    [GeneratedRegex(@"^\s*(\w+)\s*(?::\s*[\w.\[\]]+\s*)?=\s*(?:[\w.]+\.)?(APIRouter|FastAPI|Blueprint|Flask)\s*\(")]
    private static partial Regex PyRouterDecl();
    [GeneratedRegex(@"\b([\w.]+?)\s*\.\s*(include_router|register_blueprint)\s*\(")]
    private static partial Regex PyInclude();
    [GeneratedRegex(@"^@\s*([\w.]+?)\s*\.\s*(get|post|put|patch|delete|head|options|route|api_route)\s*\(")]
    private static partial Regex PyRouteDecorator();
    [GeneratedRegex(@"^@\s*(?:[\w.]+\.)?(task|shared_task)\b")]
    private static partial Regex PyCeleryDecorator();
    [GeneratedRegex(@"^if\s+__name__\s*==\s*['""]__main__['""]\s*:")]
    private static partial Regex PyMainGuard();
    [GeneratedRegex(@"^(?:main|serve|start|run|run_server|start_server|create_app|app|cli|entrypoint)$|^(?:serve|start|run)_", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherName();
    [GeneratedRegex(@"\b(?:[\w.]+\.)?add_(\w+)Servicer_to_server\s*\(")]
    private static partial Regex PyGrpcRegister();
    [GeneratedRegex(@"(\w+)Servicer\b")]
    private static partial Regex PyServicerBase();
    [GeneratedRegex(@"(?<![\w.])(path|re_path|url)\s*\(")]
    private static partial Regex DjangoPattern();
    [GeneratedRegex(@"(?<![\w.])(\w+)\s*=\s*(?:[\w.]+\.)?(\w*Router)\s*\(")]
    private static partial Regex DrfRouterDecl();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*register\s*\(")]
    private static partial Regex DrfRegister();
    [GeneratedRegex(@"\(\?P<(\w+)>(?:[^()]|\([^()]*\))*\)")]
    private static partial Regex DjangoNamedGroup();
    [GeneratedRegex(@"<(?:\w+:)?(\w+)>")]
    private static partial Regex PathConverter();
    [GeneratedRegex(@"lookup_field\s*=\s*['""](\w+)['""]")]
    private static partial Regex DrfLookupField();

    private static readonly Dictionary<string, string> ViewMethods = new(StringComparer.Ordinal)
    {
        ["get"] = "GET", ["post"] = "POST", ["put"] = "PUT", ["patch"] = "PATCH", ["delete"] = "DELETE", ["head"] = "HEAD", ["options"] = "OPTIONS",
        ["list"] = "GET", ["retrieve"] = "GET", ["create"] = "POST", ["update"] = "PUT", ["partial_update"] = "PATCH", ["destroy"] = "DELETE",
    };
    private static readonly HashSet<string> DrfCollectionActions = new(StringComparer.Ordinal) { "list", "create" };
    private static readonly HashSet<string> DrfDetailActions = new(StringComparer.Ordinal) { "retrieve", "update", "partial_update", "destroy" };
    /// <summary>DRF 通用视图 / mixin / ViewSet 基类自带的动作：子类没重写也照样对外提供（ListCreateAPIView 的 GET 走继承来的 list）。</summary>
    private static readonly Dictionary<string, string[]> DrfInheritedActions = new(StringComparer.Ordinal)
    {
        ["ListAPIView"] = ["list"], ["CreateAPIView"] = ["create"], ["RetrieveAPIView"] = ["retrieve"], ["DestroyAPIView"] = ["destroy"],
        ["UpdateAPIView"] = ["update", "partial_update"], ["ListCreateAPIView"] = ["list", "create"],
        ["RetrieveUpdateAPIView"] = ["retrieve", "update", "partial_update"], ["RetrieveDestroyAPIView"] = ["retrieve", "destroy"],
        ["RetrieveUpdateDestroyAPIView"] = ["retrieve", "update", "partial_update", "destroy"],
        ["ListModelMixin"] = ["list"], ["CreateModelMixin"] = ["create"], ["RetrieveModelMixin"] = ["retrieve"],
        ["UpdateModelMixin"] = ["update", "partial_update"], ["DestroyModelMixin"] = ["destroy"],
        ["ModelViewSet"] = ["list", "create", "retrieve", "update", "partial_update", "destroy"], ["ReadOnlyModelViewSet"] = ["list", "retrieve"],
    };
    // __main__ 文件里起了常驻进程的迹象：Web 服务器、gRPC server.start / wait_for_termination、事件循环、消费循环
    [GeneratedRegex(@"\b(?:uvicorn|gunicorn|hypercorn|waitress)\b|(?<!asyncio)\.run\s*\(|\bserve\w*\s*\(|serve_forever|wait_for_termination|\.start\s*\(\s*\)|run_simple|make_server|run_app|execute_from_command_line|run_forever|run_until_complete|while\s+True\b|start_consuming|\.listen\s*\(|\.poll\s*\(|\.worker_main\s*\(")]
    private static partial Regex PyLongRunning();
    // email_client.py / client.py / client_demo.py：调自己服务的示例 / 测试客户端
    [GeneratedRegex(@"(?:^|[_\-.])client$|^client[_\-]", RegexOptions.IgnoreCase)]
    private static partial Regex PyClientStem();

    internal static bool IsPython(string path) => path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>import 引入的本地名：指向一个模块文件，或某文件里的某个名字。</summary>
    private sealed record PyRef(string? ModuleFile, string? File, string? Name);

    private static void DiscoverPython(Scan scan)
    {
        // 记下有 HTTP 入口的容器：同容器里不起服务的 __main__ 文件是一次性脚本（见文件末尾 __main__ 的裁定）
        var httpContainers = new HashSet<string>(StringComparer.Ordinal);
        var add = scan.Add;
        scan = scan with { Add = e => { if (e.Kind == "http") httpContainers.Add(e.ContainerKey); add(e); } };
        var mains = new List<(string File, MapSymbol Target, int Line, string Name, IReadOnlyList<ArchNode> Owners)>();
        var pySymbols = scan.Symbols.Where(s => IsPython(s.Path) && !PipelineBuilder.IsTest(s.Path, "")).ToList();
        if (pySymbols.Count == 0) return;
        var files = pySymbols.Select(s => s.Path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var fileSet = files.ToHashSet(StringComparer.Ordinal);
        var callables = pySymbols.Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method).ToList();
        var atLine = callables.GroupBy(s => (s.Path, s.StartLine)).ToDictionary(g => g.Key, g => g.First());
        var classes = pySymbols.Where(s => s.Kind == SymbolKind.Class).ToList();
        var modules = pySymbols.Where(s => s.Kind == SymbolKind.Module).GroupBy(s => s.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        static string ModuleName(string file)
        {
            var m = file[..^3];
            if (m.EndsWith("/__init__", StringComparison.Ordinal)) m = m[..^"/__init__".Length];
            return m.Replace('/', '.');
        }
        var moduleNames = files.ToDictionary(f => f, ModuleName, StringComparer.Ordinal);

        // 点分模块名 → 文件。绝对导入按后缀匹配（仓库里的包根可能是 backend/），多个候选取与导入方目录前缀最长的
        string? ResolveModule(string dotted, string fromFile, int level)
        {
            if (level > 0)
            {
                var dir = MapBuilder.DirectoryOf(fromFile);
                for (var i = 1; i < level; i++) dir = MapBuilder.DirectoryOf(dir);
                var rel = dotted.Length == 0 ? dir : (dir.Length == 0 ? "" : dir + "/") + dotted.Replace('.', '/');
                if (fileSet.Contains(rel + ".py")) return rel + ".py";
                if (fileSet.Contains(rel + "/__init__.py")) return rel + "/__init__.py";
                return null;
            }
            if (dotted.Length == 0) return null;
            return files.Where(f => moduleNames[f] == dotted || moduleNames[f].EndsWith("." + dotted, StringComparison.Ordinal))
                .OrderByDescending(f => CommonDirLength(f, fromFile)).ThenBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        }

        var importsCache = new Dictionary<string, Dictionary<string, PyRef>>(StringComparer.Ordinal);
        Dictionary<string, PyRef> ImportsOf(string file)
        {
            if (importsCache.TryGetValue(file, out var cached)) return cached;
            var imports = new Dictionary<string, PyRef>(StringComparer.Ordinal);
            var lines = scan.ReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var from = PyFromImport().Match(lines[i]);
                if (from.Success)
                {
                    var names = from.Groups[3].Value;
                    // from x import (\n a,\n b\n)
                    for (var j = i + 1; names.Contains('(') && !names.Contains(')') && j < lines.Length; j++) names += " " + lines[j];
                    var level = from.Groups[1].Value.Length;
                    var mod = from.Groups[2].Value;
                    foreach (var part in names.Replace("(", " ").Replace(")", " ").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var bits = part.Split(" as ", StringSplitOptions.TrimEntries);
                        var name = bits[0];
                        if (name.Length == 0 || name == "*" || !Regex.IsMatch(name, @"^\w+$")) continue;
                        var alias = bits.Length > 1 ? bits[1] : name;
                        var asModule = ResolveModule(mod.Length == 0 ? name : mod + "." + name, file, level);
                        imports[alias] = asModule is not null ? new PyRef(asModule, null, null) : new PyRef(null, ResolveModule(mod, file, level), name);
                    }
                    continue;
                }
                var imp = PyImport().Match(lines[i]);
                if (imp.Success && ResolveModule(imp.Groups[1].Value, file, 0) is { } target)
                    imports[imp.Groups[2].Success ? imp.Groups[2].Value : imp.Groups[1].Value] = new PyRef(target, null, null);
            }
            return importsCache[file] = imports;
        }

        // 表达式（items.router / api_router / app）→ 它定义所在的 (文件, 名字)
        (string File, string Name) ResolveRef(string file, string expr)
        {
            expr = expr.Trim();
            var parts = expr.Split('.');
            var imports = ImportsOf(file);
            if (parts.Length >= 2 && imports.TryGetValue(parts[0], out var m) && m.ModuleFile is not null) return (m.ModuleFile, parts[^1]);
            if (parts.Length == 1 && imports.TryGetValue(expr, out var s) && s.File is not null && s.Name is not null) return (s.File, s.Name);
            return (file, parts[^1]);
        }

        // 字符串值：字面量原样；常量（settings.API_V1_STR / API_PREFIX）在仓库的 .py 里找唯一的字面量赋值；拿不到 → ""
        var constCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        string StringValue(string? expr)
        {
            if (expr is null) return "";
            if (ArgParser.Literal(expr) is { } lit && !expr.TrimStart().StartsWith('f')) return lit;
            var name = Regex.Match(expr.Trim(), @"(\w+)\s*$");
            if (!name.Success) return "";
            var key = name.Groups[1].Value;
            if (!constCache.TryGetValue(key, out var value))
            {
                var assign = new Regex($@"^\s*{Regex.Escape(key)}\s*(?::[^=]+)?=\s*[rRuU]?(['""])([^'""]*)\1\s*(?:#.*)?$");
                var values = files.SelectMany(f => scan.ReadLines(f)).Select(l => assign.Match(l)).Where(m => m.Success).Select(m => m.Groups[2].Value).Distinct(StringComparer.Ordinal).ToList();
                constCache[key] = value = values.Count == 1 ? values[0] : null;
            }
            return value ?? "";
        }

        MapSymbol? ClassNamed(string name, string? preferFile, string fromFile)
        {
            var dir = MapBuilder.DirectoryOf(fromFile);
            return classes.Where(c => c.Name == name)
                .OrderBy(c => c.Path == preferFile ? 0 : c.Path == fromFile ? 1 : MapBuilder.DirectoryOf(c.Path) == dir ? 2 : 3)
                .ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault();
        }
        MapSymbol? FunctionNamed(string name, string? preferFile, string fromFile)
        {
            var dir = MapBuilder.DirectoryOf(fromFile);
            return callables.Where(c => c.Kind == SymbolKind.Function && c.Name == name)
                .OrderBy(c => c.Path == preferFile ? 0 : c.Path == fromFile ? 1 : MapBuilder.DirectoryOf(c.Path) == dir ? 2 : 3)
                .ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault();
        }

        // ── FastAPI / Flask：路由器对象与 include 链 ──
        var routers = new Dictionary<(string File, string Name), (string Kind, string Prefix)>();
        var includes = new List<((string File, string Name) Parent, (string File, string Name) Child, string? Prefix, bool Blueprint)>();
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var chunk = string.Join('\n', lines[i..Math.Min(lines.Length, i + 8)]);
                var decl = PyRouterDecl().Match(lines[i]);
                if (decl.Success)
                {
                    var (args, _) = ArgParser.SplitArgs(chunk, decl.Index + decl.Length - 1);
                    var kind = decl.Groups[2].Value;
                    var prefix = StringValue(ArgParser.Keyword(args, kind == "Blueprint" ? "url_prefix" : "prefix"));
                    routers[(file, decl.Groups[1].Value)] = (kind, prefix);
                }
                foreach (Match inc in PyInclude().Matches(lines[i]))
                {
                    var hash = RouteLinker.PyCommentStart(lines[i]);
                    if (hash >= 0 && hash < inc.Index) continue;
                    var (args, _) = ArgParser.SplitArgs(chunk, inc.Index + inc.Length - 1);
                    var positional = args.Where(a => !ArgParser.IsKeyword(a)).ToList();
                    if (positional.Count == 0) continue;
                    var blueprint = inc.Groups[2].Value == "register_blueprint";
                    var prefixExpr = ArgParser.Keyword(args, blueprint ? "url_prefix" : "prefix");
                    includes.Add((ResolveRef(file, inc.Groups[1].Value), ResolveRef(file, positional[0]), prefixExpr is null ? null : StringValue(prefixExpr), blueprint));
                }
            }
        }
        var routerPrefixMemo = new Dictionary<(string, string), List<string>>();
        List<string> RouterPrefixes((string File, string Name) key, int depth)
        {
            if (routerPrefixMemo.TryGetValue(key, out var memo)) return memo;
            var own = routers.TryGetValue(key, out var r) ? r.Prefix : "";
            var incs = includes.Where(x => x.Child == key).ToList();
            if (incs.Count == 0 || depth > 8) return [own];
            var result = new List<string>();
            foreach (var inc in incs)
                foreach (var parent in RouterPrefixes(inc.Parent, depth + 1))
                    result.Add(ConcatPath(parent, inc.Blueprint ? inc.Prefix ?? own : ConcatPath(inc.Prefix ?? "", own))); // Flask：注册时的 url_prefix 覆盖 Blueprint 的
            return routerPrefixMemo[key] = result.Distinct(StringComparer.Ordinal).ToList();
        }

        // ── 装饰器：路由、Celery 任务；以及 gRPC 注册与 __main__ ──
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            var owners = scan.ContainersOf(file);
            if (owners.Count == 0) continue;
            var mentionsFastApi = Array.Exists(lines, l => l.Contains("fastapi", StringComparison.Ordinal));
            foreach (var (defLine, decorators) in Decorated(lines))
            {
                var handler = atLine.GetValueOrDefault((file, defLine));
                if (handler is null) continue;
                foreach (var (text, _) in decorators)
                {
                    var route = PyRouteDecorator().Match(text);
                    if (route.Success)
                    {
                        var (args, _) = ArgParser.SplitArgs(text, route.Index + route.Length - 1);
                        var path = args.Where(a => !ArgParser.IsKeyword(a)).Select(ArgParser.Literal).FirstOrDefault()
                                   ?? ArgParser.Literal(ArgParser.Keyword(args, "path") ?? ArgParser.Keyword(args, "rule") ?? "");
                        if (path is null) continue;
                        var verb = route.Groups[2].Value;
                        var methods = verb is "route" or "api_route"
                            ? (ArgParser.Keyword(args, "methods") is { } ms ? Regex.Matches(ms, @"['""](\w+)['""]").Select(m => m.Groups[1].Value.ToUpperInvariant()).ToList() : ["GET"])
                            : [verb.ToUpperInvariant()];
                        var key = ResolveRef(file, route.Groups[1].Value);
                        var kind = routers.TryGetValue(key, out var r) ? r.Kind : mentionsFastApi ? "FastAPI" : "Flask";
                        var ruleId = kind is "FastAPI" or "APIRouter" ? "python.fastapi" : "python.flask";
                        foreach (var prefix in RouterPrefixes(key, 0))
                            foreach (var method in methods.Count == 0 ? ["GET"] : methods)
                                foreach (var c in owners)
                                    scan.Add(new EntryPoint(handler.Id, c.Key, ruleId, "http", PyRoute(ConcatPath(prefix, path)), method, handler.Path, handler.StartLine));
                        continue;
                    }
                    if (PyCeleryDecorator().IsMatch(text))
                        foreach (var c in owners) scan.Add(new EntryPoint(handler.Id, c.Key, "python.celery", "job", handler.Name, null, handler.Path, handler.StartLine));
                }
            }

            for (var i = 0; i < lines.Length; i++)
            {
                // gRPC：add_XxxServicer_to_server(Impl(), server) / (service, server)，service = Impl()
                // 生成的 *_pb2_grpc.py 里 def add_XxxServicer_to_server(servicer, server) 是定义，不是注册
                var reg = PyGrpcRegister().Match(lines[i]);
                if (reg.Success && !IsGeneratedPyStub(file) && !lines[i].TrimStart().StartsWith("def ", StringComparison.Ordinal)
                    && (RouteLinker.PyCommentStart(lines[i]) is var h && (h < 0 || h > reg.Index)))
                {
                    var (args, _) = ArgParser.SplitArgs(lines[i], reg.Index + reg.Length - 1);
                    var service = reg.Groups[1].Value;
                    // 实例的类找不到（工厂函数、条件赋值）时退到同文件里继承 XxxServicer 的类
                    var impl = args.Count >= 1 && PyImplClass(args[0], lines) is { } implName ? ClassNamed(implName, null, file) : null;
                    impl ??= classes.Where(c => c.Path == file && c.Name != service + "Servicer" && c.StartLine >= 1 && c.StartLine <= lines.Length
                                                && Regex.IsMatch(lines[c.StartLine - 1], $@"\(.*\b{Regex.Escape(service)}Servicer\b"))
                        .OrderBy(c => c.StartLine).FirstOrDefault();
                    if (impl is not null)
                    {
                        var servicers = ServicerNames(impl, scan, classes);
                        // 同一个实例又交给 add_HealthServicer_to_server(service, server)：它的 Check / Watch 是 grpc.health 服务的，不是本服务的方法
                        var instance = args.Count >= 1 ? args[0].Trim() : "";
                        var sharesHealth = instance.Length > 0 && Array.Exists(lines, l => Regex.IsMatch(l, $@"\badd_HealthServicer_to_server\s*\(\s*{Regex.Escape(instance)}\s*,"));
                        if (servicers.Count == 0 || servicers.Contains(service))
                            foreach (var m in scan.Children[impl.Id].Where(m => m.Kind == SymbolKind.Method && !m.Name.StartsWith('_')
                                                                                && !(m.Name is "Check" or "Watch" && sharesHealth) && !IsPyHealthMethod(m, scan.ReadLines)).OrderBy(m => m.StartLine))
                                foreach (var c in owners) scan.Add(new EntryPoint(m.Id, c.Key, "python.grpc", "grpc", service + "/" + m.Name, null, m.Path, m.StartLine));
                    }
                }
                // if __name__ == "__main__": → 模块里的 main()；没有就取 if 块里调用的、名字像启动函数（serve / start / run / create_app）的本模块顶层函数；
                // 都没有落在模块上（块里直接写启动代码，先调的 initProfiling 之类不是入口）
                if (PyMainGuard().IsMatch(lines[i]))
                {
                    var topLevel = callables.Where(s => s.Path == file && s.Kind == SymbolKind.Function && (s.ParentId is null || !scan.ById.TryGetValue(s.ParentId.Value, out var p) || p.Kind == SymbolKind.Module)).ToList();
                    var entry = topLevel.FirstOrDefault(s => s.Name == "main");
                    for (var j = i + 1; entry is null && j < lines.Length && (lines[j].Length == 0 || char.IsWhiteSpace(lines[j][0])); j++)
                        foreach (Match call in Regex.Matches(lines[j], @"\b(\w+)\s*\("))
                            if (LauncherName().IsMatch(call.Groups[1].Value) && (entry = topLevel.FirstOrDefault(s => s.Name == call.Groups[1].Value)) is not null) break;
                    var target = entry ?? modules.GetValueOrDefault(file);
                    if (target is null) continue;
                    mains.Add((file, target, entry?.StartLine ?? i + 1, MapSymbol.FileName(file)[..^3], owners));
                }
            }
        }

        DiscoverDjango(scan, files, ImportsOf, ResolveModule, ClassNamed, FunctionNamed);
        DiscoverPythonCli(scan, files, atLine, ResolveRef, FunctionNamed);

        // __main__ 的裁定（要等 HTTP 入口都出来）：
        // - 示例 / 测试客户端（email_client.py、client.py）：同容器里另有不是客户端的 __main__（真正的服务端）时不算入口——它只是拿来调本服务的；
        // - 一次性脚本：容器靠框架对外提供 HTTP（uvicorn / gunicorn 从命令行拉起，不经这个文件），文件里又没有起服务 / 常驻循环的迹象
        //   （initial_data.py 建初始数据、跑完就退）→ kind command，不是常驻服务；
        // - 其余照旧是 hosted。
        bool IsClient(string file) => PyClientStem().IsMatch(MapSymbol.FileName(file)[..^3]);
        foreach (var (file, target, line, name, owners) in mains)
            foreach (var c in owners)
            {
                if (IsClient(file) && mains.Any(m => !IsClient(m.File) && m.Owners.Any(o => o.Key == c.Key))) continue;
                var script = httpContainers.Contains(c.Key) && !scan.ReadLines(file).Any(l => PyLongRunning().IsMatch(l) && (RouteLinker.PyCommentStart(l) is var h && (h < 0 || h > PyLongRunning().Match(l).Index)));
                scan.Add(new EntryPoint(target.Id, c.Key, script ? "python.script" : "python.main", script ? "command" : "hosted", name, null, file, line));
            }
    }

    private static void DiscoverDjango(
        Scan scan, IReadOnlyList<string> files, Func<string, Dictionary<string, PyRef>> importsOf, Func<string, string, int, string?> resolveModule,
        Func<string, string?, string, MapSymbol?> classNamed, Func<string, string?, string, MapSymbol?> functionNamed)
    {
        var urlFiles = files.Where(f => Array.Exists(scan.ReadLines(f), l => l.Contains("urlpatterns", StringComparison.Ordinal))).ToList();
        if (urlFiles.Count == 0) return;

        var includes = new List<(string Parent, string Pattern, bool Regex, string Child)>();                 // include('a.urls')
        var routerIncludes = new List<(string File, string Router, string Pattern, bool Regex)>();              // include(router.urls)
        var views = new List<(string File, string Pattern, bool Regex, string Expr)>();
        var registrations = new List<(string File, string Router, string Prefix, string Expr)>();
        var trailing = new Dictionary<(string, string), bool>();
        var admins = new List<(string File, string Pattern, bool Regex, int Line)>();                        // admin.site.urls

        foreach (var file in urlFiles)
        {
            var text = string.Join('\n', scan.ReadLines(file).Select(l => RouteLinker.PyCommentStart(l) is var h and >= 0 ? l[..h] : l));
            foreach (Match r in DrfRouterDecl().Matches(text))
            {
                var (args, _) = ArgParser.SplitArgs(text, r.Index + r.Length - 1);
                trailing[(file, r.Groups[1].Value)] = ArgParser.Keyword(args, "trailing_slash")?.Trim() != "False";
            }
            foreach (Match reg in DrfRegister().Matches(text))
            {
                if (!trailing.ContainsKey((file, reg.Groups[1].Value))) continue;
                var (args, _) = ArgParser.SplitArgs(text, reg.Index + reg.Length - 1);
                if (args.Count >= 2 && ArgParser.Literal(args[0]) is { } prefix) registrations.Add((file, reg.Groups[1].Value, prefix.Trim('^', '$'), args[1]));
            }
            foreach (Match p in DjangoPattern().Matches(text))
            {
                var (args, _) = ArgParser.SplitArgs(text, p.Index + p.Length - 1);
                if (args.Count < 2 || ArgParser.Literal(args[0]) is not { } pattern) continue;
                var isRegex = p.Groups[1].Value != "path";
                var view = args[1];
                // url(r'^admin/', admin.site.urls)：Django 自带的管理后台，整站挂在这个前缀下
                if (Regex.IsMatch(view.Trim(), @"^(?:\w+\.)?admin\s*\.\s*site\s*\.\s*urls$"))
                {
                    admins.Add((file, pattern, isRegex, text.AsSpan(0, p.Index).Count('\n') + 1));
                    continue;
                }
                var inc = Regex.Match(view, @"^include\s*\(");
                if (inc.Success)
                {
                    var (inner, _) = ArgParser.SplitArgs(view, inc.Length - 1);
                    if (inner.Count == 0) continue;
                    if (ArgParser.Literal(inner[0]) is { } dotted && resolveModule(dotted, file, 0) is { } child) includes.Add((file, pattern, isRegex, child));
                    else if (Regex.Match(inner[0], @"^(\w+)\.urls$") is { Success: true } ru) routerIncludes.Add((file, ru.Groups[1].Value, pattern, isRegex));
                    continue;
                }
                views.Add((file, pattern, isRegex, view));
            }
        }

        var memo = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Prefixes(string file, int depth)
        {
            if (memo.TryGetValue(file, out var cached)) return cached;
            var parents = includes.Where(x => x.Child == file && x.Parent != file).ToList();
            if (parents.Count == 0 || depth > 8) return [""];
            var result = parents.SelectMany(x => Prefixes(x.Parent, depth + 1).Select(pp => pp + DjangoSegment(x.Pattern, x.Regex))).Distinct(StringComparer.Ordinal).ToList();
            return memo[file] = result;
        }

        void AddEntry(MapSymbol target, string route, string? method, string ruleId)
        {
            foreach (var c in scan.ContainersOf(target.Path)) scan.Add(new EntryPoint(target.Id, c.Key, ruleId, "http", route, method, target.Path, target.StartLine));
        }

        // 类视图 / 函数视图
        foreach (var (file, pattern, isRegex, expr) in views)
        {
            var imports = importsOf(file);
            foreach (var prefix in Prefixes(file, 0))
            {
                var route = "/" + (prefix + DjangoSegment(pattern, isRegex)).TrimStart('/');
                var asView = Regex.Match(expr, @"(?:^|\.)(\w+)\s*\.\s*as_view\s*\(");
                if (asView.Success)
                {
                    var name = asView.Groups[1].Value;
                    var cls = classNamed(name, PreferredFile(imports, expr, name), file);
                    if (cls is null) continue;
                    var methods = scan.Children[cls.Id].Where(m => m.Kind == SymbolKind.Method && ViewMethods.ContainsKey(m.Name)).OrderBy(m => m.StartLine).ToList();
                    var inherited = InheritedActions(cls, viewSet: false);
                    if (methods.Count == 0 && inherited.Count == 0) AddEntry(cls, route, null, "python.django");
                    foreach (var m in methods) AddEntry(m, route, ViewMethods[m.Name], "python.django");
                    // 通用视图继承来、子类没重写的动作（ListCreateAPIView 的 list → GET）：同一个 HTTP 方法已有重写的处理方法时不重复
                    foreach (var action in inherited.Where(a => methods.All(m => ViewMethods[m.Name] != ViewMethods[a])))
                        AddEntry(InheritedTarget(cls, action, methods), route, ViewMethods[action], "python.django");
                    continue;
                }
                var fn = Regex.Match(expr.Trim(), @"^(?:(\w+)\.)?(\w+)$");
                if (!fn.Success) continue;
                var preferred = fn.Groups[1].Success && imports.TryGetValue(fn.Groups[1].Value, out var mref) ? mref.ModuleFile : imports.TryGetValue(fn.Groups[2].Value, out var sref) ? sref.File : null;
                if (fn.Groups[1].Success && preferred is null) continue; // admin.site.urls 之类：前缀不是导入的模块
                if (functionNamed(fn.Groups[2].Value, preferred, file) is { } f && (preferred is null || f.Path == preferred)) AddEntry(f, route, null, "python.django");
            }
        }

        // DRF：router.register('articles', ViewSet) → list / create 在集合路径，retrieve / update / partial_update / destroy 在 {lookup}
        foreach (var (file, router, regPrefix, expr) in registrations)
        {
            var name = Regex.Match(expr.Trim(), @"(\w+)\s*$").Groups[1].Value;
            var cls = classNamed(name, PreferredFile(importsOf(file), expr, name), file);
            if (cls is null) continue;
            var slash = trailing.GetValueOrDefault((file, router), true) ? "/" : "";
            var lines = scan.ReadLines(cls.Path);
            var lookup = "pk";
            for (var i = Math.Max(0, cls.StartLine - 1); i < Math.Min(lines.Length, cls.EndLine); i++)
                if (DrfLookupField().Match(lines[i]) is { Success: true } lf) { lookup = lf.Groups[1].Value; break; }
            var mounts = routerIncludes.Where(x => x.File == file && x.Router == router).ToList();
            var bases = mounts.Count == 0
                ? Prefixes(file, 0)
                : mounts.SelectMany(x => Prefixes(file, 0).Select(p => p + DjangoSegment(x.Pattern, x.Regex))).ToList();
            var actions = scan.Children[cls.Id].Where(m => m.Kind == SymbolKind.Method && (DrfCollectionActions.Contains(m.Name) || DrfDetailActions.Contains(m.Name))).OrderBy(m => m.StartLine).ToList();
            // mixin / ModelViewSet 带来的、没重写的动作：路由器照样按它们生成路由（hasattr(viewset, action)）
            var inherited = InheritedActions(cls, viewSet: true).Where(a => actions.All(m => m.Name != a)).ToList();
            string ActionRoute(string collection, string action) => DrfCollectionActions.Contains(action) ? collection + slash : collection + "/{" + lookup + "}" + slash;
            foreach (var b in bases.Distinct(StringComparer.Ordinal))
            {
                var collection = "/" + (b + regPrefix.Trim('/')).TrimStart('/');
                if (actions.Count == 0 && inherited.Count == 0) AddEntry(cls, collection + slash, null, "python.drf");
                foreach (var m in actions) AddEntry(m, ActionRoute(collection, m.Name), ViewMethods[m.Name], "python.drf");
                foreach (var action in inherited) AddEntry(InheritedTarget(cls, action, actions), ActionRoute(collection, action), ViewMethods[action], "python.drf");
            }
        }

        // admin.site.urls：入口落在 urls.py 模块上（管理后台的视图在 Django 里，仓库里没有对应函数），File / Line 是那条 url()
        foreach (var (file, pattern, isRegex, line) in admins)
        {
            if (scan.Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Module && s.Path == file) is not { } module) continue;
            foreach (var prefix in Prefixes(file, 0))
                foreach (var c in scan.ContainersOf(file))
                    scan.Add(new EntryPoint(module.Id, c.Key, "python.django", "http", "/" + (prefix + DjangoSegment(pattern, isRegex)).TrimStart('/'), null, file, line));
        }

        // 类声明的基类里出现的 DRF 通用视图 / mixin / ViewSet（经仓库里的中间基类也算，最多 4 层）带来的动作。
        // as_view() 的类视图只认 XxxAPIView（mixin 只给 list() 这类方法，get / post 处理器是通用视图类定义的）；路由器注册的 ViewSet 认 mixin 与 XxxViewSet
        HashSet<string> InheritedActions(MapSymbol cls, bool viewSet, int depth = 0)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (depth > 4) return result;
            foreach (var b in PyBaseNames(scan.ReadLines(cls.Path), cls))
            {
                if (DrfInheritedActions.TryGetValue(b, out var acts))
                {
                    if (viewSet ? b.EndsWith("Mixin", StringComparison.Ordinal) || b.EndsWith("ViewSet", StringComparison.Ordinal) : b.EndsWith("APIView", StringComparison.Ordinal)) result.UnionWith(acts);
                }
                else if (classNamed(b, null, cls.Path) is { } parent && parent.Id != cls.Id)
                {
                    var own = scan.Children[parent.Id].Where(m => m.Kind == SymbolKind.Method && ViewMethods.ContainsKey(m.Name)).Select(m => m.Name);
                    result.UnionWith(InheritedActions(parent, viewSet, depth + 1).Concat(own.Where(n => DrfCollectionActions.Contains(n) || DrfDetailActions.Contains(n))));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// 继承来的 DRF 动作落在哪个符号上：partial_update 没重写但 update 重写了 → update（DRF 的 partial_update 就是 self.update(partial=True)，
    /// PATCH /api/user 实际跑的是子类的 update）；其余 → 类本身（处理代码在框架里）。
    /// </summary>
    private static MapSymbol InheritedTarget(MapSymbol cls, string action, IReadOnlyList<MapSymbol> overridden) =>
        action == "partial_update" && overridden.FirstOrDefault(m => m.Name == "update") is { } update ? update : cls;

    /// <summary>class X(a.B, C, metaclass=M): 的基类简名（B、C），声明可跨行。</summary>
    private static List<string> PyBaseNames(string[] lines, MapSymbol cls)
    {
        if (cls.StartLine < 1 || cls.StartLine > lines.Length) return [];
        var head = string.Join('\n', lines[(cls.StartLine - 1)..Math.Min(lines.Length, cls.StartLine + 8)]);
        var at = Regex.Match(head, $@"\bclass\s+{Regex.Escape(cls.Name)}\s*\(");
        if (!at.Success) return [];
        var (args, _) = ArgParser.SplitArgs(head, at.Index + at.Length - 1);
        return args.Where(a => a.Length > 0 && !ArgParser.IsKeyword(a)).Select(a => Regex.Replace(a, @"\[.*$", "").Trim()).Select(a => a[(a.LastIndexOf('.') + 1)..]).ToList();
    }

    /// <summary>gRPC 健康检查（grpc.health.v1 的 Check / Watch）：方法体里构造 HealthCheckResponse。和业务服务写在同一个实现类里时也不算业务入口。</summary>
    private static bool IsPyHealthMethod(MapSymbol m, Func<string, string[]> readLines)
    {
        if (m.Name is not ("Check" or "Watch")) return false;
        var lines = readLines(m.Path);
        for (var i = Math.Max(0, m.StartLine - 1); i < Math.Min(lines.Length, Math.Max(m.EndLine, m.StartLine + 3)); i++)
            if (lines[i].Contains("HealthCheckResponse", StringComparison.Ordinal) || lines[i].Contains("health_pb2", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsGeneratedPyStub(string path) =>
        path.EndsWith("_pb2_grpc.py", StringComparison.OrdinalIgnoreCase) || path.EndsWith("_pb2.py", StringComparison.OrdinalIgnoreCase);

    /// <summary>视图表达式里的名字由 import 引进时，它定义所在的文件（from .views import X → views.py；views.X → views 模块）。</summary>
    private static string? PreferredFile(Dictionary<string, PyRef> imports, string expr, string name)
    {
        if (imports.TryGetValue(name, out var s) && s.File is not null) return s.File;
        var head = Regex.Match(expr.Trim(), @"^(\w+)\.");
        return head.Success && imports.TryGetValue(head.Groups[1].Value, out var m) ? m.ModuleFile : null;
    }

    /// <summary>Django 的一段 URL 模式 → 路由模板片段：path() 的 &lt;int:pk&gt; → {pk}；re_path / url 去掉 ^ $ 与可选尾斜杠 /?，(?P&lt;x&gt;…) → {x}。</summary>
    internal static string DjangoSegment(string pattern, bool isRegex)
    {
        if (!isRegex) return PathConverter().Replace(pattern, "{$1}");
        var s = pattern.TrimStart('^');
        if (s.EndsWith('$')) s = s[..^1];
        s = DjangoNamedGroup().Replace(s, "{$1}");
        s = Regex.Replace(s, @"/\?$", "");
        return s.Replace("\\.", ".").Replace("\\-", "-");
    }

    /// <summary>两段 URL 原样拼接（框架就是这么拼的），只去掉接缝处重复的 /。</summary>
    private static string ConcatPath(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a.EndsWith('/') && b.StartsWith('/') ? a + b[1..] : a + b;
    }

    /// <summary>Flask 的 &lt;int:id&gt; → {id}，统一以 / 开头。</summary>
    private static string PyRoute(string path)
    {
        var p = PathConverter().Replace(path, "{$1}");
        return p.StartsWith('/') ? p : "/" + p;
    }

    /// <summary>注册实参 → 实现类名：Impl() / pkg.Impl(…)，或文件里赋过 Impl() 的变量（取最后一次）。</summary>
    private static string? PyImplClass(string expr, string[] lines)
    {
        expr = expr.Trim();
        var call = Regex.Match(expr, @"^(?:[\w.]+\.)?(\w+)\s*\(");
        if (call.Success) return call.Groups[1].Value;
        if (!Regex.IsMatch(expr, @"^\w+$")) return null;
        string? found = null;
        var assign = new Regex($@"^\s*{Regex.Escape(expr)}\s*=\s*(?:[\w.]+\.)?([A-Z]\w*)\s*\(");
        foreach (var l in lines) if (assign.Match(l) is { Success: true } m) found = m.Groups[1].Value;
        return found;
    }

    /// <summary>类声明（及同文件里的直接基类）的基类列表里出现的 XxxServicer 名字（去掉 Servicer 后缀）。</summary>
    private static HashSet<string> ServicerNames(MapSymbol cls, Scan scan, IReadOnlyList<MapSymbol> classes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lines = scan.ReadLines(cls.Path);
        string Header(MapSymbol c) => c.StartLine >= 1 && c.StartLine <= lines.Length ? lines[c.StartLine - 1] : "";
        var header = Header(cls);
        foreach (Match m in PyServicerBase().Matches(header)) names.Add(m.Groups[1].Value);
        var bases = Regex.Match(header, @"\(([^)]*)\)");
        if (bases.Success)
            foreach (var b in bases.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                if (classes.FirstOrDefault(c => c.Path == cls.Path && c.Name == b.Split('.')[^1] && c.Id != cls.Id) is { } parent)
                    foreach (Match m in PyServicerBase().Matches(Header(parent))) names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>按文件顺序把装饰器（可跨行，括号配平为止）挂到下一个 def / class 行上。返回 (def 行号, [(装饰器文本, 起始行号)])。</summary>
    private static IEnumerable<(int DefLine, List<(string Text, int Line)> Decorators)> Decorated(string[] lines)
    {
        var pending = new List<(string Text, int Line)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith('@'))
            {
                var start = i;
                var text = t;
                var open = text.IndexOf('(');
                while (open >= 0 && ArgParser.MatchClose(text, open) < 0 && i + 1 < lines.Length && i - start < 30) text += "\n" + lines[++i];
                pending.Add((text, start + 1));
                continue;
            }
            if (PyDefOrClass().IsMatch(lines[i]))
            {
                if (pending.Count > 0) yield return (i + 1, pending);
                pending = [];
                continue;
            }
            if (t.Length == 0 || t.StartsWith('#')) continue;
            pending = [];
        }
    }

    private static int CommonDirLength(string a, string b)
    {
        var sa = a.Split('/');
        var sb = b.Split('/');
        var n = 0;
        while (n < sa.Length - 1 && n < sb.Length - 1 && sa[n] == sb[n]) n++;
        return n;
    }
}
