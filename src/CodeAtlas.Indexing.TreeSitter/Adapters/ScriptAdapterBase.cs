using CodeAtlas.Core.Model;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// TS / TSX / JS 共用：可见性（export / export default，CommonJS 的 module.exports / exports.f）、默认导出的标记、
/// import 与 require 绑定表、类的带类型成员与父类，以及给每条引用补上 <see cref="ScriptBinding"/>（名字或接收者来自哪条 import、
/// 接收者是什么类型），解析器据此按模块解析而不是按名字猜。JSX 的 on 开头属性里传出去的处理函数标 via = "event"。
/// </summary>
public abstract class ScriptAdapterBase(string languageId, string grammarName, IReadOnlyList<string> extensions, string definitionsQuery, string referencesQuery)
    : LanguageAdapterBase(languageId, grammarName, extensions, definitionsQuery, referencesQuery)
{
    public const string ExportDefault = "export default";
    public const string EventVia = "event";

    /// <summary>
    /// 本文件的绑定：本地名 → (模块说明符, 导出名)，导出名是 "default"、"*" 或具名导出的原名；
    /// Classes 按类节点起点索引：带类型的成员（字段 / 构造参数属性 → 类型名）与父类名。
    /// </summary>
    internal sealed record ImportBindings(Dictionary<string, (string Source, string Imported)> ByLocal, Dictionary<int, ClassInfo> Classes);

    /// <param name="FieldTypes">this.x 的 x → 类型名（本文件里写的名字，可能是 import 的别名）。</param>
    /// <param name="BaseName">extends 的父类名（标识符）；没有或是成员表达式（React.Component）为 null。</param>
    internal sealed record ClassInfo(Dictionary<string, string> FieldTypes, string? BaseName);

    protected override string? ExtractVisibility(Node definition) => ScriptVisibility(definition);

    /// <summary>accessibility_modifier（public/private/protected）优先；否则定义（或其所在声明）直接被 export_statement 包裹则为 "export"，带 default 的为 "export default"。</summary>
    internal static string? ScriptVisibility(Node definition)
    {
        foreach (var child in definition.Children)
            if (child.Type == "accessibility_modifier") return child.Text;
        var parent = definition.Parent;
        if (parent is not null && parent.Type == "export_statement") return IsDefaultExport(parent) ? ExportDefault : "export";
        if (parent is not null && parent.Parent is { } grand && grand.Type == "export_statement") return IsDefaultExport(grand) ? ExportDefault : "export";
        return null;
    }

    private static bool IsDefaultExport(Node exportStatement)
    {
        foreach (var c in exportStatement.Children)
            if (c.Type == "default") return true;
        return false;
    }

    /// <summary>
    /// JSX 里只有首字母大写的标签是组件（&lt;div&gt; 是宿主元素）；成员式 &lt;X.Y&gt; 一律算组件。
    /// require('m') 这个调用本身不记（它是 import，见引用查询）。
    /// </summary>
    protected override bool AcceptReference(string captureName, Node node)
    {
        if (captureName == "calls") return !IsRequireCallee(node);
        if (captureName != "render") return true;
        if (node.Parent is { Type: "member_expression" }) return true;
        return node.Text.Length > 0 && char.IsUpper(node.Text[0]);
    }

    private static bool IsRequireCallee(Node node) =>
        node.Type == "identifier" && node.Text == "require" && node.Parent is { Type: "call_expression" } call && RequireSource(call) is not null;

    /// <summary>require('m') 调用的模块说明符；不是这种形态返回 null。</summary>
    private static string? RequireSource(Node? node)
    {
        if (node is null || node.Type != "call_expression") return null;
        if (node.GetChildForField("function") is not { Type: "identifier", Text: "require" }) return null;
        var first = node.GetChildForField("arguments")?.NamedChildren.FirstOrDefault();
        return first is { Type: "string" } ? StringValue(first) : null;
    }

    /// <summary>
    /// 定义收齐后的修正：
    /// `export default X;` / `export default memo(X)` / `export default connect(...)(X)` 与 CommonJS 的 `module.exports = X / new X()`：把顶层的 X 标成默认导出；
    /// `module.exports = { f, g }`、`exports.f = …`、`module.exports.f = …`：标成具名导出；
    /// `export default memo(() => …)` 这类匿名包装组件改用文件名命名；类里 `handle = () => {}` 这类箭头函数字段改记为方法；
    /// `const Root = styled(…)(…)` 不是组件，改记为变量。
    /// </summary>
    protected override void AdjustSymbols(Node root, List<ParsedSymbol> symbols)
    {
        foreach (var top in root.NamedChildren)
        {
            var stmt = top;
            if (stmt.Type == "export_statement")
            {
                var value = stmt.GetChildForField("value");
                if (value is not null && IsDefaultExport(stmt))
                {
                    MarkTopLevel(symbols, CandidateNames(value), ExportDefault);
                    if (value.Type == "call_expression") RenameWrappedDefault(symbols, value);
                }
                // export class X / export const X = …：往里看声明
                stmt = stmt.GetChildForField("declaration") ?? stmt;
            }
            switch (stmt.Type)
            {
                case "class_declaration" or "abstract_class_declaration":
                    ArrowFieldsAsMethods(stmt, symbols);
                    break;
                case "lexical_declaration" or "variable_declaration":
                    foreach (var declarator in stmt.NamedChildren)
                        if (declarator.Type == "variable_declarator" && IsStyledCall(declarator.GetChildForField("value")))
                            SetKind(symbols, declarator, SymbolKind.Function, SymbolKind.Variable);
                    break;
                case "expression_statement" when stmt.NamedChildren.FirstOrDefault() is { Type: "assignment_expression" } assignment:
                    MarkCommonJsExport(assignment, symbols);
                    break;
            }
        }
    }

    private static void MarkCommonJsExport(Node assignment, List<ParsedSymbol> symbols)
    {
        var left = assignment.GetChildForField("left");
        var right = assignment.GetChildForField("right");
        if (left is null || right is null) return;
        if (IsModuleExports(left))
        {
            switch (right.Type)
            {
                case "function_expression" or "arrow_function" or "class":
                    SetVisibility(symbols, FindAt(symbols, right), ExportDefault);
                    if (right.Type == "class") ArrowFieldsAsMethods(right, symbols);
                    break;
                case "object":
                    foreach (var entry in right.NamedChildren)
                    {
                        switch (entry.Type)
                        {
                            case "shorthand_property_identifier":
                                MarkTopLevel(symbols, [entry.Text], "export");
                                break;
                            case "method_definition":
                                SetVisibility(symbols, FindAt(symbols, entry), "export");
                                break;
                            case "pair" when entry.GetChildForField("value") is { } v:
                                if (v.Type == "identifier") MarkTopLevel(symbols, [v.Text], "export");
                                else SetVisibility(symbols, FindAt(symbols, entry), "export");
                                break;
                        }
                    }
                    break;
                default:
                    MarkTopLevel(symbols, CandidateNames(right), ExportDefault); // module.exports = X / new X() / wrap(X)
                    break;
            }
            return;
        }
        // exports.f = … / module.exports.f = …
        if (left.Type == "member_expression" && left.GetChildForField("object") is { } target
            && (target is { Type: "identifier", Text: "exports" } || IsModuleExports(target)))
        {
            if (right.Type == "identifier") MarkTopLevel(symbols, [right.Text], "export");
            else SetVisibility(symbols, FindAt(symbols, assignment), "export");
        }
    }

    private static bool IsModuleExports(Node node) =>
        node.Type == "member_expression"
        && node.GetChildForField("object") is { Type: "identifier", Text: "module" }
        && node.GetChildForField("property") is { Text: "exports" };

    /// <summary>`export default memo(() => …)`：匿名包装组件名叫 "default" 时改成文件名（index 文件取目录名）。</summary>
    private static void RenameWrappedDefault(List<ParsedSymbol> symbols, Node call)
    {
        var index = FindAt(symbols, call);
        if (index < 0 || symbols[index].Name != "default") return;
        var path = symbols[0].QualifiedName;
        var name = Path.GetFileNameWithoutExtension(path);
        if (name == "index" && path.Contains('/')) name = Path.GetFileName(path[..path.LastIndexOf('/')]);
        if (string.IsNullOrEmpty(name)) return;
        var oldQualified = symbols[index].QualifiedName;
        var newQualified = path + ":" + name;
        symbols[index] = symbols[index] with { Name = name, QualifiedName = newQualified };
        for (var i = index + 1; i < symbols.Count; i++)
            if (symbols[i].QualifiedName.StartsWith(oldQualified + ".", StringComparison.Ordinal))
                symbols[i] = symbols[i] with { QualifiedName = newQualified + symbols[i].QualifiedName[oldQualified.Length..] };
    }

    /// <summary>类体里值是箭头函数 / 函数表达式的字段（handleClick = () => {}）：可以 this.handleClick() 调用、作为 onClick 传出去，记为方法。</summary>
    private static void ArrowFieldsAsMethods(Node classNode, List<ParsedSymbol> symbols)
    {
        var body = classNode.GetChildForField("body");
        if (body is null) return;
        foreach (var member in body.NamedChildren)
            if (member.Type is "public_field_definition" or "field_definition"
                && member.GetChildForField("value") is { Type: "arrow_function" or "function_expression" })
                SetKind(symbols, member, SymbolKind.Field, SymbolKind.Method);
    }

    /// <summary>styled(X)(…) / styled.div(…) / styled('div')(…)：被调用者的根是 styled。</summary>
    private static bool IsStyledCall(Node? value)
    {
        if (value is null || value.Type != "call_expression") return false;
        var fn = value.GetChildForField("function");
        for (var i = 0; fn is not null && i < 8; i++)
        {
            if (fn.Type == "identifier") return fn.Text == "styled";
            fn = fn.Type switch
            {
                "call_expression" => fn.GetChildForField("function"),
                "member_expression" => fn.GetChildForField("object"),
                _ => null,
            };
        }
        return false;
    }

    private static int FindAt(List<ParsedSymbol> symbols, Node def) =>
        symbols.FindIndex(1, s => s.StartLine == def.StartPosition.Row + 1 && s.StartCol == def.StartPosition.Column + 1);

    private static void SetKind(List<ParsedSymbol> symbols, Node def, SymbolKind from, SymbolKind to)
    {
        var index = symbols.FindIndex(1, s => s.Kind == from && s.StartLine == def.StartPosition.Row + 1 && s.StartCol == def.StartPosition.Column + 1);
        if (index > 0) symbols[index] = symbols[index] with { Kind = to };
    }

    /// <summary>没有可见性的符号标上；"export" 可以升成 "export default"，反过来不降。</summary>
    private static void SetVisibility(List<ParsedSymbol> symbols, int index, string visibility)
    {
        if (index <= 0) return;
        var current = symbols[index].Visibility;
        if (current is null || (current == "export" && visibility == ExportDefault))
            symbols[index] = symbols[index] with { Visibility = visibility };
    }

    /// <summary>顶层符号里第一个名字匹配的标上可见性。</summary>
    private static void MarkTopLevel(List<ParsedSymbol> symbols, IEnumerable<string> names, string visibility)
    {
        foreach (var name in names)
        {
            var index = symbols.FindIndex(s => s.ParentIndex == 0 && s.Name == name);
            if (index < 0) continue;
            SetVisibility(symbols, index, visibility);
            break;
        }
    }

    /// <summary>默认导出表达式里可能指向组件的标识符：本身、new 的类、调用的实参（由外向内）。</summary>
    private static IEnumerable<string> CandidateNames(Node value)
    {
        if (value.Type == "identifier") { yield return value.Text; yield break; }
        if (value.Type == "new_expression")
        {
            if (value.GetChildForField("constructor") is { Type: "identifier" } ctor) yield return ctor.Text;
            yield break;
        }
        if (value.Type != "call_expression") yield break;
        var args = value.GetChildForField("arguments");
        if (args is not null)
            foreach (var a in args.NamedChildren)
                foreach (var n in CandidateNames(a)) yield return n;
        var fn = value.GetChildForField("function");
        if (fn is not null && fn.Type == "call_expression")
            foreach (var n in CandidateNames(fn)) yield return n;
    }

    protected override object? PrepareFile(Node root)
    {
        var byLocal = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var stmt in root.NamedChildren)
        {
            if (stmt.Type != "import_statement") continue;
            var source = StringValue(stmt.GetChildForField("source"));
            if (source is null) continue;
            foreach (var clause in stmt.NamedChildren)
            {
                if (clause.Type != "import_clause") continue;
                foreach (var part in clause.NamedChildren)
                {
                    switch (part.Type)
                    {
                        case "identifier":
                            byLocal[part.Text] = (source, "default");
                            break;
                        case "namespace_import":
                            foreach (var id in part.NamedChildren)
                                if (id.Type == "identifier") byLocal[id.Text] = (source, "*");
                            break;
                        case "named_imports":
                            foreach (var spec in part.NamedChildren)
                            {
                                if (spec.Type != "import_specifier") continue;
                                var name = spec.GetChildForField("name");
                                if (name is null) continue;
                                var alias = spec.GetChildForField("alias");
                                var imported = StringValue(name) ?? name.Text;
                                byLocal[(alias ?? name).Text] = (source, imported);
                            }
                            break;
                    }
                }
            }
        }
        var classes = new Dictionary<int, ClassInfo>();
        Scan(root, byLocal, classes, 0);
        return new ImportBindings(byLocal, classes);
    }

    /// <summary>
    /// 全文件找 require 绑定（可能写在函数里、if 里）与类。不进 JSX、字符串、类型标注这类不会有它们的子树。
    /// require 的绑定不覆盖同名的 ES import。
    /// </summary>
    private static void Scan(Node node, Dictionary<string, (string, string)> byLocal, Dictionary<int, ClassInfo> classes, int depth)
    {
        if (depth > 256) return;
        foreach (var child in node.NamedChildren)
        {
            switch (child.Type)
            {
                case "jsx_element" or "jsx_self_closing_element" or "jsx_fragment" or "string" or "template_string" or "comment"
                    or "type_annotation" or "interface_declaration" or "type_alias_declaration" or "import_statement":
                    continue;
                case "variable_declarator":
                    CollectRequire(child, byLocal);
                    break;
                case "class_declaration" or "abstract_class_declaration" or "class":
                    classes[child.StartIndex] = ClassInfoOf(child);
                    break;
            }
            Scan(child, byLocal, classes, depth + 1);
        }
    }

    /// <summary>const x = require('m') → m 的 default；const { f, g: h } = require('m') → 具名 f / g；const x = require('m').f → 具名 f。</summary>
    private static void CollectRequire(Node declarator, Dictionary<string, (string, string)> byLocal)
    {
        var value = declarator.GetChildForField("value");
        var name = declarator.GetChildForField("name");
        if (value is null || name is null) return;
        string? source;
        string? property = null;
        if (value.Type == "member_expression")
        {
            source = RequireSource(value.GetChildForField("object"));
            property = value.GetChildForField("property")?.Text;
        }
        else source = RequireSource(value);
        if (source is null) return;
        if (name.Type == "identifier")
        {
            byLocal.TryAdd(name.Text, (source, property ?? "default"));
            return;
        }
        if (name.Type != "object_pattern" || property is not null) return;
        foreach (var part in name.NamedChildren)
        {
            switch (part.Type)
            {
                case "shorthand_property_identifier_pattern":
                    byLocal.TryAdd(part.Text, (source, part.Text));
                    break;
                case "object_assignment_pattern" when part.GetChildForField("left") is { Type: "shorthand_property_identifier_pattern" } left:
                    byLocal.TryAdd(left.Text, (source, left.Text)); // const { f = noop } = require('m')
                    break;
                case "pair_pattern":
                {
                    var key = part.GetChildForField("key");
                    var local = part.GetChildForField("value");
                    if (local is { Type: "assignment_pattern" }) local = local.GetChildForField("left");
                    if (key is not null && local is { Type: "identifier" }) byLocal.TryAdd(local.Text, (source, StringValue(key) ?? key.Text));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 类的带类型成员：带类型标注的字段、构造参数属性（constructor(private readonly svc: T)），
    /// 以及构造函数体里 this.x = 带类型的参数；再加 extends 的父类名。
    /// </summary>
    private static ClassInfo ClassInfoOf(Node classNode)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? baseName = null;
        foreach (var child in classNode.NamedChildren)
        {
            if (child.Type == "class_heritage")
            {
                foreach (var h in child.NamedChildren)
                {
                    if (h.Type == "extends_clause") // TS：extends_clause value: …
                    {
                        if (h.GetChildForField("value") is { Type: "identifier" } v) baseName = v.Text;
                    }
                    else if (h.Type == "identifier") baseName = h.Text; // JS：class_heritage 直接是表达式
                }
            }
            if (child.Type != "class_body") continue;
            foreach (var member in child.NamedChildren)
            {
                if (member.Type == "public_field_definition")
                {
                    if (member.GetChildForField("name") is { Type: "property_identifier" } n && TypeNameOf(member.GetChildForField("type")) is { } t)
                        fields[n.Text] = t;
                }
                else if (member.Type == "method_definition" && member.GetChildForField("name") is { Text: "constructor" })
                    CollectConstructorFields(member, fields);
            }
        }
        return new ClassInfo(fields, baseName);
    }

    private static void CollectConstructorFields(Node constructor, Dictionary<string, string> fields)
    {
        var typedParams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constructor.GetChildForField("parameters") is { } parameters)
        {
            foreach (var p in parameters.NamedChildren)
            {
                if (p.Type is not ("required_parameter" or "optional_parameter")) continue;
                if (p.GetChildForField("pattern") is not { Type: "identifier" } pattern || TypeNameOf(p.GetChildForField("type")) is not { } type) continue;
                typedParams[pattern.Text] = type;
                if (p.Children.Any(c => c.Type is "accessibility_modifier" or "readonly")) fields[pattern.Text] = type;
            }
        }
        if (typedParams.Count == 0 || constructor.GetChildForField("body") is not { } body) return;
        // this.svc = svc;
        foreach (var stmt in body.NamedChildren)
        {
            if (stmt.Type != "expression_statement" || stmt.NamedChildren.FirstOrDefault() is not { Type: "assignment_expression" } assignment) continue;
            if (assignment.GetChildForField("left") is { Type: "member_expression" } left
                && left.GetChildForField("object") is { Type: "this" }
                && left.GetChildForField("property") is { } property
                && assignment.GetChildForField("right") is { Type: "identifier" } right
                && typedParams.TryGetValue(right.Text, out var t))
                fields.TryAdd(property.Text, t);
        }
    }

    /// <summary>type_annotation 里的类型名：T 或 T&lt;…&gt;；其他形态（联合、A.B、数组、内置类型）返回 null。</summary>
    private static string? TypeNameOf(Node? annotation)
    {
        var type = annotation?.NamedChildren.FirstOrDefault();
        return type?.Type switch
        {
            "type_identifier" => type.Text,
            "generic_type" => type.GetChildForField("name") is { Type: "type_identifier" } n ? n.Text : null,
            _ => null,
        };
    }

    private static string? StringValue(Node? node)
    {
        if (node is null || node.Type != "string") return null;
        foreach (var c in node.NamedChildren)
            if (c.Type == "string_fragment") return c.Text;
        return "";
    }

    // ---------- 调用大纲：调用所在的控制结构 ----------

    private static readonly HashSet<string> ContextBoundaries = new(StringComparer.Ordinal)
    {
        "program", "function_declaration", "generator_function_declaration", "method_definition",
        "class_declaration", "abstract_class_declaration", "class", "internal_module", "module",
        "public_field_definition", "field_definition", "class_static_block",
    };

    /// <summary>
    /// 往上找控制结构时到这里停：函数声明、方法、类、命名空间、类字段初始化器；
    /// 以及本身就是符号的箭头函数 / 函数表达式（const f = () => …、导出、类字段、module.exports、memo(() => …) 包装的组件）——
    /// 它体里的调用归这个符号，按它的体算，不是回调。
    /// </summary>
    protected override bool IsContextBoundary(Node ancestor) =>
        ContextBoundaries.Contains(ancestor.Type)
        || (ancestor.Type is "arrow_function" or "function_expression" or "generator_function" && IsDefinedFunction(ancestor));

    /// <summary>
    /// loop：for / for…in / for…of / while / do 的 body（初始化、条件、步进、被遍历的集合不算）；
    /// cond：if 的两个分支、switch 的 body、三元的两个分支、?? 与 ??= 的右边、?. 之后（含链上更后面的 .c() 与实参）；
    /// try：try 的 body 与 finally；catch：catch 子句；callback：箭头函数、函数表达式（不是符号的那些）。
    /// JSX on* 属性里直接传出去的处理函数（onClick={save}）在 <see cref="AnnotateReference"/> 里标 callback。
    /// </summary>
    protected override string? ContextOf(Node ancestor, Node child) => ancestor.Type switch
    {
        "for_statement" or "for_in_statement" or "while_statement" or "do_statement" => IsField(ancestor, "body", child) ? CallContext.Loop : null,
        "if_statement" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        "switch_statement" => IsField(ancestor, "body", child) ? CallContext.Cond : null,
        "ternary_expression" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        "binary_expression" => IsField(ancestor, "right", child) && ancestor.GetChildForField("operator")?.Type == "??" ? CallContext.Cond : null,
        "augmented_assignment_expression" => IsField(ancestor, "right", child) && ancestor.GetChildForField("operator")?.Type == "??=" ? CallContext.Cond : null,
        // a?.b() 的 b、a?.b().c() 的 c：整条链一起短路
        "member_expression" => IsField(ancestor, "property", child) && (HasOptionalChain(ancestor) || ShortCircuits(ancestor.GetChildForField("object"))) ? CallContext.Cond : null,
        // fn?.() 本身；a?.b(f())：实参也只在 a 非空时求值
        "call_expression" => IsField(ancestor, "function", child) ? (HasOptionalChain(ancestor) ? CallContext.Cond : null)
            : IsField(ancestor, "arguments", child) && (HasOptionalChain(ancestor) || ShortCircuits(ancestor.GetChildForField("function"))) ? CallContext.Cond : null,
        "try_statement" => IsField(ancestor, "body", child) || IsField(ancestor, "finalizer", child) ? CallContext.Try : null,
        "catch_clause" => IsField(ancestor, "body", child) ? CallContext.Catch : null,
        "arrow_function" or "function_expression" or "generator_function" => CallContext.Callback,
        _ => null,
    };

    private static bool HasOptionalChain(Node node)
    {
        foreach (var c in node.Children)
            if (c.Type is "optional_chain" or "?.") return true;
        return false;
    }

    /// <summary>接收者链（.成员 / 调用 / 下标 / 非空断言往左走）上有没有 ?.；括号截断短路。</summary>
    private static bool ShortCircuits(Node? expr)
    {
        for (var depth = 0; expr is not null && depth < 32; depth++)
        {
            switch (expr.Type)
            {
                case "member_expression" or "subscript_expression":
                    if (HasOptionalChain(expr)) return true;
                    expr = expr.GetChildForField("object");
                    break;
                case "call_expression":
                    if (HasOptionalChain(expr)) return true;
                    expr = expr.GetChildForField("function");
                    break;
                case "non_null_expression":
                    expr = expr.FirstNamedChild;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// 箭头函数 / 函数表达式是不是某个符号的体（与定义查询对应）：变量初始值、export default、类字段、
    /// exports.f = / module.exports = / module.exports = { f: … }、顶层 const X = wrap(() => …) / wrap(wrap2(() => …))（可带 as）。
    /// </summary>
    private static bool IsDefinedFunction(Node fn)
    {
        var parent = fn.Parent;
        switch (parent?.Type)
        {
            case "variable_declarator":
                return IsField(parent, "value", fn);
            case "export_statement" or "public_field_definition" or "field_definition":
                return true;
            case "assignment_expression":
                return IsField(parent, "right", fn) && IsExportsTarget(parent.GetChildForField("left"));
            case "pair":
                return parent.Parent is { Type: "object" } obj && obj.Parent is { Type: "assignment_expression" } assign
                       && IsField(assign, "right", obj) && assign.GetChildForField("left") is { } left && IsModuleExports(left);
            case "arguments":
            {
                // 顶层的包装调用：const X = wrap(fn) / wrap(wrap2(fn)) / wrap(fn) as T；export default wrap(fn)
                var call = parent.Parent;
                for (var level = 0; level < 2 && call is { Type: "call_expression" }; level++)
                {
                    var up = call.Parent;
                    if (up is { Type: "as_expression" }) up = up.Parent;
                    switch (up?.Type)
                    {
                        case "export_statement":
                            return true;
                        case "variable_declarator":
                            return up.Parent?.Parent is { Type: "program" or "export_statement" };
                        case "arguments":
                            call = up.Parent;
                            continue;
                    }
                    return false;
                }
                return false;
            }
            default:
                return false;
        }
    }

    /// <summary>exports.f / module.exports / module.exports.f。</summary>
    private static bool IsExportsTarget(Node? left) =>
        left is { Type: "member_expression" }
        && (IsModuleExports(left)
            || left.GetChildForField("object") is { } target && (target is { Type: "identifier", Text: "exports" } || IsModuleExports(target)));

    protected override ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState)
    {
        if (fileState is not ImportBindings bindings) return reference;
        var via = reference.Via ?? (captureName == "calls" && IsEventHandler(node) ? EventVia : null);
        // onClick={save}：处理函数被传出去，事件发生时才执行
        if (via == EventVia) reference = reference with { Context = CallContext.Callback };
        // 成员调用 / 成员式标签：看接收者
        if (node.Parent is { Type: "member_expression" } member && member.GetChildForField("property") == node)
        {
            var receiver = member.GetChildForField("object");
            var binding = receiver?.Type switch
            {
                "this" or "super" => ThisBinding(node, bindings),
                "identifier" when bindings.ByLocal.TryGetValue(receiver.Text, out var b) => new ScriptBinding(ScriptReceiver.Import, b.Source, b.Imported),
                "member_expression" => FieldBinding(receiver, bindings) ?? new ScriptBinding(ScriptReceiver.Other),
                "call_expression" when RequireSource(receiver) is { } source => new ScriptBinding(ScriptReceiver.Import, source, "default"), // require('m').f()
                _ => new ScriptBinding(ScriptReceiver.Other),
            };
            return reference with { Script = binding, Via = via };
        }
        return reference with
        {
            Via = via,
            Script = bindings.ByLocal.TryGetValue(node.Text, out var own)
                ? new ScriptBinding(ScriptReceiver.None, own.Source, own.Imported)
                : new ScriptBinding(ScriptReceiver.None),
        };
    }

    /// <summary>
    /// 引用是否是 on 开头 JSX 属性里传出去的处理函数（查询已限定属性名）：
    /// onClick={handleX} / onClick={this.handleX} / onSubmit={form.handleSubmit(onSubmit)} 里的 onSubmit。
    /// </summary>
    private static bool IsEventHandler(Node node)
    {
        var p = node.Parent;
        if (p is null) return false;
        if (node.Type == "property_identifier" && p.Type == "member_expression") p = p.Parent;
        else if (p.Type == "arguments") p = p.Parent?.Parent;
        return p is { Type: "jsx_expression" } && p.Parent is { Type: "jsx_attribute" };
    }

    /// <summary>this.x()：带上所在类的父类（import 进来的 → 它的 import；本文件的 → 名字）。</summary>
    private static ScriptBinding ThisBinding(Node node, ImportBindings bindings)
    {
        var info = EnclosingClass(node, bindings);
        if (info?.BaseName is not { } baseName) return new ScriptBinding(ScriptReceiver.This);
        return bindings.ByLocal.TryGetValue(baseName, out var b)
            ? new ScriptBinding(ScriptReceiver.This, b.Source, b.Imported)
            : new ScriptBinding(ScriptReceiver.This, null, baseName);
    }

    /// <summary>this.svc.x()：svc 是本类带类型的成员时给出类型的绑定。</summary>
    private static ScriptBinding? FieldBinding(Node receiver, ImportBindings bindings)
    {
        if (receiver.GetChildForField("object") is not { Type: "this" } || receiver.GetChildForField("property") is not { Type: "property_identifier" } field) return null;
        if (EnclosingClass(receiver, bindings) is not { } info || !info.FieldTypes.TryGetValue(field.Text, out var type)) return null;
        return bindings.ByLocal.TryGetValue(type, out var b)
            ? new ScriptBinding(ScriptReceiver.Typed, b.Source, b.Imported)
            : new ScriptBinding(ScriptReceiver.Typed, null, type);
    }

    private static ClassInfo? EnclosingClass(Node node, ImportBindings bindings)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (p.Type is "class_declaration" or "abstract_class_declaration" or "class")
                return bindings.Classes.GetValueOrDefault(p.StartIndex);
        return null;
    }
}
