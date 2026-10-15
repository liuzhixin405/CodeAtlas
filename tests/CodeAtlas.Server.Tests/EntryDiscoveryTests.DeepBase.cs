using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server.Tests;

/// <summary>Roslyn 把只差泛型参数个数的同名基类分开，控制器到 ControllerBase 的继承链会比 4 层深（Bing 实测 TreesControllerBase 走 5 层）。</summary>
public partial class EntryDiscoveryTests
{
    [Fact]
    public void GenericController_FiveLevelsBelowControllerBase_IsStillAController_AndSelfInheritanceDoesNotLoop()
    {
        var path = "Acme.Api/Trees.cs";
        Files[path] =
        [
            "namespace Acme.Api;",                                                // 1
            "public abstract class TreesControllerBase<T> : CrudControllerBase<T, T> {", // 2
            "    [HttpGet(\"tree\")]",                                            // 3
            "    public void GetTree() { }",                                       // 4
            "}",                                                                   // 5
        ];
        List<MapSymbol> symbols =
        [
            Sym(200, SymbolKind.Class, "Acme.Api.TreesControllerBase<T>", path, null, 2, 5),
            Sym(201, SymbolKind.Method, "Acme.Api.TreesControllerBase<T>.GetTree()", path, 200, 4, 4, "public"),   // Roslyn 的限定名
            Sym(202, SymbolKind.Class, "Acme.Api.CrudControllerBase<TDto, TQuery>", "Acme.Api/Crud.cs", null, 1, 1),
            Sym(203, SymbolKind.Class, "Acme.Api.CrudControllerBase<TDto, TCreate, TUpdate, TQuery>", "Acme.Api/Crud.cs", null, 2, 2),
            Sym(204, SymbolKind.Class, "Acme.Api.QueryControllerBase<TDto, TQuery>", "Acme.Api/Query.cs", null, 1, 1),
            Sym(205, SymbolKind.Class, "Acme.Api.ApiControllerBase", "Acme.Api/ApiBase.cs", null, 1, 1),
            Sym(206, SymbolKind.Class, "Acme.Api.BingControllerBase", "Acme.Api/BingBase.cs", null, 1, 1),
            Sym(290, SymbolKind.External, "external:Microsoft.AspNetCore.Mvc.ControllerBase", "<external>", null, 0, 0),
        ];
        List<MapEdge> edges =
        [
            new(200, 200, EdgeKind.Inherits),                                      // tree-sitter 式的自环，不能卡住
            new(200, 202, EdgeKind.Inherits), new(202, 203, EdgeKind.Inherits), new(203, 204, EdgeKind.Inherits),
            new(204, 205, EdgeKind.Inherits), new(205, 206, EdgeKind.Inherits), new(206, 290, EdgeKind.Inherits),
        ];
        try
        {
            var entries = EntryDiscovery.Discover(symbols, edges, Containers, Read);
            var e = Assert.Single(entries, x => x.SymbolId == 201);   // 泛型类成员的短名是 GetTree，不是类名（曾被当成构造函数滤掉）
            Assert.Equal(("aspnet.controller", "http", "GET"), (e.RuleId, e.Kind, e.HttpMethod));
        }
        finally { Files.Remove(path); }
    }
}

public class MapSymbolNameTests
{
    [Theory]
    [InlineData("Ns.Repo<T>.Find(int)", "Find")]                                   // Roslyn：泛型类的成员
    [InlineData("Ns.Outer<TKey, TValue>.Inner<T>.Run<TResult>(T)", "Run")]
    [InlineData("Ns.Cache<Dictionary<string, List<int>>>.Get", "Get")]             // 嵌套泛型实参
    [InlineData("Ns.Repo<T>", "Repo")]
    [InlineData("external:System.Linq.Enumerable.ToList<TSource>(IEnumerable<TSource>)", "ToList")]
    public void Name_StripsEveryGenericArgumentList(string qualified, string expected) =>
        Assert.Equal(expected, new MapSymbol(1, SymbolKind.Method, qualified, "a.cs", null).Name);
}
