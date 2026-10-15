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
/// NS.x()（NS 是 import 进来的）→ 那个模块里的 x；其他接收者 → 本文件 0.9、import 范围 0.7，否则 external。
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

        public ScriptIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, SymbolRecord> byId, Dictionary<long, FileRecord> files,
            Dictionary<string, SymbolRecord> modulesByPathNoExt, Dictionary<long, List<(string Target, bool IsPath)>> importsByFile, IReadOnlyList<ScriptPathConfig> configs)
        {
            _modules = new ScriptModuleResolver(modulesByPathNoExt, configs);
            _children = all.Where(s => s.ParentId is not null).ToLookup(s => s.ParentId!.Value);
            _moduleOfFile = all.Where(s => s.Kind == SymbolKind.Module).GroupBy(s => s.FileId).ToDictionary(g => g.Key, g => g.First());
            _files = files;
            _byId = byId;
            _importsByFile = importsByFile;
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
    /// </summary>
    private List<NewEdge>? ResolveScript(PendingReference r, List<SymbolRecord> candidates, ScriptIndex index, IndexWriter writer)
    {
        var edges = ResolveScriptBinding(r, candidates, index, writer);
        if (r.Via != ScriptEventVia) return edges;
        return edges?.Select(e => e with { Confidence = Math.Min(e.Confidence, ScriptEventConfidence) }).ToList() ?? [];
    }

    public const string ScriptEventVia = "event";
    public const double ScriptEventConfidence = 0.9;

    private List<NewEdge>? ResolveScriptBinding(PendingReference r, List<SymbolRecord> candidates, ScriptIndex index, IndexWriter writer)
    {
        var b = r.Script!;
        var isEvent = r.Via == ScriptEventVia;
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
            case ScriptReceiver.Typed:
            {
                // this.svc.x()：svc 的类型由构造参数属性 / 字段的类型标注给出
                var type = index.ResolveType(r, b.ImportSource, b.ImportedName!);
                if (type is null)
                {
                    if (b.ImportSource is not null && index.ResolveModule(r.FilePath, b.ImportSource) is null)
                        return External(PackageExternalName(b.ImportSource, b.ImportedName) + "." + r.Name); // 类型来自 npm 包
                    goto default;
                }
                var member = index.DirectMember(type, r.Name, Accept);
                if (member is null) goto default;
                var edges = One(member, 0.9);
                // 接口 / 抽象类且仓库里只有一个实现类：补一条到实现的派生边（与 C# / Java 的 impl 一致）
                if (index.SingleImplementation(type) is { } impl && index.DirectMember(impl, r.Name, Accept) is { } implMember)
                    edges.Add(Edge(r, implMember, 0.9) with { Via = ImplementationLinker.Via, EvidenceLine = r.Line });
                return edges;
            }
            default:
            {
                if (r.Kind == EdgeKind.Calls && ScriptBuiltinMethodNames.Contains(r.Name)) return External(r.Name);
                // x.y()：y 只能是对象 / 类的成员（方法）。组件里的局部函数 const has = …、顶层函数都不是谁的属性，不当候选
                if (r.Kind == EdgeKind.Calls) candidates = candidates.Where(c => c.Kind == SymbolKind.Method).ToList();
                var sameFile = candidates.Where(c => c.FileId == r.FileId).ToList();
                if (sameFile.Count == 1) return One(sameFile[0], 0.9);
                if (sameFile.Count > 1) return Many(sameFile);
                var scope = index.ImportScope(r.FileId, r.FilePath);
                var inScope = candidates.Where(c => scope.Contains(c.FileId)).ToList();
                if (inScope.Count == 1) return One(inScope[0], 0.7);
                if (inScope.Count > 1) return Many(inScope);
                return External(r.Name);
            }
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
