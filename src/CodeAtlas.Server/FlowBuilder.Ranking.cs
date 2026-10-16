namespace CodeAtlas.Server;

/// <summary>时序图同档分支的"分量"排序与外部调用的分类（FlowBuilder 的一部分）。</summary>
public static partial class FlowBuilder
{
    /// <summary>分量往下看几层（子树规模、I/O 都只看这几层）。</summary>
    internal const int WeightDepth = 3;
    /// <summary>子树规模的上限：顶层方法往下 3 层能有上千个，对数后差别已经不大，封顶省时间。</summary>
    internal const int WeightCap = 200;
    /// <summary>走到 I/O（续接 / HTTP·gRPC 出口 / 分派 / 数据库·HTTP·消息·云服务·外部进程·文件类外部调用）的加分：相当于子树大 4 倍。</summary>
    internal const double WeightIoBonus = 2;
    /// <summary>被调用方数超过这个数才开始降权（公共工具：ParsedArgs.Value、useApi）。</summary>
    internal const int WeightFanInFree = 8;
    /// <summary>外部调用单独成行（数据库 / HTTP / 消息…，同一列同一类别一行）最多几行。</summary>
    internal const int MaxKeyExternalRows = 4;
    /// <summary>外部调用按列分组最多几行，多出来的并进"外部 / 框架"。</summary>
    internal const int MaxExternalGroups = 4;

    /// <summary>
    /// 同档分支的分量（越大越先进扇出）= log2(1 + reach) + (io ? <see cref="WeightIoBonus"/> : 0) − max(0, log2(callers / <see cref="WeightFanInFree"/>))。
    /// <list type="bullet">
    /// <item>reach：往下 <see cref="WeightDepth"/> 层能走到的仓库里的方法数（不含测试、不含类型本身——new 一个对象不算干活，封顶 <see cref="WeightCap"/>）——真正干活的那一步子树大（IndexAsync 往下是扫描、解析、写库），
    /// 取参数、拼路径的小工具子树小；取对数是因为 50 和 200 的差别远没有 1 和 4 的大。</item>
    /// <item>io：这几层里有跨容器续接、HTTP / gRPC 出口、分派，或调到数据库 / HTTP / 消息类的外部 API——时序图最该看到"出去"的那一步。</item>
    /// <item>callers：有多少个不同的（非测试）方法调它。被几十处调用的是公共工具（useApi、ParsedArgs.Value），在哪张图里都不是主线；8 处以内不降权，之后每翻一倍减 1。
    /// 但它自己就是 I/O 边界（directIo：方法体里直接调数据库 / HTTP 等外部 API、直接续接或出口）时不降权——仓储的 GetByIdAsync 被 26 处调用，在每一处都是"读库"那一步。</item>
    /// </list>
    /// </summary>
    public static double Weight(int reach, bool io, int callers, bool directIo = false) =>
        Math.Log2(1 + Math.Min(reach, WeightCap)) + (io ? WeightIoBonus : 0)
        - (directIo ? 0 : Math.Max(0, Math.Log2(Math.Max(1, callers) / (double)WeightFanInFree)));

    /// <summary>hook 与调用它的组件同名相关时的加分上限（× <see cref="NameAffinity"/>）。</summary>
    internal const double WeightNameBonus = 1.5;

    /// <summary>
    /// 自己调了读数据 API 的 hook 再加的分：MT5GroupTable 的主数据 useMT5GroupByServerId（useQuery）与 useMT5GroupImportTemplate（useMutation）
    /// 名字相关度分不开（2/5 对 2/4，后者反而高），按"取不取数据"分。
    /// </summary>
    internal const double WeightReadBonus = 1.5;

    private static readonly HashSet<string> DataReadApis = new(StringComparer.Ordinal)
    {
        "useQuery", "useSuspenseQuery", "useInfiniteQuery", "useSuspenseInfiniteQuery", "useQueries", "useSuspenseQueries",
        "useSWR", "useSWRInfinite", "useSWRImmutable", "useLiveQuery", "useFetch", "useAsyncData", "useLazyQuery",
    };

    /// <summary>外部符号是 TanStack Query / SWR / Apollo / Nuxt 一类的读数据 hook（限定名最后一段，去掉参数表与泛型）。</summary>
    public static bool IsDataReadApi(string qualifiedName)
    {
        var name = qualifiedName;
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        name = MapBuilder.StripGenericArgs(name);
        var dot = name.LastIndexOfAny(['.', ':']);
        return DataReadApis.Contains(dot >= 0 ? name[(dot + 1)..] : name);
    }

    /// <summary>
    /// hook 名与调用方（组件 / 另一个 hook）名的相关度 0..1：hook 名去掉 use 后的词里，有几成也出现在调用方名里（按驼峰切词、小写、去掉复数 s）。
    /// 组件的主数据通常来自以它的主题命名的 hook：MT4SymbolTable → useMT4Symbols（mt4、symbol 都在，1.0）排在 useMT4SymbolImportTemplate（mt4、symbol / 4 个词，0.5）、
    /// useServerData（0）前面——这几个 hook 都走到 HTTP、子树规模差不多，光靠分量分不开。
    /// </summary>
    public static double NameAffinity(string caller, string hook)
    {
        var mine = Words(caller).ToHashSet(StringComparer.Ordinal);
        var words = Words(hook).Where(w => w != "use").Distinct(StringComparer.Ordinal).ToList();
        return words.Count == 0 || mine.Count == 0 ? 0 : (double)words.Count(mine.Contains) / words.Count;
    }

    private static readonly System.Text.RegularExpressions.Regex WordPattern = new("[A-Z]+[0-9]*(?![a-z])|[A-Z]?[a-z]+[0-9]*|[0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static IEnumerable<string> Words(string name) =>
        WordPattern.Matches(name).Select(m => m.Value.ToLowerInvariant()).Select(w => w.Length > 3 && w.EndsWith('s') ? w[..^1] : w);

    private static readonly HashSet<string> PersistenceSegments = new(StringComparer.Ordinal)
    {
        "DbContext", "DbSet", "EntityFrameworkCore", "RelationalDatabaseFacadeExtensions", "EntityManager", "JdbcTemplate", "NamedParameterJdbcTemplate",
        "SqlCommand", "SqlConnection", "NpgsqlCommand", "NpgsqlConnection", "IDbConnection", "DbConnection", "DbCommand", "SqlMapper", "IMongoCollection",
        "MongoCollection", "IDatabase", "sqlalchemy", "gorm", "prisma", "mongoose", "knex", "typeorm",
        // 驱动的命名空间 / 包：Microsoft.Data.Sqlite、Microsoft.Data.SqlClient、Npgsql、StackExchange.Redis、Python 的 sqlite3 / psycopg2
        "Sqlite", "SqlClient", "Npgsql", "MySqlConnector", "Dapper", "MongoDB", "Redis", "sqlite3", "psycopg2", "pymongo", "redis",
    };
    private static readonly HashSet<string> PersistenceMembers = new(StringComparer.Ordinal)
    {
        "SaveChanges", "SaveChangesAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
    };
    private static readonly HashSet<string> HttpSegments = new(StringComparer.Ordinal)
    {
        "HttpClient", "HttpClientJsonExtensions", "RestTemplate", "WebClient", "RestClient", "axios", "requests", "httpx", "aiohttp",
    };
    // 云服务 SDK 的根命名空间 / 包：AWS（Amazon.S3.IAmazonS3.PutObjectAsync）、Azure、Python 的 boto3、Node 的 @aws-sdk
    private static readonly HashSet<string> CloudSegments = new(StringComparer.Ordinal) { "Amazon", "Azure", "Minio", "boto3", "botocore", "@aws-sdk", "aws-sdk", "@azure" };

    private static readonly HashSet<string> MessagingSegments = new(StringComparer.Ordinal)
    {
        "IPublishEndpoint", "ISendEndpoint", "IBus", "IEventBus", "KafkaTemplate", "RabbitTemplate", "IProducer", "ServiceBusSender", "IConnectionMultiplexer",
    };

    // 文件系统：.NET 的 File / Directory / FileStream…（Path 只拼字符串，不算），Node 的 fs，Java 的 Files，Go 的 os.ReadFile 不认（os 太宽）
    private static readonly HashSet<string> FileSegments = new(StringComparer.Ordinal)
    {
        "File", "Directory", "FileInfo", "DirectoryInfo", "FileStream", "StreamReader", "StreamWriter", "FileSystemWatcher", "fs", "node:fs", "Files",
    };

    /// <summary>
    /// 外部调用的类别：数据库 / HTTP / 消息 / 云服务 / 外部进程 / 文件；其余（日志、集合、字符串、UI 框架……）null。只按限定名里的段判：
    /// 类型段以 Repository 结尾（JpaRepository.save、CrudRepository.findById）或在 <see cref="PersistenceSegments"/> 里（DbContext、EntityFrameworkCore 命名空间）→ 数据库；
    /// SaveChanges 一类方法名 → 数据库；HttpClient / RestTemplate / axios / requests → HTTP；裸 fetch → HTTP；IPublishEndpoint / KafkaTemplate → 消息；Amazon / Azure / boto3 → 云服务；Process / subprocess / child_process → 外部进程；File / Directory / fs → 文件。
    /// </summary>
    public static string? ExternalCategory(string qualifiedName)
    {
        var q = qualifiedName.StartsWith("external:", StringComparison.Ordinal) ? qualifiedName["external:".Length..] : qualifiedName;
        var paren = q.IndexOf('(');
        if (paren >= 0) q = q[..paren];
        q = MapBuilder.StripGenericArgs(q);
        if (q == "fetch") return "HTTP";
        var segments = q.Split(['.', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        var types = segments.Length > 1 ? segments[..^1] : segments;
        if (types.Any(s => PersistenceSegments.Contains(s) || (s.Length > "Repository".Length && s.EndsWith("Repository", StringComparison.Ordinal)))
            || (segments.Length > 1 && PersistenceMembers.Contains(segments[^1]))) return "数据库";
        if (types.Any(HttpSegments.Contains)) return "HTTP";
        if (types.Any(MessagingSegments.Contains)) return "消息";
        if (types.Any(CloudSegments.Contains)) return "云服务";
        if (types.Any(s => s is "Process" or "ProcessStartInfo" or "subprocess" or "child_process" or "ProcessBuilder")) return "外部进程";
        if (types.Any(FileSegments.Contains) && segments[^1] is not ("Exists" or "existsSync")) return "文件"; // File.Exists 只是探一下，不算读写
        return null;
    }
}
