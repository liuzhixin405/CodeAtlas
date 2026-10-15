using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Server;

/// <summary>
/// 模块关系的取数：只查中心单元（depth 2 时再加留得下的 depth 1 单元）的成员、这些成员的出入边、边另一端的父链与所在文件，
/// 每一步一次批量查询，不加载整库。服务（IndexService.Relations）与 CLI（query --relations）共用。
/// <para>单元取<b>最内层</b>的命名类型：嵌套类（Handler 里的 Validator、实体里的值对象、Java 内部类）有自己的依赖与职责，并到外层会把它们的边算到外层头上；
/// 而且光标停在嵌套类的方法里时，中心应该就是光标所在的那个类。外层与嵌套类之间的边照常画出来（同一组件里）。</para>
/// </summary>
public static class RelationsQuery
{
    private const string SymbolColumns = "s.id, s.file_id, s.kind, s.name, s.qualified_name, s.signature, s.start_line, s.start_col, s.end_line, s.end_col, s.parent_id, s.visibility, s.source";

    /// <summary>参与汇总的边（种类、置信度、分派的 via 在 SQL 里先筛一遍；RelationsBuilder.Classify 是同一条规则）。别名 e。</summary>
    private const string EdgeFilter =
        "e.to_id IS NOT NULL AND e.kind IN ('calls', 'dispatches', 'references', 'inherits', 'implements') AND e.confidence >= 0.4 AND (e.kind <> 'dispatches' OR e.via LIKE 'dispatch:%')";

    /// <summary>符号不存在返回 null。</summary>
    public static RelationsDto? Build(IndexDatabase db, long id, RelationsOptions options)
    {
        var g = new Graph(db);
        g.Load([id]);
        if (!g.Symbols.ContainsKey(id)) return null;
        var focus = g.ResolveUnits([id]).GetValueOrDefault(id) ?? id;
        g.RegisterUnit(focus);
        options = options with { Depth = Math.Clamp(options.Depth, 1, 2) };

        var edges = g.EdgesOfUnits([focus]);
        if (options.Depth >= 2)
        {
            // 只展开最终留得下的 depth 1 单元（与 RelationsBuilder 的截断同一个排序），外部类型不展开（实现 IRequestHandler 的类成百上千）
            var ring = RelationsBuilder.FirstRing(focus, edges, g.Members, g.Units, options.Tests);
            var expand = ring.Take(RelationsBuilder.MaxNodes - 1).Where(u => !g.Units[u].External).ToList();
            edges.AddRange(g.EdgesOfUnits(expand));
        }
        return RelationsBuilder.Build(focus, edges, g.Members, g.Units, options, kept => g.Components(kept, focus));
    }

    private static bool IsUnitKind(SymbolKind k) =>
        k is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Module or SymbolKind.External;

    private static bool IsType(SymbolKind k) => k is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum;

    private sealed class Graph(IndexDatabase db)
    {
        public readonly Dictionary<long, SymbolRecord> Symbols = [];
        public readonly Dictionary<long, RelationMember> Members = [];
        public readonly Dictionary<long, RelationUnit> Units = [];
        private readonly Dictionary<long, long?> _unitOf = [];
        private readonly Dictionary<long, string> _paths = [];
        private readonly HashSet<long> _expanded = [];

        public void Load(IEnumerable<long> ids)
        {
            var missing = ids.Where(i => !Symbols.ContainsKey(i)).Distinct().ToList();
            if (missing.Count == 0) return;
            foreach (var s in ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.id IN ({Ids(missing)})")) Symbols[s.Id] = s;
        }

        private string PathOf(long fileId)
        {
            if (_paths.TryGetValue(fileId, out var p)) return p;
            LoadPaths([fileId]);
            return _paths.GetValueOrDefault(fileId, IndexWriter.ExternalFilePath);
        }

        private void LoadPaths(IEnumerable<long> fileIds)
        {
            var missing = fileIds.Where(f => !_paths.ContainsKey(f)).Distinct().ToList();
            if (missing.Count == 0) return;
            using var cmd = db.CreateCommand($"SELECT id, path FROM files WHERE id IN ({Ids(missing)})");
            using var r = cmd.ExecuteReader();
            while (r.Read()) _paths[r.GetInt64(0)] = r.GetString(1);
        }

        /// <summary>
        /// 每个符号的单元：沿父链往上第一个类型 / 模块 / 外部符号（最内层类型）；父链上都没有（C# 文件里只在命名空间下的东西）就用所在文件的模块符号；
        /// 连模块都没有为 null。父链按层批量取。
        /// </summary>
        public Dictionary<long, long?> ResolveUnits(IEnumerable<long> ids)
        {
            var cursor = new Dictionary<long, long?>();
            foreach (var id in ids) if (!_unitOf.ContainsKey(id)) cursor[id] = id;
            var noUnit = new List<long>();
            for (var round = 0; cursor.Count > 0 && round < 64; round++)
            {
                Load(cursor.Values.OfType<long>());
                foreach (var (id, cur) in cursor.ToList())
                {
                    if (cur is not { } c || !Symbols.TryGetValue(c, out var s)) { noUnit.Add(id); cursor.Remove(id); continue; }
                    if (IsUnitKind(s.Kind)) { _unitOf[id] = s.Id; cursor.Remove(id); continue; }
                    if (c != id && _unitOf.TryGetValue(c, out var known)) { _unitOf[id] = known; cursor.Remove(id); continue; }
                    cursor[id] = s.ParentId;
                }
            }
            noUnit.AddRange(cursor.Keys);

            // 兜底：所在文件的模块符号
            var files = noUnit.Where(Symbols.ContainsKey).Select(i => Symbols[i].FileId).Distinct().ToList();
            var moduleOfFile = new Dictionary<long, long>();
            if (files.Count > 0)
                foreach (var m in ReadSymbols($"SELECT {SymbolColumns} FROM symbols s WHERE s.kind = 'module' AND s.file_id IN ({Ids(files)}) ORDER BY s.id"))
                {
                    Symbols.TryAdd(m.Id, m);
                    moduleOfFile.TryAdd(m.FileId, m.Id);
                }
            foreach (var id in noUnit)
                _unitOf[id] = Symbols.TryGetValue(id, out var s) && moduleOfFile.TryGetValue(s.FileId, out var mod) ? mod : null;

            return ids.Distinct().ToDictionary(i => i, i => _unitOf.GetValueOrDefault(i));
        }

        public void RegisterUnit(long unitId)
        {
            if (Units.ContainsKey(unitId)) return;
            var s = Symbols[unitId];
            var path = PathOf(s.FileId);
            var external = s.Kind == SymbolKind.External;
            var label = new MapSymbol(s.Id, s.Kind, s.QualifiedName, path, s.ParentId).Name;
            Units[unitId] = new RelationUnit(Dto(s, path), label, external, !external && PipelineBuilder.IsTest(path, s.QualifiedName));
            Members.TryAdd(unitId, new RelationMember(unitId, unitId, label, s.Kind is SymbolKind.Class or SymbolKind.Struct));
        }

        private void RegisterMember(SymbolRecord s, long unitId)
        {
            RegisterUnit(unitId); // 单元自身在这里登记（Constructs = 类 / 结构）
            if (Members.ContainsKey(s.Id)) return;
            var unit = Symbols[unitId];
            var unitLabel = Units[unitId].Label;
            var name = new MapSymbol(s.Id, s.Kind, s.QualifiedName, PathOf(s.FileId), s.ParentId).Name;
            string label;
            var constructs = false;
            if (IsType(unit.Kind))
            {
                constructs = s.Kind is SymbolKind.Method or SymbolKind.Function && s.ParentId == unitId
                             && (s.Name is ".ctor" or "constructor" or "__init__" || MapBuilder.StripGenericArgs(s.Name) == MapBuilder.StripGenericArgs(unit.Name));
                label = constructs ? unitLabel : unitLabel + "." + name;
            }
            else if (unit.Kind == SymbolKind.Module)
                label = FlowBuilder.GoReceiver(new MapSymbol(s.Id, s.Kind, s.QualifiedName, PathOf(s.FileId), s.ParentId)) is { } recv ? recv + "." + name : name;
            else label = unitLabel;
            Members[s.Id] = new RelationMember(s.Id, unitId, label, constructs);
        }

        /// <summary>这些单元的全部成员（不进嵌套类型：它们是自己的单元）的出入边；边另一端的成员与单元一并登记。</summary>
        public List<RelationRawEdge> EdgesOfUnits(IReadOnlyCollection<long> unitIds)
        {
            var todo = unitIds.Where(_expanded.Add).ToList();
            if (todo.Count == 0) return [];
            foreach (var u in todo) RegisterUnit(u);

            // CROSS JOIN 让 SQLite 以 CTE 为外层逐行查索引；写成 JOIN 时规划器会去全表扫 symbols（10 万符号的库每次 20ms）
            var memberIds = new List<long>();
            using (var cmd = db.CreateCommand($"""
                WITH RECURSIVE m(id, unit) AS (
                    SELECT id, id FROM symbols WHERE id IN ({Ids(todo)})
                    UNION ALL
                    SELECT s.id, m.unit FROM m CROSS JOIN symbols s ON s.parent_id = m.id
                    WHERE s.kind NOT IN ('class', 'interface', 'struct', 'enum', 'module')
                )
                SELECT m.unit, {SymbolColumns} FROM m CROSS JOIN symbols s ON s.id = m.id
                """))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var unit = r.GetInt64(0);
                    var s = ReadSymbol(r, 1);
                    Symbols.TryAdd(s.Id, s);
                    _unitOf[s.Id] = unit;
                    memberIds.Add(s.Id);
                }
            }
            LoadPaths(memberIds.Select(i => Symbols[i].FileId));
            foreach (var mid in memberIds) RegisterMember(Symbols[mid], _unitOf[mid]!.Value);

            var raw = new List<(long Id, long From, long To, EdgeKind Kind, double Conf, string? Via, long File, int Line, int? Pos)>();
            var ids = Ids(memberIds);
            const string Columns = "e.id, e.from_id, e.to_id, e.kind, e.confidence, e.via, e.file_id, e.line, e.pos";
            using (var cmd = db.CreateCommand($"""
                SELECT {Columns} FROM edges e WHERE e.from_id IN ({ids}) AND {EdgeFilter}
                UNION ALL
                SELECT {Columns} FROM edges e WHERE e.to_id IN ({ids}) AND {EdgeFilter}
                """))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    raw.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), DbNames.ParseEdgeKind(r.GetString(3)), r.GetDouble(4),
                        r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8)));

            // 边另一端：解析单元、登记成员
            var others = raw.SelectMany(e => new[] { e.From, e.To }).Where(i => !Members.ContainsKey(i)).Distinct().ToList();
            var unitOf = ResolveUnits(others);
            LoadPaths(Symbols.Values.Select(s => s.FileId).Concat(raw.Select(e => e.File))); // 另一端与它们的单元、边所在文件
            foreach (var o in others)
                if (unitOf.GetValueOrDefault(o) is { } u && Symbols.TryGetValue(o, out var s)) RegisterMember(s, u);

            return raw.Select(e => new RelationRawEdge(e.Id, e.From, e.To, e.Kind, e.Conf, e.Via, PathOf(e.File), e.Line, e.Pos)).ToList();
        }

        /// <summary>留下的仓库内单元的组件：容器来自索引里的架构事实；C# 单元的命名空间沿父链找。</summary>
        public IReadOnlyDictionary<long, RelationComponentRef> Components(IReadOnlyList<long> units, long focus)
        {
            var containers = ArchStorage.Read(db).Nodes.Where(n => n.Kind == ArchNodeKind.Container).ToList();
            var places = units.Select(u =>
            {
                var s = Symbols[u];
                var path = PathOf(s.FileId);
                return new RelationUnitPlace(u, path, s.QualifiedName, MapBuilder.IsCSharp(new MapSymbol(s.Id, s.Kind, s.QualifiedName, path, s.ParentId)) ? NamespaceOf(u) ?? "" : null);
            }).ToList();
            return RelationsBuilder.AssignComponents(places, focus, containers, IsPackageMember, NamespaceBase);
        }

        private string? NamespaceOf(long id)
        {
            long? cur = id;
            for (var i = 0; i < 64 && cur is { } c; i++)
            {
                Load([c]);
                if (!Symbols.TryGetValue(c, out var s)) return null;
                if (s.Kind == SymbolKind.Namespace) return s.QualifiedName;
                cur = s.ParentId;
            }
            return null;
        }

        /// <summary>DSM 的"包语言成员"：成员目录下的文件多数是 .go / .py（DSM 按符号数，这里按文件数近似，不扫符号表）。</summary>
        private bool IsPackageMember(string member) => MemberFacts.For(db).Package.GetOrAdd(member, m =>
        {
            var (where, p) = UnderMember(m);
            using var cmd = db.CreateCommand($"SELECT COUNT(*), COALESCE(SUM(path LIKE '%.go' OR path LIKE '%.py'), 0) FROM files WHERE {where}", ("$p", p));
            using var r = cmd.ExecuteReader();
            return r.Read() && r.GetInt64(0) > 0 && r.GetInt64(1) * 2 > r.GetInt64(0);
        });

        /// <summary>成员目录下 C# 命名空间的多数派公共前缀（与 DSM 钻进项目时的"主命名空间"同一算法）。</summary>
        private string NamespaceBase(string member) => MemberFacts.For(db).Base.GetOrAdd(member, m =>
        {
            var (where, p) = UnderMember(m, "f.");
            var names = new List<string>();
            using var cmd = db.CreateCommand($"SELECT DISTINCT s.qualified_name FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.kind = 'namespace' AND {where}", ("$p", p));
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return DsmBuilder.DominantPrefix(names, m);
        });

        /// <summary>成员 "" 是主根：不含外部根的 ../… 文件（与 RuleContext.Under 一致）。</summary>
        private static (string Where, string Pattern) UnderMember(string member, string alias = "")
        {
            if (member.Length == 0) return ($"{alias}path <> '{IndexWriter.ExternalFilePath}' AND {alias}path NOT LIKE '../%'", "");
            return ($"({alias}path = $p OR substr({alias}path, 1, length($p) + 1) = $p || '/')", member);
        }

        private SymbolDto Dto(SymbolRecord s, string path) => new(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, s.Signature, path,
            s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Visibility);

        private List<SymbolRecord> ReadSymbols(string sql)
        {
            var list = new List<SymbolRecord>();
            using var cmd = db.CreateCommand(sql);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadSymbol(r, 0));
            return list;
        }
    }

    /// <summary>
    /// 成员目录级的事实（是不是包语言成员、主命名空间）要扫整张 symbols 表（单体项目里几十毫秒），按库连接缓存；
    /// 指纹 = PRAGMA data_version（别的连接提交过就变：服务的写连接重建索引后失效）+ total_changes()（本连接自己写过：测试的内存库）。
    /// </summary>
    private sealed class MemberFacts
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IndexDatabase, MemberFacts> Cache = new();
        private string _fingerprint = "";
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Package { get; private set; } = new(StringComparer.Ordinal);
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Base { get; private set; } = new(StringComparer.Ordinal);

        public static MemberFacts For(IndexDatabase db)
        {
            var facts = Cache.GetValue(db, _ => new MemberFacts());
            var fp = db.Scalar<long>("PRAGMA data_version") + ":" + db.Scalar<long>("SELECT total_changes()");
            lock (facts)
            {
                if (facts._fingerprint != fp) { facts._fingerprint = fp; facts.Package = new(StringComparer.Ordinal); facts.Base = new(StringComparer.Ordinal); }
                return facts;
            }
        }
    }

    /// <summary>id 列表内联进 SQL（都是 long，没有注入问题；免得撞 SQLite 的参数个数上限）。空表给一个不存在的 id。</summary>
    private static string Ids(IReadOnlyCollection<long> ids) => ids.Count == 0 ? "-1" : string.Join(",", ids);

    private static SymbolRecord ReadSymbol(SqliteDataReader r, int o) => new(
        r.GetInt64(o), r.GetInt64(o + 1), DbNames.ParseSymbolKind(r.GetString(o + 2)), r.GetString(o + 3), r.GetString(o + 4),
        r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
        r.GetInt32(o + 6), r.GetInt32(o + 7), r.GetInt32(o + 8), r.GetInt32(o + 9),
        r.IsDBNull(o + 10) ? null : r.GetInt64(o + 10),
        r.IsDBNull(o + 11) ? null : r.GetString(o + 11),
        DbNames.ParseSource(r.GetString(o + 12)));
}
