using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAtlas.Server.Narration;

/// <summary>按 NarrationOptions.Provider 建客户端。</summary>
public static class ChatProviders
{
    public static IChatProvider Create(NarrationOptions options, HttpMessageHandler? handler = null) =>
        options.Provider == NarrationOptions.Anthropic ? new AnthropicProvider(options, handler) : new OpenAiCompatibleProvider(options, handler);
}

/// <summary>
/// 两种协议共用的 HTTP 部分：并发门（Concurrency）、超时、错误归类。
/// 连不上 / 401 / 403 → ChatUnavailableException（整体不可用，不重试）；429 → 按 Retry-After 退避再试，最多 3 次；
/// 其他非 2xx、超时、空内容 → ChatFailedException（上层重试一次）。
/// </summary>
public abstract class HttpChatProvider : IChatProvider, IDisposable
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;
    protected NarrationOptions Options { get; }

    /// <summary>429 的最长等待：Retry-After 超过它就按它截断；测试里改成 0。</summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromSeconds(30);
    public const int RateLimitAttempts = 3;

    protected HttpChatProvider(NarrationOptions options, HttpMessageHandler? handler)
    {
        Options = options;
        // 超时自己用 CancellationToken 控制，HttpClient 的留成无限，避免两处超时打架
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _gate = new SemaphoreSlim(Math.Max(1, options.Concurrency), Math.Max(1, options.Concurrency));
    }

    public string Model => Options.Model;
    public string Endpoint => Options.Endpoint;

    public async Task<ChatResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await SendWithRetryAsync(system, user, ct); }
        finally { _gate.Release(); }
    }

    private async Task<ChatResult> SendWithRetryAsync(string system, string user, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));
            var sw = Stopwatch.StartNew();
            using var request = BuildRequest(system, user);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, timeout.Token); }
            catch (HttpRequestException ex) { throw new ChatUnavailableException($"连不上 AI 端点 {Options.Endpoint}：{ex.Message}", ex); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ChatFailedException($"AI 端点 {Options.TimeoutSeconds}s 内没有响应（超时）"); }
            using (response)
            {
                string text;
                try { text = await response.Content.ReadAsStringAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ChatFailedException($"AI 端点 {Options.TimeoutSeconds}s 内没有读完响应（超时）"); }
                sw.Stop();
                var code = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ChatUnavailableException(string.IsNullOrEmpty(Options.ApiKey)
                        ? $"AI 端点要求 API Key，但没有设置（HTTP {code}）"
                        : $"API Key 无效或没有权限（HTTP {code}）：{Truncate(text, 200)}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < RateLimitAttempts)
                {
                    await Task.Delay(RetryAfter(response, attempt), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    if (OnBadRequest(response.StatusCode, text)) { attempt--; continue; } // 协议降级后重发，不算一次限流重试
                    throw new ChatFailedException(response.StatusCode == HttpStatusCode.TooManyRequests
                        ? $"AI 服务限流（HTTP 429），重试 {RateLimitAttempts} 次仍未通过"
                        : $"AI 端点返回 HTTP {code}：{Truncate(text, 300)}");
                }
                var (content, prompt, completion) = ParseResponse(text);
                if (string.IsNullOrWhiteSpace(content)) throw new ChatFailedException("AI 端点返回了空内容");
                return new ChatResult(content, prompt, completion, sw.Elapsed);
            }
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var wait = response.Headers.RetryAfter?.Delta
                   ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                   ?? TimeSpan.FromSeconds(2 * attempt);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        return wait > MaxRateLimitWait ? MaxRateLimitWait : wait;
    }

    protected abstract HttpRequestMessage BuildRequest(string system, string user);

    /// <summary>返回 (正文, prompt tokens, completion tokens)；不是 JSON 抛 ChatFailedException。</summary>
    protected abstract (string Content, int? Prompt, int? Completion) ParseResponse(string body);

    /// <summary>非 2xx 时给子类一次降级机会（例如去掉端点不认的 response_format）；返回 true 表示改了请求、重发。</summary>
    protected virtual bool OnBadRequest(HttpStatusCode code, string body) => false;

    protected static HttpRequestMessage JsonPost(string url, object body) =>
        new(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    protected static T Deserialize<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body, Json) ?? throw new ChatFailedException("AI 端点的响应是空 JSON"); }
        catch (JsonException ex) { throw new ChatFailedException($"AI 端点的响应不是 JSON：{ex.Message}"); }
    }

    protected static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() { _http.Dispose(); _gate.Dispose(); GC.SuppressFinalize(this); }
}

/// <summary>
/// OpenAI 兼容的 chat/completions：本地 llama-server / vLLM / Ollama，联网 DeepSeek、通义（DashScope 兼容模式）、OpenAI 等。
/// 有 ApiKey 时带 Authorization: Bearer。只有回环端点才发 llama-server 专用的 chat_template_kwargs（OpenAI 官方接口遇到未知字段会 400）。
/// 端点不认 response_format 返回 400 时去掉它重发一次，之后都不再带。
/// </summary>
public sealed class OpenAiCompatibleProvider(NarrationOptions options, HttpMessageHandler? handler = null) : HttpChatProvider(options, handler)
{
    private volatile bool _jsonMode = true;

    protected override HttpRequestMessage BuildRequest(string system, string user)
    {
        var body = new ChatRequest(Options.Model,
            [new ChatMessage("system", system), new ChatMessage("user", user)],
            0.2, 1600, false,
            _jsonMode ? new ResponseFormat("json_object") : null,
            // Qwen3 系列默认带思考过程，27B 在核显上思考几分钟都不止；llama-server 认这个开关
            Options.IsLoopback ? new Dictionary<string, object> { ["enable_thinking"] = false } : null);
        var request = JsonPost(Options.Endpoint.TrimEnd('/') + "/chat/completions", body);
        if (!string.IsNullOrEmpty(Options.ApiKey)) request.Headers.Authorization = new("Bearer", Options.ApiKey);
        return request;
    }

    protected override bool OnBadRequest(HttpStatusCode code, string body)
    {
        if (code != HttpStatusCode.BadRequest || !_jsonMode || !body.Contains("response_format", StringComparison.OrdinalIgnoreCase)) return false;
        _jsonMode = false;
        return true;
    }

    protected override (string, int?, int?) ParseResponse(string body)
    {
        var parsed = Deserialize<ChatResponse>(body);
        var content = parsed.Choices is { Count: > 0 } c ? c[0].Message?.Content ?? "" : "";
        return (content, parsed.Usage?.PromptTokens, parsed.Usage?.CompletionTokens);
    }

    private sealed record ChatMessage(string Role, string Content);
    private sealed record ResponseFormat(string Type);
    private sealed record ChatRequest(
        string Model, IReadOnlyList<ChatMessage> Messages, double Temperature, [property: JsonPropertyName("max_tokens")] int MaxTokens, bool Stream,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat,
        [property: JsonPropertyName("chat_template_kwargs")] Dictionary<string, object>? ChatTemplateKwargs);

    private sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices, ChatUsage? Usage);
    private sealed record ChatChoice(ChatChoiceMessage? Message);
    private sealed record ChatChoiceMessage(string? Content);
    private sealed record ChatUsage([property: JsonPropertyName("prompt_tokens")] int? PromptTokens, [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}

/// <summary>端点连得上但这一次没有产出可用结果（HTTP 错误、超时、空内容）：可以重试一次。</summary>
public sealed class ChatFailedException(string message) : Exception(message);
