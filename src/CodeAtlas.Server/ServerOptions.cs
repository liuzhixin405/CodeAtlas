namespace CodeAtlas.Server;

/// <param name="RepoRoot">仓库根目录（绝对路径）。</param>
/// <param name="DbPath">索引库文件路径。</param>
/// <param name="Port">监听端口，0 表示由系统分配。</param>
/// <param name="ParentPid">父进程 pid；它消失后服务在 WatchdogGraceSeconds 后退出。</param>
public sealed record ServerOptions(string RepoRoot, string DbPath, int Port = 0, int? ParentPid = null)
{
    /// <summary>非空时所有 /api 请求必须带 X-CodeAtlas-Token 头；用来防止浏览器里的任意网页访问本服务。</summary>
    public string? Token { get; init; }
    /// <summary>全量索引后是否接着跑 Roslyn（需要同目录有 codeatlas-roslyn 且仓库里有解决方案）。</summary>
    public bool Roslyn { get; init; } = true;
    /// <summary>本地 AI 叙述的端点 / 模型 / 开关（--ai-endpoint / --ai-model / --no-ai）。</summary>
    public Narration.NarrationOptions Narration { get; init; } = CodeAtlas.Server.Narration.NarrationOptions.Default;
    public int WatchdogGraceSeconds { get; init; } = 10;
    public int WatchdogPollSeconds { get; init; } = 2;

    public static string DefaultDbPath(string repoRoot) => Path.Combine(repoRoot, ".codeatlas", "index.db");
}
