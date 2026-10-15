using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    public static IReadOnlyList<string> DiscoverSolutions(ProjectRoots roots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(ProjectRoots.PathComparer);
        foreach (var repo in roots.Repositories())
            if (DiscoverSolution(repo.FullPath, dir => roots.IsRoot(dir.FullName)) is { } sln && seen.Add(sln)) result.Add(sln);
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
    public static async IAsyncEnumerable<RoslynRecord> RunAsync(
        string workerPath, string solutionPath, IReadOnlyList<string> repoRoots, string basePath, Action<string>? log, TimeSpan? stallTimeout,
        [EnumeratorCancellation] CancellationToken ct)
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
    /// </summary>
    public static async Task<RoslynApplyReport> RunAndApplyAsync(
        IndexDatabase db, string workerPath, IReadOnlyList<string> solutions, ProjectRoots roots,
        Action<ProgressRec>? progress, Action<string>? log, CancellationToken ct, TimeSpan? stallTimeout = null)
    {
        using var applier = new RoslynApplier(db);
        var failures = new List<string>();
        foreach (var solution in solutions)
        {
            if (solutions.Count > 1) log?.Invoke($"解决方案 {solution}");
            try
            {
                await foreach (var record in RunAsync(workerPath, solution, roots.All, roots.Main, log, stallTimeout, ct))
                {
                    if (record is ProgressRec p) progress?.Invoke(p);
                    else if (record is ErrorRec e) log?.Invoke($"[{e.Scope}] {e.Message}");
                    applier.Apply(record);
                }
            }
            catch (RoslynWorkerException ex) when (solutions.Count > 1)
            {
                failures.Add($"{solution}: {ex.Message}");
                log?.Invoke($"解决方案失败，跳过：{solution}：{ex.Message}");
            }
        }
        if (solutions.Count > 0 && failures.Count == solutions.Count)
            throw new RoslynWorkerException("全部解决方案都失败：\n" + string.Join("\n", failures));
        return applier.Finish() with { Solutions = solutions.Count, FailedSolutions = failures };
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }
}

public sealed class RoslynWorkerException(string message) : Exception(message);
