using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Server;

namespace CodeAtlas.Cli.Commands;

/// <summary>
/// query --relations：与 GET /api/symbols/{id}/relations 同一份数据。先按组件（依赖序，被依赖的在前）分组列单元，再列边
/// （"OrderRepository → Order  call 3 · reference 2"），每条边下缩进列成员层面的样例。
/// </summary>
internal static class RelationsPrinter
{
    public static void Print(IndexDatabase db, IEnumerable<long> ids, RelationsOptions options, TextWriter stdout)
    {
        var seenFocus = new HashSet<long>();
        foreach (var id in ids)
        {
            if (RelationsQuery.Build(db, id, options) is not { } rel || !seenFocus.Add(rel.Focus.Id)) continue; // 同一个类的几个重载只打一次
            Print(rel, options, stdout);
            stdout.WriteLine();
        }
    }

    internal static void Print(RelationsDto rel, RelationsOptions options, TextWriter stdout)
    {
        var label = rel.Nodes.ToDictionary(n => n.Unit.Id, n => Short(n.Unit));
        stdout.WriteLine($"中心：{rel.Focus.QualifiedName}  [{rel.Focus.Kind}]  {rel.Focus.Path}:{rel.Focus.StartLine}");
        stdout.WriteLine($"depth {options.Depth}：{rel.Nodes.Count} 个单元、{rel.Edges.Count} 条边{(rel.Truncated ? $"（超过 {RelationsBuilder.MaxNodes} 个单元，按权重截断）" : "")}");

        stdout.WriteLine("组件（依赖序，被依赖的在前）：");
        foreach (var c in rel.Components.OrderBy(c => c.Order))
        {
            var detail = c.Key == RelationsBuilder.ExternalComponentKey ? "" : $"  {c.Key}" + (c.ContainerKey is null ? "" : $"  [容器 {c.ContainerKey}]");
            stdout.WriteLine($"  {c.Order + 1}. {c.Label}{detail}");
            foreach (var n in rel.Nodes.Where(n => n.ComponentKey == c.Key))
                stdout.WriteLine($"       {(n.Distance == 0 ? "*" : " ")} {label[n.Unit.Id]}  [{n.Unit.Kind}]  d{n.Distance}");
        }

        stdout.WriteLine("边：");
        foreach (var e in rel.Edges)
        {
            var kinds = string.Join(" · ", e.Kinds.Select(k => $"{k.Key} {k.Value}"));
            stdout.WriteLine($"  {label[e.From]} → {label[e.To]}  {kinds}  （权重 {e.Weight:0.#}）");
            foreach (var s in e.Samples)
                stdout.WriteLine($"      {s.FromLabel} → {s.ToLabel}  {s.Kind}  {s.File}:{s.Line}{(s.Confidence < 1 ? $"  {s.Confidence:0.##}" : "")}");
        }
    }

    private static string Short(SymbolDto s) =>
        new MapSymbol(s.Id, DbNames.ParseSymbolKind(s.Kind), s.QualifiedName, s.Path, null).Name;
}
