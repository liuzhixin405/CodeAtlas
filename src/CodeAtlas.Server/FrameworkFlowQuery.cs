using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Server;

/// <summary>
/// 框架流程的取数（服务 IndexService.Framework 与 CLI arch --framework 共用）：容器、入口、DI 注册从库里读；
/// 方法的调用按需走 <see cref="OutlineQuery.BuildMany"/>（与调用大纲同一套规则）再补上悬空的调用（自定义委托的 next()）；源码文本从仓库根读。
/// </summary>
public static class FrameworkFlowQuery
{
    /// <param name="repoRoot">主根的绝对路径（入库路径相对它；外部根是 ../other/…）。</param>
    /// <param name="symbols">整库符号与边（服务缓存的那份，见 MapData.Load）。</param>
    /// <returns>容器不存在返回 null。</returns>
    public static FrameworkFlowDto? Build(IndexDatabase db, string repoRoot, string containerKey, IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges)
    {
        var container = ArchStorage.Read(db).Nodes.FirstOrDefault(n => n.Key == containerKey && n.Kind == ArchNodeKind.Container);
        if (container is null) return null;
        var (entries, _) = EntryStorage.Read(db);
        var reader = new IndexReader(db);

        var di = new List<FrameworkRegistration>();
        using (var cmd = db.CreateCommand("""
            SELECT d.interface_id, d.impl_id, f.path, d.line, d.conditional FROM di_registrations d JOIN files f ON f.id = d.file_id
            WHERE d.interface_id IS NOT NULL AND d.impl_id IS NOT NULL ORDER BY f.path, d.line
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) di.Add(new FrameworkRegistration(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4) != 0));

        var records = new Dictionary<long, SymbolRecord?>();
        SymbolRecord? Record(long id) => records.TryGetValue(id, out var s) ? s : records[id] = reader.GetSymbol(id);
        var paths = new Dictionary<long, string>();
        string PathOf(long fileId)
        {
            if (paths.TryGetValue(fileId, out var p)) return p;
            return paths[fileId] = reader.GetFileById(fileId)?.Path ?? IndexWriter.ExternalFilePath;
        }

        var calls = new Dictionary<long, IReadOnlyList<FrameworkCall>>();
        IReadOnlyList<FrameworkCall> Calls(long id)
        {
            if (calls.TryGetValue(id, out var cached)) return cached;
            if (Record(id) is not { } root || root.Kind is not (SymbolKind.Method or SymbolKind.Function)) return calls[id] = [];
            var outline = OutlineQuery.BuildMany(db, [root], new OutlineOptions(Externals: true, Tests: false))[root.Id];
            var list = outline.Steps.Select(ToCall).ToList();
            // 悬空的调用：Roslyn 对仓库里自定义委托（CommandHandlerDelegate）的 next() 记成 to_id 为空的边，名字是"类型.Invoke()"
            using (var cmd = db.CreateCommand("SELECT to_name, line, pos, ctx FROM edges WHERE from_id = $id AND to_id IS NULL AND kind = 'calls'", ("$id", id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var name = r.GetString(0);
                    list.Add(new FrameworkCall(null, "unresolved", name, name, r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), false, []));
                }
            return calls[id] = list;
        }

        var sources = new FrameworkSources
        {
            Calls = Calls,
            Signature = id => Record(id)?.Signature,
            ReadFile = rel =>
            {
                try
                {
                    var full = Path.GetFullPath(Path.Combine(repoRoot, rel));
                    return File.Exists(full) ? File.ReadAllText(full) : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
            },
            Symbol = id => Record(id) is { } s
                ? new SymbolDto(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, s.Signature, PathOf(s.FileId), s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Visibility)
                : null,
        };
        return FrameworkFlowBuilder.Build(new FrameworkInput(symbols, edges, container, entries, di, sources));
    }

    private static FrameworkCall ToCall(OutlineStepDto s) =>
        new(s.Target?.Id, s.Kind, s.Label, s.Target?.QualifiedName ?? s.Label, s.Line, s.Pos, s.Ctx, s.Expandable, s.Children.Select(ToCall).ToList());
}
