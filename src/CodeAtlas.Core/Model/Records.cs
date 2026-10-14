namespace CodeAtlas.Core.Model;

public sealed record FileRecord(long Id, string Path, string Language, string Hash, long Size, DateTimeOffset IndexedAt, FileStatus Status, DateTimeOffset? RoslynAt = null);

public sealed record SymbolRecord(
    long Id, long FileId, SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, long? ParentId, string? Visibility, IndexSource Source);

public sealed record EdgeRecord(
    long Id, long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via, int? EvidenceLine);

/// <summary>待写入的边；ToId 为 null 表示悬空。</summary>
public sealed record NewEdge(
    long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via = null, int? EvidenceLine = null);
