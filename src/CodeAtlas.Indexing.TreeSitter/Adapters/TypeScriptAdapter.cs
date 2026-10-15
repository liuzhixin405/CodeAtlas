namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TypeScriptAdapter() : ScriptAdapterBase(
    "typescript", "TypeScript", [".ts", ".mts", ".cts"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("typescript.references.scm"));
