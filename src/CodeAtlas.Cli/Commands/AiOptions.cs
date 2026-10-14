using CodeAtlas.Server.Narration;

namespace CodeAtlas.Cli.Commands;

/// <summary>
/// serve 与 arch 共用的 AI 参数：--ai-provider openai|anthropic、--ai-endpoint、--ai-model、--ai-timeout、--ai-concurrency、--ai-no-source、--no-ai。
/// 本地（llama-server，默认）与联网（DeepSeek / 通义 / OpenAI / Claude …）走同一套参数，只是端点不同。
/// API Key 只从环境变量 CODEATLAS_AI_API_KEY 读：命令行参数在进程列表里看得到。
/// </summary>
internal static class AiOptions
{
    public static NarrationOptions Parse(ParsedArgs args, TextWriter stderr, out bool ok) =>
        Parse(args, stderr, Environment.GetEnvironmentVariable(NarrationOptions.ApiKeyEnvironmentVariable), out ok);

    internal static NarrationOptions Parse(ParsedArgs args, TextWriter stderr, string? apiKey, out bool ok)
    {
        ok = true;
        var provider = (args.Value("--ai-provider") ?? NarrationOptions.OpenAi).ToLowerInvariant();
        if (!NarrationOptions.Providers.Contains(provider))
        {
            stderr.WriteLine($"--ai-provider 只能是 {string.Join(" / ", NarrationOptions.Providers)}，不是 {provider}");
            ok = false;
            provider = NarrationOptions.OpenAi;
        }
        var anthropic = provider == NarrationOptions.Anthropic;
        var endpoint = args.Value("--ai-endpoint") ?? (anthropic ? NarrationOptions.AnthropicEndpoint : NarrationOptions.DefaultEndpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            stderr.WriteLine($"--ai-endpoint 不是合法的 http(s) 地址：{endpoint}");
            ok = false;
        }
        var model = args.Value("--ai-model") ?? (anthropic ? NarrationOptions.AnthropicModel : NarrationOptions.DefaultModel);
        // 单次请求超时（秒）；默认 300：用户机器上 qwen3.6-27b 实测 2.1–2.8 tok/s，单条 150–233s，120s 必然超时。联网服务 60 足够
        var timeout = PositiveInt(args, "--ai-timeout", NarrationOptions.DefaultTimeoutSeconds, 3600, stderr, ref ok);
        var concurrency = PositiveInt(args, "--ai-concurrency", 1, 8, stderr, ref ok);
        return new NarrationOptions(endpoint, model, Enabled: !args.Has("--no-ai"), TimeoutSeconds: timeout)
        {
            Provider = provider,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
            Concurrency = concurrency,
            IncludeSource = !args.Has("--ai-no-source"),
        };
    }

    private static int PositiveInt(ParsedArgs args, string name, int fallback, int max, TextWriter stderr, ref bool ok)
    {
        if (args.Value(name) is not { } raw) return fallback;
        if (int.TryParse(raw, out var v) && v >= 1 && v <= max) return v;
        stderr.WriteLine($"{name} 需要 1–{max} 的整数，不是 {raw}");
        ok = false;
        return fallback;
    }
}
