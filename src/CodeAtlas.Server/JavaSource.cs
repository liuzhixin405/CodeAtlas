using System.Text;
using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

/// <summary>
/// Java 源码文本的小工具（入口规则与出口识别共用）：注解挂到声明行、注解实参取值、字符串常量、方法签名文本。
/// 符号的 StartLine 是声明行（不含注解）；注解在上方，可能跨多行（括号配平为止），也可能与声明同一行（@GetMapping("/x") public String x()）。
/// 不做完整词法：块注释按行首 /* … */ 跳过，字符串里的 "*/" 之类少见情形会切歪，规则按"拿不到就不算"处理。
/// </summary>
internal static partial class JavaSource
{
    /// <param name="Name">简名（javax.ws.rs.GET → GET）。</param>
    /// <param name="Args">括号里的原文；没有括号为 null。</param>
    internal sealed record Annotation(string Name, string? Args, int Line);

    /// <summary>一个文件的注解索引：声明行 → 它的注解；注解所在行 → 它挂到的声明行（StartLine 落在注解行上时用）。</summary>
    internal sealed class FileAnnotations(Dictionary<int, List<Annotation>> byDecl, Dictionary<int, int> declOfAnnotationLine)
    {
        public static readonly FileAnnotations Empty = new([], []);

        /// <summary>声明行 startLine 的注解；StartLine 若指在注解行上，取那组注解挂到的声明；都没有时往上看 3 行（修饰符与名字分行的声明）。</summary>
        public IReadOnlyList<Annotation> Of(int startLine)
        {
            if (byDecl.TryGetValue(startLine, out var list)) return list;
            if (declOfAnnotationLine.TryGetValue(startLine, out var decl) && byDecl.TryGetValue(decl, out var viaBlock)) return viaBlock;
            for (var k = startLine - 1; k >= startLine - 3 && k > 0; k--)
                if (byDecl.TryGetValue(k, out var above)) return above;
            return [];
        }
    }

    [GeneratedRegex(@"^@\s*([A-Za-z_][\w.]*)")]
    private static partial Regex AnnotationHead();
    // private static final String API = "/api";   String hostname = "http://visits-service/";
    [GeneratedRegex(@"\bString\s+(\w+)\s*=\s*""((?:[^""\\]|\\.)*)""\s*;")]
    private static partial Regex StringConstant();
    [GeneratedRegex(@"^[A-Za-z_][\w.]*$")]
    private static partial Regex Identifier();

    internal static bool IsJava(string path) => path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);

    /// <summary>按文件顺扫：行首的注解（括号跨行时配平为止）攒起来，挂到下一个声明行（第一个不是注解 / 注释 / 空行的行）；与声明同一行的前导注解也算这个声明的。</summary>
    public static FileAnnotations Scan(string[] lines)
    {
        var byDecl = new Dictionary<int, List<Annotation>>();
        var blockLines = new Dictionary<int, int>();
        var pending = new List<Annotation>();
        var pendingLines = new List<int>();
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (inComment)
            {
                var end = t.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) continue;
                inComment = false;
                t = t[(end + 2)..].Trim();
            }
            if (t.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = t.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0) { inComment = true; continue; }
                t = t[(end + 2)..].Trim();
            }
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) continue;

            var first = i;
            var inline = new List<Annotation>();
            while (t.StartsWith('@') && !t.StartsWith("@interface", StringComparison.Ordinal))
            {
                var head = AnnotationHead().Match(t);
                if (!head.Success) break;
                var name = head.Groups[1].Value;
                var at = head.Length;
                while (at < t.Length && char.IsWhiteSpace(t[at])) at++;
                string? args = null;
                if (at < t.Length && t[at] == '(')
                {
                    var close = ArgParser.MatchClose(t, at);
                    while (close < 0 && i + 1 < lines.Length && i - first < 30) { t += "\n" + lines[++i]; close = ArgParser.MatchClose(t, at); }
                    if (close < 0) break;
                    args = t[(at + 1)..close];
                    at = close + 1;
                }
                inline.Add(new Annotation(name[(name.LastIndexOf('.') + 1)..], args, first + 1));
                t = t[at..].TrimStart();
            }
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
            {
                // 整行都是注解：挂到后面的声明上
                pending.AddRange(inline);
                for (var k = first; k <= i; k++) pendingLines.Add(k + 1);
                continue;
            }
            var decl = i + 1;
            byDecl[decl] = [.. pending, .. inline];
            foreach (var l in pendingLines) blockLines[l] = decl;
            pending = [];
            pendingLines = [];
        }
        return new FileAnnotations(byDecl, blockLines);
    }

    /// <summary>文件里的字符串常量 / 字段：名字 → 值（同名多次赋值取第一个）。</summary>
    public static Dictionary<string, string> StringConstants(string[] lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lines)
            foreach (Match m in StringConstant().Matches(l))
                map.TryAdd(m.Groups[1].Value, Unescape(m.Groups[2].Value));
        return map;
    }

    /// <summary>
    /// 注解实参里的字符串值：依次取 keys 里的关键字参数，没有就取第一个位置参数；值是数组 {"a", "b"} 时每项一个。
    /// 每项按 <see cref="Eval"/> 求值，求不出来的项给 null（调用方决定跳过还是用原文）。没有实参 → 空表。
    /// </summary>
    public static List<(string Raw, string? Value)> Values(string? args, Func<string, string?> constant, params string[] keys)
    {
        if (string.IsNullOrWhiteSpace(args)) return [];
        var (parts, _) = ArgParser.SplitArgs("(" + args + ")", 0);
        string? expr = null;
        foreach (var k in keys)
            if ((expr = ArgParser.Keyword(parts, k)) is not null) break;
        expr ??= parts.FirstOrDefault(p => !ArgParser.IsKeyword(p) && p.Length > 0);
        if (expr is null) return [];
        expr = expr.Trim();
        var items = new List<string>();
        if (expr.StartsWith('{'))
        {
            var (inner, _) = ArgParser.SplitArgs(expr, 0);
            items.AddRange(inner.Where(x => x.Length > 0));
        }
        else items.Add(expr);
        return items.Select(x => (x, Eval(x, constant))).ToList();
    }

    private static readonly Dictionary<string, string?> SpringMappingVerbs = new(StringComparer.Ordinal)
    {
        ["GetMapping"] = "GET", ["PostMapping"] = "POST", ["PutMapping"] = "PUT", ["DeleteMapping"] = "DELETE", ["PatchMapping"] = "PATCH", ["RequestMapping"] = null,
    };

    [GeneratedRegex(@"\b(?:RequestMethod\s*\.\s*)?(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\b")]
    private static partial Regex RequestMethodToken();

    public static bool IsSpringMapping(Annotation a) => SpringMappingVerbs.ContainsKey(a.Name);

    /// <summary>
    /// Spring 的映射注解（@GetMapping … / @RequestMapping）→ (路径, HTTP 方法) 组合：value / path 是数组时每条一个，没写路径是 ""；
    /// @RequestMapping 的 method = RequestMethod.X（可为数组）每个一个，没写是 null（任意方法）。求不出来的路径表达式跳过（不猜）。
    /// </summary>
    public static List<(string Path, string? Verb)> SpringMapping(Annotation a, Func<string, string?> constant)
    {
        if (!SpringMappingVerbs.TryGetValue(a.Name, out var fixedVerb)) return [];
        var values = Values(a.Args, constant, "value", "path");
        var paths = values.Count == 0 ? [""] : values.Where(v => v.Value is not null).Select(v => v.Value!).ToList();
        List<string?> verbs = fixedVerb is not null ? [fixedVerb]
            : Keyword(a.Args, "method") is { } ms ? RequestMethodToken().Matches(ms).Select(x => (string?)x.Groups[1].Value).Distinct().ToList() : [];
        if (verbs.Count == 0) verbs = [null];
        return paths.SelectMany(p => verbs.Select(v => (p, v))).ToList();
    }

    /// <summary>注解实参里关键字参数的原文（method = RequestMethod.POST → "RequestMethod.POST"）；没有返回 null。</summary>
    public static string? Keyword(string? args, string name)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        var (parts, _) = ArgParser.SplitArgs("(" + args + ")", 0);
        return ArgParser.Keyword(parts, name);
    }

    /// <summary>
    /// 字符串表达式求值：顶层按 + 切开，字面量取内容，标识符（API、Paths.API、this.hostname）按 constant 查；
    /// holes = true 时查不到的项（变量、方法调用）写成 ${名字} 孔（URL 里的路径参数），否则整个返回 null。
    /// </summary>
    public static string? Eval(string expr, Func<string, string?> constant, bool holes = false)
    {
        var sb = new StringBuilder();
        foreach (var raw in SplitTop(expr.Trim(), '+'))
        {
            var p = raw.Trim();
            if (p.Length >= 2 && p[0] == '"' && p[^1] == '"' && !p[1..^1].Replace("\\\"", "").Contains('"')) { sb.Append(Unescape(p[1..^1])); continue; }
            if (p.StartsWith("this.", StringComparison.Ordinal)) p = p[5..];
            if (Identifier().IsMatch(p) && constant(p[(p.LastIndexOf('.') + 1)..]) is { } value) { sb.Append(value); continue; }
            if (!holes) return null;
            var name = Regex.Match(p, @"(\w+)\s*(?:\(.*\))?\s*$");
            sb.Append("${").Append(name.Success ? name.Groups[1].Value : "x").Append('}');
        }
        return sb.ToString();
    }

    /// <summary>顶层（不在括号 / 字符串里）按分隔符切。</summary>
    public static List<string> SplitTop(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is not null)
            {
                if (ch == '\\') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            if (ch is '"' or '\'') quote = ch;
            else if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == separator && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>方法签名文本：从声明行起到参数表的右括号（最多 12 行），用来看 static / StreamObserver / 参数类型。</summary>
    public static string Signature(string[] lines, int startLine, string name)
    {
        var sb = new StringBuilder();
        for (var i = startLine - 1; i >= 0 && i < lines.Length && i < startLine + 11; i++)
        {
            sb.Append(lines[i]).Append('\n');
            var text = sb.ToString();
            var at = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*\(");
            if (at.Success && ArgParser.MatchClose(text, at.Index + at.Length - 1) is var close and >= 0) return text[..(close + 1)];
        }
        return sb.ToString();
    }

    /// <summary>签名里第一个参数的类型简名（去掉注解、final、泛型与包名）：onStarted(ApplicationReadyEvent event) → ApplicationReadyEvent。</summary>
    public static string? FirstParameterType(string signature, string name)
    {
        var at = Regex.Match(signature, $@"\b{Regex.Escape(name)}\s*\(");
        if (!at.Success) return null;
        var (args, _) = ArgParser.SplitArgs(signature, at.Index + at.Length - 1);
        if (args.Count == 0 || args[0].Length == 0) return null;
        var p = Regex.Replace(args[0], @"@[\w.]+(?:\s*\([^)]*\))?", " ");
        p = Regex.Replace(p, @"\bfinal\b", " ");
        p = Regex.Replace(p, @"<.*>", "");
        var words = p.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        var type = words[^2].TrimEnd('.');
        return type[(type.LastIndexOf('.') + 1)..].Replace("[]", "");
    }

    /// <summary>类型声明的头（从声明行到第一个 '{'，最多 6 行），看 extends / implements。</summary>
    public static string TypeHeader(string[] lines, int startLine)
    {
        var sb = new StringBuilder();
        for (var i = startLine - 1; i >= 0 && i < lines.Length && i < startLine + 5; i++)
        {
            var l = lines[i];
            var brace = l.IndexOf('{');
            if (brace >= 0) { sb.Append(l[..brace]); break; }
            sb.Append(l).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>lowerCamel → PascalCase（Java 的 gRPC 方法 getAds 对 proto 的 GetAds）。</summary>
    public static string Pascal(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");
}
