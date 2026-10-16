using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// 四级启发式解析（spec 4.2）：同文件 0.9、import 范围 0.7、全仓库唯一 0.5、多候选各 0.3、无候选 external 0.5。
/// TS/JS 引用带绑定信息时先按 import 绑定解析，见 HeuristicEdgeResolver.Script.cs。
/// </summary>
public sealed partial class HeuristicEdgeResolver : IEdgeResolver
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

        // 各语言索引要看全部待解析引用里的跨文件事实（父类型、内嵌、返回值提示……）：增量时补上不在本次范围的那部分
        IReadOnlyList<PendingReference> indexed = context.SupportReferences.Count == 0 ? context.References : [.. context.References, .. context.SupportReferences];
        ScriptIndex? script = null;
        ScriptIndex Script() => script ??= new ScriptIndex(all, byId, files, modulesByPathNoExt, importsByFile, context.ScriptConfigs, indexed, reader);
        GoIndex? go = null;
        GoIndex Go() => go ??= new GoIndex(all, files, importsByFile, context.GoModules, indexed);
        PythonIndex? python = null;
        PythonIndex Python() => python ??= new PythonIndex(all, byId, files, importsByFile, indexed, reader);
        JavaIndex? java = null;
        JavaIndex Java() => java ??= new JavaIndex(all, byId, files, importsByFile, indexed, reader);
        CSharpIndex? csharp = null;
        CSharpIndex CSharp() => csharp ??= new CSharpIndex(all, byId, files, importsByFile, indexed, reader);

        var edges = new List<NewEdge>();
        // handles（Handler 类 → 它处理的类型）先处理并先入库，后面的 dispatch 推导要查它
        var ordered = context.References.OrderBy(r => r.Via == "handles" ? 0 : 1).ToList();
        // 每个引用解析出的边统一盖上调用处的字符偏移与所在控制结构（各语言的解析分支不用各自记得带上）
        foreach (var r in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var start = edges.Count;
            ResolveOne(r);
            if (r.Pos >= 0 || r.Context is not null)
                for (var i = start; i < edges.Count; i++)
                    edges[i] = edges[i] with { Pos = r.Pos >= 0 ? r.Pos : edges[i].Pos, Ctx = r.Context ?? edges[i].Ctx };
        }

        void ResolveOne(PendingReference r)
        {
            var family = LanguageFamily(r.Language);
            if (family == "go") { edges.AddRange(ResolveGo(r, Go(), writer)); return; } // 见 HeuristicEdgeResolver.Go.cs
            if (family == "python") { edges.AddRange(ResolvePython(r, Python(), writer)); return; } // 见 HeuristicEdgeResolver.Python.cs
            if (family == "java") { edges.AddRange(ResolveJava(r, Java(), writer)); return; }       // 见 HeuristicEdgeResolver.Java.cs
            // C# 调用带接收者绑定（CSharpAdapter 产出）时按类型解析，见 HeuristicEdgeResolver.CSharp.cs；没有绑定的仍走下面的按名字启发式
            if (family == "csharp" && r.Kind == EdgeKind.Calls && r.Script is not null) { edges.AddRange(ResolveCSharpCall(r, CSharp(), writer)); return; }
            if (r.Kind == EdgeKind.Imports)
            {
                // 脚本：相对路径之外还认 tsconfig 别名（@/x）；C#：using 命名空间
                if (family == "js") edges.Add(ResolveScriptImport(r, Script(), writer));
                else edges.AddRange(ResolveImport(r, modulesByPathNoExt, namespacesByName, writer));
                return;
            }

            var candidates = byName.TryGetValue(r.Name, out var list)
                ? list.Where(s => KindCompatible(r.Kind, s.Kind)
                                  && files.TryGetValue(s.FileId, out var sf)
                                  && LanguageFamily(sf.Language) == family).ToList()
                : [];
            if (r.Kind == EdgeKind.Calls) candidates = PreferImplementations(candidates, byId);

            if (r.Script is not null && family == "js" && ResolveScript(r, candidates, Script(), writer) is { } scripted)
            {
                edges.AddRange(scripted);
                return;
            }

            // 1. 同文件唯一
            var sameFile = candidates.Where(c => c.FileId == r.FileId).ToList();
            if (sameFile.Count == 1) { edges.Add(Edge(r, sameFile[0], 0.9)); return; }

            // 2. import 范围唯一
            var scope = BuildImportScope(r, importsByFile, namespacesByFile);
            var inScope = candidates.Where(c => scope.Contains(c, files)).ToList();
            if (inScope.Count == 1) { edges.Add(Edge(r, inScope[0], 0.7)); return; }

            // 3. 全仓库唯一（LINQ/BCL 常见名字除外：仓库里碰巧有一个同名方法不足为凭，更可能是框架方法）
            if (candidates.Count == 1 && !(r.Kind == EdgeKind.Calls && CommonFrameworkNames.Contains(r.Name)))
            { edges.Add(Edge(r, candidates[0], 0.5)); return; }

            // 5. 无候选（或名字太常见） → external
            if (candidates.Count == 0 || (r.Kind == EdgeKind.Calls && CommonFrameworkNames.Contains(r.Name)))
            {
                var ext = writer.GetOrCreateExternal(r.Name);
                edges.Add(new NewEdge(r.FromSymbolId, ext, "external:" + r.Name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line));
                return;
            }

            // 4. 多候选：取最窄的非空层级（同文件重载 > import 范围 > 全仓库），而不是全仓库按字母序截断
            var pool = sameFile.Count > 1 ? sameFile : inScope.Count > 1 ? inScope : candidates;
            foreach (var c in pool.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates))
                edges.Add(Edge(r, c, 0.3));
        }

        var handles = edges.Where(e => e.Via == "handles" && TakesMessage(reader, e)).ToList();
        var others = edges.Where(e => e.Via != "handles").ToList();
        writer.WriteEdges(handles);
        var dispatch = DeriveDispatchEdges(reader, others, out var conduitIndexes);
        // 分发方法（IDispatcher.QueryAsync）调用推导出的"接口 → 实现"边：消息已经由 dispatches 边送到 Handler，
        // 再连到 Dispatcher 实现（含测试里的桩）只是噪声，丢掉。与 ImplementationLinker 不给 conduit 边推实现一致
        others = others
            .Select((e, i) => !conduitIndexes.Contains(i)
                ? e.Via?.StartsWith("dispatch-site:", StringComparison.Ordinal) == true ? e with { Via = null } : e
                : e.Via == ImplementationLinker.Via ? null : e with { Via = "conduit" })
            .OfType<NewEdge>().ToList();
        writer.WriteEdges(others);
        writer.WriteEdges(dispatch);
        return Task.CompletedTask;
    }

    /// <summary>分发方法名：调用它只是把消息送进管道，本身不是链路上的一站。</summary>
    private static readonly Regex DispatcherMethod = new("^(Send|Query|Dispatch|Publish|Execute|Handle|Process|Mediate|Request)(Async)?$", RegexOptions.Compiled);

    public static bool IsDispatcherMethod(string name) => DispatcherMethod.IsMatch(name);

    private static List<NewEdge> DeriveDispatchEdges(IndexReader reader, List<NewEdge> edges, out HashSet<int> conduitIndexes)
    {
        var result = new List<NewEdge>();
        conduitIndexes = [];
        var seen = new HashSet<(long, long)>();
        var dispatchSites = edges.Select((edge, index) => (Edge: edge, Index: index))
            .Where(item => item.Edge.Kind == EdgeKind.Calls && item.Edge.Pos is not null)
            .ToLookup(item => (item.Edge.FromId, item.Edge.Pos!.Value));
        for (var i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            if (e.Kind != EdgeKind.Calls || e.ToId is not { } typeId
                || e.Via?.StartsWith("dispatch-site:", StringComparison.Ordinal) != true
                || !int.TryParse(e.Via["dispatch-site:".Length..], out var dispatchPos)) continue;
            var dispatcher = dispatchSites[(e.FromId, dispatchPos)].ToList();
            if (dispatcher.Count == 0) continue;
            var target = reader.GetSymbol(typeId);
            if (target is null || target.Kind is not (SymbolKind.Class or SymbolKind.Struct)) continue;
            var handlers = reader.GetDispatchHandlers(typeId);
            if (handlers.Count == 0) continue;

            conduitIndexes.Add(i);
            foreach (var candidate in dispatcher) conduitIndexes.Add(candidate.Index);

            foreach (var handler in handlers)
            {
                var entry = reader.GetChildren(handler.Id).FirstOrDefault(c => c.Kind == SymbolKind.Method && IsHandlerEntry(c.Name)) ?? handler;
                if (!seen.Add((e.FromId, entry.Id))) continue;
                result.Add(new NewEdge(e.FromId, entry.Id, entry.QualifiedName, EdgeKind.Dispatches, IndexSource.TreeSitter, 0.4, e.FileId, e.Line, Via: "dispatch:" + target.Name, EvidenceLine: e.Line,
                    Pos: e.Pos, Ctx: e.Ctx));
            }
        }
        return result;
    }

    /// <summary>
    /// tree-sitter 的 handles 只看得到基类列表里的 IXxxHandler&lt;M, …&gt;，看不到接口定义：用 Handler 类自己的 Handle* / Consume* / Execute* 方法签名把关——
    /// 这些方法都有签名、却没有一个以 M 为参数类型（Consume* 允许 ConsumeContext&lt;M&gt; 这种包着它的），就不是分派器的 Handler
    /// （后台作业 <c>HandleAsync(IJobContext&lt;M&gt; ctx)</c> 由作业引擎执行）。没有这类方法（处理逻辑在基类里）或签名缺失时照旧保留。
    /// </summary>
    private static bool TakesMessage(IndexReader reader, NewEdge handles)
    {
        if (handles.ToId is not { } messageId || reader.GetSymbol(handles.FromId) is not { } handler) return true;
        var message = reader.GetSymbol(messageId)?.Name;
        if (message is null) return true;
        var entries = reader.GetChildren(handler.Id).Where(c => c.Kind == SymbolKind.Method && IsHandlerEntry(c.Name)).ToList();
        if (entries.Count == 0 || entries.Any(e => string.IsNullOrEmpty(e.Signature))) return true;
        foreach (var entry in entries)
        {
            // 参数表跨行（首行签名里括号没合上）：看不全，不下结论
            if (ParameterTypes(entry.Signature!, entry.Name) is not { } types) return true;
            foreach (var type in types)
            {
                var bare = ShortTypeName(type);
                if (bare == message) return true;
                if (entry.Name.StartsWith("Consume", StringComparison.Ordinal) && Regex.IsMatch(type, @"<\s*(?:[\w.]+\.)?" + Regex.Escape(message) + @"\s*[,>]")) return true;
            }
        }
        return false;
    }

    /// <summary>签名首行里方法名后面那对括号中每个参数的类型（去掉修饰符与参数名）；括号在首行里没合上返回 null。</summary>
    public static List<string>? ParameterTypes(string signature, string methodName)
    {
        var at = signature.IndexOf(methodName + "(", StringComparison.Ordinal);
        if (at < 0) return null;
        var result = new List<string>();
        var open = at + methodName.Length;
        var depth = 0;
        var close = -1;
        for (var i = open; i < signature.Length; i++)
        {
            if (signature[i] is '(' or '<' or '[') depth++;
            else if (signature[i] is ')' or '>' or ']' && --depth == 0) { close = i; break; }
        }
        if (close < 0) return null;
        var start = open + 1;
        depth = 0;
        for (var i = open + 1; i <= close; i++)
        {
            var ch = signature[i];
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            if ((ch == ',' && depth == 0) || i == close)
            {
                var part = signature[start..i].Trim();
                start = i + 1;
                var eq = part.IndexOf('=');
                if (eq >= 0) part = part[..eq].Trim();
                var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("this" or "in" or "ref" or "out" or "params" or "scoped")).ToList();
                if (words.Count >= 2) result.Add(string.Join(' ', words.Take(words.Count - 1)));
            }
        }
        return result;
    }

    /// <summary>Ns.Foo&lt;T&gt;? → Foo。</summary>
    private static string ShortTypeName(string type)
    {
        var t = type.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
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

    internal static string NormalizePath(string path)
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

    internal static string StripScriptExtension(string path)
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
