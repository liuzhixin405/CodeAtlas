using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Node 后端与命令行的入口（只看 node-service / node-cli 容器里的 .js / .ts / .mjs / .cjs，不含 .d.ts 与测试；源码文本 + 符号的 Path / 行范围 / 名字定位）：
/// <list type="bullet">
/// <item>Express（<c>app.get('/x', h)</c> / <c>router.post</c> / <c>router.route('/x').get(h).post(h2)</c>；<c>app.use('/prefix', router)</c> 前缀尽力而为：
/// 同文件的路由器变量、import / require 进来的路由器模块、<c>app.use('/p', require('./r'))</c>）、
/// Koa-router（<c>new Router({ prefix: '/users' })</c>，<c>router.use('/x', other.routes())</c>）、
/// Fastify（<c>fastify.get('/x', opts?, h)</c>、<c>fastify.route({ method, url, handler })</c>、<c>fastify.register(plugin, { prefix })</c> 给插件文件加前缀）：
/// rule node.express / node.koa / node.fastify 按容器 package.json 的依赖定，kind http，File / Line 是注册处（与 Go / Minimal API 一致）。
/// 处理函数是名字（h / ctrl.list / asyncHandler(ctrl.list)）就按名字找（同文件 → import 的文件 → 同容器唯一），是内联函数就落在包含注册的函数上，再退回文件模块。</item>
/// <item>NestJS（<c>@Controller('users')</c> 类 + <c>@Get(':id')</c> … 方法；<c>app.setGlobalPrefix('api')</c> 加到同容器全部控制器前）：kind http，File / Line 是方法。</item>
/// <item>gRPC（<c>server.addService(proto.X.service, { method: impl })</c>，对象字面量的简写 / 键值 / 方法，<c>X.Y.bind(this)</c>；实参是变量时找同文件的对象字面量赋值）：
/// route = Service/Method（方法名转 PascalCase，与 proto 一致），grpc.health 的 Health 服务不算。</item>
/// <item>命令行：commander（<c>program.command('serve &lt;port&gt;').action(h)</c>，<c>const remote = program.command('remote')</c> 之后的 <c>remote.command('add')</c> 拼路径，
/// <c>program.action(h)</c> 是根命令）、yargs（<c>.command('x', 'desc', builder, handler)</c>、<c>.command({ command, handler })</c>，
/// 以及命令模块文件 <c>export const command = 'x'</c> + <c>export const handler</c> / <c>export default { command, handler }</c>；
/// 处理器是 <c>lazyHandler(() =&gt; import('./xHandler'))</c> 这种懒加载时落在被加载模块的默认导出上）。kind command，route = 命令名（去掉 &lt;参数&gt; [可选]）。</item>
/// </list>
/// </summary>
public static partial class EntryDiscovery
{
    private static readonly HashSet<string> NodeVerbs = new(StringComparer.Ordinal) { "get", "post", "put", "delete", "patch", "head", "options", "all", "del" };
    private static readonly HashSet<string> NodeRouterParams = new(StringComparer.Ordinal) { "app", "router", "routes", "api", "server", "fastify", "instance", "r", "route", "apiRouter" };
    private static readonly HashSet<string> NodeNotRouters = new(StringComparer.Ordinal) { "axios", "http", "https", "request", "client", "fetch", "superagent", "got", "ky", "$", "cache", "map", "params", "headers", "req", "res", "ctx", "store", "config", "storage", "localStorage", "this" };
    private static readonly Dictionary<string, string> NestVerbs = new(StringComparer.Ordinal)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Delete"] = "DELETE", ["Patch"] = "PATCH", ["Options"] = "OPTIONS", ["Head"] = "HEAD", ["All"] = "",
    };

    [GeneratedRegex(@"\b(\w+)\s*\.\s*(get|post|put|delete|patch|head|options|all|del)\s*\(")]
    private static partial Regex NodeVerbCall();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*route\s*\(")]
    private static partial Regex NodeRouteCall();
    [GeneratedRegex(@"^\s*\.\s*(\w+)\s*\(")]
    private static partial Regex NodeChainedCall();
    // const router = express.Router()   const app = express()   const router = new Router({ prefix })   const app = Fastify()   const r = Router()
    [GeneratedRegex(@"\b(\w+)\s*(?::\s*[\w.<>]+\s*)?=\s*(?:await\s+)?(?:express\s*\(\s*\)|express\s*\.\s*Router\s*\(|Router\s*\(|new\s+(?:Koa)?Router\s*\(|new\s+Koa\s*\(|[Ff]astify\s*\(|require\s*\(\s*['""]fastify['""]\s*\)\s*\()")]
    private static partial Regex NodeRouterDecl();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(use|register)\s*\(")]
    private static partial Regex NodeMount();
    [GeneratedRegex(@"(?:module\s*\.\s*exports\s*=|export\s+default)\s*(\w+)\s*;?\s*$")]
    private static partial Regex NodeDefaultExportIdent();
    // import x from './a'   import x, { y } from './a'   import { a, b as c } from './a'   import * as x from './a'
    [GeneratedRegex(@"^\s*import\s+(?:type\s+)?(.+?)\s+from\s+['""]([^'""]+)['""]")]
    private static partial Regex NodeImport();
    // const x = require('./a')   const { a, b: c } = require('./a')
    [GeneratedRegex(@"\b(?:const|let|var)\s+(\{[^}]*\}|\w+)\s*=\s*require\s*\(\s*['""]([^'""]+)['""]\s*\)")]
    private static partial Regex NodeRequire();
    [GeneratedRegex(@"\b(?:require|import)\s*\(\s*['""](\.[^'""]+)['""]\s*\)")]
    private static partial Regex NodeDynamicLoad();
    [GeneratedRegex(@"\.\s*setGlobalPrefix\s*\(\s*['""`]([^'""`]*)['""`]")]
    private static partial Regex NestGlobalPrefix();
    [GeneratedRegex(@"@\s*Controller\s*\(")]
    private static partial Regex NestController();
    [GeneratedRegex(@"@\s*(Get|Post|Put|Delete|Patch|Options|Head|All)\s*\(")]
    private static partial Regex NestMethod();
    [GeneratedRegex(@"\b([\w.]+?)\s*\.\s*addService\s*\(")]
    private static partial Regex NodeAddService();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*command\s*\(")]
    private static partial Regex NodeCommandCall();
    [GeneratedRegex(@"(?<![\w.])\.\s*command\s*\(")]
    private static partial Regex NodeChainedCommand();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(action|addCommand)\s*\(")]
    private static partial Regex NodeActionCall();
    [GeneratedRegex(@"\b(\w+)\s*(?::\s*[\w.<>]+\s*)?=\s*new\s+(?:commander\s*\.\s*)?Command\s*\(\s*(?:['""`]([^'""`]*)['""`])?")]
    private static partial Regex CommanderNew();
    [GeneratedRegex(@"\b(\w+)\s*(?::\s*[\w.<>]+\s*)?=\s*(\w+)\s*\.\s*command\s*\(\s*['""`]([^'""`]+)['""`]\s*\)\s*;?\s*$")]
    private static partial Regex CommanderSubDecl();
    [GeneratedRegex(@"(?:export\s+const|exports\s*\.|module\s*\.\s*exports\s*\.)\s*command\s*(?::\s*[\w.<>\[\]]+\s*)?=\s*['""`]([^'""`]+)['""`]")]
    private static partial Regex YargsModuleCommand();
    [GeneratedRegex(@"(?:export\s+(?:const|let|async\s+function|function)|exports\s*\.|module\s*\.\s*exports\s*\.)\s*handler\b")]
    private static partial Regex YargsModuleHandler();
    [GeneratedRegex(@"(?<![\w.])command\s*:\s*['""`]")]
    private static partial Regex YargsObjectCommand();
    [GeneratedRegex(@"(?:\b([\w$]+)\s*\.\s*)?\b([\w$]+)\s*\(")]
    private static partial Regex NodeCallSite();
    private static readonly HashSet<string> NodeNotHandlerCalls = new(StringComparer.Ordinal) { "function", "if", "for", "while", "switch", "return", "await", "require", "import", "catch", "then", "log", "error", "warn", "exit", "String", "Number", "Boolean", "parseInt", "parseFloat", "setTimeout" };

    internal static bool IsNodeSource(string path) =>
        !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
        && (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cts", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] NodeExtensions = [".ts", ".js", ".mjs", ".cjs", ".mts", ".cts", ".tsx", ".jsx"];

    private static void DiscoverNode(Scan scan)
    {
        IReadOnlyList<ArchNode> Owners(string path) => scan.ContainersOf(path).Where(c => c.SubKind is "node-service" or "node-cli").ToList();
        var nodeSymbols = scan.Symbols.Where(s => IsNodeSource(s.Path) && s.Kind != SymbolKind.External && !PipelineBuilder.IsTest(s.Path, "")).ToList();
        var files = nodeSymbols.Select(s => s.Path).Distinct().Where(f => Owners(f).Count > 0).OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (files.Count == 0) return;
        var fileSet = nodeSymbols.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        var callables = nodeSymbols.Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method).ToList();
        var byFile = callables.ToLookup(s => s.Path);
        var byName = callables.ToLookup(s => s.Name, StringComparer.Ordinal);
        var modules = nodeSymbols.Where(s => s.Kind == SymbolKind.Module).GroupBy(s => s.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool SameContainer(string a, string b) => Owners(a).Any(x => Owners(b).Any(y => y.Key == x.Key));

        // ── 容器的框架：package.json 的依赖 ──
        var frameworkCache = new Dictionary<string, string>(StringComparer.Ordinal);
        string FrameworkOf(ArchNode c)
        {
            if (frameworkCache.TryGetValue(c.Key, out var fw)) return fw;
            var pkg = string.Join('\n', scan.ReadLines(c.Path is { Length: > 0 } p ? p + "/package.json" : "package.json"));
            bool Has(string dep) => pkg.Contains("\"" + dep + "\"", StringComparison.Ordinal);
            fw = Has("@nestjs/core") ? "nestjs" : Has("fastify") ? "fastify" : Has("koa") || Has("@koa/router") || Has("koa-router") ? "koa" : "express";
            return frameworkCache[c.Key] = fw;
        }

        string? ResolveFile(string fromFile, string spec)
        {
            if (!spec.StartsWith('.')) return null;
            var dir = MapBuilder.DirectoryOf(fromFile);
            var parts = (dir.Length == 0 ? spec : dir + "/" + spec).Split('/');
            var stack = new List<string>();
            foreach (var p in parts)
            {
                if (p is "." or "") continue;
                if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(p);
            }
            var basePath = string.Join('/', stack);
            if (fileSet.Contains(basePath)) return basePath;
            // TS 的 ESM 写法 import './a.js' 实际文件是 a.ts
            var stem = Regex.Replace(basePath, @"\.(js|mjs|cjs)$", "");
            foreach (var ext in NodeExtensions)
            {
                if (fileSet.Contains(stem + ext)) return stem + ext;
                if (fileSet.Contains(basePath + "/index" + ext)) return basePath + "/index" + ext;
            }
            return null;
        }

        // ── 每个文件的导入：本地名 → (文件, 导出名；"default" / "*") ──
        var importsCache = new Dictionary<string, Dictionary<string, (string File, string Export)>>(StringComparer.Ordinal);
        Dictionary<string, (string File, string Export)> ImportsOf(string file)
        {
            if (importsCache.TryGetValue(file, out var cached)) return cached;
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var line in scan.ReadLines(file))
            {
                if (NodeImport().Match(line) is { Success: true } im && ResolveFile(file, im.Groups[2].Value) is { } importTarget)
                {
                    var clause = im.Groups[1].Value.Trim();
                    var brace = clause.IndexOf('{');
                    var head = (brace >= 0 ? clause[..brace] : clause).Trim().TrimEnd(',').Trim();
                    if (head.StartsWith("* as ", StringComparison.Ordinal)) map[head[5..].Trim()] = (importTarget, "*");
                    else if (head.Length > 0 && Regex.IsMatch(head, @"^\w+$")) map[head] = (importTarget, "default");
                    if (brace >= 0)
                        foreach (var part in clause[(brace + 1)..].TrimEnd('}', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var bits = part.Replace("type ", "").Split(" as ", StringSplitOptions.TrimEntries);
                            if (bits[0].Length > 0) map[bits.Length > 1 ? bits[1] : bits[0]] = (importTarget, bits[0]);
                        }
                    continue;
                }
                foreach (Match rq in NodeRequire().Matches(line))
                {
                    if (ResolveFile(file, rq.Groups[2].Value) is not { } target) continue;
                    var lhs = rq.Groups[1].Value;
                    if (!lhs.StartsWith('{')) { map[lhs] = (target, "default"); continue; }
                    foreach (var part in lhs.Trim('{', '}', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var bits = part.Split(':', StringSplitOptions.TrimEntries);
                        map[bits.Length > 1 ? bits[1] : bits[0]] = (target, bits[0]);
                    }
                }
            }
            return importsCache[file] = map;
        }

        MapSymbol? DefaultExportOf(string file) =>
            nodeSymbols.Where(s => s.Path == file && s.Visibility == "export default" && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Method)
                .OrderBy(s => s.StartLine).FirstOrDefault();

        MapSymbol? Enclosing(string file, int line) =>
            byFile[file].Where(s => s.StartLine <= line && line <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault() ?? modules.GetValueOrDefault(file);

        MapSymbol? FindNamed(string name, string file, string? qualifier)
        {
            var imports = ImportsOf(file);
            if (qualifier is not null && imports.TryGetValue(qualifier, out var q))
            {
                var inModule = byFile[q.File].Where(s => s.Name == name).OrderBy(s => s.StartLine).FirstOrDefault();
                if (inModule is not null) return inModule;
            }
            if (qualifier is not null && qualifier != "this")
            {
                // ctrl.list：ctrl 是本文件 / 导入的类实例或类名 → 那个类的方法
                var typed = byName[name].Where(s => s.ParentId is { } p && scan.ById.TryGetValue(p, out var t) && t.Kind == SymbolKind.Class
                                                    && (t.Name == qualifier || t.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase) || qualifier.StartsWith(t.Name[..1].ToLowerInvariant() + t.Name[1..], StringComparison.Ordinal))).ToList();
                if (typed.Count > 0) return typed.OrderBy(s => s.Path == file ? 0 : SameContainer(s.Path, file) ? 1 : 2).ThenBy(s => s.Path, StringComparer.Ordinal).First();
            }
            var local = byFile[file].Where(s => s.Name == name).OrderBy(s => s.StartLine).FirstOrDefault();
            if (local is not null) return local;
            if (imports.TryGetValue(name, out var imp))
            {
                if (imp.Export == "default" && DefaultExportOf(imp.File) is { } d) return d;
                var exported = byFile[imp.File].Where(s => s.Name == imp.Export).OrderBy(s => s.StartLine).FirstOrDefault();
                if (exported is not null) return exported;
                if (imp.Export == "default") return modules.GetValueOrDefault(imp.File);
            }
            var same = byName[name].Where(s => SameContainer(s.Path, file)).ToList();
            return same.Count == 1 ? same[0] : null;
        }

        // 处理器表达式 → 符号
        // firstCall：内联函数时取函数体里第一个能找到的调用（命令行的 action 通常只是转调）；HTTP 的内联处理器落在包含它的函数上
        MapSymbol? HandlerOf(string expr, string file, int line, int depth = 0, bool firstCall = false)
        {
            expr = expr.Trim();
            if (depth > 3 || expr.Length == 0) return null;
            if (NodeDynamicLoad().Match(expr) is { Success: true } load && ResolveFile(file, load.Groups[1].Value) is { } loaded)
                return DefaultExportOf(loaded) ?? byFile[loaded].FirstOrDefault(s => s.Name == "handler") ?? modules.GetValueOrDefault(loaded);
            // 内联函数：function / async (…) / (…) => / x =>；asyncHandler(fn) 这种名字以 async 开头的包装不算
            if (Regex.IsMatch(expr, @"^(?:function\b|async\b(?!\w)|\(|[\w$]+\s*=>)"))
            {
                if (firstCall)
                {
                    var arrow = expr.IndexOf("=>", StringComparison.Ordinal);
                    var bodyAt = arrow >= 0 ? arrow + 2 : expr.IndexOf('{');
                    if (bodyAt >= 0)
                        foreach (Match site in NodeCallSite().Matches(expr[bodyAt..]))
                        {
                            var q = site.Groups[1].Success ? site.Groups[1].Value : null;
                            if (NodeNotHandlerCalls.Contains(site.Groups[2].Value) || q is "console" or "process" or "argv" or "args" or "options" or "opts" or "JSON" or "Object" or "Promise") continue;
                            if (FindNamed(site.Groups[2].Value, file, q) is { } f) return f;
                        }
                }
                return Enclosing(file, line);
            }
            expr = Regex.Replace(expr, @"\s*\.\s*bind\s*\([^()]*\)\s*$", "");
            var chain = Regex.Match(expr, @"^(?:([\w$]+)\s*\.\s*)?(?:[\w$]+\s*\.\s*)*([\w$]+)$");
            if (chain.Success) return FindNamed(chain.Groups[2].Value, file, chain.Groups[1].Success ? chain.Groups[1].Value : null);
            // asyncHandler(ctrl.list) / wrap(fn)：取最后一个实参
            var call = Regex.Match(expr, @"^[\w$.]+\s*\(");
            if (call.Success)
            {
                var (args, _) = ArgParser.SplitArgs(expr, call.Length - 1);
                if (args.Count > 0) return HandlerOf(args[^1], file, line, depth + 1);
            }
            return null;
        }

        void Add(MapSymbol target, string file, int line, string ruleId, string kind, string route, string? method)
        {
            foreach (var c in Owners(file)) scan.Add(new EntryPoint(target.Id, c.Key, ruleId, kind, route, method, file, line));
        }

        var texts = files.ToDictionary(f => f, f => string.Join('\n', scan.ReadLines(f)), StringComparer.Ordinal);
        int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;
        bool Skip(string file, int index) => InJavaComment(scan.ReadLines(file), texts[file], index);

        // ════ HTTP：Express / Koa / Fastify ════
        // 路由器变量（含 Koa 的 prefix）；默认导出的那个
        var routers = new Dictionary<(string File, string Var), string>();
        var exportedRouter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = texts[file];
            foreach (Match m in NodeRouterDecl().Matches(text))
            {
                if (Skip(file, m.Index)) continue;
                var prefix = "";
                if (text[m.Index..(m.Index + m.Length)].Contains("Router", StringComparison.Ordinal) && text[m.Index + m.Length - 1] == '(')
                {
                    var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                    if (args.Count > 0 && args[0].StartsWith('{'))
                    {
                        var (fields, _) = ArgParser.SplitArgs(args[0], 0);
                        if (Field(fields, "prefix") is { } p && ArgParser.Literal(p) is { } lit) prefix = lit;
                    }
                }
                routers[(file, m.Groups[1].Value)] = prefix;
            }
            foreach (var line in scan.ReadLines(file))
                if (NodeDefaultExportIdent().Match(line) is { Success: true } ex && routers.ContainsKey((file, ex.Groups[1].Value))) exportedRouter[file] = ex.Groups[1].Value;
            if (!exportedRouter.ContainsKey(file) && routers.Keys.Count(k => k.File == file) == 1) exportedRouter[file] = routers.Keys.First(k => k.File == file).Var;
        }

        // 挂载：父 (文件, 变量) 在 segment 下挂子 (文件, 变量)；子变量 "*" = 文件里所有不是本地路由器的接收者（Fastify 插件的参数）
        var mounts = new List<((string File, string Var) Parent, string Segment, (string File, string Var) Child)>();
        foreach (var file in files)
        {
            var text = texts[file];
            foreach (Match m in NodeMount().Matches(text))
            {
                if (Skip(file, m.Index)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count == 0) continue;
                var parent = (file, m.Groups[1].Value);
                var segment = "";
                var targets = args;
                if (m.Groups[2].Value == "use" && ArgParser.Literal(args[0]) is { } seg) { segment = seg; targets = args.Skip(1).ToList(); }
                else if (m.Groups[2].Value == "register")
                {
                    targets = args.Take(1).ToList();
                    if (args.Count > 1 && args[1].StartsWith('{'))
                    {
                        var (fields, _) = ArgParser.SplitArgs(args[1], 0);
                        if (Field(fields, "prefix") is { } p && ArgParser.Literal(p) is { } lit) segment = lit;
                    }
                }
                foreach (var t0 in targets)
                {
                    var t = Regex.Replace(t0.Trim(), @"\s*\.\s*(?:routes|middleware|allowedMethods)\s*\(\s*\)\s*$", "");
                    (string, string)? child = null;
                    if (NodeDynamicLoad().Match(t) is { Success: true } dl && ResolveFile(file, dl.Groups[1].Value) is { } f1)
                        child = (f1, exportedRouter.GetValueOrDefault(f1) ?? "*");
                    else if (Regex.IsMatch(t, @"^\w+$"))
                    {
                        if (routers.ContainsKey((file, t))) child = (file, t);
                        else if (ImportsOf(file).TryGetValue(t, out var imp))
                            child = (imp.File, imp.Export == "default" ? exportedRouter.GetValueOrDefault(imp.File) ?? "*" : routers.ContainsKey((imp.File, imp.Export)) ? imp.Export : "*");
                    }
                    if (child is { } c && c != parent) mounts.Add((parent, segment, c));
                }
            }
        }
        var prefixMemo = new Dictionary<(string, string), List<string>>();
        List<string> PrefixesOf((string File, string Var) key, int depth)
        {
            if (prefixMemo.TryGetValue(key, out var memo)) return memo;
            var own = routers.GetValueOrDefault(key) ?? "";
            var ups = mounts.Where(m => m.Child == key || (m.Child.Var == "*" && m.Child.File == key.File && !routers.ContainsKey(key))).ToList();
            if (ups.Count == 0 || depth > 6) return [JoinRoute(own)];
            var result = ups.SelectMany(u => PrefixesOf(u.Parent, depth + 1).Select(p => JoinRoute(p, u.Segment, own))).Distinct(StringComparer.Ordinal).ToList();
            return prefixMemo[key] = result;
        }
        bool IsRouterReceiver(string file, string receiver) =>
            !NodeNotRouters.Contains(receiver) && (routers.ContainsKey((file, receiver)) || NodeRouterParams.Contains(receiver));

        foreach (var file in files)
        {
            var text = texts[file];
            var owners = Owners(file);
            var fw = FrameworkOf(owners[0]);
            if (fw == "nestjs" && !Regex.IsMatch(text, @"\bexpress\b|\bfastify\b")) continue; // Nest 的路由走装饰器
            var ruleId = "node." + (fw == "nestjs" ? "express" : fw);
            void Route(string receiver, string verb, string path, string handlerExpr, int index)
            {
                var line = LineOf(text, index);
                if (HandlerOf(handlerExpr, file, line) is not { } h) return;
                var method = verb is "all" ? null : verb == "del" ? "DELETE" : verb.ToUpperInvariant();
                foreach (var prefix in PrefixesOf((file, receiver), 0))
                    Add(h, file, line, ruleId, "http", JoinRoute(prefix, path), method);
            }
            foreach (Match m in NodeVerbCall().Matches(text))
            {
                if (Skip(file, m.Index) || !IsRouterReceiver(file, m.Groups[1].Value)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count < 2 || ArgParser.Literal(args[0]) is not { } path || !(path.StartsWith('/') || path == "*")) continue;
                Route(m.Groups[1].Value, m.Groups[2].Value, path, args[^1], m.Index);
            }
            foreach (Match m in NodeRouteCall().Matches(text))
            {
                if (Skip(file, m.Index) || !IsRouterReceiver(file, m.Groups[1].Value)) continue;
                var (args, close) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count == 0) continue;
                if (ArgParser.Literal(args[0]) is { } path)
                {
                    // router.route('/x').get(h).post(h2)
                    for (var at = close + 1; close >= 0 && at < text.Length;)
                    {
                        var next = NodeChainedCall().Match(text[at..]);
                        if (!next.Success) break;
                        var (cargs, cclose) = ArgParser.SplitArgs(text, at + next.Length - 1);
                        if (NodeVerbs.Contains(next.Groups[1].Value) && cargs.Count > 0) Route(m.Groups[1].Value, next.Groups[1].Value, path, cargs[^1], at);
                        if (cclose < 0) break;
                        at = cclose + 1;
                    }
                }
                else if (args[0].StartsWith('{'))
                {
                    // fastify.route({ method: 'GET', url: '/x', handler })
                    var (fields, _) = ArgParser.SplitArgs(args[0], 0);
                    var url = Field(fields, "url") ?? Field(fields, "path");
                    if (url is null || ArgParser.Literal(url) is not { } routePath) continue;
                    var methods = Field(fields, "method") is { } ms ? Regex.Matches(ms, @"['""](\w+)['""]").Select(x => x.Groups[1].Value.ToLowerInvariant()).ToList() : ["all"];
                    var handlerField = Field(fields, "handler");
                    var handlerExpr = handlerField ?? (fields.Any(f => Regex.IsMatch(f, @"^(?:async\s+)?handler\s*\(")) ? "function" : null);
                    if (handlerExpr is null) continue;
                    var handlerIndex = text.IndexOf("handler", m.Index, StringComparison.Ordinal);
                    foreach (var verb in methods) Route(m.Groups[1].Value, verb, routePath, handlerExpr, handlerIndex >= 0 ? handlerIndex : m.Index);
                }
            }
        }

        // ════ NestJS ════
        var globalPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
            if (NestGlobalPrefix().Match(texts[file]) is { Success: true } gp)
                foreach (var c in Owners(file)) globalPrefix.TryAdd(c.Key, gp.Groups[1].Value);
        foreach (var file in files.Where(f => texts[f].Contains("@Controller", StringComparison.Ordinal)))
        {
            var lines = scan.ReadLines(file);
            var classes = nodeSymbols.Where(s => s.Path == file && s.Kind == SymbolKind.Class).OrderBy(s => s.StartLine).ToList();
            var topLevel = nodeSymbols.Where(s => s.Path == file && s.Kind != SymbolKind.Module && (s.ParentId is null || modules.GetValueOrDefault(file)?.Id == s.ParentId)).ToList();
            foreach (var cls in classes)
            {
                var prevEnd = topLevel.Where(s => s.EndLine < cls.StartLine).Select(s => s.EndLine).DefaultIfEmpty(0).Max();
                var head = Window(lines, prevEnd, DeclLine(lines, cls.StartLine, "class " + cls.Name));
                var ctl = NestController().Match(head);
                if (!ctl.Success) continue;
                var (cargs, _) = ArgParser.SplitArgs(head, ctl.Index + ctl.Length - 1);
                var classPath = "";
                if (cargs.Count > 0)
                {
                    var a0 = cargs[0].Trim();
                    if (a0.StartsWith('[')) { var (items, _) = ArgParser.SplitArgs(a0, 0); a0 = items.FirstOrDefault() ?? ""; }
                    if (a0.StartsWith('{')) { var (fields, _) = ArgParser.SplitArgs(a0, 0); a0 = Field(fields, "path") ?? ""; }
                    classPath = ArgParser.Literal(a0) ?? "";
                }
                var members = scan.Children[cls.Id].Where(s => s.Kind == SymbolKind.Method).OrderBy(s => s.StartLine).ToList();
                var after = DeclLine(lines, cls.StartLine, "class " + cls.Name);
                foreach (var m in members)
                {
                    var window = Window(lines, after, DeclLine(lines, m.StartLine, m.Name));
                    after = Math.Max(after, m.EndLine);
                    foreach (Match d in NestMethod().Matches(window))
                    {
                        var (dargs, _) = ArgParser.SplitArgs(window, d.Index + d.Length - 1);
                        var methodPath = dargs.Count > 0 ? ArgParser.Literal(dargs[0]) ?? "" : "";
                        var verb = NestVerbs[d.Groups[1].Value];
                        foreach (var c in Owners(file))
                            scan.Add(new EntryPoint(m.Id, c.Key, "node.nestjs", "http", JoinRoute(globalPrefix.GetValueOrDefault(c.Key) ?? "", classPath, methodPath), verb.Length == 0 ? null : verb, m.Path, m.StartLine));
                    }
                }
            }
        }

        // ════ gRPC：server.addService(proto.X.service, impl) ════
        foreach (var file in files.Where(f => texts[f].Contains("addService", StringComparison.Ordinal)))
        {
            var text = texts[file];
            foreach (Match m in NodeAddService().Matches(text))
            {
                if (Skip(file, m.Index)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count < 2) continue;
                var svc = Regex.Match(args[0], @"(\w+)\s*\.\s*service\s*$");
                if (!svc.Success || svc.Groups[1].Value == "Health") continue;
                var service = svc.Groups[1].Value;
                var impl = args[1].Trim();
                var implAt = m.Index;
                if (Regex.IsMatch(impl, @"^\w+$"))
                {
                    // 变量：同文件里 const impl = { … } / impl = new Impl()
                    var decl = Regex.Match(text, $@"\b{Regex.Escape(impl)}\s*=\s*(\{{|new\s+(\w+)\s*\()");
                    if (decl.Success && decl.Groups[2].Success)
                    {
                        var cls = nodeSymbols.Where(s => s.Kind == SymbolKind.Class && s.Name == decl.Groups[2].Value).OrderBy(s => s.Path == file ? 0 : 1).FirstOrDefault();
                        if (cls is not null)
                            foreach (var meth in scan.Children[cls.Id].Where(s => s.Kind == SymbolKind.Method && s.Name != "constructor" && !s.Name.StartsWith('_')).OrderBy(s => s.StartLine))
                                Add(meth, meth.Path, meth.StartLine, "node.grpc", "grpc", service + "/" + JavaSource.Pascal(meth.Name), null);
                        continue;
                    }
                    if (!decl.Success) continue;
                    implAt = decl.Index + decl.Length - 1;
                    impl = text[implAt..(ArgParser.MatchClose(text, implAt) is var e and >= 0 ? e + 1 : text.Length)];
                }
                else implAt = text.IndexOf(impl, m.Index, StringComparison.Ordinal);
                if (!impl.StartsWith('{') || implAt < 0) continue;
                var (entries, _) = ArgParser.SplitArgs(impl, 0);
                var implLine = LineOf(text, implAt);
                var implEnd = implLine + impl.Count(ch => ch == '\n');
                foreach (var entry in entries)
                {
                    var kv = Regex.Match(entry, @"^(?:async\s+)?['""]?(\w+)['""]?\s*(:|\(|$)");
                    if (!kv.Success) continue;
                    var key = kv.Groups[1].Value;
                    MapSymbol? h = kv.Groups[2].Value switch
                    {
                        ":" => HandlerOf(entry[(entry.IndexOf(':') + 1)..], file, implLine),
                        "(" => byFile[file].FirstOrDefault(s => s.Name == key && s.StartLine >= implLine && s.StartLine <= implEnd) ?? Enclosing(file, implLine),
                        _ => FindNamed(key, file, null),
                    };
                    if (h is not null) Add(h, h.Path, h.StartLine, "node.grpc", "grpc", service + "/" + JavaSource.Pascal(key), null);
                }
            }
        }

        // ════ 命令行：commander / yargs ════
        static string CommandName(string spec)
        {
            var first = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => !t.StartsWith('<') && !t.StartsWith('[')) ?? spec;
            return first is "$0" or "*" ? "" : first;
        }
        string RootName(string file) => Owners(file).FirstOrDefault()?.Name ?? MapSymbol.FileName(file);
        void AddCli(MapSymbol h, string ruleId, string route, string file) =>
            Add(h, h.Path, h.StartLine, ruleId, "command", route.Length == 0 ? RootName(file) : route, null);

        // yargs 命令模块（文件 → 命令）与模块间的父子：命令模块的 builder 里 .command(SubModule) 挂子命令
        var yargsModules = new List<(string File, MapSymbol Handler, string Name)>();
        var yargsParent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var text = texts[file];
            var lines = scan.ReadLines(file);
            var hasCommander = text.Contains("commander", StringComparison.Ordinal);
            var hasYargs = text.Contains("yargs", StringComparison.Ordinal);

            // ── yargs 命令模块：export const command = 'x' + export const handler ──
            if (YargsModuleCommand().Match(text) is { Success: true } mc && YargsModuleHandler().IsMatch(text))
            {
                var handlerSym = byFile[file].Where(s => s.Name == "handler").OrderBy(s => s.StartLine).FirstOrDefault();
                var handlerLine = handlerSym?.StartLine ?? 0;
                // export const handler = lazyHandler(() => import('./xHandler'))：落到被加载的模块
                var handlerText = handlerLine > 0 ? string.Join('\n', lines[(handlerLine - 1)..Math.Min(lines.Length, handlerLine + 2)]) : "";
                var target = NodeDynamicLoad().Match(handlerText) is { Success: true } dl && ResolveFile(file, dl.Groups[1].Value) is { } lf
                    ? DefaultExportOf(lf) ?? byFile[lf].FirstOrDefault(s => s.Name == "handler") ?? modules.GetValueOrDefault(lf)
                    : handlerSym;
                if (target is not null) yargsModules.Add((file, target, CommandName(mc.Groups[1].Value)));
            }
            // ── 对象形式：{ command: 'x', handler } ──（export default { … } 的命令模块，或 .command({ … })）
            List<(int Open, int Close)>? spans = null;
            foreach (Match m in YargsObjectCommand().Matches(text))
            {
                if (Skip(file, m.Index)) continue;
                spans ??= BraceSpans(text);
                var obj = spans.Where(s => s.Open < m.Index && m.Index < s.Close).OrderByDescending(s => s.Open).FirstOrDefault();
                if (obj == default) continue;
                var (fields, _) = ArgParser.SplitArgs(text, obj.Open);
                if (Field(fields, "command") is not { } spec || ArgParser.Literal(spec) is not { } name) continue;
                var handlerField = Field(fields, "handler");
                var methodForm = fields.FirstOrDefault(f => Regex.IsMatch(f, @"^(?:async\s+)?handler\s*\("));
                if (handlerField is null && methodForm is null && !fields.Any(f => f.Trim() == "handler")) continue;
                var objLine = LineOf(text, obj.Open);
                var objEnd = LineOf(text, obj.Close);
                MapSymbol? h = handlerField is not null ? HandlerOf(handlerField, file, objLine, firstCall: true)
                    : methodForm is not null ? byFile[file].FirstOrDefault(s => s.Name == "handler" && s.StartLine >= objLine && s.StartLine <= objEnd) ?? Enclosing(file, objLine)
                    : FindNamed("handler", file, null);
                if (h is null) continue;
                // export default { … } / module.exports = { … } 是命令模块（可被别的模块挂成子命令）；.command({ … }) 里的就地注册
                var before = text[..obj.Open].TrimEnd();
                if (before.EndsWith("export default", StringComparison.Ordinal) || Regex.IsMatch(before, @"module\s*\.\s*exports\s*=$")) yargsModules.Add((file, h, CommandName(name)));
                else AddCli(h, "node.yargs", CommandName(name), file);
            }

            if (!text.Contains(".command", StringComparison.Ordinal) && !text.Contains(".action", StringComparison.Ordinal)) continue;
            // ── commander：命令变量（new Command('x') / const sub = program.command('x')）与父子 ──
            var cmdNames = new Dictionary<string, string>(StringComparer.Ordinal); // 变量 → 命令路径（根为 ""）
            foreach (Match m in CommanderNew().Matches(text))
                if (!Skip(file, m.Index)) cmdNames[m.Groups[1].Value] = m.Groups[2].Success ? CommandName(m.Groups[2].Value) : "";
            if (hasCommander && !cmdNames.ContainsKey("program")) cmdNames["program"] = "";
            foreach (var line in lines)
                if (CommanderSubDecl().Match(line) is { Success: true } sd)
                    cmdNames[sd.Groups[1].Value] = JoinCommand(cmdNames.GetValueOrDefault(sd.Groups[2].Value) ?? "", CommandName(sd.Groups[3].Value));
            foreach (Match m in NodeActionCall().Matches(text))
            {
                if (Skip(file, m.Index) || m.Groups[2].Value != "addCommand") continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count > 0 && cmdNames.TryGetValue(args[0].Trim(), out var childPath) && cmdNames.TryGetValue(m.Groups[1].Value, out var parentPath) && parentPath.Length > 0)
                    cmdNames[args[0].Trim()] = JoinCommand(parentPath, childPath);
            }

            foreach (Match m in NodeCommandCall().Matches(text).Concat(NodeChainedCommand().Matches(text)).GroupBy(x => x.Index + x.Length).Select(g => g.OrderBy(x => x.Index).First()))
            {
                if (Skip(file, m.Index)) continue;
                var (args, close) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count == 0) continue;
                var line = LineOf(text, m.Index);
                var receiver = m.Groups[1].Success ? m.Groups[1].Value : null;
                var basePath = receiver is not null && cmdNames.TryGetValue(receiver, out var rp) ? rp : "";
                var spec = ArgParser.Literal(args[0]);
                if (spec is null)
                {
                    // .command({ command, handler }) 走上面的对象形式；.command(module) 的模块文件自己会被识别，这里只记父子
                    var a0 = args[0].Trim();
                    var child = NodeDynamicLoad().Match(a0) is { Success: true } dl0 ? ResolveFile(file, dl0.Groups[1].Value)
                        : Regex.IsMatch(a0, @"^\w+$") && ImportsOf(file).TryGetValue(a0, out var imp0) ? imp0.File : null;
                    if (child is not null && child != file) yargsParent.TryAdd(child, file);
                    continue;
                }
                var name = JoinCommand(basePath, CommandName(spec));
                var isYargsForm = args.Count >= 2 && (ArgParser.Literal(args[1]) is not null || args[1].Trim() == "false" || args[1].Trim().StartsWith('['));
                if (isYargsForm)
                {
                    if (args.Count >= 4 && HandlerOf(args[3], file, line, firstCall: true) is { } h) AddCli(h, "node.yargs", name, file);
                    continue; // 两个实参是 commander 的独立可执行子命令（prog-x.js），三个是 yargs 的 builder：没有处理器
                }
                // commander：.command('x') 之后链上的 .action(h)（跳过 .description / .option / .argument …）
                for (var at = close + 1; close >= 0 && at < text.Length;)
                {
                    var next = NodeChainedCall().Match(text[at..]);
                    if (!next.Success) break;
                    var (cargs, cclose) = ArgParser.SplitArgs(text, at + next.Length - 1);
                    if (next.Groups[1].Value == "command") break;
                    if (next.Groups[1].Value == "action")
                    {
                        if (cargs.Count > 0 && HandlerOf(cargs[0], file, LineOf(text, at), firstCall: true) is { } h) AddCli(h, hasYargs && !hasCommander ? "node.yargs" : "node.commander", name, file);
                        break;
                    }
                    if (cclose < 0) break;
                    at = cclose + 1;
                }
            }
            // 命令变量上单独调的 .action(h)：sub.action(h) / program.action(h)（根命令）
            foreach (Match m in NodeActionCall().Matches(text))
            {
                if (Skip(file, m.Index) || m.Groups[2].Value != "action" || !cmdNames.TryGetValue(m.Groups[1].Value, out var path)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count > 0 && HandlerOf(args[0], file, LineOf(text, m.Index), firstCall: true) is { } h) AddCli(h, "node.commander", path, file);
            }
        }
        var moduleNames = yargsModules.GroupBy(m => m.File).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        string ModulePath(string file, int depth = 0) =>
            depth < 8 && yargsParent.TryGetValue(file, out var parent) && moduleNames.TryGetValue(parent, out var parentName)
                ? JoinCommand(ModulePath(parent, depth + 1), parentName) : "";
        foreach (var (file, h, name) in yargsModules) AddCli(h, "node.yargs", JoinCommand(ModulePath(file), name), file);
    }

    private static string JoinCommand(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    /// <summary>行 [from+1, to]（1 起）拼成文本：装饰器夹在上一个成员结束与声明行之间。</summary>
    private static string Window(string[] lines, int from, int to) =>
        string.Join('\n', lines[Math.Clamp(from, 0, lines.Length)..Math.Clamp(to, 0, lines.Length)]);

    /// <summary>从 startLine 起找含 marker 的那一行（装饰器可能被算进符号范围）；找不到就是 startLine。</summary>
    private static int DeclLine(string[] lines, int startLine, string marker)
    {
        for (var i = Math.Max(0, startLine - 1); i < lines.Length && i < startLine + 15; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal)) return i + 1;
        return startLine;
    }
}
