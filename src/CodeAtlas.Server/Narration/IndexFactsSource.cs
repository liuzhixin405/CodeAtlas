using System.Diagnostics;
using System.Globalization;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Server.Narration;

/// <summary>(kind, key) → 事实子图；对象不存在返回 null。</summary>
public interface IFactsSource
{
    Facts? Resolve(string kind, string key);

    /// <summary>仓库当前 HEAD 的短哈希（生成叙述时记下来给人看）；不是 git 仓库或拿不到为 null。</summary>
    string? GitHead() => null;
}

/// <summary>
/// 从 IndexService 的现成视图（容器图 / DSM / 入口 / 流程）拼事实子图。服务端与 CLI 共用。
/// 同时填缓存要的三样：稳定键、稳定 id 映射、对象覆盖文件的内容哈希。
/// </summary>
/// <param name="includeSource">false 时入口流程不附入口方法源码（发往联网服务时可关）。</param>
public sealed class IndexFactsSource(IndexService service, bool includeSource = true) : IFactsSource
{
    public Facts? Resolve(string kind, string key) => kind switch
    {
        NarrativePresets.Container => Container(key),
        NarrativePresets.Component => Component(key),
        NarrativePresets.Flow => Flow(key),
        _ => null,
    };

    private Facts? Container(string key)
    {
        var arch = service.Architecture();
        var node = arch.Nodes.FirstOrDefault(n => n.Key == key);
        if (node is null) return null;
        var entries = node.Kind == "container" ? service.Entries(key, null, null) : [];
        var facts = FactsBuilder.Container(arch, node, entries);
        var hashes = service.FileHashes();
        var files = node.Members.Count == 0 ? [] : hashes.Keys.Where(p => node.Members.Any(m => RuleContext.Under(p, m)));
        return WithStable(facts, files, hashes);
    }

    /// <summary>key = "&lt;容器键&gt;|&lt;组件键&gt;"。组件键不带层级：先试容器层（root=""），再试组件键逐段去尾当 root，最后试每个成员目录。</summary>
    private Facts? Component(string key)
    {
        var sep = key.IndexOf('|');
        if (sep < 0) return null;
        var containerKey = key[..sep];
        var compKey = key[(sep + 1)..];
        var container = service.Architecture().Nodes.FirstOrDefault(n => n.Key == containerKey && n.Kind == "container");
        if (container is null) return null;
        foreach (var root in CandidateRoots(compKey, container.Members))
        {
            var dsm = service.Dsm(containerKey, root);
            if (dsm is null) return null;
            var comp = dsm.Components.FirstOrDefault(c => c.Key == compKey);
            if (comp is null) continue;
            var facts = FactsBuilder.Component(dsm, comp, key, container.Name);
            return WithStable(facts, service.ComponentFiles(container.Members, compKey), service.FileHashes());
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots(string compKey, IReadOnlyList<string> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (seen.Add("")) yield return "";
        var k = compKey;
        while (k.Length > 0)
        {
            var cut = k.LastIndexOfAny(['.', '/']);
            if (cut <= 0) break;
            k = k[..cut];
            if (seen.Add(k)) yield return k;
        }
        foreach (var m in members) if (seen.Add(m)) yield return m;
    }

    private Facts? Flow(string key)
    {
        if (!long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var symbolId)) return null;
        var flow = service.Flow(symbolId, null);
        if (flow is null) return null;
        var containers = service.Architecture().Nodes;
        var containerName = containers.FirstOrDefault(c => c.Key == flow.ContainerKey)?.Name ?? flow.ContainerKey;
        var entry = service.Entries(flow.ContainerKey, null, null).FirstOrDefault(e => e.SymbolId == symbolId);
        var entrySymbol = service.GetSymbol(symbolId);
        IReadOnlyList<string>? source = null;
        var start = 0;
        if (includeSource && entrySymbol is { } s && s.EndLine >= s.StartLine && s.StartLine > 0)
        {
            var lines = ArchitecturePostProcess.MakeReadLines(service.RepoRoot)(s.Path);
            if (lines.Length >= s.StartLine)
            {
                start = s.StartLine;
                source = lines.Skip(s.StartLine - 1).Take(Math.Min(s.EndLine, lines.Length) - s.StartLine + 1).ToList();
            }
        }
        var facts = FactsBuilder.Flow(flow, entry, containerName, source, start);
        // 流程覆盖的文件 = 树上每个符号所在的文件（外部符号没有）
        var files = new HashSet<string>(StringComparer.Ordinal);
        if (entrySymbol is not null) files.Add(entrySymbol.Path);
        void Walk(FlowNodeDto n)
        {
            if (n.SymbolId is { } id && service.GetSymbol(id) is { } sym && sym.Kind != "external") files.Add(sym.Path);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(flow.Root);
        // 稳定键：容器 + 入口方法全名（同一个库方法可能是两个容器各自的入口）；重建索引后符号 id 变了也认得
        facts = facts with { StableKey = entrySymbol is null ? key : $"{flow.ContainerKey}|{entrySymbol.QualifiedName}" };
        return WithStable(facts, files, service.FileHashes());
    }

    /// <summary>补稳定 id 映射与覆盖文件的内容哈希。</summary>
    private Facts WithStable(Facts facts, IEnumerable<string> files, IReadOnlyDictionary<string, string> hashes)
    {
        var stable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in facts.Ids)
        {
            var colon = id.IndexOf(':');
            if (colon < 0) continue;
            var prefix = id[..colon];
            if (prefix is not ("sym" or "entry") || !long.TryParse(id[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid)) continue;
            if (service.GetSymbol(sid) is { } s) stable[id] = $"{prefix}@{s.QualifiedName}";
        }
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files) if (hashes.TryGetValue(f, out var h)) sources[f] = h;
        return facts with { StableIds = stable, Sources = sources };
    }

    private string? _head;
    private DateTime _headAt;

    public string? GitHead()
    {
        // 生成一条叙述才问一次；30 秒内复用，免得批量预生成时每条都起 git 进程
        if (DateTime.UtcNow - _headAt < TimeSpan.FromSeconds(30)) return _head;
        _headAt = DateTime.UtcNow;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", ["-C", service.RepoRoot, "rev-parse", "--short", "HEAD"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return _head = null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { /* 已退出 */ } return _head = null; }
            return _head = p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception) { return _head = null; } // 没装 git
    }
}
