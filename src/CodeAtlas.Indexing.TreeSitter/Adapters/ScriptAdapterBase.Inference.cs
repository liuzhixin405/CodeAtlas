using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// 接收者 / 返回值的形态推断（只看本文件的语法树，跨文件的部分交给解析器）：
/// x.y() 的 x 是什么——内建对象（new Set / Map / URLSearchParams、字面量、Promise / JSON 这类全局对象）、
/// 带类型标注的变量 / 参数（let panel: PanelHost）、new 出来的类实例、某个函数的返回值（const api = useApi()）、
/// npm 包 API 的返回值（vscode.window.createStatusBarItem()）；推不出就是 null，解析器按名字猜但降低置信度。
/// 函数自己的返回值形态（返回类型标注、return new X()、return f()、useMemo(() => …)）记成 via = "returns" 的引用，
/// 解析器据此把 const api = useApi(); api.post() 连到 useApi 返回的那个类的 post。
/// </summary>
public abstract partial class ScriptAdapterBase
{
    /// <summary>值的形态。</summary>
    internal abstract record ValueShape;
    /// <summary>内建 / 宿主对象（Set、Map、Promise、字符串、数组……），不是仓库里的类。</summary>
    internal sealed record BuiltinShape(string Name) : ValueShape;
    /// <summary>本文件里写下的类型名（可能是 import 的别名，或 ns.T）：类型标注、new X()、类本身（X.staticMethod()）。</summary>
    internal sealed record TypeShape(string Name) : ValueShape;
    /// <summary>本文件可见的函数 f（import 进来的或本文件定义的）的返回值。</summary>
    internal sealed record ReturnShape(string Function) : ValueShape;
    /// <summary>X.m() 的返回值：X 是类（静态方法）或 import 进来的名字（命名空间导入时是模块里的函数 m）。</summary>
    internal sealed record StaticReturnShape(string Owner, string Method) : ValueShape;
    /// <summary>ns.a.b() 的结果，ns 是 import 进来的名字：模块在仓库外（npm 包）就是外部对象，在仓库里推不出。</summary>
    internal sealed record PackageCallShape(string Local) : ValueShape;
    /// <summary>就是某个 import 进来的名字（const svc = importedSvc）。</summary>
    internal sealed record AliasShape(string Local) : ValueShape;

    public const string ReturnsVia = HeuristicEdgeResolver.ScriptReturnsVia;
    public const string CallbackVia = HeuristicEdgeResolver.ScriptCallbackVia;

    /// <summary>不声明就能用的全局对象：它们的方法（Promise.resolve、JSON.parse、console.log、document.querySelector）不是仓库方法。</summary>
    internal static readonly HashSet<string> GlobalValueNames = new(StringComparer.Ordinal)
    {
        "Promise", "JSON", "Object", "Array", "Math", "console", "Number", "String", "Boolean", "Date", "Reflect", "Symbol", "BigInt", "Intl",
        "Map", "Set", "WeakMap", "WeakSet", "RegExp", "Error", "URL", "URLSearchParams", "Atomics", "WebAssembly", "Proxy",
        "window", "document", "navigator", "localStorage", "sessionStorage", "location", "history", "globalThis", "self", "screen",
        "process", "Buffer", "crypto", "performance", "Notification", "caches", "indexedDB", "customElements",
        "fetch", "setTimeout", "setInterval", "clearTimeout", "clearInterval", "requestAnimationFrame", "cancelAnimationFrame", "queueMicrotask",
        "structuredClone", "parseInt", "parseFloat", "isNaN", "isFinite", "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
        "atob", "btoa", "alert", "confirm", "prompt", "getComputedStyle", "matchMedia",
    };

    /// <summary>
    /// 本文件没有声明、也没有 import 的这些类型名是语言 / DOM / Node 内建的：标注成它们的变量不连仓库方法。
    /// （仓库里自己声明了同名类型时以声明为准，见 <see cref="BindingFor"/>。）
    /// </summary>
    internal static readonly HashSet<string> BuiltinTypeNames = new(StringComparer.Ordinal)
    {
        "Set", "Map", "WeakMap", "WeakSet", "ReadonlySet", "ReadonlyMap", "Array", "ReadonlyArray", "Promise", "PromiseLike", "Record",
        "String", "Number", "Boolean", "Object", "Function", "Date", "RegExp", "Error", "TypeError", "RangeError", "Symbol", "BigInt",
        "URL", "URLSearchParams", "Headers", "Request", "Response", "FormData", "Blob", "File", "FileList", "ArrayBuffer", "DataView",
        "Uint8Array", "Int8Array", "Uint16Array", "Int16Array", "Uint32Array", "Int32Array", "Float32Array", "Float64Array",
        "AbortController", "AbortSignal", "TextEncoder", "TextDecoder", "Worker", "WebSocket", "EventSource", "MessagePort", "MessageChannel",
        "Element", "HTMLElement", "HTMLInputElement", "HTMLDivElement", "HTMLCanvasElement", "HTMLTextAreaElement", "HTMLSelectElement",
        "HTMLButtonElement", "HTMLFormElement", "HTMLAnchorElement", "HTMLImageElement", "SVGElement", "SVGSVGElement", "Node", "Document",
        "Window", "Event", "MouseEvent", "KeyboardEvent", "PointerEvent", "WheelEvent", "FocusEvent", "InputEvent", "CustomEvent", "DragEvent",
        "EventTarget", "DOMRect", "Range", "Selection", "Storage", "ResizeObserver", "MutationObserver", "IntersectionObserver",
        "CanvasRenderingContext2D", "Iterator", "IterableIterator", "AsyncIterator", "AsyncIterableIterator", "Generator", "AsyncGenerator",
        "Buffer", "NodeJS", "ReturnType", "Awaited",
    };

    /// <summary>返回数组 / 字符串的内建方法：const parts = s.split(",") 的 parts 是内建对象。</summary>
    private static readonly HashSet<string> BuiltinResultMethods = new(StringComparer.Ordinal)
    {
        "map", "filter", "slice", "concat", "split", "flat", "flatMap", "join", "trim", "trimStart", "trimEnd", "replace", "replaceAll",
        "toLowerCase", "toUpperCase", "padStart", "padEnd", "toFixed", "toString", "keys", "values", "entries", "substring", "toSorted",
        "toReversed", "localeCompare", "charAt", "at",
    };

    /// <summary>形态推断用的本文件状态：作用域里的声明表（按作用域节点缓存）、本文件声明的类型名。</summary>
    internal sealed class ShapeState(Dictionary<string, (string Source, string Imported)> byLocal)
    {
        public Dictionary<string, (string Source, string Imported)> ByLocal { get; } = byLocal;
        public HashSet<string> DeclaredTypes { get; } = new(StringComparer.Ordinal);
        /// <summary>本文件的类型别名：名字 → 右边的类型节点。</summary>
        public Dictionary<string, Node> TypeAliases { get; } = new(StringComparer.Ordinal);
        public Dictionary<(int, int), Dictionary<string, Node?>> Scopes { get; } = [];
        public Dictionary<int, Dictionary<string, ValueShape?>> ClassFields { get; } = [];
    }

    private static readonly HashSet<string> FunctionNodeTypes = new(StringComparer.Ordinal)
    {
        "arrow_function", "function_declaration", "function_expression", "function", "generator_function", "generator_function_declaration", "method_definition",
    };

    /// <summary>
    /// name 在 use 处可见的声明：从 use 往上逐层看块 / 程序里的声明、函数参数、for 变量、catch 参数。
    /// 找到返回声明节点（variable_declarator、参数、function_declaration、class_declaration；解构 / for / catch 这类推不出类型的给 null），
    /// Found = false 表示整个文件都没有这个名字的声明（全局或 import）。
    /// </summary>
    private static (bool Found, Node? Decl) FindDeclaration(Node use, string name, ShapeState st)
    {
        for (var p = use.Parent; p is not null; p = p.Parent)
        {
            var table = ScopeTable(p, st);
            if (table is not null && table.TryGetValue(name, out var decl)) return (true, decl);
        }
        return (false, null);
    }

    /// <summary>作用域节点 p 直接声明的名字 → 声明节点；p 不构成作用域返回 null。</summary>
    private static Dictionary<string, Node?>? ScopeTable(Node p, ShapeState st)
    {
        var isBlock = p.Type is "program" or "statement_block" or "class_static_block" or "switch_case" or "switch_default";
        var isFunction = FunctionNodeTypes.Contains(p.Type);
        var isLoopOrCatch = p.Type is "for_in_statement" or "for_statement" or "catch_clause";
        if (!isBlock && !isFunction && !isLoopOrCatch) return null;
        var key = (p.StartIndex, p.EndIndex);
        if (st.Scopes.TryGetValue(key, out var cached)) return cached;
        var table = new Dictionary<string, Node?>(StringComparer.Ordinal);
        if (isBlock)
        {
            foreach (var stmt in p.NamedChildren) AddStatement(stmt, table);
        }
        else if (isFunction)
        {
            if (p.GetChildForField("parameter") is { Type: "identifier" } single) table.TryAdd(single.Text, null); // x => …
            if (p.GetChildForField("parameters") is { } parameters)
                foreach (var param in parameters.NamedChildren) AddParameter(param, table);
        }
        else if (p.Type == "for_in_statement")
        {
            if (p.GetChildForField("left") is { } left) AddPatternNames(left, table, null);
        }
        else if (p.Type == "for_statement")
        {
            if (p.GetChildForField("initializer") is { } init) AddStatement(init, table);
        }
        else if (p.GetChildForField("parameter") is { } caught) AddPatternNames(caught, table, null);
        st.Scopes[key] = table;
        return table;
    }

    private static void AddStatement(Node stmt, Dictionary<string, Node?> table)
    {
        if (stmt.Type == "export_statement" && stmt.GetChildForField("declaration") is { } inner) stmt = inner;
        switch (stmt.Type)
        {
            case "lexical_declaration" or "variable_declaration":
                foreach (var d in stmt.NamedChildren)
                {
                    if (d.Type != "variable_declarator" || d.GetChildForField("name") is not { } n) continue;
                    if (n.Type == "identifier") table.TryAdd(n.Text, d);
                    else AddPatternNames(n, table, IsRequireDeclarator(d) ? d : null); // 解构：推不出（const { f } = require('m') 记声明子，认得出是 import）
                }
                break;
            case "function_declaration" or "generator_function_declaration" or "class_declaration" or "abstract_class_declaration":
                if (stmt.GetChildForField("name") is { } fn) table.TryAdd(fn.Text, stmt);
                break;
        }
    }

    private static void AddParameter(Node param, Dictionary<string, Node?> table)
    {
        switch (param.Type)
        {
            case "required_parameter" or "optional_parameter":
                if (param.GetChildForField("pattern") is { Type: "identifier" } id) table.TryAdd(id.Text, param);
                else if (param.GetChildForField("pattern") is { } pattern) AddPatternNames(pattern, table, null);
                break;
            case "identifier":
                table.TryAdd(param.Text, null); // JS 参数没有类型
                break;
            case "assignment_pattern" when param.GetChildForField("left") is { Type: "identifier" } left:
                table.TryAdd(left.Text, param); // x = new Set()
                break;
            default:
                AddPatternNames(param, table, null);
                break;
        }
    }

    /// <summary>解构 / 剩余参数 / for 变量里出现的全部标识符都记成"声明了、推不出"。</summary>
    private static void AddPatternNames(Node pattern, Dictionary<string, Node?> table, Node? decl, int depth = 0)
    {
        if (depth > 16) return;
        switch (pattern.Type)
        {
            case "identifier" or "shorthand_property_identifier_pattern":
                table.TryAdd(pattern.Text, decl);
                return;
            case "pair_pattern":
                if (pattern.GetChildForField("value") is { } v) AddPatternNames(v, table, decl, depth + 1);
                return;
            case "type_annotation" or "property_identifier" or "string" or "number":
                return;
        }
        foreach (var c in pattern.NamedChildren)
        {
            if (pattern.Type == "variable_declarator" && c == pattern.GetChildForField("value")) continue;
            if (pattern.Type is "assignment_pattern" or "object_assignment_pattern" && c == pattern.GetChildForField("right")) continue;
            AddPatternNames(c, table, decl, depth + 1);
        }
    }

    /// <summary>标识符 name（在 use 处）的形态；import 进来的名字是 <see cref="AliasShape"/>，本文件全局对象是 <see cref="BuiltinShape"/>。</summary>
    private static ValueShape? ShapeOfName(Node use, string name, ShapeState st, int depth)
    {
        var (found, decl) = FindDeclaration(use, name, st);
        if (!found)
        {
            if (st.ByLocal.ContainsKey(name)) return new AliasShape(name);
            return GlobalValueNames.Contains(name) ? new BuiltinShape(name) : null;
        }
        if (decl is null || depth > 6) return null;
        if (IsRequireDeclarator(decl)) return st.ByLocal.ContainsKey(name) ? new AliasShape(name) : null; // const x = require('m')
        switch (decl.Type)
        {
            case "class_declaration" or "abstract_class_declaration":
                return new TypeShape(name); // X.staticMethod()
            case "variable_declarator" or "required_parameter" or "optional_parameter" or "assignment_pattern":
            {
                if (decl.GetChildForField("type") is { } annotation) return ShapeOfType(annotation, st);
                var value = decl.GetChildForField(decl.Type == "assignment_pattern" ? "right" : "value");
                return value is null ? null : ShapeOfExpr(value, st, depth + 1);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 类型节点（含 type_annotation 外壳）的形态；Promise&lt;T&gt; / Readonly&lt;T&gt; 取 T，T | undefined 取 T，
    /// ReturnType&lt;typeof f&gt; 是 f 的返回值，本文件的类型别名（type Api = ReturnType&lt;typeof useApi&gt;）展开一层再看。
    /// </summary>
    internal static ValueShape? ShapeOfType(Node? type, ShapeState st, int depth = 0)
    {
        if (type is null || depth > 8) return null;
        if (type.Type is "type_annotation" or "parenthesized_type") return ShapeOfType(type.NamedChildren.FirstOrDefault(), st, depth + 1);
        switch (type.Type)
        {
            case "type_identifier" when st.TypeAliases.TryGetValue(type.Text, out var aliased) && !st.ByLocal.ContainsKey(type.Text):
                return ShapeOfType(aliased, st, depth + 1) ?? new TypeShape(type.Text);
            case "type_identifier" or "nested_type_identifier":
                return new TypeShape(type.Text.Replace(" ", ""));
            case "generic_type":
            {
                var name = type.GetChildForField("name");
                var firstArg = type.GetChildForField("type_arguments")?.NamedChildren.FirstOrDefault();
                if (name is { Type: "type_identifier", Text: "ReturnType" } && firstArg is { Type: "type_query" }
                    && firstArg.NamedChildren.FirstOrDefault() is { Type: "identifier" } queried)
                    return new ReturnShape(queried.Text);
                if (name is { Type: "type_identifier" } && name.Text is "Promise" or "PromiseLike" or "Readonly" or "Partial" or "Required" or "NonNullable" or "Awaited")
                    return ShapeOfType(firstArg, st, depth + 1);
                return name is { Type: "type_identifier" or "nested_type_identifier" } ? new TypeShape(name.Text.Replace(" ", "")) : null;
            }
            case "predefined_type":
                return type.Text is "any" or "unknown" or "never" or "object" ? null : new BuiltinShape(type.Text);
            case "array_type" or "tuple_type" or "readonly_type":
                return new BuiltinShape("Array");
            case "function_type" or "constructor_type":
                return new BuiltinShape("Function");
            case "literal_type" or "template_literal_type":
                return new BuiltinShape("literal");
            case "union_type":
            {
                var members = new List<Node>();
                FlattenUnion(type, members, 0);
                var rest = members.Where(m => !IsNullish(m)).ToList();
                return rest.Count == 1 ? ShapeOfType(rest[0], st, depth + 1) : null;
            }
            default:
                return null;
        }
    }

    private static void FlattenUnion(Node union, List<Node> into, int depth)
    {
        foreach (var c in union.NamedChildren)
            if (c.Type == "union_type" && depth < 16) FlattenUnion(c, into, depth + 1);
            else into.Add(c);
    }

    private static bool IsNullish(Node type) =>
        type.Text is "undefined" or "null" or "void" && type.Type is "literal_type" or "predefined_type" or "undefined" or "null";

    /// <summary>表达式的值的形态。</summary>
    private static ValueShape? ShapeOfExpr(Node expr, ShapeState st, int depth)
    {
        if (depth > 8) return null;
        switch (expr.Type)
        {
            case "new_expression":
            {
                var ctor = expr.GetChildForField("constructor");
                return ctor?.Type switch
                {
                    "identifier" => new TypeShape(ctor.Text),
                    "member_expression" when ctor.GetChildForField("object") is { Type: "identifier" } => new TypeShape(ctor.Text.Replace(" ", "")),
                    _ => null,
                };
            }
            case "call_expression":
                return ShapeOfCall(expr, st, depth);
            case "await_expression" or "parenthesized_expression" or "non_null_expression" or "satisfies_expression":
                return expr.NamedChildren.FirstOrDefault() is { } inner ? ShapeOfExpr(inner, st, depth + 1) : null;
            case "as_expression":
                return ShapeOfType(expr.NamedChildren.Skip(1).FirstOrDefault(), st) is { } asType ? asType
                    : expr.NamedChildren.FirstOrDefault() is { } asInner ? ShapeOfExpr(asInner, st, depth + 1) : null;
            case "array":
                return new BuiltinShape("Array");
            case "string" or "template_string":
                return new BuiltinShape("String");
            case "number":
                return new BuiltinShape("Number");
            case "regex":
                return new BuiltinShape("RegExp");
            case "true" or "false":
                return new BuiltinShape("Boolean");
            case "object" when !expr.NamedChildren.Any():
                return new BuiltinShape("Object");
            case "arrow_function" or "function_expression" or "function":
                return new BuiltinShape("Function");
            case "identifier":
                return ShapeOfName(expr, expr.Text, st, depth + 1);
            case "binary_expression" when expr.GetChildForField("operator")?.Type is "??" or "||":
                return (expr.GetChildForField("left") is { } l ? ShapeOfExpr(l, st, depth + 1) : null)
                       ?? (expr.GetChildForField("right") is { } r ? ShapeOfExpr(r, st, depth + 1) : null);
            default:
                return null;
        }
    }

    private static ValueShape? ShapeOfCall(Node call, ShapeState st, int depth)
    {
        var fn = call.GetChildForField("function");
        if (fn is { Type: "await_expression" }) fn = fn.NamedChildren.FirstOrDefault(); // await f<T>()
        switch (fn?.Type)
        {
            case "identifier":
            {
                var name = fn.Text;
                // useMemo(() => x, deps)：值就是 x
                if (name == "useMemo" && FirstArgument(call) is { } factory && FunctionNodeTypes.Contains(factory.Type))
                    return ShapeOfFunction(factory, st, depth + 1);
                if (name == "require") return null;
                if (st.ByLocal.ContainsKey(name)) return new ReturnShape(name);
                var (found, decl) = FindDeclaration(fn, name, st);
                if (!found) return GlobalValueNames.Contains(name) ? new BuiltinShape(name) : null;
                return decl is not null && IsFunctionDeclaration(decl) ? new ReturnShape(name) : null;
            }
            case "member_expression":
            {
                var property = fn.GetChildForField("property")?.Text;
                if (property == "useMemo" && FirstArgument(call) is { } factory && FunctionNodeTypes.Contains(factory.Type))
                    return ShapeOfFunction(factory, st, depth + 1); // React.useMemo(() => …)
                // X.m()：X 是类（本文件声明的 / import 进来的）→ 静态方法 m 的返回值（单例 getInstance()、工厂 create()）；
                // X 是命名空间导入 → 模块里的函数 m 的返回值
                if (property is not null && fn.GetChildForField("object") is { Type: "identifier" } owner)
                {
                    var (ownerFound, ownerDecl) = FindDeclaration(owner, owner.Text, st);
                    if (ownerFound && ownerDecl is { Type: "class_declaration" or "abstract_class_declaration" }) return new StaticReturnShape(owner.Text, property);
                    if (!ownerFound && st.ByLocal.ContainsKey(owner.Text) && !BuiltinResultMethods.Contains(property)) return new StaticReturnShape(owner.Text, property);
                }
                // 内建对象的方法返回的还是内建对象（map.set(k, v).get(k)、s.trim().split(",")）
                if (fn.GetChildForField("object") is { } receiverExpr && ShapeOfExpr(receiverExpr, st, depth + 1) is { } receiverShape
                    && BindingFor(receiverShape, st) is { ImportSource: HeuristicEdgeResolver.ScriptBuiltinMarker, ImportedName: var builtinName })
                    return new BuiltinShape(builtinName ?? "Object");
                var root = RootOf(fn);
                if (root is { Type: "new_expression" }) root = RootOf(root.GetChildForField("constructor")); // new sdk.Builder().withX().build()
                if (root is { Type: "identifier" })
                {
                    var (found, _) = FindDeclaration(root, root.Text, st);
                    if (!found && st.ByLocal.ContainsKey(root.Text)) return new PackageCallShape(root.Text);
                    if (!found && GlobalValueNames.Contains(root.Text)) return new BuiltinShape(root.Text); // Object.keys、JSON.parse、document.querySelector
                }
                return property is not null && BuiltinResultMethods.Contains(property) ? new BuiltinShape("Array") : null;
            }
            default:
                return null;
        }
    }

    private static Node? FirstArgument(Node call) => call.GetChildForField("arguments")?.NamedChildren.FirstOrDefault();

    /// <summary>a.b.c / a().b / a!.b / a[0].b 的最左端。</summary>
    private static Node? RootOf(Node? expr)
    {
        for (var i = 0; expr is not null && i < 32; i++)
        {
            var next = expr.Type switch
            {
                "member_expression" or "subscript_expression" => expr.GetChildForField("object"),
                "call_expression" => expr.GetChildForField("function"),
                "non_null_expression" or "parenthesized_expression" => expr.NamedChildren.FirstOrDefault(),
                _ => null,
            };
            if (next is null) return expr;
            expr = next;
        }
        return expr;
    }

    private static bool IsFunctionDeclaration(Node decl) =>
        decl.Type is "function_declaration" or "generator_function_declaration"
        || (decl.Type == "variable_declarator" && decl.GetChildForField("value") is { } v && v.Type is "arrow_function" or "function_expression" or "generator_function" or "function");

    /// <summary>
    /// 函数的返回值形态：返回类型标注优先；否则表达式体 / 全部 return 语句的形态（不进嵌套函数与类），
    /// 各处一致才算，有一处推不出就推不出。
    /// </summary>
    private static ValueShape? ShapeOfFunction(Node fn, ShapeState st, int depth)
    {
        if (depth > 8) return null;
        if (fn.GetChildForField("return_type") is { } annotated) return ShapeOfType(annotated, st);
        var body = fn.GetChildForField("body");
        if (body is null) return null;
        if (body.Type != "statement_block") return ShapeOfExpr(body, st, depth + 1);
        var returns = new List<Node>();
        CollectReturns(body, returns, 0);
        if (returns.Count == 0) return null;
        ValueShape? shape = null;
        foreach (var r in returns)
        {
            var value = r.NamedChildren.FirstOrDefault();
            var s = value is null ? null : ShapeOfExpr(value, st, depth + 1);
            if (s is null) return null;
            if (shape is null) shape = s;
            else if (shape != s) return null;
        }
        return shape;
    }

    private static void CollectReturns(Node node, List<Node> into, int depth)
    {
        if (depth > 64) return;
        foreach (var c in node.NamedChildren)
        {
            if (FunctionNodeTypes.Contains(c.Type) || c.Type is "class" or "class_declaration") continue;
            if (c.Type == "return_statement") into.Add(c);
            else CollectReturns(c, into, depth + 1);
        }
    }

    /// <summary>
    /// 形态 → 接收者绑定（null = 推不出，解析器按未知接收者处理）：
    /// 内建 → Other + <see cref="HeuristicEdgeResolver.ScriptBuiltinMarker"/>；类型 → Typed（import 的给 import，本文件的给名字）；
    /// 函数返回值 → Typed、导出名后缀 "()"（静态方法写成 "类.方法()"）；npm 包 API 的返回值 → Other + <see cref="HeuristicEdgeResolver.ScriptPackageMarker"/> 前缀的模块说明符。
    /// </summary>
    internal static ScriptBinding? BindingFor(ValueShape? shape, ShapeState st)
    {
        switch (shape)
        {
            case BuiltinShape b:
                return new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, b.Name);
            case TypeShape { Name: var name }:
            {
                var dot = name.IndexOf('.');
                if (dot > 0)
                {
                    var ns = name[..dot];
                    var last = name[(name.LastIndexOf('.') + 1)..];
                    return st.ByLocal.TryGetValue(ns, out var nb) ? new ScriptBinding(ScriptReceiver.Typed, nb.Source, last) : null;
                }
                if (st.ByLocal.TryGetValue(name, out var b)) return new ScriptBinding(ScriptReceiver.Typed, b.Source, b.Imported);
                if (!st.DeclaredTypes.Contains(name) && BuiltinTypeNames.Contains(name))
                    return new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, name);
                return new ScriptBinding(ScriptReceiver.Typed, null, name);
            }
            case ReturnShape { Function: var f }:
                return st.ByLocal.TryGetValue(f, out var fb)
                    ? new ScriptBinding(ScriptReceiver.Typed, fb.Source, fb.Imported + HeuristicEdgeResolver.ScriptReturnSuffix)
                    : new ScriptBinding(ScriptReceiver.Typed, null, f + HeuristicEdgeResolver.ScriptReturnSuffix);
            case StaticReturnShape { Owner: var owner, Method: var method }:
                // 导出名写成 "类.方法()"；命名空间导入直接是 "函数()"
                if (!st.ByLocal.TryGetValue(owner, out var sb)) return new ScriptBinding(ScriptReceiver.Typed, null, owner + "." + method + HeuristicEdgeResolver.ScriptReturnSuffix);
                return new ScriptBinding(ScriptReceiver.Typed, sb.Source, (sb.Imported == "*" ? "" : sb.Imported + ".") + method + HeuristicEdgeResolver.ScriptReturnSuffix);
            case PackageCallShape { Local: var local } when st.ByLocal.TryGetValue(local, out var pb):
                return new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptPackageMarker + pb.Source, null);
            case AliasShape { Local: var alias } when st.ByLocal.TryGetValue(alias, out var ab):
                return new ScriptBinding(ScriptReceiver.Import, ab.Source, ab.Imported);
            default:
                return null;
        }
    }

    /// <summary>接收者表达式（x.y() 的 x）的绑定；推不出返回 null。</summary>
    private static ScriptBinding? ReceiverBinding(Node receiver, ImportBindings bindings)
    {
        var st = bindings.Shapes;
        switch (receiver.Type)
        {
            case "identifier":
                return BindingFor(ShapeOfName(receiver, receiver.Text, st, 0), st);
            case "non_null_expression" or "parenthesized_expression" when receiver.NamedChildren.FirstOrDefault() is { } inner:
                return inner.Type == "identifier" && bindings.ByLocal.TryGetValue(inner.Text, out var b)
                    ? new ScriptBinding(ScriptReceiver.Import, b.Source, b.Imported)
                    : inner.Type is "identifier" or "call_expression" or "new_expression" or "as_expression" ? ReceiverBinding(inner, bindings)
                    : inner.Type == "member_expression" ? FieldBinding(inner, bindings) : null;
            case "call_expression" or "new_expression" or "as_expression" or "array" or "string" or "template_string" or "await_expression":
                return BindingFor(ShapeOfExpr(receiver, st, 0), st);
            default:
                return null;
        }
    }

    // ---------- 同一行同名的成员调用 ----------

    /// <summary>
    /// 引用按（所在符号, 名字, 行）去重，同一行的 Supported.has(x) &amp;&amp; servers.has(y) 只留第一个；
    /// 接收者推出来不一样时（一个是内建 Set、一个是 ServerManager）后面那个也要，不然 ServerManager.has 少一个调用方。
    /// 绑定相同的不重复记。
    /// </summary>
    private void CollectSameLineMemberCalls(Node root, ParsedFile file, ImportBindings bindings, List<ParsedReference> into)
    {
        var byKey = file.References.Where(r => r.Kind == EdgeKind.Calls && r.Via is null)
            .GroupBy(r => (r.FromIndex, r.Name, r.Line))
            .ToDictionary(g => g.Key, g => g.ToList());
        if (byKey.Count == 0) return;
        var ranges = file.Symbols.Select(s => (Start: (s.StartLine, s.StartCol), End: (s.EndLine, s.EndCol))).ToList();
        Walk(root, 0);

        void Walk(Node node, int depth)
        {
            if (depth > 512) return;
            foreach (var c in node.NamedChildren)
            {
                if (c.Type == "call_expression") Visit(c);
                if (c.Type is "string" or "template_string" or "comment") continue;
                Walk(c, depth + 1);
            }
        }

        void Visit(Node call)
        {
            var fn = call.GetChildForField("function");
            if (fn is { Type: "await_expression" }) fn = fn.NamedChildren.FirstOrDefault();
            if (fn is not { Type: "member_expression" } || fn.GetChildForField("property") is not { Type: "property_identifier" or "private_property_identifier" } name) return;
            var line = name.StartPosition.Row + 1;
            var from = EnclosingIndex(ranges, (line, name.StartPosition.Column + 1));
            if (!byKey.TryGetValue((from, name.Text, line), out var same) || same.Any(r => r.Pos == name.StartIndex)) return;
            var annotated = AnnotateReference(new ParsedReference(from, name.Text, EdgeKind.Calls, line, name.StartPosition.Column + 1,
                Context: CallContextOf(name), Pos: name.StartIndex), "calls", name, bindings);
            if (same.Any(r => r.Script == annotated.Script)) return;
            same.Add(annotated);
            into.Add(annotated);
        }
    }

    private static int EnclosingIndex(List<((int Line, int Col) Start, (int Line, int Col) End)> ranges, (int Line, int Col) pos)
    {
        static int Compare((int Line, int Col) a, (int Line, int Col) b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Col.CompareTo(b.Col);
        var best = 0;
        for (var i = 1; i < ranges.Count; i++)
        {
            if (Compare(ranges[i].Start, pos) > 0) break;
            if (Compare(pos, ranges[i].End) < 0) best = i;
        }
        return best;
    }

    // ---------- 回调：作为实参 / 对象属性值传出去的函数引用 ----------

    /// <summary>
    /// 作为实参（stream.on("data", onData)、setTimeout(tick)）或实参对象的属性值（useMutation({ mutationFn: login })）传出去的
    /// 函数引用：记一条 via = "callback"、ctx = callback 的 calls 引用。只认标识符与 this.x；本文件里声明成参数 / 非函数变量 / 类的不算，
    /// 解析器只保留连到仓库函数 / 方法的边。数组元素不算（useEffect 的依赖数组）。
    /// </summary>
    private void CollectCallbackReferences(Node root, ParsedFile file, ImportBindings bindings, List<ParsedReference> into)
    {
        var existing = file.References.Select(r => (r.FromIndex, r.Name, r.Kind, r.Line)).ToHashSet();
        var ranges = file.Symbols.Select(s => (Start: (s.StartLine, s.StartCol), End: (s.EndLine, s.EndCol))).ToList();
        Walk(root, 0);

        void Walk(Node node, int depth)
        {
            if (depth > 512) return;
            foreach (var c in node.NamedChildren)
            {
                if (c.Type == "arguments" && node.Type is "call_expression" or "new_expression" && !IsSkippedCall(node))
                    foreach (var arg in c.NamedChildren) Passed(arg, 0);
                if (c.Type is "string" or "template_string" or "comment") continue;
                Walk(c, depth + 1);
            }
        }

        void Passed(Node value, int level)
        {
            switch (value.Type)
            {
                case "identifier":
                    Add(value, value.Text, IsPassableFunction(value, bindings) ? NameBinding(value, bindings) : null);
                    break;
                case "member_expression" when value.GetChildForField("object") is { Type: "this" } && value.GetChildForField("property") is { Type: "property_identifier" or "private_property_identifier" } prop:
                    Add(prop, prop.Text, ThisBinding(prop, bindings));
                    break;
                case "object" when level < 3:
                    foreach (var entry in value.NamedChildren)
                        if (entry.Type == "pair" && entry.GetChildForField("value") is { } v) Passed(v, level + 1);
                    break;
            }
        }

        void Add(Node nameNode, string name, ScriptBinding? binding)
        {
            if (binding is null) return;
            var line = nameNode.StartPosition.Row + 1;
            var from = EnclosingIndex(ranges, (line, nameNode.StartPosition.Column + 1));
            if (!existing.Add((from, name, EdgeKind.Calls, line))) return;
            into.Add(new ParsedReference(from, name, EdgeKind.Calls, line, nameNode.StartPosition.Column + 1, CallbackVia, binding, CallContext.Callback, nameNode.StartIndex));
        }
    }

    /// <summary>
    /// 不看实参的调用：require、memo / forwardRef / observer 包装（渲染边另记）、bind / call / apply，
    /// 以及摘掉监听的 off / removeListener / removeEventListener（传进去的处理函数不会被调用）。
    /// </summary>
    private static bool IsSkippedCall(Node call)
    {
        var fn = call.GetChildForField("function") ?? call.GetChildForField("constructor");
        var name = fn?.Type == "member_expression" ? fn.GetChildForField("property")?.Text : fn?.Text;
        return name is "require" or "memo" or "forwardRef" or "observer" or "bind" or "call" or "apply"
            or "off" or "removeListener" or "removeEventListener" or "removeAllListeners" or "unsubscribe" or "unlisten";
    }

    /// <summary>传出去的标识符可能是函数：import 进来的，或本文件里声明成函数的；参数、非函数变量、类、全局名字都不算。</summary>
    private static bool IsPassableFunction(Node id, ImportBindings bindings)
    {
        if (IsImportedName(id, id.Text, bindings.Shapes)) return true;
        var (found, decl) = FindDeclaration(id, id.Text, bindings.Shapes);
        return found && decl is not null && IsFunctionDeclaration(decl);
    }

    private static ScriptBinding NameBinding(Node id, ImportBindings bindings) =>
        IsImportedName(id, id.Text, bindings.Shapes) && bindings.ByLocal.TryGetValue(id.Text, out var own)
            ? new ScriptBinding(ScriptReceiver.None, own.Source, own.Imported)
            : new ScriptBinding(ScriptReceiver.None);

    /// <summary>const x = require('m') / const { f } = require('m') / const g = require('m').g 的声明子。</summary>
    private static bool IsRequireDeclarator(Node? decl) =>
        decl is { Type: "variable_declarator" } && decl.GetChildForField("value") is { } v
        && (RequireSource(v) is not null || (v.Type == "member_expression" && RequireSource(v.GetChildForField("object")) is not null));

    /// <summary>name 在 use 处指的是 import / require 进来的名字（没被本地声明遮住；require 的声明子本身就是 import）。</summary>
    internal static bool IsImportedName(Node use, string name, ShapeState st)
    {
        if (!st.ByLocal.ContainsKey(name)) return false;
        var (found, decl) = FindDeclaration(use, name, st);
        return !found || IsRequireDeclarator(decl);
    }

    // ---------- 返回值提示 ----------

    /// <summary>
    /// 每个函数 / 方法的返回值形态记一条 via = "returns" 的 references 引用（名字是类型名 / 被调函数名，绑定见 <see cref="HintBinding"/>），
    /// 解析器解析成"函数 → 返回的类"的边存进库（增量时没重新解析的文件靠库里的这条边）。推不出的不记。
    /// </summary>
    private static void CollectReturnHints(Node root, ParsedFile file, ImportBindings bindings, List<ParsedReference> into)
    {
        Walk(root, 0);

        void Walk(Node node, int depth)
        {
            if (depth > 512) return;
            foreach (var c in node.NamedChildren)
            {
                if (FunctionNodeTypes.Contains(c.Type) && SymbolIndexOf(c, file.Symbols) is var index and > 0
                    && ShapeOfFunction(c, bindings.Shapes, 0) is { } shape && HintFor(shape, bindings.Shapes) is { } hint)
                {
                    var line = c.StartPosition.Row + 1;
                    into.Add(new ParsedReference(index, hint.Name, EdgeKind.References, line, c.StartPosition.Column + 1, ReturnsVia, hint.Binding));
                }
                if (c.Type is "string" or "template_string" or "comment" or "type_annotation") continue;
                Walk(c, depth + 1);
            }
        }
    }

    /// <summary>函数节点对应的符号下标：函数声明 / 方法是它自己，const f = () => … 是声明子，类字段是字段定义。没有返回 -1。</summary>
    private static int SymbolIndexOf(Node fn, IReadOnlyList<ParsedSymbol> symbols)
    {
        var def = fn.Type switch
        {
            "function_declaration" or "generator_function_declaration" or "method_definition" => fn,
            _ => fn.Parent?.Type switch
            {
                "variable_declarator" when IsField(fn.Parent, "value", fn) => fn.Parent,
                "public_field_definition" or "field_definition" => fn.Parent,
                "export_statement" => fn,
                _ => null,
            },
        };
        if (def is null) return -1;
        var line = def.StartPosition.Row + 1;
        var col = def.StartPosition.Column + 1;
        for (var i = 1; i < symbols.Count; i++)
            if (symbols[i].StartLine == line && symbols[i].StartCol == col && symbols[i].Kind is SymbolKind.Function or SymbolKind.Method) return i;
        return -1;
    }

    /// <summary>返回值形态 → 提示引用的名字与绑定：类型 None + import / 本文件；函数返回值 None、导出名后缀 "()"；内建 / npm 包同接收者的标记。</summary>
    private static (string Name, ScriptBinding Binding)? HintFor(ValueShape shape, ShapeState st)
    {
        switch (shape)
        {
            case BuiltinShape b:
                return (b.Name, new ScriptBinding(ScriptReceiver.Other, HeuristicEdgeResolver.ScriptBuiltinMarker, b.Name));
            case TypeShape t:
            {
                var binding = BindingFor(t, st);
                return binding switch
                {
                    null => null,
                    { Receiver: ScriptReceiver.Other } => (t.Name, binding),
                    _ => (t.Name[(t.Name.LastIndexOf('.') + 1)..], binding with { Receiver = ScriptReceiver.None }),
                };
            }
            case ReturnShape r:
                return BindingFor(r, st) is { } rb ? (r.Function, rb with { Receiver = ScriptReceiver.None }) : null;
            case StaticReturnShape s:
                return BindingFor(s, st) is { } sb ? (s.Method, sb with { Receiver = ScriptReceiver.None }) : null;
            case PackageCallShape p:
                return BindingFor(p, st) is { } pb ? (p.Local, pb) : null;
            default:
                return null;
        }
    }

    /// <summary>本文件声明的类型名（类、接口、类型别名、枚举；内建类型名被本文件同名声明遮住时以声明为准）与类型别名的右边。</summary>
    private static void CollectDeclaredTypes(Node node, ShapeState st, int depth)
    {
        if (depth > 6) return;
        foreach (var c in node.NamedChildren)
        {
            if (c.Type is "class_declaration" or "abstract_class_declaration" or "interface_declaration" or "type_alias_declaration" or "enum_declaration"
                && c.GetChildForField("name") is { } n)
            {
                st.DeclaredTypes.Add(n.Text);
                if (c.Type == "type_alias_declaration" && c.GetChildForField("value") is { } value) st.TypeAliases.TryAdd(n.Text, value);
            }
            if (c.Type is "export_statement" or "internal_module" or "module" or "statement_block" or "ambient_declaration")
                CollectDeclaredTypes(c, st, depth + 1);
        }
    }
}
