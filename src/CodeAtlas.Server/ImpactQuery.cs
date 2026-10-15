using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server;

/// <summary>
/// 影响范围的取数：只查需要的——被查询符号（或 BFS 的一整层）的入边、调用方符号与父符号、接口 / 基类方法、调用方还有没有入边、入口表与 route_matches，
/// 每一步一次批量 IN 查询，不加载整库。服务（IndexService.Impact）与 CLI（query --impact）共用。规则见 <see cref="ImpactBuilder"/>。
/// </summary>
public static class ImpactQuery
{
    private const string EdgeColumns = "e.id, e.from_id, e.to_id, e.to_name, e.kind, e.source, e.confidence, e.file_id, e.line, e.via, e.evidence_line, e.pos, e.ctx";

    /// <summary>可能构成影响的入边（置信度另外比较）。别名 e。handles 不要；conduit 是构造消息那一步。</summary>
    private const string ImpactEdgeFilter =
        "((e.kind = 'calls' AND (e.via IS NULL OR e.via <> 'conduit')) OR (e.kind = 'dispatches' AND e.via LIKE 'dispatch:%'))";

    /// <summary>沿 Roslyn 的方法级 implements / inherits（override → 被重写的方法）往上找接口 / 基类方法的层数。</summary>
    private const int MaxMemberBaseLevels = 4;
    /// <summary>tree-sitter 符号按类型级 implements / inherits 往上找祖先类型的层数（与入口识别找框架基类的上限一致）。</summary>
    private const int MaxTypeBaseLevels = 8;

    /// <summary>符号不存在返回 null。</summary>
    public static ImpactDto? Build(IndexDatabase db, long id, ImpactOptions options)
    {
        var g = new Graph(db);
        g.Load([id], withParents: true);
        if (g.Get(id) is not { } root) return null;
        var rootDto = ImpactBuilder.ToDto(root, g.PathOf(root));
        var keepTests = options.Tests || g.IsTest(id);

        var (entriesBySymbol, matchesByEntry) = LoadEntries(db);
        var containerNames = ContainerNames(db);
        IReadOnlyList<ImpactEntryDto> EntriesOf(long symbolId, int depth)
        {
            if (!entriesBySymbol.Contains(symbolId) || g.Get(symbolId) is not { } s) return [];
            return ImpactBuilder.SortEntries(entriesBySymbol[symbolId].Select(e => new ImpactEntryDto(
                new EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, s.Name, s.QualifiedName, e.File, e.Line),
                e.ContainerKey, containerNames.GetValueOrDefault(e.ContainerKey, e.ContainerKey), depth)));
        }

        // ---- 直接调用方 ----
        var links = g.Links([id], ImpactBuilder.ListThreshold).ToList();
        // 跨服务续接（Kind=hop）：这个符号是被别的容器调用的入口（前端 fetch、别的服务的 HTTP / gRPC 客户端）时，
        // 发起调用那一行所在的方法也是它的直接调用方——不列的话，在后端控制器上看影响范围会看不到前端页面
        links.AddRange(HopLinks(g, matchesByEntry, id));
        var callerIds = links.Select(l => l.CallerId).Distinct().ToList();
        g.Load(callerIds, withParents: true);
        var listed = callerIds.Where(c => keepTests || !g.IsTest(c)).ToList();

        // Expandable：调用方自己有满足同样规则的入边。展开时那一层按它自己的 keepTests 过滤，这里同样算（测试调用方只在 tests=true 或它本身在测试里时算数）
        var upstream = g.Links(listed, ImpactBuilder.ListThreshold);
        g.Load(upstream.Select(l => l.CallerId).Distinct().ToList(), withParents: false);
        var expandable = upstream.Where(l => options.Tests || g.IsTest(l.TargetId) || !g.IsTest(l.CallerId)).Select(l => l.TargetId).ToHashSet();

        var (callers, testCallers, truncated) = ImpactBuilder.Callers(rootDto, links, g.Symbols, g.Paths, expandable, c => EntriesOf(c, 1), options);

        // ---- 入口汇总 ----
        List<ImpactEntryDto>? entries = null;
        var entriesTruncated = false;
        if (options.Entries)
        {
            IEnumerable<long> Predecessors(IReadOnlyCollection<long> frontier)
            {
                // 分派边（tree-sitter 按 new T 推出来的固定 0.4）也走：它按消息类型对上 Handler，不是同名多候选那种猜测；
                // 不走的话只用 tree-sitter 时 Handler 往上到不了发消息的 HTTP 入口
                var up = g.Links(frontier, ImpactBuilder.ListThreshold)
                    .Where(l => l.Confidence >= ImpactBuilder.WalkThreshold || l.Kind == "dispatch").ToList();
                var ids = up.Select(l => l.CallerId).Distinct().ToList();
                // route_matches：被对上的入口 → 发起调用那一行所在的方法（前端 fetch、别的服务里的 HTTP / gRPC 客户端调用）
                foreach (var f in frontier)
                    foreach (var m in matchesByEntry[f])
                        if (g.Enclosing(m.File, m.Line) is { } site) ids.Add(site);
                g.Load(ids, withParents: false);
                return ids.Where(c => !g.IsTest(c));
            }
            var (depths, cut) = ImpactBuilder.Reach(id, Predecessors);
            entriesTruncated = cut;
            var reached = depths.Keys.Where(entriesBySymbol.Contains).ToList();
            g.Load(reached, withParents: false);
            entries = ImpactBuilder.SortEntries(reached.SelectMany(e => EntriesOf(e, depths[e])));
        }

        return new ImpactDto(rootDto, callers, entries, entriesTruncated, testCallers, truncated);
    }

    /// <summary>route_matches 里对上 <paramref name="entryId"/> 的调用点 → 包含那一行的方法（自己调自己不算）。</summary>
    internal static IEnumerable<ImpactLink> HopLinks(Graph g, ILookup<long, RouteMatch> matchesByEntry, long entryId)
    {
        foreach (var m in matchesByEntry[entryId])
            if (g.Enclosing(m.File, m.Line) is { } site && site != entryId)
                yield return new ImpactLink(entryId, site, ImpactBuilder.HopKind, Core.Architecture.EdgeMatcher.ByRoute, m.Line, null, null);
    }

    internal static (ILookup<long, EntryPoint> Entries, ILookup<long, RouteMatch> Matches) LoadEntriesFor(IndexDatabase db) => LoadEntries(db);

    private static (ILookup<long, EntryPoint> Entries, ILookup<long, RouteMatch> Matches) LoadEntries(IndexDatabase db)
    {
        var (entries, matches) = EntryStorage.Read(db);
        return (entries.ToLookup(e => e.SymbolId), matches.ToLookup(m => m.EntrySymbolId));
    }

    private static Dictionary<string, string> ContainerNames(IndexDatabase db)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand("SELECT key, name FROM arch_nodes");
        using var r = cmd.ExecuteReader();
        while (r.Read()) names[r.GetString(0)] = r.GetString(1);
        return names;
    }

    /// <summary>
    /// 参数个数：限定名带参数列表（Roslyn：Ns.T.M(int, string)）时数它，否则看签名里的第一对括号；都没有为 null（只按名字比）。
    /// 泛型实参里的逗号（Dictionary&lt;string, int&gt;）不算。
    /// </summary>
    public static int? ParamCount(SymbolRecord s)
    {
        foreach (var text in new[] { s.QualifiedName, s.Signature })
        {
            if (text is null) continue;
            var open = text.IndexOf('(');
            if (open < 0) continue;
            var depth = 0;
            var commas = 0;
            var any = false;
            for (var i = open + 1; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '(' or '<' or '[' or '{') depth++;
                else if (c is '>' or ']' or '}') depth--;
                else if (c == ')')
                {
                    if (depth == 0) return any ? commas + 1 : 0;
                    depth--;
                }
                else if (c == ',' && depth == 0) commas++;
                if (!char.IsWhiteSpace(c)) any = true;
            }
        }
        return null;
    }

    /// <summary>按需加载的子图：符号、文件路径、测试判定、每个文件里可容纳一行的方法（route_matches 续接用）。</summary>
    internal sealed class Graph(IndexDatabase db) // internal：快速跳转（LensQuery）批量复用 Links 的入边规则
    {
        private readonly Dictionary<long, SymbolRecord> _symbols = [];
        private readonly Dictionary<long, string> _paths = [];
        private readonly Dictionary<long, bool> _test = [];
        private readonly Dictionary<string, List<(long Id, int Start, int End, bool Module)>> _fileScopes = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<long, SymbolRecord> Symbols => _symbols;
        public IReadOnlyDictionary<long, string> Paths => _paths;

        public SymbolRecord? Get(long id) => _symbols.GetValueOrDefault(id);

        public string PathOf(SymbolRecord s) => _paths.GetValueOrDefault(s.FileId, IndexWriter.ExternalFilePath);

        public bool IsTest(long id)
        {
            if (_test.TryGetValue(id, out var t)) return t;
            if (Get(id) is not { } s) return false;
            return _test[id] = PipelineBuilder.IsTest(PathOf(s), s.QualifiedName);
        }

        /// <summary>批量加载缺的符号（withParents 时连父符号），再补文件路径。</summary>
        public void Load(IReadOnlyCollection<long> ids, bool withParents)
        {
            Fetch(ids.Where(i => !_symbols.ContainsKey(i)).Distinct().ToList());
            if (withParents)
                Fetch(ids.Select(Get).Where(s => s?.ParentId is { } p && !_symbols.ContainsKey(p)).Select(s => s!.ParentId!.Value).Distinct().ToList());
            var files = _symbols.Values.Select(s => s.FileId).Where(f => !_paths.ContainsKey(f)).Distinct().ToList();
            if (files.Count == 0) return;
            using var cmd = db.CreateCommand($"SELECT id, path FROM files WHERE id IN ({OutlineQuery.Ids(files)})");
            using var r = cmd.ExecuteReader();
            while (r.Read()) _paths[r.GetInt64(0)] = r.GetString(1);
        }

        private void Fetch(List<long> ids)
        {
            foreach (var s in OutlineQuery.ReadSymbols(db, $"SELECT {OutlineQuery.SymbolColumns} FROM symbols s WHERE s.id IN ({OutlineQuery.Ids(ids)})", ids.Count))
                _symbols[s.Id] = s;
        }

        private bool IsConstructor(SymbolRecord s) =>
            s.Kind is SymbolKind.Method or SymbolKind.Function && s.ParentId is { } p && Get(p) is { } owner && OutlineBuilder.IsType(owner.Kind) && OutlineBuilder.IsConstructor(s, owner);

        /// <summary>一整组被查询符号的影响链接（规则见 ImpactBuilder.Classify）：先算出每个符号的入边目标（自己、构造函数所属类型、接口 / 基类方法），再一次查完入边。</summary>
        public List<ImpactLink> Links(IReadOnlyCollection<long> ids, double minConfidence)
        {
            if (ids.Count == 0) return [];
            Load(ids, withParents: true);
            var relations = new List<(long Target, long Origin, ImpactRelation Relation)>();
            var constructs = new HashSet<long>();
            foreach (var id in ids)
            {
                if (Get(id) is not { } s) continue;
                relations.Add((id, id, ImpactRelation.Self));
                if (s.Kind is SymbolKind.Class or SymbolKind.Struct) constructs.Add(id);
                else if (IsConstructor(s))
                {
                    constructs.Add(id);
                    relations.Add((s.ParentId!.Value, id, ImpactRelation.ConstructedType));
                }
            }
            foreach (var (baseId, origin) in BaseMembers(ids)) relations.Add((baseId, origin, ImpactRelation.BaseMember));

            var targets = relations.Select(r => r.Target).Distinct().ToList();
            var edges = new List<EdgeRecord>();
            using (var cmd = db.CreateCommand($"""
                SELECT {EdgeColumns} FROM edges e
                WHERE e.to_id IN ({OutlineQuery.Ids(targets)}) AND {ImpactEdgeFilter} AND e.confidence >= $min
                """, ("$min", minConfidence)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    edges.Add(new EdgeRecord(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3),
                        DbNames.ParseEdgeKind(r.GetString(4)), DbNames.ParseSource(r.GetString(5)), r.GetDouble(6), r.GetInt64(7), r.GetInt32(8),
                        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt32(10), r.IsDBNull(11) ? null : r.GetInt32(11), r.IsDBNull(12) ? null : r.GetString(12)));
            return ImpactBuilder.Classify(edges, relations.ToLookup(r => r.Target, r => (r.Origin, r.Relation)), constructs, minConfidence);
        }

        /// <summary>
        /// 被查询方法实现 / 重写的接口方法与基类方法：(基方法 id, 被查询符号 id)。
        /// <list type="bullet">
        /// <item>Roslyn 符号：只认方法级的 implements（实现 → 接口成员）与 inherits（override → 被重写的方法）边，沿边往上 <see cref="MaxMemberBaseLevels"/> 层。精确，不按名字猜。</item>
        /// <item>tree-sitter 符号（没有方法级边）：所属类型沿类型级 implements / inherits 往上 <see cref="MaxTypeBaseLevels"/> 层的仓库内祖先类型里，
        /// 同名（去泛型参数）且参数个数相同的成员；参数个数任一方读不出来时只比名字。</item>
        /// </list>
        /// 构造函数不参与。
        /// </summary>
        private List<(long Base, long Origin)> BaseMembers(IReadOnlyCollection<long> ids)
        {
            var result = new List<(long, long)>();
            var members = ids.Select(Get).Where(s => s is { Kind: SymbolKind.Method or SymbolKind.Function or SymbolKind.Property } && !IsConstructor(s)).Select(s => s!).ToList();
            if (members.Count == 0) return result;

            // ---- Roslyn：方法级边 ----
            var seen = new HashSet<(long, long)>();
            var frontier = members.Where(s => s.Source == IndexSource.Roslyn).Select(s => (Id: s.Id, Origin: s.Id)).ToList();
            for (var level = 0; level < MaxMemberBaseLevels && frontier.Count > 0; level++)
            {
                var byFrom = frontier.ToLookup(f => f.Id, f => f.Origin);
                var next = new List<(long Id, long Origin)>();
                foreach (var (from, to) in Pairs($"""
                    SELECT e.from_id, e.to_id FROM edges e JOIN symbols t ON t.id = e.to_id
                    WHERE e.from_id IN ({OutlineQuery.Ids(byFrom.Select(x => x.Key).ToList())}) AND e.kind IN ('implements', 'inherits')
                      AND t.kind IN ('method', 'function', 'property')
                    """))
                    foreach (var origin in byFrom[from])
                        if (to != origin && seen.Add((to, origin))) { result.Add((to, origin)); next.Add((to, origin)); }
                frontier = next;
            }

            // ---- tree-sitter：类型级边 + 同名同参数个数 ----
            var guessed = members.Where(s => s.Source != IndexSource.Roslyn && s.ParentId is { } p && Get(p) is { } o && OutlineBuilder.IsType(o.Kind)).ToList();
            if (guessed.Count == 0) return result;
            var ancestors = new Dictionary<long, HashSet<long>>(); // 类型 → 它的祖先类型
            var typeFrontier = guessed.Select(s => s.ParentId!.Value).Distinct().Select(t => (Type: t, Root: t)).ToList();
            foreach (var t in typeFrontier) ancestors[t.Root] = [];
            for (var level = 0; level < MaxTypeBaseLevels && typeFrontier.Count > 0; level++)
            {
                var byType = typeFrontier.ToLookup(f => f.Type, f => f.Root);
                var next = new List<(long Type, long Root)>();
                foreach (var (from, to) in Pairs($"""
                    SELECT e.from_id, e.to_id FROM edges e JOIN symbols t ON t.id = e.to_id
                    WHERE e.from_id IN ({OutlineQuery.Ids(byType.Select(x => x.Key).ToList())}) AND e.kind IN ('implements', 'inherits')
                      AND t.kind IN ('class', 'struct', 'interface')
                    """))
                    foreach (var root in byType[from])
                        if (to != root && ancestors[root].Add(to)) next.Add((to, root));
                typeFrontier = next;
            }
            var allAncestors = ancestors.Values.SelectMany(a => a).Distinct().ToList();
            if (allAncestors.Count == 0) return result;
            var candidates = OutlineQuery.ReadSymbols(db, $"""
                SELECT {OutlineQuery.SymbolColumns} FROM symbols s
                WHERE s.parent_id IN ({OutlineQuery.Ids(allAncestors)}) AND s.kind IN ('method', 'function', 'property')
                """, allAncestors.Count).ToLookup(c => c.ParentId!.Value);
            foreach (var s in guessed)
            {
                var name = MapBuilder.StripGenericArgs(s.Name);
                var count = ParamCount(s);
                foreach (var ancestor in ancestors[s.ParentId!.Value])
                    foreach (var c in candidates[ancestor])
                    {
                        if (MapBuilder.StripGenericArgs(c.Name) != name) continue;
                        if (count is { } n && ParamCount(c) is { } m && n != m) continue;
                        _symbols.TryAdd(c.Id, c);
                        result.Add((c.Id, s.Id));
                    }
            }
            return result;
        }

        private List<(long, long)> Pairs(string sql)
        {
            var list = new List<(long, long)>();
            using var cmd = db.CreateCommand(sql);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetInt64(1)));
            return list;
        }

        /// <summary>文件里包含这一行的最内层方法 / 函数（都没有时退到模块：前端文件顶层的 fetch）。文件不在索引里或找不到返回 null。</summary>
        public long? Enclosing(string path, int line)
        {
            if (!_fileScopes.TryGetValue(path, out var scopes))
            {
                scopes = [];
                using var cmd = db.CreateCommand("""
                    SELECT s.id, s.start_line, s.end_line, s.kind FROM symbols s JOIN files f ON f.id = s.file_id
                    WHERE f.path = $p AND s.kind IN ('method', 'function', 'module')
                    """, ("$p", path));
                using var r = cmd.ExecuteReader();
                while (r.Read()) scopes.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3) == "module"));
                _fileScopes[path] = scopes;
            }
            var hit = scopes.Where(s => s.Start <= line && line <= s.End)
                .OrderBy(s => s.Module ? 1 : 0).ThenBy(s => s.End - s.Start).ThenByDescending(s => s.Start).FirstOrDefault();
            return hit.Id == 0 ? null : hit.Id;
        }
    }
}
