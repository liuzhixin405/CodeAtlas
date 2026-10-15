using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// 一个 tsconfig / jsconfig 的路径设置，全部换算成仓库相对路径（'/' 分隔、无首尾斜杠，仓库根为空串）。
/// </summary>
/// <param name="Directory">配置文件所在目录；对这个目录下的文件生效，嵌套时最近的那个优先。</param>
/// <param name="BaseUrl">baseUrl 目录；没有设为 null。非相对说明符先按 paths 匹配，再试 baseUrl/说明符。</param>
/// <param name="Paths">paths：模式（最多一个 *，如 "@/*"）→ 目标（同样最多一个 *，已是仓库相对路径，如 "app/src/*"）。</param>
public sealed record ScriptPathConfig(string Directory, string? BaseUrl, IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Paths);

/// <summary>
/// 把 TS/JS 的模块说明符解析成仓库里的模块符号：相对路径、tsconfig paths 别名、baseUrl；
/// 扩展名 .ts/.tsx/.js/.jsx（等）与目录下的 index.* 都试。解析不到（npm 包、资源文件）返回 null。
/// </summary>
public sealed class ScriptModuleResolver
{
    private readonly Dictionary<string, SymbolRecord> _modulesByPathNoExt;
    private readonly List<ScriptPathConfig> _configs;
    private readonly Dictionary<(string Dir, string Specifier), SymbolRecord?> _cache = [];

    public ScriptModuleResolver(Dictionary<string, SymbolRecord> modulesByPathNoExt, IReadOnlyList<ScriptPathConfig> configs)
    {
        _modulesByPathNoExt = modulesByPathNoExt;
        // 最近的配置优先：目录越长越近
        _configs = configs.OrderByDescending(c => c.Directory.Length).ToList();
    }

    public static bool IsRelative(string specifier) => specifier.StartsWith('.') || specifier.StartsWith('/');

    public SymbolRecord? Resolve(string fromFile, string specifier)
    {
        var dir = DirectoryOf(fromFile);
        var key = (IsRelative(specifier) ? dir : ConfigFor(fromFile)?.Directory ?? "\0", specifier);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        SymbolRecord? found = null;
        foreach (var candidate in CandidatePaths(fromFile, specifier))
            if (_modulesByPathNoExt.TryGetValue(candidate, out var module)) { found = module; break; }
        _cache[key] = found;
        return found;
    }

    /// <summary>候选的"去扩展名路径"：每个基路径本身与 /index。</summary>
    internal IEnumerable<string> CandidatePaths(string fromFile, string specifier)
    {
        if (IsRelative(specifier))
        {
            foreach (var p in HeuristicEdgeResolver.ResolveRelativePath(fromFile, specifier)) yield return p;
            yield break;
        }
        var config = ConfigFor(fromFile);
        if (config is null) yield break;
        foreach (var (pattern, targets) in config.Paths.OrderByDescending(p => p.Key.IndexOf('*') < 0 ? int.MaxValue : p.Key.IndexOf('*')))
        {
            var star = pattern.IndexOf('*');
            string? captured;
            if (star < 0) captured = specifier == pattern ? "" : null;
            else
            {
                var prefix = pattern[..star];
                var suffix = pattern[(star + 1)..];
                captured = specifier.Length >= prefix.Length + suffix.Length && specifier.StartsWith(prefix, StringComparison.Ordinal) && specifier.EndsWith(suffix, StringComparison.Ordinal)
                    ? specifier[prefix.Length..(specifier.Length - suffix.Length)]
                    : null;
            }
            if (captured is null) continue;
            foreach (var target in targets)
            {
                var path = HeuristicEdgeResolver.StripScriptExtension(HeuristicEdgeResolver.NormalizePath(target.Replace("*", captured)));
                yield return path;
                yield return path + "/index";
            }
        }
        if (config.BaseUrl is not null)
        {
            var path = HeuristicEdgeResolver.StripScriptExtension(HeuristicEdgeResolver.NormalizePath(config.BaseUrl.Length == 0 ? specifier : config.BaseUrl + "/" + specifier));
            yield return path;
            yield return path + "/index";
        }
    }

    private ScriptPathConfig? ConfigFor(string fromFile) =>
        _configs.FirstOrDefault(c => c.Directory.Length == 0 || fromFile.StartsWith(c.Directory + "/", StringComparison.Ordinal));

    private static string DirectoryOf(string file) => file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
}
