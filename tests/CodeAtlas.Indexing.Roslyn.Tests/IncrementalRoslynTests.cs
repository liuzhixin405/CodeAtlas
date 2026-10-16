using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Indexing.Roslyn.Tests;

/// <summary>
/// 增量 Roslyn 的夹具：%TEMP% 下拷一份 RoslynSample 的 Sample.Core / Sample.App（不含 Broken），主根一个解决方案；
/// 外部根 Second 另一个解决方案；主根下 Loose/Orphan.cs 不在任何项目里，web/app.ts 是非 C# 文件。
/// 建好后先跑一次全量（tree-sitter + Roslyn），各场景从这份库的拷贝开始。
/// </summary>
public sealed class IncrementalFixture : IAsyncLifetime
{
    public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "codeatlas-roslyn-incr-" + Guid.NewGuid().ToString("N"));
    public string Main => Path.Combine(Workspace, "main");
    public string Second => Path.Combine(Workspace, "second");
    public string BaselineDb => Path.Combine(Workspace, "baseline.db");
    public ProjectRoots Roots => ProjectRoots.Create(Main, [Second]);
    public RoslynApplyReport BaselineReport { get; private set; } = null!;

    /// <summary>夹具里全部文件的原始内容（相对 Workspace），场景结束后还原。</summary>
    private readonly Dictionary<string, string> _originals = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        var source = new AnalyzerFixture().Root;
        foreach (var project in new[] { "Sample.Core", "Sample.App" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(source, project)).Where(f => f.EndsWith(".cs") || f.EndsWith(".csproj")))
                Write(Path.Combine("main", project, Path.GetFileName(file)), File.ReadAllText(file));
        Write("main/Main.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Sample.Core", "Sample.Core\Sample.Core.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Sample.App", "Sample.App\Sample.App.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            EndGlobal
            """);
        Write("main/Loose/Orphan.cs", "namespace Loose;\n\npublic class Orphan\n{\n    public int Run() => 1;\n}\n");
        Write("main/web/app.ts", "export function hello(): number {\n  return 1;\n}\n");
        Write("second/Second.Lib/Second.Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        Write("second/Second.Lib/Lib.cs", "namespace Second.Lib;\n\npublic class Lib\n{\n    public int A() => B();\n    public int B() => 2;\n}\n");
        Write("second/Second.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Second.Lib", "Second.Lib\Second.Lib.csproj", "{44444444-4444-4444-4444-444444444444}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            EndGlobal
            """);
        await Restore(Path.Combine(Main, "Main.sln"));
        await Restore(Path.Combine(Second, "Second.sln"));
        foreach (var file in Directory.EnumerateFiles(Workspace, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Workspace, file).Replace('\\', '/');
            if (!rel.Contains("/obj/") && !rel.Contains("/bin/") && !_originals.ContainsKey(rel)) _originals[rel] = File.ReadAllText(file);
        }

        using var db = IndexDatabase.Open(BaselineDb);
        BaselineReport = await IncrementalRoslynTests.FullIndexAsync(db, Roots);
        db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public void Write(string relative, string content)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _originals.TryAdd(relative, content);
    }

    public void WriteNew(string relative, string content)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(Path.Combine(Workspace, relative));

    /// <summary>删掉场景新加的源码文件，改过的写回原样。</summary>
    public void Reset()
    {
        foreach (var file in Directory.EnumerateFiles(Workspace, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Workspace, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.EndsWith(".db") || rel.Contains(".db-")) continue;
            if (!_originals.TryGetValue(rel, out var original)) File.Delete(file);
            else if (File.ReadAllText(file) != original) File.WriteAllText(file, original);
        }
    }

    private static async Task Restore(string sln)
    {
        var restore = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{sln}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        await Task.WhenAll(restore.StandardOutput.ReadToEndAsync(), restore.StandardError.ReadToEndAsync());
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// 增量 Roslyn 的等价性：全量建库 → 改一个文件 → 增量（tree-sitter 增量 + 只走待更新文件的 Roslyn），
/// 结果必须与对改后内容全新全量建库一致（边、符号、DI 按限定名比较，不比 id 与时间戳）。
/// </summary>
public class IncrementalRoslynTests(IncrementalFixture fx) : IClassFixture<IncrementalFixture>
{
    internal static async Task<RoslynApplyReport> FullIndexAsync(IndexDatabase db, ProjectRoots roots, List<string>? log = null)
    {
        await TreeSitterAsync(db, roots, full: true);
        return await RoslynRunner.RunAndApplyAsync(db, RoslynRunner.FindWorker()!, RoslynRunner.DiscoverSolutions(roots), roots, null, log is null ? null : log.Add, CancellationToken.None);
    }

    private static async Task<(RoslynApplyReport? Report, IReadOnlyList<string> Pending)> IncrementalIndexAsync(IndexDatabase db, ProjectRoots roots, List<string>? log = null)
    {
        await TreeSitterAsync(db, roots, full: false);
        var pending = new IndexReader(db).GetRoslynPendingFiles();
        if (pending.Count == 0) return (null, pending);
        var report = await RoslynRunner.RunAndApplyAsync(db, RoslynRunner.FindWorker()!, RoslynRunner.DiscoverSolutions(roots), roots, null, log is null ? null : log.Add,
            CancellationToken.None, pendingFiles: pending);
        return (report, pending);
    }

    private static async Task TreeSitterAsync(IndexDatabase db, ProjectRoots roots, bool full)
    {
        List<ILanguageAdapter> adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];
        try { await new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver(), new IndexOptions { Full = full }).IndexAsync(roots); }
        finally { foreach (var a in adapters.OfType<IDisposable>()) a.Dispose(); }
    }

    /// <summary>从基线库的拷贝开始一个场景；返回库路径。</summary>
    private string CopyBaseline()
    {
        var path = Path.Combine(fx.Workspace, $"scenario-{Guid.NewGuid():N}.db");
        File.Copy(fx.BaselineDb, path);
        return path;
    }

    /// <summary>改 → 增量 → 与全新全量比较；返回增量报告。</summary>
    private async Task<RoslynApplyReport> AssertIncrementalEqualsFull(Action mutate, Action<IReadOnlyList<string>>? checkPending = null)
    {
        var incrementalPath = CopyBaseline();
        var fullPath = Path.Combine(fx.Workspace, $"full-{Guid.NewGuid():N}.db");
        try
        {
            mutate();
            RoslynApplyReport? report;
            IReadOnlyList<string> pending;
            Snapshot incremental, full;
            using (var db = IndexDatabase.Open(incrementalPath))
            {
                (report, pending) = await IncrementalIndexAsync(db, fx.Roots);
                incremental = Snapshot.Take(db);
            }
            using (var db = IndexDatabase.Open(fullPath))
            {
                await FullIndexAsync(db, fx.Roots);
                full = Snapshot.Take(db);
            }
            checkPending?.Invoke(pending);
            Assert.NotNull(report);
            Snapshot.AssertEqual(full, incremental);
            return report!;
        }
        finally
        {
            fx.Reset();
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { incrementalPath, fullPath }) foreach (var f in new[] { p, p + "-wal", p + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Baseline_FullRun_MarksOrphanAsMiss_AndNothingIsPending()
    {
        using var db = IndexDatabase.Open(CopyBaseline());
        var reader = new IndexReader(db);
        Assert.Empty(reader.GetRoslynPendingFiles());
        Assert.Equal(0, reader.CountRoslynPendingFiles());
        var orphan = db.Query<string>("SELECT COALESCE(roslyn_miss, '') = hash FROM files WHERE path = 'Loose/Orphan.cs'").Single();
        Assert.Equal("1", orphan);
        Assert.Equal("Main.sln", db.Query<string>("SELECT roslyn_sln FROM files WHERE path = 'Sample.Core/Repo.cs'").Single());
        Assert.Equal("../second/Second.sln", db.Query<string>("SELECT roslyn_sln FROM files WHERE path = '../second/Second.Lib/Lib.cs'").Single());
    }

    [Fact]
    public async Task MethodBodyChange_EqualsFullIndex_AndOnlyMainSolutionRuns()
    {
        var report = await AssertIncrementalEqualsFull(
            () => fx.WriteNew("main/Sample.Core/Repo.cs", fx.Read("main/Sample.Core/Repo.cs").Replace("public Item Find(int id) => new(id);", "public Item Find(int id) => new(All().Count + id);")),
            pending => Assert.Contains("Sample.Core/Repo.cs", pending));
        Assert.Equal(1, report.Solutions);
        Assert.DoesNotContain(report.Timings!, t => t.Phase.StartsWith("roslyn[Second]", StringComparison.Ordinal));
        Assert.Contains(report.Timings!, t => t.Phase.StartsWith("roslyn[Main]", StringComparison.Ordinal));
        // 只走待更新的文件（改的文件 + 依赖它的），不是整个解决方案
        Assert.True(report.Files < fx.BaselineReport.Files, $"incremental files={report.Files} full={fx.BaselineReport.Files}");
    }

    [Fact]
    public async Task MethodRename_EqualsFullIndex()
    {
        await AssertIncrementalEqualsFull(
            () => fx.WriteNew("main/Sample.Core/Repo.cs", fx.Read("main/Sample.Core/Repo.cs").Replace("public Item Find(string name)", "public Item FindByName(string name)")));
    }

    [Fact]
    public async Task NewHandler_EqualsFullIndex_SendersGetDispatchEdges()
    {
        var report = await AssertIncrementalEqualsFull(() => fx.WriteNew("main/Sample.Core/ArchiveHandler.cs", """
            namespace Sample.Core;

            public sealed class ArchiveItemCommandHandler : ICommandHandler<ArchiveItemCommand, bool>
            {
                public Task<bool> HandleAsync(ArchiveItemCommand command, CancellationToken ct) => Task.FromResult(true);
            }
            """));
        Assert.Equal(1, report.Solutions);
    }

    [Fact]
    public async Task NewFile_EqualsFullIndex_AndGetsTheRepositorySolution()
    {
        await AssertIncrementalEqualsFull(() => fx.WriteNew("main/Sample.App/Extra.cs", """
            using Sample.Core;

            namespace Sample.App;

            public class Extra
            {
                public Item Go(IRepo repo, Controller c) => c.ById(repo.Find(3).Id);
            }
            """), pending => Assert.Contains("Sample.App/Extra.cs", pending));
    }

    [Fact]
    public async Task SecondSolutionChange_OnlySecondSolutionRuns()
    {
        var log = new List<string>();
        var path = CopyBaseline();
        try
        {
            fx.WriteNew("second/Second.Lib/Lib.cs", fx.Read("second/Second.Lib/Lib.cs").Replace("=> 2;", "=> 3;"));
            using var db = IndexDatabase.Open(path);
            var (report, pending) = await IncrementalIndexAsync(db, fx.Roots, log);
            Assert.Equal(["../second/Second.Lib/Lib.cs"], pending);
            Assert.NotNull(report);
            Assert.Equal(1, report!.Solutions);
            Assert.DoesNotContain(report.Timings!, t => t.Phase.StartsWith("roslyn[Main]", StringComparison.Ordinal));
            Assert.Equal(1, report.Files);
            Assert.Empty(new IndexReader(db).GetRoslynPendingFiles());
        }
        finally { fx.Reset(); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task OrphanFile_ChangedOnce_IsTriedOnce_ThenNotPending()
    {
        var path = CopyBaseline();
        try
        {
            fx.WriteNew("main/Loose/Orphan.cs", fx.Read("main/Loose/Orphan.cs").Replace("=> 1;", "=> 2;"));
            using var db = IndexDatabase.Open(path);
            var (report, pending) = await IncrementalIndexAsync(db, fx.Roots);
            Assert.Equal(["Loose/Orphan.cs"], pending);
            Assert.NotNull(report);
            var reader = new IndexReader(db);
            Assert.Empty(reader.GetRoslynPendingFiles());
            // 再跑一次增量：什么都没变，不跑 Roslyn
            var (again, pendingAgain) = await IncrementalIndexAsync(db, fx.Roots);
            Assert.Empty(pendingAgain);
            Assert.Null(again);
        }
        finally { fx.Reset(); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task TypeScriptChange_LeavesNothingPendingForRoslyn()
    {
        var path = CopyBaseline();
        try
        {
            fx.WriteNew("main/web/app.ts", fx.Read("main/web/app.ts").Replace("return 1;", "return 2;"));
            using var db = IndexDatabase.Open(path);
            var (report, pending) = await IncrementalIndexAsync(db, fx.Roots);
            Assert.Empty(pending);
            Assert.Null(report);
        }
        finally { fx.Reset(); SqliteConnection.ClearAllPools(); }
    }

    /// <summary>
    /// 库内容按限定名的快照：边、符号、DI、文件的 Roslyn 状态。
    /// 不比 imports 边：它只来自 tree-sitter（Roslyn 不写也不删），C# 的 using 对每个声明该命名空间的文件各连一条，
    /// 新增文件后没改的文件的 using 不会重新解析——那是 tree-sitter 增量的事，不在这里验。
    /// </summary>
    private sealed record Snapshot(List<string> Edges, List<string> Symbols, List<string> Di, List<string> Files)
    {
        public static Snapshot Take(IndexDatabase db) => new(
            Rows(db, """
                SELECT ff.path || ' | ' || fs.qualified_name || ' -' || e.kind || '-> ' || COALESCE(ts.qualified_name, '?' || e.to_name)
                       || ' | via=' || COALESCE(e.via, '') || ' line=' || e.line || ' src=' || e.source || ' conf=' || printf('%.3f', e.confidence)
                       || ' ev=' || COALESCE(e.evidence_line, '') || ' pos=' || COALESCE(e.pos, '') || ' ctx=' || COALESCE(e.ctx, '')
                FROM edges e JOIN symbols fs ON fs.id = e.from_id LEFT JOIN symbols ts ON ts.id = e.to_id JOIN files ff ON ff.id = e.file_id
                WHERE e.kind <> 'imports'
                """),
            Rows(db, """
                SELECT f.path || ' | ' || s.kind || ' ' || s.qualified_name || ' @' || s.start_line || ':' || s.start_col || '-' || s.end_line || ':' || s.end_col
                       || ' src=' || s.source || ' parent=' || COALESCE(p.qualified_name, '') || ' vis=' || COALESCE(s.visibility, '') || ' sig=' || COALESCE(s.signature, '')
                FROM symbols s JOIN files f ON f.id = s.file_id LEFT JOIN symbols p ON p.id = s.parent_id
                """),
            Rows(db, """
                SELECT f.path || ':' || d.line || ' ' || COALESCE(i.qualified_name, '') || ' => ' || COALESCE(m.qualified_name, '') || ' ' || COALESCE(d.lifetime, '') || ' ' || d.conditional
                FROM di_registrations d JOIN files f ON f.id = d.file_id LEFT JOIN symbols i ON i.id = d.interface_id LEFT JOIN symbols m ON m.id = d.impl_id
                """),
            Rows(db, "SELECT path || ' roslyn=' || (roslyn_at IS NOT NULL) || ' sln=' || COALESCE(roslyn_sln, '') || ' miss=' || (COALESCE(roslyn_miss, '') = hash) FROM files"));

        private static List<string> Rows(IndexDatabase db, string sql) => db.Query<string>(sql).OrderBy(x => x, StringComparer.Ordinal).ToList();

        public static void AssertEqual(Snapshot expected, Snapshot actual)
        {
            Same("edges", expected.Edges, actual.Edges);
            Same("symbols", expected.Symbols, actual.Symbols);
            Same("di", expected.Di, actual.Di);
            Same("files", expected.Files, actual.Files);
        }

        private static void Same(string what, List<string> expected, List<string> actual)
        {
            var missing = Diff(expected, actual);
            var extra = Diff(actual, expected);
            if (missing.Count == 0 && extra.Count == 0) return;
            Assert.Fail($"{what} 不一致：增量缺 {missing.Count} 条、多 {extra.Count} 条\n缺：\n  {string.Join("\n  ", missing.Take(30))}\n多：\n  {string.Join("\n  ", extra.Take(30))}");
        }

        /// <summary>多重集合差：a 里比 b 多出来的行。</summary>
        private static List<string> Diff(List<string> a, List<string> b)
        {
            var counts = b.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var x in a)
            {
                if (counts.TryGetValue(x, out var n) && n > 0) counts[x] = n - 1;
                else result.Add(x);
            }
            return result;
        }
    }
}
