using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class JavaScriptAdapter() : LanguageAdapterBase(
    "javascript", "JavaScript", [".js", ".mjs", ".cjs", ".jsx"],
    QueryResources.Load("javascript.definitions.scm"),
    QueryResources.Load("javascript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition)
    {
        var parent = definition.Parent;
        if (parent is not null && parent.Type == "export_statement") return "export";
        if (parent is not null && parent.Parent is { } grand && grand.Type == "export_statement") return "export";
        return null;
    }
}
