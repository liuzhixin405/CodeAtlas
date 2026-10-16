using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// C#：限定名是命名空间链 + 类型 + 成员（方法不带参数签名，重载同名）。
/// 方法 / 构造 / 局部函数的 Signature 是声明头（参数表跨行时压成一行，解析器从这里数参数个数）；
/// 字段的 Signature 是"修饰符 + 声明类型 + 名字"（解析器从这里读继承来的字段的类型）。
/// <para>调用引用借用 <see cref="ScriptBinding"/> 记接收者，ImportedName 是实参个数：</para>
/// <list type="bullet">
/// <item>None：new T(…) / new A.B.T(…)，ImportSource 是 T 的写法（去泛型实参）；目标类型的 new(…) 按声明类型 / 返回类型 / 赋值左边 /
/// 集合初始化器的元素类型推出 T（推不出来记 This + "delegate"，不建边）。</item>
/// <item>This：m(…) / this.m(…)；base.m(…) 的 ImportSource 为 "base"；
/// 调用的是本方法里的局部变量 / 参数 / 本类字段（委托）时 ImportSource 为 "delegate"，不建边。</item>
/// <item>Other：接收者类型读得出来时 ImportSource 是类型写法（局部变量 / 参数 / 本文件外层类型的字段与属性 / 主构造参数 / new / 强转 / as /
/// 泛型参数的 where 约束），或者是交给解析器一步步推的成员链描述（var x = Foo()、a.B.C()、await x.GetAsync()，格式见 <see cref="Chain"/>）；
/// 推不出来（lambda 参数、元组解构）为 null。</item>
/// <item>Import：接收者是个名字（或 a.b.C 点号链），但不是本文件可见的局部名 / 字段 / 属性——类型名的静态调用、
/// 写在别的文件（父类、partial）里的字段 / 属性，ImportSource 是这个名字；predefined 类型（string.Join）同样记写法。</item>
/// </list>
/// 调用的行号取整个调用表达式的起始行（跨行链式调用记在第一行），与 Roslyn 一致。nameof(…) 不记调用。
/// </summary>
public sealed partial class CSharpAdapter() : LanguageAdapterBase(
    "csharp", "C#", [".cs"],
    QueryResources.Load("csharp.definitions.scm"),
    QueryResources.Load("csharp.references.scm"))
{
    private static readonly string[] VisibilityModifiers = ["public", "private", "protected", "internal"];

    private static readonly HashSet<string> TypeDeclarations = new(StringComparer.Ordinal)
    {
        "class_declaration", "struct_declaration", "record_declaration", "record_struct_declaration", "interface_declaration",
    };

    /// <summary>局部名所在的成员：从这里往下收参数与局部变量。</summary>
    private static readonly HashSet<string> MemberDeclarations = new(StringComparer.Ordinal)
    {
        "method_declaration", "constructor_declaration", "destructor_declaration", "operator_declaration", "conversion_operator_declaration",
        "property_declaration", "indexer_declaration", "event_declaration", "field_declaration", "global_statement",
    };

    private static readonly HashSet<string> MethodLike = new(StringComparer.Ordinal)
    {
        "method_declaration", "constructor_declaration", "local_function_statement",
    };

    protected override string ModuleQualifiedPrefix(string relativePath) => "";

    protected override EdgeKind AdjustReferenceKind(EdgeKind kind, string name) =>
        kind == EdgeKind.Inherits && InterfaceName().IsMatch(name) ? EdgeKind.Implements : kind;

    /// <summary>文件级命名空间延伸到整个文件；字段的范围取整个 field_declaration，让类型引用归属到字段。</summary>
    protected override Node DefinitionScope(Node definition, Node root) => definition.Type switch
    {
        "file_scoped_namespace_declaration" => root,
        "variable_declarator" => definition.Parent?.Parent ?? definition,
        _ => definition,
    };

    protected override string? ExtractVisibility(Node definition)
    {
        // 字段的可见性在 field_declaration 上，而定义节点是 variable_declarator
        var target = definition.Type == "variable_declarator" ? definition.Parent?.Parent : definition;
        if (target is null) return null;
        foreach (var child in target.Children)
            if (child.Type == "modifier" && Array.IndexOf(VisibilityModifiers, child.Text) >= 0) return child.Text;
        return null;
    }

    /// <summary>
    /// handles 捕获的是 base_list 里泛型接口的第一个类型实参；只有接口名以 Handler / Consumer 结尾才算"处理该类型"。
    /// nameof(x) 语法上是调用，不记。
    /// </summary>
    protected override bool AcceptReference(string captureName, Node node)
    {
        if (captureName == "calls")
            return !(node.Type == "identifier" && node.Text == "nameof" && node.Parent is { Type: "invocation_expression" });
        if (captureName != "handles") return true;
        var genericName = node.Parent?.Parent; // identifier → type_argument_list → generic_name
        var iface = genericName?.Type == "generic_name" ? genericName.FirstChild?.Text : null;
        return iface is not null && HandlerInterface().IsMatch(iface);
    }

    /// <summary>目标类型的 new(…) 先记个占位名，类型在 <see cref="AnnotateReference"/> 里按声明推（推不出来不建边）。</summary>
    protected override string ReferenceName(Node node) => node.Type switch
    {
        "implicit_object_creation_expression" => "new@" + node.StartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "qualified_name" => QualifiedParts(node).LastOrDefault() ?? "", // new A.B.Foo<int>(…)
        _ => base.ReferenceName(node),
    };

    // ---------- 调用大纲：调用所在的控制结构 ----------

    /// <summary>
    /// 往上找控制结构时到这里停：成员（方法 / 构造 / 属性及其访问器 / 索引器 / 事件 / 字段初始化器 / 运算符）、
    /// 局部函数（它自己是符号，体里的调用归它，按它的体算）、类型、命名空间、顶级语句。
    /// </summary>
    private static readonly HashSet<string> ContextBoundaries = new(StringComparer.Ordinal)
    {
        "method_declaration", "constructor_declaration", "destructor_declaration", "operator_declaration", "conversion_operator_declaration",
        "accessor_declaration", "property_declaration", "indexer_declaration", "event_declaration", "field_declaration", "event_field_declaration",
        "local_function_statement",
        "class_declaration", "struct_declaration", "record_declaration", "record_struct_declaration", "interface_declaration", "enum_declaration",
        "namespace_declaration", "file_scoped_namespace_declaration", "global_statement", "compilation_unit",
    };

    protected override bool IsContextBoundary(Node ancestor) => ContextBoundaries.Contains(ancestor.Type);

    /// <summary>
    /// loop：for / foreach / while / do 的 body（初始化、条件、步进、foreach 的集合不算）；
    /// cond：if 的两个分支、switch 的 body、三元的两个分支、switch 表达式的 arm、?? 与 ??= 的右边、?. 之后（含链上更后面的 .C() 与实参）；
    /// try：try 的 body 与 finally；catch：catch 子句的 body（when 过滤不算）；callback：lambda、匿名方法。
    /// &amp;&amp; / || 的右边不算 cond（与 Roslyn 侧一致）。
    /// </summary>
    protected override string? ContextOf(Node ancestor, Node child) => ancestor.Type switch
    {
        "for_statement" or "foreach_statement" or "while_statement" or "do_statement" => IsField(ancestor, "body", child) ? CallContext.Loop : null,
        "if_statement" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        "switch_statement" => IsField(ancestor, "body", child) ? CallContext.Cond : null,
        "switch_expression" => child.Type == "switch_expression_arm" ? CallContext.Cond : null,
        "conditional_expression" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        "binary_expression" => IsField(ancestor, "right", child) && ancestor.GetChildForField("operator")?.Type == "??" ? CallContext.Cond : null,
        "assignment_expression" => IsField(ancestor, "right", child) && ancestor.GetChildForField("operator")?.Type == "??=" ? CallContext.Cond : null,
        "conditional_access_expression" => IsField(ancestor, "condition", child) ? null : CallContext.Cond,
        // a?.B().C() 的 C、a?.B.C() 的 C：语法树里 .C 挂在 ?. 外面，但整条链一起短路
        "member_access_expression" => IsField(ancestor, "name", child) && ShortCircuits(ancestor.GetChildForField("expression")) ? CallContext.Cond : null,
        // a?.B(F())：实参也只在 a 非 null 时求值
        "invocation_expression" => IsField(ancestor, "arguments", child) && ShortCircuits(ancestor.GetChildForField("function")) ? CallContext.Cond : null,
        "try_statement" => IsField(ancestor, "body", child) || child.Type == "finally_clause" ? CallContext.Try : null,
        // when 过滤不算 catch（与 Roslyn 侧一致），归外层
        "catch_clause" => IsField(ancestor, "body", child) ? CallContext.Catch : null,
        "lambda_expression" or "anonymous_method_expression" => CallContext.Callback,
        _ => null,
    };

    /// <summary>表达式的接收者链（.成员 / 调用 / 索引往左走）上有没有 ?.；括号截断短路。</summary>
    private static bool ShortCircuits(Node? expr)
    {
        for (var depth = 0; expr is not null && depth < 32; depth++)
        {
            switch (expr.Type)
            {
                case "conditional_access_expression":
                    return true;
                case "member_access_expression" or "element_access_expression":
                    expr = expr.GetChildForField("expression");
                    break;
                case "invocation_expression":
                    expr = expr.GetChildForField("function");
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    // ---------- 符号修正：签名 ----------

    protected override void AdjustSymbols(Node root, List<ParsedSymbol> symbols)
    {
        var byStart = new Dictionary<(int Line, int Col, SymbolKind Kind), int>();
        for (var i = 1; i < symbols.Count; i++) byStart.TryAdd((symbols[i].StartLine, symbols[i].StartCol, symbols[i].Kind), i);
        foreach (var n in Descendants(root))
        {
            var pos = (n.StartPosition.Row + 1, n.StartPosition.Column + 1);
            if (MethodLike.Contains(n.Type))
            {
                var kind = n.Type == "local_function_statement" ? SymbolKind.Function : SymbolKind.Method;
                if (!byStart.TryGetValue((pos.Item1, pos.Item2, kind), out var idx)) continue;
                if (symbols[idx].Signature is { } sig && ParenthesesBalanced(sig)) continue;
                if (MethodHeader(n) is { } header) symbols[idx] = symbols[idx] with { Signature = header };
            }
            else if (n.Type == "variable_declarator" && n.Parent is { Type: "variable_declaration" } decl && decl.Parent is { Type: "field_declaration" } field)
            {
                if (!byStart.TryGetValue((pos.Item1, pos.Item2, SymbolKind.Field), out var idx)) continue;
                var modifiers = field.Children.Where(c => c.Type == "modifier").Select(c => c.Text);
                var type = decl.GetChildForField("type")?.Text;
                var name = n.GetChildForField("name")?.Text ?? n.Text;
                if (type is null) continue;
                symbols[idx] = symbols[idx] with { Signature = Collapse(string.Join(' ', modifiers.Append(type).Append(name))) };
            }
        }
    }

    private static bool ParenthesesBalanced(string signature)
    {
        var open = signature.IndexOf('(');
        if (open < 0) return false;
        var depth = 0;
        for (var i = open; i < signature.Length; i++)
        {
            if (signature[i] == '(') depth++;
            else if (signature[i] == ')' && --depth == 0) return true;
        }
        return false;
    }

    /// <summary>从第一个非特性子节点到参数表结束，空白压成一个空格。</summary>
    private static string? MethodHeader(Node method)
    {
        var parameters = method.GetChildForField("parameters") ?? method.NamedChildren.FirstOrDefault(c => c.Type == "parameter_list");
        if (parameters is null) return null;
        var start = method.Children.FirstOrDefault(c => c.Type != "attribute_list");
        if (start is null) return null;
        var text = method.Text;
        var from = start.StartIndex - method.StartIndex;
        var to = parameters.EndIndex - method.StartIndex;
        if (from < 0 || to > text.Length || to <= from) return null;
        var header = Collapse(text[from..to]);
        return header.Length > 400 ? header[..400] : header;
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space && ch != ')' && sb[^1] != '(') sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ---------- 调用的接收者 ----------

    /// <summary>局部名：名字 → (声明位置, 类型写法, var 的初始化表达式)，类型未知为 null；var x = 调用链 时 Init 是那个表达式，查到时再推。</summary>
    private sealed class Locals : Dictionary<string, List<(int Position, string? Type, Node? Init)>>
    {
        public Locals() : base(StringComparer.Ordinal) { }
    }

    private sealed class FileState
    {
        public Dictionary<int, Locals> MemberLocals { get; } = [];
        public Dictionary<int, Dictionary<string, string?>> Fields { get; } = [];
        /// <summary>var 初始化表达式起点 → 推出来的接收者描述（见 <see cref="Chain"/>）。</summary>
        public Dictionary<int, string?> InitChains { get; } = [];
        public int Depth { get; set; }
    }

    protected override object? PrepareFile(Node root) => new FileState();

    protected override ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState)
    {
        if (captureName != "calls" || fileState is not FileState state) return reference;
        // 行号取整个调用 / new 表达式的起始行（跨行的链式调用 a\n.B() 记在 a 那一行），与 Roslyn 的约定一致，
        // 这样 RoslynApplier 按行判断"Roslyn 这一行绑上了没有"时两边对得上
        if (node.Type == "implicit_object_creation_expression")
        {
            var written = TargetType(node, state);
            if (written is null) return reference with { Script = new ScriptBinding(ScriptReceiver.This, HeuristicEdgeResolver.CSharpDelegateMarker) };
            return reference with { Name = written.Split('.')[^1], Via = DispatchSite(node), Script = new ScriptBinding(ScriptReceiver.None, written, Argc(node)) };
        }
        var target = node.Parent is { Type: "generic_name" } g ? g : node;
        switch (target.Parent)
        {
            case { Type: "object_creation_expression" } creation:
                return reference with { Line = StartLine(creation), Via = DispatchSite(creation), Script = new ScriptBinding(ScriptReceiver.None, TypeName(target, target), Argc(creation)) };
            case { Type: "invocation_expression" } call when Same(call.GetChildForField("function"), target):
            {
                var argc = Argc(call);
                // 调用的是局部变量 / 参数 / 字段（委托）
                if (LookupLocal(node.Text, node, state, out _) || LookupField(node.Text, node, state, out _))
                    return reference with { Line = StartLine(call), Script = new ScriptBinding(ScriptReceiver.This, HeuristicEdgeResolver.CSharpDelegateMarker, argc) };
                return reference with { Line = StartLine(call), Script = new ScriptBinding(ScriptReceiver.This, null, argc) };
            }
            case { Type: "member_access_expression" } access when access.Parent is { Type: "invocation_expression" } call:
                return reference with { Line = StartLine(call), Script = Receiver(access.GetChildForField("expression"), node, state, Argc(call)) };
            case { Type: "member_binding_expression" } binding
                when binding.Parent is { Type: "conditional_access_expression" } conditional && conditional.Parent is { Type: "invocation_expression" } call:
                return reference with { Line = StartLine(call), Script = Receiver(conditional.GetChildForField("condition"), node, state, Argc(call)) };
            default:
                return reference;
        }
    }

    private static string? DispatchSite(Node creation)
    {
        var expression = creation;
        while (expression.Parent is { Type: "parenthesized_expression" } parent) expression = parent;
        if (expression.Parent is not { Type: "argument" } argument
            || argument.Parent is not { Type: "argument_list" } arguments
            || arguments.Parent is not { Type: "invocation_expression" } call) return null;
        var function = call.GetChildForField("function");
        var name = function?.Type is "member_access_expression" or "member_binding_expression"
            ? function.GetChildForField("name") : function;
        if (name?.Type == "generic_name") name = name.FirstChild;
        return name is not null && HeuristicEdgeResolver.IsDispatcherMethod(name.Text)
            ? "dispatch-site:" + name.StartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static int StartLine(Node node) => node.StartPosition.Row + 1;

    /// <summary>目标类型 new(…) 的类型写法：字段 / 局部变量 / 属性的声明类型、方法返回类型、赋值左边的类型、集合 / 数组初始化器的元素类型。</summary>
    private static string? TargetType(Node creation, FileState state)
    {
        if (creation.Parent is { Type: "assignment_expression" } assign && Same(assign.GetChildForField("right"), creation))
        {
            var left = assign.GetChildForField("left");
            string? type = null;
            var found = left switch
            {
                { Type: "identifier" } => LookupLocal(left.Text, creation, state, out type) || LookupField(left.Text, creation, state, out type),
                { Type: "member_access_expression" } when left.GetChildForField("expression") is { Type: "this" } && left.GetChildForField("name") is { } n
                    => LookupField(n.Text, creation, state, out type),
                _ => false,
            };
            return found && type is not null && !type.Contains('|') && !type.StartsWith('?') && !type.StartsWith('@') ? type : null;
        }
        return TargetTypeNode(creation, 0) is { } node ? TypeName(node, creation) : null;
    }

    private static Node? TargetTypeNode(Node creation, int depth)
    {
        if (depth > 4) return null;
        var parent = creation.Parent;
        switch (parent?.Type)
        {
            case "variable_declarator":
                return parent.Parent is { Type: "variable_declaration" } decl && decl.GetChildForField("type") is { Type: not "implicit_type" } declared ? declared : null;
            case "property_declaration":
                return parent.GetChildForField("type");
            case "arrow_expression_clause":
                return parent.Parent?.Type switch
                {
                    "method_declaration" => parent.Parent.GetChildForField("returns") ?? parent.Parent.GetChildForField("type"),
                    "local_function_statement" => parent.Parent.GetChildForField("type") ?? parent.Parent.GetChildForField("returns"),
                    "property_declaration" => parent.Parent.GetChildForField("type"),
                    _ => null,
                };
            case "return_statement":
                for (var cur = parent.Parent; cur is not null; cur = cur.Parent)
                {
                    if (cur.Type is "lambda_expression" or "anonymous_method_expression" or "accessor_declaration") return null;
                    if (cur.Type == "method_declaration") return cur.GetChildForField("returns") ?? cur.GetChildForField("type");
                    if (cur.Type == "local_function_statement") return cur.GetChildForField("type") ?? cur.GetChildForField("returns");
                }
                return null;
            case "initializer_expression":
            {
                // 集合 / 数组初始化器的元素：new List<T> { new() }、new T[] { new() }、List<T> x = new() { new() }
                var container = parent.Parent;
                var containerType = container?.Type switch
                {
                    "object_creation_expression" or "array_creation_expression" => container.GetChildForField("type"),
                    "implicit_object_creation_expression" => TargetTypeNode(container, depth + 1),
                    _ => null,
                };
                if (containerType is { Type: "array_type" }) return containerType.GetChildForField("type");
                if (containerType is { Type: "generic_name" } generic
                    && generic.NamedChildren.FirstOrDefault(c => c.Type == "type_argument_list") is { } args && args.NamedChildren.Count == 1)
                    return args.NamedChildren[0];
                return null;
            }
            default:
                return null;
        }
    }

    private static bool Same(Node? a, Node? b) =>
        a is not null && b is not null && a.StartIndex == b.StartIndex && a.EndIndex == b.EndIndex && a.Type == b.Type;

    private static string Argc(Node callOrCreation)
    {
        var args = callOrCreation.GetChildForField("arguments") ?? callOrCreation.NamedChildren.FirstOrDefault(c => c.Type == "argument_list");
        return (args?.NamedChildren.Count(a => a.Type == "argument") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ScriptBinding Receiver(Node? receiver, Node at, FileState state, string argc)
    {
        if (receiver is null) return new ScriptBinding(ScriptReceiver.Other, null, argc);
        switch (receiver.Type)
        {
            case "this":
                return new ScriptBinding(ScriptReceiver.This, null, argc);
            case "base":
                return new ScriptBinding(ScriptReceiver.This, HeuristicEdgeResolver.CSharpBaseMarker, argc);
            case "identifier":
                if (LookupLocal(receiver.Text, at, state, out var type) || LookupField(receiver.Text, at, state, out type))
                    return new ScriptBinding(ScriptReceiver.Other, type, argc);
                return new ScriptBinding(ScriptReceiver.Import, receiver.Text, argc);
            case "generic_name":
                // Foo<int>.Create()：泛型类型的静态调用
                return receiver.NamedChildren.FirstOrDefault(c => c.Type == "identifier") is { } gid
                    ? new ScriptBinding(ScriptReceiver.Import, gid.Text, argc)
                    : new ScriptBinding(ScriptReceiver.Other, null, argc);
            case "predefined_type":
                return new ScriptBinding(ScriptReceiver.Import, receiver.Text, argc);
            case "member_access_expression":
            {
                var obj = receiver.GetChildForField("expression");
                var name = receiver.GetChildForField("name");
                if (obj is null || name is not { Type: "identifier" }) return new ScriptBinding(ScriptReceiver.Other, null, argc);
                if (obj.Type == "this")
                    return LookupField(name.Text, at, state, out var fieldType)
                        ? new ScriptBinding(ScriptReceiver.Other, fieldType, argc)
                        : new ScriptBinding(ScriptReceiver.Import, name.Text, argc);
                // a.b.C.m()：全是标识符、且根不是局部名 / 字段时当作（带命名空间的）类型名；否则按成员链推
                var chain = DottedChain(receiver);
                if (chain is null || LookupLocal(chain[0], at, state, out _) || LookupField(chain[0], at, state, out _))
                    return new ScriptBinding(ScriptReceiver.Other, Chain(receiver, at, state), argc);
                return new ScriptBinding(ScriptReceiver.Import, string.Join('.', chain), argc);
            }
            case "invocation_expression" or "await_expression":
                return new ScriptBinding(ScriptReceiver.Other, Chain(receiver, at, state), argc);
            case "object_creation_expression" or "cast_expression":
                return new ScriptBinding(ScriptReceiver.Other, receiver.GetChildForField("type") is { } t ? TypeName(t, at) : null, argc);
            case "as_expression":
                return new ScriptBinding(ScriptReceiver.Other, receiver.GetChildForField("right") is { } r ? TypeName(r, at) : null, argc);
            case "parenthesized_expression":
                return receiver.NamedChildren.FirstOrDefault(c => c.Type != "comment") is { } inner
                    ? Receiver(inner, at, state, argc)
                    : new ScriptBinding(ScriptReceiver.Other, null, argc);
            default:
                return new ScriptBinding(ScriptReceiver.Other, null, argc);
        }
    }

    /// <summary>
    /// 接收者表达式的"类型描述"，解析器据此一步步推类型（见 HeuristicEdgeResolver.CSharp.cs 的 EvalChain）。
    /// 不含 '|' 的就是类型写法；否则是 根|步|步…：根为类型写法、"@this"（本类）或 "?名字"（本文件不认识的名字：父类成员或类型名）；
    /// 步为 "成员"（字段 / 属性）、"方法(实参个数)" 或 "await"。推不出来为 null。
    /// </summary>
    private static string? Chain(Node expr, Node at, FileState state)
    {
        if (state.Depth > 8) return null;
        state.Depth++;
        try
        {
            switch (expr.Type)
            {
                case "identifier":
                    if (LookupLocal(expr.Text, at, state, out var t) || LookupField(expr.Text, at, state, out t)) return t;
                    return "?" + expr.Text;
                case "this" or "base":
                    return "@this";
                case "predefined_type":
                    return expr.Text;
                case "generic_name":
                    return expr.NamedChildren.FirstOrDefault(c => c.Type == "identifier") is { } gid ? "?" + gid.Text : null;
                case "member_access_expression":
                {
                    var name = MemberName(expr.GetChildForField("name"));
                    var obj = expr.GetChildForField("expression");
                    if (name is null || obj is null) return null;
                    if (obj.Type == "this" && LookupField(name, at, state, out var fieldType)) return fieldType;
                    var inner = Chain(obj, at, state);
                    return inner is null ? null : inner + "|" + name;
                }
                case "invocation_expression":
                {
                    var function = expr.GetChildForField("function");
                    var argc = Argc(expr);
                    switch (function?.Type)
                    {
                        case "identifier" or "generic_name":
                        {
                            var name = MemberName(function);
                            if (name is null || LookupLocal(name, at, state, out _) || LookupField(name, at, state, out _)) return null;
                            return "@this|" + name + "(" + argc + ")";
                        }
                        case "member_access_expression":
                        {
                            var name = MemberName(function.GetChildForField("name"));
                            var obj = function.GetChildForField("expression");
                            if (name is null || obj is null) return null;
                            var inner = Chain(obj, at, state);
                            return inner is null ? null : inner + "|" + name + "(" + argc + ")";
                        }
                        default:
                            return null;
                    }
                }
                case "await_expression":
                {
                    var inner = expr.NamedChildren.FirstOrDefault(c => c.Type != "comment");
                    var chain = inner is null ? null : Chain(inner, at, state);
                    return chain is null ? null : chain + "|await";
                }
                case "object_creation_expression" or "cast_expression":
                    return expr.GetChildForField("type") is { } ct ? TypeName(ct, at) : null;
                case "as_expression":
                    return expr.GetChildForField("right") is { } r ? TypeName(r, at) : null;
                case "parenthesized_expression":
                    return expr.NamedChildren.FirstOrDefault(c => c.Type != "comment") is { } p ? Chain(p, at, state) : null;
                default:
                    return null;
            }
        }
        finally { state.Depth--; }
    }

    private static string? MemberName(Node? node) => node?.Type switch
    {
        "identifier" => node.Text,
        "generic_name" => node.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text,
        _ => null,
    };

    private static List<string>? DottedChain(Node node)
    {
        var parts = new List<string>();
        var cur = node;
        while (cur.Type == "member_access_expression")
        {
            var name = cur.GetChildForField("name");
            var obj = cur.GetChildForField("expression");
            if (name is not { Type: "identifier" } || obj is null) return null;
            parts.Add(name.Text);
            cur = obj;
        }
        if (cur.Type != "identifier") return null;
        parts.Add(cur.Text);
        parts.Reverse();
        return parts;
    }

    /// <summary>类型节点的写法：去泛型实参与可空标记；数组、元组、指针返回 null；外层声明的类型参数（T）返回 null。</summary>
    private static string? TypeName(Node type, Node at)
    {
        switch (type.Type)
        {
            case "identifier":
                return IsTypeParameter(type.Text, at, out var constraint) ? constraint : type.Text;
            case "generic_name":
                return type.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text;
            case "qualified_name":
            {
                var sb = new StringBuilder();
                foreach (var part in QualifiedParts(type))
                {
                    if (sb.Length > 0) sb.Append('.');
                    sb.Append(part);
                }
                return sb.Length == 0 ? null : sb.ToString();
            }
            case "alias_qualified_name":
                return type.GetChildForField("name") is { } n ? TypeName(n, at) : null;
            case "nullable_type":
                return type.GetChildForField("type") is { } inner ? TypeName(inner, at) : null;
            case "predefined_type":
                return type.Text;
            default:
                return null;
        }
    }

    private static IEnumerable<string> QualifiedParts(Node node)
    {
        if (node.Type == "qualified_name")
        {
            if (node.GetChildForField("qualifier") is { } q) foreach (var p in QualifiedParts(q)) yield return p;
            if (node.GetChildForField("name") is { } n) foreach (var p in QualifiedParts(n)) yield return p;
        }
        else if (node.Type == "generic_name") { if (node.NamedChildren.FirstOrDefault(c => c.Type == "identifier") is { } id) yield return id.Text; }
        else if (node.Type == "identifier") yield return node.Text;
    }

    /// <summary>
    /// name 是不是外层类型 / 方法 / 局部函数声明的类型参数；是的话 constraint 为 where 约束里第一个具体类型（where T : IEntity → IEntity），没有为 null。
    /// </summary>
    private static bool IsTypeParameter(string name, Node at, out string? constraint)
    {
        constraint = null;
        for (var cur = at.Parent; cur is not null; cur = cur.Parent)
        {
            if (!TypeDeclarations.Contains(cur.Type) && !MethodLike.Contains(cur.Type) && cur.Type != "delegate_declaration") continue;
            var tps = cur.GetChildForField("type_parameters") ?? cur.NamedChildren.FirstOrDefault(c => c.Type == "type_parameter_list");
            if (tps is null) continue;
            foreach (var tp in tps.NamedChildren)
            {
                if (tp.Type != "type_parameter" || (tp.GetChildForField("name") ?? tp.NamedChildren.FirstOrDefault(c => c.Type == "identifier"))?.Text != name) continue;
                foreach (var clause in cur.NamedChildren.Where(c => c.Type == "type_parameter_constraints_clause"))
                {
                    if (clause.NamedChildren.FirstOrDefault()?.Text != name) continue;
                    foreach (var c in clause.NamedChildren.Where(c => c.Type == "type_parameter_constraint"))
                        if (c.GetChildForField("type") is { } ct && ct.Type is "identifier" or "generic_name" or "qualified_name")
                        {
                            constraint = ct.Type == "identifier" ? ct.Text : TypeName(ct, cur);
                            return true;
                        }
                }
                return true;
            }
        }
        return false;
    }

    // ---------- 局部名与字段 ----------

    /// <summary>at 处可见的局部名（不分块作用域：取声明在 at 之前的最后一个）。found 表示是局部名 / 参数（类型可能仍未知）。</summary>
    private static bool LookupLocal(string name, Node at, FileState state, out string? type)
    {
        type = null;
        if (EnclosingMember(at) is not { } member) return false;
        if (!state.MemberLocals.TryGetValue(member.StartIndex, out var locals))
        {
            locals = BuildLocals(member);
            state.MemberLocals[member.StartIndex] = locals;
        }
        if (!locals.TryGetValue(name, out var entries)) return false;
        var found = false;
        Node? init = null;
        foreach (var (position, t, i) in entries)
            if (position < at.StartIndex) { type = t; init = i; found = true; }
        if (found && init is not null)
        {
            // var x = 调用链：用到时再推（初始化表达式里只看得到它之前声明的局部名）
            if (!state.InitChains.TryGetValue(init.StartIndex, out type))
            {
                state.InitChains[init.StartIndex] = null; // 占位：var x = x.Foo() 之类不会无限递归
                type = Chain(init, init, state);
                state.InitChains[init.StartIndex] = type;
            }
        }
        return found;
    }

    /// <summary>本文件里由内向外的外层类型直接声明的字段、属性与主构造参数。</summary>
    private static bool LookupField(string name, Node at, FileState state, out string? type)
    {
        type = null;
        for (var cur = at.Parent; cur is not null; cur = cur.Parent)
        {
            if (!TypeDeclarations.Contains(cur.Type)) continue;
            if (!state.Fields.TryGetValue(cur.StartIndex, out var fields))
            {
                fields = FieldsOf(cur);
                state.Fields[cur.StartIndex] = fields;
            }
            if (fields.TryGetValue(name, out type)) return true;
        }
        return false;
    }

    private static Dictionary<string, string?> FieldsOf(Node type)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        // 主构造参数 / 记录的位置参数
        if (type.NamedChildren.FirstOrDefault(c => c.Type == "parameter_list") is { } header)
            foreach (var p in header.NamedChildren.Where(p => p.Type == "parameter"))
                if (p.GetChildForField("name") is { } n) fields.TryAdd(n.Text, p.GetChildForField("type") is { } t ? TypeName(t, p) : null);
        var body = type.GetChildForField("body") ?? type.NamedChildren.FirstOrDefault(c => c.Type == "declaration_list");
        if (body is null) return fields;
        foreach (var m in body.NamedChildren)
        {
            switch (m.Type)
            {
                case "field_declaration" or "event_field_declaration":
                {
                    var decl = m.NamedChildren.FirstOrDefault(c => c.Type == "variable_declaration");
                    if (decl is null) break;
                    var typeText = decl.GetChildForField("type") is { } t ? TypeName(t, m) : null;
                    foreach (var d in decl.NamedChildren.Where(c => c.Type == "variable_declarator"))
                        if ((d.GetChildForField("name") ?? d.NamedChildren.FirstOrDefault(c => c.Type == "identifier")) is { } n) fields.TryAdd(n.Text, typeText);
                    break;
                }
                case "property_declaration":
                    if (m.GetChildForField("name") is { } pn) fields.TryAdd(pn.Text, m.GetChildForField("type") is { } pt ? TypeName(pt, m) : null);
                    break;
            }
        }
        return fields;
    }

    /// <summary>最外层的成员（方法 / 构造 / 属性 / 字段 / 顶级语句）：lambda、局部函数里的调用也看得到外层方法的局部名。</summary>
    private static Node? EnclosingMember(Node node)
    {
        Node? member = null;
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            if (MemberDeclarations.Contains(cur.Type)) member = cur;
            if (TypeDeclarations.Contains(cur.Type)) break;
        }
        // 顶级语句：从 compilation_unit 收（所有 global_statement 共享局部名）
        if (member is { Type: "global_statement" } && member.Parent is { } unit) return unit;
        return member;
    }

    /// <summary>成员里声明的名字：参数（含 lambda、局部函数）、局部变量（var 取 new / 强转 / as 的类型）、foreach、catch、模式变量、out 变量。</summary>
    private static Locals BuildLocals(Node member)
    {
        var locals = new Locals();
        void Add(Node? nameNode, string? type, Node? init = null)
        {
            if (nameNode is null) return;
            if (!locals.TryGetValue(nameNode.Text, out var list)) locals[nameNode.Text] = list = [];
            list.Add((nameNode.StartIndex, type, init));
        }
        // 顶级语句：只收 global_statement（同文件类型声明里的参数 / 局部变量不是顶级语句的局部名）
        var roots = member.Type == "compilation_unit" ? member.NamedChildren.Where(c => c.Type == "global_statement") : [member];
        foreach (var n in roots.SelectMany(Descendants))
        {
            switch (n.Type)
            {
                case "parameter":
                    // 主构造参数、类型里的方法参数由 FieldsOf / 各自的成员负责
                    Add(n.GetChildForField("name"), n.GetChildForField("type") is { } pt ? TypeName(pt, n) : null);
                    break;
                case "implicit_parameter":
                    Add(n, null);
                    break;
                case "variable_declaration":
                {
                    var declared = n.GetChildForField("type");
                    foreach (var d in n.NamedChildren.Where(c => c.Type == "variable_declarator"))
                    {
                        var name = d.GetChildForField("name") ?? d.NamedChildren.FirstOrDefault(c => c.Type == "identifier");
                        string? type = null;
                        Node? deferred = null;
                        if (declared is { Type: "implicit_type" })
                        {
                            var init = d.NamedChildren.LastOrDefault(c => !Same(c, name));
                            if (init is { Type: "equals_value_clause" }) init = init.NamedChildren.FirstOrDefault();
                            type = InitializerType(init, n);
                            if (type is null && init is { Type: "invocation_expression" or "await_expression" or "member_access_expression" or "identifier" or "parenthesized_expression" }) deferred = init;
                        }
                        else if (declared is not null) type = TypeName(declared, n);
                        Add(name, type, deferred);
                    }
                    break;
                }
                case "foreach_statement":
                {
                    var t = n.GetChildForField("type");
                    var left = n.GetChildForField("left");
                    if (left is { Type: "identifier" }) Add(left, t is null or { Type: "implicit_type" } ? null : TypeName(t, n));
                    break;
                }
                case "catch_declaration":
                    Add(n.GetChildForField("name"), n.GetChildForField("type") is { } ct ? TypeName(ct, n) : null);
                    break;
                case "declaration_pattern" or "declaration_expression":
                {
                    var t = n.GetChildForField("type");
                    var name = n.GetChildForField("name");
                    if (name is { Type: "identifier" }) Add(name, t is null or { Type: "implicit_type" } ? null : TypeName(t, n));
                    break;
                }
            }
        }
        return locals;
    }

    private static string? InitializerType(Node? init, Node at)
    {
        while (init is { Type: "parenthesized_expression" } && init.NamedChildren.FirstOrDefault() is { } inner) init = inner;
        return init?.Type switch
        {
            "object_creation_expression" or "cast_expression" => init.GetChildForField("type") is { } t ? TypeName(t, at) : null,
            "as_expression" => init.GetChildForField("right") is { } r ? TypeName(r, at) : null,
            _ => null,
        };
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            var children = cur.NamedChildren;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    [GeneratedRegex("^I[A-Z]")]
    private static partial Regex InterfaceName();

    [GeneratedRegex("(Handler|Consumer)$")]
    private static partial Regex HandlerInterface();
}
