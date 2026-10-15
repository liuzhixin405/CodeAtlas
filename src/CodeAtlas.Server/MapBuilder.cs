using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>地图聚合用的轻量符号。行号与可见性给入口识别与流程用，聚合视图不看。</summary>
public sealed record MapSymbol(long Id, SymbolKind Kind, string QualifiedName, string Path, long? ParentId, int StartLine = 0, int EndLine = 0, string? Visibility = null)
{
    /// <summary>
    /// 短名：模块取文件名（page.tsx）；其余去掉 external: 前缀与脚本限定名的 "路径:" 前缀（路径里的 (group)、[param]、扩展名的点
    /// 不能参与切分），再去掉参数列表、泛型参数，取最后一段。
    /// </summary>
    public string Name
    {
        get
        {
            if (Kind == SymbolKind.Module) return FileName(QualifiedName);
            var q = QualifiedName.StartsWith("external:", StringComparison.Ordinal) ? QualifiedName["external:".Length..] : QualifiedName;
            var colon = q.IndexOf(':');
            if (colon > 0 && IsScriptPath(q[..colon])) q = q[(colon + 1)..];
            var paren = q.IndexOf('(');
            if (paren >= 0) q = q[..paren];
            // 泛型参数可能在中间：Roslyn 给泛型类成员的限定名是 Ns.Repo<T>.Find，从第一个 < 截断会得到类名
            return MapBuilder.LastSegment(MapBuilder.StripGenericArgs(q));
        }
    }

    private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs"];

    private static bool IsScriptPath(string path) => ScriptExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    internal static string FileName(string path) => path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
}

/// <param name="Via">边的来历：dispatches 的 handles / dispatch:T，calls 的 conduit / impl / render（JSX 渲染组件）；普通边为 null。命名空间地图不用，管道视图与流程用。</param>
public sealed record MapEdge(long FromId, long ToId, EdgeKind Kind, double Confidence = 1.0, string? Via = null);

/// <param name="Key">分组键：命名空间（Acme.Api）、目录（src/app）或类型限定名。</param>
/// <param name="HasChildren">还能往下钻（有子命名空间 / 子目录）。类型节点恒为 false。</param>
/// <param name="SymbolId">类型节点对应的符号 id，可直接切到调用图；分组节点为 null。</param>
public sealed record MapGroupDto(string Key, string Label, int Symbols, int Files, bool HasChildren, long? SymbolId);

public sealed record MapEdgeDto(string From, string To, int Weight, Dictionary<string, int> Kinds);

public sealed record MapDto(string Root, IReadOnlyList<MapGroupDto> Groups, IReadOnlyList<MapEdgeDto> Edges);

/// <summary>
/// 项目地图：把符号按归属分组（C# 按命名空间，脚本按目录），统计组与组之间的依赖边。
/// root 为空显示顶层；root 是命名空间/目录时显示它的下一层，外加直接属于它的类型；root 是叶子时只有类型。
/// </summary>
public static class MapBuilder
{
    private static readonly HashSet<EdgeKind> DependencyKinds = [EdgeKind.Calls, EdgeKind.Dispatches, EdgeKind.Implements, EdgeKind.Inherits, EdgeKind.References];
    private static readonly HashSet<SymbolKind> TypeKinds = [SymbolKind.Class, SymbolKind.Interface, SymbolKind.Struct, SymbolKind.Enum];

    /// <summary>每个符号的分组键：C# 命名空间、Java 包、其余目录。external 不参与。</summary>
    public static Dictionary<long, string> GroupKeys(IReadOnlyList<MapSymbol> symbols)
    {
        var byId = symbols.ToDictionary(s => s.Id);
        var keys = new Dictionary<long, string>();
        Dictionary<string, string>? javaFilePackages = null;
        foreach (var s in symbols)
        {
            if (s.Kind == SymbolKind.External) continue;
            if (IsJava(s))
            {
                javaFilePackages ??= JavaFilePackages(symbols, byId);
                keys[s.Id] = JavaPackageOf(s, byId) ?? javaFilePackages.GetValueOrDefault(s.Path) ?? JavaPackageFromPath(s.Path);
                continue;
            }
            keys[s.Id] = IsCSharp(s) ? NamespaceOf(s, byId) : DirectoryOf(s.Path);
        }
        return keys;
    }

    internal static bool IsJava(MapSymbol s) => s.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    /// <summary>Java 符号的包：最外层类型的限定名（&lt;package&gt;.&lt;Type&gt;）去掉类型名；嵌套类、方法、字段都归外层类型的包。不在类型里（模块）→ null。</summary>
    private static string? JavaPackageOf(MapSymbol s, Dictionary<long, MapSymbol> byId)
    {
        if (TypeAncestor(s, byId) is not { } type) return null;
        var q = type.QualifiedName;
        var cut = q.IndexOfAny(['<', '(']);
        if (cut >= 0) q = q[..cut];
        var dot = q.LastIndexOf('.');
        return dot < 0 ? "" : q[..dot];
    }

    /// <summary>每个 .java 文件里类型的包（模块符号用）：取文件里第一个顶层类型的。</summary>
    private static Dictionary<string, string> JavaFilePackages(IReadOnlyList<MapSymbol> symbols, Dictionary<long, MapSymbol> byId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in symbols.Where(s => TypeKinds.Contains(s.Kind) && IsJava(s)).OrderBy(s => s.StartLine))
            if (!map.ContainsKey(s.Path) && JavaPackageOf(s, byId) is { } p) map[s.Path] = p;
        return map;
    }

    /// <summary>没有类型的 Java 文件（package-info.java）：src/main/java/ 之类的源码根（最后一个 /java/ 段）之后的目录按 '.' 连起来；找不到源码根退回目录。</summary>
    private static string JavaPackageFromPath(string path)
    {
        var p = "/" + path.Replace('\\', '/');
        var root = p.LastIndexOf("/java/", StringComparison.Ordinal);
        if (root < 0) return DirectoryOf(path);
        var rest = DirectoryOf(p[(root + "/java/".Length)..]);
        return rest.Replace('/', '.');
    }

    public static MapDto Build(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, string root)
    {
        root = root.Trim().TrimEnd('.', '/');
        var byId = symbols.ToDictionary(s => s.Id);
        var keys = GroupKeys(symbols);

        // 符号 → 地图节点键
        var nodeOf = new Dictionary<long, string>();
        var subGroups = new Dictionary<string, (HashSet<long> Symbols, HashSet<string> Files, bool HasChildren)>();
        var typeNodes = new Dictionary<string, (MapSymbol Type, HashSet<long> Symbols, HashSet<string> Files)>();

        foreach (var (id, key) in keys)
        {
            var s = byId[id];
            if (key == root)
            {
                // 直接属于 root 的符号：按所属类型聚合成类型节点
                var type = TypeAncestor(s, byId);
                if (type is null || keys.GetValueOrDefault(type.Id) != root) continue;
                if (!typeNodes.TryGetValue(type.QualifiedName, out var t))
                    typeNodes[type.QualifiedName] = t = (type, [], []);
                t.Symbols.Add(id);
                t.Files.Add(s.Path);
                nodeOf[id] = type.QualifiedName;
                continue;
            }
            var child = ChildKey(root, key);
            if (child is null) continue;
            if (!subGroups.TryGetValue(child, out var g))
                subGroups[child] = g = ([], [], false);
            g.Symbols.Add(id);
            g.Files.Add(s.Path);
            if (key != child) subGroups[child] = (g.Symbols, g.Files, true);
            nodeOf[id] = child;
        }

        var groups = new List<MapGroupDto>();
        foreach (var (key, g) in subGroups)
            groups.Add(new MapGroupDto(key, KeyLabel(key), g.Symbols.Count, g.Files.Count, g.HasChildren, null));
        foreach (var (key, t) in typeNodes)
            groups.Add(new MapGroupDto(key, LastSegment(key), t.Symbols.Count, t.Files.Count, false, t.Type.Id));
        groups.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        var agg = new Dictionary<(string, string), (int Weight, Dictionary<string, int> Kinds)>();
        foreach (var e in edges)
        {
            if (!DependencyKinds.Contains(e.Kind)) continue;
            if (e.Confidence < 0.4) continue; // 多候选（0.3）只是"可能"，不算模块间依赖
            if (!nodeOf.TryGetValue(e.FromId, out var from) || !nodeOf.TryGetValue(e.ToId, out var to) || from == to) continue;
            if (!agg.TryGetValue((from, to), out var a)) agg[(from, to)] = a = (0, new Dictionary<string, int>());
            var kind = DbNames.ToDb(e.Kind);
            a.Kinds[kind] = a.Kinds.GetValueOrDefault(kind) + 1;
            agg[(from, to)] = (a.Weight + 1, a.Kinds);
        }
        var mapEdges = agg.Select(p => new MapEdgeDto(p.Key.Item1, p.Key.Item2, p.Value.Weight, p.Value.Kinds))
            .OrderByDescending(e => e.Weight).ToList();

        return new MapDto(root, groups, mapEdges);
    }

    internal static bool IsCSharp(MapSymbol s) => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    internal static string NamespaceOf(MapSymbol s, Dictionary<long, MapSymbol> byId)
    {
        for (MapSymbol? cur = s; cur is not null; cur = cur.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
            if (cur.Kind == SymbolKind.Namespace) return cur.QualifiedName;
        return "";
    }

    internal static MapSymbol? TypeAncestor(MapSymbol s, Dictionary<long, MapSymbol> byId)
    {
        MapSymbol? found = null;
        for (MapSymbol? cur = s; cur is not null; cur = cur.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
            if (TypeKinds.Contains(cur.Kind)) found = cur; // 取最外层类型，嵌套类归到外层
        return found;
    }

    internal static string DirectoryOf(string path)
    {
        var p = path.Replace('\\', '/');
        var i = p.LastIndexOf('/');
        return i < 0 ? "" : p[..i];
    }

    /// <summary>
    /// key 在 root 之下时，返回 root 下一层的键；否则 null。'.' 与 '/' 都算分隔符（C# 项目目录里可能放着前端目录：Acme.Web/ClientApp）；
    /// 但第一个 '/' 之后只按 '/' 切——那是目录名，Go / Python 的包目录名里可能有点（src/pkg/v1.2），不能切成 v1 下的 2。
    /// </summary>
    internal static string? ChildKey(string root, string key)
    {
        if (key.Length == 0) return null;
        if (root.Length == 0)
        {
            var i = FirstSeparator(key, 0);
            return i < 0 ? key : key[..i];
        }
        if (!key.StartsWith(root, StringComparison.Ordinal) || key.Length <= root.Length) return null;
        var sepChar = key[root.Length];
        if (sepChar != '/' && (sepChar != '.' || !IsDotSeparator(key, root.Length))) return null;
        var next = FirstSeparator(key, root.Length + 1);
        return next < 0 ? key : key[..next];
    }

    private static int FirstSeparator(string key, int start)
    {
        var slash = key.IndexOf('/', start);
        var dot = key.IndexOf('.', start);
        if (dot < 0 || !IsDotSeparator(key, dot)) return slash;
        if (slash < 0) return dot;
        return Math.Min(dot, slash);
    }

    /// <summary>按 ChildKey 同一判据切段：'/' 总是分隔符，'.' 只在第一个 '/' 之前算。</summary>
    internal static string[] KeySegments(string key)
    {
        if (key.Length == 0) return [];
        var slash = key.IndexOf('/');
        if (slash < 0) return key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var head = key[..slash].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var tail = key[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return [.. head, .. tail];
    }

    /// <summary>key[index] 处的 '.' 是否分隔符：在第一个 '/' 之前才是（命名空间 / 项目目录名），之后是目录名的一部分。</summary>
    private static bool IsDotSeparator(string key, int index)
    {
        var slash = key.IndexOf('/');
        return slash < 0 || index < slash;
    }

    /// <summary>组键的显示名：目录键（含 '/'）取最后一个目录名（v1.2 原样），命名空间取最后一段。</summary>
    internal static string KeyLabel(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? LastSegment(key) : key[(slash + 1)..];
    }

    /// <summary>去掉所有成对的 &lt;…&gt;（可嵌套）：Ns.Repo&lt;T&gt;.Find → Ns.Repo.Find，Dictionary&lt;string, List&lt;int&gt;&gt; → Dictionary。</summary>
    internal static string StripGenericArgs(string name)
    {
        if (name.IndexOf('<') < 0) return name;
        var sb = new System.Text.StringBuilder(name.Length);
        var depth = 0;
        foreach (var c in name)
        {
            if (c == '<') depth++;
            else if (c == '>' && depth > 0) depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string LastSegment(string key)
    {
        var i = Math.Max(key.LastIndexOf('/'), key.LastIndexOf('.'));
        return i < 0 ? key : key[(i + 1)..];
    }
}
