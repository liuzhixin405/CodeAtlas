# CodeAtlas 里程碑 1：Core + tree-sitter + CLI 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把一个 TypeScript / JavaScript / C# 仓库用 tree-sitter 索引进 SQLite，并能用 CLI 查询任意符号的调用者与被调用者。

**Architecture:** `CodeAtlas.Core` 持有领域模型、SQLite 读写和启发式引用解析器；`CodeAtlas.Indexing.TreeSitter` 用"每语言一对 .scm 查询 + 薄适配器"把源文件变成符号和未解析引用；`CodeAtlas.Cli` 提供 `index` / `query` / `search` 三个子命令。解析并行，写库串行分批，引用解析在全部符号入库后统一进行。

**Tech Stack:** .NET 10、TreeSitter.DotNet 1.3.0（内置 TypeScript / TSX / JavaScript / C# grammar）、Microsoft.Data.Sqlite 10.0.0（SQLite 3.49，FTS5）、xunit。

**Spec:** `docs/superpowers/specs/2026-10-13-codeatlas-design.md`（本计划实现其第 2、3、4.1、4.2、4.3 节和里程碑 1）

## Global Constraints

- 目标框架 `net10.0`，`Nullable` 和 `ImplicitUsings` 启用，所有项目通过 `Directory.Build.props` 统一。
- 只允许的 NuGet 依赖：`TreeSitter.DotNet 1.3.0`、`Microsoft.Data.Sqlite 10.0.0`、`xunit 2.9.x`、`xunit.runner.visualstudio`、`Microsoft.NET.Test.Sdk`。CLI 参数解析手写，不引入 System.CommandLine。
- 索引库默认路径 `<仓库>/.codeatlas/index.db`；`.codeatlas/` 必须在被索引仓库的 `.gitignore` 中（CLI 首次索引时提示，不自动修改用户仓库）。
- 符号 kind 和边 kind 在数据库中存为小写蛇形字符串（`class`、`type_alias`、`calls`）。
- 行号列号存 1 基；tree-sitter 返回的 Row / Column 是 0 基，入库前加 1。
- 边的 confidence：同文件唯一 0.9、import 范围唯一 0.7、全仓库唯一 0.5、多候选各 0.3、外部符号 0.5。
- 第一版 tree-sitter 语言：TypeScript、TSX、JavaScript、C#。Python / Go / Java 在后续计划中按同样模式加入；SQL 不在内置 grammar 中，不做。
- 本计划的 C# 支持是 tree-sitter 版，仅为 Roslyn 不可用时的兜底和里程碑 3 之前的过渡；精度以启发式为准。
- 对 spec 的两处补充（已反映到本计划，执行时同步写回 spec）：新增符号 kind `module`（每个文件一个根符号，承载文件级引用与 import 边）；新增 `imports` 表保存原始 import 记录供解析器使用。

## Review Focus

以下输入 spec 没有明说、但真实仓库一定会碰到。每条都已在所属任务中加了测试：

1. **文件编码与 BOM**：带 UTF-8 BOM 或 GBK 的源文件不能让解析崩溃，也不能让列号偏移。→ Task 8 的 `Indexer_ReadsBomFile_WithoutOffsetShift`。
2. **语法错误的文件**：AI 生成的半成品代码常有语法错误，tree-sitter 会产出 ERROR 节点；必须仍能提取完整的定义。→ Task 5 的 `Parse_FileWithSyntaxError_StillExtractsValidSymbols`。
3. **同名符号大量重复**：`Dispose`、`Handle`、`Execute` 在大仓库里有几百个定义；多候选不能产生几百条边把库撑爆。→ Task 4 的 `Resolve_ManyCandidates_CapsAt20Edges`。
4. **删除和重命名的文件**：磁盘上消失的文件必须从库中移除，指向它们的边变为悬空而不是报错。→ Task 8 的 `Indexer_RemovedFile_DeletesSymbolsAndDanglesEdges`。
5. **空文件与超大文件**：0 字节文件和 5MB 的生成代码不能卡死索引。→ Task 8 的 `Indexer_EmptyAndLargeFiles_AreIndexedOrSkipped`（超过 2MB 的文件标记 `skipped`）。

---

## File Structure

```
Directory.Build.props
CodeAtlas.sln
src/CodeAtlas.Core/
  CodeAtlas.Core.csproj
  Model/Enums.cs                     SymbolKind / EdgeKind / IndexSource / FileStatus / Direction + DbNames 转换
  Model/Records.cs                   FileRecord / SymbolRecord / EdgeRecord / NewEdge
  Model/Parsed.cs                    ParsedSymbol / ParsedReference / ParsedImport / ParsedFile（解析器输出，无数据库 id）
  Storage/Schema.cs                  DDL 常量
  Storage/IndexDatabase.cs           打开连接、PRAGMA、建表
  Storage/IndexWriter.cs             文件 upsert、符号/边/import 写入、删除文件内容、外部符号、悬空边重挂
  Storage/IndexReader.cs             FTS 搜索、按名/限定名查、邻居、文件大纲
  Resolution/IEdgeResolver.cs        IEdgeResolver + ResolveContext + PendingReference
  Resolution/HeuristicEdgeResolver.cs 四级启发式解析
  Hashing/FileHasher.cs              SHA-256
src/CodeAtlas.Indexing.TreeSitter/
  CodeAtlas.Indexing.TreeSitter.csproj
  ILanguageAdapter.cs
  LanguageAdapterBase.cs             跑查询、算包含关系与限定名、产出 ParsedFile
  QueryResources.cs                  读取嵌入的 .scm
  Adapters/TypeScriptAdapter.cs
  Adapters/TsxAdapter.cs
  Adapters/JavaScriptAdapter.cs
  Adapters/CSharpAdapter.cs
  Queries/typescript.definitions.scm
  Queries/typescript.references.scm
  Queries/javascript.definitions.scm
  Queries/javascript.references.scm
  Queries/csharp.definitions.scm
  Queries/csharp.references.scm
  RepositoryScanner.cs               枚举文件、排除目录、扩展名到语言
  TreeSitterIndexer.cs               增量判断、并行解析、分批写入、调用解析器、重挂悬空边
src/CodeAtlas.Cli/
  CodeAtlas.Cli.csproj
  Program.cs                         参数解析与分发
  Commands/IndexCommand.cs
  Commands/QueryCommand.cs
  Commands/SearchCommand.cs
tests/CodeAtlas.Core.Tests/
  CodeAtlas.Core.Tests.csproj
  IndexDatabaseTests.cs
  IndexWriterTests.cs
  IndexReaderTests.cs
  HeuristicEdgeResolverTests.cs
tests/CodeAtlas.Indexing.TreeSitter.Tests/
  CodeAtlas.Indexing.TreeSitter.Tests.csproj
  TypeScriptAdapterTests.cs
  JavaScriptAdapterTests.cs
  CSharpAdapterTests.cs
  TreeSitterIndexerTests.cs
  Fixtures/ts/svc.ts, Fixtures/ts/app.ts, Fixtures/ts/broken.ts
  Fixtures/js/lib.js
  Fixtures/cs/OrderService.cs
  Fixtures/repo/src/svc.ts, Fixtures/repo/src/app.ts, Fixtures/repo/src/util/format.ts
tests/CodeAtlas.Cli.Tests/
  CodeAtlas.Cli.Tests.csproj
  CliEndToEndTests.cs
docs/acceptance.md                   在真实仓库上的验收记录
```

---

### Task 1: 解决方案骨架与索引库建表

**Files:**
- Create: `Directory.Build.props`
- Create: `CodeAtlas.sln`
- Create: `src/CodeAtlas.Core/CodeAtlas.Core.csproj`
- Create: `src/CodeAtlas.Core/Storage/Schema.cs`
- Create: `src/CodeAtlas.Core/Storage/IndexDatabase.cs`
- Create: `tests/CodeAtlas.Core.Tests/CodeAtlas.Core.Tests.csproj`
- Test: `tests/CodeAtlas.Core.Tests/IndexDatabaseTests.cs`

**Interfaces:**
- Produces: `IndexDatabase.Open(string path)`、`IndexDatabase.OpenInMemory()`、`IndexDatabase.Connection : SqliteConnection`、`Execute(string sql, params (string, object?)[])`、`Scalar<T>(...)`、`Query<T>(...)`（取每行第一列）、`CreateCommand(...)`（自动挂当前批次事务）、`BeginBatch() : IndexDatabase.Batch`（`Commit()`；未 Commit 即 Dispose 则回滚；不支持嵌套）

- [ ] **Step 1: 创建解决方案与项目**

```bash
cd D:/github/CodeAtlas
cat > Directory.Build.props <<'EOF'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <RootNamespace>$(MSBuildProjectName.Replace(".Tests",""))</RootNamespace>
  </PropertyGroup>
</Project>
EOF
dotnet new sln -n CodeAtlas
dotnet new classlib -n CodeAtlas.Core -o src/CodeAtlas.Core
rm src/CodeAtlas.Core/Class1.cs
dotnet add src/CodeAtlas.Core package Microsoft.Data.Sqlite --version 10.0.0
dotnet new xunit -n CodeAtlas.Core.Tests -o tests/CodeAtlas.Core.Tests
rm tests/CodeAtlas.Core.Tests/UnitTest1.cs
dotnet add tests/CodeAtlas.Core.Tests reference src/CodeAtlas.Core
dotnet sln add src/CodeAtlas.Core tests/CodeAtlas.Core.Tests
```

- [ ] **Step 2: 写失败的测试**

`tests/CodeAtlas.Core.Tests/IndexDatabaseTests.cs`:

```csharp
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexDatabaseTests
{
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
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：编译失败，`IndexDatabase` 不存在。

- [ ] **Step 4: 写 Schema 与 IndexDatabase**

`src/CodeAtlas.Core/Storage/Schema.cs`:

```csharp
namespace CodeAtlas.Core.Storage;

internal static class Schema
{
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS files (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,
            language TEXT NOT NULL,
            hash TEXT NOT NULL,
            size INTEGER NOT NULL,
            indexed_at TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'ok'
        );

        CREATE TABLE IF NOT EXISTS symbols (
            id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            signature TEXT,
            start_line INTEGER NOT NULL,
            start_col INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            end_col INTEGER NOT NULL,
            parent_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
            visibility TEXT,
            source TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_symbols_file ON symbols(file_id);
        CREATE INDEX IF NOT EXISTS ix_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS ix_symbols_qname ON symbols(qualified_name);
        CREATE INDEX IF NOT EXISTS ix_symbols_parent ON symbols(parent_id);

        CREATE TABLE IF NOT EXISTS edges (
            id INTEGER PRIMARY KEY,
            from_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
            to_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL,
            to_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            source TEXT NOT NULL,
            confidence REAL NOT NULL,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            line INTEGER NOT NULL,
            via TEXT,
            evidence_line INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_edges_from ON edges(from_id);
        CREATE INDEX IF NOT EXISTS ix_edges_to ON edges(to_id);
        CREATE INDEX IF NOT EXISTS ix_edges_dangling ON edges(to_name) WHERE to_id IS NULL;

        CREATE TABLE IF NOT EXISTS imports (
            id INTEGER PRIMARY KEY,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            target TEXT NOT NULL,
            is_path INTEGER NOT NULL,
            line INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_imports_file ON imports(file_id);

        CREATE TABLE IF NOT EXISTS di_registrations (
            id INTEGER PRIMARY KEY,
            interface_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
            impl_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
            file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            line INTEGER NOT NULL,
            lifetime TEXT,
            conditional INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS summaries (
            symbol_id INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
            summary TEXT NOT NULL,
            source_hash TEXT NOT NULL,
            model TEXT NOT NULL,
            created_at TEXT NOT NULL,
            embedding BLOB
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
            name, qualified_name,
            content='symbols', content_rowid='id',
            tokenize = "unicode61 tokenchars '_'"
        );
        CREATE TRIGGER IF NOT EXISTS symbols_ai AFTER INSERT ON symbols BEGIN
            INSERT INTO symbols_fts(rowid, name, qualified_name) VALUES (new.id, new.name, new.qualified_name);
        END;
        CREATE TRIGGER IF NOT EXISTS symbols_ad AFTER DELETE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, qualified_name) VALUES ('delete', old.id, old.name, old.qualified_name);
        END;
        CREATE TRIGGER IF NOT EXISTS symbols_au AFTER UPDATE ON symbols BEGIN
            INSERT INTO symbols_fts(symbols_fts, rowid, name, qualified_name) VALUES ('delete', old.id, old.name, old.qualified_name);
            INSERT INTO symbols_fts(rowid, name, qualified_name) VALUES (new.id, new.name, new.qualified_name);
        END;
        """;
}
```

`src/CodeAtlas.Core/Storage/IndexDatabase.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

/// <summary>一个仓库的索引库。单连接，调用方负责串行使用。</summary>
public sealed class IndexDatabase : IDisposable
{
    public SqliteConnection Connection { get; }

    private IndexDatabase(SqliteConnection connection) => Connection = connection;

    public static IndexDatabase Open(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
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

    private void Initialize()
    {
        Execute("PRAGMA foreign_keys=ON;");
        Execute(Schema.Ddl);
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

    private SqliteTransaction? _transaction;

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
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：5 个测试通过。

- [ ] **Step 6: 提交**

```bash
git add Directory.Build.props CodeAtlas.sln src/CodeAtlas.Core tests/CodeAtlas.Core.Tests
git commit -m "feat(core): 解决方案骨架与 SQLite 索引库建表"
```

---

### Task 2: 领域模型与 IndexWriter

**Files:**
- Create: `src/CodeAtlas.Core/Model/Enums.cs`
- Create: `src/CodeAtlas.Core/Model/Records.cs`
- Create: `src/CodeAtlas.Core/Model/Parsed.cs`
- Create: `src/CodeAtlas.Core/Storage/IndexWriter.cs`
- Test: `tests/CodeAtlas.Core.Tests/IndexWriterTests.cs`

**Interfaces:**
- Consumes: `IndexDatabase`（Task 1）
- Produces:
  - 枚举 `SymbolKind { Module, Namespace, Class, Interface, Struct, Enum, Function, Method, Property, Field, Variable, TypeAlias, External }`
  - 枚举 `EdgeKind { Contains, Calls, References, Inherits, Implements, Imports, Dispatches }`
  - 枚举 `IndexSource { TreeSitter, Roslyn }`、`FileStatus { Ok, ParseFailed, Skipped }`、`Direction { Outgoing, Incoming, Both }`
  - `static class DbNames { string ToDb(SymbolKind) ; SymbolKind ParseSymbolKind(string); string ToDb(EdgeKind); EdgeKind ParseEdgeKind(string); string ToDb(IndexSource); IndexSource ParseSource(string); string ToDb(FileStatus); FileStatus ParseStatus(string) }`
  - `record FileRecord(long Id, string Path, string Language, string Hash, long Size, DateTimeOffset IndexedAt, FileStatus Status)`
  - `record SymbolRecord(long Id, long FileId, SymbolKind Kind, string Name, string QualifiedName, string? Signature, int StartLine, int StartCol, int EndLine, int EndCol, long? ParentId, string? Visibility, IndexSource Source)`
  - `record EdgeRecord(long Id, long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence, long FileId, int Line, string? Via, int? EvidenceLine)`
  - `record NewEdge(long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence, long FileId, int Line, string? Via = null, int? EvidenceLine = null)`
  - `record ParsedSymbol(SymbolKind Kind, string Name, string QualifiedName, string? Signature, int StartLine, int StartCol, int EndLine, int EndCol, int? ParentIndex, string? Visibility)`
  - `record ParsedReference(int FromIndex, string Name, EdgeKind Kind, int Line, int Col)`
  - `record ParsedImport(string Target, bool IsPath, int Line)`
  - `record ParsedFile(string RelativePath, string Language, IReadOnlyList<ParsedSymbol> Symbols, IReadOnlyList<ParsedReference> References, IReadOnlyList<ParsedImport> Imports)`，约定 `Symbols[0]` 是该文件的 `Module` 符号
  - `class IndexWriter(IndexDatabase db)`：`long UpsertFile(string path, string language, string hash, long size, FileStatus status)`、`void DeleteFileContents(long fileId)`、`void DeleteFile(long fileId)`、`IReadOnlyList<long> WriteSymbols(long fileId, IReadOnlyList<ParsedSymbol> symbols, IndexSource source)`、`void WriteImports(long fileId, IReadOnlyList<ParsedImport> imports)`、`void WriteEdges(IEnumerable<NewEdge> edges)`、`long GetOrCreateExternal(string name)`、`int ReattachDanglingEdges()`

- [ ] **Step 1: 写失败的测试**

`tests/CodeAtlas.Core.Tests/IndexWriterTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexWriterTests
{
    private static ParsedSymbol Module(string path) =>
        new(SymbolKind.Module, Path.GetFileName(path), path, null, 1, 1, 100, 1, null, null);

    [Fact]
    public void UpsertFile_InsertsThenUpdatesSameRow()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);

        var id1 = w.UpsertFile("src/a.ts", "typescript", "h1", 10, FileStatus.Ok);
        var id2 = w.UpsertFile("src/a.ts", "typescript", "h2", 12, FileStatus.Ok);

        Assert.Equal(id1, id2);
        Assert.Equal("h2", db.Scalar<string>("SELECT hash FROM files WHERE id = $id", ("$id", id1)));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public void WriteSymbols_AssignsIds_ParentLinks_AndContainsEdges()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var fileId = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var symbols = new List<ParsedSymbol>
        {
            Module("src/a.ts"),
            new(SymbolKind.Class, "Svc", "src/a.ts:Svc", "class Svc", 2, 1, 10, 2, 0, "public"),
            new(SymbolKind.Method, "find", "src/a.ts:Svc.find", "find(id: number)", 3, 3, 5, 4, 1, null),
        };

        var ids = w.WriteSymbols(fileId, symbols, IndexSource.TreeSitter);

        Assert.Equal(3, ids.Count);
        Assert.Equal(ids[0], db.Scalar<long>("SELECT parent_id FROM symbols WHERE id = $id", ("$id", ids[1])));
        Assert.Equal(ids[1], db.Scalar<long>("SELECT parent_id FROM symbols WHERE id = $id", ("$id", ids[2])));
        Assert.Equal("type_alias", DbNames.ToDb(SymbolKind.TypeAlias));
        Assert.Equal(2L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'contains'"));
        Assert.Equal(1L, db.Scalar<long>(
            "SELECT COUNT(*) FROM edges WHERE kind = 'contains' AND from_id = $p AND to_id = $c AND confidence = 1.0",
            ("$p", ids[1]), ("$c", ids[2])));
    }

    [Fact]
    public void DeleteFileContents_RemovesSymbolsImportsAndOutgoingEdges_DanglesIncoming()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/b.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b, [Module("src/b.ts"), new(SymbolKind.Function, "helper", "src/b.ts:helper", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        w.WriteImports(a, [new ParsedImport("./b", true, 1)]);
        // a.main -> b.helper
        w.WriteEdges([new NewEdge(aIds[1], bIds[1], "src/b.ts:helper", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 3)]);
        // b.helper -> a.main
        w.WriteEdges([new NewEdge(bIds[1], aIds[1], "src/a.ts:main", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, b, 3)]);

        w.DeleteFileContents(a);

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM symbols WHERE file_id = $f", ("$f", a)));
        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM imports WHERE file_id = $f", ("$f", a)));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM files WHERE id = $f", ("$f", a)));
        // b.helper -> a.main 变为悬空，保留 to_name
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE from_id = $b AND to_id IS NULL AND to_name = 'src/a.ts:main'", ("$b", bIds[1])));
        // a.main -> b.helper 随 a 的符号级联删除
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM edges WHERE kind = 'calls'"));
    }

    [Fact]
    public void ReattachDanglingEdges_RelinksByQualifiedName_WhenUnique()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/b.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b, [Module("src/b.ts"), new(SymbolKind.Function, "helper", "src/b.ts:helper", null, 2, 1, 4, 1, 0, null)], IndexSource.TreeSitter);
        w.WriteEdges([new NewEdge(bIds[1], aIds[1], "src/a.ts:main", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, b, 3)]);
        w.DeleteFileContents(a);
        var newAIds = w.WriteSymbols(a, [Module("src/a.ts"), new(SymbolKind.Function, "main", "src/a.ts:main", null, 5, 1, 9, 1, 0, null)], IndexSource.TreeSitter);

        var reattached = w.ReattachDanglingEdges();

        Assert.Equal(1, reattached);
        Assert.Equal(newAIds[1], db.Scalar<long>("SELECT to_id FROM edges WHERE from_id = $b", ("$b", bIds[1])));
    }

    [Fact]
    public void GetOrCreateExternal_ReturnsSameIdForSameName()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);

        var id1 = w.GetOrCreateExternal("console.log");
        var id2 = w.GetOrCreateExternal("console.log");
        var id3 = w.GetOrCreateExternal("fetch");

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Equal("external", db.Scalar<string>("SELECT kind FROM symbols WHERE id = $id", ("$id", id1)));
        Assert.Equal("<external>", db.Scalar<string>("SELECT f.path FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id = $id", ("$id", id1)));
    }

    [Fact]
    public void DeleteFile_RemovesFileRow()
    {
        using var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/a.ts", "typescript", "h", 10, FileStatus.Ok);
        w.WriteSymbols(a, [Module("src/a.ts")], IndexSource.TreeSitter);

        w.DeleteFile(a);

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM symbols"));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：编译失败，`IndexWriter`、`ParsedSymbol` 等不存在。

- [ ] **Step 3: 写模型**

`src/CodeAtlas.Core/Model/Enums.cs`:

```csharp
namespace CodeAtlas.Core.Model;

public enum SymbolKind { Module, Namespace, Class, Interface, Struct, Enum, Function, Method, Property, Field, Variable, TypeAlias, External }

public enum EdgeKind { Contains, Calls, References, Inherits, Implements, Imports, Dispatches }

public enum IndexSource { TreeSitter, Roslyn }

public enum FileStatus { Ok, ParseFailed, Skipped }

public enum Direction { Outgoing, Incoming, Both }

/// <summary>枚举与数据库中小写蛇形字符串的双向转换。</summary>
public static class DbNames
{
    private static readonly Dictionary<SymbolKind, string> SymbolKindToDb = new()
    {
        [SymbolKind.Module] = "module", [SymbolKind.Namespace] = "namespace", [SymbolKind.Class] = "class",
        [SymbolKind.Interface] = "interface", [SymbolKind.Struct] = "struct", [SymbolKind.Enum] = "enum",
        [SymbolKind.Function] = "function", [SymbolKind.Method] = "method", [SymbolKind.Property] = "property",
        [SymbolKind.Field] = "field", [SymbolKind.Variable] = "variable", [SymbolKind.TypeAlias] = "type_alias",
        [SymbolKind.External] = "external",
    };
    private static readonly Dictionary<string, SymbolKind> DbToSymbolKind = SymbolKindToDb.ToDictionary(p => p.Value, p => p.Key);

    private static readonly Dictionary<EdgeKind, string> EdgeKindToDb = new()
    {
        [EdgeKind.Contains] = "contains", [EdgeKind.Calls] = "calls", [EdgeKind.References] = "references",
        [EdgeKind.Inherits] = "inherits", [EdgeKind.Implements] = "implements", [EdgeKind.Imports] = "imports",
        [EdgeKind.Dispatches] = "dispatches",
    };
    private static readonly Dictionary<string, EdgeKind> DbToEdgeKind = EdgeKindToDb.ToDictionary(p => p.Value, p => p.Key);

    public static string ToDb(SymbolKind kind) => SymbolKindToDb[kind];
    public static SymbolKind ParseSymbolKind(string db) => DbToSymbolKind[db];
    public static string ToDb(EdgeKind kind) => EdgeKindToDb[kind];
    public static EdgeKind ParseEdgeKind(string db) => DbToEdgeKind[db];
    public static string ToDb(IndexSource source) => source == IndexSource.TreeSitter ? "treesitter" : "roslyn";
    public static IndexSource ParseSource(string db) => db == "roslyn" ? IndexSource.Roslyn : IndexSource.TreeSitter;
    public static string ToDb(FileStatus status) => status switch
    {
        FileStatus.Ok => "ok",
        FileStatus.ParseFailed => "parse_failed",
        _ => "skipped",
    };
    public static FileStatus ParseStatus(string db) => db switch
    {
        "ok" => FileStatus.Ok,
        "parse_failed" => FileStatus.ParseFailed,
        _ => FileStatus.Skipped,
    };
}
```

`src/CodeAtlas.Core/Model/Records.cs`:

```csharp
namespace CodeAtlas.Core.Model;

public sealed record FileRecord(long Id, string Path, string Language, string Hash, long Size, DateTimeOffset IndexedAt, FileStatus Status);

public sealed record SymbolRecord(
    long Id, long FileId, SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, long? ParentId, string? Visibility, IndexSource Source);

public sealed record EdgeRecord(
    long Id, long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via, int? EvidenceLine);

/// <summary>待写入的边；ToId 为 null 表示悬空。</summary>
public sealed record NewEdge(
    long FromId, long? ToId, string ToName, EdgeKind Kind, IndexSource Source, double Confidence,
    long FileId, int Line, string? Via = null, int? EvidenceLine = null);
```

`src/CodeAtlas.Core/Model/Parsed.cs`:

```csharp
namespace CodeAtlas.Core.Model;

/// <summary>解析器产出的符号，尚无数据库 id；ParentIndex 指向同一 ParsedFile.Symbols 的下标。</summary>
public sealed record ParsedSymbol(
    SymbolKind Kind, string Name, string QualifiedName, string? Signature,
    int StartLine, int StartCol, int EndLine, int EndCol, int? ParentIndex, string? Visibility);

/// <summary>未解析的引用。FromIndex 是包含该引用的符号下标（文件级为 0，即 Module）。</summary>
public sealed record ParsedReference(int FromIndex, string Name, EdgeKind Kind, int Line, int Col);

/// <summary>import / using 记录。IsPath 为 true 表示相对路径（TS/JS），否则是命名空间（C#）。</summary>
public sealed record ParsedImport(string Target, bool IsPath, int Line);

/// <summary>一个文件的解析结果。约定 Symbols[0] 是该文件的 Module 符号。</summary>
public sealed record ParsedFile(
    string RelativePath, string Language,
    IReadOnlyList<ParsedSymbol> Symbols,
    IReadOnlyList<ParsedReference> References,
    IReadOnlyList<ParsedImport> Imports);
```

- [ ] **Step 4: 写 IndexWriter**

`src/CodeAtlas.Core/Storage/IndexWriter.cs`:

```csharp
using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Storage;

public sealed class IndexWriter(IndexDatabase db)
{
    public const string ExternalFilePath = "<external>";

    public long UpsertFile(string path, string language, string hash, long size, FileStatus status)
    {
        return db.Scalar<long>("""
            INSERT INTO files (path, language, hash, size, indexed_at, status)
            VALUES ($path, $language, $hash, $size, $now, $status)
            ON CONFLICT(path) DO UPDATE SET
                language = excluded.language, hash = excluded.hash, size = excluded.size,
                indexed_at = excluded.indexed_at, status = excluded.status
            RETURNING id;
            """,
            ("$path", path), ("$language", language), ("$hash", hash), ("$size", size),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$status", DbNames.ToDb(status)));
    }

    /// <summary>删除文件的符号、import 和从它出发的边（级联）；指向它的边变为悬空（to_id 置 NULL）。文件行保留。</summary>
    public void DeleteFileContents(long fileId)
    {
        db.Execute("DELETE FROM symbols WHERE file_id = $f", ("$f", fileId));
        db.Execute("DELETE FROM imports WHERE file_id = $f", ("$f", fileId));
    }

    public void DeleteFile(long fileId)
    {
        DeleteFileContents(fileId);
        db.Execute("DELETE FROM files WHERE id = $f", ("$f", fileId));
    }

    /// <summary>按顺序写入符号，返回与输入下标对应的数据库 id；同时写 contains 边。</summary>
    public IReadOnlyList<long> WriteSymbols(long fileId, IReadOnlyList<ParsedSymbol> symbols, IndexSource source)
    {
        var ids = new long[symbols.Count];
        using var insert = db.CreateCommand("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, signature, start_line, start_col, end_line, end_col, parent_id, visibility, source)
            VALUES ($file, $kind, $name, $qname, $sig, $sl, $sc, $el, $ec, $parent, $vis, $source)
            RETURNING id;
            """);
        var p = insert.Parameters;
        p.AddWithValue("$file", fileId); p.AddWithValue("$kind", ""); p.AddWithValue("$name", ""); p.AddWithValue("$qname", "");
        p.AddWithValue("$sig", DBNull.Value); p.AddWithValue("$sl", 0); p.AddWithValue("$sc", 0); p.AddWithValue("$el", 0); p.AddWithValue("$ec", 0);
        p.AddWithValue("$parent", DBNull.Value); p.AddWithValue("$vis", DBNull.Value); p.AddWithValue("$source", DbNames.ToDb(source));

        var containsEdges = new List<NewEdge>();
        for (var i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            if (s.ParentIndex is { } pi && (pi < 0 || pi >= i))
                throw new InvalidOperationException($"符号 {s.QualifiedName} 的 ParentIndex={pi} 必须指向更早的符号。");
            p["$kind"].Value = DbNames.ToDb(s.Kind);
            p["$name"].Value = s.Name;
            p["$qname"].Value = s.QualifiedName;
            p["$sig"].Value = (object?)s.Signature ?? DBNull.Value;
            p["$sl"].Value = s.StartLine; p["$sc"].Value = s.StartCol; p["$el"].Value = s.EndLine; p["$ec"].Value = s.EndCol;
            p["$parent"].Value = s.ParentIndex is { } pidx ? ids[pidx] : DBNull.Value;
            p["$vis"].Value = (object?)s.Visibility ?? DBNull.Value;
            ids[i] = (long)insert.ExecuteScalar()!;
            if (s.ParentIndex is { } parentIdx)
                containsEdges.Add(new NewEdge(ids[parentIdx], ids[i], s.QualifiedName, EdgeKind.Contains, source, 1.0, fileId, s.StartLine));
        }
        WriteEdges(containsEdges);
        return ids;
    }

    public void WriteImports(long fileId, IReadOnlyList<ParsedImport> imports)
    {
        using var cmd = db.CreateCommand("INSERT INTO imports (file_id, target, is_path, line) VALUES ($f, $t, $p, $l)");
        cmd.Parameters.AddWithValue("$f", fileId); cmd.Parameters.AddWithValue("$t", ""); cmd.Parameters.AddWithValue("$p", 0); cmd.Parameters.AddWithValue("$l", 0);
        foreach (var imp in imports)
        {
            cmd.Parameters["$t"].Value = imp.Target; cmd.Parameters["$p"].Value = imp.IsPath ? 1 : 0; cmd.Parameters["$l"].Value = imp.Line;
            cmd.ExecuteNonQuery();
        }
    }

    public void WriteEdges(IEnumerable<NewEdge> edges)
    {
        using var cmd = db.CreateCommand("""
            INSERT INTO edges (from_id, to_id, to_name, kind, source, confidence, file_id, line, via, evidence_line)
            VALUES ($from, $to, $toName, $kind, $source, $conf, $file, $line, $via, $ev)
            """);
        var p = cmd.Parameters;
        foreach (var n in new[] { "$from", "$to", "$toName", "$kind", "$source", "$conf", "$file", "$line", "$via", "$ev" }) p.AddWithValue(n, DBNull.Value);
        foreach (var e in edges)
        {
            p["$from"].Value = e.FromId; p["$to"].Value = (object?)e.ToId ?? DBNull.Value; p["$toName"].Value = e.ToName;
            p["$kind"].Value = DbNames.ToDb(e.Kind); p["$source"].Value = DbNames.ToDb(e.Source); p["$conf"].Value = e.Confidence;
            p["$file"].Value = e.FileId; p["$line"].Value = e.Line; p["$via"].Value = (object?)e.Via ?? DBNull.Value;
            p["$ev"].Value = (object?)e.EvidenceLine ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>外部符号（仓库外的目标）。按名字去重，挂在伪文件 &lt;external&gt; 下。</summary>
    public long GetOrCreateExternal(string name)
    {
        var qname = "external:" + name;
        var existing = db.Query<long>("SELECT id FROM symbols WHERE kind = 'external' AND qualified_name = $q LIMIT 1", ("$q", qname));
        if (existing.Count > 0) return existing[0];
        var fileId = db.Query<long>("SELECT id FROM files WHERE path = $p", ("$p", ExternalFilePath)) is { Count: > 0 } f
            ? f[0]
            : UpsertFile(ExternalFilePath, "*", "", 0, FileStatus.Ok);
        return db.Scalar<long>("""
            INSERT INTO symbols (file_id, kind, name, qualified_name, start_line, start_col, end_line, end_col, source)
            VALUES ($f, 'external', $n, $q, 0, 0, 0, 0, 'treesitter') RETURNING id;
            """, ("$f", fileId), ("$n", name), ("$q", qname));
    }

    /// <summary>把悬空边按 to_name 重新挂到唯一匹配的符号上。返回重挂数量。</summary>
    public int ReattachDanglingEdges()
    {
        return db.Execute("""
            UPDATE edges SET to_id = (
                SELECT s.id FROM symbols s WHERE s.qualified_name = edges.to_name
            )
            WHERE to_id IS NULL
              AND (SELECT COUNT(*) FROM symbols s WHERE s.qualified_name = edges.to_name) = 1
            """);
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：全部通过（Task 1 的 5 个加本任务的 6 个）。

- [ ] **Step 6: 提交**

```bash
git add src/CodeAtlas.Core tests/CodeAtlas.Core.Tests
git commit -m "feat(core): 领域模型与 IndexWriter（符号、边、import、外部符号、悬空边重挂）"
```

---

### Task 3: IndexReader

**Files:**
- Create: `src/CodeAtlas.Core/Storage/IndexReader.cs`
- Test: `tests/CodeAtlas.Core.Tests/IndexReaderTests.cs`

**Interfaces:**
- Consumes: `IndexDatabase`、`IndexWriter`、模型（Task 1、2）
- Produces: `class IndexReader(IndexDatabase db)`：
  - `IReadOnlyList<SymbolRecord> SearchSymbols(string query, int limit = 50)` FTS 前缀搜索
  - `SymbolRecord? GetSymbol(long id)`
  - `IReadOnlyList<SymbolRecord> FindByName(string name)` 精确名
  - `IReadOnlyList<SymbolRecord> FindByQualifiedName(string qualifiedName)`
  - `IReadOnlyList<SymbolRecord> GetSymbolsInFile(long fileId)`
  - `IReadOnlyList<SymbolRecord> GetAllSymbols()`
  - `IReadOnlyList<Neighbor> GetNeighbors(long symbolId, Direction direction, IReadOnlySet<EdgeKind>? kinds = null)`，`record Neighbor(EdgeRecord Edge, SymbolRecord? Other)`，Other 为 null 表示悬空
  - `FileRecord? GetFile(string path)`、`IReadOnlyList<FileRecord> GetAllFiles()`
  - `IReadOnlyList<(long FileId, string Target, bool IsPath)> GetAllImports()`
  - `IReadOnlyList<EdgeRecord> GetDanglingEdges()`

- [ ] **Step 1: 写失败的测试**

`tests/CodeAtlas.Core.Tests/IndexReaderTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class IndexReaderTests
{
    private static (IndexDatabase Db, long FileA, long FileB, IReadOnlyList<long> A, IReadOnlyList<long> B) Seed()
    {
        var db = IndexDatabase.OpenInMemory();
        var w = new IndexWriter(db);
        var a = w.UpsertFile("src/order/OrderService.ts", "typescript", "h", 10, FileStatus.Ok);
        var b = w.UpsertFile("src/repo.ts", "typescript", "h", 10, FileStatus.Ok);
        var aIds = w.WriteSymbols(a,
        [
            new(SymbolKind.Module, "OrderService.ts", "src/order/OrderService.ts", null, 1, 1, 50, 1, null, null),
            new(SymbolKind.Class, "OrderService", "src/order/OrderService.ts:OrderService", "class OrderService", 2, 1, 40, 2, 0, "public"),
            new(SymbolKind.Method, "findOrder", "src/order/OrderService.ts:OrderService.findOrder", "findOrder(id)", 3, 3, 10, 4, 1, null),
            new(SymbolKind.Method, "saveOrder", "src/order/OrderService.ts:OrderService.saveOrder", "saveOrder(o)", 11, 3, 20, 4, 1, null),
        ], IndexSource.TreeSitter);
        var bIds = w.WriteSymbols(b,
        [
            new(SymbolKind.Module, "repo.ts", "src/repo.ts", null, 1, 1, 50, 1, null, null),
            new(SymbolKind.Function, "load", "src/repo.ts:load", "load(id)", 2, 1, 5, 1, 0, null),
        ], IndexSource.TreeSitter);
        w.WriteEdges(
        [
            new NewEdge(aIds[2], bIds[1], "src/repo.ts:load", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 5),
            new NewEdge(aIds[3], aIds[2], "src/order/OrderService.ts:OrderService.findOrder", EdgeKind.Calls, IndexSource.TreeSitter, 0.9, a, 13),
            new NewEdge(aIds[3], null, "src/gone.ts:vanished", EdgeKind.Calls, IndexSource.TreeSitter, 0.7, a, 14),
        ]);
        return (db, a, b, aIds, bIds);
    }

    [Fact]
    public void SearchSymbols_PrefixMatchesName_CaseInsensitive()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            var hits = r.SearchSymbols("order");
            Assert.Contains(hits, s => s.Id == a[1]);
            Assert.Contains(hits, s => s.Id == a[2]);
            Assert.DoesNotContain(hits, s => s.Name == "load");
        }
    }

    [Fact]
    public void SearchSymbols_MultiWord_RequiresAllTerms()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var hits = new IndexReader(db).SearchSymbols("order save");
            Assert.Single(hits);
            Assert.Equal(a[3], hits[0].Id);
        }
    }

    [Fact]
    public void GetNeighbors_Outgoing_ReturnsTargetsIncludingDangling()
    {
        var (db, _, _, a, b) = Seed();
        using (db)
        {
            var n = new IndexReader(db).GetNeighbors(a[3], Direction.Outgoing);
            Assert.Equal(2, n.Count);
            Assert.Contains(n, x => x.Other?.Id == a[2] && x.Edge.Confidence == 0.9);
            Assert.Contains(n, x => x.Other is null && x.Edge.ToName == "src/gone.ts:vanished");
        }
    }

    [Fact]
    public void GetNeighbors_Incoming_ExcludesContainsByDefaultFilter()
    {
        var (db, _, _, a, b) = Seed();
        using (db)
        {
            var callers = new IndexReader(db).GetNeighbors(a[2], Direction.Incoming, new HashSet<EdgeKind> { EdgeKind.Calls });
            Assert.Single(callers);
            Assert.Equal(a[3], callers[0].Other!.Id);

            var all = new IndexReader(db).GetNeighbors(a[2], Direction.Incoming);
            Assert.Equal(2, all.Count); // calls + contains
        }
    }

    [Fact]
    public void FindByName_And_FindByQualifiedName_And_GetSymbol()
    {
        var (db, _, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            Assert.Single(r.FindByName("findOrder"));
            Assert.Equal(a[2], r.FindByQualifiedName("src/order/OrderService.ts:OrderService.findOrder").Single().Id);
            Assert.Equal("saveOrder", r.GetSymbol(a[3])!.Name);
            Assert.Null(r.GetSymbol(999999));
        }
    }

    [Fact]
    public void GetSymbolsInFile_OrderedByStartLine_AndGetFile()
    {
        var (db, fileA, _, a, _) = Seed();
        using (db)
        {
            var r = new IndexReader(db);
            var syms = r.GetSymbolsInFile(fileA);
            Assert.Equal(4, syms.Count);
            Assert.Equal(a[0], syms[0].Id);
            Assert.Equal("typescript", r.GetFile("src/order/OrderService.ts")!.Language);
            Assert.Null(r.GetFile("nope.ts"));
            Assert.Equal(2, r.GetAllFiles().Count);
        }
    }

    [Fact]
    public void GetDanglingEdges_ReturnsOnlyNullTargets()
    {
        var (db, _, _, _, _) = Seed();
        using (db)
        {
            var dangling = new IndexReader(db).GetDanglingEdges();
            Assert.Single(dangling);
            Assert.Equal("src/gone.ts:vanished", dangling[0].ToName);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：编译失败，`IndexReader` 不存在。

- [ ] **Step 3: 写 IndexReader**

`src/CodeAtlas.Core/Storage/IndexReader.cs`:

```csharp
using CodeAtlas.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Core.Storage;

public sealed record Neighbor(EdgeRecord Edge, SymbolRecord? Other);

public sealed class IndexReader(IndexDatabase db)
{
    private const string SymbolColumns = "s.id, s.file_id, s.kind, s.name, s.qualified_name, s.signature, s.start_line, s.start_col, s.end_line, s.end_col, s.parent_id, s.visibility, s.source";
    private const string EdgeColumns = "e.id, e.from_id, e.to_id, e.to_name, e.kind, e.source, e.confidence, e.file_id, e.line, e.via, e.evidence_line";

    public IReadOnlyList<SymbolRecord> SearchSymbols(string query, int limit = 50)
    {
        var terms = query.Split([' ', '\t', '.', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*");
        var match = string.Join(" AND ", terms);
        if (match.Length == 0) return [];
        return ReadSymbols($"""
            SELECT {SymbolColumns} FROM symbols_fts f JOIN symbols s ON s.id = f.rowid
            WHERE symbols_fts MATCH $m AND s.kind <> 'module'
            ORDER BY bm25(symbols_fts), length(s.name) LIMIT $limit
            """, ("$m", match), ("$limit", limit));
    }

    public SymbolRecord? GetSymbol(long id) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.id = $id", ("$id", id)).FirstOrDefault();

    public IReadOnlyList<SymbolRecord> FindByName(string name) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.name = $n ORDER BY s.qualified_name", ("$n", name));

    public IReadOnlyList<SymbolRecord> FindByQualifiedName(string qualifiedName) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.qualified_name = $q", ("$q", qualifiedName));

    public IReadOnlyList<SymbolRecord> GetSymbolsInFile(long fileId) =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.file_id = $f ORDER BY s.start_line, s.start_col", ("$f", fileId));

    public IReadOnlyList<SymbolRecord> GetAllSymbols() =>
        ReadSymbols($"SELECT {SymbolColumns} FROM symbols s");

    public IReadOnlyList<Neighbor> GetNeighbors(long symbolId, Direction direction, IReadOnlySet<EdgeKind>? kinds = null)
    {
        var kindFilter = kinds is { Count: > 0 }
            ? " AND e.kind IN (" + string.Join(",", kinds.Select(k => "'" + DbNames.ToDb(k) + "'")) + ")"
            : "";
        var result = new List<Neighbor>();
        if (direction is Direction.Outgoing or Direction.Both)
            result.AddRange(ReadNeighbors($"""
                SELECT {EdgeColumns}, {SymbolColumns} FROM edges e LEFT JOIN symbols s ON s.id = e.to_id
                WHERE e.from_id = $id{kindFilter} ORDER BY e.line
                """, symbolId));
        if (direction is Direction.Incoming or Direction.Both)
            result.AddRange(ReadNeighbors($"""
                SELECT {EdgeColumns}, {SymbolColumns} FROM edges e JOIN symbols s ON s.id = e.from_id
                WHERE e.to_id = $id{kindFilter} ORDER BY s.qualified_name, e.line
                """, symbolId));
        return result;
    }

    public FileRecord? GetFile(string path) => ReadFiles("SELECT id, path, language, hash, size, indexed_at, status FROM files WHERE path = $p", ("$p", path)).FirstOrDefault();

    public IReadOnlyList<FileRecord> GetAllFiles() => ReadFiles("SELECT id, path, language, hash, size, indexed_at, status FROM files WHERE path <> $ext ORDER BY path", ("$ext", IndexWriter.ExternalFilePath));

    public IReadOnlyList<(long FileId, string Target, bool IsPath)> GetAllImports()
    {
        using var cmd = db.CreateCommand("SELECT file_id, target, is_path FROM imports");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, bool)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) == 1));
        return list;
    }

    public IReadOnlyList<EdgeRecord> GetDanglingEdges()
    {
        using var cmd = db.CreateCommand($"SELECT {EdgeColumns} FROM edges e WHERE e.to_id IS NULL");
        using var r = cmd.ExecuteReader();
        var list = new List<EdgeRecord>();
        while (r.Read()) list.Add(ReadEdge(r, 0));
        return list;
    }

    private List<SymbolRecord> ReadSymbols(string sql, params (string, object?)[] p)
    {
        using var cmd = db.CreateCommand(sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<SymbolRecord>();
        while (r.Read()) list.Add(ReadSymbol(r, 0)!);
        return list;
    }

    private List<Neighbor> ReadNeighbors(string sql, long id)
    {
        using var cmd = db.CreateCommand(sql, ("$id", id));
        using var r = cmd.ExecuteReader();
        var list = new List<Neighbor>();
        while (r.Read()) list.Add(new Neighbor(ReadEdge(r, 0), ReadSymbol(r, 11)));
        return list;
    }

    private List<FileRecord> ReadFiles(string sql, params (string, object?)[] p)
    {
        using var cmd = db.CreateCommand(sql, p);
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read())
            list.Add(new FileRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
                DateTimeOffset.Parse(r.GetString(5)), DbNames.ParseStatus(r.GetString(6))));
        return list;
    }

    private static SymbolRecord? ReadSymbol(SqliteDataReader r, int o)
    {
        if (r.IsDBNull(o)) return null;
        return new SymbolRecord(
            r.GetInt64(o), r.GetInt64(o + 1), DbNames.ParseSymbolKind(r.GetString(o + 2)), r.GetString(o + 3), r.GetString(o + 4),
            r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
            r.GetInt32(o + 6), r.GetInt32(o + 7), r.GetInt32(o + 8), r.GetInt32(o + 9),
            r.IsDBNull(o + 10) ? null : r.GetInt64(o + 10),
            r.IsDBNull(o + 11) ? null : r.GetString(o + 11),
            DbNames.ParseSource(r.GetString(o + 12)));
    }

    private static EdgeRecord ReadEdge(SqliteDataReader r, int o) => new(
        r.GetInt64(o), r.GetInt64(o + 1), r.IsDBNull(o + 2) ? null : r.GetInt64(o + 2), r.GetString(o + 3),
        DbNames.ParseEdgeKind(r.GetString(o + 4)), DbNames.ParseSource(r.GetString(o + 5)), r.GetDouble(o + 6),
        r.GetInt64(o + 7), r.GetInt32(o + 8), r.IsDBNull(o + 9) ? null : r.GetString(o + 9), r.IsDBNull(o + 10) ? null : r.GetInt32(o + 10));
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：全部通过。

- [ ] **Step 5: 提交**

```bash
git add src/CodeAtlas.Core/Storage/IndexReader.cs tests/CodeAtlas.Core.Tests/IndexReaderTests.cs
git commit -m "feat(core): IndexReader（FTS 搜索、邻居、大纲、悬空边）"
```

---

### Task 4: 启发式引用解析器

**Files:**
- Create: `src/CodeAtlas.Core/Resolution/IEdgeResolver.cs`
- Create: `src/CodeAtlas.Core/Resolution/HeuristicEdgeResolver.cs`
- Test: `tests/CodeAtlas.Core.Tests/HeuristicEdgeResolverTests.cs`

**Interfaces:**
- Consumes: `IndexReader`、`IndexWriter`、模型
- Produces:
  - `record PendingReference(long FromSymbolId, long FileId, string FilePath, string Language, string Name, EdgeKind Kind, int Line)`
  - `class ResolveContext(IndexDatabase Db, IReadOnlyList<PendingReference> References)`
  - `interface IEdgeResolver { Task ResolveAsync(ResolveContext context, CancellationToken ct); }`
  - `class HeuristicEdgeResolver : IEdgeResolver`，常量 `MaxCandidates = 20`
  - `static string LanguageFamily(string languageId)`：typescript / tsx / javascript 归为 `"js"`，其余原样返回

解析规则（spec 4.2）：
1. 同文件内有且仅有一个同名且 kind 兼容的候选 → 0.9
2. import 范围内有且仅有一个 → 0.7。TS/JS：相对路径 import 解析到文件（去扩展名比较，支持 `/index`）；C#：`using` 的命名空间前缀，加上本文件自己声明的命名空间作为隐式范围
3. 同语言族全仓库有且仅有一个 → 0.5
4. 多个候选 → 前 20 个各一条边 0.3
5. 没有候选 → external 符号 0.5

kind 兼容表：`calls` → Function / Method / Class / Struct / Variable；`inherits` / `implements` → Class / Interface / Struct / TypeAlias；`references` → Class / Interface / Struct / Enum / TypeAlias。

import 边（kind `imports`，from 是 Module 符号）：路径 import 解析到目标文件的 Module 符号 → 1.0；命名空间 import 解析到所有同名 Namespace 符号（上限 20）→ 1.0；都没有 → external 0.5。

- [ ] **Step 1: 写失败的测试**

`tests/CodeAtlas.Core.Tests/HeuristicEdgeResolverTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

public class HeuristicEdgeResolverTests
{
    private sealed class Fixture : IDisposable
    {
        public IndexDatabase Db { get; } = IndexDatabase.OpenInMemory();
        public IndexWriter Writer { get; }
        public IndexReader Reader { get; }
        public Fixture() { Writer = new IndexWriter(Db); Reader = new IndexReader(Db); }

        public (long FileId, IReadOnlyList<long> Ids) File(string path, string language, params (SymbolKind Kind, string Name, int? Parent)[] symbols)
        {
            var fileId = Writer.UpsertFile(path, language, "h", 1, FileStatus.Ok);
            var prefix = language == "csharp" ? "" : path + ":";
            var list = new List<ParsedSymbol> { new(SymbolKind.Module, System.IO.Path.GetFileName(path), path, null, 1, 1, 999, 1, null, null) };
            var line = 2;
            foreach (var (kind, name, parent) in symbols)
            {
                var parentIdx = parent ?? 0;
                var parentQ = parentIdx == 0 ? prefix : list[parentIdx].QualifiedName + ".";
                list.Add(new ParsedSymbol(kind, name, parentQ + name, null, line, 1, line + 1, 1, parentIdx, null));
                line += 2;
            }
            return (fileId, Writer.WriteSymbols(fileId, list, IndexSource.TreeSitter));
        }

        public async Task Resolve(params PendingReference[] refs)
        {
            await new HeuristicEdgeResolver().ResolveAsync(new ResolveContext(Db, refs), CancellationToken.None);
        }

        public List<(long? ToId, string ToName, double Confidence)> EdgesFrom(long fromId, EdgeKind kind) =>
            Reader.GetNeighbors(fromId, Direction.Outgoing, new HashSet<EdgeKind> { kind })
                .Select(n => (n.Edge.ToId, n.Edge.ToName, n.Edge.Confidence)).ToList();

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task SameFileUnique_Gets09()
    {
        using var f = new Fixture();
        var (fileId, ids) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null), (SymbolKind.Function, "helper", null));
        f.File("src/b.ts", "typescript", (SymbolKind.Function, "helper", null));

        await f.Resolve(new PendingReference(ids[1], fileId, "src/a.ts", "typescript", "helper", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(ids[1], EdgeKind.Calls);
        Assert.Single(edges);
        Assert.Equal((ids[2], "src/a.ts:helper", 0.9), edges[0]);
    }

    [Fact]
    public async Task ImportScopeUnique_PathImport_Gets07()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/app/main.ts", "typescript", (SymbolKind.Function, "run", null));
        var (_, bIds) = f.File("src/lib/svc.ts", "typescript", (SymbolKind.Class, "Svc", null));
        f.File("src/other/svc.ts", "typescript", (SymbolKind.Class, "Svc", null));
        f.Writer.WriteImports(aFile, [new ParsedImport("../lib/svc", true, 1)]);

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/app/main.ts", "typescript", "Svc", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls);
        Assert.Single(edges);
        Assert.Equal((bIds[1], "src/lib/svc.ts:Svc", 0.7), edges[0]);
    }

    [Fact]
    public async Task ImportScopeUnique_IndexFile_Resolves()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/main.ts", "typescript", (SymbolKind.Function, "run", null));
        var (_, bIds) = f.File("src/lib/index.ts", "typescript", (SymbolKind.Function, "boot", null));
        f.File("src/x/index.ts", "typescript", (SymbolKind.Function, "boot", null));
        f.Writer.WriteImports(aFile, [new ParsedImport("./lib", true, 1)]);

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/main.ts", "typescript", "boot", EdgeKind.Calls, 3));

        Assert.Equal((bIds[1], "src/lib/index.ts:boot", 0.7), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
    }

    [Fact]
    public async Task CSharp_UsingNamespace_AndOwnNamespace_AreImportScope()
    {
        using var f = new Fixture();
        var (svcFile, svcIds) = f.File("Svc.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Orders", null), (SymbolKind.Class, "OrderService", 1), (SymbolKind.Method, "Find", 2));
        var (_, repoIds) = f.File("Repo.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Repo", null), (SymbolKind.Class, "OrderRepo", 1));
        f.File("Other.cs", "csharp", (SymbolKind.Namespace, "Legacy", null), (SymbolKind.Class, "OrderRepo", 1));
        var (_, valIds) = f.File("Validator.cs", "csharp",
            (SymbolKind.Namespace, "Acme.Orders", null), (SymbolKind.Class, "Validator", 1));
        f.File("Other2.cs", "csharp", (SymbolKind.Namespace, "Legacy", null), (SymbolKind.Class, "Validator", 1));
        f.Writer.WriteImports(svcFile, [new ParsedImport("Acme.Repo", false, 1)]);

        await f.Resolve(
            new PendingReference(svcIds[3], svcFile, "Svc.cs", "csharp", "OrderRepo", EdgeKind.Calls, 5),
            new PendingReference(svcIds[3], svcFile, "Svc.cs", "csharp", "Validator", EdgeKind.Calls, 6));

        var edges = f.EdgesFrom(svcIds[3], EdgeKind.Calls);
        Assert.Equal(2, edges.Count);
        Assert.Contains((repoIds[2], "Acme.Repo.OrderRepo", 0.7), edges);
        Assert.Contains((valIds[2], "Acme.Orders.Validator", 0.7), edges);
    }

    [Fact]
    public async Task RepoUnique_Gets05()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, bIds) = f.File("src/deep/b.ts", "typescript", (SymbolKind.Function, "helper", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "helper", EdgeKind.Calls, 3));

        Assert.Equal((bIds[1], "src/deep/b.ts:helper", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
    }

    [Fact]
    public async Task NoCandidate_CreatesExternal_05()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "fetch", EdgeKind.Calls, 3));

        var e = f.EdgesFrom(aIds[1], EdgeKind.Calls).Single();
        Assert.Equal(0.5, e.Confidence);
        Assert.Equal("external:fetch", e.ToName);
        Assert.Equal(SymbolKind.External, f.Reader.GetSymbol(e.ToId!.Value)!.Kind);
    }

    [Fact]
    public async Task Resolve_ManyCandidates_CapsAt20Edges()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        for (var i = 0; i < 25; i++)
            f.File($"src/h{i}.ts", "typescript", (SymbolKind.Function, "Handle", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Handle", EdgeKind.Calls, 3));

        var edges = f.EdgesFrom(aIds[1], EdgeKind.Calls);
        Assert.Equal(HeuristicEdgeResolver.MaxCandidates, edges.Count);
        Assert.All(edges, e => Assert.Equal(0.3, e.Confidence));
    }

    [Fact]
    public async Task KindMismatch_IsNotACandidate()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Class, "C", null), (SymbolKind.Field, "count", 1), (SymbolKind.Method, "run", 1));

        await f.Resolve(new PendingReference(aIds[3], aFile, "src/a.ts", "typescript", "count", EdgeKind.Calls, 5));

        Assert.Equal("external:count", f.EdgesFrom(aIds[3], EdgeKind.Calls).Single().ToName);
    }

    [Fact]
    public async Task LanguageFamily_TsCanResolveIntoTsx_ButNotCSharp()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, tsxIds) = f.File("src/View.tsx", "tsx", (SymbolKind.Function, "View", null));
        f.File("View.cs", "csharp", (SymbolKind.Class, "View", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "View", EdgeKind.Calls, 3));

        Assert.Equal((tsxIds[1], "src/View.tsx:View", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Calls).Single());
        Assert.Equal("js", HeuristicEdgeResolver.LanguageFamily("tsx"));
        Assert.Equal("csharp", HeuristicEdgeResolver.LanguageFamily("csharp"));
    }

    [Fact]
    public async Task Imports_PathResolvesToModule_NamespaceResolvesToNamespaces_UnknownToExternal()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Function, "main", null));
        var (_, bIds) = f.File("src/b.ts", "typescript", (SymbolKind.Function, "x", null));
        var (csFile, csIds) = f.File("Svc.cs", "csharp", (SymbolKind.Namespace, "Acme.Orders", null));
        var (_, ns1) = f.File("N1.cs", "csharp", (SymbolKind.Namespace, "Acme.Repo", null));
        var (_, ns2) = f.File("N2.cs", "csharp", (SymbolKind.Namespace, "Acme.Repo", null));

        await f.Resolve(
            new PendingReference(aIds[0], aFile, "src/a.ts", "typescript", "./b", EdgeKind.Imports, 1),
            new PendingReference(aIds[0], aFile, "src/a.ts", "typescript", "react", EdgeKind.Imports, 2),
            new PendingReference(csIds[0], csFile, "Svc.cs", "csharp", "Acme.Repo", EdgeKind.Imports, 1));

        var tsImports = f.EdgesFrom(aIds[0], EdgeKind.Imports);
        Assert.Contains((bIds[0], "src/b.ts", 1.0), tsImports);
        Assert.Contains(tsImports, e => e.ToName == "external:react" && e.Confidence == 0.5);
        var csImports = f.EdgesFrom(csIds[0], EdgeKind.Imports);
        Assert.Equal(2, csImports.Count);
        Assert.Contains((ns1[1], "Acme.Repo", 1.0), csImports);
        Assert.Contains((ns2[1], "Acme.Repo", 1.0), csImports);
    }

    [Fact]
    public async Task Inherits_PrefersTypes_NotMethods()
    {
        using var f = new Fixture();
        var (aFile, aIds) = f.File("src/a.ts", "typescript", (SymbolKind.Class, "Child", null), (SymbolKind.Method, "Base", 1));
        var (_, bIds) = f.File("src/b.ts", "typescript", (SymbolKind.Class, "Base", null));

        await f.Resolve(new PendingReference(aIds[1], aFile, "src/a.ts", "typescript", "Base", EdgeKind.Inherits, 2));

        Assert.Equal((bIds[1], "src/b.ts:Base", 0.5), f.EdgesFrom(aIds[1], EdgeKind.Inherits).Single());
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：编译失败，`HeuristicEdgeResolver` 不存在。

- [ ] **Step 3: 写接口与解析器**

`src/CodeAtlas.Core/Resolution/IEdgeResolver.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>一条待解析的引用：from 符号已入库，目标只有名字。</summary>
public sealed record PendingReference(long FromSymbolId, long FileId, string FilePath, string Language, string Name, EdgeKind Kind, int Line);

public sealed class ResolveContext(IndexDatabase db, IReadOnlyList<PendingReference> references)
{
    public IndexDatabase Db { get; } = db;
    public IReadOnlyList<PendingReference> References { get; } = references;
}

/// <summary>边的解析器扩展点。主解析完成后按注册顺序执行，可把未解析引用改写为边或新符号。</summary>
public interface IEdgeResolver
{
    Task ResolveAsync(ResolveContext context, CancellationToken ct);
}
```

`src/CodeAtlas.Core/Resolution/HeuristicEdgeResolver.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>四级启发式解析（spec 4.2）：同文件 0.9、import 范围 0.7、全仓库唯一 0.5、多候选各 0.3、无候选 external 0.5。</summary>
public sealed class HeuristicEdgeResolver : IEdgeResolver
{
    public const int MaxCandidates = 20;
    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];

    public static string LanguageFamily(string languageId) => languageId switch
    {
        "typescript" or "tsx" or "javascript" => "js",
        _ => languageId,
    };

    private static bool KindCompatible(EdgeKind edge, SymbolKind symbol) => edge switch
    {
        EdgeKind.Calls => symbol is SymbolKind.Function or SymbolKind.Method or SymbolKind.Class or SymbolKind.Struct or SymbolKind.Variable,
        EdgeKind.Inherits or EdgeKind.Implements => symbol is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.TypeAlias,
        EdgeKind.References => symbol is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.TypeAlias,
        _ => true,
    };

    public Task ResolveAsync(ResolveContext context, CancellationToken ct)
    {
        var reader = new IndexReader(context.Db);
        var writer = new IndexWriter(context.Db);

        var files = reader.GetAllFiles().ToDictionary(f => f.Id);
        var all = reader.GetAllSymbols();
        var byName = all.Where(s => s.Kind is not (SymbolKind.Module or SymbolKind.External))
            .GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.ToList());
        var modulesByPathNoExt = all.Where(s => s.Kind == SymbolKind.Module)
            .GroupBy(s => StripScriptExtension(s.QualifiedName)).ToDictionary(g => g.Key, g => g.First());
        var namespacesByName = all.Where(s => s.Kind == SymbolKind.Namespace)
            .GroupBy(s => s.QualifiedName).ToDictionary(g => g.Key, g => g.ToList());
        var namespacesByFile = all.Where(s => s.Kind == SymbolKind.Namespace)
            .GroupBy(s => s.FileId).ToDictionary(g => g.Key, g => g.Select(s => s.QualifiedName).ToList());
        var importsByFile = reader.GetAllImports().GroupBy(i => i.FileId)
            .ToDictionary(g => g.Key, g => g.Select(i => (i.Target, i.IsPath)).ToList());

        var edges = new List<NewEdge>();
        foreach (var r in context.References)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Kind == EdgeKind.Imports)
            {
                edges.AddRange(ResolveImport(r, modulesByPathNoExt, namespacesByName, writer));
                continue;
            }

            var family = LanguageFamily(r.Language);
            var candidates = byName.TryGetValue(r.Name, out var list)
                ? list.Where(s => KindCompatible(r.Kind, s.Kind)
                                  && files.TryGetValue(s.FileId, out var sf)
                                  && LanguageFamily(sf.Language) == family).ToList()
                : [];

            var sameFile = candidates.Where(c => c.FileId == r.FileId).ToList();
            if (sameFile.Count == 1) { edges.Add(Edge(r, sameFile[0], 0.9)); continue; }

            var scope = BuildImportScope(r, importsByFile, namespacesByFile);
            var inScope = candidates.Where(c => scope.Contains(c, files)).ToList();
            if (inScope.Count == 1) { edges.Add(Edge(r, inScope[0], 0.7)); continue; }

            if (candidates.Count == 1) { edges.Add(Edge(r, candidates[0], 0.5)); continue; }

            if (candidates.Count == 0)
            {
                var ext = writer.GetOrCreateExternal(r.Name);
                edges.Add(new NewEdge(r.FromSymbolId, ext, "external:" + r.Name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line));
                continue;
            }

            foreach (var c in candidates.OrderBy(c => c.QualifiedName).Take(MaxCandidates))
                edges.Add(Edge(r, c, 0.3));
        }

        writer.WriteEdges(edges);
        return Task.CompletedTask;
    }

    private static NewEdge Edge(PendingReference r, SymbolRecord target, double confidence) =>
        new(r.FromSymbolId, target.Id, target.QualifiedName, r.Kind, IndexSource.TreeSitter, confidence, r.FileId, r.Line);

    private static IEnumerable<NewEdge> ResolveImport(
        PendingReference r,
        Dictionary<string, SymbolRecord> modulesByPathNoExt,
        Dictionary<string, List<SymbolRecord>> namespacesByName,
        IndexWriter writer)
    {
        var isPath = r.Name.StartsWith('.') || r.Name.StartsWith('/');
        if (isPath)
        {
            foreach (var candidatePath in ResolveRelativePath(r.FilePath, r.Name))
                if (modulesByPathNoExt.TryGetValue(candidatePath, out var module))
                {
                    yield return new NewEdge(r.FromSymbolId, module.Id, module.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line);
                    yield break;
                }
        }
        else if (namespacesByName.TryGetValue(r.Name, out var namespaces))
        {
            foreach (var ns in namespaces.Take(MaxCandidates))
                yield return new NewEdge(r.FromSymbolId, ns.Id, ns.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line);
            yield break;
        }
        var ext = writer.GetOrCreateExternal(r.Name);
        yield return new NewEdge(r.FromSymbolId, ext, "external:" + r.Name, EdgeKind.Imports, IndexSource.TreeSitter, 0.5, r.FileId, r.Line);
    }

    /// <summary>把相对 import 解析成候选的"去扩展名路径"：本身，以及 /index。</summary>
    internal static IEnumerable<string> ResolveRelativePath(string fromFile, string target)
    {
        var dir = fromFile.Contains('/') ? fromFile[..fromFile.LastIndexOf('/')] : "";
        var combined = NormalizePath(dir.Length == 0 ? target : dir + "/" + target);
        var noExt = StripScriptExtension(combined);
        yield return noExt;
        yield return noExt + "/index";
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static string StripScriptExtension(string path)
    {
        foreach (var ext in ScriptExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return path[..^ext.Length];
        return path;
    }

    private static ImportScope BuildImportScope(
        PendingReference r,
        Dictionary<long, List<(string Target, bool IsPath)>> importsByFile,
        Dictionary<long, List<string>> namespacesByFile)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<string>();
        if (importsByFile.TryGetValue(r.FileId, out var imports))
            foreach (var (target, isPath) in imports)
            {
                if (isPath) foreach (var p in ResolveRelativePath(r.FilePath, target)) paths.Add(p);
                else prefixes.Add(target + ".");
            }
        if (namespacesByFile.TryGetValue(r.FileId, out var ownNamespaces))
            foreach (var ns in ownNamespaces) prefixes.Add(ns + ".");
        return new ImportScope(paths, prefixes);
    }

    private sealed record ImportScope(HashSet<string> PathsNoExt, List<string> NamespacePrefixes)
    {
        public bool Contains(SymbolRecord candidate, Dictionary<long, FileRecord> files)
        {
            if (files.TryGetValue(candidate.FileId, out var f) && PathsNoExt.Contains(StripScriptExtension(f.Path))) return true;
            foreach (var prefix in NamespacePrefixes)
                if (candidate.QualifiedName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Core.Tests
```
预期：全部通过。

- [ ] **Step 5: 提交**

```bash
git add src/CodeAtlas.Core/Resolution tests/CodeAtlas.Core.Tests/HeuristicEdgeResolverTests.cs
git commit -m "feat(core): IEdgeResolver 扩展点与四级启发式解析器"
```

---

### Task 5: tree-sitter 适配器底座与 TypeScript 适配器

**Files:**
- Create: `src/CodeAtlas.Indexing.TreeSitter/CodeAtlas.Indexing.TreeSitter.csproj`
- Create: `src/CodeAtlas.Indexing.TreeSitter/ILanguageAdapter.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/QueryResources.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/LanguageAdapterBase.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Adapters/TypeScriptAdapter.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/typescript.definitions.scm`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/typescript.references.scm`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/CodeAtlas.Indexing.TreeSitter.Tests.csproj`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/ts/svc.ts`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/ts/broken.ts`
- Test: `tests/CodeAtlas.Indexing.TreeSitter.Tests/TypeScriptAdapterTests.cs`

**Interfaces:**
- Consumes: `ParsedFile` 等模型（Task 2）
- Produces:
  - `interface ILanguageAdapter { string LanguageId; IReadOnlyList<string> Extensions; ParsedFile Parse(string relativePath, string source); }`
  - `abstract class LanguageAdapterBase : ILanguageAdapter, IDisposable`，构造函数 `(string languageId, string grammarName, IReadOnlyList<string> extensions, string definitionsQuery, string referencesQuery)`，可重写：`string ModuleQualifiedPrefix(string relativePath)`（默认 `relativePath + ":"`）、`EdgeKind AdjustReferenceKind(EdgeKind kind, string name)`、`string? ExtractVisibility(Node definition)`、`Node DefinitionScope(Node definition, Node root)`、`string ReferenceName(Node node)`（默认取最后一个 `.` 之后）、`bool ImportIsPath(string target)`
  - `static class QueryResources { string Load(string name) }` 读取嵌入资源，如 `"typescript.definitions.scm"`
  - `sealed class TypeScriptAdapter : LanguageAdapterBase`，`LanguageId = "typescript"`，扩展名 `.ts .mts .cts`

.scm 约定：定义查询里每个 pattern 必须有一个 `@def.<kind>` capture 和一个 `@name` capture；引用查询里每个 capture 叫 `@ref.<edgekind>`。`<kind>` 取值：module、namespace、class、interface、struct、enum、function、method、property、field、variable、type_alias；`<edgekind>` 取值：calls、references、inherits、implements、imports。

TreeSitter.DotNet 1.3.0 的实际 API（已在本机验证）：`new Language("TypeScript")`、`new Parser(language)`、`parser.Parse(string) : Tree`、`tree.RootNode : Node`、`new Query(language, string)`、`query.Execute(node) : QueryCursor`，`cursor.Matches : IEnumerable<QueryMatch>`（`match.Captures : IReadOnlyList<QueryCapture>`，`capture.Name`、`capture.Node`）、`cursor.Captures : IEnumerable<QueryCapture>`；`Node.Type`、`Node.Text`、`Node.StartPosition / EndPosition : Point(Row, Column)` 0 基、`Node.StartIndex / EndIndex`、`Node.Parent`、`Node.Children`、`Node.GetChildForField(string)`。grammar 名：`"TypeScript"`、`"TSX"`、`"JavaScript"`、`"C#"`。

- [ ] **Step 1: 创建项目**

```bash
cd D:/github/CodeAtlas
dotnet new classlib -n CodeAtlas.Indexing.TreeSitter -o src/CodeAtlas.Indexing.TreeSitter
rm src/CodeAtlas.Indexing.TreeSitter/Class1.cs
dotnet add src/CodeAtlas.Indexing.TreeSitter package TreeSitter.DotNet --version 1.3.0
dotnet add src/CodeAtlas.Indexing.TreeSitter reference src/CodeAtlas.Core
dotnet new xunit -n CodeAtlas.Indexing.TreeSitter.Tests -o tests/CodeAtlas.Indexing.TreeSitter.Tests
rm tests/CodeAtlas.Indexing.TreeSitter.Tests/UnitTest1.cs
dotnet add tests/CodeAtlas.Indexing.TreeSitter.Tests reference src/CodeAtlas.Indexing.TreeSitter
dotnet sln add src/CodeAtlas.Indexing.TreeSitter tests/CodeAtlas.Indexing.TreeSitter.Tests
mkdir -p src/CodeAtlas.Indexing.TreeSitter/Queries src/CodeAtlas.Indexing.TreeSitter/Adapters tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/ts
```

在 `src/CodeAtlas.Indexing.TreeSitter/CodeAtlas.Indexing.TreeSitter.csproj` 的 `<Project>` 内加入：

```xml
  <ItemGroup>
    <EmbeddedResource Include="Queries\*.scm" LogicalName="%(Filename)%(Extension)" />
  </ItemGroup>
```

在 `tests/CodeAtlas.Indexing.TreeSitter.Tests/CodeAtlas.Indexing.TreeSitter.Tests.csproj` 的 `<Project>` 内加入：

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: 写 fixture**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/ts/svc.ts`（测试断言依赖行号，不要改动）:

```typescript
import { Foo } from './foo';
import * as util from 'util';
export namespace Shapes { export const PI = 3; }
export abstract class Base<T> { abstract run(): void; }
export interface IRepo extends IBase { find(id: number): Foo; count: number; }
export enum Color { Red, Green }
export type Id = string;
export class Svc extends Base<Foo> implements IRepo, IOther {
  private repo: IRepo;
  count = 0;
  constructor(r: IRepo) { super(); this.repo = r; }
  find(id: number): Foo { return this.repo.find(id); }
  get total(): number { return this.count; }
}
export function top(x: Foo): void { const s = new Svc(null!); s.find(1); helper(); util.format('x'); }
export const arrow = (a: number) => a + 1;
const fexpr = function named() {};
```

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/ts/broken.ts`:

```typescript
export class Good {
  ok(): number { return 1; }
}
export function oops( {
  return 2;
export class StillHere {
  fine(): void { helper(); }
}
```

- [ ] **Step 3: 写失败的测试**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/TypeScriptAdapterTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class TypeScriptAdapterTests
{
    internal static string Fixture(string rel) => Path.Combine(AppContext.BaseDirectory, "Fixtures", rel);

    private static ParsedFile ParseSvc()
    {
        using var adapter = new TypeScriptAdapter();
        return adapter.Parse("src/svc.ts", File.ReadAllText(Fixture("ts/svc.ts")));
    }

    [Fact]
    public void Parse_FirstSymbolIsModule_WithPathAsQualifiedName()
    {
        var f = ParseSvc();
        Assert.Equal(SymbolKind.Module, f.Symbols[0].Kind);
        Assert.Equal("svc.ts", f.Symbols[0].Name);
        Assert.Equal("src/svc.ts", f.Symbols[0].QualifiedName);
        Assert.Null(f.Symbols[0].ParentIndex);
        Assert.Equal("typescript", f.Language);
    }

    [Fact]
    public void Parse_ExtractsAllDefinitionKinds()
    {
        var f = ParseSvc();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["src/svc.ts:Shapes"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["src/svc.ts:Base"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Base.run"].Kind);
        Assert.Equal(SymbolKind.Interface, byQ["src/svc.ts:IRepo"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:IRepo.find"].Kind);
        Assert.Equal(SymbolKind.Property, byQ["src/svc.ts:IRepo.count"].Kind);
        Assert.Equal(SymbolKind.Enum, byQ["src/svc.ts:Color"].Kind);
        Assert.Equal(SymbolKind.TypeAlias, byQ["src/svc.ts:Id"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["src/svc.ts:Svc"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["src/svc.ts:Svc.repo"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["src/svc.ts:Svc.count"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Svc.constructor"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Svc.find"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["src/svc.ts:Svc.total"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:top"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:arrow"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/svc.ts:fexpr"].Kind);
        Assert.Equal(18, f.Symbols.Count);
    }

    [Fact]
    public void Parse_PositionsAreOneBased_AndParentLinksFollowContainment()
    {
        var f = ParseSvc();
        var svc = f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc");
        var find = f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc.find");

        Assert.Equal(8, svc.StartLine);
        Assert.Equal(8, svc.StartCol);
        Assert.Equal(14, svc.EndLine);
        Assert.Equal(12, find.StartLine);
        Assert.Equal(3, find.StartCol);
        Assert.Equal(f.Symbols.IndexOf(svc), find.ParentIndex);
        Assert.Equal(0, svc.ParentIndex);
        Assert.Equal("class Svc extends Base<Foo> implements IRepo, IOther {", svc.Signature);
        Assert.Equal("find(id: number): Foo { return this.repo.find(id); }", find.Signature);
    }

    [Fact]
    public void Parse_Visibility_FromAccessibilityModifierOrExport()
    {
        var f = ParseSvc();
        Assert.Equal("private", f.Symbols.Single(s => s.Name == "repo").Visibility);
        Assert.Equal("export", f.Symbols.Single(s => s.QualifiedName == "src/svc.ts:Svc").Visibility);
        Assert.Equal("export", f.Symbols.Single(s => s.Name == "arrow").Visibility);
        Assert.Null(f.Symbols.Single(s => s.Name == "fexpr").Visibility);
    }

    [Fact]
    public void Parse_ExtractsReferences_WithEnclosingSymbol()
    {
        var f = ParseSvc();
        int Idx(string q) => f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == q));
        var top = Idx("src/svc.ts:top");
        var svcFind = Idx("src/svc.ts:Svc.find");
        var svc = Idx("src/svc.ts:Svc");
        var irepo = Idx("src/svc.ts:IRepo");

        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((top, "Svc", EdgeKind.Calls), refs);
        Assert.Contains((top, "find", EdgeKind.Calls), refs);
        Assert.Contains((top, "helper", EdgeKind.Calls), refs);
        Assert.Contains((top, "format", EdgeKind.Calls), refs);
        Assert.Contains((top, "Foo", EdgeKind.References), refs);
        Assert.Contains((svcFind, "find", EdgeKind.Calls), refs);
        Assert.Contains((svc, "Base", EdgeKind.Inherits), refs);
        Assert.Contains((svc, "IRepo", EdgeKind.Implements), refs);
        Assert.Contains((svc, "IOther", EdgeKind.Implements), refs);
        Assert.Contains((irepo, "IBase", EdgeKind.Inherits), refs);
        Assert.Contains((0, "./foo", EdgeKind.Imports), refs);
        Assert.Contains((0, "util", EdgeKind.Imports), refs);
        Assert.DoesNotContain(refs, r => r.Name == "number");
    }

    [Fact]
    public void Parse_Imports_PathFlagReflectsRelativeness()
    {
        var f = ParseSvc();
        Assert.Equal(2, f.Imports.Count);
        Assert.Contains(f.Imports, i => i.Target == "./foo" && i.IsPath && i.Line == 1);
        Assert.Contains(f.Imports, i => i.Target == "util" && !i.IsPath && i.Line == 2);
    }

    [Fact]
    public void Parse_FileWithSyntaxError_StillExtractsValidSymbols()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/broken.ts", File.ReadAllText(Fixture("ts/broken.ts")));

        var names = f.Symbols.Select(s => s.Name).ToList();
        Assert.Contains("Good", names);
        Assert.Contains("ok", names);
        Assert.Contains("StillHere", names);
        Assert.Contains("fine", names);
        Assert.Contains(f.References, r => r.Name == "helper" && r.Kind == EdgeKind.Calls);
    }

    [Fact]
    public void Parse_EmptySource_YieldsOnlyModule()
    {
        using var adapter = new TypeScriptAdapter();
        var f = adapter.Parse("src/empty.ts", "");
        Assert.Single(f.Symbols);
        Assert.Empty(f.References);
    }

    [Fact]
    public void Parse_IsSafeToCallConcurrently()
    {
        using var adapter = new TypeScriptAdapter();
        var src = File.ReadAllText(Fixture("ts/svc.ts"));
        var results = new ParsedFile[16];
        Parallel.For(0, 16, i => results[i] = adapter.Parse($"src/{i}.ts", src));
        Assert.All(results, r => Assert.Equal(18, r.Symbols.Count));
    }
}
```

- [ ] **Step 4: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：编译失败。

- [ ] **Step 5: 写查询文件**

`src/CodeAtlas.Indexing.TreeSitter/Queries/typescript.definitions.scm`:

```scheme
(class_declaration name: (type_identifier) @name) @def.class
(abstract_class_declaration name: (type_identifier) @name) @def.class
(interface_declaration name: (type_identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
(type_alias_declaration name: (type_identifier) @name) @def.type_alias
(internal_module name: (identifier) @name) @def.namespace
(function_declaration name: (identifier) @name) @def.function
(method_definition name: (property_identifier) @name) @def.method
(method_signature name: (property_identifier) @name) @def.method
(abstract_method_signature name: (property_identifier) @name) @def.method
(public_field_definition name: (property_identifier) @name) @def.field
(property_signature name: (property_identifier) @name) @def.property
(variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)]) @def.function
```

`src/CodeAtlas.Indexing.TreeSitter/Queries/typescript.references.scm`:

```scheme
(call_expression function: (identifier) @ref.calls)
(call_expression function: (member_expression property: (property_identifier) @ref.calls))
(new_expression constructor: (identifier) @ref.calls)
(extends_clause value: (identifier) @ref.inherits)
(implements_clause (type_identifier) @ref.implements)
(extends_type_clause type: (type_identifier) @ref.inherits)
(type_annotation (type_identifier) @ref.references)
(generic_type name: (type_identifier) @ref.references)
(import_statement source: (string (string_fragment) @ref.imports))
```

- [ ] **Step 6: 写接口、资源加载与底座**

`src/CodeAtlas.Indexing.TreeSitter/ILanguageAdapter.cs`:

```csharp
using CodeAtlas.Core.Model;

namespace CodeAtlas.Indexing.TreeSitter;

public interface ILanguageAdapter
{
    string LanguageId { get; }
    IReadOnlyList<string> Extensions { get; }
    ParsedFile Parse(string relativePath, string source);
}
```

`src/CodeAtlas.Indexing.TreeSitter/QueryResources.cs`:

```csharp
using System.Reflection;

namespace CodeAtlas.Indexing.TreeSitter;

public static class QueryResources
{
    public static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"嵌入的查询资源不存在：{name}。可用：{string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

`src/CodeAtlas.Indexing.TreeSitter/LanguageAdapterBase.cs`:

```csharp
using CodeAtlas.Core.Model;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter;

/// <summary>
/// 每种语言 = 一对 .scm 查询 + 一个薄子类。本类负责：跑查询、按包含关系算父子与限定名、找引用所在的符号。
/// Language 与 Query 只读可跨线程共享；Parser 每次 Parse 新建。
/// </summary>
public abstract class LanguageAdapterBase : ILanguageAdapter, IDisposable
{
    private readonly Language _language;
    private readonly Query _definitions;
    private readonly Query _references;

    protected LanguageAdapterBase(string languageId, string grammarName, IReadOnlyList<string> extensions, string definitionsQuery, string referencesQuery)
    {
        LanguageId = languageId;
        Extensions = extensions;
        _language = new Language(grammarName);
        _definitions = new Query(_language, definitionsQuery);
        _references = new Query(_language, referencesQuery);
    }

    public string LanguageId { get; }
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>模块级符号的限定名前缀。TS/JS 用 "路径:"；C# 返回空串让命名空间链自己构成限定名。</summary>
    protected virtual string ModuleQualifiedPrefix(string relativePath) => relativePath + ":";

    /// <summary>语言特定的边种类修正，如 C# 把 I 开头的基类型视为 implements。</summary>
    protected virtual EdgeKind AdjustReferenceKind(EdgeKind kind, string name) => kind;

    protected virtual string? ExtractVisibility(Node definition) => null;

    /// <summary>定义的作用范围节点，默认就是定义节点本身；C# 文件级命名空间要扩展到整个文件。</summary>
    protected virtual Node DefinitionScope(Node definition, Node root) => definition;

    /// <summary>引用节点转成要解析的名字，默认取最后一个 "." 之后的部分。</summary>
    protected virtual string ReferenceName(Node node)
    {
        var text = node.Text;
        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    protected virtual bool ImportIsPath(string target) => target.StartsWith('.') || target.StartsWith('/');

    public ParsedFile Parse(string relativePath, string source)
    {
        using var parser = new Parser(_language);
        using var tree = parser.Parse(source) ?? throw new InvalidOperationException($"tree-sitter 未能解析 {relativePath}");
        var root = tree.RootNode;

        var symbols = new List<ParsedSymbol>();
        var ranges = new List<(int Start, int End)>();
        var lineCount = source.Count(c => c == '\n') + 1;
        symbols.Add(new ParsedSymbol(SymbolKind.Module, Path.GetFileName(relativePath), relativePath, null, 1, 1, lineCount, 1, null, null));
        ranges.Add((0, int.MaxValue));

        CollectDefinitions(root, relativePath, symbols, ranges);
        var (references, imports) = CollectReferences(root, ranges);
        return new ParsedFile(relativePath, LanguageId, symbols, references, imports);
    }

    private void CollectDefinitions(Node root, string relativePath, List<ParsedSymbol> symbols, List<(int Start, int End)> ranges)
    {
        var defs = new List<(Node Scope, Node Def, string Name, SymbolKind Kind)>();
        using (var cursor = _definitions.Execute(root))
        {
            foreach (var match in cursor.Matches)
            {
                Node? def = null, name = null;
                string? kindName = null;
                foreach (var c in match.Captures)
                {
                    if (c.Name == "name") name = c.Node;
                    else if (c.Name.StartsWith("def.", StringComparison.Ordinal)) { def = c.Node; kindName = c.Name[4..]; }
                }
                if (def is null || name is null || kindName is null) continue;
                defs.Add((DefinitionScope(def, root), def, name.Text, MapSymbolKind(kindName)));
            }
        }
        // 起点升序、终点降序：父节点一定排在子节点前面
        defs.Sort((a, b) => a.Scope.StartIndex != b.Scope.StartIndex
            ? a.Scope.StartIndex.CompareTo(b.Scope.StartIndex)
            : b.Scope.EndIndex.CompareTo(a.Scope.EndIndex));

        var prefix = ModuleQualifiedPrefix(relativePath);
        var stack = new Stack<int>();
        stack.Push(0);
        var seen = new HashSet<(int, int, SymbolKind)>();
        foreach (var (scope, def, name, kind) in defs)
        {
            if (!seen.Add((def.StartIndex, def.EndIndex, kind))) continue;
            while (stack.Count > 1 && ranges[stack.Peek()].End < scope.EndIndex) stack.Pop();
            var parentIndex = stack.Peek();
            var parentQualified = parentIndex == 0 ? prefix : symbols[parentIndex].QualifiedName + ".";
            symbols.Add(new ParsedSymbol(
                kind, name, parentQualified + name, FirstLine(def.Text),
                def.StartPosition.Row + 1, def.StartPosition.Column + 1,
                scope.EndPosition.Row + 1, scope.EndPosition.Column + 1,
                parentIndex, ExtractVisibility(def)));
            ranges.Add((scope.StartIndex, scope.EndIndex));
            stack.Push(symbols.Count - 1);
        }
    }

    private (List<ParsedReference>, List<ParsedImport>) CollectReferences(Node root, List<(int Start, int End)> ranges)
    {
        var references = new List<ParsedReference>();
        var imports = new List<ParsedImport>();
        var seen = new HashSet<(int, string, EdgeKind, int)>();
        using var cursor = _references.Execute(root);
        foreach (var capture in cursor.Captures)
        {
            if (!capture.Name.StartsWith("ref.", StringComparison.Ordinal)) continue;
            var node = capture.Node;
            var kind = MapEdgeKind(capture.Name[4..]);
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;
            if (kind == EdgeKind.Imports)
            {
                var target = node.Text;
                if (target.Length == 0) continue;
                imports.Add(new ParsedImport(target, ImportIsPath(target), line));
                if (seen.Add((0, target, kind, line))) references.Add(new ParsedReference(0, target, kind, line, col));
                continue;
            }
            var name = ReferenceName(node);
            if (name.Length == 0) continue;
            var from = EnclosingSymbol(ranges, node.StartIndex);
            var adjusted = AdjustReferenceKind(kind, name);
            if (seen.Add((from, name, adjusted, line))) references.Add(new ParsedReference(from, name, adjusted, line, col));
        }
        return (references, imports);
    }

    /// <summary>包含该位置的最内层符号下标；ranges 按起点升序，后加入且仍包含的一定更内层。</summary>
    private static int EnclosingSymbol(List<(int Start, int End)> ranges, int position)
    {
        var best = 0;
        for (var i = 1; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (start > position) break;
            if (position < end) best = i;
        }
        return best;
    }

    private static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        var line = (nl >= 0 ? text[..nl] : text).TrimEnd('\r').Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    private static SymbolKind MapSymbolKind(string name) => name switch
    {
        "module" => SymbolKind.Module, "namespace" => SymbolKind.Namespace, "class" => SymbolKind.Class,
        "interface" => SymbolKind.Interface, "struct" => SymbolKind.Struct, "enum" => SymbolKind.Enum,
        "function" => SymbolKind.Function, "method" => SymbolKind.Method, "property" => SymbolKind.Property,
        "field" => SymbolKind.Field, "variable" => SymbolKind.Variable, "type_alias" => SymbolKind.TypeAlias,
        _ => throw new InvalidOperationException($"未知的定义 capture：@def.{name}"),
    };

    private static EdgeKind MapEdgeKind(string name) => name switch
    {
        "calls" => EdgeKind.Calls, "references" => EdgeKind.References, "inherits" => EdgeKind.Inherits,
        "implements" => EdgeKind.Implements, "imports" => EdgeKind.Imports,
        _ => throw new InvalidOperationException($"未知的引用 capture：@ref.{name}"),
    };

    public void Dispose()
    {
        _references.Dispose();
        _definitions.Dispose();
        _language.Dispose();
    }
}
```

`src/CodeAtlas.Indexing.TreeSitter/Adapters/TypeScriptAdapter.cs`:

```csharp
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TypeScriptAdapter() : LanguageAdapterBase(
    "typescript", "TypeScript", [".ts", ".mts", ".cts"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("typescript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition) => TypeScriptVisibility(definition);

    /// <summary>accessibility_modifier（public/private/protected）优先；否则定义（或其所在声明）直接被 export_statement 包裹则为 "export"。</summary>
    internal static string? TypeScriptVisibility(Node definition)
    {
        foreach (var child in definition.Children)
            if (child.Type == "accessibility_modifier") return child.Text;
        var parent = definition.Parent;
        if (parent is not null && parent.Type == "export_statement") return "export";
        if (parent is not null && parent.Parent is { } grand && grand.Type == "export_statement") return "export";
        return null;
    }
}
```

- [ ] **Step 7: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：9 个测试通过。若位置断言失败，用 `Console.WriteLine(tree.RootNode)` 核对 grammar 实际位置后修正测试，不要修改实现去凑数。

- [ ] **Step 8: 提交**

```bash
git add src/CodeAtlas.Indexing.TreeSitter tests/CodeAtlas.Indexing.TreeSitter.Tests
git commit -m "feat(treesitter): 适配器底座与 TypeScript 适配器"
```

---

### Task 6: TSX 与 JavaScript 适配器

**Files:**
- Create: `src/CodeAtlas.Indexing.TreeSitter/Adapters/TsxAdapter.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Adapters/JavaScriptAdapter.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/javascript.definitions.scm`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/javascript.references.scm`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/js/lib.js`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/tsx/View.tsx`
- Test: `tests/CodeAtlas.Indexing.TreeSitter.Tests/JavaScriptAdapterTests.cs`

**Interfaces:**
- Consumes: `LanguageAdapterBase`、`QueryResources`、`TypeScriptAdapter.TypeScriptVisibility`（Task 5）
- Produces: `TsxAdapter`（`LanguageId = "tsx"`，扩展名 `.tsx`，复用 typescript 的两份查询，grammar `"TSX"`）；`JavaScriptAdapter`（`LanguageId = "javascript"`，扩展名 `.js .mjs .cjs .jsx`，grammar `"JavaScript"`）

- [ ] **Step 1: 写 fixture**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/js/lib.js`:

```javascript
import x from './x';
class A extends B {
  x = 1;
  m() { this.n(); foo(); }
}
function f() { new A(); }
const g = () => 1;
const h = function() {};
```

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/tsx/View.tsx`:

```tsx
import React from 'react';
import { useStore } from './store';
export interface Props { title: string; }
export function View(props: Props) {
  const store = useStore();
  return <div onClick={() => store.save()}>{props.title}</div>;
}
```

- [ ] **Step 2: 写失败的测试**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/JavaScriptAdapterTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class JavaScriptAdapterTests
{
    [Fact]
    public void JavaScript_ExtractsDefinitionsAndReferences()
    {
        using var adapter = new JavaScriptAdapter();
        var f = adapter.Parse("lib.js", File.ReadAllText(TypeScriptAdapterTests.Fixture("js/lib.js")));

        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal(SymbolKind.Class, byQ["lib.js:A"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["lib.js:A.x"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["lib.js:A.m"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:f"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:g"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["lib.js:h"].Kind);
        Assert.Equal("javascript", f.Language);

        var m = f.Symbols.IndexOf(byQ["lib.js:A.m"]);
        var fn = f.Symbols.IndexOf(byQ["lib.js:f"]);
        var a = f.Symbols.IndexOf(byQ["lib.js:A"]);
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((m, "n", EdgeKind.Calls), refs);
        Assert.Contains((m, "foo", EdgeKind.Calls), refs);
        Assert.Contains((fn, "A", EdgeKind.Calls), refs);
        Assert.Contains((a, "B", EdgeKind.Inherits), refs);
        Assert.Contains((0, "./x", EdgeKind.Imports), refs);
    }

    [Fact]
    public void Tsx_ParsesJsxAndKeepsTypeScriptSemantics()
    {
        using var adapter = new TsxAdapter();
        var f = adapter.Parse("src/View.tsx", File.ReadAllText(TypeScriptAdapterTests.Fixture("tsx/View.tsx")));

        Assert.Equal("tsx", f.Language);
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal(SymbolKind.Interface, byQ["src/View.tsx:Props"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["src/View.tsx:View"].Kind);
        Assert.Equal("export", byQ["src/View.tsx:View"].Visibility);

        var view = f.Symbols.IndexOf(byQ["src/View.tsx:View"]);
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();
        Assert.Contains((view, "useStore", EdgeKind.Calls), refs);
        Assert.Contains((view, "save", EdgeKind.Calls), refs);
        Assert.Contains((view, "Props", EdgeKind.References), refs);
        Assert.Contains(f.Imports, i => i.Target == "react" && !i.IsPath);
        Assert.Contains(f.Imports, i => i.Target == "./store" && i.IsPath);
    }

    [Fact]
    public void Extensions_AreDistinctAcrossScriptAdapters()
    {
        using var ts = new TypeScriptAdapter();
        using var tsx = new TsxAdapter();
        using var js = new JavaScriptAdapter();
        var all = ts.Extensions.Concat(tsx.Extensions).Concat(js.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：编译失败。

- [ ] **Step 4: 写查询与适配器**

`src/CodeAtlas.Indexing.TreeSitter/Queries/javascript.definitions.scm`:

```scheme
(class_declaration name: (identifier) @name) @def.class
(function_declaration name: (identifier) @name) @def.function
(method_definition name: (property_identifier) @name) @def.method
(field_definition property: (property_identifier) @name) @def.field
(variable_declarator name: (identifier) @name value: [(arrow_function) (function_expression)]) @def.function
```

`src/CodeAtlas.Indexing.TreeSitter/Queries/javascript.references.scm`:

```scheme
(call_expression function: (identifier) @ref.calls)
(call_expression function: (member_expression property: (property_identifier) @ref.calls))
(new_expression constructor: (identifier) @ref.calls)
(class_heritage (identifier) @ref.inherits)
(import_statement source: (string (string_fragment) @ref.imports))
```

`src/CodeAtlas.Indexing.TreeSitter/Adapters/TsxAdapter.cs`:

```csharp
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class TsxAdapter() : LanguageAdapterBase(
    "tsx", "TSX", [".tsx"],
    QueryResources.Load("typescript.definitions.scm"),
    QueryResources.Load("typescript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition) => TypeScriptAdapter.TypeScriptVisibility(definition);
}
```

`src/CodeAtlas.Indexing.TreeSitter/Adapters/JavaScriptAdapter.cs`:

```csharp
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed class JavaScriptAdapter() : LanguageAdapterBase(
    "javascript", "JavaScript", [".js", ".mjs", ".cjs", ".jsx"],
    QueryResources.Load("javascript.definitions.scm"),
    QueryResources.Load("javascript.references.scm"))
{
    protected override string? ExtractVisibility(Node definition)
    {
        var parent = definition.Parent;
        if (parent is not null && parent.Type == "export_statement") return "export";
        if (parent is not null && parent.Parent is { } grand && grand.Type == "export_statement") return "export";
        return null;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：全部通过。

- [ ] **Step 6: 提交**

```bash
git add src/CodeAtlas.Indexing.TreeSitter tests/CodeAtlas.Indexing.TreeSitter.Tests
git commit -m "feat(treesitter): TSX 与 JavaScript 适配器"
```

---

### Task 7: C# 适配器（tree-sitter 版）

**Files:**
- Create: `src/CodeAtlas.Indexing.TreeSitter/Adapters/CSharpAdapter.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/csharp.definitions.scm`
- Create: `src/CodeAtlas.Indexing.TreeSitter/Queries/csharp.references.scm`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/cs/OrderService.cs`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/cs/Legacy.cs`
- Test: `tests/CodeAtlas.Indexing.TreeSitter.Tests/CSharpAdapterTests.cs`

**Interfaces:**
- Consumes: `LanguageAdapterBase`（Task 5）
- Produces: `CSharpAdapter`（`LanguageId = "csharp"`，扩展名 `.cs`，grammar `"C#"`）。规则：限定名前缀为空（由命名空间链构成）；文件级命名空间的作用范围扩展到整个文件；`base_list` 里 `I` 开头紧跟大写字母的名字视为 `implements`，其余 `inherits`；可见性取 `modifier` 子节点中的 public / private / protected / internal。

- [ ] **Step 1: 写 fixture**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/cs/OrderService.cs`（测试依赖行号）:

```csharp
using System;
using Microsoft.Extensions.Logging;
namespace Acme.Orders;
public interface IOrderRepo : IRepo<Order> { Order Find(int id); }
public record Order(int Id);
public struct Money { public decimal Amount; }
public enum Status { New, Done }
public delegate void Handler(int x);
public class OrderService : BaseService, IOrderRepo, IDisposable
{
    private readonly IOrderRepo _repo;
    private int _count, _other;
    public int Count { get; set; }
    public OrderService(IOrderRepo repo) { _repo = repo; }
    public Order Find(int id) { Log(id); return _repo.Find(id); }
    public void Run<T>(List<T> items) { var svc = new OrderService(null); var l = new List<Order>(); Helper.Do<int>(1); svc.Find(2); int Local(int a) => a; Local(1); Dispose(); }
    public void Dispose() { }
}
```

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/cs/Legacy.cs`:

```csharp
namespace Legacy.Block
{
    internal class Thing
    {
        protected void Go() { }
    }
}
class NoNamespace { }
```

- [ ] **Step 2: 写失败的测试**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/CSharpAdapterTests.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class CSharpAdapterTests
{
    private static ParsedFile ParseOrders()
    {
        using var adapter = new CSharpAdapter();
        return adapter.Parse("Orders/OrderService.cs", File.ReadAllText(TypeScriptAdapterTests.Fixture("cs/OrderService.cs")));
    }

    [Fact]
    public void FileScopedNamespace_ContainsEverythingBelowIt()
    {
        var f = ParseOrders();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["Acme.Orders"].Kind);
        Assert.Equal(SymbolKind.Interface, byQ["Acme.Orders.IOrderRepo"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.IOrderRepo.Find"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Acme.Orders.Order"].Kind);
        Assert.Equal(SymbolKind.Struct, byQ["Acme.Orders.Money"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.Money.Amount"].Kind);
        Assert.Equal(SymbolKind.Enum, byQ["Acme.Orders.Status"].Kind);
        Assert.Equal(SymbolKind.TypeAlias, byQ["Acme.Orders.Handler"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Acme.Orders.OrderService"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._repo"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._count"].Kind);
        Assert.Equal(SymbolKind.Field, byQ["Acme.Orders.OrderService._other"].Kind);
        Assert.Equal(SymbolKind.Property, byQ["Acme.Orders.OrderService.Count"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.OrderService"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Find"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Run"].Kind);
        Assert.Equal(SymbolKind.Function, byQ["Acme.Orders.OrderService.Run.Local"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Acme.Orders.OrderService.Dispose"].Kind);

        var ns = f.Symbols.Single(s => s.QualifiedName == "Acme.Orders");
        Assert.Equal(3, ns.StartLine);
        Assert.Equal(18, ns.EndLine);
        Assert.Equal(f.Symbols.IndexOf(ns), byQ["Acme.Orders.OrderService"].ParentIndex);
    }

    [Fact]
    public void BlockNamespace_AndNoNamespace()
    {
        using var adapter = new CSharpAdapter();
        var f = adapter.Parse("Legacy.cs", File.ReadAllText(TypeScriptAdapterTests.Fixture("cs/Legacy.cs")));
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);

        Assert.Equal(SymbolKind.Namespace, byQ["Legacy.Block"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["Legacy.Block.Thing"].Kind);
        Assert.Equal(SymbolKind.Method, byQ["Legacy.Block.Thing.Go"].Kind);
        Assert.Equal(SymbolKind.Class, byQ["NoNamespace"].Kind);
        Assert.Equal(0, byQ["NoNamespace"].ParentIndex);
        Assert.Equal("internal", byQ["Legacy.Block.Thing"].Visibility);
        Assert.Equal("protected", byQ["Legacy.Block.Thing.Go"].Visibility);
        Assert.Null(byQ["NoNamespace"].Visibility);
    }

    [Fact]
    public void BaseList_SplitsInheritsAndImplementsByIPrefix()
    {
        var f = ParseOrders();
        var svc = f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == "Acme.Orders.OrderService"));
        var repo = f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == "Acme.Orders.IOrderRepo"));
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();

        Assert.Contains((svc, "BaseService", EdgeKind.Inherits), refs);
        Assert.Contains((svc, "IOrderRepo", EdgeKind.Implements), refs);
        Assert.Contains((svc, "IDisposable", EdgeKind.Implements), refs);
        Assert.Contains((repo, "IRepo", EdgeKind.Implements), refs);
    }

    [Fact]
    public void Calls_IncludeMemberAccess_Generics_ObjectCreation_LocalFunctions()
    {
        var f = ParseOrders();
        int Idx(string q) => f.Symbols.IndexOf(f.Symbols.Single(s => s.QualifiedName == q));
        var find = Idx("Acme.Orders.OrderService.Find");
        var run = Idx("Acme.Orders.OrderService.Run");
        var refs = f.References.Select(r => (r.FromIndex, r.Name, r.Kind)).ToHashSet();

        Assert.Contains((find, "Log", EdgeKind.Calls), refs);
        Assert.Contains((find, "Find", EdgeKind.Calls), refs);
        Assert.Contains((run, "OrderService", EdgeKind.Calls), refs);
        Assert.Contains((run, "List", EdgeKind.Calls), refs);
        Assert.Contains((run, "Do", EdgeKind.Calls), refs);
        Assert.Contains((run, "Find", EdgeKind.Calls), refs);
        Assert.Contains((run, "Local", EdgeKind.Calls), refs);
        Assert.Contains((run, "Dispose", EdgeKind.Calls), refs);
        Assert.Contains((Idx("Acme.Orders.OrderService._repo"), "IOrderRepo", EdgeKind.References), refs);
    }

    [Fact]
    public void Usings_AreNamespaceImports_NotPaths()
    {
        var f = ParseOrders();
        Assert.Equal(2, f.Imports.Count);
        Assert.Contains(f.Imports, i => i.Target == "System" && !i.IsPath && i.Line == 1);
        Assert.Contains(f.Imports, i => i.Target == "Microsoft.Extensions.Logging" && !i.IsPath && i.Line == 2);
        Assert.Contains(f.References, r => r.FromIndex == 0 && r.Name == "Microsoft.Extensions.Logging" && r.Kind == EdgeKind.Imports);
    }

    [Fact]
    public void Visibility_FromModifiers()
    {
        var f = ParseOrders();
        var byQ = f.Symbols.ToDictionary(s => s.QualifiedName);
        Assert.Equal("public", byQ["Acme.Orders.OrderService"].Visibility);
        Assert.Equal("private", byQ["Acme.Orders.OrderService._repo"].Visibility);
        Assert.Equal("public", byQ["Acme.Orders.OrderService.Find"].Visibility);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：编译失败。

- [ ] **Step 4: 写查询与适配器**

`src/CodeAtlas.Indexing.TreeSitter/Queries/csharp.definitions.scm`:

```scheme
(namespace_declaration name: (_) @name) @def.namespace
(file_scoped_namespace_declaration name: (_) @name) @def.namespace
(class_declaration name: (identifier) @name) @def.class
(record_declaration name: (identifier) @name) @def.class
(struct_declaration name: (identifier) @name) @def.struct
(interface_declaration name: (identifier) @name) @def.interface
(enum_declaration name: (identifier) @name) @def.enum
(delegate_declaration name: (identifier) @name) @def.type_alias
(method_declaration name: (identifier) @name) @def.method
(constructor_declaration name: (identifier) @name) @def.method
(property_declaration name: (identifier) @name) @def.property
(field_declaration (variable_declaration (variable_declarator name: (identifier) @name) @def.field))
(local_function_statement name: (identifier) @name) @def.function
```

字段那一行把 `@def.field` 放在 `variable_declarator` 上而不是 `field_declaration` 上，原因：`private int _count, _other;` 是一个 `field_declaration` 带两个 declarator，底座按 `(def.StartIndex, def.EndIndex, kind)` 去重，若 def 节点是外层声明，第二个变量会被吞掉。

`src/CodeAtlas.Indexing.TreeSitter/Queries/csharp.references.scm`:

```scheme
(invocation_expression function: (identifier) @ref.calls)
(invocation_expression function: (generic_name (identifier) @ref.calls))
(invocation_expression function: (member_access_expression name: (identifier) @ref.calls))
(invocation_expression function: (member_access_expression name: (generic_name (identifier) @ref.calls)))
(object_creation_expression type: (identifier) @ref.calls)
(object_creation_expression type: (generic_name (identifier) @ref.calls))
(base_list (identifier) @ref.inherits)
(base_list (generic_name (identifier) @ref.inherits))
(variable_declaration type: (identifier) @ref.references)
(parameter type: (identifier) @ref.references)
(property_declaration type: (identifier) @ref.references)
(using_directive (identifier) @ref.imports)
(using_directive (qualified_name) @ref.imports)
```

`src/CodeAtlas.Indexing.TreeSitter/Adapters/CSharpAdapter.cs`:

```csharp
using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using TreeSitter;

namespace CodeAtlas.Indexing.TreeSitter.Adapters;

public sealed partial class CSharpAdapter() : LanguageAdapterBase(
    "csharp", "C#", [".cs"],
    QueryResources.Load("csharp.definitions.scm"),
    QueryResources.Load("csharp.references.scm"))
{
    private static readonly string[] VisibilityModifiers = ["public", "private", "protected", "internal"];

    protected override string ModuleQualifiedPrefix(string relativePath) => "";

    protected override EdgeKind AdjustReferenceKind(EdgeKind kind, string name) =>
        kind == EdgeKind.Inherits && InterfaceName().IsMatch(name) ? EdgeKind.Implements : kind;

    protected override Node DefinitionScope(Node definition, Node root) =>
        definition.Type == "file_scoped_namespace_declaration" ? root : definition;

    protected override string? ExtractVisibility(Node definition)
    {
        // 字段的可见性在 field_declaration 上，而定义节点是 variable_declarator
        var target = definition.Type == "variable_declarator" ? definition.Parent?.Parent : definition;
        if (target is null) return null;
        foreach (var child in target.Children)
            if (child.Type == "modifier" && Array.IndexOf(VisibilityModifiers, child.Text) >= 0) return child.Text;
        return null;
    }

    [GeneratedRegex("^I[A-Z]")]
    private static partial Regex InterfaceName();
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：全部通过。若 `_other` 缺失，说明字段 declarator 那行没替换；若 `Local` 的父级不是 `Run`，检查 `local_function_statement` 的范围是否落在 `Run` 的范围内。

- [ ] **Step 6: 提交**

```bash
git add src/CodeAtlas.Indexing.TreeSitter tests/CodeAtlas.Indexing.TreeSitter.Tests
git commit -m "feat(treesitter): C# 适配器（文件级命名空间、I 前缀接口、可见性）"
```

---

### Task 8: 仓库扫描、哈希与 TreeSitterIndexer

**Files:**
- Create: `src/CodeAtlas.Core/Hashing/FileHasher.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/RepositoryScanner.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/IndexOptions.cs`
- Create: `src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/svc.ts`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/app.ts`
- Create: `tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/util/format.ts`
- Test: `tests/CodeAtlas.Indexing.TreeSitter.Tests/TreeSitterIndexerTests.cs`

**Interfaces:**
- Consumes: `IndexDatabase.BeginBatch()`（Task 1）、`IndexWriter` / `IndexReader`（Task 2、3）、`IEdgeResolver` / `PendingReference` / `ResolveContext`（Task 4）、`ILanguageAdapter`（Task 5）
- Produces:
  - `static class FileHasher { string Sha256Hex(ReadOnlySpan<byte> bytes) }`
  - `record ScannedFile(string RelativePath, string FullPath, string Language, long Size)`，RelativePath 用 `/` 分隔
  - `class RepositoryScanner(IReadOnlyDictionary<string,string> extensionToLanguage, IReadOnlyCollection<string> excludedDirectories)`：`IEnumerable<ScannedFile> Scan(string root)`
  - `record IndexOptions { IReadOnlyList<string> ExcludedDirectories; long MaxFileBytes = 2_000_000; int MaxParallelism; bool Full; int WriteBatchSize = 200 }`
  - `record IndexProgress(string Phase, int Done, int Total, string? CurrentFile)`，Phase 取值 `scan / parse / write / resolve`
  - `record IndexReport(int FilesScanned, int FilesParsed, int FilesFailed, int FilesSkipped, int FilesRemoved, int FilesUnchanged, long TotalSymbols, long TotalEdges, TimeSpan Elapsed, IReadOnlyList<(string Path, string Error)> Errors)`
  - `class TreeSitterIndexer(IndexDatabase db, IReadOnlyList<ILanguageAdapter> adapters, IEdgeResolver resolver, IndexOptions? options = null)`：`Task<IndexReport> IndexAsync(string root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)`

流程：扫描 → 删除磁盘上已不存在的文件 → 并行（读字节、哈希、与库中 hash 比较、变更才解析）→ 串行分批写入（先删旧内容再写新内容，收集 PendingReference）→ 在一个事务里跑解析器并重挂悬空边。

- [ ] **Step 1: 写 fixture**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/util/format.ts`:

```typescript
export function helper(id: number): string { return String(id); }
```

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/svc.ts`:

```typescript
import { helper } from './util/format';
export class Svc {
  find(id: number): string { return helper(id); }
}
```

`tests/CodeAtlas.Indexing.TreeSitter.Tests/Fixtures/repo/src/app.ts`:

```typescript
import { Svc } from './svc';
export function main(): void { new Svc().find(1); fetch('x'); }
```

- [ ] **Step 2: 写失败的测试**

`tests/CodeAtlas.Indexing.TreeSitter.Tests/TreeSitterIndexerTests.cs`:

```csharp
using System.Text;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Indexing.TreeSitter.Tests;

public class TreeSitterIndexerTests : IDisposable
{
    private readonly string _repo;
    private readonly IndexDatabase _db = IndexDatabase.OpenInMemory();
    private readonly List<ILanguageAdapter> _adapters = [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

    public TreeSitterIndexerTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "codeatlas-repo-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(TypeScriptAdapterTests.Fixture("repo"), _repo);
    }

    private TreeSitterIndexer NewIndexer(IndexOptions? options = null) =>
        new(_db, _adapters, new HeuristicEdgeResolver(), options);

    private IndexReader Reader => new(_db);

    private SymbolRecord Sym(string qualifiedName) => Reader.FindByQualifiedName(qualifiedName).Single();

    [Fact]
    public async Task Indexer_FullIndex_ProducesExpectedSymbolsAndEdges()
    {
        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(3, report.FilesScanned);
        Assert.Equal(3, report.FilesParsed);
        Assert.Equal(0, report.FilesFailed);
        Assert.Empty(report.Errors);

        var main = Sym("src/app.ts:main");
        var find = Sym("src/svc.ts:Svc.find");
        var svc = Sym("src/svc.ts:Svc");
        var helper = Sym("src/util/format.ts:helper");

        var mainOut = Reader.GetNeighbors(main.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Contains(mainOut, n => n.Other?.Id == svc.Id && n.Edge.Confidence == 0.7);
        Assert.Contains(mainOut, n => n.Other?.Id == find.Id && n.Edge.Confidence == 0.7);
        Assert.Contains(mainOut, n => n.Edge.ToName == "external:fetch" && n.Edge.Confidence == 0.5);

        var findOut = Reader.GetNeighbors(find.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(helper.Id, findOut.Single().Other!.Id);
        Assert.Equal(0.7, findOut.Single().Edge.Confidence);
        Assert.Equal(3, findOut.Single().Edge.Line);

        var appModule = Sym("src/app.ts");
        var imports = Reader.GetNeighbors(appModule.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Imports });
        Assert.Equal(Sym("src/svc.ts").Id, imports.Single().Other!.Id);
    }

    [Fact]
    public async Task Indexer_SecondRun_ReparsesNothing()
    {
        await NewIndexer().IndexAsync(_repo);
        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(0, report.FilesParsed);
        Assert.Equal(3, report.FilesUnchanged);
        Assert.Equal(3, Reader.GetAllFiles().Count);
    }

    [Fact]
    public async Task Indexer_ModifiedFile_ReparsesOnlyThatFile_AndReattachesIncomingEdges()
    {
        await NewIndexer().IndexAsync(_repo);
        var oldHelperId = Sym("src/util/format.ts:helper").Id;
        var formatPath = Path.Combine(_repo, "src", "util", "format.ts");
        File.WriteAllText(formatPath, "export function extra(): void {}\n" + File.ReadAllText(formatPath));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesParsed);
        Assert.Equal(2, report.FilesUnchanged);
        var newHelper = Sym("src/util/format.ts:helper");
        Assert.NotEqual(oldHelperId, newHelper.Id);
        Assert.Equal(2, newHelper.StartLine);
        var findOut = Reader.GetNeighbors(Sym("src/svc.ts:Svc.find").Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Calls });
        Assert.Equal(newHelper.Id, findOut.Single().Other!.Id);
        Assert.Empty(Reader.GetDanglingEdges());
    }

    [Fact]
    public async Task Indexer_RemovedFile_DeletesSymbolsAndDanglesEdges()
    {
        await NewIndexer().IndexAsync(_repo);
        File.Delete(Path.Combine(_repo, "src", "util", "format.ts"));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesRemoved);
        Assert.Null(Reader.GetFile("src/util/format.ts"));
        Assert.Empty(Reader.FindByQualifiedName("src/util/format.ts:helper"));
        var dangling = Reader.GetDanglingEdges();
        Assert.Contains(dangling, e => e.ToName == "src/util/format.ts:helper" && e.Kind == EdgeKind.Calls);
        Assert.Contains(dangling, e => e.ToName == "src/util/format.ts" && e.Kind == EdgeKind.Imports);
    }

    [Fact]
    public async Task Indexer_ReadsBomFile_WithoutOffsetShift()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("export class Bom {}\n")).ToArray();
        File.WriteAllBytes(Path.Combine(_repo, "src", "bom.ts"), bytes);

        await NewIndexer().IndexAsync(_repo);

        var bom = Sym("src/bom.ts:Bom");
        Assert.Equal(1, bom.StartLine);
        Assert.Equal(8, bom.StartCol);
    }

    [Fact]
    public async Task Indexer_EmptyAndLargeFiles_AreIndexedOrSkipped()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "empty.ts"), "");
        File.WriteAllText(Path.Combine(_repo, "src", "large.ts"), new string('/', 2_500_000));

        var report = await NewIndexer().IndexAsync(_repo);

        Assert.Equal(1, report.FilesSkipped);
        Assert.Equal(FileStatus.Skipped, Reader.GetFile("src/large.ts")!.Status);
        Assert.Empty(Reader.GetSymbolsInFile(Reader.GetFile("src/large.ts")!.Id));
        Assert.Equal(FileStatus.Ok, Reader.GetFile("src/empty.ts")!.Status);
        Assert.Single(Reader.GetSymbolsInFile(Reader.GetFile("src/empty.ts")!.Id));
    }

    private sealed class ExplodingAdapter : ILanguageAdapter
    {
        public string LanguageId => "boom";
        public IReadOnlyList<string> Extensions => [".boom"];
        public ParsedFile Parse(string relativePath, string source) => throw new InvalidOperationException("kaboom");
    }

    [Fact]
    public async Task Indexer_ParseFailure_IsRecordedNotThrown()
    {
        File.WriteAllText(Path.Combine(_repo, "src", "x.boom"), "anything");
        var indexer = new TreeSitterIndexer(_db, [.. _adapters, new ExplodingAdapter()], new HeuristicEdgeResolver());

        var report = await indexer.IndexAsync(_repo);

        Assert.Equal(1, report.FilesFailed);
        Assert.Equal(3, report.FilesParsed);
        Assert.Contains(report.Errors, e => e.Path == "src/x.boom" && e.Error.Contains("kaboom"));
        Assert.Equal(FileStatus.ParseFailed, Reader.GetFile("src/x.boom")!.Status);
        Assert.NotNull(Reader.GetFile("src/app.ts"));
    }

    [Fact]
    public async Task Indexer_ExcludesConfiguredDirectories_AndReportsPhases()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_repo, "node_modules", "pkg", "index.ts"), "export function hidden() {}");
        var phases = new List<string>();
        var progress = new Progress<IndexProgress>(p => { lock (phases) phases.Add(p.Phase); });

        var report = await NewIndexer().IndexAsync(_repo, progress);
        await Task.Delay(50); // Progress<T> 回调是异步投递的

        Assert.Equal(3, report.FilesScanned);
        Assert.Empty(Reader.FindByName("hidden"));
        Assert.Contains("scan", phases);
        Assert.Contains("parse", phases);
        Assert.Contains("write", phases);
        Assert.Contains("resolve", phases);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var a in _adapters.OfType<IDisposable>()) a.Dispose();
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：编译失败。

- [ ] **Step 4: 写 FileHasher、RepositoryScanner、IndexOptions**

`src/CodeAtlas.Core/Hashing/FileHasher.cs`:

```csharp
using System.Security.Cryptography;

namespace CodeAtlas.Core.Hashing;

public static class FileHasher
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
```

`src/CodeAtlas.Indexing.TreeSitter/RepositoryScanner.cs`:

```csharp
namespace CodeAtlas.Indexing.TreeSitter;

public sealed record ScannedFile(string RelativePath, string FullPath, string Language, long Size);

/// <summary>按扩展名枚举仓库文件，跳过排除目录与重解析点（符号链接、junction）。</summary>
public sealed class RepositoryScanner(IReadOnlyDictionary<string, string> extensionToLanguage, IReadOnlyCollection<string> excludedDirectories)
{
    private readonly HashSet<string> _excluded = new(excludedDirectories, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ScannedFile> Scan(string root)
    {
        root = Path.GetFullPath(root);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo)
                {
                    if (!_excluded.Contains(entry.Name)) stack.Push(entry.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                if (!extensionToLanguage.TryGetValue(file.Extension, out var language)) continue;
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                yield return new ScannedFile(relative, file.FullName, language, file.Length);
            }
        }
    }
}
```

`src/CodeAtlas.Indexing.TreeSitter/IndexOptions.cs`:

```csharp
namespace CodeAtlas.Indexing.TreeSitter;

public sealed record IndexOptions
{
    public IReadOnlyList<string> ExcludedDirectories { get; init; } =
        ["node_modules", "bin", "obj", ".git", "dist", "build", ".codeatlas", ".vs", ".idea"];
    public long MaxFileBytes { get; init; } = 2_000_000;
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;
    public bool Full { get; init; }
    public int WriteBatchSize { get; init; } = 200;
}

public sealed record IndexProgress(string Phase, int Done, int Total, string? CurrentFile);

public sealed record IndexReport(
    int FilesScanned, int FilesParsed, int FilesFailed, int FilesSkipped, int FilesRemoved, int FilesUnchanged,
    long TotalSymbols, long TotalEdges, TimeSpan Elapsed, IReadOnlyList<(string Path, string Error)> Errors);
```

- [ ] **Step 5: 写 TreeSitterIndexer**

`src/CodeAtlas.Indexing.TreeSitter/TreeSitterIndexer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Indexing.TreeSitter;

public sealed class TreeSitterIndexer(IndexDatabase db, IReadOnlyList<ILanguageAdapter> adapters, IEdgeResolver resolver, IndexOptions? options = null)
{
    private readonly IndexOptions _options = options ?? new IndexOptions();

    private sealed record ParseResult(ScannedFile File, ParsedFile? Parsed, string Hash, FileStatus Status, string? Error, bool Unchanged = false);

    public async Task<IndexReport> IndexAsync(string root, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var reader = new IndexReader(db);
        var writer = new IndexWriter(db);
        var adapterByLanguage = adapters.ToDictionary(a => a.LanguageId);
        var extensionToLanguage = adapters
            .SelectMany(a => a.Extensions.Select(e => (Extension: e, a.LanguageId)))
            .ToDictionary(x => x.Extension, x => x.LanguageId, StringComparer.OrdinalIgnoreCase);

        progress?.Report(new IndexProgress("scan", 0, 0, null));
        var scanned = new RepositoryScanner(extensionToLanguage, _options.ExcludedDirectories).Scan(root).ToList();
        var existing = reader.GetAllFiles().ToDictionary(f => f.Path, StringComparer.Ordinal);

        // 1. 磁盘上已不存在的文件
        var scannedPaths = scanned.Select(s => s.RelativePath).ToHashSet(StringComparer.Ordinal);
        var removed = existing.Values.Where(f => !scannedPaths.Contains(f.Path)).ToList();
        if (removed.Count > 0)
        {
            using var batch = db.BeginBatch();
            foreach (var f in removed) writer.DeleteFile(f.Id);
            batch.Commit();
        }

        // 2. 并行：读、哈希、比较、解析
        var results = new ConcurrentBag<ParseResult>();
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(scanned, parallel, async (file, token) =>
        {
            try
            {
                if (file.Size > _options.MaxFileBytes)
                {
                    results.Add(new ParseResult(file, null, "", FileStatus.Skipped, null));
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(file.FullPath, token);
                var hash = FileHasher.Sha256Hex(bytes);
                if (!_options.Full && existing.TryGetValue(file.RelativePath, out var old) && old.Hash == hash && old.Status == FileStatus.Ok)
                {
                    results.Add(new ParseResult(file, null, hash, FileStatus.Ok, null, Unchanged: true));
                    return;
                }
                var parsed = adapterByLanguage[file.Language].Parse(file.RelativePath, Decode(bytes));
                results.Add(new ParseResult(file, parsed, hash, FileStatus.Ok, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new ParseResult(file, null, "", FileStatus.ParseFailed, ex.Message));
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(new IndexProgress("parse", d, scanned.Count, file.RelativePath));
            }
        });

        // 3. 串行分批写入
        var pending = new List<PendingReference>();
        var toWrite = results.Where(r => !r.Unchanged).OrderBy(r => r.File.RelativePath, StringComparer.Ordinal).ToList();
        var written = 0;
        foreach (var chunk in toWrite.Chunk(_options.WriteBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            using var batch = db.BeginBatch();
            foreach (var r in chunk)
            {
                if (existing.TryGetValue(r.File.RelativePath, out var old)) writer.DeleteFileContents(old.Id);
                var fileId = writer.UpsertFile(r.File.RelativePath, r.File.Language, r.Hash, r.File.Size, r.Status);
                if (r.Parsed is null) continue;
                var ids = writer.WriteSymbols(fileId, r.Parsed.Symbols, IndexSource.TreeSitter);
                writer.WriteImports(fileId, r.Parsed.Imports);
                foreach (var reference in r.Parsed.References)
                    pending.Add(new PendingReference(ids[reference.FromIndex], fileId, r.File.RelativePath, r.File.Language, reference.Name, reference.Kind, reference.Line));
            }
            batch.Commit();
            written += chunk.Length;
            progress?.Report(new IndexProgress("write", written, toWrite.Count, null));
        }

        // 4. 解析引用并重挂悬空边
        progress?.Report(new IndexProgress("resolve", 0, pending.Count, null));
        using (var batch = db.BeginBatch())
        {
            await resolver.ResolveAsync(new ResolveContext(db, pending), ct);
            writer.ReattachDanglingEdges();
            batch.Commit();
        }
        progress?.Report(new IndexProgress("resolve", pending.Count, pending.Count, null));

        var list = results.ToList();
        return new IndexReport(
            FilesScanned: scanned.Count,
            FilesParsed: list.Count(r => r.Parsed is not null),
            FilesFailed: list.Count(r => r.Status == FileStatus.ParseFailed),
            FilesSkipped: list.Count(r => r.Status == FileStatus.Skipped),
            FilesRemoved: removed.Count,
            FilesUnchanged: list.Count(r => r.Unchanged),
            TotalSymbols: db.Scalar<long>("SELECT COUNT(*) FROM symbols"),
            TotalEdges: db.Scalar<long>("SELECT COUNT(*) FROM edges"),
            Elapsed: stopwatch.Elapsed,
            Errors: list.Where(r => r.Error is not null).Select(r => (r.File.RelativePath, r.Error!)).ToList());
    }

    /// <summary>按 BOM 识别编码，默认 UTF-8；无效字节用替换字符，不抛异常。</summary>
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

```bash
dotnet test tests/CodeAtlas.Indexing.TreeSitter.Tests
```
预期：全部通过。若出现 "命令需要事务" 之类的 SQLite 异常，说明 Task 1 的 `IndexDatabase.CreateCommand` 没有把当前批次的事务挂到命令上。

- [ ] **Step 7: 提交**

```bash
git add src/CodeAtlas.Core/Hashing src/CodeAtlas.Indexing.TreeSitter tests/CodeAtlas.Indexing.TreeSitter.Tests
git commit -m "feat(treesitter): 仓库扫描、增量判断与并行索引器"
```

---

### Task 9: CLI（index / query / search）与真实仓库验收

**Files:**
- Create: `src/CodeAtlas.Cli/CodeAtlas.Cli.csproj`
- Create: `src/CodeAtlas.Cli/Program.cs`
- Create: `src/CodeAtlas.Cli/Commands/CommonOptions.cs`
- Create: `src/CodeAtlas.Cli/Commands/IndexCommand.cs`
- Create: `src/CodeAtlas.Cli/Commands/QueryCommand.cs`
- Create: `src/CodeAtlas.Cli/Commands/SearchCommand.cs`
- Create: `tests/CodeAtlas.Cli.Tests/CodeAtlas.Cli.Tests.csproj`
- Create: `docs/acceptance.md`
- Test: `tests/CodeAtlas.Cli.Tests/CliEndToEndTests.cs`

**Interfaces:**
- Consumes: `TreeSitterIndexer`、`IndexOptions`（Task 8）、四个适配器（Task 5–7）、`IndexReader`（Task 3）、`HeuristicEdgeResolver`（Task 4）
- Produces: `static class Program { int Main(string[] args); int Run(string[] args, TextWriter stdout, TextWriter stderr) }`。退出码：0 成功；1 未找到 / 索引有失败文件；2 参数错误。

命令：

```
codeatlas index  <repo> [--db <path>] [--full]
codeatlas query  <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds calls,implements,...] [--db <path>]
codeatlas search <repo> <text> [--limit N] [--db <path>]
```

`--db` 缺省为 `<repo>/.codeatlas/index.db`。`query` 不带 `--in` / `--out` 时两个方向都显示；默认 kinds 为 calls、implements、inherits、references、dispatches（不显示 contains 和 imports）。

- [ ] **Step 1: 创建项目**

```bash
cd D:/github/CodeAtlas
dotnet new console -n CodeAtlas.Cli -o src/CodeAtlas.Cli
dotnet add src/CodeAtlas.Cli reference src/CodeAtlas.Core src/CodeAtlas.Indexing.TreeSitter
dotnet new xunit -n CodeAtlas.Cli.Tests -o tests/CodeAtlas.Cli.Tests
rm tests/CodeAtlas.Cli.Tests/UnitTest1.cs
dotnet add tests/CodeAtlas.Cli.Tests reference src/CodeAtlas.Cli
dotnet sln add src/CodeAtlas.Cli tests/CodeAtlas.Cli.Tests
mkdir -p src/CodeAtlas.Cli/Commands
```

在 `src/CodeAtlas.Cli/CodeAtlas.Cli.csproj` 的第一个 `<PropertyGroup>` 内加入 `<AssemblyName>codeatlas</AssemblyName>` 和 `<InvariantGlobalization>true</InvariantGlobalization>`。

- [ ] **Step 2: 写失败的测试**

`tests/CodeAtlas.Cli.Tests/CliEndToEndTests.cs`:

```csharp
using CodeAtlas.Cli;

namespace CodeAtlas.Cli.Tests;

public class CliEndToEndTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codeatlas-cli-" + Guid.NewGuid().ToString("N"));

    public CliEndToEndTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "util"));
        File.WriteAllText(Path.Combine(_repo, "src", "util", "format.ts"), "export function helper(id: number): string { return String(id); }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "svc.ts"), "import { helper } from './util/format';\nexport class Svc {\n  find(id: number): string { return helper(id); }\n}\n");
        File.WriteAllText(Path.Combine(_repo, "src", "app.ts"), "import { Svc } from './svc';\nexport function main(): void { new Svc().find(1); fetch('x'); }\n");
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Index_ThenQuery_ShowsCallersAndCallees()
    {
        var index = Run("index", _repo);
        Assert.Equal(0, index.Code);
        Assert.Contains("files: 3", index.Out);
        Assert.True(File.Exists(Path.Combine(_repo, ".codeatlas", "index.db")));

        var query = Run("query", _repo, "find");
        Assert.Equal(0, query.Code);
        Assert.Contains("src/svc.ts:Svc.find", query.Out);
        Assert.Contains("→ calls  src/util/format.ts:helper  (0.7)", query.Out);
        Assert.Contains("← calls  src/app.ts:main  (0.7)", query.Out);
    }

    [Fact]
    public void Query_WithDepth_ExpandsTransitively()
    {
        Run("index", _repo);
        var query = Run("query", _repo, "src/app.ts:main", "--out", "--depth", "2");
        Assert.Equal(0, query.Code);
        Assert.Contains("→ calls  src/svc.ts:Svc.find", query.Out);
        Assert.Contains("    → calls  src/util/format.ts:helper", query.Out);
        Assert.Contains("external:fetch", query.Out);
    }

    [Fact]
    public void Query_UnknownSymbol_ReturnsOne()
    {
        Run("index", _repo);
        var query = Run("query", _repo, "doesNotExist");
        Assert.Equal(1, query.Code);
        Assert.Contains("未找到", query.Err);
    }

    [Fact]
    public void Search_ListsMatches()
    {
        Run("index", _repo);
        var search = Run("search", _repo, "hel");
        Assert.Equal(0, search.Code);
        Assert.Contains("src/util/format.ts:helper", search.Out);
        Assert.Contains("[function]", search.Out);
    }

    [Fact]
    public void UnknownCommand_PrintsUsage_ReturnsTwo()
    {
        var r = Run("frobnicate");
        Assert.Equal(2, r.Code);
        Assert.Contains("codeatlas index", r.Err);
    }

    [Fact]
    public void Index_WarnsWhenGitignoreLacksCodeatlas()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "node_modules/\n");
        var r = Run("index", _repo);
        Assert.Contains(".codeatlas/", r.Err);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

```bash
dotnet test tests/CodeAtlas.Cli.Tests
```
预期：编译失败。

- [ ] **Step 4: 写 CLI**

`src/CodeAtlas.Cli/Commands/CommonOptions.cs`:

```csharp
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;
using CodeAtlas.Indexing.TreeSitter.Adapters;

namespace CodeAtlas.Cli.Commands;

/// <summary>手写参数解析：位置参数 + "--name value" / "--flag"。</summary>
internal sealed class ParsedArgs
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);

    public static ParsedArgs Parse(IReadOnlyList<string> args, ISet<string> flags)
    {
        var result = new ParsedArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) { result.Positional.Add(a); continue; }
            if (flags.Contains(a)) { result.Options[a] = null; continue; }
            if (i + 1 >= args.Count) throw new ArgumentException($"选项 {a} 缺少值");
            result.Options[a] = args[++i];
        }
        return result;
    }

    public bool Has(string name) => Options.ContainsKey(name);
    public string? Value(string name) => Options.TryGetValue(name, out var v) ? v : null;
}

internal static class CommonOptions
{
    public static string DbPath(string repo, ParsedArgs args) =>
        args.Value("--db") ?? Path.Combine(Path.GetFullPath(repo), ".codeatlas", "index.db");

    public static List<ILanguageAdapter> CreateAdapters() =>
        [new TypeScriptAdapter(), new TsxAdapter(), new JavaScriptAdapter(), new CSharpAdapter()];

    public static IndexDatabase OpenExisting(string repo, ParsedArgs args, TextWriter stderr)
    {
        var path = DbPath(repo, args);
        if (!File.Exists(path))
        {
            stderr.WriteLine($"索引库不存在：{path}。先运行 codeatlas index {repo}");
            throw new FileNotFoundException(path);
        }
        return IndexDatabase.Open(path);
    }
}
```

`src/CodeAtlas.Cli/Commands/IndexCommand.cs`:

```csharp
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;
using CodeAtlas.Indexing.TreeSitter;

namespace CodeAtlas.Cli.Commands;

internal static class IndexCommand
{
    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine("用法：codeatlas index <repo> [--db <path>] [--full]"); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine($"目录不存在：{repo}"); return 2; }

        WarnIfNotIgnored(repo, stderr);

        var adapters = CommonOptions.CreateAdapters();
        try
        {
            using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
            var options = new IndexOptions { Full = args.Has("--full") };
            var indexer = new TreeSitterIndexer(db, adapters, new HeuristicEdgeResolver(), options);

            var lastPhase = "";
            var progress = new Progress<IndexProgress>(p =>
            {
                if (p.Phase != lastPhase) { lastPhase = p.Phase; stderr.WriteLine($"[{p.Phase}]"); }
                if (p.Phase == "parse" && (p.Done % 500 == 0 || p.Done == p.Total)) stderr.WriteLine($"  {p.Done}/{p.Total}");
            });
            var report = indexer.IndexAsync(repo, progress).GetAwaiter().GetResult();

            stdout.WriteLine($"files: {report.FilesScanned}  parsed: {report.FilesParsed}  unchanged: {report.FilesUnchanged}  skipped: {report.FilesSkipped}  removed: {report.FilesRemoved}  failed: {report.FilesFailed}");
            stdout.WriteLine($"symbols: {report.TotalSymbols}  edges: {report.TotalEdges}  elapsed: {report.Elapsed.TotalSeconds:F1}s");
            foreach (var (path, error) in report.Errors) stderr.WriteLine($"  失败 {path}: {error}");
            return report.FilesFailed > 0 ? 1 : 0;
        }
        finally
        {
            foreach (var a in adapters.OfType<IDisposable>()) a.Dispose();
        }
    }

    private static void WarnIfNotIgnored(string repo, TextWriter stderr)
    {
        var gitignore = Path.Combine(repo, ".gitignore");
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(gitignore)) return;
        var lines = File.Exists(gitignore) ? File.ReadAllLines(gitignore) : [];
        if (lines.Any(l => l.Trim() is ".codeatlas" or ".codeatlas/" or "/.codeatlas" or "/.codeatlas/")) return;
        stderr.WriteLine("提示：建议把 .codeatlas/ 加入该仓库的 .gitignore（索引库不应提交）。");
    }
}
```

`src/CodeAtlas.Cli/Commands/QueryCommand.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Commands;

internal static class QueryCommand
{
    private static readonly HashSet<EdgeKind> DefaultKinds =
        [EdgeKind.Calls, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References, EdgeKind.Dispatches];

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2)
        {
            stderr.WriteLine("用法：codeatlas query <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds a,b] [--db <path>]");
            return 2;
        }
        var repo = args.Positional[0];
        var target = args.Positional[1];
        var depth = int.TryParse(args.Value("--depth"), out var d) ? Math.Clamp(d, 1, 6) : 1;
        var direction = (args.Has("--in"), args.Has("--out")) switch
        {
            (true, false) => Direction.Incoming,
            (false, true) => Direction.Outgoing,
            _ => Direction.Both,
        };
        var kinds = args.Value("--kinds") is { } k
            ? k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(DbNames.ParseEdgeKind).ToHashSet()
            : DefaultKinds;

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(repo, args, stderr); }
        catch (FileNotFoundException) { return 1; }

        using (db)
        {
            var reader = new IndexReader(db);
            var matches = reader.FindByQualifiedName(target);
            if (matches.Count == 0) matches = reader.FindByName(target);
            if (matches.Count == 0) { stderr.WriteLine($"未找到符号：{target}"); return 1; }

            if (matches.Count > 3)
            {
                stdout.WriteLine($"有 {matches.Count} 个同名符号，请用限定名：");
                foreach (var m in matches) stdout.WriteLine($"  {m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {reader.GetFile(FilePath(reader, m))?.Path}:{m.StartLine}");
                return 0;
            }

            foreach (var m in matches)
            {
                stdout.WriteLine($"{m.QualifiedName}  [{DbNames.ToDb(m.Kind)}]  {FilePath(reader, m)}:{m.StartLine}");
                Print(reader, m.Id, direction, kinds, depth, indent: 1, visited: [m.Id], stdout);
                stdout.WriteLine();
            }
            return 0;
        }
    }

    private static string FilePath(IndexReader reader, SymbolRecord s) =>
        reader.GetAllFiles().FirstOrDefault(f => f.Id == s.FileId)?.Path ?? "?";

    private static void Print(IndexReader reader, long id, Direction direction, IReadOnlySet<EdgeKind> kinds, int depth, int indent, HashSet<long> visited, TextWriter stdout)
    {
        var pad = new string(' ', indent * 2);
        foreach (var n in reader.GetNeighbors(id, direction, kinds))
        {
            var isOutgoing = n.Edge.FromId == id;
            var arrow = isOutgoing ? "→" : "←";
            var name = n.Other?.QualifiedName ?? n.Edge.ToName + "  (悬空)";
            stdout.WriteLine($"{pad}{arrow} {DbNames.ToDb(n.Edge.Kind)}  {name}  ({n.Edge.Confidence:0.0})  @ line {n.Edge.Line}");
            if (depth > 1 && n.Other is { } other && other.Kind != SymbolKind.External && visited.Add(other.Id))
                Print(reader, other.Id, isOutgoing ? Direction.Outgoing : Direction.Incoming, kinds, depth - 1, indent + 1, visited, stdout);
        }
    }
}
```

`src/CodeAtlas.Cli/Commands/SearchCommand.cs`:

```csharp
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Commands;

internal static class SearchCommand
{
    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 2) { stderr.WriteLine("用法：codeatlas search <repo> <text> [--limit N] [--db <path>]"); return 2; }
        var limit = int.TryParse(args.Value("--limit"), out var l) ? Math.Clamp(l, 1, 500) : 30;

        IndexDatabase db;
        try { db = CommonOptions.OpenExisting(args.Positional[0], args, stderr); }
        catch (FileNotFoundException) { return 1; }

        using (db)
        {
            var reader = new IndexReader(db);
            var files = reader.GetAllFiles().ToDictionary(f => f.Id, f => f.Path);
            var hits = reader.SearchSymbols(args.Positional[1], limit);
            if (hits.Count == 0) { stdout.WriteLine("没有匹配。"); return 0; }
            foreach (var h in hits)
                stdout.WriteLine($"{h.QualifiedName}  [{DbNames.ToDb(h.Kind)}]  {files.GetValueOrDefault(h.FileId, "?")}:{h.StartLine}");
            return 0;
        }
    }
}
```

`src/CodeAtlas.Cli/Program.cs`:

```csharp
using CodeAtlas.Cli.Commands;

namespace CodeAtlas.Cli;

public static class Program
{
    private const string Usage = """
        CodeAtlas — 代码地图索引器（里程碑 1：tree-sitter + CLI）

        用法：
          codeatlas index  <repo> [--db <path>] [--full]
          codeatlas query  <repo> <name|qualified-name> [--in] [--out] [--depth N] [--kinds calls,implements,...] [--db <path>]
          codeatlas search <repo> <text> [--limit N] [--db <path>]
        """;

    private static readonly HashSet<string> Flags = ["--full", "--in", "--out"];

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0) { stderr.WriteLine(Usage); return 2; }
        ParsedArgs parsed;
        try { parsed = ParsedArgs.Parse(args.Skip(1).ToList(), Flags); }
        catch (ArgumentException ex) { stderr.WriteLine(ex.Message); stderr.WriteLine(Usage); return 2; }

        try
        {
            return args[0] switch
            {
                "index" => IndexCommand.Run(parsed, stdout, stderr),
                "query" => QueryCommand.Run(parsed, stdout, stderr),
                "search" => SearchCommand.Run(parsed, stdout, stderr),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"未知命令：{command}");
        stderr.WriteLine(Usage);
        return 2;
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

```bash
dotnet test
```
预期：所有测试项目全部通过。

- [ ] **Step 6: 提交**

```bash
git add src/CodeAtlas.Cli tests/CodeAtlas.Cli.Tests
git commit -m "feat(cli): index / query / search 子命令"
```

- [ ] **Step 7: 在真实仓库上验收（ccs-ui，TypeScript）**

```bash
cd D:/github/CodeAtlas
dotnet run --project src/CodeAtlas.Cli -c Release -- index D:/gitlab/RC_SZ/ccs-backend/ccs-ui
dotnet run --project src/CodeAtlas.Cli -c Release -- search D:/gitlab/RC_SZ/ccs-backend/ccs-ui login
```

从搜索结果里挑 3 个符号，分别 `query`，打开对应源文件人工核对：它调用的东西是否都在图里、confidence 是否合理、有没有明显漏掉的调用。

- [ ] **Step 8: 在真实仓库上验收（ccs-backend 全仓库，C# 走 tree-sitter）**

```bash
dotnet run --project src/CodeAtlas.Cli -c Release -- index D:/gitlab/RC_SZ/ccs-backend
```

记录 `elapsed`、`symbols`、`edges`、`failed`。目标：tree-sitter 全量 2 分钟内（spec 8.2）。然后挑 5 个方法 `query`（建议：一个 Controller action、一个 Service 方法、一个 Repository 方法、一个接口方法、一个被广泛调用的工具方法），人工核对。

- [ ] **Step 9: 写验收记录**

`docs/acceptance.md`，按下面模板填入真实数字与核对结果：

```markdown
# 验收记录

## 里程碑 1（tree-sitter + CLI）— 2026-MM-DD

### ccs-ui（TypeScript / TSX）
| 指标 | 值 |
|---|---|
| files | |
| symbols | |
| edges | |
| elapsed | |
| failed | |

抽查（符号 / 预期 / 实际 / 结论）：
1.
2.
3.

### ccs-backend（C#，tree-sitter 兜底）
| 指标 | 值 |
|---|---|
| files | |
| symbols | |
| edges | |
| elapsed（目标 ≤ 120s） | |
| failed | |

抽查：
1. Controller action：
2. Service 方法：
3. Repository 方法：
4. 接口方法（预期能看到 implements 边）：
5. 高频工具方法（预期多候选折叠为 0.3）：

### 发现的问题
-
```

- [ ] **Step 10: 提交验收记录**

```bash
git add docs/acceptance.md
git commit -m "docs: 里程碑 1 在 ccs-ui 与 ccs-backend 上的验收记录"
```

---

## Self-Review（已执行）

**Spec coverage**：第 2 节（架构中的 Core / TreeSitter / Cli 三个项目）→ Task 1、5、9；第 3 节表结构 → Task 1，模型与写入 → Task 2，读取 → Task 3，增量与悬空边 → Task 2、8；第 4.1 节底座与 .scm 约定 → Task 5；4.2 四级解析 → Task 4；4.3 `IEdgeResolver` → Task 4；里程碑 1 → Task 9 验收。4.4 Roslyn、4.5 DI、4.7 间接调用链、第 5、6、7 节不在本计划范围，属于里程碑 2–5。

**Placeholder scan**：验收记录模板中的空表格是刻意留给真实数据的，不是占位。其余步骤均含完整代码。

**Type consistency**：`IndexDatabase.BeginBatch()` 在 Task 1 定义（返回 `IndexDatabase.Batch`，有 `Commit()`），Task 8 使用；`ParsedSymbol` 的 `ParentIndex` 在 Task 2 定义为 `int?`，Task 5 底座用 `parentIndex`（int）传入隐式转换；`Neighbor.Other` 可空在 Task 3 定义，Task 9 按可空处理；`DbNames.ParseEdgeKind` 在 Task 2 定义，Task 9 用于 `--kinds`；`TypeScriptAdapterTests.Fixture` 在 Task 5 声明为 `internal static`，Task 6、7、8 复用。

**Review Focus 对应测试**：BOM → Task 8 `Indexer_ReadsBomFile_WithoutOffsetShift`；语法错误 → Task 5 `Parse_FileWithSyntaxError_StillExtractsValidSymbols`；大量同名 → Task 4 `Resolve_ManyCandidates_CapsAt20Edges`；删除文件 → Task 8 `Indexer_RemovedFile_DeletesSymbolsAndDanglesEdges`；空/超大文件 → Task 8 `Indexer_EmptyAndLargeFiles_AreIndexedOrSkipped`。

**已知限制（写入 acceptance.md 的"发现的问题"或后续计划）**：
- 列号按 tree-sitter 的 UTF-8 字节列计算，含非 ASCII 字符的行会偏移。
- 增量索引只重新解析变更文件的引用；未变更文件中原本指向 external 的引用，在新文件补上定义后不会自动升级，需 `--full`。
- TS 的 JSX 组件引用（`<Foo />`）、对象字面量方法、`export default` 匿名函数未纳入 M1 查询。
- C# 的 `base_list` 用 `I` 前缀区分接口，命名不规范时会判错；里程碑 3 由 Roslyn 修正。
