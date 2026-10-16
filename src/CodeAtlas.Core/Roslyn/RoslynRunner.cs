using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeAtlas.Core.Diagnostics;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Roslyn;

/// <summary>
/// 在子进程里跑 codeatlas-roslyn worker，把它 stdout 上的 JSON Lines 反序列化成记录。
/// 单独进程的原因：Roslyn + MSBuild 占内存大、可能崩，不能拖垮服务；MSBuildLocator 一个进程只能注册一次。
/// </summary>
public static class RoslynRunner
{
    public const string WorkerName = "codeatlas-roslyn";
    public const string WorkerPathEnv = "CODEATLAS_ROSLYN_WORKER";

    /// <summary>worker 连续多久没有任何输出就视为挂死并杀掉。打开大解决方案并编译可能要几分钟，所以给得宽。</summary>
    public static TimeSpan DefaultStallTimeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>worker 的子目录：codeatlas 自带运行时发布，worker 依赖本机 .NET（它要用 SDK 里的 MSBuild），两者不能放同一目录——
    /// 同目录里有自带运行时的 hostfxr，worker 的启动器会误用它。</summary>
    public const string WorkerSubdirectory = "roslyn";

    /// <summary>worker --probe 的退出码：运行时在，但没有 .NET SDK。</summary>
    public const int ProbeNoSdkExitCode = 3;

    /// <summary>worker 的退出码：仓库 global.json 要求的 SDK 本机没有兼容版本。</summary>
    public const int GlobalJsonSdkMissingExitCode = 4;

    /// <summary>缺运行时 / SDK 时附在原因后面的说明。</summary>
    public const string FallbackNote = "C# 改用 tree-sitter 解析（精度略低），其他语言不受影响。";

    /// <summary>
    /// 找 worker：环境变量 CODEATLAS_ROSLYN_WORKER → 宿主程序目录下 roslyn/ 子目录 → 宿主程序同目录（旧布局、开发环境）；
    /// 每处依次找 codeatlas-roslyn.exe / codeatlas-roslyn / codeatlas-roslyn.dll。
    /// 返回 .dll 时由 RunAsync 用 dotnet 启动（开发/测试环境）。找不到返回 null。
    /// </summary>
    public static string? FindWorker(string? baseDirectory = null)
    {
        var env = Environment.GetEnvironmentVariable(WorkerPathEnv);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, WorkerSubdirectory), baseDir })
            foreach (var candidate in new[] { WorkerName + ".exe", WorkerName, WorkerName + ".dll" })
            {
                var path = Path.Combine(dir, candidate);
                if (File.Exists(path) && (candidate != WorkerName || !OperatingSystem.IsWindows())) return path;
            }
        return null;
    }

    private static readonly Dictionary<string, string?> ProbeCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 跑 Roslyn 之前探测 worker 能不能用：返回 null 表示能用，否则是给用户看的原因（缺 .NET 10 运行时 / 缺 SDK / 启动失败）。
    /// 同一进程里同一个 worker 只探测一次。缺的只是 C# 精确语义，tree-sitter 的结果照常可用，原因里写明退回了什么。
    /// </summary>
    public static async Task<string?> ProbeAsync(string workerPath, CancellationToken ct = default)
    {
        lock (ProbeCache) if (ProbeCache.TryGetValue(workerPath, out var cached)) return cached;
        var reason = await ProbeCoreAsync(workerPath, ct);
        lock (ProbeCache) ProbeCache[workerPath] = reason;
        return reason;
    }

    private static async Task<string?> ProbeCoreAsync(string workerPath, CancellationToken ct)
    {
        EnsureExecutable(workerPath);
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { psi.FileName = "dotnet"; psi.ArgumentList.Add(workerPath); }
        else psi.FileName = workerPath;
        psi.ArgumentList.Add("--probe");
        Process? process;
        try { process = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return psi.FileName == "dotnet"
                ? $"没有找到 dotnet 命令，C# 精确语义（Roslyn）需要 .NET 10。{FallbackNote}"
                : $"无法启动 {WorkerName}：{ex.Message}。{FallbackNote}";
        }
        if (process is null) return $"无法启动 {WorkerName}。{FallbackNote}";
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { TryKill(process); return $"{WorkerName} 探测超时。{FallbackNote}"; }
            catch (OperationCanceledException) { TryKill(process); throw; }
            await stdout;
            return DescribeProbeFailure(process.ExitCode, await stderr);
        }
    }

    /// <summary>把 worker --probe 的退出码与 stderr 翻译成给用户看的原因；成功返回 null。</summary>
    public static string? DescribeProbeFailure(int exitCode, string stderr)
    {
        if (exitCode == 0) return null;
        if (exitCode == ProbeNoSdkExitCode) return $"没有找到 .NET SDK，C# 精确语义（Roslyn）要用它读取项目文件。{FallbackNote}";
        // .NET 启动器找不到运行时：Windows 上退出码是 0x8000808x / 0x8000809x，Linux / macOS 上只剩低 8 位（实测 Ubuntu 24.04 是 131）；
        // stderr 是 "You must install .NET to run this application."（没装）或 "You must install or update .NET …"（版本不够），
        // 后面跟 "Learn more: https://aka.ms/dotnet/app-launch-failed"
        var hostError = (uint)exitCode is >= 0x80008080 and <= 0x800080FF;
        if (hostError || stderr.Contains("You must install", StringComparison.OrdinalIgnoreCase) || stderr.Contains("app-launch-failed", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("framework 'Microsoft.NETCore.App'", StringComparison.OrdinalIgnoreCase))
            return $"没有找到 .NET 10 运行时，C# 精确语义（Roslyn）需要它（装 .NET 10 SDK 即可）。{FallbackNote}";
        var tail = string.Join(" ", stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (tail.Length > 300) tail = tail[^300..];
        return $"{WorkerName} 探测失败（退出码 {exitCode}）{(tail.Length > 0 ? "：" + tail : "")}。{FallbackNote}";
    }

    /// <summary>Linux / macOS：VSIX 解压后不一定保留可执行位，启动前补上。</summary>
    internal static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserExecute) == 0) File.SetUnixFileMode(path, mode | exec);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 每个要单独处理的仓库（主根、外部根、被纳入的嵌套仓库，见 ProjectRoots.Repositories）各找一个解决方案，按仓库顺序、去重。
    /// 一个仓库里的查找不进嵌套仓库与其他根（它们各找各的）。
    /// </summary>
    public static IReadOnlyList<string> DiscoverSolutions(ProjectRoots roots) =>
        DiscoverSolutionsByRepository(roots).Select(x => x.Solution).Distinct(ProjectRoots.PathComparer).ToList();

    /// <summary>每个仓库（见 ProjectRoots.Repositories）与它的解决方案，按仓库顺序；没有解决方案的仓库不出现。</summary>
    public static IReadOnlyList<(RepoDir Repo, string Solution)> DiscoverSolutionsByRepository(ProjectRoots roots)
    {
        var result = new List<(RepoDir, string)>();
        foreach (var repo in roots.Repositories())
            if (DiscoverSolution(repo.FullPath, dir => roots.IsRoot(dir.FullName)) is { } sln) result.Add((repo, sln));
        return result;
    }

    /// <summary>
    /// 在仓库里找解决方案：根目录优先，其次向下两层；多个时取文件最大的（通常是包含项目最多的）。
    /// .slnx 与 .sln 同等对待。带自己 .git 的子目录（独立克隆、子模块、worktree）不进：它们是别的仓库或副本。找不到返回 null。
    /// </summary>
    public static string? DiscoverSolution(string repoRoot) => DiscoverSolution(repoRoot, null);

    private static readonly EnumerationOptions AnyCase = new() { MatchCasing = MatchCasing.CaseInsensitive };

    private static string? DiscoverSolution(string repoRoot, Func<DirectoryInfo, bool>? skipDir)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".git", "dist", "build", ".vs", ".idea", ".codeatlas" };
        var candidates = new List<FileInfo>();
        void Scan(DirectoryInfo dir, int depth)
        {
            try
            {
                // Linux 上文件名区分大小写：App.SLN 在 Windows 上能找到，这里也要能找到
                candidates.AddRange(dir.EnumerateFiles("*.sln", AnyCase));
                candidates.AddRange(dir.EnumerateFiles("*.slnx", AnyCase));
                if (depth == 0) return;
                foreach (var sub in dir.EnumerateDirectories())
                {
                    if (skip.Contains(sub.Name) || sub.Name.StartsWith('.')) continue;
                    // 符号链接 / junction 不进（与扫描器一致；Linux 上指回上级的链接会无限递归）
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (ProjectRoots.Classify(sub.FullName) != NestedRepoKind.None || skipDir?.Invoke(sub) == true) continue;
                    Scan(sub, depth - 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        var root = new DirectoryInfo(repoRoot);
        if (!root.Exists) return null;
        Scan(root, 2);
        if (candidates.Count == 0) return null;
        // 根目录下的优先；同级按大小
        var rootDepth = root.FullName.TrimEnd(Path.DirectorySeparatorChar).Count(c => c == Path.DirectorySeparatorChar);
        return candidates
            .OrderBy(f => f.DirectoryName!.TrimEnd(Path.DirectorySeparatorChar).Count(c => c == Path.DirectorySeparatorChar) - rootDepth)
            .ThenByDescending(f => f.Length)
            .ThenBy(f => f.FullName, StringComparer.Ordinal)
            .First().FullName;
    }

    /// <summary>启动 worker 并流式返回记录。stderr 每行交给 log。worker 非零退出或挂死抛 RoslynWorkerException。</summary>
    public static IAsyncEnumerable<RoslynRecord> RunAsync(
        string workerPath, string solutionPath, string repoRoot, Action<string>? log, TimeSpan? stallTimeout, CancellationToken ct) =>
        RunAsync(workerPath, solutionPath, [repoRoot], repoRoot, log, stallTimeout, ct);

    /// <summary>
    /// 多根版本：repoRoots 是"算仓库内"的目录（主根 + 外部根），落在它们之下的源码文件才输出、指向它们的边才不算外部；
    /// basePath（主根）是输出路径的基准，外部根的文件输出成 ../other/…，与 tree-sitter 入库的路径一致。
    /// </summary>
    /// <param name="onlyListPath">只输出这些文件（列表文件里每行一个绝对路径，见 worker 的 --only）；null = 解决方案里的全部文件。</param>
    public static async IAsyncEnumerable<RoslynRecord> RunAsync(
        string workerPath, string solutionPath, IReadOnlyList<string> repoRoots, string basePath, Action<string>? log, TimeSpan? stallTimeout,
        [EnumeratorCancellation] CancellationToken ct, string? onlyListPath = null)
    {
        var repoRoot = basePath;
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = repoRoot,
        };
        EnsureExecutable(workerPath);
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { psi.FileName = "dotnet"; psi.ArgumentList.Add(workerPath); }
        else psi.FileName = workerPath;
        psi.ArgumentList.Add("--sln"); psi.ArgumentList.Add(solutionPath);
        foreach (var r in repoRoots) { psi.ArgumentList.Add("--repo"); psi.ArgumentList.Add(r); }
        psi.ArgumentList.Add("--base"); psi.ArgumentList.Add(basePath);
        if (onlyListPath is not null) { psi.ArgumentList.Add("--only"); psi.ArgumentList.Add(onlyListPath); }

        using var process = Process.Start(psi) ?? throw new RoslynWorkerException($"无法启动 {workerPath}");
        var stderrTail = new Queue<string>();
        var stderrPump = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                log?.Invoke(line);
                stderrTail.Enqueue(line);
                if (stderrTail.Count > 20) stderrTail.Dequeue();
            }
        }, CancellationToken.None);

        var timeout = stallTimeout ?? DefaultStallTimeout;
        try
        {
            while (true)
            {
                var readLine = process.StandardOutput.ReadLineAsync(ct).AsTask();
                var finished = await Task.WhenAny(readLine, Task.Delay(timeout, ct));
                if (finished != readLine)
                {
                    TryKill(process);
                    ct.ThrowIfCancellationRequested();
                    throw new RoslynWorkerException($"Roslyn worker {timeout.TotalMinutes:F0} 分钟没有输出，已终止。");
                }
                var line = await readLine;
                if (line is null) break;
                RoslynRecord? record;
                try { record = RecordJson.Deserialize(line); }
                catch (System.Text.Json.JsonException ex) { log?.Invoke($"忽略无法解析的输出行：{ex.Message}"); continue; }
                if (record is not null) yield return record;
            }
        }
        finally
        {
            if (ct.IsCancellationRequested) TryKill(process);
        }

        await process.WaitForExitAsync(ct);
        await stderrPump;
        if (process.ExitCode == GlobalJsonSdkMissingExitCode)
            throw new RoslynWorkerException((stderrTail.LastOrDefault(l => l.Contains("global.json", StringComparison.Ordinal))?.Replace("codeatlas-roslyn: 失败 ", "")
                ?? "仓库 global.json 要求的 .NET SDK 本机没有兼容版本。") + FallbackNote);
        if (process.ExitCode != 0)
            throw new RoslynWorkerException($"Roslyn worker 退出码 {process.ExitCode}。" + (stderrTail.Count > 0 ? "\n" + string.Join("\n", stderrTail) : ""));
    }

    /// <summary>跑 worker 并把结果写回索引库。progress 收到 worker 的进度记录；log 收到 worker 的 stderr。</summary>
    public static Task<RoslynApplyReport> RunAndApplyAsync(
        IndexDatabase db, string workerPath, string solutionPath, string repoRoot,
        Action<ProgressRec>? progress, Action<string>? log, CancellationToken ct, TimeSpan? stallTimeout = null) =>
        RunAndApplyAsync(db, workerPath, [solutionPath], ProjectRoots.Single(repoRoot), progress, log, ct, stallTimeout);

    /// <summary>
    /// 多解决方案：依次各起一次 worker（隔离崩溃、MSBuildLocator 每进程只注册一次），记录流进同一个 RoslynApplier——
    /// 前一个解决方案的符号能给后一个的跨仓库边挂接；同一个文件出现在多个解决方案里只写第一次。
    /// 某个解决方案失败（打不开、崩溃、挂死）只记进 FailedSolutions，其余照常写回；全部失败才抛 RoslynWorkerException（整体回滚）。
    /// <para>
    /// pendingFiles（相对主根的路径，通常是 IndexReader.GetRoslynPendingFiles）：非 null 时增量——每个文件交给上次覆盖它的解决方案（files.roslyn_sln），
    /// 新文件交给所在仓库的解决方案；只启动分到了文件的解决方案，worker 只走这些文件（外加新 Handler 影响到的发送方，见 worker 的 --only）。
    /// 待更新文件超过 C# 文件总数一半时直接走全量。null = 全量。
    /// </para>
    /// 跑完后，交给成功的解决方案却没被输出的文件（不在任何项目里、项目加载失败）记 roslyn_miss，内容不变就不再算待更新；失败的解决方案的文件不记。
    /// </summary>
    public static async Task<RoslynApplyReport> RunAndApplyAsync(
        IndexDatabase db, string workerPath, IReadOnlyList<string> solutions, ProjectRoots roots,
        Action<ProgressRec>? progress, Action<string>? log, CancellationToken ct, TimeSpan? stallTimeout = null, IReadOnlyCollection<string>? pendingFiles = null)
    {
        var timings = new PhaseTimings();
        var byRepository = DiscoverSolutionsByRepository(roots);
        if (pendingFiles is not null)
        {
            var csharp = db.Scalar<long>("SELECT COUNT(*) FROM files WHERE language = 'csharp'");
            if (pendingFiles.Count * 2L > csharp)
            {
                log?.Invoke($"待更新 {pendingFiles.Count}/{csharp} 个 C# 文件，超过一半，走全量");
                pendingFiles = null;
            }
        }

        // 计划：(解决方案, 交给它的文件；null = 全部)
        List<(string Solution, List<string>? Files)> runs;
        var unassigned = new List<string>();
        if (pendingFiles is null) runs = solutions.Select(s => (s, (List<string>?)null)).ToList();
        else
        {
            var assigned = Assign(db, pendingFiles, solutions, roots, byRepository, unassigned);
            runs = solutions.Where(assigned.ContainsKey).Select(s => (s, (List<string>?)assigned[s])).ToList();
        }
        timings.Lap("roslyn.plan");

        using var applier = new RoslynApplier(db, incremental: pendingFiles is not null);
        var failures = new List<string>();
        var succeeded = new List<(string Solution, List<string>? Files)>();
        foreach (var (solution, files) in runs)
        {
            if (runs.Count > 1 || files is not null) log?.Invoke($"解决方案 {solution}{(files is null ? "" : $"（{files.Count} 个待更新文件）")}");
            // 阶段名带解决方案名：嵌套仓库各有一个解决方案，分开看才知道慢在哪个
            var prefix = $"roslyn[{Path.GetFileNameWithoutExtension(solution)}]";
            var wall = Stopwatch.StartNew();
            var applying = new Stopwatch();
            applier.Solution = RelativeToMain(roots, solution);
            string? onlyList = null;
            try
            {
                if (files is not null)
                {
                    onlyList = Path.Combine(Path.GetTempPath(), $"codeatlas-roslyn-only-{Guid.NewGuid():N}.txt");
                    await File.WriteAllLinesAsync(onlyList, files.Select(f => Path.GetFullPath(Path.Combine(roots.Main, f))), ct);
                }
                await foreach (var record in RunAsync(workerPath, solution, roots.All, roots.Main, log, stallTimeout, ct, onlyList))
                {
                    if (record is ProgressRec p) progress?.Invoke(p);
                    else if (record is ErrorRec e) log?.Invoke($"[{e.Scope}] {e.Message}");
                    else if (record is TimingRec t) timings.Add($"{prefix}.{t.Phase}", t.Seconds);
                    applying.Start();
                    applier.Apply(record);
                    applying.Stop();
                }
                succeeded.Add((solution, files));
            }
            catch (RoslynWorkerException ex) when (runs.Count > 1)
            {
                failures.Add($"{solution}: {ex.Message}");
                log?.Invoke($"解决方案失败，跳过：{solution}：{ex.Message}");
            }
            finally
            {
                if (onlyList is not null) try { File.Delete(onlyList); } catch (IOException) { }
            }
            // apply 是父进程逐条写回的累计时间，与 worker 的 walk 重叠；total 是这个解决方案从起进程到读完输出的墙钟时间
            timings.Add($"{prefix}.apply", applying.Elapsed.TotalSeconds);
            timings.Add($"{prefix}.total", wall.Elapsed.TotalSeconds);
        }
        if (runs.Count > 0 && failures.Count == runs.Count)
            throw new RoslynWorkerException("全部解决方案都失败：\n" + string.Join("\n", failures));

        timings.Lap("roslyn.run");
        applier.MarkMisses(Misses(db, pendingFiles, unassigned, succeeded, runs, solutions, roots, byRepository, applier.ProcessedFiles));
        timings.Lap("roslyn.misses");
        var report = applier.Finish();
        timings.AddRange(report.Timings ?? []);
        return report with { Solutions = runs.Count, FailedSolutions = failures, Timings = timings.Items };
    }

    /// <summary>
    /// 把待更新文件分给解决方案：files.roslyn_sln（在本次的解决方案里）→ 所在仓库（最深的前缀）的解决方案 → 只有一个解决方案时给它；
    /// 都不行的进 unassigned。
    /// </summary>
    private static Dictionary<string, List<string>> Assign(IndexDatabase db, IEnumerable<string> files, IReadOnlyList<string> solutions, ProjectRoots roots,
        IReadOnlyList<(RepoDir Repo, string Solution)> byRepository, List<string> unassigned)
    {
        var available = new HashSet<string>(solutions, ProjectRoots.PathComparer);
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand("SELECT path, roslyn_sln FROM files WHERE language = 'csharp' AND roslyn_sln IS NOT NULL"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) recorded[r.GetString(0)] = r.GetString(1);
        // 最深的前缀优先：主根的前缀是 ""，嵌套仓库 "mt4proxy/"，外部根 "../other/"
        var repos = byRepository.OrderByDescending(x => x.Repo.Prefix.Length).ToList();
        var result = new Dictionary<string, List<string>>(ProjectRoots.PathComparer);
        foreach (var file in files)
        {
            string? solution = null;
            if (recorded.TryGetValue(file, out var rel) && Path.GetFullPath(Path.Combine(roots.Main, rel)) is var abs && available.Contains(abs)) solution = abs;
            solution ??= repos.FirstOrDefault(x => file.StartsWith(x.Repo.Prefix, StringComparison.Ordinal) && available.Contains(x.Solution)).Solution;
            if (solution is null && solutions.Count == 1) solution = solutions[0];
            if (solution is null) { unassigned.Add(file); continue; }
            if (!result.TryGetValue(solution, out var list)) result[solution] = list = [];
            list.Add(file);
        }
        return result;
    }

    /// <summary>
    /// 要记 roslyn_miss 的文件。增量：交给成功的解决方案却没输出的，加上分不到解决方案的。
    /// 全量：所有仍未被覆盖的 C# 文件，除了按 Assign 规则归属失败的解决方案的。
    /// </summary>
    private static List<string> Misses(IndexDatabase db, IReadOnlyCollection<string>? pendingFiles, List<string> unassigned,
        List<(string Solution, List<string>? Files)> succeeded, List<(string Solution, List<string>? Files)> runs, IReadOnlyList<string> solutions, ProjectRoots roots,
        IReadOnlyList<(RepoDir Repo, string Solution)> byRepository, IReadOnlySet<string> processed)
    {
        if (pendingFiles is not null)
            return [.. unassigned, .. succeeded.SelectMany(s => s.Files!).Where(f => !processed.Contains(f))];
        var uncovered = db.Query<string>("SELECT path FROM files WHERE language = 'csharp' AND roslyn_at IS NULL");
        if (succeeded.Count == runs.Count) return uncovered;
        var ok = new HashSet<string>(succeeded.Select(s => s.Solution), ProjectRoots.PathComparer);
        var result = new List<string>();
        foreach (var (solution, files) in Assign(db, uncovered, solutions, roots, byRepository, result))
            if (ok.Contains(solution)) result.AddRange(files);
        return result;
    }

    /// <summary>解决方案相对主根的路径（'/' 分隔），存进 files.roslyn_sln。</summary>
    private static string RelativeToMain(ProjectRoots roots, string solution) => Path.GetRelativePath(roots.Main, solution).Replace('\\', '/');

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }
}

public sealed class RoslynWorkerException(string message) : Exception(message);
