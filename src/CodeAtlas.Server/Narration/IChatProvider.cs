namespace CodeAtlas.Server.Narration;

/// <param name="Content">assistant 消息正文（可能带 ```json 围栏或思考残留，由 NarrativeSchema 宽松解析）。</param>
/// <param name="PromptTokens">响应 usage 里的数字；端点没给为 null。</param>
public sealed record ChatResult(string Content, int? PromptTokens, int? CompletionTokens, TimeSpan Elapsed)
{
    /// <summary>生成速度；没有 usage 为 null。</summary>
    public double? TokensPerSecond => CompletionTokens is { } n && Elapsed.TotalSeconds > 0 ? n / Elapsed.TotalSeconds : null;
}

/// <summary>端点连不上（连接被拒、DNS 失败）。与"连得上但答错了"区分开：前者整体 unavailable，后者 failed 并重试。</summary>
public sealed class ChatUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>一次聊天补全。实现保证同时在飞的请求不超过 NarrationOptions.Concurrency。</summary>
public interface IChatProvider
{
    string Model { get; }
    string Endpoint { get; }
    Task<ChatResult> CompleteAsync(string system, string user, CancellationToken ct);
}
