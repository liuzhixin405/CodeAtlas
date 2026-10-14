using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;

namespace CodeAtlas.Cli.Commands;

internal static class IndexCommand
{
    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine("用法：codeatlas index <repo> [--db <path>] [--full] [--roslyn auto|on|off] [--sln <path>]"); return 2; }
        var roslynMode = args.Value("--roslyn") ?? "auto";
        if (roslynMode is not ("auto" or "on" or "off")) { stderr.WriteLine($"--roslyn 只能是 auto / on / off，不是 {roslynMode}"); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine($"目录不存在：{repo}"); return 2; }

        WarnIfNotIgnored(repo, stderr);

        var adapters = CommonOptions.CreateAdapters();
        try
        {
            using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
            var options = new IndexOptions { Full = args.Has("--full") };
            var indexer = new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver(), options);

            var lastPhase = "";
            var progress = new Progress<IndexProgress>(p =>
            {
                if (p.Phase != lastPhase) { lastPhase = p.Phase; stderr.WriteLine($"[{p.Phase}]"); }
                if (p.Phase == "parse" && (p.Done % 500 == 0 || p.Done == p.Total)) stderr.WriteLine($"  {p.Done}/{p.Total}");
            });
            var report = indexer.IndexAsync(repo, progress).GetAwaiter().GetResult();

            stdout.WriteLine($"files: {report.FilesScanned}  parsed: {report.FilesParsed}  unchanged: {report.FilesUnchanged}  skipped: {report.FilesSkipped}  removed: {report.FilesRemoved}  failed: {report.FilesFailed}");
            stdout.WriteLine($"symbols: {report.TotalSymbols}  edges: {report.TotalEdges}  elapsed: {report.Elapsed.TotalSeconds:F1}s");
            foreach (var (path, error) in report.Errors) stderr.WriteLine($"  失败 {path}: {error}");
            var exit = report.FilesFailed > 0 ? 1 : 0;

            if (roslynMode != "off")
            {
                var roslynExit = RunRoslyn(db, repo, args.Value("--sln"), roslynMode == "on", stdout, stderr);
                if (roslynExit != 0) exit = roslynExit;
            }

            // 容器图事实：只读文件系统与源码文本，几秒钟；失败不影响索引结果
            try
            {
                var (_, arch) = CodeAtlas.Core.Architecture.ArchitectureScanner.ScanAndStore(repo, db);
                stdout.WriteLine($"arch: containers: {arch.Containers}  externals: {arch.Externals}  edges: {arch.Edges}  unmatched: {arch.OutboundUnmatched}  elapsed: {arch.Elapsed.TotalSeconds:F1}s");
            }
            catch (Exception ex) { stderr.WriteLine($"提示：容器图扫描失败：{ex.Message}"); }

            if (!args.Has("--no-git"))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var git = CodeAtlas.Core.Architecture.GitHistory.Run(repo);
                    CodeAtlas.Core.Storage.GitStorage.Replace(db, git);
                    stdout.WriteLine(git.Error is null
                        ? $"git: commits: {git.Commits}  files: {git.Files.Count}  coupled-pairs: {git.Coupling.Count}  skipped-large: {git.CommitsSkippedForCoupling}  elapsed: {sw.Elapsed.TotalSeconds:F1}s"
                        : $"git: 不可用（{git.Error}）");
                }
                catch (Exception ex) { stderr.WriteLine($"提示：git 统计失败：{ex.Message}"); }
            }

            // 入口识别与路由匹配：依赖上面写好的符号、边和容器；失败不影响索引结果
            try
            {
                var post = CodeAtlas.Server.ArchitecturePostProcess.Run(repo, db);
                stdout.WriteLine(FormatEntries(post));
                stdout.WriteLine(post.FormatRules());
            }
            catch (Exception ex) { stderr.WriteLine($"提示：入口识别失败：{ex.Message}"); }
            return exit;
        }
        finally
        {
            foreach (var a in adapters.OfType<IDisposable>()) a.Dispose();
        }
    }

    internal static string FormatEntries(CodeAtlas.Server.PostProcessReport post) =>
        $"entries: {post.Entries}  ({string.Join("  ", post.EntriesByKind.Select(kv => $"{kv.Key}: {kv.Value}"))})  route-matches: {post.Matches}  elapsed: {post.Elapsed.TotalSeconds:F1}s";

    /// <summary>required 为 false（auto）时缺 worker / 解决方案只提示不报错。</summary>
    private static int RunRoslyn(IndexDatabase db, string repo, string? slnArg, bool required, TextWriter stdout, TextWriter stderr)
    {
        var worker = RoslynRunner.FindWorker();
        if (worker is null)
        {
            stderr.WriteLine($"{(required ? "错误" : "提示")}：没有找到 {RoslynRunner.WorkerName}（应与 codeatlas 同目录，或用环境变量 {RoslynRunner.WorkerPathEnv} 指定），跳过 Roslyn 精确语义。");
            return required ? 1 : 0;
        }
        var sln = slnArg is not null ? Path.GetFullPath(slnArg) : RoslynRunner.DiscoverSolution(repo);
        if (sln is null || !File.Exists(sln))
        {
            stderr.WriteLine($"{(required ? "错误" : "提示")}：{(sln is null ? "仓库里没有找到 .sln/.slnx" : $"解决方案不存在：{sln}")}，跳过 Roslyn 精确语义。");
            return required ? 1 : 0;
        }

        stderr.WriteLine($"[roslyn] {sln}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = RoslynRunner.RunAndApplyAsync(db, worker, sln, repo,
                p => { if (p.Done % 200 == 0 || p.Done == p.Total) stderr.WriteLine($"  {p.Done}/{p.Total}"); },
                line => stderr.WriteLine("  " + line), CancellationToken.None).GetAwaiter().GetResult();
            stdout.WriteLine($"roslyn: files: {r.Files}  skipped: {r.FilesSkipped}  merged: {r.SymbolsMerged}  added: {r.SymbolsAdded}  edges: {r.Edges}  di: {r.DiRegistrations}  errors: {r.Errors}  elapsed: {sw.Elapsed.TotalSeconds:F1}s");
            return 0;
        }
        catch (RoslynWorkerException ex)
        {
            stderr.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static void WarnIfNotIgnored(string repo, TextWriter stderr)
    {
        var gitignore = Path.Combine(repo, ".gitignore");
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(gitignore)) return;
        var lines = File.Exists(gitignore) ? File.ReadAllLines(gitignore) : [];
        if (lines.Any(l => l.Trim() is ".codeatlas" or ".codeatlas/" or "/.codeatlas" or "/.codeatlas/")) return;
        stderr.WriteLine("提示：建议把 .codeatlas/ 加入该仓库的 .gitignore（索引库不应提交）。");
    }
}
