using System.Text.Json.Serialization;

namespace CodeAtlas.Server.Narration;

/// <summary>
/// Anthropic Messages API：POST {endpoint}/messages，头 x-api-key 与 anthropic-version: 2023-06-01；
/// system 是顶层字段，回答在 content[] 的 text 块里，用量在 usage.input_tokens / output_tokens。
/// </summary>
public sealed class AnthropicProvider(NarrationOptions options, HttpMessageHandler? handler = null) : HttpChatProvider(options, handler)
{
    public const string ApiVersion = "2023-06-01";

    protected override HttpRequestMessage BuildRequest(string system, string user)
    {
        var request = JsonPost(Options.Endpoint.TrimEnd('/') + "/messages",
            new MessagesRequest(Options.Model, 1600, system, [new Message("user", user)], 0.2));
        if (!string.IsNullOrEmpty(Options.ApiKey)) request.Headers.Add("x-api-key", Options.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    protected override (string, int?, int?) ParseResponse(string body)
    {
        var parsed = Deserialize<MessagesResponse>(body);
        var text = string.Concat((parsed.Content ?? []).Where(b => b.Type == "text").Select(b => b.Text));
        return (text, parsed.Usage?.InputTokens, parsed.Usage?.OutputTokens);
    }

    private sealed record Message(string Role, string Content);
    private sealed record MessagesRequest(string Model, [property: JsonPropertyName("max_tokens")] int MaxTokens, string System, IReadOnlyList<Message> Messages, double Temperature);
    private sealed record MessagesResponse(IReadOnlyList<ContentBlock>? Content, MessagesUsage? Usage);
    private sealed record ContentBlock(string? Type, string? Text);
    private sealed record MessagesUsage([property: JsonPropertyName("input_tokens")] int? InputTokens, [property: JsonPropertyName("output_tokens")] int? OutputTokens);
}
