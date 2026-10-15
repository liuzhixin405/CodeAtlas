using System.Text.Json;

namespace CodeAtlas.Core.Workspace;

/// <summary>子目录里 .git 标记的含义。</summary>
public enum NestedRepoKind
{
    /// <summary>没有 .git：普通目录。</summary>
    None,
    /// <summary>.git 是目录：独立克隆（或老式子模块），内容只在这里，纳入。</summary>
    Clone,
    /// <summary>.git 是文件且 gitdir 指向 …/.git/modules/…：子模块，内容只在这里，纳入。</summary>
    Submodule,
    /// <summary>.git 是文件且 gitdir 指向 …/.git/worktrees/…（或认不出）：同一仓库的另一份工作副本，跳过，否则符号与容器重复。</summary>
    Worktree,
}

/// <param name="FullPath">仓库目录的绝对路径。</param>
/// <param name="Prefix">该仓库里的文件入库时的路径前缀（相对主根，'/' 分隔，以 '/' 结尾；主根本身为 ""；外部根形如 "../other/"）。</param>
/// <param name="IsExternalRoot">是外部根本身（--project / config.json）。</param>
/// <param name="IsNested">是某个根之下被纳入的嵌套仓库（独立克隆 / 子模块）。</param>
public sealed record RepoDir(string FullPath, string Prefix, bool IsExternalRoot, bool IsNested);

/// <summary>
/// 一次索引覆盖的目录集合：主根 + 外部根（--project 与 .codeatlas/config.json 的 projects），以及是否纳入嵌套仓库。
/// 所有文件都以"相对主根"的路径入库：外部根 D:\a\backend、主根 D:\a\ccs-ui → ../backend/Api/Foo.cs，
/// 这样 Path.Combine(主根, 相对路径) 总能读到文件，扩展打开文件也不用另做映射。
/// 根之间互相包含时按真实全路径去重：遍历一个根时遇到另一个根的目录就跳过，那个根自己遍历，同一个文件只出现一次。
/// </summary>
public sealed class ProjectRoots
{
    public const string ConfigRelativePath = ".codeatlas/config.json";

    /// <summary>
    /// 磁盘绝对路径（根、扫描到的全路径、Roslyn 文档路径）的比较：Windows / macOS 文件系统默认不区分大小写，Linux 区分。
    /// 入库的相对路径（files.path，'/' 分隔）不用它：那是数据，一律按 Ordinal 比较（与 SQLite 的 = 一致），两个平台结果才一样。
    /// </summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    /// <summary>与 <see cref="PathComparer"/> 同一规则的 StringComparison（StartsWith / Equals 用）。</summary>
    public static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>嵌套仓库与解决方案发现时不进的目录（与 ProjectInventory.IgnoredDirectories 一致）。</summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea", ".next", "coverage" };

    private readonly HashSet<string> _rootSet;

    /// <summary>主根（绝对路径，无结尾分隔符）。</summary>
    public string Main { get; }
    /// <summary>存在的外部根（绝对路径，已去重，不含主根）。</summary>
    public IReadOnlyList<string> Externals { get; }
    /// <summary>配置了但不存在的外部根：它们之前入库的文件不当作"已删除"。</summary>
    public IReadOnlyList<string> Missing { get; }
    /// <summary>纳入带独立 .git 目录的克隆与子模块；false 时恢复旧行为（凡有 .git 的子目录都跳过）。worktree 无论如何都跳过。</summary>
    public bool IncludeNested { get; }

    /// <summary>主根在前，外部根按配置顺序。</summary>
    public IReadOnlyList<string> All { get; }

    private ProjectRoots(string main, List<string> externals, List<string> missing, bool includeNested)
    {
        Main = main;
        Externals = externals;
        Missing = missing;
        IncludeNested = includeNested;
        All = [main, .. externals];
        _rootSet = new HashSet<string>(All, PathComparer);
    }

    public static ProjectRoots Single(string main, bool includeNested = true) => Create(main, [], includeNested);

    /// <summary>外部根：规范成绝对路径（相对路径按当前目录），去掉与主根相同、重复的项；不存在的进 Missing。</summary>
    public static ProjectRoots Create(string main, IEnumerable<string> externals, bool includeNested = true)
    {
        var mainFull = Normalize(main);
        var seen = new HashSet<string>(PathComparer) { mainFull };
        var list = new List<string>();
        var missing = new List<string>();
        foreach (var e in externals)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            var full = Normalize(e);
            if (!seen.Add(full)) continue;
            if (Directory.Exists(full)) list.Add(full); else missing.Add(full);
        }
        return new ProjectRoots(mainFull, list, missing, includeNested);
    }

    /// <summary>命令行的外部根（已是绝对路径或相对当前目录）+ 主根下 .codeatlas/config.json 的 projects（相对主根）。</summary>
    public static ProjectRoots Load(string main, IEnumerable<string>? cliProjects, bool includeNested = true)
    {
        var mainFull = Normalize(main);
        var all = new List<string>();
        foreach (var p in cliProjects ?? []) all.Add(Path.GetFullPath(p));
        all.AddRange(ReadConfigProjects(mainFull));
        return Create(mainFull, all, includeNested);
    }

    /// <summary>
    /// 读 .codeatlas/config.json 的 {"projects": ["../other-repo", "D:/abs"]}；相对路径按主根解析。
    /// 文件不存在、不是合法 JSON 或没有 projects 都返回空（不让一个坏配置挡住索引）。
    /// </summary>
    public static IReadOnlyList<string> ReadConfigProjects(string main)
    {
        var path = Path.Combine(main, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return [];
            return projects.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                .Select(p => Path.GetFullPath(Path.Combine(main, p.GetString()!.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>绝对路径 → 入库用的相对路径（相对主根，'/' 分隔；主根本身为 ""）。</summary>
    public string Relative(string fullPath)
    {
        var rel = Path.GetRelativePath(Main, fullPath).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    /// <summary>绝对路径是否落在某个根（主根或外部根）之下。</summary>
    public bool Contains(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        return All.Any(r => IsUnder(full, r));
    }

    /// <summary>落在某个根之下时返回入库用的相对路径，否则 null。</summary>
    public string? ToRelative(string fullPath) => Contains(fullPath) ? Relative(Path.GetFullPath(fullPath)) : null;

    /// <summary>目录本身就是某个根。</summary>
    public bool IsRoot(string dirFullPath) => _rootSet.Contains(Normalize(dirFullPath));

    /// <summary>不存在的外部根的入库前缀（"../other/"）：增量索引的删除检测跳过以它开头的文件。</summary>
    public IReadOnlyList<string> MissingPrefixes => Missing.Select(m => Relative(m) + "/").ToList();

    /// <summary>入库路径是否属于一个不存在的外部根。</summary>
    public bool IsUnderMissingRoot(string relativePath) => MissingPrefixes.Any(p => relativePath.StartsWith(p, PathComparison));

    /// <summary>
    /// 遍历 currentRoot 时能否进入这个子目录：另一个根的目录不进（它自己遍历）；worktree 不进；
    /// 独立克隆 / 子模块按 IncludeNested。名字排除（node_modules 等）由调用方各自判断。
    /// </summary>
    public bool ShouldDescend(string dirFullPath, string currentRoot)
    {
        if (!PathComparer.Equals(Normalize(dirFullPath), Normalize(currentRoot)) && IsRoot(dirFullPath)) return false;
        return Classify(dirFullPath) switch
        {
            NestedRepoKind.None => true,
            NestedRepoKind.Clone or NestedRepoKind.Submodule => IncludeNested,
            _ => false,
        };
    }

    /// <summary>
    /// 看子目录的 .git：目录 → 独立克隆；文件 → 读 "gitdir: …"，含 /worktrees/ 是 worktree，含 /modules/ 是子模块，
    /// 其他（--separate-git-dir 之类认不出的）按 worktree 处理——宁可漏，不重复。
    /// </summary>
    public static NestedRepoKind Classify(string dirFullPath)
    {
        var marker = Path.Combine(dirFullPath, ".git");
        if (Directory.Exists(marker)) return NestedRepoKind.Clone;
        if (!File.Exists(marker)) return NestedRepoKind.None;
        string text;
        try { text = File.ReadAllText(marker); }
        catch (IOException) { return NestedRepoKind.Worktree; }
        catch (UnauthorizedAccessException) { return NestedRepoKind.Worktree; }
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return NestedRepoKind.Worktree;
        var gitdir = "/" + line["gitdir:".Length..].Trim().Replace('\\', '/').Trim('/') + "/";
        if (gitdir.Contains("/worktrees/", StringComparison.OrdinalIgnoreCase)) return NestedRepoKind.Worktree;
        if (gitdir.Contains("/modules/", StringComparison.OrdinalIgnoreCase)) return NestedRepoKind.Submodule;
        return NestedRepoKind.Worktree;
    }

    /// <summary>
    /// 全部要单独处理的仓库目录（git 历史、解决方案发现用）：每个根，加上它之下被纳入的嵌套仓库（不进点目录与忽略目录，最深 maxDepth 层）。
    /// 顺序：主根、主根下的嵌套仓库、外部根、外部根下的嵌套仓库；同一目录只出现一次。
    /// </summary>
    public IReadOnlyList<RepoDir> Repositories(int maxDepth = 6)
    {
        var result = new List<RepoDir>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var root in All)
        {
            if (!seen.Add(root)) continue;
            var isMain = PathComparer.Equals(root, Main);
            result.Add(new RepoDir(root, PrefixOf(root), !isMain, false));
            if (!IncludeNested) continue;
            Walk(root, root, 0);
        }
        return result;

        void Walk(string currentRoot, string dir, int depth)
        {
            if (depth >= maxDepth) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || IgnoredDirectories.Contains(name)) continue;
                try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; }
                catch (IOException) { continue; }
                if (IsRoot(sub)) continue; // 另一个根，外层循环处理
                var kind = Classify(sub);
                if (kind == NestedRepoKind.Worktree) continue;
                if (kind is NestedRepoKind.Clone or NestedRepoKind.Submodule && seen.Add(Normalize(sub)))
                    result.Add(new RepoDir(Normalize(sub), PrefixOf(sub), false, true));
                Walk(currentRoot, sub, depth + 1);
            }
        }
    }

    /// <summary>目录的入库前缀：主根 ""，其他 "rel/"。</summary>
    public string PrefixOf(string dirFullPath)
    {
        var rel = Relative(Normalize(dirFullPath));
        return rel.Length == 0 ? "" : rel + "/";
    }

    /// <summary>path 是否等于 root 或在 root 之下（按分隔符边界，D:\a\ui 不算 D:\a\ui-old 的父目录）。</summary>
    public static bool IsUnder(string fullPath, string root)
    {
        if (string.Equals(fullPath.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), PathComparison)) return true;
        var withSep = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(withSep, PathComparison);
    }

    /// <summary>绝对路径、去掉结尾分隔符（盘符根 "D:\" 保留）。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }
}
