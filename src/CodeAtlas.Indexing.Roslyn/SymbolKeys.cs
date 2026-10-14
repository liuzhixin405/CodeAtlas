using CodeAtlas.Core.Roslyn;
using Microsoft.CodeAnalysis;

namespace CodeAtlas.Indexing.Roslyn;

/// <summary>Roslyn 符号 → CodeAtlas 的限定名 / 种类 / 签名。限定名是边挂接的依据，必须稳定。</summary>
public static class SymbolKeys
{
    private static readonly SymbolDisplayFormat TypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly SymbolDisplayFormat ParamTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// 限定名。方法：Ns.Type.Method&lt;T&gt;(int, List&lt;Item&gt;)；构造函数用类型名；局部函数挂在外层方法下。
    /// 泛型实例化回到原始定义，扩展方法回到定义它的静态方法。
    /// </summary>
    public static string Key(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamespaceSymbol ns:
                return ns.IsGlobalNamespace ? "" : ns.ToDisplayString();
            case INamedTypeSymbol t:
                return t.OriginalDefinition.ToDisplayString(TypeFormat);
            case IMethodSymbol m:
            {
                m = (m.ReducedFrom ?? m).OriginalDefinition;
                if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && m.AssociatedSymbol is { } prop) return Key(prop);
                var name = m.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? m.ContainingType.Name : m.Name;
                var typeParams = m.TypeParameters.Length > 0 ? "<" + string.Join(", ", m.TypeParameters.Select(p => p.Name)) + ">" : "";
                var parameters = string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(ParamTypeFormat)));
                var container = m.ContainingSymbol is IMethodSymbol outer ? Key(outer) : Key(m.ContainingType);
                return $"{container}.{name}{typeParams}({parameters})";
            }
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                return $"{Key(symbol.ContainingType)}.{symbol.Name}";
            default:
                return symbol.ToDisplayString();
        }
    }

    /// <summary>
    /// 分派匹配用的消息键。闭合的构造泛型（IdentifiedCommand&lt;CreateOrderCommand, bool&gt;）保留类型实参，
    /// 这样 Handler 字典能把具体子类和开放泛型基 Handler 分开；其他类型与 <see cref="Key"/> 相同（泛型定义）。
    /// </summary>
    public static string MessageKey(ITypeSymbol type) =>
        IsClosedConstructedGeneric(type) ? type.ToDisplayString(TypeFormat) : Key(type);

    /// <summary>via 里显示的消息短名：构造泛型带类型实参短名（Identified&lt;CreateItemCommand, bool&gt;），其他只取名字。</summary>
    public static string MessageShortName(ITypeSymbol type) =>
        IsClosedConstructedGeneric(type) ? type.ToDisplayString(ParamTypeFormat) : type.Name;

    /// <summary>构造泛型且类型实参里没有任何类型参数（T、R 这类占位）。</summary>
    public static bool IsClosedConstructedGeneric(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named
        && !SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition)
        && !ContainsTypeParameter(named);

    internal static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
        INamedTypeSymbol n => n.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    /// <summary>与数据库里 symbols.kind 的小写名一致。</summary>
    public static string? Kind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "type_alias",
            _ => null,
        },
        IMethodSymbol m => m.MethodKind == MethodKind.LocalFunction ? "function" : "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "field",
        _ => null,
    };

    public static string Signature(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => $"{t.TypeKind.ToString().ToLowerInvariant()} {t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}",
        INamespaceSymbol ns => $"namespace {ns.ToDisplayString()}",
        _ => symbol.ToDisplayString(SignatureFormat),
    };

    public static string? Visibility(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => null,
    };

    /// <summary>符号是否定义在仓库源码里（而不是引用的程序集或仓库外的项目）。</summary>
    /// <summary>obj/ bin/ 下的生成代码（gRPC stub、AssemblyInfo、GlobalUsings）不在索引里，指向它们的边按仓库外处理。</summary>
    public static bool IsGeneratedPath(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/", StringComparison.OrdinalIgnoreCase) || p.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInRepo(ISymbol symbol, string repoRoot)
    {
        foreach (var loc in symbol.OriginalDefinition.Locations)
        {
            if (!loc.IsInSource) continue;
            var path = loc.SourceTree?.FilePath;
            if (path is not null && path.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) && !IsGeneratedPath(path)) return true;
        }
        return false;
    }
}
