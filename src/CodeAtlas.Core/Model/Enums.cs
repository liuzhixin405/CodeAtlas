namespace CodeAtlas.Core.Model;

public enum SymbolKind { Module, Namespace, Class, Interface, Struct, Enum, Function, Method, Property, Field, Variable, TypeAlias, External }

public enum EdgeKind { Contains, Calls, References, Inherits, Implements, Imports, Dispatches }

public enum IndexSource { TreeSitter, Roslyn }

public enum FileStatus { Ok, ParseFailed, Skipped }

public enum Direction { Outgoing, Incoming, Both }

/// <summary>枚举与数据库中小写蛇形字符串的双向转换。</summary>
public static class DbNames
{
    private static readonly Dictionary<SymbolKind, string> SymbolKindToDb = new()
    {
        [SymbolKind.Module] = "module", [SymbolKind.Namespace] = "namespace", [SymbolKind.Class] = "class",
        [SymbolKind.Interface] = "interface", [SymbolKind.Struct] = "struct", [SymbolKind.Enum] = "enum",
        [SymbolKind.Function] = "function", [SymbolKind.Method] = "method", [SymbolKind.Property] = "property",
        [SymbolKind.Field] = "field", [SymbolKind.Variable] = "variable", [SymbolKind.TypeAlias] = "type_alias",
        [SymbolKind.External] = "external",
    };
    private static readonly Dictionary<string, SymbolKind> DbToSymbolKind = SymbolKindToDb.ToDictionary(p => p.Value, p => p.Key);

    private static readonly Dictionary<EdgeKind, string> EdgeKindToDb = new()
    {
        [EdgeKind.Contains] = "contains", [EdgeKind.Calls] = "calls", [EdgeKind.References] = "references",
        [EdgeKind.Inherits] = "inherits", [EdgeKind.Implements] = "implements", [EdgeKind.Imports] = "imports",
        [EdgeKind.Dispatches] = "dispatches",
    };
    private static readonly Dictionary<string, EdgeKind> DbToEdgeKind = EdgeKindToDb.ToDictionary(p => p.Value, p => p.Key);

    public static string ToDb(SymbolKind kind) => SymbolKindToDb[kind];
    public static SymbolKind ParseSymbolKind(string db) => DbToSymbolKind[db];
    public static bool TryParseSymbolKind(string db, out SymbolKind kind) => DbToSymbolKind.TryGetValue(db, out kind);
    public static string ToDb(EdgeKind kind) => EdgeKindToDb[kind];
    public static EdgeKind ParseEdgeKind(string db) => DbToEdgeKind[db];
    public static bool TryParseEdgeKind(string db, out EdgeKind kind) => DbToEdgeKind.TryGetValue(db, out kind);
    public static IReadOnlyCollection<string> EdgeKindNames => DbToEdgeKind.Keys;
    public static string ToDb(IndexSource source) => source == IndexSource.TreeSitter ? "treesitter" : "roslyn";
    public static IndexSource ParseSource(string db) => db == "roslyn" ? IndexSource.Roslyn : IndexSource.TreeSitter;
    public static string ToDb(FileStatus status) => status switch
    {
        FileStatus.Ok => "ok",
        FileStatus.ParseFailed => "parse_failed",
        _ => "skipped",
    };
    public static FileStatus ParseStatus(string db) => db switch
    {
        "ok" => FileStatus.Ok,
        "parse_failed" => FileStatus.ParseFailed,
        _ => FileStatus.Skipped,
    };
}
