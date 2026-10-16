using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using static CodeAtlas.Server.FrameworkFlowBuilder;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程 · Node（node-service）：Express / Koa / NestJS / Fastify，纯 gRPC 服务给一句说明。由 <see cref="FrameworkFlowBuilder.Build"/> 按容器 SubKind 分派进来。
/// 框架按容器 package.json 的依赖定（@nestjs/core &gt; fastify &gt; koa &gt; express，与入口识别一致），再在容器的源码里找创建应用的地方
/// （const app = express() / new Koa() / Fastify() / await NestFactory.create(…)）；有多处时 package.json 的 main / start 脚本、src/main.ts、app.js、server.js、index.js 一类优先。
/// 注册从源码文本读（注释、字符串内容先清掉，见 <see cref="StripJs"/>），函数体里的调用用 Sources.Calls（内联函数没有符号，从外层符号的调用里按偏移截出来，
/// 外层是文件模块时用模块的调用边），中间件按 next() 的位置切成前置（子节点）与后置（收尾列，洋葱倒序）。
/// <list type="number">
/// <item>Express / Koa（<see cref="BuildExpressLike"/>）：app.use(...) 书写顺序；app.use 里传 app 的仓库函数（registerSecurity(app)）进去接着找（3 层）；
/// app.use('/api', router) / app.use(router.routes()) 是路由挂载，画在路由占位下面，router 文件里的 router.use(...) 挂在挂载节点下、Note 写清前缀；
/// 四个参数的 (err, req, res, next) 进收尾"出错时"；路由之后注册的普通中间件进收尾（没有路由匹配时才走到）；res.on('finish') 回调里的调用进收尾"响应发完时"；
/// Koa 的 app.on('error') 进收尾"出错时"。</item>
/// <item>NestJS（<see cref="BuildNest"/>）：main.ts 的 app.use / enableCors / useGlobalGuards / useGlobalInterceptors / useGlobalPipes / useGlobalFilters，
/// 模块里的 APP_GUARD / APP_INTERCEPTOR / APP_PIPE / APP_FILTER provider，configure(consumer) 的 consumer.apply(...).forRoutes(...)；
/// 固定顺序 中间件 → 守卫 → 拦截器前置 → 管道 → 控制器 → 拦截器后置（next.handle() 之后）→ 异常过滤器（出错时）；控制器级 @UseGuards 一类只在 Note 里统计。</item>
/// <item>Fastify（<see cref="BuildFastify"/>）：addHook 按生命周期排（onRequest → preParsing → preValidation → preHandler → 处理函数 → preSerialization → onSend → onResponse，
/// onError / setErrorHandler 是出错时），同一钩子按注册顺序；register(仓库插件) 进插件函数接着找（没用 fastify-plugin 包的是封装作用域，Note 写明）；第三方插件画成一行。</item>
/// <item>纯 gRPC（@grpc/grpc-js 的 server.addService，没有 HTTP 框架）：只给"服务方法"占位与一句说明，不画中间件。</item>
/// </list>
/// </summary>
public static partial class NodeFrameworkFlow
{
    /// <summary>NestJS 的管道（Pipe）列。页面不按键分支，列名用阶段的 Label。</summary>
    public const string StagePipe = "pipe";

    /// <summary>创建应用的那一处：框架、文件、变量名、偏移。</summary>
    internal sealed record AppDecl(string Framework, string File, string Var, int Pos);

    private static readonly string[] ScriptExtensions = [".js", ".ts", ".mjs", ".cjs", ".mts", ".cts", ".jsx", ".tsx"];

    [GeneratedRegex(@"(?<![\w$.])([\w$]+)\s*(?::\s*[\w$.<>]+\s*)?=\s*express\s*\(\s*\)")]
    private static partial Regex ExpressDecl();
    [GeneratedRegex(@"(?<![\w$.])([\w$]+)\s*(?::\s*[\w$.<>]+\s*)?=\s*new\s+Koa\s*\(")]
    private static partial Regex KoaDecl();
    // NoComments 视图上用（require('fastify') 的字符串要在）
    [GeneratedRegex(@"(?<![\w$.])([\w$]+)\s*(?::\s*[\w$.<>]+\s*)?=\s*(?:await\s+)?(?:require\s*\(\s*['""]fastify['""]\s*\)|[Ff]astify)\s*\(")]
    private static partial Regex FastifyDecl();
    [GeneratedRegex(@"(?<![\w$.])([\w$]+)\s*(?::\s*[\w$.<>]+\s*)?=\s*await\s+NestFactory\s*\.\s*create\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex NestDecl();
    [GeneratedRegex(@"""main""\s*:\s*""([^""]+)""")]
    private static partial Regex PackageMain();
    [GeneratedRegex(@"""start""\s*:\s*""[^""]*?\b(?:node|ts-node|tsx|nodemon)\s+(?:-r\s+\S+\s+)?([^\s""]+)")]
    private static partial Regex PackageStart();

    // import / require 绑定（NoComments 视图）
    [GeneratedRegex(@"\bimport\s+(?:type\s+)?(?:([\w$]+)\s*,?\s*)?(?:\{([^}]*)\}|\*\s*as\s+([\w$]+))?\s*from\s*['""]([^'""]+)['""]")]
    private static partial Regex ImportFrom();
    [GeneratedRegex(@"\b(?:const|let|var)\s+([\w$]+)\s*=\s*require\s*\(\s*['""]([^'""]+)['""]\s*\)(?:\s*\.\s*([\w$]+))?")]
    private static partial Regex RequireDefault();
    [GeneratedRegex(@"\b(?:const|let|var)\s+\{([^}]*)\}\s*=\s*require\s*\(\s*['""]([^'""]+)['""]\s*\)")]
    private static partial Regex RequireNamed();
    [GeneratedRegex(@"\bimport\s+([\w$]+)\s*=\s*require\s*\(\s*['""]([^'""]+)['""]\s*\)")]
    private static partial Regex ImportRequire();

    public static FrameworkFlowDto Build(FrameworkInput input)
    {
        var x = new Ctx(input);
        var pkg = x.Read(x.Rel("package.json"));
        bool Dep(string name) => pkg is not null && pkg.Contains("\"" + name + "\"", StringComparison.Ordinal);
        var declared = Dep("@nestjs/core") ? "nestjs" : Dep("fastify") ? "fastify" : Dep("koa") ? "koa" : Dep("express") ? "express" : null;
        var app = declared is not null ? FindApp(x, declared, pkg) : null;
        if (app is null)
            foreach (var fw in new[] { "nestjs", "fastify", "koa", "express" })
                if (fw != declared && FindApp(x, fw, pkg) is { } a) { app = a; break; }
        var grpc = x.Entries.Count(e => e.Kind == "grpc");
        var prefaceNotes = new List<string>();
        if (declared is not null && (app is null || app.Framework != declared))
            prefaceNotes.Add($"package.json 依赖 {FrameworkName(declared)}，但没找到创建应用的地方（{DeclHint(declared)}）");

        if (app is not null)
        {
            var flow = app.Framework switch
            {
                "nestjs" => BuildNest(x, app),
                "fastify" => BuildFastify(x, app),
                _ => BuildExpressLike(x, app),
            };
            var extra = new List<string>(prefaceNotes);
            if (grpc > 0) extra.Add($"另有 {grpc} 个 gRPC 方法（server.addService），不经过 {FrameworkName(app.Framework)} 的这条链");
            return extra.Count == 0 ? flow : flow with { Notes = [.. extra, .. flow.Notes] };
        }
        if (grpc > 0 || Dep("@grpc/grpc-js") || Dep("grpc"))
            return BuildGrpc(x, grpc, prefaceNotes);
        prefaceNotes.Add("没认出 Node 的 HTTP 框架（package.json 里没有 express / koa / fastify / @nestjs/core，源码里也没有创建应用的地方），也没有 gRPC 服务");
        return Empty(x.Container, prefaceNotes);
    }

    private static string FrameworkName(string fw) => fw switch { "nestjs" => "NestJS", "fastify" => "Fastify", "koa" => "Koa", _ => "Express" };

    private static string DeclHint(string fw) => fw switch
    {
        "nestjs" => "const app = await NestFactory.create(…)",
        "fastify" => "const app = Fastify(…)",
        "koa" => "const app = new Koa()",
        _ => "const app = express()",
    };

    /// <summary>容器里创建应用的地方；多处时入口文件优先，再看 use / addHook 的次数，再按路径。</summary>
    private static AppDecl? FindApp(Ctx x, string fw, string? pkg)
    {
        var priority = x.EntryPriority(pkg);
        var found = new List<(AppDecl Decl, int Rank, int Uses)>();
        foreach (var file in x.ScriptFiles)
        {
            if (x.Text(file) is not { } t) continue;
            var m = fw switch
            {
                "nestjs" => NestDecl().Match(t.Code),
                "fastify" => FastifyDecl().Match(t.NoComments),
                "koa" => KoaDecl().Match(t.Code),
                _ => ExpressDecl().Match(t.Code),
            };
            if (!m.Success) continue;
            var v = m.Groups[1].Value;
            var uses = Regex.Matches(t.Code, @"(?<![\w$.])" + Regex.Escape(v) + @"\s*\.\s*(?:use|addHook|register|useGlobal\w+)\s*\(").Count;
            found.Add((new AppDecl(fw, file, v, m.Index), priority.TryGetValue(file, out var r) ? r : int.MaxValue, uses));
        }
        return found.OrderBy(f => f.Rank).ThenByDescending(f => f.Uses).ThenBy(f => f.Decl.File, StringComparer.Ordinal).Select(f => f.Decl).FirstOrDefault();
    }

    // ================= 纯 gRPC =================

    private static FrameworkFlowDto BuildGrpc(Ctx x, int count, List<string> notes)
    {
        var services = x.Entries.Where(e => e.Kind == "grpc" && e.Route is not null)
            .GroupBy(e => e.Route![..Math.Max(0, e.Route!.IndexOf('/'))], StringComparer.Ordinal)
            .Select(g => $"{(g.Key.Length == 0 ? "?" : g.Key)} {g.Count()} 个方法").ToList();
        var files = x.Entries.Where(e => e.Kind == "grpc").Select(e => MapSymbol.FileName(e.File)).Distinct(StringComparer.Ordinal).ToList();
        notes.Add(count > 0
            ? $"认出 gRPC 服务（@grpc/grpc-js 的 server.addService，{string.Join("、", files)}）：{string.Join("、", services)}"
            : "依赖里有 @grpc/grpc-js，但没找到 server.addService 注册的服务方法");
        notes.Add("grpc-js 服务端没有中间件链：请求直接进 addService 注册的处理函数，框架流程只有「服务方法」一列，具体调用看入口流程");
        // new grpc.Server({ interceptors: [...] })：服务端拦截器只提一句
        foreach (var file in x.ScriptFiles)
            if (x.Text(file) is { } t && Regex.Match(t.Code, @"new\s+[\w$.]*Server\s*\(\s*\{[^}]*\binterceptors\s*:\s*\[([^\]]*)\]") is { Success: true } m)
                notes.Add($"{MapSymbol.FileName(file)} 的 Server 选项里配置了服务端拦截器（{string.Join("、", m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}），没画");
        if (count == 0) return Empty(x.Container, notes);
        var placeholder = Node(null, "服务方法", "", "controllers", StageController, null, [], null, null, count);
        var root = Node(null, "gRPC 调用", "", "entry", StageController, null, [placeholder], null, null);
        var stages = new List<FrameworkStageDto> { new(StageController, "服务方法", true, x.EntrySymbols("grpc")) };
        return Assemble(x.Container, "gRPC 调用", root, stages, [], notes);
    }

    // ================= 共用的上下文 =================

    /// <summary>路由器声明：自带前缀、声明处链式调用的起点（-1 没有）、声明位置。</summary>
    internal sealed record RouterDecl(string Prefix, int ChainAt, int Pos);

    [GeneratedRegex(@"(?:(?<![\w$.])([\w$]+)\s*(?::\s*[\w$.<>]+\s*)?=|export\s+default|module\s*\.\s*exports\s*=)\s*(?:new\s+)?(?:express\s*\.\s*|require\s*\([^()]*\)\s*\.\s*)?(?:Koa)?Router\s*\(")]
    private static partial Regex RouterDeclRx();

    /// <summary>一个名字解析到的东西：仓库符号；或 npm 包（Package）；或仓库文件（File + 导出名，路由器变量要用）。</summary>
    internal sealed record Resolved(MapSymbol? Symbol, string? Package, string? File, string Export);

    internal sealed class Ctx
    {
        public readonly FrameworkInput Input;
        public readonly ArchNode Container;
        public readonly FrameworkSources Src;
        public readonly Dictionary<long, MapSymbol> ById = [];
        public readonly ILookup<string, MapSymbol> ByFile;
        public readonly List<string> ScriptFiles;
        public readonly HashSet<string> FileSet;
        public readonly List<EntryPoint> Entries;
        public readonly string Dir;
        private readonly Dictionary<string, JsSource?> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, (string Spec, string Export)>> _imports = new(StringComparer.Ordinal);
        private Dictionary<long, List<MapEdge>>? _callEdges;
        private Dictionary<(string File, string Var), RouterDecl>? _routers;
        private ILookup<long, MapSymbol>? _children;

        public Ctx(FrameworkInput input)
        {
            Input = input;
            Container = input.Container;
            Src = input.Sources;
            foreach (var s in input.Symbols) ById[s.Id] = s;
            var own = input.Symbols.Where(InContainer).ToList();
            ByFile = own.ToLookup(s => s.Path, StringComparer.Ordinal);
            FileSet = own.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            ScriptFiles = FileSet.Where(IsScript).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Entries = input.Entries.Where(e => e.ContainerKey == Container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
            Dir = Container.Path ?? Container.Members.FirstOrDefault() ?? "";
        }

        public bool InContainer(MapSymbol s) =>
            s.Kind != SymbolKind.External && Container.Members.Any(m => Under(s.Path, m)) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);

        private static bool IsScript(string path) =>
            ScriptExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && !path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(path, @"(^|/)(node_modules|dist|build|coverage|\.next)/");

        public string Rel(string p) => Dir.Length == 0 ? p : Dir + "/" + p;

        public string? Read(string path)
        {
            try { return Src.ReadFile(path); } catch { return null; }
        }

        public JsSource? Text(string path)
        {
            if (_texts.TryGetValue(path, out var t)) return t;
            var raw = Read(path);
            return _texts[path] = raw is null ? null : new JsSource(raw);
        }

        public string Where(string path, int pos) => $"{MapSymbol.FileName(path)}:{Text(path)?.LineAt(pos) ?? 0}";
        public int LineOf(string path, int pos) => Text(path)?.LineAt(pos) ?? 0;

        /// <summary>入口文件的先后：package.json 的 main / start 脚本（dist/x.js 也试 src/x.ts），再是 src/main.ts、app.js、server.js、index.js 一类。</summary>
        public Dictionary<string, int> EntryPriority(string? pkg)
        {
            var list = new List<string>();
            void AddPath(string p)
            {
                p = p.Replace('\\', '/');
                while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
                list.Add(Rel(p));
                var stem = Regex.Replace(p, @"\.(m|c)?js$", "");
                if (Regex.Match(stem, @"^(dist|build|lib|out)/(.+)$") is { Success: true } m) { list.Add(Rel("src/" + m.Groups[2].Value + ".ts")); list.Add(Rel(m.Groups[2].Value + ".ts")); }
                list.Add(Rel(stem + ".ts"));
            }
            if (pkg is not null)
            {
                if (PackageStart().Match(pkg) is { Success: true } s) AddPath(s.Groups[1].Value);
                if (PackageMain().Match(pkg) is { Success: true } m) AddPath(m.Groups[1].Value);
            }
            foreach (var p in new[] { "src/main.ts", "src/main.js", "src/app.ts", "src/app.js", "src/server.ts", "src/server.js", "src/index.ts", "src/index.js",
                                      "app.js", "app.ts", "server.js", "server.ts", "index.js", "index.ts", "main.js", "main.ts" })
                list.Add(Rel(p));
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++) result.TryAdd(list[i], i);
            return result;
        }

        public List<SymbolDto> EntrySymbols(string kind) =>
            Entries.Where(e => e.Kind == kind).Select(e => ById.GetValueOrDefault(e.SymbolId)).Where(s => s is not null).DistinctBy(s => s!.Id).Select(s => Dto(s!)).ToList();

        public SymbolDto Dto(MapSymbol s) => Src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);

        /// <summary>显示名：类的方法是"类.方法"，匿名默认导出（default）用文件名。</summary>
        public string Label(MapSymbol s)
        {
            var name = s.Name == "default" ? FileStem(s.Path) : s.Name;
            if (s.Kind is SymbolKind.Method or SymbolKind.Function && s.ParentId is { } p && ById.TryGetValue(p, out var owner) && OutlineBuilder.IsType(owner.Kind))
                return owner.Name + "." + name;
            return name;
        }

        public static string FileStem(string path)
        {
            var name = MapSymbol.FileName(path);
            var dot = name.IndexOf('.');
            var stem = dot > 0 ? name[..dot] : name;
            if (stem == "index" && path.Contains('/')) stem = MapSymbol.FileName(MapBuilder.DirectoryOf(path));
            return stem;
        }

        public MapSymbol? Module(string path) => ByFile[path].FirstOrDefault(s => s.Kind == SymbolKind.Module);

        public IEnumerable<MapSymbol> Classes => ByFile.SelectMany(g => g).Where(s => s.Kind == SymbolKind.Class);

        public MapSymbol? Method(MapSymbol type, params string[] names)
        {
            _children ??= Input.Symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            foreach (var n in names)
                if (_children[type.Id].FirstOrDefault(s => s.Kind is SymbolKind.Method or SymbolKind.Function && s.Name == n) is { } hit) return hit;
            return null;
        }

        /// <summary>符号自己的函数文本（参数表、体）；不是函数返回 null。</summary>
        public JsFunction? FunctionOf(MapSymbol s)
        {
            if (s.Kind is not (SymbolKind.Function or SymbolKind.Method) || Text(s.Path) is not { } t) return null;
            var (st, en) = t.Span(s.StartLine, s.EndLine);
            return FunctionAt(s.Path, t.Code, st, en);
        }

        // ---------- import / require 与名字解析 ----------

        public Dictionary<string, (string Spec, string Export)> Imports(string file)
        {
            if (_imports.TryGetValue(file, out var cached)) return cached;
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            if (Text(file) is { } t)
            {
                var nc = t.NoComments;
                foreach (Match m in ImportFrom().Matches(nc))
                {
                    var spec = m.Groups[4].Value;
                    if (m.Groups[1].Success && m.Groups[1].Value != "type") map[m.Groups[1].Value] = (spec, "default");
                    if (m.Groups[3].Success) map[m.Groups[3].Value] = (spec, "*");
                    if (m.Groups[2].Success)
                        foreach (var part in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var bits = Regex.Replace(part, @"^type\s+", "").Split(" as ", StringSplitOptions.TrimEntries);
                            if (bits[0].Length > 0) map[bits.Length > 1 ? bits[1] : bits[0]] = (spec, bits[0]);
                        }
                }
                foreach (Match m in RequireDefault().Matches(nc)) map.TryAdd(m.Groups[1].Value, (m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : "default"));
                foreach (Match m in ImportRequire().Matches(nc)) map.TryAdd(m.Groups[1].Value, (m.Groups[2].Value, "default"));
                foreach (Match m in RequireNamed().Matches(nc))
                    foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var bits = part.Split(':', StringSplitOptions.TrimEntries);
                        var local = Regex.Replace(bits.Length > 1 ? bits[1] : bits[0], @"\s*=.*$", "");
                        if (local.Length > 0) map.TryAdd(local, (m.Groups[2].Value, bits[0]));
                    }
            }
            return _imports[file] = map;
        }

        /// <summary>相对说明符 → 容器里的文件（补扩展名、index、TS 的 ESM 写法 './a.js' 实际是 a.ts）；不是相对的或找不到返回 null。</summary>
        public string? ResolveFile(string fromFile, string spec)
        {
            if (!spec.StartsWith('.')) return null;
            var dir = MapBuilder.DirectoryOf(fromFile);
            var stack = new List<string>();
            foreach (var p in (dir.Length == 0 ? spec : dir + "/" + spec).Split('/'))
            {
                if (p is "." or "") continue;
                if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
                stack.Add(p);
            }
            var basePath = string.Join('/', stack);
            if (FileSet.Contains(basePath)) return basePath;
            var stem = Regex.Replace(basePath, @"\.(js|mjs|cjs)$", "");
            foreach (var ext in ScriptExtensions)
            {
                if (FileSet.Contains(stem + ext)) return stem + ext;
                if (FileSet.Contains(basePath + "/index" + ext)) return basePath + "/index" + ext;
            }
            return null;
        }

        /// <summary>文件的默认导出：标了 export default 的函数 / 类；没有返回 null。</summary>
        public MapSymbol? DefaultExport(string file) =>
            ByFile[file].Where(s => s.Visibility == ScriptDefault && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Method).OrderBy(s => s.StartLine).FirstOrDefault();

        private const string ScriptDefault = "export default";

        /// <summary>文件里叫这个名字的顶层函数 / 类（含 exports.f = function…、对象字面量里的方法）。</summary>
        public MapSymbol? Named(string file, string name) =>
            ByFile[file].Where(s => s.Name == name && s.Kind is SymbolKind.Function or SymbolKind.Method or SymbolKind.Class
                                    && (s.ParentId is null || ById.TryGetValue(s.ParentId.Value, out var p) && p.Kind == SymbolKind.Module))
                .OrderBy(s => s.StartLine).FirstOrDefault();

        /// <summary>名字（可带一段限定 a.b）在 file 里指什么：本文件的顶层函数 / 类 → import / require 的仓库文件 → npm 包 → 容器里唯一同名的函数 / 类。</summary>
        public Resolved? Resolve(string name, string file)
        {
            string? member = null;
            var dot = name.IndexOf('.');
            if (dot > 0) { member = name[(dot + 1)..]; name = name[..dot]; if (member.Contains('.')) member = member[(member.LastIndexOf('.') + 1)..]; }
            if (member is null && Named(file, name) is { } local) return new Resolved(local, null, file, name);
            if (Imports(file).TryGetValue(name, out var imp))
            {
                if (ResolveFile(file, imp.Spec) is not { } target) return new Resolved(null, imp.Spec, null, member ?? imp.Export);
                if (member is not null)
                {
                    // ctrl.list / auth.authenticate：模块对象上的成员
                    var inner = imp.Export is "default" or "*" ? member : imp.Export;
                    return new Resolved(Named(target, inner) ?? ClassMember(target, imp.Export, member), null, target, inner);
                }
                if (imp.Export == "default") return new Resolved(DefaultExport(target), null, target, "default");
                return new Resolved(Named(target, imp.Export), null, target, imp.Export);
            }
            if (member is not null)
            {
                // 本文件的类 / 对象：Payments.charge
                if (Named(file, name) is { Kind: SymbolKind.Class } cls && Method(cls, member) is { } mm) return new Resolved(mm, null, file, member);
                return null;
            }
            var same = ByFile.SelectMany(g => g).Where(s => s.Name == name && s.Kind is SymbolKind.Function or SymbolKind.Class).Take(2).ToList();
            return same.Count == 1 ? new Resolved(same[0], null, same[0].Path, name) : null;
        }

        private MapSymbol? ClassMember(string file, string export, string member)
        {
            var cls = export == "default" ? DefaultExport(file) : Named(file, export);
            return cls is { Kind: SymbolKind.Class } ? Method(cls, member) : null;
        }

        /// <summary>按类名找容器里的类（先看 file 的 import 指向，再看唯一同名）。</summary>
        public MapSymbol? ClassNamed(string name, string file)
        {
            if (Resolve(name, file) is { Symbol: { Kind: SymbolKind.Class } c }) return c;
            var all = Classes.Where(s => s.Name == name).ToList();
            return all.Count >= 1 ? all.OrderBy(s => s.Path, StringComparer.Ordinal).First() : null;
        }

        // ---------- 路由器变量（Express Router / koa-router）----------

        /// <summary>
        /// (文件, 变量) → 路由器：自带的前缀（koa-router 的 new Router({ prefix })；Express 为空）与声明处紧跟的链式调用起点
        /// （const api = Router().use(a).use(b)，ChainAt 是 Router(...) 之后那个 '.' 的下一位；没有为 -1）。
        /// 直接导出的匿名路由器（export default Router().use('/api', api)）记作变量 "default"。
        /// </summary>
        public Dictionary<(string File, string Var), RouterDecl> Routers
        {
            get
            {
                if (_routers is not null) return _routers;
                _routers = [];
                foreach (var file in ScriptFiles)
                {
                    if (Text(file) is not { } t) continue;
                    foreach (Match m in RouterDeclRx().Matches(t.Code))
                    {
                        var prefix = "";
                        var open = m.Index + m.Length - 1;
                        var (args, close) = SplitArgsAt(t.Code, open);
                        if (args.Count > 0 && t.Code[args[0].Start] == '{')
                        {
                            var obj = t.NoComments[args[0].Start..args[0].End];
                            if (Regex.Match(obj, @"\bprefix\s*:\s*['""`]([^'""`]*)['""`]") is { Success: true } p) prefix = p.Groups[1].Value;
                        }
                        var chain = -1;
                        if (close > 0 && SkipWs(t.Code, close + 1) is var j && j < t.Code.Length && t.Code[j] == '.') chain = j + 1;
                        var name = m.Groups[1].Success ? m.Groups[1].Value : "default";
                        _routers.TryAdd((file, name), new RouterDecl(prefix, chain, m.Index));
                    }
                }
                return _routers;
            }
        }

        /// <summary>文件导出的路由器变量：直接导出的匿名路由器 / module.exports = r / export default r，否则文件里唯一的那个。</summary>
        public string? ExportedRouter(string file)
        {
            var vars = Routers.Keys.Where(k => k.File == file).Select(k => k.Var).ToList();
            if (vars.Count == 0 || Text(file) is not { } t) return null;
            if (vars.Contains("default")) return "default";
            foreach (Match m in Regex.Matches(t.Code, @"(?:module\s*\.\s*exports\s*=|export\s+default)\s*([\w$]+)\s*;?"))
                if (vars.Contains(m.Groups[1].Value)) return m.Groups[1].Value;
            return vars.Count == 1 ? vars[0] : null;
        }

        // ---------- 调用 ----------

        /// <summary>符号体里的调用（源码顺序）：Sources.Calls；给空（文件模块、旧索引）时用它的调用边。外部调用按源码补上接收者（logger.info）。</summary>
        public List<FrameworkCall> CallsOf(MapSymbol s)
        {
            var calls = s.Kind is SymbolKind.Function or SymbolKind.Method ? Src.Calls(s.Id) : [];
            var list = calls.Count > 0 ? Ordered(calls).ToList() : EdgeCalls(s.Id);
            var t = Text(s.Path);
            return t is null ? list : list.Select(c => Relabel(c, t)).ToList();
        }

        /// <summary>
        /// 内联函数（没有符号）里的调用：取包住它的最内层函数 / 方法（没有就是文件模块）的调用，按偏移截 [start, end)；
        /// ctx 相对内联函数自己的体重算（外层给的全是 callback）。
        /// </summary>
        public List<FrameworkCall> CallsInSpan(string path, int start, int end, int bodyStart)
        {
            if (Text(path) is not { } t) return [];
            var l0 = t.LineAt(start);
            var l1 = t.LineAt(Math.Max(start, end - 1));
            var host = ByFile[path].Where(s => s.Kind is SymbolKind.Function or SymbolKind.Method && s.StartLine <= l0 && s.EndLine >= l1)
                .OrderByDescending(s => s.StartLine).ThenBy(s => s.EndLine).FirstOrDefault();
            var all = host is not null ? CallsOf(host) : Module(path) is { } mod ? EdgeCalls(mod.Id).Select(c => Relabel(c, t)).ToList() : [];
            if (host is not null && all.Count == 0 && Module(path) is { } m2) all = EdgeCalls(m2.Id).Select(c => Relabel(c, t)).ToList();
            return all.Where(c => c.Pos is { } p ? p >= start && p < end : c.Line >= l0 && c.Line <= l1)
                .Select(c => c with { Ctx = c.Pos is { } p ? CtxAt(t.Code, p, bodyStart) : c.Ctx })
                .ToList();
        }

        /// <summary>符号的调用边（calls，置信度 ≥ 0.4，不含 conduit）拼成 FrameworkCall，按位置排。</summary>
        public List<FrameworkCall> EdgeCalls(long fromId)
        {
            _callEdges ??= Input.Edges.Where(e => e.Kind == EdgeKind.Calls && e.Confidence >= 0.4 && e.Via != "conduit").GroupBy(e => e.FromId).ToDictionary(g => g.Key, g => g.ToList());
            if (!_callEdges.TryGetValue(fromId, out var edges)) return [];
            var list = new List<FrameworkCall>();
            foreach (var e in edges)
            {
                if (!ById.TryGetValue(e.ToId, out var t)) continue;
                var kind = t.Kind == SymbolKind.External ? "external" : OutlineBuilder.IsType(t.Kind) ? "new" : "call";
                var label = t.Kind == SymbolKind.External ? OutlineBuilder.ExternalLabel(t.QualifiedName) : Label(t);
                list.Add(new FrameworkCall(t.Id, kind, label, t.QualifiedName, e.Line, e.Pos, null, t.Kind is SymbolKind.Function or SymbolKind.Method, []));
            }
            return Ordered(list).ToList();
        }

        /// <summary>外部调用的名字只有方法名（external:info）：按源码补上接收者（logger.info；this.logger.info → logger.info）。</summary>
        private static FrameworkCall Relabel(FrameworkCall c, JsSource t)
        {
            var kids = c.Children.Count == 0 ? c.Children : c.Children.Select(k => Relabel(k, t)).ToList();
            if (c.Kind is not ("external" or "unresolved") || c.Pos is not { } p || p <= 0 || p >= t.Code.Length) return kids == c.Children ? c : c with { Children = kids };
            var name = c.Label.Contains('.') ? c.Label[(c.Label.LastIndexOf('.') + 1)..] : c.Label;
            if (name.Length == 0 || string.CompareOrdinal(t.Code, p, name, 0, name.Length) != 0) return c with { Children = kids };
            var k = SkipWsBack(t.Code, p - 1);
            if (k < 0 || t.Code[k] != '.') return c with { Label = name, Children = kids };
            k = SkipWsBack(t.Code, k - 1);
            if (k >= 0 && t.Code[k] == '?') k = SkipWsBack(t.Code, k - 1);
            var recv = WordBefore(t.Code, k + 1, out _);
            if (recv.Length == 0 || recv == "this") return c with { Label = name, Children = kids };
            return c with { Label = recv + "." + name, Children = kids };
        }
    }

    // ================= 函数体：按 next() 切 =================

    /// <summary>中间件体按 next() 切开：前置、后置、res.on('finish') 回调里的；是否找到 next、是否有条件（有分支里的 next，或有分支不调 next 直接 return）。</summary>
    internal sealed record BodySplit(List<FrameworkCall> Pre, List<FrameworkCall> Post, List<FrameworkCall> Finish, bool HasNext, bool Conditional);

    /// <summary>JS 里不算"关键调用"的外部方法名（JSON / 数组 / 字符串 / 日期 / Promise 这一类）。</summary>
    private static readonly HashSet<string> NoiseMembers = new(StringComparer.Ordinal)
    {
        "stringify", "toString", "push", "pop", "shift", "unshift", "map", "filter", "forEach", "reduce", "some", "every",
        "includes", "indexOf", "join", "split", "slice", "splice", "substr", "substring", "replace", "replaceAll", "trim", "toLowerCase", "toUpperCase",
        "startsWith", "endsWith", "toISOString", "getTime", "then", "catch", "finally", "bind", "call", "apply",
        "concat", "sort", "hasOwnProperty", "pipe", "bigint", "toFixed", "padStart",
    };

    /// <summary>不带接收者时才算噪声的名字（String(x)、Number(x)、require、new Error…）；带接收者的 repo.find / tenants.resolve 是正经调用。</summary>
    private static readonly HashSet<string> BareNoise = new(StringComparer.Ordinal)
    {
        "Date", "Number", "String", "Boolean", "parseInt", "parseFloat", "isNaN", "Array", "Object", "Map", "Set", "Promise", "require", "Error", "TypeError",
        "RangeError", "Symbol", "setTimeout", "clearTimeout", "setImmediate", "encodeURIComponent", "decodeURIComponent", "Buffer", "now", "resolve", "reject",
        "find", "findIndex", "from", "of", "floor", "round", "ceil", "max", "min", "random", "abs", "match", "test", "exec", "isArray", "assign", "freeze",
        "keys", "values", "entries", "parse", "hrtime", "isBuffer",
    };

    private static readonly string[] NoiseReceivers = ["console.", "JSON.", "Math.", "Object.", "Array.", "Date.", "Promise.", "Number.", "String.", "Reflect.", "process."];

    /// <summary>关键调用：去掉没解析到的、噪声外部调用（见 <see cref="NoiseMembers"/>，console.* / JSON.* 一类）、没有构造逻辑的 new。</summary>
    internal static bool JsKeep(FrameworkCall c)
    {
        switch (c.Kind)
        {
            case "unresolved": return false;
            case "new": return c.Expandable;
            case "external":
                if (NoiseReceivers.Any(r => c.Label.StartsWith(r, StringComparison.Ordinal))) return false;
                var bare = !c.Label.Contains('.');
                var member = bare ? c.Label : c.Label[(c.Label.LastIndexOf('.') + 1)..];
                return !NoiseMembers.Contains(member) && !(bare && BareNoise.Contains(member));
            default: return true;
        }
    }

    /// <summary>
    /// fn 的体里按 next 切：nextName 是 next 参数名（Nest 拦截器 handle = true 时找 next.handle(…)）。主 next = 第一个不在分支 / catch 里的那次（都在分支里取最后一次）；
    /// 调用位置在主 next 的调用名之前是前置、之后是后置；next 自己（含 next.handle）不算；res.on / once('finish' | 'close', …) 回调里的单列。
    /// </summary>
    internal static BodySplit SplitAtNext(Ctx x, JsFunction fn, IReadOnlyList<FrameworkCall> calls, string? nextName, bool handle = false)
    {
        var t = x.Text(fn.Path);
        var pre = new List<FrameworkCall>();
        var post = new List<FrameworkCall>();
        var finish = new List<FrameworkCall>();
        if (t is null) return new BodySplit(pre, post, finish, false, false);
        var code = t.Code;
        var body = (Start: fn.BodyStart, End: Math.Min(fn.End, code.Length));
        var nexts = new List<(int Start, int Paren, string? Ctx)>();
        if (nextName is { Length: > 0 } && nextName != "{}")
            foreach (Match m in Regex.Matches(code[..body.End], @"(?<![\w$.])" + Regex.Escape(nextName) + (handle ? @"\s*\.\s*handle" : "") + @"\s*\("))
                if (m.Index >= body.Start) nexts.Add((m.Index, m.Index + m.Length - 1, CtxAt(code, m.Index, body.Start)));
        (int Start, int Paren, string? Ctx)? main = null;
        foreach (var n in nexts)
            if (n.Ctx is not (CallContext.Cond or CallContext.Catch)) { main = n; break; }
        if (main is null && nexts.Count > 0) main = nexts[^1];
        var conditional = nexts.Any(n => n.Ctx == CallContext.Cond);
        if (main is { } mn && !conditional)
            foreach (Match r in Regex.Matches(code[..mn.Start], @"(?<![\w$.])(?:return|throw)\b|(?<![\w$.])[\w$]+\s*\.\s*throw\s*\("))
                if (r.Index >= body.Start && CtxAt(code, r.Index, body.Start) == CallContext.Cond) { conditional = true; break; }
        // res.on('finish', …) / onFinished(res, …)
        var finishSpans = new List<(int CalleeStart, int CalleeEnd, int Close)>();
        foreach (Match m in Regex.Matches(t.NoComments[..body.End], @"(?<![\w$.])[\w$]+\s*\.\s*(on|once)\s*\(\s*['""](?:finish|close)['""]|(?<![\w$.])(onFinished|onHeaders)\s*\("))
        {
            if (m.Index < body.Start) continue;
            var open = t.NoComments.IndexOf('(', m.Index);
            var close = MatchClose(code, open);
            var g = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];
            if (close > open) finishSpans.Add((g.Index, g.Index + g.Length, close));
        }
        // 主 next 所在的块；它之后的调用里，落在 else 分支块里的是"不调 next 的那条路"，算前置（短路）不算后置；catch 块是 next 出错之后，照样算后置
        var b0 = main is { } mb ? UnmatchedOpenBefore(code, mb.Start, '{', '}') : -1;
        var b1 = b0 >= 0 ? MatchClose(code, b0) is var bc and >= 0 ? bc : code.Length : code.Length;
        bool IsPost(int pos)
        {
            if (main is not { } m || pos <= m.Paren) return false;
            if (pos < b1 || b0 < body.Start) return true;
            var c0 = UnmatchedOpenBefore(code, pos, '{', '}');
            if (c0 <= b0) return true;
            var between = code[Math.Min(b1 + 1, c0)..c0].TrimStart();
            return !between.StartsWith("else", StringComparison.Ordinal);
        }
        foreach (var c in calls)
        {
            var pos = c.Pos ?? t.LineStart(c.Line);
            if (pos < body.Start || pos >= body.End) continue;
            if (nexts.Any(n => pos >= n.Start && pos <= n.Paren)) continue;
            if (nextName is not null && c.Kind is "external" or "unresolved" && (c.Label == nextName || c.Label.StartsWith(nextName + ".", StringComparison.Ordinal))) continue;
            if (finishSpans.Any(f => pos >= f.CalleeStart && pos < f.CalleeEnd)) continue;
            if (!JsKeep(c)) continue;
            if (finishSpans.Any(f => pos > f.CalleeEnd && pos < f.Close)) finish.Add(c);
            else if (IsPost(pos)) post.Add(c);
            else pre.Add(c);
        }
        return new BodySplit(pre, post, finish, main is not null, conditional);
    }

    /// <summary>一组调用画成一行行（Cap 过）。</summary>
    internal static List<FlowNodeDto> Rows(IEnumerable<FrameworkCall> calls, string path, string actor, string? note) =>
        Cap(calls.Select(c => CallNode(c, path, actor, note)).ToList(), actor);

    internal static string JoinRoute(params string[] parts)
    {
        var segs = parts.SelectMany(p => (p ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        return "/" + string.Join('/', segs);
    }
}
