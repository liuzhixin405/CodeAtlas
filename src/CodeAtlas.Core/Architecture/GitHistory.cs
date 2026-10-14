using System.Diagnostics;
using System.Globalization;

namespace CodeAtlas.Core.Architecture;

/// <param name="Path">相对仓库根，'/' 分隔；嵌套 git 仓库的文件带子目录前缀。</param>
/// <param name="Revisions">改过它的提交数。</param>
public sealed record GitFileStats(string Path, int Revisions, int Added, int Deleted, DateOnly FirstCommit, DateOnly LastCommit, int Authors);

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
            current.Add(path);
        }
        Flush();

        var stats = files.Select(p => new GitFileStats(p.Key, p.Value.Revisions, p.Value.Added, p.Value.Deleted, p.Value.First, p.Value.Last, p.Value.Authors.Count))
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

    /// <summary>在 root 与其下的嵌套仓库各跑一次 git log；git 不可用时返回 Error。</summary>
    public static GitHistoryResult Run(string root, IEnumerable<string>? nestedRepoDirs = null, TimeSpan? timeout = null)
    {
        var all = new List<GitFileStats>();
        var coupling = new List<GitCoupling>();
        var commits = 0;
        var skipped = 0;
        var targets = new List<(string Dir, string Prefix)> { (root, "") };
        foreach (var d in nestedRepoDirs ?? FindNestedRepos(root)) targets.Add((Path.Combine(root, d.Replace('/', Path.DirectorySeparatorChar)), d.TrimEnd('/') + "/"));
        foreach (var (dir, prefix) in targets)
        {
            var (output, error) = RunGit(dir, LogArguments, timeout ?? TimeSpan.FromMinutes(2));
            if (error is not null) { if (prefix.Length == 0) return new GitHistoryResult([], [], 0, 0, error); continue; }
            var r = Parse(output, prefix);
            all.AddRange(r.Files); coupling.AddRange(r.Coupling); commits += r.Commits; skipped += r.CommitsSkippedForCoupling;
        }
        return new GitHistoryResult(all, coupling, commits, skipped, null);
    }

    /// <summary>root 下（不含 root 本身）含 .git 的目录，相对路径；跳过点目录与 node_modules 等。</summary>
    public static List<string> FindNestedRepos(string root)
    {
        var found = new List<string>();
        void Walk(string dir, int depth)
        {
            if (depth > 4) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || ProjectInventory.IgnoredDirectories.Contains(name)) continue;
                if (Directory.Exists(Path.Combine(sub, ".git")) || File.Exists(Path.Combine(sub, ".git"))) { found.Add(ProjectInventory.Rel(root, sub)); continue; }
                Walk(sub, depth + 1);
            }
        }
        Walk(root, 0);
        return found;
    }

    private static (string Output, string? Error) RunGit(string dir, string args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            using var p = Process.Start(psi);
            if (p is null) return ("", "无法启动 git");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } return ("", "git log 超时"); }
            var output = outTask.GetAwaiter().GetResult();
            var err = errTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0) return ("", $"git 退出码 {p.ExitCode}：{err.Trim()}");
            return (output, null);
        }
        catch (System.ComponentModel.Win32Exception) { return ("", "没有找到 git 可执行文件"); }
    }
}
