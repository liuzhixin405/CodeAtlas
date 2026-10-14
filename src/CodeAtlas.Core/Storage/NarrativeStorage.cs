using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// 一条 AI 叙述缓存。
/// StableKey 跨索引不变（流程是"容器|入口方法全名"，不是符号 id）；Fingerprint 是架构事实指纹；
/// SourcesJson 是生成时对象覆盖的文件 → 内容哈希（判"生成后 N 个相关文件有改动"）；GitCommit 是生成时的 HEAD，给人看。
/// Json 是校验后的正文，evidence 里会随索引变的 id 已换成稳定形式（sym@全名）。
/// </summary>
public sealed record NarrativeRow(
    string Kind, string StableKey, string Model, string Status, string Json, string? Error, string GeneratedAt,
    string Fingerprint, string SourcesJson, string? GitCommit);

/// <summary>
/// 叙述缓存单独一个 SQLite 文件（默认 .codeatlas/narratives.db，与 index.db 同目录）：
/// 全量重建索引会重建 index.db，本地模型几分钟一条算出来的叙述不能跟着丢。
/// 按 (kind, stable_key) 一条，整行覆盖；只存终态（ready / failed）。连接不是线程安全的，方法内部加锁。
/// </summary>
public sealed class NarrativeCache : IDisposable
{
    public const string FileName = "narratives.db";
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _c;
    private readonly object _lock = new();

    private NarrativeCache(SqliteConnection connection)
    {
        _c = connection;
        Exec("""
            CREATE TABLE IF NOT EXISTS narratives (
                kind TEXT NOT NULL,
                stable_key TEXT NOT NULL,
                model TEXT NOT NULL,
                status TEXT NOT NULL,
                json TEXT NOT NULL,
                error TEXT,
                generated_at TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                sources_json TEXT NOT NULL,
                git_commit TEXT,
                PRIMARY KEY (kind, stable_key)
            );
            """);
        Exec($"PRAGMA user_version = {SchemaVersion}");
    }

    /// <summary>索引库路径旁边的缓存文件。</summary>
    public static string PathBeside(string indexDbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexDbPath)) ?? ".", FileName);

    public static NarrativeCache Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        var cache = new NarrativeCache(c);
        cache.Exec("PRAGMA journal_mode=WAL;");
        return cache;
    }

    public static NarrativeCache OpenInMemory()
    {
        var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        return new NarrativeCache(c);
    }

    public void Upsert(NarrativeRow row)
    {
        lock (_lock)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO narratives (kind, stable_key, model, status, json, error, generated_at, fingerprint, sources_json, git_commit)
                VALUES ($k, $s, $m, $st, $j, $e, $g, $f, $src, $git)
                ON CONFLICT(kind, stable_key) DO UPDATE SET model = excluded.model, status = excluded.status, json = excluded.json,
                    error = excluded.error, generated_at = excluded.generated_at, fingerprint = excluded.fingerprint,
                    sources_json = excluded.sources_json, git_commit = excluded.git_commit
                """;
            cmd.Parameters.AddWithValue("$k", row.Kind);
            cmd.Parameters.AddWithValue("$s", row.StableKey);
            cmd.Parameters.AddWithValue("$m", row.Model);
            cmd.Parameters.AddWithValue("$st", row.Status);
            cmd.Parameters.AddWithValue("$j", row.Json);
            cmd.Parameters.AddWithValue("$e", (object?)row.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$g", row.GeneratedAt);
            cmd.Parameters.AddWithValue("$f", row.Fingerprint);
            cmd.Parameters.AddWithValue("$src", row.SourcesJson);
            cmd.Parameters.AddWithValue("$git", (object?)row.GitCommit ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public NarrativeRow? Get(string kind, string stableKey)
    {
        lock (_lock)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT kind, stable_key, model, status, json, error, generated_at, fingerprint, sources_json, git_commit FROM narratives WHERE kind = $k AND stable_key = $s";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$s", stableKey);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new NarrativeRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
        }
    }

    public long Count()
    {
        lock (_lock)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM narratives";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    private void Exec(string sql)
    {
        lock (_lock)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _c.Dispose();
}
