using System.Net;
using System.Text;
using System.Text.Json;
using CodeAtlas.Server.Narration;

namespace CodeAtlas.Server.Tests;

/// <summary>本地与联网两类端点的请求形状、鉴权、限流、降级与并发。不测模型质量。</summary>
public class NarrationProviderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>记下请求头与请求体，按顺序吐预设响应；可选延迟，用来量同时在飞的请求数。</summary>
    private sealed class RecordingHandler(TimeSpan? delay = null) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        private int _inFlight;
        public int MaxInFlight;
        public List<(string Url, Dictionary<string, string> Headers, JsonElement Body)> Requests { get; } = [];
        public Func<HttpResponseMessage> Default { get; set; } = () => OpenAiOk(FakeChat.ValidJson(["node:k"]));

        public RecordingHandler Then(Func<HttpResponseMessage> r) { _responses.Enqueue(r); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Requests)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
                var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
                var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult()).RootElement.Clone();
                Requests.Add((request.RequestUri!.ToString(), headers, body));
            }
            try
            {
                if (delay is { } d) await Task.Delay(d, ct);
                Func<HttpResponseMessage> next;
                lock (_responses) next = _responses.Count > 0 ? _responses.Dequeue() : Default;
                return next();
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private static HttpResponseMessage OpenAiOk(string content) => FakeChatHandler.Ok(content);

    private static HttpResponseMessage AnthropicOk(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "msg_1", type = "message", role = "assistant",
            content = new object[] { new { type = "text", text = content } },
            usage = new { input_tokens = 321, output_tokens = 45 },
        }), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body) };

    /// <summary>按协议建 provider（429 不真等 Retry-After）接到服务上。</summary>
    private static NarrationService Make(NarrationOptions o, RecordingHandler h, params Facts[] facts)
    {
        HttpChatProvider provider = o.Provider == NarrationOptions.Anthropic
            ? new AnthropicProvider(o, h) { MaxRateLimitWait = TimeSpan.Zero }
            : new OpenAiCompatibleProvider(o, h) { MaxRateLimitWait = TimeSpan.Zero };
        return new NarrationService(o, provider, new StubFacts(facts.Length == 0 ? [FakeChat.ContainerFacts()] : facts), Core.Storage.NarrativeCache.OpenInMemory(), ownsCache: true);
    }

    private static NarrationOptions Local => new("http://127.0.0.1:8080/v1", "qwen3.6-27b", TimeoutSeconds: 5);
    private static NarrationOptions DeepSeek => new("https://api.deepseek.com/v1", "deepseek-chat", TimeoutSeconds: 5) { ApiKey = "sk-test-not-real" };
    private static NarrationOptions Claude => new(NarrationOptions.AnthropicEndpoint, NarrationOptions.AnthropicModel, TimeoutSeconds: 5) { Provider = NarrationOptions.Anthropic, ApiKey = "sk-ant-test-not-real" };

    [Fact]
    public void Factory_PicksProtocolByProvider()
    {
        Assert.IsType<OpenAiCompatibleProvider>(ChatProviders.Create(Local));
        Assert.IsType<OpenAiCompatibleProvider>(ChatProviders.Create(DeepSeek));
        Assert.IsType<AnthropicProvider>(ChatProviders.Create(Claude));
    }

    [Fact]
    public async Task Local_NoAuthHeader_KeepsLlamaServerThinkingSwitch()
    {
        var h = new RecordingHandler();
        using var svc = Make(Local, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        var (url, headers, body) = Assert.Single(h.Requests);
        Assert.Equal("http://127.0.0.1:8080/v1/chat/completions", url);
        Assert.False(headers.ContainsKey("authorization"));
        Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean()); // 本地 Qwen3 关思考，原样保留
        Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Null(svc.Status().RemoteWarning);
    }

    [Fact]
    public async Task OnlineOpenAiCompatible_SendsBearer_DropsLlamaServerOnlyField()
    {
        var h = new RecordingHandler();
        using var svc = Make(DeepSeek, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        var (url, headers, body) = Assert.Single(h.Requests);
        Assert.Equal("https://api.deepseek.com/v1/chat/completions", url);
        Assert.Equal("Bearer sk-test-not-real", headers["authorization"]);
        Assert.False(body.TryGetProperty("chat_template_kwargs", out _)); // OpenAI 官方接口遇到未知字段会 400
        Assert.Equal("deepseek-chat", body.GetProperty("model").GetString());
        var s = svc.Status();
        Assert.Equal(("openai", true, "事实子图与入口源码将发送到 api.deepseek.com"), (s.Provider, s.HasApiKey, s.RemoteWarning));
    }

    [Fact]
    public async Task Anthropic_MessagesApi_HeadersBodyAndParse()
    {
        var h = new RecordingHandler { Default = () => AnthropicOk(FakeChat.ValidJson(["node:k"])) };
        using var svc = Make(Claude, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.Equal("K 是一个 Web 容器。", r.Dto.Summary);
        Assert.Equal((321, 45), (r.Chat!.PromptTokens, r.Chat.CompletionTokens));
        var (url, headers, body) = Assert.Single(h.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", url);
        Assert.Equal("sk-ant-test-not-real", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.False(headers.ContainsKey("authorization"));
        Assert.Equal(NarrationOptions.AnthropicModel, body.GetProperty("model").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("system").GetString()));      // system 是顶层字段
        var msg = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", msg.GetProperty("role").GetString());
        Assert.Equal(1600, body.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task BadKey_Unavailable_WithChineseReason_NoRetry(HttpStatusCode code)
    {
        var h = new RecordingHandler().Then(() => Status(code, "{\"error\":\"invalid api key\"}"));
        using var svc = Make(DeepSeek, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("unavailable", r.Dto.Status);
        Assert.Contains("API Key 无效或没有权限", r.Dto.Error);
        Assert.Contains(((int)code).ToString(), r.Dto.Error);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task MissingKey_401_SaysKeyNotSet()
    {
        var h = new RecordingHandler().Then(() => Status(HttpStatusCode.Unauthorized));
        using var svc = Make(DeepSeek with { ApiKey = null }, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("unavailable", r.Dto.Status);
        Assert.Contains("没有设置", r.Dto.Error);
        Assert.False(svc.Status().HasApiKey);
    }

    [Fact]
    public async Task RateLimited_BacksOffAndRetries_ThenReady()
    {
        var h = new RecordingHandler()
            .Then(() => { var m = Status(HttpStatusCode.TooManyRequests); m.Headers.RetryAfter = new(TimeSpan.FromSeconds(5)); return m; })
            .Then(() => Status(HttpStatusCode.TooManyRequests));
        using var svc = Make(DeepSeek, h);
        var r = await svc.GenerateAsync("container", "k", false, CancellationToken.None);
        Assert.Equal("ready", r.Dto.Status);
        Assert.Equal(3, h.Requests.Count); // 两次 429 + 一次成功，都在 provider 里，没有耗掉上层的那次重试
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task EndpointRejectsResponseFormat_DegradesOnce_AndRemembers()
    {
        var h = new RecordingHandler().Then(() => Status(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Unrecognized request argument supplied: response_format\"}}"));
        using var svc = Make(DeepSeek, h, FakeChat.ContainerFacts("a"), FakeChat.ContainerFacts("b"));
        Assert.Equal("ready", (await svc.GenerateAsync("container", "a", false, CancellationToken.None)).Dto.Status);
        Assert.Equal("ready", (await svc.GenerateAsync("container", "b", false, CancellationToken.None)).Dto.Status);
        Assert.Equal(3, h.Requests.Count);
        Assert.True(h.Requests[0].Body.TryGetProperty("response_format", out _));
        Assert.False(h.Requests[1].Body.TryGetProperty("response_format", out _));
        Assert.False(h.Requests[2].Body.TryGetProperty("response_format", out _)); // 之后不再带
    }

    [Fact]
    public async Task Concurrency_RunsThatManyAtOnce_LocalStaysSerial()
    {
        var facts = Enumerable.Range(0, 4).Select(i => FakeChat.ContainerFacts("c" + i)).ToArray();

        var online = new RecordingHandler(TimeSpan.FromMilliseconds(150));
        using (var svc = Make(DeepSeek with { Concurrency = 2 }, online, facts))
        {
            foreach (var f in facts) svc.Enqueue("container", f.Key, force: false);
            await svc.WaitIdleAsync(Wait);
            Assert.Equal(4, online.Requests.Count);
            Assert.Equal(2, online.MaxInFlight);
            Assert.Equal(2, svc.Status().Concurrency);
        }

        var local = new RecordingHandler(TimeSpan.FromMilliseconds(50));
        using (var svc = Make(Local, local, facts))
        {
            foreach (var f in facts) svc.Enqueue("container", f.Key, force: false);
            await svc.WaitIdleAsync(Wait);
            Assert.Equal(4, local.Requests.Count);
            Assert.Equal(1, local.MaxInFlight);
        }
    }

    [Fact]
    public void Options_ToString_NeverPrintsTheKey()
    {
        var text = DeepSeek.ToString();
        Assert.DoesNotContain("sk-test-not-real", text);
        Assert.Contains("ApiKey = ***", text);
        Assert.Equal("事实子图将发送到 api.deepseek.com", (DeepSeek with { IncludeSource = false }).RemoteWarning);
    }
}
