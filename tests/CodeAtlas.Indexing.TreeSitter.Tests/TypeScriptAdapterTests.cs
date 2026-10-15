using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class TypeScriptAdapterTests
{
    internal static string Fixture(string rel) => Path.Combine(AppContext.BaseDirectory, "Fixtures", rel);

    private static ParsedFile ParseSvc()
    {
        using var adapter = new TypeScriptAdapter();
        return adapter.Parse("src/svc.ts", File.ReadAllText(Fixture("ts/svc.ts")));
    }

    [Fact]
    public void Parse_FirstSymbolIsModule_WithPathAsQualifiedName()
    {
        var f = ParseSvc();
        Assert.Equal(SymbolKind.Module, f.Symbols[0].Kind);
        Assert.Equal("svc.ts", f.Symbols[0].Name);
        Assert.Equal("src/svc.ts", f.Symbols[0].QualifiedName);
        Assert.Null(f.Symbols[0].ParentIndex);
        Assert.Equal("typescript", f.Language);
    }

    [Fact]
    public void Parse_ExtractsAllDefinitionKinds()
    {
        var f = ParseSvc();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["src/svc.ts:Shapes"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["src/svc.ts:Base"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Base.run"].Kind);
        Assert.Equal(SymbolKind.Interface, byQ["src/svc.ts:IRepo"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:IRepo.find"].Kind);
        Assert.Equal(SymbolKind.Property, byQ["src/svc.ts:IRepo.count"].Kind);
        Assert.Equal(SymbolKind.Enum, byQ["src/svc.ts:Color"].Kind);
        Assert.Equal(SymbolKind.TypeAlias, byQ["src/svc.ts:Id"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["src/svc.ts:Svc"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["src/svc.ts:Svc.repo"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["src/svc.ts:Svc.count"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Svc.constructor"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Svc.find"].Kind);
        // get total() 是 getter：按属性访问（svc.total），不是可调用的方法 → Property；没有同名 setter，符号总数不变
        Assert.Equal(SymbolKind.Property, byQ["src/svc.ts:Svc.total"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:top"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:arrow"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:fexpr"].Kind);
        Assert.Equal(18, f.Symbols.Count);
    }

    [Fact]
    public void Parse_PositionsAreOneBased_AndParentLinksFollowContainment()
    {
        var f = ParseSvc();
        var svc = f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc");
        var find = f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc.find");

        Assert.Equal(8, svc.StartLine);
        Assert.Equal(8, svc.StartCol);
        Assert.Equal(14, svc.EndLine);
        Assert.Equal(12, find.StartLine);
        Assert.Equal(3, find.StartCol);
        Assert.Equal(f.Symbols.IndexOf(svc), find.ParentIndex);
        Assert.Equal(0, svc.ParentIndex);
        Assert.Equal("class Svc extends Base<Foo> implements IRepo, IOther {", svc.Signature);
        Assert.Equal("find(id: number): Foo { return this.repo.find(id); }", find.Signature);
    }

    [Fact]
    public void Parse_Visibility_FromAccessibilityModifierOrExport()
    {
        var f = ParseSvc();
        Assert.Equal("private", f.Symbols.Single(s => s.Name == "repo").Visibility);
        Assert.Equal("export", f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc").Visibility);
        Assert.Equal("export", f.Symbols.Single(s => s.Name == "arrow").Visibility);
        Assert.Null(f.Symbols.Single(s => s.Name == "fexpr").Visibility);
    }

    [Fact]
    public void Parse_ExtractsReferences_WithEnclosingSymbol()
    {
        var f = ParseSvc();
        int Idx(string q) => f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == q));
        var top = Idx("src/svc.ts:top");
        var svcFind = Idx("src/svc.ts:Svc.find");
        var svc = Idx("src/svc.ts:Svc");
        var irepo = Idx("src/svc.ts:IRepo");

        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((top, "Svc", EdgeKind.Calls), refs);
        Assert.Contains((top, "find", EdgeKind.Calls), refs);
        Assert.Contains((top, "helper", EdgeKind.Calls), refs);
        Assert.Contains((top, "format", EdgeKind.Calls), refs);
        Assert.Contains((top, "Foo", EdgeKind.References), refs);
        Assert.Contains((svcFind, "find", EdgeKind.Calls), refs);
        Assert.Contains((svc, "Base", EdgeKind.Inherits), refs);
        Assert.Contains((svc, "IRepo", EdgeKind.Implements), refs);
        Assert.Contains((svc, "IOther", EdgeKind.Implements), refs);
        Assert.Contains((irepo, "IBase", EdgeKind.Inherits), refs);
        Assert.Contains((0, "./foo", EdgeKind.Imports), refs);
        Assert.Contains((0, "util", EdgeKind.Imports), refs);
        Assert.DoesNotContain(refs, r => r.Name == "number");
    }

    [Fact]
    public void Parse_Imports_PathFlagReflectsRelativeness()
    {
        var f = ParseSvc();
        Assert.Equal(2, f.Imports.Count);
        Assert.Contains(f.Imports, i => i.Target == "./foo" && i.IsPath && i.Line == 1);
        Assert.Contains(f.Imports, i => i.Target == "util" && !i.IsPath && i.Line == 2);
    }

    [Fact]
    public void Parse_FileWithSyntaxError_StillExtractsValidSymbols()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/broken.ts", File.ReadAllText(Fixture("ts/broken.ts")));

        var names = f.Symbols.Select(s => s.Name).ToList();
        Assert.Contains("Good", names);
        Assert.Contains("ok", names);
        Assert.Contains("StillHere", names);
        Assert.Contains("fine", names);
        Assert.Contains(f.References, r => r.Name == "helper" && r.Kind == EdgeKind.Calls);
    }

    [Fact]
    public void Parse_EmptySource_YieldsOnlyModule()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/empty.ts", "");
        Assert.Single(f.Symbols);
        Assert.Empty(f.References);
    }

    [Fact]
    public void Parse_IsSafeToCallConcurrently()
    {
        using var adapter = new TypeScriptAdapter();
        var src = File.ReadAllText(Fixture("ts/svc.ts"));
        var results = new ParsedFile[16];
        Parallel.For(0, 16, i => results[i] = adapter.Parse($"src/{i}.ts", src));
        Assert.All(results, r => Assert.Equal(18, r.Symbols.Count));
    }
}
