using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Server;

namespace CodeAtlas.Cli.Commands;

internal static class QueryCommand
{
    private static readonly HashSet<EdgeKind> DefaultKinds =
        [EdgeKind.Calls, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References, EdgeKind.Dispatches];

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2)
        {
            stderr.WriteLine("用法：codeatlas query <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds a,b] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --outline [--depth N] [--no-externals] [--tests] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --impact [--depth N] [--tests] [--db <path>]\n" +
                             "       codeatlas query <repo> <name|qualified-name> --relations [--depth 1|2] [--tests] [--db <path>]");
            return 2;
        }
        var repo = args.Positional[0];
        var target = args.Positional[1];
        var depth = int.TryParse(args.Value("--depth"), out var d) ? Math.Clamp(d, 1, 6) : 1;
        var direction = (args.Has("--in"), args.Has("--out")) switch
        {
            (true, false) => Direction.Incoming,
            (false, true) => Direction.Outgoing,
            _ => Direction.Both,
        };
        var kinds = DefaultKinds;
        if (args.Value("--kinds") is { } k)
        {
            kinds = [];
            foreach (var name in k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!DbNames.TryParseEdgeKind(name, out var kind))
                {
                    stderr.WriteLine($"未知的边类型：{name}。可用：{string.Join(", ", DbNames.EdgeKindNames)}");
                    return 2;
                }
                kinds.Add(kind);
            }
        }

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(repo, args, stderr); }
        catch (FileNotFoundException) { return 1; }

        using (db)
        {
            var reader = new IndexReader(db);
            var files = reader.GetAllFiles().ToDictionary(f => f.Id, f => f.Path);
            var matches = reader.FindByQualifiedName(target);
            if (matches.Count == 0) matches = reader.FindByName(target);
            if (matches.Count == 0) { stderr.WriteLine($"未找到符号：{target}"); return 1; }

            if (matches.Count > 3)
            {
                stdout.WriteLine($"有 {matches.Count} 个同名符号，请用限定名：");
                foreach (var m in matches) stdout.WriteLine($"  {m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                return 0;
            }

            if (args.Has("--relations"))
            {
                RelationsPrinter.Print(db, matches.Select(m => m.Id), new RelationsOptions(int.TryParse(args.Value("--depth"), out var rd) ? Math.Clamp(rd, 1, 2) : 1, args.Has("--tests")), stdout);
                return 0;
            }

            if (args.Has("--impact"))
            {
                // 调用方树默认 3 层：一层只看得到直接调用方，看不出"一路影响到哪"；入口汇总另外整趟 BFS，不受这个深度限制
                var treeDepth = int.TryParse(args.Value("--depth"), out var td) ? Math.Clamp(td, 1, ImpactBuilder.MaxDepth) : 3;
                var tests = args.Has("--tests");
                foreach (var m in matches)
                {
                    stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                    PrintImpact(db, m.Id, tests, treeDepth, stdout);
                    stdout.WriteLine();
                }
                return 0;
            }

            if (args.Has("--outline"))
            {
                var options = new OutlineOptions(Externals: !args.Has("--no-externals"), Tests: args.Has("--tests"));
                foreach (var m in matches)
                {
                    stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                    PrintOutline(db, m.Id, options, depth, indent: 1, visited: [m.Id], stdout);
                    stdout.WriteLine();
                }
                return 0;
            }

            foreach (var m in matches)
            {
                stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                Print(reader, m.Id, direction, kinds, depth, indent: 1, visited: [m.Id], stdout);
                stdout.WriteLine();
            }
            return 0;
        }
    }

    /// <summary>
    /// 调用大纲（与 GET /api/symbols/{id}/outline 同一份数据）：缩进树，行尾是行号、所在控制结构、相邻重复次数；
    /// "+" 表示能展开，--depth N 时就地展开 N 层（已在当前路径上的不再展开）。
    /// </summary>
    private static void PrintOutline(IndexDatabase db, long id, OutlineOptions options, int depth, int indent, HashSet<long> visited, TextWriter stdout)
    {
        if (OutlineQuery.Build(db, id, options) is not { } outline) return;
        PrintSteps(outline.Steps, indent);
        if (outline.Truncated) stdout.WriteLine($"{new string(' ', indent * 2)}…（超过 {OutlineBuilder.MaxSteps} 步，已截断）");

        void PrintSteps(IReadOnlyList<OutlineStepDto> steps, int level)
        {
            var pad = new string(' ', level * 2);
            foreach (var s in steps)
            {
                var tail = $"L{s.Line}" + (s.Ctx is null ? "" : $" [{s.Ctx}]") + (s.Repeat > 1 ? $" x{s.Repeat}" : "");
                var conf = s.Confidence < 1 ? $" {s.Confidence:0.##}" : "";
                stdout.WriteLine($"{pad}{s.Order}. {(s.Expandable ? "+" : " ")} {s.Kind,-9} {s.Label}{conf}  @ {tail}");
                PrintSteps(s.Children, level + 1); // impl / dispatch 子项各自按同样规则展开
                if (s.Children.Count == 0 && ExpandTarget(s) is { } t) Expand(t, level + 1);
            }
        }

        long? ExpandTarget(OutlineStepDto s) => depth > 1 && s.Expandable && s.Target is { } t && !visited.Contains(t.Id) ? t.Id : null;

        void Expand(long target, int level)
        {
            visited.Add(target);
            PrintOutline(db, target, options, depth - 1, level, visited, stdout);
            visited.Remove(target);
        }
    }

    /// <summary>
    /// 影响范围（与 GET /api/symbols/{id}/impact 同一份数据）：先是入口汇总（按容器分组，行尾是往上走了几层），
    /// 再是调用方树（缩进一层 = 往上一层；Kind、置信度、各处行号与所在控制结构；本身是入口的标 ⇐；行尾 ↺ 是已在当前路径上的（递归），不再展开；
    /// 行尾 + 表示还能往上展开但超过了 --depth）。
    /// </summary>
    private static void PrintImpact(IndexDatabase db, long id, bool tests, int depth, TextWriter stdout)
    {
        if (ImpactQuery.Build(db, id, new ImpactOptions(Entries: true, Tests: tests)) is not { } impact) return;
        var entries = impact.Entries ?? [];
        stdout.WriteLine($"  入口（{entries.Count}）{(impact.EntriesTruncated ? $"：超过 {ImpactBuilder.MaxDepth} 层或 {ImpactBuilder.MaxNodes} 个节点，可能不全" : "")}");
        if (entries.Count == 0) stdout.WriteLine("    （往上没有走到入口）");
        foreach (var e in entries.OrderBy(e => e.ContainerName, StringComparer.Ordinal).ThenBy(e => e.Depth))
            stdout.WriteLine($"    [{e.ContainerName}] {EntryText(e.Entry)}  ← 深度 {e.Depth}");
        stdout.WriteLine("  调用方");
        PrintCallers(impact, level: 2, remaining: depth, path: [id]);

        void PrintCallers(ImpactDto node, int level, int remaining, HashSet<long> path)
        {
            var pad = new string(' ', level * 2);
            if (node.Callers.Count == 0 && level == 2) stdout.WriteLine($"{pad}（没有调用方）");
            foreach (var c in node.Callers)
            {
                var recursive = path.Contains(c.Caller.Id);
                var expand = !recursive && c.Expandable && remaining > 1;
                var mark = recursive ? "  ↺" : c.Expandable && !expand ? "  +" : "";
                var sites = string.Join(" ", c.Sites.Select(s => $"L{s.Line}" + (s.Ctx is null ? "" : $"[{s.Ctx}]")));
                var conf = c.Confidence < 1 ? $" {c.Confidence:0.##}" : "";
                var entry = c.AsEntry.Count > 0 ? "  ⇐ " + string.Join("；", c.AsEntry.Select(e => EntryText(e.Entry))) : "";
                stdout.WriteLine($"{pad}← {c.Label}  {c.Kind}{conf}  @ {sites}{entry}{mark}");
                if (!expand || ImpactQuery.Build(db, c.Caller.Id, new ImpactOptions(Entries: false, Tests: tests)) is not { } up) continue;
                path.Add(c.Caller.Id);
                PrintCallers(up, level + 1, remaining - 1, path);
                path.Remove(c.Caller.Id);
            }
            if (node.Truncated) stdout.WriteLine($"{pad}…（超过 {ImpactBuilder.MaxCallers} 个调用方，已截断）");
            if (node.TestCallers > 0 && !tests) stdout.WriteLine($"{pad}（另有 {node.TestCallers} 个测试方法直接调用，--tests 显示）");
        }
    }

    /// <summary>HTTP POST /api/orders；其余是"kind 路由或入口名"：handler CreateOrderCommand、hosted OrderWorker。</summary>
    private static string EntryText(EntryDto e) => e.Kind == "http"
        ? $"HTTP {(e.HttpMethod is null ? "" : e.HttpMethod + " ")}{e.Route ?? e.Name}"
        : $"{e.Kind} {e.Route ?? e.Name}";

    private static void Print(IndexReader reader, long id, Direction direction, IReadOnlySet<EdgeKind> kinds, int depth, int indent, HashSet<long> visited, TextWriter stdout)
    {
        var pad = new string(' ', indent * 2);
        var seenEdges = new HashSet<long>();
        foreach (var n in reader.GetNeighbors(id, direction, kinds))
        {
            // 自环边（递归调用）在 Both 方向下会被 outgoing 和 incoming 各返回一次，只打印一次
            if (!seenEdges.Add(n.Edge.Id)) continue;
            var isOutgoing = n.Edge.FromId == id;
            var arrow = isOutgoing ? "→" : "←";
            var name = n.Other?.QualifiedName ?? n.Edge.ToName + "  (悬空)";
            stdout.WriteLine($"{pad}{arrow} {DbNames.ToDb(n.Edge.Kind)}  {name}  ({n.Edge.Confidence:0.00})  @ line {n.Edge.Line}");
            if (depth > 1 && n.Other is { } other && other.Kind != SymbolKind.External && visited.Add(other.Id))
                Print(reader, other.Id, isOutgoing ? Direction.Outgoing : Direction.Incoming, kinds, depth - 1, indent + 1, visited, stdout);
        }
    }
}
