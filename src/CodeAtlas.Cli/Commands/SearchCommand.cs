using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Commands;

internal static class SearchCommand
{
    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2) { stderr.WriteLine("用法：codeatlas search <repo> <text> [--limit N] [--db <path>]"); return 2; }
        var limit = int.TryParse(args.Value("--limit"), out var l) ? Math.Clamp(l, 1, 500) : 30;

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(args.Positional[0], args, stderr); }
        catch (FileNotFoundException) { return 1; }

        using (db)
        {
            var reader = new IndexReader(db);
            var files = reader.GetAllFiles().ToDictionary(f => f.Id, f => f.Path);
            var hits = reader.SearchSymbols(args.Positional[1], limit);
            if (hits.Count == 0) { stdout.WriteLine("没有匹配。"); return 0; }
            foreach (var h in hits)
                stdout.WriteLine($"{h.QualifiedName}  [{DbNames.ToDb(h.Kind)}]  {files.GetValueOrDefault(h.FileId, "?")}:{h.StartLine}");
            return 0;
        }
    }
}
