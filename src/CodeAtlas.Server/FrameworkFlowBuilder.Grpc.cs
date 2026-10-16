using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

public static partial class FrameworkFlowBuilder
{
    /// <summary>
    /// 纯 gRPC 服务（没有 HTTP 框架）的框架流程，Go / Python / Java 共用（Node 的在 NodeFrameworkFlow.BuildGrpc，.NET 的 gRPC 走同一条 ASP.NET Core 管道、画在中间件下面）：
    /// 根"gRPC 调用" → 服务端拦截器（有就按注册顺序画，没有在说明里写）→ 占位"服务方法 ×N"（N = kind = grpc 的入口数）。
    /// 拦截器从源码文本认：Go 的 grpc.NewServer(grpc.UnaryInterceptor / ChainUnaryInterceptor / StreamInterceptor / StatsHandler …)，
    /// Python 的 grpc.server(…, interceptors=[…])，Java 的 ServerBuilder….intercept(x) / ServerInterceptors.intercept(svc, x…)。
    /// 没有 gRPC 入口返回 null（调用方照旧走自己的非 Web 画法）。
    /// </summary>
    internal static FrameworkFlowDto? GrpcOnly(FrameworkInput input, string language, IReadOnlyList<string> preface)
    {
        var container = input.Container;
        var entries = input.Entries.Where(e => e.ContainerKey == container.Key && !PipelineBuilder.IsTest(e.File, "")).ToList();
        var grpc = entries.Where(e => e.Kind == "grpc").ToList();
        if (grpc.Count == 0) return null;
        var ext = language switch { "go" => ".go", "python" => ".py", _ => ".java" };
        var files = input.Symbols.Where(s => s.Kind != SymbolKind.External && s.Path.EndsWith(ext, StringComparison.Ordinal) && container.Members.Any(m => Under(s.Path, m))
                                             && !PipelineBuilder.IsTest(s.Path, s.QualifiedName) && !s.Path.Contains("genproto", StringComparison.Ordinal) && !s.Path.EndsWith("_pb2_grpc.py", StringComparison.Ordinal))
            .Select(s => s.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        var interceptors = new List<FlowNodeDto>();
        string? server = null;
        foreach (var path in files)
        {
            string? raw;
            try { raw = input.Sources.ReadFile(path); } catch { raw = null; }
            if (raw is null) continue;
            var text = language == "python" ? Regex.Replace(raw, @"#[^\n]*", m => new string(' ', m.Length)) : StripCommentsAndStrings(raw).Replace('\0', ' ');
            int LineOf(int pos) { var n = 1; for (var i = 0; i < pos && i < raw.Length; i++) if (raw[i] == '\n') n++; return n; }
            string Where(int pos) => $"{MapSymbol.FileName(path)}:{LineOf(pos)}";
            switch (language)
            {
                case "go":
                    foreach (Match m in Regex.Matches(text, @"\bgrpc\s*\.\s*NewServer\s*\("))
                    {
                        server ??= $"grpc.NewServer，{Where(m.Index)}";
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var args = raw[(m.Index + m.Length)..close];
                        foreach (Match o in Regex.Matches(args, @"grpc\s*\.\s*(?<k>ChainUnaryInterceptor|UnaryInterceptor|ChainStreamInterceptor|StreamInterceptor|StatsHandler)\s*\("))
                        {
                            var oc = MatchBracket(args, o.Index + o.Length - 1, '(', ')');
                            var inner = oc < 0 ? "" : Regex.Replace(args[(o.Index + o.Length)..oc], @"\s+", "");
                            var kind = o.Groups["k"].Value;
                            var pos = m.Index + m.Length + o.Index;
                            var note = kind == "StatsHandler"
                                ? $"StatsHandler（{Where(pos)}）：每个 RPC 开始 / 结束时回调（链路追踪、指标），包在服务方法外层"
                                : $"{(kind.Contains("Stream", StringComparison.Ordinal) ? "流式" : "一元")}拦截器（grpc.{kind}，{Where(pos)}）：包在服务方法外层，按注册顺序由外到内";
                            foreach (var part in SplitTopLevel(inner).Where(p => p.Length > 0))
                                interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, note, [], path, LineOf(pos)));
                        }
                    }
                    break;
                case "python":
                    foreach (Match m in Regex.Matches(text, @"\bgrpc\s*\.\s*(?:aio\s*\.\s*)?server\s*\("))
                    {
                        server ??= $"grpc.server，{Where(m.Index)}";
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var args = raw[(m.Index + m.Length)..close];
                        if (Regex.Match(args, @"interceptors\s*=\s*(?<o>[\[(])") is { Success: true } im
                            && MatchBracket(args, im.Groups["o"].Index, im.Groups["o"].Value[0], im.Groups["o"].Value == "[" ? ']' : ')') is var ic and > 0)
                            foreach (var part in SplitTopLevel(args[(im.Groups["o"].Index + 1)..ic]).Select(p => Regex.Replace(p, @"\s+", "")).Where(p => p.Length > 0))
                                interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, $"服务端拦截器（interceptors=，{Where(m.Index)}）：按列表顺序由外到内", [], path, LineOf(m.Index)));
                    }
                    break;
                default:
                    foreach (Match m in Regex.Matches(text, @"\b(?:Netty)?ServerBuilder\s*\.\s*forPort\s*\("))
                        server ??= $"ServerBuilder.forPort，{Where(m.Index)}";
                    foreach (Match m in Regex.Matches(text, @"\.\s*intercept\s*\(|\bServerInterceptors\s*\.\s*intercept(?:Forward)?\s*\("))
                    {
                        var close = MatchBracket(text, m.Index + m.Length - 1, '(', ')');
                        if (close < 0) continue;
                        var parts = SplitTopLevel(raw[(m.Index + m.Length)..close]).Select(p => Regex.Replace(p, @"\s+", "")).Where(p => p.Length > 0).ToList();
                        if (m.Value.Contains("ServerInterceptors", StringComparison.Ordinal) && parts.Count > 0) parts.RemoveAt(0); // 第一个参数是服务
                        foreach (var part in parts)
                            interceptors.Add(Node(null, part, part, "external-group", StageInterceptor, $"服务端拦截器（{Where(m.Index)}）：包在服务方法外层", [], path, LineOf(m.Index)));
                    }
                    break;
            }
        }

        var services = grpc.Where(e => e.Route is not null).GroupBy(e => e.Route!.Contains('/') ? e.Route![..e.Route!.IndexOf('/')] : "?", StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()} 个方法").ToList();
        var notes = new List<string>(preface)
        {
            $"认出 gRPC 服务（{server ?? "没找到创建服务器的地方"}）：{string.Join("、", services)}；没有 HTTP 框架，框架流程画 gRPC 这条链",
            interceptors.Count == 0
                ? language switch
                {
                    "go" => "没有注册服务端拦截器（grpc.NewServer 的参数里没有 UnaryInterceptor / StreamInterceptor / StatsHandler）：请求直接进服务方法",
                    "python" => "没有注册服务端拦截器（grpc.server 没传 interceptors=）：请求直接进服务方法",
                    _ => "没有注册服务端拦截器（ServerBuilder 上没有 .intercept(…)，也没有 ServerInterceptors.intercept）：请求直接进服务方法",
                }
                : $"服务端拦截器 {interceptors.Count} 个，按注册顺序包在服务方法外层（先注册的在外层）",
        };
        var others = entries.Count - grpc.Count;
        if (others > 0) notes.Add($"另有 {others} 个非 gRPC 入口（{string.Join("、", entries.Where(e => e.Kind != "grpc").GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}），没画在这条链上");

        var placeholder = Node(null, "服务方法", "", "controllers", StageController, "具体做了什么看各方法的入口流程", [], null, null, grpc.Count);
        var root = Node(null, "gRPC 调用", "", "entry", interceptors.Count > 0 ? StageInterceptor : StageController, null, [.. interceptors, placeholder], null, null);
        var byId = input.Symbols.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        SymbolDto? Dto(long id) => input.Sources.Symbol?.Invoke(id)
            ?? (byId.TryGetValue(id, out var s) ? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, null, s.Path, s.StartLine, 0, s.EndLine, 0, s.Visibility) : null);
        var stages = new List<FrameworkStageDto>();
        if (interceptors.Count > 0) stages.Add(new FrameworkStageDto(StageInterceptor, "拦截器", true, []));
        stages.Add(new FrameworkStageDto(StageController, "服务方法", true, grpc.Select(e => Dto(e.SymbolId)).Where(d => d is not null).Select(d => d!).ToList()));
        return Assemble(container, "gRPC 调用", root, stages, [], notes, interceptors.Count > 0 ? "拦截器" : "服务方法");
    }
}
