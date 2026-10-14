using CodeAtlas.Core.Model;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter;

/// <summary>
/// 每种语言 = 一对 .scm 查询 + 一个薄子类。本类负责：跑查询、按包含关系算父子与限定名、找引用所在的符号。
/// Language 与 Query 只读可跨线程共享；Parser 每次 Parse 新建。
/// </summary>
public abstract class LanguageAdapterBase : ILanguageAdapter, IDisposable
{
    private readonly Language _language;
    private readonly Query _definitions;
    private readonly Query _references;

    protected LanguageAdapterBase(string languageId, string grammarName, IReadOnlyList<string> extensions, string definitionsQuery, string referencesQuery)
    {
        LanguageId = languageId;
        Extensions = extensions;
        _language = new Language(grammarName);
        _definitions = new Query(_language, definitionsQuery);
        _references = new Query(_language, referencesQuery);
    }

    public string LanguageId { get; }
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>模块级符号的限定名前缀。TS/JS 用 "路径:"；C# 返回空串让命名空间链自己构成限定名。</summary>
    protected virtual string ModuleQualifiedPrefix(string relativePath) => relativePath + ":";

    /// <summary>语言特定的边种类修正，如 C# 把 I 开头的基类型视为 implements。</summary>
    protected virtual EdgeKind AdjustReferenceKind(EdgeKind kind, string name) => kind;

    protected virtual string? ExtractVisibility(Node definition) => null;

    /// <summary>定义的作用范围节点，默认就是定义节点本身；C# 文件级命名空间要扩展到整个文件。</summary>
    protected virtual Node DefinitionScope(Node definition, Node root) => definition;

    /// <summary>引用节点转成要解析的名字，默认取最后一个 "." 之后的部分。</summary>
    protected virtual string ReferenceName(Node node)
    {
        var text = node.Text;
        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    protected virtual bool ImportIsPath(string target) => target.StartsWith('.') || target.StartsWith('/');

    /// <summary>语言可否决某条引用捕获，例如 C# 只接受 Handler/Consumer 类接口的 handles。</summary>
    protected virtual bool AcceptReference(string captureName, Node node) => true;

    public ParsedFile Parse(string relativePath, string source)
    {
        using var parser = new Parser(_language);
        using var tree = parser.Parse(source) ?? throw new InvalidOperationException($"tree-sitter 未能解析 {relativePath}");
        var root = tree.RootNode;

        var symbols = new List<ParsedSymbol>();
        var ranges = new List<(int Start, int End)>();
        var lineCount = source.Count(c => c == '\n') + 1;
        symbols.Add(new ParsedSymbol(SymbolKind.Module, Path.GetFileName(relativePath), relativePath, null, 1, 1, lineCount, 1, null, null));
        ranges.Add((0, int.MaxValue));

        CollectDefinitions(root, relativePath, symbols, ranges);
        var (references, imports) = CollectReferences(root, ranges);
        return new ParsedFile(relativePath, LanguageId, symbols, references, imports);
    }

    private void CollectDefinitions(Node root, string relativePath, List<ParsedSymbol> symbols, List<(int Start, int End)> ranges)
    {
        var defs = new List<(Node Scope, Node Def, string Name, SymbolKind Kind)>();
        using (var cursor = _definitions.Execute(root))
        {
            foreach (var match in cursor.Matches)
            {
                Node? def = null, name = null;
                string? kindName = null;
                foreach (var c in match.Captures)
                {
                    if (c.Name == "name") name = c.Node;
                    else if (c.Name.StartsWith("def.", StringComparison.Ordinal)) { def = c.Node; kindName = c.Name[4..]; }
                }
                if (def is null || name is null || kindName is null) continue;
                defs.Add((DefinitionScope(def, root), def, name.Text, MapSymbolKind(kindName)));
            }
        }
        // 起点升序、终点降序：父节点一定排在子节点前面
        defs.Sort((a, b) => a.Scope.StartIndex != b.Scope.StartIndex
            ? a.Scope.StartIndex.CompareTo(b.Scope.StartIndex)
            : b.Scope.EndIndex.CompareTo(a.Scope.EndIndex));

        var prefix = ModuleQualifiedPrefix(relativePath);
        var stack = new Stack<int>();
        stack.Push(0);
        var seen = new HashSet<(int, int, SymbolKind)>();
        foreach (var (scope, def, name, kind) in defs)
        {
            if (!seen.Add((def.StartIndex, def.EndIndex, kind))) continue;
            // 栈顶不再包含当前定义就弹出；范围完全相同的是兄弟（如 C# 的 int a, b;），也弹出
            while (stack.Count > 1
                   && (ranges[stack.Peek()].End < scope.EndIndex
                       || (ranges[stack.Peek()].Start == scope.StartIndex && ranges[stack.Peek()].End == scope.EndIndex)))
                stack.Pop();
            var parentIndex = stack.Peek();
            var parentQualified = parentIndex == 0 ? prefix : symbols[parentIndex].QualifiedName + ".";
            symbols.Add(new ParsedSymbol(
                kind, name, parentQualified + name, FirstLine(def.Text),
                def.StartPosition.Row + 1, def.StartPosition.Column + 1,
                scope.EndPosition.Row + 1, scope.EndPosition.Column + 1,
                parentIndex, ExtractVisibility(def)));
            ranges.Add((scope.StartIndex, scope.EndIndex));
            stack.Push(symbols.Count - 1);
        }
    }

    private (List<ParsedReference>, List<ParsedImport>) CollectReferences(Node root, List<(int Start, int End)> ranges)
    {
        var references = new List<ParsedReference>();
        var imports = new List<ParsedImport>();
        var seen = new HashSet<(int, string, EdgeKind, int)>();
        using var cursor = _references.Execute(root);
        foreach (var capture in cursor.Captures)
        {
            if (!capture.Name.StartsWith("ref.", StringComparison.Ordinal)) continue;
            var node = capture.Node;
            var captureName = capture.Name[4..];
            if (!AcceptReference(captureName, node)) continue;
            var (kind, via) = MapEdgeKind(captureName);
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;
            if (kind == EdgeKind.Imports)
            {
                var target = node.Text;
                if (target.Length == 0) continue;
                imports.Add(new ParsedImport(target, ImportIsPath(target), line));
                if (seen.Add((0, target, kind, line))) references.Add(new ParsedReference(0, target, kind, line, col));
                continue;
            }
            var name = ReferenceName(node);
            if (name.Length == 0) continue;
            var from = EnclosingSymbol(ranges, node.StartIndex);
            var adjusted = via is null ? AdjustReferenceKind(kind, name) : kind;
            if (seen.Add((from, name, adjusted, line))) references.Add(new ParsedReference(from, name, adjusted, line, col, via));
        }
        return (references, imports);
    }

    /// <summary>包含该位置的最内层符号下标；ranges 按起点升序，后加入且仍包含的一定更内层。</summary>
    private static int EnclosingSymbol(List<(int Start, int End)> ranges, int position)
    {
        var best = 0;
        for (var i = 1; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (start > position) break;
            if (position < end) best = i;
        }
        return best;
    }

    /// <summary>签名取定义的第一行有效代码：跳过 C# 特性行（[...]）和 TS 装饰器行（@...）。</summary>
    private static string FirstLine(string text)
    {
        string? fallback = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            fallback ??= line;
            if (line.StartsWith('[') || line.StartsWith('@')) continue;
            return line.Length > 200 ? line[..200] : line;
        }
        var f = fallback ?? "";
        return f.Length > 200 ? f[..200] : f;
    }

    private static SymbolKind MapSymbolKind(string name) => name switch
    {
        "module" => SymbolKind.Module, "namespace" => SymbolKind.Namespace, "class" => SymbolKind.Class,
        "interface" => SymbolKind.Interface, "struct" => SymbolKind.Struct, "enum" => SymbolKind.Enum,
        "function" => SymbolKind.Function, "method" => SymbolKind.Method, "property" => SymbolKind.Property,
        "field" => SymbolKind.Field, "variable" => SymbolKind.Variable, "type_alias" => SymbolKind.TypeAlias,
        _ => throw new InvalidOperationException($"未知的定义 capture：@def.{name}"),
    };

    private static (EdgeKind Kind, string? Via) MapEdgeKind(string name) => name switch
    {
        "calls" => (EdgeKind.Calls, null), "references" => (EdgeKind.References, null), "inherits" => (EdgeKind.Inherits, null),
        "implements" => (EdgeKind.Implements, null), "imports" => (EdgeKind.Imports, null),
        "handles" => (EdgeKind.Dispatches, "handles"),
        _ => throw new InvalidOperationException($"未知的引用 capture：@ref.{name}"),
    };

    public void Dispose()
    {
        _references.Dispose();
        _definitions.Dispose();
        _language.Dispose();
    }
}
