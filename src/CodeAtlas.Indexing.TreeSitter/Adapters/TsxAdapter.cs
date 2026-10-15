namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TsxAdapter() : ScriptAdapterBase(
    "tsx", "TSX", [".tsx"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("tsx.references.scm"));
