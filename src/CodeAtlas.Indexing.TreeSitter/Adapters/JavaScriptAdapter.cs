namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class JavaScriptAdapter() : ScriptAdapterBase(
    "javascript", "JavaScript", [".js", ".mjs", ".cjs", ".jsx"],
    QueryResources.Load("javascript.definitions.scm"),
    QueryResources.Load("javascript.references.scm"));
