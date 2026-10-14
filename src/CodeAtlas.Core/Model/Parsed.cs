namespace CodeAtlas.Core.Model;

/// <summary>解析器产出的符号，尚无数据库 id；ParentIndex 指向同一 ParsedFile.Symbols 的下标。</summary>
public sealed record ParsedSymbol(
    SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, int? ParentIndex, string? Visibility);

/// <summary>
/// 未解析的引用。FromIndex 是包含该引用的符号下标（文件级为 0，即 Module）。
/// Via 标记间接关系：目前只有 "handles"（该类型实现了 IXxxHandler&lt;Name, …&gt;，即它处理 Name）。
/// </summary>
public sealed record ParsedReference(int FromIndex, string Name, EdgeKind Kind, int Line, int Col, string? Via = null);

/// <summary>import / using 记录。IsPath 为 true 表示相对路径（TS/JS），否则是命名空间（C#）。</summary>
public sealed record ParsedImport(string Target, bool IsPath, int Line);

/// <summary>一个文件的解析结果。约定 Symbols[0] 是该文件的 Module 符号。</summary>
public sealed record ParsedFile(
    string RelativePath, string Language,
    IReadOnlyList<ParsedSymbol> Symbols,
    IReadOnlyList<ParsedReference> References,
    IReadOnlyList<ParsedImport> Imports);
