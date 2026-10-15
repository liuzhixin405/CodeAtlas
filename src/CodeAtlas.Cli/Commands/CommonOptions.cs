using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Cli.Commands;

/// <summary>手写参数解析：位置参数 + "--name value" / "--flag"。</summary>
internal sealed class ParsedArgs
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);
    /// <summary>同一选项出现多次时的全部值（按出现顺序）；Options 里只留最后一个。</summary>
    private readonly Dictionary<string, List<string>> _all = new(StringComparer.Ordinal);

    public static ParsedArgs Parse(IReadOnlyList<string> args, ISet<string> flags)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { result.Positional.Add(a); continue; }
            if (flags.Contains(a)) { result.Options[a] = null; continue; }
            if (i + 1 >= args.Count) throw new ArgumentException($"选项 {a} 缺少值");
            var value = args[++i];
            result.Options[a] = value;
            if (!result._all.TryGetValue(a, out var list)) result._all[a] = list = [];
            list.Add(value);
        }
        return result;
    }

    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var v) ? v : null;
    /// <summary>可重复选项（--project a --project b）的全部值。</summary>
    public IReadOnlyList<string> Values(string name) => _all.TryGetValue(name, out var list) ? list : [];
}

internal static class CommonOptions
{
    public static string DbPath(string repo, ParsedArgs args) =>
        args.Value("--db") ?? Path.Combine(Path.GetFullPath(repo), ".codeatlas", "index.db");

    /// <summary>
    /// 主根 + 外部根：--project 可重复（相对路径按当前目录），再并上主根下 .codeatlas/config.json 的 projects（相对主根）；
    /// --no-nested 关掉嵌套仓库（独立克隆 / 子模块）。不存在的外部根提示一次，它们之前入库的文件保留。
    /// </summary>
    public static ProjectRoots Roots(string repo, ParsedArgs args, TextWriter stderr)
    {
        var roots = ProjectRoots.Load(repo, args.Values("--project").Select(Path.GetFullPath), includeNested: !args.Has("--no-nested"));
        foreach (var m in roots.Missing) stderr.WriteLine($"提示：外部项目根不存在，跳过（已入库的文件保留）：{m}");
        if (roots.Externals.Count > 0) stderr.WriteLine($"[roots] 主根 {roots.Main}；外部根 {string.Join("、", roots.Externals)}");
        return roots;
    }

    public static List<ILanguageAdapter> CreateAdapters() =>
        LanguageAdapters.Create();

    public static IndexDatabase OpenExisting(string repo, ParsedArgs args, TextWriter stderr)
    {
        var path = DbPath(repo, args);
        if (!File.Exists(path))
        {
            stderr.WriteLine($"索引库不存在：{path}。先运行 codeatlas index {repo}");
            throw new FileNotFoundException(path);
        }
        return IndexDatabase.Open(path);
    }
}
