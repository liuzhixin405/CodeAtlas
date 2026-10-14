using CodeAtlas.Core.Roslyn;
namespace CodeAtlas.Indexing.Roslyn.Tests;

/// <summary>对 Fixtures/RoslynSample 跑一次分析器，所有测试共享结果（打开解决方案要几秒）。</summary>
public sealed class AnalyzerFixture : IAsyncLifetime
{
    public List<RoslynRecord> Records { get; } = [];
    /// <summary>用源码树里的 fixture（不是 bin 下的拷贝），这样 obj/project.assets.json 里的路径才对得上。</summary>
    public string Root { get; } = LocateFixture();

    private static string LocateFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeAtlas.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("找不到仓库根目录（CodeAtlas.slnx）");
        return Path.Combine(dir.FullName, "tests", "CodeAtlas.Indexing.Roslyn.Tests", "Fixtures", "RoslynSample");
    }

    public async Task InitializeAsync()
    {
        var sln = Path.Combine(Root, "RoslynSample.sln");
        if (!File.Exists(Path.Combine(Root, "Sample.App", "obj", "project.assets.json")))
        {
            var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{Path.Combine(Root, "Sample.App", "Sample.App.csproj")}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            await restore.WaitForExitAsync();
        }
        await foreach (var r in RoslynAnalyzer.AnalyzeAsync(sln, Root, CancellationToken.None))
            Records.Add(r);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public IEnumerable<SymbolRec> Symbols => Records.OfType<SymbolRec>();
    public IEnumerable<EdgeRec> Edges => Records.OfType<EdgeRec>();
    /// <summary>partial 类型与命名空间会在多个文件里各出现一次，取第一个。</summary>
    public SymbolRec Symbol(string qname) => Symbols.First(s => s.QualifiedName == qname);
    public IEnumerable<EdgeRec> EdgesFrom(string fromQname, string kind) => Edges.Where(e => e.FromQualifiedName == fromQname && e.Kind == kind);
}

public class AnalyzerTests(AnalyzerFixture fx) : IClassFixture<AnalyzerFixture>
{
    [Fact]
    public void Symbols_QualifiedNamesCarryParameterTypes_SoOverloadsAreDistinct()
    {
        Assert.NotNull(fx.Symbol("Sample.Core.IRepo.Find(int)"));
        Assert.NotNull(fx.Symbol("Sample.Core.IRepo.Find(string)"));
        Assert.NotNull(fx.Symbol("Sample.Core.Repo.Find(int)"));
        Assert.Equal("method", fx.Symbol("Sample.Core.Repo.Find(string)").Kind);
        Assert.Equal("Sample.Core.Repo", fx.Symbol("Sample.Core.Repo.Find(string)").ParentQualifiedName);
        Assert.Equal("class", fx.Symbol("Sample.Core.Repo").Kind);
        Assert.Equal("interface", fx.Symbol("Sample.Core.IRepo").Kind);
        Assert.Equal("class", fx.Symbol("Sample.Core.Item").Kind); // record → class
        Assert.Equal("namespace", fx.Symbol("Sample.Core").Kind);
        Assert.Equal("Item Find(int id)", fx.Symbol("Sample.Core.Repo.Find(int)").Signature);
        Assert.Equal("public", fx.Symbol("Sample.Core.Repo.Find(int)").Visibility);
    }

    [Fact]
    public void Symbols_PositionsAreOneBased_AndStartAtDeclaration()
    {
        var find = fx.Symbol("Sample.Core.Repo.Find(int)");
        Assert.Equal("Sample.Core/Repo.cs", find.Path);
        Assert.Equal(5, find.StartLine);
        Assert.Equal(5, find.StartCol);
    }

    [Fact]
    public void Calls_ResolveToTheExactOverload()
    {
        var byId = fx.EdgesFrom("Sample.App.Controller.ById(int)", "calls").Single();
        Assert.Equal("Sample.Core.IRepo.Find(int)", byId.ToQualifiedName);
        Assert.Equal(1.0, byId.Confidence);
        Assert.False(byId.ToIsExternal);
        var byName = fx.EdgesFrom("Sample.App.Controller.ByName(string)", "calls").Single();
        Assert.Equal("Sample.Core.IRepo.Find(string)", byName.ToQualifiedName);
    }

    [Fact]
    public void Calls_ExtensionMethodFromFramework_IsExternal()
    {
        var all = fx.EdgesFrom("Sample.Core.Repo.All()", "calls").ToList();
        var toList = Assert.Single(all, e => e.ToQualifiedName.Contains("ToList"));
        Assert.True(toList.ToIsExternal);
        Assert.StartsWith("System.Linq.Enumerable.ToList", toList.ToQualifiedName);
        // new Item(1) → 目标是类型，不是构造函数
        Assert.Contains(all, e => e.ToQualifiedName == "Sample.Core.Item" && !e.ToIsExternal);
    }

    [Fact]
    public void PropertyAccess_IsNotACall()
    {
        var count = fx.EdgesFrom("Sample.App.Controller.Count()", "calls").ToList();
        Assert.Single(count); // 只有 repo.All()
        Assert.Equal("Sample.Core.IRepo.All()", count[0].ToQualifiedName);
    }

    [Fact]
    public void Implements_TypeLevelAndMethodLevel()
    {
        Assert.Contains(fx.EdgesFrom("Sample.Core.Repo", "implements"), e => e.ToQualifiedName == "Sample.Core.IRepo");
        Assert.Contains(fx.EdgesFrom("Sample.Core.Repo.Find(int)", "implements"), e => e.ToQualifiedName == "Sample.Core.IRepo.Find(int)");
        Assert.Contains(fx.EdgesFrom("Sample.Core.Repo.Find(string)", "implements"), e => e.ToQualifiedName == "Sample.Core.IRepo.Find(string)");
        Assert.Contains(fx.EdgesFrom("Sample.Core.Repo.All()", "implements"), e => e.ToQualifiedName == "Sample.Core.IRepo.All()");
        // 泛型接口实例化：Handler 实现的是 IQueryHandler<ListQuery, List<Item>>，方法级边指向接口定义
        Assert.Contains(fx.EdgesFrom("Sample.Core.ListQueryHandler.HandleAsync(ListQuery, CancellationToken)", "implements"),
            e => e.ToQualifiedName == "Sample.Core.IQueryHandler<TQuery, TResult>.HandleAsync(TQuery, CancellationToken)");
    }

    [Fact]
    public void PartialClass_EmitsOneSymbolPerFile_WithSameQualifiedName()
    {
        var repos = fx.Symbols.Where(s => s.QualifiedName == "Sample.Core.Repo").ToList();
        Assert.Equal(2, repos.Count);
        Assert.Equal(new[] { "Sample.Core/Repo.Extra.cs", "Sample.Core/Repo.cs" }, repos.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Dispatch_HandlesAndDispatchEdges_WithConduitMarks()
    {
        var handles = fx.EdgesFrom("Sample.Core.ListQueryHandler", "dispatches").Single(e => e.Via == "handles");
        Assert.Equal("Sample.Core.ListQuery", handles.ToQualifiedName);

        var list = "Sample.App.Controller.List(int, CancellationToken)";
        var dispatch = fx.EdgesFrom(list, "dispatches").Single();
        Assert.Equal("Sample.Core.ListQueryHandler.HandleAsync(ListQuery, CancellationToken)", dispatch.ToQualifiedName);
        Assert.Equal("dispatch:ListQuery", dispatch.Via);
        Assert.Equal(21, dispatch.EvidenceLine);

        var calls = fx.EdgesFrom(list, "calls").ToList();
        Assert.Contains(calls, e => e.ToQualifiedName.StartsWith("Sample.Core.IDispatcher.QueryAsync") && e.Via == "conduit");
        Assert.Contains(calls, e => e.ToQualifiedName == "Sample.Core.ListQuery" && e.Via == "conduit");
    }

    [Fact]
    public void Dispatch_ConstructedGenericMessage_GoesOnlyToTheMatchingHandlerSubclass()
    {
        // Identified<CreateItemCommand, bool> 有具体子类 → 只分派到它（子类没有自己的 HandleAsync，入口落在类型上）
        var dispatch = fx.EdgesFrom("Sample.App.Controller.Create(string, CancellationToken)", "dispatches").Single();
        Assert.Equal("Sample.Core.CreateItemIdentifiedHandler", dispatch.ToQualifiedName);
        Assert.Equal("dispatch:Identified<CreateItemCommand, bool>", dispatch.Via);

        // handles 边的目标仍是消息的泛型定义（符号表里只有定义）
        var handles = fx.EdgesFrom("Sample.Core.CreateItemIdentifiedHandler", "dispatches").Single(e => e.Via == "handles");
        Assert.Equal("Sample.Core.Identified<TCommand, TResult>", handles.ToQualifiedName);
    }

    [Fact]
    public void Dispatch_ConstructedGenericMessage_WithoutSubclass_FallsBackToOpenGenericHandler()
    {
        var dispatch = fx.EdgesFrom("Sample.App.Controller.Archive(int, CancellationToken)", "dispatches").Single();
        Assert.Equal("Sample.Core.IdentifiedHandler<TCommand, TResult>.HandleAsync(Identified<TCommand, TResult>, CancellationToken)", dispatch.ToQualifiedName);
        Assert.Equal("dispatch:Identified<ArchiveItemCommand, bool>", dispatch.Via);
    }

    [Fact]
    public void HandlerSubclassWithoutOwnHandle_CallsInheritedHandle_AndRedispatchesTheSubstitutedInnerCommand()
    {
        const string baseHandle = "Sample.Core.IdentifiedHandler<TCommand, TResult>.HandleAsync(Identified<TCommand, TResult>, CancellationToken)";
        foreach (var (subclass, inner, target) in new[]
        {
            ("Sample.Core.CreateItemIdentifiedHandler", "CreateItemCommand", "Sample.Core.CreateItemCommandHandler.HandleAsync(CreateItemCommand, CancellationToken)"),
            ("Sample.Core.DeleteItemIdentifiedHandler", "DeleteItemCommand", "Sample.Core.DeleteItemCommandHandler.HandleAsync(DeleteItemCommand, CancellationToken)"),
        })
        {
            // 子类 → 继承来的基类 HandleAsync（时序图从子类节点能走进基类方法体）
            var call = fx.EdgesFrom(subclass, "calls").Single();
            Assert.Equal((baseHandle, (string?)null), (call.ToQualifiedName, call.Via));
            // 基类里 SendAsync(command.Command) 的 TCommand 按子类的类型实参代换 → 只到对应的那一个内层 Handler
            var redispatch = fx.EdgesFrom(subclass, "dispatches").Single(e => e.Via != "handles");
            Assert.Equal((target, "dispatch:" + inner), (redispatch.ToQualifiedName, redispatch.Via));
        }
        // 基类方法自身的 SendAsync(TCommand) 不代换：不产生分派边（否则又会扇出到全部内层 Handler）
        Assert.Empty(fx.EdgesFrom(baseHandle, "dispatches"));
        Assert.Contains(fx.EdgesFrom(baseHandle, "calls"), e => e.ToQualifiedName == "Sample.Core.IDispatcher.SendAsync<T>(object, CancellationToken)");
    }

    [Fact]
    public void Handles_ConsumerInterface_IsAHandlerToo()
    {
        var handles = fx.EdgesFrom("Sample.Core.OrderPlacedConsumer", "dispatches").Single(e => e.Via == "handles");
        Assert.Equal("Sample.Core.OrderPlaced", handles.ToQualifiedName);
    }

    [Fact]
    public void Di_Registrations_WithLifetimeAndConditional()
    {
        var di = fx.Records.OfType<DiRec>().ToList();
        var scoped = Assert.Single(di, d => d.ImplQualifiedName == "Sample.Core.Repo");
        Assert.Equal(("Sample.Core.IRepo", "scoped", false), (scoped.InterfaceQualifiedName, scoped.Lifetime, scoped.Conditional));
        var singleton = Assert.Single(di, d => d.ImplQualifiedName == "Sample.Core.Dispatcher");
        Assert.Equal(("Sample.Core.IDispatcher", "singleton", true), (singleton.InterfaceQualifiedName, singleton.Lifetime, singleton.Conditional));
    }

    [Fact]
    public void References_FieldAndParameterTypes()
    {
        Assert.Contains(fx.EdgesFrom("Sample.App.Controller.repo", "references"), e => e.ToQualifiedName == "Sample.Core.IRepo");
        Assert.Contains(fx.EdgesFrom("Sample.App.Controller.List(int, CancellationToken)", "references"), e => e.ToIsExternal && e.ToQualifiedName.Contains("CancellationToken"));
    }

    [Fact]
    public void TopLevelStatements_GetAFunctionSymbol_AndTheirCallsAttachToIt()
    {
        var main = fx.Symbol("Program.<Main>$(string[])");
        Assert.Equal("function", main.Kind);
        Assert.Equal("Sample.App/Program.cs", main.Path);
        Assert.Null(main.ParentQualifiedName); // Program 类型是编译器生成的，没有声明可挂，归到文件
        Assert.Contains(fx.EdgesFrom("Program.<Main>$(string[])", "calls"), e => e.ToQualifiedName == "Sample.Core.Repo.Find(int)");
    }

    [Fact]
    public void Indexer_IsAPropertySymbol_AndItsBodyCallsAttachToIt()
    {
        Assert.Equal("property", fx.Symbol("Sample.Core.Catalog.this[]").Kind);
        Assert.Contains(fx.EdgesFrom("Sample.Core.Catalog.this[]", "calls"), e => e.ToQualifiedName == "Sample.Core.Repo.Find(int)");
    }

    [Fact]
    public void PropertyInitializer_CallsBelongToTheProperty_NotTheBackingField()
    {
        Assert.Contains(fx.EdgesFrom("Sample.Core.Catalog.Items", "calls"), e => e.ToIsExternal && e.ToQualifiedName.StartsWith("System.Collections.Generic.List<T>"));
        Assert.DoesNotContain(fx.Edges, e => e.FromQualifiedName.Contains("k__BackingField"));
    }

    [Fact]
    public void RecordSynthesizedMembers_AreNotEmittedAsEdgeSources()
    {
        Assert.Equal("class", fx.Symbol("Sample.Core.Tag").Kind);
        Assert.DoesNotContain(fx.Edges, e => e.FromQualifiedName.StartsWith("Sample.Core.Tag.") || e.FromQualifiedName.StartsWith("Sample.Core.Item."));
        Assert.Contains(fx.EdgesFrom("Sample.Core.Tag", "implements"), e => e.ToQualifiedName == "System.IEquatable<T>"); // 类型级的仍保留
    }

    [Fact]
    public void GeneratedCodeUnderObj_IsNeitherAFile_NorInRepo()
    {
        Assert.DoesNotContain(fx.Records.OfType<FileRec>(), f => f.Path.Contains("/obj/") || f.Path.Contains("/bin/"));
        // 指向 obj/ 下生成代码（AssemblyInfo 等）的边即使出现，也应标为 external 而不是悬空
        Assert.All(fx.Edges.Where(e => e.ToQualifiedName.Contains("GeneratedCode")), e => Assert.True(e.ToIsExternal));
    }

    [Fact]
    public void BrokenProject_IsReportedAsError_OthersStillAnalyzed()
    {
        Assert.Contains(fx.Records.OfType<ErrorRec>(), e => e.Message.Contains("Broken", StringComparison.OrdinalIgnoreCase) || e.Scope.Contains("Broken", StringComparison.OrdinalIgnoreCase));
        var done = fx.Records.OfType<DoneRec>().Single();
        Assert.True(done.Documents >= 6, $"documents={done.Documents}");
        Assert.Contains(fx.Records.OfType<ProgressRec>(), p => p.Phase == "roslyn" && p.Total > 0);
        Assert.Contains(fx.Records.OfType<FileRec>(), f => f.Path == "Sample.App/Controller.cs");
    }
}
