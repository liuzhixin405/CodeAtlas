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
    private static readonly HashSet<string> HostedBases = new(StringComparer.Ordinal) { "BackgroundService" };
    private static readonly HashSet<string> HostedInterfaces = new(StringComparer.Ordinal) { "IHostedService", "IHostedLifecycleService" };
    private static readonly HashSet<string> JobInterfaces = new(StringComparer.Ordinal) { "IJob" };
    private static readonly HashSet<string> HubBases = new(StringComparer.Ordinal) { "Hub" };
    private static readonly HashSet<string> ConsumerInterfaces = new(StringComparer.Ordinal) { "IConsumer" };

    // app.MapGrpcService<GreeterService>()
    [GeneratedRegex(@"\.MapGrpcService\s*<\s*(?:[\w.]+\.)?(\w+)\s*>")]
    private static partial Regex MapGrpc();
    // RecurringJob.AddOrUpdate<T>("id", x => x.Method(...), …) / BackgroundJob.Enqueue<T>(x => x.Method(...)) / Schedule<T> / ContinueJobWith<T>
    [GeneratedRegex(@"\b(?:RecurringJob|BackgroundJob)\s*\.\s*(?:AddOrUpdate|Enqueue|Schedule|ContinueJobWith)\s*<\s*(?:[\w.]+\.)?(\w+)\s*>\s*\([^;]*?\b\w+\s*=>\s*\w+\s*\.\s*(\w+)\s*\(")]
    private static partial Regex HangfireTyped();
    // RecurringJob.AddOrUpdate("id", () => Static.Method(...), …)
    [GeneratedRegex(@"\b(?:RecurringJob|BackgroundJob)\s*\.\s*(?:AddOrUpdate|Enqueue|Schedule|ContinueJobWith)\s*\([^;]*?\(\s*\)\s*=>\s*(?:[\w.]+\.)?(\w+)\s*\.\s*(\w+)\s*\(")]
    private static partial Regex HangfireStatic();

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

    public static List<EntryPoint> Discover(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, IReadOnlyList<ArchNode> containers, Func<string, string[]> readLines)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var children = symbols.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
        var inherits = edges.Where(e => e.Kind == EdgeKind.Inherits).ToLookup(e => e.FromId, e => e.ToId);
        var implements = edges.Where(e => e.Kind == EdgeKind.Implements).ToLookup(e => e.FromId, e => e.ToId);
        var result = new List<EntryPoint>();
        var seen = new HashSet<(long, string)>();

        void Add(EntryPoint e) { if (seen.Add((e.SymbolId, e.ContainerKey))) result.Add(e); }

        IReadOnlyList<ArchNode> ContainersOf(string path) =>
            containers.Where(c => c.Members.Any(m => RuleContext.Under(path, m))).ToList();

        // 继承链上的名字（最多 4 层）与直接实现的接口名
        IEnumerable<string> BaseNames(long typeId)
        {
            var cur = typeId;
            for (var depth = 0; depth < 4; depth++)
            {
                var bases = inherits[cur].ToList();
                if (bases.Count == 0) yield break;
                var next = bases[0];
                if (!byId.TryGetValue(next, out var b)) yield break;
                yield return b.Name;
                if (b.Kind == SymbolKind.External) yield break;
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

        foreach (var type in symbols.Where(s => s.Kind is SymbolKind.Class && s.Kind != SymbolKind.External))
        {
            if (PipelineBuilder.IsTest(type.Path, type.QualifiedName)) continue;
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
                    var route = CombineRoute(classRoute, methodRoute, type.Name, m.Name);
                    foreach (var c in owners) Add(new EntryPoint(m.Id, c.Key, "aspnet.controller", "http", route, http?.Groups[1].Value.ToUpperInvariant(), m.Path, m.StartLine));
                }
                continue;
            }
            if (bases.Any(HubBases.Contains))
            {
                foreach (var m in methods.Where(m => m.Visibility is null or "public"))
                    foreach (var c in owners) Add(new EntryPoint(m.Id, c.Key, "aspnet.signalr", "hub", type.Name + "." + m.Name, null, m.Path, m.StartLine));
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
                var entry = methods.FirstOrDefault(m => m.Name is "ExecuteAsync" or "StartAsync") ?? methods.FirstOrDefault();
                if (entry is not null) foreach (var c in owners) Add(new EntryPoint(entry.Id, c.Key, "hosted.background", "hosted", type.Name, null, entry.Path, entry.StartLine));
                continue;
            }
            if (ifaces.Any(JobInterfaces.Contains))
            {
                var entry = methods.FirstOrDefault(m => m.Name == "Execute") ?? methods.FirstOrDefault();
                if (entry is not null) foreach (var c in owners) Add(new EntryPoint(entry.Id, c.Key, "quartz.job", "job", type.Name, null, entry.Path, entry.StartLine));
            }
        }

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

        // Minimal API：文件里的 MapGet("…") 字面量 → 包含该行的最内层方法 / 函数
        foreach (var file in csFiles)
        {
            var owners = ContainersOf(file);
            if (owners.Count == 0) continue;
            var lines = readLines(file);
            if (!Array.Exists(lines, l => l.Contains(".Map", StringComparison.Ordinal))) continue;
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
            for (var i = 0; i < lines.Length; i++)
            {
                var m = MinimalApi().Match(lines[i]);
                if (!m.Success || RuleContext.InCommentOrString(lines[i], m.Index)) continue;
                var owner = inFile.Where(s => s.StartLine <= i + 1 && i + 1 <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault();
                if (owner is null) continue;
                if (m.Groups[3].Success && !m.Groups[3].Value.Equals("async", StringComparison.Ordinal))
                {
                    var name = m.Groups[3].Value;
                    var handler = inFile.FirstOrDefault(s => s.Name == name && s.ParentId == owner.ParentId) ?? inFile.FirstOrDefault(s => s.Name == name);
                    if (handler is not null) owner = handler;
                }
                var before = lines[i][..m.Index];
                var inline = InlineGroup().Matches(before); // app.MapGroup("/a").MapGroup("b").MapGet(...)
                var receiver = TrailingIdent().Match(inline.Count > 0 ? before[..inline[0].Index] : before);
                var prefix = (receiver.Success ? Prefix(receiver.Groups[1].Value) : "") + string.Concat(inline.Select(x => "/" + x.Groups[1].Value.Trim('/')));
                var route = "/" + (prefix.Trim('/') + "/" + m.Groups[2].Value.Trim('/')).Trim('/');
                foreach (var c in owners) result.Add(new EntryPoint(owner.Id, c.Key, "aspnet.minimal", "http", route, m.Groups[1].Value.ToUpperInvariant(), file, i + 1));
            }
        }

        // 前端：按文件路径约定
        foreach (var c in containers.Where(c => c.SubKind == "frontend"))
        {
            foreach (var module in symbols.Where(s => s.Kind == SymbolKind.Module && RuleContext.Under(s.Path, c.Path ?? "") && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)))
            {
                var page = NextPage().Match(module.Path);
                if (page.Success) { Add(new EntryPoint(module.Id, c.Key, "frontend.route", "page", NextRoute(page.Groups["path"].Value), null, module.Path, module.StartLine)); continue; }
                var api = NextApiRoute().Match(module.Path);
                if (api.Success) { Add(new EntryPoint(module.Id, c.Key, "frontend.route", "api-route", NextRoute(api.Groups["path"].Value), null, module.Path, module.StartLine)); continue; }
                var pages = PagesRouter().Match(module.Path);
                if (pages.Success && !pages.Groups["path"].Value.StartsWith('_'))
                    Add(new EntryPoint(module.Id, c.Key, "frontend.route", "page", NextRoute(pages.Groups["path"].Value.Replace("/index", "")), null, module.Path, module.StartLine));
            }
        }

        return result.OrderBy(e => e.ContainerKey, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.File, StringComparer.Ordinal).ThenBy(e => e.Line).ToList();
    }

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
        for (var i = declLine - 2; i >= 0 && i >= declLine - 13; i--)
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
