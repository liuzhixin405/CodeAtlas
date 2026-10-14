using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeAtlas.Core.Architecture;

/// <param name="Dir">项目目录，相对仓库根，'/' 分隔；仓库根本身为 ""。</param>
/// <param name="ProjectRefs">引用的项目目录（同样相对仓库根）。</param>
/// <param name="Ports">http 监听端口：launchSettings 的 applicationUrl（只取 http）、Dockerfile 的 EXPOSE / ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS。</param>
public sealed record CsProject(
    string Dir, string File, string Name, string? Sdk, string? OutputType, bool IsTest,
    IReadOnlyList<string> ProjectRefs, IReadOnlyList<PackageRef> Packages, IReadOnlyList<LocalRef> LocalReferences,
    IReadOnlyList<int> Ports, IReadOnlyList<Evidence> DeployEvidence, string? Dockerfile);

public sealed record PackageRef(string Name, int Line);

/// <summary>csproj 里的 &lt;Reference Include&gt; 本地程序集（厂商 SDK、原生封装）。</summary>
public sealed record LocalRef(string Name, string? HintPath, int Line);

public sealed record FrontendApp(string Dir, string File, string Name, string Framework, IReadOnlyList<int> Ports, string? Dockerfile);

/// <param name="ProjectDir">appsettings 所在项目目录。</param>
/// <param name="Environment">appsettings.{Environment}.json 的环境名；主文件为 null。</param>
public sealed record ConfigEntry(string ProjectDir, string Key, string Value, string File, string? Environment);

/// <summary>
/// 一个仓库里和"部署 / 容器"有关的文件事实：项目、包引用、本地程序集、端口、配置、前端应用。只读文件系统，不碰代码语义。
/// </summary>
public sealed class ProjectInventory
{
    public static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea", ".next", "coverage" };

    private static readonly Regex ApplicationUrl = new("\"applicationUrl\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex DockerPort = new(@"^\s*(?:EXPOSE\s+(\d+)|ENV\s+ASPNETCORE_HTTP_PORTS=(\d+)|ENV\s+ASPNETCORE_URLS=\S*:(\d+))", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly string[] FrontendFrameworks = ["next", "vite", "react-scripts", "@angular/core", "nuxt", "@sveltejs/kit", "vue"];
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest.TestFramework"];

    public string Root { get; }
    public IReadOnlyList<CsProject> Projects { get; }
    public IReadOnlyList<FrontendApp> Frontends { get; }
    public IReadOnlyList<ConfigEntry> ConfigEntries { get; }
    /// <summary>仓库里全部源码文件（相对路径，'/' 分隔），已排除忽略目录；规则扫描用。</summary>
    public IReadOnlyList<string> SourceFiles { get; }

    private ProjectInventory(string root, List<CsProject> projects, List<FrontendApp> frontends, List<ConfigEntry> config, List<string> sources)
    {
        Root = root; Projects = projects; Frontends = frontends; ConfigEntries = config; SourceFiles = sources;
    }

    /// <summary>主文件（appsettings.json）里的值；没有则取任一环境文件的值；都没有返回 null。</summary>
    public string? Config(string projectDir, string key)
    {
        var hits = ConfigEntries.Where(c => c.ProjectDir == projectDir && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return (hits.FirstOrDefault(c => c.Environment is null) ?? hits.FirstOrDefault())?.Value;
    }

    public string Absolute(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static ProjectInventory Load(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        var csprojs = new List<string>();
        var packageJsons = new List<string>();
        var sources = new List<string>();
        Walk(root, root, csprojs, packageJsons, sources);

        var projects = csprojs.Select(p => ReadProject(root, p)).OrderBy(p => p.Dir, StringComparer.Ordinal).ToList();
        var frontends = packageJsons.Select(p => ReadFrontend(root, p)).Where(f => f is not null).Select(f => f!).OrderBy(f => f.Dir, StringComparer.Ordinal).ToList();
        var config = new List<ConfigEntry>();
        foreach (var p in projects) config.AddRange(ReadAppSettings(root, p.Dir));
        return new ProjectInventory(root, projects, frontends, config, sources);
    }

    private static void Walk(string root, string dir, List<string> csprojs, List<string> packageJsons, List<string> sources)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        foreach (var entry in entries)
        {
            var name = System.IO.Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                // 点目录（.git / .claude 的 worktrees / .vs …）一律跳过：里面是工具状态或仓库副本，不是部署单元
                if (IgnoredDirectories.Contains(name) || name.StartsWith('.')) continue;
                Walk(root, entry, csprojs, packageJsons, sources);
                continue;
            }
            var rel = Rel(root, entry);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) csprojs.Add(rel);
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) packageJsons.Add(rel);
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts") sources.Add(rel);
        }
    }

    internal static string Rel(string root, string full)
    {
        var rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static CsProject ReadProject(string root, string relFile)
    {
        var full = System.IO.Path.Combine(root, relFile.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var dir = relFile.Contains('/') ? relFile[..relFile.LastIndexOf('/')] : "";
        var name = System.IO.Path.GetFileNameWithoutExtension(relFile);
        XDocument doc;
        try { doc = XDocument.Load(full, LoadOptions.SetLineInfo); }
        catch (XmlException) { return new CsProject(dir, relFile, name, null, null, false, [], [], [], [], [new Evidence(relFile, null, "csproj 解析失败")], null); }
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var sdk = doc.Root?.Attribute("Sdk")?.Value;
        var outputType = doc.Descendants(ns + "OutputType").Select(e => e.Value.Trim()).FirstOrDefault();
        var refs = doc.Descendants(ns + "ProjectReference").Select(e => e.Attribute("Include")?.Value).Where(v => v is not null)
            .Select(v => ResolveRef(root, dir, v!)).Where(v => v is not null).Select(v => v!).Distinct().ToList();
        var packages = doc.Descendants(ns + "PackageReference").Select(e => new PackageRef(e.Attribute("Include")?.Value ?? "", Line(e))).Where(p => p.Name.Length > 0).ToList();
        var locals = doc.Descendants(ns + "Reference").Select(e => new LocalRef(e.Attribute("Include")?.Value ?? "", e.Element(ns + "HintPath")?.Value, Line(e))).Where(r => r.Name.Length > 0).ToList();
        // 测试项目：测试框架包、测试 SDK（MSTest.Sdk 这种不带 PackageReference 的），或名字以 Tests 结尾且引用了 xunit / NUnit
        var isTest = packages.Any(p => TestPackages.Any(t => p.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase)))
                     || (sdk is not null && (sdk.Contains("Test", StringComparison.OrdinalIgnoreCase)))
                     || Regex.IsMatch(name, @"\.Tests?$|Tests?$", RegexOptions.IgnoreCase) && packages.Any(p => p.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("NUnit", StringComparison.OrdinalIgnoreCase));
        var (ports, deploy, dockerfile) = ReadDeployment(root, dir);
        return new CsProject(dir, relFile, name, sdk, outputType, isTest, refs, packages, locals, ports, deploy, dockerfile);
    }

    private static int Line(XElement e) => ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : 0;

    private static string? ResolveRef(string root, string fromDir, string include)
    {
        var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, fromDir.Replace('/', System.IO.Path.DirectorySeparatorChar), include.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var rel = Rel(root, combined);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
        return rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    }

    /// <summary>launchSettings 与 Dockerfile 里的端口；Dockerfile 在项目目录或上一级目录。</summary>
    private static (List<int> Ports, List<Evidence> Evidence, string? Dockerfile) ReadDeployment(string root, string dir)
    {
        var ports = new List<int>();
        var evidence = new List<Evidence>();
        var launch = Combine(dir, "Properties/launchSettings.json");
        var launchFull = System.IO.Path.Combine(root, launch.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(launchFull))
        {
            var text = File.ReadAllText(launchFull);
            foreach (Match m in ApplicationUrl.Matches(text))
                foreach (var url in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && !ports.Contains(u.Port)) ports.Add(u.Port);
            evidence.Add(new Evidence(launch, null, "applicationUrl"));
        }
        string? dockerfile = null;
        foreach (var candidateDir in new[] { dir, Parent(dir) })
        {
            if (candidateDir is null) continue;
            var dfRel = Combine(candidateDir, "Dockerfile");
            var dfFull = System.IO.Path.Combine(root, dfRel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(dfFull)) continue;
            dockerfile = dfRel;
            var lines = File.ReadAllLines(dfFull);
            for (var i = 0; i < lines.Length; i++)
            {
                var m = DockerPort.Match(lines[i]);
                if (!m.Success) continue;
                var g = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(x => x.Success);
                if (g is not null && int.TryParse(g.Value, out var port) && !ports.Contains(port)) { ports.Add(port); evidence.Add(new Evidence(dfRel, i + 1, lines[i].Trim())); }
            }
            if (!evidence.Any(e => e.File == dfRel)) evidence.Add(new Evidence(dfRel));
            break;
        }
        return (ports, evidence, dockerfile);
    }

    private static FrontendApp? ReadFrontend(string root, string relFile)
    {
        var full = System.IO.Path.Combine(root, relFile.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var dir = relFile.Contains('/') ? relFile[..relFile.LastIndexOf('/')] : "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(full));
            var rootEl = doc.RootElement;
            // 有 build / dev / start 任一类脚本才算应用（build:dev、start:prod 这种带环境后缀的也算）；纯库没有
            if (!rootEl.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object
                || !scripts.EnumerateObject().Any(s => s.Name.StartsWith("build", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("dev", StringComparison.OrdinalIgnoreCase) || s.Name.StartsWith("start", StringComparison.OrdinalIgnoreCase)))
                return null;
            var deps = new List<string>();
            foreach (var section in new[] { "dependencies", "devDependencies" })
                if (rootEl.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object) deps.AddRange(d.EnumerateObject().Select(p => p.Name));
            var framework = FrontendFrameworks.FirstOrDefault(f => deps.Contains(f, StringComparer.OrdinalIgnoreCase));
            if (framework is null) return null;
            var name = rootEl.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : (dir.Length == 0 ? "web" : dir[(dir.LastIndexOf('/') + 1)..]);
            var (ports, _, dockerfile) = ReadDeployment(root, dir);
            return new FrontendApp(dir, relFile, name, framework, ports, dockerfile);
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<ConfigEntry> ReadAppSettings(string root, string dir)
    {
        var dirFull = System.IO.Path.Combine(root, dir.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!Directory.Exists(dirFull)) yield break;
        foreach (var file in Directory.EnumerateFiles(dirFull, "appsettings*.json"))
        {
            var fileName = System.IO.Path.GetFileName(file);
            var env = fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ? null : fileName["appsettings.".Length..^".json".Length];
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var rel = Rel(root, file);
                foreach (var (key, value) in Flatten(doc.RootElement, ""))
                    yield return new ConfigEntry(dir, key, value, rel, env);
            }
        }
    }

    private static IEnumerable<(string Key, string Value)> Flatten(JsonElement el, string prefix)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    foreach (var kv in Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + ":" + p.Name)) yield return kv;
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    foreach (var kv in Flatten(item, prefix + ":" + i++)) yield return kv;
                break;
            case JsonValueKind.String: yield return (prefix, el.GetString() ?? ""); break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False: yield return (prefix, el.GetRawText()); break;
        }
    }

    internal static string Combine(string dir, string rel) => dir.Length == 0 ? rel : dir + "/" + rel;
    private static string? Parent(string dir) => dir.Length == 0 ? null : dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
}
