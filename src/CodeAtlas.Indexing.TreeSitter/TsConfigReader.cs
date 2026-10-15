using System.Text.Json;
using CodeAtlas.Core.Resolution;

namespace CodeAtlas.Indexing.TreeSitter;

/// <summary>
/// 找出脚本文件所在目录及其祖先目录里的 tsconfig.json / jsconfig.json，读 compilerOptions.baseUrl 与 paths
/// （沿相对路径的 extends 最多 5 层，子配置覆盖父配置），换算成仓库相对路径。读不了的配置跳过。
/// 只靠扫描结果里每个文件的（仓库相对路径, 磁盘路径）对照往上走，多根索引时外部根下的配置也找得到。
/// </summary>
public static class TsConfigReader
{
    private static readonly string[] ConfigNames = ["tsconfig.json", "jsconfig.json"];
    private static readonly HashSet<string> ScriptLanguages = new(StringComparer.Ordinal) { "typescript", "tsx", "javascript" };
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<ScriptPathConfig> Discover(IEnumerable<ScannedFile> files)
    {
        // 仓库相对目录 → 磁盘目录
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (!ScriptLanguages.Contains(f.Language)) continue;
            var rel = ParentOf(f.RelativePath);
            var full = Path.GetDirectoryName(f.FullPath);
            // 外部根的文件路径以 "../" 开头：走到 ".." 就停，别把主根 "" 错配到外部根的上级目录
            while (rel is not null && full is not null && !(rel == ".." || rel.EndsWith("/..", StringComparison.Ordinal)) && dirs.TryAdd(rel, full))
            {
                if (rel.Length == 0) break;
                rel = ParentOf(rel);
                full = Path.GetDirectoryName(full);
            }
        }
        var result = new List<ScriptPathConfig>();
        foreach (var (rel, full) in dirs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var file = ConfigNames.Select(n => Path.Combine(full, n)).FirstOrDefault(File.Exists);
            if (file is not null && Load(rel, full, file) is { } config) result.Add(config);
        }
        return result;
    }

    internal static ScriptPathConfig? Load(string dirRelative, string dirFull, string configFile)
    {
        string? baseUrl = null;
        List<KeyValuePair<string, IReadOnlyList<string>>>? paths = null;
        string? pathsBase = null;
        // 先读子配置，再沿 extends 往上；已经有值的字段不被父配置覆盖
        var current = configFile;
        for (var depth = 0; depth < 5 && current is not null && File.Exists(current); depth++)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(current), JsonOptions); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { break; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) break;
                var configDir = Path.GetDirectoryName(Path.GetFullPath(current))!;
                if (doc.RootElement.TryGetProperty("compilerOptions", out var options) && options.ValueKind == JsonValueKind.Object)
                {
                    if (baseUrl is null && options.TryGetProperty("baseUrl", out var b) && b.ValueKind == JsonValueKind.String)
                        baseUrl = Path.GetFullPath(Path.Combine(configDir, b.GetString()!));
                    if (paths is null && options.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        paths = [];
                        pathsBase = configDir;
                        foreach (var entry in p.EnumerateObject())
                            if (entry.Value.ValueKind == JsonValueKind.Array)
                                paths.Add(new(entry.Name, entry.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()));
                    }
                }
                current = doc.RootElement.TryGetProperty("extends", out var ext) && ext.ValueKind == JsonValueKind.String && ext.GetString() is { } e && e.StartsWith('.')
                    ? Path.GetFullPath(Path.Combine(configDir, e.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? e : e + ".json"))
                    : null;
            }
        }
        if (baseUrl is null && paths is null) return null;
        // paths 相对 baseUrl（有的话），否则相对定义它的那个配置文件所在目录
        var targetBase = baseUrl ?? pathsBase ?? dirFull;
        var converted = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        foreach (var (pattern, targets) in paths ?? [])
        {
            var list = targets.Select(t => ToRepoRelative(dirRelative, dirFull, Path.Combine(targetBase, t))).OfType<string>().ToList();
            if (list.Count > 0) converted.Add(new(pattern, list));
        }
        return new ScriptPathConfig(dirRelative, baseUrl is null ? null : ToRepoRelative(dirRelative, dirFull, baseUrl), converted);
    }

    /// <summary>磁盘路径 → 仓库相对路径（以配置所在目录为锚）；爬出仓库根返回 null。* 原样保留。</summary>
    private static string? ToRepoRelative(string dirRelative, string dirFull, string full)
    {
        const string star = "__codeatlas_star__";
        var rel = Path.GetRelativePath(dirFull, Path.GetFullPath(full.Replace("*", star))).Replace('\\', '/').Replace(star, "*");
        if (Path.IsPathRooted(rel)) return null;
        var parts = dirRelative.Length == 0 ? new List<string>() : dirRelative.Split('/').ToList();
        foreach (var seg in rel.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string? ParentOf(string relative) => relative.Length == 0 ? null : relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
}
