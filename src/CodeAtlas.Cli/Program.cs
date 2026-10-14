using CodeAtlas.Cli.Commands;

namespace CodeAtlas.Cli;

public static class Program
{
    private const string Usage = """
        CodeAtlas — 代码地图索引器（里程碑 1：tree-sitter + CLI）

        用法：
          codeatlas index  <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>]
          codeatlas query  <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds calls,implements,...] [--db <path>]
          codeatlas search <repo> <text> [--limit N] [--db <path>]
          codeatlas serve  <repo> [--db <path>] [--port N] [--parent-pid N] [--token T] [--roslyn auto|off] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai]
          codeatlas arch   <repo> [--db <path>] [--json] [--no-store]
          codeatlas arch   <repo> --dsm <容器键> [--root <组件键>] [--json] [--db <path>]
          codeatlas arch   <repo> --entries <容器键> [--kind http|hosted|job|hub|grpc|handler|page|api-route] [--q <文本>] [--json] [--db <path>]
          codeatlas arch   <repo> --flow <入口符号 id> [--depth N] [--json] [--db <path>]
          codeatlas arch   <repo> --narrate <container|component|flow> <键> [--force] [--facts] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai] [--json] [--db <path>]
          codeatlas arch   <repo> --narrate-all [--flows N] [--force] [--ai-* …] [--db <path>]

        --roslyn auto（默认）：tree-sitter 之后，若同目录有 codeatlas-roslyn 且仓库里有 .sln/.slnx，就用 Roslyn 覆盖 C# 的精确语义；on 缺任一项即报错；off 不跑。
        arch：扫描容器图事实（可部署单元、外部系统、它们之间的边）并打印；写库时接着识别入口并匹配路由；index 结束时也会自动跑一次。
        --entries 列出容器的入口（控制器动作、常驻服务、分发处理器、前端页面）；--flow 打印从某入口出发的时序树。
        --narrate 用 AI（默认本地 llama-server http://127.0.0.1:8080/v1 的 qwen3.6-27b；--ai-provider openai 也能接 DeepSeek / 通义 / OpenAI 等联网的 OpenAI 兼容服务，anthropic 接 Claude）为一个容器 / 组件（<容器键>|<组件键>）/ 入口流程（入口符号 id）生成叙述并打印。
        --facts 只打印喂给模型的事实文本（不调模型）；--narrate-all 批量预生成全部容器与项目层组件的叙述（--flows N 再加每个容器前 N 个 HTTP 入口），已有且未过期的跳过，可中断续跑——本地慢模型适合夜里跑。叙述缓存在索引库旁的 narratives.db，重建索引不丢，按相关文件内容与架构事实判过期，换模型不作废；--ai-timeout 单次请求超时秒数（默认 300）；--no-ai 关闭叙述（serve 的叙述接口全部返回 unavailable）。--ai-concurrency 同时在飞的请求数（默认 1，联网可 2–4）；--ai-no-source 不发入口方法源码。联网服务的 API Key 只从环境变量 CODEATLAS_AI_API_KEY 读。端点不是回环地址时会提示数据将发往该主机。
        """;

    private static readonly HashSet<string> Flags = ["--full", "--in", "--out", "--json", "--no-store", "--no-git", "--no-ai", "--force", "--facts", "--ai-no-source", "--narrate-all"];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Run(args, Console.Out, Console.Error, cts.Token);
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) => Run(args, stdout, stderr, CancellationToken.None);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Length == 0) { stderr.WriteLine(Usage); return 2; }
        ParsedArgs parsed;
        try { parsed = ParsedArgs.Parse(args.Skip(1).ToList(), Flags); }
        catch (ArgumentException ex) { stderr.WriteLine(ex.Message); stderr.WriteLine(Usage); return 2; }

        try
        {
            return args[0] switch
            {
                "index" => IndexCommand.Run(parsed, stdout, stderr),
                "query" => QueryCommand.Run(parsed, stdout, stderr),
                "search" => SearchCommand.Run(parsed, stdout, stderr),
                "serve" => ServeCommand.Run(parsed, stdout, stderr, ct),
                "arch" => ArchCommand.Run(parsed, stdout, stderr),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"未知命令：{command}");
        stderr.WriteLine(Usage);
        return 2;
    }
}
