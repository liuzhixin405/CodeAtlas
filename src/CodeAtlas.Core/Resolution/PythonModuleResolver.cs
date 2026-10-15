using CodeAtlas.Core.Model;

namespace CodeAtlas.Core.Resolution;

/// <summary>
/// 把 Python 的模块名（"app.models"、相对的 "..core"、"."）解析成仓库里的模块符号。
/// <para>
/// 包根：解析器只看得到入库的 .py 文件（看不到 pyproject.toml / requirements.txt），所以用"导入方所在目录往上、自身不是包
/// （没有 __init__.py）的每一级目录"当候选包根，近的优先；每个候选根再试一次 根/src（src 布局）；仓库根总在最后。
/// 带标记文件的包根（backend/、每个微服务目录、manage.py 所在目录）正好都是这样的目录；
/// 平铺脚本 import 同目录模块（import demo_pb2）也按这条规则找到。
/// </para>
/// 包（a/b/__init__.py）优先于同名模块（a/b.py）。解析不到（标准库、第三方）返回 null。
/// </summary>
public sealed class PythonModuleResolver
{
    private readonly Dictionary<string, SymbolRecord> _modules = new(StringComparer.Ordinal);   // 去 .py 的路径 → 模块
    private readonly Dictionary<string, SymbolRecord> _packages = new(StringComparer.Ordinal);  // 包目录 → __init__.py 模块
    private readonly Dictionary<(string Dir, string Name), SymbolRecord?> _cache = [];
    private readonly Dictionary<string, List<string>> _rootsCache = new(StringComparer.Ordinal);

    /// <param name="modules">Python 文件的模块符号（QualifiedName 是仓库相对路径）。</param>
    public PythonModuleResolver(IEnumerable<SymbolRecord> modules)
    {
        foreach (var m in modules)
        {
            var path = m.QualifiedName;
            if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) continue;
            var noExt = path[..^3];
            if (noExt == "__init__" || noExt.EndsWith("/__init__", StringComparison.Ordinal))
                _packages.TryAdd(noExt.Length == 8 ? "" : noExt[..^9], m);
            else _modules.TryAdd(noExt, m);
        }
    }

    /// <summary>文件路径 → 模块名：去 .py、'/' 换 '.'，__init__.py 取所在包名（仓库根的 __init__.py 记作 "__init__"）。</summary>
    public static string ModuleName(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        if (p.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) p = p[..^3];
        if (p.EndsWith("/__init__", StringComparison.Ordinal)) p = p[..^9];
        return p.Replace('/', '.');
    }

    public static bool IsRelative(string name) => name.StartsWith('.');

    /// <summary>拼接模块名与成员名："." + "x" → ".x"，"..core" + "x" → "..core.x"，"a" + "b" → "a.b"。</summary>
    public static string Join(string module, string member) => module.EndsWith('.') ? module + member : module + "." + member;

    /// <summary>绝对模块名的顶层包（"google.cloud.storage" → "google"）。</summary>
    public static string TopLevel(string name) => name.Split('.')[0];

    public SymbolRecord? Resolve(string fromFile, string name)
    {
        var dir = DirectoryOf(fromFile);
        var key = (dir, name);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        SymbolRecord? found = null;
        if (IsRelative(name))
        {
            // 前导点数 n：从文件所在目录（即所在包）往上 n-1 级
            var dots = 0;
            while (dots < name.Length && name[dots] == '.') dots++;
            var baseDir = dir;
            for (var i = 1; i < dots && baseDir is not null; i++) baseDir = baseDir.Length == 0 ? null : DirectoryOf(baseDir);
            if (baseDir is not null) found = Lookup(Combine(baseDir, name[dots..].Replace('.', '/')));
        }
        else
        {
            var rel = name.Replace('.', '/');
            foreach (var root in Roots(dir))
                if (Lookup(Combine(root, rel)) is { } m) { found = m; break; }
        }
        _cache[key] = found;
        return found;
    }

    /// <summary>模块是不是包（__init__.py）：from pkg import x 时 x 可能是子模块。</summary>
    public static bool IsPackage(SymbolRecord module) =>
        module.QualifiedName.EndsWith("/__init__.py", StringComparison.Ordinal) || module.QualifiedName == "__init__.py";

    private SymbolRecord? Lookup(string pathNoExt) =>
        _packages.TryGetValue(pathNoExt, out var pkg) ? pkg : _modules.GetValueOrDefault(pathNoExt);

    /// <summary>候选包根，近的优先：不是包的祖先目录，以及它们的 src/；仓库根在最后。</summary>
    private List<string> Roots(string dir)
    {
        if (_rootsCache.TryGetValue(dir, out var cached)) return cached;
        var roots = new List<string>();
        for (string? d = dir; d is not null; d = d.Length == 0 ? null : DirectoryOf(d))
        {
            if (d.Length > 0 && _packages.ContainsKey(d)) continue; // 仓库根即使有 __init__.py 也保留
            roots.Add(d);
            roots.Add(Combine(d, "src"));
        }
        _rootsCache[dir] = roots;
        return roots;
    }

    private static string Combine(string dir, string rel) => dir.Length == 0 ? rel : rel.Length == 0 ? dir : dir + "/" + rel;

    private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
