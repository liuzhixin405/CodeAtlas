using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TypeScriptAdapter() : LanguageAdapterBase(
    "typescript", "TypeScript", [".ts", ".mts", ".cts"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("typescript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition) => TypeScriptVisibility(definition);

    /// <summary>accessibility_modifier（public/private/protected）优先；否则定义（或其所在声明）直接被 export_statement 包裹则为 "export"。</summary>
    internal static string? TypeScriptVisibility(Node definition)
    {
        foreach (var child in definition.Children)
            if (child.Type == "accessibility_modifier") return child.Text;
        var parent = definition.Parent;
        if (parent is not null && parent.Type == "export_statement") return "export";
        if (parent is not null && parent.Parent is { } grand && grand.Type == "export_statement") return "export";
        return null;
    }
}
