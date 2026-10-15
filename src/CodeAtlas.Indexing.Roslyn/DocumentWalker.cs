using CodeAtlas.Core.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Indexing.Roslyn;

/// <summary>一个文档的符号与边。先输出全部符号，再输出边；边用限定名引用。</summary>
internal sealed class DocumentWalker
{
    private readonly SemanticModel _model;
    private readonly SyntaxNode _root;
    private readonly string _path;
    /// <summary>"仓库内"的根目录（主根 + 外部根）：定义在它们之下的符号是内部目标，否则记为 external。</summary>
    private readonly IReadOnlyList<string> _repoRoot;
    private readonly Dictionary<string, List<(INamedTypeSymbol Type, ISymbol Entry)>> _handlers;
    private readonly Action<RoslynRecord> _emit;
    private readonly HashSet<(string, string, string, int, string?)> _seenEdges = [];
    private readonly HashSet<SyntaxNode> _conduitCreations = [];

    public DocumentWalker(SemanticModel model, SyntaxNode root, string path, IReadOnlyList<string> repoRoot,
        Dictionary<string, List<(INamedTypeSymbol Type, ISymbol Entry)>> handlers, Action<RoslynRecord> emit)
    {
        _model = model; _root = root; _path = path; _repoRoot = repoRoot; _handlers = handlers; _emit = emit;
    }

    public void Run()
    {
        var declarations = _root.DescendantNodes().Where(IsDeclaration).ToList();
        foreach (var node in declarations) EmitSymbol(node);
        EmitTopLevelMain();
        foreach (var node in declarations)
        {
            if (node is BaseTypeDeclarationSyntax && _model.GetDeclaredSymbol(node) is INamedTypeSymbol t) EmitTypeEdges(t, node);
            if (node is MethodDeclarationSyntax && _model.GetDeclaredSymbol(node) is IMethodSymbol m && m.OverriddenMethod is { } overridden)
                Edge(SymbolKeys.Key(m), overridden, "inherits", 1.0, Line(node));
        }
        // 先处理调用（里面决定哪些 new 是管道），再处理对象创建
        foreach (var inv in _root.DescendantNodes().OfType<InvocationExpressionSyntax>()) EmitInvocation(inv);
        foreach (var oc in _root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()) EmitObjectCreation(oc);
        foreach (var ts in _root.DescendantNodes().OfType<TypeSyntax>()) EmitTypeReference(ts);
    }

    // ---------- 符号 ----------

    private static bool IsDeclaration(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => true,
        MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or LocalFunctionStatementSyntax => true,
        VariableDeclaratorSyntax v => v.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax,
        _ => false,
    };

    private void EmitSymbol(SyntaxNode node)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol is null) return;
        var kind = SymbolKeys.Kind(symbol);
        if (kind is null) return;
        var span = node.GetLocation().GetLineSpan();
        var parent = symbol.ContainingSymbol;
        var parentKey = parent is null || parent is INamespaceSymbol { IsGlobalNamespace: true } ? null : SymbolKeys.Key(parent);
        _emit(new SymbolRec(_path, kind, symbol.Name, SymbolKeys.Key(symbol), SymbolKeys.Signature(symbol),
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1,
            string.IsNullOrEmpty(parentKey) ? null : parentKey, SymbolKeys.Visibility(symbol)));
    }

    /// <summary>顶级语句：编译器生成 Program.&lt;Main&gt;$，没有声明节点，这里补一个 function 符号覆盖全部顶级语句，调用才有归属。</summary>
    private void EmitTopLevelMain()
    {
        var globals = _root.DescendantNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count == 0) return;
        var symbol = _model.GetEnclosingSymbol(globals[0].Statement.SpanStart);
        while (symbol is not null && symbol is not IMethodSymbol) symbol = symbol.ContainingSymbol;
        if (symbol is not IMethodSymbol main) return;
        var start = globals[0].GetLocation().GetLineSpan().StartLinePosition;
        var end = globals[^1].GetLocation().GetLineSpan().EndLinePosition;
        _emit(new SymbolRec(_path, "function", "<top-level>", SymbolKeys.Key(main), "顶级语句（Main）",
            start.Line + 1, start.Character + 1, end.Line + 1, end.Character + 1, null, "internal"));
    }

    // ---------- 类型级 / 方法级边 ----------

    private void EmitTypeEdges(INamedTypeSymbol type, SyntaxNode node)
    {
        var line = Line(node);
        var typeKey = SymbolKeys.Key(type);
        if (type.BaseType is { SpecialType: SpecialType.None } baseType && baseType.TypeKind != TypeKind.Error)
            Edge(typeKey, baseType, "inherits", 1.0, line);
        foreach (var iface in type.Interfaces)
            Edge(typeKey, iface, "implements", 1.0, line);

        // 接口成员 → 本类型里的实现（只输出声明在本文件里的实现）
        foreach (var iface in type.AllInterfaces)
            foreach (var member in iface.GetMembers())
            {
                if (member is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;
                if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                var impl = type.FindImplementationForInterfaceMember(member);
                // record 合成的 Equals/GetHashCode 等没有源码符号，跳过
                if (impl is null || impl.IsImplicitlyDeclared || !SymbolEqualityComparer.Default.Equals(impl.ContainingType, type) || !DeclaredHere(impl)) continue;
                Edge(SymbolKeys.Key(impl), member, "implements", 1.0, Line(impl));
            }

        // Handler<T>：类型 → 它处理的消息类型
        var handled = RoslynAnalyzer.HandledMessages(type).ToList();
        foreach (var (message, _) in handled)
            Edge(typeKey, message, "dispatches", 1.0, line, via: "handles");
        // 入口落在类型上 = 子类自己没有 Handle，处理逻辑在基类里
        if (handled.Any(h => h.Entry is INamedTypeSymbol)) EmitInheritedHandle(type, typeKey, line);
    }

    /// <summary>
    /// 泛型基 Handler 的具体子类（CreateOrderIdentifiedCommandHandler : IdentifiedCommandHandler&lt;CreateOrderCommand, bool&gt;）没有自己的 Handle 时：
    /// 输出 子类 → 继承来的基类 Handle 的 calls 边；基类 Handle 里以类型参数为实参的分派（_mediator.Send(message.Command)）按子类的类型实参代换后再查 Handler。
    /// 再分派边从子类出发：基类方法是全部子类共用的一个节点，挂在它上面会重新扇出到所有内层 Handler。
    /// </summary>
    private void EmitInheritedHandle(INamedTypeSymbol type, string typeKey, int line)
    {
        for (var b = type.BaseType; b is { SpecialType: SpecialType.None }; b = b.BaseType)
        {
            var inherited = b.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && !m.IsAbstract && RoslynAnalyzer.HandlerEntry().IsMatch(m.Name));
            if (inherited is null) continue;
            var definition = inherited.OriginalDefinition;
            if (!SymbolKeys.IsInRepo(definition, _repoRoot)) return;
            Edge(typeKey, definition, "calls", 1.0, line);
            if (!b.IsGenericType) return;

            // b 是沿子类继承链得到的构造类型（已是子类视角的实参），基类方法体里看到的是 b 的泛型定义上的类型参数
            var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            for (var i = 0; i < b.TypeArguments.Length; i++) map[b.OriginalDefinition.TypeParameters[i]] = b.TypeArguments[i];
            foreach (var reference in definition.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                if (!_model.Compilation.ContainsSyntaxTree(tree)) continue; // 基类在别的项目里：拿不到同一编译的语义模型，只留 calls 边
                var model = tree == _model.SyntaxTree ? _model : _model.Compilation.GetSemanticModel(tree);
                foreach (var inv in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method || !RoslynAnalyzer.DispatcherMethod().IsMatch(method.Name)) continue;
                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        var argType = model.GetTypeInfo(arg.Expression).Type;
                        if (argType is null || !SymbolKeys.ContainsTypeParameter(argType)) continue; // 不含类型参数的分派已由基类方法自己输出
                        if (Substitute(argType, map) is not { } concrete) continue;
                        if (!_handlers.TryGetValue(SymbolKeys.MessageKey(concrete), out var list)
                            && !_handlers.TryGetValue(SymbolKeys.Key(concrete), out list)) continue;
                        var dispatchVia = "dispatch:" + SymbolKeys.MessageShortName(concrete);
                        foreach (var (_, entry) in list)
                            Edge(typeKey, entry, "dispatches", 1.0, line, via: dispatchVia, evidenceLine: line);
                    }
                }
            }
            return;
        }
    }

    /// <summary>按 map 代换类型里的类型参数；遇到 map 之外的类型参数（方法自己的 T 等）返回 null。</summary>
    private ITypeSymbol? Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> map)
    {
        switch (type)
        {
            case ITypeParameterSymbol tp:
                return map.GetValueOrDefault(tp);
            case IArrayTypeSymbol a:
                return Substitute(a.ElementType, map) is { } element ? _model.Compilation.CreateArrayTypeSymbol(element, a.Rank) : null;
            case INamedTypeSymbol { IsGenericType: true } n when SymbolKeys.ContainsTypeParameter(n):
                var args = new ITypeSymbol[n.TypeArguments.Length];
                for (var i = 0; i < args.Length; i++)
                {
                    if (Substitute(n.TypeArguments[i], map) is not { } arg) return null;
                    args[i] = arg;
                }
                return n.ConstructedFrom.Construct(args);
            default:
                return type;
        }
    }

    private bool DeclaredHere(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(r => r.SyntaxTree == _root.SyntaxTree);

    // ---------- 调用 ----------

    private void EmitInvocation(InvocationExpressionSyntax inv)
    {
        var from = EnclosingMemberKey(inv.SpanStart);
        if (from is null) return;
        var line = Line(inv);
        var site = (CallSyntax.NamePos(inv), CallSyntax.ContextOf(inv));
        var info = _model.GetSymbolInfo(inv);
        var method = info.Symbol as IMethodSymbol;

        if (method is null)
        {
            foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>())
                Edge(from, candidate, "calls", 0.8, line, site: site);
            return;
        }

        var via = (string?)null;
        if (RoslynAnalyzer.DispatcherMethod().IsMatch(method.Name))
        {
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                var argType = _model.GetTypeInfo(arg.Expression).Type;
                if (argType is null) continue;
                // 先按构造键（含类型实参）找具体 Handler 子类，找不到再退回泛型定义键（开放泛型 Handler 的旧行为）
                if (!_handlers.TryGetValue(SymbolKeys.MessageKey(argType), out var list)
                    && !_handlers.TryGetValue(SymbolKeys.Key(argType), out list)) continue;
                var dispatchVia = "dispatch:" + SymbolKeys.MessageShortName(argType);
                foreach (var (_, entry) in list)
                    Edge(from, entry, "dispatches", 1.0, line, via: dispatchVia, evidenceLine: line, site: site);
                via = "conduit";
                if (arg.Expression is BaseObjectCreationExpressionSyntax creation) _conduitCreations.Add(creation);
            }
        }

        if (RoslynAnalyzer.DiMethods.Contains(method.Name)) EmitDi(inv, method);
        Edge(from, method, "calls", 1.0, line, via, site: site);
    }

    private void EmitObjectCreation(BaseObjectCreationExpressionSyntax oc)
    {
        var from = EnclosingMemberKey(oc.SpanStart);
        if (from is null) return;
        if (_model.GetTypeInfo(oc).Type is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error) return;
        // 目标是类型而不是构造函数：读图的人关心"new 了什么"，和 tree-sitter 的约定一致
        Edge(from, type, "calls", 1.0, Line(oc), _conduitCreations.Contains(oc) ? "conduit" : null,
            site: (CallSyntax.NamePos(oc), CallSyntax.ContextOf(oc)));
    }

    private void EmitDi(InvocationExpressionSyntax inv, IMethodSymbol method)
    {
        ITypeSymbol? iface = null, impl = null;
        if (method.TypeArguments.Length == 2) { iface = method.TypeArguments[0]; impl = method.TypeArguments[1]; }
        else
        {
            var typeOfs = inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
            if (typeOfs.Count == 2) { iface = _model.GetTypeInfo(typeOfs[0].Type).Type; impl = _model.GetTypeInfo(typeOfs[1].Type).Type; }
        }
        if (iface is null || impl is null) return;
        var lifetime = method.Name.Contains("Singleton") ? "singleton" : method.Name.Contains("Transient") ? "transient" : "scoped";
        var conditional = inv.Ancestors().Any(a => a is IfStatementSyntax or ConditionalExpressionSyntax or SwitchStatementSyntax or SwitchExpressionSyntax);
        _emit(new DiRec(SymbolKeys.Key(iface), SymbolKeys.Key(impl), _path, Line(inv), lifetime, conditional));
    }

    // ---------- 类型引用 ----------

    private void EmitTypeReference(TypeSyntax ts)
    {
        // 只看声明位置上的类型：参数、字段/局部变量、属性、返回值、泛型实参；表达式里的（new、调用、基类列表）另有边
        var parent = ts.Parent;
        var isDeclarationType = parent is ParameterSyntax or VariableDeclarationSyntax or PropertyDeclarationSyntax or MethodDeclarationSyntax
            or TypeArgumentListSyntax or ArrayTypeSyntax or NullableTypeSyntax;
        if (!isDeclarationType) return;
        if (ts is PredefinedTypeSyntax) return;
        if (ts.Ancestors().Any(a => a is BaseListSyntax or BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax or TypeOfExpressionSyntax or AttributeSyntax)) return;
        if (_model.GetSymbolInfo(ts).Symbol is not INamedTypeSymbol type || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter) return;

        // 参数列表、返回类型位置上 GetEnclosingSymbol 会给出类型而不是方法，所以按语法祖先找所属成员
        var member = ts.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax or LocalFunctionStatementSyntax);
        string? from = member switch
        {
            FieldDeclarationSyntax field => _model.GetDeclaredSymbol(field.Declaration.Variables[0]) is { } f ? SymbolKeys.Key(f) : null,
            EventFieldDeclarationSyntax ev => _model.GetDeclaredSymbol(ev.Declaration.Variables[0]) is { } f ? SymbolKeys.Key(f) : null,
            BaseNamespaceDeclarationSyntax => null,
            not null => _model.GetDeclaredSymbol(member) is { } m ? SymbolKeys.Key(m) : null,
            null => EnclosingMemberKey(ts.SpanStart),
        };
        if (from is null) return;
        Edge(from, type, "references", 1.0, Line(ts));
    }

    // ---------- 工具 ----------

    /// <summary>包含该位置的成员级符号（方法 / 属性 / 字段 / 类型）；lambda 和访问器归到外层。</summary>
    private string? EnclosingMemberKey(int position)
    {
        var symbol = _model.GetEnclosingSymbol(position);
        while (symbol is not null)
        {
            switch (symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LambdaMethod }:
                    symbol = symbol.ContainingSymbol; continue;
                case IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet, AssociatedSymbol: { } prop }:
                    return SymbolKeys.Key(prop);
                case IFieldSymbol { AssociatedSymbol: IPropertySymbol autoProp }:
                    return SymbolKeys.Key(autoProp); // 属性初始化器里的位置给出的是自动属性的后备字段
                case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol:
                    return SymbolKeys.Key(symbol);
                case INamespaceSymbol:
                    return null;
                default:
                    symbol = symbol.ContainingSymbol; continue;
            }
        }
        return null;
    }

    /// <param name="site">调用点：调用名的偏移与所在控制结构（见 <see cref="CallSyntax"/>）；类型级 / 声明级的边没有调用点。</param>
    private void Edge(string from, ISymbol to, string kind, double confidence, int line, string? via = null, int? evidenceLine = null,
        (int Pos, string? Ctx)? site = null)
    {
        var toKey = SymbolKeys.Key(to);
        if (toKey.Length == 0) return;
        if (!_seenEdges.Add((from, toKey, kind, line, via))) return;
        var external = !SymbolKeys.IsInRepo(to, _repoRoot);
        _emit(new EdgeRec(from, toKey, external, kind, confidence, _path, line, via, evidenceLine, site?.Pos, site?.Ctx));
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int Line(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? 0;
}
