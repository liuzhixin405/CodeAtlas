using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// ASP.NET Core 终结点的补充规则：
/// <list type="bullet">
/// <item>Minimal API 的 lambda 处理器：落在 lambda 里调的那个仓库方法上（先认"注入参数.方法()"，表达式体再退到第一个能解析的调用），
/// 都没有才留在外层方法上——这时流程按入口那一行到下一条路由之前的行范围取根节点的调用（<see cref="FlowEdges"/>），不把同一方法里的别的路由混进来；</item>
/// <item><c>MapHealthChecks("/x")</c>、<c>MapForwarder("/x", …)</c> 这类框架终结点（处理代码不在仓库里，入口留在映射所在方法、流程只取那一行）；</item>
/// <item>MVC 约定路由（<c>MapDefaultControllerRoute()</c> / <c>MapControllerRoute(pattern: …)</c>）下没有特性路由的动作：<c>/{Controller}/{Action}</c>；</item>
/// <item>Blazor <c>@page "/x"</c>：.razor 文件进了索引（有模块符号）时按文件文本认，落在组件类或文件模块上。</item>
/// </list>
/// </summary>
public static partial class EntryDiscovery
{
    // app.MapHealthChecks("/healthz/live", …)   app.MapForwarder("/product-images/{id}", "https+http://catalog-api", …)
    [GeneratedRegex(@"\.Map(HealthChecks|Forwarder)\s*\(\s*""([^""]+)""")]
    private static partial Regex FrameworkEndpoint();
    [GeneratedRegex(@"\bMapDefaultControllerRoute\s*\(|\bMapControllerRoute\s*\(")]
    private static partial Regex ConventionalRouting();
    // MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}")
    [GeneratedRegex(@"\bMapControllerRoute\s*\([^;]*?pattern\s*:\s*""([^""]+)""")]
    private static partial Regex ConventionalPattern();
    [GeneratedRegex(@"^﻿?\s*@page\s+""([^""]*)""")]
    private static partial Regex RazorPage();
    // 注入参数上的调用：service.DoAsync(  repo.Get<T>(
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(\w+)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex MemberCall();

    /// <summary>流程按行范围截取根节点调用的规则：入口落在包着处理器的外层方法上。</summary>
    private static readonly HashSet<string> WindowedRules = new(StringComparer.Ordinal) { "aspnet.minimal", "aspnet.healthchecks", "aspnet.forwarder" };

    /// <summary>
    /// 按 (符号, 路由, 方法) 取入口。没给路由 / 方法时取这个符号的第一条（入口表的顺序）；给了就要对上，对不上返回 null。
    /// </summary>
    public static EntryPoint? PickEntry(IReadOnlyList<EntryPoint> entries, long symbolId, string? route = null, string? httpMethod = null) =>
        entries.FirstOrDefault(e => e.SymbolId == symbolId
                                    && (route is null || string.Equals(e.Route, route, StringComparison.Ordinal))
                                    && (httpMethod is null || string.Equals(e.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 流程用的边：入口落在包着 lambda 的外层方法上（规则见 <see cref="WindowedRules"/>，入口行在符号体内）时，
    /// 根符号的出边只留 [入口行, 同文件下一条路由行) 范围里的——那段 lambda 自己的调用；其他情况原样返回。
    /// </summary>
    public static IReadOnlyList<MapEdge> FlowEdges(EntryPoint entry, IReadOnlyList<EntryPoint> all, IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges)
    {
        if (!WindowedRules.Contains(entry.RuleId)) return edges;
        var symbol = symbols.FirstOrDefault(s => s.Id == entry.SymbolId);
        if (symbol is null || symbol.Path != entry.File || entry.Line < symbol.StartLine || entry.Line > symbol.EndLine) return edges;
        var next = all.Where(e => e.File == entry.File && WindowedRules.Contains(e.RuleId) && e.Line > entry.Line && e.Line <= symbol.EndLine)
            .Select(e => e.Line).DefaultIfEmpty(symbol.EndLine + 1).Min();
        return edges.Where(e => e.FromId != entry.SymbolId || (e.Line >= entry.Line && e.Line < next)).ToList();
    }

    /// <summary>
    /// lambda 处理器的落点。handler 是 Map 调用的第二个实参（已涂掉字符串 / 注释）。不是 lambda 或找不到合适的调用返回 null。
    /// </summary>
    private sealed class CsHandlers
    {
        private readonly ILookup<string, MapSymbol> _methodsByName;
        private readonly ILookup<string, MapSymbol> _typesByName;
        private readonly ILookup<long, long> _supers;
        private readonly IReadOnlyDictionary<long, MapSymbol> _byId;
        private readonly Func<string, IReadOnlyList<ArchNode>> _containersOf;

        public CsHandlers(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, IReadOnlyDictionary<long, MapSymbol> byId, Func<string, IReadOnlyList<ArchNode>> containersOf)
        {
            bool Cs(MapSymbol s) => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName);
            _methodsByName = symbols.Where(s => s.Kind == SymbolKind.Method && Cs(s)).ToLookup(s => s.Name, StringComparer.Ordinal);
            _typesByName = symbols.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct && Cs(s)).ToLookup(s => s.Name, StringComparer.Ordinal);
            _supers = edges.Where(e => e.Kind is EdgeKind.Inherits or EdgeKind.Implements).ToLookup(e => e.FromId, e => e.ToId);
            _byId = byId;
            _containersOf = containersOf;
        }

        private string? ParentName(MapSymbol s) => s.ParentId is { } p && _byId.TryGetValue(p, out var o) ? o.Name : null;

        private bool SameContainer(string a, string b)
        {
            var keys = _containersOf(a).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            return _containersOf(b).Any(c => keys.Contains(c.Key));
        }

        private MapSymbol? Best(IEnumerable<MapSymbol> candidates, string file) =>
            candidates.OrderBy(c => c.Path == file ? 0 : SameContainer(c.Path, file) ? 1 : 2).ThenBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.StartLine).FirstOrDefault();

        /// <summary>类型名 T 的符号与它的基类 / 接口（往上 4 层）：方法定义在这些类型上都算 T 的方法。</summary>
        private HashSet<long> TypeAndSupers(string typeName)
        {
            var set = new HashSet<long>();
            var frontier = _typesByName[typeName].Select(t => t.Id).ToList();
            for (var depth = 0; depth < 5 && frontier.Count > 0; depth++)
            {
                var next = new List<long>();
                foreach (var id in frontier) if (set.Add(id)) next.AddRange(_supers[id]);
                frontier = next;
            }
            return set;
        }

        public MapSymbol? Resolve(string handler, string file)
        {
            var k = SkipWs(handler, 0);
            while (k < handler.Length && handler[k] == '[') { var close = MatchMasked(handler, k); if (close < 0) return null; k = SkipWs(handler, close + 1); } // [ProducesResponseType…]
            var arrow = TopLevelArrow(handler, k);
            if (arrow < 0) return null;
            var head = handler[k..arrow];
            var body = handler[(arrow + 2)..].Trim();
            var block = body.StartsWith('{');

            // 形参：(Type name, …)；单个无类型参数 x => … 没有类型
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal); // 名字 → 类型短名
            var closeParen = head.LastIndexOf(')');
            if (closeParen >= 0)
            {
                var openParen = MatchBack(head, closeParen);
                if (openParen >= 0)
                {
                    var (args, _) = SplitMasked(head, openParen);
                    foreach (var raw in args)
                    {
                        var a = Regex.Replace(raw, @"^\s*(?:\[[^\]]*\]\s*)*", "");
                        var nm = Regex.Match(a, @"(\w+)\s*(?:=.*)?$");
                        if (!nm.Success) continue;
                        var type = Regex.Match(a[..nm.Index].Trim(), @"(\w+)\s*(?:<.*>)?\s*\??$");
                        if (type.Success) parameters[nm.Groups[1].Value] = type.Groups[1].Value;
                    }
                }
            }

            // 1. 注入参数（仓库里的类型）上的方法调用：handler 真正干活的那一句
            foreach (Match call in MemberCall().Matches(body))
            {
                if (!parameters.TryGetValue(call.Groups[1].Value, out var type) || !_typesByName[type].Any()) continue;
                var owners = TypeAndSupers(type);
                var hit = Best(_methodsByName[call.Groups[2].Value].Where(m => m.ParentId is { } p && owners.Contains(p)), file);
                if (hit is null) continue;
                // 接口方法：流程从接口方法本身走不下去（实现边挂在调用方上）→ 唯一实现就落在实现上，否则留在外层方法（流程按行范围截，经调用方的实现边展开）
                if (hit.ParentId is { } ip && _byId.TryGetValue(ip, out var iface) && iface.Kind == SymbolKind.Interface)
                {
                    var impls = _methodsByName[hit.Name].Where(m => m.ParentId is { } p && _supers[p].Contains(ip)).ToList();
                    return impls.Count == 1 ? impls[0] : null;
                }
                return hit;
            }
            if (block) return null; // 多语句 lambda 没有注入服务上的调用：留在外层方法上，流程按行范围截

            // 2. 表达式体：第一个能在仓库里找到的调用
            foreach (Match call in CallSite().Matches(body))
            {
                var name = call.Groups[2].Value;
                var qualifier = call.Groups[1].Success ? call.Groups[1].Value : null;
                if (NotHandlerCalls.Contains(name) || (qualifier is not null && NotHandlerQualifiers.Contains(qualifier))) continue;
                var candidates = _methodsByName[name].ToList();
                if (candidates.Count == 0) continue;
                if (qualifier is not null && parameters.TryGetValue(qualifier, out var ptype) && _typesByName[ptype].Any()) continue; // 仓库类型上的调用上面已经找过
                if (qualifier is not null && !parameters.ContainsKey(qualifier) && char.IsUpper(qualifier[0]))
                {
                    var typed = candidates.Where(c => ParentName(c) == qualifier).ToList();
                    candidates = typed.Count > 0 ? typed : candidates.Where(c => c.Path == file).ToList();
                }
                if (Best(candidates, file) is { } m) return m;
            }
            return null;
        }

        private static int TopLevelArrow(string t, int from)
        {
            var depth = 0;
            for (var i = from; i + 1 < t.Length; i++)
            {
                var ch = t[i];
                if (ch is '(' or '[' or '{') depth++;
                else if (ch is ')' or ']' or '}') depth--;
                else if (ch == '=' && t[i + 1] == '>' && depth == 0) return i;
            }
            return -1;
        }

        private static int MatchBack(string t, int close)
        {
            var depth = 0;
            for (var i = close; i >= 0; i--)
            {
                if (t[i] is ')' or ']' or '}') depth++;
                else if (t[i] is '(' or '[' or '{' && --depth == 0) return i;
            }
            return -1;
        }
    }

    /// <summary>MVC 约定路由模板 → 动作的路径：{controller…} / {action…} 换成名字，可选段与带默认值的其他参数去掉。</summary>
    public static string ConventionalRoute(string pattern, string className, string methodName)
    {
        var controller = className.EndsWith("Controller", StringComparison.Ordinal) ? className[..^"Controller".Length] : className;
        var segs = new List<string>();
        foreach (var seg in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(seg, @"^\{(\w+)(?::[^=?}]*)?(=[^}]*)?(\?)?\}$");
            if (!m.Success) { segs.Add(seg); continue; }
            var name = m.Groups[1].Value;
            if (name.Equals("controller", StringComparison.OrdinalIgnoreCase)) segs.Add(controller);
            else if (name.Equals("action", StringComparison.OrdinalIgnoreCase)) segs.Add(methodName);
            else if (m.Groups[2].Success || m.Groups[3].Success || name.Equals("area", StringComparison.OrdinalIgnoreCase)) continue;
            else segs.Add(seg);
        }
        return "/" + string.Join('/', segs);
    }

    /// <summary>仓库里的 MVC 约定路由模板：MapControllerRoute 的 pattern 字面量；只有 MapDefaultControllerRoute 时是默认模板；都没有返回 null。</summary>
    private static string? ConventionalTemplate(IReadOnlyList<string> csFiles, Func<string, string[]> readLines)
    {
        string? found = null;
        foreach (var file in csFiles)
        {
            var lines = readLines(file);
            if (!Array.Exists(lines, l => l.Contains("ControllerRoute", StringComparison.Ordinal))) continue;
            var text = CsMask(string.Join('\n', lines));
            if (!ConventionalRouting().IsMatch(text)) continue;
            var raw = string.Join('\n', lines);
            var pattern = ConventionalPattern().Match(raw);
            if (pattern.Success && pattern.Groups[1].Value.Contains("{controller", StringComparison.OrdinalIgnoreCase)) return pattern.Groups[1].Value;
            found ??= "{controller=Home}/{action=Index}/{id?}";
        }
        return found;
    }

    /// <summary>Blazor 页面：.razor 模块里的 @page 指令（一个组件可以有多条）。</summary>
    private static void DiscoverBlazorPages(IReadOnlyList<MapSymbol> symbols, ILookup<long, MapSymbol> children, Func<string, IReadOnlyList<ArchNode>> containersOf,
        Func<string, string[]> readLines, Action<EntryPoint> add)
    {
        foreach (var module in symbols.Where(s => s.Kind == SymbolKind.Module && s.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)))
        {
            var owners = containersOf(module.Path).Where(c => c.SubKind.StartsWith("dotnet", StringComparison.Ordinal)).ToList();
            if (owners.Count == 0) continue;
            var lines = readLines(module.Path);
            var target = children[module.Id].Where(s => s.Kind == SymbolKind.Class).OrderBy(s => s.StartLine).FirstOrDefault() ?? module;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = RazorPage().Match(lines[i]);
                if (!m.Success) continue;
                var route = "/" + m.Groups[1].Value.Trim('/');
                foreach (var c in owners) add(new EntryPoint(target.Id, c.Key, "blazor.page", "page", route, null, module.Path, i + 1));
            }
        }
    }

    // 宿主程序的 Main / 顶级语句里起了常驻宿主（WebApplication / Host / MAUI / Aspire …）就是常驻服务，否则是跑一次就退出的命令行
    [GeneratedRegex(@"\b(?:WebApplication|WebHost|Host|DistributedApplication|MauiApp|UIApplication|Application|HostApplicationBuilder)\s*\.\s*\w+\s*\(|\bnew\s+(?:HostBuilder|WebHostBuilder|HostApplicationBuilder)\b|(?<!\bTask\s*)\.\s*Run(?:Async)?\s*\(|\.\s*RunConsoleAsync\s*\(|\.\s*WaitForShutdown(?:Async)?\s*\(")]
    private static partial Regex HostRunMarker();

    /// <summary>csharp.main 入口的 kind：dotnet-exe 容器里、启动代码不起常驻宿主的 → command；其余 hosted。</summary>
    private static string MainKind(ArchNode container, MapSymbol main, Func<string, string[]> readLines)
    {
        if (container.SubKind != "dotnet-exe") return "hosted";
        var lines = readLines(main.Path);
        if (lines.Length == 0) return "hosted";
        var from = Math.Max(main.StartLine - 1, 0);
        var to = Math.Min(Math.Max(main.EndLine, main.StartLine), lines.Length);
        var text = CsMask(string.Join('\n', lines[from..to]));
        return HostRunMarker().IsMatch(text) ? "hosted" : "command";
    }
}
