using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Java 入口（源码文本 + 符号的 Path / 行范围 / 名字 / ParentId 定位，不依赖限定名细节；注解见 <see cref="JavaSource.Scan"/>）：
/// Spring MVC / WebFlux 注解控制器（类上 @RestController / @Controller；类级 @RequestMapping 前缀，数组取第一个；方法级 @GetMapping … / @RequestMapping(method = …)，
/// 方法级数组每条一个入口；路径里的 {ownerId} 与 * 原样保留；路径表达式里的常量按同文件 / 仓库内唯一的 String 常量展开）、
/// JAX-RS（类 @Path + 方法 @GET / @POST … + 方法 @Path）、gRPC 服务端（extends XxxGrpc.XxxImplBase 的 public 方法、签名含 StreamObserver，route 用 proto 的 PascalCase）、
/// @Scheduled（job）、@KafkaListener / @RabbitListener / @JmsListener / @StreamListener / @EventListener（handler，route = topic / queue / 事件类型）、
/// @SpringBootApplication 类的 main（hosted）；容器里没有 Spring Boot 的 main 时，其余 static main(String[]) 也算（hosted）。
/// Spring Cloud Gateway 不是入口（它是代理，路由穿透见 RouteLinker）；@FeignClient / Retrofit 接口的映射注解是出口，不是入口。入口的 File / Line 是方法的声明行。
/// </summary>
public static partial class EntryDiscovery
{
    private static readonly HashSet<string> SpringControllers = new(StringComparer.Ordinal) { "RestController", "Controller" };
    private static readonly HashSet<string> JaxRsVerbs = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };
    private static readonly HashSet<string> EventListeners = new(StringComparer.Ordinal) { "EventListener", "TransactionalEventListener" };

    // extends hipstershop.AdServiceGrpc.AdServiceImplBase   extends AdServiceImplBase（导入了嵌套类）
    [GeneratedRegex(@"\bextends\s+(?:[\w.]*?\b(\w+)Grpc\s*\.\s*)?(\w+)ImplBase\b")]
    private static partial Regex JavaGrpcBase();
    [GeneratedRegex(@"\bstatic\b[^(]*\bvoid\s+main\s*\(\s*(?:final\s+)?String\s*(?:\[\s*\]|\.\.\.)")]
    private static partial Regex JavaMainSignature();
    // route(GET("/x")…, h::m)   .andRoute(POST("/x"), h::m)
    [GeneratedRegex(@"(?<![\w.])(?:RouterFunctions\s*\.\s*)?route\s*\(|\.\s*andRoute\s*\(")]
    private static partial Regex JavaRouteCall();
    [GeneratedRegex(@"^\s*(?:RequestPredicates\s*\.\s*)?(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s*\(\s*""([^""]*)""")]
    private static partial Regex JavaRoutePredicate();
    // RouterFunctions.route().GET("/x", h::m)
    [GeneratedRegex(@"\.\s*(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s*\((?=\s*"")")]
    private static partial Regex JavaRouteBuilderVerb();

    internal static bool IsJava(string path) => JavaSource.IsJava(path);

    /// <summary>text 里 index 处所在的行是不是注释行（// 或块注释的 * 行）。</summary>
    private static bool InJavaComment(string[] lines, string text, int index)
    {
        var line = text.AsSpan(0, index).Count('\n');
        var t = line < lines.Length ? lines[line].TrimStart() : "";
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*') || t.StartsWith("/*", StringComparison.Ordinal);
    }

    private static void DiscoverJava(Scan scan)
    {
        var javaSymbols = scan.Symbols.Where(s => IsJava(s.Path) && s.Kind != SymbolKind.External && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)).ToList();
        if (javaSymbols.Count == 0) return;
        var files = javaSymbols.Select(s => s.Path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

        var annotations = new Dictionary<string, JavaSource.FileAnnotations>(StringComparer.Ordinal);
        JavaSource.FileAnnotations AnnotationsOf(string file) =>
            annotations.TryGetValue(file, out var a) ? a : annotations[file] = JavaSource.Scan(scan.ReadLines(file));
        var constants = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> ConstantsOf(string file) =>
            constants.TryGetValue(file, out var c) ? c : constants[file] = JavaSource.StringConstants(scan.ReadLines(file));
        // 别的文件里的常量（Paths.API）：仓库里唯一的同名 String 常量
        Dictionary<string, string?>? global = null;
        string? Global(string name)
        {
            if (global is null)
            {
                global = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var f in files)
                    foreach (var (k, v) in ConstantsOf(f))
                        global[k] = global.TryGetValue(k, out var old) && old != v ? null : v;
            }
            return global.GetValueOrDefault(name);
        }
        Func<string, string?> ConstantResolver(string file) => name => ConstantsOf(file).TryGetValue(name, out var v) ? v : Global(name);

        // Java 文件只进非前端容器（前端容器的成员目录包住整个网关项目时，不把网关的控制器算成前端的入口）
        IReadOnlyList<ArchNode> Owners(string path)
        {
            var owners = scan.ContainersOf(path);
            return owners.Any(c => c.SubKind != "frontend") ? owners.Where(c => c.SubKind != "frontend").ToList() : owners;
        }
        void AddAll(MapSymbol m, string ruleId, string kind, string? route, string? method)
        {
            foreach (var c in Owners(m.Path)) scan.Add(new EntryPoint(m.Id, c.Key, ruleId, kind, route, method, m.Path, m.StartLine));
        }

        var hosted = new List<(MapSymbol Main, bool SpringBoot)>();
        var picocli = new List<(MapSymbol Type, string Name, List<string> Subcommands, MapSymbol? Handler, List<(MapSymbol Method, string Name)> Methods)>();
        foreach (var type in javaSymbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum).OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine))
        {
            var lines = scan.ReadLines(type.Path);
            var fileAnnotations = AnnotationsOf(type.Path);
            var typeAnnotations = fileAnnotations.Of(type.StartLine);
            var constant = ConstantResolver(type.Path);
            var methods = scan.Children[type.Id].Where(m => m.Kind == SymbolKind.Method && m.Name != type.Name).OrderBy(m => m.StartLine).ToList();
            bool Has(IReadOnlyList<JavaSource.Annotation> list, string name) => list.Any(a => a.Name == name);
            JavaSource.Annotation? Find(IReadOnlyList<JavaSource.Annotation> list, string name) => list.FirstOrDefault(a => a.Name == name);
            string MethodRoute(MapSymbol m) => type.Name + "." + m.Name;

            // ── Spring MVC / WebFlux ──
            if (type.Kind == SymbolKind.Class && typeAnnotations.Any(a => SpringControllers.Contains(a.Name)))
            {
                var classMapping = Find(typeAnnotations, "RequestMapping");
                var classPath = classMapping is null ? "" : JavaSource.Values(classMapping.Args, constant, "value", "path").Select(v => v.Value).FirstOrDefault() ?? "";
                foreach (var m in methods)
                {
                    foreach (var a in fileAnnotations.Of(m.StartLine).Where(JavaSource.IsSpringMapping))
                        foreach (var (p, verb) in JavaSource.SpringMapping(a, constant))
                            AddAll(m, "java.spring-mvc", "http", JoinRoute(classPath, p), verb);
                }
            }

            // ── JAX-RS ──
            if (type.Kind == SymbolKind.Class && Find(typeAnnotations, "Path") is { } classPathAnnotation)
            {
                var classPath = JavaSource.Values(classPathAnnotation.Args, constant, "value").Select(v => v.Value).FirstOrDefault() ?? "";
                foreach (var m in methods)
                {
                    var ma = fileAnnotations.Of(m.StartLine);
                    var verb = ma.FirstOrDefault(a => JaxRsVerbs.Contains(a.Name) && a.Args is null)?.Name;
                    if (verb is null) continue;
                    var methodPath = Find(ma, "Path") is { } mp ? JavaSource.Values(mp.Args, constant, "value").Select(v => v.Value).FirstOrDefault() : "";
                    if (methodPath is null) continue;
                    AddAll(m, "java.jaxrs", "http", JoinRoute(classPath, methodPath), verb);
                }
            }

            // ── gRPC 服务端 ──
            if (type.Kind == SymbolKind.Class && JavaGrpcBase().Match(JavaSource.TypeHeader(lines, type.StartLine)) is { Success: true } grpc)
            {
                var service = grpc.Groups[1].Success ? grpc.Groups[1].Value : grpc.Groups[2].Value;
                foreach (var m in methods.Where(m => m.Visibility is null or "public"))
                    if (JavaSource.Signature(lines, m.StartLine, m.Name).Contains("StreamObserver", StringComparison.Ordinal))
                        AddAll(m, "java.grpc", "grpc", service + "/" + JavaSource.Pascal(m.Name), null);
            }

            // ── 定时任务与消息监听（任意类）──
            foreach (var m in methods)
            {
                foreach (var a in fileAnnotations.Of(m.StartLine))
                {
                    switch (a.Name)
                    {
                        case "Scheduled":
                            AddAll(m, "java.scheduled", "job", MethodRoute(m), null);
                            break;
                        case "KafkaListener":
                            AddListener(m, a, "java.kafka", "topics", "topicPattern", "value");
                            break;
                        case "RabbitListener":
                            AddListener(m, a, "java.rabbit", "queues", "value");
                            break;
                        case "JmsListener":
                            AddListener(m, a, "java.jms", "destination", "value");
                            break;
                        case "StreamListener":
                            AddListener(m, a, "java.stream", "value", "target");
                            break;
                        case var e when EventListeners.Contains(e):
                            var classes = JavaSource.Values(a.Args, _ => null, "classes", "value").Select(v => Regex.Replace(v.Raw, @"\.class$", "")).Select(n => n[(n.LastIndexOf('.') + 1)..]).ToList();
                            if (classes.Count == 0 && JavaSource.FirstParameterType(JavaSource.Signature(lines, m.StartLine, m.Name), m.Name) is { } param) classes.Add(param);
                            if (classes.Count == 0) classes.Add(MethodRoute(m));
                            foreach (var cls in classes) AddAll(m, "java.event", "handler", cls, null);
                            break;
                    }
                }
            }

            // ── picocli：@Command(name = "x", subcommands = {…}) 类的 call / run，以及类里 @Command 方法（子命令）──
            if (Find(typeAnnotations, "Command") is { } command && Array.Exists(lines, l => l.Contains("picocli", StringComparison.Ordinal)))
            {
                var name = JavaSource.Keyword(command.Args, "name") is not null ? JavaSource.Values(command.Args, constant, "name").Select(v => v.Value).FirstOrDefault() : null;
                var subs = JavaSource.Keyword(command.Args, "subcommands") is not null
                    ? JavaSource.Values(command.Args, _ => null, "subcommands").Select(v => Regex.Replace(v.Raw, @"\.class$", "")).Select(n => n[(n.LastIndexOf('.') + 1)..]).ToList()
                    : [];
                var handler = methods.FirstOrDefault(m => m.Name is "call" or "run");
                var methodCommands = new List<(MapSymbol Method, string Name)>();
                foreach (var m in methods)
                    if (Find(fileAnnotations.Of(m.StartLine), "Command") is { } mc)
                        methodCommands.Add((m, (JavaSource.Keyword(mc.Args, "name") is not null ? JavaSource.Values(mc.Args, constant, "name").Select(v => v.Value).FirstOrDefault() : null) ?? m.Name));
                picocli.Add((type, name ?? Kebab(type.Name), subs, handler, methodCommands));
            }

            // ── main ──
            var springBoot = Has(typeAnnotations, "SpringBootApplication");
            foreach (var m in methods.Where(m => m.Name == "main"))
                if (JavaMainSignature().IsMatch(JavaSource.Signature(lines, m.StartLine, m.Name))) hosted.Add((m, springBoot));

            void AddListener(MapSymbol m, JavaSource.Annotation a, string ruleId, params string[] keys)
            {
                // 解析不了的常量（Sink.INPUT）用原文；${…} 占位符原样
                var names = JavaSource.Values(a.Args, constant, keys).Select(v => v.Value ?? v.Raw).Where(n => n.Length > 0).ToList();
                if (names.Count == 0) names.Add(MethodRoute(m));
                foreach (var n in names) AddAll(m, ruleId, "handler", n, null);
            }
        }

        // ── WebFlux 函数式路由：route(GET("/x"), h::m) / .andRoute(POST("/x"), h::m) / RouterFunctions.route().GET("/x", h::m) ──
        // 入口落在处理方法上：h 的声明类型（参数 / 字段）里叫 m 的方法；拿不到类型时取同容器里唯一叫 m 的方法。nest(path("/api"), …) 的前缀不拼（少见，不猜）
        var methodsByName = javaSymbols.Where(s => s.Kind == SymbolKind.Method).ToLookup(s => s.Name, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            if (!Array.Exists(lines, l => l.Contains("::", StringComparison.Ordinal)) || !Array.Exists(lines, l => l.Contains("route", StringComparison.OrdinalIgnoreCase))) continue;
            var text = string.Join('\n', lines);
            MapSymbol? Handler(string variable, string method)
            {
                var decl = Regex.Match(text, $@"\b([A-Z]\w*)(?:<[^<>]*>)?\s+{Regex.Escape(variable)}\b");
                var candidates = methodsByName[method].ToList();
                if (decl.Success)
                {
                    var typed = candidates.Where(c => c.ParentId is { } p && scan.ById.TryGetValue(p, out var t) && t.Name == decl.Groups[1].Value).ToList();
                    if (typed.Count == 1) return typed[0];
                }
                var owners = Owners(file).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                var local = candidates.Where(c => Owners(c.Path).Any(o => owners.Contains(o.Key))).ToList();
                return local.Count == 1 ? local[0] : null;
            }
            void Register(string verb, string path, string handlerExpr)
            {
                var reference = Regex.Match(handlerExpr.Trim(), @"^(?:this\s*\.\s*)?(\w+)\s*::\s*(\w+)$");
                if (reference.Success && Handler(reference.Groups[1].Value, reference.Groups[2].Value) is { } h)
                    AddAll(h, "java.webflux-fn", "http", JoinRoute(path), verb);
            }
            foreach (Match m in JavaRouteCall().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count < 2 || JavaRoutePredicate().Match(args[0]) is not { Success: true } pred) continue;
                Register(pred.Groups[1].Value, pred.Groups[2].Value, args[^1]);
            }
            foreach (Match m in JavaRouteBuilderVerb().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index)) continue;
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (args.Count < 2 || ArgParser.Literal(args[0]) is not { } path) continue;
                Register(m.Groups[1].Value, path, args[^1]);
            }
        }

        // picocli 的命令路径：父命令来自 subcommands；根命令（没有父）的名字是程序名，不进子命令路径
        var picocliParent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in picocli) foreach (var sub in p.Subcommands) picocliParent.TryAdd(sub, p.Type.Name);
        var picocliByName = picocli.GroupBy(p => p.Type.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string PicocliPath(string typeName)
        {
            var parts = new List<string>();
            for (var (cur, depth) = (typeName, 0); depth < 8 && picocliParent.TryGetValue(cur, out var parent); depth++)
            {
                parts.Insert(0, picocliByName.TryGetValue(cur, out var info) ? info.Name : Kebab(cur));
                cur = parent;
            }
            return string.Join(' ', parts);
        }
        foreach (var p in picocli)
        {
            var path = PicocliPath(p.Type.Name);
            if (p.Handler is not null) AddAll(p.Handler, "java.picocli", "command", path.Length > 0 ? path : p.Name, null);
            foreach (var (m, name) in p.Methods) AddAll(m, "java.picocli", "command", path.Length > 0 ? path + " " + name : name, null);
        }

        // 容器里有 Spring Boot 的 main 就只认它；没有才认其余的 static main（工具类的 main 不抢启动入口）
        foreach (var (main, springBoot) in hosted)
            foreach (var c in Owners(main.Path))
            {
                if (!springBoot && hosted.Any(h => h.SpringBoot && Owners(h.Main.Path).Any(o => o.Key == c.Key))) continue;
                scan.Add(new EntryPoint(main.Id, c.Key, springBoot ? "java.spring-boot" : "java.main", "hosted", c.Name, null, main.Path, main.StartLine));
            }
    }
}
