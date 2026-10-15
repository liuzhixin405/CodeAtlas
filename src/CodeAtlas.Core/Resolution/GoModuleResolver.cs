namespace CodeAtlas.Core.Resolution;

/// <summary>
/// 一个 go.mod，路径全部换算成仓库相对目录（'/' 分隔、无首尾斜杠，仓库根为空串；外部根下形如 "../other"）。
/// </summary>
/// <param name="Directory">go.mod 所在目录；对这个目录下（不含更近的另一个 go.mod 管辖范围）的 .go 文件生效。</param>
/// <param name="ModulePath">module 行，如 "github.com/x/y"。</param>
/// <param name="Replaces">指向本地相对路径的 replace：被替换的模块路径 → 仓库相对目录。指向别的模块版本的 replace 不在内。</param>
public sealed record GoModFile(string Directory, string ModulePath, IReadOnlyList<KeyValuePair<string, string>> Replaces);

/// <summary>
/// 把 Go 的 import 路径解析成仓库里的包目录（包 = 目录）：
/// 最近的 go.mod 的 module 前缀 → 本模块目录；本地 replace；本模块的 vendor/；同仓库另一个 go.mod 的 module 前缀；
/// 文件没有任何 go.mod 管辖时退回"import 路径以某个已索引目录结尾"（取最长）。解析不到（标准库、第三方）返回 null。
/// </summary>
public sealed class GoModuleResolver
{
    private readonly List<GoModFile> _modules;
    private readonly IReadOnlySet<string> _packageDirs;
    private readonly Dictionary<(string Module, string Path), string?> _cache = [];

    /// <param name="packageDirs">已索引的 Go 包目录（有 .go 文件的目录）。解析结果必须落在其中。</param>
    public GoModuleResolver(IReadOnlyList<GoModFile> modules, IReadOnlySet<string> packageDirs)
    {
        // 最近的 go.mod 优先：目录越长越近
        _modules = modules.OrderByDescending(m => m.Directory.Length).ToList();
        _packageDirs = packageDirs;
    }

    public string? Resolve(string fromFile, string importPath)
    {
        var module = ModuleFor(fromFile);
        var key = (module?.Directory ?? "\0", importPath);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var found = Candidates(module, importPath).FirstOrDefault(_packageDirs.Contains);
        _cache[key] = found;
        return found;
    }

    private IEnumerable<string> Candidates(GoModFile? module, string importPath)
    {
        if (module is null)
        {
            // 没有 go.mod（只索引了模块里的一个子目录，或 GOPATH 布局）：import 路径以某个已索引目录结尾
            var best = _packageDirs.Where(d => d.Length > 0 && (importPath == d || importPath.EndsWith("/" + d, StringComparison.Ordinal)))
                .OrderByDescending(d => d.Length).FirstOrDefault();
            if (best is not null) yield return best;
            yield break;
        }
        if (Under(importPath, module.ModulePath) is { } rest) yield return Join(module.Directory, rest);
        foreach (var (old, dir) in module.Replaces)
            if (Under(importPath, old) is { } r) yield return Join(dir, r);
        yield return Join(module.Directory, "vendor/" + importPath);
        foreach (var other in _modules.Where(m => m != module).OrderByDescending(m => m.ModulePath.Length))
            if (Under(importPath, other.ModulePath) is { } r) yield return Join(other.Directory, r);
    }

    /// <summary>importPath 等于 prefix 返回 ""，在其下返回剩余部分，否则 null。</summary>
    private static string? Under(string importPath, string prefix) =>
        importPath == prefix ? "" : importPath.StartsWith(prefix + "/", StringComparison.Ordinal) ? importPath[(prefix.Length + 1)..] : null;

    private GoModFile? ModuleFor(string fromFile) =>
        _modules.FirstOrDefault(m => m.Directory.Length == 0 ? !fromFile.StartsWith("../", StringComparison.Ordinal) : fromFile.StartsWith(m.Directory + "/", StringComparison.Ordinal));

    /// <summary>仓库相对目录拼接并规范化；开头的 ".." 保留（外部根）。</summary>
    internal static string Join(string dir, string rest)
    {
        var parts = new List<string>();
        foreach (var seg in (dir.Length == 0 ? rest : rest.Length == 0 ? dir : dir + "/" + rest).Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public static string DirectoryOf(string file) => file.Contains('/') ? file[..file.LastIndexOf('/')] : "";

    /// <summary>
    /// 没写别名时 import 进来的包在代码里叫什么（同 goimports 的约定）：最后一段；是 v2/v3 这类版本段时取上一段；
    /// 去掉 "go-" 前缀；截到第一个非标识符字符（yaml.v3 → yaml）。
    /// </summary>
    public static string DefaultPackageName(string importPath)
    {
        var segments = importPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return importPath;
        var name = segments[^1];
        if (segments.Length > 1 && name.Length > 1 && name[0] == 'v' && name[1..].All(char.IsAsciiDigit)) name = segments[^2];
        if (name.StartsWith("go-", StringComparison.Ordinal)) name = name[3..];
        var end = 0;
        while (end < name.Length && (char.IsLetterOrDigit(name[end]) || name[end] == '_')) end++;
        return end > 0 ? name[..end] : name;
    }

    /// <summary>
    /// 从函数签名读第一个返回值的核心命名类型（见 <see cref="CoreType"/>：*T、[]T、map[K]T、chan T 都取 T；`pkg.T` 拆成限定符与名字）。
    /// 认三种写法：`func [(接收者)] 名字[类型参数](参数) 返回值 {…`、接口方法 `名字(参数) 返回值`、函数类型 `func(参数) 返回值`。
    /// 函数 / 匿名 struct 等返回类型、没有返回值、签名读不全时返回 null。
    /// </summary>
    public static (string? Qualifier, string Name)? FirstResultType(string? signature)
    {
        if (signature is null) return null;
        var s = signature.Trim();
        int i;
        if (s.StartsWith("func", StringComparison.Ordinal) && (s.Length == 4 || !IsIdentChar(s[4])))
        {
            i = SkipSpaces(s, 4);
            if (i < s.Length && s[i] == '(')
            {
                var afterGroup = SkipGroup(s, i);
                if (afterGroup < 0) return null;
                var j = SkipSpaces(s, afterGroup);
                // 后面跟着名字：刚才那组是接收者；否则是函数类型 func(参数) 返回值，那组就是参数
                if (j < s.Length && IsIdentChar(s[j])) i = j;
                else return ResultAfterParameters(s, afterGroup);
            }
        }
        else i = 0; // 接口方法：Get(id string) (*Item, error)
        while (i < s.Length && IsIdentChar(s[i])) i++;
        i = SkipSpaces(s, i);
        if (i < s.Length && s[i] == '[') { i = SkipGroup(s, i); if (i < 0) return null; }                       // 类型参数
        if (i >= s.Length || s[i] != '(') return null;
        i = SkipGroup(s, i);                                                                                    // 参数
        return i < 0 ? null : ResultAfterParameters(s, i);
    }

    private static (string? Qualifier, string Name)? ResultAfterParameters(string s, int i)
    {
        var rest = s[i..];
        var brace = TopLevelIndex(rest, '{');
        rest = (brace >= 0 ? rest[..brace] : rest).Trim();
        if (rest.Length == 0) return null;
        if (rest[0] == '(')
        {
            var close = SkipGroup(rest, 0);
            if (close < 0) return null;
            var inner = rest[1..(close - 1)];
            var comma = TopLevelIndex(inner, ',');
            rest = (comma >= 0 ? inner[..comma] : inner).Trim();
            // 具名返回值 (db *DB, err error)：去掉名字
            var space = TopLevelIndex(rest, ' ');
            if (space > 0 && rest[..space].All(IsIdentChar) && rest[..space] is not ("map" or "chan" or "func")) rest = rest[(space + 1)..];
        }
        return CoreType(rest);
    }

    /// <summary>
    /// 类型表达式的核心命名类型：去掉 *、[]、[N]、map[K]、chan / &lt;-chan / chan&lt;-、括号与类型实参，`pkg.T` 拆成限定符与名字。
    /// 函数类型、匿名 struct / interface 返回 null。只用于"接收者是这个类型时方法在哪"——切片本身没有方法，所以 []T 当 T 看。
    /// </summary>
    public static (string? Qualifier, string Name)? CoreType(string? typeText)
    {
        if (typeText is null) return null;
        var t = typeText.Trim();
        for (var guard = 0; guard < 16 && t.Length > 0; guard++)
        {
            if (t[0] is '*' or '(' or ')') { t = t.TrimStart('*', '(', ')').Trim(); continue; }
            if (t.StartsWith("...", StringComparison.Ordinal)) { t = t[3..].Trim(); continue; }
            if (t[0] == '[') { var end = SkipGroup(t, 0); if (end < 0) return null; t = t[end..].Trim(); continue; }
            if (t.StartsWith("map[", StringComparison.Ordinal)) { var end = SkipGroup(t, 3); if (end < 0) return null; t = t[end..].Trim(); continue; }
            if (t.StartsWith("<-chan", StringComparison.Ordinal)) { t = t[6..].Trim(); continue; }
            if (t.StartsWith("chan<-", StringComparison.Ordinal)) { t = t[6..].Trim(); continue; }
            if (t.StartsWith("chan", StringComparison.Ordinal) && (t.Length == 4 || !IsIdentChar(t[4]))) { t = t[4..].Trim(); continue; }
            break;
        }
        var n = 0;
        while (n < t.Length && (IsIdentChar(t[n]) || t[n] == '.')) n++;
        var type = t[..n].TrimEnd('.');
        if (type.Length == 0 || type is "func" or "struct" or "interface" or "map" or "chan") return null;
        var dot = type.IndexOf('.');
        return dot >= 0 ? (type[..dot], type[(dot + 1)..]) : (null, type);
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipSpaces(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    /// <summary>s[start] 是 ( 或 [：返回配对的闭括号之后的位置；没闭合返回 -1。</summary>
    private static int SkipGroup(string s, int start)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{') depth++;
            else if (s[i] is ')' or ']' or '}' && --depth == 0) return i + 1;
        }
        return -1;
    }

    private static int TopLevelIndex(string s, char target)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (depth == 0 && s[i] == target) return i;
            if (s[i] is '(' or '[') depth++;
            else if (s[i] is ')' or ']') depth--;
        }
        return -1;
    }

    // ---------- go.mod ----------

    /// <summary>
    /// 从扫描到的 .go 文件（仓库相对路径, 磁盘路径）往上找 go.mod（只在扫描根之内找），读 module 与本地 replace。读不了的跳过。
    /// </summary>
    public static IReadOnlyList<GoModFile> Discover(IEnumerable<(string RelativePath, string FullPath)> goFiles)
    {
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relativePath, fullPath) in goFiles)
        {
            string? rel = DirectoryOf(relativePath);
            var full = Path.GetDirectoryName(fullPath);
            // 外部根的文件路径以 "../" 开头：走到 ".." 就停，别把主根 "" 错配到外部根的上级目录
            while (rel is not null && full is not null && !(rel == ".." || rel.EndsWith("/..", StringComparison.Ordinal)) && dirs.TryAdd(rel, full))
            {
                if (rel.Length == 0) break;
                rel = DirectoryOf(rel);
                full = Path.GetDirectoryName(full);
            }
        }
        var result = new List<GoModFile>();
        foreach (var (rel, full) in dirs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var file = Path.Combine(full, "go.mod");
            if (!File.Exists(file)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (ParseGoMod(rel, text) is { } module) result.Add(module);
        }
        return result;
    }

    /// <summary>解析 go.mod 文本；没有 module 行返回 null。</summary>
    public static GoModFile? ParseGoMod(string directory, string text)
    {
        string? modulePath = null;
        var replaces = new List<KeyValuePair<string, string>>();
        var inReplaceBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (inReplaceBlock)
            {
                if (line == ")") { inReplaceBlock = false; continue; }
                AddReplace(line);
                continue;
            }
            if (line.StartsWith("module", StringComparison.Ordinal) && line.Length > 6 && char.IsWhiteSpace(line[6]))
                modulePath = Unquote(line[6..].Trim());
            else if (line.StartsWith("replace", StringComparison.Ordinal) && line.Length > 7 && (char.IsWhiteSpace(line[7]) || line[7] == '('))
            {
                var body = line[7..].Trim();
                if (body == "(") inReplaceBlock = true;
                else AddReplace(body);
            }
        }
        return modulePath is null or "" ? null : new GoModFile(directory, modulePath, replaces);

        void AddReplace(string spec)
        {
            var arrow = spec.IndexOf("=>", StringComparison.Ordinal);
            if (arrow < 0) return;
            var oldPart = spec[..arrow].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var newPart = spec[(arrow + 2)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (oldPart.Length == 0 || newPart.Length != 1) return; // 带版本号的新路径是另一个模块，不是本地目录
            var target = Unquote(newPart[0]);
            if (!(target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith("../", StringComparison.Ordinal) || target is "." or "..")) return;
            replaces.Add(new(Unquote(oldPart[0]), Join(directory, target)));
        }
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] is '"' or '`' && s[^1] == s[0] ? s[1..^1] : s;
}
