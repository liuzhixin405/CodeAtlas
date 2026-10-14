using System.Diagnostics;
using System.Runtime.CompilerServices;
using CodeAtlas.Core.Storage;

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

    /// <summary>
    /// 找 worker：环境变量 CODEATLAS_ROSLYN_WORKER → 与宿主程序同目录的 codeatlas-roslyn.exe / codeatlas-roslyn.dll。
    /// 返回 .dll 时由 RunAsync 用 dotnet 启动（开发/测试环境）。找不到返回 null。
    /// </summary>
    public static string? FindWorker(string? baseDirectory = null)
    {
        var env = Environment.GetEnvironmentVariable(WorkerPathEnv);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);
        var dir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[] { WorkerName + ".exe", WorkerName, WorkerName + ".dll" })
        {
            var path = Path.Combine(dir, candidate);
            if (File.Exists(path) && (candidate != WorkerName || !OperatingSystem.IsWindows())) return path;
        }
        return null;
    }

    /// <summary>
    /// 在仓库里找解决方案：根目录优先，其次向下两层；多个时取文件最大的（通常是包含项目最多的）。
    /// .slnx 与 .sln 同等对待。找不到返回 null。
    /// </summary>
    public static string? DiscoverSolution(string repoRoot)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".git", "dist", "build", ".vs", ".idea", ".codeatlas" };
        var candidates = new List<FileInfo>();
        void Scan(DirectoryInfo dir, int depth)
        {
            try
            {
                candidates.AddRange(dir.EnumerateFiles("*.sln"));
                candidates.AddRange(dir.EnumerateFiles("*.slnx"));
                if (depth == 0) return;
                foreach (var sub in dir.EnumerateDirectories())
                    if (!skip.Contains(sub.Name) && !sub.Name.StartsWith('.')) Scan(sub, depth - 1);
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
    public static async IAsyncEnumerable<RoslynRecord> RunAsync(
        string workerPath, string solutionPath, string repoRoot, Action<string>? log, TimeSpan? stallTimeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
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
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { psi.FileName = "dotnet"; psi.ArgumentList.Add(workerPath); }
        else psi.FileName = workerPath;
        psi.ArgumentList.Add("--sln"); psi.ArgumentList.Add(solutionPath);
        psi.ArgumentList.Add("--repo"); psi.ArgumentList.Add(repoRoot);

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
        if (process.ExitCode != 0)
            throw new RoslynWorkerException($"Roslyn worker 退出码 {process.ExitCode}。" + (stderrTail.Count > 0 ? "\n" + string.Join("\n", stderrTail) : ""));
    }

    /// <summary>跑 worker 并把结果写回索引库。progress 收到 worker 的进度记录；log 收到 worker 的 stderr。</summary>
    public static async Task<RoslynApplyReport> RunAndApplyAsync(
        IndexDatabase db, string workerPath, string solutionPath, string repoRoot,
        Action<ProgressRec>? progress, Action<string>? log, CancellationToken ct, TimeSpan? stallTimeout = null)
    {
        using var applier = new RoslynApplier(db);
        await foreach (var record in RunAsync(workerPath, solutionPath, repoRoot, log, stallTimeout, ct))
        {
            if (record is ProgressRec p) progress?.Invoke(p);
            else if (record is ErrorRec e) log?.Invoke($"[{e.Scope}] {e.Message}");
            applier.Apply(record);
        }
        return applier.Finish();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }
}

public sealed class RoslynWorkerException(string message) : Exception(message);
