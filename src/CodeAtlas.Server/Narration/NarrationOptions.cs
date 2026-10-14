using System.Text;

namespace CodeAtlas.Server.Narration;

/// <param name="Endpoint">接口根，形如 http://127.0.0.1:8080/v1、https://api.deepseek.com/v1、https://api.anthropic.com/v1（不带 /chat/completions、/messages）。</param>
/// <param name="Model">模型名，原样放进请求的 model 字段。</param>
/// <param name="Enabled">false（--no-ai）时所有叙述返回 unavailable，不发任何请求。</param>
/// <param name="TimeoutSeconds">单次请求超时；本地 27B 一条要两三分钟，联网一般十几秒。</param>
public sealed record NarrationOptions(string Endpoint, string Model, bool Enabled = true, int TimeoutSeconds = NarrationOptions.DefaultTimeoutSeconds)
{
    /// <summary>默认 300 秒：设计稿写的是 120，但用户机器上 qwen3.6-27b 实测 2.1–2.8 tok/s、单条 150–233 秒，120 必然超时。</summary>
    public const int DefaultTimeoutSeconds = 300;

    public const string DefaultEndpoint = "http://127.0.0.1:8080/v1";
    public const string DefaultModel = "qwen3.6-27b";

    /// <summary>协议：OpenAI 兼容 chat/completions（llama-server、DeepSeek、通义、OpenAI …）。</summary>
    public const string OpenAi = "openai";
    /// <summary>协议：Anthropic Messages API。</summary>
    public const string Anthropic = "anthropic";
    public static readonly IReadOnlyList<string> Providers = [OpenAi, Anthropic];

    public const string AnthropicEndpoint = "https://api.anthropic.com/v1";
    public const string AnthropicModel = "claude-sonnet-5-5";

    /// <summary>API Key 的环境变量名：只走环境变量，不进命令行（进程列表可见）和日志。</summary>
    public const string ApiKeyEnvironmentVariable = "CODEATLAS_AI_API_KEY";

    public static NarrationOptions Default { get; } = new(DefaultEndpoint, DefaultModel);

    public string Provider { get; init; } = OpenAi;

    /// <summary>不打印：PrintMembers 里只输出有没有。</summary>
    public string? ApiKey { get; init; }

    /// <summary>同时在飞的请求数：本地 1；联网服务可以 2–4。</summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>false 时入口流程不附入口方法源码，只发事实子图。</summary>
    public bool IncludeSource { get; init; } = true;

    public bool IsLoopback => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>端点不是回环地址时给用户的提示；回环为 null。</summary>
    public string? RemoteWarning
    {
        get
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.IsLoopback) return null;
            return IncludeSource ? $"事实子图与入口源码将发送到 {uri.Host}" : $"事实子图将发送到 {uri.Host}";
        }
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Provider = {Provider}, Endpoint = {Endpoint}, Model = {Model}, Enabled = {Enabled}, TimeoutSeconds = {TimeoutSeconds}, Concurrency = {Concurrency}, IncludeSource = {IncludeSource}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "无" : "***")}");
        return true;
    }
}
