using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed partial class CSharpAdapter() : LanguageAdapterBase(
    "csharp", "C#", [".cs"],
    QueryResources.Load("csharp.definitions.scm"),
    QueryResources.Load("csharp.references.scm"))
{
    private static readonly string[] VisibilityModifiers = ["public", "private", "protected", "internal"];

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

    /// <summary>handles 捕获的是 base_list 里泛型接口的第一个类型实参；只有接口名以 Handler / Consumer 结尾才算"处理该类型"。</summary>
    protected override bool AcceptReference(string captureName, Node node)
    {
        if (captureName != "handles") return true;
        var genericName = node.Parent?.Parent; // identifier → type_argument_list → generic_name
        var iface = genericName?.Type == "generic_name" ? genericName.FirstChild?.Text : null;
        return iface is not null && HandlerInterface().IsMatch(iface);
    }

    [GeneratedRegex("^I[A-Z]")]
    private static partial Regex InterfaceName();

    [GeneratedRegex("(Handler|Consumer)$")]
    private static partial Regex HandlerInterface();
}
