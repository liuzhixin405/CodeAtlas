using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Server;

/// <summary>
/// 调用大纲的取数：只查这个符号的出边、边的目标、目标的父符号、类型的构造函数和"目标还有没有调用"（各一次批量查询），
/// 不加载整库。服务（IndexService.Outline）与 CLI（query --outline）共用。
/// </summary>
public static class OutlineQuery
{
    internal const string SymbolColumns = "s.id, s.file_id, s.kind, s.name, s.qualified_name, s.signature, s.start_line, s.start_col, s.end_line, s.end_col, s.parent_id, s.visibility, s.source";

    /// <summary>满足大纲第 1 条的出边（Expandable 判定用；与 OutlineBuilder 的过滤一致）。别名 e。</summary>
    private const string OutlineEdgeFilter =
        "((e.kind = 'calls' AND e.confidence >= 0.4 AND (e.via IS NULL OR e.via <> 'conduit')) OR (e.kind = 'dispatches' AND e.via LIKE 'dispatch:%'))";

    /// <summary>符号不存在返回 null。</summary>
    public static OutlineDto? Build(IndexDatabase db, long id, OutlineOptions options)
    {
        var reader = new IndexReader(db);
        if (reader.GetSymbol(id) is not { } root) return null;
        return BuildMany(db, [root], options)[root.Id];
    }

    private const string EdgeColumns = "e.id, e.from_id, e.to_id, e.to_name, e.kind, e.source, e.confidence, e.file_id, e.line, e.via, e.evidence_line, e.pos, e.ctx";

    /// <summary>
    /// 一组方法的大纲：出边、目标、父符号、构造函数、"目标还有没有调用"都按整组一次批量查，再对每个方法分别跑 <see cref="OutlineBuilder.Build"/>。
    /// 单个方法的大纲（<see cref="Build"/>）走的就是这里，所以批量与单个的结果一致（快速跳转的 Callees 靠这个）。
    /// </summary>
    /// <param name="expandable">false 时不查"目标还有没有调用"（所有步骤 Expandable = false）：只要步骤数时省掉一趟查询，步骤本身不受影响。</param>
    internal static Dictionary<long, OutlineDto> BuildMany(IndexDatabase db, IReadOnlyList<SymbolRecord> roots, OutlineOptions options, bool expandable = true)
    {
        var result = new Dictionary<long, OutlineDto>();
        if (roots.Count == 0) return result;
        var rootIds = roots.Select(r => r.Id).Distinct().ToList();

        // 出边 + 目标符号（与 IndexReader.GetNeighbors 同样的 LEFT JOIN）；按 (起点, 行, 边 id) 排，单个与批量顺序一致
        var edgesByRoot = new Dictionary<long, List<EdgeRecord>>();
        var symbols = new Dictionary<long, SymbolRecord>();
        foreach (var root in roots) symbols[root.Id] = root;
        var neighborTargets = new List<SymbolRecord>();
        using (var cmd = db.CreateCommand($"""
            SELECT {EdgeColumns}, {SymbolColumns} FROM edges e LEFT JOIN symbols s ON s.id = e.to_id
            WHERE e.from_id IN ({Ids(rootIds)}) AND e.kind IN ('calls', 'dispatches') ORDER BY e.from_id, e.line, e.id
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var e = ReadEdge(r);
                if (!edgesByRoot.TryGetValue(e.FromId, out var list)) edgesByRoot[e.FromId] = list = [];
                list.Add(e);
                if (r.IsDBNull(13)) continue;
                var o = ReadSymbol(r, 13);
                symbols[o.Id] = o;
                neighborTargets.Add(o);
            }

        // 父符号（拼"类型.方法"）
        var parentIds = symbols.Values.Where(s => s.ParentId is { } p && !symbols.ContainsKey(p)).Select(s => s.ParentId!.Value).Distinct().ToList();
        foreach (var s in ReadSymbols(db, $"SELECT {SymbolColumns} FROM symbols s WHERE s.id IN ({Ids(parentIds)})", parentIds.Count)) symbols[s.Id] = s;

        // 被 new 的仓库内类型：唯一的构造函数（多个重载不知道是哪个，不换）
        var typeIds = neighborTargets.Where(o => o.Kind is SymbolKind.Class or SymbolKind.Struct).Select(o => o.Id).Distinct().ToList();
        var constructors = new Dictionary<long, long>();
        var ctorRows = ReadSymbols(db, $"""
            SELECT {SymbolColumns} FROM symbols s JOIN symbols t ON t.id = s.parent_id
            WHERE s.parent_id IN ({Ids(typeIds)}) AND s.kind IN ('method', 'function') AND (s.name = t.name OR s.name IN ('constructor', '__init__'))
            """, typeIds.Count);
        foreach (var g in ctorRows.GroupBy(c => c.ParentId!.Value))
        {
            if (g.Count() != 1) continue;
            var ctor = g.Single();
            symbols[ctor.Id] = ctor;
            constructors[g.Key] = ctor.Id;
        }

        // 目标还有没有调用：一次查完
        var hasCalls = new HashSet<long>();
        var callable = expandable ? symbols.Values.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function).Select(s => s.Id).ToList() : [];
        if (callable.Count > 0)
        {
            var externalFilter = options.Externals ? "" : " AND (t.kind IS NULL OR t.kind <> 'external')";
            using var cmd = db.CreateCommand($"""
                SELECT DISTINCT e.from_id FROM edges e LEFT JOIN symbols t ON t.id = e.to_id
                WHERE e.from_id IN ({Ids(callable)}) AND e.to_id IS NOT NULL AND {OutlineEdgeFilter}{externalFilter}
                """);
            using var r = cmd.ExecuteReader();
            while (r.Read()) hasCalls.Add(r.GetInt64(0));
        }

        var fileIds = symbols.Values.Select(s => s.FileId).Distinct().ToList();
        var paths = new Dictionary<long, string>();
        using (var cmd = db.CreateCommand($"SELECT id, path FROM files WHERE id IN ({Ids(fileIds)})"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) paths[r.GetInt64(0)] = r.GetString(1);

        foreach (var root in roots)
        {
            if (result.ContainsKey(root.Id)) continue;
            var rootDto = new SymbolDto(root.Id, DbNames.ToDb(root.Kind), root.Name, root.QualifiedName, root.Signature, paths.GetValueOrDefault(root.FileId, "?"),
                root.StartLine, root.StartCol, root.EndLine, root.EndCol, root.Visibility);
            result[root.Id] = OutlineBuilder.Build(rootDto, edgesByRoot.GetValueOrDefault(root.Id) ?? [], symbols, paths, hasCalls, constructors, options);
        }
        return result;
    }

    /// <summary>
    /// 按用户写的名字找符号（CLI query 与面板搜索用）：完整限定名 → 短名 → "类型.方法"之类的限定名后缀。
    /// 后缀比较时两边都去掉参数表与泛型参数（Roslyn 的限定名带参数表，泛型类成员是 Ns.Repo&lt;T&gt;.Find），脚本限定名去掉"路径:"前缀：
    /// IndexCommand.Run、TransactionBehavior.Handle、JobLockRepository.AddAsync、Repo&lt;T&gt;.Find、Find(int) 都能找到；
    /// 名字里写了参数表（Find(int)）且有多个重载时，只留参数表一致的那个。后缀按段对齐（Run 不会匹配 RunRoslyn，Index.Run 不会匹配 IndexCommand.Run）。
    /// </summary>
    public static IReadOnlyList<SymbolRecord> FindSymbols(IndexDatabase db, string target)
    {
        target = target.Trim();
        if (target.Length == 0) return [];
        var exact = ReadSymbols(db, $"SELECT {SymbolColumns} FROM symbols s WHERE s.qualified_name = $q", 1, ("$q", target));
        if (exact.Count > 0) return exact;
        var want = NormalizeName(target);
        var member = MapBuilder.MemberName(want);
        if (member.Length == 0) return [];
        // 短名：表里的 name 列（Roslyn 的方法名、TS 的函数名）；局部函数、顶级语句的 name 也是最后一段
        var named = ReadSymbols(db, $"SELECT {SymbolColumns} FROM symbols s WHERE s.name = $n ORDER BY s.qualified_name", 1, ("$n", member));
        if (target == member) return named;
        var hits = named.Where(s => EndsWithSegments(NormalizeName(s.QualifiedName), want)).ToList();
        // 写了脚本路径（useOrders.ts:useOrders）：路径也要按目录段对上（别的文件里的同名函数不算）
        if (ScriptPath(target) is { } path)
            hits = hits.Where(s => ScriptPath(s.QualifiedName) is { } p && (p == path || p.EndsWith("/" + path, StringComparison.Ordinal))).ToList();
        // 写了参数表：多个重载时按参数表文本（去空白）再挑一次；一个都对不上就保留全部（参数类型写法不同：int vs Int32）
        if (hits.Count > 1 && target.IndexOf('(') is var paren and >= 0)
        {
            var args = Compact(target[paren..]);
            var same = hits.Where(s => s.QualifiedName.IndexOf('(') is var p and >= 0 && Compact(s.QualifiedName[p..]) == args).ToList();
            if (same.Count > 0) hits = same;
        }
        return hits;

        static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
    }

    /// <summary>脚本限定名的"路径:"部分（src/a.ts:Comp.method → src/a.ts）；不是脚本限定名返回 null。</summary>
    private static string? ScriptPath(string q)
    {
        var colon = q.LastIndexOf(':');
        return colon > 0 && !q.StartsWith("external:", StringComparison.Ordinal) && q[..colon].Contains('.') && q.IndexOf('(') is var p && (p < 0 || p > colon) ? q[..colon] : null;
    }

    /// <summary>名字比较用的形状：去掉 external: 与脚本"路径:"前缀、所有括号里的内容（参数表）与泛型参数，'/' 当作 '.'。</summary>
    internal static string NormalizeName(string q)
    {
        if (q.StartsWith("external:", StringComparison.Ordinal)) q = q["external:".Length..];
        if (ScriptPath(q) is { } path) q = q[(path.Length + 1)..]; // src/a.ts:Comp.method
        var sb = new System.Text.StringBuilder(q.Length);
        var paren = 0;
        foreach (var c in q)
        {
            if (c == '(') paren++;
            else if (c == ')') { if (paren > 0) paren--; }
            else if (paren == 0) sb.Append(c == '/' ? '.' : c);
        }
        return MapBuilder.StripGenericArgs(sb.ToString());
    }

    private static bool EndsWithSegments(string qualified, string suffix) =>
        qualified == suffix || (qualified.EndsWith(suffix, StringComparison.Ordinal) && qualified[qualified.Length - suffix.Length - 1] == '.');

    private static EdgeRecord ReadEdge(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3),
        DbNames.ParseEdgeKind(r.GetString(4)), DbNames.ParseSource(r.GetString(5)), r.GetDouble(6),
        r.GetInt64(7), r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt32(10),
        r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12));

    /// <summary>id 列表内联进 SQL（都是 long，没有注入问题；免得撞 SQLite 的参数个数上限）。空表给一个不存在的 id。</summary>
    internal static string Ids(IReadOnlyCollection<long> ids) => ids.Count == 0 ? "-1" : string.Join(",", ids);

    internal static List<SymbolRecord> ReadSymbols(IndexDatabase db, string sql, int count, params (string Name, object? Value)[] parameters)
    {
        var list = new List<SymbolRecord>();
        if (count == 0) return list;
        using var cmd = db.CreateCommand(sql, parameters);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSymbol(r));
        return list;
    }

    internal static SymbolRecord ReadSymbol(SqliteDataReader r, int o = 0) => new(
        r.GetInt64(o), r.GetInt64(o + 1), DbNames.ParseSymbolKind(r.GetString(o + 2)), r.GetString(o + 3), r.GetString(o + 4),
        r.IsDBNull(o + 5) ? null : r.GetString(o + 5),
        r.GetInt32(o + 6), r.GetInt32(o + 7), r.GetInt32(o + 8), r.GetInt32(o + 9),
        r.IsDBNull(o + 10) ? null : r.GetInt64(o + 10),
        r.IsDBNull(o + 11) ? null : r.GetString(o + 11),
        DbNames.ParseSource(r.GetString(o + 12)));
}
