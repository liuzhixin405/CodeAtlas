using System.Net;
using System.Text;
using System.Text.Json;
using CodeAtlas.Server.Narration;

namespace CodeAtlas.Server.Tests;

/// <summary>假的 OpenAI 兼容端点：按顺序吐预设响应，记下收到的请求体。</summary>
public sealed class FakeChatHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<string> RequestBodies { get; } = [];
    public List<string> RequestUris { get; } = [];
    public int Calls => RequestBodies.Count;

    /// <summary>下一次请求返回 assistant 内容为 content 的 200。</summary>
    public FakeChatHandler Reply(string content, int? completionTokens = 7)
    {
        _responses.Enqueue(_ => Ok(content, completionTokens));
        return this;
    }

    public FakeChatHandler ReplyStatus(HttpStatusCode code, string body = "boom")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(code) { Content = new StringContent(body) });
        return this;
    }

    public FakeChatHandler Refuse()
    {
        _responses.Enqueue(_ => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));
        return this;
    }

    public static HttpResponseMessage Ok(string content, int? completionTokens = 7)
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 123, completion_tokens = completionTokens },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestUris.Add($"{request.Method} {request.RequestUri}");
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (_responses.Count == 0) return Ok(FakeChat.ValidJson(["node:k"]));
        return _responses.Dequeue()(request);
    }
}

/// <summary>固定事实的来源：测试不碰索引库。</summary>
public sealed class StubFacts(params Facts[] facts) : IFactsSource
{
    public Facts? Resolve(string kind, string key) => facts.FirstOrDefault(f => f.Kind == kind && f.Key == key);
}

/// <summary>ServerFixture 用：不走 HTTP，直接给一段合法 JSON。</summary>
public sealed class FakeChatProvider(string model = "fake-model") : IChatProvider
{
    public string Model => model;
    public string Endpoint => "http://127.0.0.1:1/v1";
    public int Calls;
    public Task<ChatResult> CompleteAsync(string system, string user, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ChatResult(FakeChat.ValidJson(["node:k"]), 10, 5, TimeSpan.FromMilliseconds(5)));
    }
}

public static class FakeChat
{
    public static Facts ContainerFacts(string key = "k") =>
        new("container", key, "K", $"[node:{key}] 容器 K\n[edge:a→{key}:http] A 调 K\n[entry:12] GET /x\n", new HashSet<string> { $"node:{key}", $"edge:a→{key}:http", "entry:12" }, 0);

    public static string ValidJson(string[] evidence, string layout = "columns", string summary = "K 是一个 Web 容器。") =>
        JsonSerializer.Serialize(new
        {
            summary,
            keyPoints = new[] { new { text = "被 A 通过 HTTP 调用", evidence }, new { text = "有一个入口", evidence = new[] { "entry:12" } } },
            risks = new[] { new { text = "边的置信度不高", evidence = new[] { "edge:a→k:http" } } },
            layout,
        });

    public static NarrationService Service(FakeChatHandler handler, Facts? facts = null, NarrationOptions? options = null)
    {
        var o = options ?? new NarrationOptions("http://127.0.0.1:1/v1", "fake-model", TimeoutSeconds: 5);
        return new NarrationService(o, new OpenAiCompatibleProvider(o, handler), new StubFacts(facts ?? ContainerFacts()), Core.Storage.NarrativeCache.OpenInMemory(), ownsCache: true);
    }
}
