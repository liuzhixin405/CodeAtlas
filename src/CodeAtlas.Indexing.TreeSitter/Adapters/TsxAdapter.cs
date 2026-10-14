using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TsxAdapter() : LanguageAdapterBase(
    "tsx", "TSX", [".tsx"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("typescript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition) => TypeScriptAdapter.TypeScriptVisibility(definition);
}
