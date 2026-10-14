using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Cli.Commands;

/// <summary>手写参数解析：位置参数 + "--name value" / "--flag"。</summary>
internal sealed class ParsedArgs
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);

    public static ParsedArgs Parse(IReadOnlyList<string> args, ISet<string> flags)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { result.Positional.Add(a); continue; }
            if (flags.Contains(a)) { result.Options[a] = null; continue; }
            if (i + 1 >= args.Count) throw new ArgumentException($"选项 {a} 缺少值");
            result.Options[a] = args[++i];
        }
        return result;
    }

    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var v) ? v : null;
}

internal static class CommonOptions
{
    public static string DbPath(string repo, ParsedArgs args) =>
        args.Value("--db") ?? Path.Combine(Path.GetFullPath(repo), ".codeatlas", "index.db");

    public static List<ILanguageAdapter> CreateAdapters() =>
        [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

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
