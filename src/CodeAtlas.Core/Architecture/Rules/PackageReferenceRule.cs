namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// 包引用直接说明碰了什么外部系统：Npgsql → PostgreSQL、gorm.io/driver/sqlite → SQLite、psycopg → PostgreSQL 等。
/// .NET 的边挂到引用该包的项目所属的每个容器；go.mod 的挂到模块的每个 main 容器；Python 的挂到该项目的容器。置信度 0.5。
/// </summary>
public sealed class PackageReferenceRule : IDiscoveryRule
{
    public string Id => "package-reference";
    public string Description => "按 NuGet 包（含 packages.config）、go.mod require、Python 依赖、Maven / Gradle 依赖与 Spring 数据源配置推断外部系统（数据库、缓存、消息、存储、遥测、邮件）";

    /// <summary>包名前缀 → (外部系统种类, 名字, 协议)。按顺序匹配，先命中先用。</summary>
    /// <summary>
    /// 进程内的嵌入式数据库：库文件或内存，跟着应用进程走，不是另起的数据库服务。名字带"（内嵌）"，和 PostgreSQL 这类外部服务区分开
    /// （petclinic 的 H2 / HSQLDB 是默认内存库，MySQL / PostgreSQL 才是可选的外部库）。
    /// </summary>
    public const string SQLite = "SQLite（内嵌）", H2 = "H2（内嵌）", HSQLDB = "HSQLDB（内嵌）", Derby = "Derby（内嵌）";

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
        ("Microsoft.EntityFrameworkCore.Sqlite", "database", SQLite, "sql"),
        ("MongoDB.Driver", "database", "MongoDB", "mongo"),
        ("Elastic.Clients.Elasticsearch", "database", "Elasticsearch", "http"),
        ("NEST", "database", "Elasticsearch", "http"),
        ("StackExchange.Redis", "cache", "Redis", "redis"),
        // IDistributedCache 的 Redis 实现（AddStackExchangeRedisCache）：包名不以 StackExchange.Redis 开头
        ("Microsoft.Extensions.Caching.StackExchangeRedis", "cache", "Redis", "redis"),
        ("Microsoft.AspNetCore.DataProtection.StackExchangeRedis", "cache", "Redis", "redis"),
        ("Google.Cloud.Spanner", "database", "Cloud Spanner", "grpc"),
        ("Confluent.Kafka", "broker", "Kafka", "kafka"),
        ("RabbitMQ.Client", "broker", "RabbitMQ", "amqp"),
        ("MassTransit.RabbitMQ", "broker", "RabbitMQ", "amqp"),
        ("AWSSDK.S3", "storage", "AWS S3", "http"),
        ("AWSSDK.SimpleSystemsManagement", "config-center", "AWS SSM Parameter Store", "http"),
        ("Azure.Storage.Blobs", "storage", "Azure Blob Storage", "http"),
        ("OpenTelemetry.Exporter.OpenTelemetryProtocol", "observability", "OpenTelemetry Collector", "otlp"),
        ("AWSSDK.CloudWatchLogs", "observability", "AWS CloudWatch Logs", "http"),
        ("AWS.Logger", "observability", "AWS CloudWatch Logs", "http"),
        // 交易服务器厂商 SDK（以 NuGet 包形式引用的原生封装）
        ("MetaTrader4.Manager", "native-sdk", "MetaTrader4 Manager API", "native"),
        ("P23.MetaTrader4", "native-sdk", "MetaTrader4 Manager API", "native"),
        ("MetaQuotes.MT5", "native-sdk", "MetaTrader5 Manager API", "native"),
    ];

    /// <summary>
    /// go.mod require 的模块路径前缀 → 外部系统。按路径段匹配（等于前缀或以"前缀/"开头，带 /v5 这种主版本后缀也算）。
    /// otel 只认 OTLP 导出器（API / SDK / 插桩不是外部系统）；cloud.google.com/go 只认能说清是什么系统的几个包（profiler / trace 归观测性），
    /// 其余（auth、compute/metadata、secretmanager…）不算。
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, string SubKind, string Name, string Protocol)> GoMap =
    [
        ("gorm.io/driver/postgres", "database", "PostgreSQL", "sql"),
        ("gorm.io/driver/mysql", "database", "MySQL", "sql"),
        ("gorm.io/driver/sqlite", "database", SQLite, "sql"),
        ("gorm.io/driver/sqlserver", "database", "SQL Server", "sql"),
        ("gorm.io/driver/clickhouse", "database", "ClickHouse", "sql"),
        ("github.com/jackc/pgx", "database", "PostgreSQL", "sql"),
        ("github.com/lib/pq", "database", "PostgreSQL", "sql"),
        ("github.com/go-sql-driver/mysql", "database", "MySQL", "sql"),
        ("github.com/mattn/go-sqlite3", "database", SQLite, "sql"),
        ("modernc.org/sqlite", "database", SQLite, "sql"),
        ("github.com/glebarez/sqlite", "database", SQLite, "sql"),
        ("github.com/microsoft/go-mssqldb", "database", "SQL Server", "sql"),
        ("github.com/denisenkom/go-mssqldb", "database", "SQL Server", "sql"),
        ("go.mongodb.org/mongo-driver", "database", "MongoDB", "mongo"),
        ("github.com/elastic/go-elasticsearch", "database", "Elasticsearch", "http"),
        ("github.com/olivere/elastic", "database", "Elasticsearch", "http"),
        ("github.com/redis/go-redis", "cache", "Redis", "redis"),
        ("github.com/go-redis/redis", "cache", "Redis", "redis"),
        ("github.com/gomodule/redigo", "cache", "Redis", "redis"),
        ("github.com/segmentio/kafka-go", "broker", "Kafka", "kafka"),
        ("github.com/IBM/sarama", "broker", "Kafka", "kafka"),
        ("github.com/Shopify/sarama", "broker", "Kafka", "kafka"),
        ("github.com/confluentinc/confluent-kafka-go", "broker", "Kafka", "kafka"),
        ("github.com/rabbitmq/amqp091-go", "broker", "RabbitMQ", "amqp"),
        ("github.com/streadway/amqp", "broker", "RabbitMQ", "amqp"),
        ("github.com/nats-io/nats.go", "broker", "NATS", "nats"),
        ("cloud.google.com/go/pubsub", "broker", "Google Cloud Pub/Sub", "grpc"),
        ("cloud.google.com/go/storage", "storage", "Google Cloud Storage", "http"),
        ("cloud.google.com/go/spanner", "database", "Cloud Spanner", "grpc"),
        ("cloud.google.com/go/firestore", "database", "Firestore", "grpc"),
        ("cloud.google.com/go/bigquery", "database", "BigQuery", "http"),
        ("cloud.google.com/go/alloydbconn", "database", "AlloyDB", "sql"),
        ("cloud.google.com/go/profiler", "observability", "Google Cloud Profiler", "http"),
        ("cloud.google.com/go/trace", "observability", "Google Cloud Trace", "http"),
        ("github.com/aws/aws-sdk-go-v2/service/s3", "storage", "AWS S3", "http"),
        ("github.com/aws/aws-sdk-go-v2", "http-service", "AWS", "http"),
        ("github.com/aws/aws-sdk-go", "http-service", "AWS", "http"),
        ("go.opentelemetry.io/otel/exporters/otlp", "observability", "OpenTelemetry Collector", "otlp"),
        ("github.com/getsentry/sentry-go", "observability", "Sentry", "http"),
    ];

    /// <summary>
    /// Python 包名（PEP 503 规范化后）→ 外部系统；以 * 结尾的是前缀。HTTP 客户端库（requests / httpx / aiohttp）、gRPC 运行时、
    /// opentelemetry 的 API / SDK / 插桩都不是外部系统；celery 的 broker 要看配置，这里只标"任务队列"；emails / sendgrid 是邮件。
    /// </summary>
    public static readonly IReadOnlyList<(string Package, string SubKind, string Name, string Protocol)> PythonMap =
    [
        ("psycopg", "database", "PostgreSQL", "sql"),
        ("psycopg-binary", "database", "PostgreSQL", "sql"),
        ("psycopg2", "database", "PostgreSQL", "sql"),
        ("psycopg2-binary", "database", "PostgreSQL", "sql"),
        ("asyncpg", "database", "PostgreSQL", "sql"),
        ("pg8000", "database", "PostgreSQL", "sql"),
        ("pymysql", "database", "MySQL", "sql"),
        ("mysqlclient", "database", "MySQL", "sql"),
        ("mysql-connector-python", "database", "MySQL", "sql"),
        ("aiomysql", "database", "MySQL", "sql"),
        ("pymssql", "database", "SQL Server", "sql"),
        ("pyodbc", "database", "SQL Server", "sql"),
        ("oracledb", "database", "Oracle", "sql"),
        ("cx-oracle", "database", "Oracle", "sql"),
        ("aiosqlite", "database", SQLite, "sql"),
        ("pymongo", "database", "MongoDB", "mongo"),
        ("motor", "database", "MongoDB", "mongo"),
        ("mongoengine", "database", "MongoDB", "mongo"),
        ("elasticsearch", "database", "Elasticsearch", "http"),
        ("google-cloud-spanner", "database", "Cloud Spanner", "grpc"),
        ("google-cloud-firestore", "database", "Firestore", "grpc"),
        ("google-cloud-bigquery", "database", "BigQuery", "http"),
        ("google-cloud-alloydb-connector", "database", "AlloyDB", "sql"),
        ("langchain-google-alloydb-pg", "database", "AlloyDB", "sql"),
        ("redis", "cache", "Redis", "redis"),
        ("aioredis", "cache", "Redis", "redis"),
        ("redis-py-cluster", "cache", "Redis", "redis"),
        ("kafka-python", "broker", "Kafka", "kafka"),
        ("confluent-kafka", "broker", "Kafka", "kafka"),
        ("aiokafka", "broker", "Kafka", "kafka"),
        ("pika", "broker", "RabbitMQ", "amqp"),
        ("aio-pika", "broker", "RabbitMQ", "amqp"),
        ("celery", "broker", "Celery 任务队列", "celery"),
        ("google-cloud-pubsub", "broker", "Google Cloud Pub/Sub", "grpc"),
        ("nats-py", "broker", "NATS", "nats"),
        ("google-cloud-storage", "storage", "Google Cloud Storage", "http"),
        ("boto3", "http-service", "AWS", "http"),
        ("google-cloud-profiler", "observability", "Google Cloud Profiler", "http"),
        ("google-cloud-trace", "observability", "Google Cloud Trace", "http"),
        ("opentelemetry-exporter-otlp*", "observability", "OpenTelemetry Collector", "otlp"),
        ("sentry-sdk", "observability", "Sentry", "http"),
        ("emails", "email", "SMTP 邮件服务", "smtp"),
        ("sendgrid", "email", "SendGrid", "http"),
        ("langchain-google-genai", "http-service", "Google Gemini", "http"),
        ("google-generativeai", "http-service", "Google Gemini", "http"),
        ("openai", "http-service", "OpenAI", "http"),
        ("anthropic", "http-service", "Anthropic", "http"),
    ];

    /// <summary>
    /// Java 依赖 groupId:artifactId → 外部系统；以 * 结尾的是前缀。io.grpc:*（gRPC 运行时，跨服务连线交给 gRPC 规则）、
    /// Eureka / Spring Cloud Config 客户端（仓库里常有对应的服务端容器，交给 <see cref="SpringCloudRule"/>）不在这里；
    /// com.google.cloud 只认能说清是什么系统的几个包（同 Go 的取舍）。
    /// </summary>
    public static readonly IReadOnlyList<(string Coordinate, string SubKind, string Name, string Protocol)> JavaMap =
    [
        ("org.postgresql:*", "database", "PostgreSQL", "sql"),
        ("io.r2dbc:r2dbc-postgresql", "database", "PostgreSQL", "sql"),
        ("mysql:mysql-connector-java", "database", "MySQL", "sql"),
        ("com.mysql:mysql-connector-j", "database", "MySQL", "sql"),
        ("io.asyncer:r2dbc-mysql", "database", "MySQL", "sql"),
        ("org.mariadb.jdbc:*", "database", "MariaDB", "sql"),
        ("org.mariadb:r2dbc-mariadb", "database", "MariaDB", "sql"),
        ("com.h2database:h2", "database", H2, "sql"),
        ("org.hsqldb:hsqldb", "database", HSQLDB, "sql"),
        ("org.apache.derby:*", "database", Derby, "sql"),
        ("com.microsoft.sqlserver:*", "database", "SQL Server", "sql"),
        ("com.oracle.database.jdbc:*", "database", "Oracle", "sql"),
        ("com.oracle.ojdbc:*", "database", "Oracle", "sql"),
        ("org.xerial:sqlite-jdbc", "database", SQLite, "sql"),
        ("org.springframework.boot:spring-boot-starter-data-mongodb*", "database", "MongoDB", "mongo"),
        ("org.mongodb:*", "database", "MongoDB", "mongo"),
        ("org.springframework.boot:spring-boot-starter-data-elasticsearch", "database", "Elasticsearch", "http"),
        ("co.elastic.clients:*", "database", "Elasticsearch", "http"),
        ("org.elasticsearch.client:*", "database", "Elasticsearch", "http"),
        ("org.springframework.boot:spring-boot-starter-data-cassandra*", "database", "Cassandra", "cql"),
        ("com.datastax.oss:*", "database", "Cassandra", "cql"),
        ("org.springframework.boot:spring-boot-starter-data-redis*", "cache", "Redis", "redis"),
        ("redis.clients:jedis", "cache", "Redis", "redis"),
        ("io.lettuce:lettuce-core", "cache", "Redis", "redis"),
        ("org.redisson:*", "cache", "Redis", "redis"),
        ("org.springframework.kafka:spring-kafka", "broker", "Kafka", "kafka"),
        ("org.apache.kafka:kafka-clients", "broker", "Kafka", "kafka"),
        ("org.springframework.cloud:spring-cloud-starter-stream-kafka", "broker", "Kafka", "kafka"),
        ("org.springframework.boot:spring-boot-starter-amqp", "broker", "RabbitMQ", "amqp"),
        ("com.rabbitmq:amqp-client", "broker", "RabbitMQ", "amqp"),
        ("org.springframework.cloud:spring-cloud-starter-stream-rabbit", "broker", "RabbitMQ", "amqp"),
        ("org.springframework.boot:spring-boot-starter-activemq", "broker", "ActiveMQ", "jms"),
        ("org.springframework.boot:spring-boot-starter-artemis", "broker", "ActiveMQ Artemis", "jms"),
        ("org.apache.activemq:*", "broker", "ActiveMQ", "jms"),
        ("org.springframework.cloud:spring-cloud-starter-consul-discovery", "service-registry", "Consul", "http"),
        ("com.alibaba.cloud:spring-cloud-starter-alibaba-nacos-discovery", "service-registry", "Nacos", "http"),
        ("com.alibaba.cloud:spring-cloud-starter-alibaba-nacos-config", "config-center", "Nacos 配置中心", "http"),
        ("io.micrometer:micrometer-registry-prometheus*", "observability", "Prometheus", "http"),
        ("io.micrometer:micrometer-registry-otlp", "observability", "OpenTelemetry Collector", "otlp"),
        ("io.opentelemetry:opentelemetry-exporter-otlp*", "observability", "OpenTelemetry Collector", "otlp"),
        ("io.opentelemetry.instrumentation:opentelemetry-spring-boot-starter", "observability", "OpenTelemetry Collector", "otlp"),
        ("org.springframework.boot:spring-boot-starter-opentelemetry", "observability", "OpenTelemetry Collector", "otlp"),
        ("io.opentelemetry:opentelemetry-exporter-zipkin", "observability", "Zipkin", "http"),
        ("org.springframework.boot:spring-boot-starter-zipkin", "observability", "Zipkin", "http"),
        ("org.springframework.cloud:spring-cloud-starter-zipkin", "observability", "Zipkin", "http"),
        ("org.springframework.cloud:spring-cloud-sleuth-zipkin", "observability", "Zipkin", "http"),
        ("io.zipkin.reporter2:*", "observability", "Zipkin", "http"),
        ("io.sentry:*", "observability", "Sentry", "http"),
        ("org.springframework.boot:spring-boot-starter-mail", "email", "SMTP 邮件服务", "smtp"),
        ("com.sun.mail:*", "email", "SMTP 邮件服务", "smtp"),
        ("org.eclipse.angus:angus-mail", "email", "SMTP 邮件服务", "smtp"),
        ("com.sendgrid:*", "email", "SendGrid", "http"),
        ("com.google.cloud:google-cloud-pubsub", "broker", "Google Cloud Pub/Sub", "grpc"),
        ("com.google.cloud:spring-cloud-gcp-starter-pubsub", "broker", "Google Cloud Pub/Sub", "grpc"),
        ("com.google.cloud:google-cloud-storage", "storage", "Google Cloud Storage", "http"),
        ("com.google.cloud:google-cloud-spanner", "database", "Cloud Spanner", "grpc"),
        ("com.google.cloud:google-cloud-firestore", "database", "Firestore", "grpc"),
        ("com.google.cloud:google-cloud-bigquery", "database", "BigQuery", "http"),
        ("com.google.cloud:google-cloud-trace", "observability", "Google Cloud Trace", "http"),
        ("software.amazon.awssdk:s3", "storage", "AWS S3", "http"),
        ("software.amazon.awssdk:sqs", "broker", "AWS SQS", "http"),
        ("software.amazon.awssdk:dynamodb*", "database", "AWS DynamoDB", "http"),
        ("com.amazonaws:aws-java-sdk-s3", "storage", "AWS S3", "http"),
        ("io.awspring.cloud:spring-cloud-aws-starter-s3", "storage", "AWS S3", "http"),
        ("org.springframework.ai:spring-ai-starter-model-azure-openai", "http-service", "Azure OpenAI", "http"),
        ("org.springframework.ai:spring-ai-azure-openai*", "http-service", "Azure OpenAI", "http"),
        ("org.springframework.ai:spring-ai-starter-model-openai", "http-service", "OpenAI", "http"),
        ("org.springframework.ai:spring-ai-openai*", "http-service", "OpenAI", "http"),
        ("org.springframework.ai:spring-ai-starter-model-anthropic", "http-service", "Anthropic", "http"),
        ("org.springframework.ai:spring-ai-anthropic*", "http-service", "Anthropic", "http"),
        ("org.springframework.ai:spring-ai-starter-model-ollama", "http-service", "Ollama", "http"),
        ("com.openai:openai-java", "http-service", "OpenAI", "http"),
        ("com.anthropic:anthropic-java", "http-service", "Anthropic", "http"),
    ];

    /// <summary>Java 的 ORM / JDBC 抽象：容器的依赖与配置里都没有具体数据库时登记笼统的"数据库（JPA）"之类。</summary>
    public static readonly IReadOnlyList<(string Coordinate, string Name)> JavaOrmMap =
    [
        ("org.springframework.boot:spring-boot-starter-data-jpa", "数据库（JPA）"),
        ("org.springframework.boot:spring-boot-starter-jdbc", "数据库（JDBC）"),
        ("org.springframework.boot:spring-boot-starter-data-jdbc", "数据库（JDBC）"),
        ("org.springframework.boot:spring-boot-starter-data-r2dbc", "数据库（R2DBC）"),
        ("org.hibernate.orm:hibernate-core", "数据库（Hibernate）"),
        ("org.hibernate:hibernate-core", "数据库（Hibernate）"),
        ("org.mybatis.spring.boot:mybatis-spring-boot-starter", "数据库（MyBatis）"),
        ("com.baomidou:mybatis-plus*", "数据库（MyBatis-Plus）"),
        ("org.jooq:jooq", "数据库（jOOQ）"),
    ];

    /// <summary>JDBC / R2DBC URL 的子协议 → 数据库名（jdbc:mysql://… → MySQL；Testcontainers 的 jdbc:tc:mysql:… 取后一段）。</summary>
    private static readonly Dictionary<string, string> JdbcDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mysql"] = "MySQL", ["postgresql"] = "PostgreSQL", ["postgres"] = "PostgreSQL", ["h2"] = H2, ["hsqldb"] = HSQLDB, ["mariadb"] = "MariaDB",
        ["sqlserver"] = "SQL Server", ["oracle"] = "Oracle", ["sqlite"] = SQLite, ["db2"] = "Db2", ["derby"] = Derby, ["clickhouse"] = "ClickHouse",
    };

    /// <summary>Spring 配置里直接说明外部系统的键（数据源 URL 之外）。</summary>
    private static readonly (string Key, string SubKind, string Name, string Protocol)[] JavaConfigKeys =
    [
        ("spring.data.mongodb.uri", "database", "MongoDB", "mongo"),
        ("spring.data.mongodb.host", "database", "MongoDB", "mongo"),
        ("spring.data.redis.host", "cache", "Redis", "redis"),
        ("spring.data.redis.url", "cache", "Redis", "redis"),
        ("spring.redis.host", "cache", "Redis", "redis"),
        ("spring.kafka.bootstrap-servers", "broker", "Kafka", "kafka"),
        ("spring.rabbitmq.host", "broker", "RabbitMQ", "amqp"),
        ("spring.rabbitmq.addresses", "broker", "RabbitMQ", "amqp"),
    ];

    private static readonly System.Text.RegularExpressions.Regex JdbcUrl = new(@"^(?:jdbc|r2dbc):(?:tc:)?(\w+):", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool MatchCoordinate(string coordinate, string pattern) =>
        pattern.EndsWith('*') ? coordinate.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : coordinate.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>只是 ORM、看不出是哪种数据库：同一项目的依赖里没有任何具体数据库时才登记一个笼统的"数据库（ORM 名）"。</summary>
    public static readonly IReadOnlyList<(string Package, string Name)> OrmMap =
    [
        ("gorm.io/gorm", "数据库（GORM）"),
        ("entgo.io/ent", "数据库（ent）"),
        ("sqlalchemy", "数据库（SQLAlchemy）"),
        ("sqlmodel", "数据库（SQLModel）"),
        ("peewee", "数据库（peewee）"),
        ("tortoise-orm", "数据库（Tortoise ORM）"),
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
                var evidence = new Evidence(pkg.File ?? project.File, pkg.Line, pkg.Name);
                var key = ctx.External(hit.SubKind, hit.Name, evidence);
                foreach (var c in containers) ctx.DirectEdges.Add(new DirectEdge(c.Key, key, hit.Protocol, evidence));
            }
        }
        // go.mod：依赖挂到这个模块的每个 main 容器
        foreach (var mod in ctx.Inventory.GoModules)
        {
            var mains = mod.Mains.Select(m => m.Dir).ToHashSet(StringComparer.Ordinal);
            var containers = ctx.Containers.Where(c => c.SubKind == "go-service" && c.Path is not null && mains.Contains(c.Path)).ToList();
            Register(ctx, containers, mod.Requires, d => GoMap.FirstOrDefault(m => d.Equals(m.Prefix, StringComparison.Ordinal) || d.StartsWith(m.Prefix + "/", StringComparison.Ordinal)));
        }
        foreach (var py in ctx.Inventory.PythonProjects)
        {
            var containers = ctx.Containers.Where(c => c.SubKind == "python-service" && c.Path == py.Dir).ToList();
            Register(ctx, containers, py.Dependencies, d => PythonMap.FirstOrDefault(m => m.Package.EndsWith('*') ? d.StartsWith(m.Package[..^1], StringComparison.Ordinal) : d == m.Package));
        }
        ApplyJava(ctx);
    }

    /// <summary>
    /// Java：每个模块的依赖与 Spring 配置挂到成员含该模块的 Java 容器（库模块的依赖经模块引用并进来）。
    /// 配置里的数据源 URL（含 profile 文件 / 文档）能判库就登记，证据注明 profile；容器最后仍没有具体数据库时才用 JPA / JDBC 之类的笼统节点。
    /// </summary>
    private static void ApplyJava(RuleContext ctx)
    {
        var orm = new Dictionary<string, DependencyRef>(StringComparer.Ordinal);
        var ormName = new Dictionary<string, string>(StringComparer.Ordinal);
        var javaContainers = ctx.Containers.Where(c => c.SubKind == "java-service").ToList();
        foreach (var module in ctx.Inventory.JavaModules.Where(m => !m.IsTest))
        {
            var containers = javaContainers.Where(c => c.Members.Any(m => m == module.Dir || RuleContext.Under(m, module.Dir))).ToList();
            if (containers.Count == 0) continue;
            foreach (var dep in module.Dependencies)
            {
                var hit = JavaMap.FirstOrDefault(m => MatchCoordinate(dep.Name, m.Coordinate));
                if (hit != default) { Add(ctx, containers, hit.SubKind, hit.Name, hit.Protocol, dep); continue; }
                var o = JavaOrmMap.FirstOrDefault(x => MatchCoordinate(dep.Name, x.Coordinate));
                if (o == default) continue;
                foreach (var c in containers)
                    if (orm.TryAdd(c.Key, dep)) ormName[c.Key] = o.Name;
            }
            foreach (var entry in module.Config)
            {
                var value = SpringConfigReader.Resolve(entry.Value);
                if (value is null) continue;
                (string SubKind, string Name, string Protocol)? found = null;
                if (SpringConfigEntry.SameKey(entry.Key, "spring.datasource.url") || SpringConfigEntry.SameKey(entry.Key, "spring.datasource.jdbc-url") || SpringConfigEntry.SameKey(entry.Key, "spring.r2dbc.url"))
                {
                    if (JdbcUrl.Match(value) is { Success: true } jm && JdbcDatabases.TryGetValue(jm.Groups[1].Value, out var db)) found = ("database", db, "sql");
                }
                else if (JavaConfigKeys.FirstOrDefault(k => SpringConfigEntry.SameKey(entry.Key, k.Key)) is { Key: not null } ck) found = (ck.SubKind, ck.Name, ck.Protocol);
                if (found is not { } f) continue;
                var note = $"{entry.Key}={value}" + (entry.Profile is null ? "" : $"（profile {entry.Profile}）");
                Add(ctx, containers, f.SubKind, f.Name, f.Protocol, new DependencyRef(note, entry.File, entry.Line));
            }
        }
        foreach (var (containerKey, dep) in orm)
        {
            if (ctx.DirectEdges.Any(d => d.ContainerKey == containerKey && d.ExternalKey.StartsWith("external:database:", StringComparison.Ordinal))) continue;
            Add(ctx, javaContainers.Where(c => c.Key == containerKey).ToList(), "database", ormName[containerKey], "sql", dep);
        }
    }

    private static void Register(RuleContext ctx, List<ArchNode> containers, IReadOnlyList<DependencyRef> deps, Func<string, (string, string SubKind, string Name, string Protocol)> lookup)
    {
        if (containers.Count == 0) return;
        var hasDatabase = false;
        foreach (var dep in deps)
        {
            var hit = lookup(dep.Name);
            if (hit == default) continue;
            hasDatabase |= hit.SubKind == "database";
            Add(ctx, containers, hit.SubKind, hit.Name, hit.Protocol, dep);
        }
        if (hasDatabase) return;
        // 没有驱动只有 ORM：笼统的数据库节点（只取第一个 ORM）
        var orm = deps.Select(d => (Dep: d, Orm: OrmMap.FirstOrDefault(o => o.Package == d.Name))).FirstOrDefault(x => x.Orm != default);
        if (orm.Dep is not null) Add(ctx, containers, "database", orm.Orm.Name, "sql", orm.Dep);
    }

    private static void Add(RuleContext ctx, List<ArchNode> containers, string subKind, string name, string protocol, DependencyRef dep)
    {
        var evidence = new Evidence(dep.File, dep.Line, dep.Name);
        var key = ctx.External(subKind, name, evidence);
        foreach (var c in containers) ctx.DirectEdges.Add(new DirectEdge(c.Key, key, protocol, evidence));
    }
}
