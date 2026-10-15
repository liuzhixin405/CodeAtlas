using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// Python 按 import 绑定解析（适配器给每条引用记了 <see cref="ScriptBinding"/>，语义见 PythonAdapter）：
/// <list type="bullet">
/// <item>名字来自 import（from x import y [as z] / import x.y [as z] / 相对导入）→ 按模块链找到定义，0.95（经别的模块转手一层 0.9）；</item>
/// <item>没有绑定的裸名字 → 本文件按词法作用域找，0.95；找不到（内置函数、全局变量、星号导入）→ external，不按全仓同名猜；</item>
/// <item>self.f() / cls.f() → 本类，再沿 inherits 往上找基类（基类可在别的文件），0.9；super().f() 跳过本类；
/// f 是类属性别名（serializer_class = ArticleSerializer）时连到被别名的类，0.9；</item>
/// <item>C.f() 的 C 是本文件可见的类（EmailService.send_email()）→ 按类成员找，0.9；</item>
/// <item>接收者带类型描述（ImportedName 以 "=" 开头）→ 一步步落到类，按类成员找，0.9；落到第三方包 → external:包.方法；</item>
/// <item>链式调用的接收者是第三方调用的返回值（select(A).where(B)）→ external:包.方法；</item>
/// <item>obj.f() 接收者类型未知 → 本模块与 import 进来的模块里同名方法，唯一 0.7、多个各 0.3；</item>
/// <item>没有绑定的裸名字、本文件有 from x import * → x 的顶层定义（有 __all__ 只认其中的名字），0.9；</item>
/// <item>本仓库模块里没有定义的名字（运行时生成的 protobuf 类 demo_pb2.Empty）→ external:模块名.名字；</item>
/// <item>第三方 / 标准库 → external:顶层包.名字（时序图按包名成列）；类的方法在仓库里找不到、而外部基类都来自同一个包时也记成该包。</item>
/// </list>
/// 类型描述：根是名字（ImportSource 是它绑定的完整点分路径；为 null 时在本文件按词法作用域找，"self" 是所在的类），
/// 后接若干步：".x"（属性 / 方法 / 类属性别名）、"()"（调用：类 → 实例，函数 / 方法 → 返回注解的类型）。
/// 例 "=make_repo().all()"：调用 make_repo，结果上取 all 再调用。返回注解从签名里读（Optional[T] / list[T] / "T" 都取 T），
/// 注解里的名字按定义所在模块解析；类型别名（SessionDep = Annotated[Session, …]）落到它右边的类型。
/// </summary>
public sealed partial class HeuristicEdgeResolver
{
    public const double PythonImportConfidence = 0.95;
    public const double PythonReexportConfidence = 0.9;
    public const double PythonSelfConfidence = 0.9;
    public const double PythonUnknownReceiverConfidence = 0.7;
    private const string PythonSuperMarker = "super";       // 同 PythonAdapter.SuperMarker：Receiver = This 时表示 super().f()
    private const string PythonCallResultMarker = "()";     // 同 PythonAdapter.CallResultMarker：Receiver = Other 时 ImportSource 是调用链根的模块路径（旧格式）
    private const string PythonTypedMarker = "=";           // 同 PythonAdapter.TypedMarker：Receiver = Other 时后面是接收者的类型描述
    private const string PythonStarMarker = "**";           // 同 PythonAdapter.StarMarker：Receiver = None 时 ImportSource 是本文件星号导入的模块

    /// <summary>接收者类型未知时直接当内置方法的名字（str / list / dict / 文件 / logging）。get、add、update、save、filter 这类仓储里也常见的不在内。</summary>
    public static readonly HashSet<string> PythonBuiltinMethodNames = new(StringComparer.Ordinal)
    {
        "append", "extend", "insert", "pop", "remove", "sort", "reverse", "copy", "clear", "count", "index",
        "keys", "values", "items", "setdefault", "popitem",
        "join", "split", "rsplit", "splitlines", "strip", "lstrip", "rstrip", "lower", "upper", "title", "capitalize", "casefold",
        "startswith", "endswith", "replace", "format", "format_map", "encode", "decode", "isdigit", "isalpha", "isalnum", "isspace", "zfill", "ljust", "rjust",
        "read", "readline", "readlines", "write", "writelines", "close", "flush", "seek",
        "debug", "info", "warning", "warn", "error", "exception", "critical",
    };

    private sealed record PyHit(SymbolRecord? Symbol, string? External, double Confidence);

    /// <summary>
    /// 模块链解析结果：Symbol 是模块或模块里的定义；ThirdParty 表示一个前缀都解析不到（绝对导入）；
    /// MissingIn 是"模块解析到了、它的顶层却没有这个名字"时的那个模块（运行时生成的 demo_pb2.Empty）。
    /// </summary>
    private readonly record struct PyDotted(SymbolRecord? Symbol, bool Direct, bool ThirdParty, SymbolRecord? MissingIn = null);

    /// <summary>类型描述求值的中间结果：本仓库的符号（类 / 函数 / 方法 / 模块），或第三方包名。</summary>
    private sealed record PyVal(SymbolRecord? Symbol, string? External);

    private sealed class PythonIndex
    {
        public PythonModuleResolver Modules { get; }
        private readonly ILookup<long, SymbolRecord> _children;
        private readonly Dictionary<long, SymbolRecord> _byId;
        private readonly Dictionary<string, List<SymbolRecord>> _byName;
        private readonly Dictionary<long, List<(string Target, bool IsPath)>> _importsByFile;
        private readonly ILookup<long, PendingReference> _inheritsByClass;
        private readonly ILookup<long, PendingReference> _referencesByFrom;
        private readonly ILookup<long, ScriptBinding> _bindingsByFile;
        private readonly Dictionary<long, List<string>> _starsByFile = [];
        private readonly IndexReader _reader;
        private readonly Dictionary<long, (List<SymbolRecord> Classes, List<string> ExternalPackages)> _bases = [];
        private readonly Dictionary<long, HashSet<long>> _scopes = [];

        public PythonIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, SymbolRecord> byId, Dictionary<long, FileRecord> files,
            Dictionary<long, List<(string Target, bool IsPath)>> importsByFile, IReadOnlyList<PendingReference> references, IndexReader reader)
        {
            var python = all.Where(s => files.TryGetValue(s.FileId, out var f) && f.Language == "python").ToList();
            Modules = new PythonModuleResolver(python.Where(s => s.Kind == SymbolKind.Module));
            _children = python.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            _byName = python.Where(s => s.Kind != SymbolKind.Module).GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            _byId = byId;
            _importsByFile = importsByFile;
            var pending = references.Where(r => r.Language == "python").ToList();
            _inheritsByClass = pending.Where(r => r.Kind == EdgeKind.Inherits).ToLookup(r => r.FromSymbolId);
            _referencesByFrom = pending.Where(r => r.Kind == EdgeKind.References).ToLookup(r => r.FromSymbolId);
            _bindingsByFile = pending.Where(r => r.Script?.ImportSource is not null).ToLookup(r => r.FileId, r => r.Script!);
            foreach (var r in pending)
                if (r.Script is { Receiver: ScriptReceiver.None, ImportedName: PythonStarMarker, ImportSource: { } stars } && !_starsByFile.ContainsKey(r.FileId))
                    _starsByFile[r.FileId] = stars.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            _reader = reader;
        }

        /// <summary>本文件星号导入的模块名（来自本次解析的引用；没有为空）。</summary>
        public IReadOnlyList<string> StarImports(long fileId) => _starsByFile.GetValueOrDefault(fileId) ?? [];

        public IEnumerable<PendingReference> PendingReferencesFrom(long symbolId) => _referencesByFrom[symbolId];

        public bool HasPendingReferences(long symbolId) => _referencesByFrom.Contains(symbolId);

        public IReadOnlyList<Neighbor> StoredReferences(long symbolId) =>
            _reader.GetNeighbors(symbolId, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.References });

        /// <summary>from module import * 会带出 name 吗：有 __all__（记在 Module 的签名里）只认其中的名字，否则不以 _ 开头的都算。</summary>
        public static bool StarExports(SymbolRecord module, string name)
        {
            if (module.Signature is { } sig && sig.StartsWith("__all__ = ", StringComparison.Ordinal))
                return sig["__all__ = ".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(name);
            return !name.StartsWith('_');
        }

        /// <summary>符号所在文件的模块符号。</summary>
        public SymbolRecord? ModuleOf(SymbolRecord s) => ScopeChain(s.Id).Select(Symbol).LastOrDefault(x => x is { Kind: SymbolKind.Module });

        public IReadOnlyList<SymbolRecord> Named(string name) => _byName.TryGetValue(name, out var list) ? list : [];

        public SymbolRecord? Symbol(long id) => _byId.GetValueOrDefault(id);

        /// <summary>from 符号的祖先链（含自身），从内到外，最后是模块。</summary>
        public List<long> ScopeChain(long fromId)
        {
            var chain = new List<long>();
            for (long? cur = fromId; cur is { } id && _byId.TryGetValue(id, out var s) && chain.Count < 64; cur = s.ParentId) chain.Add(id);
            return chain;
        }

        public SymbolRecord? EnclosingClass(long fromId) =>
            ScopeChain(fromId).Select(id => _byId[id]).FirstOrDefault(s => s.Kind == SymbolKind.Class);

        /// <summary>
        /// 点分路径（"app.models.User"、"..core.base"、".items"）：最长的能解析成模块的前缀，剩下的段在模块里 / 类里逐段找。
        /// </summary>
        public PyDotted ResolveDotted(string fromFile, string path)
        {
            var dots = 0;
            while (dots < path.Length && path[dots] == '.') dots++;
            var rest = path[dots..];
            var segments = rest.Length == 0 ? [] : rest.Split('.');
            for (var k = segments.Length; k >= (dots > 0 ? 0 : 1); k--)
            {
                var module = Modules.Resolve(fromFile, path[..dots] + string.Join('.', segments[..k]));
                if (module is null) continue;
                var cur = module;
                var direct = true;
                for (var i = k; i < segments.Length; i++)
                {
                    SymbolRecord? next;
                    if (cur.Kind == SymbolKind.Module)
                    {
                        var hit = FindTopLevel(cur, segments[i], _ => true);
                        next = hit?.Symbol;
                        direct &= hit?.Direct ?? true;
                    }
                    else next = _children[cur.Id].Where(c => c.Name == segments[i]).OrderBy(c => c.StartLine).FirstOrDefault();
                    if (next is null) return new PyDotted(null, false, false, cur.Kind == SymbolKind.Module && i == segments.Length - 1 ? cur : null);
                    cur = next;
                }
                return new PyDotted(cur, direct, false);
            }
            return new PyDotted(null, false, dots == 0);
        }

        /// <summary>模块顶层的定义；没有就沿这个模块自己的 import 往下找最多 3 层（__init__.py 里 from .x import Y 转手）。</summary>
        public (SymbolRecord Symbol, bool Direct)? FindTopLevel(SymbolRecord module, string name, Func<SymbolRecord, bool> accept, int depth = 0, HashSet<long>? visited = null)
        {
            visited ??= [];
            if (!visited.Add(module.Id)) return null;
            var hit = _children[module.Id].Where(c => c.Name == name && accept(c)).OrderBy(c => c.StartLine).FirstOrDefault();
            if (hit is not null) return (hit, depth == 0);
            if (depth >= 3 || !_importsByFile.TryGetValue(module.FileId, out var imports)) return null;
            foreach (var (target, _) in imports)
            {
                var next = Modules.Resolve(module.QualifiedName, target);
                if (next is null || visited.Contains(next.Id)) continue;
                if (FindTopLevel(next, name, accept, depth + 1, visited) is { } inner) return (inner.Symbol, false);
            }
            return null;
        }

        /// <summary>类及其基类（广度优先）里名叫 name 的成员；找不到时带回途经的外部基类所属的包。</summary>
        public (SymbolRecord? Member, List<string> ExternalPackages) FindMember(SymbolRecord type, string name, Func<SymbolRecord, bool> accept, bool skipSelf)
        {
            var externals = new List<string>();
            var visited = new HashSet<long> { type.Id };
            var queue = new Queue<SymbolRecord>();
            void EnqueueBases(SymbolRecord c)
            {
                var (classes, packages) = Bases(c);
                foreach (var p in packages) if (!externals.Contains(p)) externals.Add(p);
                foreach (var b in classes) if (visited.Add(b.Id)) queue.Enqueue(b);
            }
            if (skipSelf) EnqueueBases(type); else queue.Enqueue(type);
            while (queue.Count > 0 && visited.Count < 64)
            {
                var c = queue.Dequeue();
                var hit = _children[c.Id].Where(m => m.Name == name && accept(m)).OrderBy(m => m.StartLine).FirstOrDefault();
                if (hit is not null) return (hit, externals);
                EnqueueBases(c);
            }
            return (null, externals);
        }

        /// <summary>
        /// 类的直接基类：本次待解析的 inherits 引用当场解析（不写库）；不在本次（增量时未变的文件）就读库里已有的 inherits 边。
        /// </summary>
        private (List<SymbolRecord> Classes, List<string> ExternalPackages) Bases(SymbolRecord type)
        {
            if (_bases.TryGetValue(type.Id, out var cached)) return cached;
            var result = (Classes: new List<SymbolRecord>(), ExternalPackages: new List<string>());
            _bases[type.Id] = result; // 先占位，防循环继承 / 互相引用时无限递归
            void AddExternal(string name)
            {
                var dot = name.IndexOf('.');
                if (dot > 0 && !result.ExternalPackages.Contains(name[..dot])) result.ExternalPackages.Add(name[..dot]);
            }
            if (_inheritsByClass.Contains(type.Id))
            {
                foreach (var r in _inheritsByClass[type.Id])
                    foreach (var hit in ResolvePythonReference(r, this))
                    {
                        if (hit.Symbol is { Kind: SymbolKind.Class } c) result.Classes.Add(c);
                        else if (hit.External is { } e) AddExternal(e);
                    }
            }
            else
            {
                foreach (var n in _reader.GetNeighbors(type.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.Inherits }))
                {
                    if (n.Other is { Kind: SymbolKind.Class } c) result.Classes.Add(c);
                    else if (n.Edge.ToName.StartsWith("external:", StringComparison.Ordinal)) AddExternal(n.Edge.ToName["external:".Length..]);
                }
            }
            return result;
        }

        /// <summary>
        /// 类属性别名（serializer_class = ArticleSerializer）指向的类：本次待解析的 references 当场解析；不在本次就读库里的 references 边。
        /// </summary>
        public SymbolRecord? AliasTarget(SymbolRecord field)
        {
            if (_referencesByFrom.Contains(field.Id))
                return _referencesByFrom[field.Id].SelectMany(r => ResolvePythonReference(r, this))
                    .Select(h => h.Symbol).FirstOrDefault(s => s is { Kind: SymbolKind.Class });
            return _reader.GetNeighbors(field.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.References })
                .Select(n => n.Other).FirstOrDefault(s => s is { Kind: SymbolKind.Class });
        }

        /// <summary>本文件与它 import 进来的模块所在的文件（from pkg import sub 的子模块也算）。</summary>
        public HashSet<long> ImportScope(long fileId, string filePath)
        {
            if (_scopes.TryGetValue(fileId, out var cached)) return cached;
            var set = new HashSet<long> { fileId };
            if (_importsByFile.TryGetValue(fileId, out var imports))
                foreach (var (target, _) in imports)
                    if (Modules.Resolve(filePath, target) is { } m) set.Add(m.FileId);
            foreach (var b in _bindingsByFile[fileId].Distinct())
                if (b.ImportedName is { } imported and not "*" && Modules.Resolve(filePath, PythonModuleResolver.Join(b.ImportSource!, imported)) is { } sub)
                    set.Add(sub.FileId);
            _scopes[fileId] = set;
            return set;
        }
    }

    private static IEnumerable<NewEdge> ResolvePython(PendingReference r, PythonIndex index, IndexWriter writer)
    {
        if (r.Kind == EdgeKind.Imports)
        {
            if (index.Modules.Resolve(r.FilePath, r.Name) is { } module)
                return [new NewEdge(r.FromSymbolId, module.Id, module.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line)];
            var package = PythonModuleResolver.IsRelative(r.Name) ? r.Name : PythonModuleResolver.TopLevel(r.Name);
            return [new NewEdge(r.FromSymbolId, writer.GetOrCreateExternal(package), "external:" + package, EdgeKind.Imports, IndexSource.TreeSitter, 0.5, r.FileId, r.Line)];
        }
        return ResolvePythonReference(r, index).Select(h => h.Symbol is { } s
            ? Edge(r, s, h.Confidence)
            : new NewEdge(r.FromSymbolId, writer.GetOrCreateExternal(h.External!), "external:" + h.External, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line, Via: r.Via));
    }

    /// <summary>只算目标、不写库：解析基类时也用它。</summary>
    private static List<PyHit> ResolvePythonReference(PendingReference r, PythonIndex index)
    {
        var b = r.Script ?? new ScriptBinding(ScriptReceiver.None);
        bool Accept(SymbolRecord s) => KindCompatible(r.Kind, s.Kind);
        List<PyHit> External(string name) => [new PyHit(null, name, 0.5)];
        List<PyHit> One(SymbolRecord s, double confidence) => [new PyHit(s, null, confidence)];
        List<PyHit> Many(IEnumerable<SymbolRecord> targets) =>
            targets.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).Select(c => new PyHit(c, null, 0.3)).ToList();
        string InheritedExternal(List<string> packages) => packages.Count == 1 ? packages[0] + "." + r.Name : r.Name;
        // 类（及基类）的成员；调用落在类属性别名上（self.serializer_class()）时连到被别名的类
        List<PyHit> Member(SymbolRecord type, bool skipSelf)
        {
            var (member, packages) = index.FindMember(type, r.Name, Accept, skipSelf);
            if (member is not null) return One(member, PythonSelfConfidence);
            if (r.Kind == EdgeKind.Calls
                && index.FindMember(type, r.Name, s => s.Kind == SymbolKind.Field, skipSelf).Member is { } field
                && index.AliasTarget(field) is { } aliased)
                return One(aliased, PythonSelfConfidence);
            return External(InheritedExternal(packages));
        }
        // 模块里有定义就连；没有（运行时生成的 protobuf 类）记 external:模块名.名字
        List<PyHit> InModule(SymbolRecord module)
        {
            if (index.FindTopLevel(module, r.Name, Accept) is { } hit) return One(hit.Symbol, hit.Direct ? PythonImportConfidence : PythonReexportConfidence);
            return External(PyModuleLabel(module) + "." + r.Name);
        }

        switch (b.Receiver)
        {
            case ScriptReceiver.None when b.ImportedName == PythonStarMarker:
            {
                // 没有绑定的裸名字，本文件有 from x import *：先看本文件，再看星号导入的模块
                var visible = PyLexical(r, index, r.Name, Accept);
                if (visible is { Count: 1 }) return One(visible[0], PythonImportConfidence);
                if (visible is { Count: > 1 }) return Many(visible);
                return PyFromStars(r, index, r.Name, Accept) is { } starred ? One(starred, PythonReexportConfidence) : External(r.Name);
            }
            case ScriptReceiver.None when b.ImportSource is not null:
            {
                if (b.ImportedName is null or "*") return External(r.Name); // 把模块本身当函数调：不认识
                var dotted = index.ResolveDotted(r.FilePath, PythonModuleResolver.Join(b.ImportSource, b.ImportedName));
                if (dotted.ThirdParty) return External(PythonModuleResolver.TopLevel(b.ImportSource) + "." + b.ImportedName);
                if (dotted.Symbol is { } s && s.Kind != SymbolKind.Module && Accept(s))
                    return One(s, dotted.Direct ? PythonImportConfidence : PythonReexportConfidence);
                if (dotted.MissingIn is { } missingIn) return External(PyModuleLabel(missingIn) + "." + b.ImportedName); // from demo_pb2 import Empty
                return External(b.ImportedName);
            }
            case ScriptReceiver.None:
            {
                var visible = PyLexical(r, index, r.Name, Accept);
                if (visible is { Count: 1 }) return One(visible[0], PythonImportConfidence);
                if (visible is { Count: > 1 }) return Many(visible);
                return External(r.Name);
            }
            case ScriptReceiver.This:
            {
                var type = index.EnclosingClass(r.FromSymbolId);
                if (type is null) return External(r.Name);
                return Member(type, b.ImportedName == PythonSuperMarker);
            }
            case ScriptReceiver.Import:
            {
                var path = b.ImportedName is null or "*" ? b.ImportSource! : PythonModuleResolver.Join(b.ImportSource!, b.ImportedName);
                var dotted = index.ResolveDotted(r.FilePath, path);
                if (dotted.ThirdParty) return External(PythonModuleResolver.TopLevel(path) + "." + r.Name);
                switch (dotted.Symbol)
                {
                    case { Kind: SymbolKind.Module } module:
                        return InModule(module);
                    case { Kind: SymbolKind.Class } type:
                        return Member(type, skipSelf: false);
                    default:
                        return External(r.Name);
                }
            }
            default:
            {
                // 接收者的类型描述读得出来：按类成员找；落到第三方包就归那个包。
                // 内置方法名（append / info / items…）在类里找不到时仍当内置方法：list[Item] 按核心类型看成 Item，Item 的基类包不该接走 append
                var builtinName = r.Kind == EdgeKind.Calls && PythonBuiltinMethodNames.Contains(r.Name);
                if (b.ImportedName is { } typed && typed.StartsWith(PythonTypedMarker, StringComparison.Ordinal)
                    && EvalPyDescriptor(r, index, b.ImportSource, typed[PythonTypedMarker.Length..]) is { } value)
                {
                    if (value.External is { } package && !builtinName) return External(package + "." + r.Name);
                    switch (value.Symbol)
                    {
                        case { Kind: SymbolKind.Class } cls when !builtinName || index.FindMember(cls, r.Name, Accept, skipSelf: false).Member is not null:
                            return Member(cls, skipSelf: false);
                        case { Kind: SymbolKind.Module } module:
                            return InModule(module);
                    }
                }
                if (builtinName) return External(r.Name);
                // 接收者是本文件可见的类（EmailService.send_email()、Model.create()）：按类成员找
                if (b.ImportSource is null && b.ImportedName is { } receiver && receiver != PythonCallResultMarker && !receiver.StartsWith(PythonTypedMarker, StringComparison.Ordinal)
                    && PyLexical(r, index, receiver, s => s.Kind == SymbolKind.Class) is [var localClass])
                    return Member(localClass, skipSelf: false);
                // 接收者是第三方调用的返回值（select(A).where(B)，旧格式）：方法归到那个包
                if (b.ImportedName == PythonCallResultMarker && b.ImportSource is { } root && index.ResolveDotted(r.FilePath, root).ThirdParty)
                    return External(PythonModuleResolver.TopLevel(root) + "." + r.Name);
                var scope = index.ImportScope(r.FileId, r.FilePath);
                var pool = index.Named(r.Name)
                    .Where(c => (r.Kind == EdgeKind.Calls ? c.Kind == SymbolKind.Method : Accept(c)) && scope.Contains(c.FileId)).ToList();
                if (pool.Count == 1) return One(pool[0], PythonUnknownReceiverConfidence);
                if (pool.Count > 1) return Many(pool);
                return External(r.Name);
            }
        }
    }

    /// <summary>模块的短名（external 名字里用）：src/emailservice/demo_pb2.py → demo_pb2。</summary>
    private static string PyModuleLabel(SymbolRecord module)
    {
        var name = PythonModuleResolver.ModuleName(module.QualifiedName);
        return name[(name.LastIndexOf('.') + 1)..];
    }

    /// <summary>词法作用域：本文件里、父级在调用点祖先链上的定义，取最近一层。类体里的名字只在类体本身可见，方法里看不到。</summary>
    private static List<SymbolRecord>? PyLexical(PendingReference r, PythonIndex index, string name, Func<SymbolRecord, bool> accept)
    {
        var chain = index.ScopeChain(r.FromSymbolId);
        return index.Named(name)
            .Where(c => c.FileId == r.FileId && accept(c) && c.ParentId is { } p && chain.Contains(p)
                        && (index.Symbol(p)?.Kind != SymbolKind.Class || p == chain[0]))
            .GroupBy(c => chain.IndexOf(c.ParentId!.Value)).OrderBy(g => g.Key).FirstOrDefault()?.ToList();
    }

    /// <summary>本文件星号导入的模块里、会被带出来的顶层定义。</summary>
    private static SymbolRecord? PyFromStars(PendingReference r, PythonIndex index, string name, Func<SymbolRecord, bool> accept)
    {
        foreach (var source in index.StarImports(r.FileId))
            if (index.Modules.Resolve(r.FilePath, source) is { } module && PythonIndex.StarExports(module, name)
                && index.FindTopLevel(module, name, accept) is { } hit)
                return hit.Symbol;
        return null;
    }

    // ---------- 类型描述求值 ----------

    private static readonly Regex PyDescRoot = new(@"^[A-Za-z_]\w*", RegexOptions.Compiled);
    private static readonly Regex PyDescStep = new(@"\G(?:\.([A-Za-z_]\w*)|(\(\)))", RegexOptions.Compiled);

    /// <summary>引用所在位置上的类型描述落到类 / 模块 / 第三方包；读不出来返回 null。</summary>
    private static PyVal? EvalPyDescriptor(PendingReference r, PythonIndex index, string? rootPath, string descriptor)
    {
        var m = PyDescRoot.Match(descriptor);
        if (!m.Success) return null;
        var root = m.Value;
        PyVal? cur;
        if (rootPath is not null)
        {
            var dotted = index.ResolveDotted(r.FilePath, rootPath);
            cur = dotted.ThirdParty ? new PyVal(null, PythonModuleResolver.TopLevel(rootPath))
                : dotted.MissingIn is { } missingIn ? new PyVal(null, PyModuleLabel(missingIn))
                : dotted.Symbol is { } s ? PyFollowAlias(index, s, 0) : null;
        }
        else if (root == "self") cur = index.EnclosingClass(r.FromSymbolId) is { } cls ? new PyVal(cls, null) : null;
        else
        {
            static bool Named(SymbolRecord s) => s.Kind is SymbolKind.Class or SymbolKind.Function or SymbolKind.TypeAlias;
            var hit = PyLexical(r, index, root, Named) is [var single] ? single : PyFromStars(r, index, root, Named);
            cur = hit is null ? null : PyFollowAlias(index, hit, 0);
        }

        for (var pos = m.Length; pos < descriptor.Length && cur is not null;)
        {
            var step = PyDescStep.Match(descriptor, pos);
            if (!step.Success || step.Length == 0) return null;
            pos += step.Length;
            if (cur.External is not null) continue; // 第三方对象上的属性 / 调用：仍归那个包
            cur = step.Groups[2].Success ? PyCall(index, cur.Symbol!) : PyAttribute(index, cur.Symbol!, step.Groups[1].Value);
        }
        return cur;
    }

    /// <summary>调用一个值：类 → 它的实例（仍按类找成员）；函数 / 方法 → 返回注解的类型；类属性别名 → 被别名的类。</summary>
    private static PyVal? PyCall(PythonIndex index, SymbolRecord callee) => callee.Kind switch
    {
        SymbolKind.Class => new PyVal(callee, null),
        SymbolKind.Function or SymbolKind.Method => PyReturnType(index, callee),
        SymbolKind.Field => index.AliasTarget(callee) is { } aliased ? new PyVal(aliased, null) : null,
        _ => null,
    };

    /// <summary>
    /// 值上取属性：类 → 成员（含基类）；模块 → 顶层定义。类里找不到的属性（框架基类塞进来的 self.request、别处赋值的实例属性）不猜：
    /// 不归到基类所在的第三方包——self.request.user.profile 其实是本仓库的模型，归进 rest_framework 会把后面的调用全带偏。
    /// </summary>
    private static PyVal? PyAttribute(PythonIndex index, SymbolRecord owner, string name)
    {
        switch (owner.Kind)
        {
            case SymbolKind.Class:
            {
                var member = index.FindMember(owner, name, _ => true, skipSelf: false).Member;
                if (member is null) return null;
                if (member.Kind == SymbolKind.Field) return index.AliasTarget(member) is { } aliased ? new PyVal(aliased, null) : null;
                return PyFollowAlias(index, member, 0);
            }
            case SymbolKind.Module:
                return index.FindTopLevel(owner, name, _ => true) is { } hit ? PyFollowAlias(index, hit.Symbol, 0) : new PyVal(null, PyModuleLabel(owner));
            default:
                return null;
        }
    }

    /// <summary>类型别名落到它右边的类型（本次的 references 当场解析，不在本次就读库里的边）；其他符号原样。</summary>
    private static PyVal? PyFollowAlias(PythonIndex index, SymbolRecord symbol, int depth)
    {
        if (symbol.Kind != SymbolKind.TypeAlias) return new PyVal(symbol, null);
        if (depth > 3) return null;
        if (index.HasPendingReferences(symbol.Id))
        {
            foreach (var reference in index.PendingReferencesFrom(symbol.Id).OrderBy(x => x.Line))
                foreach (var hit in ResolvePythonReference(reference, index))
                {
                    if (hit.Symbol is { } s) return PyFollowAlias(index, s, depth + 1);
                    if (PyExternalPackage(hit.External) is { } package) return new PyVal(null, package);
                }
            return null;
        }
        foreach (var n in index.StoredReferences(symbol.Id).OrderBy(n => n.Edge.Line))
        {
            // 连到 external 的边，Other 是 External 符号：按名字取包
            if (n.Edge.ToName.StartsWith("external:", StringComparison.Ordinal))
            {
                if (PyExternalPackage(n.Edge.ToName["external:".Length..]) is { } package) return new PyVal(null, package);
                continue;
            }
            if (n.Other is { Kind: not SymbolKind.External } s) return PyFollowAlias(index, s, depth + 1);
        }
        return null;
    }

    /// <summary>"sqlmodel.Session" → "sqlmodel"；没有包名的（"User"）返回 null。</summary>
    private static string? PyExternalPackage(string? external) =>
        external is not null && external.IndexOf('.') is var dot and > 0 ? external[..dot] : null;

    private static readonly Regex PyAnnotationName = new(@"[A-Za-z_][\w.]*", RegexOptions.Compiled);

    private static readonly HashSet<string> PyAnnotationWrappers = new(StringComparer.Ordinal)
    {
        "Optional", "Union", "List", "list", "Set", "set", "FrozenSet", "frozenset", "Sequence", "Iterable", "Iterator", "Generator", "AsyncIterator",
        "AsyncIterable", "AsyncGenerator", "Awaitable", "Coroutine", "Annotated", "Type", "type", "ClassVar", "Final", "Tuple", "tuple", "Dict", "dict",
        "Mapping", "MutableMapping", "Collection", "None", "Any", "str", "int", "float", "bool", "bytes", "object", "Self", "Literal", "Callable",
        "typing", "collections", "abc", "NoReturn", "Never",
    };

    /// <summary>函数 / 方法签名里 -> 后面的注解里的核心类型（Optional[T]、list[T]、"T"、T | None 都取 T），按定义所在模块解析。</summary>
    private static PyVal? PyReturnType(PythonIndex index, SymbolRecord fn)
    {
        if (fn.Signature is not { } sig) return null;
        var arrow = sig.LastIndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var annotation = sig[(arrow + 2)..].Trim().TrimEnd(':').Replace("\"", "").Replace("'", "");
        var name = PyAnnotationName.Matches(annotation).Select(x => x.Value)
            .FirstOrDefault(n => !PyAnnotationWrappers.Contains(n[(n.LastIndexOf('.') + 1)..]));
        if (name is null) return null;
        var last = name[(name.LastIndexOf('.') + 1)..];

        // 注解本身是这个函数的 references：本次的当场解析，不在本次就读库里的边
        if (index.HasPendingReferences(fn.Id))
        {
            foreach (var reference in index.PendingReferencesFrom(fn.Id).Where(x => x.Name == last))
                foreach (var hit in ResolvePythonReference(reference, index))
                {
                    if (hit.Symbol is { } s) return PyFollowAlias(index, s, 0);
                    if (PyExternalPackage(hit.External) is { } package) return new PyVal(null, package);
                }
        }
        else
        {
            foreach (var n in index.StoredReferences(fn.Id))
            {
                var to = n.Edge.ToName;
                if (to.StartsWith("external:", StringComparison.Ordinal))
                {
                    if (to.EndsWith("." + last, StringComparison.Ordinal) && PyExternalPackage(to["external:".Length..]) is { } package) return new PyVal(null, package);
                    continue;
                }
                if (n.Other is { Kind: not SymbolKind.External } s && s.Name == last) return PyFollowAlias(index, s, 0);
            }
        }
        // 兜底：定义所在模块的顶层（含它 import 进来的本仓库定义）
        return index.ModuleOf(fn) is { } module && index.FindTopLevel(module, last, s => s.Kind is SymbolKind.Class or SymbolKind.TypeAlias) is { } top
            ? PyFollowAlias(index, top.Symbol, 0)
            : null;
    }

}
