using System.Diagnostics;
using System.Globalization;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Architecture;

/// <param name="Path">相对仓库根，'/' 分隔；嵌套 git 仓库的文件带子目录前缀。</param>
/// <param name="Revisions">改过它的提交数。</param>
/// <param name="Commits">改过它的提交（短哈希，嵌套仓库带路径前缀以免撞号）；从库里读回的统计没有，为 null。
/// 多个文件合起来算"涉及多少次提交"要按它去重——各文件 Revisions 相加是"文件修订合计"，同一次提交改了 N 个文件会算 N 次。</param>
public sealed record GitFileStats(string Path, int Revisions, int Added, int Deleted, DateOnly FirstCommit, DateOnly LastCommit, int Authors, IReadOnlyList<string>? Commits = null);

/// <param name="Degree">共同修订 ÷ 两个文件的平均修订数。</param>
public sealed record GitCoupling(string PathA, string PathB, int Shared, double Degree);

public sealed record GitHistoryResult(IReadOnlyList<GitFileStats> Files, IReadOnlyList<GitCoupling> Coupling, int Commits, int CommitsSkippedForCoupling, string? Error);

/// <summary>
/// git 历史的行为分析（code-maat 的 revisions / churn / age / coupling 子集，用 C# 重写）。
/// 解析 `git log --all --numstat --date=short --no-renames --pretty=format:--%h--%ad--%aN` 的输出。
/// </summary>
public static class GitHistory
{
    /// <summary>超过这么多文件的提交不算耦合（批量格式化、合并），但仍算修订。</summary>
    public const int MaxChangesetForCoupling = 30;
    public const int MinSharedRevisions = 5;
    public const double MinCouplingDegree = 0.3;
    public const string LogArguments = "log --all --numstat --date=short --no-renames --pretty=format:--%h--%ad--%aN";

    private sealed class Acc
    {
        public int Revisions; public int Added; public int Deleted; public DateOnly First = DateOnly.MaxValue; public DateOnly Last = DateOnly.MinValue;
        public HashSet<string> Authors = new(StringComparer.Ordinal);
        public List<string> Commits = [];
    }

    /// <summary>解析一段 git log 文本。prefix 加在每个路径前（嵌套仓库）。</summary>
    public static GitHistoryResult Parse(string log, string prefix = "")
    {
        var files = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string, string), int>();
        var commits = 0;
        var skipped = 0;
        var current = new List<string>();
        DateOnly date = default;
        string author = "";
        string hash = "";

        void Flush()
        {
            if (current.Count == 0) return;
            if (current.Count <= MaxChangesetForCoupling)
            {
                var sorted = current.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
                for (var i = 0; i < sorted.Count; i++)
                    for (var j = i + 1; j < sorted.Count; j++)
                        pairs[(sorted[i], sorted[j])] = pairs.GetValueOrDefault((sorted[i], sorted[j])) + 1;
            }
            else skipped++;
            current.Clear();
        }

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                Flush();
                var parts = line.Split("--", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) { date = default; continue; }
                author = parts.Length > 2 ? parts[2] : "";
                hash = prefix + parts[0];
                commits++;
                continue;
            }
            if (line.Length == 0 || date == default) continue;
            var cols = line.Split('\t');
            if (cols.Length < 3) continue;
            var path = prefix + cols[2].Trim();
            if (!files.TryGetValue(path, out var acc)) files[path] = acc = new Acc();
            acc.Revisions++;
            if (int.TryParse(cols[0], out var added)) acc.Added += added;     // 二进制是 "-"
            if (int.TryParse(cols[1], out var deleted)) acc.Deleted += deleted;
            if (date < acc.First) acc.First = date;
            if (date > acc.Last) acc.Last = date;
            if (author.Length > 0) acc.Authors.Add(author);
            acc.Commits.Add(hash);
            current.Add(path);
        }
        Flush();

        var stats = files.Select(p => new GitFileStats(p.Key, p.Value.Revisions, p.Value.Added, p.Value.Deleted, p.Value.First, p.Value.Last, p.Value.Authors.Count, p.Value.Commits))
            .OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
        var coupling = new List<GitCoupling>();
        foreach (var ((a, b), shared) in pairs)
        {
            if (shared < MinSharedRevisions) continue;
            var avg = (files[a].Revisions + files[b].Revisions) / 2.0;
            var degree = avg > 0 ? shared / avg : 0;
            if (degree >= MinCouplingDegree) coupling.Add(new GitCoupling(a, b, shared, Math.Round(degree, 3)));
        }
        coupling.Sort((x, y) => y.Shared != x.Shared ? y.Shared.CompareTo(x.Shared) : string.CompareOrdinal(x.PathA + x.PathB, y.PathA + y.PathB));
        return new GitHistoryResult(stats, coupling, commits, skipped, null);
    }

    /// <summary>在 root 与其下的嵌套仓库各跑一次 git log；git 不可用时返回 Error。nestedRepoDirs 为 null 时自动找（跳过 worktree）。</summary>
    public static GitHistoryResult Run(string root, IEnumerable<string>? nestedRepoDirs = null, TimeSpan? timeout = null)
    {
        if (nestedRepoDirs is null) return Run(ProjectRoots.Single(root), timeout);
        var targets = new List<(string Dir, string Prefix, bool IsMain)> { (root, "", true) };
        foreach (var d in nestedRepoDirs) targets.Add((Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)), d.TrimEnd('/') + "/", false));
        return RunTargets(targets, timeout);
    }

    /// <summary>
    /// 多根：主根、外部根与它们之下被纳入的嵌套仓库各跑一次 git log，路径加上相对主根的前缀（外部根是 ../other/）。
    /// 主根拿不到历史时整体返回 Error；其他仓库失败只跳过。
    /// </summary>
    public static GitHistoryResult Run(ProjectRoots roots, TimeSpan? timeout = null) =>
        RunTargets(roots.Repositories().Select(r => (r.FullPath, r.Prefix, IsMain: r.Prefix.Length == 0)).ToList(), timeout);

    /// <summary>各仓库并行跑 git log 与解析（ccs-backend 主仓库 + 两个嵌套仓库），结果按 targets 的顺序合并，与串行时一致。</summary>
    private static GitHistoryResult RunTargets(IReadOnlyList<(string Dir, string Prefix, bool IsMain)> targets, TimeSpan? timeout)
    {
        var results = new (GitHistoryResult? Result, string? Error)[targets.Count];
        Parallel.For(0, targets.Count, i =>
        {
            var (dir, prefix, _) = targets[i];
            var (output, error) = RunGit(dir, LogArguments, timeout ?? TimeSpan.FromMinutes(2));
            results[i] = error is null ? (Parse(output, prefix), null) : (null, error);
        });
        var all = new List<GitFileStats>();
        var coupling = new List<GitCoupling>();
        var commits = 0;
        var skipped = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var (r, error) = results[i];
            if (r is null) { if (targets[i].IsMain) return new GitHistoryResult([], [], 0, 0, error); continue; }
            all.AddRange(r.Files); coupling.AddRange(r.Coupling); commits += r.Commits; skipped += r.CommitsSkippedForCoupling;
        }
        return new GitHistoryResult(all, coupling, commits, skipped, null);
    }

    /// <summary>
    /// git 统计的输入指纹：每个仓库（主根、外部根、被纳入的嵌套仓库）里决定 <c>git log --all</c> 结果的东西——HEAD 与全部 ref
    /// （分支 / 标签 / 远端 / stash；只看 HEAD 不够：fetch 下来的远端分支、别的本地分支也在 --all 里）、浅克隆边界、grafts，
    /// 加上仓库根 .mailmap（%aN 按它归并作者名）。统计只看提交历史，工作区里没提交的改动不影响结果，所以不进指纹。
    /// 不起 git 进程：读 .git 里的 HEAD、packed-refs、refs/**、reftable/**、shallow、info/grafts 的内容（任何 ref 变动都会改写这些文件；
    /// Windows 上起一次 git 进程要 0.3s 以上）。.git 认不出时退回 <c>git show-ref --head</c>；那也失败（没有 git、不是仓库）返回 null，调用方照常全量重算。
    /// </summary>
    public static string? RefsFingerprint(ProjectRoots roots, TimeSpan? timeout = null)
    {
        var repos = roots.Repositories().ToList();
        var parts = new string?[repos.Count];
        Parallel.For(0, repos.Count, i =>
        {
            var state = RefFilesState(repos[i].FullPath);
            if (state is null)
            {
                var (output, error) = RunGit(repos[i].FullPath, "show-ref --head", timeout ?? TimeSpan.FromSeconds(30), okExitCodes: [0, 1]); // 1 = 没有任何 ref（空仓库）
                if (error is not null) return;
                state = "show-ref|" + output.Replace("\r", "");
            }
            var mailmap = new FileInfo(Path.Combine(repos[i].FullPath, ".mailmap"));
            parts[i] = repos[i].Prefix + "\n" + state + "\nmailmap " + (mailmap.Exists ? mailmap.Length + " " + mailmap.LastWriteTimeUtc.Ticks : "-");
        });
        if (parts.Any(p => p is null)) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(LogArguments + "\n" + string.Join("\n--\n", parts));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    /// <summary>
    /// 仓库的 ref 文件状态（路径 + 内容哈希）。.git 是目录（普通克隆）或 "gitdir: …" 文件（子模块 / worktree，后者再按 commondir 找共享的 ref）。
    /// 认不出返回 null。
    /// </summary>
    private static string? RefFilesState(string repoDir)
    {
        try
        {
            var dotGit = Path.Combine(repoDir, ".git");
            string gitDir;
            if (Directory.Exists(dotGit)) gitDir = dotGit;
            else if (File.Exists(dotGit))
            {
                var line = File.ReadAllText(dotGit).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                gitDir = Path.GetFullPath(Path.Combine(repoDir, line["gitdir:".Length..].Trim()));
            }
            else return null;
            if (!File.Exists(Path.Combine(gitDir, "HEAD"))) return null;
            var commonFile = Path.Combine(gitDir, "commondir");
            var common = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())) : gitDir;
            var sb = new System.Text.StringBuilder();
            void Stat(string full)
            {
                // 读内容而不是看修改时间：ref 文件只有几十字节，连续两次提交写出的大小相同、修改时间在 Windows 上可能落在同一个时钟刻度里
                sb.Append(full).Append('|').Append(File.Exists(full) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full))) : "-").Append('\n');
            }
            void StatTree(string dir)
            {
                if (!Directory.Exists(dir)) { sb.Append(dir).Append("|-\n"); return; }
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) Stat(f);
            }
            Stat(Path.Combine(gitDir, "HEAD"));
            if (!string.Equals(gitDir, common, StringComparison.OrdinalIgnoreCase)) StatTree(Path.Combine(gitDir, "refs"));
            Stat(Path.Combine(common, "packed-refs"));
            StatTree(Path.Combine(common, "refs"));
            StatTree(Path.Combine(common, "reftable"));
            Stat(Path.Combine(common, "shallow"));
            Stat(Path.Combine(common, "info", "grafts"));
            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>root 下（不含 root 本身）被纳入的嵌套仓库（独立克隆 / 子模块，不含 worktree），相对路径；跳过点目录与 node_modules 等。</summary>
    public static List<string> FindNestedRepos(string root) =>
        ProjectRoots.Single(root).Repositories().Where(r => r.IsNested).Select(r => r.Prefix.TrimEnd('/')).ToList();

    private static (string Output, string? Error) RunGit(string dir, string args, TimeSpan timeout, int[]? okExitCodes = null)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            using var p = Process.Start(psi);
            if (p is null) return ("", "无法启动 git");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } return ("", $"git {args.Split(' ')[0]} 超时"); }
            var output = outTask.GetAwaiter().GetResult();
            var err = errTask.GetAwaiter().GetResult();
            if (!(okExitCodes ?? [0]).Contains(p.ExitCode)) return ("", $"git 退出码 {p.ExitCode}：{err.Trim()}");
            return (output, null);
        }
        catch (System.ComponentModel.Win32Exception) { return ("", "没有找到 git 可执行文件"); }
    }
}
