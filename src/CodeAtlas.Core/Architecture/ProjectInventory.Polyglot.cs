using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Architecture;

/// <summary>go.mod 的 require、requirements / pyproject / Pipfile / setup.py 的依赖：名字 + 出处（文件、1 起的行号）。</summary>
public sealed record DependencyRef(string Name, string File, int Line);

/// <summary>Go 的可部署单元：含 package main 且有 func main() 的目录。</summary>
/// <param name="File">func main() 所在文件；Line 是那一行。</param>
/// <param name="Ports">Dockerfile EXPOSE（main 目录、再模块目录）+ 代码里的端口缺省值 / 监听字面量。</param>
public sealed record GoMain(string Dir, string File, int Line, IReadOnlyList<int> Ports, IReadOnlyList<Evidence> Evidence, string? Dockerfile);

/// <param name="Dir">go.mod 所在目录（相对主根）。</param>
/// <param name="Requires">直接依赖（// indirect 的不算）。</param>
/// <param name="PackageDirs">属于本模块（最近的 go.mod 是它）的、含非测试 .go 文件的目录。</param>
public sealed record GoModule(string Dir, string File, string ModulePath, IReadOnlyList<DependencyRef> Requires, IReadOnlyList<GoMain> Mains, IReadOnlyList<string> PackageDirs);

/// <param name="Dir">清单（pyproject.toml / setup.py / requirements*.txt / manage.py / Pipfile）所在目录。</param>
/// <param name="DeployEvidence">可部署的证据：manage.py、Dockerfile、入口文件（uvicorn.run / FastAPI( / Flask( / grpc.server( / __main__）、命令行脚本（console_scripts）；为空即不是容器。</param>
/// <param name="Framework">django / fastapi / flask / grpc；都不是为 null。</param>
/// <param name="PackageDirs">属于本项目（最近的清单目录是它）的、含非测试 .py 文件的目录。</param>
public sealed record PythonProject(
    string Dir, IReadOnlyList<string> Manifests, string Name, IReadOnlyList<DependencyRef> Dependencies, IReadOnlyList<Evidence> DeployEvidence,
    IReadOnlyList<int> Ports, string? Dockerfile, string? Framework, bool IsTest, IReadOnlyList<string> PackageDirs);

/// <summary>
/// Node 后端服务：不是前端应用的 package.json，依赖了服务端框架（@grpc/grpc-js / express / fastify …），且同目录有 Dockerfile；
/// 或命令行工具：package.json 有 bin 字段（IsCli，Server = "cli"，不要求 Dockerfile——命令行是 npm 发布、本机执行的）。
/// </summary>
/// <param name="IsVsCodeExtension">VS Code 扩展：package.json 有 engines.vscode 与 main / browser（扩展宿主加载的入口）；Server = "vscode"。</param>
/// <param name="PortEvidence">端口出处：Dockerfile EXPOSE 之外，入口文件里的 .listen(3000) / PORT || 3000。</param>
public sealed record NodeService(string Dir, string File, string Name, string Server, IReadOnlyList<int> Ports, string? Dockerfile, bool IsCli = false,
    bool IsVsCodeExtension = false, IReadOnlyList<Evidence>? PortEvidence = null);

public sealed partial class ProjectInventory
{
    public IReadOnlyList<GoModule> GoModules { get; }
    public IReadOnlyList<PythonProject> PythonProjects { get; }
    public IReadOnlyList<NodeService> NodeServices { get; }

    /// <summary>Python / Go 生态里不是源码的目录：虚拟环境、字节码缓存、site-packages、带 modules.txt 的 Go vendor。</summary>
    private static readonly HashSet<string> PolyglotIgnored = new(StringComparer.OrdinalIgnoreCase) { "venv", "__pycache__", "site-packages", "__pypackages__" };
    private static readonly string[] NodeServers = ["@grpc/grpc-js", "grpc", "@nestjs/core", "express", "fastify", "koa", "@hapi/hapi", "restify"];
    private static readonly HashSet<string> PythonTestDirs = new(StringComparer.OrdinalIgnoreCase) { "tests", "test", "testing", "e2e", "integration_tests" };

    private static readonly Regex GoPackage = new(@"^\s*package\s+(\w+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex GoFuncMain = new(@"^func\s+main\s*\(\s*\)", RegexOptions.Compiled);
    // port = "8081" / listenPort = "5050" / defaultPort = "50051"（PORT 环境变量的缺省值通常这么写）
    private static readonly Regex GoPortVar = new(@"\b\w*[Pp]ort\w*\s*(?::=|=)\s*""(\d{2,5})""", RegexOptions.Compiled);
    // http.ListenAndServe(":8080" / net.Listen("tcp", ":50051") / r.Run(":8080") / Addr: ":8080"
    private static readonly Regex GoListenCall = new(@"\b(?:Listen\w*|Run|Serve\w*)\s*\(|\bAddr\s*:", RegexOptions.Compiled);
    private static readonly Regex GoAddrLiteral = new(@"""(?:[\w.\-]+|\[[:\w]*\])?:(\d{2,5})""", RegexOptions.Compiled);
    private static readonly Regex GoFlagPort = new(@"\bflag\.(?:Int|String|Uint)(?:Var)?\(\s*(?:&?\w+\s*,\s*)?""[\w\-]*port[\w\-]*""\s*,\s*""?(\d{2,5})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PyFastApi = new(@"^\s*\w+\s*(?::\s*\w+\s*)?=\s*FastAPI\(", RegexOptions.Compiled);
    private static readonly Regex PyFlask = new(@"^\s*\w+\s*=\s*Flask\(", RegexOptions.Compiled);
    private static readonly Regex PyUvicorn = new(@"\buvicorn\.run\(", RegexOptions.Compiled);
    private static readonly Regex PyGrpcServer = new(@"\bgrpc\.(?:aio\.)?server\(", RegexOptions.Compiled);
    private static readonly Regex PyMain = new(@"^if\s+__name__\s*==\s*['""]__main__['""]", RegexOptions.Compiled);
    private static readonly Regex PyEnvPort = new(@"(?:environ\.get|getenv)\(\s*['""]\w*PORT\w*['""]\s*,\s*['""]?(\d{2,5})", RegexOptions.Compiled);
    private static readonly Regex PyRunPort = new(@"\.run\(.*\bport\s*=\s*['""]?(\d{2,5})\b", RegexOptions.Compiled);
    private static readonly Regex PyGrpcPort = new(@"add_(?:in)?secure_port\(\s*['""][^'""]*:(\d{2,5})['""]", RegexOptions.Compiled);
    private static readonly Regex PyName = new(@"^\s*([A-Za-z0-9][A-Za-z0-9._\-]*)", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"""([^""]*)""|'([^']*)'", RegexOptions.Compiled);

    private static bool IsPolyglotNoise(string fullDir, string name) =>
        PolyglotIgnored.Contains(name)
        || (name.Equals("vendor", StringComparison.OrdinalIgnoreCase) && File.Exists(System.IO.Path.Combine(fullDir, "modules.txt")));

    private static bool IsPolyglotManifest(string name) =>
        name is "go.mod" or "pyproject.toml" or "setup.py" or "Pipfile" or "manage.py"
        || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".in", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// 按文件名约定认生成代码：protoc 的 *.pb.go（含 *_grpc.pb.go、*.pb.gw.go）、*_pb2.py / *_pb2_grpc.py / *_pb2.pyi、grpc-tools 的 *_pb.js / *_grpc_pb.js，
    /// C# 的 *.g.cs / *.g.i.cs / *.Designer.cs / *.generated.cs。里面是桩与设计器代码，不是调用 / 注册，也不是架构的一部分。
    /// </summary>
    public static bool IsGeneratedSource(string rel) => GeneratedSuffixes.Any(s => rel.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] GeneratedSuffixes =
        [".pb.go", ".pb.gw.go", "_pb2.py", "_pb2_grpc.py", "_pb2.pyi", "_pb.js", "_grpc_pb.js", "_pb.d.ts", "_grpc_pb.d.ts", ".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs"];

    /// <summary>生成器在文件头留的标记：Go 的 "Code generated … DO NOT EDIT."、C# 的 &lt;auto-generated&gt;、protoc 的 "Generated by the protocol buffer compiler"。</summary>
    private static readonly Regex GeneratedHeader = new(@"^\s*(?://|#|/\*|\*)?\s*(?:Code generated\b.*\bDO NOT EDIT\b|<auto-generated\b|Generated by the protocol buffer compiler\b|@generated\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>文件头看多少行：许可证头之后才有生成标记（microservices-demo 的 genproto/*.pb.go 在第 15 行）。</summary>
    public const int GeneratedHeaderLines = 40;

    /// <summary>文件名约定或文件头前 <see cref="GeneratedHeaderLines"/> 行里的生成标记，任一命中就是生成代码。</summary>
    public static bool IsGeneratedSource(string rel, IReadOnlyList<string> lines)
    {
        if (IsGeneratedSource(rel)) return true;
        for (var i = 0; i < lines.Count && i < GeneratedHeaderLines; i++)
            if (GeneratedHeader.IsMatch(lines[i])) return true;
        return false;
    }

    private static string DirOf(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    private static string LastSegmentOf(string dir) => dir.Contains('/') ? dir[(dir.LastIndexOf('/') + 1)..] : dir;

    private static string[] ReadLines(ProjectRoots roots, string rel)
    {
        try { return File.ReadAllLines(System.IO.Path.Combine(roots.Main, rel.Replace('/', System.IO.Path.DirectorySeparatorChar))); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>rel 所在的、目录最长的那个 dir（最近的清单）；都不在之下返回 null。</summary>
    private static string? Nearest(string rel, IEnumerable<string> dirs) =>
        dirs.Where(d => RuleContext.Under(rel, d)).OrderByDescending(d => d.Length).Cast<string?>().FirstOrDefault();

    // ───── Go ─────

    private static List<GoModule> ReadGoModules(ProjectRoots roots, List<string> manifests, List<string> sources)
    {
        var mods = manifests.Where(m => System.IO.Path.GetFileName(m) == "go.mod").ToList();
        if (mods.Count == 0) return [];
        var modDirs = mods.Select(DirOf).ToList();
        var goFiles = sources.Where(s => s.EndsWith(".go", StringComparison.OrdinalIgnoreCase) && !s.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)).ToList();
        var byModule = goFiles.GroupBy(f => Nearest(f, modDirs)).Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<GoModule>();
        foreach (var mod in mods)
        {
            var dir = DirOf(mod);
            var (path, requires) = ParseGoMod(mod, ReadLines(roots, mod));
            var files = byModule.TryGetValue(dir, out var fs) ? fs : [];
            var packageDirs = files.Select(DirOf).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
            var mains = new List<GoMain>();
            foreach (var g in files.Where(f => !IsGeneratedSource(f)).GroupBy(DirOf).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                (string File, int Line)? entry = null;
                var mainFiles = new List<(string File, string[] Lines)>();
                foreach (var f in g.OrderBy(f => f, StringComparer.Ordinal))
                {
                    var lines = ReadLines(roots, f);
                    if (IsGeneratedSource(f, lines)) continue;   // 文件名不带 .pb.go 的生成代码：看文件头
                    var pkg = lines.Select(l => GoPackage.Match(l)).FirstOrDefault(m => m.Success);
                    if (pkg is null || pkg.Groups[1].Value != "main") continue;
                    mainFiles.Add((f, lines));
                    var idx = Array.FindIndex(lines, l => GoFuncMain.IsMatch(l));
                    if (idx >= 0 && entry is null) entry = (f, idx + 1);
                }
                if (entry is not { } e) continue;
                var ports = new List<int>();
                var evidence = new List<Evidence>();
                var dockerfile = ReadDockerfile(roots.Main, g.Key, ports, evidence) ?? (g.Key != dir ? ReadDockerfile(roots.Main, dir, ports, evidence) : null);
                foreach (var (f, lines) in mainFiles) GoPorts(f, lines, ports, evidence);
                ports.Sort();
                mains.Add(new GoMain(g.Key, e.File, e.Line, ports, evidence, dockerfile));
            }
            result.Add(new GoModule(dir, mod, path, requires, mains, packageDirs));
        }
        return result.OrderBy(m => m.Dir, StringComparer.Ordinal).ToList();
    }

    /// <summary>module 行 + require（块与单行两种写法）；// indirect 的跳过。</summary>
    private static (string Path, List<DependencyRef> Requires) ParseGoMod(string file, string[] lines)
    {
        var path = "";
        var requires = new List<DependencyRef>();
        var inBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("module ", StringComparison.Ordinal)) { path = line["module ".Length..].Trim().Trim('"'); continue; }
            string? spec = null;
            if (inBlock)
            {
                if (line.StartsWith(')')) { inBlock = false; continue; }
                spec = line;
            }
            else if (line.StartsWith("require", StringComparison.Ordinal))
            {
                var rest = line["require".Length..].Trim();
                if (rest.StartsWith('(')) { inBlock = true; continue; }
                spec = rest;
            }
            if (string.IsNullOrEmpty(spec) || spec.StartsWith("//", StringComparison.Ordinal) || spec.Contains("// indirect", StringComparison.Ordinal)) continue;
            var name = spec.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            requires.Add(new DependencyRef(name, file, i + 1));
        }
        return (path, requires);
    }

    private static void GoPorts(string file, string[] lines, List<int> ports, List<Evidence> evidence)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal)) continue;
            var hits = new List<Match>();
            if (GoPortVar.Match(line) is { Success: true } v) hits.Add(v);
            if (GoFlagPort.Match(line) is { Success: true } fl) hits.Add(fl);
            if (GoListenCall.IsMatch(line)) hits.AddRange(GoAddrLiteral.Matches(line));
            foreach (var m in hits)
                if (int.TryParse(m.Groups[1].Value, out var port) && !ports.Contains(port)) { ports.Add(port); evidence.Add(new Evidence(file, i + 1, t.Trim())); }
        }
    }

    /// <summary>模块路径最后一段（去掉 /v2 这种主版本后缀）：github.com/acme/pricing → pricing。</summary>
    public static string GoModuleName(string modulePath, string fallback)
    {
        var segs = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return fallback;
        var last = segs[^1];
        if (segs.Length > 1 && Regex.IsMatch(last, @"^v\d+$")) last = segs[^2];
        return last;
    }

    // ───── Python ─────

    private static List<PythonProject> ReadPythonProjects(ProjectRoots roots, List<string> manifests, List<string> sources)
    {
        var groups = manifests.Where(m => System.IO.Path.GetFileName(m) != "go.mod").GroupBy(DirOf).ToList();
        if (groups.Count == 0) return [];
        var dirs = groups.Select(g => g.Key).ToList();
        var pyFiles = sources.Where(s => s.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && !IsGeneratedSource(s)).ToList();
        var byProject = pyFiles.GroupBy(f => Nearest(f, dirs)).Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.Ordinal);
        var result = new List<PythonProject>();
        foreach (var g in groups)
        {
            var dir = g.Key;
            var files = g.OrderBy(f => f, StringComparer.Ordinal).ToList();
            var names = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
            var deps = new List<DependencyRef>();
            void AddAll(IEnumerable<DependencyRef> found)
            {
                foreach (var d in found) if (!deps.Any(x => x.Name == d.Name)) deps.Add(d);
            }
            foreach (var f in files.Where(f => f.EndsWith("pyproject.toml", StringComparison.Ordinal))) AddAll(ParsePyProject(f, ReadLines(roots, f)));
            foreach (var f in files.Where(f => f.EndsWith("setup.py", StringComparison.Ordinal))) AddAll(ParseSetupPy(f, ReadLines(roots, f)));
            foreach (var f in files.Where(f => f.EndsWith("Pipfile", StringComparison.Ordinal))) AddAll(ParseTomlTable(f, ReadLines(roots, f), "packages"));
            // pip-tools：有 requirements*.in 就只读 .in（直接依赖），编译出来的 .txt 含传递依赖；dev / test / lint / doc 的不读
            var reqs = files.Where(f => System.IO.Path.GetFileName(f).StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
                                        && !Regex.IsMatch(System.IO.Path.GetFileName(f), "dev|test|lint|doc", RegexOptions.IgnoreCase)).ToList();
            if (reqs.Any(r => r.EndsWith(".in", StringComparison.OrdinalIgnoreCase))) reqs = reqs.Where(r => r.EndsWith(".in", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var f in reqs) AddAll(ParseRequirements(f, ReadLines(roots, f)));

            var own = byProject.TryGetValue(dir, out var fs) ? fs : [];
            var nonTest = own.Where(f => !RuleContext.IsTestFile(f)).ToList();
            var isTest = PythonTestDirs.Contains(LastSegmentOf(dir)) || (own.Count > 0 && nonTest.Count == 0);
            var evidence = new List<Evidence>();
            var ports = new List<int>();
            string? framework = null;
            if (names.Contains("manage.py")) { evidence.Add(new Evidence(ProjectInventory.Combine(dir, "manage.py"), null, "Django manage.py")); framework = "django"; }
            // 命令行工具：pyproject 的 [project.scripts] / [tool.poetry.scripts]、setup.py 的 console_scripts——pip 装上就是一条命令
            foreach (var f in files.Where(f => f.EndsWith("pyproject.toml", StringComparison.Ordinal) || f.EndsWith("setup.py", StringComparison.Ordinal)))
            {
                var lines = ReadLines(roots, f);
                var inScripts = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith('[')) { inScripts = t is "[project.scripts]" or "[tool.poetry.scripts]"; continue; }
                    if ((inScripts && Regex.IsMatch(t, @"^[\w.\-""']+\s*=\s*[""'][\w.]+:\w")) || t.Contains("console_scripts", StringComparison.Ordinal))
                    {
                        evidence.Add(new Evidence(f, i + 1, "console_scripts"));
                        break;
                    }
                }
            }
            var dockerfile = ReadDockerfile(roots.Main, dir, ports, evidence);
            var rank = new Dictionary<string, int> { ["django"] = 0, ["fastapi"] = 1, ["flask"] = 2, ["grpc"] = 3 };
            foreach (var f in nonTest.OrderBy(f => f, StringComparer.Ordinal))
            {
                var lines = ReadLines(roots, f);
                // scripts/ tools/ 之类目录里的 __main__ 是发布 / 运维脚本，不说明项目是个服务
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (InToolDir(f, dir)) seen.Add("__main__");
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith('#')) continue;
                    foreach (var (regex, note, fw) in new (Regex, string, string?)[] { (PyFastApi, "FastAPI", "fastapi"), (PyFlask, "Flask", "flask"), (PyUvicorn, "uvicorn.run", null), (PyGrpcServer, "grpc.server", "grpc"), (PyMain, "__main__", null) })
                    {
                        if (!regex.IsMatch(line) || !seen.Add(note)) continue;
                        evidence.Add(new Evidence(f, i + 1, note));
                        if (fw is not null && (framework is null || rank[fw] < rank[framework])) framework = fw;
                    }
                    foreach (var r in new[] { PyEnvPort, PyRunPort, PyGrpcPort })
                        if (r.Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var port) && !ports.Contains(port)) { ports.Add(port); evidence.Add(new Evidence(f, i + 1, line.Trim())); }
                }
            }
            ports.Sort();
            var name = dir.Length == 0 ? RootName(roots) : LastSegmentOf(dir);
            var packageDirs = nonTest.Select(DirOf).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
            var deployable = !isTest && (evidence.Count > 0);
            result.Add(new PythonProject(dir, files, name, deps, deployable ? evidence : [], ports, dockerfile, deployable ? framework : null, isTest, packageDirs));
        }
        return result.OrderBy(p => p.Dir, StringComparer.Ordinal).ToList();
    }

    private static readonly HashSet<string> ToolDirs = new(StringComparer.OrdinalIgnoreCase) { "scripts", "script", "tools", "bin", "docs", "examples", "benchmarks", "hack" };

    /// <summary>文件相对项目目录的路径里有 scripts / tools / docs … 段。</summary>
    private static bool InToolDir(string file, string projectDir)
    {
        var rel = projectDir.Length == 0 ? file : file[(projectDir.Length + 1)..];
        return rel.Split('/').SkipLast(1).Any(ToolDirs.Contains);
    }

    private static string RootName(ProjectRoots roots) => System.IO.Path.GetFileName(roots.Main.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : "app";

    /// <summary>PEP 508 依赖串 → 规范化包名：小写，_ 与 . 换成 -，去掉 extras 与版本（"psycopg[binary]>=3" → psycopg）。</summary>
    public static string? NormalizePythonName(string spec)
    {
        var m = PyName.Match(spec);
        if (!m.Success) return null;
        return Regex.Replace(m.Groups[1].Value.ToLowerInvariant(), "[._]+", "-").TrimEnd('-');
    }

    private static IEnumerable<DependencyRef> ParseRequirements(string file, string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-')) continue;
            if (line.Contains("://") && !line.Contains(" @ ")) continue; // 直接写 URL 的不知道包名
            if (NormalizePythonName(line) is { } n) yield return new DependencyRef(n, file, i + 1);
        }
    }

    /// <summary>[project] 的 dependencies = [ … ]（可跨行）；没有就读 [tool.poetry.dependencies]。</summary>
    private static IEnumerable<DependencyRef> ParsePyProject(string file, string[] lines)
    {
        var table = "";
        var found = new List<DependencyRef>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && !t.StartsWith("[[", StringComparison.Ordinal) && t.EndsWith(']')) { table = t.Trim('[', ']').Trim(); continue; }
            if (table != "project" || !Regex.IsMatch(t, @"^dependencies\s*=\s*\[")) continue;
            var start = t.IndexOf('[');
            for (var k = i; k < lines.Length; k++)
            {
                var text = k == i ? t[(start + 1)..] : lines[k];
                var close = CloseBracket(text);
                foreach (Match q in Quoted.Matches(close >= 0 ? text[..close] : text))
                    if (NormalizePythonName(q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value) is { } n) found.Add(new DependencyRef(n, file, k + 1));
                if (close >= 0) { i = k; break; }
            }
        }
        return found.Count > 0 ? found : ParseTomlTable(file, lines, "tool.poetry.dependencies");
    }

    /// <summary>行里不在引号内的第一个 ]；没有返回 -1。</summary>
    private static int CloseBracket(string text)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#') return -1;
            else if (c == ']') return i;
        }
        return -1;
    }

    /// <summary>TOML 表里的 name = … 键（Pipfile 的 [packages]、poetry 的依赖表）；python 本身不算。</summary>
    private static IEnumerable<DependencyRef> ParseTomlTable(string file, string[] lines, string tableName)
    {
        var inTable = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[')) { inTable = t.Trim('[', ']').Trim() == tableName; continue; }
            if (!inTable || t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var key = t[..eq].Trim().Trim('"', '\'');
            if (key.Equals("python", StringComparison.OrdinalIgnoreCase)) continue;
            if (NormalizePythonName(key) is { } n) yield return new DependencyRef(n, file, i + 1);
        }
    }

    /// <summary>setup.py 的 install_requires=[ … ]（可跨行）。</summary>
    private static IEnumerable<DependencyRef> ParseSetupPy(string file, string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"install_requires\s*=\s*\[");
            if (!m.Success) continue;
            for (var k = i; k < lines.Length; k++)
            {
                var text = k == i ? lines[i][(m.Index + m.Length)..] : lines[k];
                var close = CloseBracket(text);
                foreach (Match q in Quoted.Matches(close >= 0 ? text[..close] : text))
                    if (NormalizePythonName(q.Groups[1].Success ? q.Groups[1].Value : q.Groups[2].Value) is { } n) yield return new DependencyRef(n, file, k + 1);
                if (close >= 0) yield break;
            }
        }
    }

    // ───── Node 后端 ─────

    private static bool HasStartScript(JsonElement package) =>
        package.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
        && scripts.EnumerateObject().Any(s => s.Name is "start" or "start:prod" && s.Value.ValueKind == JsonValueKind.String && s.Value.GetString() is { Length: > 0 });

    private static bool IsVsCodeExtension(JsonElement package) =>
        package.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object && engines.TryGetProperty("vscode", out _)
        && (package.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String || package.TryGetProperty("browser", out var browser) && browser.ValueKind == JsonValueKind.String);

    // app.listen(3000) / server.listen(8080, …)；const PORT = process.env.PORT || 3000（?? 与带引号的也认）
    private static readonly Regex NodeListen = new(@"\.listen\(\s*(\d{2,5})\b", RegexOptions.Compiled);
    private static readonly Regex NodePortDefault = new(@"\bPORT\w*\s*(?:\|\||\?\?)\s*['""`]?(\d{2,5})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] NodeEntryCandidates =
        ["src/main.ts", "src/main.js", "src/index.ts", "src/index.js", "src/server.ts", "src/server.js", "src/app.ts", "src/app.js",
         "main.ts", "main.js", "index.ts", "index.js", "server.ts", "server.js", "app.ts", "app.js", "bin/www"];

    /// <summary>入口文件里的监听端口：package.json 的 main（编译产物 dist/x.js 也试 src/x.ts）加常见入口文件名，各看一遍 .listen(数字) 与 PORT || 数字。</summary>
    private static void NodeEntryPorts(ProjectRoots roots, string dir, JsonElement package, List<int> ports, List<Evidence> evidence)
    {
        var candidates = new List<string>();
        if (package.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String && main.GetString() is { Length: > 0 } m)
        {
            var rel = m.TrimStart('.', '/');
            candidates.Add(rel);
            var stem = System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(rel), null);
            candidates.Add("src/" + stem + ".ts");
        }
        candidates.AddRange(NodeEntryCandidates);
        foreach (var c in candidates.Distinct(StringComparer.Ordinal))
        {
            var file = Combine(dir, c);
            var lines = ReadLines(roots, file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (var r in new[] { NodeListen, NodePortDefault })
                    if (r.Match(lines[i]) is { Success: true } hit && int.TryParse(hit.Groups[1].Value, out var port) && !ports.Contains(port))
                    {
                        ports.Add(port);
                        evidence.Add(new Evidence(file, i + 1, lines[i].Trim()));
                    }
            }
        }
    }

    private static NodeService? ReadNodeService(ProjectRoots roots, string relFile)
    {
        var dir = DirOf(relFile);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(roots.Main, relFile.Replace('/', System.IO.Path.DirectorySeparatorChar))));
            var name = dir.Length == 0 ? RootName(roots) : LastSegmentOf(dir);
            // VS Code 扩展：扩展宿主里跑的进程（webview 也在同一目录下，合成一个容器）
            if (IsVsCodeExtension(doc.RootElement))
            {
                var display = doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String && dn.GetString() is { Length: > 0 } d ? d : name;
                return new NodeService(dir, relFile, display, "vscode", [], null, IsVsCodeExtension: true);
            }
            var names = doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object ? deps.EnumerateObject().Select(p => p.Name).ToList() : [];
            var server = NodeServers.FirstOrDefault(s => names.Contains(s, StringComparer.OrdinalIgnoreCase));
            var ports = new List<int>();
            var dockerfile = server is null ? null : ReadDockerfile(roots.Main, dir, ports, []);
            // bin 字段 = 命令行工具（npm 装上就是一条命令）；同时是带 Dockerfile 的服务时按服务算
            var hasBin = doc.RootElement.TryGetProperty("bin", out var bin) && (bin.ValueKind == JsonValueKind.String ? bin.GetString() is { Length: > 0 } : bin.ValueKind == JsonValueKind.Object && bin.EnumerateObject().Any());
            if (dockerfile is null && hasBin)
                return new NodeService(dir, relFile, name, "cli", [], null, IsCli: true);
            if (server is null) return null;
            // 没有 Dockerfile 的多是工具脚本、本地开发服务器，不当容器——除非 scripts 里有 start（npm start 起服务：NestJS / Express 示例常不带 Dockerfile）
            if (dockerfile is null && !HasStartScript(doc.RootElement)) return null;
            var portEvidence = new List<Evidence>();
            NodeEntryPorts(roots, dir, doc.RootElement, ports, portEvidence);
            ports.Sort();
            return new NodeService(dir, relFile, name, server, ports, dockerfile, PortEvidence: portEvidence);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
