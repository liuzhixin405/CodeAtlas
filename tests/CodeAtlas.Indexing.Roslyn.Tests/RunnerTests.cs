using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.Roslyn.Tests;

public class RunnerTests(AnalyzerFixture fx) : IClassFixture<AnalyzerFixture>
{
    [Fact]
    public void FindWorker_LocatesWorkerNextToHostAssembly_ExePreferredOverDll()
    {
        var worker = RoslynRunner.FindWorker();
        Assert.NotNull(worker);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "codeatlas-roslyn.exe"), worker); // 两者都在时取 exe

        var onlyDll = Path.Combine(Path.GetTempPath(), "codeatlas-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(onlyDll);
        try
        {
            File.WriteAllText(Path.Combine(onlyDll, "codeatlas-roslyn.dll"), "");
            Assert.EndsWith("codeatlas-roslyn.dll", RoslynRunner.FindWorker(onlyDll), StringComparison.Ordinal);
            Assert.Null(RoslynRunner.FindWorker(Path.GetTempPath()));
        }
        finally { Directory.Delete(onlyDll, true); }
    }

    [Fact]
    public void DiscoverSolution_PicksSlnAtRoot_AndNullWhenNone()
    {
        Assert.Equal(Path.Combine(fx.Root, "RoslynSample.sln"), RoslynRunner.DiscoverSolution(fx.Root));
        var empty = Path.Combine(Path.GetTempPath(), "codeatlas-nosln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try { Assert.Null(RoslynRunner.DiscoverSolution(empty)); }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public async Task RunAndApply_OverRealProcess_UpgradesTreeSitterIndex()
    {
        using var db = IndexDatabase.OpenInMemory();
        List<ILanguageAdapter> adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];
        try { await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver()).IndexAsync(fx.Root); }
        finally { foreach (var a in adapters.OfType<IDisposable>()) a.Dispose(); }

        var reader = new IndexReader(db);
        var byIdBefore = reader.FindByName("ById").Single();
        Assert.Equal(IndexSource.TreeSitter, byIdBefore.Source);

        var progress = new List<ProgressRec>();
        var log = new List<string>();
        var report = await RoslynRunner.RunAndApplyAsync(db, RoslynRunner.FindWorker()!, Path.Combine(fx.Root, "RoslynSample.sln"), fx.Root,
            progress.Add, log.Add, CancellationToken.None);

        Assert.True(report.Files >= 6, $"files={report.Files}");
        Assert.Equal(0, report.FilesSkipped); // obj/ 下的生成代码不该被分析器输出
        Assert.True(report.Errors >= 1, "Broken 项目应记为错误"); // Broken.csproj 的 Sdk 不存在
        Assert.NotEmpty(progress);
        Assert.Equal(progress[^1].Total, progress[^1].Done);

        var byId = reader.GetSymbol(byIdBefore.Id)!;
        Assert.Equal(IndexSource.Roslyn, byId.Source);
        Assert.Equal("Sample.App.Controller.ById(int)", byId.QualifiedName);
        var call = reader.GetNeighbors(byId.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls })
            .Single(n => n.Other?.QualifiedName == "Sample.Core.IRepo.Find(int)");
        Assert.Equal(1.0, call.Edge.Confidence);
        Assert.Equal(IndexSource.Roslyn, call.Edge.Source);

        var csFiles = reader.GetAllFiles().Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal) && !f.Path.Contains("/obj/")).ToList();
        Assert.True(csFiles.Count >= 7, $"cs files={csFiles.Count}");
        // 加载失败的 Broken 项目里的文件保持 tree-sitter 结果（roslyn_at 为空），其余都被覆盖
        Assert.Equal(["Broken/Nothing.cs"], csFiles.Where(f => f.RoslynAt is null).Select(f => f.Path));
    }
}
