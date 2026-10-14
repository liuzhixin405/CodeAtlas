namespace CodeAtlas.Core.Architecture;

/// <summary>容器 = 非测试的可执行 .NET 项目（Web / Worker / Exe）与前端应用。成员 = 传递项目引用。</summary>
public static class ContainerDiscovery
{
    public static IReadOnlyList<ArchNode> Discover(ProjectInventory inv)
    {
        var byDir = inv.Projects.ToDictionary(p => p.Dir, StringComparer.Ordinal);
        var nodes = new List<ArchNode>();
        foreach (var p in inv.Projects)
        {
            if (p.IsTest) continue;
            var subKind = SubKindOf(p);
            if (subKind is null) continue;
            var members = Closure(p.Dir, byDir);
            var evidence = new List<Evidence> { new(p.File, null, p.Sdk is not null ? $"Sdk={p.Sdk}" : $"OutputType={p.OutputType}") };
            evidence.AddRange(p.DeployEvidence);
            nodes.Add(new ArchNode(p.Dir.Length == 0 ? p.Name : p.Dir, ArchNodeKind.Container, subKind, p.Name, p.Dir, ArchSource.Discovered, evidence, members, p.Ports));
        }
        foreach (var f in inv.Frontends)
        {
            var evidence = new List<Evidence> { new(f.File, null, $"framework={f.Framework}") };
            if (f.Dockerfile is not null) evidence.Add(new Evidence(f.Dockerfile));
            nodes.Add(new ArchNode(f.Dir.Length == 0 ? f.Name : f.Dir, ArchNodeKind.Container, "frontend", f.Name, f.Dir, ArchSource.Discovered, evidence, [f.Dir], f.Ports));
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
        return null;
    }

    private static List<string> Closure(string start, Dictionary<string, CsProject> byDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            if (!byDir.TryGetValue(dir, out var p)) continue;
            foreach (var r in p.ProjectRefs) if (seen.Add(r)) queue.Enqueue(r);
        }
        return seen.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }
}
