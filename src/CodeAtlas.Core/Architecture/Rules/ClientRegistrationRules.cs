using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// 从一个表达式里抽地址来源：字符串字面量 → URL；configuration["A:B"] → 配置键；GetSection("A") … .Url → "A:Url"；
/// 局部变量 → 在同一方法里往回找它的赋值。只做文本层面的回溯，找不到就返回空，不猜。
/// </summary>
public static class AddressResolver
{
    // 含 Aspire 服务发现的 https+http:// 方案
    private static readonly Regex UrlLiteral = new(@"""((?:https?|https\+http)://[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Indexer = new(@"\w+\[\s*""([^""]+)""\s*\]", RegexOptions.Compiled);
    private static readonly Regex GetSection = new(@"GetSection\(\s*(?:""([^""]+)""|nameof\(\s*([\w.]+)\s*\))\s*\)", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    public sealed record Address(string? Url, string? ConfigKey);

    /// <summary>解析 expr（可能是字面量、索引器、变量名）；lines/lineIndex 用来回溯变量。</summary>
    public static Address Resolve(string expr, string[] lines, int lineIndex)
    {
        expr = expr.Trim().TrimEnd('!');
        var url = UrlLiteral.Match(expr);
        if (url.Success) return new Address(url.Groups[1].Value, null);
        var idx = Indexer.Match(expr);
        if (idx.Success) return new Address(null, idx.Groups[1].Value);
        var section = GetSection.Match(expr);
        if (section.Success) return new Address(null, SectionKey(section, expr));

        // 变量：往回找 `var name = …` / `name = …`，最多 40 行，遇到方法边界停。赋值可能跨行（链式调用），拼到分号为止
        var name = expr.Split(['.', ' ', '(', ')', '?'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? expr;
        if (!Identifier.IsMatch(name)) return new Address(null, null);
        var assign = new Regex(@"(?:var|string|Uri)?\s*\b" + Regex.Escape(name) + @"\s*=\s*(.+?);\s*$");
        for (var i = lineIndex - 1; i >= 0 && i >= lineIndex - 40; i--)
        {
            var line = lines[i];
            if (line.Contains("IServiceCollection ", StringComparison.Ordinal) && line.Contains('(') && i < lineIndex - 1 && line.TrimStart().StartsWith("public", StringComparison.Ordinal)) break;
            if (!Regex.IsMatch(line, @"\b" + Regex.Escape(name) + @"\s*=[^=]")) continue;
            var statement = line;
            for (var k = i + 1; k < lines.Length && k <= i + 6 && !statement.TrimEnd().EndsWith(';'); k++) statement += " " + lines[k].Trim();
            var m = assign.Match(statement);
            if (!m.Success) continue;
            var rhs = m.Groups[1].Value;
            var inner = Resolve(rhs, lines, i);
            if (inner.Url is not null || inner.ConfigKey is not null)
            {
                // `billing.Url.TrimEnd(…)`：变量 billing 来自 GetSection("Billing").Get<T>()，再接 .Url → Billing:Url
                if (inner.ConfigKey is not null && !inner.ConfigKey.Contains(':') && expr.Contains(".Url", StringComparison.OrdinalIgnoreCase)) return new Address(null, inner.ConfigKey + ":Url");
                return inner;
            }
            // 右边本身又是一个变量加属性（apiConfig.Url）：继续回溯那个变量
            var rhsName = rhs.Split(['.', ' ', '(', ')', '?'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (rhsName is not null && rhsName != name && Identifier.IsMatch(rhsName))
            {
                var deeper = Resolve(rhsName, lines, i);
                if (deeper.ConfigKey is not null && !deeper.ConfigKey.Contains(':') && rhs.Contains(".Url", StringComparison.OrdinalIgnoreCase)) return new Address(null, deeper.ConfigKey + ":Url");
                if (deeper.Url is not null || deeper.ConfigKey is not null) return deeper;
            }
        }
        return new Address(null, null);
    }

    private static string SectionKey(Match section, string expr)
    {
        var key = section.Groups[1].Success ? section.Groups[1].Value : LastSegment(section.Groups[2].Value);
        // GetSection("A")["Url"] 或 GetSection("A").GetValue<string>("Url")
        var after = expr[(section.Index + section.Length)..];
        var sub = Regex.Match(after, @"^\s*(?:\[\s*""([^""]+)""\s*\]|\.GetValue<\w+>\(\s*""([^""]+)""\s*\))");
        if (sub.Success) return key + ":" + (sub.Groups[1].Success ? sub.Groups[1].Value : sub.Groups[2].Value);
        return key;
    }

    private static string LastSegment(string dotted) => dotted.Contains('.') ? dotted[(dotted.LastIndexOf('.') + 1)..] : dotted;

    /// <summary>调用的第一个实参：字符串字面量取内容，标识符链取最后一段（ConstConfig.LARKAPICLIENT → LARKAPICLIENT），泛型参数取类型名。</summary>
    public static string? ClientName(string callText)
    {
        var generic = Regex.Match(callText, @"AddHttpClient<\s*([\w.]+)\s*(?:,\s*[\w.]+\s*)?>\s*\(");
        if (generic.Success) return LastSegment(generic.Groups[1].Value);
        var m = Regex.Match(callText, @"AddHttpClient\s*\(\s*(?:""([^""]+)""|([\w.]+))");
        if (!m.Success) return null;
        if (m.Groups[1].Success) return m.Groups[1].Value;
        if (!m.Groups[2].Success) return null;
        var chain = m.Groups[2].Value;
        // Options.DefaultName、以及小写开头的变量名（name、clientName）都是转发别人的名字的封装方法，不是一个客户端
        if (chain.EndsWith("DefaultName", StringComparison.Ordinal) || char.IsLower(LastSegment(chain)[0])) return null;
        return LastSegment(chain);
    }
}

/// <summary>services.AddHttpClient(...)：命名 / 类型化 HTTP 客户端 → 出口。地址从 BaseAddress = new Uri(x) 解析。</summary>
public sealed class HttpClientRule : IDiscoveryRule
{
    public string Id => "http-client";
    public string Description => "AddHttpClient 注册的 HTTP 客户端，地址来自 BaseAddress 的字面量或配置键";

    private static readonly Regex Call = new(@"AddHttpClient(?:<[^>]+>)?\s*\(", RegexOptions.Compiled);
    // BaseAddress = new Uri(x) / new(x)（目标类型 new）/ = x
    private static readonly Regex NewUri = new(@"BaseAddress\s*=\s*(?:new\s*(?:Uri)?\s*\((.+?)\)|([^;,)]+))\s*[;,)]", RegexOptions.Compiled);

    public void Apply(RuleContext ctx)
    {
        var found = new List<Outbound>();
        foreach (var file in ctx.CSharpFiles())
        {
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => l.Contains("AddHttpClient", StringComparison.Ordinal))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Call.Match(lines[i]);
                if (!m.Success || RuleContext.InCommentOrString(lines[i], m.Index)) continue;
                var name = AddressResolver.ClientName(lines[i][m.Index..]);
                var address = new AddressResolver.Address(null, null);
                // 在调用体内（到下一个 AddHttpClient 或 12 行内）找 new Uri(...)
                for (var j = i; j < lines.Length && j <= i + 12; j++)
                {
                    if (j > i && Call.IsMatch(lines[j])) break;
                    var uri = NewUri.Match(lines[j]);
                    if (!uri.Success) continue;
                    address = AddressResolver.Resolve(uri.Groups[1].Success ? uri.Groups[1].Value : uri.Groups[2].Value, lines, j);
                    break;
                }
                if (name is null && address.ConfigKey is null && address.Url is null) continue; // 转发名字的封装方法，不是客户端
                foreach (var c in containers)
                    found.Add(new Outbound(c.Key, Id, "http", name, address.ConfigKey, address.Url, file, i + 1, null));
            }
        }
        // 同一容器里同名客户端注册多次（先占名再配地址）：有地址的那条说了算
        foreach (var group in found.GroupBy(o => (o.ContainerKey, o.ClientName)))
        {
            var withAddress = group.Where(o => o.ConfigKey is not null || o.Url is not null).ToList();
            ctx.Outbound.AddRange(group.Key.ClientName is not null && withAddress.Count > 0 ? withAddress : group);
        }
    }
}

/// <summary>GrpcChannel.ForAddress(x) 与 AddGrpcClient&lt;T&gt;(o =&gt; o.Address = …) → gRPC 出口。</summary>
public sealed class GrpcChannelRule : IDiscoveryRule
{
    public string Id => "grpc-channel";
    public string Description => "GrpcChannel.ForAddress 创建的通道、AddGrpcClient 注册的客户端";

    private static readonly Regex Call = new(@"GrpcChannel\.ForAddress\s*\((.+?)\)\s*[;,)]?", RegexOptions.Compiled);
    private static readonly Regex ClientReg = new(@"AddGrpcClient<\s*([\w.]+)\s*>", RegexOptions.Compiled);
    private static readonly Regex Address = new(@"\.Address\s*=\s*(?:new\s*(?:Uri)?\s*\((.+?)\)|([^;,)]+))\s*[;,)]", RegexOptions.Compiled);

    public void Apply(RuleContext ctx)
    {
        foreach (var file in ctx.CSharpFiles())
        {
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => l.Contains("GrpcChannel", StringComparison.Ordinal) || l.Contains("AddGrpcClient", StringComparison.Ordinal))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Call.Match(lines[i]);
                if (m.Success && !RuleContext.InCommentOrString(lines[i], m.Index))
                {
                    var address = AddressResolver.Resolve(m.Groups[1].Value, lines, i);
                    foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, "grpc", null, address.ConfigKey, address.Url, file, i + 1, null));
                    continue;
                }
                var reg = ClientReg.Match(lines[i]);
                if (!reg.Success || RuleContext.InCommentOrString(lines[i], reg.Index)) continue;
                var client = reg.Groups[1].Value;
                client = client.Contains('.') ? client[(client.LastIndexOf('.') + 1)..] : client;
                var addr = new AddressResolver.Address(null, null);
                for (var j = i; j < lines.Length && j <= i + 6; j++)
                {
                    var a = Address.Match(lines[j]);
                    if (!a.Success) continue;
                    addr = AddressResolver.Resolve(a.Groups[1].Success ? a.Groups[1].Value : a.Groups[2].Value, lines, j);
                    break;
                }
                foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, Id, "grpc", client, addr.ConfigKey, addr.Url, file, i + 1, null));
            }
        }
    }
}

/// <summary>按类型名 / 调用名直接判定外部系统的代码模式：Kafka 生产 / 消费、RabbitMQ、Redis、数据库、原生互操作。</summary>
public sealed class InfrastructurePatternRule : IDiscoveryRule
{
    public string Id => "infra-pattern";
    public string Description => "IProducer / IConsumer / IConnectionMultiplexer / DbContext / DllImport 等类型与调用模式";

    private sealed record Pattern(Regex Regex, string SubKind, string Name, string Protocol, bool NameFromMatch = false);

    private static readonly Pattern[] Patterns =
    [
        new(new(@"\bIProducer<|\bProducerBuilder<", RegexOptions.Compiled), "broker", "Kafka", "kafka-produce"),
        // Subscribe 太通用（Rx、事件），只在同文件出现 Kafka 消费者类型时才算
        new(new(@"\bIConsumer<|\bConsumerBuilder<|\.Subscribe\(", RegexOptions.Compiled), "broker", "Kafka", "kafka-consume"),
        new(new(@"\bBasicPublish(?:Async)?\(|\bIModel\b.*Channel", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-produce"),
        new(new(@"\bBasicConsume(?:Async)?\(|EventingBasicConsumer\b", RegexOptions.Compiled), "broker", "RabbitMQ", "amqp-consume"),
        new(new(@"\bIConnectionMultiplexer\b|ConnectionMultiplexer\.Connect|AddRedisClient\(", RegexOptions.Compiled), "cache", "Redis", "redis"),
        new(new(@"\bNpgsqlConnection\b|\bNpgsqlDataSource\b|UseNpgsql\(|AddNpgsqlDbContext<|AddNpgsqlDataSource\(", RegexOptions.Compiled), "database", "PostgreSQL", "sql"),
        new(new(@"\bSqlConnection\b|UseSqlServer\(", RegexOptions.Compiled), "database", "SQL Server", "sql"),
        new(new(@"\bMySqlConnection\b|UseMySql\(", RegexOptions.Compiled), "database", "MySQL", "sql"),
        new(new(@":\s*DbContext\b", RegexOptions.Compiled), "database", "数据库", "sql"),
        new(new(@"\[DllImport\(\s*""([^""]+)""", RegexOptions.Compiled), "native-sdk", "", "native", NameFromMatch: true),
    ];

    private static readonly string[] QuickKeywords = ["IProducer", "ProducerBuilder", "IConsumer", "ConsumerBuilder", ".Subscribe(", "BasicPublish", "BasicConsume", "EventingBasicConsumer", "IModel", "ConnectionMultiplexer", "AddRedisClient", "NpgsqlConnection", "NpgsqlDataSource", "UseNpgsql", "AddNpgsql", "SqlConnection", "UseSqlServer", "MySqlConnection", "UseMySql", "DbContext", "DllImport"];

    public void Apply(RuleContext ctx)
    {
        foreach (var file in ctx.CSharpFiles())
        {
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => QuickKeywords.Any(k => l.Contains(k, StringComparison.Ordinal)))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var kafkaFile = Array.Exists(lines, l => l.Contains("IConsumer<", StringComparison.Ordinal) || l.Contains("ConsumerBuilder<", StringComparison.Ordinal) || l.Contains("Confluent.Kafka", StringComparison.Ordinal));
            for (var i = 0; i < lines.Length; i++)
            {
                if (!QuickKeywords.Any(k => lines[i].Contains(k, StringComparison.Ordinal))) continue;
                foreach (var p in Patterns)
                {
                    var m = p.Regex.Match(lines[i]);
                    if (!m.Success || RuleContext.InCommentOrString(lines[i], m.Index)) continue;
                    if (m.Value.StartsWith(".Subscribe", StringComparison.Ordinal) && !kafkaFile) continue;
                    var name = p.NameFromMatch ? m.Groups[1].Value : p.Name;
                    var evidence = new Evidence(file, i + 1, lines[i].Trim());
                    foreach (var c in containers)
                    {
                        // `: DbContext` 只说明用了数据库；包引用已经知道是哪种数据库时就用那个，不另造一个"数据库"节点
                        var known = p.Name == "数据库"
                            ? ctx.DirectEdges.FirstOrDefault(d => d.ContainerKey == c.Key && d.ExternalKey.StartsWith("external:database:", StringComparison.Ordinal))?.ExternalKey
                            : null;
                        var key = known ?? ctx.External(p.SubKind, name, evidence);
                        if (known is not null) ctx.External(p.SubKind, ctx.Externals[known].Name, evidence);
                        ctx.Outbound.Add(new Outbound(c.Key, Id, p.Protocol, known is not null ? ctx.Externals[known].Name : name, null, null, file, i + 1, key));
                    }
                }
            }
        }
    }
}

/// <summary>csproj 里的本地程序集引用（厂商 SDK、原生封装）→ native-sdk 外部系统。</summary>
public sealed class LocalAssemblyRule : IDiscoveryRule
{
    public string Id => "local-assembly";
    public string Description => "<Reference Include> 的本地程序集（厂商 SDK）";

    public void Apply(RuleContext ctx)
    {
        foreach (var project in ctx.Inventory.Projects)
        {
            var containers = ctx.Containers.Where(c => c.Members.Contains(project.Dir)).ToList();
            if (containers.Count == 0) continue;
            foreach (var r in project.LocalReferences)
            {
                var name = Regex.Replace(r.Name, @"(64|32)$", "");
                var evidence = new Evidence(project.File, r.Line, r.HintPath ?? r.Name);
                var key = ctx.External("native-sdk", name, evidence);
                foreach (var c in containers) ctx.DirectEdges.Add(new DirectEdge(c.Key, key, "native", evidence));
            }
        }
    }
}


/// <summary>
/// 前端的 HTTP 调用 → 出口。认三种写法：fetch(url, { method })、axios.get/post/…(url)、HTTP 客户端变量上的 .get/.post/.put/.patch/.delete(url, …)；
/// 后两种的接收者也可以是括号表达式 (options.client ?? client)，第一个实参也可以是带 url 属性的对象（openapi 生成的客户端）。
/// 客户端变量的判据（宁可漏，不把 map.get(key) 当 HTTP）：由 useXxx() / createXxx() 之类的工厂初始化，且工厂所在文件（或它调用的导入函数，往下两层）
/// 用了 fetch( / axios；或 axios.create(…)；或变量名本身是 api / http / request / *client。
/// URL 实参交给 <see cref="TsConstantResolver"/> 展开常量；工厂链上 axios.create({ baseURL }) 的前缀拼在前面。
/// 路径以前端代理前缀开头（Next.js 里用剩余段 [...x] 转发请求的 API 路由，或 architecture.yaml 的 proxies）就在 ClientName 记"经代理 前缀"，
/// 路由匹配时剥掉。调本应用自己（非代理）的 API 路由不算出口。占位外部系统按剥前缀后的前两段路径聚合，证据只列前 20 条路径加一条汇总。
/// </summary>
public sealed class FrontendHttpRule : IDiscoveryRule
{
    public string Id => "frontend-http";
    public string Description => "fetch / axios / HTTP 客户端（含 useXxx() / createXxx() 包装）的调用，URL 经常量解析，识别前端代理前缀";

    private const string ProxyMark = " 经代理 ";
    private const int EvidencePaths = 20;
    private const int FactoryDepth = 2;
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "get", "post", "put", "patch", "delete" };
    private static readonly HashSet<string> ClientVariableNames = new(StringComparer.Ordinal) { "api", "http", "request" };
    private static readonly HashSet<string> PathIdentifiers = new(StringComparer.Ordinal) { "nextUrl", "pathname", "params", "query" };
    private static readonly Regex AppRoute = new(@"^(?:src/)?app/(?<p>(?:[^/]+/)*)route\.(?:ts|tsx|js|jsx|mts)$", RegexOptions.Compiled);
    private static readonly Regex PagesApi = new(@"^(?:src/)?pages/(?<p>api(?:/[^/]+)*)\.(?:ts|tsx|js|jsx)$", RegexOptions.Compiled);
    private static readonly Regex CatchAll = new(@"^\[\[?\.\.\.(?<n>[\w$]+)\]\]?$", RegexOptions.Compiled);

    /// <summary>认出是 HTTP 调用、但 URL 解析不出路径（或调的是本应用自己的 API 路由）的地方。</summary>
    public sealed record UnresolvedCall(string ContainerKey, string File, int Line, string Reason);
    /// <param name="Evidence">代理路由文件，或声明它的 architecture.yaml。</param>
    public sealed record Proxy(string ContainerKey, string Prefix, string Evidence);

    /// <summary>最近一次 Apply 的解析失败明细（诊断用，不进模型）。</summary>
    public List<UnresolvedCall> Unresolved { get; } = [];
    /// <summary>最近一次 Apply 识别 / 声明的代理前缀。</summary>
    public List<Proxy> Proxies { get; } = [];

    private sealed record Site(int Line, string Method, TsVal Value, string? Base);
    private sealed record Call(string File, int Line, string Method, string Url, string? Proxy, string BusinessPath);
    private sealed record ApiRoute(string[] Template, string File, bool IsProxy, string Prefix);
    private readonly Dictionary<(string File, int Depth), (bool Http, HashSet<string> Bases)> _httpModules = new();

    public void Apply(RuleContext ctx)
    {
        Unresolved.Clear();
        Proxies.Clear();
        _httpModules.Clear();
        var resolver = TsConstantResolver.ForInventory(ctx.Inventory);
        var declared = (ArchitectureDeclarations.Load(ctx.Inventory.Root)?.Document.Proxies ?? [])
            .Where(p => p.Strip && !string.IsNullOrWhiteSpace(p.Prefix) && p.Prefix.Trim().Trim('/').Length > 0)
            .Select(p => "/" + p.Prefix!.Trim().Trim('/')).Distinct(StringComparer.Ordinal).ToList();
        foreach (var c in ctx.Containers.Where(c => c.SubKind == "frontend"))
        {
            var root = c.Path ?? "";
            // 只要 TS / JS：清单里还有 .cs / .go / .py（前端目录在仓库根时会把它们都圈进来）
            var files = ctx.Inventory.SourceFiles.Where(f => RuleContext.Under(f, root) && !RuleContext.IsTestFile(f)
                && PolyText.LangOf(f) == SourceLang.JavaScript && !f.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)).ToList();
            var routes = ApiRoutes(resolver, root, files);
            foreach (var r in routes.Where(r => r.IsProxy)) Proxies.Add(new Proxy(c.Key, r.Prefix, r.File));
            foreach (var d in declared)
                if (!Proxies.Any(p => p.ContainerKey == c.Key && p.Prefix == d)) Proxies.Add(new Proxy(c.Key, d, ArchitectureDeclarations.RelativePath));
            var prefixes = Proxies.Where(p => p.ContainerKey == c.Key).OrderByDescending(p => p.Prefix.Length).ToList();
            var own = routes.Where(r => !r.IsProxy).ToList();

            var calls = new List<Call>();
            foreach (var file in files)
            {
                var m = resolver.Module(file);
                if (m is null) continue;
                foreach (var site in Sites(resolver, m))
                    if (Classify(c.Key, file, site, prefixes, own) is { } call) calls.Add(call);
            }
            Register(ctx, c.Key, calls, prefixes);
        }
    }

    // ───── 调用处 ─────

    private IEnumerable<Site> Sites(TsConstantResolver resolver, TsModule m)
    {
        var t = m.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            // (options?.client ?? client).get<…>({ url: '/api/v1/items/' })：openapi 生成客户端（@hey-api/openapi-ts）的写法。
            // 接收者是括号表达式（不是函数调用的结果），取括号里最后一个标识符判断是不是 HTTP 客户端
            if (t[i].Kind == TsTokKind.Punct && t[i].Text == ")" && m.IsPunct(i + 1, ".") && m.IsIdent(i + 2) && Verbs.Contains(t[i + 2].Text) && m.IsIdent(i - 1)
                && IsParenthesizedExpression(m, i))
            {
                var pOpen = i + 3;
                if (m.IsPunct(pOpen, "<")) pOpen = SkipGeneric(m, pOpen);
                if (m.IsPunct(pOpen, "(") && IsHttpClient(resolver, m, i - 1, m.IsPunct(i - 2, ".") || m.IsPunct(i - 2, "?."), out var parenBase))
                {
                    var (pArg, _) = Args(m, pOpen);
                    yield return new Site(m.LineOf(t[i].Offset), t[i + 2].Text.ToUpperInvariant(), UrlArg(resolver, m, pArg), parenBase);
                }
                continue;
            }
            if (t[i].Kind != TsTokKind.Ident) continue;
            var name = t[i].Text;
            var memberOf = m.IsPunct(i - 1, ".") || m.IsPunct(i - 1, "?.");
            // fetch(url, { method })；window.fetch / globalThis.fetch 也算
            if (name == "fetch" && m.IsPunct(i + 1, "(") && (!memberOf || m.IsIdent(i - 2, "window") || m.IsIdent(i - 2, "globalThis")))
            {
                var (a0, a1) = Args(m, i + 1);
                yield return new Site(m.LineOf(t[i].Offset), FetchMethod(m, a1) ?? "GET", resolver.EvalTokens(m, a0.Start, a0.End), null);
                continue;
            }
            if (!m.IsPunct(i + 1, ".") || !m.IsIdent(i + 2)) continue;
            var verb = t[i + 2].Text;
            var isAxios = name == "axios" && !memberOf;
            if (!Verbs.Contains(verb) && !(isAxios && verb == "request")) continue;
            var open = i + 3;
            if (m.IsPunct(open, "<")) open = SkipGeneric(m, open);
            if (!m.IsPunct(open, "(")) continue;
            string? baseUrl = null;
            if (!isAxios && !IsHttpClient(resolver, m, i, memberOf, out baseUrl)) continue;
            var (arg0, _) = Args(m, open);
            yield return new Site(m.LineOf(t[i].Offset), verb == "request" ? "GET" : verb.ToUpperInvariant(), UrlArg(resolver, m, arg0), baseUrl);
        }
    }

    /// <summary>
    /// 第一个实参的取值：对象字面量 { url: …, … } 且 url 能解析成字符串时取 url 属性（openapi 生成客户端、axios.request 的配置对象）；
    /// 其余照旧对整个实参求值（解析不了的对象字面量仍报"不是字符串: 对象字面量"）。
    /// </summary>
    private static TsVal UrlArg(TsConstantResolver resolver, TsModule m, (int Start, int End) arg)
    {
        if (m.IsPunct(arg.Start, "{"))
        {
            var depth = 0;
            for (var i = arg.Start; i < arg.End; i++)
            {
                var tok = m.Tokens[i];
                if (tok.Kind == TsTokKind.Punct && tok.Text is "{" or "(" or "[") depth++;
                else if (tok.Kind == TsTokKind.Punct && tok.Text is "}" or ")" or "]") depth--;
                else if (depth == 1 && m.IsIdent(i, "url") && m.IsPunct(i + 1, ":") && (m.IsPunct(i - 1, "{") || m.IsPunct(i - 1, ",")))
                {
                    var value = resolver.EvalTokens(m, i + 2, ArgEnd(m, i + 2));
                    if (TsConstantResolver.ToResolution(value).Value is not null) return value;
                    break;
                }
            }
        }
        return resolver.EvalTokens(m, arg.Start, arg.End);
    }

    /// <summary>tokens[close] 是 ")"：它配对的 "(" 前面不是标识符 / ")" / "]"（那是函数调用），即一个括号表达式。</summary>
    private static bool IsParenthesizedExpression(TsModule m, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            var tok = m.Tokens[i];
            if (tok.Kind != TsTokKind.Punct) continue;
            if (tok.Text == ")") depth++;
            else if (tok.Text == "(" && --depth == 0)
                return !(m.IsIdent(i - 1) || m.IsPunct(i - 1, ")") || m.IsPunct(i - 1, "]") || m.IsPunct(i - 1, "?.")) || m.IsIdent(i - 1, "return") || m.IsIdent(i - 1, "await");
            else if (tok.Text is ";" or "{" or "}") return false;
        }
        return false;
    }

    /// <summary>tokens[i] 是 .get/.post… 的接收者：判断它是不是 HTTP 客户端，顺带拿到工厂链上的 baseURL。</summary>
    private bool IsHttpClient(TsConstantResolver resolver, TsModule m, int i, bool memberOf, out string? baseUrl)
    {
        baseUrl = null;
        var name = m.Tokens[i].Text;
        var byName = ClientVariableNames.Contains(name) || name.EndsWith("client", StringComparison.OrdinalIgnoreCase);
        if (memberOf) return byName; // this.http.get / this.client.get：只认名字
        var decl = resolver.FindDecl(m, name, m.Tokens[i].Offset);
        if (decl is { TokenIndex: >= 0 })
        {
            var j = decl.TokenIndex;
            if (m.IsIdent(j, "await")) j++;
            if (m.IsIdent(j, "axios") && m.IsPunct(j + 1, ".") && m.IsIdent(j + 2, "create") && m.IsPunct(j + 3, "("))
            {
                var close = TsParser.MatchClose(m.Tokens, j + 3);
                baseUrl = SingleBase(BaseUrls(resolver, m, j + 3, close < 0 ? m.Tokens.Count : close));
                return true;
            }
            if (m.IsIdent(j) && m.IsPunct(j + 1, "("))
            {
                var (http, b) = FactoryHttp(resolver, m, m.Tokens[j].Text);
                if (http) { baseUrl = b; return true; }
            }
        }
        else if (decl is null && resolver.ImportTarget(m, name).Target is { } imported)
        {
            // import api from "@/lib/api"：导入的就是客户端实例。只看那个文件自己（不往下跟），免得把导入了 API hook 的工具模块当客户端
            var info = HttpModule(resolver, imported, FactoryDepth);
            if (info.Http) { baseUrl = SingleBase(info.Bases); return true; }
        }
        return byName;
    }

    /// <summary>
    /// 工厂函数 fn（在 m 里被调用）是否返回 HTTP 客户端。只看 fn 自己的声明范围（导入的跟到定义处，含桶文件再导出）：
    /// 范围里直接出现 fetch( / axios；或调用 / new 了导入的函数，且那个模块发 HTTP（再往下跟 FactoryDepth 层）；
    /// 或调用了同文件的函数 / 类，且这个文件本身直接用了 fetch( / axios。组件文件里的 useMemo(() => new Map()) 这类不会被当成客户端。
    /// </summary>
    private (bool Http, string? Base) FactoryHttp(TsConstantResolver resolver, TsModule m, string fn)
    {
        (TsModule Module, TsDecl Decl)? found;
        if (m.Imports.TryGetValue(fn, out var imp))
            found = resolver.ImportTarget(m, fn).Target is { } target ? resolver.ExportDecl(target, imp.Imported) : null;
        else
            found = resolver.FindDecl(m, fn, int.MaxValue) is { } d ? (m, d) : null;
        if (found is not { } f) return (false, null);
        var home = f.Module;
        var (start, end) = home.DeclRange(f.Decl);
        var http = false;
        var callsLocal = false;
        var bases = new HashSet<string>(BaseUrls(resolver, home, start, end), StringComparer.Ordinal);
        var t = home.Tokens;
        for (var i = start + 1; i < end; i++)
        {
            if (t[i].Kind != TsTokKind.Ident) continue;
            var name = t[i].Text;
            var memberOf = home.IsPunct(i - 1, ".") || home.IsPunct(i - 1, "?.");
            if (name == "axios" || (name == "fetch" && home.IsPunct(i + 1, "(") && !memberOf)) { http = true; continue; }
            if (memberOf || !(home.IsPunct(i + 1, "(") || home.IsIdent(i - 1, "new"))) continue;
            if (home.Imports.ContainsKey(name))
            {
                if (resolver.ImportTarget(home, name).Target is not { } target || target.File == home.File) continue;
                var sub = HttpModule(resolver, target, 1);
                http |= sub.Http;
                bases.UnionWith(sub.Bases);
            }
            else if (home.Decls.Any(d => d.Name == name)) callsLocal = true; // new Map()、Promise() 这类全局不算
        }
        if (callsLocal)
        {
            var direct = HttpModule(resolver, home, FactoryDepth);
            if (direct.Http) { http = true; bases.UnionWith(direct.Bases); }
        }
        return (http, http ? SingleBase(bases) : null);
    }

    /// <summary>模块是否发 HTTP（出现 fetch( 或 axios），以及其中 baseURL: … 的取值；跟着它调用的导入函数再往下 FactoryDepth 层。</summary>
    private (bool Http, HashSet<string> Bases) HttpModule(TsConstantResolver resolver, TsModule m, int depth)
    {
        if (_httpModules.TryGetValue((m.File, depth), out var cached)) return cached;
        var bases = new HashSet<string>(StringComparer.Ordinal);
        _httpModules[(m.File, depth)] = (false, bases); // 循环导入保护
        var http = false;
        var called = new HashSet<string>(StringComparer.Ordinal);
        var t = m.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind != TsTokKind.Ident) continue;
            var memberOf = m.IsPunct(i - 1, ".") || m.IsPunct(i - 1, "?.");
            if (t[i].Text == "axios" || (t[i].Text == "fetch" && m.IsPunct(i + 1, "(") && !memberOf)) http = true;
            if (t[i].Text == "baseURL" && m.IsPunct(i + 1, ":"))
            {
                var end = ArgEnd(m, i + 2);
                if (TsConstantResolver.ToResolution(resolver.EvalTokens(m, i + 2, end)).Value is { } b && (b.StartsWith('/') || IsAbsolute(b))) bases.Add(b);
            }
            if (!memberOf && m.IsPunct(i + 1, "(") && m.Imports.ContainsKey(t[i].Text)) called.Add(t[i].Text);
        }
        if (depth < FactoryDepth)
            foreach (var fn in called)
            {
                if (resolver.ImportTarget(m, fn).Target is not { } target || target.File == m.File) continue;
                var sub = HttpModule(resolver, target, depth + 1);
                http |= sub.Http;
                bases.UnionWith(sub.Bases);
            }
        return _httpModules[(m.File, depth)] = (http, bases);
    }

    private static IEnumerable<string> BaseUrls(TsConstantResolver resolver, TsModule m, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (m.IsIdent(i, "baseURL") && m.IsPunct(i + 1, ":")
                && TsConstantResolver.ToResolution(resolver.EvalTokens(m, i + 2, ArgEnd(m, i + 2))).Value is { } b && (b.StartsWith('/') || IsAbsolute(b)))
                yield return b;
    }

    /// <summary>多个不同的 baseURL 不猜。</summary>
    private static string? SingleBase(IEnumerable<string> bases)
    {
        var distinct = bases.Distinct(StringComparer.Ordinal).Take(2).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>open 是 "(" 的下标：第一、第二个实参的 token 范围（同层逗号分隔）。实参可以跨行。</summary>
    private static ((int Start, int End) First, (int Start, int End) Second) Args(TsModule m, int open)
    {
        var first = ArgEnd(m, open + 1);
        var second = m.IsPunct(first, ",") ? (first + 1, ArgEnd(m, first + 1)) : (first, first);
        return ((open + 1, first), second);
    }

    /// <summary>从 start 起到同层的 , ) ] } 为止（不含）。</summary>
    private static int ArgEnd(TsModule m, int start)
    {
        var d = 0;
        var t = m.Tokens;
        for (var i = start; i < t.Count; i++)
        {
            if (t[i].Kind != TsTokKind.Punct) continue;
            var p = t[i].Text;
            if (p is "(" or "[" or "{") d++;
            else if (p is ")" or "]" or "}") { if (d == 0) return i; d--; }
            else if (d == 0 && p is "," or ";") return i;
        }
        return t.Count;
    }

    /// <summary>fetch 第二个实参里的 method: "POST"。</summary>
    private static string? FetchMethod(TsModule m, (int Start, int End) options)
    {
        for (var i = options.Start; i < options.End; i++)
            if (m.IsIdent(i, "method") && m.IsPunct(i + 1, ":") && i + 2 < m.Tokens.Count && m.Tokens[i + 2].Kind == TsTokKind.Str)
                return m.Tokens[i + 2].Text.ToUpperInvariant();
        return null;
    }

    /// <summary>open 是泛型实参的 "&lt;"：返回配对 "&gt;" 之后的下标；配不上返回 open（调用方再看是不是 "("）。</summary>
    private static int SkipGeneric(TsModule m, int open)
    {
        var d = 0;
        for (var i = open; i < m.Tokens.Count; i++)
        {
            var t = m.Tokens[i];
            if (t.Kind != TsTokKind.Punct) continue;
            if (t.Text == "<") d++;
            else if (t.Text == ">") { d--; if (d == 0) return i + 1; }
            else if (t.Text is ";" or "{" or "}" or "=>") return open;
        }
        return open;
    }

    private Call? Classify(string containerKey, string file, Site site, IReadOnlyList<Proxy> proxies, IReadOnlyList<ApiRoute> own)
    {
        var res = TsConstantResolver.ToResolution(site.Value);
        if (res.Value is null) { Unresolved.Add(new UnresolvedCall(containerKey, file, site.Line, res.Failure ?? "无法解析")); return null; }
        var raw = res.Value;
        if (site.Base is not null && !IsAbsolute(raw) && !raw.StartsWith("${", StringComparison.Ordinal)) raw = site.Base.TrimEnd('/') + "/" + raw.TrimStart('/');
        var path = PathOf(raw);
        if (path is null)
        {
            Unresolved.Add(new UnresolvedCall(containerKey, file, site.Line, (OnlyHoles(raw) ? "运行时变量: " : "不是路径: ") + raw));
            return null;
        }
        if (IsAbsolute(path)) return new Call(file, site.Line, site.Method, path, null, path);
        var proxy = proxies.FirstOrDefault(p => path == p.Prefix || path.StartsWith(p.Prefix + "/", StringComparison.Ordinal))?.Prefix;
        if (proxy is null && own.Any(r => RouteMatches(r.Template, path)))
        {
            Unresolved.Add(new UnresolvedCall(containerKey, file, site.Line, "本应用 API 路由: " + path));
            return null;
        }
        var business = proxy is null ? path : path.Length == proxy.Length ? "/" : path[proxy.Length..];
        return new Call(file, site.Line, site.Method, path, proxy, business);
    }

    // ───── 登记 ─────

    private void Register(RuleContext ctx, string containerKey, List<Call> calls, IReadOnlyList<Proxy> proxies)
    {
        foreach (var call in calls)
        {
            string key;
            if (IsAbsolute(call.Url))
            {
                if (!Uri.TryCreate(call.Url, UriKind.Absolute, out var u)) continue;
                key = ctx.External("http-service", u.Host, new Evidence(call.File, call.Line, call.Url));
            }
            else key = $"external:http-service:{RuleContext.Slug("API " + Group(call.BusinessPath))}";
            ctx.Outbound.Add(new Outbound(containerKey, Id, "http", FormatCall(call.Method, call.Proxy), null, call.Url, call.File, call.Line, key));
        }
        // 占位外部系统：每个 API 根一个节点；证据按路径聚合（次数降序）列前 20 条，再加一条汇总（业务段分布、经哪个代理）
        foreach (var g in calls.Where(c => !IsAbsolute(c.Url)).GroupBy(c => Group(c.BusinessPath)))
        {
            var name = "API " + g.Key;
            var byPath = g.GroupBy(c => (c.BusinessPath, c.Method))
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key.BusinessPath, StringComparer.Ordinal).ThenBy(x => x.Key.Method, StringComparer.Ordinal).ToList();
            foreach (var p in byPath.Take(EvidencePaths))
            {
                var first = p.First();
                ctx.External("http-service", name, new Evidence(first.File, first.Line, $"{p.Key.Method} {p.Key.BusinessPath}（{p.Count()} 处）"));
            }
            var paths = g.Select(c => c.BusinessPath).Distinct(StringComparer.Ordinal).Count();
            var segs = g.GroupBy(c => BusinessSegment(c.BusinessPath)).OrderByDescending(s => s.Count()).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
            var note = $"共 {g.Count()} 处调用、{paths} 条路径{(byPath.Count > EvidencePaths ? $"（证据列前 {EvidencePaths} 条）" : "")}；按业务段："
                       + string.Join("、", segs.Take(10).Select(s => $"{s.Key} {s.Count()}")) + (segs.Count > 10 ? $" 等 {segs.Count} 段" : "");
            foreach (var p in g.Where(c => c.Proxy is not null).GroupBy(c => c.Proxy!).OrderBy(p => p.Key, StringComparer.Ordinal))
                note += $"；经 {p.Key} 代理 {p.Count()} 处";
            var usedProxy = g.FirstOrDefault(c => c.Proxy is not null)?.Proxy;
            var summaryFile = usedProxy is not null ? proxies.First(p => p.Prefix == usedProxy).Evidence : g.First().File;
            ctx.External("http-service", name, new Evidence(summaryFile, null, note));
        }
    }

    // ───── Next.js API 路由与代理识别 ─────

    /// <summary>
    /// 容器里的 Next.js API 路由（app/**/route.ts、pages/api/**）。最后一段是剩余段 [...x] / [[...x]]、且文件里有出站调用（fetch( / axios / .request(）
    /// 并用到请求路径（nextUrl / pathname / params / query / req.url / 剩余段参数名）的，算代理，前缀 = 去掉剩余段的路由路径。
    /// </summary>
    private static List<ApiRoute> ApiRoutes(TsConstantResolver resolver, string root, List<string> files)
    {
        var list = new List<ApiRoute>();
        foreach (var f in files)
        {
            var rel = root.Length == 0 ? f : f[(root.Length + 1)..];
            string[] segs;
            var app = AppRoute.Match(rel);
            if (app.Success)
                segs = app.Groups["p"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !(s.StartsWith('(') && s.EndsWith(')')) && !s.StartsWith('@')).ToArray(); // 路由组 (x)、并行路由 @x 不进路径
            else
            {
                var pages = PagesApi.Match(rel);
                if (!pages.Success) continue;
                segs = pages.Groups["p"].Value.Split('/');
                if (segs[^1] == "index") segs = segs[..^1];
            }
            var isProxy = false;
            var prefix = "/" + string.Join('/', segs);
            if (segs.Length >= 2 && CatchAll.Match(segs[^1]) is { Success: true } ca && resolver.Module(f) is { } m && Forwards(m, ca.Groups["n"].Value))
            {
                isProxy = true;
                prefix = "/" + string.Join('/', segs[..^1]);
            }
            list.Add(new ApiRoute(segs, f, isProxy, prefix));
        }
        return list;
    }

    private static bool Forwards(TsModule m, string restParam)
    {
        bool outgoing = false, usesPath = false;
        var t = m.Tokens;
        for (var i = 0; i < t.Count; i++)
        {
            if (t[i].Kind != TsTokKind.Ident) continue;
            var name = t[i].Text;
            var memberOf = m.IsPunct(i - 1, ".") || m.IsPunct(i - 1, "?.");
            if (name == "axios" || (name == "fetch" && m.IsPunct(i + 1, "(")) || (name == "request" && memberOf && m.IsPunct(i + 1, "("))) outgoing = true;
            if (PathIdentifiers.Contains(name) || name == restParam || (name == "url" && memberOf)) usesPath = true;
        }
        return outgoing && usesPath;
    }

    /// <summary>Next 路由段模板对具体路径：[x] 任意一段，[...x] 其余至少一段，[[...x]] 其余可为空；路径里的 ${…} 段匹配任意段。</summary>
    private static bool RouteMatches(string[] template, string path)
    {
        var p = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var k = 0; k < template.Length; k++)
        {
            var seg = template[k];
            if (CatchAll.IsMatch(seg)) return seg.StartsWith("[[", StringComparison.Ordinal) ? p.Length >= k : p.Length > k;
            if (k >= p.Length) return false;
            if (seg.StartsWith('[') || p[k].Contains("${", StringComparison.Ordinal)) continue;
            if (!string.Equals(seg, p[k], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return template.Length == p.Length;
    }

    // ───── 公共小工具（RouteLinker、EdgeMatcher 也用） ─────

    /// <summary>出口的 ClientName：方法，经代理时再加" 经代理 前缀"。库里没有单独的方法 / 代理列，借这一列存。</summary>
    public static string FormatCall(string method, string? proxyPrefix) => proxyPrefix is null ? method : method + ProxyMark + proxyPrefix;

    /// <summary><see cref="FormatCall"/> 的逆：(方法, 代理前缀)；旧库里的前端出口 ClientName 为 null → (null, null)。</summary>
    public static (string? Method, string? ProxyPrefix) ParseCall(string? clientName)
    {
        if (string.IsNullOrEmpty(clientName)) return (null, null);
        var i = clientName.IndexOf(ProxyMark, StringComparison.Ordinal);
        return i < 0 ? (clientName, null) : (clientName[..i], clientName[(i + ProxyMark.Length)..]);
    }

    /// <summary>后端看到的路径：经代理（且 Url 以该前缀开头）就剥掉前缀。</summary>
    public static string BackendPath(string url, string? proxyPrefix)
    {
        if (proxyPrefix is null) return url;
        if (url == proxyPrefix) return "/";
        return url.StartsWith(proxyPrefix + "/", StringComparison.Ordinal) ? url[proxyPrefix.Length..] : url;
    }

    /// <summary>
    /// 展开后的 URL → 路径：绝对 URL 原样；开头的 ${…}（不知道的主机 / 基地址）去掉；去掉查询串与片段；中间的 ${id} 保留（路由匹配时当任意段）。
    /// `${base}/api/v5/orders/${id}` → /api/v5/orders/${id}；"/api/v5/users" 原样；纯变量或不是 / 开头 → null。
    /// </summary>
    public static string? PathOf(string raw)
    {
        if (IsAbsolute(raw)) return raw;
        var s = raw;
        while (s.StartsWith("${", StringComparison.Ordinal))
        {
            var close = HoleEnd(s);
            if (close < 0) return null;
            s = s[(close + 1)..];
        }
        if (!s.StartsWith('/')) return null;
        var q = s.IndexOfAny(['?', '#']);
        return q >= 0 ? s[..q] : s;
    }

    /// <summary>整段都是 ${…}（`${url}`、`${base}${path}`）：运行时才知道的地址。</summary>
    private static bool OnlyHoles(string raw)
    {
        var s = raw;
        while (s.StartsWith("${", StringComparison.Ordinal))
        {
            var close = HoleEnd(s);
            if (close < 0) return false;
            s = s[(close + 1)..];
        }
        return s.Length == 0 && raw.Length > 0;
    }

    private static int HoleEnd(string s)
    {
        var d = 0;
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '{') d++;
            else if (s[i] == '}') { d--; if (d == 0) return i; }
        }
        return -1;
    }

    private static bool IsAbsolute(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>前两段路径：/api/v5/orders → /api/v5；/graphql → /graphql；含 ${…} 的运行时段及其后不要（/realms/${realm}/… → /realms）。</summary>
    public static string Group(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segs.Take(2).TakeWhile(s => !s.Contains("${", StringComparison.Ordinal)));
    }

    /// <summary>API 根之后的第一段（/api/v5/mt5/groups → mt5）；路径不够长取最后一段。</summary>
    private static string BusinessSegment(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length > 2 ? segs[2] : segs.Length > 0 ? segs[^1] : "/";
    }
}