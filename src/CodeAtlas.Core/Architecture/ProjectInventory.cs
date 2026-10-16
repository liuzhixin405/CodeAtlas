using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Architecture;

/// <param name="Dir">项目目录，相对仓库根，'/' 分隔；仓库根本身为 ""。</param>
/// <param name="ProjectRefs">引用的项目目录（同样相对仓库根）。</param>
/// <param name="Ports">http 监听端口：launchSettings 的 applicationUrl（只取 http）、Dockerfile 的 EXPOSE / ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS。</param>
/// <param name="Packages">&lt;PackageReference&gt; 与同目录 packages.config（旧式 .NET Framework 项目）的包。</param>
/// <param name="TargetFramework">TargetFramework(s)，旧式项目是 TargetFrameworkVersion（v4.5）。</param>
/// <param name="LegacyWeb">旧式 ASP.NET Web 应用：ProjectTypeGuids 里有 Web Application 类型（OutputType 是 Library）。</param>
/// <param name="AssemblyName">&lt;AssemblyName&gt;（可执行文件名，codeatlas）；没写为 null（= 项目名）。</param>
public sealed record CsProject(
    string Dir, string File, string Name, string? Sdk, string? OutputType, bool IsTest,
    IReadOnlyList<string> ProjectRefs, IReadOnlyList<PackageRef> Packages, IReadOnlyList<LocalRef> LocalReferences,
    IReadOnlyList<int> Ports, IReadOnlyList<Evidence> DeployEvidence, string? Dockerfile,
    string? TargetFramework = null, bool LegacyWeb = false, string? AssemblyName = null);

/// <param name="File">包声明所在文件；null 表示就是 csproj 本身（PackageReference）。packages.config 的包指向 packages.config。</param>
public sealed record PackageRef(string Name, int Line, string? File = null);

/// <summary>csproj 里的 &lt;Reference Include&gt; 本地程序集（厂商 SDK、原生封装）。</summary>
public sealed record LocalRef(string Name, string? HintPath, int Line);

public sealed record FrontendApp(string Dir, string File, string Name, string Framework, IReadOnlyList<int> Ports, string? Dockerfile);

/// <param name="ProjectDir">appsettings 所在项目目录。</param>
/// <param name="Environment">appsettings.{Environment}.json 的环境名；主文件为 null。</param>
public sealed record ConfigEntry(string ProjectDir, string Key, string Value, string File, string? Environment);

/// <summary>
/// 一个仓库里和"部署 / 容器"有关的文件事实：.NET 项目、包引用、本地程序集、端口、配置、前端应用，
/// 以及 Go 模块、Python 项目、Node 后端服务（见 ProjectInventory.Polyglot.cs）、Java 的 Maven / Gradle 模块（见 ProjectInventory.Java.cs）。只读文件系统，不碰代码语义。
/// </summary>
public sealed partial class ProjectInventory
{
    public static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea", ".next", "coverage" };

    private static readonly Regex ApplicationUrl = new("\"applicationUrl\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex DockerPort = new(@"^\s*(?:EXPOSE\s+(\d+)|ENV\s+ASPNETCORE_HTTP_PORTS=(\d+)|ENV\s+ASPNETCORE_URLS=\S*:(\d+))", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly string[] FrontendFrameworks = ["next", "vite", "react-scripts", "@angular/core", "nuxt", "@sveltejs/kit", "vue"];
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest.TestFramework"];

    /// <summary>主根。所有相对路径都相对它；外部根的文件形如 ../other/…，Absolute 照样能拼回去。</summary>
    public string Root { get; }
    public ProjectRoots Roots { get; }
    public IReadOnlyList<CsProject> Projects { get; }
    public IReadOnlyList<FrontendApp> Frontends { get; }
    public IReadOnlyList<ConfigEntry> ConfigEntries { get; }
    /// <summary>仓库里全部源码文件（相对路径，'/' 分隔），已排除忽略目录；规则扫描用。</summary>
    public IReadOnlyList<string> SourceFiles { get; }

    private ProjectInventory(ProjectRoots roots, List<CsProject> projects, List<FrontendApp> frontends, List<ConfigEntry> config, List<string> sources,
        List<GoModule> goModules, List<PythonProject> pythonProjects, List<NodeService> nodeServices)
    {
        Roots = roots; Root = roots.Main; Projects = projects; Frontends = frontends; ConfigEntries = config; SourceFiles = sources;
        GoModules = goModules; PythonProjects = pythonProjects; NodeServices = nodeServices;
    }

    /// <summary>主文件（appsettings.json）里的值；没有则取任一环境文件的值；都没有返回 null。</summary>
    public string? Config(string projectDir, string key)
    {
        var hits = ConfigEntries.Where(c => c.ProjectDir == projectDir && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
        return (hits.FirstOrDefault(c => c.Environment is null) ?? hits.FirstOrDefault())?.Value;
    }

    public string Absolute(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>单根：纳入嵌套的独立克隆与子模块，跳过 worktree。</summary>
    public static ProjectInventory Load(string root) => Load(ProjectRoots.Single(root));

    /// <summary>多根：主根 + 外部根依次遍历，路径都相对主根；根互相包含时不重复（遍历一个根时跳过另一个根的目录）。</summary>
    public static ProjectInventory Load(ProjectRoots roots)
    {
        var root = roots.Main;
        var csprojs = new List<string>();
        var packageJsons = new List<string>();
        var sources = new List<string>();
        var manifests = new List<string>();
        var javaManifests = new List<string>();
        foreach (var r in roots.All) Walk(roots, r, r, csprojs, packageJsons, sources, manifests, javaManifests);

        var projects = csprojs.Select(p => ReadProject(roots, p)).OrderBy(p => p.Dir, StringComparer.Ordinal).ToList();
        var frontends = packageJsons.Select(p => ReadFrontend(roots, p)).Where(f => f is not null).Select(f => f!).OrderBy(f => f.Dir, StringComparer.Ordinal).ToList();
        var config = new List<ConfigEntry>();
        foreach (var p in projects) config.AddRange(ReadAppSettings(root, p.Dir));
        var goModules = ReadGoModules(roots, manifests, sources);
        var pythonProjects = ReadPythonProjects(roots, manifests, sources);
        var frontendFiles = frontends.Select(f => f.File).ToHashSet(StringComparer.Ordinal);
        var nodeServices = packageJsons.Where(p => !frontendFiles.Contains(p)).Select(p => ReadNodeService(roots, p)).Where(n => n is not null).Select(n => n!)
            .OrderBy(n => n.Dir, StringComparer.Ordinal).ToList();
        return new ProjectInventory(roots, projects, frontends, config, sources, goModules, pythonProjects, nodeServices)
        {
            JavaModules = ReadJavaModules(roots, javaManifests, sources),
        };
    }

    private static void Walk(ProjectRoots roots, string currentRoot, string dir, List<string> csprojs, List<string> packageJsons, List<string> sources, List<string> manifests, List<string> javaManifests)
    {
        IEnumerable<FileSystemInfo> infos;
        try { infos = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        foreach (var info in infos)
        {
            // 符号链接 / junction 不跟（与 RepositoryScanner 一致）：Linux / macOS 上 git 检出的是真链接，指回上级目录会无限递归、指向兄弟目录会重复；
            // Windows 默认 core.symlinks=false，同一个仓库检出来是普通小文件，本来就进不去
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var entry = info.FullName;
            var name = info.Name;
            if (info is DirectoryInfo)
            {
                if (!ShouldEnter(roots, currentRoot, entry, name)) continue;
                Walk(roots, currentRoot, entry, csprojs, packageJsons, sources, manifests, javaManifests);
                continue;
            }
            var rel = roots.Relative(entry);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) csprojs.Add(rel);
            else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) packageJsons.Add(rel);
            else if (IsPolyglotManifest(name)) manifests.Add(rel);
            else if (IsJavaManifest(name)) javaManifests.Add(rel);
            // 规则逐行扫描的源码：C#、TS / JS（前端与 Node）、Go、Python、Java；各规则再按扩展名挑自己的
            var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            if (ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts" or ".go" or ".py" or ".java") sources.Add(rel);
        }
    }

    /// <summary>
    /// 清单遍历进不进这个子目录。点目录（.git / .claude 的 worktrees / .vs …）一律跳过：里面是工具状态或仓库副本，不是部署单元；
    /// 另一个根的目录（它自己遍历）、worktree、--no-nested 时的嵌套仓库也跳过。<see cref="InputFingerprint"/> 用同一套规则。
    /// </summary>
    private static bool ShouldEnter(ProjectRoots roots, string currentRoot, string entry, string name) =>
        !(IgnoredDirectories.Contains(name) || name.StartsWith('.'))
        && !(IsPolyglotNoise(entry, name) || IsMavenOutput(entry, name))
        && roots.ShouldDescend(entry, currentRoot);

    /// <summary>
    /// 清单与规则可能读到的全部输入的指纹：按清单遍历的规则走一遍（只 stat，不读内容），每个文件记 相对路径 + 大小 + 修改时间，
    /// 再加 .codeatlas/architecture.yaml（点目录不遍历）。任何源码、csproj、package.json、appsettings*.json、launchSettings.json、Dockerfile、
    /// k8s / compose 清单的增删改都会改变它。ccs-backend（7 千个源码文件）上约 0.25s（子目录并行）。
    /// </summary>
    public static string InputFingerprint(ProjectRoots roots)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string s) => hash.AppendData(System.Text.Encoding.UTF8.GetBytes(s + "\n"));
        Add("roots|" + roots.Main + "|" + string.Join("|", roots.Externals) + "|" + roots.IncludeNested);
        // 子目录并行走（每个目录还要判断嵌套仓库 / vendor / target，系统调用多），结果按名字顺序拼回，指纹与串行一致
        List<string> WalkStat(string currentRoot, string dir, int depth)
        {
            List<FileSystemInfo> infos;
            try { infos = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal).ToList(); }
            catch (UnauthorizedAccessException) { return []; }
            catch (IOException) { return []; }
            var lines = new List<string>();
            var subdirs = new List<(int At, string Dir)>();
            foreach (var info in infos)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (info is DirectoryInfo)
                {
                    if (ShouldEnter(roots, currentRoot, info.FullName, info.Name)) subdirs.Add((lines.Count, info.FullName));
                    continue;
                }
                var f = (FileInfo)info;
                lines.Add(roots.Relative(f.FullName) + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks);
            }
            var nested = new List<string>[subdirs.Count];
            if (depth < 3) Parallel.For(0, subdirs.Count, i => nested[i] = WalkStat(currentRoot, subdirs[i].Dir, depth + 1));
            else for (var i = 0; i < subdirs.Count; i++) nested[i] = WalkStat(currentRoot, subdirs[i].Dir, depth + 1);
            // 目录排在它在名字顺序里的位置上（与文件交错），和逐个递归时的顺序相同
            var result = new List<string>(lines.Count + nested.Sum(n => n.Count));
            var next = 0;
            for (var i = 0; i < subdirs.Count; i++)
            {
                for (; next < subdirs[i].At; next++) result.Add(lines[next]);
                result.Add("dir|" + roots.Relative(subdirs[i].Dir));
                result.AddRange(nested[i]);
            }
            for (; next < lines.Count; next++) result.Add(lines[next]);
            return result;
        }
        foreach (var r in roots.All)
            foreach (var line in WalkStat(r, r, 0)) Add(line);
        var declarations = new FileInfo(System.IO.Path.Combine(roots.Main, ArchitectureDeclarations.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        Add("declarations|" + (declarations.Exists ? declarations.Length + "|" + declarations.LastWriteTimeUtc.Ticks : "-"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string Rel(string root, string full)
    {
        var rel = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static CsProject ReadProject(ProjectRoots roots, string relFile)
    {
        var root = roots.Main;
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
            .Select(v => ResolveRef(roots, dir, v!)).Where(v => v is not null).Select(v => v!).Distinct().ToList();
        var packages = doc.Descendants(ns + "PackageReference").Select(e => new PackageRef(e.Attribute("Include")?.Value ?? "", Line(e))).Where(p => p.Name.Length > 0).ToList();
        foreach (var p in ReadPackagesConfig(root, dir))
            if (!packages.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) packages.Add(p);
        var locals = doc.Descendants(ns + "Reference").Select(e => new LocalRef(AssemblyName(e.Attribute("Include")?.Value ?? ""), e.Element(ns + "HintPath")?.Value, Line(e)))
            .Where(r => r.Name.Length > 0 && !IsFrameworkOrNuGetReference(r)).ToList();
        var targetFramework = new[] { "TargetFramework", "TargetFrameworks", "TargetFrameworkVersion" }
            .Select(n => doc.Descendants(ns + n).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0)).FirstOrDefault(v => v is not null);
        var legacyWeb = doc.Descendants(ns + "ProjectTypeGuids").Any(e => e.Value.Contains(WebApplicationGuid, StringComparison.OrdinalIgnoreCase));
        // 测试项目：测试框架包、测试 SDK（MSTest.Sdk 这种不带 PackageReference 的），或名字以 Tests 结尾且引用了 xunit / NUnit
        var isTest = packages.Any(p => TestPackages.Any(t => p.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith(t + ".", StringComparison.OrdinalIgnoreCase)))
                     || (sdk is not null && (sdk.Contains("Test", StringComparison.OrdinalIgnoreCase)))
                     || Regex.IsMatch(name, @"\.Tests?$|Tests?$", RegexOptions.IgnoreCase) && packages.Any(p => p.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("NUnit", StringComparison.OrdinalIgnoreCase))
                     // 测试包写在共享 props 里（<Import Project="..\framework.tests.props" />）：名字以 Tests 结尾且导入了带 test 的 props
                     || Regex.IsMatch(name, @"Tests?$", RegexOptions.IgnoreCase) && doc.Descendants(ns + "Import").Any(e => e.Attribute("Project")?.Value.Contains("test", StringComparison.OrdinalIgnoreCase) == true);
        var (ports, deploy, dockerfile) = ReadDeployment(roots, dir);
        var assemblyName = doc.Descendants(ns + "AssemblyName").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        return new CsProject(dir, relFile, name, sdk, outputType, isTest, refs, packages, locals, ports, deploy, dockerfile, targetFramework, legacyWeb, assemblyName);
    }

    private static int Line(XElement e) => ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : 0;

    /// <summary>旧式 ASP.NET Web Application 的项目类型 GUID。</summary>
    private const string WebApplicationGuid = "349c5851-65df-11da-9384-00065b846f21";
    private static readonly Regex SharedCodeAssembly = new(@"(?:Contracts?|Shared|Common|Abstractions|Dtos?|Models|Messages)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NuGetPackagesHint =new(@"(^|[\\/])packages[\\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> FrameworkAssemblies = new(StringComparer.OrdinalIgnoreCase)
        { "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework", "UIAutomationProvider", "UIAutomationTypes", "ReachFramework" };

    /// <summary>强名称 "Npgsql, Version=4.1.0.0, Culture=neutral, …" → "Npgsql"。</summary>
    private static string AssemblyName(string include) => (include.Contains(',') ? include[..include.IndexOf(',')] : include).Trim();

    /// <summary>
    /// 不是本地厂商程序集的 &lt;Reference&gt;：没有 HintPath 的框架程序集（System*、Microsoft.*、WPF 那几个），
    /// 以及 HintPath 指向 packages\ 的 NuGet 程序集（packages.config 的还原目录，包本身从 packages.config 读）。
    /// </summary>
    private static bool IsFrameworkOrNuGetReference(LocalRef r)
    {
        // 托管的共享契约 / 公共库 DLL（libs/ 下的 XxxSharedContracts.dll、Xxx.Common.dll）是代码依赖，不是运行时连接的外部系统
        if (SharedCodeAssembly.IsMatch(r.Name)) return true;
        if (r.HintPath is not null) return NuGetPackagesHint.IsMatch(r.HintPath);
        return r.Name.Equals("System", StringComparison.OrdinalIgnoreCase) || r.Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || r.Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || FrameworkAssemblies.Contains(r.Name);
    }

    /// <summary>同目录 packages.config 的 &lt;package id="…"&gt;：行号是 packages.config 里的行，File 指向它。</summary>
    private static IEnumerable<PackageRef> ReadPackagesConfig(string root, string dir)
    {
        var rel = Combine(dir, "packages.config");
        var full = System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return [];
        try
        {
            var doc = XDocument.Load(full, LoadOptions.SetLineInfo);
            return doc.Descendants().Where(e => e.Name.LocalName == "package")
                .Select(e => new PackageRef(e.Attribute("id")?.Value ?? "", Line(e), rel)).Where(p => p.Name.Length > 0).ToList();
        }
        catch (XmlException) { return []; }
        catch (IOException) { return []; }
    }

    /// <summary>ProjectReference → 被引用项目的目录（相对主根）；不在任何根之下返回 null。</summary>
    private static string? ResolveRef(ProjectRoots roots, string fromDir, string include)
    {
        var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(roots.Main, fromDir.Replace('/', System.IO.Path.DirectorySeparatorChar), include.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!roots.Contains(combined)) return null;
        var rel = roots.Relative(combined);
        return rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    }

    /// <summary>launchSettings 与 Dockerfile 里的端口；Dockerfile 在项目目录或上一级目录（项目目录本身是某个根时不往上找）。</summary>
    private static (List<int> Ports, List<Evidence> Evidence, string? Dockerfile) ReadDeployment(ProjectRoots roots, string dir)
    {
        var root = roots.Main;
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
        var isRootDir = roots.IsRoot(System.IO.Path.Combine(root, dir.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        foreach (var candidateDir in new[] { dir, isRootDir ? null : Parent(dir) })
        {
            if (candidateDir is null) continue;
            dockerfile = ReadDockerfile(root, candidateDir, ports, evidence);
            if (dockerfile is not null) break;
        }
        return (ports, evidence, dockerfile);
    }

    /// <summary>dir 下的 Dockerfile：端口（EXPOSE / ASPNETCORE_*）与证据追加到 ports / evidence；没有 Dockerfile 返回 null。</summary>
    private static string? ReadDockerfile(string root, string dir, List<int> ports, List<Evidence> evidence)
    {
        var dirFull = System.IO.Path.Combine(root, dir.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (FindFileIgnoreCase(dirFull, "Dockerfile") is not { } actualName) return null;
        var dfRel = Combine(dir, actualName);
        var dfFull = System.IO.Path.Combine(dirFull, actualName);
        var lines = File.ReadAllLines(dfFull);
        for (var i = 0; i < lines.Length; i++)
        {
            var m = DockerPort.Match(lines[i]);
            if (!m.Success) continue;
            var g = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(x => x.Success);
            if (g is not null && int.TryParse(g.Value, out var port) && !ports.Contains(port)) { ports.Add(port); evidence.Add(new Evidence(dfRel, i + 1, lines[i].Trim())); }
        }
        if (!evidence.Any(e => e.File == dfRel)) evidence.Add(new Evidence(dfRel));
        return dfRel;
    }

    private static FrontendApp? ReadFrontend(ProjectRoots roots, string relFile)
    {
        var root = roots.Main;
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
            var (ports, _, dockerfile) = ReadDeployment(roots, dir);
            return new FrontendApp(dir, relFile, name, framework, ports, dockerfile);
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<ConfigEntry> ReadAppSettings(string root, string dir)
    {
        var dirFull = System.IO.Path.Combine(root, dir.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!Directory.Exists(dirFull)) yield break;
        // 不分大小写（AppSettings.json 在 Windows 上也认）且按文件名排序：Config() 在没有主文件时取第一个环境文件，
        // 枚举顺序在 NTFS 上是按名字排好的、在 ext4 上是散列序，不排序两个平台会取到不同的值。排序键与 NTFS 一致（不分大小写在前）。
        var files = Directory.EnumerateFiles(dirFull, "appsettings*.json", CaseInsensitiveMatch)
            .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ThenBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal);
        foreach (var file in files)
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

    /// <summary>与 Directory.EnumerateFiles(path, pattern) 的默认行为相同，只是文件名匹配在所有平台都不分大小写。</summary>
    private static readonly EnumerationOptions CaseInsensitiveMatch = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Win32, AttributesToSkip = 0, IgnoreInaccessible = false,
    };

    /// <summary>
    /// 目录里名为 name 的文件（不分大小写，大小写完全相同的优先），返回磁盘上的真实文件名；没有返回 null。
    /// Windows 上 File.Exists(".../Dockerfile") 对 dockerfile 也成立、Linux 上不成立，按目录列表找两边结果一样，证据里记的也是真实文件名。
    /// </summary>
    internal static string? FindFileIgnoreCase(string dirFull, string name)
    {
        try
        {
            string? found = null;
            foreach (var f in Directory.EnumerateFiles(dirFull, name, CaseInsensitiveMatch))
            {
                var actual = System.IO.Path.GetFileName(f);
                if (actual == name) return actual;
                if (found is null || string.CompareOrdinal(actual, found) < 0) found = actual;
            }
            return found;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static string Combine(string dir, string rel) => dir.Length == 0 ? rel : dir + "/" + rel;
    private static string? Parent(string dir) => dir.Length == 0 ? null : dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
}
