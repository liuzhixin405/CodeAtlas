namespace CodeAtlas.Core.Model;

public sealed record FileRecord(long Id, string Path, string Language, string Hash, long Size, DateTimeOffset IndexedAt, FileStatus Status, DateTimeOffset? RoslynAt = null,
    long? Mtime = null);

public sealed record SymbolRecord(
    long Id, long FileId, SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, long? ParentId, string? Visibility, IndexSource Source);

/// <param name="Pos">调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致）；null = 未知（旧数据、非调用边）。</param>
/// <param name="Ctx">调用所在控制结构，见 <see cref="CallContext"/>。</param>
public sealed record EdgeRecord(
    long Id, long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via, int? EvidenceLine, int? Pos = null, string? Ctx = null);

/// <summary>待写入的边；ToId 为 null 表示悬空。</summary>
public sealed record NewEdge(
    long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via = null, int? EvidenceLine = null, int? Pos = null, string? Ctx = null);
