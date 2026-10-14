using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAtlas.Core.Roslyn;

/// <summary>
/// Worker 向父进程流式输出的记录，一行一个 JSON，"t" 字段区分类型。
/// 边与 DI 用限定名引用符号，由父进程挂接成 id。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(FileRec), "file")]
[JsonDerivedType(typeof(SymbolRec), "symbol")]
[JsonDerivedType(typeof(EdgeRec), "edge")]
[JsonDerivedType(typeof(DiRec), "di")]
[JsonDerivedType(typeof(ProgressRec), "progress")]
[JsonDerivedType(typeof(ErrorRec), "error")]
[JsonDerivedType(typeof(DoneRec), "done")]
public abstract record RoslynRecord;

/// <summary>开始输出某个文件的符号。Path 相对仓库根，'/' 分隔。</summary>
public sealed record FileRec(string Path) : RoslynRecord;

/// <param name="Kind">与 Core 的 SymbolKind 数据库名一致：namespace / class / interface / struct / enum / type_alias / method / function / property / field。</param>
/// <param name="QualifiedName">方法带参数类型：Ns.Type.Method(int, string)。</param>
public sealed record SymbolRec(
    string Path, string Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, string? ParentQualifiedName, string? Visibility) : RoslynRecord;

/// <param name="ToIsExternal">目标不在仓库源码里（框架、第三方包），父进程记为 external 符号。</param>
/// <param name="Via">null / handles / dispatch:T / conduit。</param>
public sealed record EdgeRec(
    string FromQualifiedName, string ToQualifiedName, bool ToIsExternal, string Kind, double Confidence,
    string Path, int Line, string? Via = null, int? EvidenceLine = null) : RoslynRecord;

public sealed record DiRec(string InterfaceQualifiedName, string ImplQualifiedName, string Path, int Line, string Lifetime, bool Conditional) : RoslynRecord;

public sealed record ProgressRec(string Phase, int Done, int Total, string? Current) : RoslynRecord;

public sealed record ErrorRec(string Scope, string Message) : RoslynRecord;

public sealed record DoneRec(int Projects, int Documents, int Errors) : RoslynRecord;

public static class RecordJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(RoslynRecord record) => JsonSerializer.Serialize(record, Options);

    public static RoslynRecord? Deserialize(string line) =>
        string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<RoslynRecord>(line, Options);
}
