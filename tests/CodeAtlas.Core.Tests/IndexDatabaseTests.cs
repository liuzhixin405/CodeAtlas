using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexDatabaseTests
{
    [Fact]
    public void OpenInMemory_UsesSqlitePatchedForCve20256965()
    {
        using var db = IndexDatabase.OpenInMemory();
        var version = Version.Parse(db.Scalar<string>("SELECT sqlite_version()"));

        Assert.True(version >= new Version(3, 50, 2), $"SQLite {version} is affected by CVE-2025-6965.");
    }

    [Fact]
    public void OpenInMemory_CreatesAllTables()
    {
        using var db = IndexDatabase.OpenInMemory();

        var tables = db.Query<string>("SELECT name FROM sqlite_master WHERE type IN ('table','view') ORDER BY name");

        Assert.Contains("files", tables);
        Assert.Contains("symbols", tables);
        Assert.Contains("edges", tables);
        Assert.Contains("imports", tables);
        Assert.Contains("di_registrations", tables);
        Assert.Contains("summaries", tables);
        Assert.Contains("symbols_fts", tables);
        Assert.Contains("arch_nodes", tables);
        Assert.Contains("arch_edges", tables);
        Assert.Contains("arch_outbound", tables);
        Assert.Contains("arch_conflicts", tables);
    }

    [Fact]
    public void OpenInMemory_EnablesForeignKeys()
    {
        using var db = IndexDatabase.OpenInMemory();
        Assert.Equal(1L, db.Scalar<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public void Open_CreatesParentDirectoryAndFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codeatlas-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, ".codeatlas", "index.db");
        try
        {
            using (var db = IndexDatabase.Open(path))
            {
                Assert.True(File.Exists(path));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BeginBatch_CommitPersists_DisposeWithoutCommitRollsBack()
    {
        using var db = IndexDatabase.OpenInMemory();
        db.Execute("CREATE TABLE t (v INTEGER)");

        using (var batch = db.BeginBatch())
        {
            db.Execute("INSERT INTO t VALUES (1)");
            batch.Commit();
        }
        using (db.BeginBatch())
        {
            db.Execute("INSERT INTO t VALUES (2)");
            // 不 Commit
        }

        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM t"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var outer = db.BeginBatch();
            db.BeginBatch();
        });
    }

    [Fact]
    public void IndexVersion_FreshDbIsCurrent_OldDbNeedsRebuild_MarkCurrentClearsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "codeatlas-ver-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var fresh = IndexDatabase.Open(path))
            {
                Assert.False(fresh.NeedsRebuild);
                Assert.Equal(IndexDatabase.CurrentIndexVersion, fresh.Scalar<long>("PRAGMA user_version"));
                fresh.Execute("PRAGMA user_version = 1"); // 模拟旧版本建的库
            }
            using (var old = IndexDatabase.Open(path))
            {
                Assert.True(old.NeedsRebuild);
                old.MarkCurrentVersion();
                Assert.False(old.NeedsRebuild);
            }
            using var reopened = IndexDatabase.Open(path);
            Assert.False(reopened.NeedsRebuild);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_MigratesOldSchema_AddsRoslynAtColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), "codeatlas-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = IndexDatabase.Open(path))
            {
                // 模拟旧库：去掉 roslyn_at 列
                db.Execute("ALTER TABLE files DROP COLUMN roslyn_at");
                db.Execute("PRAGMA user_version = 2");
            }
            using var reopened = IndexDatabase.Open(path);
            Assert.Contains("roslyn_at", reopened.Query<string>("SELECT name FROM pragma_table_info('files')"));
            Assert.True(reopened.NeedsRebuild);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Open_IsIdempotent_SecondOpenDoesNotFail()
    {
        var path = Path.Combine(Path.GetTempPath(), "codeatlas-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (IndexDatabase.Open(path)) { }
            using (IndexDatabase.Open(path)) { }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
