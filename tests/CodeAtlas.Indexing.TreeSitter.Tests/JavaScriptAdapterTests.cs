using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class JavaScriptAdapterTests
{
    [Fact]
    public void JavaScript_ExtractsDefinitionsAndReferences()
    {
        using var adapter = new JavaScriptAdapter();
        var f = adapter.Parse("lib.js", File.ReadAllText(TypeScriptAdapterTests.Fixture("js/lib.js")));

        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal(SymbolKind.Class, byQ["lib.js:A"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["lib.js:A.x"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["lib.js:A.m"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:f"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:g"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:h"].Kind);
        Assert.Equal("javascript", f.Language);

        var m = f.Symbols.IndexOf(byQ["lib.js:A.m"]);
        var fn = f.Symbols.IndexOf(byQ["lib.js:f"]);
        var a = f.Symbols.IndexOf(byQ["lib.js:A"]);
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((m, "n", EdgeKind.Calls), refs);
        Assert.Contains((m, "foo", EdgeKind.Calls), refs);
        Assert.Contains((fn, "A", EdgeKind.Calls), refs);
        Assert.Contains((a, "B", EdgeKind.Inherits), refs);
        Assert.Contains((0, "./x", EdgeKind.Imports), refs);
    }

    [Fact]
    public void Tsx_ParsesJsxAndKeepsTypeScriptSemantics()
    {
        using var adapter = new TsxAdapter();
        var f = adapter.Parse("src/View.tsx", File.ReadAllText(TypeScriptAdapterTests.Fixture("tsx/View.tsx")));

        Assert.Equal("tsx", f.Language);
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal(SymbolKind.Interface, byQ["src/View.tsx:Props"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/View.tsx:View"].Kind);
        Assert.Equal("export", byQ["src/View.tsx:View"].Visibility);

        var view = f.Symbols.IndexOf(byQ["src/View.tsx:View"]);
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((view, "useStore", EdgeKind.Calls), refs);
        Assert.Contains((view, "save", EdgeKind.Calls), refs);
        Assert.Contains((view, "Props", EdgeKind.References), refs);
        Assert.Contains(f.Imports, i => i.Target == "react" && !i.IsPath);
        Assert.Contains(f.Imports, i => i.Target == "./store" && i.IsPath);
    }

    [Fact]
    public void Extensions_AreDistinctAcrossScriptAdapters()
    {
        using var ts = new TypeScriptAdapter();
        using var tsx = new TsxAdapter();
        using var js = new JavaScriptAdapter();
        var all = ts.Extensions.Concat(tsx.Extensions).Concat(js.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
