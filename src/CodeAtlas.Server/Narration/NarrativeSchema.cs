using System.Text.Json;

namespace CodeAtlas.Server.Narration;

/// <summary>模型输出校验后的正文（也是 narratives.json 列的形状）。</summary>
public sealed record NarrativeBody(string Summary, IReadOnlyList<NarrativePointDto> KeyPoints, IReadOnlyList<NarrativePointDto> Risks, string? Layout);

/// <summary>模型的回答不是合法的叙述 JSON：触发一次重试。</summary>
public sealed class NarrativeParseException(string message) : Exception(message);

/// <summary>
/// 模型输出 JSON 的宽松解析与校验：去掉 ``` 围栏与思考残留（取第一个 { 到最后一个 }）；
/// evidence 不在输入事实 id 集合里的那条标 inferred；layout 不在该 kind 的预设里置 null。
/// </summary>
public static class NarrativeSchema
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static NarrativeBody Parse(string raw, string kind, IReadOnlySet<string> factIds)
    {
        var text = ExtractJson(raw) ?? throw new NarrativeParseException("回答里没有 JSON 对象");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException ex) { throw new NarrativeParseException("回答不是合法 JSON：" + ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new NarrativeParseException("回答的顶层不是对象");
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";
            if (summary.Length == 0) throw new NarrativeParseException("回答缺少 summary");
            var keyPoints = ReadPoints(root, "keyPoints", factIds);
            var risks = ReadPoints(root, "risks", factIds);
            string? layout = null;
            if (root.TryGetProperty("layout", out var l) && l.ValueKind == JsonValueKind.String)
            {
                var id = l.GetString()!.Trim();
                layout = NarrativePresets.For(kind).Any(p => p.Id == id) ? id : null;
            }
            return new NarrativeBody(summary, keyPoints, risks, layout);
        }
    }

    private static List<NarrativePointDto> ReadPoints(JsonElement root, string name, IReadOnlySet<string> factIds)
    {
        var list = new List<NarrativePointDto>();
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            string? text = null;
            var evidence = new List<string>();
            if (item.ValueKind == JsonValueKind.String) text = item.GetString();
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text = t.GetString();
                if (item.TryGetProperty("evidence", out var ev))
                {
                    if (ev.ValueKind == JsonValueKind.Array)
                        evidence.AddRange(ev.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0));
                    else if (ev.ValueKind == JsonValueKind.String && ev.GetString() is { Length: > 0 } one) evidence.Add(one.Trim());
                }
            }
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            // 没给任何引用也算推断：叙述的每一条都应落在事实上
            var inferred = evidence.Count == 0 || evidence.Any(e => !factIds.Contains(e));
            list.Add(new NarrativePointDto(text, evidence, inferred));
        }
        return list;
    }

    /// <summary>去掉 ```json 围栏、&lt;think&gt; 段等杂音，取第一个 { 到最后一个 }。</summary>
    public static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw;
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0) text = text[(thinkEnd + "</think>".Length)..];
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }
}
