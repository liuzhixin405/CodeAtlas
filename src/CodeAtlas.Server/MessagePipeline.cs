using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>管道里对某条消息生效的一步：行为（框架流程行为链里的那一行）+ 按消息类型注入的组件（验证器、Enricher……）。</summary>
/// <param name="Behavior">框架流程里这个行为的节点（Handle 方法、注册位置的 Note）。</param>
/// <param name="Components">按这条消息找到的实现（<c>LogClientExportCommandValidator : AbstractValidator&lt;LogClientExportCommand&gt;</c>）：源码里没有直接引用，靠 DI 扫描 / 反射接上。</param>
/// <param name="Post">行为在 next() 之后的调用（框架流程收尾列里 Note 为"XxxBehavior 后置"的那些行），请求流程里接在 Handler 之后。</param>
public sealed record PipelineStep(FlowNodeDto Behavior, string Name, IReadOnlyList<PipelineComponent> Components, IReadOnlyList<FlowNodeDto> Post);

/// <param name="Base">它实现 / 继承的那个泛型（AbstractValidator / IRequestEnricher）；顺着仓库里的泛型基类链（XxxValidatorBase&lt;M&gt; : AbstractValidator&lt;M&gt;）找到的写最终那个。</param>
/// <param name="Dependency">行为里按消息解析的依赖（IValidator&lt;TRequest&gt; / IRequestEnricher&lt;TRequest&gt;）。</param>
/// <param name="Via">依赖不是行为自己解析、而是它调用的服务里按消息类型反射解析的（<c>typeof(IEntityStateFetcher&lt;&gt;).MakeGenericType(request.GetType())</c>）：那个服务接口名。</param>
public sealed record PipelineComponent(MapSymbol Type, string Base, string Dependency, string? Via = null);

/// <summary>
/// 一个容器的消息管道（.NET 的行为链，取自框架流程），回答"这条消息经过了哪几步"：
/// <list type="number">
/// <item>行为按注册顺序（与框架流程同一份：DI / AddOpenBehavior 的位置）。</item>
/// <item>行为对这条消息生不生效：泛型约束 <c>where TRequest : IX</c>，以及 Handle 开头的守卫
/// <c>if (request is not IX …) return await next()</c>——消息（含基类）没实现 IX 就跳过，跳过的原因写进流程的 Note。</item>
/// <item>行为里按消息类型解析的依赖（构造注入 <c>IEnumerable&lt;IValidator&lt;TRequest&gt;&gt;</c>、<c>GetService(typeof(IRequestEnricher&lt;TRequest&gt;))</c>）
/// → 仓库里声明为 <c>: IValidator&lt;M&gt;</c> / <c>: AbstractValidator&lt;M&gt;</c>（基类名以依赖名去掉 I 结尾也算）/ <c>: IRequestEnricher&lt;M&gt;</c> 的类。
/// 这些类型多由 AddValidatorsFromAssembly、程序集扫描注册，源码里找不到对它们的调用，只有按消息类型才连得上。</item>
/// </list>
/// 注入点（<see cref="Inject"/>）：流程里每个 dispatch:M 节点（目标是命令 / 查询 Handler，事件 Handler 不走行为链）前面插入生效的各步。
/// </summary>
public sealed partial class MessagePipeline
{
    /// <param name="Reflective">经调用的服务反射解析的依赖 → 服务接口名（IEntityStateFetcher → IAuditStateProvider）。</param>
    private sealed record BehaviorInfo(FlowNodeDto Node, string Name, IReadOnlyList<string> Requires, IReadOnlyList<string> Dependencies,
        IReadOnlyDictionary<string, string> Reflective, IReadOnlyList<FlowNodeDto> Post);

    private readonly List<BehaviorInfo> _behaviors;
    private readonly Dictionary<string, List<(MapSymbol Type, string Base)>> _componentsByMessage;
    private readonly Dictionary<string, MapSymbol> _messages;
    private readonly Func<long, IReadOnlySet<string>> _interfacesOf;
    private readonly Func<long, bool> _isEventHandler;

    private MessagePipeline(List<BehaviorInfo> behaviors, Dictionary<string, List<(MapSymbol, string)>> components, Dictionary<string, MapSymbol> messages,
        Func<long, IReadOnlySet<string>> interfacesOf, Func<long, bool> isEventHandler)
    {
        _behaviors = behaviors;
        _componentsByMessage = components;
        _messages = messages;
        _interfacesOf = interfacesOf;
        _isEventHandler = isEventHandler;
    }

    public IReadOnlyList<string> BehaviorNames => _behaviors.Select(b => b.Name).ToList();

    private static readonly HashSet<string> NotDependencies = new(StringComparer.Ordinal)
    {
        "IPipelineBehavior", "IRequest", "IRequestHandler", "ICommand", "IQuery", "ICommandHandler", "IQueryHandler", "INotification", "INotificationHandler",
        "ILogger", "Task", "ValueTask", "IEnumerable", "IList", "List", "IReadOnlyList", "Func", "Action", "IEquatable", "IComparable",
    };

    [GeneratedRegex(@"\bwhere\s+(?<t>\w+)\s*:\s*(?<c>[^{\r\n]+)")]
    private static partial Regex WhereClause();

    /// <summary>从框架流程的行为链建管道；没有行为链（不是 .NET / 没认出行为）返回 null。readFile 读相对仓库根的源码。</summary>
    public static MessagePipeline? From(FrameworkFlowDto framework, IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, Func<string, string?> readFile)
    {
        var byId = new Dictionary<long, MapSymbol>(symbols.Count);
        foreach (var s in symbols) byId[s.Id] = s;
        // 行为链那一列的顶层节点：符号是行为类（找不到 Handle 时）或行为类的方法（阶段的 Symbols 是行为类，按注册顺序）
        var behaviorTypes = framework.Stages.FirstOrDefault(s => s.Key == FrameworkFlowBuilder.StageBehavior)?.Symbols.Select(s => s.Id).ToHashSet() ?? [];
        bool IsBehaviorNode(FlowNodeDto n) =>
            n.Actor == FrameworkFlowBuilder.StageBehavior && n.SymbolId is { } id
            && (behaviorTypes.Contains(id) || (byId.TryGetValue(id, out var m) && m.ParentId is { } p && behaviorTypes.Contains(p)));
        var behaviorNodes = new List<FlowNodeDto>();
        void Collect(FlowNodeDto n)
        {
            if (IsBehaviorNode(n)) behaviorNodes.Add(n);
            else foreach (var c in n.Children) Collect(c);
        }
        Collect(framework.Flow.Root);
        if (behaviorNodes.Count == 0) return null;
        // 收尾列里的行为后置（框架流程按洋葱倒序放好，Note 以"XxxBehavior 后置"开头）
        var tailNodes = new List<FlowNodeDto>();
        void CollectTail(FlowNodeDto n)
        {
            if (n.Actor == FrameworkFlowBuilder.StageTail && n.Note is { } note && note.Contains(" 后置", StringComparison.Ordinal)) { tailNodes.Add(n); return; }
            foreach (var c in n.Children) CollectTail(c);
        }
        CollectTail(framework.Flow.Root);

        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Text(string path) => texts.TryGetValue(path, out var t) ? t : texts[path] = readFile(path);
        string Lines(MapSymbol s, int extra = 0)
        {
            if (Text(s.Path) is not { } text) return "";
            var (start, end) = FrameworkFlowBuilder.LineSpan(text, s.StartLine, Math.Max(s.EndLine, s.StartLine + extra));
            return FrameworkFlowBuilder.StripCommentsAndStrings(text[start..end]);
        }

        // ---- 行为：守卫与按消息解析的依赖 ----
        var typeEdges = edges.Where(e => e.Kind is EdgeKind.Implements or EdgeKind.Inherits).ToLookup(e => e.FromId, e => e.ToId);
        var implementers = edges.Where(e => e.Kind == EdgeKind.Implements).ToLookup(e => e.ToId, e => e.FromId);
        var interfacesByName = symbols.Where(s => s.Kind == SymbolKind.Interface && !PipelineBuilder.IsTest(s.Path, s.QualifiedName))
            .GroupBy(s => ShortType(s.QualifiedName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var reflectiveCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // 服务接口的实现里按消息类型反射解析的泛型接口：typeof(IEntityStateFetcher<>).MakeGenericType(request.GetType())
        List<string> ReflectiveDependencies(string service)
        {
            if (reflectiveCache.TryGetValue(service, out var hit)) return hit;
            var found = new List<string>();
            foreach (var iface in interfacesByName.GetValueOrDefault(service) ?? [])
                foreach (var implId in implementers[iface.Id])
                    if (byId.TryGetValue(implId, out var impl) && !PipelineBuilder.IsTest(impl.Path, impl.QualifiedName))
                        foreach (Match m in MakeGenericTypeOf().Matches(Lines(impl)))
                            if (!found.Contains(m.Groups["n"].Value)) found.Add(m.Groups["n"].Value);
            return reflectiveCache[service] = found;
        }
        var behaviors = new List<BehaviorInfo>();
        foreach (var node in behaviorNodes.DistinctBy(n => n.SymbolId))
        {
            var method = byId.GetValueOrDefault(node.SymbolId!.Value);
            var type = method is null ? null : OutlineBuilder.IsType(method.Kind) ? method : method.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            if (type is null) continue;
            var typeParam = TypeParameters(type.QualifiedName).FirstOrDefault() ?? "TRequest";
            var typeText = Lines(type);
            var requires = new List<string>();
            var header = typeText.Length == 0 ? "" : typeText[..Math.Max(0, typeText.IndexOf('{') < 0 ? Math.Min(typeText.Length, 400) : typeText.IndexOf('{'))];
            foreach (Match w in WhereClause().Matches(header))
                if (w.Groups["t"].Value == typeParam)
                    requires.AddRange(FrameworkFlowBuilder.SplitTopLevel(Regex.Split(w.Groups["c"].Value, @"\bwhere\b")[0]).Select(c => ShortType(c.Trim()))
                        .Where(c => c.Length > 1 && c[0] == 'I' && char.IsUpper(c[1]) && c is not ("IRequest" or "IBaseRequest" or "ICommand" or "IQuery")));
            if (method is not null && !OutlineBuilder.IsType(method.Kind)) requires.AddRange(Guards(Lines(method)));
            var deps = Regex.Matches(typeText, @"\b(?<n>[A-Z]\w*)\s*<\s*" + Regex.Escape(typeParam) + @"\s*[,>]")
                .Select(m => m.Groups["n"].Value)
                .Where(n => !NotDependencies.Contains(n) && !n.EndsWith("Behavior", StringComparison.Ordinal) && !n.EndsWith("Delegate", StringComparison.Ordinal)
                            && n != MapBuilder.StripGenericArgs(type.Name))
                .Distinct(StringComparer.Ordinal).ToList();
            var reflective = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(typeText, @"\b(?<n>I[A-Z]\w*)\b"))
            {
                var service = m.Groups["n"].Value;
                if (NotDependencies.Contains(service) || deps.Contains(service) || reflective.ContainsValue(service) || !interfacesByName.ContainsKey(service)) continue;
                foreach (var dep in ReflectiveDependencies(service))
                    if (!deps.Contains(dep)) reflective.TryAdd(dep, service);
            }
            var name = MapBuilder.StripGenericArgs(type.Name);
            // 最后一个 next() 是 return await next()（全在 if 分支里，ApprovalBehavior 那种）：它之后的调用是"不放行"那条路（建审批任务后直接返回），不在 Handler 之后执行
            var post = method is not null && !OutlineBuilder.IsType(method.Kind) && LastNextReturns(Lines(method)) ? []
                : tailNodes.Where(n => n.Note!.StartsWith(name + " 后置", StringComparison.Ordinal)).ToList();
            behaviors.Add(new BehaviorInfo(node, name, requires.Distinct(StringComparer.Ordinal).ToList(), deps, reflective, post));
        }
        if (behaviors.Count == 0) return null;

        // ---- 消息与它实现的接口（含基类链，按短名）----
        var handlesTo = edges.Where(e => e.Kind == EdgeKind.Dispatches && e.Via == "handles").Select(e => e.ToId).ToHashSet();
        var messages = new Dictionary<string, MapSymbol>(StringComparer.Ordinal);
        foreach (var id in handlesTo)
            if (byId.TryGetValue(id, out var m) && m.Kind != SymbolKind.External) messages.TryAdd(MapBuilder.StripGenericArgs(m.Name), m);
        var ifaceCache = new Dictionary<long, IReadOnlySet<string>>();
        IReadOnlySet<string> InterfacesOf(long typeId)
        {
            if (ifaceCache.TryGetValue(typeId, out var hit)) return hit;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<long> { typeId };
            var frontier = new List<long> { typeId };
            for (var depth = 0; depth < 8 && frontier.Count > 0; depth++)
            {
                var next = new List<long>();
                foreach (var f in frontier)
                    foreach (var to in typeEdges[f])
                    {
                        if (!byId.TryGetValue(to, out var t)) continue;
                        names.Add(ShortType(t.QualifiedName));
                        if (seen.Add(to)) next.Add(to);
                    }
                frontier = next;
            }
            // 声明头里写的（tree-sitter 对泛型接口有时不出边）
            if (byId.TryGetValue(typeId, out var self))
            {
                var text = Lines(self, 3);
                var colon = BaseListStart(text);
                if (colon >= 0)
                {
                    var brace = text.IndexOf('{', colon);
                    var list = text[(colon + 1)..(brace < 0 ? text.Length : brace)];
                    list = Regex.Split(list, @"\bwhere\b")[0];
                    foreach (var part in FrameworkFlowBuilder.SplitTopLevel(list)) if (ShortType(part.Trim()) is { Length: > 0 } n) names.Add(n);
                }
            }
            return ifaceCache[typeId] = names;
        }

        // ---- 按消息类型的组件：声明头里 Base<M, …> 的类；Base 不直接匹配时顺着仓库里的泛型基类往上（XxxValidatorBase<TQuery> : AbstractValidator<TQuery>）----
        var components = new Dictionary<string, List<(MapSymbol, string)>>(StringComparer.Ordinal);
        var genericTargets = byId.Values.Where(s => s.QualifiedName.Contains('<')).Select(s => s.Id).ToHashSet();
        var allDeps = behaviors.SelectMany(b => b.Dependencies.Concat(b.Reflective.Keys)).ToHashSet(StringComparer.Ordinal);
        var genericClasses = symbols.Where(s => s.Kind == SymbolKind.Class && s.QualifiedName.EndsWith('>') && !PipelineBuilder.IsTest(s.Path, s.QualifiedName))
            .GroupBy(s => ShortType(s.QualifiedName), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<(string Name, List<string> Args)> BaseParts(MapSymbol cls)
        {
            var result = new List<(string, List<string>)>();
            var text = Lines(cls, 3);
            var colon = BaseListStart(text);
            if (colon < 0) return result;
            var list = Regex.Split(text[(colon + 1)..BaseListEnd(text, colon)], @"\bwhere\b")[0];
            foreach (var part in FrameworkFlowBuilder.SplitTopLevel(list))
            {
                var p = part.Trim();
                // 主构造函数的基类调用：Base<A, B>(x, new Y())
                if (p.EndsWith(')') && p.IndexOf('(') is var open and > 0 && FrameworkFlowBuilder.MatchBracket(p, open, '(', ')') == p.Length - 1) p = p[..open].TrimEnd();
                var lt = p.IndexOf('<');
                if (lt <= 0 || !p.EndsWith('>')) continue;
                result.Add((ShortType(p[..lt]), FrameworkFlowBuilder.SplitTopLevel(p[(lt + 1)..^1]).Select(a => a.Trim()).ToList()));
            }
            return result;
        }
        // (最终匹配到依赖的基类名, 消息实参)：args 是 baseName 这一层的类型实参
        (string Base, string Message)? Resolve(string baseName, List<string> args, int depth)
        {
            if (allDeps.Any(d => Matches(d, baseName))) return args.Count > 0 && ShortType(args[0]) is { Length: > 0 } msg ? (baseName, msg) : null;
            if (depth >= 4 || !genericClasses.TryGetValue(baseName, out var baseClass)) return null;
            var parameters = TypeParameters(baseClass.QualifiedName);
            foreach (var (name, partArgs) in BaseParts(baseClass))
            {
                var mapped = partArgs.Select(a => parameters.IndexOf(a) is var i and >= 0 && i < args.Count ? args[i] : a).ToList();
                if (Resolve(name, mapped, depth + 1) is { } hit) return hit;
            }
            return null;
        }
        if (allDeps.Count > 0)
            foreach (var cls in symbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Struct && typeEdges[s.Id].Any(genericTargets.Contains) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)))
                foreach (var (baseName, args) in BaseParts(cls))
                {
                    if (Resolve(baseName, args, 0) is not { } hit) continue;
                    if (!components.TryGetValue(hit.Message, out var bucket)) components[hit.Message] = bucket = [];
                    bucket.Add((cls, hit.Base));
                }

        bool IsEventHandler(long handlerMethodId)
        {
            if (!byId.TryGetValue(handlerMethodId, out var m)) return false;
            var type = OutlineBuilder.IsType(m.Kind) ? m : m.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            if (type is null) return false;
            var names = InterfacesOf(type.Id);
            var handlerIfaces = names.Where(n => n.EndsWith("Handler", StringComparison.Ordinal)).ToList();
            return handlerIfaces.Count > 0 && handlerIfaces.All(n => n.Contains("Event", StringComparison.Ordinal) || n.Contains("Notification", StringComparison.Ordinal) || n.Contains("Consumer", StringComparison.Ordinal));
        }

        return new MessagePipeline(behaviors, components, messages, InterfacesOf, IsEventHandler);
    }

    /// <summary>依赖 IValidator 认 IValidator&lt;M&gt; 与 AbstractValidator&lt;M&gt;（基类名以去掉 I 的依赖名结尾）；非接口依赖只认同名。</summary>
    private static bool Matches(string dependency, string baseName) =>
        baseName == dependency || (dependency.Length > 2 && dependency[0] == 'I' && char.IsUpper(dependency[1]) && baseName.EndsWith(dependency[1..], StringComparison.Ordinal));

    /// <summary>
    /// 这条消息经过哪几步：(生效的步骤, 跳过的行为与原因)。message 是消息短名（dispatch:M 的 M）。
    /// </summary>
    public (IReadOnlyList<PipelineStep> Steps, IReadOnlyList<string> Skipped) For(string message)
    {
        var ifaces = _messages.TryGetValue(message, out var m) ? _interfacesOf(m.Id) : null;
        var steps = new List<PipelineStep>();
        var skipped = new List<string>();
        foreach (var b in _behaviors)
        {
            // 消息不在符号表里（外部类型）时不判断守卫，按生效画
            var missing = ifaces is null ? [] : b.Requires.Where(r => !ifaces.Contains(r)).ToList();
            if (missing.Count > 0) { skipped.Add($"{b.Name}（{message} 没实现 {string.Join(" / ", missing)}）"); continue; }
            var comps = new List<PipelineComponent>();
            if (_componentsByMessage.TryGetValue(message, out var list))
                foreach (var (type, baseName) in list)
                {
                    if (b.Dependencies.FirstOrDefault(d => Matches(d, baseName)) is { } dep) comps.Add(new PipelineComponent(type, baseName, dep));
                    else if (b.Reflective.FirstOrDefault(r => Matches(r.Key, baseName)) is { Key: { } rdep, Value: var service }) comps.Add(new PipelineComponent(type, baseName, rdep, service));
                }
            steps.Add(new PipelineStep(b.Node, b.Name, comps.DistinctBy(c => c.Type.Id).ToList(), b.Post));
        }
        return (steps, skipped);
    }

    /// <summary>
    /// 在流程里每个 dispatch:M 节点（命令 / 查询 Handler）前面插入这条消息生效的管道步骤（深度与 dispatch 节点相同，组件挂在行为下面一层），
    /// 后面按洋葱倒序接上各行为 next() 之后的调用（Note "XxxBehavior 后置"）。
    /// FlowDto.Behaviors 换成按注册顺序、实际生效的行为，跳过的写进 Note；流程里一个分派都没有（入口直接调服务）时清空——那些行为根本不经过。
    /// pipelineFor 按 dispatch 节点所在文件给出管道（没有就不动）。
    /// </summary>
    public static FlowDto Inject(FlowDto flow, Func<FlowNodeDto, MessagePipeline?> pipelineFor)
    {
        var applied = new List<string>();
        var skippedAll = new List<string>();
        var added = 0;
        var sawDispatch = false;
        var sawPipeline = false;
        FlowNodeDto Visit(FlowNodeDto node)
        {
            var kids = new List<FlowNodeDto>(node.Children.Count);
            foreach (var c in node.Children)
            {
                var posts = new List<FlowNodeDto>();
                if (c.Kind == "dispatch" && c.Via is { } via && via.StartsWith("dispatch:", StringComparison.Ordinal)) sawDispatch = true;
                if (c.Kind == "dispatch" && c.Via is { } v && v.StartsWith("dispatch:", StringComparison.Ordinal) && c.SymbolId is { } hid && pipelineFor(c) is { } pipe)
                {
                    sawPipeline = true;
                    if (!pipe._isEventHandler(hid))
                    {
                        var message = MapBuilder.StripGenericArgs(v["dispatch:".Length..]);
                        var (steps, skipped) = pipe.For(message);
                        for (var i = 0; i < steps.Count; i++)
                        {
                            var s = steps[i];
                            // 行为自己在 next() 之前的关键调用（框架流程里那几行）；组件挂在调用依赖的那一行下面（IValidator.ValidateAsync → XxxValidator），没有这一行就挂在行为下
                            var own = s.Behavior.Children.Select(k => Rehome(k, c.Actor)).ToList();
                            var loose = new List<FlowNodeDto>();
                            foreach (var x in s.Components)
                            {
                                var how = x.Via is null
                                    ? $"对应 {x.Dependency}<…>；源码里没有直接调用，由 DI 扫描 / 反射接上"
                                    : $"{x.Via} 里按消息类型反射解析 {x.Dependency}<…>（MakeGenericType）；源码里没有直接调用";
                                var comp = new FlowNodeDto(x.Type.Id, MapBuilder.StripGenericArgs(x.Type.Name), x.Type.QualifiedName, "call", c.Actor, null, 1.0, 1, 0, [], 0,
                                    $"按消息类型注入：{x.Base}<{message}>（{how}）", x.Type.Path, x.Type.StartLine);
                                var at = own.FindIndex(k => k.Label.StartsWith((x.Via ?? x.Dependency) + ".", StringComparison.Ordinal));
                                if (at >= 0) own[at] = own[at] with { Children = [.. own[at].Children, comp] };
                                else loose.Add(comp);
                            }
                            var note = $"管道 {i + 1}/{steps.Count} · {s.Behavior.Note}";
                            var count = 0;
                            kids.Add(FrameworkFlowBuilder.Redepth(new FlowNodeDto(s.Behavior.SymbolId, s.Behavior.Label, s.Behavior.QualifiedName, "call", c.Actor, null, 1.0, 1, 0,
                                [.. own, .. loose], 0, note, s.Behavior.Path, s.Behavior.Line), c.Depth, ref count));
                            added += count;
                            if (!applied.Contains(s.Name)) applied.Add(s.Name);
                        }
                        // 后置：Handler 返回之后洋葱往回走，最里层的行为先执行
                        for (var i = steps.Count - 1; i >= 0; i--)
                            foreach (var p in steps[i].Post)
                            {
                                var count = 0;
                                posts.Add(FrameworkFlowBuilder.Redepth(Rehome(p, c.Actor), c.Depth, ref count));
                                added += count;
                            }
                        foreach (var sk in skipped) if (!skippedAll.Contains(sk)) skippedAll.Add(sk);
                    }
                }
                kids.Add(Visit(c));
                kids.AddRange(posts);
            }
            return node with { Children = kids };
        }
        var root = Visit(flow.Root);
        if (added == 0)
        {
            // 没有分派（入口直接调服务）、或分派到的都是不走行为链的事件 Handler：流程头不该再列"管道行为"
            if ((!sawDispatch || sawPipeline) && flow.Behaviors.Count > 0) return flow with { Behaviors = [] };
            return flow;
        }
        var skipNote = skippedAll.Count == 0 ? null : "管道里跳过：" + string.Join("；", skippedAll);
        var note = flow.Note is null ? skipNote : skipNote is null ? flow.Note : flow.Note + "\n" + skipNote;
        return flow with { Root = root, Behaviors = applied, Nodes = flow.Nodes + added, Note = note };
    }

    /// <summary>按 dispatch 目标所在的 .NET 容器挑管道（流程自己的容器优先）再 <see cref="Inject"/>；pipelineOf 按容器键给管道（可缓存）。</summary>
    public static FlowDto InjectByContainer(FlowDto flow, IReadOnlyList<CodeAtlas.Core.Architecture.ArchNode> containers, Func<string, MessagePipeline?> pipelineOf)
    {
        var dotnet = containers.Where(c => c.SubKind.StartsWith("dotnet", StringComparison.Ordinal)).ToList();
        if (dotnet.Count == 0) return flow;
        return Inject(flow, node =>
        {
            if (node.Path is not { } path) return null;
            var owners = dotnet.Where(c => c.Members.Any(m => CodeAtlas.Core.Architecture.Rules.RuleContext.Under(path, m))).ToList();
            if (owners.Count == 0) return null;
            return pipelineOf((owners.FirstOrDefault(o => o.Key == flow.ContainerKey) ?? owners[0]).Key);
        });
    }

    /// <summary>CLI 用：现算各容器的框架流程（<see cref="FrameworkFlowQuery"/>）取管道并注入。</summary>
    public static FlowDto InjectFromDb(CodeAtlas.Core.Storage.IndexDatabase db, string repoRoot, FlowDto flow, IReadOnlyList<CodeAtlas.Core.Architecture.ArchNode> containers,
        IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges)
    {
        var cache = new Dictionary<string, MessagePipeline?>(StringComparer.Ordinal);
        string? Read(string rel)
        {
            try { var full = Path.GetFullPath(Path.Combine(repoRoot, rel)); return File.Exists(full) ? File.ReadAllText(full) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
        return InjectByContainer(flow, containers, key =>
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            var fw = FrameworkFlowQuery.Build(db, repoRoot, key, symbols, edges);
            return cache[key] = fw is null ? null : From(fw, symbols, edges, Read);
        });
    }

    // ---------- 小工具 ----------

    /// <summary>框架流程里的节点搬进请求流程：列（Actor）换成 dispatch 节点所在的那一列。</summary>
    private static FlowNodeDto Rehome(FlowNodeDto n, string actor) => n with { Actor = actor, Children = n.Children.Select(c => Rehome(c, actor)).ToList() };

    /// <summary>Ns.Foo&lt;T&gt; / external:Ns.Foo&lt;T&gt;.Bar(…) 里的类型短名（去命名空间、泛型参数）。</summary>
    internal static string ShortType(string s)
    {
        var t = s.StartsWith("external:", StringComparison.Ordinal) ? s["external:".Length..] : s;
        var paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren];
        t = MapBuilder.StripGenericArgs(t).Trim();
        var dot = t.LastIndexOf('.');
        return (dot >= 0 ? t[(dot + 1)..] : t).Trim('?', ' ');
    }

    /// <summary>限定名最后一段的泛型参数名（ValidationBehavior&lt;TRequest, TResponse&gt; → TRequest, TResponse）。</summary>
    private static List<string> TypeParameters(string qualifiedName)
    {
        if (!qualifiedName.EndsWith('>')) return [];
        var depth = 0;
        for (var i = qualifiedName.Length - 1; i >= 0; i--)
        {
            if (qualifiedName[i] == '>') depth++;
            else if (qualifiedName[i] == '<' && --depth == 0) return FrameworkFlowBuilder.SplitTopLevel(qualifiedName[(i + 1)..^1]).Select(x => x.Trim()).ToList();
        }
        return [];
    }

    /// <summary>基类列表的结尾：类型体的 { 或没有类型体的声明末尾的 ;（class X(…) : Base&lt;M&gt;(…);），跳过括号里的。</summary>
    private static int BaseListEnd(string text, int colon)
    {
        var depth = 0;
        for (var i = colon + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch is '{' or ';' && depth == 0) return i;
        }
        return text.Length;
    }

    /// <summary>类型声明头里基类列表的冒号（跳过泛型参数与主构造函数参数里的）；没有返回 -1。</summary>
    private static int BaseListStart(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            else if (ch == '{' && depth == 0) return -1;
            else if (ch == ':' && depth == 0 && (i + 1 >= text.Length || text[i + 1] != ':')) return i;
        }
        return -1;
    }

    [GeneratedRegex(@"\bif\s*\(")]
    private static partial Regex IfStart();

    /// <summary>方法体里最后一个 next(…) 调用是不是 <c>return [await] next(…)</c> 这条语句本身（语句从上一个 ; { } 算起）。</summary>
    public static bool LastNextReturns(string methodText)
    {
        var calls = Regex.Matches(methodText, @"(?<![\w.])next\s*\(");
        if (calls.Count == 0) return false;
        var at = calls[^1].Index;
        var start = methodText.LastIndexOfAny([';', '{', '}'], Math.Max(0, at - 1)) + 1;
        return Regex.IsMatch(methodText[start..at], @"\breturn\s+(await\s+)?$");
    }

    /// <summary>typeof(IEntityStateFetcher&lt;&gt;).MakeGenericType(…)：按运行时类型闭合开放泛型接口再解析。</summary>
    [GeneratedRegex(@"\btypeof\s*\(\s*(?:[\w.]+\.)?(?<n>I[A-Z]\w*)\s*<[\s,]*>\s*\)\s*\.\s*MakeGenericType\s*\(")]
    private static partial Regex MakeGenericTypeOf();

    /// <summary>
    /// Handle 开头的守卫：<c>if (request is not IX x || request is not IY y) return await next(…);</c>（条件里的每个 <c>is not I…</c> 都算前提）。
    /// 只认 if 体（到下一个 ; 为止，或 {…} 块）里直接 return 了 next 的那种。
    /// </summary>
    internal static List<string> Guards(string methodText)
    {
        var open = methodText.IndexOf('(');
        if (open < 0) return [];
        var close = FrameworkFlowBuilder.MatchBracket(methodText, open, '(', ')');
        if (close < 0) return [];
        var firstParam = FrameworkFlowBuilder.SplitTopLevel(methodText[(open + 1)..close]).FirstOrDefault()?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (firstParam is null) return [];
        var result = new List<string>();
        foreach (Match m in IfStart().Matches(methodText, close))
        {
            var condOpen = m.Index + m.Length - 1;
            var condClose = FrameworkFlowBuilder.MatchBracket(methodText, condOpen, '(', ')');
            if (condClose < 0) continue;
            var cond = methodText[(condOpen + 1)..condClose];
            var names = Regex.Matches(cond, @"\b" + Regex.Escape(firstParam) + @"\s+is\s+not\s+(?<t>I[A-Z][\w.]*)").Select(x => ShortType(x.Groups["t"].Value)).ToList();
            // && 连着几个 is not（实现其中一个就生效）不当前提；|| 连着的是每个都要实现
            if (names.Count == 0 || (names.Count > 1 && cond.Contains("&&", StringComparison.Ordinal))) continue;
            var rest = methodText[(condClose + 1)..].TrimStart();
            var body = rest.StartsWith('{') ? rest[..(Math.Max(0, FrameworkFlowBuilder.MatchBracket(rest, 0, '{', '}')) + 1)] : rest[..Math.Max(0, rest.IndexOf(';') + 1)];
            // return await next(…); 或代码块 { var r = await next(); …; return r; }
            if (Regex.IsMatch(body, @"\breturn\b[^;]*\bnext\s*\(") || (body.StartsWith('{') && Regex.IsMatch(body, @"\bnext\s*\(") && Regex.IsMatch(body, @"\breturn\b")))
                result.AddRange(names);
        }
        return result;
    }
}
