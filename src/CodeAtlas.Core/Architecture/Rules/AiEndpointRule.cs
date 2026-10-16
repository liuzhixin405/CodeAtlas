using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>
/// 直接用 HTTP 调大模型接口的代码（没有 SDK 包可认）：看请求路径的字符串字面量——OpenAI 兼容的 /chat/completions、
/// Anthropic 的 /v1/messages（或同文件带 anthropic-version 头时的 /messages）→ http-service 外部系统。
/// 只有主机名（api.openai.com 这类）的字面量不算：多是设置里的预设列表，调不调、调哪家看用户配置。
/// 字面量要像在拼请求地址：完整 URL（https://…/chat/completions）、带插值（{base}/chat/completions）、或紧跟在 + 后面（endpoint + "/messages"）；
/// 规则表、说明文字里的同名字符串不算。注释里的不算（按整个文件剥注释）；每个文件每种接口只登记第一处。
/// </summary>
public sealed class AiEndpointRule : IDiscoveryRule
{
    public const string RuleId = "ai-endpoint";
    public string Id => RuleId;
    public string Description => "字符串里的大模型接口路径（/chat/completions、/v1/messages）";

    private const string OpenAiCompatible = "AI 端点（OpenAI 兼容）";
    private const string Anthropic = "Anthropic";

    private static readonly (string Marker, string Name)[] Markers =
    [
        ("/chat/completions", OpenAiCompatible),
        // Anthropic 只在同文件有 anthropic-version 头（或 anthropic.com）时算：/v1/messages 别家也用（飞书的 im/v1/messages）
        ("/messages", Anthropic),
    ];

    private static readonly Regex Literal = new(@"[""'`]([^""'`\n]*)[""'`]", RegexOptions.Compiled);

    public void Apply(RuleContext ctx)
    {
        foreach (var file in ctx.Inventory.SourceFiles)
        {
            if (PolyText.LangOf(file) is not { } lang || RuleContext.IsTestFile(file) || ProjectInventory.IsGeneratedSource(file)) continue;
            var lines = ctx.Lines(file);
            if (!Array.Exists(lines, l => Markers.Any(m => l.Contains(m.Marker, StringComparison.OrdinalIgnoreCase)))) continue;
            var containers = ctx.ContainersOf(file);
            if (containers.Count == 0) continue;
            var text = SourceMask.WithoutComments(lines, lang);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var anthropicFile = text.Any(t => t.Contains("anthropic-version", StringComparison.OrdinalIgnoreCase) || t.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < text.Length; i++)
            {
                foreach (Match lit in Literal.Matches(text[i]))
                {
                    var value = lit.Groups[1].Value;
                    var before = text[i][..lit.Index].TrimEnd();
                    var building = value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || value.Contains('{') || before.EndsWith('+');
                    if (!building) continue;
                    var hit = Markers.FirstOrDefault(m => m.Name == Anthropic
                        ? anthropicFile && (value.EndsWith(m.Marker, StringComparison.OrdinalIgnoreCase) || value.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase))
                        : value.Contains(m.Marker, StringComparison.OrdinalIgnoreCase));
                    if (hit.Name is null || !seen.Add(hit.Name)) continue;
                    var evidence = new Evidence(file, i + 1, lines[i].Trim());
                    var key = ctx.External("http-service", hit.Name, evidence);
                    var url = value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : null;
                    foreach (var c in containers) ctx.Outbound.Add(new Outbound(c.Key, RuleId, "http", hit.Name, null, url, file, i + 1, key));
                }
            }
        }
    }
}
