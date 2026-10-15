using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// Java 的解析（限定名 = package.类型[.嵌套].成员，方法带参数类型简名，见 JavaAdapter；绑定信息的含义也见 JavaAdapter）。
/// <list type="bullet">
/// <item>类型名：外层类型及其成员类型（含继承来的）→ 本文件顶层类型 → 单类型 import → 同包 → 通配 import → 全限定写法；
/// 按限定名找（同名多候选时优先同一模块，见 <see cref="JavaSourceRoots"/>）。找不到 → 仓库外类型，记简单类名。</item>
/// <item>调用：接收者类型读得出来（字段 / 参数 / 局部变量 / new / 强转 / 继承来的字段 / 静态调用 Type.m()）→ 在该类型及其父类型里
/// 按方法名 + 实参个数找，唯一 0.95、个数相同多个各 0.5；m() / this.m() 找本类与父类，再找外层类，再找 static import；super.m() 跳过本类。
/// 父类型链上出现仓库外类型（extends JpaRepository）而方法没声明 → external:&lt;那个类名&gt;.m；接收者类型在仓库外 → external:&lt;类名&gt;.m。</item>
/// <item>接收者类型未知（调用结果、lambda 参数、Lombok 的 log）→ 本文件 / 同包 / import 进来的类型里同名同个数的方法，唯一 0.7、多个各 0.3；
/// 常见集合 / 流 / 字符串方法名直接 external。</item>
/// <item>接口 → 实现：调用落在本仓库接口的方法上时，按 implements（含经子接口）找直接实现类里同名同个数的方法，
/// 只有一个实现类 0.9、多个各 0.5，via = "impl"（与 Roslyn 的 ImplementationLinker 一致）。</item>
/// <item>new T(…) 连到类型 T；extends / implements 解析不到 → external:&lt;简单类名&gt;；references 只连本仓库类型。</item>
/// <item>import a.b.C → C 所在文件的 Module（1.0）；a.b.* → 包里每个文件；static 成员 import 落到所在类型的文件；解析不到 → external:&lt;import 路径&gt;。</item>
/// </list>
/// </summary>
public sealed partial class HeuristicEdgeResolver
{
    public const double JavaResolvedConfidence = 0.95;
    public const double JavaOverloadConfidence = 0.5;
    public const double JavaUnknownReceiverConfidence = 0.7;
    /// <summary>JavaAdapter：Receiver = This 时 ImportSource 为此值表示 super.m()。</summary>
    public const string JavaSuperMarker = "super";
    /// <summary>JavaAdapter：方法引用 X::m 的实参个数（不限）。</summary>
    public const string JavaAnyArity = "-1";
    /// <summary>JavaAdapter：Receiver = Other 时 ImportSource 以此开头表示接收者是调用链的结果，后面是链根的类型写法（或名字）。</summary>
    public const string JavaChainMarker = "()";

    /// <summary>仓库外类型的类名：去掉开头的小写包名段（java.util.List → List），嵌套类型保留外层类（WebClient.Builder）。</summary>
    internal static string JavaExternalLabel(string written)
    {
        var segments = written.Split('.');
        var first = Array.FindIndex(segments, s => s.Length > 0 && char.IsUpper(s[0]));
        return first < 0 ? segments[^1] : string.Join('.', segments[first..]);
    }

    /// <summary>JDK 的包：调用链根是这些包里的类型时不把链上的方法归给它（stream().map() 不是 List 的方法）。</summary>
    private static bool IsJdkImport(string? import) =>
        import is null || import.StartsWith("java.", StringComparison.Ordinal) || import.StartsWith("javax.", StringComparison.Ordinal);

    /// <summary>接收者类型未知时直接当 JDK 方法的名字（集合、Optional、Stream、字符串、日志、Reactor）。</summary>
    public static readonly HashSet<string> JavaCommonMethodNames = new(StringComparer.Ordinal)
    {
        "get", "set", "put", "putAll", "add", "addAll", "remove", "removeIf", "contains", "containsKey", "containsValue", "size", "isEmpty", "clear",
        "iterator", "next", "hasNext", "keySet", "values", "entrySet", "getKey", "getValue", "toArray",
        "stream", "map", "flatMap", "filter", "collect", "forEach", "toList", "reduce", "sorted", "distinct", "limit", "skip", "count",
        "findFirst", "findAny", "anyMatch", "allMatch", "noneMatch", "min", "max",
        "orElse", "orElseThrow", "orElseGet", "isPresent", "ifPresent", "ifPresentOrElse",
        "equals", "hashCode", "toString", "getClass", "compareTo", "length", "charAt", "substring", "trim", "strip", "split", "replace",
        "startsWith", "endsWith", "toLowerCase", "toUpperCase", "append", "format", "valueOf", "join", "matches",
        "trace", "debug", "info", "warn", "error",
        "subscribe", "block", "then", "thenReturn", "just", "retrieve", "bodyToMono", "bodyToFlux", "uri", "onErrorResume", "doOnNext",
    };

    private sealed record JavaFile(long Id, string Path, string Package, string ModuleRoot, SymbolRecord Module);

    /// <summary>父类型：本仓库的 Type，或仓库外类型的简单类名 External。</summary>
    private sealed record JavaSuper(SymbolRecord? Type, string? External);

    /// <summary>
    /// 类型名解析结果：本仓库类型，或 External（仓库外类型的类名：去掉包名，嵌套类型保留外层类，如 WebClient.Builder）；
    /// Import 是让它成为仓库外类型的那条单类型 import（java.lang 等没有 import 的为 null）。
    /// </summary>
    private readonly record struct JavaTypeHit(SymbolRecord? Type, string External, string? Import = null);

    private sealed record JavaMethods(List<SymbolRecord> Exact, List<SymbolRecord> NameOnly, string? ExternalSuper);

    private sealed class JavaIndex
    {
        private readonly Dictionary<long, JavaFile> _files = [];
        private readonly Dictionary<long, SymbolRecord> _byId;
        private readonly ILookup<long, SymbolRecord> _children;
        private readonly Dictionary<string, List<SymbolRecord>> _typesByQualified;
        private readonly Dictionary<string, List<SymbolRecord>> _topTypesByPackage;
        private readonly Dictionary<string, List<SymbolRecord>> _methodsByName;
        private readonly List<SymbolRecord> _types;
        private readonly Dictionary<long, List<string>> _imports = [];
        private readonly ILookup<long, PendingReference> _pendingSupers;
        private readonly IndexReader _reader;
        private readonly Dictionary<long, List<JavaSuper>> _supers = [];
        private readonly Dictionary<long, HashSet<long>> _scopes = [];
        private Dictionary<long, List<SymbolRecord>>? _implementors;

        public JavaIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, SymbolRecord> byId, Dictionary<long, FileRecord> files,
            Dictionary<long, List<(string Target, bool IsPath)>> importsByFile, IReadOnlyList<PendingReference> references, IndexReader reader)
        {
            var java = all.Where(s => files.TryGetValue(s.FileId, out var f) && f.Language == "java").ToList();
            foreach (var module in java.Where(s => s.Kind == SymbolKind.Module))
            {
                var package = JavaSourceRoots.PackageOf(module.Signature);
                var path = files[module.FileId].Path;
                _files[module.FileId] = new JavaFile(module.FileId, path, package, JavaSourceRoots.ModuleRoot(path, package), module);
            }
            _byId = byId;
            _children = java.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            _types = java.Where(IsType).ToList();
            _typesByQualified = _types.GroupBy(s => s.QualifiedName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _topTypesByPackage = _types.Where(t => t.ParentId is { } p && byId.TryGetValue(p, out var parent) && parent.Kind == SymbolKind.Module && _files.ContainsKey(t.FileId))
                .GroupBy(t => _files[t.FileId].Package, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _methodsByName = java.Where(s => s.Kind == SymbolKind.Method).GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var (fileId, list) in importsByFile)
                if (_files.ContainsKey(fileId)) _imports[fileId] = list.Select(i => i.Target).ToList();
            _pendingSupers = references.Where(r => r.Language == "java" && r.Kind is EdgeKind.Inherits or EdgeKind.Implements).ToLookup(r => r.FromSymbolId);
            _reader = reader;
        }

        public static bool IsType(SymbolRecord s) => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum;

        public SymbolRecord? Symbol(long id) => _byId.GetValueOrDefault(id);

        public JavaFile? File(long fileId) => _files.GetValueOrDefault(fileId);

        public IReadOnlyList<string> Imports(long fileId) => _imports.GetValueOrDefault(fileId) ?? [];

        public IEnumerable<SymbolRecord> Children(long id) => _children[id];

        public IReadOnlyList<SymbolRecord> TopTypesIn(string package) => _topTypesByPackage.GetValueOrDefault(package) ?? [];

        /// <summary>符号自身或最近的外层类型。</summary>
        public SymbolRecord? EnclosingType(long? symbolId)
        {
            for (var (cur, n) = (symbolId, 0); cur is { } id && _byId.TryGetValue(id, out var s) && n < 64; cur = s.ParentId, n++)
                if (IsType(s)) return s;
            return null;
        }

        /// <summary>限定名为 qualified 的类型；多个候选（多模块重名）时优先与 fileId 同模块的，再按路径取第一个。</summary>
        public SymbolRecord? ByQualified(string qualified, long fileId)
        {
            if (!_typesByQualified.TryGetValue(qualified, out var list)) return null;
            if (list.Count == 1) return list[0];
            var module = File(fileId)?.ModuleRoot;
            return list.OrderBy(t => File(t.FileId)?.ModuleRoot == module ? 0 : 1).ThenBy(t => File(t.FileId)?.Path, StringComparer.Ordinal).First();
        }

        /// <summary>在 fileId 文件里、contextId 符号处写下的类型名 written（Foo、Outer.Inner、a.b.Foo）。</summary>
        public JavaTypeHit ResolveType(string written, long fileId, long contextId)
        {
            var segments = written.Split('.');
            var cur = ResolveSimple(segments[0], fileId, contextId, out var import);
            var external = import is not null;
            var next = 1;
            if (cur is null && segments.Length > 1 && !external)
            {
                // 全限定写法 a.b.C / a.b.Outer.Inner：最长的能找到类型的前缀
                for (var k = segments.Length; k >= 2 && cur is null; k--)
                    if (ByQualified(string.Join('.', segments[..k]), fileId) is { } t) { cur = t; next = k; }
            }
            for (var i = next; i < segments.Length && cur is not null; i++) cur = MemberType(cur, segments[i]);
            return new JavaTypeHit(cur, JavaExternalLabel(written), import);
        }

        /// <summary>import 是那条把 name 引成仓库外类型的单类型 import（import 了却不在仓库里）。</summary>
        private SymbolRecord? ResolveSimple(string name, long fileId, long contextId, out string? import)
        {
            import = null;
            var file = File(fileId);
            // 1. 外层类型本身与它们的成员类型（含从父类型继承来的）
            for (var t = EnclosingType(contextId); t is not null; t = EnclosingType(t.ParentId))
            {
                if (t.Name == name) return t;
                if (MemberType(t, name) is { } member) return member;
            }
            if (file is null) return null;
            // 2. 本文件的顶层类型
            if (_children[file.Module.Id].FirstOrDefault(c => IsType(c) && c.Name == name) is { } own) return own;
            // 3. 单类型 import：import 了却不在仓库里 → 仓库外类型
            foreach (var target in Imports(fileId))
            {
                if (target.EndsWith(".*", StringComparison.Ordinal) || !target.EndsWith("." + name, StringComparison.Ordinal)) continue;
                if (ByQualified(target, fileId) is { } imported) return imported;
                import = target;
                return null;
            }
            // 4. 同包
            if (ByQualified(file.Package.Length == 0 ? name : file.Package + "." + name, fileId) is { } samePackage) return samePackage;
            // 5. 通配 import（含 static 通配到类型的嵌套类型）
            foreach (var target in Imports(fileId))
                if (target.EndsWith(".*", StringComparison.Ordinal) && ByQualified(target[..^1] + name, fileId) is { } wild) return wild;
            return null;
        }

        /// <summary>类型的成员类型（嵌套类），找不到沿父类型往上（最多 3 层）。</summary>
        public SymbolRecord? MemberType(SymbolRecord type, string name, int depth = 0)
        {
            if (_children[type.Id].FirstOrDefault(c => IsType(c) && c.Name == name) is { } nested) return nested;
            if (depth >= 3) return null;
            foreach (var super in Supers(type))
                if (super.Type is { } s && MemberType(s, name, depth + 1) is { } inherited) return inherited;
            return null;
        }

        /// <summary>
        /// 直接父类型（extends 在前，implements 在后）：本次待解析的引用当场解析（不写库）；不在本次（增量时未变的文件）就读库里已有的边。
        /// </summary>
        public List<JavaSuper> Supers(SymbolRecord type)
        {
            if (_supers.TryGetValue(type.Id, out var cached)) return cached;
            var result = new List<JavaSuper>();
            _supers[type.Id] = result; // 先占位：循环继承、解析父类型名时又回到本类型都不会无限递归
            if (_pendingSupers.Contains(type.Id))
            {
                foreach (var r in _pendingSupers[type.Id])
                {
                    var hit = ResolveType(r.Script?.ImportSource ?? r.Name, r.FileId, type.Id);
                    result.Add(hit.Type is { } t ? new JavaSuper(t, null) : new JavaSuper(null, hit.External));
                }
            }
            else
            {
                foreach (var n in _reader.GetNeighbors(type.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Inherits, EdgeKind.Implements }))
                {
                    if (n.Other is { } o && IsType(o)) result.Add(new JavaSuper(o, null));
                    else if (n.Edge.ToName.StartsWith("external:", StringComparison.Ordinal)) result.Add(new JavaSuper(null, n.Edge.ToName["external:".Length..]));
                }
            }
            return result;
        }

        /// <summary>类型及其父类型（广度优先，近的优先）里名叫 name、实参个数合得上的方法；途经的第一个仓库外父类型一并带回。</summary>
        public JavaMethods FindMethods(SymbolRecord type, string name, int argc, bool skipSelf = false, bool classesOnly = false)
        {
            var nameOnly = new List<SymbolRecord>();
            string? external = null;
            var visited = new HashSet<long> { type.Id };
            var queue = new Queue<SymbolRecord>();
            void EnqueueSupers(SymbolRecord t)
            {
                foreach (var s in Supers(t))
                {
                    if (s.Type is { } st)
                    {
                        if ((!classesOnly || st.Kind != SymbolKind.Interface) && visited.Add(st.Id)) queue.Enqueue(st);
                    }
                    else if (!classesOnly) external ??= s.External;
                }
            }
            if (skipSelf) EnqueueSupers(type); else queue.Enqueue(type);
            while (queue.Count > 0 && visited.Count < 64)
            {
                var t = queue.Dequeue();
                var named = _children[t.Id].Where(m => m.Kind == SymbolKind.Method && m.Name == name).OrderBy(m => m.StartLine).ToList();
                var exact = named.Where(m => ArityMatches(m, argc)).ToList();
                if (exact.Count > 0) return new JavaMethods(exact, nameOnly, external);
                if (nameOnly.Count == 0) nameOnly.AddRange(named);
                EnqueueSupers(t);
            }
            return new JavaMethods([], nameOnly, external);
        }

        /// <summary>类型及其父类型（广度优先）里名叫 name 的字段。</summary>
        public SymbolRecord? FindField(SymbolRecord type, string name)
        {
            var visited = new HashSet<long> { type.Id };
            var queue = new Queue<SymbolRecord>([type]);
            while (queue.Count > 0 && visited.Count < 64)
            {
                var t = queue.Dequeue();
                if (_children[t.Id].FirstOrDefault(c => c.Kind == SymbolKind.Field && c.Name == name) is { } field) return field;
                foreach (var s in Supers(t))
                    if (s.Type is { } st && visited.Add(st.Id)) queue.Enqueue(st);
            }
            return null;
        }

        /// <summary>直接实现接口 iface 的类型（implements 它或它的子接口；不含实现类的子类）。</summary>
        public IReadOnlyList<SymbolRecord> Implementors(SymbolRecord iface)
        {
            if (_implementors is null)
            {
                _implementors = [];
                foreach (var t in _types.Where(t => t.Kind != SymbolKind.Interface))
                {
                    var reached = new HashSet<long>();
                    var stack = new Stack<SymbolRecord>(Supers(t).Select(s => s.Type).OfType<SymbolRecord>().Where(s => s.Kind == SymbolKind.Interface));
                    while (stack.Count > 0 && reached.Count < 64)
                    {
                        var i = stack.Pop();
                        if (!reached.Add(i.Id)) continue;
                        if (!_implementors.TryGetValue(i.Id, out var list)) _implementors[i.Id] = list = [];
                        list.Add(t);
                        foreach (var s in Supers(i)) if (s.Type is { Kind: SymbolKind.Interface } si) stack.Push(si);
                    }
                }
            }
            return _implementors.GetValueOrDefault(iface.Id) ?? [];
        }

        /// <summary>本文件、同包、import 进来的类型与通配 import 的包所在的文件：接收者类型未知时的候选范围。</summary>
        public HashSet<long> Scope(long fileId)
        {
            if (_scopes.TryGetValue(fileId, out var cached)) return cached;
            var set = new HashSet<long> { fileId };
            if (File(fileId) is { } file)
            {
                foreach (var t in TopTypesIn(file.Package)) set.Add(t.FileId);
                foreach (var target in Imports(fileId))
                {
                    if (target.EndsWith(".*", StringComparison.Ordinal))
                        foreach (var t in TopTypesIn(target[..^2])) set.Add(t.FileId);
                    else if (ByQualified(target, fileId) is { } imported) set.Add(imported.FileId);
                }
            }
            _scopes[fileId] = set;
            return set;
        }

        public IReadOnlyList<SymbolRecord> MethodsNamed(string name) => _methodsByName.GetValueOrDefault(name) ?? [];
    }

    /// <summary>方法限定名里的参数个数与是否变长："a.B.m(String,int...)" → (2, true)。</summary>
    private static (int Count, bool Varargs) JavaArity(SymbolRecord method)
    {
        var q = method.QualifiedName;
        var open = q.LastIndexOf('(');
        if (open < 0 || !q.EndsWith(')')) return (0, false);
        var inner = q[(open + 1)..^1];
        return inner.Length == 0 ? (0, false) : (inner.Count(c => c == ',') + 1, inner.EndsWith("...", StringComparison.Ordinal));
    }

    private static bool ArityMatches(SymbolRecord method, int argc)
    {
        if (argc < 0) return true;
        var (count, varargs) = JavaArity(method);
        return varargs ? argc >= count - 1 : argc == count;
    }

    /// <summary>字段 Signature（"private final Map&lt;K, V&gt; name"）里的声明类型写法：去名字、修饰符与泛型实参；数组返回 null。</summary>
    internal static string? JavaFieldType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return null;
        var text = signature.Trim();
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace <= 0) return null;
        text = text[..lastSpace].Trim();
        string[] modifiers = ["public ", "protected ", "private ", "static ", "final ", "transient ", "volatile "];
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in modifiers)
                if (text.StartsWith(m, StringComparison.Ordinal)) { text = text[m.Length..].TrimStart(); changed = true; }
        }
        var lt = text.IndexOf('<');
        if (lt >= 0) text = text[..lt];
        text = text.Trim();
        return text.Length == 0 || text.EndsWith(']') ? null : text;
    }

    private static List<NewEdge> ResolveJava(PendingReference r, JavaIndex j, IndexWriter writer)
    {
        List<NewEdge> External(string name) =>
            [new NewEdge(r.FromSymbolId, writer.GetOrCreateExternal(name), "external:" + name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line, Via: r.Via)];
        List<NewEdge> To(SymbolRecord target, double confidence) => [Edge(r, target, confidence)];
        var b = r.Script ?? new ScriptBinding(ScriptReceiver.None);

        switch (r.Kind)
        {
            case EdgeKind.Imports:
                return ResolveJavaImport(r, j, writer);
            case EdgeKind.References:
                return j.ResolveType(b.ImportSource ?? r.Name, r.FileId, r.FromSymbolId).Type is { } referenced ? To(referenced, JavaResolvedConfidence) : NoEdges;
            case EdgeKind.Inherits or EdgeKind.Implements:
            {
                var hit = j.ResolveType(b.ImportSource ?? r.Name, r.FileId, r.FromSymbolId);
                return hit.Type is { } super ? To(super, JavaResolvedConfidence) : External(hit.External);
            }
            case EdgeKind.Calls:
                break;
            default:
                return NoEdges;
        }

        var argc = int.TryParse(b.ImportedName, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;

        // 在类型 type 上调 r.Name：本仓库方法（含接口 → 实现的派生边）、继承自仓库外类型的 external:<类名>.m、找不到时的 external:m
        List<NewEdge>? OnType(SymbolRecord type, bool skipSelf, bool fallbackToExternal)
        {
            var found = j.FindMethods(type, r.Name, argc, skipSelf);
            if (found.Exact.Count > 0) return WithImplementations(found.Exact, found.Exact.Count == 1 ? JavaResolvedConfidence : JavaOverloadConfidence);
            if (found.NameOnly.Count > 0) return WithImplementations(found.NameOnly, found.NameOnly.Count == 1 ? JavaUnknownReceiverConfidence : 0.3);
            if (found.ExternalSuper is { } ext) return External(ext + "." + r.Name);
            if (IsGeneratedAccessor(type)) return NoEdges;
            return fallbackToExternal ? External(r.Name) : null;
        }

        // 记录组件的访问器 p.left()、Lombok 生成的 getX() / isX() / setX(v)：类型（或父类）里有对应字段却没声明方法 → 不记边（不是仓库外调用）
        bool IsGeneratedAccessor(SymbolRecord type)
        {
            var name = r.Name;
            if (argc == 0 && j.FindField(type, name) is not null) return true;
            string? property = argc switch
            {
                0 when name.Length > 3 && name.StartsWith("get", StringComparison.Ordinal) && char.IsUpper(name[3]) => name[3..],
                0 when name.Length > 2 && name.StartsWith("is", StringComparison.Ordinal) && char.IsUpper(name[2]) => name[2..],
                1 when name.Length > 3 && name.StartsWith("set", StringComparison.Ordinal) && char.IsUpper(name[3]) => name[3..],
                _ => null,
            };
            return property is not null && j.FindField(type, char.ToLowerInvariant(property[0]) + property[1..]) is not null;
        }

        List<NewEdge> WithImplementations(List<SymbolRecord> targets, double confidence)
        {
            var edges = targets.Take(MaxCandidates).Select(t => Edge(r, t, confidence)).ToList();
            foreach (var target in targets.Take(MaxCandidates))
            {
                if (target.ParentId is not { } p || j.Symbol(p) is not { Kind: SymbolKind.Interface } iface) continue;
                var implementors = j.Implementors(iface);
                if (implementors.Count is 0 or > ImplementationLinker.MaxImplementations) continue;
                var derived = implementors.Count == 1 ? 0.9 : 0.5;
                var seen = new HashSet<long>();
                foreach (var impl in implementors.OrderBy(i => i.QualifiedName, StringComparer.Ordinal))
                {
                    // 实现类及其父类（不经接口）里同名、同个数的方法
                    var method = j.FindMethods(impl, target.Name, JavaArity(target).Count, classesOnly: true).Exact.FirstOrDefault();
                    if (method is null || !seen.Add(method.Id)) continue;
                    edges.Add(new NewEdge(r.FromSymbolId, method.Id, method.QualifiedName, EdgeKind.Calls, IndexSource.TreeSitter, derived,
                        r.FileId, r.Line, Via: ImplementationLinker.Via, EvidenceLine: r.Line));
                }
            }
            return edges;
        }

        List<NewEdge> OnWrittenType(string written, long fileId, long contextId)
        {
            var hit = j.ResolveType(written, fileId, contextId);
            return hit.Type is { } type ? OnType(type, skipSelf: false, fallbackToExternal: true)! : External(hit.External + "." + r.Name);
        }

        switch (b.Receiver)
        {
            case ScriptReceiver.None:
            {
                // new T(…)：连到类型
                var hit = j.ResolveType(b.ImportSource ?? r.Name, r.FileId, r.FromSymbolId);
                return hit.Type is { } created ? To(created, JavaResolvedConfidence) : External(hit.External);
            }
            case ScriptReceiver.This:
            {
                var enclosing = j.EnclosingType(r.FromSymbolId);
                if (enclosing is null) return External(r.Name);
                if (b.ImportSource == JavaSuperMarker) return OnType(enclosing, skipSelf: true, fallbackToExternal: true)!;
                // 本类及父类 → 外层类 → static import；都没有时记成最内层类继承的仓库外类型上的方法
                string? inheritedExternal = null;
                for (var t = enclosing; t is not null; t = j.EnclosingType(t.ParentId))
                {
                    var found = j.FindMethods(t, r.Name, argc);
                    if (found.Exact.Count > 0 || found.NameOnly.Count > 0) return OnType(t, skipSelf: false, fallbackToExternal: true)!;
                    inheritedExternal ??= found.ExternalSuper;
                }
                if (StaticImport() is { } imported) return imported;
                if (inheritedExternal is null && IsGeneratedAccessor(enclosing)) return NoEdges;
                return External(inheritedExternal is null ? r.Name : inheritedExternal + "." + r.Name);
            }
            case ScriptReceiver.Other when b.ImportSource is { } chain && chain.StartsWith(JavaChainMarker, StringComparison.Ordinal):
            {
                // 调用链的结果：返回值类型不推。链根是 import 进来的第三方类型（WebClient.Builder、Mono）时归到它的顶层类，否则当类型未知
                var root = chain[JavaChainMarker.Length..];
                JavaTypeHit hit;
                if (root.Length > 0 && !root.Contains('.') && char.IsLower(root[0]))
                {
                    if (j.EnclosingType(r.FromSymbolId) is not { } enclosing || j.FindField(enclosing, root) is not { } field || JavaFieldType(field.Signature) is not { } fieldType)
                        return Unknown();
                    hit = j.ResolveType(fieldType, field.FileId, field.Id);
                }
                else if (root.Length > 0) hit = j.ResolveType(root, r.FileId, r.FromSymbolId);
                else return Unknown();
                return hit.Type is null && !IsJdkImport(hit.Import) ? External(hit.External.Split('.')[0] + "." + r.Name) : Unknown();
            }
            case ScriptReceiver.Other when b.ImportSource is { } written:
                return OnWrittenType(written, r.FileId, r.FromSymbolId);
            case ScriptReceiver.Import when b.ImportSource is { } name:
            {
                // 继承来的字段（本文件声明的字段适配器已经认出来了）
                if (!name.Contains('.') && j.EnclosingType(r.FromSymbolId) is { } enclosing && j.FindField(enclosing, name) is { } field)
                    return JavaFieldType(field.Signature) is { } fieldType ? OnWrittenType(fieldType, field.FileId, field.Id) : Unknown();
                // 类型名：静态调用 Type.m()、方法引用 Type::m
                var hit = j.ResolveType(name, r.FileId, r.FromSymbolId);
                if (hit.Type is { } type) return OnType(type, skipSelf: false, fallbackToExternal: true)!;
                if (hit.Import is not null) return External(hit.External + "." + r.Name); // import 进来的仓库外类型（含 WebClient.Builder 这类嵌套）
                var segments = name.Split('.');
                // 大写开头的名字（Math、LoggerFactory）或 a.b.Type 全限定写法：仓库外类型；System.out 这类字段链不认
                if (char.IsUpper(segments[^1][0]) && segments[..^1].All(s => s.Length > 0 && char.IsLower(s[0]))) return External(segments[^1] + "." + r.Name);
                return segments.Length > 1 ? External(r.Name) : Unknown();
            }
            default:
                return Unknown();
        }

        // m() 来自 import static a.b.C.m / a.b.C.*
        List<NewEdge>? StaticImport()
        {
            foreach (var target in j.Imports(r.FileId))
            {
                if (target.EndsWith("." + r.Name, StringComparison.Ordinal))
                {
                    var owner = target[..^(r.Name.Length + 1)];
                    if (j.ByQualified(owner, r.FileId) is { } type) return OnType(type, skipSelf: false, fallbackToExternal: true);
                    return External(owner[(owner.LastIndexOf('.') + 1)..] + "." + r.Name);
                }
                if (target.EndsWith(".*", StringComparison.Ordinal) && j.ByQualified(target[..^2], r.FileId) is { } wildType
                    && OnType(wildType, skipSelf: false, fallbackToExternal: false) is { } viaWildcard)
                    return viaWildcard;
            }
            return null;
        }

        // 接收者类型未知：本文件 / 同包 / import 范围里同名同个数的方法
        List<NewEdge> Unknown()
        {
            if (JavaCommonMethodNames.Contains(r.Name)) return External(r.Name);
            var scope = j.Scope(r.FileId);
            var pool = j.MethodsNamed(r.Name).Where(m => scope.Contains(m.FileId) && ArityMatches(m, argc)).ToList();
            if (pool.Count == 1) return WithImplementations(pool, JavaUnknownReceiverConfidence);
            if (pool.Count > 1) return WithImplementations(pool.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).ToList(), 0.3);
            return External(r.Name);
        }
    }

    private static List<NewEdge> ResolveJavaImport(PendingReference r, JavaIndex j, IndexWriter writer)
    {
        List<NewEdge> Modules(IEnumerable<SymbolRecord> types) => types
            .Select(t => j.File(t.FileId)?.Module).OfType<SymbolRecord>().DistinctBy(m => m.Id)
            .OrderBy(m => m.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates)
            .Select(m => new NewEdge(r.FromSymbolId, m.Id, m.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line)).ToList();

        var target = r.Name;
        if (target.EndsWith(".*", StringComparison.Ordinal))
        {
            var package = target[..^2];
            if (j.TopTypesIn(package) is { Count: > 0 } types) return Modules(types);
            if (j.ByQualified(package, r.FileId) is { } owner) return Modules([owner]); // static 通配 / 嵌套类型通配
        }
        else
        {
            if (j.ByQualified(target, r.FileId) is { } type) return Modules([type]);
            var dot = target.LastIndexOf('.');
            if (dot > 0 && j.ByQualified(target[..dot], r.FileId) is { } owner) return Modules([owner]); // static 成员 import
        }
        return [new NewEdge(r.FromSymbolId, writer.GetOrCreateExternal(target), "external:" + target, EdgeKind.Imports, IndexSource.TreeSitter, 0.5, r.FileId, r.Line)];
    }
}
