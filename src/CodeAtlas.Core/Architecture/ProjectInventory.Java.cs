using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Architecture;

/// <summary>Spring 配置（application / bootstrap 的 yml / properties）扁平化后的一项。</summary>
/// <param name="Key">点分键，列表项写成 [i]：spring.cloud.gateway.routes[0].uri。</param>
/// <param name="Profile">profile 文件（application-mysql.properties）或文档里 spring.config.activate.on-profile 给的 profile；主配置为 null。</param>
public sealed record SpringConfigEntry(string Key, string Value, string File, int Line, string? Profile)
{
    /// <summary>Spring 的宽松绑定：大小写、- 与 _ 不敏感（serviceUrl ↔ service-url）。</summary>
    public static bool SameKey(string a, string b) => Canonical(a) == Canonical(b);
    public static string Canonical(string key) => new(key.Where(c => c is not ('-' or '_')).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// Java 的一个构建模块：一个 pom.xml（Maven）或 build.gradle / build.gradle.kts（Gradle）所在目录；同目录两种都有时 Maven 优先。
/// </summary>
/// <param name="BuildTool">maven / gradle。</param>
/// <param name="ArtifactId">Maven 的 artifactId；Gradle 是目录名（根目录取仓库目录名）。</param>
/// <param name="Name">容器名：spring.application.name 优先，没有就是 ArtifactId。</param>
/// <param name="Dependencies">直接依赖 groupId:artifactId（test 范围 / test* 配置的不算），出处是 pom 的 &lt;dependency&gt; 行或 Gradle 的坐标行。</param>
/// <param name="ModuleRefs">依赖的本仓库模块目录（Maven 按 groupId:artifactId 对上，Gradle 按 project(':x')）。</param>
/// <param name="SubModules">Maven &lt;modules&gt; / settings.gradle include 列出的子模块目录。</param>
/// <param name="DeployEvidence">可部署的证据：@SpringBootApplication、main(String[])、Dockerfile、端口；为空即不是容器。</param>
/// <param name="Markers">服务端角色注解：@EnableEurekaServer / @EnableConfigServer。</param>
/// <param name="PackageDirs">属于本模块（最近的构建文件是它）的、含非测试 .java 文件的目录。</param>
public sealed record JavaModule(
    string Dir, string BuildFile, string BuildTool, string? GroupId, string ArtifactId, string? Packaging, string Name,
    IReadOnlyList<DependencyRef> Dependencies, IReadOnlyList<string> ModuleRefs, IReadOnlyList<string> SubModules,
    string? AppName, bool IsSpringBoot, bool IsTest, IReadOnlyList<Evidence> DeployEvidence, IReadOnlyList<Evidence> Markers,
    IReadOnlyList<int> Ports, string? Dockerfile, IReadOnlyList<SpringConfigEntry> Config, IReadOnlyList<string> PackageDirs)
{
    /// <summary>主配置（非 profile）里的键。</summary>
    public SpringConfigEntry? Setting(string key) => Config.FirstOrDefault(c => c.Profile is null && SpringConfigEntry.SameKey(c.Key, key));

    public bool HasDependency(string groupArtifact) => Dependencies.Any(d => d.Name.Equals(groupArtifact, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Spring Boot 配置文件的极简读取：YAML 的缩进映射、列表（含与父键同缩进的写法）、多文档（---），properties 的 key=value / key: value 与 #--- 分文档。
/// 不处理锚点、流式映射、多行字符串（块标量整个跳过）。
/// </summary>
public static class SpringConfigReader
{
    private static readonly Regex YamlKey = new(@"^(""[^""]*""|'[^']*'|[^:#\s][^:#]*?)\s*:(?:\s+(.*))?$", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"^\$\{([^:}]+)(?::(.*))?\}$", RegexOptions.Compiled);

    private sealed record Frame(int Indent, string Path, bool IsItem)
    {
        public int NextIndex { get; set; }
    }

    /// <param name="fileProfile">文件名带的 profile（application-mysql.yml → mysql）；文档自己声明的 profile 优先。</param>
    public static List<SpringConfigEntry> Parse(string file, string[] lines, string? fileProfile) =>
        file.EndsWith(".properties", StringComparison.OrdinalIgnoreCase) ? ParseProperties(file, lines, fileProfile) : ParseYaml(file, lines, fileProfile);

    /// <summary>${ENV:缺省值} → 缺省值；${ENV} 没有缺省值 → null；不是占位符原样返回。</summary>
    public static string? Resolve(string? value)
    {
        if (value is null) return null;
        var m = Placeholder.Match(value.Trim());
        if (!m.Success) return value;
        return m.Groups[2].Success ? m.Groups[2].Value : null;
    }

    private static List<SpringConfigEntry> ParseYaml(string file, string[] lines, string? fileProfile)
    {
        var result = new List<SpringConfigEntry>();
        var doc = new List<SpringConfigEntry>();
        var stack = new List<Frame>();
        var blockIndent = -1; // 块标量（| / >）的键缩进：更深的行都是字符串内容
        void Flush()
        {
            var activation = doc.FirstOrDefault(e => SpringConfigEntry.SameKey(e.Key, "spring.config.activate.on-profile") || SpringConfigEntry.SameKey(e.Key, "spring.profiles"))?.Value;
            var profile = activation ?? fileProfile;
            result.AddRange(doc.Select(e => e with { Profile = profile }));
            doc.Clear();
            stack.Clear();
            blockIndent = -1;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.TrimEnd() is "---" or "...") { Flush(); continue; }
            var content = StripComment(raw).TrimEnd();
            if (content.Trim().Length == 0) continue;
            var indent = content.Length - content.TrimStart().Length;
            if (blockIndent >= 0)
            {
                if (indent > blockIndent) continue;
                blockIndent = -1;
            }
            var text = content.TrimStart();
            if (text == "-" || text.StartsWith("- ", StringComparison.Ordinal))
            {
                while (stack.Count > 0 && (stack[^1].Indent > indent || (stack[^1].Indent == indent && stack[^1].IsItem))) stack.RemoveAt(stack.Count - 1);
                var parent = stack.Count > 0 ? stack[^1] : null;
                var index = parent is null ? 0 : parent.NextIndex++;
                var itemPath = (parent?.Path ?? "") + "[" + index + "]";
                stack.Add(new Frame(indent, itemPath, IsItem: true));
                var rest = text.Length > 1 ? text[2..].TrimStart() : "";
                if (rest.Length == 0) continue;
                var restIndent = indent + (text.Length - rest.Length);
                if (YamlKey.Match(rest) is { Success: true } km) KeyLine(km, restIndent, i);
                else doc.Add(new SpringConfigEntry(itemPath, Unquote(rest), file, i + 1, null));
                continue;
            }
            var m = YamlKey.Match(text);
            if (!m.Success) continue;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            KeyLine(m, indent, i);
        }
        Flush();
        return result;

        void KeyLine(Match m, int indent, int i)
        {
            var key = Unquote(m.Groups[1].Value.Trim());
            var parent = stack.Count > 0 ? stack[^1] : null;
            var path = parent is null ? key : parent.Path + "." + key;
            var value = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            if (value.Length == 0) { stack.Add(new Frame(indent, path, IsItem: false)); return; }
            if (value[0] is '|' or '>') { blockIndent = indent; return; }
            doc.Add(new SpringConfigEntry(path, Unquote(value), file, i + 1, null));
        }
    }

    private static List<SpringConfigEntry> ParseProperties(string file, string[] lines, string? fileProfile)
    {
        var result = new List<SpringConfigEntry>();
        var doc = new List<SpringConfigEntry>();
        void Flush()
        {
            var activation = doc.FirstOrDefault(e => SpringConfigEntry.SameKey(e.Key, "spring.config.activate.on-profile") || SpringConfigEntry.SameKey(e.Key, "spring.profiles"))?.Value;
            result.AddRange(doc.Select(e => e with { Profile = activation ?? fileProfile }));
            doc.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t is "#---" or "!---") { Flush(); continue; }
            if (t.Length == 0 || t[0] is '#' or '!') continue;
            var start = i;
            while (t.EndsWith('\\') && i + 1 < lines.Length) t = t[..^1] + lines[++i].Trim();
            var cut = t.IndexOfAny(['=', ':']);
            if (cut <= 0) continue;
            doc.Add(new SpringConfigEntry(t[..cut].Trim(), t[(cut + 1)..].Trim(), file, start + 1, null));
        }
        Flush();
        return result;
    }

    /// <summary>去掉 # 注释（引号里的不算；# 前面要是空白或行首）。</summary>
    private static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '"' or '\'' && (i == 0 || line[i - 1] is ' ' or ':' or '-' or '\t' or '[' or ',')) quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')) ? s[1..^1] : s;
}

public sealed partial class ProjectInventory
{
    public IReadOnlyList<JavaModule> JavaModules { get; private set; } = [];

    private static readonly Regex JavaSpringBootApp = new(@"^\s*@SpringBootApplication\b", RegexOptions.Compiled);
    private static readonly Regex JavaMain = new(@"\bpublic\s+static\s+void\s+main\s*\(\s*(?:final\s+)?String\s*(?:\[\s*\]\s*\w+|\.\.\.\s*\w+|\w+\s*\[\s*\])\s*\)", RegexOptions.Compiled);
    private static readonly Regex JavaMarker = new(@"^\s*@(EnableEurekaServer|EnableConfigServer|EnableAdminServer)\b", RegexOptions.Compiled);
    private static readonly Regex JavaCodePort = new(@"(?:getOrDefault|getProperty|getenv)\(\s*""[\w.\-]*(?:PORT|port)[\w.\-]*""\s*,\s*""(\d{2,5})""|\bforPort\(\s*(\d{2,5})\s*\)|new\s+InetSocketAddress\(\s*(\d{2,5})\s*\)", RegexOptions.Compiled);
    private static readonly Regex GradleConfig = new(@"^\s*(implementation|api|compile|runtime|runtimeOnly|compileOnly|compileOnlyApi|developmentOnly|annotationProcessor|kapt|ksp|\w*[Tt]est\w*)\b\s*[\s(]", RegexOptions.Compiled);
    private static readonly Regex GradleCoordinate = new(@"[""']([A-Za-z0-9_.\-]+):([A-Za-z0-9_.\-]+)(?::[^""'\s]*)?[""']", RegexOptions.Compiled);
    private static readonly Regex GradleMapCoordinate = new(@"group\s*[:=]\s*[""']([\w.\-]+)[""']\s*,\s*name\s*[:=]\s*[""']([\w.\-]+)[""']", RegexOptions.Compiled);
    private static readonly Regex GradleProjectRef = new(@"project\(\s*(?:path\s*[:=]\s*)?[""'](:?[\w.\-:]+)[""']\s*\)", RegexOptions.Compiled);
    private static readonly Regex SpringConfigFile = new(@"^(application|bootstrap)(?:-([\w\-.]+?))?\.(yml|yaml|properties)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JavaTestModule = new(@"(^|[-_.])(it|its|tests?|test-?support|integration-?tests?|e2e|acceptance-?tests?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 会起 HTTP 监听的依赖（Spring Boot 没写 server.port 时才按缺省 8080 记）
    private static readonly Regex JavaWebDependency = new(@":(spring-boot-starter-(?:web|webmvc|webflux|jersey|thymeleaf|freemarker|mustache|graphql|websocket)|spring-cloud-starter-gateway[\w\-]*|spring-cloud-starter-netflix-eureka-server|spring-cloud-config-server|spring-boot-admin-starter-server)$", RegexOptions.Compiled);

    private static bool IsJavaManifest(string name) =>
        name is "pom.xml" or "build.gradle" or "build.gradle.kts" or "settings.gradle" or "settings.gradle.kts";

    /// <summary>pom.xml 旁边的 target/ 是 Maven 的构建输出（Gradle 的 build/ 已在忽略目录里）。</summary>
    private static bool IsMavenOutput(string fullDir, string name) =>
        name.Equals("target", StringComparison.OrdinalIgnoreCase) && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fullDir) ?? "", "pom.xml"));

    /// <summary>构建中的模块：先读构建文件，所有模块都读完后再解析模块引用、可部署性。</summary>
    private sealed class JavaDraft
    {
        public required string Dir;
        public required string BuildFile;
        public required string Tool;
        public string? GroupId;
        public string? ParentGroupId;
        public required string ArtifactId;
        public string? Packaging;
        public List<(string Group, string Artifact, string Scope, int Line)> RawDeps = [];
        public List<string> ProjectPaths = [];
        public List<string> SubModules = [];
    }

    private static List<JavaModule> ReadJavaModules(ProjectRoots roots, List<string> manifests, List<string> sources)
    {
        if (manifests.Count == 0) return [];
        var byDir = manifests.GroupBy(DirOf).ToDictionary(g => g.Key, g => g.Select(f => System.IO.Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var drafts = new List<JavaDraft>();
        var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal); // settings.gradle 目录 → include 的项目路径
        foreach (var (dir, names) in byDir.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var settingsFile = names.Contains("settings.gradle") ? "settings.gradle" : names.Contains("settings.gradle.kts") ? "settings.gradle.kts" : null;
            if (settingsFile is not null) settings[dir] = ParseGradleSettings(ReadLines(roots, Combine(dir, settingsFile)));
            if (names.Contains("pom.xml")) { if (ParsePom(roots, dir) is { } d) drafts.Add(d); }
            else if (names.Contains("build.gradle") || names.Contains("build.gradle.kts"))
                drafts.Add(ParseGradle(roots, dir, names.Contains("build.gradle") ? "build.gradle" : "build.gradle.kts"));
        }
        foreach (var d in drafts.Where(d => d.Tool == "gradle" && settings.TryGetValue(d.Dir, out _)))
            d.SubModules.AddRange(settings[d.Dir].Select(p => Combine(d.Dir, GradlePathToDir(p))));

        var dirs = drafts.Select(d => d.Dir).ToList();
        var javaFiles = sources.Where(s => s.EndsWith(".java", StringComparison.OrdinalIgnoreCase)).ToList();
        var byModule = javaFiles.GroupBy(f => Nearest(f, dirs)).Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.Ordinal);

        // 模块引用：Maven 按 artifactId（groupId 已知时也要相同），Gradle 的 project(':a:b') 相对最近的 settings.gradle 目录
        var refs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in drafts)
        {
            var list = new List<string>();
            foreach (var dep in d.RawDeps.Where(x => x.Scope != "test"))
            {
                var target = drafts.FirstOrDefault(o => o != d && o.Tool == "maven" && o.ArtifactId == dep.Artifact && (o.GroupId is null || dep.Group.Length == 0 || o.GroupId == dep.Group));
                if (target is not null && !list.Contains(target.Dir)) list.Add(target.Dir);
            }
            foreach (var p in d.ProjectPaths)
            {
                var settingsDir = settings.Keys.Where(s => RuleContext.Under(d.Dir, s)).OrderByDescending(s => s.Length).Cast<string?>().FirstOrDefault() ?? Parent(d.Dir) ?? "";
                var target = Combine(settingsDir, GradlePathToDir(p));
                if (drafts.Any(o => o.Dir == target) && !list.Contains(target)) list.Add(target);
            }
            refs[d.Dir] = list;
        }
        var dependedUpon = refs.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);

        var result = new List<JavaModule>();
        foreach (var d in drafts)
        {
            var deps = d.RawDeps.Where(x => x.Scope != "test").Select(x => new DependencyRef(x.Group + ":" + x.Artifact, Combine(d.Dir, d.BuildFile), x.Line)).ToList();
            var own = byModule.TryGetValue(d.Dir, out var fs) ? fs : [];
            var nonTest = own.Where(f => !RuleContext.IsTestFile(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var isTest = JavaTestModule.IsMatch(d.ArtifactId) || JavaTestModule.IsMatch(LastSegmentOf(d.Dir)) || (own.Count > 0 && nonTest.Count == 0);
            var config = ReadSpringConfig(roots, d.Dir);
            var appName = config.Where(c => c.Profile is null && SpringConfigEntry.SameKey(c.Key, "spring.application.name"))
                .Select(c => SpringConfigReader.Resolve(c.Value)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var evidence = new List<Evidence>();
            var markers = new List<Evidence>();
            var ports = new List<int>();
            bool boot = false, main = false;
            var mainFiles = new List<(string File, string[] Lines)>();
            foreach (var f in nonTest)
            {
                var lines = ReadLines(roots, f);
                var hasMain = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*')) continue;
                    if (JavaSpringBootApp.IsMatch(lines[i])) { boot = true; evidence.Add(new Evidence(f, i + 1, "@SpringBootApplication")); }
                    if (JavaMain.IsMatch(lines[i]) && !hasMain) { hasMain = true; main = true; evidence.Add(new Evidence(f, i + 1, "main(String[])")); }
                    if (JavaMarker.Match(lines[i]) is { Success: true } mk) markers.Add(new Evidence(f, i + 1, "@" + mk.Groups[1].Value));
                }
                if (hasMain) mainFiles.Add((f, lines));
            }
            var portEvidence = new List<Evidence>();
            var dockerfile = ReadDockerfile(roots.Main, d.Dir, ports, portEvidence);
            if (config.FirstOrDefault(c => c.Profile is null && SpringConfigEntry.SameKey(c.Key, "server.port")) is { } sp
                && int.TryParse(SpringConfigReader.Resolve(sp.Value), out var port) && port > 0 && !ports.Contains(port))
            {
                ports.Add(port);
                portEvidence.Add(new Evidence(sp.File, sp.Line, $"server.port={sp.Value}"));
            }
            foreach (var (f, lines) in mainFiles)
                for (var i = 0; i < lines.Length; i++)
                    if (JavaCodePort.Match(lines[i]) is { Success: true } pm && int.TryParse(pm.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value, out var cp) && !ports.Contains(cp))
                    { ports.Add(cp); portEvidence.Add(new Evidence(f, i + 1, lines[i].Trim())); }
            // 配置来自配置中心（spring.config.import: configserver: / spring-cloud-starter-config）时端口多半在那边，不按缺省猜
            var configImport = config.Any(c => SpringConfigEntry.SameKey(c.Key, "spring.config.import") && c.Value.Contains("configserver:", StringComparison.OrdinalIgnoreCase))
                               || deps.Any(x => x.Name.EndsWith(":spring-cloud-starter-config", StringComparison.Ordinal) || x.Name.EndsWith(":spring-cloud-config-client", StringComparison.Ordinal));
            if (ports.Count == 0 && boot && !configImport && deps.Any(x => JavaWebDependency.IsMatch(x.Name)))
            {
                ports.Add(8080);
                var at = evidence.First(e => e.Note == "@SpringBootApplication");
                portEvidence.Add(new Evidence(at.File, at.Line, "Spring Boot 缺省端口 8080"));
            }
            ports.Sort();
            // 可部署：Spring Boot 应用、Dockerfile，或有 main 且没被别的模块依赖（被依赖的是库，里面的 main 多是演示）；纯父 pom、测试模块不是
            var deployable = !isTest && d.Packaging != "pom" && (boot || dockerfile is not null || (main && !dependedUpon.Contains(d.Dir)));
            if (dockerfile is not null && !portEvidence.Any(e => e.File == dockerfile)) evidence.Add(new Evidence(dockerfile));
            evidence.AddRange(portEvidence);
            if (appName is not null && config.FirstOrDefault(c => c.Profile is null && SpringConfigEntry.SameKey(c.Key, "spring.application.name")) is { } an)
                evidence.Add(new Evidence(an.File, an.Line, $"spring.application.name={appName}"));
            var packageDirs = nonTest.Select(DirOf).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            result.Add(new JavaModule(d.Dir, Combine(d.Dir, d.BuildFile), d.Tool, d.GroupId, d.ArtifactId, d.Packaging, appName ?? d.ArtifactId,
                deps, refs[d.Dir], d.SubModules, appName, boot, isTest, deployable ? evidence : [], markers, ports, dockerfile, config, packageDirs));
        }
        return result.OrderBy(m => m.Dir, StringComparer.Ordinal).ToList();

        string GradleName(string dir) => dir.Length == 0 ? RootName(roots) : LastSegmentOf(dir);

        JavaDraft ParseGradle(ProjectRoots r, string dir, string file)
        {
            var draft = new JavaDraft { Dir = dir, BuildFile = file, Tool = "gradle", ArtifactId = GradleName(dir) };
            var lines = ReadLines(r, Combine(dir, file));
            string? scope = null;
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var cut = line.IndexOf("//", StringComparison.Ordinal);
                if (cut >= 0 && !line[..cut].Contains("://", StringComparison.Ordinal)) line = line[..cut];
                if (GradleConfig.Match(line) is { Success: true } cm) { scope = cm.Groups[1].Value.Contains("test", StringComparison.OrdinalIgnoreCase) ? "test" : "compile"; depth = 0; }
                else if (scope is null) continue;
                foreach (Match m in GradleCoordinate.Matches(line)) draft.RawDeps.Add((m.Groups[1].Value, m.Groups[2].Value, scope, i + 1));
                foreach (Match m in GradleMapCoordinate.Matches(line)) draft.RawDeps.Add((m.Groups[1].Value, m.Groups[2].Value, scope, i + 1));
                if (scope != "test") foreach (Match m in GradleProjectRef.Matches(line)) draft.ProjectPaths.Add(m.Groups[1].Value);
                // 配置语句跨行：行尾逗号，或括号没闭合
                depth += line.Count(c => c == '(') - line.Count(c => c == ')');
                if (!(line.TrimEnd().EndsWith(',') || depth > 0)) scope = null;
            }
            return draft;
        }
    }

    /// <summary>pom.xml：groupId（没写取 parent 的）、artifactId、packaging、modules、project/dependencies（不含 dependencyManagement、插件、profile 里的）。</summary>
    private static JavaDraft? ParsePom(ProjectRoots roots, string dir)
    {
        var rel = Combine(dir, "pom.xml");
        XDocument doc;
        try { doc = XDocument.Load(System.IO.Path.Combine(roots.Main, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)), LoadOptions.SetLineInfo); }
        catch (XmlException) { return null; }
        catch (IOException) { return null; }
        var project = doc.Root;
        if (project is null || project.Name.LocalName != "project") return null;
        var ns = project.Name.Namespace;
        string? Text(XElement? e, string name) => e?.Element(ns + name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var parent = project.Element(ns + "parent");
        var draft = new JavaDraft
        {
            Dir = dir, BuildFile = "pom.xml", Tool = "maven", ParentGroupId = Text(parent, "groupId"),
            ArtifactId = Text(project, "artifactId") ?? LastSegmentOf(dir), Packaging = Text(project, "packaging") ?? "jar",
        };
        draft.GroupId = Text(project, "groupId") ?? draft.ParentGroupId;
        var properties = project.Element(ns + "properties")?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal) ?? [];
        string Expand(string v) => Regex.Replace(v, @"\$\{([^}]+)\}", m => m.Groups[1].Value switch
        {
            "project.groupId" or "groupId" or "pom.groupId" => draft.GroupId ?? m.Value,
            "project.parent.groupId" or "parent.groupId" => draft.ParentGroupId ?? m.Value,
            var p => properties.TryGetValue(p, out var x) ? x : m.Value,
        });
        foreach (var m in project.Element(ns + "modules")?.Elements(ns + "module") ?? [])
            if (m.Value.Trim() is { Length: > 0 } sub) draft.SubModules.Add(Combine(dir, sub.Replace('\\', '/').TrimEnd('/')));
        foreach (var dep in project.Element(ns + "dependencies")?.Elements(ns + "dependency") ?? [])
        {
            var g = Text(dep, "groupId");
            var a = Text(dep, "artifactId");
            if (a is null) continue;
            var scope = Text(dep, "scope") ?? "compile";
            draft.RawDeps.Add((g is null ? "" : Expand(g), Expand(a), scope == "test" ? "test" : scope, Line(dep)));
        }
        return draft;
    }

    /// <summary>settings.gradle 的 include 'a', ':b:c' / include("a")。</summary>
    private static List<string> ParseGradleSettings(string[] lines)
    {
        var found = new List<string>();
        foreach (var line in lines)
        {
            if (!Regex.IsMatch(line, @"^\s*include\b")) continue;
            foreach (Match m in Quoted.Matches(line)) found.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        return found;
    }

    private static string GradlePathToDir(string projectPath) => projectPath.Trim().TrimStart(':').Replace(':', '/');

    /// <summary>模块的 Spring 配置：src/main/resources、模块目录、config/ 下的 application*/bootstrap* 的 yml / yaml / properties。bootstrap 先读。</summary>
    private static List<SpringConfigEntry> ReadSpringConfig(ProjectRoots roots, string dir)
    {
        var result = new List<SpringConfigEntry>();
        foreach (var sub in new[] { "src/main/resources", "src/main/resources/config", "", "config" })
        {
            var d = sub.Length == 0 ? dir : Combine(dir, sub);
            var full = System.IO.Path.Combine(roots.Main, d.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(full).Select(f => System.IO.Path.GetFileName(f)).Where(n => SpringConfigFile.IsMatch(n)).ToList(); }
            catch (IOException) { continue; }
            foreach (var name in files.OrderBy(n => n.StartsWith("bootstrap", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal))
            {
                var m = SpringConfigFile.Match(name);
                var rel = Combine(d, name);
                result.AddRange(SpringConfigReader.Parse(rel, ReadLines(roots, rel), m.Groups[2].Success ? m.Groups[2].Value : null));
            }
        }
        return result;
    }
}
