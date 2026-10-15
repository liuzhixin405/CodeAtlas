using System.Text;
using System.Text.Json;

namespace CodeAtlas.Core.Architecture.Rules;

/// <summary>常量解析结果：Value 是展开后的字符串（解析不了的运行时值保留成 ${原文}），否则 Failure 说明原因。</summary>
public sealed record TsResolution(string? Value, string? Failure);

/// <summary>
/// TS / JS 常量解析：跟着 import 找到定义文件（tsconfig.json 的 paths 别名、相对路径、目录的 index.ts、export * 桶文件），
/// 求值顶层 / 局部 const 的初始化表达式——字符串、模板字符串、+ 拼接、嵌套对象字面量（含 as const）、箭头函数成员（参数保留成 ${参数}）。
/// 只做静态文本层面的求值：函数体、运算符、new、动态下标都不求值，给出失败原因。每个文件只分词一次，声明的求值结果缓存。
/// </summary>
public sealed class TsConstantResolver
{
    private static readonly string[] Extensions = [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts"];
    private const int MaxDepth = 40;

    private readonly Func<string, string?> _read;
    private readonly Func<string, bool> _exists;
    private readonly Dictionary<string, TsModule?> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TsConfig?> _tsconfigByDir = new(StringComparer.Ordinal);
    private readonly Dictionary<(string File, int Token), TsVal> _declValues = new();
    private readonly HashSet<(string File, int Token)> _evaluating = [];

    /// <param name="read">仓库相对路径（'/' 分隔）→ 文件内容；读不到返回 null。</param>
    /// <param name="exists">仓库相对路径的源码文件是否存在（模块解析时逐个试扩展名）。</param>
    public TsConstantResolver(Func<string, string?> read, Func<string, bool> exists)
    {
        _read = read;
        _exists = exists;
    }

    public static TsConstantResolver ForInventory(ProjectInventory inventory)
    {
        var files = new HashSet<string>(inventory.SourceFiles, StringComparer.Ordinal);
        return new TsConstantResolver(rel =>
        {
            try
            {
                var full = inventory.Absolute(rel);
                return File.Exists(full) ? File.ReadAllText(full) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }, files.Contains);
    }

    // ───────────── 对外 ─────────────

    /// <summary>import 说明符 → 仓库相对路径；外部包、找不到文件 → null。</summary>
    public string? ResolveModule(string fromFile, string specifier) => ResolveModuleCore(fromFile, specifier).Path;

    /// <summary>在 file 的末尾求值一段表达式文本（名字按文件里的声明与 import 找）。</summary>
    public TsResolution ResolveExpression(string file, string expression)
    {
        var m = Module(file);
        if (m is null) return new TsResolution(null, "读不到: " + file);
        var tokens = TsLexer.Tokenize(expression);
        var parser = new TsParser(tokens, expression, int.MaxValue / 2);
        var i = 0;
        var expr = parser.ParseExpression(ref i);
        if (i < tokens.Count) expr = new UnkE("复杂表达式", 0);
        return ToResolution(Eval(m, expr, EmptyParams, 0));
    }

    /// <summary>file 导出的名字（含 export * / export { } from 转出的）求值。</summary>
    public TsResolution ResolveExport(string file, string name)
    {
        var m = Module(file);
        return m is null ? new TsResolution(null, "读不到: " + file) : ToResolution(ExportLookup(m, name, 0));
    }

    // ───────────── 给规则用的内部接口 ─────────────

    internal TsModule? Module(string file)
    {
        if (_modules.TryGetValue(file, out var cached)) return cached;
        var text = _read(file);
        return _modules[file] = text is null ? null : new TsModule(file, text);
    }

    /// <summary>module 的 tokens[start, end) 当一个表达式求值（调用实参、初始化表达式）。</summary>
    internal TsVal EvalTokens(TsModule m, int start, int end)
    {
        if (end <= start) return new VFail("空表达式");
        var slice = m.Tokens.GetRange(start, end - start);
        var parser = new TsParser(slice, m.Text, 0);
        var i = 0;
        var expr = parser.ParseExpression(ref i);
        if (i < slice.Count) return new VFail("复杂表达式");
        return Eval(m, expr, EmptyParams, 0);
    }

    internal static TsResolution ToResolution(TsVal v) => v switch
    {
        VStr s => new TsResolution(s.Value, null),
        VFail f => new TsResolution(null, f.Reason),
        VObj => new TsResolution(null, "不是字符串: 对象字面量"),
        VFn => new TsResolution(null, "不是字符串: 函数"),
        _ => new TsResolution(null, "不是字符串: 模块"),
    };

    /// <summary>名字在 offset 处可见的声明（最近的前一个；没有就用顶层的）的初始化表达式起点；函数声明返回 -1 号 token。</summary>
    internal TsDecl? FindDecl(TsModule m, string name, int offset)
    {
        TsDecl? before = null, top = null;
        foreach (var d in m.Decls)
        {
            if (d.Name != name) continue;
            if (d.Offset < offset) before = d;
            if (d.Depth == 0) top ??= d;
        }
        return before ?? top;
    }

    /// <summary>m 导出的 name 的声明所在（跟着 export { a as b }、import 再导出、export * 桶文件）；找不到 → null。</summary>
    internal (TsModule Module, TsDecl Decl)? ExportDecl(TsModule m, string name, int depth = 0)
    {
        if (depth > 10) return null;
        if (m.LocalExportAliases.TryGetValue(name, out var original)) name = original;
        if (m.Decls.FirstOrDefault(d => d.Name == name && d.Depth == 0) is { } top) return (m, top);
        if (m.Imports.TryGetValue(name, out var imp))
            return ResolveModuleCore(m.File, imp.Specifier).Path is { } p && Module(p) is { } t ? ExportDecl(t, imp.Imported, depth + 1) : null;
        foreach (var re in m.ReExports)
        {
            if (re.Name == "*" || (re.Name is not null && re.Alias != name)) continue;
            if (ResolveModuleCore(m.File, re.Specifier).Path is not { } path || Module(path) is not { } target) continue;
            if (ExportDecl(target, re.Name ?? name, depth + 1) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>一个 import 进来的本地名字指向的模块文件；外部包 → null，Local 标明说明符是相对路径 / 别名。</summary>
    internal (TsModule? Target, bool Local, string? Specifier) ImportTarget(TsModule m, string localName)
    {
        if (!m.Imports.TryGetValue(localName, out var imp)) return (null, false, null);
        var (path, local) = ResolveModuleCore(m.File, imp.Specifier);
        return (path is null ? null : Module(path), local, imp.Specifier);
    }

    // ───────────── 求值 ─────────────

    private static readonly IReadOnlySet<string> EmptyParams = new HashSet<string>();

    private TsVal Eval(TsModule m, TsExpr e, IReadOnlySet<string> ps, int depth)
    {
        if (depth > MaxDepth) return new VFail("引用过深");
        switch (e)
        {
            case StrE s: return new VStr(s.Value);
            case UnkE u: return new VFail(u.Reason);
            case ObjE o: return new VObj(m, o);
            case FnE f: return new VFn(m, f);
            case TplE t:
            {
                var sb = new StringBuilder();
                foreach (var part in t.Parts)
                {
                    if (part is string lit) { sb.Append(lit); continue; }
                    var hole = (TplHole)part;
                    sb.Append(Eval(m, hole.Expr, ps, depth + 1) is VStr hv ? hv.Value : "${" + hole.Raw + "}");
                }
                return new VStr(sb.ToString());
            }
            case CatE c:
            {
                var sb = new StringBuilder();
                for (var k = 0; k < c.Items.Count; k++)
                    sb.Append(Eval(m, c.Items[k], ps, depth + 1) is VStr iv ? iv.Value : "${" + c.Raws[k] + "}");
                return new VStr(sb.ToString());
            }
            case RefE r: return EvalRef(m, r, ps, depth);
            default: return new VFail("无法解析");
        }
    }

    private TsVal EvalRef(TsModule m, RefE r, IReadOnlySet<string> ps, int depth)
    {
        if (ps.Contains(r.Path[0])) return new VStr("${" + r.Raw + "}"); // 箭头函数的参数：运行时值
        var cur = Lookup(m, r.Path[0], r.Offset, depth + 1);
        for (var k = 1; k < r.Path.Count; k++)
        {
            if (cur is VFail) return cur;
            var next = Member(cur, r.Path[k], depth + 1);
            if (next is null) return new VFail("没有成员: " + string.Join('.', r.Path.Take(k + 1)));
            cur = next;
        }
        if (!r.Called) return cur;
        if (cur is VFail) return cur;
        if (cur is VFn fn && fn.Fn.Body is { } body)
        {
            var inner = new HashSet<string>(ps);
            inner.UnionWith(fn.Fn.Params);
            return Eval(fn.Module, body, inner, depth + 1);
        }
        return new VFail("函数调用: " + r.Raw);
    }

    /// <summary>对象 / 模块的成员；找不到返回 null（由调用方拼"没有成员"），求值失败返回 VFail。</summary>
    private TsVal? Member(TsVal owner, string key, int depth)
    {
        switch (owner)
        {
            case VObj o:
                if (o.Obj.Members.TryGetValue(key, out var expr)) return Eval(o.Module, expr, EmptyParams, depth + 1);
                foreach (var spread in o.Obj.Spreads)
                {
                    var sv = Eval(o.Module, spread, EmptyParams, depth + 1);
                    if (sv is not (VObj or VMod)) continue;
                    var hit = Member(sv, key, depth + 1);
                    if (hit is not null && hit is not VFail) return hit;
                }
                return null;
            case VMod mod:
                var v = ExportLookup(mod.Module, key, depth + 1);
                return v is VFail f && f.Reason.StartsWith("未定义: ", StringComparison.Ordinal) ? null : v;
            default:
                return null;
        }
    }

    private TsVal Lookup(TsModule m, string name, int offset, int depth)
    {
        if (depth > MaxDepth) return new VFail("引用过深");
        if (FindDecl(m, name, offset) is { } decl) return DeclValue(m, decl, depth);
        if (m.Imports.TryGetValue(name, out var imp)) return ImportValue(m, imp, depth);
        return new VFail("未定义: " + name);
    }

    private TsVal ImportValue(TsModule m, TsImport imp, int depth)
    {
        var (path, local) = ResolveModuleCore(m.File, imp.Specifier);
        if (path is null) return new VFail((local ? "找不到模块: " : "外部包: ") + imp.Specifier);
        var target = Module(path);
        if (target is null) return new VFail("读不到: " + path);
        return imp.Imported == "*" ? new VMod(target) : ExportLookup(target, imp.Imported, depth + 1);
    }

    private TsVal ExportLookup(TsModule m, string name, int depth)
    {
        if (depth > MaxDepth) return new VFail("引用过深");
        if (m.LocalExportAliases.TryGetValue(name, out var original)) return Lookup(m, original, int.MaxValue, depth + 1);
        var top = m.Decls.FirstOrDefault(d => d.Name == name && d.Depth == 0);
        if (top is not null) return DeclValue(m, top, depth);
        if (m.Imports.TryGetValue(name, out var imp)) return ImportValue(m, imp, depth); // import { X } from …; export { X }
        foreach (var re in m.ReExports)
        {
            if (re.Name is not null && re.Alias != name) continue;
            var (path, _) = ResolveModuleCore(m.File, re.Specifier);
            var target = path is null ? null : Module(path);
            if (target is null) continue;
            if (re.Name == "*") return new VMod(target);      // export * as NS from
            if (re.Name is not null) return ExportLookup(target, re.Name, depth + 1);
            var v = ExportLookup(target, name, depth + 1);     // export * from
            if (v is not VFail f || !f.Reason.StartsWith("未定义: ", StringComparison.Ordinal)) return v;
        }
        return new VFail("未定义: " + name);
    }

    private TsVal DeclValue(TsModule m, TsDecl d, int depth)
    {
        if (d.TokenIndex < 0) return new VFn(m, new FnE([], null, d.Offset)); // function name() { … } / class Name { … }
        var key = (m.File, d.TokenIndex);
        if (_declValues.TryGetValue(key, out var cached)) return cached;
        if (!_evaluating.Add(key)) return new VFail("循环引用: " + d.Name);
        try
        {
            var parser = new TsParser(m.Tokens, m.Text, 0);
            var i = d.TokenIndex;
            var expr = parser.ParseExpression(ref i);
            var v = Eval(m, expr, EmptyParams, depth + 1);
            if (depth < MaxDepth) _declValues[key] = v; // 过深被截断的结果不缓存
            return v;
        }
        finally { _evaluating.Remove(key); }
    }

    // ───────────── 模块解析 ─────────────

    private (string? Path, bool Local) ResolveModuleCore(string fromFile, string spec)
    {
        var dir = DirOf(fromFile);
        if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec is "." or "..")
            return (Normalize(Join(dir, spec)) is { } b ? TryCandidates(b) : null, true);
        var cfg = TsConfigFor(dir);
        if (cfg is null) return (null, false);
        foreach (var (pattern, targets) in cfg.Paths)
        {
            string? rest = null;
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (spec.StartsWith(prefix, StringComparison.Ordinal)) rest = spec[prefix.Length..];
            }
            else if (pattern == spec) rest = "";
            if (rest is null) continue;
            foreach (var t in targets)
                if (Normalize(Join(cfg.BaseDir, t.Replace("*", rest, StringComparison.Ordinal))) is { } candidate && TryCandidates(candidate) is { } hit)
                    return (hit, true);
            return (null, true);
        }
        // 只写了 baseUrl：非相对说明符先当成相对 baseUrl 的路径试一下，试不到才算外部包
        if (cfg.HasBaseUrl && Normalize(Join(cfg.BaseDir, spec)) is { } viaBase && TryCandidates(viaBase) is { } found) return (found, true);
        return (null, false);
    }

    private string? TryCandidates(string basePath)
    {
        if (_exists(basePath)) return basePath;
        foreach (var ext in Extensions) if (_exists(basePath + ext)) return basePath + ext;
        foreach (var ext in Extensions) if (_exists(basePath + "/index" + ext)) return basePath + "/index" + ext;
        return null;
    }

    /// <summary>最近的 tsconfig.json（从文件目录往上找到仓库根）；paths 按前缀长度降序。extends 只跟一层相对路径。</summary>
    private TsConfig? TsConfigFor(string dir)
    {
        var visited = new List<string>();
        TsConfig? found = null;
        for (var d = dir; ; d = DirOf(d))
        {
            if (_tsconfigByDir.TryGetValue(d, out var c)) { found = c; break; }
            visited.Add(d);
            var path = d.Length == 0 ? "tsconfig.json" : d + "/tsconfig.json";
            if (_read(path) is { } text) { found = ParseTsConfig(d, text); break; }
            if (d.Length == 0) break;
        }
        foreach (var v in visited) _tsconfigByDir[v] = found;
        return found;
    }

    private TsConfig? ParseTsConfig(string dir, string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return new TsConfig(dir, [], false); }
        using (doc)
        {
            var root = doc.RootElement;
            string? baseUrl = null;
            var paths = new List<(string, string[])>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("compilerOptions", out var co) && co.ValueKind == JsonValueKind.Object)
            {
                if (co.TryGetProperty("baseUrl", out var bu) && bu.ValueKind == JsonValueKind.String) baseUrl = bu.GetString();
                if (co.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    foreach (var p in ps.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Array)
                            paths.Add((p.Name, p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()));
            }
            if (paths.Count == 0 && baseUrl is null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("extends", out var ext)
                && ext.ValueKind == JsonValueKind.String && ext.GetString() is { } e && e.StartsWith('.'))
            {
                var parentPath = Normalize(Join(dir, e.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? e : e + ".json"));
                if (parentPath is not null && _read(parentPath) is { } parentText) return ParseTsConfig(DirOf(parentPath), parentText);
            }
            var baseDir = baseUrl is null ? dir : Normalize(Join(dir, baseUrl)) ?? dir;
            return new TsConfig(baseDir, paths.OrderByDescending(p => p.Item1.Length).ToList(), baseUrl is not null);
        }
    }

    private sealed record TsConfig(string BaseDir, List<(string Pattern, string[] Targets)> Paths, bool HasBaseUrl);

    internal static string DirOf(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    private static string Join(string dir, string rel) => dir.Length == 0 ? rel : dir + "/" + rel;

    /// <summary>折叠 . 与 ..；越过仓库根 → null。</summary>
    private static string? Normalize(string path)
    {
        var stack = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return string.Join('/', stack);
    }
}

// ───────────── 值 ─────────────

internal abstract record TsVal;
internal sealed record VStr(string Value) : TsVal;
internal sealed record VObj(TsModule Module, ObjE Obj) : TsVal;
internal sealed record VFn(TsModule Module, FnE Fn) : TsVal;
internal sealed record VMod(TsModule Module) : TsVal;
internal sealed record VFail(string Reason) : TsVal;

// ───────────── 模块：分词结果 + import / 再导出 / 声明表 ─────────────

internal sealed record TsImport(string Specifier, string Imported);
/// <param name="Name">null = export * from；"*" = export * as Alias from。</param>
internal sealed record TsReExport(string Specifier, string? Name, string? Alias);
/// <param name="TokenIndex">初始化表达式的第一个 token；-1 = function / class 声明（不求值，调用时报"函数调用"）。</param>
/// <param name="Depth">花括号深度，0 = 顶层。</param>
/// <param name="NameIndex">名字 token 的下标。</param>
internal sealed record TsDecl(string Name, int TokenIndex, int Offset, int Depth, int NameIndex);

internal sealed class TsModule
{
    public string File { get; }
    public string Text { get; }
    public List<TsToken> Tokens { get; }
    public Dictionary<string, TsImport> Imports { get; } = new(StringComparer.Ordinal);
    public List<TsReExport> ReExports { get; } = [];
    public Dictionary<string, string> LocalExportAliases { get; } = new(StringComparer.Ordinal);
    public List<TsDecl> Decls { get; } = [];
    private readonly int[] _lineStarts;

    public TsModule(string file, string text)
    {
        File = file;
        Text = text;
        Tokens = TsLexer.Tokenize(text);
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = [.. starts];
        Index();
    }

    /// <summary>字符偏移 → 1 起的行号。</summary>
    public int LineOf(int offset)
    {
        var i = Array.BinarySearch(_lineStarts, offset);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    /// <summary>
    /// 一个声明占的 token 范围 [名字, 结束)：const 到同层的 ; 或下一条顶层语句（export / const / function …，兼容不写分号）；
    /// function 到函数体的 }。用来只看工厂函数自己的函数体，而不是整个文件。
    /// </summary>
    public (int Start, int End) DeclRange(TsDecl d)
    {
        var t = Tokens;
        if (d.TokenIndex < 0)
        {
            var open = d.NameIndex;
            while (open < t.Count && !IsPunct(open, "{")) open++;
            var close = TsParser.MatchClose(t, open);
            return (d.NameIndex, close < 0 ? t.Count : close + 1);
        }
        var depth = 0;
        for (var i = d.TokenIndex; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Kind == TsTokKind.Punct)
            {
                if (tok.Text is "(" or "[" or "{") depth++;
                else if (tok.Text is ")" or "]" or "}") { if (depth == 0) return (d.NameIndex, i); depth--; }
                else if (depth == 0 && tok.Text == ";") return (d.NameIndex, i);
            }
            else if (depth == 0 && i > d.TokenIndex && tok.Kind == TsTokKind.Ident && tok.Text is "export" or "const" or "let" or "var" or "function" or "class" or "import"
                     && !IsPunct(i - 1, ".")) return (d.NameIndex, i);
        }
        return (d.NameIndex, t.Count);
    }

    public bool IsPunct(int i, string p) => i >= 0 && i < Tokens.Count && Tokens[i].Kind == TsTokKind.Punct && Tokens[i].Text == p;
    public bool IsIdent(int i, string? name = null) => i >= 0 && i < Tokens.Count && Tokens[i].Kind == TsTokKind.Ident && (name is null || Tokens[i].Text == name);

    private void Index()
    {
        var t = Tokens;
        var depth = 0;
        for (var i = 0; i < t.Count; i++)
        {
            var tok = t[i];
            if (tok.Kind == TsTokKind.Punct)
            {
                if (tok.Text == "{") depth++;
                else if (tok.Text == "}" && depth > 0) depth--;
                continue;
            }
            if (tok.Kind != TsTokKind.Ident) continue;
            var prevDot = IsPunct(i - 1, ".") || IsPunct(i - 1, "?.");
            if (prevDot) continue;
            switch (tok.Text)
            {
                case "import" when depth == 0 && !IsPunct(i + 1, "(") && !IsPunct(i + 1, "."):
                    i = ParseImport(i);
                    break;
                case "export" when depth == 0 && (IsPunct(i + 1, "{") || IsPunct(i + 1, "*")):
                    i = ParseExportList(i);
                    break;
                case "const" or "let" or "var" when IsIdent(i + 1):
                    if (ParseDecl(i, depth) is { } after) i = after;
                    break;
                case "function" or "class" when IsIdent(i + 1):
                    Decls.Add(new TsDecl(t[i + 1].Text, -1, t[i + 1].Offset, depth, i + 1));
                    break;
            }
        }
    }

    /// <summary>import X, { a, b as c, type d } from "m"；import * as NS from "m"；import "m"。返回最后一个已处理 token 的下标。</summary>
    private int ParseImport(int i)
    {
        var t = Tokens;
        var j = i + 1;
        if (IsIdent(j, "type") && !IsIdent(j + 1, "from") && !IsPunct(j + 1, ",")) j++;
        var bindings = new List<(string Local, string Imported)>();
        for (; j < t.Count; j++)
        {
            if (t[j].Kind == TsTokKind.Str) break;
            if (IsIdent(j, "from")) continue;
            if (IsPunct(j, ";")) return j;
            if (IsPunct(j, "*") && IsIdent(j + 1, "as") && IsIdent(j + 2)) { bindings.Add((t[j + 2].Text, "*")); j += 2; continue; }
            if (IsPunct(j, "{"))
            {
                for (j++; j < t.Count && !IsPunct(j, "}"); j++)
                {
                    if (IsIdent(j, "type") && IsIdent(j + 1) && !IsIdent(j + 1, "as")) j++;
                    if (!IsIdent(j) && t[j].Kind != TsTokKind.Str) continue;
                    var imported = t[j].Text;
                    if (IsIdent(j + 1, "as") && IsIdent(j + 2)) { bindings.Add((t[j + 2].Text, imported)); j += 2; }
                    else bindings.Add((imported, imported));
                }
                continue;
            }
            if (IsIdent(j)) bindings.Add((t[j].Text, "default"));
        }
        if (j >= t.Count) return j;
        foreach (var (local, imported) in bindings) Imports[local] = new TsImport(t[j].Text, imported);
        return j;
    }

    /// <summary>export { a, b as c } [from "m"]；export * [as NS] from "m"。</summary>
    private int ParseExportList(int i)
    {
        var t = Tokens;
        var j = i + 1;
        if (IsPunct(j, "*"))
        {
            string? alias = null;
            if (IsIdent(j + 1, "as") && IsIdent(j + 2)) { alias = t[j + 2].Text; j += 2; }
            if (IsIdent(j + 1, "from") && j + 2 < t.Count && t[j + 2].Kind == TsTokKind.Str)
            {
                ReExports.Add(new TsReExport(t[j + 2].Text, alias is null ? null : "*", alias));
                return j + 2;
            }
            return j;
        }
        var names = new List<(string Name, string Alias)>();
        for (j++; j < t.Count && !IsPunct(j, "}"); j++)
        {
            if (IsIdent(j, "type") && IsIdent(j + 1) && !IsIdent(j + 1, "as")) j++;
            if (!IsIdent(j)) continue;
            var name = t[j].Text;
            if (IsIdent(j + 1, "as") && IsIdent(j + 2)) { names.Add((name, t[j + 2].Text)); j += 2; }
            else names.Add((name, name));
        }
        if (IsIdent(j + 1, "from") && j + 2 < t.Count && t[j + 2].Kind == TsTokKind.Str)
        {
            foreach (var (name, alias) in names) ReExports.Add(new TsReExport(t[j + 2].Text, name, alias));
            return j + 2;
        }
        foreach (var (name, alias) in names) if (name != alias) LocalExportAliases[alias] = name;
        return j;
    }

    /// <summary>const name [: 类型] = 表达式。类型注解里的 = 不会出现（=> 是单独的 token）。</summary>
    private int? ParseDecl(int i, int depth)
    {
        var t = Tokens;
        var name = t[i + 1];
        var j = i + 2;
        if (IsPunct(j, ":"))
        {
            var d = 0;
            for (j++; j < t.Count; j++)
            {
                if (t[j].Kind != TsTokKind.Punct) continue;
                var p = t[j].Text;
                if (p is "(" or "[" or "{" or "<") d++;
                else if (p is ")" or "]" or "}" or ">") { if (d == 0) return null; d--; }
                else if (d == 0 && p is "=") break;
                else if (d == 0 && p is ";") return null;
            }
        }
        if (!IsPunct(j, "=") || j + 1 >= t.Count) return null;
        Decls.Add(new TsDecl(name.Text, j + 1, name.Offset, depth, i + 1));
        return j;
    }
}

// ───────────── 分词 ─────────────

internal enum TsTokKind { Ident, Str, Num, Template, Punct, Regex }

internal sealed record TsTemplateHole(List<TsToken> Tokens, string Raw);

/// <param name="Text">标识符名 / 标点 / 字符串的内容（已去转义）/ 数字原文。</param>
/// <param name="Parts">模板字符串：string（字面段）或 TsTemplateHole（${…} 里的 token）。</param>
internal sealed record TsToken(TsTokKind Kind, string Text, int Offset, int End, List<object>? Parts = null);

internal static class TsLexer
{
    private static readonly string[] Puncts = ["...", "===", "!==", "?.", "=>", "==", "!=", "&&", "||", "??", "++", "--", "+=", "-=", "*=", "/=", "**"];
    private static readonly HashSet<string> RegexAfterKeywords = ["return", "typeof", "case", "in", "of", "delete", "void", "throw", "new", "else", "do", "await", "yield"];

    public static List<TsToken> Tokenize(string s)
    {
        var p = 0;
        return Run(s, ref p, stopAtBrace: false);
    }

    /// <summary>分词到文本末尾；stopAtBrace 时遇到同层的 } 停下（模板字符串的 ${…}），p 停在 } 之后。</summary>
    private static List<TsToken> Run(string s, ref int p, bool stopAtBrace)
    {
        var list = new List<TsToken>();
        var depth = 0;
        while (p < s.Length)
        {
            var c = s[p];
            if (char.IsWhiteSpace(c)) { p++; continue; }
            if (c == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
            if (c == '/' && p + 1 < s.Length && s[p + 1] == '*') { var e = s.IndexOf("*/", p + 2, StringComparison.Ordinal); p = e < 0 ? s.Length : e + 2; continue; }
            var start = p;
            if (c is '"' or '\'') { var v = ReadQuoted(s, ref p, c); list.Add(new TsToken(TsTokKind.Str, v, start, p)); continue; }
            if (c == '`') { list.Add(ReadTemplate(s, ref p)); continue; }
            if (IsIdentStart(c))
            {
                while (p < s.Length && IsIdentPart(s[p])) p++;
                list.Add(new TsToken(TsTokKind.Ident, s[start..p], start, p));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                while (p < s.Length && (char.IsAsciiLetterOrDigit(s[p]) || s[p] is '.' or '_')) p++;
                list.Add(new TsToken(TsTokKind.Num, s[start..p], start, p));
                continue;
            }
            if (c == '/' && RegexAllowed(list))
            {
                SkipRegex(s, ref p);
                list.Add(new TsToken(TsTokKind.Regex, s[start..p], start, p));
                continue;
            }
            if (stopAtBrace)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) { p++; return list; }
                    depth--;
                }
            }
            var at = p;
            var punct = Puncts.FirstOrDefault(x => string.CompareOrdinal(s, at, x, 0, x.Length) == 0) ?? c.ToString();
            list.Add(new TsToken(TsTokKind.Punct, punct, start, start + punct.Length));
            p += punct.Length;
        }
        return list;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c is '_' or '$';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static bool RegexAllowed(List<TsToken> list)
    {
        if (list.Count == 0) return true;
        var last = list[^1];
        return last.Kind switch
        {
            TsTokKind.Punct => last.Text is not (")" or "]" or "}"),
            TsTokKind.Ident => RegexAfterKeywords.Contains(last.Text),
            _ => false,
        };
    }

    private static void SkipRegex(string s, ref int p)
    {
        p++;
        var inClass = false;
        while (p < s.Length && s[p] != '\n')
        {
            var ch = s[p];
            if (ch == '\\') { p += 2; continue; }
            if (ch == '[') inClass = true;
            else if (ch == ']') inClass = false;
            else if (ch == '/' && !inClass) { p++; break; }
            p++;
        }
        while (p < s.Length && char.IsAsciiLetter(s[p])) p++;
    }

    /// <summary>'…' / "…"：处理转义；遇到换行当作未闭合（JSX 文本里的撇号），只吞到行尾。</summary>
    private static string ReadQuoted(string s, ref int p, char quote)
    {
        var sb = new StringBuilder();
        p++;
        while (p < s.Length)
        {
            var ch = s[p];
            if (ch == quote) { p++; break; }
            if (ch == '\n') break;
            if (ch == '\\' && p + 1 < s.Length) { sb.Append(Unescape(s[p + 1])); p += 2; continue; }
            sb.Append(ch);
            p++;
        }
        return sb.ToString();
    }

    private static TsToken ReadTemplate(string s, ref int p)
    {
        var start = p;
        var parts = new List<object>();
        var lit = new StringBuilder();
        p++;
        while (p < s.Length)
        {
            var ch = s[p];
            if (ch == '`') { p++; break; }
            if (ch == '\\' && p + 1 < s.Length) { lit.Append(Unescape(s[p + 1])); p += 2; continue; }
            if (ch == '$' && p + 1 < s.Length && s[p + 1] == '{')
            {
                if (lit.Length > 0) { parts.Add(lit.ToString()); lit.Clear(); }
                p += 2;
                var holeStart = p;
                var tokens = Run(s, ref p, stopAtBrace: true);
                var holeEnd = Math.Max(holeStart, p - 1);
                parts.Add(new TsTemplateHole(tokens, s[holeStart..holeEnd].Trim()));
                continue;
            }
            lit.Append(ch);
            p++;
        }
        if (lit.Length > 0) parts.Add(lit.ToString());
        return new TsToken(TsTokKind.Template, s[start..p], start, p, parts);
    }

    private static char Unescape(char c) => c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c };
}

// ───────────── 表达式 ─────────────

internal abstract record TsExpr(int Offset);
internal sealed record StrE(string Value, int Offset) : TsExpr(Offset);
internal sealed record TplHole(TsExpr Expr, string Raw);
/// <param name="Parts">string（字面段）或 TplHole。</param>
internal sealed record TplE(IReadOnlyList<object> Parts, int Offset) : TsExpr(Offset);
internal sealed record CatE(IReadOnlyList<TsExpr> Items, IReadOnlyList<string> Raws, int Offset) : TsExpr(Offset);
/// <param name="Path">a.b["c"] → [a, b, c]。</param>
/// <param name="Called">链的最后是一次调用 a.b(…)；实参不求值。</param>
internal sealed record RefE(IReadOnlyList<string> Path, bool Called, string Raw, int Offset) : TsExpr(Offset);
internal sealed record ObjE(IReadOnlyDictionary<string, TsExpr> Members, IReadOnlyList<TsExpr> Spreads, int Offset) : TsExpr(Offset);
/// <param name="Body">表达式体；null = 花括号函数体（不求值）。</param>
internal sealed record FnE(IReadOnlyList<string> Params, TsExpr? Body, int Offset) : TsExpr(Offset);
internal sealed record UnkE(string Reason, int Offset) : TsExpr(Offset);

/// <summary>TS 表达式子集的递归下降解析：遇到认不出的结构给 UnkE 并跳到同层的结束符，不抛异常。</summary>
internal sealed class TsParser(List<TsToken> tokens, string text, int offsetBias)
{
    private static readonly HashSet<string> Terminators = [",", ")", "]", "}", ";"];
    private static readonly HashSet<string> BinaryOps = ["||", "??", "&&", "?", "-", "*", "/", "%", "==", "===", "!=", "!==", "<", ">", "|", "&", "**"];

    private bool IsPunct(int i, string p) => i < tokens.Count && tokens[i].Kind == TsTokKind.Punct && tokens[i].Text == p;
    private bool IsIdent(int i, string? name = null) => i < tokens.Count && tokens[i].Kind == TsTokKind.Ident && (name is null || tokens[i].Text == name);
    private int Off(int i) => (i < tokens.Count ? tokens[i].Offset : text.Length) + offsetBias;
    private string Raw(int from, int to) => from >= to || to > tokens.Count ? "" : text[tokens[from].Offset..tokens[to - 1].End].Trim();

    public TsExpr ParseExpression(ref int i)
    {
        var start = i;
        var items = new List<TsExpr> { ParseUnary(ref i) };
        var raws = new List<string> { Raw(start, i) };
        while (IsPunct(i, "+"))
        {
            i++;
            var s = i;
            items.Add(ParseUnary(ref i));
            raws.Add(Raw(s, i));
        }
        if (i < tokens.Count && ((tokens[i].Kind == TsTokKind.Punct && BinaryOps.Contains(tokens[i].Text)) || IsIdent(i, "instanceof") || IsIdent(i, "in")))
        {
            var op = tokens[i].Text;
            SkipToTerminator(ref i);
            return new UnkE("运算 " + op, Off(start));
        }
        return items.Count == 1 ? items[0] : new CatE(items, raws, Off(start));
    }

    private TsExpr ParseUnary(ref int i)
    {
        if (IsIdent(i, "await")) i++;
        if (i >= tokens.Count) return new UnkE("空表达式", Off(i));
        if (IsIdent(i, "new") || IsIdent(i, "typeof") || IsPunct(i, "!") || IsPunct(i, "-"))
        {
            var what = tokens[i].Text;
            var s = Off(i);
            SkipToTerminator(ref i);
            return new UnkE(what == "new" ? "new 表达式" : "运算 " + what, s);
        }
        return ParsePostfix(ParsePrimary(ref i), ref i);
    }

    private TsExpr ParsePostfix(TsExpr e, ref int i)
    {
        while (true)
        {
            if (IsIdent(i, "as") || IsIdent(i, "satisfies")) { i++; SkipType(ref i); continue; }
            if (IsPunct(i, "!") && !IsPunct(i + 1, "=")) { i++; continue; }
            return e;
        }
    }

    private TsExpr ParsePrimary(ref int i)
    {
        var tok = tokens[i];
        var off = Off(i);
        switch (tok.Kind)
        {
            case TsTokKind.Str: i++; return new StrE(tok.Text, off);
            case TsTokKind.Num: i++; return new StrE(tok.Text, off);
            case TsTokKind.Template: i++; return Template(tok, off);
            case TsTokKind.Ident:
                if (IsPunct(i + 1, "=>")) { var p = tok.Text; i += 2; return ArrowBody([p], ref i, off); }
                if (tok.Text == "async" && (IsPunct(i + 1, "(") || IsIdent(i + 1))) { i++; return ParsePrimary(ref i); }
                if (tok.Text is "true" or "false" or "null" or "undefined" or "this" or "function" or "class")
                {
                    SkipToTerminator(ref i);
                    return new UnkE("不是常量: " + tok.Text, off);
                }
                return Reference(ref i);
            case TsTokKind.Punct when tok.Text == "(":
            {
                var close = MatchClose(i);
                if (close < 0) { i = tokens.Count; return new UnkE("括号不配对", off); }
                if (IsPunct(close + 1, "=>") || (IsPunct(close + 1, ":") && ArrowAfterReturnType(close + 1) is not null))
                {
                    var ps = Params(i + 1, close);
                    i = IsPunct(close + 1, "=>") ? close + 2 : ArrowAfterReturnType(close + 1)!.Value + 1;
                    return ArrowBody(ps, ref i, off);
                }
                i++;
                var inner = ParseExpression(ref i);
                if (!IsPunct(i, ")")) { i = close + 1; return new UnkE("复杂表达式", off); }
                i++;
                return inner;
            }
            case TsTokKind.Punct when tok.Text == "{": return ObjectLiteral(ref i);
            case TsTokKind.Punct when tok.Text == "[":
            {
                var close = MatchClose(i);
                i = close < 0 ? tokens.Count : close + 1;
                return new UnkE("数组", off);
            }
            default:
                i++;
                return new UnkE("无法解析: " + tok.Text, off);
        }
    }

    private TsExpr Template(TsToken tok, int off)
    {
        var parts = new List<object>();
        foreach (var part in tok.Parts!)
        {
            if (part is string s) { parts.Add(s); continue; }
            var hole = (TsTemplateHole)part;
            var sub = new TsParser(hole.Tokens, text, offsetBias);
            var j = 0;
            var expr = hole.Tokens.Count == 0 ? new UnkE("空插值", off) : sub.ParseExpression(ref j);
            if (j < hole.Tokens.Count) expr = new UnkE("复杂表达式", off);
            parts.Add(new TplHole(expr, hole.Raw));
        }
        return new TplE(parts, off);
    }

    /// <summary>a.b?.c["d"]!.e(…)：调用只允许在链尾。</summary>
    private TsExpr Reference(ref int i)
    {
        var start = i;
        var off = Off(i);
        var path = new List<string> { tokens[i].Text };
        i++;
        while (true)
        {
            if ((IsPunct(i, ".") || IsPunct(i, "?.")) && IsIdent(i + 1)) { path.Add(tokens[i + 1].Text); i += 2; continue; }
            if (IsPunct(i, "!") && (IsPunct(i + 1, ".") || IsPunct(i + 1, "?."))) { i++; continue; }
            if (IsPunct(i, "[") || (IsPunct(i, "?.") && IsPunct(i + 1, "[")))
            {
                if (IsPunct(i, "?.")) i++;
                if (i + 2 < tokens.Count && tokens[i + 1].Kind is TsTokKind.Str or TsTokKind.Num && IsPunct(i + 2, "]")) { path.Add(tokens[i + 1].Text); i += 3; continue; }
                var close = MatchClose(i);
                i = close < 0 ? tokens.Count : close + 1;
                SkipToTerminator(ref i);
                return new UnkE("动态下标: " + Raw(start, i), off);
            }
            break;
        }
        var called = false;
        if (IsPunct(i, "(") || (IsPunct(i, "?.") && IsPunct(i + 1, "(")))
        {
            if (IsPunct(i, "?.")) i++;
            var close = MatchClose(i);
            i = close < 0 ? tokens.Count : close + 1;
            called = true;
            if (IsPunct(i, ".") || IsPunct(i, "?.") || IsPunct(i, "[") || IsPunct(i, "("))
            {
                SkipToTerminator(ref i);
                return new UnkE("调用结果: " + Raw(start, i), off);
            }
        }
        var raw = called ? string.Join('.', path) + "()" : Raw(start, i);
        return new RefE(path, called, raw, off);
    }

    private TsExpr ObjectLiteral(ref int i)
    {
        var off = Off(i);
        var close = MatchClose(i);
        if (close < 0) { i = tokens.Count; return new UnkE("花括号不配对", off); }
        var members = new Dictionary<string, TsExpr>(StringComparer.Ordinal);
        var spreads = new List<TsExpr>();
        i++;
        while (i < close)
        {
            if (IsPunct(i, ",")) { i++; continue; }
            if (IsPunct(i, "...")) { i++; spreads.Add(ParseExpression(ref i)); SkipMember(ref i, close); continue; }
            string? key = null;
            if (tokens[i].Kind is TsTokKind.Ident or TsTokKind.Str or TsTokKind.Num)
            {
                // get x() / set x(v) / async x()：修饰词后面跟名字
                if (tokens[i].Kind == TsTokKind.Ident && tokens[i].Text is "get" or "set" or "async" && i + 1 < close && tokens[i + 1].Kind is TsTokKind.Ident or TsTokKind.Str) i++;
                key = tokens[i].Text;
                i++;
            }
            else if (IsPunct(i, "["))
            {
                var c = MatchClose(i);
                i = c < 0 ? close : c + 1;
            }
            else { SkipMember(ref i, close); continue; }

            if (IsPunct(i, "?")) i++;
            if (IsPunct(i, ":"))
            {
                i++;
                var value = ParseExpression(ref i);
                if (key is not null) members[key] = value;
            }
            else if (IsPunct(i, "(") || IsPunct(i, "<"))
            {
                if (key is not null) members[key] = new FnE([], null, Off(i)); // 方法：函数体不求值
            }
            else if (key is not null && (IsPunct(i, ",") || i == close))
            {
                members[key] = new RefE([key], false, key, Off(i - 1)); // 简写 { key }
            }
            SkipMember(ref i, close);
        }
        i = close + 1;
        return new ObjE(members, spreads, off);
    }

    /// <summary>跳到对象成员的分隔逗号（同层）或对象结尾。</summary>
    private void SkipMember(ref int i, int close)
    {
        while (i < close && !IsPunct(i, ","))
        {
            if (tokens[i].Kind == TsTokKind.Punct && tokens[i].Text is "(" or "[" or "{")
            {
                var c = MatchClose(i);
                i = c < 0 ? close : c + 1;
                continue;
            }
            i++;
        }
    }

    private TsExpr ArrowBody(List<string> ps, ref int i, int off)
    {
        if (IsPunct(i, "{"))
        {
            var close = MatchClose(i);
            i = close < 0 ? tokens.Count : close + 1;
            return new FnE(ps, null, off);
        }
        if (i >= tokens.Count) return new FnE(ps, null, off);
        return new FnE(ps, ParseExpression(ref i), off);
    }

    /// <summary>(…): 返回类型 =>：从冒号起跳过类型找到同层的 =>，返回它的下标。</summary>
    private int? ArrowAfterReturnType(int colon)
    {
        var d = 0;
        for (var j = colon + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Kind != TsTokKind.Punct) continue;
            var p = tokens[j].Text;
            if (p is "(" or "[" or "{" or "<") d++;
            else if (p is ")" or "]" or "}" or ">") { if (d == 0) return null; d--; }
            else if (d == 0 && p == "=>") return j;
            else if (d == 0 && p is ";" or "," or "=") return null;
        }
        return null;
    }

    /// <summary>形参名：每个逗号分段的第一个标识符（解构参数记成 _）。</summary>
    private List<string> Params(int from, int to)
    {
        var ps = new List<string>();
        var d = 0;
        var segmentStart = true;
        for (var j = from; j < to; j++)
        {
            var t = tokens[j];
            if (t.Kind == TsTokKind.Punct)
            {
                if (t.Text is "(" or "[" or "{" or "<") { if (d == 0 && segmentStart && t.Text is "{" or "[") { ps.Add("_"); segmentStart = false; } d++; }
                else if (t.Text is ")" or "]" or "}" or ">") d--;
                else if (t.Text == "," && d == 0) segmentStart = true;
                continue;
            }
            if (d == 0 && segmentStart && t.Kind == TsTokKind.Ident)
            {
                ps.Add(t.Text);
                segmentStart = false;
            }
        }
        return ps;
    }

    /// <summary>类型注解：跳到同层的结束符（, ) ] } ; = + ? :）。</summary>
    private void SkipType(ref int i)
    {
        var d = 0;
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TsTokKind.Punct) continue;
            if (t.Text is "(" or "[" or "{" or "<") d++;
            else if (t.Text is ")" or "]" or "}" or ">") { if (d == 0) return; d--; }
            else if (d == 0 && t.Text is "," or ";" or "=" or "+" or "?" or ":" or "=>") return;
        }
    }

    /// <summary>跳到同层的结束符（, ) ] } ;），不越过外层括号。</summary>
    private void SkipToTerminator(ref int i)
    {
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == TsTokKind.Punct)
            {
                if (t.Text is "(" or "[" or "{")
                {
                    var c = MatchClose(i);
                    i = c < 0 ? tokens.Count : c + 1;
                    continue;
                }
                if (Terminators.Contains(t.Text)) return;
            }
            i++;
        }
    }

    /// <summary>( [ { 的配对下标；不配对 → -1。</summary>
    public int MatchClose(int open) => MatchClose(tokens, open);

    public static int MatchClose(List<TsToken> tokens, int open)
    {
        var d = 0;
        for (var j = open; j < tokens.Count; j++)
        {
            if (tokens[j].Kind != TsTokKind.Punct) continue;
            var p = tokens[j].Text;
            if (p is "(" or "[" or "{") d++;
            else if (p is ")" or "]" or "}") { d--; if (d == 0) return j; }
        }
        return -1;
    }
}
