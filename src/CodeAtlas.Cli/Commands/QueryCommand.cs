using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Commands;

internal static class QueryCommand
{
    private static readonly HashSet<EdgeKind> DefaultKinds =
        [EdgeKind.Calls, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References, EdgeKind.Dispatches];

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2)
        {
            stderr.WriteLine("用法：codeatlas query <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds a,b] [--db <path>]");
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

            foreach (var m in matches)
            {
                stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {files.GetValueOrDefault(m.FileId, "?")}:{m.StartLine}");
                Print(reader, m.Id, direction, kinds, depth, indent: 1, visited: [m.Id], stdout);
                stdout.WriteLine();
            }
            return 0;
        }
    }

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
            stdout.WriteLine($"{pad}{arrow} {DbNames.ToDb(n.Edge.Kind)}  {name}  ({n.Edge.Confidence:0.0})  @ line {n.Edge.Line}");
            if (depth > 1 && n.Other is { } other && other.Kind != SymbolKind.External && visited.Add(other.Id))
                Print(reader, other.Id, isOutgoing ? Direction.Outgoing : Direction.Incoming, kinds, depth - 1, indent + 1, visited, stdout);
        }
    }
}
