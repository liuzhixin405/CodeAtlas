using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <summary>一个仓库的索引库。单连接，调用方负责串行使用。</summary>
public sealed class IndexDatabase : IDisposable
{
    public SqliteConnection Connection { get; }

    private SqliteTransaction? _transaction;

    private IndexDatabase(SqliteConnection connection) => Connection = connection;

    public static IndexDatabase Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // 单个长连接，禁用连接池：否则 Dispose 后池仍持有文件句柄，索引库无法被删除或移动。
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        var db = new IndexDatabase(connection);
        db.Execute("PRAGMA journal_mode=WAL;");
        db.Initialize();
        return db;
    }

    public static IndexDatabase OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new IndexDatabase(connection);
        db.Initialize();
        return db;
    }

    /// <summary>
    /// 索引格式版本。解析规则有不兼容变化（会产生新的边种类 / via）时递增；
    /// 打开旧版本的库时 NeedsRebuild 为 true，调用方应做一次全量重建。
    /// 2：引入 dispatches（handles / dispatch:T / conduit）边。
    /// 3：files.roslyn_at 列；Roslyn 精确语义覆盖（符号限定名带参数类型）。
    /// 4：架构事实表 arch_nodes / arch_edges / arch_outbound / arch_conflicts（容器图）。
    /// 5：git 行为统计表 git_file_stats / git_coupling / git_meta（DSM 叠加）。
    /// 6：入口与路由匹配表 entry_points / route_matches（入口流程）。
    /// 7：声明规则表 arch_rules（NetArchTest 还原）。（AI 叙述缓存后来挪到单独的 narratives.db，旧库里残留的空 narratives 表无害。）
    /// 8：TS / TSX / JS 解析规则变了：JSX 渲染边（calls + via=render）、按 import 绑定解析调用、Next.js 页面入口指向默认导出组件；
    ///    嵌套仓库纳入索引、Roslyn 一个仓库一个解决方案。旧库没有这些边，增量索引只重解析改过的文件补不齐，必须全量重建。
    /// 9：加入 Java；TS/JS 认 CommonJS require / exports、memo / forwardRef、onX 事件边、this.字段 按类型；Go / Python 按字段、链式、局部变量类型解析，
    ///    外部调用带包名；C# 的 tree-sitter 解析修正；Roslyn 绑不上的行保留 ≥0.7 的启发式调用边。已有语言的边都变了，必须全量重建。
    /// 10：edges.pos / edges.ctx——调用名的字符偏移与所在控制结构（循环 / 条件 / try / catch / 回调），调用大纲按偏移排序并在行尾标注。旧边没有这两列的值，必须全量重建。
    /// </summary>
    public const long CurrentIndexVersion = 10;

    /// <summary>库是旧版本建的，内容与当前解析规则不一致。</summary>
    public bool NeedsRebuild { get; private set; }

    private void Initialize()
    {
        Execute("PRAGMA foreign_keys=ON;");
        var hadTables = Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'files'") > 0;
        Execute(Schema.Ddl);
        Migrate();
        var version = Scalar<long>("PRAGMA user_version");
        if (!hadTables || version == 0 && !hadTables) MarkCurrentVersion();
        else NeedsRebuild = version != CurrentIndexVersion;
    }

    /// <summary>旧库补列。CREATE TABLE IF NOT EXISTS 不会改已有表，这里按列是否存在逐个补。</summary>
    private void Migrate()
    {
        var fileColumns = Query<string>("SELECT name FROM pragma_table_info('files')");
        if (!fileColumns.Contains("roslyn_at")) Execute("ALTER TABLE files ADD COLUMN roslyn_at TEXT");
        var edgeColumns = Query<string>("SELECT name FROM pragma_table_info('edges')");
        if (!edgeColumns.Contains("pos")) Execute("ALTER TABLE edges ADD COLUMN pos INTEGER");
        if (!edgeColumns.Contains("ctx")) Execute("ALTER TABLE edges ADD COLUMN ctx TEXT");
    }

    /// <summary>全量重建完成后调用，把库标记为当前版本。</summary>
    public void MarkCurrentVersion()
    {
        Execute($"PRAGMA user_version = {CurrentIndexVersion}");
        NeedsRebuild = false;
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = CreateCommand(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = CreateCommand(sql, parameters);
        var result = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(result!, typeof(T));
    }

    /// <summary>执行查询并返回每行的第一列。</summary>
    public List<T> Query<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = CreateCommand(sql, parameters);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add((T)Convert.ChangeType(reader.GetValue(0), typeof(T)));
        return list;
    }

    /// <summary>创建命令并自动挂上当前批次的事务（Microsoft.Data.Sqlite 要求连接有事务时命令必须显式关联）。</summary>
    public SqliteCommand CreateCommand(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>开始一个写入批次（一个 SQLite 事务）。不调用 Commit 就 Dispose 会回滚。不支持嵌套。</summary>
    public Batch BeginBatch()
    {
        if (_transaction is not null) throw new InvalidOperationException("已有未结束的批次，不支持嵌套。");
        _transaction = Connection.BeginTransaction();
        return new Batch(this);
    }

    public sealed class Batch(IndexDatabase db) : IDisposable
    {
        private bool _finished;

        public void Commit()
        {
            if (_finished) return;
            db._transaction!.Commit();
            Finish();
        }

        public void Dispose()
        {
            if (_finished) return;
            db._transaction?.Rollback();
            Finish();
        }

        private void Finish()
        {
            db._transaction?.Dispose();
            db._transaction = null;
            _finished = true;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        Connection.Dispose();
    }
}
