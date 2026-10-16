using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// TS/JS 按 import 绑定解析（引用带 <see cref="ScriptBinding"/> 时走这里；CommonJS 的 require 也记成绑定）：
/// 名字来自 import → 解析模块（相对路径 / tsconfig 别名 / index）后在它的导出里找，0.95（经 re-export 转一手 0.9）；
/// 导入自 npm 包 → external "包.名字"；
/// 没有绑定的裸名字 → 只在本文件按词法作用域找（0.9），找不到就是全局 / 局部变量 → external，不再按全仓库同名去猜；
/// this.x() → 本类成员，没有再找仓库里的父类，0.9；this.svc.x()（svc 是构造注入 / 带类型的字段）→ 类型的成员 0.9，
/// 类型是接口 / 抽象类且只有一个实现类时再加 via = "impl" 0.9 到实现；
/// NS.x()（NS 是 import 进来的）→ 那个模块里的 x；
/// 适配器推得出接收者形态时（见 ScriptAdapterBase.Inference.cs）：带类型的变量 / 参数 / new 出来的实例 → 类型的成员 0.9；
/// 函数的返回值（const api = useApi()）→ 顺着 via = returns 的提示找到返回的类，再取成员 0.9；内建 / 宿主对象（Set、Map、Promise、vscode 对象）→ external；
/// 推不出的接收者 → 只按方法名：本文件 0.6、import 范围 0.5，否则 external。
/// </summary>
public sealed partial class HeuristicEdgeResolver
{
    public const double ScriptImportConfidence = 0.95;
    public const double ScriptReexportConfidence = 0.9;

    /// <summary>
    /// 接收者类型未知时（arr.map()、promise.then()、console.log()）不在 import 范围里按名字找的方法名：几乎总是内置对象的方法。
    /// find / get / add / has 这类仓储、服务里也常见的名字不在内：new Svc().find() 仍按 import 范围找。
    /// </summary>
    public static readonly HashSet<string> ScriptBuiltinMethodNames = new(StringComparer.Ordinal)
    {
        "map", "filter", "reduce", "forEach", "findIndex", "some", "every", "push", "pop", "shift", "unshift", "slice", "splice", "concat",
        "join", "indexOf", "includes", "keys", "values", "entries", "then", "catch", "finally", "toString", "log", "warn", "error",
        "flat", "flatMap", "replace", "replaceAll", "split", "trim", "startsWith", "endsWith", "toLowerCase", "toUpperCase", "toFixed", "padStart", "padEnd",
        "stringify", "isArray", "preventDefault", "stopPropagation", "addEventListener", "removeEventListener",
    };

    private sealed class ScriptIndex
    {
        private readonly ScriptModuleResolver _modules;
        private readonly ILookup<long, SymbolRecord> _children;
        private readonly Dictionary<long, SymbolRecord> _moduleOfFile;
        private readonly Dictionary<long, FileRecord> _files;
        private readonly Dictionary<long, SymbolRecord> _byId;
        private readonly Dictionary<long, List<(string Target, bool IsPath)>> _importsByFile;
        private readonly Dictionary<long, HashSet<long>> _scopeCache = [];

        private readonly IReadOnlyList<PendingReference> _pending;
        private readonly IndexReader? _reader;
        private ILookup<long, PendingReference>? _returnHints;
        private HashSet<string>? _repoTypeNames;
        private readonly Dictionary<long, ReturnInfo?> _returnCache = [];

        /// <param name="pending">本次要解析的全部引用：函数的返回值提示（via = returns）从这里取；为空时只读库里已有的 returns 边。</param>
        /// <param name="reader">读库里没重新解析的文件的 returns 边（增量索引）。</param>
        public ScriptIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, SymbolRecord> byId, Dictionary<long, FileRecord> files,
            Dictionary<string, SymbolRecord> modulesByPathNoExt, Dictionary<long, List<(string Target, bool IsPath)>> importsByFile, IReadOnlyList<ScriptPathConfig> configs,
            IReadOnlyList<PendingReference>? pending = null, IndexReader? reader = null)
        {
            _modules = new ScriptModuleResolver(modulesByPathNoExt, configs);
            _children = all.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            _moduleOfFile = all.Where(s => s.Kind == SymbolKind.Module).GroupBy(s => s.FileId).ToDictionary(g => g.Key, g => g.First());
            _files = files;
            _byId = byId;
            _importsByFile = importsByFile;
            _pending = pending ?? [];
            _reader = reader;
        }

        public SymbolRecord? Symbol(long id) => _byId.GetValueOrDefault(id);

        /// <summary>仓库里（TS / JS 文件）有没有叫 name 的类 / 接口 / 类型别名 / 枚举；没有就是语言 / DOM / 宿主类型。</summary>
        public bool HasRepoType(string name)
        {
            _repoTypeNames ??= _byId.Values
                .Where(s => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.TypeAlias or SymbolKind.Enum
                            && _files.TryGetValue(s.FileId, out var f) && LanguageFamily(f.Language) == "js")
                .Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            return _repoTypeNames.Contains(name);
        }

        private static bool IsFunctionLike(SymbolRecord s) => s.Kind is SymbolKind.Function or SymbolKind.Method;

        /// <summary>
        /// 引用处可见的函数 name：source 不为 null 时是那个模块的导出（"default" 找默认导出）；
        /// 否则按词法作用域在本文件里找（调用点祖先链上某一层的直接子符号，类成员不算）。
        /// </summary>
        public SymbolRecord? ResolveFunction(PendingReference r, string? source, string name)
        {
            if (source is not null)
                return ResolveModule(r.FilePath, source) is { } module ? FindExport(module, name, IsFunctionLike)?.Symbol : null;
            foreach (var id in ScopeChain(r.FromSymbolId))
            {
                if (_byId[id].Kind is SymbolKind.Class or SymbolKind.Interface) continue;
                if (_children[id].Where(s => s.Name == name && IsFunctionLike(s)).OrderBy(s => s.StartLine).FirstOrDefault() is { } hit) return hit;
            }
            return null;
        }

        /// <summary>
        /// "返回值" 绑定里的路径指向的函数：f（import 的导出名 / 本文件的函数名）或 X.m（类 X 的静态方法 m；X 是 import 的导出名或本文件的类）。
        /// </summary>
        public SymbolRecord? ResolveReturnTarget(PendingReference r, string? source, string path)
        {
            var dot = path.LastIndexOf('.');
            if (dot < 0) return ResolveFunction(r, source, path);
            return ResolveType(r, source, path[..dot]) is { } owner ? DirectMember(owner, path[(dot + 1)..], IsFunctionLike) : null;
        }

        /// <summary>
        /// 函数 fn 的返回值：仓库里的类 / 接口，或外部对象（External 是外部名字）；推不出返回 null。
        /// 先看本次解析的 via = returns 提示（一条函数返回值提示会顺着被调函数接着推，最多 6 层），没有再读库里的 returns 边。
        /// </summary>
        public ReturnInfo? ReturnOf(SymbolRecord fn, int depth = 0)
        {
            if (depth > 6) return null;
            if (_returnCache.TryGetValue(fn.Id, out var cached)) return cached;
            _returnCache[fn.Id] = null; // 递归保护：a 返回 b()、b 返回 a()
            ReturnInfo? result = null;
            _returnHints ??= _pending.Where(p => p.Via == ScriptReturnsVia).ToLookup(p => p.FromSymbolId);
            if (_returnHints.Contains(fn.Id))
            {
                foreach (var hint in _returnHints[fn.Id])
                    if ((result = ResolveReturnHint(hint, depth)) is not null) break;
            }
            else if (_reader is not null)
            {
                foreach (var n in _reader.GetNeighbors(fn.Id, Direction.Outgoing, new HashSet<EdgeKind> { EdgeKind.References }))
                {
                    if (n.Edge.Via != ScriptReturnsVia || n.Other is not { } other) continue;
                    if (other.Kind == SymbolKind.External) { result = new ReturnInfo(null, n.Edge.ToName["external:".Length..]); break; }
                    if (other.Kind is SymbolKind.Class or SymbolKind.Interface) { result = new ReturnInfo(other, null); break; }
                }
            }
            _returnCache[fn.Id] = result;
            return result;
        }

        /// <summary>
        /// 一条返回值提示指向什么：内建 / npm 包 → 外部；类型名 → 仓库里的类 / 接口；函数名（导出名后缀 "()"）→ 那个函数的返回值。
        /// </summary>
        public ReturnInfo? ResolveReturnHint(PendingReference h, int depth)
        {
            if (h.Script is not { } b) return null;
            if (b.Receiver == ScriptReceiver.Other)
            {
                if (b.ImportSource == ScriptBuiltinMarker) return new ReturnInfo(null, b.ImportedName ?? h.Name);
                if (b.ImportSource is { } marked && marked.StartsWith(ScriptPackageMarker, StringComparison.Ordinal))
                {
                    var package = marked[ScriptPackageMarker.Length..];
                    return ResolveModule(h.FilePath, package) is null ? new ReturnInfo(null, package) : null;
                }
                return null;
            }
            var imported = b.ImportedName;
            if (imported is not null && imported.EndsWith(ScriptReturnSuffix, StringComparison.Ordinal))
            {
                var path = imported[..^ScriptReturnSuffix.Length];
                if (ResolveReturnTarget(h, b.ImportSource, path) is { } fn) return ReturnOf(fn, depth + 1);
                return b.ImportSource is { } src && ResolveModule(h.FilePath, src) is null ? new ReturnInfo(null, PackageExternalName(src, path)) : null;
            }
            if (ResolveType(h, b.ImportSource, imported ?? h.Name) is { } type) return new ReturnInfo(type, null);
            return b.ImportSource is { } typeSource && ResolveModule(h.FilePath, typeSource) is null
                ? new ReturnInfo(null, PackageExternalName(typeSource, imported))
                : null;
        }

        public SymbolRecord? ResolveModule(string fromFile, string specifier) => _modules.Resolve(fromFile, specifier);

        /// <summary>
        /// 模块的导出里找 exportName（"default" 找 export default 标记的符号）。没有就沿这个模块自己的 import / export…from
        /// 往下找最多 3 层（index.ts 桶文件），转手一层的文件名等于 exportName 时取它的默认导出（export { default as X } from './X'）。
        /// </summary>
        public (SymbolRecord Symbol, bool Direct)? FindExport(SymbolRecord module, string exportName, Func<SymbolRecord, bool> accept, int depth = 0, HashSet<long>? visited = null)
        {
            visited ??= [];
            if (!visited.Add(module.Id)) return null;
            var top = _children[module.Id].Where(accept).OrderBy(s => s.StartLine).ToList();
            var hit = exportName == "default"
                ? top.FirstOrDefault(s => s.Visibility == "export default")
                : top.FirstOrDefault(s => s.Name == exportName && s.Visibility is "export" or "export default") ?? top.FirstOrDefault(s => s.Name == exportName);
            if (hit is not null) return (hit, depth == 0);
            if (depth >= 3 || exportName == "default" || !_importsByFile.TryGetValue(module.FileId, out var imports)) return null;
            foreach (var (target, _) in imports)
            {
                var next = ResolveModule(module.QualifiedName, target);
                if (next is null || visited.Contains(next.Id)) continue;
                var inner = FindExport(next, exportName, accept, depth + 1, visited);
                if (inner is not null) return (inner.Value.Symbol, false);
                if (StripScriptExtension(FileName(next.QualifiedName)) == exportName
                    && _children[next.Id].Where(accept).FirstOrDefault(s => s.Visibility == "export default") is { } d)
                    return (d, false);
            }
            return null;
        }

        /// <summary>符号 owner 之下（任意深度）名叫 name 的成员。</summary>
        public SymbolRecord? FindMember(SymbolRecord owner, string name, Func<SymbolRecord, bool> accept)
        {
            var queue = new Queue<SymbolRecord>(_children[owner.Id]);
            while (queue.Count > 0)
            {
                var s = queue.Dequeue();
                if (s.Name == name && accept(s)) return s;
                foreach (var c in _children[s.Id]) queue.Enqueue(c);
            }
            return null;
        }

        /// <summary>from 符号的祖先链（含自身），从内到外，最后是模块。</summary>
        public List<long> ScopeChain(long fromId)
        {
            var chain = new List<long>();
            for (long? cur = fromId; cur is { } id && _byId.TryGetValue(id, out var s) && chain.Count < 64; cur = s.ParentId) chain.Add(id);
            return chain;
        }

        public SymbolRecord? EnclosingType(long fromId)
        {
            foreach (var id in ScopeChain(fromId))
                if (_byId[id].Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct) return _byId[id];
            return null;
        }

        /// <summary>本文件 import 的、解析得到的模块所在文件 id。</summary>
        public HashSet<long> ImportScope(long fileId, string filePath)
        {
            if (_scopeCache.TryGetValue(fileId, out var cached)) return cached;
            var set = new HashSet<long>();
            if (_importsByFile.TryGetValue(fileId, out var imports))
                foreach (var (target, _) in imports)
                    if (ResolveModule(filePath, target) is { } m) set.Add(m.FileId);
            _scopeCache[fileId] = set;
            return set;
        }

        public SymbolRecord? ModuleOf(long fileId) => _moduleOfFile.GetValueOrDefault(fileId);
        public FileRecord? File(long fileId) => _files.GetValueOrDefault(fileId);

        /// <summary>owner 的直接成员里名叫 name 的。</summary>
        public SymbolRecord? DirectMember(SymbolRecord owner, string name, Func<SymbolRecord, bool> accept) =>
            _children[owner.Id].Where(s => s.Name == name && accept(s)).OrderBy(s => s.StartLine).FirstOrDefault();

        /// <summary>
        /// 类型引用 → 类 / 接口符号：source 不为 null 时在那个模块的导出里找 name（"default" 找默认导出），否则是引用所在文件的顶层类型。
        /// 模块解析不到（npm 包）返回 null。
        /// </summary>
        public SymbolRecord? ResolveType(PendingReference r, string? source, string name)
        {
            static bool IsType(SymbolRecord s) => s.Kind is SymbolKind.Class or SymbolKind.Interface;
            if (source is not null)
                return ResolveModule(r.FilePath, source) is { } module ? FindExport(module, name, IsType)?.Symbol : null;
            return ModuleOf(r.FileId) is { } own ? _children[own.Id].FirstOrDefault(s => s.Name == name && IsType(s)) : null;
        }

        private List<(SymbolRecord Class, HashSet<string> Implements, string? Extends)>? _heritage;
        private readonly Dictionary<long, SymbolRecord?> _implCache = [];

        /// <summary>
        /// 接口（implements 它）或抽象类（extends 它）在仓库里唯一的实现类；没有、多于一个、type 是普通类都返回 null。
        /// 继承关系从类签名（定义的第一行，跳过装饰器）里读；实现类得和 type 同文件或 import 了 type 所在文件，免得同名接口串线。
        /// </summary>
        public SymbolRecord? SingleImplementation(SymbolRecord type)
        {
            var isInterface = type.Kind == SymbolKind.Interface;
            if (!isInterface && !(type.Kind == SymbolKind.Class && type.Signature?.Contains("abstract class", StringComparison.Ordinal) == true)) return null;
            if (_implCache.TryGetValue(type.Id, out var cached)) return cached;
            _heritage ??= _byId.Values
                .Where(s => s.Kind == SymbolKind.Class && s.Signature is not null && _files.TryGetValue(s.FileId, out var f) && LanguageFamily(f.Language) == "js")
                .Select(s => (s, ParseImplements(s.Signature!), ParseExtends(s.Signature!)))
                .ToList();
            var found = _heritage
                .Where(h => h.Class.Id != type.Id && (isInterface ? h.Implements.Contains(type.Name) : h.Extends == type.Name))
                .Where(h => h.Class.FileId == type.FileId || (File(h.Class.FileId) is { } file && ImportScope(file.Id, file.Path).Contains(type.FileId)))
                .Select(h => h.Class).Take(2).ToList();
            var result = found.Count == 1 ? found[0] : null;
            _implCache[type.Id] = result;
            return result;
        }
    }

    /// <summary>函数返回值：仓库里的类 / 接口（Type），或外部对象（External 是外部名字，如 "Set"、"@tanstack/react-query.useQuery"）。</summary>
    internal sealed record ReturnInfo(SymbolRecord? Type, string? External);

    private static readonly Regex ScriptExtendsPattern = new(@"\bextends\s+([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)", RegexOptions.Compiled);
    private static readonly Regex ScriptImplementsPattern = new(@"\bimplements\s+([^{]+)", RegexOptions.Compiled);

    /// <summary>类签名里 extends 的父类名（React.Component 原样保留）。</summary>
    internal static string? ParseExtends(string signature) =>
        ScriptExtendsPattern.Match(signature) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>类签名里 implements 的接口名：去掉类型实参（A&lt;B, C&gt; 里的逗号不算分隔）。</summary>
    internal static HashSet<string> ParseImplements(string signature)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (ScriptImplementsPattern.Match(signature) is not { Success: true } m) return result;
        var text = m.Groups[1].Value;
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '<') depth++;
            else if (ch == '>') depth = Math.Max(0, depth - 1);
            else if (depth == 0)
            {
                if (ch == ',') { Add(current.ToString()); current.Clear(); }
                else current.Append(ch);
            }
        }
        Add(current.ToString());
        return result;

        void Add(string name)
        {
            name = name.Trim();
            if (name.Length > 0) result.Add(name);
        }
    }

    private static string FileName(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>npm 包里的名字：具名导入 "包.名字"，默认 / 命名空间导入就是包本身。</summary>
    internal static string PackageExternalName(string source, string? importedName) =>
        importedName is null or "default" or "*" ? source : source + "." + importedName;

    /// <summary>
    /// 带绑定信息的脚本引用。返回 null 表示交给通用启发式（只有没有绑定、本文件也没有的类型名会这样：.d.ts 里的全局类型常见）。
    /// via = "event"（作为 onXxx 传出去的处理函数）：只保留解析到仓库符号的边，置信度封顶 0.9；解析不到（多半是 prop）不建边。
    /// via = "callback"（作为实参 / 实参对象的属性值传出去的函数）：只保留连到仓库函数 / 方法的边，置信度封顶 0.85。
    /// via = "returns"（函数的返回值提示）：存一条 函数 → 返回的类（或外部对象）的 references 边，推不出不存。
    /// </summary>
    private List<NewEdge>? ResolveScript(PendingReference r, List<SymbolRecord> candidates, ScriptIndex index, IndexWriter writer)
    {
        if (r.Via == ScriptReturnsVia) return ResolveReturnHintEdge(r, index, writer);
        var edges = ResolveScriptBinding(r, candidates, index, writer);
        if (r.Via == ScriptCallbackVia)
            return edges?.Where(e => e.ToId is { } id && index.Symbol(id) is { Kind: SymbolKind.Function or SymbolKind.Method })
                .Select(e => e with { Confidence = Math.Min(e.Confidence, ScriptCallbackConfidence) }).ToList() ?? [];
        if (r.Via != ScriptEventVia) return edges;
        return edges?.Select(e => e with { Confidence = Math.Min(e.Confidence, ScriptEventConfidence) }).ToList() ?? [];
    }

    public const string ScriptEventVia = "event";
    public const double ScriptEventConfidence = 0.9;
    public const string ScriptCallbackVia = "callback";
    public const double ScriptCallbackConfidence = 0.85;
    public const string ScriptReturnsVia = "returns";
    /// <summary>
    /// returns 边是解析用的辅助边（增量索引时没重新解析的函数的返回值从库里读它），置信度压在各视图的 0.4 门槛以下：
    /// 它表达的依赖本来就有（返回类型标注有 references 边、new X() / f() 有 calls 边），不该让 DSM / 模块关系多算一遍。
    /// </summary>
    public const double ScriptReturnsConfidence = 0.3;

    /// <summary>接收者是内建 / 宿主对象（ScriptBinding.ImportSource 取这个值，ImportedName 是类型名）：方法调用归外部，不按名字连仓库方法。</summary>
    public const string ScriptBuiltinMarker = "#builtin";
    /// <summary>接收者是 import 进来的模块的 API 返回值（ImportSource = 这个前缀 + 模块说明符）：模块在仓库外归外部，在仓库里推不出。</summary>
    public const string ScriptPackageMarker = "#pkg:";
    /// <summary>Receiver = Typed 且 ImportedName 以它结尾：接收者是那个函数（import 的导出名 / 本文件的函数名）的返回值。</summary>
    public const string ScriptReturnSuffix = "()";

    /// <summary>接收者类型推不出、只能按方法名猜时的置信度：同文件唯一 0.6、import 范围唯一 0.5（以前是 0.9 / 0.7，Set.has 连到仓库的 has 就是这么来的）。</summary>
    public const double ScriptUnknownReceiverSameFile = 0.6;
    public const double ScriptUnknownReceiverImportScope = 0.5;

    private static List<NewEdge> ResolveReturnHintEdge(PendingReference r, ScriptIndex index, IndexWriter writer)
    {
        switch (index.ResolveReturnHint(r, 0))
        {
            case { Type: { } type }:
                return [Edge(r, type, ScriptReturnsConfidence)];
            case { External: { } name }:
                var ext = writer.GetOrCreateExternal(name);
                return [new NewEdge(r.FromSymbolId, ext, "external:" + name, r.Kind, IndexSource.TreeSitter, ScriptReturnsConfidence, r.FileId, r.Line, Via: r.Via)];
            default:
                return [];
        }
    }

    private List<NewEdge>? ResolveScriptBinding(PendingReference r, List<SymbolRecord> candidates, ScriptIndex index, IndexWriter writer)
    {
        var b = r.Script!;
        var isEvent = r.Via is ScriptEventVia or ScriptCallbackVia;
        bool Accept(SymbolRecord s) => KindCompatible(r.Kind, s.Kind);
        List<NewEdge> External(string name)
        {
            if (isEvent) return []; // 事件处理函数解析不到：不建 external 边
            var ext = writer.GetOrCreateExternal(name);
            return [new NewEdge(r.FromSymbolId, ext, "external:" + name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line, Via: r.Via)];
        }
        List<NewEdge> One(SymbolRecord target, double confidence) => [Edge(r, target, confidence)];
        List<NewEdge> Many(IEnumerable<SymbolRecord> targets) =>
            targets.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).Select(c => Edge(r, c, 0.3)).ToList();

        switch (b.Receiver)
        {
            case ScriptReceiver.None when b.ImportSource is not null:
            {
                var module = index.ResolveModule(r.FilePath, b.ImportSource);
                if (module is null) return External(PackageExternalName(b.ImportSource, b.ImportedName));
                if (b.ImportedName == "*") return External(r.Name);
                var hit = index.FindExport(module, b.ImportedName ?? r.Name, Accept);
                // 默认导出没有标记（export default 一个表达式）时，退回模块里与本地名同名的符号
                if (hit is null && b.ImportedName == "default") hit = index.FindExport(module, r.Name, Accept) is { } h ? (h.Symbol, false) : null;
                return hit is { } x ? One(x.Symbol, x.Direct ? ScriptImportConfidence : ScriptReexportConfidence) : External(r.Name);
            }
            case ScriptReceiver.None:
            {
                // 词法作用域：本文件里、父级是调用点祖先链上某一层的候选；取最近的一层。类成员不是裸名字能引用到的
                var chain = index.ScopeChain(r.FromSymbolId);
                var visible = candidates
                    .Where(c => c.FileId == r.FileId && c.ParentId is { } p && chain.Contains(p) && !IsTypeMember(c, index))
                    .GroupBy(c => chain.IndexOf(c.ParentId!.Value)).OrderBy(g => g.Key).FirstOrDefault()?.ToList();
                if (visible is { Count: 1 }) return One(visible[0], 0.9);
                if (visible is { Count: > 1 }) return Many(visible);
                if (r.Kind == EdgeKind.Calls) return External(r.Name); // 没 import、本文件也没定义：全局函数或局部变量，不猜
                return null;
            }
            case ScriptReceiver.Import:
            {
                var module = index.ResolveModule(r.FilePath, b.ImportSource!);
                if (module is null) return External(b.ImportSource + "." + r.Name);
                SymbolRecord? hit = null;
                if (b.ImportedName is not null and not "*" && index.FindExport(module, b.ImportedName, _ => true) is { } owner)
                    hit = index.FindMember(owner.Symbol, r.Name, Accept);
                hit ??= index.FindExport(module, r.Name, Accept)?.Symbol;
                return hit is not null ? One(hit, ScriptReexportConfidence) : External(r.Name);
            }
            case ScriptReceiver.This:
            {
                var type = index.EnclosingType(r.FromSymbolId);
                var members = type is null ? [] : candidates.Where(c => c.ParentId == type.Id).ToList();
                if (members.Count == 1) return One(members[0], 0.9);
                if (members.Count > 1) return Many(members);
                // 父类在仓库里（适配器给了父类的 import / 本文件名字）：父类的直接成员
                if (b.ImportedName is not null && index.ResolveType(r, b.ImportSource, b.ImportedName) is { } baseType)
                {
                    var inherited = candidates.Where(c => c.ParentId == baseType.Id).ToList();
                    if (inherited.Count == 1) return One(inherited[0], 0.9);
                    if (inherited.Count > 1) return Many(inherited);
                }
                if (isEvent) return [];
                goto default; // 更远的祖先 / 框架基类的成员：按未知接收者处理
            }
            case ScriptReceiver.Typed when b.ImportedName is { } returned && returned.EndsWith(ScriptReturnSuffix, StringComparison.Ordinal):
            {
                // const api = useApi(); api.post()：接收者是 useApi 的返回值，看 useApi 返回什么（见 ScriptIndex.ReturnOf）
                var fn = index.ResolveReturnTarget(r, b.ImportSource, returned[..^ScriptReturnSuffix.Length]);
                if (fn is null)
                {
                    if (b.ImportSource is not null && index.ResolveModule(r.FilePath, b.ImportSource) is null) return External(r.Name); // npm 包函数的返回值
                    goto default;
                }
                var ret = index.ReturnOf(fn);
                if (ret is { External: not null }) return External(r.Name);
                if (ret?.Type is not { } retType || MemberOfType(retType) is not { } viaReturn) goto default;
                return viaReturn;
            }
            case ScriptReceiver.Typed:
            {
                // this.svc.x() / panel.show()：类型由构造参数属性 / 字段 / 变量 / 参数的类型标注或 new X() 给出
                var type = index.ResolveType(r, b.ImportSource, b.ImportedName!);
                if (type is null)
                {
                    if (b.ImportSource is not null && index.ResolveModule(r.FilePath, b.ImportSource) is null)
                        return External(PackageExternalName(b.ImportSource, b.ImportedName) + "." + r.Name); // 类型来自 npm 包
                    // 本文件没有、仓库里也没有这个类型：语言 / DOM / 宿主类型（HTMLElement、Event），方法不是仓库的
                    if (b.ImportSource is null && !index.HasRepoType(b.ImportedName!)) return External(r.Name);
                    goto default;
                }
                if (MemberOfType(type) is not { } typed) goto default;
                return typed;
            }
            case ScriptReceiver.Other when b.ImportSource == ScriptBuiltinMarker:
                if (isEvent) return [];
                return External(r.Name); // new Set().has()、Promise.resolve()、字面量：内建对象的方法
            case ScriptReceiver.Other when b.ImportSource is { } marked && marked.StartsWith(ScriptPackageMarker, StringComparison.Ordinal)
                                           && index.ResolveModule(r.FilePath, marked[ScriptPackageMarker.Length..]) is null:
                return External(r.Name); // vscode.window.createStatusBarItem() 的返回值：npm 包 / 宿主 API 的对象
            default:
            {
                if (r.Kind == EdgeKind.Calls && ScriptBuiltinMethodNames.Contains(r.Name)) return External(r.Name);
                // x.y()：y 只能是对象 / 类的成员（方法）。组件里的局部函数 const has = …、顶层函数都不是谁的属性，不当候选
                if (r.Kind == EdgeKind.Calls) candidates = candidates.Where(c => c.Kind == SymbolKind.Method).ToList();
                // 接收者类型推不出（局部变量、参数、调用结果）：只凭方法名，置信度降一档，免得 has / get / show 这类常见名字连错后长出整棵调用方树
                var unknown = b.Receiver == ScriptReceiver.Other
                              || (b.Receiver == ScriptReceiver.Typed && b.ImportedName?.EndsWith(ScriptReturnSuffix, StringComparison.Ordinal) == true);
                var sameFile = candidates.Where(c => c.FileId == r.FileId).ToList();
                if (sameFile.Count == 1) return One(sameFile[0], unknown ? ScriptUnknownReceiverSameFile : 0.9);
                if (sameFile.Count > 1) return Many(sameFile);
                var scope = index.ImportScope(r.FileId, r.FilePath);
                var inScope = candidates.Where(c => scope.Contains(c.FileId)).ToList();
                if (inScope.Count == 1) return One(inScope[0], unknown ? ScriptUnknownReceiverImportScope : 0.7);
                if (inScope.Count > 1) return Many(inScope);
                return External(r.Name);
            }
        }

        // 类型的直接成员 0.9；接口 / 抽象类且仓库里只有一个实现类：补一条到实现的派生边（与 C# / Java 的 impl 一致）
        List<NewEdge>? MemberOfType(SymbolRecord type)
        {
            var member = index.DirectMember(type, r.Name, Accept);
            if (member is null) return null;
            var edges = One(member, 0.9);
            if (index.SingleImplementation(type) is { } impl && index.DirectMember(impl, r.Name, Accept) is { } implMember)
                edges.Add(Edge(r, implMember, 0.9) with { Via = ImplementationLinker.Via, EvidenceLine = r.Line });
            return edges;
        }
    }

    private static bool IsTypeMember(SymbolRecord s, ScriptIndex index) =>
        s.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field
        && index.ModuleOf(s.FileId) is { } m && s.ParentId != m.Id;

    /// <summary>脚本的 import 边：相对路径与 tsconfig 别名都解析到模块，解析不到（npm 包）是 external。</summary>
    private static NewEdge ResolveScriptImport(PendingReference r, ScriptIndex index, IndexWriter writer)
    {
        if (index.ResolveModule(r.FilePath, r.Name) is { } module)
            return new NewEdge(r.FromSymbolId, module.Id, module.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line);
        var ext = writer.GetOrCreateExternal(r.Name);
        return new NewEdge(r.FromSymbolId, ext, "external:" + r.Name, EdgeKind.Imports, IndexSource.TreeSitter, 0.5, r.FileId, r.Line);
    }
}
