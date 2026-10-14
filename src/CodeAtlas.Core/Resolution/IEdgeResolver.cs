using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>一条待解析的引用：from 符号已入库，目标只有名字。</summary>
public sealed record PendingReference(long FromSymbolId, long FileId, string FilePath, string Language, string Name, EdgeKind Kind, int Line, string? Via = null);

public sealed class ResolveContext(IndexDatabase db, IReadOnlyList<PendingReference> references)
{
    public IndexDatabase Db { get; } = db;
    public IReadOnlyList<PendingReference> References { get; } = references;
}

/// <summary>边的解析器扩展点。主解析完成后按注册顺序执行，可把未解析引用改写为边或新符号。</summary>
public interface IEdgeResolver
{
    Task ResolveAsync(ResolveContext context, CancellationToken ct);
}
