using System.Globalization;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// C# 调用在只有 tree-sitter 时的解析（绑定信息的含义见 CSharpAdapter；限定名不带参数签名，参数个数从方法的 Signature 数）。
/// <list type="bullet">
/// <item>类型名：外层类型及其嵌套类型 → 所在命名空间链（由内向外）→ using 的命名空间 → 全仓库唯一（多半是 global using，降一档）；
/// 同一限定名的多个定义（partial、多项目重名）合成一个类型，成员取并集，候选里优先与调用方路径最接近的。</item>
/// <item>接收者类型读得出来（字段 / 属性 / 参数 / 局部变量 / new / 强转 / 静态调用 Type.m() / 父类与 partial 里的字段）→
/// 在该类型及其父类型里按方法名 + 实参个数找：唯一 0.9（类型只是全仓库唯一时 0.7）、个数相同多个各 0.3；
/// 只有名字对得上（个数对不上）唯一 0.7。父类型链上出现仓库外类型而方法没声明 → external:&lt;那个类名&gt;.m。
/// 类型在仓库外 / 方法不在类型上 → 仓库里的扩展方法（this 参数类型对得上 0.9，否则唯一 0.7）→
/// 仓库里在 base_list 写了这个仓库外类型名的类上的同名方法（接口定义不在仓库里，唯一 0.5、多个各 0.3）→ external:&lt;类名&gt;.m。</item>
/// <item>成员链（var x = Foo()、a.B.C()、await x.GetAsync()）：字段 / 属性看声明类型，方法看返回类型（await 拆 Task&lt;T&gt;，ConfigureAwait 忽略，
/// NSubstitute 的 Received(…) 视为返回自身）；推到仓库外类型之后断了 → 只认范围内的扩展方法，否则 external:m。</item>
/// <item>m() / this.m()：局部函数 → 本类与父类 → 外层类 → using static；base.m() 跳过本类。调用委托（局部变量 / 参数 / 字段）不建边。
/// 唯一对得上的是调用方自己、父类里也有同名同个数的 → 两个都当候选 0.3。</item>
/// <item>接收者类型未知（lambda 参数、元组解构、推不下去的链）→ 本文件 / 命名空间链 / using 范围里同名、个数合得上的方法
/// （含按扩展方式调用的扩展方法），唯一 0.7、多个各 0.3；范围外全仓库唯一 0.5、多个各 0.3；常见框架方法名直接 external。</item>
/// <item>接口 → 实现：落在本仓库接口方法上时，按 base_list 找直接实现类里同名同个数的方法，唯一实现 0.9、多个各 0.5，via = "impl"。</item>
/// </list>
/// </summary>
public sealed partial class HeuristicEdgeResolver
{
    /// <summary>CSharpAdapter：Receiver = This 时 ImportSource 为此值表示 base.m()。</summary>
    public const string CSharpBaseMarker = "base";
    /// <summary>CSharpAdapter：Receiver = This 时 ImportSource 为此值表示调用的是局部变量 / 参数 / 字段（委托），不建边。</summary>
    public const string CSharpDelegateMarker = "delegate";
    public const double CSharpTypedConfidence = 0.9;
    public const double CSharpNameConfidence = 0.7;
    public const double CSharpAmbiguousConfidence = 0.3;

    private static readonly HashSet<string> CSharpPredefinedTypes = new(StringComparer.Ordinal)
    {
        "string", "object", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "bool", "char", "decimal", "double", "float", "nint", "nuint", "dynamic", "void",
    };

    /// <summary>
    /// 返回接收者本身的仓库外扩展方法（NSubstitute 的 T Received&lt;T&gt;(this T)）：mock.Received(1).Foo() 的 Foo 仍在 mock 的类型上。
    /// </summary>
    private static readonly HashSet<string> CSharpIdentityExtensions = new(StringComparer.Ordinal)
    {
        "Received", "DidNotReceive", "ReceivedWithAnyArgs", "DidNotReceiveWithAnyArgs",
    };

    /// <summary>同一限定名的类型（partial 的各部分、多项目里的同名类型合在一起）。</summary>
    private sealed record CsType(string QualifiedName, string Name, string Namespace, List<SymbolRecord> Parts)
    {
        public bool IsInterface => Parts.All(p => p.Kind == SymbolKind.Interface);
    }

    /// <summary>父类型：本仓库的 Type，或仓库外类型的简单类名 External。</summary>
    private sealed record CsSuper(CsType? Type, string? External);

    /// <summary>类型名解析结果：本仓库类型（Fallback 表示只是全仓库唯一）；Ambiguous 是范围内找不到、全仓库又有多个的候选。</summary>
    private sealed record CsTypeHit(CsType? Type, string External, bool Fallback = false, IReadOnlyList<CsType>? Ambiguous = null);

    private sealed record CsMethods(List<SymbolRecord> Exact, List<SymbolRecord> NameOnly, string? ExternalSuper);

    /// <summary>
    /// 接收者描述（见 CSharpAdapter.Chain）推出来的类型：本仓库类型 Type，或仓库外类型名 External；都为 null 是推不出来，
    /// 此时 ExternalRooted 表示是走到仓库外类型之后断的（之后的调用只可能是仓库里的扩展方法）。
    /// </summary>
    private sealed record CsChainHit(CsType? Type, string? External, bool Fallback, bool ExternalRooted);

    /// <summary>方法签名里数出来的参数：最少 / 最多实参个数（可选参数、params），是不是扩展方法、this 参数的类型名。</summary>
    public readonly record struct CSharpParameters(int Min, int Max, bool Extension, string? ThisType)
    {
        public static readonly CSharpParameters Unknown = new(0, int.MaxValue, false, null);
        public bool Accepts(int argc) => argc < 0 || (Min <= argc && argc <= Max);
    }

    private sealed class CSharpIndex
    {
        private readonly Dictionary<long, SymbolRecord> _byId;
        private readonly Dictionary<long, FileRecord> _files;
        private readonly ILookup<long, SymbolRecord> _children;
        private readonly Dictionary<long, CsType> _typeOfPart = [];
        private readonly Dictionary<string, CsType> _typesByQualified = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CsType>> _typesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SymbolRecord>> _methodsByName;
        private readonly Dictionary<long, List<string>> _imports = [];
        private readonly ILookup<long, PendingReference> _pendingSupers;
        private readonly IndexReader _reader;
        private readonly Dictionary<string, List<CsSuper>> _supers = new(StringComparer.Ordinal);
        private readonly Dictionary<long, CSharpParameters> _parameters = [];
        private readonly Dictionary<long, string> _namespaceOf = [];
        private Dictionary<string, List<CsType>>? _implementors;
        private Dictionary<string, List<CsType>>? _externalImplementors;

        public CSharpIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, SymbolRecord> byId, Dictionary<long, FileRecord> files,
            Dictionary<long, List<(string Target, bool IsPath)>> importsByFile, IReadOnlyList<PendingReference> references, IndexReader reader)
        {
            _byId = byId;
            _files = files;
            var cs = all.Where(s => files.TryGetValue(s.FileId, out var f) && f.Language == "csharp").ToList();
            _children = cs.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            foreach (var group in cs.Where(IsType).GroupBy(s => s.QualifiedName, StringComparer.Ordinal))
            {
                var parts = group.OrderBy(p => files[p.FileId].Path, StringComparer.Ordinal).ThenBy(p => p.StartLine).ToList();
                var type = new CsType(group.Key, parts[0].Name, NamespaceOf(parts[0].Id), parts);
                _typesByQualified[group.Key] = type;
                foreach (var p in parts) _typeOfPart[p.Id] = type;
                if (!_typesByName.TryGetValue(type.Name, out var list)) _typesByName[type.Name] = list = [];
                list.Add(type);
            }
            _methodsByName = cs.Where(s => s.Kind == SymbolKind.Method).GroupBy(s => s.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var (fileId, list) in importsByFile)
                if (files.TryGetValue(fileId, out var f) && f.Language == "csharp") _imports[fileId] = list.Select(i => i.Target).ToList();
            _pendingSupers = references.Where(r => r.Language == "csharp" && r.Kind is EdgeKind.Inherits or EdgeKind.Implements).ToLookup(r => r.FromSymbolId);
            _reader = reader;
        }

        public static bool IsType(SymbolRecord s) => s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum;

        public SymbolRecord? Symbol(long id) => _byId.GetValueOrDefault(id);

        public IReadOnlyList<string> Imports(long fileId) => _imports.GetValueOrDefault(fileId) ?? [];

        public string PathOf(long fileId) => _files.TryGetValue(fileId, out var f) ? f.Path : "";

        /// <summary>符号所在的命名空间（最近的 Namespace 祖先的限定名），没有为 ""。</summary>
        public string NamespaceOf(long symbolId)
        {
            if (_namespaceOf.TryGetValue(symbolId, out var cached)) return cached;
            var ns = "";
            for (var (cur, n) = ((long?)symbolId, 0); cur is { } id && _byId.TryGetValue(id, out var s) && n < 64; cur = s.ParentId, n++)
                if (s.Kind == SymbolKind.Namespace) { ns = s.QualifiedName; break; }
            _namespaceOf[symbolId] = ns;
            return ns;
        }

        /// <summary>命名空间链由内向外：A.B.C → A.B.C、A.B、A、""。</summary>
        public static IEnumerable<string> NamespaceChain(string ns)
        {
            for (var cur = ns; cur.Length > 0; cur = cur.Contains('.') ? cur[..cur.LastIndexOf('.')] : "")
                yield return cur;
            yield return "";
        }

        /// <summary>符号自身或最近的外层类型。</summary>
        public CsType? EnclosingType(long? symbolId)
        {
            for (var (cur, n) = (symbolId, 0); cur is { } id && _byId.TryGetValue(id, out var s) && n < 64; cur = s.ParentId, n++)
                if (IsType(s)) return _typeOfPart.GetValueOrDefault(s.Id);
            return null;
        }

        /// <summary>类型外面的那层类型（嵌套类型），没有为 null。</summary>
        public CsType? OuterType(CsType type) => EnclosingType(type.Parts[0].ParentId);

        public IEnumerable<SymbolRecord> Members(CsType type) => type.Parts.SelectMany(p => _children[p.Id]);

        public IEnumerable<SymbolRecord> Children(long id) => _children[id];

        public CSharpParameters Parameters(SymbolRecord method)
        {
            if (!_parameters.TryGetValue(method.Id, out var p)) _parameters[method.Id] = p = CSharpParameterInfo(method.Signature, method.Name);
            return p;
        }

        /// <summary>在 fileId 文件里、contextId 符号处写下的类型名 written（Foo、Outer.Inner、A.B.Foo）。</summary>
        public CsTypeHit ResolveType(string written, long fileId, long? contextId)
        {
            var segments = written.Split('.');
            var label = segments[^1];
            if (segments.Length == 1) return Simple(written, fileId, contextId);
            if (_typesByQualified.TryGetValue(written, out var exact)) return new CsTypeHit(exact, label);
            foreach (var ns in NamespaceChain(contextId is { } c ? NamespaceOf(c) : ""))
                if (ns.Length > 0 && _typesByQualified.TryGetValue(ns + "." + written, out var relative)) return new CsTypeHit(relative, label);
            foreach (var import in Imports(fileId))
                if (_typesByQualified.TryGetValue(import + "." + written, out var viaUsing)) return new CsTypeHit(viaUsing, label);
            // Outer.Inner：先解析最左边的类型再往里走
            var head = Simple(segments[0], fileId, contextId);
            var cur = head.Type;
            for (var i = 1; i < segments.Length && cur is not null; i++) cur = NestedType(cur, segments[i]);
            return cur is not null ? new CsTypeHit(cur, label, head.Fallback) : new CsTypeHit(null, label);
        }

        private CsTypeHit Simple(string name, long fileId, long? contextId)
        {
            // 1. 外层类型本身与它们的嵌套类型（含父类型里的）
            for (var t = EnclosingType(contextId); t is not null; t = OuterType(t))
            {
                if (t.Name == name) return new CsTypeHit(t, name);
                if (NestedType(t, name) is { } nested) return new CsTypeHit(nested, name);
            }
            if (!_typesByName.TryGetValue(name, out var candidates)) return new CsTypeHit(null, name);
            // 2. 命名空间链（由内向外）
            foreach (var ns in NamespaceChain(contextId is { } c ? NamespaceOf(c) : ""))
            {
                var inNs = candidates.Where(t => t.Namespace == ns).ToList();
                if (inNs.Count > 0) return new CsTypeHit(Nearest(inNs, fileId), name);
            }
            // 3. using
            var imports = Imports(fileId);
            var viaUsing = candidates.Where(t => imports.Contains(t.Namespace)).ToList();
            if (viaUsing.Count > 0) return new CsTypeHit(Nearest(viaUsing, fileId), name);
            // 4. 全仓库唯一：多半来自 global using，降一档
            return candidates.Count == 1 ? new CsTypeHit(candidates[0], name, Fallback: true) : new CsTypeHit(null, name, Ambiguous: candidates);
        }

        /// <summary>多个候选里与调用文件路径公共前缀最长的（同项目优先）。</summary>
        public CsType Nearest(List<CsType> types, long fileId)
        {
            if (types.Count == 1) return types[0];
            var path = PathOf(fileId);
            return types.OrderByDescending(t => t.Parts.Max(p => CommonPrefix(PathOf(p.FileId), path))).ThenBy(t => t.QualifiedName, StringComparer.Ordinal).First();
        }

        public List<SymbolRecord> NearestSymbols(List<SymbolRecord> symbols, long fileId)
        {
            if (symbols.Count < 2) return symbols;
            var path = PathOf(fileId);
            var best = symbols.Max(s => CommonPrefix(PathOf(s.FileId), path));
            return symbols.Where(s => CommonPrefix(PathOf(s.FileId), path) == best).ToList();
        }

        private static int CommonPrefix(string a, string b)
        {
            var n = 0;
            var segA = a.Split('/');
            var segB = b.Split('/');
            while (n < segA.Length - 1 && n < segB.Length - 1 && string.Equals(segA[n], segB[n], StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        /// <summary>类型的嵌套类型，找不到沿父类型往上（最多 3 层）。</summary>
        public CsType? NestedType(CsType type, string name, int depth = 0)
        {
            foreach (var m in Members(type))
                if (IsType(m) && m.Name == name && _typeOfPart.TryGetValue(m.Id, out var nested)) return nested;
            if (depth >= 3) return null;
            foreach (var super in Supers(type))
                if (super.Type is { } s && NestedType(s, name, depth + 1) is { } inherited) return inherited;
            return null;
        }

        /// <summary>直接父类型（各 partial 部分的 base_list 合起来）。本次待解析的引用当场解析；不在本次（增量时未变的文件）就读库里已有的边。</summary>
        public List<CsSuper> Supers(CsType type)
        {
            if (_supers.TryGetValue(type.QualifiedName, out var cached)) return cached;
            var result = new List<CsSuper>();
            _supers[type.QualifiedName] = result; // 先占位：循环继承不会无限递归
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in type.Parts)
            {
                if (_pendingSupers.Contains(part.Id))
                {
                    foreach (var r in _pendingSupers[part.Id])
                    {
                        // 从类型外面那层开始找：class Repo : Repo<T> 不能解析成自己
                        var hit = ResolveType(r.Name, r.FileId, part.ParentId);
                        if (hit.Type is { } t && t.QualifiedName != type.QualifiedName) { if (seen.Add(t.QualifiedName)) result.Add(new CsSuper(t, null)); }
                        else if (hit.Type is null && hit.Ambiguous is null && seen.Add("external:" + hit.External)) result.Add(new CsSuper(null, hit.External));
                    }
                }
                else
                {
                    foreach (var n in _reader.GetNeighbors(part.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Inherits, EdgeKind.Implements }))
                    {
                        if (n.Other is { } o && _typeOfPart.TryGetValue(o.Id, out var t)) { if (seen.Add(t.QualifiedName)) result.Add(new CsSuper(t, null)); }
                        else if (n.Edge.ToName.StartsWith("external:", StringComparison.Ordinal) && seen.Add(n.Edge.ToName))
                            result.Add(new CsSuper(null, n.Edge.ToName["external:".Length..]));
                    }
                }
            }
            return result;
        }

        /// <summary>类型及其父类型（广度优先，近的优先）里名叫 name、实参个数合得上的方法；途经的第一个仓库外父类型一并带回。</summary>
        public CsMethods FindMethods(CsType type, string name, int argc, bool skipSelf = false, bool classesOnly = false)
        {
            var nameOnly = new List<SymbolRecord>();
            string? external = null;
            var visited = new HashSet<string>(StringComparer.Ordinal) { type.QualifiedName };
            var queue = new Queue<CsType>();
            void EnqueueSupers(CsType t)
            {
                foreach (var s in Supers(t))
                {
                    if (s.Type is { } st)
                    {
                        if ((!classesOnly || !st.IsInterface) && visited.Add(st.QualifiedName)) queue.Enqueue(st);
                    }
                    else if (!classesOnly) external ??= s.External;
                }
            }
            if (skipSelf) EnqueueSupers(type); else queue.Enqueue(type);
            while (queue.Count > 0 && visited.Count < 64)
            {
                var t = queue.Dequeue();
                var named = Members(t).Where(m => m.Kind == SymbolKind.Method && m.Name == name).OrderBy(m => m.FileId).ThenBy(m => m.StartLine).ToList();
                var exact = named.Where(m => Parameters(m).Accepts(argc)).ToList();
                if (exact.Count > 0) return new CsMethods(exact, nameOnly, external);
                if (nameOnly.Count == 0) nameOnly.AddRange(named);
                EnqueueSupers(t);
            }
            return new CsMethods([], nameOnly, external);
        }

        /// <summary>类型及其父类型里名叫 name 的字段或属性。</summary>
        public SymbolRecord? FindMember(CsType type, string name)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { type.QualifiedName };
            var queue = new Queue<CsType>([type]);
            while (queue.Count > 0 && visited.Count < 64)
            {
                var t = queue.Dequeue();
                if (Members(t).FirstOrDefault(c => c.Kind is SymbolKind.Field or SymbolKind.Property && c.Name == name) is { } member) return member;
                foreach (var s in Supers(t))
                    if (s.Type is { } st && visited.Add(st.QualifiedName)) queue.Enqueue(st);
            }
            return null;
        }

        /// <summary>type 自身与全部父类型的简单名（扩展方法的 this 类型比对用）。</summary>
        public HashSet<string> SelfAndSuperNames(CsType? type, string? written)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (written is not null) names.Add(written.Split('.')[^1]);
            if (type is null) return names;
            var queue = new Queue<CsType>([type]);
            var visited = new HashSet<string>(StringComparer.Ordinal) { type.QualifiedName };
            while (queue.Count > 0 && visited.Count < 64)
            {
                var t = queue.Dequeue();
                names.Add(t.Name);
                foreach (var s in Supers(t))
                {
                    if (s.Type is { } st) { if (visited.Add(st.QualifiedName)) queue.Enqueue(st); }
                    else if (s.External is { } e) names.Add(e.Split('.')[^1]);
                }
            }
            return names;
        }

        /// <summary>直接实现接口 iface 的类型（base_list 里有它或它的子接口；不含实现类的子类）。</summary>
        public IReadOnlyList<CsType> Implementors(CsType iface)
        {
            if (_implementors is null)
            {
                _implementors = new Dictionary<string, List<CsType>>(StringComparer.Ordinal);
                foreach (var t in _typesByQualified.Values.Where(t => !t.IsInterface))
                {
                    var reached = new HashSet<string>(StringComparer.Ordinal);
                    var stack = new Stack<CsType>(Supers(t).Select(s => s.Type).OfType<CsType>().Where(s => s.IsInterface));
                    while (stack.Count > 0 && reached.Count < 64)
                    {
                        var i = stack.Pop();
                        if (!reached.Add(i.QualifiedName)) continue;
                        if (!_implementors.TryGetValue(i.QualifiedName, out var list)) _implementors[i.QualifiedName] = list = [];
                        list.Add(t);
                        foreach (var s in Supers(i)) if (s.Type is { IsInterface: true } si) stack.Push(si);
                    }
                }
            }
            return _implementors.GetValueOrDefault(iface.QualifiedName) ?? [];
        }

        /// <summary>base_list 里写着仓库外类型名 name（接口定义不在仓库里）的本仓库类型。</summary>
        public IReadOnlyList<CsType> ImplementorsOfExternal(string name)
        {
            if (_externalImplementors is null)
            {
                _externalImplementors = new Dictionary<string, List<CsType>>(StringComparer.Ordinal);
                foreach (var t in _typesByQualified.Values)
                    foreach (var s in Supers(t))
                        if (s.External is { } e)
                        {
                            if (!_externalImplementors.TryGetValue(e, out var list)) _externalImplementors[e] = list = [];
                            list.Add(t);
                        }
            }
            return _externalImplementors.GetValueOrDefault(name) ?? [];
        }

        public CsType? TypeOfPart(long id) => _typeOfPart.GetValueOrDefault(id);

        public CsType? ByQualified(string qualified) => _typesByQualified.GetValueOrDefault(qualified);

        public IReadOnlyList<SymbolRecord> MethodsNamed(string name) => _methodsByName.GetValueOrDefault(name) ?? [];

        /// <summary>不含 '|' 且不以 '?' / '@' 开头的是普通类型写法。</summary>
        public static bool IsChain(string written) => written.Contains('|') || written.StartsWith('?') || written.StartsWith('@');

        /// <summary>按 根|步|步… 一步步推类型：字段 / 属性看声明类型，方法看返回类型（后面跟 await 时拆 Task&lt;T&gt;），找不到方法时试 this 类型对得上的扩展方法。</summary>
        public CsChainHit EvalChain(string chain, long fileId, long contextId)
        {
            static CsChainHit Fail(bool externalRooted) => new(null, null, false, externalRooted);
            // .ConfigureAwait(x) 不改变 await 出来的类型
            var steps = chain.Split('|').Where(st => !st.StartsWith("ConfigureAwait(", StringComparison.Ordinal)).ToArray();
            CsType? cur;
            string? ext;
            var fallback = false;
            var root = steps[0];
            if (root == "@this")
            {
                cur = EnclosingType(contextId);
                ext = null;
                if (cur is null) return Fail(false);
            }
            else if (root.StartsWith('?'))
            {
                var name = root[1..];
                SymbolRecord? member = null;
                for (var t = EnclosingType(contextId); t is not null && member is null; t = OuterType(t)) member = FindMember(t, name);
                if (member is not null)
                {
                    if (CSharpMemberType(member) is not { } memberType || !FromWritten(memberType, member.FileId, member.Id, out cur, out ext, out fallback)) return Fail(false);
                }
                else
                {
                    // 类型名（静态成员）：仓库里找不到、大写开头的当仓库外类型
                    var hit = ResolveType(name, fileId, contextId);
                    if (hit.Type is { } t) { cur = t; ext = null; fallback = hit.Fallback; }
                    else if (hit.Ambiguous is null && name.Length > 0 && char.IsUpper(name[0]) && !LooksLikeTypeParameter(name)) { cur = null; ext = name; }
                    else return Fail(false);
                }
            }
            else if (!FromWritten(root, fileId, contextId, out cur, out ext, out fallback)) return Fail(false);

            for (var i = 1; i < steps.Length; i++)
            {
                var step = steps[i];
                if (step == "await") continue; // 方法返回值那一步已经拆过 Task<T>
                var awaited = i + 1 < steps.Length && steps[i + 1] == "await";
                string? written;
                SymbolRecord context;
                var open = step.IndexOf('(');
                if (open > 0 && step.EndsWith(')'))
                {
                    var name = step[..open];
                    var n = int.TryParse(step[(open + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
                    List<SymbolRecord> methods;
                    if (cur is not null)
                    {
                        var found = FindMethods(cur, name, n);
                        methods = found.Exact.Count > 0 ? found.Exact : ExtensionMethods(name, n, SelfAndSuperNames(cur, null));
                        if (methods.Count == 0 && CSharpIdentityExtensions.Contains(name)) continue;
                        if (methods.Count == 0) return Fail(found.ExternalSuper is not null);
                    }
                    else
                    {
                        methods = ExtensionMethods(name, n, new HashSet<string>(StringComparer.Ordinal) { ext! });
                        if (methods.Count == 0 && CSharpIdentityExtensions.Contains(name)) continue;
                        if (methods.Count == 0) return Fail(true);
                    }
                    var returns = methods.Select(m => CSharpReturnType(m, awaited)).Distinct(StringComparer.Ordinal).ToList();
                    if (returns.Count != 1 || returns[0] is null) return Fail(false);
                    written = returns[0];
                    context = methods[0];
                }
                else
                {
                    if (cur is null) return Fail(true);
                    var member = FindMember(cur, step);
                    if (member is null) return Fail(Supers(cur).Any(s => s.External is not null));
                    written = CSharpMemberType(member);
                    context = member;
                    if (written is null) return Fail(false);
                }
                if (!FromWritten(written!, context.FileId, context.Id, out cur, out ext, out var fb)) return Fail(false);
                fallback |= fb;
            }
            return new CsChainHit(cur, ext, fallback, false);
        }

        /// <summary>类型写法 → 本仓库类型或仓库外类型名；范围外多候选、像泛型参数（T、TEntity）的推不下去。</summary>
        private bool FromWritten(string written, long fileId, long contextId, out CsType? type, out string? external, out bool fallback)
        {
            type = null;
            external = null;
            fallback = false;
            if (CSharpPredefinedTypes.Contains(written)) { external = written; return true; }
            var hit = ResolveType(written, fileId, contextId);
            if (hit.Type is { } t) { type = t; fallback = hit.Fallback; return true; }
            if (hit.Ambiguous is not null || LooksLikeTypeParameter(hit.External)) return false;
            external = hit.External;
            return true;
        }

        private static bool LooksLikeTypeParameter(string name) =>
            name.Length == 1 ? char.IsUpper(name[0]) : name.Length > 1 && name[0] == 'T' && char.IsUpper(name[1]) && !name.Contains('.');

        /// <summary>this 参数类型在 thisTypes 里、按扩展方式调用（实参个数 + 1）合得上的扩展方法。</summary>
        public List<SymbolRecord> ExtensionMethods(string name, int argc, HashSet<string> thisTypes) =>
            MethodsNamed(name).Where(m => Parameters(m) is { Extension: true, ThisType: { } t } p && thisTypes.Contains(t) && p.Accepts(argc < 0 ? -1 : argc + 1)).ToList();

        /// <summary>引用处可见的命名空间：命名空间链 + using。</summary>
        public HashSet<string> VisibleNamespaces(long fileId, long contextId)
        {
            var set = new HashSet<string>(NamespaceChain(NamespaceOf(contextId)), StringComparer.Ordinal);
            foreach (var i in Imports(fileId)) set.Add(i);
            return set;
        }
    }

    /// <summary>
    /// 从 C# 方法的 Signature（声明头，见 CSharpAdapter）数参数："public static int Run(string[] a, int b = 1, params int[] c)" → (1, ∞)。
    /// 读不出参数表（截断、没有括号）时个数不限。
    /// </summary>
    public static CSharpParameters CSharpParameterInfo(string? signature, string name)
    {
        if (string.IsNullOrEmpty(signature)) return CSharpParameters.Unknown;
        var open = -1;
        for (var from = 0; from < signature.Length;)
        {
            var i = signature.IndexOf(name, from, StringComparison.Ordinal);
            if (i < 0) break;
            from = i + 1;
            if (i > 0 && (char.IsLetterOrDigit(signature[i - 1]) || signature[i - 1] == '_')) continue;
            var j = i + name.Length;
            while (j < signature.Length && signature[j] == ' ') j++;
            if (j < signature.Length && signature[j] == '<')
            {
                for (var depth = 0; j < signature.Length; j++)
                {
                    if (signature[j] == '<') depth++;
                    else if (signature[j] == '>' && --depth == 0) { j++; break; }
                }
                while (j < signature.Length && signature[j] == ' ') j++;
            }
            if (j < signature.Length && signature[j] == '(') { open = j; break; }
        }
        if (open < 0) return CSharpParameters.Unknown;

        var parameters = new List<string>();
        var current = new System.Text.StringBuilder();
        var nesting = 0;
        var closed = false;
        for (var k = open + 1; k < signature.Length && !closed; k++)
        {
            var ch = signature[k];
            switch (ch)
            {
                case '"' or '\'':
                {
                    // 默认值里的字符串 / 字符字面量：原样跳过
                    current.Append(ch);
                    for (k++; k < signature.Length && signature[k] != ch; k++)
                    {
                        if (signature[k] == '\\') { current.Append(signature[k]); k++; if (k >= signature.Length) break; }
                        current.Append(signature[k]);
                    }
                    if (k < signature.Length) current.Append(ch);
                    continue;
                }
                case '(' or '<' or '[' or '{':
                    nesting++;
                    break;
                case ')' when nesting == 0:
                    closed = true;
                    continue;
                case ')' or '>' or ']' or '}':
                    nesting--;
                    break;
                case ',' when nesting == 0:
                    parameters.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
            }
            current.Append(ch);
        }
        if (!closed) return CSharpParameters.Unknown;
        var last = current.ToString().Trim();
        if (last.Length > 0 || parameters.Count > 0) parameters.Add(last);

        int min = 0, max = 0;
        var extension = false;
        string? thisType = null;
        var variadic = false;
        for (var p = 0; p < parameters.Count; p++)
        {
            var text = parameters[p];
            while (text.StartsWith('['))
            {
                // 参数上的特性 [FromBody] x
                var end = text.IndexOf(']');
                if (end < 0) break;
                text = text[(end + 1)..].TrimStart();
            }
            if (p == 0 && text.StartsWith("this ", StringComparison.Ordinal))
            {
                extension = true;
                var rest = text[5..].TrimStart();
                var cut = rest.IndexOfAny([' ', '<', '?', '[']);
                thisType = cut < 0 ? rest : rest[..cut];
            }
            if (text.StartsWith("params ", StringComparison.Ordinal)) { variadic = true; continue; }
            max++;
            if (!HasTopLevelDefault(text)) min++;
        }
        return new CSharpParameters(min, variadic ? int.MaxValue : max, extension, thisType);
    }

    private static bool HasTopLevelDefault(string parameter)
    {
        var nesting = 0;
        for (var i = 0; i < parameter.Length; i++)
        {
            var ch = parameter[i];
            if (ch is '(' or '<' or '[' or '{') nesting++;
            else if (ch is ')' or '>' or ']' or '}') nesting--;
            else if (ch == '=' && nesting == 0 && (i + 1 >= parameter.Length || parameter[i + 1] != '>')) return true;
        }
        return false;
    }

    /// <summary>字段 / 属性 Signature 里的声明类型写法："private readonly IRepo&lt;T&gt;? _repo" → IRepo；数组、元组返回 null。</summary>
    internal static string? CSharpMemberType(SymbolRecord member)
    {
        var sig = member.Signature;
        if (string.IsNullOrWhiteSpace(sig)) return null;
        var text = sig.Trim();
        // 属性："public IFoo Foo { get; }" / "public IFoo Foo => _foo;"——截到名字之前
        var nameAt = -1;
        for (var from = 0; from < text.Length;)
        {
            var i = text.IndexOf(member.Name, from, StringComparison.Ordinal);
            if (i < 0) break;
            from = i + 1;
            var before = i == 0 ? ' ' : text[i - 1];
            var afterIndex = i + member.Name.Length;
            var after = afterIndex < text.Length ? text[afterIndex] : ' ';
            if (before == ' ' && !(char.IsLetterOrDigit(after) || after == '_')) { nameAt = i; break; }
        }
        if (nameAt <= 0) return null;
        text = text[..nameAt].Trim();
        string[] modifiers = ["public ", "protected ", "private ", "internal ", "static ", "readonly ", "virtual ", "override ", "abstract ", "sealed ", "new ", "required ", "volatile ", "const ", "extern ", "unsafe ", "partial ", "event "];
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in modifiers)
                if (text.StartsWith(m, StringComparison.Ordinal)) { text = text[m.Length..].TrimStart(); changed = true; }
        }
        // 显式接口实现 IFoo.Bar 之类的不管；去泛型实参与可空
        var lt = text.IndexOf('<');
        if (lt >= 0) text = text[..lt];
        text = text.TrimEnd('?').Trim();
        if (text.Length == 0 || text.EndsWith(']') || text.StartsWith('(') || text.Contains(' ')) return null;
        return text;
    }

    /// <summary>
    /// 方法 Signature 里的返回类型写法（去泛型实参与可空）："public async Task&lt;IFoo&gt; GetAsync(" → Task；awaited 时拆 Task&lt;T&gt; / ValueTask&lt;T&gt; → IFoo。
    /// void、元组、数组、await 一个不带结果的 Task 为 null。
    /// </summary>
    internal static string? CSharpReturnType(SymbolRecord method, bool awaited)
    {
        var sig = method.Signature;
        if (string.IsNullOrWhiteSpace(sig)) return null;
        var nameAt = -1;
        for (var from = 0; from < sig.Length;)
        {
            var i = sig.IndexOf(method.Name, from, StringComparison.Ordinal);
            if (i < 0) break;
            from = i + 1;
            if (i == 0 || char.IsLetterOrDigit(sig[i - 1]) || sig[i - 1] == '_') continue;
            var j = i + method.Name.Length;
            while (j < sig.Length && sig[j] == ' ') j++;
            if (j < sig.Length && sig[j] is '(' or '<') { nameAt = i; break; }
        }
        if (nameAt <= 0) return null;
        // 名字前面按顶层空格切词，最后一个词是返回类型（显式接口实现 IFoo.Bar 的 "IFoo." 跳过）
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var ch in sig[..nameAt])
        {
            if (ch is '<' or '(' or '[') depth++;
            else if (ch is '>' or ')' or ']') depth--;
            if (ch == ' ' && depth == 0) { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(ch);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        if (tokens.Count > 0 && tokens[^1].EndsWith('.')) tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0) return null;
        var raw = tokens[^1].Trim();
        if (awaited)
        {
            foreach (var wrapper in (string[])["Task<", "ValueTask<", "System.Threading.Tasks.Task<"])
                if (raw.StartsWith(wrapper, StringComparison.Ordinal) && raw.EndsWith('>')) { raw = raw[wrapper.Length..^1].Trim(); goto unwrapped; }
            return null;
        }
        unwrapped:
        var lt = raw.IndexOf('<');
        if (lt >= 0) raw = raw[..lt];
        raw = raw.TrimEnd('?').Trim();
        if (raw.Length == 0 || raw == "void" || raw.EndsWith(']') || raw.StartsWith('(')) return null;
        return raw;
    }

    private static List<NewEdge> ResolveCSharpCall(PendingReference r, CSharpIndex cs, IndexWriter writer)
    {
        List<NewEdge> External(string name) =>
            [new NewEdge(r.FromSymbolId, writer.GetOrCreateExternal(name), "external:" + name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line, Via: r.Via)];
        var b = r.Script!;
        var argc = int.TryParse(b.ImportedName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

        List<NewEdge> WithImplementations(List<SymbolRecord> targets, double confidence)
        {
            var edges = targets.Take(MaxCandidates).Select(t => Edge(r, t, confidence)).ToList();
            foreach (var target in targets.Take(MaxCandidates))
            {
                if (target.ParentId is not { } p || cs.TypeOfPart(p) is not { IsInterface: true } iface) continue;
                var implementors = cs.Implementors(iface);
                if (implementors.Count is 0 or > ImplementationLinker.MaxImplementations) continue;
                var derived = implementors.Count == 1 ? 0.9 : 0.5;
                var parameters = cs.Parameters(target);
                var seen = new HashSet<long>();
                foreach (var impl in implementors.OrderBy(i => i.QualifiedName, StringComparer.Ordinal))
                {
                    // 实现类及其父类（不经接口）里同名、个数合得上的方法
                    var found = cs.FindMethods(impl, target.Name, argc, classesOnly: true);
                    var method = found.Exact.FirstOrDefault(m => cs.Parameters(m).Max == parameters.Max) ?? found.Exact.FirstOrDefault();
                    if (method is null || !seen.Add(method.Id)) continue;
                    edges.Add(new NewEdge(r.FromSymbolId, method.Id, method.QualifiedName, EdgeKind.Calls, IndexSource.TreeSitter, derived,
                        r.FileId, r.Line, Via: ImplementationLinker.Via, EvidenceLine: r.Line));
                }
            }
            return edges;
        }

        List<NewEdge> Pick(List<SymbolRecord> found, double unique)
        {
            var near = cs.NearestSymbols(found, r.FileId);
            return near.Count == 1 ? WithImplementations(near, unique) : WithImplementations(near.OrderBy(m => m.QualifiedName, StringComparer.Ordinal).ToList(), CSharpAmbiguousConfidence);
        }

        // 仓库里的扩展方法：按扩展方式调用（实参个数 + 1）；this 参数类型对得上的优先
        List<NewEdge>? Extensions(CsType? receiverType, string? written)
        {
            var all = cs.MethodsNamed(r.Name).Where(m => cs.Parameters(m) is { Extension: true } p && p.Accepts(argc < 0 ? -1 : argc + 1)).ToList();
            if (all.Count == 0) return null;
            var names = cs.SelfAndSuperNames(receiverType, written);
            var typed = all.Where(m => cs.Parameters(m).ThisType is { } t && names.Contains(t)).ToList();
            if (typed.Count > 0) return Pick(typed, CSharpTypedConfidence);
            var visible = cs.VisibleNamespaces(r.FileId, r.FromSymbolId);
            var inScope = all.Where(m => m.FileId == r.FileId || visible.Contains(cs.NamespaceOf(m.Id))).ToList();
            // this 参数是泛型 T / object 的扩展方法（MapTo<T>(this object)）
            var generic = inScope.Where(m => cs.Parameters(m).ThisType is { } t && (t == "object" || (t.Length <= 2 && char.IsUpper(t[0])) || (t.Length > 1 && t[0] == 'T' && char.IsUpper(t[1])))).ToList();
            if (generic.Count > 0) return Pick(generic, CSharpNameConfidence);
            return null;
        }

        // 在类型 type 上调 r.Name
        List<NewEdge> OnType(CsType type, bool skipSelf, double unique)
        {
            var found = cs.FindMethods(type, r.Name, argc, skipSelf);
            if (found.Exact.Count > 0) return Pick(found.Exact, unique);
            if (found.NameOnly.Count > 0) return Pick(found.NameOnly, Math.Min(unique, CSharpNameConfidence));
            if (Extensions(type, null) is { } ext) return ext;
            if (found.ExternalSuper is { } super) return External(super + "." + r.Name);
            return External(r.Name);
        }

        List<NewEdge> OnWrittenType(string written, long fileId, long contextId)
        {
            if (CSharpIndex.IsChain(written))
            {
                var chain = cs.EvalChain(written, fileId, contextId);
                if (chain.Type is { } chained) return OnType(chained, skipSelf: false, chain.Fallback ? CSharpNameConfidence : CSharpTypedConfidence);
                if (chain.External is { } ext) return OnExternalType(ext);
                // 走到仓库外类型之后断了：之后的调用只可能是仓库里的扩展方法
                return chain.ExternalRooted ? ExtensionsInScope() ?? External(r.Name) : Unknown();
            }
            if (CSharpPredefinedTypes.Contains(written)) return Extensions(null, written) ?? External(written + "." + r.Name);
            var hit = cs.ResolveType(written, fileId, contextId);
            if (hit.Type is { } type) return OnType(type, skipSelf: false, hit.Fallback ? CSharpNameConfidence : CSharpTypedConfidence);
            if (hit.Ambiguous is not null) return Unknown();
            return OnExternalType(hit.External);
        }

        List<NewEdge> OnExternalType(string typeName) =>
            CSharpPredefinedTypes.Contains(typeName)
                ? Extensions(null, typeName) ?? External(typeName + "." + r.Name)
                : Extensions(null, typeName) ?? ExternalTypeImplementations(typeName) ?? External(typeName + "." + r.Name);

        List<NewEdge>? ExtensionsInScope()
        {
            var visible = cs.VisibleNamespaces(r.FileId, r.FromSymbolId);
            var pool = cs.MethodsNamed(r.Name).Where(m => cs.Parameters(m) is { Extension: true } p && p.Accepts(argc < 0 ? -1 : argc + 1)
                && (m.FileId == r.FileId || visible.Contains(cs.NamespaceOf(m.Id)))).ToList();
            return pool.Count == 0 ? null : Pick(pool, CSharpNameConfidence);
        }

        // 接收者类型不在仓库里（接口定义在别的仓库 / 包里），但仓库里有类在 base_list 写了它：连到这些类上的同名方法，名字匹配的低档
        List<NewEdge>? ExternalTypeImplementations(string typeName)
        {
            var methods = cs.ImplementorsOfExternal(typeName)
                .SelectMany(t => cs.FindMethods(t, r.Name, argc, classesOnly: true).Exact.Take(1)).DistinctBy(m => m.Id).ToList();
            if (methods.Count == 0) return null;
            return methods.Count == 1 ? [Edge(r, methods[0], 0.5)]
                : methods.OrderBy(m => m.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).Select(m => Edge(r, m, CSharpAmbiguousConfidence)).ToList();
        }

        List<NewEdge> Unknown()
        {
            if (CommonFrameworkNames.Contains(r.Name)) return External(r.Name);
            var pool = cs.MethodsNamed(r.Name).Where(m => cs.Parameters(m) is var p && (p.Accepts(argc) || (p.Extension && p.Accepts(argc < 0 ? -1 : argc + 1)))).ToList();
            if (pool.Count == 0) return External(r.Name);
            var visible = cs.VisibleNamespaces(r.FileId, r.FromSymbolId);
            var inScope = pool.Where(m => m.FileId == r.FileId || visible.Contains(cs.NamespaceOf(m.Id))).ToList();
            if (inScope.Count > 0) return Pick(inScope, CSharpNameConfidence);
            if (pool.Count == 1) return WithImplementations(pool, 0.5);
            return WithImplementations(pool.OrderBy(m => m.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).ToList(), CSharpAmbiguousConfidence);
        }

        switch (b.Receiver)
        {
            case ScriptReceiver.None:
            {
                // new T(…)：连到类型
                var hit = cs.ResolveType(b.ImportSource ?? r.Name, r.FileId, r.FromSymbolId);
                if (hit.Type is { } created) return [Edge(r, created.Parts[0], hit.Fallback ? CSharpNameConfidence : CSharpTypedConfidence)];
                if (hit.Ambiguous is { } many)
                    return many.OrderBy(t => t.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).Select(t => Edge(r, t.Parts[0], CSharpAmbiguousConfidence)).ToList();
                return External(hit.External);
            }
            case ScriptReceiver.This when b.ImportSource == CSharpDelegateMarker:
                return [];
            case ScriptReceiver.This:
            {
                var enclosing = cs.EnclosingType(r.FromSymbolId);
                if (b.ImportSource == CSharpBaseMarker)
                    return enclosing is null ? External(r.Name) : OnType(enclosing, skipSelf: true, CSharpTypedConfidence);
                // 局部函数（含顶级语句里的）：从调用方往外找
                for (var (cur, depth) = ((long?)r.FromSymbolId, 0); cur is { } id && depth < 16; depth++)
                {
                    var local = cs.Children(id).Where(c => c.Kind == SymbolKind.Function && c.Name == r.Name).ToList();
                    if (local.Count > 0) return [Edge(r, local[0], CSharpTypedConfidence)];
                    var s = cs.Symbol(id);
                    if (s is null || CSharpIndex.IsType(s)) break;
                    cur = s.ParentId;
                }
                if (enclosing is null) return Unknown(); // 顶级语句
                // 本类及父类 → 外层类 → using static；都没有时记成最内层类继承的仓库外类型上的方法
                string? inheritedExternal = null;
                for (var t = enclosing; t is not null; t = cs.OuterType(t))
                {
                    var found = cs.FindMethods(t, r.Name, argc);
                    // 唯一对得上的是调用方自己，而父类里也有同名同个数的（重载转调父类：GenerateUrlAsync(request.ToParam())）：
                    // 只看个数分不出是递归还是调父类，两个都当候选
                    if (found.Exact is [var self] && self.Id == r.FromSymbolId
                        && cs.FindMethods(t, r.Name, argc, skipSelf: true).Exact is { Count: > 0 } inherited)
                        return [.. new[] { self }.Concat(inherited).Take(MaxCandidates).Select(m => Edge(r, m, CSharpAmbiguousConfidence))];
                    if (found.Exact.Count > 0 || found.NameOnly.Count > 0) return OnType(t, skipSelf: false, CSharpTypedConfidence);
                    inheritedExternal ??= found.ExternalSuper;
                }
                foreach (var import in cs.Imports(r.FileId))
                    if (cs.ByQualified(import) is { } staticType && cs.FindMethods(staticType, r.Name, argc) is { } viaStatic
                        && (viaStatic.Exact.Count > 0 || viaStatic.NameOnly.Count > 0))
                        return OnType(staticType, skipSelf: false, CSharpTypedConfidence);
                return External(inheritedExternal is null ? r.Name : inheritedExternal + "." + r.Name);
            }
            case ScriptReceiver.Other when b.ImportSource is { } written:
                return OnWrittenType(written, r.FileId, r.FromSymbolId);
            case ScriptReceiver.Import when b.ImportSource is { } name:
            {
                if (CSharpPredefinedTypes.Contains(name)) return External(name + "." + r.Name);
                // 写在别的文件（父类、partial 的另一部分）里的字段 / 属性
                if (!name.Contains('.') && cs.EnclosingType(r.FromSymbolId) is { } enclosing)
                    for (var t = enclosing; t is not null; t = cs.OuterType(t))
                        if (cs.FindMember(t, name) is { } member)
                            return CSharpMemberType(member) is { } memberType ? OnWrittenType(memberType, member.FileId, member.Id) : Unknown();
                // 类型名：静态调用 Type.m()
                var hit = cs.ResolveType(name, r.FileId, r.FromSymbolId);
                if (hit.Type is { } type) return OnType(type, skipSelf: false, hit.Fallback ? CSharpNameConfidence : CSharpTypedConfidence);
                if (hit.Ambiguous is not null) return Unknown();
                // a.B.C()：a 是父类 / partial 里的成员时按成员链推
                if (name.Contains('.') && cs.EvalChain("?" + name.Replace('.', '|'), r.FileId, r.FromSymbolId) is { } chained && (chained.Type is not null || chained.External is not null))
                    return chained.Type is { } ct ? OnType(ct, skipSelf: false, chained.Fallback ? CSharpNameConfidence : CSharpTypedConfidence) : OnExternalType(chained.External!);
                var last = name.Split('.')[^1];
                // 大写开头：仓库外类型（Console、File、Task、JsonSerializer）；小写多半是没认出来的变量
                return last.Length > 0 && char.IsUpper(last[0]) ? External(last + "." + r.Name) : Unknown();
            }
            default:
                return Unknown();
        }
    }
}
