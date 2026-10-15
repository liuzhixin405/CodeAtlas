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

    /// <summary>文件级预处理，结果传给 <see cref="AnnotateReference"/>；脚本语言在这里读 import 绑定。</summary>
    protected virtual object? PrepareFile(Node root) => null;

    /// <summary>给引用补语言特定的信息（脚本的 import 绑定与接收者）。</summary>
    protected virtual ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState) => reference;

    // ---- 调用大纲：调用所在控制结构与字符偏移（edges.ctx / edges.pos）----

    /// <summary>
    /// 从调用节点往上走时，路径上的祖先 <paramref name="ancestor"/> 让调用处在哪种控制结构里（<see cref="CallContext"/> 的取值），不构成结构返回 null。
    /// <paramref name="child"/> 是路径上 ancestor 的直接子节点：用来区分"条件 / 循环头里的调用（总会执行，不算）"与"分支体 / 循环体里的调用（算）"。
    /// 每种语言覆写；默认不认任何结构。
    /// </summary>
    protected virtual string? ContextOf(Node ancestor, Node child) => null;

    /// <summary>走到这个祖先就停：它是调用所属成员的边界（方法 / 函数 / 类 / 模块）。默认不停（走到根）。</summary>
    protected virtual bool IsContextBoundary(Node ancestor) => false;

    /// <summary>调用所在的最内层控制结构：从调用节点往上，遇到的第一个 <see cref="ContextOf"/> 非 null 的祖先；先遇到边界则 null。</summary>
    protected string? CallContextOf(Node node)
    {
        var child = node;
        for (var p = node.Parent; p is not null; child = p, p = p.Parent)
        {
            if (IsContextBoundary(p)) return null;
            if (ContextOf(p, child) is { } ctx) return ctx;
        }
        return null;
    }

    /// <summary>
    /// 调用名的字符偏移（UTF-16，0 起，与 Roslyn SpanStart 一致），调用大纲按它排先后；未知返回 -1。
    /// 取"被调名字"那个 token 的起点（a.B().C() 里 C 的位置），这样同一行的链式调用也能排对。
    /// 默认取 <see cref="NameToken"/> 的起点。TreeSitter.DotNet 按 UTF-16 喂源码，Node.StartIndex 已经是 UTF-16 字符偏移
    /// （实测：中文注释、emoji 代理对之后的位置与 string.IndexOf 一致），不用再从字节换算。
    /// </summary>
    protected virtual int ReferencePos(Node node, string captureName) => NameToken(node).StartIndex;

    /// <summary>
    /// 捕获节点里"名字"那一段：成员访问 / 限定名取最后的名字（new A.B.Foo() 的 Foo、Java a.b.Foo），泛型取类型名（Foo&lt;T&gt; 的 Foo）；
    /// 本身就是名字 token、或认不出的形态（C# 的 new()）原样返回。
    /// </summary>
    protected static Node NameToken(Node node)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            var next = node.Type switch
            {
                "qualified_name" or "member_access_expression" or "member_binding_expression" or "scoped_identifier" or "qualified_type" => node.GetChildForField("name"),
                "member_expression" => node.GetChildForField("property"),
                "selector_expression" => node.GetChildForField("field"),
                "attribute" => node.GetChildForField("attribute"),
                "generic_name" or "generic_type" => node.FirstNamedChild,
                "scoped_type_identifier" => node.LastNamedChild,
                _ => null,
            };
            if (next is null) return node;
            node = next;
        }
        return node;
    }

    /// <summary><paramref name="child"/> 是不是 <paramref name="parent"/> 的 <paramref name="field"/> 字段（ContextOf 里区分条件 / 循环头与体）。</summary>
    protected static bool IsField(Node parent, string field, Node child) => parent.GetChildForField(field) is { } f && f == child;

    /// <summary>定义收齐后的修正，如脚本把 `export default X;` 指到的 X 标成默认导出。</summary>
    protected virtual void AdjustSymbols(Node root, List<ParsedSymbol> symbols) { }

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
        AdjustSymbols(root, symbols);
        var (references, imports) = CollectReferences(root, ranges, PrepareFile(root));
        return CompleteFile(root, new ParsedFile(relativePath, LanguageId, symbols, references, imports));
    }

    /// <summary>
    /// 整个文件收齐（含引用）后的最后修正，默认原样返回。可以重排符号，但要同步改 ParentIndex 与引用的 FromIndex，
    /// 且父符号必须排在子符号前面。Go 在这里把方法挂到接收者类型下（类型可能声明在方法之后）。
    /// </summary>
    protected virtual ParsedFile CompleteFile(Node root, ParsedFile file) => file;

    private void CollectDefinitions(Node root, string relativePath, List<ParsedSymbol> symbols, List<(int Start, int End)> ranges)
    {
        var defs = new List<(Node Scope, Node Def, string Name, SymbolKind Kind)>();
        using (var cursor = _definitions.Execute(root))
        {
            foreach (var match in cursor.Matches)
            {
                Node? def = null, name = null;
                string? kindName = null, literalName = null;
                foreach (var c in match.Captures)
                {
                    if (c.Name == "name") name = c.Node;
                    // 匿名默认导出（export default function () {} / () => … / class {}）：名字就叫 default
                    else if (c.Name == "name.default") { name = c.Node; literalName = "default"; }
                    else if (c.Name.StartsWith("def.", StringComparison.Ordinal)) { def = c.Node; kindName = c.Name[4..]; }
                }
                if (def is null || name is null || kindName is null) continue;
                defs.Add((DefinitionScope(def, root), def, literalName ?? name.Text, MapSymbolKind(kindName)));
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

    private (List<ParsedReference>, List<ParsedImport>) CollectReferences(Node root, List<(int Start, int End)> ranges, object? fileState)
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
            if (seen.Add((from, name, adjusted, line)))
                references.Add(AnnotateReference(new ParsedReference(from, name, adjusted, line, col, via,
                    Context: adjusted is EdgeKind.Calls or EdgeKind.Dispatches ? CallContextOf(node) : null, Pos: ReferencePos(node, captureName)), captureName, node, fileState));
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
        "render" => (EdgeKind.Calls, "render"),
        _ => throw new InvalidOperationException($"未知的引用 capture：@ref.{name}"),
    };

    public void Dispose()
    {
        _references.Dispose();
        _definitions.Dispose();
        _language.Dispose();
    }
}
