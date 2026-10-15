using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

/// <summary>
/// Go：包 = 目录，限定名 &lt;包目录&gt;.&lt;名字&gt;（根目录的包没有前缀）；方法挂在接收者类型下，限定名 &lt;包目录&gt;.&lt;接收者&gt;.&lt;方法&gt;。
/// struct → Class、interface → Interface、其他命名类型 → TypeAlias；struct 的具名字段 → Field（签名 "名字 类型"），
/// 包级 var → Variable（签名 "var 名字 类型" 或 "var 名字 = 值"）；首字母大写 public，否则 private。
/// 引用借用 <see cref="ScriptBinding"/> 带上导入名 / 接收者类型描述，含义见 HeuristicEdgeResolver.Go.cs。
/// 把函数 / 方法当值传给别的函数（router.POST("", Handler)、http.HandleFunc("/", srv.Handle)）记 references，Via = "funcvalue"。
/// Module 符号的 Signature 是 "package 包名"。
/// </summary>
public sealed class GoAdapter() : LanguageAdapterBase(
    "go", "Go", [".go"],
    QueryResources.Load("go.definitions.scm"),
    QueryResources.Load("go.references.scm"))
{
    /// <summary>函数值实参引用的 Via。</summary>
    public const string FuncValueVia = HeuristicEdgeResolver.GoFuncValueVia;

    /// <summary>适配器内部标记：这条函数值候选其实是局部变量 / 导入名，CompleteFile 里删掉。</summary>
    private const string DropVia = "\0drop";

    /// <summary>内置函数：调用它们不记边。</summary>
    private static readonly HashSet<string> BuiltinFunctions = new(StringComparer.Ordinal)
    {
        "append", "cap", "clear", "close", "complex", "copy", "delete", "imag", "len", "make", "max", "min", "new", "panic", "print", "println", "real", "recover",
    };

    /// <summary>预声明类型：类型引用不记；T(x) 是类型转换不是调用；接收者是它们时方法在 external。</summary>
    private static readonly HashSet<string> PredeclaredTypes = new(StringComparer.Ordinal)
    {
        "any", "bool", "byte", "comparable", "complex64", "complex128", "error", "float32", "float64", "int", "int8", "int16", "int32", "int64",
        "rune", "string", "uint", "uint8", "uint16", "uint32", "uint64", "uintptr",
    };

    /// <summary>
    /// 表达式的类型描述：Path 是描述根所在包的 import 路径（null 为本包，"builtin" 为预声明类型，"?名字" 为对不上任何导入默认名的限定符）；
    /// Desc 的写法见 HeuristicEdgeResolver.Go.cs（"T"、"F()"、"$V" 起头，后接 ".f"、".M()"、"[]"）。
    /// </summary>
    private sealed record GoType(string? Path, string Desc)
    {
        public GoType Then(string step) => this with { Desc = Desc + step };
    }

    /// <summary>一个函数（含其中的闭包）里声明的名字：名字 → (声明位置, 类型)，类型未知为 null。</summary>
    private sealed class Locals : Dictionary<string, List<(int Position, GoType? Type)>>
    {
        public Locals() : base(StringComparer.Ordinal) { }
    }

    private sealed class FileState
    {
        /// <summary>本文件的导入名（别名，或默认包名）→ import 路径；_ 不在内。</summary>
        public Dictionary<string, string> Imports { get; } = new(StringComparer.Ordinal);
        public List<string> DotImports { get; } = [];
        /// <summary>文件级 var 声明（包级变量，只看本文件；别的文件里的由解析器按 Variable 符号找）。</summary>
        public Locals FileVars { get; } = new();
        public Dictionary<int, Locals> FunctionLocals { get; } = [];
        public string? DotSource => DotImports.Count == 0 ? null : string.Join('\n', DotImports);
    }

    protected override string ModuleQualifiedPrefix(string relativePath)
    {
        var dir = GoModuleResolver.DirectoryOf(relativePath);
        return dir.Length == 0 ? "" : dir + ".";
    }

    protected override bool ImportIsPath(string target) => false;

    // ---- 调用大纲：调用所在控制结构 ----

    /// <summary>
    /// for 各形态（三段式 / range / 条件 / 无限）只有 body 算 loop，for_clause / range_clause / 条件是循环头；
    /// if 的 consequence / alternative 算 cond（else if 的条件落在外层 if 的 alternative 里，也是 cond），initializer / condition 不算；
    /// expression_switch / type_switch 的 case / default 整条算 cond（case 值按顺序比较，本身就是有条件求值），switch 头不算；
    /// select 的 communication_case 除 communication（进 select 时就求值）外算 cond，default 算 cond；
    /// func_literal 算 callback；defer / go 语句里被延后的那次调用（call 的 function 部分）算 callback，实参在语句执行时就求值，不算。
    /// </summary>
    protected override string? ContextOf(Node ancestor, Node child) => ancestor.Type switch
    {
        "for_statement" => IsField(ancestor, "body", child) ? CallContext.Loop : null,
        "if_statement" => IsField(ancestor, "consequence", child) || IsField(ancestor, "alternative", child) ? CallContext.Cond : null,
        "expression_case" or "type_case" or "default_case" => CallContext.Cond,
        "communication_case" => IsField(ancestor, "communication", child) ? null : CallContext.Cond,
        "func_literal" => CallContext.Callback,
        "call_expression" when ancestor.Parent?.Type is "defer_statement" or "go_statement" =>
            IsField(ancestor, "function", child) ? CallContext.Callback : null,
        "type_conversion_expression" when ancestor.Parent?.Type is "defer_statement" or "go_statement" =>
            IsField(ancestor, "type", child) ? CallContext.Callback : null,
        _ => null,
    };

    protected override bool IsContextBoundary(Node ancestor) => ancestor.Type is "function_declaration" or "method_declaration" or "source_file";

    protected override string? ExtractVisibility(Node definition) => Visibility(definition.GetChildForField("name")?.Text);

    private static string? Visibility(string? name) => string.IsNullOrEmpty(name) ? null : char.IsUpper(name[0]) ? "public" : "private";

    protected override string ReferenceName(Node node) =>
        node.Type is "identifier" or "field_identifier" ? node.Text : CoreType(node)?.Name.Text ?? "";

    protected override bool AcceptReference(string captureName, Node node)
    {
        switch (captureName)
        {
            case "calls":
                // 裸调用：内置函数与类型转换 string(x) 不算；F[T](x) 的 F 是 type_identifier，照收
                return node.Type != "identifier" || !(BuiltinFunctions.Contains(node.Text) || PredeclaredTypes.Contains(node.Text));
            case "references" when IsFuncValue(node):
                return true;
            case "references" or "inherits":
            {
                if (CoreType(node) is not { } core) return false;
                if (core.Package is null && (PredeclaredTypes.Contains(core.Name.Text) || IsTypeParameter(core.Name.Text, node))) return false;
                var parent = node.Parent;
                // 带名字的字段是普通字段，不是内嵌
                if (captureName == "inherits" && parent is { Type: "field_declaration" } field && field.GetChildForField("name") is not null) return false;
                // 接收者 (s *Server)：方法本来就挂在 Server 下
                if (parent is { Type: "parameter_declaration" } p && IsReceiver(p)) return false;
                return true;
            }
            default:
                return true;
        }
    }

    /// <summary>实参位置上的裸名字 f 或 x.f（不是调用）：可能是函数值。</summary>
    private static bool IsFuncValue(Node node) =>
        (node.Type == "identifier" && node.Parent is { Type: "argument_list" })
        || (node.Type == "field_identifier" && node.Parent is { Type: "selector_expression" } sel && sel.Parent is { Type: "argument_list" });

    private static bool IsReceiver(Node parameter) =>
        parameter.Parent is { Type: "parameter_list" } list && list.Parent is { Type: "method_declaration" } method
        && method.GetChildForField("receiver") is { } receiver && receiver.StartIndex == list.StartIndex;

    /// <summary>name 是不是外层函数 / 类型声明的类型参数（[T any]，或方法接收者 G[T] 里的 T）。</summary>
    private static bool IsTypeParameter(string name, Node node)
    {
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            if (cur.Type is not ("function_declaration" or "method_declaration" or "type_spec")) continue;
            if (cur.GetChildForField("type_parameters") is { } tps)
                foreach (var decl in tps.NamedChildren)
                    foreach (var n in decl.GetChildrenForField("name"))
                        if (n.Text == name) return true;
            if (cur.Type == "method_declaration"
                && cur.GetChildForField("receiver")?.NamedChildren.FirstOrDefault()?.GetChildForField("type") is { } recvType)
            {
                var generic = recvType.Type == "pointer_type" ? recvType.NamedChildren.FirstOrDefault() : recvType;
                if (generic is { Type: "generic_type" } && generic.GetChildForField("type_arguments") is { } args)
                    foreach (var elem in args.NamedChildren)
                        if (elem.Text == name) return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>*T、[]T、[N]T、map[K]T、chan T、G[T]、(T) 里的命名类型；pkg.T 拆出包名。函数类型、匿名 struct 等返回 null。</summary>
    private static (Node? Package, Node Name)? CoreType(Node node)
    {
        switch (node.Type)
        {
            case "type_identifier": return (null, node);
            case "qualified_type":
                return node.GetChildForField("name") is { } name ? (node.GetChildForField("package"), name) : null;
            case "pointer_type" or "parenthesized_type":
                return node.NamedChildren.FirstOrDefault() is { } inner ? CoreType(inner) : null;
            case "slice_type" or "array_type":
                return node.GetChildForField("element") is { } element ? CoreType(element) : null;
            case "map_type" or "channel_type":
                return node.GetChildForField("value") is { } value ? CoreType(value) : null;
            case "generic_type":
                return node.GetChildForField("type") is { } type ? CoreType(type) : null;
            default: return null;
        }
    }

    // ---------- 文件级预处理：import 表与包级变量 ----------

    protected override object? PrepareFile(Node root)
    {
        var state = new FileState();
        foreach (var decl in root.NamedChildren)
        {
            if (decl.Type != "import_declaration") continue;
            foreach (var part in decl.NamedChildren)
            {
                var specs = part.Type == "import_spec_list" ? part.NamedChildren.Where(s => s.Type == "import_spec") : part.Type == "import_spec" ? [part] : [];
                foreach (var spec in specs)
                {
                    var path = ImportPath(spec);
                    if (path is null) continue;
                    var alias = spec.GetChildForField("name");
                    switch (alias?.Type)
                    {
                        case "blank_identifier": break;
                        case "dot": state.DotImports.Add(path); break;
                        case "package_identifier": state.Imports[alias.Text] = path; break;
                        default: state.Imports[GoModuleResolver.DefaultPackageName(path)] = path; break;
                    }
                }
            }
        }
        foreach (var decl in root.NamedChildren)
            if (decl.Type == "var_declaration")
                foreach (var spec in Descendants(decl).Where(n => n.Type == "var_spec"))
                    AddVarSpec(spec, state, state.FileVars);
        return state;
    }

    private static string? ImportPath(Node spec)
    {
        var path = spec.GetChildForField("path");
        if (path is null) return null;
        var content = path.NamedChildren.FirstOrDefault(c => c.Type == "interpreted_string_literal_content");
        return content?.Text ?? (path.Text.Length >= 2 ? path.Text[1..^1] : null);
    }

    // ---------- 引用绑定 ----------

    protected override ParsedReference AnnotateReference(ParsedReference reference, string captureName, Node node, object? fileState)
    {
        if (fileState is not FileState state) return reference;
        switch (captureName)
        {
            case "calls":
                if (node.Parent is { Type: "selector_expression" } selector && selector.GetChildForField("operand") is { } operand)
                    return reference with { Script = ReceiverBinding(operand, state) };
                // 泛型调用 pkg.F[T](x)
                if (node.Parent is { Type: "qualified_type" } qualified && qualified.GetChildForField("package") is { } qualifier)
                    return reference with { Script = PackageBinding(qualifier.Text, state) };
                return reference with { Script = new ScriptBinding(ScriptReceiver.None, state.DotSource) };
            case "references" when IsFuncValue(node):
                return FuncValue(reference, node, state);
            case "references" or "inherits":
                if (CoreType(node)?.Package is { } pkg) return reference with { Script = PackageBinding(pkg.Text, state) };
                return reference with { Script = new ScriptBinding(ScriptReceiver.None, state.DotSource) };
            default:
                return reference;
        }
    }

    /// <summary>pkg.X 的 pkg：本文件的导入名；对不上任何导入默认名时记 "?pkg"（可能是包名与目录名不一致的导入，解析器按 package 子句找）。</summary>
    private static ScriptBinding PackageBinding(string qualifier, FileState state) =>
        new(ScriptReceiver.Import, state.Imports.TryGetValue(qualifier, out var path) ? path : "?" + qualifier, qualifier);

    /// <summary>实参里的 f / x.f：局部变量、参数、导入名不是函数值，标记删除；其余带上绑定交给解析器（只连本仓库的函数 / 方法）。</summary>
    private ParsedReference FuncValue(ParsedReference reference, Node node, FileState state)
    {
        var drop = reference with { Via = DropVia };
        if (node.Type == "identifier")
        {
            if (Lookup(node.Text, node, state, out _) || state.Imports.ContainsKey(node.Text)) return drop;
            return reference with { Via = FuncValueVia, Script = new ScriptBinding(ScriptReceiver.None, state.DotSource) };
        }
        if (node.Parent?.GetChildForField("operand") is not { } operand) return drop;
        var binding = ReceiverBinding(operand, state);
        return binding is { Receiver: ScriptReceiver.Other, ImportedName: null } ? drop : reference with { Via = FuncValueVia, Script = binding };
    }

    private ScriptBinding ReceiverBinding(Node operand, FileState state)
    {
        if (operand.Type == "identifier")
        {
            // 局部变量 / 参数 / 包级变量优先（会遮住同名的导入名）
            if (Lookup(operand.Text, operand, state, out var type)) return Typed(type);
            if (state.Imports.TryGetValue(operand.Text, out var path)) return new ScriptBinding(ScriptReceiver.Import, path, operand.Text);
            // 本文件里没声明：包名与目录名不一致的导入，或同包别的文件里的包级变量——解析器再分辨
            return new ScriptBinding(ScriptReceiver.Import, "?" + operand.Text, operand.Text);
        }
        // pb.NewXClient(conn).M()、s.db.Where(…).First()、(&T{}).M()、x.(T).M()、xs[i].M()
        return Typed(ValueType(operand, state, operand, 0));
    }

    private static ScriptBinding Typed(GoType? type) =>
        type is null ? new ScriptBinding(ScriptReceiver.Other) : new ScriptBinding(ScriptReceiver.Other, type.Path, type.Desc);

    /// <summary>at 处可见的名字 name 的类型。found 表示名字是本函数的局部名 / 参数或包级变量（类型可能仍未知）。</summary>
    private bool Lookup(string name, Node at, FileState state, out GoType? type)
    {
        type = null;
        if (EnclosingFunction(at) is { } fn)
        {
            if (!state.FunctionLocals.TryGetValue(fn.StartIndex, out var locals))
            {
                locals = BuildLocals(fn, state);
                state.FunctionLocals[fn.StartIndex] = locals;
            }
            if (Latest(locals, name, at.StartIndex, out type)) return true;
        }
        return Latest(state.FileVars, name, int.MaxValue, out type);
    }

    private static bool Latest(Locals locals, string name, int before, out GoType? type)
    {
        type = null;
        if (!locals.TryGetValue(name, out var entries)) return false;
        var found = false;
        foreach (var (position, t) in entries)
            if (position < before) { type = t; found = true; }
        return found;
    }

    /// <summary>最外层的函数 / 方法声明；顶层 var 里的闭包取最外层的 func_literal。</summary>
    private static Node? EnclosingFunction(Node node)
    {
        Node? literal = null;
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            if (cur.Type is "function_declaration" or "method_declaration") return cur;
            if (cur.Type == "func_literal") literal = cur;
        }
        return literal;
    }

    /// <summary>
    /// 函数里声明的名字与能直接读出来的类型：接收者、参数（含闭包的）、var x T、var x = …、x := …、for _, v := range xs（v 是 xs 的元素）。
    /// 不分块作用域：同名取声明位置在引用之前的最后一个。
    /// </summary>
    private Locals BuildLocals(Node fn, FileState state)
    {
        var locals = new Locals();
        foreach (var n in Descendants(fn))
        {
            switch (n.Type)
            {
                case "parameter_declaration" or "variadic_parameter_declaration":
                {
                    var typeNode = n.GetChildForField("type");
                    var type = n.Type == "parameter_declaration" && typeNode is not null ? TypeOf(typeNode, state) : null;
                    foreach (var name in n.GetChildrenForField("name")) Add(locals, name, type);
                    break;
                }
                case "var_spec":
                    AddVarSpec(n, state, locals);
                    break;
                case "short_var_declaration":
                {
                    var left = n.GetChildForField("left")?.NamedChildren.ToList() ?? [];
                    var right = n.GetChildForField("right")?.NamedChildren.ToList() ?? [];
                    for (var i = 0; i < left.Count; i++)
                    {
                        if (left[i].Type != "identifier") continue;
                        // a, b := x, y 一一对应；a, err := F() 只有第一个取返回值
                        var value = right.Count == left.Count ? right[i] : i == 0 && right.Count == 1 ? right[0] : null;
                        Add(locals, left[i], value is null ? null : ValueType(value, state, n, 0, locals));
                    }
                    break;
                }
                case "range_clause":
                {
                    // for k, v := range xs：v 是元素（切片 / 数组 / map 的值）；k 是下标或键，类型不推（channel 的 for v := range ch 也不推）
                    var left = n.GetChildForField("left")?.NamedChildren.ToList() ?? [];
                    var right = n.GetChildForField("right");
                    for (var i = 0; i < left.Count; i++)
                        if (left[i].Type == "identifier")
                            Add(locals, left[i], i == 1 && right is not null ? ValueType(right, state, n, 0, locals)?.Then("[]") : null);
                    break;
                }
            }
        }
        return locals;
    }

    private void AddVarSpec(Node spec, FileState state, Locals locals)
    {
        var names = spec.GetChildrenForField("name").ToList();
        var typeNode = spec.GetChildForField("type");
        var values = spec.GetChildForField("value")?.NamedChildren.ToList() ?? [];
        for (var i = 0; i < names.Count; i++)
        {
            var type = typeNode is not null ? TypeOf(typeNode, state)
                : values.Count == names.Count ? ValueType(values[i], state, spec, 0, locals)
                : i == 0 && values.Count == 1 ? ValueType(values[0], state, spec, 0, locals) : null;
            Add(locals, names[i], type);
        }
    }

    private static void Add(Locals locals, Node name, GoType? type)
    {
        if (name.Text == "_") return;
        if (!locals.TryGetValue(name.Text, out var list)) locals[name.Text] = list = [];
        list.Add((name.StartIndex, type));
    }

    private static GoType? TypeOf(Node typeNode, FileState state)
    {
        if (CoreType(typeNode) is not { } core) return null;
        if (core.Package is { } pkg)
            return new GoType(state.Imports.TryGetValue(pkg.Text, out var path) ? path : "?" + pkg.Text, core.Name.Text);
        return PredeclaredTypes.Contains(core.Name.Text)
            ? new GoType(HeuristicEdgeResolver.GoBuiltinPackage, core.Name.Text)
            : new GoType(null, core.Name.Text);
    }

    /// <summary>
    /// 表达式的类型描述（只认能直接读出来的几种，解析器负责把字段、方法返回值、元素一步步落到具体类型）。
    /// locals 给出时在其中找已声明的变量，否则按 at 的位置查。
    /// </summary>
    private GoType? ValueType(Node expr, FileState state, Node at, int depth, Locals? locals = null)
    {
        if (depth > 8) return null;
        switch (expr.Type)
        {
            case "composite_literal":
                return expr.GetChildForField("type") is { } t ? TypeOf(t, state) : null;
            case "unary_expression":
            {
                if (expr.GetChildForField("operand") is not { } operand) return null;
                var inner = ValueType(operand, state, at, depth + 1, locals);
                return expr.GetChildForField("operator")?.Text == "<-" ? inner?.Then("[]") : inner; // <-ch 是元素
            }
            case "parenthesized_expression":
                return expr.NamedChildren.FirstOrDefault() is { } inner2 ? ValueType(inner2, state, at, depth + 1, locals) : null;
            case "type_assertion_expression":
                // x.(interface{ M() })：匿名接口上的方法没有定义处，当预声明类型处理（记 external:M，不去猜同名方法）
                return expr.GetChildForField("type") is { } asserted
                    ? asserted.Type == "interface_type" ? new GoType(HeuristicEdgeResolver.GoBuiltinPackage, "any") : TypeOf(asserted, state)
                    : null;
            case "type_conversion_expression":
            {
                // F[T](x)：tree-sitter 当成类型转换，其实多半是泛型函数调用——记成 F 的返回值，解析器发现 F 是类型时按类型算
                var type = expr.GetChildForField("type");
                if (type is { Type: "generic_type" } && type.GetChildForField("type") is { } generic)
                {
                    if (generic.Type == "type_identifier") return new GoType(null, generic.Text + "()");
                    if (generic.Type == "qualified_type" && generic.GetChildForField("package") is { } q && generic.GetChildForField("name") is { } gn)
                        return new GoType(state.Imports.TryGetValue(q.Text, out var gp) ? gp : "?" + q.Text, gn.Text + "()");
                    return null;
                }
                return type is null ? null : TypeOf(type, state);
            }
            case "call_expression":
            {
                var fn = expr.GetChildForField("function");
                if (fn is { Type: "identifier" })
                {
                    if (fn.Text is "new" or "make") // new(T) / make([]T, n) / make(map[K]V)
                        return expr.GetChildForField("arguments")?.NamedChildren.FirstOrDefault() is { } arg ? TypeOf(arg, state) : null;
                    if (BuiltinFunctions.Contains(fn.Text) || PredeclaredTypes.Contains(fn.Text)) return null;
                    if (IsDeclared(fn.Text, at, state, locals)) return null; // 函数类型的局部变量
                    return new GoType(null, fn.Text + "()");
                }
                if (fn is { Type: "selector_expression" } && fn.GetChildForField("field") is { } field)
                {
                    var operand = fn.GetChildForField("operand");
                    // pkg.F(...)：pkg 是导入名（不是同名的局部变量）；对不上导入的名字记 "?x"，解析器再分辨包名还是别的文件里的包级变量
                    if (operand is { Type: "identifier" } && !IsDeclared(operand.Text, at, state, locals))
                        return new GoType(state.Imports.TryGetValue(operand.Text, out var path) ? path : "?" + operand.Text, field.Text + "()");
                    // x.M(...)：x 的类型再加一步"方法 M 的返回值"
                    return operand is null ? null : ValueType(operand, state, at, depth + 1, locals)?.Then("." + field.Text + "()");
                }
                return null; // (*T).M(x)、fs[i](x)、f()(x)：不推
            }
            case "selector_expression":
            {
                // x.f（不是调用）：字段；pkg.V：包级变量
                if (expr.GetChildForField("field") is not { } field || expr.GetChildForField("operand") is not { } operand) return null;
                if (operand.Type == "identifier" && !IsDeclared(operand.Text, at, state, locals))
                    return new GoType(state.Imports.TryGetValue(operand.Text, out var path) ? path : "?" + operand.Text, "$" + field.Text);
                return ValueType(operand, state, at, depth + 1, locals)?.Then("." + field.Text);
            }
            case "index_expression":
                return expr.GetChildForField("operand") is { } indexed ? ValueType(indexed, state, at, depth + 1, locals)?.Then("[]") : null;
            case "slice_expression":
                return expr.GetChildForField("operand") is { } sliced ? ValueType(sliced, state, at, depth + 1, locals) : null;
            case "identifier":
            {
                GoType? type;
                if (locals is not null)
                {
                    if (Latest(locals, expr.Text, at.StartIndex, out type) || Latest(state.FileVars, expr.Text, int.MaxValue, out type)) return type;
                }
                else if (Lookup(expr.Text, at, state, out type)) return type;
                // 本文件没声明、也不是导入名：同包别的文件里的包级变量
                return state.Imports.ContainsKey(expr.Text) || expr.Text is "nil" or "true" or "false" or "iota" ? null : new GoType(null, "$" + expr.Text);
            }
            default:
                return null;
        }
    }

    private bool IsDeclared(string name, Node at, FileState state, Locals? locals) =>
        locals is not null ? Latest(locals, name, at.StartIndex, out _) || Latest(state.FileVars, name, int.MaxValue, out _) : Lookup(name, at, state, out _);

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

    // ---------- 收尾：方法挂到接收者类型下、字段与包级变量、跨行签名 ----------

    /// <summary>
    /// 方法（有接收者的顶层函数）改名为 &lt;包目录&gt;.&lt;接收者&gt;.&lt;方法&gt;，父设为同文件的接收者类型（不在本文件就是 Module）。
    /// 接收者类型可能声明在方法之后，所以把方法统一排到其他符号后面，并同步 ParentIndex 与引用的 FromIndex。
    /// 另外补上 struct 字段（Field）与包级 var（Variable）符号，把跨行的函数签名收成一行，删掉标记为丢弃的函数值候选。
    /// </summary>
    protected override ParsedFile CompleteFile(Node root, ParsedFile file)
    {
        var symbols = file.Symbols.ToList();
        var prefix = ModuleQualifiedPrefix(file.RelativePath);
        var receivers = new Dictionary<(int Line, int Col), string>();
        var functions = new Dictionary<(int Line, int Col), Node>();
        string? packageName = null;
        foreach (var decl in root.NamedChildren)
        {
            if (decl.Type == "package_clause") packageName ??= decl.NamedChildren.FirstOrDefault()?.Text;
            if (decl.Type is "function_declaration" or "method_declaration") functions[(decl.StartPosition.Row + 1, decl.StartPosition.Column + 1)] = decl;
            if (decl.Type != "method_declaration") continue;
            var recvType = decl.GetChildForField("receiver")?.NamedChildren.FirstOrDefault(c => c.Type == "parameter_declaration")?.GetChildForField("type");
            if (recvType is not null && CoreType(recvType) is { Package: null } core)
                receivers[(decl.StartPosition.Row + 1, decl.StartPosition.Column + 1)] = core.Name.Text;
        }

        for (var i = 1; i < symbols.Count; i++)
            if (symbols[i].Kind is SymbolKind.Function or SymbolKind.Method && functions.TryGetValue((symbols[i].StartLine, symbols[i].StartCol), out var fnNode)
                && FullSignature(fnNode, symbols[i].Signature) is { } full)
                symbols[i] = symbols[i] with { Signature = full };

        AddFieldsAndVariables(root, symbols, prefix);

        var isMethod = new bool[symbols.Count];
        for (var i = 1; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (s.Kind != SymbolKind.Method || s.ParentIndex != 0 || !receivers.TryGetValue((s.StartLine, s.StartCol), out var receiver)) continue;
            var parent = symbols.FindIndex(1, t => t.ParentIndex == 0 && t.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.TypeAlias && t.Name == receiver);
            symbols[i] = s with { QualifiedName = prefix + receiver + "." + s.Name, ParentIndex = parent > 0 ? parent : 0 };
            isMethod[i] = true;
        }
        if (packageName is not null) symbols[0] = symbols[0] with { Signature = "package " + packageName };

        var order = new List<int> { 0 };
        order.AddRange(Enumerable.Range(1, symbols.Count - 1).Where(i => !isMethod[i]));
        order.AddRange(Enumerable.Range(1, symbols.Count - 1).Where(i => isMethod[i]));
        var newIndex = new int[symbols.Count];
        for (var i = 0; i < order.Count; i++) newIndex[order[i]] = i;

        var reordered = order.Select(i => symbols[i] is { ParentIndex: { } p } s ? s with { ParentIndex = newIndex[p] } : symbols[i]).ToList();
        var references = file.References.Where(r => r.Via != DropVia).Select(r => r with { FromIndex = newIndex[r.FromIndex] }).ToList();
        return file with { Symbols = reordered, References = references };
    }

    /// <summary>签名首行没包含函数体的左花括号（参数 / 返回值跨行）时，把整段签名收成一行；太长时参数写成 (...)，保住返回值。</summary>
    private static string? FullSignature(Node fn, string? firstLine)
    {
        if (firstLine is null || firstLine.Contains('{') || fn.GetChildForField("body") is not { } body) return null;
        var text = fn.Text;
        var sig = Collapse(text.EndsWith(body.Text, StringComparison.Ordinal) ? text[..^body.Text.Length] : firstLine);
        if (sig.Length <= 200) return sig;
        var head = fn.Type == "method_declaration" && fn.GetChildForField("receiver") is { } recv ? "func " + Collapse(recv.Text) + " " : "func ";
        var tps = fn.GetChildForField("type_parameters") is { } tp ? Collapse(tp.Text) : "";
        var result = fn.GetChildForField("result") is { } res ? " " + Collapse(res.Text) : "";
        var shortSig = head + fn.GetChildForField("name")?.Text + tps + "(...)" + result;
        return shortSig.Length > 200 ? shortSig[..200] : shortSig;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();

    /// <summary>
    /// 顶层 struct 的具名字段 → Field（父是 struct，签名 "名字 类型"，a, b T 拆成两个）；顶层 var → Variable（签名 "var 名字 类型" / "var 名字 = 值"）。
    /// 字段与变量的范围从名字到声明结束；里面的引用仍归原来的外层符号（struct / Module）。
    /// </summary>
    private static void AddFieldsAndVariables(Node root, List<ParsedSymbol> symbols, string prefix)
    {
        foreach (var decl in root.NamedChildren)
        {
            if (decl.Type == "type_declaration")
            {
                foreach (var spec in decl.NamedChildren.Where(c => c.Type == "type_spec"))
                {
                    if (spec.GetChildForField("type") is not { Type: "struct_type" } structType) continue;
                    var owner = symbols.FindIndex(1, s => s.Kind == SymbolKind.Class && s.ParentIndex == 0
                                                          && s.StartLine == spec.StartPosition.Row + 1 && s.StartCol == spec.StartPosition.Column + 1);
                    if (owner < 0) continue;
                    var list = structType.NamedChildren.FirstOrDefault(c => c.Type == "field_declaration_list");
                    foreach (var field in list?.NamedChildren.Where(c => c.Type == "field_declaration") ?? [])
                    {
                        if (field.GetChildForField("type") is not { } type) continue;
                        foreach (var name in field.GetChildrenForField("name"))
                            symbols.Add(new ParsedSymbol(SymbolKind.Field, name.Text, symbols[owner].QualifiedName + "." + name.Text, Truncate(name.Text + " " + Collapse(type.Text)),
                                name.StartPosition.Row + 1, name.StartPosition.Column + 1, field.EndPosition.Row + 1, field.EndPosition.Column + 1, owner, Visibility(name.Text)));
                    }
                }
            }
            else if (decl.Type == "var_declaration")
            {
                foreach (var spec in Descendants(decl).Where(n => n.Type == "var_spec"))
                {
                    var names = spec.GetChildrenForField("name").ToList();
                    var type = spec.GetChildForField("type");
                    var values = spec.GetChildForField("value")?.NamedChildren.ToList() ?? [];
                    for (var i = 0; i < names.Count; i++)
                    {
                        if (names[i].Text == "_") continue;
                        var sig = "var " + names[i].Text + (type is not null ? " " + Collapse(type.Text)
                            : values.Count == names.Count ? " = " + Collapse(values[i].Text)
                            : values.Count == 1 && i == 0 ? " = " + Collapse(values[0].Text) : "");
                        symbols.Add(new ParsedSymbol(SymbolKind.Variable, names[i].Text, prefix + names[i].Text, Truncate(sig),
                            names[i].StartPosition.Row + 1, names[i].StartPosition.Column + 1, spec.EndPosition.Row + 1, spec.EndPosition.Column + 1, 0, Visibility(names[i].Text)));
                    }
                }
            }
        }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;
}
