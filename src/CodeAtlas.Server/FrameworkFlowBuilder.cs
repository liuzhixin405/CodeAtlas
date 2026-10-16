using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 方法体里的一次调用（框架流程用）：调用大纲第一层（<see cref="OutlineBuilder"/> 的规则：via=impl 挂在接口调用下面、dispatch 挂在分发调用下面），
/// 外加没解析到符号的调用（Kind = unresolved，ToName 原样放在 Label / QualifiedName 里：Roslyn 对仓库里自定义委托的 next() 记成悬空边）。
/// </summary>
/// <param name="Kind">call / new / dispatch / impl / external / ambiguous / unresolved。</param>
public sealed record FrameworkCall(
    long? TargetId, string Kind, string Label, string QualifiedName, int Line, int? Pos, string? Ctx, bool Expandable,
    IReadOnlyList<FrameworkCall> Children);

/// <summary>一条 DI 注册（Roslyn 产出的 di_registrations，接口与实现都解析到了的）：实现、所在文件与行、是否在 if / 三元里。</summary>
public sealed record FrameworkRegistration(long InterfaceId, long ImplId, string Path, int Line, bool Conditional);

/// <summary>框架流程按需取的明细：方法的调用、签名、源码文本、完整的符号。服务与 CLI 用库实现（<see cref="FrameworkFlowQuery"/>），测试给字典。</summary>
public sealed class FrameworkSources
{
    /// <summary>方法里的调用（按源码顺序；不认识的方法给空表）。</summary>
    public required Func<long, IReadOnlyList<FrameworkCall>> Calls { get; init; }
    /// <summary>符号的签名（参数名与类型，认 next 委托用）；没有为 null。</summary>
    public required Func<long, string?> Signature { get; init; }
    /// <summary>相对仓库根的路径 → 文件文本；读不到为 null。</summary>
    public required Func<string, string?> ReadFile { get; init; }
    /// <summary>完整的符号（签名、列号）；不给时用 MapSymbol 拼。</summary>
    public Func<long, SymbolDto?>? Symbol { get; init; }
}

public sealed record FrameworkInput(
    IReadOnlyList<MapSymbol> Symbols, IReadOnlyList<MapEdge> Edges, ArchNode Container, IReadOnlyList<EntryPoint> Entries,
    IReadOnlyList<FrameworkRegistration> Di, FrameworkSources Sources);

/// <summary>
/// 框架流程（GET /api/arch/framework）：一个容器里每个请求都要经过的那套框架。纯函数，取数见 <see cref="FrameworkFlowQuery"/>。
/// 阶段（FlowActorDto.Kind = "stage"，Key 即阶段键）与识别规则：
/// <list type="number">
/// <item>middleware：Program.cs（app = builder.Build() 之后）/ Startup.Configure 里 app.UseXxx(...) 的书写顺序（链式调用也认）；
/// UseMiddleware&lt;T&gt; / typeof(T) 落到仓库里的类 → 自研中间件，挂它的 InvokeAsync / Invoke，子节点是它的调用（next 之前 / 之后分开）；
/// UseXxx 是仓库里的扩展方法 → 进去接着找（最多 3 层），里面的中间件按顺序摊平（Note 写"经 UseXxx()"）；其余 UseXxx 是框架内置，画成 external-group 行。
/// MVC 全局过滤器（Filters.Add&lt;T&gt; / Add(typeof(T)) / Add(new T) / AddService&lt;T&gt;）并进这一列，排在中间件之后、控制器之前（Note 写"MVC 全局过滤器"）。
/// 实现 IMiddleware 或 *Middleware 类带 InvokeAsync(HttpContext…) 但找不到注册位置的，追加在最后，阶段 OrderKnown = false。</item>
/// <item>controller：占位 Kind = "controllers"，Count = 容器里 kind = http 的入口数。容器不是 Web（不是 dotnet-web，或既没有 HTTP 入口也没有中间件）时
/// 改成"入口"占位（Kind 仍是 controllers，Label 写"入口（常驻服务 / 任务 / 消息消费）"，Count = 非分派 Handler 的入口数），没有中间件阶段。</item>
/// <item>dispatcher：容器里 via = conduit 的调用指向的方法（目标不是 Handler 自己），按调用次数排；第一个是主分派器，行为链挂在它下面。
/// 目标是仓库里的接口方法 → 换成实现类的同名方法（自研，展开看它的调用，同文件里的辅助方法再进一层）；外部的（MediatR ISender.Send）画成一行外部节点。
/// 名字带 Publish 且多数调用方是 Handler 的算事件发布，放进收尾。</item>
/// <item>behavior：实现了名字以 Behavior 结尾的接口的类（与旧管道视图同一条）；顺序 = DI 注册（di_registrations）与源码里
/// AddOpenBehavior(typeof(X&lt;,&gt;)) / AddBehavior&lt;I, X&gt;() / AddBehavior(typeof(I), typeof(X)) 的位置（文件路径、行）。
/// 每个行为挂 Handle / HandleAsync，子节点是它调用大纲第一层里 next() 之前的那些（前置）；之后的（后置）放进收尾列，Note 写"XxxBehavior 后置"，按行为倒序（洋葱往回走）。
/// next 是签名里类型以 Delegate 结尾 / Func / Action 的参数：调用名是参数名，或目标是那个委托类型的 Invoke。
/// 主 next 取第一个不在 if 分支里的那次（都在分支里取最后一次）；另有分支里的 next（短路）或主 next 本身在分支里 → Note 标"有条件"。</item>
/// <item>handler：占位 Kind = "handlers"，Count = Handlers 清单条数（Handler × 消息）。清单来自 handles 边；入口 = 沿 calls / dispatch:T 反查能走到它的入口（8 层内）。</item>
/// <item>tail：行为后置、分派器在调用管道之后的调用、工作单元提交（DbContext 子类 / 实现 *UnitOfWork 接口的类型上的 SaveChanges* / SaveEntities* / Commit*，
/// Handler 往下 3 层内调到的）、事件发布（见分派器）。工作单元与事件发布挂在 Handler 占位下面。</item>
/// </list>
/// 找不到的阶段不画，Notes 里说为什么。
/// </summary>
public static partial class FrameworkFlowBuilder
{
    public const string StageMiddleware = "middleware";
    public const string StageController = "controller";
    public const string StageDispatcher = "dispatcher";
    public const string StageBehavior = "behavior";
    public const string StageHandler = "handler";
    public const string StageTail = "tail";
    // 别的语言的阶段（页面不按键分支，列名用 FrameworkStageDto.Label）：
    /// <summary>Spring HandlerInterceptor、NestJS Interceptor……：控制器前后各一段。</summary>
    public const string StageInterceptor = "interceptor";
    /// <summary>Spring AOP 切面（@Around / @Before / @After）、NestJS Guard / Pipe 这类挂在处理函数外面的。</summary>
    public const string StageAspect = "aspect";
    /// <summary>FastAPI Depends 依赖链。</summary>
    public const string StageDependency = "dependency";
    /// <summary>前端：Provider 嵌套（Router / QueryClient / Store / Theme……）。</summary>
    public const string StageProvider = "provider";
    /// <summary>前端：路由（路由守卫 / loader / beforeLoad）。</summary>
    public const string StageRoute = "route";
    /// <summary>前端：请求封装（axios / fetch 实例与拦截器）。</summary>
    public const string StageRequest = "request";

    /// <summary>每个节点下最多列多少个调用，多出来的折成"还有 N 个"。</summary>
    public const int MaxChildren = 12;
    /// <summary>每个 Handler 最多列多少个入口。</summary>
    public const int MaxEntries = 20;
    private const int EntrySearchDepth = 8;
    private const int EntrySearchCap = 4000;
    private const int MaxExtensionDepth = 3;
    /// <summary>入口往下多少层内调到分派器才算"经分派器"（反查的层数；经接口方法那一步也算一层）。</summary>
    private const int DispatchReachDepth = 8;

    private static readonly string[] HandlerMethodNames = ["Handle", "HandleAsync", "Consume", "ConsumeAsync", "Execute", "ExecuteAsync", "Process", "ProcessAsync", "Invoke", "InvokeAsync", "Run", "RunAsync"];
    private static readonly string[] BehaviorMethodNames = ["Handle", "HandleAsync", "InvokeAsync", "Invoke", "ExecuteAsync", "Execute", "Process", "ProcessAsync"];
    private static readonly string[] MiddlewareMethodNames = ["InvokeAsync", "Invoke"];
    private static readonly string[] FilterMethodNames =
        ["OnActionExecutionAsync", "OnActionExecuting", "OnResourceExecutionAsync", "OnResourceExecuting", "OnAuthorizationAsync", "OnAuthorization",
         "OnResultExecutionAsync", "OnResultExecuting", "OnExceptionAsync", "OnException"];
    private static readonly HashSet<string> FilterAfterNames = new(StringComparer.Ordinal) { "OnActionExecuted", "OnResourceExecuted", "OnResultExecuted", "OnException", "OnExceptionAsync" };
    private static readonly HashSet<string> UnitOfWorkMethods = new(StringComparer.Ordinal)
        { "SaveEntitiesAsync", "SaveEntities", "SaveChangesAsync", "SaveChanges", "CommitAsync", "Commit", "CommitTransactionAsync", "CommitTransaction" };
    private static readonly HashSet<string> BehaviorRegistrationMethods = new(StringComparer.Ordinal) { "AddOpenBehavior", "AddBehavior", "AddOpenStreamBehavior", "AddStreamBehavior" };
    private static readonly HashSet<string> MvcSetupMethods = new(StringComparer.Ordinal) { "AddControllers", "AddMvc", "AddControllersWithViews", "AddMvcCore", "AddRazorPages" };

    [GeneratedRegex(@"\b(\w+)\s*=\s*(?:await\s+)?[\w.]+\s*\.\s*Build(?:Async)?\s*\(\s*\)")]
    private static partial Regex BuildCall();

    // 管道扩展方法的第一个参数：this IApplicationBuilder app（Roslyn 的签名里没有 this，按类型认）
    [GeneratedRegex(@"\(\s*(?:this\s+)?(?:[\w.]+\.)?(?:IApplicationBuilder|WebApplication|IEndpointRouteBuilder|IHost)\s+(\w+)\s*[,)=]")]
    private static partial Regex PipelineReceiver();

    [GeneratedRegex(@"AddOpen(?:Stream)?Behavior\s*\(\s*typeof\s*\(\s*(?<t>[\w.]+)")]
    private static partial Regex AddOpenBehavior();

    [GeneratedRegex(@"Add(?:Stream)?Behavior\s*\(\s*typeof\s*\([^()]*\)\s*,\s*typeof\s*\(\s*(?<t>[\w.]+)")]
    private static partial Regex AddBehaviorTypeOf();

    [GeneratedRegex(@"Add(?:Stream)?Behavior\s*<")]
    private static partial Regex AddBehaviorGeneric();

    // tree-sitter 兜底（没有 Roslyn 的 di_registrations）：services.AddTransient(typeof(IPipelineBehavior<,>), typeof(X<,>))
    [GeneratedRegex(@"(?:Try)?Add(?:Transient|Scoped|Singleton)\s*\(\s*typeof\s*\(\s*[\w.]*Behavior\s*(?:<[^()]*>)?\s*\)\s*,\s*typeof\s*\(\s*(?<t>[\w.]+)")]
    private static partial Regex AddServiceBehavior();

    [GeneratedRegex(@"Filters\s*\.\s*Add(?:Service)?\s*(?:<\s*(?<g>[\w.]+)\s*(?:<[^<>]*>)?\s*>\s*\(|\(\s*(?:typeof\s*\(\s*(?<t>[\w.]+)|new\s+(?<n>[\w.]+)))")]
    private static partial Regex FilterAdd();

    [GeneratedRegex(@"typeof\s*\(\s*(?<t>[\w.]+)")]
    private static partial Regex TypeOfArg();

    // 起 Web 宿主的写法（不是 dotnet-web 的容器里也认：控制台程序 serve 子命令里起 Kestrel）
    [GeneratedRegex(@"\bWebApplication\s*\.\s*Create(?:Slim|Empty)?Builder\b|\bWebHost\s*\.\s*CreateDefaultBuilder\b|\bConfigureWebHost(?:Defaults)?\s*\(")]
    private static partial Regex WebHostText();

    // 终结点上的授权要求：xxx.MapYyy().RequireAuthorization(...)
    [GeneratedRegex(@"\.\s*RequireAuthorization\s*\(")]
    private static partial Regex RequireAuthorizationCall();

    [GeneratedRegex("(Dispatcher|Mediator|Sender|Publisher|MessageBus|EventBus|CommandBus|QueryBus)$")]
    private static partial Regex DispatcherTypeName();

    private static readonly string[] DispatcherLibraries = ["MediatR", "MassTransit", "Wolverine", "Paramore", "Brighter", "SlimMessageBus", "Mediator", "DotNetCore.CAP"];

    [GeneratedRegex("Event|Notification")]
    private static partial Regex EventName();

    private sealed record Split(List<FrameworkCall> Pre, List<FrameworkCall> Post, bool Conditional, bool HasNext);

    /// <summary>源码里找到的一处管道配置调用（app.UseXxx / Filters.Add / AddOpenBehavior）。</summary>
    private sealed record ChainCall(string Name, string? Generic, string Args, int Pos);

    public static FrameworkFlowDto Build(FrameworkInput input)
    {
        var container = input.Container;
        var symbols = input.Symbols;
        var edges = input.Edges;
        var src = input.Sources;
        var byId = new Dictionary<long, MapSymbol>(symbols.Count);
        foreach (var s in symbols) byId[s.Id] = s;
        var childrenOf = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var notes = new List<string>();

        bool InContainer(MapSymbol s) => s.Kind != SymbolKind.External && container.Members.Any(m => Under(s.Path, m)) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);
        MapSymbol? TypeOf(long id) => byId.TryGetValue(id, out var s) ? MapBuilder.TypeAncestor(s, byId) : null;
        string ShortName(MapSymbol s) => MapBuilder.StripGenericArgs(s.Name);
        string MemberLabel(MapSymbol m) => m.ParentId is { } p && byId.TryGetValue(p, out var o) && OutlineBuilder.IsType(o.Kind) ? ShortName(o) + "." + ShortName(m) : ShortName(m);
        SymbolDto Dto(MapSymbol s) => src.Symbol?.Invoke(s.Id)
            ?? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility);
        IEnumerable<MapSymbol> Methods(MapSymbol type) => childrenOf[type.Id].Where(c => c.Kind is SymbolKind.Method or SymbolKind.Function);
        MapSymbol? MethodNamed(MapSymbol type, IEnumerable<string> names)
        {
            var methods = Methods(type).ToList();
            foreach (var n in names) if (methods.FirstOrDefault(m => ShortName(m) == n) is { } hit) return hit;
            return null;
        }

        // 别的语言各有一套框架（各自一个文件，输出同一个 FrameworkFlowDto；共用下面 internal 的小工具与 Assemble）
        if (!container.SubKind.StartsWith("dotnet", StringComparison.Ordinal))
            return container.SubKind switch
            {
                "java-service" => SpringFrameworkFlow.Build(input),
                "go-service" => GoFrameworkFlow.Build(input),
                "python-service" => PythonFrameworkFlow.Build(input),
                "node-service" => NodeFrameworkFlow.Build(input),
                "frontend" => FrontendFrameworkFlow.Build(input),
                _ => Empty(container, ["框架流程还不认这种容器（" + container.SubKind + "）"]),
            };

        // ---- 边的索引（一趟）----
        var interfacesOf = new Dictionary<long, List<MapSymbol>>();
        var basesOf = new Dictionary<long, List<MapSymbol>>();
        var derivedOf = new Dictionary<long, List<long>>();
        var implementersOf = new Dictionary<long, List<long>>();
        var handlesEdges = new List<MapEdge>();
        var conduitEdges = new List<MapEdge>();
        var reverse = new Dictionary<long, List<long>>();
        var callersOfName = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        // Handler 清单反查入口用：分派边带着消息名（开放泛型 EnqueueJobHandler<TCommand> 一个类接 19 种命令，各条只算分派自己那种消息的入口）
        var dispatchIn = new Dictionary<long, List<(long From, string Message)>>();
        var callsIn = new Dictionary<long, List<long>>();
        foreach (var e in edges)
        {
            switch (e.Kind)
            {
                case EdgeKind.Implements when byId.TryGetValue(e.ToId, out var iface) && byId.ContainsKey(e.FromId):
                    Add(interfacesOf, e.FromId, iface);
                    Add(implementersOf, e.ToId, e.FromId);
                    break;
                case EdgeKind.Inherits when byId.TryGetValue(e.ToId, out var b):
                    Add(basesOf, e.FromId, b);
                    Add(derivedOf, e.ToId, e.FromId);
                    break;
                case EdgeKind.Dispatches when e.Via == "handles":
                    handlesEdges.Add(e);
                    break;
                case EdgeKind.Dispatches when e.Via is not null && e.Via.StartsWith("dispatch:", StringComparison.Ordinal):
                    Add(reverse, e.ToId, e.FromId);
                    Add(dispatchIn, e.ToId, (e.FromId, MapBuilder.StripGenericArgs(MapBuilder.LastSegment(MapBuilder.StripGenericArgs(e.Via["dispatch:".Length..])))));
                    break;
                case EdgeKind.Calls:
                    if (e.Via == "conduit") conduitEdges.Add(e);
                    else if (e.Confidence >= 0.4) { Add(reverse, e.ToId, e.FromId); Add(callsIn, e.ToId, e.FromId); }
                    if (byId.TryGetValue(e.ToId, out var t) && (BehaviorRegistrationMethods.Contains(t.Name) || MvcSetupMethods.Contains(t.Name) || t.Name == "UseMiddleware"
                        || t.QualifiedName.Contains("FilterCollection", StringComparison.Ordinal)))
                        Add(callersOfName, t.Name, e.FromId);
                    break;
            }
        }
        IEnumerable<string> InterfaceNames(long typeId) => (interfacesOf.GetValueOrDefault(typeId) ?? []).Select(i => MapBuilder.StripGenericArgs(MapBuilder.LastSegment(MapBuilder.StripGenericArgs(i.QualifiedName))));

        // 文本缓存与行号
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Text(string path)
        {
            if (texts.TryGetValue(path, out var t)) return t;
            string? raw;
            try { raw = src.ReadFile(path); } catch { raw = null; }
            return texts[path] = raw is null ? null : StripCommentsAndStrings(raw);
        }
        int LineAt(string text, int pos) { var n = 1; for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') n++; return n; }
        string FileName(string path) => MapSymbol.FileName(path);

        // ---- Handler 与消息 ----
        var handlerTypes = new HashSet<long>();
        var handlerPairs = new List<(MapSymbol Handler, MapSymbol Message)>();
        foreach (var e in handlesEdges)
        {
            if (TypeOf(e.FromId) is not { } h || !byId.TryGetValue(e.ToId, out var msg)) continue;
            if (!InContainer(h) || PipelineBuilder.IsTest(msg.Path, msg.QualifiedName)) continue;
            handlerTypes.Add(h.Id);
            if (!handlerPairs.Any(p => p.Handler.Id == h.Id && p.Message.Id == msg.Id)) handlerPairs.Add((h, msg));
        }
        // 泛型基类 Handler（IdentifiedCommandHandler<T, R>）与它的子类（CancelOrderIdentifiedCommandHandler : IdentifiedCommandHandler<CancelOrderCommand, bool>）
        // 处理的是同一个消息：清单里只留子类那几条，基类自己那条（"IdentifiedCommand"）是重复
        var pairKeys = handlerPairs.Select(p => (p.Handler.Id, p.Message.Id)).ToHashSet();
        var shadowed = handlerPairs.Where(p => (derivedOf.GetValueOrDefault(p.Handler.Id) ?? []).Any(d => pairKeys.Contains((d, p.Message.Id)))).ToList();
        handlerPairs.RemoveAll(shadowed.Contains);

        // ---- 行为 ----
        var behaviorTypes = new List<MapSymbol>();
        foreach (var (typeId, ifaces) in interfacesOf)
        {
            if (!byId.TryGetValue(typeId, out var t) || t.Kind != SymbolKind.Class || !InContainer(t)) continue;
            if (ifaces.Any(i => MapBuilder.StripGenericArgs(MapBuilder.LastSegment(MapBuilder.StripGenericArgs(i.QualifiedName))).EndsWith("Behavior", StringComparison.Ordinal)))
                behaviorTypes.Add(t);
        }
        var behaviorIds = behaviorTypes.Select(b => b.Id).ToHashSet();
        var behaviorByName = behaviorTypes.GroupBy(ShortName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // 注册位置：DI 与源码里 MediatR 配置的 AddOpenBehavior / AddBehavior
        var registrations = new Dictionary<long, (string Path, int Line, bool Conditional, string How)>();
        void Register(long implId, string path, int line, bool conditional, string how)
        {
            if (registrations.TryGetValue(implId, out var old) && Compare((old.Path, old.Line), (path, line)) <= 0) return;
            registrations[implId] = (path, line, conditional, how);
        }
        foreach (var d in input.Di)
            if (behaviorIds.Contains(d.ImplId) && container.Members.Any(m => Under(d.Path, m))) Register(d.ImplId, d.Path, d.Line, d.Conditional, "DI 注册");
        var registrationFiles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in BehaviorRegistrationMethods)
            foreach (var caller in callersOfName.GetValueOrDefault(name) ?? [])
                if (byId.TryGetValue(caller, out var c) && InContainer(c)) registrationFiles.Add(c.Path);
        foreach (var d in input.Di)
            if (byId.TryGetValue(d.InterfaceId, out var i) && i.Name.EndsWith("Behavior", StringComparison.Ordinal) && container.Members.Any(m => Under(d.Path, m))) registrationFiles.Add(d.Path);
        foreach (var path in registrationFiles)
        {
            if (Text(path) is not { } text) continue;
            foreach (var (name, pos) in BehaviorRegistrationsInText(text))
                if (behaviorByName.TryGetValue(name, out var b)) Register(b.Id, path, LineAt(text, pos), false, "AddOpenBehavior / AddBehavior");
        }
        var orderedBehaviors = behaviorTypes
            .OrderBy(b => registrations.ContainsKey(b.Id) ? 0 : 1)
            .ThenBy(b => registrations.TryGetValue(b.Id, out var r) ? r.Path : "", StringComparer.Ordinal)
            .ThenBy(b => registrations.TryGetValue(b.Id, out var r) ? r.Line : 0)
            .ThenBy(b => b.QualifiedName, StringComparer.Ordinal).ToList();
        var behaviorsOrdered = behaviorTypes.All(b => registrations.ContainsKey(b.Id));

        // ---- 分派器（conduit 调用的目标）----
        var conduitTargets = new Dictionary<long, (int Count, int FromHandlers)>();
        foreach (var e in conduitEdges)
        {
            if (!byId.TryGetValue(e.ToId, out var t) || !byId.TryGetValue(e.FromId, out var from) || !InContainer(from)) continue;
            if (t.Kind == SymbolKind.External ? !t.QualifiedName.Contains('(') : t.Kind is not (SymbolKind.Method or SymbolKind.Function)) continue; // 构造消息那条
            if (t.Kind != SymbolKind.External && (PipelineBuilder.IsTest(t.Path, t.QualifiedName) || TypeOf(t.Id) is { } tt && handlerTypes.Contains(tt.Id))) continue;
            if (!LooksLikeDispatcher(t)) continue;
            var fromHandler = TypeOf(from.Id) is { } ft && handlerTypes.Contains(ft.Id);
            var (n, h) = conduitTargets.GetValueOrDefault(t.Id);
            conduitTargets[t.Id] = (n + 1, h + (fromHandler ? 1 : 0));
        }
        // 分派器的样子：外部的只认分派库（MediatR / MassTransit / Wolverine…）或类型名像分派器的；仓库里的要么是接口方法，要么类型名像分派器——
        // 把消息对象传给普通服务方法（XxxService.ExecuteAsync(command)）、Handler 基类、行为自己的调用也会被记成 conduit，那些不算
        bool LooksLikeDispatcher(MapSymbol t)
        {
            var (type, _) = OwnerAndMember(t.QualifiedName);
            if (DispatcherTypeName().IsMatch(type)) return true;
            if (type.EndsWith("Behavior", StringComparison.Ordinal) || type.EndsWith("Handler", StringComparison.Ordinal)) return false;
            if (t.Kind == SymbolKind.External) return DispatcherLibraries.Any(l => t.QualifiedName.StartsWith("external:" + l + ".", StringComparison.Ordinal));
            if (TypeOf(t.Id) is not { } owner) return false;
            if (ShortName(owner).EndsWith("Handler", StringComparison.Ordinal) || ShortName(owner).EndsWith("HandlerBase", StringComparison.Ordinal) || behaviorIds.Contains(owner.Id)) return false;
            return owner.Kind == SymbolKind.Interface;
        }
        var dispatchers = new List<(MapSymbol Target, MapSymbol Method, int Count, string? ViaInterface)>();
        var publishers = new List<(MapSymbol Target, MapSymbol Method, int Count, int FromHandlers)>();
        foreach (var (id, (count, fromHandlers)) in conduitTargets.OrderByDescending(p => p.Value.Count).ThenBy(p => byId[p.Key].QualifiedName, StringComparer.Ordinal))
        {
            var t = byId[id];
            var method = t;
            string? viaInterface = null;
            if (t.Kind != SymbolKind.External && t.ParentId is { } pid && byId.TryGetValue(pid, out var owner) && owner.Kind == SymbolKind.Interface)
            {
                var impl = (implementersOf.GetValueOrDefault(owner.Id) ?? [])
                    .Select(i => byId.GetValueOrDefault(i)).Where(i => i is not null && i.Kind is SymbolKind.Class or SymbolKind.Struct && InContainer(i!))
                    .Select(i => Methods(i!).FirstOrDefault(m => ShortName(m) == ShortName(t))).FirstOrDefault(m => m is not null);
                if (impl is not null) { method = impl; viaInterface = ShortName(owner); }
            }
            if (ShortName(t).Contains("Publish", StringComparison.Ordinal) && fromHandlers * 2 >= count) publishers.Add((t, method, count, fromHandlers));
            else dispatchers.Add((t, method, count, viaInterface));
        }
        // 主分派器：实现里能走到行为接口（自研：Dispatcher → 包装器 → IPipelineBehavior.HandleAsync）的优先；Publish 一类（事件通常不过请求管道）排后；再按调用次数
        dispatchers = dispatchers
            .OrderByDescending(d => d.Method.Kind != SymbolKind.External && ReachesBehavior(d.Method))
            .ThenBy(d => ShortName(d.Target).Contains("Publish", StringComparison.Ordinal))
            .ThenByDescending(d => d.Count).ToList();

        // ---- 入口 ----
        var containerEntries = input.Entries.Where(e => e.ContainerKey == container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
        var httpEntries = containerEntries.Where(e => e.Kind == "http").ToList();

        // ---- 中间件与全局过滤器（源码文本）----
        var middlewareNodes = new List<FlowNodeDto>();
        var middlewareSymbols = new List<MapSymbol>();
        var middlewareOrderKnown = true;
        var pipelineFiles = new SortedSet<string>(StringComparer.Ordinal);
        // 真的贡献了中间件的文件（说明里只列这些；只有 MapHub / MapControllers 的扩展方法文件不算）
        var middlewareFiles = new List<string>();
        var startupFound = false;
        string? startupFile = null;
        (int Start, int End) startupSpan = (0, 0);
        var endpointMaps = 0;
        var endpointNames = new List<string>();
        var home = container.Path ?? container.Members.FirstOrDefault() ?? "";
        var homeFiles = symbols.Where(s => s.Kind != SymbolKind.External && Under(s.Path, home) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName))
            .Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
        var extensionMethods = symbols.Where(s => s.Kind == SymbolKind.Method && InContainer(s) && (s.Name.StartsWith("Use", StringComparison.Ordinal) || s.Name.StartsWith("Map", StringComparison.Ordinal)))
            .GroupBy(ShortName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var classesByName = symbols.Where(s => s.Kind == SymbolKind.Class && InContainer(s)).GroupBy(ShortName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        FlowNodeDto MiddlewareClassNode(MapSymbol cls, string note, string? path, int? line)
        {
            middlewareSymbols.Add(cls);
            var method = MethodNamed(cls, MiddlewareMethodNames);
            if (method is null) return Node(cls.Id, ShortName(cls), cls.QualifiedName, "call", StageMiddleware, note, [], path ?? cls.Path, line ?? cls.StartLine);
            var split = SplitAtNext(method, true);
            var kids = Children(split.Pre, method.Path, StageMiddleware, null);
            kids.AddRange(Children(split.Post, method.Path, StageMiddleware, "next 之后（后置，响应往回走时执行）"));
            return Node(method.Id, MemberLabel(method), method.QualifiedName, "call", StageMiddleware, note + (split.Conditional ? "；有条件（有分支不调 next 直接返回）" : "") + (split.HasNext ? "" : "；没找到 next 调用"),
                Cap(kids, StageMiddleware), path ?? method.Path, line ?? method.StartLine);
        }

        // receiver 上的 UseXxx 链；depth 是进扩展方法的层数，via 是外层扩展方法名
        void ScanPipeline(string path, string text, int start, int end, string receiver, int depth, string? via, HashSet<long> visited)
        {
            pipelineFiles.Add(path);
            foreach (var call in ScanReceiver(text, receiver, start, end))
            {
                var line = LineAt(text, call.Pos);
                var where = $"{FileName(path)}:{line}";
                var viaNote = via is null ? "" : $"经 {via}()；";
                if (call.Name is "Run" or "RunAsync" or "Start" or "StartAsync" && via is null) break;
                if (call.Name == "UseMiddleware")
                {
                    var typeName = call.Generic ?? (TypeOfArg().Match(call.Args) is { Success: true } m ? m.Groups["t"].Value : null);
                    var key = typeName is null ? null : MapBuilder.StripGenericArgs(MapBuilder.LastSegment(MapBuilder.StripGenericArgs(typeName)));
                    AddMiddlewareFile(path);
                    if (key is not null && classesByName.TryGetValue(key, out var cls))
                        middlewareNodes.Add(MiddlewareClassNode(cls, $"{viaNote}自研中间件（UseMiddleware，{where}）", path, line));
                    else
                        middlewareNodes.Add(Node(null, "UseMiddleware" + (key is null ? "" : $"<{key}>"), "", "external-group", StageMiddleware, $"{viaNote}中间件类不在这个容器里（{where}）", [], path, line));
                    continue;
                }
                var isUse = call.Name.StartsWith("Use", StringComparison.Ordinal);
                var isMap = call.Name.StartsWith("Map", StringComparison.Ordinal);
                if (!isUse && !isMap) continue;
                if (call.Name is "UseEndpoints" or "UseMvc") { endpointMaps++; endpointNames.Add(call.Name); continue; }
                // 仓库里的扩展方法：进去接着找（UseApiPipeline → app.UseA / UseMiddleware<B>）
                if (depth < MaxExtensionDepth && extensionMethods.TryGetValue(call.Name, out var exts)
                    && exts.FirstOrDefault(x => !visited.Contains(x.Id) && src.Signature(x.Id) is { } sig && PipelineReceiver().IsMatch(sig)) is { } ext
                    && Text(ext.Path) is { } extText)
                {
                    var param = PipelineReceiver().Match(src.Signature(ext.Id)!).Groups[1].Value;
                    var (s, e) = LineSpan(extText, ext.StartLine, ext.EndLine);
                    var before = middlewareNodes.Count;
                    var mapsBefore = endpointMaps;
                    ScanPipeline(ext.Path, extText, s, e, param, depth + 1, call.Name, [.. visited, ext.Id]);
                    if (middlewareNodes.Count > before) continue;
                    // 里面只有 MapXxx（UseDefaultOpenApi 里是 MapOpenApi / MapGet）：是终结点注册，不是中间件
                    if (endpointMaps > mapsBefore) continue;
                    if (isMap) { endpointMaps++; endpointNames.Add(call.Name); continue; }
                    AddMiddlewareFile(path);
                    middlewareNodes.Add(Node(ext.Id, MemberLabel(ext), ext.QualifiedName, "call", StageMiddleware, $"{viaNote}仓库里的扩展方法（{where}），里面没认出中间件", [], path, line));
                    middlewareSymbols.Add(ext);
                    continue;
                }
                if (isMap) { endpointMaps++; endpointNames.Add(call.Name); continue; }
                AddMiddlewareFile(path);
                var cond = InsideIf(text, call.Pos) ? "；在 if 里，按条件注册" : "";
                if (call.Name is "Use" or "UseWhen")
                    middlewareNodes.Add(Node(null, call.Name == "Use" ? "Use（内联中间件）" : call.Name, call.Name, "external-group", StageMiddleware, $"{viaNote}内联中间件（{where}，lambda 写在管道配置里）{cond}", [], path, line));
                else
                {
                    var label = call.Name + (call.Generic is null ? "" : $"<{call.Generic}>");
                    middlewareNodes.Add(Node(null, label, call.Name, "external-group", StageMiddleware, $"{viaNote}框架内置（{where}）{cond}", [], path, line));
                }
            }
        }

        void AddMiddlewareFile(string path) { if (!middlewareFiles.Contains(path)) middlewareFiles.Add(path); }

        var isDotnetWeb = container.SubKind == "dotnet-web";
        // 管道配置：先找宿主项目里的 Program.cs / Startup.Configure；SubKind 不是 dotnet-web 的（控制台程序里起 Kestrel，
        // 如 CLI 的 serve 子命令在 ServerHost.Build 里 WebApplication.CreateSlimBuilder → app.UseCors / app.Use / MapGet）也找，
        // 找不到再到容器成员项目的源码里找 WebApplication.Create… / WebHost.Create… 之后的 x = builder.Build()，扫描范围限在所在方法里
        foreach (var path in homeFiles.Where(p => FileName(p) is "Program.cs" or "Startup.cs").OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal))
        {
            if (Text(path) is not { } text) continue;
            if (FileName(path) == "Program.cs")
            {
                var m = BuildCall().Match(text);
                if (!m.Success) continue;
                if (!isDotnetWeb && !WebHostText().IsMatch(text)) continue;
                startupFound = true;
                startupFile = path;
                startupSpan = (m.Index + m.Length, text.Length);
                ScanPipeline(path, text, m.Index + m.Length, text.Length, m.Groups[1].Value, 0, null, []);
            }
            else
            {
                var configure = symbols.FirstOrDefault(s => s.Path == path && s.Kind == SymbolKind.Method && ShortName(s) == "Configure");
                if (configure is null || src.Signature(configure.Id) is not { } sig) continue;
                var param = Regex.Match(sig, @"IApplicationBuilder\s+(\w+)");
                if (!param.Success) continue;
                startupFound = true;
                startupFile = path;
                var (s, e) = LineSpan(text, configure.StartLine, configure.EndLine);
                startupSpan = (s, e);
                ScanPipeline(path, text, s, e, param.Groups[1].Value, 0, null, []);
            }
            if (startupFound) break;
        }
        if (!startupFound)
        {
            var candidates = symbols.Where(s => s.Kind != SymbolKind.External && s.Path.EndsWith(".cs", StringComparison.Ordinal) && InContainer(s))
                .Select(s => s.Path).Distinct(StringComparer.Ordinal).OrderBy(p => Under(p, home) ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal);
            foreach (var path in candidates)
            {
                string? raw;
                try { raw = src.ReadFile(path); } catch { raw = null; }
                if (raw is null || !WebHostText().IsMatch(raw) || Text(path) is not { } text) continue;
                var host = WebHostText().Match(text);
                if (!host.Success) continue;
                var m = BuildCall().Match(text, host.Index);
                if (!m.Success) continue;
                var line = LineAt(text, m.Index);
                var scope = symbols.Where(s => s.Path == path && s.Kind is SymbolKind.Method or SymbolKind.Function && s.StartLine <= line && s.EndLine >= line)
                    .OrderBy(s => s.EndLine - s.StartLine).FirstOrDefault();
                var end = scope is null ? text.Length : LineSpan(text, scope.StartLine, scope.EndLine).End;
                startupFound = true;
                startupFile = path;
                startupSpan = (m.Index + m.Length, end);
                ScanPipeline(path, text, m.Index + m.Length, end, m.Groups[1].Value, 0, null, []);
                break;
            }
        }
        var grpcEntries = containerEntries.Where(e => e.Kind == "grpc").ToList();
        if (isDotnetWeb || startupFound || httpEntries.Count > 0 || grpcEntries.Count > 0)
        {

            // 找不到注册位置的中间件类：实现 IMiddleware，或 *Middleware 类带 InvokeAsync(HttpContext…)
            var seen = middlewareSymbols.Select(s => s.Id).ToHashSet();
            foreach (var cls in classesByName.Values.OrderBy(c => c.QualifiedName, StringComparer.Ordinal))
            {
                if (seen.Contains(cls.Id)) continue;
                var isMiddleware = InterfaceNames(cls.Id).Contains("IMiddleware")
                    || ShortName(cls).EndsWith("Middleware", StringComparison.Ordinal) && MethodNamed(cls, MiddlewareMethodNames) is { } inv && (src.Signature(inv.Id) ?? "").Contains("HttpContext", StringComparison.Ordinal);
                if (!isMiddleware) continue;
                middlewareOrderKnown = false;
                middlewareNodes.Add(MiddlewareClassNode(cls, "自研中间件；没找到 UseMiddleware 注册位置，顺序未知", null, null));
            }

            // MVC 全局过滤器：Program / Startup / 进过的扩展方法 + 调了 AddControllers / FilterCollection 的文件
            var filterFiles = new SortedSet<string>(pipelineFiles, StringComparer.Ordinal);
            foreach (var (name, callers) in callersOfName)
                if (MvcSetupMethods.Contains(name) || name == "Add" || name == "AddService")
                    foreach (var caller in callers) if (byId.TryGetValue(caller, out var c) && InContainer(c)) filterFiles.Add(c.Path);
            foreach (var path in filterFiles)
            {
                if (Text(path) is not { } text) continue;
                foreach (Match m in FilterAdd().Matches(text))
                {
                    var name = m.Groups["g"].Success ? m.Groups["g"].Value : m.Groups["t"].Success ? m.Groups["t"].Value : m.Groups["n"].Value;
                    var key = MapBuilder.LastSegment(name);
                    var where = $"{FileName(path)}:{LineAt(text, m.Index)}";
                    if (!classesByName.TryGetValue(key, out var cls))
                    {
                        middlewareNodes.Add(Node(null, key, name, "external-group", StageMiddleware, $"MVC 全局过滤器（框架 / 第三方，{where}）", [], path, LineAt(text, m.Index)));
                        continue;
                    }
                    if (middlewareSymbols.Any(s => s.Id == cls.Id)) continue;
                    middlewareSymbols.Add(cls);
                    var main = MethodNamed(cls, FilterMethodNames);
                    if (main is null)
                    {
                        middlewareNodes.Add(Node(cls.Id, ShortName(cls), cls.QualifiedName, "call", StageMiddleware, $"MVC 全局过滤器（{where}）", [], path, LineAt(text, m.Index)));
                        continue;
                    }
                    var split = SplitAtNext(main, false);
                    var kids = Children(split.Pre, main.Path, StageMiddleware, null);
                    kids.AddRange(Children(split.Post, main.Path, StageMiddleware, "next 之后（动作执行完）"));
                    foreach (var after in Methods(cls).Where(x => x.Id != main.Id && FilterAfterNames.Contains(ShortName(x))).OrderBy(x => x.StartLine))
                        kids.Add(Node(after.Id, MemberLabel(after), after.QualifiedName, "call", StageMiddleware, ShortName(after).StartsWith("OnException", StringComparison.Ordinal) ? "出异常时" : "动作执行之后", [], after.Path, after.StartLine));
                    middlewareNodes.Add(Node(main.Id, MemberLabel(main), main.QualifiedName, "call", StageMiddleware,
                        $"MVC 全局过滤器（{where}），包在控制器动作外层" + (split.Conditional ? "；有条件" : ""), Cap(kids, StageMiddleware), path, LineAt(text, m.Index)));
                }
            }
        }
        // Web：有 HTTP / gRPC 入口，或认出了中间件（dotnet-web 的容器，或找到了 WebApplication 的管道配置）——不只看 SubKind
        var isWeb = httpEntries.Count > 0 || grpcEntries.Count > 0 || middlewareNodes.Count > 0 && (isDotnetWeb || startupFound);

        // ---- 行为节点与后置 ----
        var behaviorNodes = new List<FlowNodeDto>();
        var behaviorPost = new List<(MapSymbol Behavior, List<FrameworkCall> Post, string Path)>();
        var tailSymbols = new List<MapSymbol>();
        var behaviorMethods = new HashSet<long>();
        foreach (var b in orderedBehaviors)
        {
            var method = MethodNamed(b, BehaviorMethodNames);
            var reg = registrations.TryGetValue(b.Id, out var r) ? $"{r.How}（{FileName(r.Path)}:{r.Line}）" + (r.Conditional ? "，有条件注册" : "") : "没有注册记录，顺序不可信";
            if (method is null) { behaviorNodes.Add(Node(b.Id, ShortName(b), b.QualifiedName, "call", StageBehavior, reg, [], b.Path, b.StartLine)); continue; }
            behaviorMethods.Add(method.Id);
            var split = SplitAtNext(method, false);
            var constraint = GenericConstraint(b);
            var note = reg + (split.Conditional ? "；有条件（有分支直接 next() 跳过）" : "") + (constraint is null ? "" : $"；只对 {constraint} 生效") + (split.HasNext ? "" : "；没找到 next 调用");
            behaviorNodes.Add(Node(method.Id, MemberLabel(method), method.QualifiedName, "call", StageBehavior, note, Cap(Children(split.Pre, method.Path, StageBehavior, null), StageBehavior), method.Path, method.StartLine));
            if (split.Post.Count > 0) behaviorPost.Add((b, split.Post, method.Path));
        }

        // ---- 工作单元与事件发布（Handler 往下 3 层内调到的）----
        bool IsDbContext(MapSymbol type, int depth = 0)
        {
            if (depth > 4) return false;
            foreach (var b in basesOf.GetValueOrDefault(type.Id) ?? [])
            {
                if (ShortName(b).EndsWith("DbContext", StringComparison.Ordinal)) return true;
                if (b.Kind != SymbolKind.External && IsDbContext(b, depth + 1)) return true;
            }
            return false;
        }
        HashSet<long> HandlersReachingSet(long methodId)
        {
            var reached = new HashSet<long>();
            var frontier = new List<long> { methodId };
            var visited = new HashSet<long> { methodId };
            for (var level = 0; level < 3 && frontier.Count > 0; level++)
            {
                var next = new List<long>();
                foreach (var x in frontier)
                    foreach (var f in reverse.GetValueOrDefault(x) ?? [])
                    {
                        if (!visited.Add(f)) continue;
                        if (TypeOf(f) is { } ft && handlerTypes.Contains(ft.Id)) { reached.Add(ft.Id); continue; }
                        next.Add(f);
                    }
                frontier = next;
            }
            return reached;
        }
        int HandlersReaching(long methodId) => HandlersReachingSet(methodId).Count;
        var handlerChildren = new List<FlowNodeDto>();
        var shownInBehaviors = behaviorPost.SelectMany(p => Flatten(p.Post)).Select(c => c.TargetId).Where(i => i is not null).Select(i => i!.Value).ToHashSet();
        // 提交：Handler 自己调的 SaveChanges / SaveEntities / Commit——工作单元（*UnitOfWork）、DbContext 子类、仓储（*Repository）上的都算，
        // 接口方法（repository.SaveChangesAsync() 落在 I…Repository 上）的 Handler 并进它在容器里的实现。行为后置里已经画了的（事务行为统一提交）不算。
        // 以前只认工作单元 / DbContext，ccs 一类"26 个 Handler 自己调仓储的 SaveChangesAsync"只数出 5 个，还写成"工作单元提交"像框架统一提交
        bool IsCommitOwner(MapSymbol owner) =>
            owner.Kind == SymbolKind.Class && IsDbContext(owner)
            || ShortName(owner).Contains("UnitOfWork", StringComparison.Ordinal) || ShortName(owner).EndsWith("Repository", StringComparison.Ordinal)
            || InterfaceNames(owner.Id).Any(i => i.Contains("UnitOfWork", StringComparison.Ordinal) || i.EndsWith("Repository", StringComparison.Ordinal));
        var commitCandidates = symbols.Where(s => s.Kind == SymbolKind.Method && UnitOfWorkMethods.Contains(ShortName(s)) && InContainer(s) && !shownInBehaviors.Contains(s.Id)
                && s.ParentId is { } p && byId.TryGetValue(p, out var owner) && owner.Kind is SymbolKind.Class or SymbolKind.Interface && IsCommitOwner(owner))
            .OrderBy(s => s.QualifiedName, StringComparer.Ordinal).ToList();
        // 每个提交方法：直接调它的 Handler（方法体里 repository.SaveChangesAsync()）与经服务往下 3 层内调到的 Handler 分开数
        (HashSet<long> Direct, HashSet<long> All) CommitReach(long methodId)
        {
            var direct = new HashSet<long>();
            foreach (var f in reverse.GetValueOrDefault(methodId) ?? [])
                if (TypeOf(f) is { } ft && handlerTypes.Contains(ft.Id)) direct.Add(ft.Id);
            var all = HandlersReachingSet(methodId);
            all.UnionWith(direct);
            return (direct, all);
        }
        var commitSets = new List<(MapSymbol Method, HashSet<long> Direct, HashSet<long> All)>();
        foreach (var u in commitCandidates.Where(c => byId.TryGetValue(c.ParentId!.Value, out var o) && o.Kind == SymbolKind.Class))
        {
            var (direct, all) = CommitReach(u.Id);
            // 它实现的接口方法上的调用（repository.SaveChangesAsync() 的边落在接口上）
            foreach (var iface in interfacesOf.GetValueOrDefault(u.ParentId!.Value) ?? [])
                foreach (var im in Methods(iface).Where(m => ShortName(m) == ShortName(u)))
                {
                    var (d2, a2) = CommitReach(im.Id);
                    direct.UnionWith(d2);
                    all.UnionWith(a2);
                }
            if (all.Count > 0) commitSets.Add((u, direct, all));
        }
        var coveredInterfaces = commitSets.SelectMany(c => interfacesOf.GetValueOrDefault(c.Method.ParentId!.Value) ?? []).Select(i => i.Id).ToHashSet();
        foreach (var u in commitCandidates.Where(c => byId.TryGetValue(c.ParentId!.Value, out var o) && o.Kind == SymbolKind.Interface && !coveredInterfaces.Contains(o.Id)))
            if (CommitReach(u.Id) is { All.Count: > 0 } r) commitSets.Add((u, r.Direct, r.All));
        var directCommit = commitSets.SelectMany(c => c.Direct).ToHashSet();
        var anyCommit = commitSets.SelectMany(c => c.All).ToHashSet();
        var handlerTypeTotal = handlerPairs.Select(p => p.Handler.Id).Distinct().Count();
        FlowNodeDto CommitNode(MapSymbol u, string notePrefix)
        {
            var calls = Ordered(src.Calls(u.Id)).Where(Keep).ToList();
            var domainEvents = Flatten(calls).Any(c => c.Label.Contains("DomainEvent", StringComparison.Ordinal) || c.Kind == "dispatch");
            tailSymbols.Add(u);
            return Node(u.Id, MemberLabel(u), u.QualifiedName, "call", StageTail, notePrefix + (domainEvents ? "；提交前派发领域事件" : ""),
                Cap(Children(calls, u.Path, StageTail, null), StageTail), u.Path, u.StartLine);
        }
        string Reach(HashSet<long> direct, HashSet<long> all) =>
            all.Count == direct.Count ? $"{direct.Count} 个 Handler 直接调用" : direct.Count == 0 ? $"{all.Count} 个 Handler 经服务往下调到" : $"{direct.Count} 个 Handler 直接调用、另 {all.Count - direct.Count} 个经服务往下调到";
        string CommitWho() =>
            (anyCommit.Count >= handlerTypeTotal ? "Handler 自己提交" : "部分 Handler 自己提交")
            + $"（{Reach(directCommit, anyCommit)}，共 {handlerTypeTotal} 个 Handler）：不是框架统一提交，没调到的 Handler 不落库或在别处提交";
        if (commitSets.Count == 1)
            handlerChildren.Add(CommitNode(commitSets[0].Method, CommitWho()));
        else if (commitSets.Count > 1)
        {
            var kids = commitSets.OrderByDescending(c => c.Direct.Count).ThenByDescending(c => c.All.Count).ThenBy(c => c.Method.QualifiedName, StringComparer.Ordinal)
                .Select(c => CommitNode(c.Method, Reach(c.Direct, c.All))).ToList();
            handlerChildren.Add(Node(null, "Handler 自己提交", "", "call", StageTail,
                CommitWho() + $"；经 {commitSets.Count} 个提交方法（工作单元 / DbContext / 仓储的 SaveChanges*、Commit*）", Cap(kids, StageTail), null, null));
        }
        foreach (var (target, method, count, fromHandlers) in publishers)
        {
            tailSymbols.Add(method);
            var calls = method.Kind == SymbolKind.External ? new List<FrameworkCall>() : Ordered(src.Calls(method.Id)).Where(Keep).ToList();
            handlerChildren.Add(Node(method.Id, method.Kind == SymbolKind.External ? OutlineBuilder.ExternalLabel(method.QualifiedName) : MemberLabel(method), method.QualifiedName, "call", StageTail,
                $"事件发布：{count} 处调用，{fromHandlers} 处在 Handler 里" + (method.Id != target.Id ? $"（经接口 {ShortName(byId[target.ParentId!.Value])}）" : ""),
                Cap(Children(calls, method.Path, StageTail, null), StageTail), method.Kind == SymbolKind.External ? null : method.Path, method.Kind == SymbolKind.External ? null : method.StartLine));
        }
        // Handler 里直接调的集成事件发布（IEventBus.PublishAsync / *IntegrationEventService / Outbox），已在行为后置里出现的不重复
        var shownTail = tailSymbols.Select(t => t.Id).Concat(shownInBehaviors).ToHashSet();
        foreach (var m in symbols.Where(s => s.Kind == SymbolKind.Method && InContainer(s) && ShortName(s).StartsWith("Publish", StringComparison.Ordinal)
                     && s.ParentId is { } p && byId.TryGetValue(p, out var owner) && owner.Kind == SymbolKind.Class
                     && (IsEventBusName(ShortName(owner)) || InterfaceNames(owner.Id).Any(IsEventBusName)))
                 .OrderBy(s => s.QualifiedName, StringComparer.Ordinal))
        {
            if (shownTail.Contains(m.Id)) continue;
            var n = HandlersReaching(m.Id);
            if (n == 0) continue;
            tailSymbols.Add(m);
            handlerChildren.Add(Node(m.Id, MemberLabel(m), m.QualifiedName, "call", StageTail, $"集成事件发布：{n} 个 Handler 往下调到", [], m.Path, m.StartLine));
        }

        // ---- 入口分类 ----
        // gRPC 服务方法（MapGrpcService<T>）在 ASP.NET Core 里走同一条中间件管道；Hub 方法（SignalR）单独计；
        // 常驻服务 / 任务：Web 容器里不算 Main（宿主本身），非 Web 容器里 Main 就是入口
        var hubEntries = containerEntries.Where(e => e.Kind == "hub").ToList();
        bool IsHandlerEntry(EntryPoint e) => e.Kind == "handler" || byId.TryGetValue(e.SymbolId, out var es) && TypeOf(es.Id) is { } et && handlerTypes.Contains(et.Id);
        var hostedEntries = containerEntries.Where(e => e.Kind is not ("http" or "grpc" or "hub") && !IsHandlerEntry(e) && !(isWeb && e.RuleId == "csharp.main"))
            .DistinctBy(e => e.SymbolId).ToList();
        var entryById = containerEntries.GroupBy(e => e.SymbolId).ToDictionary(g => g.Key, g => g.First());

        // ---- 哪些入口真的调到主分派器（DispatchReachDepth 层内）：从分派调用处沿调用 / 分派边反查 ----
        var mainGroup = new List<(MapSymbol Target, MapSymbol Method, int Count, string? ViaInterface)>();
        if (dispatchers.Count > 0)
        {
            var first = dispatchers[0];
            var ownerKey = DispatcherOwnerKey(first.Method);
            mainGroup.Add(first);
            // 同一个分派器类上的 Send / Query（自研 Dispatcher.SendAsync / QueryAsync、MediatR ISender.Send）对等，并成一行；Publish 一类不并
            foreach (var d in dispatchers.Skip(1))
                if (DispatcherOwnerKey(d.Method) == ownerKey && !ShortName(d.Target).Contains("Publish", StringComparison.Ordinal)
                    && (d.Method.Kind == SymbolKind.External || first.Method.Kind == SymbolKind.External || ReachesBehavior(d.Method) == ReachesBehavior(first.Method)))
                    mainGroup.Add(d);
        }
        var mainTargets = mainGroup.Select(d => d.Target.Id).ToHashSet();
        var reachesDispatcher = new HashSet<long>();
        {
            // 起点：带消息对象的分派调用（conduit），加上普通调用边指向分派方法的（转发：SendApprovedAsync(command) 里 _dispatcher.SendAsync(command) 的实参是参数，不记 conduit）
            var frontier = conduitEdges.Where(e => mainTargets.Contains(e.ToId)).Select(e => e.FromId)
                .Concat(mainGroup.SelectMany(d => new[] { d.Target.Id, d.Method.Id }).SelectMany(id => reverse.GetValueOrDefault(id) ?? []))
                .Where(id => byId.TryGetValue(id, out var f) && InContainer(f) && !(TypeOf(id) is { } ft && mainGroup.Any(d => TypeOf(d.Method.Id)?.Id == ft.Id)))
                .Distinct().ToList();
            reachesDispatcher.UnionWith(frontier);
            for (var depth = 0; depth < DispatchReachDepth && frontier.Count > 0 && reachesDispatcher.Count < EntrySearchCap * 4; depth++)
            {
                var next = new List<long>();
                foreach (var x in frontier)
                {
                    foreach (var f in reverse.GetValueOrDefault(x) ?? []) if (reachesDispatcher.Add(f)) next.Add(f);
                    // 实现方法 → 它实现的接口方法（调用边落在接口上：handler.ExecuteAsync(...) 经 IApprovalModuleHandler，实现有二十几个时没有逐个补派生边）；
                    // 重写方法 → 基类的抽象 / 虚方法（ApprovalModuleHandlerBase.ExecuteAsync），再到它实现的接口方法
                    foreach (var im in (interfacesOf.GetValueOrDefault(x) ?? []).Concat(basesOf.GetValueOrDefault(x) ?? []))
                        if (im.Kind is SymbolKind.Method && reachesDispatcher.Add(im.Id)) next.Add(im.Id);
                    // Handler 的方法 → Handler 类（分派边落在类上：Handle 继承自基类）；别的类不上溯（new / 注册它的地方不是调用方）
                    if (TypeOf(x) is { } t && handlerTypes.Contains(t.Id) && reachesDispatcher.Add(t.Id)) next.Add(t.Id);
                }
                frontier = next;
            }
        }
        // 入口写成内联 lambda（app.MapGet("/x", () => …)，入口符号是外层的 Program 顶级语句 / ServerHost.Build）：外层方法什么都调，
        // 只看这条 MapXxx 语句（入口行到语句结束的分号）里的调用
        var mainMethodIds = mainGroup.SelectMany(d => new[] { d.Target.Id, d.Method.Id }).ToHashSet();
        bool Dispatches(EntryPoint e)
        {
            if (!byId.TryGetValue(e.SymbolId, out var s)) return false;
            var inline = s.Path == e.File && s.StartLine < e.Line && e.Line <= s.EndLine;
            if (!inline) return reachesDispatcher.Contains(e.SymbolId);
            if (Text(e.File) is not { } text) return false;
            var (start, _) = LineSpan(text, e.Line, e.Line);
            var depth = 0;
            var end = start;
            for (; end < text.Length; end++)
            {
                var ch = text[end];
                if (ch is '(' or '{' or '[') depth++;
                else if (ch is ')' or '}' or ']') depth--;
                else if (ch == ';' && depth <= 0) break;
            }
            var endLine = LineAt(text, end);
            return Flatten(src.Calls(s.Id)).Any(c => c.Line >= e.Line && c.Line <= endLine && c.TargetId is { } t && (mainMethodIds.Contains(t) || reachesDispatcher.Contains(t)));
        }
        var httpDispatching = httpEntries.Count(Dispatches);
        var grpcDispatching = grpcEntries.Count(Dispatches);
        var hostedDispatching = hostedEntries.Where(Dispatches).ToList();

        // ---- Handler：经分派器的 vs 消息消费的（集成事件 / 消息队列，事件总线触发，不在 HTTP 请求链上）----
        var handlerEntriesOf = handlerPairs.ToDictionary(p => (p.Handler.Id, p.Message.Id), p => EntriesOf(p.Handler, p.Message, entryById));
        bool IsConsumerHandler(MapSymbol h) =>
            InterfaceNames(h.Id).Any(i => i.Contains("IntegrationEvent", StringComparison.Ordinal) || i.EndsWith("Consumer", StringComparison.Ordinal))
            || (basesOf.GetValueOrDefault(h.Id) ?? []).Any(b => ShortName(b).Contains("IntegrationEvent", StringComparison.Ordinal) || ShortName(b).EndsWith("Consumer", StringComparison.Ordinal));
        var consumerPairs = handlerPairs.Where(p => IsConsumerHandler(p.Handler)).ToList();
        var restPairs = handlerPairs.Where(p => !IsConsumerHandler(p.Handler)).ToList();
        if (dispatchers.Count == 0)
        {
            // 没有分派器：HTTP / gRPC 入口往下调得到的 Handler 挂在请求链上，其余的（没有 HTTP 入口调到）按消息消费画
            var byHttp = restPairs.Where(p => handlerEntriesOf[(p.Handler.Id, p.Message.Id)].Any(e => e.Kind is "http" or "grpc")).ToList();
            consumerPairs.AddRange(restPairs.Except(byHttp));
            restPairs = byHttp;
        }

        // ---- 组装：分派器 → 行为 → Handler → 后置 ----
        var handlerCount = handlerPairs.Count;
        FlowNodeDto? handlersNode = restPairs.Count > 0 ? Node(null, "Handler", "", "handlers", StageHandler, null, Cap(handlerChildren, StageTail), null, null, restPairs.Count) : null;
        // 消息消费的 Handler 里有的再经分派器发命令（集成事件 Handler → mediator.Send(XxxCommand)）
        var consumerDispatching = consumerPairs.Select(p => p.Handler).DistinctBy(h => h.Id)
            .Count(h => reachesDispatcher.Contains(h.Id) || Methods(h).Any(m => reachesDispatcher.Contains(m.Id)));
        FlowNodeDto? consumerNode = consumerPairs.Count > 0
            ? Node(null, "Handler", "", "handlers", StageHandler, $"消息消费：{consumerPairs.Count} 个 Handler 处理集成事件 / 消息（事件总线订阅触发，不经 HTTP 请求链）"
                + (consumerDispatching > 0 && mainGroup.Count > 0 ? $"；其中 {consumerDispatching} 个再经分派器发命令（走上面那条分派器 → 行为链）" : ""),
                handlersNode is null ? Cap(handlerChildren, StageTail) : [], null, null, consumerPairs.Count)
            : null;

        var tailNodes = new List<FlowNodeDto>();
        foreach (var (b, post, path) in Enumerable.Reverse(behaviorPost))
            foreach (var c in post)
            {
                if (c.TargetId is { } tid && byId.TryGetValue(tid, out var ts) && ts.Kind != SymbolKind.External) tailSymbols.Add(ts);
                tailNodes.Add(CallNode(c, path, StageTail, $"{ShortName(b)} 后置"));
            }

        FlowNodeDto? dispatcherNode = null;
        var dispatcherSymbols = new List<MapSymbol>();
        var secondaryDispatchers = new List<FlowNodeDto>();
        string MethodLabel(MapSymbol m) => m.Kind == SymbolKind.External ? OutlineBuilder.ExternalLabel(m.QualifiedName) : MemberLabel(m);
        var mainCount = mainGroup.Sum(d => d.Count);
        var mainLabel = "";
        if (mainGroup.Count > 0)
        {
            var (_, method, _, viaInterface) = mainGroup[0];
            var ordered = mainGroup.OrderByDescending(d => d.Count).ThenBy(d => MethodLabel(d.Method), StringComparer.Ordinal).ToList();
            var ownerLabel = MethodLabel(method);
            var dot = ownerLabel.LastIndexOf('.');
            mainLabel = mainGroup.Count == 1 || dot < 0 ? ownerLabel
                : ownerLabel[..dot] + "." + string.Join(" / ", ordered.Select(d => { var l = MethodLabel(d.Method); var i = l.LastIndexOf('.'); return i < 0 ? l : l[(i + 1)..]; }));
            var perMethod = mainGroup.Count == 1 ? "" : "：" + string.Join("、", ordered.Select(d => { var l = MethodLabel(d.Method); var i = l.LastIndexOf('.'); return $"{(i < 0 ? l : l[(i + 1)..])} {d.Count}"; }));
            foreach (var d in mainGroup) dispatcherSymbols.Add(d.Method);
            var kids = new List<FlowNodeDto>();
            var post = new List<FlowNodeDto>();
            string note;
            if (method.Kind == SymbolKind.External)
            {
                var lib = method.QualifiedName["external:".Length..].Split('.')[0];
                note = lib == "MediatR"
                    ? $"MediatR（外部库，{mainCount} 处调用{perMethod}）：按消息类型解析 IRequestHandler，按注册顺序套上 IPipelineBehavior"
                    : $"外部库 {lib}（{mainCount} 处调用{perMethod}）";
            }
            else
            {
                note = $"自研分派器（{mainCount} 处调用{perMethod}{(viaInterface is null ? "" : "，经接口 " + viaInterface)}）";
                if (mainGroup.Count == 1)
                {
                    var (pre, after) = DispatcherSteps(method);
                    kids.AddRange(pre);
                    post.AddRange(after);
                }
                else
                    // 并成一行的几个分派方法各走各的包装器（CommandHandlerWrapper / QueryHandlerWrapper）：每个方法一行，下面是它进管道之前的步骤
                    foreach (var d in ordered)
                    {
                        if (d.Method.Kind == SymbolKind.External) continue;
                        var (pre, after) = DispatcherSteps(d.Method);
                        kids.Add(Node(d.Method.Id, MemberLabel(d.Method), d.Method.QualifiedName, "call", StageDispatcher, $"{d.Count} 处调用", pre, d.Method.Path, d.Method.StartLine));
                        post.AddRange(after);
                    }
            }
            kids.AddRange(behaviorNodes);
            if (handlersNode is not null) kids.Add(handlersNode);
            kids.AddRange(tailNodes);
            kids.AddRange(post.DistinctBy(p => (p.Label, p.QualifiedName)));
            dispatcherNode = Node(method.Id, mainLabel, method.QualifiedName, "call", StageDispatcher,
                note, kids, method.Kind == SymbolKind.External ? null : method.Path, method.Kind == SymbolKind.External ? null : method.StartLine);
            foreach (var (_, m2, c2, via2) in dispatchers.Where(d => !mainGroup.Contains(d)))
            {
                dispatcherSymbols.Add(m2);
                secondaryDispatchers.Add(Node(m2.Id, MethodLabel(m2), m2.QualifiedName, "call", StageDispatcher,
                    $"另一个分派方法（{c2} 处调用{(via2 is null ? "" : "，经接口 " + via2)}）", [], m2.Kind == SymbolKind.External ? null : m2.Path, m2.Kind == SymbolKind.External ? null : m2.StartLine));
            }
        }

        // 没有分派器时：行为（如果有）与 Handler 直接挂在控制器 / 入口下面
        var chain = new List<FlowNodeDto>();
        if (dispatcherNode is not null) { chain.Add(dispatcherNode); chain.AddRange(secondaryDispatchers); }
        else
        {
            chain.AddRange(behaviorNodes);
            if (handlersNode is not null) chain.Add(handlersNode);
            chain.AddRange(tailNodes);
        }
        var chainPlaced = false;
        var consumerPlaced = consumerNode is null;
        // 分派器（连同行为链、Handler、收尾）挂在谁下面：真的调到它的入口（HTTP 优先，再 gRPC、常驻服务）；谁都没调到（orphan）放在常驻服务或 HTTP 下并在说明里写清
        var placeIn = dispatcherNode is null
            ? (isWeb && httpEntries.Count > 0 ? "http" : isWeb && grpcEntries.Count > 0 ? "grpc" : "hosted")
            : isWeb && httpDispatching > 0 ? "http" : isWeb && grpcDispatching > 0 ? "grpc" : hostedDispatching.Count > 0 ? "hosted" : "orphan";
        var chainOrphan = placeIn == "orphan" && chain.Count > 0;
        if (chainOrphan) placeIn = hostedEntries.Count > 0 ? "hosted" : isWeb && httpEntries.Count > 0 ? "http" : isWeb && grpcEntries.Count > 0 ? "grpc" : "hosted";

        // ---- HTTP / gRPC 入口占位 ----
        var minimal = httpEntries.Count(e => e.RuleId == "aspnet.minimal");
        var httpLabel = minimal == httpEntries.Count ? "终结点" : minimal == 0 ? "控制器" : "控制器 / 终结点";
        FlowNodeDto? httpNode = null;
        FlowNodeDto? grpcNode = null;
        string? DispatchNote(int total, int dispatching) =>
            dispatcherNode is null || total == 0 ? null
            : dispatching == 0 ? $"这 {total} 个入口都不经分派器"
            : dispatching < total ? $"其中 {total - dispatching} 个入口不经分派器（直接调服务），{dispatching} 个经 {mainLabel}"
            : null;
        if (isWeb && httpEntries.Count > 0)
        {
            var kids = new List<FlowNodeDto>();
            if (placeIn == "http") { kids.AddRange(chain); chainPlaced = true; }
            httpNode = Node(null, httpLabel, "", "controllers", StageController, DispatchNote(httpEntries.Count, httpDispatching), kids, null, null, httpEntries.Count);
        }
        if (isWeb && grpcEntries.Count > 0)
        {
            var kids = new List<FlowNodeDto>(GrpcInterceptorNodes());
            if (placeIn == "grpc") { kids.AddRange(chain); chainPlaced = true; }
            grpcNode = Node(null, "gRPC 服务方法", "", "controllers", StageController,
                (kids.Count == 0 ? "MapGrpcService 注册的服务方法，走同一条中间件管道（HTTP/2）；没有注册 gRPC 拦截器（AddGrpc 里没有 Interceptors.Add）" : "MapGrpcService 注册的服务方法，走同一条中间件管道（HTTP/2）")
                + (DispatchNote(grpcEntries.Count, grpcDispatching) is { } dn ? "；" + dn : ""), kids, null, null, grpcEntries.Count);
        }

        // ---- 常驻服务 / 任务 / 消息消费 ----
        // Worker 一类（HTTP 只有健康检查、版本号几个终结点、都不调分派器；真正的活在常驻服务里）、消息消费的 Handler、非 Web 容器：以入口自己的关键调用画
        var showHosted = hostedEntries.Count > 0 && (!isWeb || consumerNode is not null || placeIn == "hosted" && chain.Count > 0);
        FlowNodeDto? hostedNode = null;
        if (showHosted)
        {
            bool IsBus(EntryPoint e) => byId.TryGetValue(e.SymbolId, out var s) && TypeOf(s.Id) is { } t
                && (IsEventBusName(ShortName(t)) || ShortName(t).Contains("Consumer", StringComparison.Ordinal) || InterfaceNames(t.Id).Any(IsEventBusName));
            var kids = new List<FlowNodeDto>();
            foreach (var e in hostedEntries.OrderBy(e => Dispatches(e) ? 0 : IsBus(e) ? 1 : 2).ThenBy(e => byId.TryGetValue(e.SymbolId, out var s) ? s.QualifiedName : "", StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(e.SymbolId, out var s)) continue;
                var calls = s.Kind is SymbolKind.Method or SymbolKind.Function ? Ordered(src.Calls(s.Id)).Where(Keep).ToList() : [];
                var entryKids = Cap(Children(calls, s.Path, StageController, null), StageController);
                var note = e.Kind switch { "job" => "定时任务", "command" => "命令", _ => e.RuleId == "csharp.main" ? "程序入口（Main）" : "常驻服务" } + "：下面是它自己的关键调用";
                if (!chainPlaced && placeIn == "hosted" && !chainOrphan && dispatcherNode is not null && Dispatches(e)) { entryKids = [.. entryKids, .. chain]; chainPlaced = true; note += "；往下调到分派器"; }
                else if (dispatcherNode is not null && Dispatches(e)) note += $"；也经 {mainLabel}（见上）";
                if (!consumerPlaced && IsBus(e)) { entryKids = [.. entryKids, consumerNode!]; consumerPlaced = true; note += "；消息到了按类型找 Handler"; }
                var entryLabel = e.RuleId == "csharp.main" ? $"{FileName(e.File)} Main" : OutlineBuilder.IsType(s.Kind) ? ShortName(s) : MemberLabel(s);
                kids.Add(Node(s.Id, entryLabel, s.QualifiedName, "call", StageController, note, entryKids, e.File, e.Line));
            }
            if (!consumerPlaced) { kids.Add(consumerNode!); consumerPlaced = true; }
            if (!chainPlaced && placeIn == "hosted" && chain.Count > 0) { kids.AddRange(chain); chainPlaced = true; }
            var label = "常驻服务 / 任务" + (consumerNode is not null ? " / 消息消费" : "");
            hostedNode = Node(null, isWeb ? label : "入口（" + label + "）", "", "controllers", StageController, null, kids, null, null, hostedEntries.Count);
        }
        // 兜底（放不进去的：比如该挂常驻服务但没画常驻服务）
        if (!chainPlaced && chain.Count > 0)
        {
            if (httpNode is not null) httpNode = httpNode with { Children = [.. httpNode.Children, .. chain] };
            else if (grpcNode is not null) grpcNode = grpcNode with { Children = [.. grpcNode.Children, .. chain] };
            else if (hostedNode is not null) hostedNode = hostedNode with { Children = [.. hostedNode.Children, .. chain] };
            chainPlaced = httpNode is not null || grpcNode is not null || hostedNode is not null;
        }

        var webChildren = new List<FlowNodeDto>();
        if (isWeb) webChildren.AddRange(middlewareNodes);
        if (httpNode is not null) webChildren.Add(httpNode);
        if (grpcNode is not null) webChildren.Add(grpcNode);
        var rootChildren = new List<FlowNodeDto>();
        string rootLabel;
        string rootActor;
        if (isWeb && hostedNode is not null)
        {
            // 两条路：HTTP 请求一组（中间件 → 终结点），常驻服务一组
            rootLabel = "入口";
            rootActor = StageController;
            var webActor = middlewareNodes.Count > 0 ? StageMiddleware : StageController;
            rootChildren.Add(Node(null, "HTTP 请求", "", "entry", webActor, null, webChildren, null, null));
            rootChildren.Add(hostedNode);
        }
        else if (isWeb)
        {
            rootLabel = "HTTP 请求";
            rootChildren.AddRange(webChildren);
            if (!consumerPlaced) { rootChildren.Add(consumerNode!); consumerPlaced = true; }
            if (!chainPlaced) rootChildren.AddRange(chain);
            rootActor = middlewareNodes.Count > 0 ? StageMiddleware : rootChildren.Any(c => c.Kind == "controllers") ? StageController : rootChildren.FirstOrDefault()?.Actor ?? StageController;
        }
        else
        {
            rootLabel = "入口";
            if (hostedNode is not null) rootChildren.Add(hostedNode);
            else
            {
                rootChildren.AddRange(chain);
                if (!consumerPlaced) rootChildren.Add(consumerNode!);
            }
            rootActor = hostedNode is not null ? StageController : rootChildren.FirstOrDefault()?.Actor ?? StageController;
        }
        var root = Node(null, rootLabel, "", "entry", rootActor, null, rootChildren, null, null);
        var nodeCount = 0;
        root = Redepth(root, 0, ref nodeCount);

        // ---- 阶段 ----
        var stages = new List<FrameworkStageDto>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        void Collect(FlowNodeDto n) { present.Add(n.Actor); foreach (var c in n.Children) Collect(c); }
        Collect(root);
        if (isWeb && middlewareNodes.Count > 0) stages.Add(new FrameworkStageDto(StageMiddleware, "中间件", middlewareOrderKnown, middlewareSymbols.DistinctBy(s => s.Id).Select(Dto).ToList()));
        var shownEntries = new List<EntryPoint>();
        if (httpNode is not null) shownEntries.AddRange(httpEntries);
        if (grpcNode is not null) shownEntries.AddRange(grpcEntries);
        if (hostedNode is not null) shownEntries.AddRange(hostedEntries);
        if (shownEntries.Count > 0)
        {
            var label = hostedNode is not null ? "入口" : grpcNode is not null && httpNode is not null ? httpLabel + " / gRPC" : grpcNode is not null ? "gRPC 服务方法" : httpLabel;
            // 一个入口一条（同一个宿主方法上的多条 Minimal API 路由各算一条，与占位上的个数一致）
            stages.Add(new FrameworkStageDto(StageController, label, true,
                shownEntries.Where(e => byId.ContainsKey(e.SymbolId)).Select(e => Dto(byId[e.SymbolId])).ToList()));
        }
        if (dispatcherNode is not null) stages.Add(new FrameworkStageDto(StageDispatcher, "分派器", true, dispatcherSymbols.DistinctBy(s => s.Id).Select(Dto).ToList()));
        if (behaviorNodes.Count > 0) stages.Add(new FrameworkStageDto(StageBehavior, "行为链", behaviorsOrdered, orderedBehaviors.Select(Dto).ToList()));
        if (handlersNode is not null || consumerNode is not null)
            stages.Add(new FrameworkStageDto(StageHandler, "Handler", true, handlerPairs.Select(p => Dto(HandlerMethod(p.Handler, p.Message) ?? p.Handler)).ToList()));
        if (present.Contains(StageTail)) stages.Add(new FrameworkStageDto(StageTail, "收尾", behaviorsOrdered, tailSymbols.DistinctBy(s => s.Id).Select(Dto).ToList()));
        stages = AlignStageCounts(stages, root);
        var actors = stages.Select(s => new FlowActorDto(s.Key, s.Label, "stage")).ToList();
        if (!actors.Any(a => a.Key == root.Actor)) actors.Insert(0, new FlowActorDto(root.Actor, root.Actor == StageMiddleware ? "中间件" : "入口", "stage"));

        // ---- Notes ----
        string Files(IEnumerable<string> paths) => string.Join("、", paths.Select(FileName).Distinct());
        if (!isWeb)
            notes.Add(isDotnetWeb
                ? "这个容器没有 HTTP 入口，也没认出中间件：按非 Web 容器画，入口是常驻服务 / 任务 / 消息消费"
                : $"这个容器不是 Web 服务（{container.SubKind}，没有 HTTP / gRPC 入口，也没找到 WebApplication 的管道配置）：没有中间件与控制器阶段，入口是常驻服务 / 任务 / 消息消费");
        else
        {
            if (!isDotnetWeb)
                notes.Add($"项目类型是 {container.SubKind}，但" + (startupFile is not null ? $" {FileName(startupFile)} 里起了 Web 宿主（WebApplication / Kestrel），" : "")
                          + $"有 {httpEntries.Count} 个 HTTP 入口" + (grpcEntries.Count > 0 ? $"、{grpcEntries.Count} 个 gRPC 方法" : "") + "：按 Web 服务画");
            var maps = endpointNames.Count == 0 ? "" : $"（{string.Join("、", endpointNames.GroupBy(n => n, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key))}）";
            if (!startupFound) notes.Add("没有找到管道配置（Program.cs 里 builder.Build() 之后的 app.UseXxx，或 Startup.Configure）");
            else if (middlewareNodes.Count == 0)
                notes.Add($"{FileName(startupFile ?? "Program.cs")} 里没有 app.UseXxx：只有框架默认的中间件" + (endpointMaps > 0 ? $"（{endpointMaps} 处终结点注册{maps}，不算中间件）" : ""));
            else notes.Add($"中间件按 {(middlewareFiles.Count > 0 ? Files(middlewareFiles) : FileName(startupFile ?? "Program.cs"))} 里 app.UseXxx 的书写顺序" + (endpointMaps > 0 ? $"；另有 {endpointMaps} 处终结点注册{maps}，不算中间件" : "")
                           + (middlewareOrderKnown ? "" : "；有中间件类没找到注册位置，追加在最后"));
            if (AuthorizationNote() is { } auth) notes.Add(auth);
            var mains = containerEntries.Count(e => e.RuleId == "csharp.main");
            if (mains > 0 && hostedNode is null && hostedEntries.Count == 0)
                notes.Add($"另有 {mains} 个程序入口（Main：宿主本身{(isDotnetWeb ? "" : "，命令行的其他子命令也从这里进")}），没画在这条请求链上");
            if (hostedNode is null && hostedEntries.Count > 0)
                notes.Add($"另有 {hostedEntries.Count} 个常驻服务 / 任务入口，没画在这条请求链上" + (hostedDispatching.Count > 0 && dispatcherNode is not null ? $"（其中 {hostedDispatching.Count} 个也经 {mainLabel}）" : ""));
        }
        if (hostedNode is not null)
            notes.Add($"{hostedEntries.Count} 个常驻服务 / 任务画在「{hostedNode.Label}」下，各自列关键调用" + (isWeb && httpEntries.Count > 0 && httpDispatching == 0 && dispatcherNode is not null && !chainOrphan && placeIn == "hosted" ? $"：HTTP 的 {httpEntries.Count} 个{httpLabel}都不调分派器，真正的活在这些常驻服务里" : "")
                      + (dispatcherNode is not null && hostedDispatching.Count > 0 ? $"；其中 {hostedDispatching.Count} 个往下调到分派器（{string.Join("、", hostedDispatching.Take(3).Select(e => byId.TryGetValue(e.SymbolId, out var s) ? (TypeOf(s.Id) is { } t ? ShortName(t) : ShortName(s)) : "?"))}）" : ""));
        if (hubEntries.Count > 0) notes.Add($"另有 {hubEntries.Count} 个 SignalR Hub 方法（{Files(hubEntries.Select(e => e.File))}），没画");
        if (httpNode is null && grpcNode is null && hostedNode is null) notes.Add(isWeb ? "没有发现 HTTP 入口（控制器 / Minimal API）" : "没有发现入口");
        if (dispatcherNode is null)
            notes.Add(handlerCount > 0 ? "没有发现分派器（没有 Send / SendAsync 一类的分发调用）：Handler 可能是入口直接调用的" : "没有发现分派器：入口直接调用服务");
        else
        {
            var (_, m0, _, _) = mainGroup[0];
            var kind = m0.Kind == SymbolKind.External ? (m0.QualifiedName.StartsWith("external:MediatR.", StringComparison.Ordinal) ? "MediatR（外部库）" : "外部库") : "自研";
            var secondary = dispatchers.Where(d => !mainGroup.Contains(d)).ToList();
            notes.Add($"分派器：{kind} {mainLabel}，{mainCount} 处调用" + (mainGroup.Count > 1 ? $"（{string.Join("、", mainGroup.OrderByDescending(d => d.Count).Select(d => $"{MethodLabel(d.Method)} {d.Count}"))}，同一个分派器上的方法对等，并成一行）" : "")
                      + (secondary.Count > 0 ? $"；另有 {string.Join("、", secondary.Select(d => MethodLabel(d.Method)))}" : "")
                      + (publishers.Count > 0 ? $"；{string.Join("、", publishers.Select(p => MethodLabel(p.Method)))} 多在 Handler 里调用，算事件发布放进收尾" : ""));
            if (httpEntries.Count > 0 && httpDispatching < httpEntries.Count)
            {
                var direct = httpEntries.Where(e => !Dispatches(e)).ToList();
                var types = direct.Select(e => byId.TryGetValue(e.SymbolId, out var s) && TypeOf(s.Id) is { } t ? ShortName(t) : null).Where(t => t is not null).Distinct(StringComparer.Ordinal).ToList();
                notes.Add($"{direct.Count} 个 HTTP 入口不经分派器（{DispatchReachDepth} 层内没调到 {mainLabel}，直接调服务）" + (types.Count > 0 ? $"，在 {types.Count} 个类里：{string.Join("、", types.Take(6))}{(types.Count > 6 ? " …" : "")}" : ""));
            }
            if (chainOrphan) notes.Add($"没找到哪个入口在 {DispatchReachDepth} 层内调到 {mainLabel}：分派器画在「{(placeIn == "hosted" ? hostedNode?.Label ?? "入口" : placeIn == "grpc" ? "gRPC 服务方法" : httpLabel)}」下面，实际由谁调用看调用图");
        }
        if (consumerPairs.Count > 0)
            notes.Add($"{consumerPairs.Count} 个 Handler 是消息消费（集成事件 / 消息队列，事件总线收到消息后按类型调用），画在" + (hostedNode is not null ? "常驻服务下" : "请求链之外") + "，不挂在 HTTP 请求链上");
        if (behaviorTypes.Count == 0) notes.Add("没有发现行为链（没有实现 *Behavior 接口的类）");
        else if (behaviorsOrdered)
        {
            var how = registrations.Values.Select(r => r.How).Distinct().ToList();
            notes.Add($"行为顺序来自 {string.Join(" 与 ", how)} 的位置（{string.Join("、", registrations.Values.Select(r => FileName(r.Path)).Distinct())}）");
        }
        else notes.Add($"{behaviorTypes.Count(b => !registrations.ContainsKey(b.Id))} 个行为没有注册记录（DI 或 AddOpenBehavior），排在最后按名字排，顺序不可信");
        if (handlerCount == 0) notes.Add("没有发现 Handler（没有 handles 边：没有实现 I…Handler<消息> 的类）");
        if (!present.Contains(StageTail)) notes.Add("没有发现收尾（行为 next() 之后的调用、工作单元提交、事件发布）");

        // ---- Handler 清单 ----
        var handlers = BuildHandlerList(handlerPairs, handlerEntriesOf);

        var flow = new FlowDto(0, rootLabel, null, container.Key, actors, root, orderedBehaviors.Select(ShortName).ToList(), nodeCount, false, null);
        return new FrameworkFlowDto(container.Key, container.Name, flow, stages, handlers, notes);

        // ================= 局部函数 =================

        // 分派器的"类"：外部的按限定名里的类型（ISender），仓库里的按所属类型符号
        string DispatcherOwnerKey(MapSymbol m) => m.Kind == SymbolKind.External ? "x:" + OwnerAndMember(m.QualifiedName).Type : TypeOf(m.Id) is { } t ? "t:" + t.Id : "m:" + m.Id;

        // 分派器里 IPipelineBehavior.HandleAsync / IQueryHandler.HandleAsync 下面按字母序列出的实现（via impl）：和下面按注册顺序画的行为链、Handler 占位重复，去掉，
        // 在那一行的 Note 里说一句
        FlowNodeDto StripPipelineImpls(FlowNodeDto n)
        {
            var kids = new List<FlowNodeDto>();
            int behaviorsDropped = 0, handlersDropped = 0;
            foreach (var c in n.Children)
            {
                if ((c.Via == "impl" || c.Kind == "dispatch") && c.SymbolId is { } id && TypeOf(id) is { } t)
                {
                    if (behaviorIds.Contains(t.Id)) { behaviorsDropped++; continue; }
                    if (handlerTypes.Contains(t.Id)) { handlersDropped++; continue; }
                }
                kids.Add(StripPipelineImpls(c));
            }
            if (behaviorsDropped == 0 && handlersDropped == 0) return n with { Children = kids };
            var extra = behaviorsDropped > 0 ? $"{behaviorsDropped} 个实现按注册顺序画在下面的行为链" : $"{handlersDropped} 个实现见下面的 Handler";
            return n with { Children = kids, Note = n.Note is null ? extra : n.Note + " · " + extra };
        }

        // gRPC 服务端拦截器：AddGrpc(o => o.Interceptors.Add<T>()) / Interceptors.Add(typeof(T))（容器里调了 AddGrpc 的文件）
        List<FlowNodeDto> GrpcInterceptorNodes()
        {
            var nodes = new List<FlowNodeDto>();
            var files = homeFiles.Concat(pipelineFiles).Distinct(StringComparer.Ordinal);
            foreach (var path in files)
            {
                if (Text(path) is not { } text || !text.Contains("Interceptors", StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(text, @"Interceptors\s*\.\s*Add\s*(?:<\s*(?<g>[\w.]+)\s*>\s*\(|\(\s*typeof\s*\(\s*(?<t>[\w.]+))"))
                {
                    var name = MapBuilder.LastSegment(m.Groups["g"].Success ? m.Groups["g"].Value : m.Groups["t"].Value);
                    var line = LineAt(text, m.Index);
                    var where = $"{FileName(path)}:{line}";
                    if (classesByName.TryGetValue(name, out var cls) && MethodNamed(cls, ["UnaryServerHandler", "ServerStreamingServerHandler", "ClientStreamingServerHandler", "DuplexStreamingServerHandler"]) is { } h)
                        nodes.Add(Node(h.Id, MemberLabel(h), h.QualifiedName, "call", StageInterceptor, $"gRPC 服务端拦截器（{where}），包在每个服务方法外层",
                            Cap(Children(Ordered(src.Calls(h.Id)).Where(Keep).ToList(), h.Path, StageInterceptor, null), StageInterceptor), path, line));
                    else nodes.Add(Node(null, name, name, "external-group", StageInterceptor, $"gRPC 服务端拦截器（{where}）", [], path, line));
                }
            }
            return nodes;
        }

        // 终结点上的授权：MapXxx().RequireAuthorization()、控制器上的 [Authorize]；管道里没写 UseAuthentication / UseAuthorization 的说明 .NET 会自动加
        string? AuthorizationNote()
        {
            var sites = new List<string>();
            var files = (startupFile is null ? [] : new[] { startupFile }).Concat(pipelineFiles).Concat(homeFiles).Concat(httpEntries.Select(e => e.File)).Distinct(StringComparer.Ordinal);
            foreach (var path in files)
            {
                if (Text(path) is not { } text) continue;
                foreach (Match m in RequireAuthorizationCall().Matches(text))
                {
                    var start = m.Index - 1;
                    while (start >= 0 && text[start] is not (';' or '{' or '}')) start--;
                    var stmt = Regex.Replace(text[(start + 1)..m.Index], @"\s+", "").Trim();
                    if (stmt.Length > 60) stmt = "…" + stmt[^60..];
                    sites.Add($"{FileName(path)}:{LineAt(text, m.Index)} {stmt}.RequireAuthorization()");
                }
            }
            var authorizeFiles = httpEntries.Select(e => e.File).Distinct(StringComparer.Ordinal)
                .Count(f => Text(f) is { } t && Regex.IsMatch(t, @"\[\s*Authorize\b"));
            if (sites.Count == 0 && authorizeFiles == 0) return null;
            var parts = new List<string>();
            if (sites.Count > 0) parts.Add($"{sites.Count} 处 RequireAuthorization（{string.Join("；", sites.Distinct().Take(3))}{(sites.Count > 3 ? " …" : "")}）");
            if (authorizeFiles > 0) parts.Add($"{authorizeFiles} 个入口文件带 [Authorize]");
            var hasMw = middlewareNodes.Any(n => n.Label is "UseAuthorization" or "UseAuthentication");
            return "终结点上的授权要求：" + string.Join("，", parts) + "——在终结点执行前检查（未登录 401、没权限 403），不是单独一列"
                   + (hasMw ? "" : "；管道里没写 UseAuthentication / UseAuthorization：.NET 7+ 的 WebApplication 在注册了认证 / 授权服务时会自动加上（路由之后、终结点之前）");
        }

        // 抽象方法 / 虚方法在同一文件里的重写（Dispatcher → RequestHandlerWrapper<T>.Handle → RequestHandlerWrapperImpl<…>.Handle）
        IEnumerable<MapSymbol> Overrides(MapSymbol method)
        {
            if (method.ParentId is not { } pid) yield break;
            foreach (var d in derivedOf.GetValueOrDefault(pid) ?? [])
                if (byId.TryGetValue(d, out var dt) && dt.Path == method.Path && Methods(dt).FirstOrDefault(m => ShortName(m) == ShortName(method)) is { } o) yield return o;
        }

        // 同文件里往下 3 层内有没有调到行为接口的方法（behavior.HandleAsync(...)）
        bool ReachesBehavior(MapSymbol method)
        {
            var visited = new HashSet<long> { method.Id };
            var frontier = new List<MapSymbol> { method };
            for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
            {
                var next = new List<MapSymbol>();
                foreach (var m in frontier)
                    foreach (var c in Flatten(src.Calls(m.Id)))
                    {
                        var (type, member) = OwnerAndMember(c.QualifiedName);
                        if (type.EndsWith("Behavior", StringComparison.Ordinal) && BehaviorMethodNames.Contains(member)) return true;
                        if (c.TargetId is not { } id || !byId.TryGetValue(id, out var t) || t.Path != method.Path || !visited.Add(id)) continue;
                        next.Add(t);
                        foreach (var o in Overrides(t)) if (visited.Add(o.Id)) next.Add(o);
                    }
                frontier = next;
            }
            return false;
        }

        Split SplitAtNext(MapSymbol method, bool middleware)
        {
            var calls = Ordered(src.Calls(method.Id)).ToList();
            var (names, types) = DelegateParameters(src.Signature(method.Id));
            if (middleware) { names.Add("_next"); names.Add("next"); types.Add("RequestDelegate"); }
            var nextCalls = calls.Where(c => IsNextCall(c, names, types)).ToList();
            var main = nextCalls.FirstOrDefault(c => c.Ctx != CallContext.Cond) ?? nextCalls.LastOrDefault();
            var conditional = nextCalls.Any(c => c.Ctx == CallContext.Cond);
            var pre = new List<FrameworkCall>();
            var post = new List<FrameworkCall>();
            var passed = false;
            foreach (var c in calls)
            {
                if (ReferenceEquals(c, main)) { passed = true; continue; }
                if (nextCalls.Contains(c) || !Keep(c)) continue;
                (passed ? post : pre).Add(c);
            }
            return new Split(pre, post, conditional, main is not null);
        }

        // 分派器方法：第一层调用；同文件里的辅助方法（包装器、实现）再进一层。最后一个"进管道"的调用之后的放进收尾
        (List<FlowNodeDto> Pre, List<FlowNodeDto> Post) DispatcherSteps(MapSymbol method)
        {
            var calls = Ordered(src.Calls(method.Id)).Where(Keep).ToList();
            bool EntersPipeline(FrameworkCall c) => Flatten([c]).Any(x => x.Kind == "dispatch" || IsPipelineInvoke(x)) || (c.TargetId is { } id && InnerEntersPipeline(id, 0));
            bool InnerEntersPipeline(long id, int depth)
            {
                if (depth > 1 || !byId.TryGetValue(id, out var s) || s.Path != method.Path) return false;
                if (Overrides(s).Any(o => InnerEntersPipeline(o.Id, depth + 1))) return true;
                return Ordered(src.Calls(id)).Any(x => Flatten([x]).Any(y => y.Kind == "dispatch" || IsPipelineInvoke(y)) || (x.TargetId is { } xi && xi != id && InnerEntersPipeline(xi, depth + 1)));
            }
            var last = calls.FindLastIndex(c => EntersPipeline(c));
            FlowNodeDto Expand(FrameworkCall c, string actor, string? note)
            {
                var node = CallNode(c, method.Path, actor, note);
                // 同文件里的辅助方法（包装器 Handle、实现类）：再列它一层调用
                var inner = Flatten([c]).Where(x => x.TargetId is { } id && byId.TryGetValue(id, out var s) && s.Path == method.Path && s.Id != method.Id && x.Kind is "call" or "impl")
                    .Select(x => byId[x.TargetId!.Value]).SelectMany(s => Overrides(s).Prepend(s)).DistinctBy(s => s.Id).ToList();
                if (inner.Count == 0) return StripPipelineImpls(node);
                var extra = inner.SelectMany(s => Children(Ordered(src.Calls(s.Id)).Where(Keep).ToList(), s.Path, actor, null)).ToList();
                return StripPipelineImpls(node with { Children = Cap([.. node.Children, .. extra], actor) });
            }
            var pre = calls.Take(last < 0 ? calls.Count : last + 1).Select(c => Expand(c, StageDispatcher, null)).ToList();
            var post = last < 0 ? new List<FlowNodeDto>() : calls.Skip(last + 1).Select(c => CallNode(c, method.Path, StageTail, "分派器后置")).ToList();
            foreach (var c in calls.Skip(last + 1)) if (c.TargetId is { } id && byId.TryGetValue(id, out var ts) && ts.Kind != SymbolKind.External) tailSymbols.Add(ts);
            return (Cap(pre, StageDispatcher), post);
        }

        // 调 Handler / 行为接口的方法、调委托（next()）：分派器里"进管道"的那一步
        bool IsPipelineInvoke(FrameworkCall c)
        {
            var (type, member) = OwnerAndMember(c.QualifiedName);
            if (member == "Invoke" && (type.EndsWith("Delegate", StringComparison.Ordinal) || type is "Func" or "Action")) return true;
            return type.Length > 0 && (type.EndsWith("Handler", StringComparison.Ordinal) || type.EndsWith("Behavior", StringComparison.Ordinal)) && type.StartsWith('I')
                   && HandlerMethodNames.Contains(member);
        }

        string? GenericConstraint(MapSymbol type)
        {
            if (Text(type.Path) is not { } text) return null;
            var (s, _) = LineSpan(text, type.StartLine, type.StartLine + 3);
            var brace = text.IndexOf('{', s);
            var header = text[s..(brace < 0 ? Math.Min(text.Length, s + 400) : brace)];
            var m = Regex.Match(header, @"where\s+\w+\s*:\s*(?<c>[^{\r\n]+)");
            if (!m.Success) return null;
            var parts = SplitTopLevel(Regex.Split(m.Groups["c"].Value, @"\bwhere\b")[0]).Select(p => p.Trim()).Where(p => p.Length > 0 && p is not ("class" or "struct" or "notnull" or "new()")).ToList();
            // IRequest<TResponse>（MediatR 的通用约束）不算条件
            parts.RemoveAll(p => MapBuilder.StripGenericArgs(p) is "IRequest" or "IBaseRequest" or "IRequest`1");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }

        // 模块 = Handler 所在命名空间（领域事件 Handler 在 Ordering.API/Application/DomainEventHandlers，消息在 Ordering.Domain：按消息分会全归到 Domain）
        List<FrameworkHandlerGroupDto> BuildHandlerList(List<(MapSymbol Handler, MapSymbol Message)> pairs, Dictionary<(long, long), List<EntryDto>> entriesOf)
        {
            if (pairs.Count == 0) return [];
            var keys = pairs.Select(p => p.Handler).DistinctBy(h => h.Id).ToDictionary(h => h.Id, h => MapBuilder.NamespaceOf(h, byId));
            var root = AutoRoot(PipelineBuilder.CommonPrefix(keys.Values), keys.Values.ToList());
            var items = new List<(string Module, FrameworkHandlerDto Item)>();
            foreach (var (h, msg) in pairs)
            {
                var method = HandlerMethod(h, msg) ?? h;
                var key = keys[h.Id];
                var module = key == root ? MapBuilder.KeyLabel(root) : MapBuilder.ChildKey(root, key) is { } child ? MapBuilder.KeyLabel(child) : MapBuilder.KeyLabel(key);
                if (module.Length == 0) module = "（全局命名空间）";
                items.Add((module, new FrameworkHandlerDto(Dto(method), MessageTitle(h, msg, method), MessageKind(msg, h), entriesOf.GetValueOrDefault((h.Id, msg.Id)) ?? [])));
            }
            return items.GroupBy(i => i.Module, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new FrameworkHandlerGroupDto(g.Key, g.Select(i => i.Item).OrderBy(i => i.MessageType, StringComparer.Ordinal).ThenBy(i => i.Handler.QualifiedName, StringComparer.Ordinal).ToList()))
                .ToList();
        }

        // Handle 继承自泛型基类（CancelOrderIdentifiedCommandHandler : IdentifiedCommandHandler<CancelOrderCommand, bool>）：
        // 各子类落到同一个基类方法、消息短名也一样，清单里分不出来。从子类声明里读基类的泛型实参代进消息（参数个数对得上才代），读不到就带上子类名
        string MessageTitle(MapSymbol handler, MapSymbol message, MapSymbol method)
        {
            var name = ShortName(message);
            if (method.Id == handler.Id || method.ParentId == handler.Id) return name;
            var b = (basesOf.GetValueOrDefault(handler.Id) ?? []).FirstOrDefault(x => x.Kind == SymbolKind.Class);
            if (b is not null && Text(handler.Path) is { } text)
            {
                var (s, _) = LineSpan(text, handler.StartLine, handler.StartLine + 3);
                var brace = text.IndexOf('{', s);
                var header = text[s..(brace < 0 ? Math.Min(text.Length, s + 400) : brace)];
                var m = Regex.Match(header, @"\b" + Regex.Escape(ShortName(b)) + @"\s*<");
                var open = m.Success ? m.Index + m.Length - 1 : -1;
                var close = open < 0 ? -1 : MatchBracket(header, open, '<', '>');
                var arity = GenericArity(message.QualifiedName);
                if (close > open && arity > 0)
                {
                    var args = SplitTopLevel(header[(open + 1)..close]).Select(a => a.Trim()).ToList();
                    if (args.Count == arity && args.All(a => a.Length > 0)) return $"{name}<{string.Join(", ", args)}>";
                }
            }
            return $"{name}（{ShortName(handler)}）";
        }

        MapSymbol? HandlerMethod(MapSymbol handler, MapSymbol message)
        {
            var msgName = ShortName(message);
            var type = handler;
            for (var depth = 0; depth < 3; depth++)
            {
                var candidates = Methods(type).Where(m => HandlerMethodNames.Contains(ShortName(m))).ToList();
                var hit = candidates.FirstOrDefault(m => m.QualifiedName.Contains("(" + msgName, StringComparison.Ordinal) || m.QualifiedName.Contains(msgName + "<", StringComparison.Ordinal))
                          ?? candidates.OrderBy(m => Array.IndexOf(HandlerMethodNames, ShortName(m))).FirstOrDefault();
                if (hit is not null) return hit;
                var b = (basesOf.GetValueOrDefault(type.Id) ?? []).FirstOrDefault(x => x.Kind == SymbolKind.Class);
                if (b is null) break;
                type = b;
            }
            return null;
        }

        string? MessageKind(MapSymbol message, MapSymbol handler)
        {
            static string? ByNames(IEnumerable<string> names)
            {
                var list = names.ToList();
                if (list.Any(n => n.Contains("Query", StringComparison.Ordinal))) return "query";
                if (list.Any(n => EventName().IsMatch(n))) return "event";
                if (list.Any(n => n.Contains("Command", StringComparison.Ordinal))) return "command";
                return null;
            }
            var name = ShortName(message);
            return ByNames(InterfaceNames(message.Id)) ?? ByNames(InterfaceNames(handler.Id))
                   ?? (name.EndsWith("Query", StringComparison.Ordinal) ? "query"
                       : name.EndsWith("Event", StringComparison.Ordinal) || name.EndsWith("Notification", StringComparison.Ordinal) ? "event"
                       : name.EndsWith("Command", StringComparison.Ordinal) ? "command" : null);
        }

        // 沿 calls / dispatch:T 反查能走到这个 Handler 的入口（8 层内）；Handler 自己那条入口（kind=handler）不算，路过的别的 Handler 入口照常往上走。
        // 第一跳（进 Handler 自己的那条边）只认分派这条消息的 dispatch:<消息>（一个 Handler 接多种消息时各算各的）与直接调用
        List<EntryDto> EntriesOf(MapSymbol handler, MapSymbol message, Dictionary<long, EntryPoint> entryById)
        {
            var msgName = ShortName(message);
            IEnumerable<long> Into(long x, int depth) => depth > 0 ? reverse.GetValueOrDefault(x) ?? []
                : (callsIn.GetValueOrDefault(x) ?? []).Concat((dispatchIn.GetValueOrDefault(x) ?? []).Where(d => d.Message == msgName).Select(d => d.From));
            var own = new HashSet<long> { handler.Id };
            foreach (var m in Methods(handler)) own.Add(m.Id);
            var visited = new HashSet<long>(own);
            var frontier = own.ToList();
            var found = new List<EntryPoint>();
            for (var depth = 0; depth < EntrySearchDepth && frontier.Count > 0 && visited.Count < EntrySearchCap; depth++)
            {
                var next = new List<long>();
                foreach (var x in frontier)
                    foreach (var f in Into(x, depth))
                    {
                        if (!visited.Add(f)) continue;
                        next.Add(f);
                        if (entryById.TryGetValue(f, out var ep)) found.Add(ep);
                        // 分派落在类上（Handle 继承自基类）：类的入边也算；落在方法上：它所属的 Handler 类的入边也算
                        if (byId.TryGetValue(f, out var fs))
                        {
                            if (OutlineBuilder.IsType(fs.Kind)) { foreach (var m in Methods(fs)) if (visited.Add(m.Id)) { next.Add(m.Id); if (entryById.TryGetValue(m.Id, out var mep)) found.Add(mep); } }
                            else if (TypeOf(f) is { } ft && handlerTypes.Contains(ft.Id) && visited.Add(ft.Id)) { next.Add(ft.Id); if (entryById.TryGetValue(ft.Id, out var tep)) found.Add(tep); }
                        }
                    }
                frontier = next;
            }
            return found.DistinctBy(e => e.SymbolId)
                .OrderBy(e => e.Kind == "handler" ? 1 : 0).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line)
                .Take(MaxEntries)
                .Select(e => byId.TryGetValue(e.SymbolId, out var s)
                    ? new EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, s.Name, s.QualifiedName, e.File, e.Line)
                    : new EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, "?", "?", e.File, e.Line))
                .ToList();
        }

        List<FlowNodeDto> Children(IReadOnlyList<FrameworkCall> calls, string path, string actor, string? note) => calls.Select(c => CallNode(c, path, actor, note)).ToList();

    }

    /// <summary>方法里的一次调用画成一行：外部 / 没解析到的 → external-group（不带符号 id）；子节点是 Keep 过的下一层；Note = note · 上下文（循环里 / 条件分支里……）。path 是调用所在文件。</summary>
    internal static FlowNodeDto CallNode(FrameworkCall c, string? path, string actor, string? note)
    {
        var kind = c.Kind switch { "external" or "unresolved" => "external-group", "dispatch" => "dispatch", _ => "call" };
        var symbolId = kind == "external-group" ? null : c.TargetId;
        var ctx = CtxNote(c.Ctx);
        var fullNote = note is null ? ctx : ctx is null ? note : note + " · " + ctx;
        var kids = c.Children.Where(Keep).Select(x => CallNode(x, path, actor, null)).ToList();
        return new FlowNodeDto(symbolId, c.Label, c.QualifiedName, kind, actor, c.Kind == "impl" ? "impl" : null, 1.0, 1, 0, kids, 0, fullNote, path, c.Line > 0 ? c.Line : null, c.Pos);
    }

    /// <summary>
    /// 别的语言收尾用：root（没排深度的树）→ 排深度、数节点；列 = stages 的顺序（root 的 actor 不在里面时补在最前，名字 rootActorLabel）。
    /// rootLabel 是时序图的起点（"HTTP 请求" / "页面加载"……）；behaviors 进 FlowDto.Behaviors（页面标题里"经…"那一行不画，框架流程自己不用）。
    /// </summary>
    internal static FrameworkFlowDto Assemble(ArchNode container, string rootLabel, FlowNodeDto root, IReadOnlyList<FrameworkStageDto> stages,
        IReadOnlyList<FrameworkHandlerGroupDto> handlers, IReadOnlyList<string> notes, string rootActorLabel = "入口", IReadOnlyList<string>? behaviors = null)
    {
        var count = 0;
        root = Redepth(root, 0, ref count);
        stages = AlignStageCounts(stages, root);
        var actors = stages.Select(st => new FlowActorDto(st.Key, st.Label, "stage")).ToList();
        if (!actors.Any(a => a.Key == root.Actor)) actors.Insert(0, new FlowActorDto(root.Actor, rootActorLabel, "stage"));
        var flow = new FlowDto(0, rootLabel, null, container.Key, actors, root, behaviors ?? [], count, false, null);
        return new FrameworkFlowDto(container.Key, container.Name, flow, stages, handlers, notes);
    }

    /// <summary>不是 .NET 容器 / 什么都没认出来时的结果：只有一个根。</summary>
    internal static FrameworkFlowDto Empty(ArchNode container, List<string> notes)
    {
        var root = new FlowNodeDto(null, "入口", "", "entry", StageController, null, 1.0, 1, 0, [], 0, null);
        return new FrameworkFlowDto(container.Key, container.Name,
            new FlowDto(0, "入口", null, container.Key, [new FlowActorDto(StageController, "入口", "stage")], root, [], 1, false, null), [], [], notes);
    }

    internal static FlowNodeDto Node(long? symbolId, string label, string qualifiedName, string kind, string actor, string? note, List<FlowNodeDto> children, string? path, int? line, int count = 1) =>
        new(symbolId, label, qualifiedName, kind, actor, null, 1.0, count, 0, children, 0, note, path, line is > 0 ? line : null, null);

    /// <summary>超过 <see cref="MaxChildren"/> 的折成"还有 N 个"。</summary>
    internal static List<FlowNodeDto> Cap(List<FlowNodeDto> nodes, string actor)
    {
        if (nodes.Count <= MaxChildren) return nodes;
        var hidden = nodes.Count - MaxChildren;
        return [.. nodes.Take(MaxChildren), new FlowNodeDto(null, $"还有 {hidden} 个", "", "more", actor, null, 1.0, 1, 0, [], hidden, null)];
    }

    internal static FlowNodeDto Redepth(FlowNodeDto n, int depth, ref int count)
    {
        count++;
        var kids = new List<FlowNodeDto>(n.Children.Count);
        foreach (var c in n.Children) kids.Add(Redepth(c, depth + 1, ref count));
        return n with { Depth = depth, Children = kids };
    }

    internal static IEnumerable<FrameworkCall> Ordered(IReadOnlyList<FrameworkCall> calls) => OutlineBuilder.SortBySite(calls, c => c.Line, c => c.Pos, _ => 0);

    internal static IEnumerable<FrameworkCall> Flatten(IEnumerable<FrameworkCall> calls)
    {
        foreach (var c in calls)
        {
            yield return c;
            foreach (var x in Flatten(c.Children)) yield return x;
        }
    }

    /// <summary>这些命名空间里的外部调用（LINQ、集合、Task、字符串……）不是框架流程里的"关键调用"。</summary>
    private static readonly string[] NoiseExternals =
        ["external:System.Linq.", "external:System.Collections.", "external:System.Threading.Tasks.", "external:System.String.", "external:System.Object.",
         "external:System.Type.", "external:System.Guid.", "external:System.ArgumentNullException.", "external:System.ArgumentException.", "external:System.Math."];

    /// <summary>关键调用：去掉没解析到的、外部类型的构造（new List&lt;T&gt;）、LINQ / 集合 / Task 一类的外部调用、没有构造逻辑的 new（DTO / 异常）。</summary>
    internal static bool Keep(FrameworkCall c) =>
        c.Kind switch
        {
            "unresolved" => false,
            "external" => c.QualifiedName.Contains('(') && !NoiseExternals.Any(p => c.QualifiedName.StartsWith(p, StringComparison.Ordinal)),
            "new" => c.Expandable,
            _ => true,
        };

    internal static string? CtxNote(string? ctx) => ctx switch
    {
        CallContext.Cond => "条件分支里",
        CallContext.Loop => "循环里",
        CallContext.Catch => "catch 里（出错时）",
        CallContext.Callback => "回调 / lambda 里",
        _ => null,
    };

    private static bool IsEventBusName(string name) =>
        name.Contains("EventBus", StringComparison.Ordinal) || name.Contains("IntegrationEventService", StringComparison.Ordinal) || name.Contains("Outbox", StringComparison.Ordinal);

    /// <summary>限定名的"类型.成员"（去掉 external: 前缀、参数列表、泛型参数）：external:MediatR.RequestHandlerDelegate&lt;T&gt;.Invoke(CancellationToken) → (RequestHandlerDelegate, Invoke)。</summary>
    public static (string Type, string Member) OwnerAndMember(string qualifiedName)
    {
        var q = qualifiedName.StartsWith("external:", StringComparison.Ordinal) ? qualifiedName["external:".Length..] : qualifiedName;
        var paren = q.IndexOf('(');
        if (paren >= 0) q = q[..paren];
        q = MapBuilder.StripGenericArgs(q);
        var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => ("", ""), 1 => ("", parts[0]), _ => (parts[^2], parts[^1]) };
    }

    /// <summary>签名里委托类型的参数：类型以 Delegate 结尾，或是 Func / Action。返回（参数名，委托类型短名）。</summary>
    public static (HashSet<string> Names, HashSet<string> Types) DelegateParameters(string? signature)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (signature is null) return (names, types);
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open) return (names, types);
        foreach (var raw in SplitTopLevel(signature[(open + 1)..close]))
        {
            var p = raw.Trim();
            var eq = p.IndexOf('=');
            if (eq >= 0) p = p[..eq].Trim();
            var space = p.LastIndexOf(' ');
            if (space <= 0) continue;
            var name = p[(space + 1)..];
            var type = MapBuilder.LastSegment(MapBuilder.StripGenericArgs(p[..space].Trim().Split(' ')[^1]));
            if (type.EndsWith("Delegate", StringComparison.Ordinal) || type is "Func" or "Action")
            {
                names.Add(name);
                types.Add(type);
            }
        }
        return (names, types);
    }

    /// <summary>
    /// 这次调用是不是 next：目标是委托类型的 Invoke（Roslyn：external:MediatR.RequestHandlerDelegate&lt;T&gt;.Invoke()，
    /// 仓库里自定义的委托记成悬空边，名字同样是 类型.Invoke()），或调用名就是委托参数名（tree-sitter：next() / _next(context)）。
    /// </summary>
    public static bool IsNextCall(FrameworkCall c, IReadOnlySet<string> names, IReadOnlySet<string> types)
    {
        var (type, member) = OwnerAndMember(c.QualifiedName);
        if (member == "Invoke" && types.Contains(type)) return true;
        if (names.Contains(member) && (type.Length == 0 || c.Kind is "unresolved" or "external")) return true;
        var (_, labelMember) = OwnerAndMember(c.Label);
        return names.Contains(labelMember) && c.Kind is "unresolved" or "external";
    }

    /// <summary>源码里注册行为的地方：AddOpenBehavior(typeof(X&lt;,&gt;)) / AddBehavior&lt;I, X&gt;() / AddBehavior(typeof(I), typeof(X)) / AddTransient(typeof(I…Behavior&lt;,&gt;), typeof(X&lt;,&gt;))。返回（行为短名，位置）。</summary>
    public static List<(string Name, int Pos)> BehaviorRegistrationsInText(string text)
    {
        var list = new List<(string, int)>();
        foreach (Match m in AddOpenBehavior().Matches(text)) list.Add((MapBuilder.LastSegment(m.Groups["t"].Value), m.Index));
        foreach (Match m in AddBehaviorTypeOf().Matches(text)) list.Add((MapBuilder.LastSegment(m.Groups["t"].Value), m.Index));
        foreach (Match m in AddServiceBehavior().Matches(text)) list.Add((MapBuilder.LastSegment(m.Groups["t"].Value), m.Index));
        foreach (Match m in AddBehaviorGeneric().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchBracket(text, open, '<', '>');
            if (close < 0) continue;
            var args = SplitTopLevel(text[(open + 1)..close]);
            if (args.Count < 2) continue;
            list.Add((MapBuilder.LastSegment(MapBuilder.StripGenericArgs(args[^1].Trim())), m.Index));
        }
        return list.OrderBy(x => x.Item2).ToList();
    }

    /// <summary>
    /// receiver 开头的链式调用（app.UseA().UseB&lt;T&gt;(x)）：[start, end) 里每处 receiver（前面不是标识符或 '.'），依次读 .Name&lt;Generic&gt;(Args)，
    /// 遇到不带括号的成员（app.Environment.IsDevelopment()）就停。text 应先去掉注释与字符串。
    /// </summary>
    public static List<(string Name, string? Generic, string Args, int Pos)> ScanReceiver(string text, string receiver, int start, int end)
    {
        var result = new List<(string, string?, string, int)>();
        var rx = new Regex(@"(?<![\w.])" + Regex.Escape(receiver) + @"\s*\??\s*\.");
        end = Math.Min(end, text.Length);
        for (var m = rx.Match(text, start); m.Success && m.Index < end; m = m.NextMatch())
        {
            var i = m.Index + m.Length; // '.' 之后
            while (true)
            {
                i = SkipWs(text, i);
                var nameStart = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                if (i == nameStart) break;
                var name = text[nameStart..i];
                i = SkipWs(text, i);
                string? generic = null;
                if (i < text.Length && text[i] == '<')
                {
                    var close = MatchBracket(text, i, '<', '>');
                    if (close < 0) break;
                    generic = text[(i + 1)..close].Trim();
                    i = SkipWs(text, close + 1);
                }
                if (i >= text.Length || text[i] != '(') break;
                var closeParen = MatchBracket(text, i, '(', ')');
                if (closeParen < 0) break;
                result.Add((name, generic, text[(i + 1)..closeParen], nameStart));
                i = SkipWs(text, closeParen + 1);
                if (i < text.Length && text[i] == '?') i = SkipWs(text, i + 1);
                if (i >= text.Length || text[i] != '.') break;
                i++;
            }
        }
        return result;
    }

    /// <summary>
    /// text（已去掉注释与字符串）里 pos 处的语句是不是写在 if / else 里：所在花括号块前面是 if (...) / else，或者语句本身是不带花括号的 if (...) x;。
    /// 只看最近的一层（管道配置里 if (env.IsDevelopment()) { app.UseX(); } 这种）。
    /// </summary>
    internal static bool InsideIf(string text, int pos)
    {
        // 不带花括号：往回找语句开头（上一个 ; { }），看是不是 if (…) / else
        var stmt = pos - 1;
        while (stmt >= 0 && text[stmt] is not (';' or '{' or '}')) stmt--;
        var head = text[(stmt + 1)..Math.Min(pos, text.Length)].TrimStart();
        if (Regex.IsMatch(head, @"^(?:else\s+)?if\s*\(|^else\b")) return true;
        // 所在的花括号块
        var depth = 0;
        for (var i = pos - 1; i >= 0; i--)
        {
            if (text[i] == '}') depth++;
            else if (text[i] == '{' && depth-- == 0)
            {
                var j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
                if (j >= 3 && text[(j - 3)..(j + 1)] == "else" && (j < 4 || !char.IsLetterOrDigit(text[j - 4]))) return true;
                if (j < 0 || text[j] != ')') return false;
                var paren = 0;
                for (var k = j; k >= 0; k--)
                {
                    if (text[k] == ')') paren++;
                    else if (text[k] == '(' && --paren == 0)
                    {
                        var w = k - 1;
                        while (w >= 0 && char.IsWhiteSpace(text[w])) w--;
                        return w >= 1 && text[(w - 1)..(w + 1)] == "if" && (w < 2 || !char.IsLetterOrDigit(text[w - 2]));
                    }
                }
                return false;
            }
        }
        return false;
    }

    internal static int SkipWs(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    /// <summary>text[open] 是开括号，返回配对的闭括号位置；没配上返回 -1。'&lt;' 配对时遇到 ';' '{' '}' 放弃（那是小于号）。</summary>
    internal static int MatchBracket(string text, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == o) depth++;
            else if (text[i] == c && --depth == 0) return i;
            else if (o == '<' && text[i] is ';' or '{' or '}' or '(' or ')') return -1;
        }
        return -1;
    }

    /// <summary>类型限定名末尾的泛型参数个数（Ns.IdentifiedCommand&lt;T, R&gt; → 2；不是泛型 → 0）。</summary>
    public static int GenericArity(string qualifiedName)
    {
        if (!qualifiedName.EndsWith('>')) return 0;
        var depth = 0;
        for (var i = qualifiedName.Length - 1; i >= 0; i--)
        {
            if (qualifiedName[i] == '>') depth++;
            else if (qualifiedName[i] == '<' && --depth == 0) return SplitTopLevel(qualifiedName[(i + 1)..^1]).Count;
        }
        return 0;
    }

    /// <summary>按顶层逗号切（尖括号、圆括号、方括号里的逗号不切）。</summary>
    internal static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '<' or '(' or '[': depth++; break;
                case '>' or ')' or ']': depth--; break;
                case ',' when depth == 0: parts.Add(s[start..i]); start = i + 1; break;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>第 startLine..endLine 行（1 起，含）在 text 里的 [起，止) 偏移。</summary>
    internal static (int Start, int End) LineSpan(string text, int startLine, int endLine)
    {
        int Offset(int line)
        {
            if (line <= 1) return 0;
            var n = 1;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n' && ++n == line) return i + 1;
            return text.Length;
        }
        var s = Offset(startLine);
        var e = endLine <= 0 ? text.Length : Offset(endLine + 1);
        return (s, Math.Max(s, e));
    }

    /// <summary>注释与字符串 / 字符字面量换成空格（换行保留，偏移与行号不变）：免得注释里的 app.UseXxx、字符串里的括号干扰扫描。</summary>
    public static string StripCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        var i = 0;
        void Blank(int from, int to) { for (var k = from; k < to && k < chars.Length; k++) if (chars[k] is not ('\n' or '\r')) chars[k] = ' '; }
        while (i < chars.Length)
        {
            var ch = chars[i];
            if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(i, e); i = e; continue;
            }
            if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 2;
                Blank(i, e); i = e; continue;
            }
            if (ch == '"' && i + 2 < chars.Length && chars[i + 1] == '"' && chars[i + 2] == '"')
            {
                var e = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 3;
                Blank(i + 1, e - 1); i = e; continue;
            }
            if (ch == '"')
            {
                var verbatim = i > 0 && (chars[i - 1] == '@' || (chars[i - 1] == '$' && i > 1 && chars[i - 2] == '@'));
                var j = i + 1;
                while (j < chars.Length)
                {
                    if (verbatim && text[j] == '"' && j + 1 < text.Length && text[j + 1] == '"') { j += 2; continue; }
                    if (!verbatim && text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '"') break;
                    if (!verbatim && text[j] == '\n') break;
                    j++;
                }
                Blank(i + 1, j); i = j + 1; continue;
            }
            if (ch == '\'' )
            {
                var j = i + 1;
                while (j < chars.Length && text[j] != '\'' && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                Blank(i + 1, j); i = j + 1; continue;
            }
            i++;
        }
        return new string(chars);
    }

    /// <summary>与旧管道视图同一套模块层：消息命名空间的公共前缀，某一子层占 ≥ 90% 且下面还有更深的就继续往下。</summary>
    private static string AutoRoot(string start, List<string> keys)
    {
        var root = start;
        while (true)
        {
            var children = new Dictionary<string, int>(StringComparer.Ordinal);
            var total = 0;
            foreach (var k in keys)
            {
                var child = k == root ? root : MapBuilder.ChildKey(root, k);
                if (child is null) continue;
                total++;
                children[child] = children.GetValueOrDefault(child) + 1;
            }
            if (total == 0) return root;
            var (big, n) = children.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            if (big == root || n < total * 0.9) return root;
            if (!keys.Any(k => MapBuilder.ChildKey(big, k) is not null)) return root;
            root = big;
        }
    }

    private static int Compare((string Path, int Line) a, (string Path, int Line) b)
    {
        var c = string.CompareOrdinal(a.Path, b.Path);
        return c != 0 ? c : a.Line.CompareTo(b.Line);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> dict, TKey key, TValue value) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = [];
        list.Add(value);
    }

    internal static bool Under(string rel, string dir) => Core.Architecture.Rules.RuleContext.Under(rel, dir);
}
