using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Core.Storage;

/// <summary>git 行为统计的整表替换与读取。</summary>
public static class GitStorage
{
    /// <param name="fingerprint">这份结果的输入指纹（<see cref="GitHistory.RefsFingerprint"/>），同一事务里记下；null = 不记（下次一定重算）。</param>
    public static void Replace(IndexDatabase db, GitHistoryResult result, string? fingerprint = null)
    {
        using var batch = db.BeginBatch();
        ArchFingerprintStorage.Set(db, ArchFingerprintStorage.Git, result.Error is null ? fingerprint : null);
        db.Execute("DELETE FROM git_file_stats");
        db.Execute("DELETE FROM git_coupling");
        db.Execute("DELETE FROM git_meta");
        foreach (var f in result.Files)
            db.Execute("""
                INSERT INTO git_file_stats (path, revisions, added, deleted, first_commit, last_commit, authors)
                VALUES ($p, $r, $a, $d, $f, $l, $au)
                """, ("$p", f.Path), ("$r", f.Revisions), ("$a", f.Added), ("$d", f.Deleted),
                ("$f", f.FirstCommit.ToString("yyyy-MM-dd")), ("$l", f.LastCommit.ToString("yyyy-MM-dd")), ("$au", f.Authors));
        foreach (var c in result.Coupling)
            db.Execute("INSERT INTO git_coupling (path_a, path_b, shared, degree) VALUES ($a, $b, $s, $d)", ("$a", c.PathA), ("$b", c.PathB), ("$s", c.Shared), ("$d", c.Degree));
        db.Execute("INSERT INTO git_meta (commits, commits_skipped, error, scanned_at) VALUES ($c, $s, $e, $t)",
            ("$c", result.Commits), ("$s", result.CommitsSkippedForCoupling), ("$e", result.Error), ("$t", DateTimeOffset.UtcNow.ToString("O")));
        batch.Commit();
    }

    /// <summary>
    /// 各仓库的 ref（HEAD、分支、标签、远端、stash）与 .mailmap 都没变就跳过 git log 与写库，返回 false；变了（或第一次、上次失败）就重算写库，返回 true。
    /// 统计只取决于提交历史，工作区里的改动不影响它。拿不到 ref（没有 git、不是仓库）时照常重算，由 git log 报错写进 git_meta.error。
    /// </summary>
    public static bool RefreshIfChanged(IndexDatabase db, ProjectRoots roots, TimeSpan? timeout = null)
    {
        var fingerprint = GitHistory.RefsFingerprint(roots);
        if (fingerprint is not null && ArchFingerprintStorage.Get(db, ArchFingerprintStorage.Git)?.Value == fingerprint) return false;
        Replace(db, GitHistory.Run(roots, timeout), fingerprint);
        return true;
    }

    public static GitHistoryResult Read(IndexDatabase db)
    {
        var files = new List<GitFileStats>();
        using (var cmd = db.CreateCommand("SELECT path, revisions, added, deleted, first_commit, last_commit, authors FROM git_file_stats"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                files.Add(new GitFileStats(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), DateOnly.Parse(r.GetString(4)), DateOnly.Parse(r.GetString(5)), r.GetInt32(6)));
        var coupling = new List<GitCoupling>();
        using (var cmd = db.CreateCommand("SELECT path_a, path_b, shared, degree FROM git_coupling"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) coupling.Add(new GitCoupling(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetDouble(3)));
        var commits = 0; var skipped = 0; string? error = null;
        using (var cmd = db.CreateCommand("SELECT commits, commits_skipped, error FROM git_meta ORDER BY id DESC LIMIT 1"))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { commits = r.GetInt32(0); skipped = r.GetInt32(1); error = r.IsDBNull(2) ? null : r.GetString(2); }
        return new GitHistoryResult(files, coupling, commits, skipped, error);
    }
}
