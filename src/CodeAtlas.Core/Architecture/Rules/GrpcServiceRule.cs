using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// gRPC 按服务名跨服务连线（跨语言）：客户端桩与服务端注册对上同一个服务名就连，不依赖地址。
/// 服务端：Go pb.RegisterXxxServer(…)、Python xxx_pb2_grpc.add_XxxServicer_to_server(…)、C# MapGrpcService&lt;T&gt;()（T 继承 Xxx.XxxBase）、
/// Node server.addService(pkg.Xxx.service, …)、Java class X extends XxxGrpc.XxxImplBase。
/// 客户端：Go pb.NewXxxClient(conn)、Python xxx_pb2_grpc.XxxStub(channel)、C# new Xxx.XxxClient(channel) / AddGrpcClient&lt;Xxx.XxxClient&gt;、
/// Node new pkg.Xxx(addr, grpc.credentials…)、Java XxxGrpc.newStub / newBlockingStub / newFutureStub(channel)。
/// 服务名只由一个容器提供 → 出口 TargetKey 是那个容器（EdgeMatcher 给 0.9）；多个容器都提供 → 不猜，挂 grpc-service 外部系统（0.5）；
/// 没有容器提供 → 同样挂外部系统；调用自己容器提供的服务 → 不登记。Health 服务不算。生成的桩代码（*.pb.go、*_pb2_grpc.py）不扫。
/// 与通道规则（GrpcChannelRule / go-outbound / python-outbound 的 grpc 出口）去重：通道被桩"解释"（同一行、其后 5 行内的桩用了通道变量、
/// 通道的配置键 / 地址里含服务名、或通道地址解析不出且本容器有桩）且桩连上了容器（或是自调用）时去掉通道；
/// 桩没连上容器、又和通道在同一行（C# 的 AddGrpcClient&lt;T&gt;(o =&gt; o.Address = …)）时保留通道、去掉桩，结果与只有通道规则时一样；
/// 不在同一行、通道又只有配置键没有地址时去掉通道（AD_SERVICE_ADDR 不如 AdService 说得清）。
/// </summary>
public sealed class GrpcServiceRule : IDiscoveryRule
{
    public const string RuleId = "grpc-service";
    public string Id => RuleId;
    public string Description => "gRPC 客户端桩与服务端注册按服务名对上（Go / Python / C# / Node / Java）";

    private const int ChannelWindow = 5;

    private static readonly Regex GoRegister = new(@"\b(\w+)\.Register(\w+)Server\(", RegexOptions.Compiled);
    private static readonly Regex GoClient = new(@"\b(\w+)\.New(\w+)Client\(", RegexOptions.Compiled);
    private static readonly Regex GoImportLine = new(@"^\s*(?:import\s+)?(?:([\w.]+)\s+)?""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex PyRegister = new(@"\badd_(\w+)Servicer_to_server\(", RegexOptions.Compiled);
    private static readonly Regex PyStub = new(@"\b\w+_pb2_grpc\.(\w+)Stub\(", RegexOptions.Compiled);
    private static readonly Regex PyBareStub = new(@"(?<![\w.])(\w+)Stub\(", RegexOptions.Compiled);
    private static readonly Regex CsMap = new(@"\bMapGrpcService<\s*([\w.]+)\s*>", RegexOptions.Compiled);
    private static readonly Regex CsClass = new(@"\bclass\s+(\w+)\b[^{;]*?:\s*([\w.]+)", RegexOptions.Compiled);
    private static readonly Regex CsClient = new(@"(?:\bnew\s+|\bAddGrpcClient<\s*|\bCreateClient<\s*)(?:\w+\.)*?(\w+)\.(\w+)Client\s*[(>]", RegexOptions.Compiled);
    private static readonly Regex JsAddService = new(@"\.addService\(\s*(?:[\w$]+\.)*([\w$]+)\.service\b", RegexOptions.Compiled);
    private static readonly Regex JsClient = new(@"\bnew\s+(?:[\w$]+\.)+([\w$]+)\(([^()]|\([^()]*\))*credentials\.", RegexOptions.Compiled);
    // Java：class X extends [pkg.]XxxGrpc.XxxImplBase（grpc-java 生成的服务基类）；XxxGrpc.newStub / newBlockingStub / newFutureStub(channel)
    private static readonly Regex JavaImplBase = new(@"\bextends\s+(?:[\w$]+\.)*(\w+)Grpc\s*\.\s*(\w+)ImplBase\b", RegexOptions.Compiled);
    private static readonly Regex JavaStub = new(@"\b(\w+)Grpc\s*\.\s*new(?:Blocking|Future|BlockingV2)?Stub\s*\(", RegexOptions.Compiled);
    private static readonly Regex ProtoSegment = new(@"^(?:gen)?protos?$|^(?:gen)?pb$|pb$|proto$|^grpc$|_grpc$|^grpc_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>一处服务端注册 / 客户端桩（诊断用）。</summary>
    public sealed record Site(string ContainerKey, string Service, string File, int Line);

    /// <summary>最近一次 Apply 认出的服务端注册。</summary>
    public List<Site> Servers { get; } = [];
    /// <summary>最近一次 Apply 认出的客户端桩（含调用自己容器的）。</summary>
    public List<Site> Clients { get; } = [];

    private sealed record Stub(Site Site, string? Target, bool Self);

    public void Apply(RuleContext ctx)
    {
        Servers.Clear();
        Clients.Clear();
        var files = ctx.Inventory.SourceFiles.Where(f => !RuleContext.IsTestFile(f) && !ProjectInventory.IsGeneratedSource(f) && PolyText.LangOf(f) is not null).ToList();
        var csBases = CSharpBases(ctx, files);
        foreach (var file in files)
        {
            var lang = PolyText.LangOf(file)!.Value;
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => l.Contains("grpc", StringComparison.OrdinalIgnoreCase) || l.Contains("Grpc", StringComparison.Ordinal) || l.Contains("Client", StringComparison.Ordinal) || l.Contains("Register", StringComparison.Ordinal))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var (servers, clients) = Scan(file, lines, lang, csBases);
            foreach (var c in containers)
            {
                Servers.AddRange(servers.Select(s => new Site(c.Key, s.Service, file, s.Line)));
                Clients.AddRange(clients.Select(s => new Site(c.Key, s.Service, file, s.Line)));
            }
        }

        var providers = Servers.GroupBy(s => s.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.ContainerKey).Distinct(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var stubs = new List<Stub>();
        foreach (var client in Clients)
        {
            var p = providers.TryGetValue(client.Service, out var list) ? list : [];
            if (p.Contains(client.ContainerKey)) { stubs.Add(new Stub(client, null, Self: true)); continue; }
            stubs.Add(new Stub(client, p.Count == 1 ? p[0] : null, Self: false));
        }

        // 与通道出口去重
        var channels = ctx.Outbound.Where(o => o.Protocol == "grpc" && o.RuleId != RuleId).ToList();
        var dropStubs = new HashSet<Stub>();
        foreach (var ch in channels)
        {
            var own = stubs.Where(s => s.Site.ContainerKey == ch.ContainerKey).ToList();
            if (own.Count == 0) continue;
            var anonymous = ch.Url is null && ch.ConfigKey is null && ch.ClientName is null;
            var explaining = anonymous ? own : own.Where(s => Explains(ctx, ch, s.Site)).ToList();
            if (explaining.Count == 0) continue;
            if (anonymous || explaining.Any(s => s.Target is not null || s.Self)) { ctx.Outbound.Remove(ch); continue; }
            var sameLine = explaining.Where(s => s.Site.File == ch.File && s.Site.Line == ch.Line).ToList();
            if (sameLine.Count > 0) { foreach (var s in sameLine) dropStubs.Add(s); continue; }
            // 桩也没连上容器、通道又只有配置键（环境变量名）没有地址：桩的服务名更能说明是什么，去掉通道
            if (ch.Url is null) ctx.Outbound.Remove(ch);
        }

        foreach (var s in stubs.Where(s => !s.Self && !dropStubs.Contains(s)))
        {
            var siteLines = ctx.Lines(s.Site.File);
            var target = s.Target ?? ctx.External("grpc-service", s.Site.Service, new Evidence(s.Site.File, s.Site.Line, s.Site.Line <= siteLines.Length ? siteLines[s.Site.Line - 1].Trim() : null));
            ctx.Outbound.Add(new Outbound(s.Site.ContainerKey, RuleId, "grpc", s.Site.Service, null, null, s.Site.File, s.Site.Line, target));
        }
    }

    /// <summary>
    /// 通道是不是给这个桩用的：同一行；或通道那行把通道赋给了变量（channel = grpc.insecure_channel(…) / var channel = GrpcChannel.ForAddress(…)），
    /// 其后 5 行内的桩用了这个变量；或通道的配置键 / 地址 / 客户端名里含服务名（PRODUCT_CATALOG_SERVICE_ADDR ↔ ProductCatalogService）。
    /// </summary>
    private static bool Explains(RuleContext ctx, Outbound ch, Site s)
    {
        if (s.File == ch.File && s.Line == ch.Line) return true;
        if (s.File == ch.File && s.Line > ch.Line && s.Line - ch.Line <= ChannelWindow)
        {
            var lines = ctx.Lines(ch.File);
            var assigned = Regex.Match(lines[ch.Line - 1], @"^\s*(?:var\s+)?(\w+)\s*(?:,\s*\w+\s*)?:?=(?!=)");
            if (assigned.Success && Regex.IsMatch(lines[s.Line - 1], @"\b" + Regex.Escape(assigned.Groups[1].Value) + @"\b")) return true;
        }
        var service = Normalize(s.Service);
        if (service.Length < 4) return false;
        return new[] { ch.ConfigKey, ch.Url, ch.ClientName }.Any(x => x is not null && Normalize(x).Contains(service, StringComparison.Ordinal));
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool Ignored(string service) => service.Length == 0 || service.Equals("Health", StringComparison.OrdinalIgnoreCase);

    private static (List<(string Service, int Line)> Servers, List<(string Service, int Line)> Clients) Scan(string file, string[] lines, SourceLang lang, Dictionary<string, string> csBases)
    {
        var servers = new List<(string, int)>();
        var clients = new List<(string, int)>();
        var goImports = lang == SourceLang.Go ? GoImports(lines) : null;
        var pyBare = lang == SourceLang.Python && Array.Exists(lines, l => Regex.IsMatch(l, @"^\s*from\s+[\w.]*_pb2_grpc\s+import\b"));
        var jsGrpc = lang == SourceLang.JavaScript && Array.Exists(lines, l => l.Contains("grpc", StringComparison.Ordinal));
        // 提交进仓库的 grpc-java 生成代码（XxxGrpc.java）：里面的 newStub / ImplBase 是定义
        if (lang == SourceLang.Java && Array.Exists(lines, l => l.Contains("GrpcGenerated", StringComparison.Ordinal) || l.Contains("Generated by the protocol buffer compiler", StringComparison.Ordinal)))
            return (servers, clients);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            switch (lang)
            {
                case SourceLang.Go:
                    if (trimmed.StartsWith("func ", StringComparison.Ordinal)) break; // 生成代码之外手写的 RegisterXServer 定义也不算
                    foreach (Match m in GoRegister.Matches(line))
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && IsProtoPackage(m.Groups[1].Value, goImports!) && !m.Groups[2].Value.EndsWith("Handler", StringComparison.Ordinal) && !Ignored(m.Groups[2].Value))
                            servers.Add((m.Groups[2].Value, i + 1));
                    foreach (Match m in GoClient.Matches(line))
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && IsProtoPackage(m.Groups[1].Value, goImports!) && !Ignored(m.Groups[2].Value))
                            clients.Add((m.Groups[2].Value, i + 1));
                    break;
                case SourceLang.Python:
                    if (Regex.IsMatch(trimmed, @"^(?:def|class)\s")) break;
                    foreach (Match m in PyRegister.Matches(line))
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && !Ignored(m.Groups[1].Value)) servers.Add((m.Groups[1].Value, i + 1));
                    var stubs = PyStub.Matches(line).Cast<Match>().ToList();
                    if (stubs.Count == 0 && pyBare) stubs = PyBareStub.Matches(line).Cast<Match>().ToList();
                    foreach (var m in stubs)
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && !Ignored(m.Groups[1].Value)) clients.Add((m.Groups[1].Value, i + 1));
                    break;
                case SourceLang.CSharp:
                    foreach (Match m in CsMap.Matches(line))
                    {
                        if (PolyText.InCommentOrString(line, m.Index, lang)) continue;
                        var cls = LastSegment(m.Groups[1].Value);
                        if (csBases.TryGetValue(cls, out var service) && !Ignored(service)) servers.Add((service, i + 1));
                    }
                    foreach (Match m in CsClient.Matches(line))
                        // 生成的客户端类是服务类里嵌套的 XxxClient：外层名 + Client
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && m.Groups[1].Value == m.Groups[2].Value && !Ignored(m.Groups[1].Value))
                            clients.Add((m.Groups[1].Value, i + 1));
                    break;
                case SourceLang.Java:
                    if (trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal)) break; // Javadoc / 块注释
                    foreach (Match m in JavaImplBase.Matches(line))
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && m.Groups[1].Value == m.Groups[2].Value && !Ignored(m.Groups[1].Value))
                            servers.Add((m.Groups[1].Value, i + 1));
                    foreach (Match m in JavaStub.Matches(line))
                        if (!PolyText.InCommentOrString(line, m.Index, lang) && !Ignored(m.Groups[1].Value))
                            clients.Add((m.Groups[1].Value, i + 1));
                    break;
                case SourceLang.JavaScript:
                    if (!jsGrpc) break;
                    var stmt = PolyText.Statement(lines, i, 2);
                    if (line.Contains(".addService(", StringComparison.Ordinal) && JsAddService.Match(stmt) is { Success: true } a
                        && !PolyText.InCommentOrString(line, line.IndexOf(".addService(", StringComparison.Ordinal), lang) && !Ignored(a.Groups[1].Value))
                        servers.Add((a.Groups[1].Value, i + 1));
                    if (line.Contains("new ", StringComparison.Ordinal) && JsClient.Match(stmt) is { Success: true } c && c.Index < line.Length
                        && !PolyText.InCommentOrString(line, c.Index, lang) && !Ignored(c.Groups[1].Value))
                        clients.Add((c.Groups[1].Value, i + 1));
                    break;
            }
        }
        return (servers, clients);
    }

    /// <summary>C# 类名 → 它继承的生成基类对应的服务名（LedgerApi : Ledger.LedgerBase → Ledger；: GreeterBase → Greeter）。</summary>
    private static Dictionary<string, string> CSharpBases(RuleContext ctx, List<string> files)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var lines = ctx.Lines(f);
            if (!Array.Exists(lines, l => l.Contains("Base", StringComparison.Ordinal))) continue;
            foreach (var l in lines)
            {
                var m = CsClass.Match(l);
                if (!m.Success) continue;
                var segs = m.Groups[2].Value.Split('.');
                var last = segs[^1];
                if (!last.EndsWith("Base", StringComparison.Ordinal) || last.Length <= 4) continue;
                var service = last[..^4];
                if (segs.Length >= 2 && segs[^2] != service) continue; // X.YBase 里 Y 必须等于 X，才是 protoc 生成的服务基类
                map.TryAdd(m.Groups[1].Value, service);
            }
        }
        return map;
    }

    /// <summary>Go 文件的导入：别名（没写就取路径最后一段，去掉 /vN）→ 路径。</summary>
    private static Dictionary<string, string> GoImports(string[] lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("func ", StringComparison.Ordinal) || line.StartsWith("type ", StringComparison.Ordinal)) break;
            if (line.StartsWith("import (", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (inBlock && line.StartsWith(')')) { inBlock = false; continue; }
            if (!inBlock && !line.StartsWith("import ", StringComparison.Ordinal)) continue;
            var m = GoImportLine.Match(line);
            if (!m.Success) continue;
            var path = m.Groups[2].Value;
            var alias = m.Groups[1].Success ? m.Groups[1].Value : DefaultPackage(path);
            if (alias is "_" or ".") continue;
            map[alias] = path;
        }
        return map;
    }

    private static string DefaultPackage(string path)
    {
        var segs = path.Split('/');
        var last = segs[^1];
        if (segs.Length > 1 && Regex.IsMatch(last, @"^v\d+$")) last = segs[^2];
        return last;
    }

    /// <summary>限定符是不是 protoc 生成的包：别名以 pb 结尾（pb、healthpb、orderpb），或导入路径里有 proto / pb / genproto / grpc 之类的段。</summary>
    private static bool IsProtoPackage(string qualifier, Dictionary<string, string> imports)
    {
        if (!imports.TryGetValue(qualifier, out var path)) return false;
        if (qualifier.EndsWith("pb", StringComparison.OrdinalIgnoreCase)) return true;
        return path.Split('/').Any(seg => ProtoSegment.IsMatch(seg));
    }

    private static string LastSegment(string dotted) => dotted.Contains('.') ? dotted[(dotted.LastIndexOf('.') + 1)..] : dotted;
}
