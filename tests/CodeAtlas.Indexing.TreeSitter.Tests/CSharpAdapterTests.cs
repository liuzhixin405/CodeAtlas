using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class CSharpAdapterTests
{
    private static ParsedFile ParseOrders()
    {
        using var adapter = new CSharpAdapter();
        return adapter.Parse("Orders/OrderService.cs", File.ReadAllText(TypeScriptAdapterTests.Fixture("cs/OrderService.cs")));
    }

    [Fact]
    public void FileScopedNamespace_ContainsEverythingBelowIt()
    {
        var f = ParseOrders();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["Acme.Orders"].Kind);
        Assert.Equal(SymbolKind.Interface, byQ["Acme.Orders.IOrderRepo"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.IOrderRepo.Find"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Acme.Orders.Order"].Kind);
        Assert.Equal(SymbolKind.Struct, byQ["Acme.Orders.Money"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.Money.Amount"].Kind);
        Assert.Equal(SymbolKind.Enum, byQ["Acme.Orders.Status"].Kind);
        Assert.Equal(SymbolKind.TypeAlias, byQ["Acme.Orders.Handler"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Acme.Orders.OrderService"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._repo"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._count"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._other"].Kind);
        Assert.Equal(SymbolKind.Property, byQ["Acme.Orders.OrderService.Count"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.OrderService"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Find"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Run"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["Acme.Orders.OrderService.Run.Local"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Dispose"].Kind);

        var ns = f.Symbols.Single(s => s.QualifiedName == "Acme.Orders");
        Assert.Equal(3, ns.StartLine);
        Assert.Equal(f.Symbols[0].EndLine, ns.EndLine); // 延伸到文件末尾，与 Module 一致
        Assert.Equal(f.Symbols.IndexOf(ns), byQ["Acme.Orders.OrderService"].ParentIndex);
    }

    [Fact]
    public void BlockNamespace_AndNoNamespace()
    {
        using var adapter = new CSharpAdapter();
        var f = adapter.Parse("Legacy.cs", File.ReadAllText(TypeScriptAdapterTests.Fixture("cs/Legacy.cs")));
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["Legacy.Block"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Legacy.Block.Thing"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Legacy.Block.Thing.Go"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["NoNamespace"].Kind);
        Assert.Equal(0, byQ["NoNamespace"].ParentIndex);
        Assert.Equal("internal", byQ["Legacy.Block.Thing"].Visibility);
        Assert.Equal("protected", byQ["Legacy.Block.Thing.Go"].Visibility);
        Assert.Null(byQ["NoNamespace"].Visibility);
    }

    [Fact]
    public void BaseList_SplitsInheritsAndImplementsByIPrefix()
    {
        var f = ParseOrders();
        var svc = f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == "Acme.Orders.OrderService"));
        var repo = f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == "Acme.Orders.IOrderRepo"));
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();

        Assert.Contains((svc, "BaseService", EdgeKind.Inherits), refs);
        Assert.Contains((svc, "IOrderRepo", EdgeKind.Implements), refs);
        Assert.Contains((svc, "IDisposable", EdgeKind.Implements), refs);
        Assert.Contains((repo, "IRepo", EdgeKind.Implements), refs);
    }

    [Fact]
    public void Calls_IncludeMemberAccess_Generics_ObjectCreation_LocalFunctions()
    {
        var f = ParseOrders();
        int Idx(string q) => f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == q));
        var find = Idx("Acme.Orders.OrderService.Find");
        var run = Idx("Acme.Orders.OrderService.Run");
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();

        Assert.Contains((find, "Log", EdgeKind.Calls), refs);
        Assert.Contains((find, "Find", EdgeKind.Calls), refs);
        Assert.Contains((run, "OrderService", EdgeKind.Calls), refs);
        Assert.Contains((run, "List", EdgeKind.Calls), refs);
        Assert.Contains((run, "Do", EdgeKind.Calls), refs);
        Assert.Contains((run, "Find", EdgeKind.Calls), refs);
        Assert.Contains((run, "Local", EdgeKind.Calls), refs);
        Assert.Contains((run, "Dispose", EdgeKind.Calls), refs);
        Assert.Contains((Idx("Acme.Orders.OrderService._repo"), "IOrderRepo", EdgeKind.References), refs);
    }

    [Fact]
    public void Usings_AreNamespaceImports_NotPaths()
    {
        var f = ParseOrders();
        Assert.Equal(2, f.Imports.Count);
        Assert.Contains(f.Imports, i => i.Target == "System" && !i.IsPath && i.Line == 1);
        Assert.Contains(f.Imports, i => i.Target == "Microsoft.Extensions.Logging" && !i.IsPath && i.Line == 2);
        Assert.Contains(f.References, r => r.FromIndex == 0 && r.Name == "Microsoft.Extensions.Logging" && r.Kind == EdgeKind.Imports);
    }

    [Fact]
    public void HandlerInterface_EmitsHandlesReference_ForFirstTypeArgument()
    {
        using var adapter = new CSharpAdapter();
        var f = adapter.Parse("H.cs", """
            namespace A;
            public sealed class ListFeedsHandler : IQueryHandler<ListFeedsQuery, List<FeedDto>>, IDisposable
            {
                public Task<List<FeedDto>> HandleAsync(ListFeedsQuery q, CancellationToken ct) => repo.ListAsync(q.Id, ct);
            }
            public class Behavior : IPipelineBehavior<ListFeedsQuery, int> { }
            """);
        var handler = f.Symbols.IndexOf(f.Symbols.Single(s => s.Name == "ListFeedsHandler"));
        var handles = f.References.Where(r => r.Kind == EdgeKind.Dispatches && r.Via == "handles").ToList();
        Assert.Single(handles);
        Assert.Equal((handler, "ListFeedsQuery"), (handles[0].FromIndex, handles[0].Name));
        // IPipelineBehavior 不是 Handler/Consumer，不产生 handles；普通 implements 边照旧
        Assert.Contains(f.References, r => r.FromIndex == handler && r.Name == "IQueryHandler" && r.Kind == EdgeKind.Implements);
    }

    [Fact]
    public void ConsumerInterface_EmitsHandlesReference_ForMassTransitStyleIConsumer()
    {
        using var adapter = new CSharpAdapter();
        var f = adapter.Parse("C.cs", """
            namespace A;
            public sealed class OrderPlacedConsumer : IConsumer<OrderPlaced>
            {
                public Task Consume(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
            }
            """);
        var consumer = f.Symbols.IndexOf(f.Symbols.Single(s => s.Name == "OrderPlacedConsumer"));
        var handles = Assert.Single(f.References, r => r.Kind == EdgeKind.Dispatches && r.Via == "handles");
        Assert.Equal((consumer, "OrderPlaced"), (handles.FromIndex, handles.Name));
        Assert.Contains(f.References, r => r.FromIndex == consumer && r.Name == "IConsumer" && r.Kind == EdgeKind.Implements);
    }

    [Fact]
    public void Signature_SkipsAttributeLines()
    {
        using var adapter = new CSharpAdapter();
        var f = adapter.Parse("C.cs", "public class C\n{\n    [HttpPost(\"{id:int}/approve\")]\n    [ProducesResponseType(200)]\n    public async Task<int> ApproveAsync(int id)\n    {\n        return id;\n    }\n}\n");
        var m = f.Symbols.Single(s => s.Name == "ApproveAsync");
        Assert.Equal("public async Task<int> ApproveAsync(int id)", m.Signature);
        Assert.Equal(3, m.StartLine); // 定义范围仍从特性开始
    }

    [Fact]
    public void Visibility_FromModifiers()
    {
        var f = ParseOrders();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal("public", byQ["Acme.Orders.OrderService"].Visibility);
        Assert.Equal("private", byQ["Acme.Orders.OrderService._repo"].Visibility);
        Assert.Equal("public", byQ["Acme.Orders.OrderService.Find"].Visibility);
    }
}
