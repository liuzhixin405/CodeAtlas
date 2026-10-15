using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

/// <summary>
/// 只有 tree-sitter 时 C# 调用的解析（接收者类型、重载个数、扩展方法、this / base、委托、nameof）。
/// 每个用例在临时目录里写一个小仓库，整仓索引后看边落到哪个符号、置信度多少。
/// </summary>
public class CSharpResolutionTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-csres-" + Guid.NewGuid().ToString("N"));
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
    }

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<IndexReader> IndexAsync()
    {
        var report = await new TreeSitterIndexer(_db, [new CSharpAdapter()], new HeuristicEdgeResolver()).IndexAsync(_repo);
        Assert.Empty(report.Errors);
        return new IndexReader(_db);
    }

    /// <summary>from 方法在 line 行的 calls 边：(目标限定名, 目标起始行, 置信度, via)；external 的起始行为 0。</summary>
    private static List<(string To, int ToLine, double Confidence, string? Via)> Calls(IndexReader reader, string from, int line) =>
        reader.GetNeighbors(reader.FindByQualifiedName(from).Single().Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Where(n => n.Edge.Line == line)
            .Select(n => (n.Edge.ToName, n.Other is { Kind: not SymbolKind.External } o ? o.StartLine : 0, n.Edge.Confidence, n.Edge.Via))
            .OrderBy(x => x.Via ?? "", StringComparer.Ordinal).ThenBy(x => x.ToName, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task OverloadedStaticRun_ResolvesInsideRepo_ByArgumentCount()
    {
        Write("Cli/Program.cs", """
            using App.Cli.Commands;
            namespace App.Cli;
            public static class Program
            {
                public static int Main(string[] args)
                {
                    return Run(args, null, null, default);
                }
                public static int Run(string[] args, object stdout, object stderr) => Run(args, stdout, stderr, default);
                public static int Run(string[] args, object stdout, object stderr, int ct)
                {
                    return ArchCommand.Run(args, stdout, stderr);
                }
            }
            """);
        Write("Cli/Commands/ArchCommand.cs", """
            namespace App.Cli.Commands;
            internal static class ArchCommand
            {
                public static int Run(string[] args, object stdout, object stderr) => 0;
            }
            """);
        // 干扰：别的项目里同名的 Program.Run / ArchCommand.Run
        Write("Worker/Program.cs", """
            namespace App.Worker;
            public static class Program
            {
                public static int Run(string[] args, object a, object b, int c) => 0;
            }
            public static class ArchCommand
            {
                public static int Run(string[] args, object a, object b) => 0;
            }
            """);
        var reader = await IndexAsync();

        // Main → 4 个实参的 Run（第 10 行那个重载），不是 external:Run
        Assert.Equal([("App.Cli.Program.Run", 10, 0.9, null)], Calls(reader, "App.Cli.Program.Main", 7));
        // 3 个实参的重载转调 4 个实参的
        var run = reader.FindByQualifiedName("App.Cli.Program.Run").Single(s => s.StartLine == 9);
        var fromRun3 = reader.GetNeighbors(run.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).Single();
        Assert.Equal((10, 0.9), (fromRun3.Other!.StartLine, fromRun3.Edge.Confidence));
        // ArchCommand.Run：using 的命名空间里的那个，不是 App.Worker 里的
        var run4 = reader.FindByQualifiedName("App.Cli.Program.Run").Single(s => s.StartLine == 10);
        var arch = reader.GetNeighbors(run4.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls }).Single();
        Assert.Equal(("App.Cli.Commands.ArchCommand.Run", 0.9), (arch.Edge.ToName, arch.Edge.Confidence));
    }

    [Fact]
    public async Task SameArgumentCountOverloads_AreCandidatesBelowHalf()
    {
        Write("A.cs", """
            namespace App;
            public class Printer
            {
                public void Print(int x) { }
                public void Print(string s) { }
                public void Go() { Print(1); }
            }
            """);
        var reader = await IndexAsync();
        Assert.Equal([("App.Printer.Print", 4, 0.3, null), ("App.Printer.Print", 5, 0.3, null)], Calls(reader, "App.Printer.Go", 6));
    }

    [Fact]
    public async Task FieldReceiver_ResolvesByFieldType_NotToSameNamedMethodOfOwnClass()
    {
        Write("Uow/IUnitOfWork.cs", """
            namespace App.Uow;
            public interface IUnitOfWork
            {
                int Commit();
                Task<int> CommitAsync(CancellationToken cancellationToken = default);
            }
            public class EfUnitOfWork : IUnitOfWork
            {
                public int Commit() => 0;
                public Task<int> CommitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
            }
            """);
        Write("App/CrudService.cs", """
            using App.Uow;
            namespace App.Services;
            public class CrudService
            {
                private readonly IUnitOfWork _unitOfWork;
                private readonly IOutsideUow _outside;
                public CrudService(IUnitOfWork unitOfWork) { _unitOfWork = unitOfWork; }
                private async Task CommitAsync() => await _unitOfWork.CommitAsync();
                private async Task FlushAsync() => await _outside.CommitAsync();
                private async Task SaveAsync() => await this._unitOfWork.CommitAsync();
            }
            """);
        var reader = await IndexAsync();

        // 接收者是 IUnitOfWork 字段 → 接口方法 0.9，加上唯一实现的派生边；绝不是本类的 CommitAsync
        Assert.Equal([("App.Uow.IUnitOfWork.CommitAsync", 5, 0.9, null), ("App.Uow.EfUnitOfWork.CommitAsync", 10, 0.9, "impl")],
            Calls(reader, "App.Services.CrudService.CommitAsync", 8));
        Assert.Equal([("App.Uow.IUnitOfWork.CommitAsync", 5, 0.9, null), ("App.Uow.EfUnitOfWork.CommitAsync", 10, 0.9, "impl")],
            Calls(reader, "App.Services.CrudService.SaveAsync", 10));
        // 字段类型在仓库外 → external:<类型>.方法
        Assert.Equal([("external:IOutsideUow.CommitAsync", 0, 0.5, null)], Calls(reader, "App.Services.CrudService.FlushAsync", 9));
    }

    [Fact]
    public async Task LocalVariable_Parameter_Property_And_InheritedField_Receivers()
    {
        Write("Lib.cs", """
            namespace App;
            public interface IFoo { void Bar(); }
            public class Foo { public void Bar() { } }
            public class Repo { public int Get(int id) => id; }
            public abstract class ServiceBase
            {
                protected readonly Repo _repo = new Repo();
            }
            """);
        Write("Svc.cs", """
            namespace App;
            public class Svc : ServiceBase
            {
                public Repo Store { get; } = new Repo();
                public void Bar() { }
                public void Get(int id) { }
                public void Run(IFoo foo)
                {
                    var x = new Foo();
                    x.Bar();
                    foo.Bar();
                    Store.Get(1);
                    _repo.Get(2);
                    x?.Bar();
                }
            }
            """);
        var reader = await IndexAsync();

        Assert.Equal([("App.Foo", 3, 0.9, null)], Calls(reader, "App.Svc.Run", 9));
        Assert.Equal([("App.Foo.Bar", 3, 0.9, null)], Calls(reader, "App.Svc.Run", 10));
        Assert.Equal([("App.IFoo.Bar", 2, 0.9, null)], Calls(reader, "App.Svc.Run", 11));          // 参数类型（接口没有实现类）
        Assert.Equal([("App.Repo.Get", 4, 0.9, null)], Calls(reader, "App.Svc.Run", 12));          // 属性类型
        Assert.Equal([("App.Repo.Get", 4, 0.9, null)], Calls(reader, "App.Svc.Run", 13));          // 父类（另一个文件）里的字段
        Assert.Equal([("App.Foo.Bar", 3, 0.9, null)], Calls(reader, "App.Svc.Run", 14));           // x?.Bar()
    }

    [Fact]
    public async Task ThisAndBase_Calls()
    {
        Write("A.cs", """
            namespace App;
            public class Base
            {
                public virtual void Save() { }
            }
            public class Child : Base
            {
                public override void Save()
                {
                    base.Save();
                    this.Helper();
                    Inherited();
                }
                private void Helper() { }
            }
            public class Base2 { }
            """);
        Write("B.cs", """
            namespace App;
            public partial class Child
            {
                private void Inherited() { }
            }
            """);
        var reader = await IndexAsync();

        Assert.Equal([("App.Base.Save", 4, 0.9, null)], Calls(reader, "App.Child.Save", 10));      // base.：跳过本类的同名 override
        Assert.Equal([("App.Child.Helper", 14, 0.9, null)], Calls(reader, "App.Child.Save", 11));
        Assert.Equal([("App.Child.Inherited", 4, 0.9, null)], Calls(reader, "App.Child.Save", 12)); // partial 的另一部分
    }

    [Fact]
    public async Task ExtensionMethods_GenericMethods_Nameof_AndDelegates()
    {
        Write("Ext.cs", """
            namespace App.Extensions;
            public static class MapExtensions
            {
                public static T MapTo<T>(this object source) => default!;
                public static IServiceCollection AddApp(this IServiceCollection services, int n) => services;
            }
            """);
        Write("Use.cs", """
            using App.Extensions;
            namespace App;
            public class Use
            {
                public T Make<T>() => default!;
                public int f() => 0;
                public void Run(IServiceCollection services, Order order)
                {
                    var dto = order.MapTo<OrderDto>();
                    services.AddApp(1);
                    Make<int>();
                    var name = nameof(Run);
                    Func<int> f = () => 1;
                    f();
                }
            }
            """);
        var reader = await IndexAsync();

        // this object 的扩展方法：类型对不上（Order 不在仓库）但在 using 范围里 → 0.7
        Assert.Equal([("App.Extensions.MapExtensions.MapTo", 4, 0.7, null)], Calls(reader, "App.Use.Run", 9));
        // this IServiceCollection：this 参数类型与接收者类型一致 → 0.9（实参个数按扩展方式 +1）
        Assert.Equal([("App.Extensions.MapExtensions.AddApp", 5, 0.9, null)], Calls(reader, "App.Use.Run", 10));
        Assert.Equal([("App.Use.Make", 5, 0.9, null)], Calls(reader, "App.Use.Run", 11));
        Assert.Empty(Calls(reader, "App.Use.Run", 12));  // nameof 不是调用
        Assert.Empty(Calls(reader, "App.Use.Run", 14));  // 调用局部委托：不连到同名方法 f
    }

    [Fact]
    public async Task ReceiverChains_VarFromCall_PropertyChain_Await_AndGenericConstraint()
    {
        Write("Lib.cs", """
            namespace App;
            public interface IProvider { int Sum(int x); }
            public interface IClause { void AppendSql(string s); }
            public interface IAccessor { IClause WhereClause { get; } }
            public interface IEntity { void Init(); }
            public class Repo { public Task<IProvider> LoadAsync() => null!; }
            """);
        Write("Exec.cs", """
            namespace App;
            public class Exec<TEntity> where TEntity : class, IEntity
            {
                private readonly Repo _repo = new Repo();
                protected virtual IProvider FindProvider(int q) => null!;
                public int Sum(int q)
                {
                    var provider = FindProvider(q);
                    return provider.Sum(q);
                }
                public void Where(object source, TEntity entity)
                {
                    if (source is IAccessor accessor)
                        accessor.WhereClause.AppendSql("x");
                    entity.Init();
                }
                public async Task Load()
                {
                    var p = await _repo.LoadAsync();
                    p.Sum(1);
                    (await _repo.LoadAsync()).Sum(2);
                }
            }
            """);
        var reader = await IndexAsync();

        // var provider = FindProvider(q) → 返回类型 IProvider；以前按名字匹配会落到本类的 Sum
        Assert.Equal([("App.IProvider.Sum", 2, 0.9, null)], Calls(reader, "App.Exec.Sum", 9));
        // 模式变量 accessor 的属性 WhereClause → IClause
        Assert.Equal([("App.IClause.AppendSql", 3, 0.9, null)], Calls(reader, "App.Exec.Where", 14));
        // where TEntity : IEntity
        Assert.Equal([("App.IEntity.Init", 5, 0.9, null)], Calls(reader, "App.Exec.Where", 15));
        // await 拆 Task<IProvider>
        Assert.Equal([("App.IProvider.Sum", 2, 0.9, null)], Calls(reader, "App.Exec.Load", 20));
        Assert.Equal([("App.IProvider.Sum", 2, 0.9, null), ("App.Repo.LoadAsync", 6, 0.9, null)], Calls(reader, "App.Exec.Load", 21));
    }

    [Fact]
    public async Task TargetTypedNew_QualifiedNew_MultiLineChainLine_Received_ConfigureAwait()
    {
        Write("Lib.cs", """
            namespace App.Models;
            public class Dto { }
            public class Options { }
            public interface IRepo { Task<Dto> GetAsync(); void Save(); }
            """);
        Write("Use.cs", """
            using App.Models;
            namespace App;
            public class Use
            {
                private readonly Options _options = new();
                private readonly IRepo _repo = null!;
                public Dto Make() => new();
                public void Run()
                {
                    List<Dto> list = new() { new() };
                    var other = new App.Models.Options();
                    _repo
                        .Save();
                    _repo.Received(1).Save();
                }
                public async Task Load()
                {
                    var dto = await _repo.GetAsync().ConfigureAwait(false);
                    var again = (await _repo.GetAsync().ConfigureAwait(false)).ToString();
                }
            }
            """);
        var reader = await IndexAsync();

        Assert.Equal([("App.Models.Options", 3, 0.9, null)], Calls(reader, "App.Use._options", 5));
        Assert.Equal([("App.Models.Dto", 2, 0.9, null)], Calls(reader, "App.Use.Make", 7));
        // List<Dto> x = new() { new() }：外层是仓库外的 List，元素是 Dto
        Assert.Equal([("App.Models.Dto", 2, 0.9, null), ("external:List", 0, 0.5, null)], Calls(reader, "App.Use.Run", 10));
        Assert.Equal([("App.Models.Options", 3, 0.9, null)], Calls(reader, "App.Use.Run", 11));
        // 跨行的链式调用记在接收者那一行（与 Roslyn 一致）
        Assert.Equal([("App.Models.IRepo.Save", 4, 0.9, null)], Calls(reader, "App.Use.Run", 12));
        Assert.Empty(Calls(reader, "App.Use.Run", 13));
        // NSubstitute：mock.Received(1).Save() 的 Save 仍在 IRepo 上
        Assert.Contains(("App.Models.IRepo.Save", 4, 0.9, (string?)null), Calls(reader, "App.Use.Run", 14));
        // ConfigureAwait 不挡 await 拆 Task<Dto>：ToString 落到 external:ToString 而不是名字匹配
        Assert.Contains(("App.Models.IRepo.GetAsync", 4, 0.9, (string?)null), Calls(reader, "App.Use.Load", 18));
        Assert.Contains(("external:ToString", 0, 0.5, (string?)null), Calls(reader, "App.Use.Load", 19));
    }

    [Fact]
    public async Task SelfCallWithSameArity_AndBaseOverload_AreBothCandidates()
    {
        Write("A.cs", """
            namespace App;
            public abstract class ProviderBase
            {
                public Task<string> GenerateUrlAsync(object param) => Task.FromResult("");
            }
            public class WeiboProvider : ProviderBase
            {
                public async Task<string> GenerateUrlAsync(WeiboRequest request) => await GenerateUrlAsync(request.ToParam());
            }
            """);
        var reader = await IndexAsync();
        Assert.Equal([("App.ProviderBase.GenerateUrlAsync", 4, 0.3, null), ("App.WeiboProvider.GenerateUrlAsync", 8, 0.3, null)],
            Calls(reader, "App.WeiboProvider.GenerateUrlAsync", 8).Where(c => c.To.EndsWith("GenerateUrlAsync")).ToList());
    }

    [Fact]
    public void ParameterInfo_CountsOptionalParamsAndExtension()
    {
        Assert.Equal(new HeuristicEdgeResolver.CSharpParameters(1, int.MaxValue, false, null),
            HeuristicEdgeResolver.CSharpParameterInfo("public static int Run(string[] a, int b = 1, params int[] c)", "Run"));
        Assert.Equal(new HeuristicEdgeResolver.CSharpParameters(1, 2, true, "IServiceCollection"),
            HeuristicEdgeResolver.CSharpParameterInfo("public static IServiceCollection AddX<T>(this IServiceCollection s, Dictionary<string, int> m = null) where T : new()", "AddX"));
        Assert.Equal(new HeuristicEdgeResolver.CSharpParameters(0, 0, false, null),
            HeuristicEdgeResolver.CSharpParameterInfo("public void Go()", "Go"));
        Assert.Equal(new HeuristicEdgeResolver.CSharpParameters(2, 2, false, null),
            HeuristicEdgeResolver.CSharpParameterInfo("public void Go([FromBody] Req r, string s)", "Go"));
        Assert.Equal(new HeuristicEdgeResolver.CSharpParameters(1, 2, false, null),
            HeuristicEdgeResolver.CSharpParameterInfo("public void Go([FromBody] Req r, string s = \"a,b\")", "Go"));
        // 参数表跨行被截断：个数不限
        Assert.Equal(HeuristicEdgeResolver.CSharpParameters.Unknown, HeuristicEdgeResolver.CSharpParameterInfo("public Svc(", "Svc"));
    }
}
