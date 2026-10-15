using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// Go 的解析（包 = 目录；限定名 &lt;包目录&gt;.&lt;名字&gt;、&lt;包目录&gt;.&lt;接收者类型&gt;.&lt;方法 / 字段&gt;，根目录的包没有前缀）。
/// Go 适配器借用 <see cref="ScriptBinding"/> 传绑定信息，含义：
/// <list type="bullet">
/// <item>Receiver=None：裸名字 F() / 类型 T；ImportSource 是本文件的点导入路径（多个用 '\n' 连接），没有为 null。</item>
/// <item>Receiver=Import：pkg.X，pkg 是本文件的导入名；ImportSource = import 路径，ImportedName = 代码里写的包名（别名或默认名）。
/// ImportSource 为 "?pkg" 表示 pkg 对不上任何导入的默认名：可能是包名与目录名不一致的导入（genproto 目录里 package hipstershop），
/// 按导入目录的 package 子句找；都对不上时 pkg 当作同包别的文件里的包级变量。</item>
/// <item>Receiver=Other：x.M()；ImportedName = x 的类型描述，null 为未知；ImportSource 是描述根所在包的 import 路径
/// （null 为本包，"builtin" 为预声明类型，"?pkg" 同上）。</item>
/// </list>
/// 类型描述：根是 "T"（类型 T 的值）、"F()"（函数 F 的第一个返回值；F 其实是类型时就是 T）、"$V"（包级变量 V）；
/// 后接若干步：".f"（字段 f）、".M()"（方法 M 的第一个返回值，按签名读）、"[]"（元素：切片 / 数组 / map 的值、channel 收到的值）。
/// 例："service.db.Where()" = service 的字段 db 的方法 Where 的返回值。第三方类型上的步一律留在那个包里（db.Where(…).First → external:gorm.First）。
/// 规则：导入名限定的 pkg.F() → 那个包目录里的 F，0.95；同包裸调用 → 本目录所有文件里找，0.95；
/// 接收者类型读得出来 → 那个类型的方法（含内嵌字段提升），0.9；类型未知 → 本包与本文件 import 的包里同名方法，唯一 0.7、多个各 0.3；
/// 解析不到本仓库的包（标准库、第三方）→ external:&lt;包名&gt;.&lt;名字&gt;。不猜接口实现（Go 的接口是结构化实现）。
/// 函数值实参（Via = funcvalue，references）只连本仓库的函数 / 方法（0.95 / 0.9），解析不到不记 external：实参多半是变量、常量。
/// 裸调用 T(x) / pkg.T(x) / F[T](x) 落到的是类型时是类型转换，改记 references 到那个类型。
/// </summary>
public sealed partial class HeuristicEdgeResolver
{
    public const double GoPackageConfidence = 0.95;
    public const double GoReceiverTypeConfidence = 0.9;
    public const string GoBuiltinPackage = "builtin";
    /// <summary>函数 / 方法作为实参传出去（注册到路由、回调）：references 边的 Via。</summary>
    public const string GoFuncValueVia = "funcvalue";

    private sealed class GoIndex
    {
        private readonly GoModuleResolver _modules;
        private readonly Dictionary<long, string> _dirOfFile;
        private readonly Dictionary<long, string> _pathOfFile;
        private readonly Dictionary<string, List<SymbolRecord>> _byQualified;
        private readonly Dictionary<string, List<SymbolRecord>> _modulesByDir;
        private readonly Dictionary<string, List<SymbolRecord>> _methodsByName;
        private readonly Dictionary<long, List<PendingReference>> _embeds;
        private readonly Dictionary<long, List<(string Target, bool IsPath)>> _importsByFile;
        private readonly Dictionary<long, HashSet<string>> _importDirsCache = [];
        private readonly Dictionary<string, string?> _packageNames = new(StringComparer.Ordinal);
        /// <summary>本次解析的文件里写了别名的导入：文件 → 别名 → import 路径（来自引用上的绑定；未变的文件只认默认名与 package 子句）。</summary>
        private readonly Dictionary<long, Dictionary<string, string>> _aliases = [];

        public GoIndex(IReadOnlyList<SymbolRecord> all, Dictionary<long, FileRecord> files, Dictionary<long, List<(string Target, bool IsPath)>> importsByFile,
            IReadOnlyList<GoModFile> modules, IReadOnlyList<PendingReference> references)
        {
            var goFiles = files.Values.Where(f => LanguageFamily(f.Language) == "go").ToList();
            _dirOfFile = goFiles.ToDictionary(f => f.Id, f => GoModuleResolver.DirectoryOf(f.Path));
            _pathOfFile = goFiles.ToDictionary(f => f.Id, f => f.Path);
            var goSymbols = all.Where(s => _dirOfFile.ContainsKey(s.FileId)).ToList();
            _byQualified = goSymbols.Where(s => s.Kind != SymbolKind.Module).GroupBy(s => s.QualifiedName).ToDictionary(g => g.Key, g => g.ToList());
            _modulesByDir = goSymbols.Where(s => s.Kind == SymbolKind.Module).GroupBy(s => _dirOfFile[s.FileId])
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.QualifiedName, StringComparer.Ordinal).ToList());
            _methodsByName = goSymbols.Where(s => s.Kind == SymbolKind.Method).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.ToList());
            var goReferences = references.Where(r => LanguageFamily(r.Language) == "go").ToList();
            // 内嵌字段（type A struct{ B }）：A 上找不到的方法沿它们往下找
            _embeds = goReferences.Where(r => r.Kind == EdgeKind.Inherits).GroupBy(r => r.FromSymbolId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var r in goReferences)
            {
                if (r.Script is not { Receiver: ScriptReceiver.Import, ImportSource: { } path, ImportedName: { } alias } || path.StartsWith('?')) continue;
                if (!_aliases.TryGetValue(r.FileId, out var map)) _aliases[r.FileId] = map = new(StringComparer.Ordinal);
                map.TryAdd(alias, path);
            }
            _importsByFile = importsByFile;
            _modules = new GoModuleResolver(modules, _dirOfFile.Values.ToHashSet(StringComparer.Ordinal));
        }

        public string? ResolveImport(string fromFile, string importPath) => _modules.Resolve(fromFile, importPath);

        public IReadOnlyList<SymbolRecord> ModulesIn(string dir) => _modulesByDir.GetValueOrDefault(dir) ?? [];

        public string DirOf(SymbolRecord s) => _dirOfFile[s.FileId];

        public string PathOf(SymbolRecord s) => _pathOfFile[s.FileId];

        /// <summary>包目录 dir 里限定名为 &lt;dir&gt;.&lt;name&gt; 的符号（name 可以是 "T.M"）。</summary>
        public SymbolRecord? Find(string dir, string name, Func<SymbolRecord, bool> accept) =>
            _byQualified.TryGetValue(Prefix(dir) + name, out var list) ? list.FirstOrDefault(s => accept(s) && DirOf(s) == dir) : null;

        public IReadOnlyList<SymbolRecord> MethodsNamed(string name) => _methodsByName.GetValueOrDefault(name) ?? [];

        public IReadOnlyList<PendingReference> EmbedsOf(long typeId) => _embeds.GetValueOrDefault(typeId) ?? [];

        /// <summary>本文件 import 的、解析到本仓库的包目录。</summary>
        public HashSet<string> ImportDirs(long fileId, string filePath)
        {
            if (_importDirsCache.TryGetValue(fileId, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (_importsByFile.TryGetValue(fileId, out var imports))
                foreach (var (target, _) in imports)
                    if (ResolveImport(filePath, target) is { } d) set.Add(d);
            _importDirsCache[fileId] = set;
            return set;
        }

        /// <summary>包目录的包名：目录里各文件 package 子句里最常见的那个（xxx_test 不算）。</summary>
        public string? PackageName(string dir)
        {
            if (_packageNames.TryGetValue(dir, out var cached)) return cached;
            var name = ModulesIn(dir).Select(m => m.Signature).Where(s => s is not null && s.StartsWith("package ", StringComparison.Ordinal))
                .Select(s => s!["package ".Length..].Trim()).Where(n => !n.EndsWith("_test", StringComparison.Ordinal))
                .GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
            _packageNames[dir] = name;
            return name;
        }

        /// <summary>
        /// 文件里代码写作 qualifier 的那条 import 的路径：别名（本次解析的文件）→ 默认包名 → 解析到本仓库的目录的 package 子句。都对不上返回 null。
        /// </summary>
        public string? ImportForQualifier(long fileId, string filePath, string qualifier)
        {
            if (_aliases.TryGetValue(fileId, out var map) && map.TryGetValue(qualifier, out var aliased)) return aliased;
            if (!_importsByFile.TryGetValue(fileId, out var imports)) return null;
            var byDefault = imports.Select(i => i.Target).FirstOrDefault(t => GoModuleResolver.DefaultPackageName(t) == qualifier);
            if (byDefault is not null) return byDefault;
            return imports.Select(i => i.Target).FirstOrDefault(t => ResolveImport(filePath, t) is { } d && PackageName(d) == qualifier);
        }

        public static string Prefix(string dir) => dir.Length == 0 ? "" : dir + ".";
    }

    /// <summary>解析出来的类型：本仓库的（Dir, Name），或仓库外包里的（ExternalPackage, Name；ExternalPackage 为 "" 表示预声明类型 error）。</summary>
    private sealed record GoTypeRef(string? Dir, string? ExternalPackage, string Name);

    /// <summary>import 来源落到哪：本仓库目录，或仓库外包的标签（null 表示对不上任何导入）。</summary>
    private readonly record struct GoPlace(string? Dir, string? External, bool Unknown);

    private static readonly List<NewEdge> NoEdges = [];

    private static List<NewEdge> ResolveGo(PendingReference r, GoIndex g, IndexWriter writer)
    {
        var funcValue = r.Via == GoFuncValueVia;
        List<NewEdge> External(string name)
        {
            if (funcValue) return NoEdges; // 实参解析不到本仓库函数：多半是变量 / 常量，不记 external
            var ext = writer.GetOrCreateExternal(name);
            return [new NewEdge(r.FromSymbolId, ext, "external:" + name, r.Kind, IndexSource.TreeSitter, 0.5, r.FileId, r.Line, Via: r.Via)];
        }
        List<NewEdge> One(SymbolRecord target, double confidence) => [Edge(r, target, confidence)];
        // 调用落到的是类型：类型转换 T(x)，记成对类型的引用
        List<NewEdge> Conversion(SymbolRecord type) =>
            [new NewEdge(r.FromSymbolId, type.Id, type.QualifiedName, EdgeKind.References, IndexSource.TreeSitter, GoPackageConfidence, r.FileId, r.Line)];

        if (r.Kind == EdgeKind.Imports)
        {
            // 包 = 目录：连到目录下每个文件的 Module
            if (g.ResolveImport(r.FilePath, r.Name) is { } importDir && g.ModulesIn(importDir) is { Count: > 0 } modules)
                return modules.Take(MaxCandidates)
                    .Select(m => new NewEdge(r.FromSymbolId, m.Id, m.QualifiedName, EdgeKind.Imports, IndexSource.TreeSitter, 1.0, r.FileId, r.Line)).ToList();
            return External(r.Name);
        }

        var b = r.Script ?? new ScriptBinding(ScriptReceiver.None);
        var here = GoModuleResolver.DirectoryOf(r.FilePath);
        var callLike = r.Kind == EdgeKind.Calls || funcValue;
        bool Accept(SymbolRecord s) => callLike ? s.Kind == SymbolKind.Function : IsGoType(s);

        switch (b.Receiver)
        {
            case ScriptReceiver.None:
            {
                if (g.Find(here, r.Name, Accept) is { } own) return One(own, GoPackageConfidence);
                var dotDirs = (b.ImportSource ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => g.ResolveImport(r.FilePath, p)).OfType<string>().ToList();
                foreach (var dotDir in dotDirs)
                    if (g.Find(dotDir, r.Name, Accept) is { } dotted) return One(dotted, GoPackageConfidence);
                if (r.Kind == EdgeKind.Calls && !funcValue)
                    foreach (var dir in dotDirs.Prepend(here))
                        if (g.Find(dir, r.Name, IsGoType) is { } type) return Conversion(type);
                // 调用：局部函数变量、闭包 → external；类型：多半是类型参数，不连
                return r.Kind == EdgeKind.Calls ? External(r.Name) : NoEdges;
            }
            case ScriptReceiver.Import:
            {
                var place = Locate(g, r.FileId, r.FilePath, b.ImportSource, b.ImportedName);
                if (place.Unknown && b.ImportedName is { } name && callLike)
                {
                    // 不是导入名：同包别的文件里的包级变量 x，x.M() 按变量的类型找方法
                    if (GoVarType(g, here, name) is { } varType) return ReceiverMethod(varType);
                    return funcValue ? NoEdges : Unknown();
                }
                if (place.Dir is { } dir)
                {
                    if (g.Find(dir, r.Name, Accept) is { } hit) return One(hit, GoPackageConfidence);
                    if (r.Kind == EdgeKind.Calls && !funcValue && g.Find(dir, r.Name, IsGoType) is { } type) return Conversion(type);
                }
                return External((place.External ?? b.ImportedName ?? "?") + "." + r.Name);
            }
            default:
            {
                if (!callLike) return NoEdges;
                if (b.ImportSource == GoBuiltinPackage) return External(r.Name); // err.Error()
                if (b.ImportedName is not null && EvalGoType(g, r, b.ImportSource, b.ImportedName, here) is { } type) return ReceiverMethod(type);
                return funcValue ? NoEdges : Unknown();
            }
        }

        List<NewEdge> ReceiverMethod(GoTypeRef type)
        {
            if (type.ExternalPackage is not null) return External(type.ExternalPackage.Length == 0 ? r.Name : type.ExternalPackage + "." + r.Name); // "" 是预声明的 error
            var (method, externalPackage) = FindGoMethod(g, type.Dir!, type.Name, r.Name, 0);
            if (method is not null) return One(method, GoReceiverTypeConfidence);
            if (externalPackage is not null) return External(externalPackage + "." + r.Name); // 内嵌的是仓库外的类型
            return funcValue ? NoEdges : Unknown();
        }

        // 类型未知：只在本包与本文件 import 的包里找同名方法
        List<NewEdge> Unknown()
        {
            var scope = g.ImportDirs(r.FileId, r.FilePath);
            var candidates = g.MethodsNamed(r.Name).Where(m => g.DirOf(m) == here || scope.Contains(g.DirOf(m))).ToList();
            if (candidates.Count == 1) return One(candidates[0], 0.7);
            if (candidates.Count > 1)
                return candidates.OrderBy(c => c.QualifiedName, StringComparer.Ordinal).Take(MaxCandidates).Select(c => Edge(r, c, 0.3)).ToList();
            return External(r.Name);
        }
    }

    private static bool IsGoType(SymbolRecord s) => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.TypeAlias;

    /// <summary>
    /// import 来源落到哪：null → 本包；"builtin" → 预声明类型；"?q" → 按 q 找本文件的导入（别名、默认名、package 子句），找不到 Unknown；
    /// 其他是 import 路径。解析不到本仓库目录的给出包标签（默认包名）。
    /// </summary>
    private static GoPlace Locate(GoIndex g, long fileId, string filePath, string? source, string? written)
    {
        if (source is null && written is null) return new GoPlace(GoModuleResolver.DirectoryOf(filePath), null, false);
        if (source == GoBuiltinPackage) return new GoPlace(null, "", false);
        if (source is null || source.StartsWith('?'))
        {
            var qualifier = source?[1..] ?? written!;
            source = g.ImportForQualifier(fileId, filePath, qualifier);
            if (source is null) return new GoPlace(null, qualifier, true);
        }
        return g.ResolveImport(filePath, source) is { } dir ? new GoPlace(dir, null, false) : new GoPlace(null, GoModuleResolver.DefaultPackageName(source), false);
    }

    // ---------- 类型描述求值 ----------

    private static readonly Regex GoDescRoot = new(@"^(\$?)([A-Za-z_]\w*)(\(\))?", RegexOptions.Compiled);
    private static readonly Regex GoDescStep = new(@"\G(?:\.([A-Za-z_]\w*)(\(\))?|(\[\]))", RegexOptions.Compiled);

    /// <summary>引用所在文件里的类型描述落到具体类型；读不出来返回 null。</summary>
    private static GoTypeRef? EvalGoType(GoIndex g, PendingReference r, string? source, string descriptor, string here)
    {
        var m = GoDescRoot.Match(descriptor);
        if (!m.Success) return null;
        var isVar = m.Groups[1].Length > 0;
        var root = m.Groups[2].Value;
        var isCall = m.Groups[3].Success;
        var steps = new List<(string? Name, bool Call, bool Elem)>();
        for (var pos = m.Length; pos < descriptor.Length;)
        {
            var s = GoDescStep.Match(descriptor, pos);
            if (!s.Success || s.Length == 0) return null;
            steps.Add(s.Groups[3].Success ? (null, false, true) : (s.Groups[1].Value, s.Groups[2].Success, false));
            pos += s.Length;
        }

        GoTypeRef? cur;
        var place = source is null ? new GoPlace(here, null, false) : Locate(g, r.FileId, r.FilePath, source, null);
        if (place.Unknown)
        {
            // "?x" 不是导入名：x 是同包的包级变量，根上的名字变成它的一步（x.F() / x.f）
            cur = GoVarType(g, here, source![1..]);
            steps.Insert(0, (root, isCall, false));
        }
        else if (place.Dir is null) cur = new GoTypeRef(null, place.External, root); // 仓库外包里的类型 / 函数返回值 / 变量
        else if (isVar) cur = GoVarType(g, place.Dir, root);
        else if (isCall) cur = GoFuncResult(g, place.Dir, root);
        else cur = new GoTypeRef(place.Dir, null, root);

        foreach (var step in steps)
        {
            if (cur is null) return null;
            if (cur.ExternalPackage is not null) continue; // 第三方类型上的字段 / 方法：仍归那个包
            cur = step.Elem ? GoElementType(g, cur) : step.Call ? GoMethodResult(g, cur, step.Name!) : GoFieldType(g, cur, step.Name!);
        }
        return cur;
    }

    /// <summary>函数 dir.name 的第一个返回值；name 其实是类型（类型转换、泛型类型实例化）时就是这个类型；读不出来时按 NewT() 返回 T 的约定。</summary>
    private static GoTypeRef? GoFuncResult(GoIndex g, string dir, string name)
    {
        var fn = g.Find(dir, name, s => s.Kind == SymbolKind.Function);
        if (fn is not null && GoSignatureType(g, fn, GoModuleResolver.FirstResultType(fn.Signature)) is { } result) return result;
        if (fn is null && g.Find(dir, name, IsGoType) is not null) return new GoTypeRef(dir, null, name);
        return name.Length > 3 && name.StartsWith("New", StringComparison.Ordinal) && char.IsUpper(name[3]) ? new GoTypeRef(dir, null, name[3..]) : null;
    }

    /// <summary>签名里读出的类型（限定符按定义所在文件的导入解析）落到具体类型。</summary>
    private static GoTypeRef? GoSignatureType(GoIndex g, SymbolRecord owner, (string? Qualifier, string Name)? type)
    {
        if (type is not { } t) return null;
        if (t.Qualifier is null)
            return t.Name == "error" || PredeclaredGoType(t.Name) ? new GoTypeRef(null, "", t.Name) : new GoTypeRef(g.DirOf(owner), null, t.Name);
        var place = Locate(g, owner.FileId, g.PathOf(owner), "?" + t.Qualifier, null);
        if (place.Unknown) return new GoTypeRef(null, t.Qualifier, t.Name);
        return place.Dir is { } dir ? new GoTypeRef(dir, null, t.Name) : new GoTypeRef(null, place.External, t.Name);
    }

    private static bool PredeclaredGoType(string name) => name is "any" or "bool" or "byte" or "complex64" or "complex128" or "float32" or "float64"
        or "int" or "int8" or "int16" or "int32" or "int64" or "rune" or "string" or "uint" or "uint8" or "uint16" or "uint32" or "uint64" or "uintptr";

    private static readonly Regex GoVarValue = new(@"^&?\s*([A-Za-z_]\w*)(?:\.([A-Za-z_]\w*))?\s*([({])", RegexOptions.Compiled);

    /// <summary>包级变量 dir.name 的类型：签名 "var x T" 的 T；"var x = T{…}" / "&amp;T{…}" 的 T；"var x = F(…)" / "pkg.F(…)" 的返回值。</summary>
    private static GoTypeRef? GoVarType(GoIndex g, string dir, string name)
    {
        var v = g.Find(dir, name, s => s.Kind == SymbolKind.Variable);
        if (v?.Signature is not { } sig || !sig.StartsWith("var ", StringComparison.Ordinal)) return null;
        var rest = sig[(4 + name.Length)..].Trim();
        if (!rest.StartsWith('=')) return GoSignatureType(g, v, GoModuleResolver.CoreType(rest));
        var value = rest[1..].Trim();
        if (value.StartsWith("make(", StringComparison.Ordinal) || value.StartsWith("new(", StringComparison.Ordinal))
            return GoSignatureType(g, v, GoModuleResolver.CoreType(value[(value.IndexOf('(') + 1)..])); // make([]T, n) / new(T)
        var m = GoVarValue.Match(value);
        if (!m.Success) return null;
        var (qualifier, ident) = m.Groups[2].Success ? (m.Groups[1].Value, m.Groups[2].Value) : ((string?)null, m.Groups[1].Value);
        if (m.Groups[3].Value == "{") return GoSignatureType(g, v, (qualifier, ident));
        if (qualifier is null) return GoFuncResult(g, dir, ident);
        var place = Locate(g, v.FileId, g.PathOf(v), "?" + qualifier, null);
        return place.Dir is { } fnDir ? GoFuncResult(g, fnDir, ident) : place.Unknown ? null : new GoTypeRef(null, place.External, ident);
    }

    /// <summary>type.field 的类型：字段（含内嵌字段提升）的声明类型；按类型名访问内嵌字段（s.Base）时就是那个类型。</summary>
    private static GoTypeRef? GoFieldType(GoIndex g, GoTypeRef type, string field, int depth = 0)
    {
        if (type.Dir is not { } dir) return null;
        if (g.Find(dir, type.Name + "." + field, s => s.Kind == SymbolKind.Field) is { } f)
        {
            var sig = f.Signature ?? "";
            return GoSignatureType(g, f, GoModuleResolver.CoreType(sig.StartsWith(field + " ", StringComparison.Ordinal) ? sig[(field.Length + 1)..] : null));
        }
        if (depth >= 3 || g.Find(dir, type.Name, IsGoType) is not { } typeSymbol) return null;
        foreach (var embed in g.EmbedsOf(typeSymbol.Id))
        {
            var embedded = GoEmbeddedType(g, embed, dir);
            if (embed.Name == field) return embedded;
            if (embedded is { ExternalPackage: null } && GoFieldType(g, embedded, field, depth + 1) is { } promoted) return promoted;
        }
        return null;
    }

    private static GoTypeRef? GoEmbeddedType(GoIndex g, PendingReference embed, string dir)
    {
        if (embed.Script is not { Receiver: ScriptReceiver.Import } e) return new GoTypeRef(dir, null, embed.Name);
        var place = Locate(g, embed.FileId, embed.FilePath, e.ImportSource, e.ImportedName);
        return place.Dir is { } d ? new GoTypeRef(d, null, embed.Name) : new GoTypeRef(null, place.External ?? e.ImportedName ?? "?", embed.Name);
    }

    /// <summary>type.M() 的第一个返回值：方法（含内嵌提升）的签名；没有这个方法而有同名的函数类型字段时取那个函数类型的返回值。</summary>
    private static GoTypeRef? GoMethodResult(GoIndex g, GoTypeRef type, string method)
    {
        if (type.Dir is not { } dir) return null;
        var (m, external) = FindGoMethod(g, dir, type.Name, method, 0);
        if (m is not null) return GoSignatureType(g, m, GoModuleResolver.FirstResultType(m.Signature));
        if (external is not null) return new GoTypeRef(null, external, method);
        if (g.Find(dir, type.Name + "." + method, s => s.Kind == SymbolKind.Field) is { Signature: { } sig } f && sig.StartsWith(method + " ", StringComparison.Ordinal))
            return GoSignatureType(g, f, GoModuleResolver.FirstResultType(sig[(method.Length + 1)..]));
        return null;
    }

    private static readonly Regex GoNamedContainer = new(@"^type\s+[A-Za-z_]\w*(?:\[[^\]]*\])?\s+(?:\[[^\]]*\]|map\[|chan\b|<-chan\b)", RegexOptions.Compiled);

    /// <summary>元素类型：命名的切片 / map / channel 类型（type Items []Item）取元素；其他（已经是核心类型）不变。</summary>
    private static GoTypeRef? GoElementType(GoIndex g, GoTypeRef type)
    {
        if (type.Dir is not { } dir || g.Find(dir, type.Name, s => s.Kind == SymbolKind.TypeAlias) is not { Signature: { } sig } alias || !GoNamedContainer.IsMatch(sig)) return type;
        var body = sig[(sig.IndexOf(type.Name, StringComparison.Ordinal) + type.Name.Length)..].Trim();
        if (body.StartsWith('[') && body.IndexOf(']') is var close and > 0 && !body.StartsWith("[]", StringComparison.Ordinal) && body[1..close].Any(char.IsLetter))
            body = body[(close + 1)..].Trim(); // type List[T any] []T：先跳过类型参数
        return GoSignatureType(g, alias, GoModuleResolver.CoreType(body)) ?? type;
    }

    /// <summary>类型 dir.typeName 上的方法；找不到时沿内嵌字段往下找（最多 3 层）。返回方法，或"内嵌了仓库外的类型"时的包名。</summary>
    private static (SymbolRecord? Method, string? ExternalPackage) FindGoMethod(GoIndex g, string dir, string typeName, string method, int depth)
    {
        if (g.Find(dir, typeName + "." + method, s => s.Kind == SymbolKind.Method) is { } own) return (own, null);
        if (depth >= 3) return (null, null);
        var type = g.Find(dir, typeName, IsGoType);
        if (type is null) return (null, null);
        string? external = null;
        foreach (var embed in g.EmbedsOf(type.Id))
        {
            var embedded = GoEmbeddedType(g, embed, dir);
            if (embedded is null) continue;
            if (embedded.Dir is null) { external ??= embedded.ExternalPackage; continue; }
            var (m, x) = FindGoMethod(g, embedded.Dir, embedded.Name, method, depth + 1);
            if (m is not null) return (m, null);
            external ??= x;
        }
        return (null, external);
    }
}
