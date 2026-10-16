using System.Security.Cryptography;
using System.Text;

namespace CodeAtlas.Core.Storage;

/// <summary>
/// 索引后处理各步上次成功时的输入指纹（post_fingerprints 表，按需建）。指纹相同 = 输入没变，结果直接沿用，不必重算。
/// 写结果表的入口（ArchStorage.Replace、GitStorage.Replace）都会顺手清掉对应指纹：别处（codeatlas arch、旧调用方）写进去的结果
/// 不带指纹，下一次后处理一定重算。
/// </summary>
public static class ArchFingerprintStorage
{
    /// <summary>容器图 + 入口 + 规则（arch_* / entry_points / route_matches / arch_rules）。</summary>
    public const string Post = "post";
    /// <summary>git 行为统计（git_*）。</summary>
    public const string Git = "git";

    private const string EnsureSql = "CREATE TABLE IF NOT EXISTS post_fingerprints (step TEXT PRIMARY KEY, value TEXT NOT NULL, info TEXT, at TEXT NOT NULL)";

    public static (string Value, string? Info)? Get(IndexDatabase db, string step)
    {
        db.Execute(EnsureSql);
        using var cmd = db.CreateCommand("SELECT value, info FROM post_fingerprints WHERE step = $s", ("$s", step));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }

    /// <summary>value 为 null 时删除。可以在调用方的批次（事务）里调用。</summary>
    public static void Set(IndexDatabase db, string step, string? value, string? info = null)
    {
        db.Execute(EnsureSql);
        if (value is null) { db.Execute("DELETE FROM post_fingerprints WHERE step = $s", ("$s", step)); return; }
        db.Execute("INSERT OR REPLACE INTO post_fingerprints (step, value, info, at) VALUES ($s, $v, $i, $t)",
            ("$s", step), ("$v", value), ("$i", info), ("$t", DateTimeOffset.UtcNow.ToString("O")));
    }

    /// <summary>
    /// 入口识别读的索引内容（符号与边）的指纹：files 表逐行（路径、内容哈希、Roslyn 写回时间、状态），加 symbols / edges 的行数、最大 id
    /// 与按行取模求和的校验值（含 id：entry_points / route_matches 存的是符号 id，同样内容换了 id 也要重算）。ccs-backend（10 万符号、88 万边）约 0.3s。
    /// </summary>
    public static string IndexContent(IndexDatabase db)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s) => hash.AppendData(Encoding.UTF8.GetBytes(s + "\n"));
        using (var cmd = db.CreateCommand("SELECT id, path, hash, COALESCE(roslyn_at, ''), status FROM files ORDER BY id"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) Add($"{r.GetInt64(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}|{r.GetString(4)}");
        Add(Aggregate(db, """
            SELECT COUNT(*), COALESCE(MAX(id), 0), COALESCE(SUM((id * 1000003 + file_id * 7919 + start_line * 31 + end_line * 17 + COALESCE(parent_id, 0) * 13
                + length(qualified_name) * 7 + length(kind) * 5 + length(COALESCE(visibility, '')) * 3) % 2147483647), 0) FROM symbols
            """));
        Add(Aggregate(db, """
            SELECT COUNT(*), COALESCE(MAX(id), 0), COALESCE(SUM((from_id * 1000003 + COALESCE(to_id, 0) * 7919 + line * 31 + length(kind) * 17
                + length(COALESCE(via, '')) * 13 + CAST(confidence * 100 AS INTEGER) * 7 + COALESCE(pos, 0) * 3) % 2147483647), 0) FROM edges
            """));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Aggregate(IndexDatabase db, string sql)
    {
        using var cmd = db.CreateCommand(sql);
        using var r = cmd.ExecuteReader();
        r.Read();
        return $"{r.GetInt64(0)}|{r.GetInt64(1)}|{r.GetInt64(2)}";
    }

    /// <summary>几段文本合成一个指纹。</summary>
    public static string Combine(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n|\n", parts))));
}
