using System.Text;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// Java：每个 .java 一个 Module 符号；限定名照 C# 的命名空间风格：类型 &lt;package&gt;.&lt;Type&gt;（嵌套 &lt;package&gt;.&lt;Outer&gt;.&lt;Inner&gt;），
/// 方法 &lt;package&gt;.&lt;Type&gt;.&lt;method&gt;(&lt;参数类型简名，逗号分隔&gt;)，构造函数 &lt;Type&gt;.&lt;Type&gt;(…)，字段 &lt;package&gt;.&lt;Type&gt;.&lt;field&gt;。
/// StartLine / StartCol 是声明行（第一个非注解的修饰符，或返回类型 / class 关键字），不含注解；Signature 去掉注解，
/// 字段的 Signature 是"修饰符 + 声明类型 + 名字"（解析器从这里读继承来的字段的类型）。Module 的 Signature 是 "package x.y"。
/// 可见性：public / protected / private，缺省 "package"；接口与注解类型的成员缺省 public。
/// <para>引用借用 <see cref="ScriptBinding"/> 记接收者，ImportedName 统一是实参个数（"-1" 为方法引用，个数不限）：</para>
/// <list type="bullet">
/// <item>None：new T(…)（ImportSource 是 T 的写法，去泛型）；类型引用 / extends / implements 同样记写法（ImportSource）。</item>
/// <item>This：m(…) / this.m(…)；super.m(…) 的 ImportSource 为 "super"。</item>
/// <item>Other：接收者类型读得出来时 ImportSource 是类型写法（局部变量 / 参数 / 本文件外层类的字段 / new / 强转），读不出来为 null；
/// 接收者是调用链 a.b().c() 时 ImportSource 是 "()" + 链根的类型写法（或不认识的名字）。</item>
/// <item>Import：接收者是个名字，但不是本文件可见的局部变量 / 参数 / 字段（类型名的静态调用、继承来的字段、a.b.C 全限定名），ImportSource 是这个名字。</item>
/// </list>
/// </summary>
public sealed class JavaAdapter() : LanguageAdapterBase(
    "java", "Java", [".java"],
    QueryResources.Load("java.definitions.scm"),
    QueryResources.Load("java.references.scm"))
{
    private static readonly HashSet<string> TypeDeclarations = new(StringComparer.Ordinal)
    {
        "class_declaration", "interface_declaration", "enum_declaration", "record_declaration", "annotation_type_declaration",
    };

    private static readonly HashSet<string> MethodDeclarations = new(StringComparer.Ordinal)
    {
        "method_declaration", "constructor_declaration", "compact_constructor_declaration", "annotation_type_element_declaration",
    };

    /// <summary>局部名：名字 → (声明位置, 类型写法)，类型未知为 null。</summary>
    private sealed class Locals : Dictionary<string, List<(int Position, string? Type)>>
    {
        public Locals() : base(StringComparer.Ordinal) { }
    }

    private sealed class FileState
    {
        /// <summary>成员（方法 / 构造 / 初始化块 / 字段）起点 → 其中声明的局部名。</summary>
        public Dictionary<int, Locals> MemberLocals { get; } = [];
        /// <summary>类型声明 / 匿名类起点 → 它直接声明的字段（含记录组件、枚举常量）。</summary>
        public Dictionary<int, Dictionary<string, string?>> Fields { get; } = [];
    }

    protected override string ModuleQualifiedPrefix(string relativePath) => "";

    protected override bool ImportIsPath(string target) => false;

    // ---- 调用大纲：调用所在控制结构 ----

    /// <summary>
    /// for / 增强 for / while / do 只有 body 算 loop（init / condition / update、增强 for 的被遍历对象都是循环头）；
    /// if 的 consequence / alternative 与三元的两个值算 cond，条件不算；switch 语句与 switch 表达式的 case 体（switch_block）算 cond，被 switch 的值不算；
    /// try / try-with-resources 的资源、try 块、finally 算 try，catch 子句算 catch；
    /// lambda 的 body、方法引用 X::m、匿名类（含枚举常量的类体）里的调用算 callback——匿名类的方法不单列符号，调用归外层方法，所以它们不是边界。
    /// </summary>
    protected override string? ContextOf(Node ancestor, Node child) => ancestor.Type switch
    {
        "for_statement" or "enhanced_for_statement" or "while_statement" or "do_statement" =>
            IsField(ancestor, "body", child) ? CallContext.Loop : null,
        "if_statement" or "ternary_expression" =>
            IsField(ancestor, "consequence", child) || IsField(ancestor, "alternative", child) ? CallContext.Cond : null,
        "switch_expression" or "switch_statement" => IsField(ancestor, "body", child) ? CallContext.Cond : null,
        "try_statement" or "try_with_resources_statement" => child.Type == "catch_clause" ? CallContext.Catch : CallContext.Try,
        "lambda_expression" => IsField(ancestor, "body", child) ? CallContext.Callback : null,
        "method_reference" => CallContext.Callback,
        "class_body" when IsAnonymousBody(ancestor) => CallContext.Callback,
        _ => null,
    };

    /// <summary>具名类型 / 具名类型里的方法与构造函数是边界；匿名类里的方法不是（往上走到匿名类体记 callback）。</summary>
    protected override bool IsContextBoundary(Node ancestor) =>
        TypeDeclarations.Contains(ancestor.Type)
        || (MethodDeclarations.Contains(ancestor.Type) && !(ancestor.Parent is { Type: "class_body" } body && IsAnonymousBody(body)))
        || ancestor.Type == "program";

    private static bool IsAnonymousBody(Node classBody) => classBody.Parent?.Type is "object_creation_expression" or "enum_constant";

    /// <summary>字段的定义节点是 variable_declarator，范围扩到整个 field_declaration / constant_declaration，让类型引用与初始化器里的调用归属到字段。</summary>
    protected override Node DefinitionScope(Node definition, Node root) =>
        definition.Type == "variable_declarator" && definition.Parent is { } decl ? decl : definition;

    protected override string? ExtractVisibility(Node definition)
    {
        if (IsRecordComponent(definition)) return "private";
        var decl = definition.Type == "variable_declarator" ? definition.Parent : definition;
        if (decl is null) return null;
        foreach (var child in decl.Children)
        {
            if (child.Type != "modifiers") continue;
            foreach (var m in child.Children)
                if (m.Text is "public" or "protected" or "private") return m.Text;
        }
        return decl.Parent?.Type is "interface_body" or "annotation_type_body" ? "public" : "package";
    }

    protected override string ReferenceName(Node node) =>
        node.Type == "identifier" ? node.Text : TypeName(node) is { } t ? LastSegment(t) : "";

    protected override bool AcceptReference(string captureName, Node node)
    {
        switch (captureName)
        {
            case "calls":
                // Foo::new 的最后一个具名子节点是 Foo：不是方法调用
                if (node.Parent is { Type: "method_reference" } mr && mr.NamedChildren.FirstOrDefault() is { } first && Same(first, node)) return false;
                return node.Type == "identifier" || TypeName(node) is not null;
            case "references":
            {
                if (node.Parent is { Type: "scoped_type_identifier" }) return false; // 只记最外层的 a.b.C
                if (node.Type == "type_identifier" && node.Text == "var") return false;
                if (!InTypePosition(node)) return false;
                var name = TypeName(node);
                return name is not null && !IsTypeParameter(name.Split('.')[0], node);
            }
            case "inherits" or "implements":
                return TypeName(node) is { } n && !IsTypeParameter(n.Split('.')[0], node);
            default:
                return true;
        }
    }

    /// <summary>类型节点（含泛型实参、数组元素里的）是不是字段 / 参数 / 返回值 / 局部变量的声明类型。</summary>
    private static bool InTypePosition(Node node)
    {
        var cur = node;
        while (cur.Parent is { Type: "generic_type" or "type_arguments" or "array_type" or "wildcard" or "annotated_type" } p) cur = p;
        var parent = cur.Parent;
        return parent?.Type switch
        {
            "field_declaration" or "constant_declaration" or "formal_parameter" or "method_declaration" or "local_variable_declaration"
                or "enhanced_for_statement" or "resource" or "annotation_type_element_declaration" => parent.GetChildForField("type") is { } t && Same(t, cur),
            "spread_parameter" => cur.Type != "variable_declarator" && cur.Type != "modifiers",
            _ => false,
        };
    }

    /// <summary>name 是不是外层类型 / 方法声明的类型参数（class Box&lt;T&gt;、&lt;R&gt; R map(…)）。</summary>
    private static bool IsTypeParameter(string name, Node node)
    {
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            if (!TypeDeclarations.Contains(cur.Type) && !MethodDeclarations.Contains(cur.Type)) continue;
            var tps = cur.GetChildForField("type_parameters") ?? cur.NamedChildren.FirstOrDefault(c => c.Type == "type_parameters");
            if (tps is null) continue;
            foreach (var tp in tps.NamedChildren)
                if (tp.Type == "type_parameter" && tp.NamedChildren.FirstOrDefault(c => c.Type is "type_identifier" or "identifier") is { } id && id.Text == name)
                    return true;
        }
        return false;
    }

    // ---------- 引用绑定 ----------

    protected override object? PrepareFile(Node root) => new FileState();

    protected override ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState)
    {
        if (fileState is not FileState state) return reference;
        switch (captureName)
        {
            case "calls":
                switch (node.Parent)
                {
                    case { Type: "object_creation_expression" } creation:
                        return reference with { Script = new ScriptBinding(ScriptReceiver.None, TypeName(node), Argc(creation)) };
                    case { Type: "method_reference" } mr:
                        return reference with { Script = Receiver(mr.NamedChildren.First(), node, state, HeuristicEdgeResolver.JavaAnyArity) };
                    case { Type: "method_invocation" } call:
                        return reference with { Script = Receiver(call.GetChildForField("object"), node, state, Argc(call)) };
                    default:
                        return reference;
                }
            case "references" or "inherits" or "implements":
                return reference with { Script = new ScriptBinding(ScriptReceiver.None, TypeName(node)) };
            default:
                return reference;
        }
    }

    private static string Argc(Node callOrCreation)
    {
        var args = callOrCreation.GetChildForField("arguments");
        return (args?.NamedChildren.Count(a => !IsComment(a)) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private ScriptBinding Receiver(Node? receiver, Node at, FileState state, string argc)
    {
        if (receiver is null) return new ScriptBinding(ScriptReceiver.This, null, argc);
        switch (receiver.Type)
        {
            case "this":
                return new ScriptBinding(ScriptReceiver.This, null, argc);
            case "super":
                return new ScriptBinding(ScriptReceiver.This, HeuristicEdgeResolver.JavaSuperMarker, argc);
            case "identifier":
                if (LookupLocal(receiver.Text, at, state, out var localType) || LookupField(receiver.Text, at, state, out localType))
                    return new ScriptBinding(ScriptReceiver.Other, localType, argc);
                return new ScriptBinding(ScriptReceiver.Import, receiver.Text, argc);
            case "field_access":
            {
                var obj = receiver.GetChildForField("object");
                var field = receiver.GetChildForField("field");
                if (field is null || obj is null) return new ScriptBinding(ScriptReceiver.Other, null, argc);
                if (field.Type == "this") return new ScriptBinding(ScriptReceiver.This, null, argc); // Outer.this.m()
                if (obj.Type == "this")
                    return LookupField(field.Text, at, state, out var fieldType)
                        ? new ScriptBinding(ScriptReceiver.Other, fieldType, argc)
                        : new ScriptBinding(ScriptReceiver.Import, field.Text, argc);
                // a.b.C.m()：全是标识符、且根不是变量 / 字段时当作（全限定）类型名
                var chain = DottedChain(receiver);
                if (chain is null || LookupLocal(chain[0], at, state, out _) || LookupField(chain[0], at, state, out _))
                    return new ScriptBinding(ScriptReceiver.Other, null, argc);
                return new ScriptBinding(ScriptReceiver.Import, string.Join('.', chain), argc);
            }
            case "object_creation_expression" or "cast_expression":
                return new ScriptBinding(ScriptReceiver.Other, receiver.GetChildForField("type") is { } t ? TypeName(t) : null, argc);
            case "parenthesized_expression":
                return receiver.NamedChildren.FirstOrDefault(c => !IsComment(c)) is { } inner
                    ? Receiver(inner, at, state, argc)
                    : new ScriptBinding(ScriptReceiver.Other, null, argc);
            case "method_invocation":
                // 调用链 a.b().c()：返回值类型不推，记下链根的类型（或名字），解析器在链根是第三方类型时把方法归到它
                return new ScriptBinding(ScriptReceiver.Other, ChainRoot(receiver, at, state) is { } root ? HeuristicEdgeResolver.JavaChainMarker + root : null, argc);
            default:
                return new ScriptBinding(ScriptReceiver.Other, null, argc);
        }
    }

    /// <summary>调用链最左端接收者的类型写法；是个不认识的名字（类型名、继承来的字段）时就是这个名字；读不出来为 null。</summary>
    private static string? ChainRoot(Node call, Node at, FileState state)
    {
        var cur = call;
        for (var i = 0; i < 64 && cur.Type == "method_invocation"; i++)
        {
            if (cur.GetChildForField("object") is not { } obj) return null; // foo().bar()：链根是本类方法
            cur = obj;
        }
        while (cur.Type == "parenthesized_expression" && cur.NamedChildren.FirstOrDefault(c => !IsComment(c)) is { } inner) cur = inner;
        switch (cur.Type)
        {
            case "identifier":
                if (LookupLocal(cur.Text, at, state, out var type) || LookupField(cur.Text, at, state, out type)) return type;
                return cur.Text;
            case "field_access" when cur.GetChildForField("object") is { Type: "this" } && cur.GetChildForField("field") is { Type: "identifier" } f:
                return LookupField(f.Text, at, state, out var fieldType) ? fieldType : f.Text;
            case "object_creation_expression" or "cast_expression":
                return cur.GetChildForField("type") is { } t ? TypeName(t) : null;
            default:
                return null;
        }
    }

    private static List<string>? DottedChain(Node node)
    {
        var parts = new List<string>();
        var cur = node;
        while (cur.Type == "field_access")
        {
            var field = cur.GetChildForField("field");
            var obj = cur.GetChildForField("object");
            if (field is not { Type: "identifier" } || obj is null) return null;
            parts.Add(field.Text);
            cur = obj;
        }
        if (cur.Type != "identifier") return null;
        parts.Add(cur.Text);
        parts.Reverse();
        return parts;
    }

    // ---------- 局部变量 / 参数 / 字段 ----------

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
        foreach (var (position, t) in entries)
            if (position < at.StartIndex) { type = t; found = true; }
        return found;
    }

    /// <summary>本文件里由内向外的外层类型（含匿名类）直接声明的字段。</summary>
    private static bool LookupField(string name, Node at, FileState state, out string? type)
    {
        type = null;
        for (var cur = at.Parent; cur is not null; cur = cur.Parent)
        {
            var isAnonymous = cur.Type == "object_creation_expression" && cur.NamedChildren.Any(c => c.Type == "class_body");
            if (!TypeDeclarations.Contains(cur.Type) && !isAnonymous) continue;
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
        // 记录组件 record P(Item left, …)
        if (type.Type == "record_declaration" && type.GetChildForField("parameters") is { } header)
            foreach (var p in header.NamedChildren.Where(p => p.Type == "formal_parameter"))
                if (p.GetChildForField("name") is { } n) fields.TryAdd(n.Text, p.GetChildForField("type") is { } t ? TypeName(t) : null);
        var body = type.GetChildForField("body") ?? type.NamedChildren.FirstOrDefault(c => c.Type == "class_body");
        if (body is null) return fields;
        var members = body.NamedChildren.ToList();
        if (body.Type == "enum_body")
        {
            // 枚举常量的类型就是这个枚举
            var enumName = type.GetChildForField("name")?.Text;
            foreach (var c in members.Where(c => c.Type == "enum_constant"))
                if (c.GetChildForField("name") is { } n) fields.TryAdd(n.Text, enumName);
            members = members.Where(c => c.Type == "enum_body_declarations").SelectMany(c => c.NamedChildren).ToList();
        }
        foreach (var m in members.Where(m => m.Type is "field_declaration" or "constant_declaration"))
        {
            var typeText = m.GetChildForField("type") is { } t ? TypeName(t) : null;
            foreach (var d in m.GetChildrenForField("declarator"))
                if (d.GetChildForField("name") is { } n) fields.TryAdd(n.Text, typeText);
        }
        return fields;
    }

    /// <summary>最外层的成员（方法 / 构造 / 初始化块 / 字段）：匿名类、lambda 里的调用也看得到外层方法的局部名。</summary>
    private static Node? EnclosingMember(Node node)
    {
        Node? member = null;
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            if (MethodDeclarations.Contains(cur.Type) || cur.Type is "static_initializer" or "field_declaration" or "constant_declaration"
                || (cur.Type == "block" && cur.Parent is { Type: "class_body" or "enum_body_declarations" }))
                member = cur;
        }
        return member;
    }

    /// <summary>成员里声明的名字：参数（含 lambda、catch、for、try 资源）、局部变量（var 取 new / 强转的类型）、instanceof 模式变量。</summary>
    private static Locals BuildLocals(Node member)
    {
        var locals = new Locals();
        foreach (var n in Descendants(member))
        {
            switch (n.Type)
            {
                case "formal_parameter":
                    if (n.GetChildForField("name") is { } pn) Add(locals, pn, n.GetChildForField("type") is { } pt ? TypeName(pt) : null);
                    break;
                case "spread_parameter":
                    // String... rest 是数组：类型不记
                    if (n.NamedChildren.FirstOrDefault(c => c.Type == "variable_declarator")?.GetChildForField("name") is { } sn) Add(locals, sn, null);
                    break;
                case "local_variable_declaration":
                {
                    var typeNode = n.GetChildForField("type");
                    foreach (var d in n.GetChildrenForField("declarator"))
                        if (d.GetChildForField("name") is { } dn) Add(locals, dn, DeclaredType(typeNode, d.GetChildForField("value")));
                    break;
                }
                case "enhanced_for_statement" or "resource":
                    if (n.GetChildForField("name") is { } en) Add(locals, en, DeclaredType(n.GetChildForField("type"), n.GetChildForField("value"), forEach: n.Type == "enhanced_for_statement"));
                    break;
                case "catch_formal_parameter":
                {
                    var types = n.NamedChildren.FirstOrDefault(c => c.Type == "catch_type")?.NamedChildren.ToList() ?? [];
                    if (n.GetChildForField("name") is { } cn) Add(locals, cn, types.Count == 1 ? TypeName(types[0]) : null);
                    break;
                }
                case "lambda_expression":
                {
                    var ps = n.GetChildForField("parameters");
                    if (ps is { Type: "identifier" }) Add(locals, ps, null);
                    else if (ps is { Type: "inferred_parameters" })
                        foreach (var id in ps.NamedChildren.Where(c => c.Type == "identifier")) Add(locals, id, null);
                    break; // 带类型的 lambda 参数是 formal_parameter，上面已收
                }
                case "instanceof_expression":
                    if (n.GetChildForField("name") is { } iname) Add(locals, iname, n.GetChildForField("right") is { } rt ? TypeName(rt) : null);
                    break;
            }
        }
        return locals;
    }

    /// <summary>声明类型；var 取初始化表达式里能直接读出的类型（new T(…)、(T) x）。</summary>
    private static string? DeclaredType(Node? typeNode, Node? value, bool forEach = false)
    {
        if (typeNode is null) return null;
        if (typeNode.Type == "type_identifier" && typeNode.Text == "var")
            return forEach || value is null ? null : ValueType(value);
        return TypeName(typeNode);
    }

    private static string? ValueType(Node value) => value.Type switch
    {
        "object_creation_expression" or "cast_expression" => value.GetChildForField("type") is { } t ? TypeName(t) : null,
        "parenthesized_expression" => value.NamedChildren.FirstOrDefault(c => !IsComment(c)) is { } inner ? ValueType(inner) : null,
        _ => null,
    };

    private static void Add(Locals locals, Node name, string? type)
    {
        if (!locals.TryGetValue(name.Text, out var list)) locals[name.Text] = list = [];
        list.Add((name.StartIndex, type));
    }

    // ---------- 类型写法 ----------

    /// <summary>类型写法（去泛型实参与空白）：Foo、Outer.Inner、java.util.List；数组 / 基本类型 / void 返回 null。</summary>
    private static string? TypeName(Node t) => t.Type switch
    {
        "type_identifier" => t.Text,
        "scoped_type_identifier" => ScopedName(t),
        "generic_type" => t.NamedChildren.FirstOrDefault(c => c.Type is "type_identifier" or "scoped_type_identifier") is { } raw ? TypeName(raw) : null,
        "annotated_type" => t.NamedChildren.LastOrDefault(c => c.Type is not ("annotation" or "marker_annotation")) is { } inner ? TypeName(inner) : null,
        _ => null,
    };

    private static string? ScopedName(Node t)
    {
        var parts = new List<string>();
        foreach (var c in t.NamedChildren)
        {
            if (c.Type is "annotation" or "marker_annotation") continue;
            var part = TypeName(c);
            if (part is null) return null;
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('.', parts);
    }

    /// <summary>限定名里的参数类型简名：去泛型实参与包名，保留 [] 与 ...。</summary>
    private static string ParameterTypeName(Node t)
    {
        switch (t.Type)
        {
            case "array_type":
            {
                var element = t.GetChildForField("element");
                var dims = t.GetChildForField("dimensions")?.Text ?? "[]";
                return (element is null ? "?" : ParameterTypeName(element)) + Collapse(dims).Replace(" ", "");
            }
            case "type_identifier" or "scoped_type_identifier" or "generic_type" or "annotated_type":
                return TypeName(t) is { } name ? LastSegment(name) : Collapse(t.Text);
            default:
                return Collapse(t.Text).Replace(" ", ""); // int、boolean、void
        }
    }

    private static string LastSegment(string name) => name[(name.LastIndexOf('.') + 1)..];

    // ---------- 符号修正：限定名、声明行、签名 ----------

    /// <summary>
    /// 按包含关系重写限定名（package 前缀、方法带参数类型），StartLine / StartCol 改到声明行（跳过注解），Signature 去掉注解。
    /// 符号与定义节点按定义节点的起点对应。
    /// </summary>
    protected override void AdjustSymbols(Node root, List<ParsedSymbol> symbols)
    {
        var nodes = new Dictionary<(int Line, int Col), Node>();
        foreach (var n in Descendants(root))
            if (TypeDeclarations.Contains(n.Type) || MethodDeclarations.Contains(n.Type) || n.Type == "variable_declarator" || IsRecordComponent(n))
                nodes.TryAdd((n.StartPosition.Row + 1, n.StartPosition.Column + 1), n);

        var package = root.NamedChildren.FirstOrDefault(c => c.Type == "package_declaration")
            ?.NamedChildren.FirstOrDefault(c => c.Type is "scoped_identifier" or "identifier")?.Text;
        package = package is null ? null : Collapse(package).Replace(" ", "");
        if (!string.IsNullOrEmpty(package)) symbols[0] = symbols[0] with { Signature = "package " + package };

        for (var i = 1; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (!nodes.TryGetValue((s.StartLine, s.StartCol), out var node)) continue;
            var parentQualified = s.ParentIndex is { } p && p > 0
                ? symbols[p].QualifiedName + "."
                : string.IsNullOrEmpty(package) ? "" : package + ".";
            var decl = node.Type == "variable_declarator" && node.Parent is { } fieldDecl ? fieldDecl : node;
            var start = DeclarationStart(decl);
            var qualified = parentQualified + s.Name + (MethodDeclarations.Contains(node.Type) ? "(" + string.Join(',', Parameters(node)) + ")" : "");
            var signature = node.Type == "variable_declarator" ? FieldSignature(decl, node)
                : IsRecordComponent(node) ? "private " + FieldSignature(node, node)
                : DeclarationSignature(decl);
            symbols[i] = s with
            {
                QualifiedName = qualified,
                StartLine = start.Row + 1,
                StartCol = start.Column + 1,
                Signature = signature.Length > 200 ? signature[..200] : signature,
            };
        }
    }

    /// <summary>方法 / 构造的参数类型简名；紧凑构造函数取记录头的参数。</summary>
    private static IEnumerable<string> Parameters(Node method)
    {
        var list = method.Type == "compact_constructor_declaration"
            ? method.Parent?.Parent?.GetChildForField("parameters")   // compact ctor → class_body → record_declaration
            : method.GetChildForField("parameters");
        if (list is null) yield break;
        foreach (var p in list.NamedChildren)
        {
            switch (p.Type)
            {
                case "formal_parameter" when p.GetChildForField("type") is { } t:
                    yield return ParameterTypeName(t);
                    break;
                case "spread_parameter" when p.NamedChildren.FirstOrDefault(c => c.Type is not ("modifiers" or "variable_declarator") && !IsComment(c)) is { } st:
                    yield return ParameterTypeName(st) + "...";
                    break;
            }
        }
    }

    /// <summary>声明行：第一个非注解的修饰符；修饰符全是注解（或没有）时是其后的第一个子节点（返回类型、class 关键字……）。</summary>
    private static Point DeclarationStart(Node decl)
    {
        foreach (var child in decl.Children)
        {
            if (IsComment(child)) continue;
            if (child.Type == "modifiers")
            {
                foreach (var m in child.Children)
                    if (!IsAnnotation(m) && !IsComment(m)) return m.StartPosition;
                continue;
            }
            return child.StartPosition;
        }
        return decl.StartPosition;
    }

    /// <summary>声明的头部（到类型体 / 方法体之前），去掉注解，空白折叠成一个空格。</summary>
    private static string DeclarationSignature(Node decl)
    {
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var child in decl.Children)
        {
            if (IsComment(child)) continue;
            if (child.Type is "class_body" or "interface_body" or "enum_body" or "annotation_type_body" or "block" or "constructor_body" or ";" or "default") break;
            if (child.Type == "modifiers")
            {
                foreach (var m in child.Children)
                    if (!IsAnnotation(m) && !IsComment(m)) Append(sb, m.Text, glue: false);
                previous = child.Type;
                continue;
            }
            // 参数表紧跟名字；类的类型参数 Box<T> 也紧跟名字（方法的 <R> 在返回类型前，照常空格）
            var glue = child.Type is "formal_parameters" or "dimensions" || (child.Type == "type_parameters" && previous == "identifier");
            Append(sb, child.Text, glue);
            previous = child.Type;
        }
        return sb.ToString();
    }

    private static string FieldSignature(Node decl, Node declarator)
    {
        var sb = new StringBuilder();
        foreach (var child in decl.Children)
        {
            if (child.Type != "modifiers") continue;
            foreach (var m in child.Children)
                if (!IsAnnotation(m) && !IsComment(m)) Append(sb, m.Text, glue: false);
        }
        if (decl.GetChildForField("type") is { } type) Append(sb, type.Text, glue: false);
        if (declarator.GetChildForField("name") is { } name) Append(sb, name.Text, glue: false);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string text, bool glue)
    {
        var piece = Collapse(text);
        if (piece.Length == 0) return;
        if (sb.Length > 0 && !glue) sb.Append(' ');
        sb.Append(piece);
    }

    /// <summary>空白（含换行）折叠成一个空格；"( " 与 " )" 去掉空格。</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space && c != ')' && sb[^1] != '(') sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsAnnotation(Node n) => n.Type is "annotation" or "marker_annotation";

    /// <summary>记录头里的参数 record P(Item left, …)：记成字段。</summary>
    private static bool IsRecordComponent(Node n) =>
        n.Type == "formal_parameter" && n.Parent is { Type: "formal_parameters" } list && list.Parent is { Type: "record_declaration" } record
        && record.GetChildForField("parameters") is { } header && Same(header, list);

    private static bool IsComment(Node n) => n.Type is "line_comment" or "block_comment";

    private static bool Same(Node a, Node b) => a.StartIndex == b.StartIndex && a.EndIndex == b.EndIndex && a.Type == b.Type;

    // ---------- import ----------

    /// <summary>
    /// import 直接从 import_declaration 读：普通 a.b.C、通配 a.b.*、静态 a.b.C.m / a.b.C.*（static 不另外标记，解析器先当类型找、找不到再按成员找）。
    /// 每条也记一条从 Module 出发的 imports 引用。
    /// </summary>
    protected override ParsedFile CompleteFile(Node root, ParsedFile file)
    {
        var imports = new List<ParsedImport>();
        var references = file.References.Where(r => r.Kind != EdgeKind.Imports).ToList();
        foreach (var decl in root.NamedChildren.Where(c => c.Type == "import_declaration"))
        {
            var path = decl.NamedChildren.FirstOrDefault(c => c.Type is "scoped_identifier" or "identifier")?.Text;
            if (string.IsNullOrEmpty(path)) continue;
            path = Collapse(path).Replace(" ", "");
            if (decl.NamedChildren.Any(c => c.Type == "asterisk")) path += ".*";
            var line = decl.StartPosition.Row + 1;
            imports.Add(new ParsedImport(path, false, line));
            // imports 引用和基类路径一样不带 Context（只对 calls 有意义）与 Pos（大纲不列 import）
            references.Add(new ParsedReference(0, path, EdgeKind.Imports, line, decl.StartPosition.Column + 1));
        }
        return file with { Imports = imports, References = references };
    }

    /// <summary>先序遍历（文档顺序），含 node 本身。</summary>
    private static IEnumerable<Node> Descendants(Node node)
    {
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            var children = n.NamedChildren.ToList();
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }
}
