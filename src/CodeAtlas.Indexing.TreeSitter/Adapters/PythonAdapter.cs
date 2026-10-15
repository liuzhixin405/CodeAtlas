using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// Python：每个 .py 一个 Module 符号；类 / 函数 / 方法的限定名用点分模块名（backend/app/crud.py → backend.app.crud.X）。
/// 定义节点是 class_definition / function_definition 本身，所以 StartLine 是 def / class 那一行，不是装饰器那一行。
/// 模块级的类型别名（X = Annotated[T, …] / Optional[T] / Union[…]、type X = T）是 TypeAlias，右边的类型记成它的 references。
/// Module 的 Signature 记 __all__（"__all__ = a, b"），没有为 null；跨行的 def 签名收成一行（解析器要从中读返回注解）。
/// 引用复用 <see cref="ScriptBinding"/> 记 import 绑定：
/// 裸名字（Receiver=None）记它来自哪条 import；没有绑定而本文件有 from x import * 时 ImportSource 是这些模块（'\n' 连接）、ImportedName 为 <see cref="StarMarker"/>；
/// obj.f() 的接收者是 import 进来的名字或模块链时记 Import
/// （ImportSource 是模块名，如 "app"、"."、"..core"、"os.path"；ImportedName 是 from 导入的原名，整模块导入为 "*"）；
/// self.f() / cls.f() 记 This，super().f() 记 This 且 ImportedName = "super"；
/// 接收者的类型读得出来时记 Other + 类型描述（ImportedName 以 <see cref="TypedMarker"/> 开头，写法见 HeuristicEdgeResolver.Python.cs；
/// ImportSource 是描述根绑定的完整点分路径，根是本文件的名字时为 null）；
/// 其他接收者记 Other：接收者是没有绑定的裸名字时 ImportedName 记这个名字（ImportSource 为 null）。
/// </summary>
public sealed class PythonAdapter() : LanguageAdapterBase(
    "python", "Python", [".py"],
    QueryResources.Load("python.definitions.scm"),
    QueryResources.Load("python.references.scm"))
{
    /// <summary>super().f() 的标记，放在 ScriptBinding.ImportedName 里（Receiver = This）。</summary>
    public const string SuperMarker = "super";

    /// <summary>Receiver = Other 时 ImportedName 以它开头：后面是接收者的类型描述。</summary>
    public const string TypedMarker = "=";

    /// <summary>Receiver = None 时的 ImportedName：名字没有绑定，可能来自 ImportSource 里的星号导入。</summary>
    public const string StarMarker = "**";

    /// <summary>本文件的 import 绑定：本地名 → (模块名, 导入的原名)；import x / import x.y as z 的导入名是 "*"。</summary>
    internal sealed record ImportBindings(Dictionary<string, (string Source, string Imported)> ByLocal);

    /// <summary>注解里不记引用的内置类型与 typing 常用名：连过去只会是一堆 external:str。</summary>
    private static readonly HashSet<string> IgnoredTypeNames = new(StringComparer.Ordinal)
    {
        "int", "str", "float", "bool", "bytes", "bytearray", "complex", "object", "type", "list", "dict", "set", "frozenset", "tuple",
        "None", "Any", "Optional", "Union", "Callable", "Iterable", "Iterator", "Generator", "Sequence", "Mapping", "Literal", "Self",
    };

    /// <summary>注解里包着真正类型的泛型外壳：Optional[T]、list[T]、Annotated[T, …] 取 T；dict / Mapping 取值类型（最后一个实参）。</summary>
    private static readonly HashSet<string> WrapperTypeNames = new(StringComparer.Ordinal)
    {
        "Optional", "Union", "List", "list", "Set", "set", "FrozenSet", "frozenset", "Sequence", "Iterable", "Iterator", "Generator",
        "AsyncIterator", "AsyncIterable", "AsyncGenerator", "Awaitable", "Coroutine", "Annotated", "Type", "type", "ClassVar", "Final",
        "Tuple", "tuple", "Collection", "MutableSequence", "Required", "NotRequired", "ReadOnly",
    };

    private static readonly HashSet<string> MappingTypeNames = new(StringComparer.Ordinal) { "Dict", "dict", "Mapping", "MutableMapping", "DefaultDict", "defaultdict", "OrderedDict" };

    /// <summary>class A(object) / class M(type)：不记继承。</summary>
    private static readonly HashSet<string> IgnoredBaseNames = new(StringComparer.Ordinal) { "object", "type" };

    private static readonly Regex DottedName = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$", RegexOptions.Compiled);

    /// <summary>类型描述：Path 是根名字绑定的完整点分路径（null 为本文件可见的名字）；Desc 是根 + 步（".x"、"()"）。</summary>
    private sealed record PyType(string? Path, string Desc)
    {
        public PyType Then(string step) => this with { Desc = Desc + step };
    }

    /// <summary>一个作用域（函数或模块）里赋值过的名字：名字 → (位置, 类型)。</summary>
    private sealed class Scope(Node node, Scope? parent)
    {
        public Node Node { get; } = node;
        public Scope? Parent { get; } = parent;
        public Dictionary<string, List<(int Position, PyType? Type)>> Names { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FileState(ImportBindings bindings, List<string> stars, Node root)
    {
        public ImportBindings Bindings { get; } = bindings;
        public List<string> Stars { get; } = stars;
        public Node Root { get; } = root;
        public Dictionary<int, Scope> Scopes { get; } = [];
        public Dictionary<int, Dictionary<string, PyType?>> ClassAttributes { get; } = [];
        public HashSet<int> Building { get; } = [];
        public string? StarSource => Stars.Count == 0 ? null : string.Join('\n', Stars);
    }

    protected override string ModuleQualifiedPrefix(string relativePath) => PythonModuleResolver.ModuleName(relativePath) + ".";

    // ---- 调用大纲：调用所在控制结构 ----

    /// <summary>
    /// for / while 的 body 算 loop，循环头（for 的 left / right、while 的 condition）不算，循环的 else 子句（没 break 才执行）算 cond；
    /// 列表 / 字典 / 集合推导式与生成器表达式：第一个 for_in_clause（其中的可迭代对象只求值一次）不算，其余（body、if 子句、后续 for 子句）算 loop；
    /// if 的 consequence / elif / else 算 cond，condition 不算；三元 a if c else b 的 a、b 算 cond，c 不算；match 的 case 子句（含 guard）算 cond，subject 不算；
    /// try 的 body / else / finally 算 try，except / except* 子句算 catch；lambda 的 body 算 callback。
    /// 嵌套 def 本身是独立的符号（定义查询收所有 function_definition），里面的调用归它，所以 def 是边界而不是 callback。
    /// </summary>
    protected override string? ContextOf(Node ancestor, Node child) => ancestor.Type switch
    {
        "for_statement" or "while_statement" => IsField(ancestor, "body", child) ? CallContext.Loop
            : IsField(ancestor, "alternative", child) ? CallContext.Cond : null,
        "list_comprehension" or "dictionary_comprehension" or "set_comprehension" or "generator_expression" =>
            ancestor.NamedChildren.FirstOrDefault(c => c.Type == "for_in_clause") == child ? null : CallContext.Loop,
        "if_statement" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        "conditional_expression" => ancestor.NamedChildren.Count > 1 && ancestor.NamedChildren[1] == child ? null : CallContext.Cond,
        "case_clause" => CallContext.Cond,
        "try_statement" => child.Type is "except_clause" or "except_group_clause" ? CallContext.Catch : CallContext.Try,
        "lambda" => IsField(ancestor, "body", child) ? CallContext.Callback : null,   // 默认参数值在定义时求值
        _ => null,
    };

    protected override bool IsContextBoundary(Node ancestor) => ancestor.Type is "function_definition" or "class_definition" or "module";

    /// <summary>名字以 _ 开头为 private（含 __init__ 这类双下划线方法），否则 public。类属性别名 / 类型别名的名字在 assignment 的 left 上。</summary>
    protected override string? ExtractVisibility(Node definition)
    {
        var nameNode = definition.GetChildForField("name") ?? definition.GetChildForField("left") ?? definition.NamedChildren.FirstOrDefault();
        var name = nameNode?.Type == "type" ? nameNode.NamedChildren.FirstOrDefault()?.Text : nameNode?.Text;
        return name is null ? null : name.StartsWith('_') ? "private" : "public";
    }

    /// <summary>直接挂在类下的函数是方法；跨行的 def 签名收成一行；模块的 __all__ 记进 Module 的签名。</summary>
    protected override void AdjustSymbols(Node root, List<ParsedSymbol> symbols)
    {
        for (var i = 0; i < symbols.Count; i++)
            if (symbols[i].Kind == SymbolKind.Function && symbols[i].ParentIndex is { } p && symbols[p].Kind == SymbolKind.Class)
                symbols[i] = symbols[i] with { Kind = SymbolKind.Method };

        var multiLine = symbols.Select((s, i) => (s, i))
            .Where(x => x.s.Kind is SymbolKind.Function or SymbolKind.Method && x.s.Signature is { } sig && !sig.TrimEnd().EndsWith(':'))
            .ToDictionary(x => (x.s.StartLine, x.s.StartCol), x => x.i);
        if (multiLine.Count > 0)
            foreach (var fn in Descendants(root, n => n.Type is "function_definition"))
                if (multiLine.TryGetValue((fn.StartPosition.Row + 1, fn.StartPosition.Column + 1), out var index) && FullSignature(fn) is { } full)
                    symbols[index] = symbols[index] with { Signature = full };

        if (AllList(root) is { } all) symbols[0] = symbols[0] with { Signature = "__all__ = " + string.Join(", ", all) };
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();

    /// <summary>def 到函数体之前的整段签名收成一行；太长时参数写成 (...)，保住返回注解。</summary>
    private static string? FullSignature(Node fn)
    {
        if (fn.GetChildForField("body") is not { } body) return null;
        var text = fn.Text;
        var head = text.EndsWith(body.Text, StringComparison.Ordinal) ? text[..^body.Text.Length] : null;
        if (head is null) return null;
        var sig = Collapse(head);
        if (sig.Length <= 200) return sig;
        var prefix = text.StartsWith("async", StringComparison.Ordinal) ? "async def " : "def ";
        var returns = fn.GetChildForField("return_type") is { } rt ? " -> " + Collapse(rt.Text) : "";
        var shortSig = prefix + fn.GetChildForField("name")?.Text + "(...)" + returns + ":";
        return shortSig.Length > 200 ? shortSig[..200] : shortSig;
    }

    /// <summary>模块顶层 __all__ = [...] / (...) 里的字符串。</summary>
    private static List<string>? AllList(Node root)
    {
        foreach (var stmt in root.NamedChildren)
        {
            var assignment = stmt.Type == "assignment" ? stmt : stmt.Type == "expression_statement" ? stmt.NamedChildren.FirstOrDefault(c => c.Type == "assignment") : null;
            if (assignment?.GetChildForField("left") is not { Type: "identifier", Text: "__all__" }) continue;
            if (assignment.GetChildForField("right") is not { Type: "list" or "tuple" } list) return null;
            return list.NamedChildren.Where(c => c.Type == "string")
                .Select(s => s.NamedChildren.FirstOrDefault(c => c.Type == "string_content")?.Text).OfType<string>().ToList();
        }
        return null;
    }

    protected override bool AcceptReference(string captureName, Node node)
    {
        if (node.Type == "string_content")
        {
            // 字符串注解 "User" / "models.User"：只认点分名字
            if (!DottedName.IsMatch(node.Text)) return false;
            var last = node.Text[(node.Text.LastIndexOf('.') + 1)..];
            return !IgnoredTypeNames.Contains(last);
        }
        // type X = T 的 X 是被定义的名字，不是引用
        if (node.Parent is { Type: "type" } t && t.Parent is { Type: "type_alias_statement" } alias && alias.NamedChildren.FirstOrDefault() == t) return false;
        var bare = node.Parent is not { Type: "attribute" };
        return captureName switch
        {
            "references" => !bare || !IgnoredTypeNames.Contains(node.Text),
            "inherits" => !bare || !IgnoredBaseNames.Contains(node.Text),
            "calls" => !bare || node.Text != "super", // super() 本身不记；super().f() 的 f 照记
            _ => true,
        };
    }

    protected override object? PrepareFile(Node root)
    {
        var byLocal = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var stars = new List<string>();
        CollectImports(root, byLocal, stars, 0);
        return new FileState(new ImportBindings(byLocal), stars, root);
    }

    /// <summary>import 语句可能在函数里、if TYPE_CHECKING: 里、try 里：只往语句容器里走，不进表达式。</summary>
    private static void CollectImports(Node node, Dictionary<string, (string, string)> byLocal, List<string> stars, int depth)
    {
        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "import_statement":
                    foreach (var name in child.GetChildrenForField("name"))
                    {
                        if (name.Type == "dotted_name")
                        {
                            // import a.b.c 绑定的是顶层名字 a
                            var top = name.NamedChildren.FirstOrDefault()?.Text;
                            if (top is not null) byLocal[top] = (top, "*");
                        }
                        else if (name.Type == "aliased_import" && name.GetChildForField("name") is { } dotted && name.GetChildForField("alias") is { } alias)
                            byLocal[alias.Text] = (dotted.Text, "*");
                    }
                    break;
                case "import_from_statement":
                {
                    var module = child.GetChildForField("module_name")?.Text;
                    if (string.IsNullOrEmpty(module)) break;
                    if (child.NamedChildren.Any(c => c.Type == "wildcard_import")) { if (!stars.Contains(module)) stars.Add(module); break; }
                    foreach (var name in child.GetChildrenForField("name"))
                    {
                        if (name.Type == "dotted_name") byLocal[name.Text] = (module, name.Text);
                        else if (name.Type == "aliased_import" && name.GetChildForField("name") is { } original && name.GetChildForField("alias") is { } alias)
                            byLocal[alias.Text] = (module, original.Text);
                    }
                    break;
                }
                default:
                    if (depth < 64 && IsStatementContainer(child.Type)) CollectImports(child, byLocal, stars, depth + 1);
                    break;
            }
        }
    }

    private static bool IsStatementContainer(string type) =>
        type is "block" or "decorated_definition"
        || type.EndsWith("_statement", StringComparison.Ordinal)
        || type.EndsWith("_definition", StringComparison.Ordinal)
        || type.EndsWith("_clause", StringComparison.Ordinal);

    protected override ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState)
    {
        if (fileState is not FileState state) return reference;
        var bindings = state.Bindings;
        if (node.Type == "string_content")
        {
            var parts = node.Text.Split('.');
            if (parts.Length == 1) return reference with { Script = BareBinding(parts[0], state) };
            if (!bindings.ByLocal.TryGetValue(parts[0], out var root)) return reference with { Script = new ScriptBinding(ScriptReceiver.Other) };
            var head = root.Imported == "*" ? root.Source : PythonModuleResolver.Join(root.Source, root.Imported);
            var middle = string.Join('.', parts[1..^1]);
            return reference with { Script = new ScriptBinding(ScriptReceiver.Import, middle.Length == 0 ? head : head + "." + middle, "*") };
        }
        if (node.Parent is { Type: "attribute" } attribute && attribute.GetChildForField("attribute") == node)
            return reference with { Script = ReceiverBinding(attribute.GetChildForField("object"), state) };
        return reference with { Script = BareBinding(node.Text, state) };
    }

    private static ScriptBinding BareBinding(string name, FileState state) =>
        state.Bindings.ByLocal.TryGetValue(name, out var own) ? new ScriptBinding(ScriptReceiver.None, own.Source, own.Imported)
        : state.StarSource is { } stars ? new ScriptBinding(ScriptReceiver.None, stars, StarMarker)
        : new ScriptBinding(ScriptReceiver.None);

    private ScriptBinding ReceiverBinding(Node? receiver, FileState state)
    {
        var bindings = state.Bindings;
        switch (receiver?.Type)
        {
            case "identifier":
            {
                if (receiver.Text is "self" or "cls") return new ScriptBinding(ScriptReceiver.This);
                // 局部变量 / 参数 / 模块级变量的类型读得出来：按类型
                if (Lookup(receiver.Text, receiver, state, out var type))
                    return type is null ? new ScriptBinding(ScriptReceiver.Other, null, receiver.Text) : Typed(type);
                // 没有绑定的名字（本文件的类等）：记下名字，解析器看它是不是本文件可见的类
                return bindings.ByLocal.TryGetValue(receiver.Text, out var b)
                    ? new ScriptBinding(ScriptReceiver.Import, b.Source, b.Imported)
                    : new ScriptBinding(ScriptReceiver.Other, null, receiver.Text);
            }
            case "call" when receiver.GetChildForField("function") is { Type: "identifier", Text: "super" }:
                return new ScriptBinding(ScriptReceiver.This, null, SuperMarker);
            case "attribute" when DottedChain(receiver) is { } chain && bindings.ByLocal.TryGetValue(chain[0], out var root) && !Lookup(chain[0], receiver, state, out _):
            {
                // 模块链 a.b.c.f()：根名字是 import 进来的才算，拼成完整的点分路径交给解析器一段段找
                var head = root.Imported == "*" ? root.Source : PythonModuleResolver.Join(root.Source, root.Imported);
                return new ScriptBinding(ScriptReceiver.Import, head + "." + string.Join('.', chain.Skip(1)), "*");
            }
            case null:
                return new ScriptBinding(ScriptReceiver.Other);
            default:
                // self.repo.get()、serializer.data.get()、select(A).where(B)、Runner().start()、xs[0].f()
                return ValueType(receiver, state, receiver, 0) is { } t ? Typed(t) : new ScriptBinding(ScriptReceiver.Other);
        }
    }

    private static ScriptBinding Typed(PyType type) => new(ScriptReceiver.Other, type.Path, TypedMarker + type.Desc);

    // ---------- 类型描述 ----------

    /// <summary>表达式的类型描述；读不出来返回 null。at 是查局部名字的位置。</summary>
    private PyType? ValueType(Node expr, FileState state, Node at, int depth)
    {
        if (depth > 12) return null;
        switch (expr.Type)
        {
            case "identifier":
            {
                if (expr.Text is "self" or "cls") return EnclosingClass(expr) is null ? null : new PyType(null, "self");
                if (Lookup(expr.Text, at, state, out var local)) return local;
                if (state.Bindings.ByLocal.TryGetValue(expr.Text, out var b))
                    return new PyType(b.Imported == "*" ? b.Source : PythonModuleResolver.Join(b.Source, b.Imported), expr.Text);
                return new PyType(null, expr.Text);
            }
            case "call":
            {
                var fn = expr.GetChildForField("function");
                if (fn is null || fn is { Type: "identifier", Text: "super" }) return null;
                return ValueType(fn, state, at, depth + 1)?.Then("()");
            }
            case "attribute":
            {
                if (expr.GetChildForField("object") is not { } obj || expr.GetChildForField("attribute") is not { } attr) return null;
                // self.x：本文件里这个类给 x 赋过值 / 注解过类型时直接用那个类型
                if (obj is { Type: "identifier", Text: "self" or "cls" } && EnclosingClass(expr) is { } cls
                    && ClassAttributes(cls, state).TryGetValue(attr.Text, out var attrType) && attrType is not null)
                    return attrType;
                // 模块链 httpx.Client：根是导入名
                if (DottedChain(expr) is { } chain && state.Bindings.ByLocal.TryGetValue(chain[0], out var root) && !Lookup(chain[0], at, state, out _))
                {
                    var head = root.Imported == "*" ? root.Source : PythonModuleResolver.Join(root.Source, root.Imported);
                    return new PyType(head + "." + string.Join('.', chain.Skip(1)), chain[^1]);
                }
                return ValueType(obj, state, at, depth + 1)?.Then("." + attr.Text);
            }
            case "await" or "parenthesized_expression":
                return expr.NamedChildren.FirstOrDefault() is { } inner ? ValueType(inner, state, at, depth + 1) : null;
            case "subscript":
                // xs[0]、d["k"]：元素，按核心类型看就是同一个类型
                return expr.GetChildForField("value") is { } value ? ValueType(value, state, at, depth + 1) : null;
            default:
                return null;
        }
    }

    /// <summary>注解的类型描述：T、"T"、mod.T、Optional[T]、T | None、Annotated[T, …]、list[T]（元素）、dict[K, V]（值）。</summary>
    private PyType? AnnotationType(Node? node, FileState state, int depth = 0)
    {
        if (node is null || depth > 8) return null;
        switch (node.Type)
        {
            case "type":
                return AnnotationType(node.NamedChildren.FirstOrDefault(), state, depth + 1);
            case "identifier":
                if (IgnoredTypeNames.Contains(node.Text) && !WrapperTypeNames.Contains(node.Text)) return null;
                return NamedType(node.Text, state);
            case "string":
            {
                var content = node.NamedChildren.FirstOrDefault(c => c.Type == "string_content")?.Text;
                if (content is null || !DottedName.IsMatch(content)) return null;
                var parts = content.Split('.');
                if (parts.Length == 1) return IgnoredTypeNames.Contains(content) ? null : NamedType(content, state);
                return state.Bindings.ByLocal.TryGetValue(parts[0], out var r)
                    ? new PyType((r.Imported == "*" ? r.Source : PythonModuleResolver.Join(r.Source, r.Imported)) + "." + string.Join('.', parts[1..]), parts[^1])
                    : null;
            }
            case "attribute":
            {
                if (DottedChain(node) is not { } chain) return null;
                if (!state.Bindings.ByLocal.TryGetValue(chain[0], out var r)) return new PyType(null, chain[0]).Then(string.Concat(chain.Skip(1).Select(c => "." + c)));
                return new PyType((r.Imported == "*" ? r.Source : PythonModuleResolver.Join(r.Source, r.Imported)) + "." + string.Join('.', chain.Skip(1)), chain[^1]);
            }
            case "binary_operator":
                // T | None
                return AnnotationType(node.GetChildForField("left"), state, depth + 1) ?? AnnotationType(node.GetChildForField("right"), state, depth + 1);
            case "union_type":
                foreach (var part in node.NamedChildren)
                    if (AnnotationType(part, state, depth + 1) is { } member) return member;
                return null;
            case "none":
                return null;
            case "subscript" or "generic_type":
            {
                var head = node.Type == "subscript" ? node.GetChildForField("value") : node.NamedChildren.FirstOrDefault();
                var args = node.Type == "subscript"
                    ? node.GetChildrenForField("subscript").ToList()
                    : node.NamedChildren.Skip(1).SelectMany(c => c.Type == "type_parameter" ? c.NamedChildren : [c]).ToList();
                var headName = head?.Type == "identifier" ? head.Text : head?.Type == "attribute" ? head.GetChildForField("attribute")?.Text : null;
                if (headName is not null && MappingTypeNames.Contains(headName)) return args.Count > 0 ? AnnotationType(args[^1], state, depth + 1) : null;
                if (headName is not null && WrapperTypeNames.Contains(headName))
                {
                    foreach (var arg in args)
                        if (AnnotationType(arg, state, depth + 1) is { } inner) return inner;
                    return null;
                }
                return AnnotationType(head, state, depth + 1); // 用户泛型 Repo[User]：就是 Repo
            }
            default:
                return null;
        }
    }

    private static PyType NamedType(string name, FileState state) =>
        state.Bindings.ByLocal.TryGetValue(name, out var b)
            ? new PyType(b.Imported == "*" ? b.Source : PythonModuleResolver.Join(b.Source, b.Imported), name)
            : new PyType(null, name);

    // ---------- 作用域：函数 / 模块里赋值过的名字 ----------

    /// <summary>at 处可见的名字：从最近的函数往外到模块。found 表示名字在某个作用域里赋过值 / 是参数（类型可能未知）。</summary>
    private bool Lookup(string name, Node at, FileState state, out PyType? type)
    {
        type = null;
        var innermost = true;
        for (var scope = ScopeOf(at, state); scope is not null; scope = scope.Parent)
        {
            if (scope.Names.TryGetValue(name, out var entries))
            {
                var found = false;
                foreach (var (position, t) in entries)
                    if (!innermost || position < at.StartIndex) { type = t; found = true; }
                if (found) return true;
            }
            innermost = false;
        }
        return false;
    }

    private Scope ScopeOf(Node at, FileState state) => GetScope(EnclosingFunction(at) ?? state.Root, state);

    private Scope GetScope(Node node, FileState state)
    {
        var isModule = node.Type == "module";
        var key = node.StartIndex * 2 + (isModule ? 1 : 0);
        if (state.Scopes.TryGetValue(key, out var cached)) return cached;
        var parent = isModule ? null : GetScope(EnclosingFunction(node) ?? state.Root, state);
        var scope = new Scope(node, parent);
        state.Scopes[key] = scope; // 先登记：建作用域时查前面的名字要用到它
        if (node.Type == "function_definition" && node.GetChildForField("parameters") is { } parameters)
            foreach (var p in parameters.NamedChildren) AddParameter(p, scope, state);
        var body = node.Type == "module" ? node : node.GetChildForField("body");
        if (body is not null) Walk(body, scope, state);
        return scope;
    }

    private void AddParameter(Node p, Scope scope, FileState state)
    {
        switch (p.Type)
        {
            case "identifier":
                if (p.Text is not ("self" or "cls")) Add(scope, p, null);
                break;
            case "typed_parameter":
                if (p.NamedChildren.FirstOrDefault() is { Type: "identifier" } name && name.Text is not ("self" or "cls"))
                    Add(scope, name, AnnotationType(p.GetChildForField("type"), state));
                break;
            case "default_parameter" or "typed_default_parameter":
                if (p.GetChildForField("name") is { Type: "identifier" } named)
                    Add(scope, named, p.Type == "typed_default_parameter" ? AnnotationType(p.GetChildForField("type"), state) : null);
                break;
            case "list_splat_pattern" or "dictionary_splat_pattern":
                if (p.NamedChildren.FirstOrDefault() is { Type: "identifier" } splat) Add(scope, splat, null);
                break;
        }
    }

    /// <summary>作用域里的语句：赋值、for、with … as、海象赋值；不进嵌套的函数 / 类 / lambda（它们是别的作用域）。</summary>
    private void Walk(Node node, Scope scope, FileState state)
    {
        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "function_definition" or "class_definition" or "decorated_definition" or "lambda":
                    continue;
                case "assignment":
                {
                    var left = child.GetChildForField("left");
                    var annotation = child.GetChildForField("type");
                    var right = child.GetChildForField("right");
                    if (left is { Type: "identifier" })
                        Add(scope, left, annotation is not null ? AnnotationType(annotation, state) : right is not null ? ValueType(right, state, child, 0) : null);
                    else if (left is not null) DeclarePattern(left, scope);
                    break;
                }
                case "for_statement" or "for_in_clause":
                {
                    var left = child.GetChildForField("left");
                    var right = child.GetChildForField("right");
                    if (left is { Type: "identifier" }) Add(scope, left, right is null ? null : ValueType(right, state, child, 0)); // 元素：按核心类型看与容器同一个类型
                    else if (left is not null) DeclarePattern(left, scope);
                    break;
                }
                case "as_pattern":
                    // with A() as a、except E as e
                    if (child.NamedChildren.FirstOrDefault() is { } value
                        && child.NamedChildren.FirstOrDefault(c => c.Type == "as_pattern_target")?.NamedChildren.FirstOrDefault() is { Type: "identifier" } target)
                        Add(scope, target, ValueType(value, state, child, 0));
                    break;
                case "named_expression":
                    if (child.GetChildForField("name") is { Type: "identifier" } walrus)
                        Add(scope, walrus, child.GetChildForField("value") is { } v ? ValueType(v, state, child, 0) : null);
                    break;
            }
            Walk(child, scope, state);
        }
    }

    private static void DeclarePattern(Node pattern, Scope scope)
    {
        if (pattern.Type == "identifier") { Add(scope, pattern, null); return; }
        if (pattern.Type is "pattern_list" or "tuple_pattern" or "list_pattern" or "tuple" or "list" or "expression_list")
            foreach (var c in pattern.NamedChildren) DeclarePattern(c, scope);
    }

    private static void Add(Scope scope, Node name, PyType? type)
    {
        if (!scope.Names.TryGetValue(name.Text, out var list)) scope.Names[name.Text] = list = [];
        list.Add((name.StartIndex, type));
    }

    // ---------- 类的实例属性（只看本文件） ----------

    /// <summary>
    /// 类的属性 → 类型：类体里的注解（repo: Repo，dataclass / pydantic）与赋值（serializer_class = X），
    /// 以及各方法里 self.x = … / self.x: T = …（取注解，否则取第一个读得出类型的值；值里的参数按那个方法的作用域看）。
    /// </summary>
    private Dictionary<string, PyType?> ClassAttributes(Node cls, FileState state)
    {
        if (state.ClassAttributes.TryGetValue(cls.StartIndex, out var cached)) return cached;
        var attrs = new Dictionary<string, PyType?>(StringComparer.Ordinal);
        if (!state.Building.Add(cls.StartIndex)) return attrs; // 正在算（属性的值又引用 self.y）：先当没有
        try
        {
            var body = cls.GetChildForField("body");
            foreach (var stmt in body?.NamedChildren ?? [])
            {
                var assignment = stmt.Type == "assignment" ? stmt : stmt.Type == "expression_statement" ? stmt.NamedChildren.FirstOrDefault(c => c.Type == "assignment") : null;
                if (assignment?.GetChildForField("left") is { Type: "identifier" } left)
                {
                    var annotation = assignment.GetChildForField("type");
                    var right = assignment.GetChildForField("right");
                    Set(attrs, left.Text, annotation is not null ? AnnotationType(annotation, state) : right is not null ? ValueType(right, state, assignment, 0) : null, annotation is not null);
                }
            }
            foreach (var method in body?.NamedChildren.Select(c => c.Type == "decorated_definition" ? c.GetChildForField("definition") : c).Where(c => c is { Type: "function_definition" }) ?? [])
                foreach (var a in Descendants(method!.GetChildForField("body"), n => n.Type == "assignment", n => n.Type is "function_definition" or "class_definition" or "lambda"))
                {
                    if (a.GetChildForField("left") is not { Type: "attribute" } target
                        || target.GetChildForField("object") is not { Type: "identifier", Text: "self" or "cls" }
                        || target.GetChildForField("attribute") is not { } attr) continue;
                    var annotation = a.GetChildForField("type");
                    var right = a.GetChildForField("right");
                    Set(attrs, attr.Text, annotation is not null ? AnnotationType(annotation, state) : right is not null ? ValueType(right, state, a, 0) : null, annotation is not null);
                }
        }
        finally { state.Building.Remove(cls.StartIndex); }
        state.ClassAttributes[cls.StartIndex] = attrs;
        return attrs;

        static void Set(Dictionary<string, PyType?> attrs, string name, PyType? type, bool annotated)
        {
            if (type is null) { attrs.TryAdd(name, null); return; }
            if (annotated || !attrs.TryGetValue(name, out var existing) || existing is null) attrs[name] = type;
        }
    }

    /// <summary>最近的外层函数（不算 lambda）；不在函数里返回 null。</summary>
    private static Node? EnclosingFunction(Node node)
    {
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
            if (cur.Type == "function_definition") return cur;
        return null;
    }

    /// <summary>node 所在方法的类：最近的函数直接（或经装饰器）定义在类体里时就是那个类。</summary>
    private static Node? EnclosingClass(Node node)
    {
        for (var fn = EnclosingFunction(node); fn is not null; fn = EnclosingFunction(fn))
        {
            var holder = fn.Parent is { Type: "decorated_definition" } d ? d.Parent : fn.Parent;
            if (holder is { Type: "block" } && holder.Parent is { Type: "class_definition" } cls) return cls;
        }
        return null;
    }

    /// <summary>先序遍历（文档顺序）里满足 match 的节点；stop 为真的节点不往下走（本身也不算）。</summary>
    private static IEnumerable<Node> Descendants(Node? node, Func<Node, bool> match, Func<Node, bool>? stop = null)
    {
        if (node is null) yield break;
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n != node && stop?.Invoke(n) == true) continue;
            if (match(n)) yield return n;
            var children = n.NamedChildren.ToList();
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>Receiver = Other 且 ImportedName 为此值：旧格式（接收者是一串调用的结果，ImportSource 是链根的模块路径）。解析器仍认。</summary>
    public const string CallResultMarker = "()";

    /// <summary>纯标识符组成的 a.b.c；中间夹调用、下标的返回 null。</summary>
    private static List<string>? DottedChain(Node node)
    {
        var parts = new List<string>();
        var cur = node;
        while (cur.Type == "attribute")
        {
            var attr = cur.GetChildForField("attribute");
            var obj = cur.GetChildForField("object");
            if (attr is null || obj is null) return null;
            parts.Add(attr.Text);
            cur = obj;
        }
        if (cur.Type != "identifier") return null;
        parts.Add(cur.Text);
        parts.Reverse();
        return parts;
    }
}
