using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
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
    public void FindWorker_PrefersRoslynSubdirectory_OverHostDirectory()
    {
        // 自带运行时发布：worker 在 roslyn/ 子目录（与宿主同目录会误用自带运行时的 hostfxr）
        var host = Path.Combine(Path.GetTempPath(), "codeatlas-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(host, "roslyn"));
        try
        {
            File.WriteAllText(Path.Combine(host, "codeatlas-roslyn.dll"), "");
            File.WriteAllText(Path.Combine(host, "roslyn", "codeatlas-roslyn.dll"), "");
            Assert.Equal(Path.Combine(host, "roslyn", "codeatlas-roslyn.dll"), RoslynRunner.FindWorker(host));
        }
        finally { Directory.Delete(host, true); }
    }

    [Theory]
    [InlineData(0, "", null)]
    [InlineData(3, "codeatlas-roslyn: 没有找到 .NET SDK", "没有找到 .NET SDK")]
    [InlineData(unchecked((int)0x80008096), "", "没有找到 .NET 10 运行时")]                                  // Windows 启动器：找不到框架
    [InlineData(131, "You must install .NET to run this application.\n\nApp: /opt/codeatlas/roslyn/codeatlas-roslyn\nArchitecture: x64", "没有找到 .NET 10 运行时")] // Ubuntu 24.04 实测原文
    [InlineData(150, "You must install or update .NET to run this application.", "没有找到 .NET 10 运行时")]   // 版本不够
    [InlineData(1, "boom\n  second line", "探测失败（退出码 1）：boom second line")]
    public void DescribeProbeFailure_TranslatesHostAndSdkErrors(int exitCode, string stderr, string? expected)
    {
        var reason = RoslynRunner.DescribeProbeFailure(exitCode, stderr);
        if (expected is null) { Assert.Null(reason); return; }
        Assert.Contains(expected, reason);
        Assert.EndsWith(RoslynRunner.FallbackNote, reason);
    }

    [Fact]
    public async Task Probe_RealWorker_SucceedsOnThisMachine()
    {
        // 本机装着 .NET 10 SDK：探测应成功（测得到 --probe 入口本身能跑通）
        Assert.Null(await RoslynRunner.ProbeAsync(RoslynRunner.FindWorker()!));
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

    [Fact]
    public async Task RunAndApply_ExternalRoot_WorkerPathsCarryTheRelativePrefix()
    {
        // 主根是 fixture 旁边的空目录，RoslynSample 作为外部根：tree-sitter 入库路径是 ../RoslynSample/…，Roslyn 写回必须对上同样的路径
        var main = Path.Combine(Path.GetDirectoryName(fx.Root)!, ".tmp-main-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(main);
        try
        {
            var roots = ProjectRoots.Create(main, [fx.Root]);
            using var db = IndexDatabase.OpenInMemory();
            List<ILanguageAdapter> adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];
            try { await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver()).IndexAsync(roots); }
            finally { foreach (var a in adapters.OfType<IDisposable>()) a.Dispose(); }

            var solutions = RoslynRunner.DiscoverSolutions(roots);
            Assert.Equal([Path.Combine(fx.Root, "RoslynSample.sln")], solutions);
            var report = await RoslynRunner.RunAndApplyAsync(db, RoslynRunner.FindWorker()!, solutions, roots, null, null, CancellationToken.None);

            Assert.Equal((0, 1), (report.FilesSkipped, report.Solutions));
            Assert.Empty(report.FailedSolutions!);
            var reader = new IndexReader(db);
            var byId = reader.FindByName("ById").Single();
            Assert.Equal(("Sample.App.Controller.ById(int)", IndexSource.Roslyn), (byId.QualifiedName, byId.Source));
            Assert.Equal("../RoslynSample/Sample.App/Controller.cs", reader.GetFileById(byId.FileId)!.Path);
            Assert.NotNull(reader.GetFile("../RoslynSample/Sample.App/Controller.cs")!.RoslynAt);
        }
        finally { Directory.Delete(main, recursive: true); }
    }
}
