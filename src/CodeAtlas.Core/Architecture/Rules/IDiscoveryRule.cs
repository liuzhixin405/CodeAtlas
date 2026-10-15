using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>一条发现规则：读清单与源码，登记出口（Outbound）或直接的容器→外部系统边。每条命中都要带证据。</summary>
public interface IDiscoveryRule
{
    string Id { get; }
    string Description { get; }
    void Apply(RuleContext ctx);
}

/// <summary>包引用等不经代码就能确定的边：容器 → 外部系统，置信度 0.5。</summary>
public sealed record DirectEdge(string ContainerKey, string ExternalKey, string Protocol, Evidence Evidence);

public sealed class RuleContext
{
    // Go 的 x_test.go、Python 的 test_x.py / x_test.py / conftest.py 也是测试文件
    private static readonly Regex TestPath = new(@"(^|[/.])(tests?|__tests__|spec|specs)([/.]|$)|\.(test|spec)\.[tj]sx?$|Tests?\.cs$|_test\.go$|(^|/)test_[^/]*\.py$|_test\.py$|(^|/)conftest\.py$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);

    public ProjectInventory Inventory { get; }
    public IReadOnlyList<ArchNode> Containers { get; }
    public List<Outbound> Outbound { get; } = [];
    public List<DirectEdge> DirectEdges { get; } = [];
    public Dictionary<string, ArchNode> Externals { get; } = new(StringComparer.Ordinal);

    public RuleContext(ProjectInventory inventory, IReadOnlyList<ArchNode> containers)
    {
        Inventory = inventory;
        Containers = containers;
    }

    public static bool IsTestFile(string rel) => TestPath.IsMatch(rel);

    /// <summary>一个文件属于哪些容器：文件在容器某个成员目录之下。共享库的文件会属于多个容器。</summary>
    public IReadOnlyList<ArchNode> ContainersOf(string rel)
    {
        // Under 要求目录完全相等或以 "dir/" 开头，Acme.App 的文件不会算进 Acme.AppX
        return Containers.Where(c => c.Members.Any(m => Under(rel, m))).ToList();
    }

    /// <summary>所属的项目目录（最长匹配的 csproj 目录），用来查该项目的 appsettings。</summary>
    public string? ProjectDirOf(string rel) =>
        Inventory.Projects.Select(p => p.Dir).Where(d => Under(rel, d)).OrderByDescending(d => d.Length).FirstOrDefault();

    /// <summary>
    /// rel 是否在 dir 之下。dir 为 "" 表示主根：只含主根内的文件——外部根的文件（../other/…）不算，
    /// 否则主根上的前端容器（package.json 在根）会把后端外部根整个吞进去。
    /// </summary>
    public static bool Under(string rel, string dir) =>
        dir.Length == 0 ? !IsOutsideMain(rel) : rel == dir || rel.StartsWith(dir + "/", StringComparison.Ordinal);

    /// <summary>相对主根的路径落在主根之外：外部根的 ../…，或跨盘时的绝对路径。</summary>
    public static bool IsOutsideMain(string rel) =>
        rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel);

    /// <summary>C# 源码文件，排除测试文件与 obj/bin（清单已排除目录）。</summary>
    public IEnumerable<string> CSharpFiles() => Inventory.SourceFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsTestFile(f));

    /// <summary>某扩展名的源码文件（如 ".go"、".py"），排除测试文件与 protoc 生成的桩代码。</summary>
    public IEnumerable<string> SourceFilesOf(string extension) =>
        Inventory.SourceFiles.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !IsTestFile(f) && !ProjectInventory.IsGeneratedSource(f));

    public string[] Lines(string rel)
    {
        if (_lines.TryGetValue(rel, out var cached)) return cached;
        string[] lines;
        try { lines = File.ReadAllLines(Inventory.Absolute(rel)); }
        catch (IOException) { lines = []; }
        catch (UnauthorizedAccessException) { lines = []; }
        return _lines[rel] = lines;
    }

    /// <summary>登记（或取回）一个外部系统节点。key = external:{subkind}:{slug}。</summary>
    public string External(string subKind, string name, Evidence evidence)
    {
        var key = $"external:{subKind}:{Slug(name)}";
        if (Externals.TryGetValue(key, out var existing))
        {
            if (!existing.Evidence.Any(e => e.File == evidence.File && e.Line == evidence.Line))
                Externals[key] = existing with { Evidence = [.. existing.Evidence, evidence] };
            return key;
        }
        Externals[key] = new ArchNode(key, ArchNodeKind.External, subKind, name, null, ArchSource.Discovered, [evidence], [], []);
        return key;
    }

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9一-鿿]+", "-").Trim('-');
        return s.Length == 0 ? "unnamed" : s;
    }

    /// <summary>这一行在匹配位置之前是否处于注释或字符串里（粗判：// 之后，或引号数为奇数）。</summary>
    public static bool InCommentOrString(string line, int index)
    {
        var before = line[..index];
        var comment = before.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) return true;
        var quotes = 0;
        for (var i = 0; i < before.Length; i++) if (before[i] == '"' && (i == 0 || before[i - 1] != '\\')) quotes++;
        return quotes % 2 == 1;
    }
}
