namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>包引用直接说明碰了什么外部系统：Npgsql → PostgreSQL 等。边挂到引用该包的项目所属的每个容器，置信度 0.5。</summary>
public sealed class PackageReferenceRule : IDiscoveryRule
{
    public string Id => "package-reference";
    public string Description => "按 NuGet 包引用推断外部系统（数据库、缓存、消息、存储、遥测）";

    /// <summary>包名前缀 → (外部系统种类, 名字, 协议)。按顺序匹配，先命中先用。</summary>
    public static readonly IReadOnlyList<(string Prefix, string SubKind, string Name, string Protocol)> Map =
    [
        // .NET Aspire 的客户端集成包（包名以 Aspire. 开头，后面是被包装的库）
        ("Aspire.Npgsql", "database", "PostgreSQL", "sql"),
        ("Aspire.Microsoft.EntityFrameworkCore.SqlServer", "database", "SQL Server", "sql"),
        ("Aspire.Microsoft.Data.SqlClient", "database", "SQL Server", "sql"),
        ("Aspire.Pomelo.EntityFrameworkCore.MySql", "database", "MySQL", "sql"),
        ("Aspire.MongoDB.Driver", "database", "MongoDB", "mongo"),
        ("Aspire.StackExchange.Redis", "cache", "Redis", "redis"),
        ("Aspire.RabbitMQ.Client", "broker", "RabbitMQ", "amqp"),
        ("Aspire.Confluent.Kafka", "broker", "Kafka", "kafka"),
        ("Aspire.Azure.Messaging.ServiceBus", "broker", "Azure Service Bus", "amqp"),
        ("Aspire.Azure.Storage.Blobs", "storage", "Azure Blob Storage", "http"),
        ("Aspire.Azure.AI.OpenAI", "http-service", "Azure OpenAI", "http"),
        ("Aspire.OpenAI", "http-service", "OpenAI", "http"),
        ("Azure.AI.OpenAI", "http-service", "Azure OpenAI", "http"),
        ("OpenAI", "http-service", "OpenAI", "http"),
        ("OllamaSharp", "http-service", "Ollama", "http"),
        ("Duende.IdentityServer", "identity", "IdentityServer（本进程内）", "oidc"),
        ("Npgsql", "database", "PostgreSQL", "sql"),
        ("Microsoft.EntityFrameworkCore.SqlServer", "database", "SQL Server", "sql"),
        ("Microsoft.Data.SqlClient", "database", "SQL Server", "sql"),
        ("Pomelo.EntityFrameworkCore.MySql", "database", "MySQL", "sql"),
        ("MySql", "database", "MySQL", "sql"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "database", "SQLite", "sql"),
        ("MongoDB.Driver", "database", "MongoDB", "mongo"),
        ("Elastic.Clients.Elasticsearch", "database", "Elasticsearch", "http"),
        ("NEST", "database", "Elasticsearch", "http"),
        ("StackExchange.Redis", "cache", "Redis", "redis"),
        ("Confluent.Kafka", "broker", "Kafka", "kafka"),
        ("RabbitMQ.Client", "broker", "RabbitMQ", "amqp"),
        ("MassTransit.RabbitMQ", "broker", "RabbitMQ", "amqp"),
        ("AWSSDK.S3", "storage", "AWS S3", "http"),
        ("Azure.Storage.Blobs", "storage", "Azure Blob Storage", "http"),
        ("OpenTelemetry.Exporter.OpenTelemetryProtocol", "observability", "OpenTelemetry Collector", "otlp"),
        // 交易服务器厂商 SDK（以 NuGet 包形式引用的原生封装）
        ("MetaTrader4.Manager", "native-sdk", "MetaTrader4 Manager API", "native"),
        ("P23.MetaTrader4", "native-sdk", "MetaTrader4 Manager API", "native"),
        ("MetaQuotes.MT5", "native-sdk", "MetaTrader5 Manager API", "native"),
    ];

    /// <summary>这些协议只是"用了这个系统"，代码规则给出更具体的方向（kafka-produce / kafka-consume）时以代码为准。</summary>
    public static readonly HashSet<string> GenericProtocols = ["kafka", "amqp"];

    public void Apply(RuleContext ctx)
    {
        foreach (var project in ctx.Inventory.Projects)
        {
            var containers = ctx.Containers.Where(c => c.Members.Contains(project.Dir)).ToList();
            if (containers.Count == 0) continue;
            foreach (var pkg in project.Packages)
            {
                var hit = Map.FirstOrDefault(m => pkg.Name.Equals(m.Prefix, StringComparison.OrdinalIgnoreCase) || pkg.Name.StartsWith(m.Prefix + ".", StringComparison.OrdinalIgnoreCase));
                if (hit == default) continue;
                var evidence = new Evidence(project.File, pkg.Line, pkg.Name);
                var key = ctx.External(hit.SubKind, hit.Name, evidence);
                foreach (var c in containers) ctx.DirectEdges.Add(new DirectEdge(c.Key, key, hit.Protocol, evidence));
            }
        }
    }
}
