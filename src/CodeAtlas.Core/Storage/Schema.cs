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
            status TEXT NOT NULL DEFAULT 'ok',
            roslyn_at TEXT
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
            evidence_line INTEGER,
            pos INTEGER,
            ctx TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_edges_from ON edges(from_id);
        CREATE INDEX IF NOT EXISTS ix_edges_to ON edges(to_id);
        CREATE INDEX IF NOT EXISTS ix_edges_dangling ON edges(to_name) WHERE to_id IS NULL;
        CREATE INDEX IF NOT EXISTS ix_edges_file ON edges(file_id);

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

        -- 架构事实（容器图）：整表替换，每次索引后重算。列表字段存 JSON。
        CREATE TABLE IF NOT EXISTS arch_nodes (
            id INTEGER PRIMARY KEY,
            key TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            subkind TEXT NOT NULL,
            name TEXT NOT NULL,
            path TEXT,
            source TEXT NOT NULL,
            members_json TEXT NOT NULL,
            ports_json TEXT NOT NULL,
            evidence_json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS arch_edges (
            id INTEGER PRIMARY KEY,
            from_key TEXT NOT NULL,
            to_key TEXT NOT NULL,
            protocol TEXT NOT NULL,
            confidence REAL NOT NULL,
            source TEXT NOT NULL,
            config_key TEXT,
            url TEXT,
            evidence_json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS arch_outbound (
            id INTEGER PRIMARY KEY,
            container_key TEXT NOT NULL,
            rule_id TEXT NOT NULL,
            protocol TEXT NOT NULL,
            client_name TEXT,
            config_key TEXT,
            url TEXT,
            file TEXT NOT NULL,
            line INTEGER NOT NULL,
            target_key TEXT
        );
        CREATE TABLE IF NOT EXISTS arch_conflicts (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            message TEXT NOT NULL
        );

        -- git 行为统计（按文件），索引后重算
        CREATE TABLE IF NOT EXISTS git_file_stats (
            path TEXT PRIMARY KEY,
            revisions INTEGER NOT NULL,
            added INTEGER NOT NULL,
            deleted INTEGER NOT NULL,
            first_commit TEXT NOT NULL,
            last_commit TEXT NOT NULL,
            authors INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS git_coupling (
            id INTEGER PRIMARY KEY,
            path_a TEXT NOT NULL,
            path_b TEXT NOT NULL,
            shared INTEGER NOT NULL,
            degree REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_git_coupling_a ON git_coupling(path_a);
        CREATE INDEX IF NOT EXISTS ix_git_coupling_b ON git_coupling(path_b);
        CREATE TABLE IF NOT EXISTS git_meta (
            id INTEGER PRIMARY KEY,
            commits INTEGER NOT NULL,
            commits_skipped INTEGER NOT NULL,
            error TEXT,
            scanned_at TEXT NOT NULL
        );

        -- 入口（控制器动作、常驻服务、定时任务、分发处理器、前端页面）与出口路径对上入口的匹配
        CREATE TABLE IF NOT EXISTS entry_points (
            id INTEGER PRIMARY KEY,
            symbol_id INTEGER NOT NULL,
            container_key TEXT NOT NULL,
            rule_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            route TEXT,
            http_method TEXT,
            file TEXT NOT NULL,
            line INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_entry_points_container ON entry_points(container_key);
        CREATE TABLE IF NOT EXISTS route_matches (
            id INTEGER PRIMARY KEY,
            from_container TEXT NOT NULL,
            file TEXT NOT NULL,
            line INTEGER NOT NULL,
            path TEXT NOT NULL,
            entry_symbol_id INTEGER NOT NULL,
            to_container TEXT NOT NULL
        );

        -- 声明规则（NetArchTest / ArchUnitNET 测试还原、architecture.yaml 分层）：整表替换
        -- kind: forbid-dependency | require-dependency | placement；pattern 是命名空间前缀或 contains 片段，match: prefix | contains | exact
        CREATE TABLE IF NOT EXISTS arch_rules (
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            from_pattern TEXT NOT NULL,
            from_match TEXT NOT NULL,
            to_pattern TEXT,
            to_match TEXT,
            file TEXT NOT NULL,
            line INTEGER NOT NULL,
            test_name TEXT,
            source TEXT NOT NULL
        );

        -- AI 叙述缓存不在这里：单独放 narratives.db（NarrativeCache），全量重建索引不会把它清掉

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
