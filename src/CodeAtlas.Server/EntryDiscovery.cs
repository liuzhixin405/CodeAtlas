using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 入口识别：索引里的继承 / 实现边说明"这是什么"，声明上方的特性文本给出路由。
/// 规则 v1：ASP.NET 控制器（经中间基类）、Minimal API、SignalR Hub、BackgroundService / IHostedService、Quartz IJob、分发处理器（handles 边）、Next.js / React 页面与 API 路由。
/// v2 补：gRPC 服务（MapGrpcService&lt;T&gt; 字面量或 external *Base + ServerCallContext）、Hangfire 作业（RecurringJob / BackgroundJob 注册字面量）、MassTransit IConsumer&lt;T&gt;（handles 边，单独的 ruleId）。
/// </summary>
public static partial class EntryDiscovery
{
    private static readonly HashSet<string> ControllerBases = new(StringComparer.Ordinal) { "ControllerBase", "Controller" };

    /// <summary>沿继承链找 ControllerBase / Hub 等框架基类时最多往上走几层。</summary>
    private const int MaxBaseDepth = 8;
    private static readonly HashSet<string> HostedBases = new(StringComparer.Ordinal) { "BackgroundService" };
    private static readonly HashSet<string> HostedInterfaces = new(StringComparer.Ordinal) { "IHostedService", "IHostedLifecycleService" };
    private static readonly HashSet<string> JobInterfaces = new(StringComparer.Ordinal) { "IJob" };
    private static readonly HashSet<string> HubBases = new(StringComparer.Ordinal) { "Hub" };
    private static readonly HashSet<string> ConsumerInterfaces = new(StringComparer.Ordinal) { "IConsumer" };
    private static readonly HashSet<string> HttpVerbs = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    // app.MapGrpcService<GreeterService>()
    [GeneratedRegex(@"\.MapGrpcService\s*<\s*(?:[\w.]+\.)?(\w+)\s*>")]
    private static partial Regex MapGrpc();
    // RecurringJob.AddOrUpdate<T>("id", x => x.Method(...), …) / BackgroundJob.Enqueue<T>(x => x.Method(...)) / Schedule<T> / ContinueJobWith<T>
    [GeneratedRegex(@"\b(?:RecurringJob|BackgroundJob)\s*\.\s*(?:AddOrUpdate|Enqueue|Schedule|ContinueJobWith)\s*<\s*(?:[\w.]+\.)?(\w+)\s*>\s*\([^;]*?\b\w+\s*=>\s*\w+\s*\.\s*(\w+)\s*\(")]
    private static partial Regex HangfireTyped();
    // RecurringJob.AddOrUpdate("id", () => Static.Method(...), …)
    [GeneratedRegex(@"\b(?:RecurringJob|BackgroundJob)\s*\.\s*(?:AddOrUpdate|Enqueue|Schedule|ContinueJobWith)\s*\([^;]*?\(\s*\)\s*=>\s*(?:[\w.]+\.)?(\w+)\s*\.\s*(\w+)\s*\(")]
    private static partial Regex HangfireStatic();

    private static readonly HashSet<string> HostSubKinds = new(StringComparer.Ordinal) { "dotnet-exe", "dotnet-worker", "dotnet-web" };
    // static void Main(string[] args) / static int Main() / static async Task<int> Main(string[] args)
    [GeneratedRegex(@"\bstatic\b[^(]*\bMain\s*\(\s*(?:string\s*\[\s*\]\s*\w+)?\s*\)")]
    private static partial Regex StaticMain();

    /// <summary>
    /// 顶级语句：Roslyn 给它补的 function 符号（限定名 Program.&lt;Main&gt;$(string[])，手造数据是 &lt;top-level&gt;）。它没有父符号，入库时挂到文件模块上；
    /// 顶级语句里的局部函数（Program.&lt;Main&gt;$(string[]).Usage()）父符号是它，不算。
    /// </summary>
    private static bool IsTopLevelStatements(MapSymbol s, IReadOnlyDictionary<long, MapSymbol> byId) =>
        s.Kind == SymbolKind.Function
        && (s.ParentId is not { } p || !byId.TryGetValue(p, out var parent) || parent.Kind is SymbolKind.Module or SymbolKind.Namespace)
        && (s.QualifiedName.Contains("<Main>$", StringComparison.Ordinal) || s.QualifiedName.Contains("<top-level>", StringComparison.Ordinal));

    [GeneratedRegex(@"\[\s*Route\s*\(\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex RouteAttr();
    [GeneratedRegex(@"\[\s*Http(Get|Post|Put|Delete|Patch|Head|Options)\s*(?:\(\s*""([^""]*)""\s*\))?", RegexOptions.IgnoreCase)]
    private static partial Regex HttpAttr();
    [GeneratedRegex(@"\[\s*NonAction\s*\]")]
    private static partial Regex NonAction();
    // MapGet("/x", Handler) 的第二个参数若是方法组名，入口就是那个方法；是 lambda 就落在包含这行的方法上
    [GeneratedRegex(@"\.Map(Get|Post|Put|Delete|Patch)\s*\(\s*""([^""]+)""\s*(?:,\s*(?:[\w.]+\.)?(\w+)\s*[,)])?")]
    private static partial Regex MinimalApi();
    // var api = app.MapGroup("api/orders").HasApiVersion(1.0);  → api 的前缀 = app 的前缀 + /api/orders
    [GeneratedRegex(@"\b(\w+)\s*=\s*(\w+)[^;]*?\.MapGroup\s*\(\s*""([^""]*)""\s*\)")]
    private static partial Regex GroupDecl();
    [GeneratedRegex(@"\.MapGroup\s*\(\s*""([^""]*)""\s*\)")]
    private static partial Regex InlineGroup();
    [GeneratedRegex(@"(\w+)\s*$")]
    private static partial Regex TrailingIdent();
    [GeneratedRegex(@"(^|/)app/(?<path>.*?/?)page\.(tsx|jsx|ts|js)$")]
    private static partial Regex NextPage();
    [GeneratedRegex(@"(^|/)app/(?<path>.*?/?)route\.(ts|js)$")]
    private static partial Regex NextApiRoute();
    [GeneratedRegex(@"(^|/)pages/(?<path>.+)\.(tsx|jsx)$")]
    private static partial Regex PagesRouter();

    /// <param name="timings">给了就按规则记细分阶段 entries.d.&lt;段&gt;（各段相加约等于整个 Discover）。</param>
    public static List<EntryPoint> Discover(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, IReadOnlyList<ArchNode> containers, Func<string, string[]> readLines,
        Core.Diagnostics.PhaseTimings? timings = null)
    {
        var lap = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string phase) { if (timings is null) return; timings.Add("entries.d." + phase, lap.Elapsed.TotalSeconds); lap.Restart(); }
        var byId = symbols.ToDictionary(s => s.Id);
        var children = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var inherits = edges.Where(e => e.Kind == EdgeKind.Inherits).ToLookup(e => e.FromId, e => e.ToId);
        var implements = edges.Where(e => e.Kind == EdgeKind.Implements).ToLookup(e => e.FromId, e => e.ToId);
        var result = new List<EntryPoint>();
        var seen = new HashSet<(long, string)>();

        void Add(EntryPoint e) { if (seen.Add((e.SymbolId, e.ContainerKey))) result.Add(e); }

        IReadOnlyList<ArchNode> ContainersOf(string path) =>
            containers.Where(c => c.Members.Any(m => RuleContext.Under(path, m))).ToList();

        // 继承链上的名字（最多 MaxBaseDepth 层）与直接实现的接口名。
        // Roslyn 把只差泛型参数个数的同名基类分开（CrudControllerBase<,> → CrudControllerBase<,,,> → QueryControllerBase<,> → …），
        // 链比 tree-sitter 看到的深；tree-sitter 又可能给出"自己继承自己"的边，所以记下走过的类型防环
        IEnumerable<string> BaseNames(long typeId)
        {
            var cur = typeId;
            var seen = new HashSet<long> { typeId };
            for (var depth = 0; depth < MaxBaseDepth; depth++)
            {
                var next = inherits[cur].FirstOrDefault(id => !seen.Contains(id));
                if (next == 0 || !byId.TryGetValue(next, out var b)) yield break;
                yield return b.Name;
                if (b.Kind == SymbolKind.External) yield break;
                seen.Add(next);
                cur = next;
            }
        }
        IEnumerable<string> InterfaceNames(long typeId) => implements[typeId].Select(id => byId.TryGetValue(id, out var s) ? s.Name : "").Where(n => n.Length > 0);
        MapSymbol? FirstBase(long typeId) => inherits[typeId].Select(id => byId.GetValueOrDefault(id)).FirstOrDefault(b => b is not null);

        var csFiles = symbols.Where(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)).Select(s => s.Path).Distinct().ToList();

        // gRPC 注册字面量：MapGrpcService<T>() 里的 T（类型名）。先收齐，类型循环里用
        var grpcServices = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in csFiles)
        {
            var lines = readLines(file);
            if (!Array.Exists(lines, l => l.Contains("MapGrpcService", StringComparison.Ordinal))) continue;
            foreach (var l in lines)
            {
                var m = MapGrpc().Match(l);
                if (m.Success && !RuleContext.InCommentOrString(l, m.Index)) grpcServices.Add(m.Groups[1].Value);
            }
        }

        // 宿主注册（常驻服务 / Hub / Quartz 作业 / 共享库里的 Minimal API 归哪个容器）：用到才扫，见 EntryDiscovery.CSharpHosts.cs
        bool IsHostedClass(MapSymbol t) => BaseNames(t.Id).Any(HostedBases.Contains) || InterfaceNames(t.Id).Any(HostedInterfaces.Contains);
        HashSet<string>? hostedNames = null;
        CsHosts? hostScan = null;
        CsHosts Hosts() => hostScan ??= new CsHosts(symbols, edges, containers, byId, readLines, csFiles, name =>
            (hostedNames ??= symbols.Where(s => s.Kind == SymbolKind.Class && s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && IsHostedClass(s)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal)).Contains(name));
        // 注册点所在容器 ∩ 类所在的成员容器；认不出任何注册时照旧按成员项目给。后缀写明条件注册 / 未确认
        List<(ArchNode Container, string Suffix)> Registered(string kind, MapSymbol type, IReadOnlyList<ArchNode> members)
        {
            var h = Hosts();
            var regs = h.Registrations(kind, type.Name);
            if (regs is not null)
            {
                var reached = members.Where(c => regs.ContainsKey(c.Key)).Select(c => (c, regs[c.Key] == HostReg.Definite ? "" : ConditionalSuffix)).ToList();
                if (reached.Count > 0) return reached;
                if (regs.Count == 0) return members.Select(c => (c, UnconfirmedSuffix)).ToList(); // 有注册点，但没有哪个容器的启动代码调到它
            }
            if (!h.HasSites(kind)) return members.Select(c => (c, "")).ToList(); // 仓库里一处注册都认不出（反射扫描之类）：照旧
            // 这个类没有注册点：代码里别处还提到它（可能是认不出的注册写法）就照给并写明，只在注释 / 自己文件里出现的不算入口
            return ReferencedElsewhere(h, type, csFiles, readLines) ? members.Select(c => (c, UnconfirmedSuffix)).ToList() : [];
        }
        var conventional = new Lazy<string?>(() => ConventionalTemplate(csFiles, readLines));
        Lap("setup");

        foreach (var type in symbols.Where(s => s.Kind is SymbolKind.Class && s.Kind != SymbolKind.External))
        {
            if (PipelineBuilder.IsTest(type.Path, type.QualifiedName) || IsGo(type.Path) || IsPython(type.Path) || IsJava(type.Path)) continue; // Go / Python / Java 有自己的规则
            var owners = ContainersOf(type.Path);
            if (owners.Count == 0) continue;
            var bases = BaseNames(type.Id).ToList();
            var ifaces = InterfaceNames(type.Id).ToList();
            var lines = readLines(type.Path);
            var methods = children[type.Id].Where(m => m.Kind == SymbolKind.Method && m.Name != type.Name).OrderBy(m => m.StartLine).ToList();

            if (bases.Any(ControllerBases.Contains))
            {
                var classRoute = AttributesAbove(lines, type.StartLine).Select(l => RouteAttr().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
                foreach (var m in methods)
                {
                    if (m.Visibility is not (null or "public")) continue;
                    var attrs = AttributesAbove(lines, m.StartLine).ToList();
                    if (attrs.Any(a => NonAction().IsMatch(a))) continue;
                    var http = attrs.Select(a => HttpAttr().Match(a)).FirstOrDefault(x => x.Success);
                    var methodRoute = http?.Groups[2].Success == true ? http.Groups[2].Value : attrs.Select(a => RouteAttr().Match(a)).FirstOrDefault(x => x.Success)?.Groups[1].Value;
                    // 类和方法上都没有特性路由：走 MVC 约定路由（MapDefaultControllerRoute → /Account/Login）
                    var route = classRoute is null && methodRoute is null && conventional.Value is { } pattern
                        ? ConventionalRoute(pattern, type.Name, m.Name)
                        : CombineRoute(classRoute, methodRoute, type.Name, m.Name);
                    foreach (var c in owners) Add(new EntryPoint(m.Id, c.Key, "aspnet.controller", "http", route, http?.Groups[1].Value.ToUpperInvariant(), m.Path, m.StartLine));
                }
                continue;
            }
            if (bases.Any(HubBases.Contains))
            {
                // 要有 MapHub<T>，而且是这个容器的启动代码走得到的那处
                var hubOwners = Registered("hub", type, owners);
                foreach (var m in methods.Where(m => m.Visibility is null or "public"))
                    foreach (var (c, suffix) in hubOwners) Add(new EntryPoint(m.Id, c.Key, "aspnet.signalr", "hub", type.Name + "." + m.Name + suffix, null, m.Path, m.StartLine));
                continue;
            }
            // gRPC 服务：注册字面量点名的类型 → public override 方法；没有字面量时，父类是 external 的 *Base 且方法签名里有 ServerCallContext 也算。
            // 服务名取自基类 GreeterBase 去掉 Base（proto 里的 service 名），拿不到就用类名
            var firstBase = FirstBase(type.Id);
            var baseIsGrpcLike = firstBase is { Kind: SymbolKind.External } && firstBase.Name.EndsWith("Base", StringComparison.Ordinal) && firstBase.Name.Length > "Base".Length;
            var registered = grpcServices.Contains(type.Name);
            if (registered || (baseIsGrpcLike && methods.Any(m => DeclarationText(lines, m).Contains("ServerCallContext", StringComparison.Ordinal))))
            {
                var service = baseIsGrpcLike ? firstBase!.Name[..^"Base".Length] : type.Name;
                // gRPC 健康检查（Grpc.Health.V1.Health.HealthBase 的 Check / Watch）是给探针用的，不是业务入口
                if (service == "Health") continue;
                var marker = registered ? "override" : "ServerCallContext";
                foreach (var m in methods.Where(m => m.Visibility is null or "public"))
                {
                    if (!DeclarationText(lines, m).Contains(marker, StringComparison.Ordinal)) continue;
                    foreach (var c in owners) Add(new EntryPoint(m.Id, c.Key, "grpc.service", "grpc", service + "/" + m.Name, null, m.Path, m.StartLine));
                }
                continue;
            }
            if (bases.Any(HostedBases.Contains) || ifaces.Any(HostedInterfaces.Contains))
            {
                if (IsAbstract(lines, type)) continue; // 抽象基类注册不了，子类各自是入口
                var entry = methods.FirstOrDefault(m => m.Name is "ExecuteAsync" or "StartAsync") ?? methods.FirstOrDefault();
                // 要有 AddHostedService<T> 一类注册，而且是这个容器的启动代码走得到的那处（Api 传 false 关掉的不算）
                if (entry is not null)
                    foreach (var (c, suffix) in Registered("hosted", type, owners)) Add(new EntryPoint(entry.Id, c.Key, "hosted.background", "hosted", type.Name + suffix, null, entry.Path, entry.StartLine));
                continue;
            }
            if (ifaces.Any(JobInterfaces.Contains))
            {
                var entry = methods.FirstOrDefault(m => m.Name == "Execute") ?? methods.FirstOrDefault();
                if (entry is not null)
                    foreach (var (c, suffix) in Registered("job", type, owners)) Add(new EntryPoint(entry.Id, c.Key, "quartz.job", "job", type.Name + suffix, null, entry.Path, entry.StartLine));
            }
        }

        Lap("types");
        // 控制台 / 宿主程序的启动入口：顶级语句（Roslyn 补的 function 符号，限定名带 <Main>$）或 static Main。
        // 一个容器只取一个——两者并存时编译器用顶级语句（Main 只给警告）；只看容器自己的项目目录，引用的类库里的 Main 不是它的入口
        foreach (var c in containers.Where(c => c.Kind == ArchNodeKind.Container && HostSubKinds.Contains(c.SubKind)))
        {
            IEnumerable<MapSymbol> Own(Func<MapSymbol, bool> match) => symbols
                .Where(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && match(s) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)
                            && (c.Path is { } home ? RuleContext.Under(s.Path, home) : c.Members.Any(m => RuleContext.Under(s.Path, m))))
                .OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine);
            var main = Own(s => IsTopLevelStatements(s, byId)).FirstOrDefault()
                       ?? Own(s => s.Kind == SymbolKind.Method && s.Name == "Main" && StaticMain().IsMatch(DeclarationText(readLines(s.Path), s))).FirstOrDefault();
            // 控制台程序（dotnet-exe）启动代码里不起常驻宿主的是跑一次就退出的命令行（kind = command），其余是常驻服务
            if (main is not null) Add(new EntryPoint(main.Id, c.Key, "csharp.main", MainKind(c, main, readLines), c.Name, null, main.Path, main.StartLine));
        }

        Lap("main");
        // 分发处理器：handles 边的起点类型 → 入口方法
        foreach (var e in edges.Where(e => e.Kind == EdgeKind.Dispatches && e.Via == "handles"))
        {
            if (!byId.TryGetValue(e.FromId, out var handler) || !byId.TryGetValue(e.ToId, out var message)) continue;
            if (PipelineBuilder.IsTest(handler.Path, handler.QualifiedName) || PipelineBuilder.IsTest(message.Path, message.QualifiedName)) continue;
            var owners = ContainersOf(handler.Path);
            if (owners.Count == 0) continue;
            var entry = children[handler.Id].FirstOrDefault(m => m.Kind == SymbolKind.Method && (m.Name.StartsWith("Handle", StringComparison.Ordinal) || m.Name.StartsWith("Consume", StringComparison.Ordinal) || m.Name.StartsWith("Execute", StringComparison.Ordinal))) ?? handler;
            // MassTransit 的 IConsumer<T> 走同一条 handles 边，ruleId 区分开
            var ruleId = InterfaceNames(handler.Id).Any(ConsumerInterfaces.Contains) ? "masstransit.consumer" : "dispatch.handler";
            foreach (var c in owners) Add(new EntryPoint(entry.Id, c.Key, ruleId, "handler", message.Name, null, entry.Path, entry.StartLine));
        }

        Lap("handlers");
        // Hangfire：注册字面量 → T.Method（符号表里按类型名 + 方法名找）。容器取注册所在文件的（作业在哪个宿主里排期就归哪）
        ILookup<(string Type, string Method), MapSymbol>? methodsByTypeAndName = null;
        foreach (var file in csFiles)
        {
            var lines = readLines(file);
            if (!Array.Exists(lines, l => l.Contains("RecurringJob", StringComparison.Ordinal) || l.Contains("BackgroundJob", StringComparison.Ordinal))) continue;
            var owners = ContainersOf(file);
            if (owners.Count == 0) continue;
            methodsByTypeAndName ??= symbols
                .Where(s => s.Kind == SymbolKind.Method && s.ParentId is { } p && byId.ContainsKey(p) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName))
                .ToLookup(s => (byId[s.ParentId!.Value].Name, s.Name));
            foreach (var l in lines)
            {
                var m = HangfireTyped().Match(l);
                if (!m.Success) m = HangfireStatic().Match(l);
                if (!m.Success || RuleContext.InCommentOrString(l, m.Index)) continue;
                var typeName = m.Groups[1].Value;
                var methodName = m.Groups[2].Value;
                var job = methodsByTypeAndName[(typeName, methodName)].OrderBy(s => s.Path, StringComparer.Ordinal).FirstOrDefault();
                if (job is null) continue;
                foreach (var c in owners) Add(new EntryPoint(job.Id, c.Key, "hangfire.job", "job", typeName + "." + methodName, null, job.Path, job.StartLine));
            }
        }

        Lap("hangfire");
        // Minimal API：文件里的 MapGet("…") 字面量 → 包含该行的最内层方法 / 函数；lambda 处理器落在它调的仓库方法上（见 EntryDiscovery.CSharpEndpoints.cs）
        CsHandlers? handlerScan = null;
        foreach (var file in csFiles)
        {
            var members = ContainersOf(file);
            if (members.Count == 0) continue;
            var lines = readLines(file);
            if (!Array.Exists(lines, l => l.Contains(".Map", StringComparison.Ordinal))) continue;
            var text = Hosts().Text(file);
            // 映射写在共享库的扩展方法里（UseDefaultOpenApi）：只算启动代码真的调到这个方法的容器；谁都没调到（反射加载的模块）照旧按成员项目
            IReadOnlyList<ArchNode> OwnersAt(MapSymbol outer, int index)
            {
                var own = members.Where(c => c.Path is { } p && RuleContext.Under(file, p)).ToList();
                if (own.Count > 0) return own;
                var reach = Hosts().Reaching(outer, index);
                var reached = members.Where(c => reach.ContainsKey(c.Key)).ToList();
                return reached.Count > 0 ? reached : members;
            }
            var inFile = symbols.Where(s => s.Path == file && s.Kind is SymbolKind.Method or SymbolKind.Function).ToList();
            // 文件内的 MapGroup 变量：名字 → (接收者, 段)；前缀沿接收者链拼起来
            var groups = new Dictionary<string, (string Parent, string Segment)>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var g = GroupDecl().Match(lines[i]);
                if (g.Success && !RuleContext.InCommentOrString(lines[i], g.Index)) groups[g.Groups[1].Value] = (g.Groups[2].Value, g.Groups[3].Value);
            }
            string Prefix(string ident)
            {
                var prefix = "";
                for (var depth = 0; depth < 8 && groups.TryGetValue(ident, out var g); depth++) { prefix = "/" + g.Segment.Trim('/') + prefix; ident = g.Parent; }
                return prefix;
            }
            string RouteAt(int i, int matchIndex, string literal)
            {
                var before = lines[i][..matchIndex];
                var inline = InlineGroup().Matches(before); // app.MapGroup("/a").MapGroup("b").MapGet(...)
                var receiver = TrailingIdent().Match(inline.Count > 0 ? before[..inline[0].Index] : before);
                var prefix = (receiver.Success ? Prefix(receiver.Groups[1].Value) : "") + string.Concat(inline.Select(x => "/" + x.Groups[1].Value.Trim('/')));
                return "/" + (prefix.Trim('/') + "/" + literal.Trim('/')).Trim('/');
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var m = MinimalApi().Match(lines[i]);
                if (m.Success && !RuleContext.InCommentOrString(lines[i], m.Index))
                {
                    var outer = inFile.Where(s => s.StartLine <= i + 1 && i + 1 <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault();
                    if (outer is null) continue;
                    var owner = outer;
                    if (m.Groups[3].Success && !m.Groups[3].Value.Equals("async", StringComparison.Ordinal))
                    {
                        var name = m.Groups[3].Value;
                        var handler = inFile.FirstOrDefault(s => s.Name == name && s.ParentId == outer.ParentId) ?? inFile.FirstOrDefault(s => s.Name == name);
                        if (handler is not null) owner = handler;
                    }
                    else
                    {
                        // 第二个实参是 lambda：落在它调的仓库方法上（同一个外层方法里的 23 条路由不再挤在一个符号上）
                        var open = text.LineStart(i + 1) + m.Index + m.Value.IndexOf('(');
                        var (args, _) = SplitMasked(text.Masked, open);
                        if (args.Count >= 2)
                        {
                            handlerScan ??= new CsHandlers(symbols, edges, byId, ContainersOf);
                            if (handlerScan.Resolve(args[1], file) is { } target) owner = target;
                        }
                    }
                    var route = RouteAt(i, m.Index, m.Groups[2].Value);
                    foreach (var c in OwnersAt(outer, text.LineStart(i + 1) + m.Index))
                        result.Add(new EntryPoint(owner.Id, c.Key, "aspnet.minimal", "http", route, m.Groups[1].Value.ToUpperInvariant(), file, i + 1));
                    continue;
                }
                // 框架终结点：MapHealthChecks / MapForwarder（处理代码不在仓库里；入口留在映射所在方法上，流程按这一行截）
                var f = FrameworkEndpoint().Match(lines[i]);
                if (f.Success && !RuleContext.InCommentOrString(lines[i], f.Index))
                {
                    var outer = inFile.Where(s => s.StartLine <= i + 1 && i + 1 <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault();
                    if (outer is null) continue;
                    var health = f.Groups[1].Value == "HealthChecks";
                    var route = RouteAt(i, f.Index, f.Groups[2].Value);
                    foreach (var c in OwnersAt(outer, text.LineStart(i + 1) + f.Index))
                        result.Add(new EntryPoint(outer.Id, c.Key, health ? "aspnet.healthchecks" : "aspnet.forwarder", "http", route, health ? "GET" : null, file, i + 1));
                }
            }
        }

        Lap("minimal");
        // Blazor @page（.razor 进了索引时）
        var seenPages = new HashSet<(long, string, string?)>(); // 一个组件可以有多条 @page：按 (符号, 容器, 路由) 去重
        DiscoverBlazorPages(symbols, children, ContainersOf, readLines, e => { if (seenPages.Add((e.SymbolId, e.ContainerKey, e.Route))) result.Add(e); });

        Lap("blazor");
        // 前端：按文件路径约定。入口落在默认导出的组件 / 处理函数上（流程从它往下走渲染边与调用），找不到才退回模块
        MapSymbol DefaultExport(MapSymbol module) =>
            children[module.Id].Where(s => s.Visibility == "export default" && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable)
                .OrderBy(s => s.StartLine).FirstOrDefault() ?? module;
        Dictionary<string, Dictionary<string, List<MapSymbol>>>? exportedByContainer = null; // PageComponent 的缓存
        foreach (var c in containers.Where(c => c.SubKind == "frontend"))
        {
            foreach (var module in symbols.Where(s => s.Kind == SymbolKind.Module && RuleContext.Under(s.Path, c.Path ?? "") && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)))
            {
                var page = NextPage().Match(module.Path);
                if (page.Success)
                {
                    var target = DefaultExport(module);
                    Add(new EntryPoint(target.Id, c.Key, "frontend.route", "page", NextRoute(page.Groups["path"].Value), null, module.Path, target.StartLine));
                    continue;
                }
                var api = NextApiRoute().Match(module.Path);
                if (api.Success)
                {
                    // App Router 的 route.ts 按 HTTP 方法导出具名函数（export async function GET），一个方法一个入口
                    var route = NextRoute(api.Groups["path"].Value);
                    var handlers = children[module.Id].Where(s => s.Kind == SymbolKind.Function && s.Visibility == "export" && HttpVerbs.Contains(s.Name)).OrderBy(s => s.StartLine).ToList();
                    foreach (var h in handlers) Add(new EntryPoint(h.Id, c.Key, "frontend.route", "api-route", route, h.Name, module.Path, h.StartLine));
                    if (handlers.Count == 0)
                    {
                        var target = DefaultExport(module);
                        Add(new EntryPoint(target.Id, c.Key, "frontend.route", "api-route", route, null, module.Path, target.StartLine));
                    }
                    continue;
                }
                var pages = PagesRouter().Match(module.Path);
                if (pages.Success && !pages.Groups["path"].Value.StartsWith('_'))
                {
                    var target = DefaultExport(module);
                    Add(new EntryPoint(target.Id, c.Key, "frontend.route", "page", NextRoute(pages.Groups["path"].Value.Replace("/index", "")), null, module.Path, target.StartLine));
                    continue;
                }
                // 单页应用的路由表（TanStack Router / React Router，见 EntryDiscovery.Spa.cs）
                if (!SpaExtensions.Any(x => module.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || module.Path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) continue;
                var spaText = string.Join('\n', readLines(module.Path));
                if (!spaText.Contains("Route", StringComparison.Ordinal) && !spaText.Contains("useRoutes", StringComparison.Ordinal)) continue;
                foreach (var (route, line, component) in SpaRoutes(StripJsComments(spaText)))
                    if (PageComponent(c, module, component) is { } target)
                        Add(new EntryPoint(target.Id, c.Key, "frontend.route", "page", route, null, module.Path, line));
            }
        }

        // 页面组件按名字找：同文件 → 同容器里唯一一个导出的同名组件
        MapSymbol? PageComponent(ArchNode c, MapSymbol module, string name)
        {
            if (children[module.Id].FirstOrDefault(s => s.Name == name && s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable) is { } local) return local;
            exportedByContainer ??= [];
            if (!exportedByContainer.TryGetValue(c.Key, out var byName))
                exportedByContainer[c.Key] = byName = symbols
                    .Where(s => s.Kind is SymbolKind.Function or SymbolKind.Class or SymbolKind.Variable && s.Visibility is { } v && v.StartsWith("export", StringComparison.Ordinal)
                                && RuleContext.Under(s.Path, c.Path ?? "") && !PipelineBuilder.IsTest(s.Path, s.QualifiedName))
                    .GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            return byName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;
        }

        // Go / Python：同一个处理函数可以挂在多条路由上，按 (符号, 容器, 规则, 路由, 方法) 去重
        var seenRoutes = new HashSet<(long, string, string, string?, string?)>();
        // 容器嵌套（仓库根的成员 "" 包住 backend/）时只归成员目录最具体的那个；成员同样具体（C# 共享库那样同一目录进多个容器）照旧都算
        IReadOnlyList<ArchNode> SpecificContainersOf(string path)
        {
            var owners = ContainersOf(path);
            if (owners.Count <= 1) return owners;
            int Specificity(ArchNode c) => c.Members.Where(m => RuleContext.Under(path, m)).Max(m => m.Length);
            var best = owners.Max(Specificity);
            return owners.Where(c => Specificity(c) == best).ToList();
        }
        var scan = new Scan(symbols, byId, children, readLines, SpecificContainersOf,
            e => { if (seenRoutes.Add((e.SymbolId, e.ContainerKey, e.RuleId, e.Route, e.HttpMethod))) result.Add(e); });
        Lap("frontend");
        DiscoverCSharpCli(scan); Lap("csharp-cli");
        DiscoverGo(scan); Lap("go");
        DiscoverPython(scan); Lap("python");
        DiscoverJava(scan); Lap("java");
        DiscoverNode(scan); Lap("node");

        return result.OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ToList();
    }

    /// <summary>Go / Python 规则共用的索引视图。Add 已带去重。</summary>
    private sealed record Scan(
        IReadOnlyList<MapSymbol> Symbols, IReadOnlyDictionary<long, MapSymbol> ById, ILookup<long, MapSymbol> Children,
        Func<string, string[]> ReadLines, Func<string, IReadOnlyList<ArchNode>> ContainersOf, Action<EntryPoint> Add);

    /// <summary>方法声明的文本：从起始行起到出现 "Name(" 的那一行为止（最多 4 行，不越过 EndLine），用来看 override / ServerCallContext 这类修饰与参数。</summary>
    internal static string DeclarationText(string[] lines, MapSymbol m)
    {
        var sb = new System.Text.StringBuilder();
        var last = Math.Min(Math.Max(m.EndLine, m.StartLine), m.StartLine + 3);
        for (var i = m.StartLine - 1; i >= 0 && i < lines.Length && i < last; i++)
        {
            sb.Append(lines[i]).Append(' ');
            if (lines[i].Contains(m.Name + "(", StringComparison.Ordinal) || lines[i].Contains(m.Name + " (", StringComparison.Ordinal)) break;
        }
        return sb.ToString();
    }

    /// <summary>声明行上方连续的特性 / 注释 / 空行（最多 12 行）；Roslyn 的符号起始行是特性列表的第一行，所以从声明行起向下的连续特性行也算。</summary>
    public static IEnumerable<string> AttributesAbove(string[] lines, int declLine)
    {
        for (var i = declLine - 1; i >= 0 && i < lines.Length && lines[i].TrimStart().StartsWith('['); i++)
            yield return lines[i].Trim();
        // 文件比索引里记的短（索引之后被改过、或读不到）时不越界
        for (var i = Math.Min(declLine - 2, lines.Length - 1); i >= 0 && i >= declLine - 13; i--)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal)) continue;
            if (t.StartsWith('[')) { yield return t; continue; }
            yield break;
        }
    }

    /// <summary>类路由 + 方法路由，`[controller]` / `[action]` 展开，`~/` 覆盖类路由，统一以 / 开头。</summary>
    public static string CombineRoute(string? classRoute, string? methodRoute, string className, string methodName)
    {
        var controller = className.EndsWith("Controller", StringComparison.Ordinal) ? className[..^"Controller".Length] : className;
        string Expand(string r) => r.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase).Replace("[action]", methodName, StringComparison.OrdinalIgnoreCase);
        string route;
        if (methodRoute is not null && (methodRoute.StartsWith("~/", StringComparison.Ordinal) || methodRoute.StartsWith('/'))) route = Expand(methodRoute.TrimStart('~'));
        else
        {
            var parts = new[] { classRoute, methodRoute }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Expand(p!).Trim('/')).Where(p => p.Length > 0);
            route = string.Join('/', parts);
        }
        return "/" + route.Trim('/');
    }

    /// <summary>Next.js 目录 → 路径：去掉 (group) 段，[param] → {param}，[...slug] → {*slug}。</summary>
    public static string NextRoute(string dirPath)
    {
        var segs = dirPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !(s.StartsWith('(') && s.EndsWith(')')))
            .Select(s => s.StartsWith("[...") ? "{*" + s[4..^1] + "}" : s.StartsWith('[') ? "{" + s[1..^1] + "}" : s);
        return "/" + string.Join('/', segs);
    }
}
