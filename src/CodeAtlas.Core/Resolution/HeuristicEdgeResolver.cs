using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>四级启发式解析（spec 4.2）：同文件 0.9、import 范围 0.7、全仓库唯一 0.5、多候选各 0.3、无候选 external 0.5。</summary>
public sealed class HeuristicEdgeResolver : IEdgeResolver
{
    public const int MaxCandidates = 20;

    /// <summary>
    /// 在同文件 / import 范围之外不按"全仓库唯一"解析的名字：LINQ、集合、Task、常见映射库。
    /// 它们几乎总是框架方法，仓库里碰巧有一个同名定义时连过去只会制造噪声。
    /// </summary>
    public static readonly HashSet<string> CommonFrameworkNames = new(StringComparer.Ordinal)
    {
        "Select", "SelectMany", "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy", "Join",
        "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Any", "All", "Count", "Sum", "Min", "Max",
        "Distinct", "Skip", "Take", "Concat", "Contains", "Aggregate", "Reverse", "Zip", "Cast", "OfType", "Except", "Union", "Intersect",
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "ToLookup", "AsEnumerable", "AsQueryable", "Include", "ThenInclude", "AsNoTracking",
        "ToListAsync", "ToArrayAsync", "FirstOrDefaultAsync", "FirstAsync", "SingleOrDefaultAsync", "SingleAsync", "AnyAsync", "CountAsync", "ToDictionaryAsync",
        "Add", "AddRange", "Remove", "Clear", "Insert", "IndexOf", "TryGetValue", "ContainsKey", "GetOrAdd", "TryAdd", "AddOrUpdate",
        "ToString", "Equals", "GetHashCode", "GetType", "Dispose", "DisposeAsync", "CompareTo", "Clone",
        "Format", "Join", "Split", "Trim", "TrimEnd", "TrimStart", "Replace", "StartsWith", "EndsWith", "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant", "Substring", "IsNullOrEmpty", "IsNullOrWhiteSpace",
        "Parse", "TryParse", "Delay", "Run", "WhenAll", "WhenAny", "FromResult", "ConfigureAwait", "Wait", "GetAwaiter", "GetResult", "CompletedTask",
        "Log", "LogInformation", "LogWarning", "LogError", "LogDebug", "LogTrace", "LogCritical",
        "Adapt", "Map", "ProjectTo", "Invoke", "BeginInvoke", "Execute", "Send", "Publish",
        "map", "filter", "reduce", "forEach", "find", "some", "every", "push", "pop", "slice", "splice", "concat", "join", "indexOf", "includes", "keys", "values", "entries", "then", "catch", "finally", "toString", "log", "warn", "error",
    };
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
        EdgeKind.Dispatches => symbol is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface, // handles：目标是消息/命令/查询类型
        _ => true,
    };

    public Task ResolveAsync(ResolveContext context, CancellationToken ct)
    {
        var reader = new IndexReader(context.Db);
        var writer = new IndexWriter(context.Db);

        var files = reader.GetAllFiles().ToDictionary(f => f.Id);
        var all = reader.GetAllSymbols();
        var byId = all.ToDictionary(s => s.Id);
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
        // handles（Handler 类 → 它处理的类型）先处理并先入库，后面的 dispatch 推导要查它
        var ordered = context.References.OrderBy(r => r.Via == "handles" ? 0 : 1).ToList();
        foreach (var r in ordered)
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
            if (r.Kind == EdgeKind.Calls) candidates = PreferImplementations(candidates, byId);

            // 1. 同文件唯一
            var sameFile = candidates.Where(c => c.FileId == r.FileId).ToList();
            if (sameFile.Count == 1) { edges.Add(Edge(r, sameFile[0], 0.9)); continue; }

            // 2. import 范围唯一
            var scope = BuildImportScope(r, importsByFile, namespacesByFile);
            var inScope = candidates.Where(c => scope.Contains(c, files)).ToList();
            if (inScope.Count == 1) { edges.Add(Edge(r, inScope[0], 0.7)); continue; }

            // 3. 全仓库唯一（LINQ/BCL 常见名字除外：仓库里碰巧有一个同名方法不足为凭，更可能是框架方法）
            if (candidates.Count == 1 && !(r.Kind == EdgeKind.Calls && CommonFrameworkNames.Contains(r.Name)))
            { edges.Add(Edge(r, candidates[0], 0.5)); continue; }

            // 5. 无候选（或名字太常见） → external
            if (candidates.Count == 0 || (r.Kind == EdgeKind.Calls && CommonFrameworkNames.Contains(r.Name)))
            {
                var ext = writer.GetOrCreateExternal(r.Name);
                edges.Add(new NewEdge(r.FromSymbolId, ext, "external:" + r.Name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line));
                continue;
            }

            // 4. 多候选：取最窄的非空层级（同文件重载 > import 范围 > 全仓库），而不是全仓库按字母序截断
            var pool = sameFile.Count > 1 ? sameFile : inScope.Count > 1 ? inScope : candidates;
            foreach (var c in pool.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates))
                edges.Add(Edge(r, c, 0.3));
        }

        var handles = edges.Where(e => e.Via == "handles").ToList();
        var others = edges.Where(e => e.Via != "handles").ToList();
        writer.WriteEdges(handles);
        var dispatch = DeriveDispatchEdges(reader, others, out var conduitIndexes);
        for (var i = 0; i < others.Count; i++)
            if (conduitIndexes.Contains(i)) others[i] = others[i] with { Via = "conduit" };
        writer.WriteEdges(others);
        writer.WriteEdges(dispatch);
        return Task.CompletedTask;
    }

    /// <summary>分发方法名：调用它只是把消息送进管道，本身不是链路上的一站。</summary>
    private static readonly Regex DispatcherMethod = new("^(Send|Query|Dispatch|Publish|Execute|Handle|Process|Mediate|Request)(Async)?$", RegexOptions.Compiled);

    /// <summary>
    /// 解耦调用链的弱版本（spec 4.7）：方法里 new 了一个有 Handler 的类型 T，就视为把 T 分发给了它的 Handler。
    /// 证据行是 new T 那一行；目标优先取 Handler 的 Handle*/Consume*/Execute* 方法，没有就连到类。
    /// </summary>
    private static List<NewEdge> DeriveDispatchEdges(IndexReader reader, List<NewEdge> edges, out HashSet<int> conduitIndexes)
    {
        var result = new List<NewEdge>();
        conduitIndexes = [];
        var seen = new HashSet<(long, long)>();
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if (e.Kind != EdgeKind.Calls || e.ToId is not { } typeId) continue;
            var target = reader.GetSymbol(typeId);
            if (target is null || target.Kind is not (SymbolKind.Class or SymbolKind.Struct)) continue;
            var handlers = reader.GetDispatchHandlers(typeId);
            if (handlers.Count == 0) continue;

            // 这条 new T 的构造调用是管道细节；同一行（±1）上对分发方法的调用也是
            conduitIndexes.Add(i);
            for (var j = 0; j < edges.Count; j++)
            {
                var c = edges[j];
                if (j == i || c.Kind != EdgeKind.Calls || c.FromId != e.FromId || Math.Abs(c.Line - e.Line) > 1) continue;
                // 目标可能是 "Ns.Type.Method"、"path.ts:Type.method" 或 "external:QueryAsync"
                var methodName = c.ToName[(Math.Max(c.ToName.LastIndexOf('.'), c.ToName.LastIndexOf(':')) + 1)..];
                if (DispatcherMethod.IsMatch(methodName)) conduitIndexes.Add(j);
            }

            foreach (var handler in handlers)
            {
                var entry = reader.GetChildren(handler.Id).FirstOrDefault(c => c.Kind == SymbolKind.Method && IsHandlerEntry(c.Name)) ?? handler;
                if (!seen.Add((e.FromId, entry.Id))) continue;
                result.Add(new NewEdge(e.FromId, entry.Id, entry.QualifiedName, EdgeKind.Dispatches, IndexSource.TreeSitter, 0.4, e.FileId, e.Line, Via: "dispatch:" + target.Name, EvidenceLine: e.Line));
            }
        }
        return result;
    }

    private static bool IsHandlerEntry(string name) =>
        name.StartsWith("Handle", StringComparison.Ordinal) || name.StartsWith("Consume", StringComparison.Ordinal) || name.StartsWith("Execute", StringComparison.Ordinal);

    /// <summary>
    /// 调用候选里既有接口上的方法又有类上的同名方法时，去掉接口上的：代码在实现里，读链路要看实现。
    /// 两个以上实现仍然保留为多候选。
    /// </summary>
    private static List<SymbolRecord> PreferImplementations(List<SymbolRecord> candidates, Dictionary<long, SymbolRecord> byId)
    {
        if (candidates.Count < 2) return candidates;
        SymbolKind? ParentKind(SymbolRecord s) => s.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent.Kind : null;
        var onInterfaces = candidates.Where(c => c.Kind == SymbolKind.Method && ParentKind(c) == SymbolKind.Interface).ToList();
        var onTypes = candidates.Where(c => c.Kind == SymbolKind.Method && ParentKind(c) is SymbolKind.Class or SymbolKind.Struct).ToList();
        if (onInterfaces.Count == 0 || onTypes.Count == 0) return candidates;
        return candidates.Where(c => !onInterfaces.Contains(c)).ToList();
    }

    private static NewEdge Edge(PendingReference r, SymbolRecord target, double confidence) =>
        new(r.FromSymbolId, target.Id, target.QualifiedName, r.Kind, IndexSource.TreeSitter, confidence, r.FileId, r.Line, Via: r.Via);

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
