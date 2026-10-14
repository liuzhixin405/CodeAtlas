using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// .codeatlas/architecture.yaml：人声明的容器 / 外部系统 / 人 / 关系，用来补静态发现不到的、改名、隐藏。
/// 声明与发现都保留；冲突记入 conflicts。
/// </summary>
public sealed class ArchitectureDeclarations
{
    public const string RelativePath = ".codeatlas/architecture.yaml";

    public sealed class NodeDecl { public string? Key { get; set; } public string? Name { get; set; } public string? Kind { get; set; } public bool Hide { get; set; } }
    public sealed class RelDecl { public string? From { get; set; } public string? To { get; set; } public string? Protocol { get; set; } public string? Note { get; set; } }
    /// <summary>分层：按从上到下的顺序列出；下层不得依赖上层。namespace 不写时用 name。</summary>
    public sealed class LayerDecl { public string? Name { get; set; } public string? Namespace { get; set; } }
    /// <summary>单条规则："A.Application -> A.Infrastructure" 的写法，forbid / require 二选一。</summary>
    public sealed class RuleDecl { public string? Forbid { get; set; } public string? Require { get; set; } }
    public sealed class Doc
    {
        public List<NodeDecl> Containers { get; set; } = [];
        public List<NodeDecl> Externals { get; set; } = [];
        public List<NodeDecl> People { get; set; } = [];
        public List<RelDecl> Relationships { get; set; } = [];
        public List<LayerDecl> Layers { get; set; } = [];
        public List<RuleDecl> Rules { get; set; } = [];
    }

    /// <summary>
    /// yaml 里声明的依赖规则：rules 的每条 forbid / require 一条；layers 按顺序两两生成"下层不得依赖上层"的 forbid。
    /// 命名空间按前缀匹配；Line 为 0（YamlDotNet 不给行号），TestName 为 null。
    /// </summary>
    public List<ArchRule> DependencyRules()
    {
        var rules = new List<ArchRule>();
        foreach (var r in Document.Rules)
        {
            var (kind, text) = r.Forbid is not null ? (ArchRuleKind.ForbidDependency, r.Forbid) : (ArchRuleKind.RequireDependency, r.Require);
            if (text is null) continue;
            var arrow = text.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var from = text[..arrow].Trim();
            var to = text[(arrow + 2)..].Trim();
            if (from.Length == 0 || to.Length == 0) continue;
            rules.Add(new ArchRule(kind, from, ArchRuleMatch.Prefix, to, ArchRuleMatch.Prefix, RelativePath, 0, null, "yaml"));
        }
        var layers = Document.Layers.Select(l => l.Namespace ?? l.Name).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToList();
        for (var lower = 1; lower < layers.Count; lower++)
            for (var upper = 0; upper < lower; upper++)
                rules.Add(new ArchRule(ArchRuleKind.ForbidDependency, layers[lower], ArchRuleMatch.Prefix, layers[upper], ArchRuleMatch.Prefix, RelativePath, 0, null, "yaml（layers）"));
        return rules;
    }

    public Doc Document { get; }

    public ArchitectureDeclarations(Doc document) => Document = document;

    public static ArchitectureDeclarations? Load(string root)
    {
        var path = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        return Parse(File.ReadAllText(path));
    }

    public static ArchitectureDeclarations Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var doc = deserializer.Deserialize<Doc>(yaml) ?? new Doc();
        return new ArchitectureDeclarations(doc);
    }

    /// <summary>把声明合并进发现结果。返回新模型；conflicts 只增不减。</summary>
    public ArchModel Apply(ArchModel discovered)
    {
        var evidence = new Evidence(RelativePath);
        var nodes = discovered.Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = discovered.Conflicts.ToList();

        void Merge(IEnumerable<NodeDecl> decls, ArchNodeKind kind, string defaultSubKind)
        {
            foreach (var d in decls)
            {
                // 没写 key 就按名字生成，格式与发现的一致：容器用名字，外部系统 external:{kind}:{slug}，人 person:{slug}
                var key = d.Key ?? (d.Name is null ? null : kind switch
                {
                    ArchNodeKind.Container => d.Name,
                    ArchNodeKind.External => $"external:{d.Kind ?? defaultSubKind}:{Rules.RuleContext.Slug(d.Name)}",
                    _ => $"person:{Rules.RuleContext.Slug(d.Name)}",
                });
                if (key is null) continue;
                if (d.Hide) { hidden.Add(key); continue; }
                if (nodes.TryGetValue(key, out var existing))
                {
                    var renamed = d.Name is not null && d.Name != existing.Name;
                    var rekinded = d.Kind is not null && d.Kind != existing.SubKind;
                    if (renamed || rekinded) conflicts.Add(new ArchConflict("declared-overrides", $"{key}：声明把 {(renamed ? $"名字 {existing.Name} 改为 {d.Name}" : "")}{(renamed && rekinded ? "，" : "")}{(rekinded ? $"种类 {existing.SubKind} 改为 {d.Kind}" : "")}"));
                    nodes[key] = existing with { Name = d.Name ?? existing.Name, SubKind = d.Kind ?? existing.SubKind, Evidence = [.. existing.Evidence, evidence] };
                }
                else
                {
                    nodes[key] = new ArchNode(key, kind, d.Kind ?? defaultSubKind, d.Name ?? key, null, ArchSource.Declared, [evidence], [], []);
                    if (kind != ArchNodeKind.Person) conflicts.Add(new ArchConflict("declared-not-found", $"{key}（{d.Name ?? key}）只有声明，静态发现不到"));
                }
            }
        }
        Merge(Document.Containers, ArchNodeKind.Container, "declared");
        Merge(Document.Externals, ArchNodeKind.External, "other");
        Merge(Document.People, ArchNodeKind.Person, "person");

        var edges = discovered.Edges.ToList();
        foreach (var h in hidden)
        {
            var used = edges.Count(e => e.FromKey == h || e.ToKey == h);
            if (used > 0) conflicts.Add(new ArchConflict("hidden-but-used", $"{h} 被声明隐藏，但有 {used} 条发现的边连着它"));
            nodes.Remove(h);
            edges.RemoveAll(e => e.FromKey == h || e.ToKey == h);
        }
        foreach (var r in Document.Relationships)
        {
            if (r.From is null || r.To is null) continue;
            if (!nodes.ContainsKey(r.From) || !nodes.ContainsKey(r.To)) { conflicts.Add(new ArchConflict("declared-not-found", $"关系 {r.From} → {r.To} 的端点不存在")); continue; }
            edges.Add(new ArchEdge(r.From, r.To, r.Protocol ?? "unknown", 1.0, ArchSource.Declared, [evidence with { Note = r.Note }]));
        }
        return new ArchModel(nodes.Values.OrderBy(n => n.Kind).ThenBy(n => n.Key, StringComparer.Ordinal).ToList(),
            edges.OrderBy(e => e.FromKey, StringComparer.Ordinal).ThenBy(e => e.ToKey, StringComparer.Ordinal).ThenBy(e => e.Protocol, StringComparer.Ordinal).ToList(),
            discovered.Outbound, conflicts);
    }
}
