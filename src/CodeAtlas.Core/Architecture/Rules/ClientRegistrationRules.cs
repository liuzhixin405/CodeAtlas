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

/// <summary>前端的 fetch / axios 调用：取 URL 的路径部分，按前两段路径聚成一个"后端 API"外部系统（5c 再按路由对到具体容器）。</summary>
public sealed class FrontendHttpRule : IDiscoveryRule
{
    public string Id => "frontend-http";
    public string Description => "fetch / axios / http 客户端调用的 URL 字面量";

    private static readonly Regex Call = new(@"\b(?:fetch|axios\.(?:get|post|put|delete|patch|request)|\w*[cC]lient\.(?:get|post|put|delete|patch))\s*\(\s*([""'`])(.*?)\1", RegexOptions.Compiled);

    public void Apply(RuleContext ctx)
    {
        foreach (var c in ctx.Containers.Where(c => c.SubKind == "frontend"))
        {
            foreach (var file in ctx.Inventory.SourceFiles)
            {
                if (!RuleContext.Under(file, c.Path ?? "") || RuleContext.IsTestFile(file) || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                var lines = ctx.Lines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in Call.Matches(lines[i]))
                    {
                        var raw = m.Groups[2].Value;
                        var path = PathOf(raw);
                        if (path is null) continue;
                        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!Uri.TryCreate(path, UriKind.Absolute, out var u)) continue;
                            var ekey = ctx.External("http-service", u.Host, new Evidence(file, i + 1, raw));
                            ctx.Outbound.Add(new Outbound(c.Key, Id, "http", null, null, path, file, i + 1, ekey));
                            continue;
                        }
                        var group = Group(path);
                        var key = ctx.External("http-service", "API " + group, new Evidence(file, i + 1, raw));
                        ctx.Outbound.Add(new Outbound(c.Key, Id, "http", null, null, path, file, i + 1, key));
                    }
                }
            }
        }
    }

    /// <summary>`${base}/api/v5/orders` → /api/v5/orders；"/api/v5/users" 原样；纯变量或拼不出路径 → null。</summary>
    public static string? PathOf(string raw)
    {
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return raw;
        var s = raw;
        if (s.Contains("${", StringComparison.Ordinal))
        {
            var close = s.LastIndexOf('}');
            if (close < 0) return null;
            s = s[(close + 1)..];
        }
        if (!s.StartsWith('/')) return null;
        var q = s.IndexOf('?');
        return q >= 0 ? s[..q] : s;
    }

    /// <summary>前两段路径：/api/v5/orders → /api/v5；/graphql → /graphql。</summary>
    public static string Group(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', segs.Take(2));
    }
}
