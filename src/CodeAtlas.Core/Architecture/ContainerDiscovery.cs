using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 容器 = 可部署单元：非测试的可执行 .NET 项目（Web / Worker / Exe，成员 = 传递项目引用）、前端应用、
/// Go 的 main 包目录（成员 = 所在模块）、可部署的 Python 项目、Node 后端服务与命令行工具（package.json 的 bin）、可部署的 Java 模块（成员 = 自己 + 依赖的本仓库模块）。
/// </summary>
public static class ContainerDiscovery
{
    public static IReadOnlyList<ArchNode> Discover(ProjectInventory inv)
    {
        // 同一目录可以放多个 csproj：项目引用按目录合并（测试项目的引用不算），一个目录只出一个容器——
        // 优先可部署的，再按名字取第一个
        var byDir = inv.Projects.GroupBy(p => p.Dir, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => (IReadOnlyList<string>)g.Where(p => !p.IsTest).SelectMany(p => p.ProjectRefs).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var nodes = new List<ArchNode>();
        var hosts = inv.Projects.Where(p => !p.IsTest && SubKindOf(p) is not null).GroupBy(p => p.Dir, StringComparer.Ordinal)
            .Select(g => g.OrderBy(p => p.Name, StringComparer.Ordinal).First());
        foreach (var p in hosts)
        {
            var subKind = SubKindOf(p)!;
            var members = Closure(p.Dir, byDir);
            var evidence = new List<Evidence> { new(p.File, null, ProjectNote(p)) };
            evidence.AddRange(p.DeployEvidence);
            nodes.Add(new ArchNode(p.Dir.Length == 0 ? p.Name : p.Dir, ArchNodeKind.Container, subKind, p.Name, p.Dir, ArchSource.Discovered, evidence, members, p.Ports));
        }
        foreach (var f in inv.Frontends)
        {
            var evidence = new List<Evidence> { new(f.File, null, $"framework={f.Framework}") };
            if (f.Dockerfile is not null) evidence.Add(new Evidence(f.Dockerfile));
            nodes.Add(new ArchNode(f.Dir.Length == 0 ? f.Name : f.Dir, ArchNodeKind.Container, "frontend", f.Name, f.Dir, ArchSource.Discovered, evidence, [f.Dir], f.Ports));
        }
        // 其他语言的项目目录：算 Go / Python 成员时，模块目录之下有别的项目就不能整个目录都算进来
        var projectDirs = inv.Projects.Select(p => p.Dir).Concat(inv.Frontends.Select(f => f.Dir)).Concat(inv.GoModules.Select(m => m.Dir))
            .Concat(inv.PythonProjects.Select(p => p.Dir)).Concat(inv.NodeServices.Select(n => n.Dir)).Concat(inv.JavaModules.Select(m => m.Dir)).ToList();
        foreach (var mod in inv.GoModules)
            foreach (var main in mod.Mains)
            {
                var name = main.Dir == mod.Dir ? ProjectInventory.GoModuleName(mod.ModulePath, LastSegment(main.Dir)) : LastSegment(main.Dir);
                var foreign = mod.Mains.Where(m => m.Dir != main.Dir).Select(m => m.Dir)
                    .Concat(projectDirs.Where(d => d != mod.Dir && RuleContext.Under(d, mod.Dir))).ToList();
                var evidence = new List<Evidence> { new(mod.File, null, $"module {mod.ModulePath}"), new(main.File, main.Line, "func main()") };
                evidence.AddRange(main.Evidence);
                AddUnique(nodes, new ArchNode(Key(main.Dir, name), ArchNodeKind.Container, "go-service", name, main.Dir, ArchSource.Discovered, evidence,
                    Members(mod.Dir, main.Dir, mod.PackageDirs, foreign), main.Ports));
            }
        foreach (var py in inv.PythonProjects.Where(p => !p.IsTest && p.DeployEvidence.Count > 0))
        {
            var foreign = projectDirs.Where(d => d != py.Dir && RuleContext.Under(d, py.Dir)).ToList();
            var evidence = py.Manifests.Select(m => new Evidence(m, null, py.Framework is null ? null : $"framework={py.Framework}")).Concat(py.DeployEvidence).ToList();
            AddUnique(nodes, new ArchNode(Key(py.Dir, py.Name), ArchNodeKind.Container, "python-service", py.Name, py.Dir, ArchSource.Discovered, evidence,
                Members(py.Dir, py.Dir, py.PackageDirs, foreign), py.Ports));
        }
        foreach (var n in inv.NodeServices)
        {
            var evidence = new List<Evidence> { new(n.File, null, n.IsCli ? "bin（命令行）" : $"server={n.Server}") };
            if (n.Dockerfile is not null) evidence.Add(new Evidence(n.Dockerfile));
            AddUnique(nodes, new ArchNode(Key(n.Dir, n.Name), ArchNodeKind.Container, n.IsCli ? "node-cli" : "node-service", n.Name, n.Dir, ArchSource.Discovered, evidence, [n.Dir], n.Ports));
        }
        var javaRefs = inv.JavaModules.ToDictionary(m => m.Dir, m => m.ModuleRefs, StringComparer.Ordinal);
        foreach (var m in inv.JavaModules.Where(m => m.DeployEvidence.Count > 0))
        {
            var evidence = new List<Evidence> { new(m.BuildFile, null, m.BuildTool == "maven" ? $"artifactId={m.ArtifactId}" : "gradle") };
            evidence.AddRange(m.DeployEvidence);
            // 成员 = 自己 + 传递依赖的本仓库模块（同 .NET 的传递项目引用）；模块目录下嵌着别的项目时只取自己的包目录
            var members = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dir in Closure(m.Dir, javaRefs))
            {
                var module = inv.JavaModules.First(x => x.Dir == dir);
                var foreign = projectDirs.Where(d => d != dir && RuleContext.Under(d, dir)).ToList();
                if (foreign.Count == 0) { members.Add(dir); continue; }
                var own = module.PackageDirs.Where(p => !foreign.Any(f => RuleContext.Under(f, p) || RuleContext.Under(p, f))).ToList();
                foreach (var p in own.Count > 0 ? own : [dir]) members.Add(p);
            }
            AddUnique(nodes, new ArchNode(Key(m.Dir, m.Name), ArchNodeKind.Container, "java-service", m.Name, m.Dir, ArchSource.Discovered, evidence, members.ToList(), m.Ports));
        }
        return nodes.OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>可执行判定：Sdk.Web → dotnet-web；Sdk.Worker → dotnet-worker；OutputType Exe / WinExe → dotnet-exe；其余是库。</summary>
    internal static string? SubKindOf(CsProject p)
    {
        if (p.Sdk is not null)
        {
            if (p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase)) return "dotnet-web";
            if (p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)) return "dotnet-worker";
        }
        if (p.OutputType is not null && (p.OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || p.OutputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))) return "dotnet-exe";
        if (p.LegacyWeb) return "dotnet-web";
        return null;
    }

    /// <summary>csproj 证据的说明：SDK 项目写 Sdk=…；旧式项目写 OutputType（或 Web Application 类型）+ .NET Framework 版本。</summary>
    private static string ProjectNote(CsProject p)
    {
        if (p.Sdk is not null) return $"Sdk={p.Sdk}";
        var kind = p.LegacyWeb && SubKindOf(p with { LegacyWeb = false }) is null ? "ProjectTypeGuids=Web Application" : $"OutputType={p.OutputType}";
        return p.TargetFramework is { } tf && tf.StartsWith('v') ? $"{kind}，.NET Framework {tf}" : kind;
    }

    /// <summary>
    /// Go / Python 容器的成员目录。模块（项目）目录之下没有别的 main / 别的项目时就是整个目录；
    /// 有的话（同模块多个 cmd/x，或目录里嵌着别的项目）改成：自己的入口目录 + 本模块里不包含它们的包目录——
    /// 成员按目录前缀匹配，整个模块目录会把别的入口也吞进来。
    /// </summary>
    private static List<string> Members(string projectDir, string entryDir, IReadOnlyList<string> packageDirs, List<string> foreign)
    {
        if (foreign.Count == 0) return [projectDir];
        var set = new SortedSet<string>(StringComparer.Ordinal) { entryDir };
        foreach (var d in packageDirs)
        {
            if (RuleContext.Under(d, entryDir)) continue;
            if (foreign.Any(f => RuleContext.Under(f, d) || RuleContext.Under(d, f))) continue;
            set.Add(d);
        }
        return set.ToList();
    }

    /// <summary>同一目录已经是容器（比如同时有 csproj 与 go.mod）时保留先来的，不出两个同键节点。</summary>
    private static void AddUnique(List<ArchNode> nodes, ArchNode node)
    {
        if (!nodes.Any(n => n.Key == node.Key)) nodes.Add(node);
    }

    private static string Key(string dir, string name) => dir.Length == 0 ? name : dir;
    private static string LastSegment(string dir) => dir.Contains('/') ? dir[(dir.LastIndexOf('/') + 1)..] : dir;

    /// <param name="refsByDir">目录 → 该目录（全部非测试项目合并后）的项目引用。</param>
    internal static List<string> Closure(string start, IReadOnlyDictionary<string, IReadOnlyList<string>> refsByDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            if (!refsByDir.TryGetValue(dir, out var refs)) continue;
            foreach (var r in refs) if (seen.Add(r)) queue.Enqueue(r);
        }
        return seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
