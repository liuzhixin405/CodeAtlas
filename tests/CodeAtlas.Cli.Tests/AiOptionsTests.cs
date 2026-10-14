using CodeAtlas.Cli;
using CodeAtlas.Cli.Commands;
using CodeAtlas.Server.Narration;

namespace CodeAtlas.Cli.Tests;

public class AiOptionsTests
{
    private static readonly HashSet<string> Flags = ["--no-ai", "--ai-no-source"];

    private static (NarrationOptions Options, bool Ok, string Err) Parse(string? key, params string[] args)
    {
        var err = new StringWriter();
        var o = AiOptions.Parse(ParsedArgs.Parse(["repo", .. args], Flags), err, key, out var ok);
        return (o, ok, err.ToString());
    }

    [Fact]
    public void Default_IsTheLocalLlamaServer()
    {
        var (o, ok, _) = Parse(null);
        Assert.True(ok);
        Assert.Equal(("openai", "http://127.0.0.1:8080/v1", "qwen3.6-27b", 300, 1, true, (string?)null),
            (o.Provider, o.Endpoint, o.Model, o.TimeoutSeconds, o.Concurrency, o.IncludeSource, o.ApiKey));
    }

    [Fact]
    public void Anthropic_FillsItsOwnDefaults_KeyComesFromEnvironmentOnly()
    {
        var (o, ok, _) = Parse("  sk-ant-x  ", "--ai-provider", "anthropic", "--ai-concurrency", "3", "--ai-no-source", "--ai-timeout", "60");
        Assert.True(ok);
        Assert.Equal(("anthropic", NarrationOptions.AnthropicEndpoint, NarrationOptions.AnthropicModel, 60, 3, false, "sk-ant-x"),
            (o.Provider, o.Endpoint, o.Model, o.TimeoutSeconds, o.Concurrency, o.IncludeSource, o.ApiKey));
    }

    [Fact]
    public void OnlineOpenAiCompatible_KeepsGivenEndpointAndModel()
    {
        var (o, ok, _) = Parse("sk-x", "--ai-endpoint", "https://api.deepseek.com/v1", "--ai-model", "deepseek-chat");
        Assert.True(ok);
        Assert.Equal(("openai", "https://api.deepseek.com/v1", "deepseek-chat"), (o.Provider, o.Endpoint, o.Model));
    }

    [Theory]
    [InlineData("--ai-provider", "gemini", "--ai-provider")]
    [InlineData("--ai-concurrency", "0", "--ai-concurrency")]
    [InlineData("--ai-concurrency", "9", "--ai-concurrency")]
    [InlineData("--ai-endpoint", "ftp://x", "--ai-endpoint")]
    public void BadValues_AreRejected(string name, string value, string mentioned)
    {
        var (_, ok, err) = Parse(null, name, value);
        Assert.False(ok);
        Assert.Contains(mentioned, err);
    }
}
