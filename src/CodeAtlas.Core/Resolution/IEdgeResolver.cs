using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>一条待解析的引用：from 符号已入库，目标只有名字。Script 见 <see cref="ScriptBinding"/>。</summary>
/// <param name="Pos">调用名在文件里的 UTF-16 字符偏移（0 起，与 Roslyn 的 SpanStart、VS Code 的 document.positionAt 一致）；-1 = 未知。</param>
/// <param name="Context">所在控制结构，见 <see cref="CallContext"/>。</param>
public sealed record PendingReference(long FromSymbolId, long FileId, string FilePath, string Language, string Name, EdgeKind Kind, int Line, string? Via = null, ScriptBinding? Script = null,
    int Pos = -1, string? Context = null);

public sealed class ResolveContext(IndexDatabase db, IReadOnlyList<PendingReference> references, IReadOnlyList<ScriptPathConfig>? scriptConfigs = null)
{
    public IndexDatabase Db { get; } = db;
    public IReadOnlyList<PendingReference> References { get; } = references;
    /// <summary>仓库里的 tsconfig / jsconfig（baseUrl 与 paths 别名），解析 "@/x" 这类非相对 import 用。</summary>
    public IReadOnlyList<ScriptPathConfig> ScriptConfigs { get; } = scriptConfigs ?? [];
    /// <summary>仓库里的 go.mod（module 路径与本地 replace），解析 Go 的 import 路径用。</summary>
    public IReadOnlyList<GoModFile> GoModules { get; init; } = [];
    /// <summary>
    /// 增量只解析受影响文件时，其余文件里解析别处要用的引用（父类型、Go 内嵌、返回值提示、Python 绑定等）：
    /// 只用来建各语言的索引（与全量时看到的一样），不解析、不写边。全量时为空。
    /// </summary>
    public IReadOnlyList<PendingReference> SupportReferences { get; init; } = [];
}

/// <summary>边的解析器扩展点。主解析完成后按注册顺序执行，可把未解析引用改写为边或新符号。</summary>
public interface IEdgeResolver
{
    Task ResolveAsync(ResolveContext context, CancellationToken ct);
}
