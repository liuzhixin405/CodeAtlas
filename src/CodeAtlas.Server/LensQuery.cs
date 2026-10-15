using CodeAtlas.Core.Hashing;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;
using CodeAtlas.Core.Workspace;

namespace CodeAtlas.Server;

/// <summary>
/// 快速跳转（GET /api/files/lens）：一个文件里每个方法 / 函数 / 构造函数的调用计数，编辑器 CodeLens 用。整个文件一次算完，每一步一次批量 IN 查询。
/// <list type="number">
/// <item>列哪些：文件里 kind 为 method / function 的符号（构造函数、局部函数、各语言的方法与函数都落在这两种里），按 (StartLine, StartCol, id) 排。</item>
/// <item>Callers / CallSites / TestCallers：直接复用影响范围的入边规则（<see cref="ImpactQuery"/> 的 Graph.Links → <see cref="ImpactBuilder.Classify"/>，
/// 阈值 <see cref="ImpactBuilder.ListThreshold"/>）：calls 入边（via 不是 conduit，via=impl 也算）、dispatches 的 dispatch:*、构造函数被 new、
/// 经接口 / 基类方法补充的调用方；递归不算。Callers 是去重后的调用方个数（同一调用方以不同方式调用也只算一个），
/// CallSites 是去重后的 (调用方, 行, 偏移) 个数；测试代码里的调用方（<see cref="PipelineBuilder.IsTest"/>）不进这两项、只计 TestCallers——
/// 方法本身在测试代码里时也一样（编辑器上"被 N 处调用"始终指产品代码）。</item>
/// <item>Callees：调用大纲第一层的步骤数，与 GET /api/symbols/{id}/outline 的默认参数（含框架调用、不含测试目标）一致：
/// 走同一个 <see cref="OutlineQuery.BuildMany"/>（单个方法的大纲也是它）。</item>
/// <item>IsEntry：entry_points 里有这个符号。</item>
/// <item>Stale：磁盘上的文件内容哈希（与索引器同一个 <see cref="FileHasher.Sha256Hex"/>）与 files.hash 不一致，或文件已不在磁盘上。
/// 文件不在索引里时 Symbols 为空、Stale = false。</item>
/// </list>
/// </summary>
public static class LensQuery
{
    /// <param name="roots">把相对路径还原成磁盘路径（判 Stale）。</param>
    /// <param name="requested">请求里的原始路径（不在任何根下时原样回给调用方）。</param>
    /// <param name="relative">规范化后的相对路径（IndexService.NormalizeToRelative）；null = 绝对路径不在任何根下。</param>
    public static FileLensDto Build(IndexDatabase db, ProjectRoots roots, string requested, string? relative)
    {
        if (relative is null) return new FileLensDto(requested.Replace('\\', '/'), [], false);
        var file = new IndexReader(db).GetFile(relative);
        if (file is null || file.Path == IndexWriter.ExternalFilePath) return new FileLensDto(relative, [], false);

        var methods = OutlineQuery.ReadSymbols(db, $"""
            SELECT {OutlineQuery.SymbolColumns} FROM symbols s
            WHERE s.file_id = {file.Id} AND s.kind IN ('method', 'function')
            ORDER BY s.start_line, s.start_col, s.id
            """, 1);
        var stale = IsStale(file, roots);
        if (methods.Count == 0) return new FileLensDto(file.Path, [], stale);
        var ids = methods.Select(m => m.Id).ToList();

        // ---- 调用方：影响范围同一套入边规则，一次取整个文件 ----
        var g = new ImpactQuery.Graph(db);
        var links = g.Links(ids, ImpactBuilder.ListThreshold).ToList();
        // 跨服务调用方（前端 fetch 对上后端控制器）也算：不算的话后端控制器上方会显示"没有调用方"
        var (_, matchesByEntry) = ImpactQuery.LoadEntriesFor(db);
        foreach (var id in ids) links.AddRange(ImpactQuery.HopLinks(g, matchesByEntry, id));
        g.Load(links.Select(l => l.CallerId).Distinct().ToList(), withParents: false);
        var counts = new Dictionary<long, (HashSet<long> Callers, HashSet<(long, int, int?)> Sites, HashSet<long> Tests)>();
        foreach (var l in links)
        {
            if (g.Get(l.CallerId) is null) continue; // 调用方符号已不在库里（与影响范围列表一致：跳过）
            if (!counts.TryGetValue(l.TargetId, out var c)) counts[l.TargetId] = c = ([], [], []);
            if (g.IsTest(l.CallerId)) { c.Tests.Add(l.CallerId); continue; }
            c.Callers.Add(l.CallerId);
            c.Sites.Add((l.CallerId, l.Line, l.Pos));
        }

        // ---- 被调方：调用大纲第一层（默认参数）----
        var outlines = OutlineQuery.BuildMany(db, methods, new OutlineOptions(), expandable: false);

        // ---- 入口 ----
        var entries = new HashSet<long>();
        using (var cmd = db.CreateCommand($"SELECT DISTINCT symbol_id FROM entry_points WHERE symbol_id IN ({OutlineQuery.Ids(ids)})"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) entries.Add(r.GetInt64(0));

        var symbols = methods.Select(m =>
        {
            var c = counts.GetValueOrDefault(m.Id);
            return new SymbolLensDto(ImpactBuilder.ToDto(m, file.Path), c.Callers?.Count ?? 0, c.Sites?.Count ?? 0, c.Tests?.Count ?? 0,
                outlines[m.Id].Steps.Count, entries.Contains(m.Id));
        }).ToList();
        return new FileLensDto(file.Path, symbols, stale);
    }

    /// <summary>读不到文件（被删、被锁）算过期；记录里没有哈希（解析失败 / 超大跳过）不判过期。</summary>
    private static bool IsStale(FileRecord file, ProjectRoots roots)
    {
        if (string.IsNullOrEmpty(file.Hash)) return false;
        var full = Path.GetFullPath(Path.Combine(roots.Main, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        try { return FileHasher.Sha256Hex(File.ReadAllBytes(full)) != file.Hash; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
