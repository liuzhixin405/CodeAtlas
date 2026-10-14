using CodeAtlas.Core.Architecture;

namespace CodeAtlas.Core.Storage;

/// <summary>git 行为统计的整表替换与读取。</summary>
public static class GitStorage
{
    public static void Replace(IndexDatabase db, GitHistoryResult result)
    {
        using var batch = db.BeginBatch();
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
