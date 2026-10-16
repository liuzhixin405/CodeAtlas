using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>Module 只用作用法记号（Python 模块顶层代码按模块名找 import），切分不产出。</summary>
internal enum MemberKind { Type, Method, Property, Module }

/// <summary>一个有名字的成员（类型 / 方法 / 属性）在文件里的范围。行号 1 起；NameLine 是名字所在行（声明头可能跨行）。</summary>
internal sealed record MemberSpan(string Name, MemberKind Kind, int NameLine, int StartLine, int EndLine, bool IsPublic, bool IsOverride);

/// <summary>
/// 纯文本的成员切分（不编译）：在剥掉注释与字符串的代码上数花括号，C# / Java / Go / JS 按声明头认类型与方法，Python 按缩进。
/// 只服务于"这一行在哪个方法里、谁调了这个方法"这种粗判；认不出的块（lambda、初始化器、命名空间）当匿名块，不切断外层成员。
/// </summary>
internal static class MemberIndex
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "catch", "try", "finally", "using", "lock", "fixed", "return", "new", "nameof", "typeof",
        "sizeof", "default", "checked", "unchecked", "await", "throw", "yield", "when", "base", "this", "super", "synchronized", "function", "select", "from", "where",
        "get", "set", "init", "add", "remove", "go", "defer", "func", "match", "with", "elif", "lambda", "unsafe", "stackalloc",
    };

    private static readonly Regex Attributes = new(@"^\s*(?:\[[^\[\]]*(?:\[[^\[\]]*\][^\[\]]*)*\]\s*)+", RegexOptions.Compiled);
    private static readonly Regex JavaAnnotations = new(@"@\w+(?:\.\w+)*(?:\s*\((?:[^()]|\([^()]*\))*\))?", RegexOptions.Compiled);
    private static readonly Regex TypeDecl = new(@"\b(?:class|interface|struct|record|enum|trait)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex GoFunc = new(@"^\s*func\s*(?:\(\s*\w*\s*\*?\s*[\w.]+(?:\[[^\]]*\])?\s*\)\s*)?([A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled);
    private static readonly Regex GoType = new(@"^\s*type\s+([A-Za-z_]\w*)\b", RegexOptions.Compiled);
    private static readonly Regex JsFunction = new(@"\bfunction\s*\*?\s*([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex JsArrowConst = new(@"\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|(?:\([^()]*(?:\([^()]*\)[^()]*)*\)|[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=>)", RegexOptions.Compiled);
    private static readonly Regex JsObjectMethod = new(@"(?:^|[,{]\s*)([A-Za-z_$][\w$]*)\s*:\s*(?:async\s+)?(?:function\b|\([^()]*\)\s*=>)", RegexOptions.Compiled);
    private static readonly Regex PyDef = new(@"^(\s*)(?:async\s+)?def\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PyClass = new(@"^(\s*)class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PropertyHeader = new(@"^[\w\s<>\[\],.?:()]*?\b([A-Za-z_]\w*)\s*$", RegexOptions.Compiled);

    public static List<MemberSpan> Parse(string[] code, SourceLang lang) =>
        lang == SourceLang.Python ? ParsePython(code) : ParseBraces(code, lang);

    private sealed record Block(string? Name, MemberKind Kind, int NameLine, int StartLine, bool IsPublic, bool IsOverride);

    private static List<MemberSpan> ParseBraces(string[] code, SourceLang lang)
    {
        var spans = new List<MemberSpan>();
        var stack = new Stack<Block>();
        var header = new StringBuilder();
        var headerLine = 0;     // header 第一个字符所在行（0 起）
        for (var li = 0; li < code.Length; li++)
        {
            var line = code[li];
            for (var ci = 0; ci < line.Length; ci++)
            {
                var ch = line[ci];
                if (ch == '{')
                {
                    var top = stack.Count > 0 ? stack.Peek() : null;
                    var block = Classify(header.ToString(), headerLine, li, lang, top);
                    stack.Push(block);
                    header.Clear(); headerLine = li;
                }
                else if (ch == '}')
                {
                    if (stack.Count > 0)
                    {
                        var b = stack.Pop();
                        if (b.Name is not null) spans.Add(new MemberSpan(b.Name, b.Kind, b.NameLine + 1, b.StartLine + 1, li + 1, b.IsPublic, b.IsOverride));
                    }
                    header.Clear(); headerLine = li;
                }
                else if (ch == ';')
                {
                    // 没有花括号体的成员：C# 表达式体（=> …;）、接口 / 抽象方法声明（…);）、JS 的 const f = () => …;
                    var top = stack.Count > 0 ? stack.Peek() : null;
                    if (BodylessMember(header.ToString(), headerLine, lang, top) is { } m) spans.Add(m with { EndLine = li + 1 });
                    header.Clear(); headerLine = li;
                }
                else
                {
                    if (header.Length == 0 && char.IsWhiteSpace(ch)) continue;
                    if (header.Length == 0) headerLine = li;
                    header.Append(ch);
                }
            }
            if (header.Length > 0) header.Append('\n');
        }
        return spans;
    }

    /// <summary>header 里第 index 个字符在哪一行（0 起）。</summary>
    private static int LineAt(string header, int index, int headerLine)
    {
        var n = 0;
        for (var i = 0; i < index && i < header.Length; i++) if (header[i] == '\n') n++;
        return headerLine + n;
    }

    private static string StripDecorations(string header, SourceLang lang)
    {
        // 特性 / 注解换成等长空格，下标仍能对回原 header
        string Blank(Match m) => new(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray());
        if (lang == SourceLang.CSharp) header = Attributes.Replace(header, Blank);
        if (lang is SourceLang.Java or SourceLang.JavaScript) header = JavaAnnotations.Replace(header, Blank);
        return header;
    }

    private static Block Classify(string raw, int headerLine, int braceLine, SourceLang lang, Block? top)
    {
        var header = StripDecorations(raw, lang);
        Block Anonymous() => new(null, MemberKind.Method, 0, braceLine, false, false);
        if (header.Trim().Length == 0) return Anonymous();

        if (lang == SourceLang.Go)
        {
            if (GoFunc.Match(header) is { Success: true } gf) return new Block(gf.Groups[1].Value, MemberKind.Method, LineAt(header, gf.Groups[1].Index, headerLine), headerLine, char.IsUpper(gf.Groups[1].Value[0]), false);
            if (GoType.Match(header) is { Success: true } gt) return new Block(gt.Groups[1].Value, MemberKind.Type, LineAt(header, gt.Groups[1].Index, headerLine), headerLine, char.IsUpper(gt.Groups[1].Value[0]), false);
            return Anonymous();
        }

        var td = TypeDecl.Match(header);
        if (td.Success && !header[..td.Index].Contains('(') && !header.Contains("=>", StringComparison.Ordinal))
            return new Block(td.Groups[1].Value, MemberKind.Type, LineAt(header, td.Groups[1].Index, headerLine), headerLine, IsPublicHeader(header[..td.Index], lang, top), false);

        if (lang == SourceLang.JavaScript)
        {
            if (JsArrowConst.Match(header) is { Success: true } ac) return Named(ac.Groups[1]);
            if (JsFunction.Match(header) is { Success: true } jf) return Named(jf.Groups[1]);
            if (JsObjectMethod.Match(header) is { Success: true } om) return Named(om.Groups[1]);
            if (header.Contains("=>", StringComparison.Ordinal)) return Anonymous();
            // 类里的方法：name(args) { / async name(args): T {
            if (top is { Kind: MemberKind.Type } && MethodHeader(header, lang, allowEmptyPrefix: true) is { } jm) return Named(jm);
            return Anonymous();
        }

        if (header.Contains("=>", StringComparison.Ordinal)) return Anonymous();
        if (MethodHeader(header, lang, allowEmptyPrefix: top is { Kind: MemberKind.Type }) is { } g)
        {
            // Java / C# 构造函数可以没有修饰符；其余空前缀的必须在类型体里
            return Named(g);
        }
        // C# 属性：public string Name { get; set; }
        if (lang == SourceLang.CSharp && top is { Kind: MemberKind.Type } && !header.Contains('=') && PropertyHeader.Match(header.Trim()) is { Success: true } pm
            && !Keywords.Contains(pm.Groups[1].Value) && header.Trim().Contains(' '))
        {
            var idx = header.LastIndexOf(pm.Groups[1].Value, StringComparison.Ordinal);
            return new Block(pm.Groups[1].Value, MemberKind.Property, LineAt(header, idx, headerLine), headerLine, IsPublicHeader(header, lang, top), false);
        }
        return Anonymous();

        Block Named(Group name) => new(name.Value, MemberKind.Method, LineAt(header, name.Index, headerLine), headerLine, IsPublicHeader(header[..name.Index], lang, top),
            Regex.IsMatch(header[..name.Index], @"\boverride\b"));
    }

    /// <summary>
    /// 方法声明头：第一个顶层 ( 前面是名字（可带泛型参数），名字前面是修饰符 / 返回类型（不能以 . 结尾、不能有 = / new / return），
    /// ) 后面只能是 : base(…) / where … / throws … / 返回类型注解。是声明返回名字所在的 Group。
    /// </summary>
    private static Group? MethodHeader(string header, SourceLang lang, bool allowEmptyPrefix)
    {
        // 泛型参数里可以有特性与括号：AddSubscription<T, [DynamicallyAccessedMembers(…)] TH>(…)
        var m = Regex.Match(header, @"([A-Za-z_$][\w$]*)\s*(?:<(?:[^<>;{}]|<(?:[^<>;{}]|<[^<>;{}]*>)*>)*>)?\s*\(");
        if (!m.Success) return null;
        var name = m.Groups[1];
        if (Keywords.Contains(name.Value)) return null;
        var prefix = header[..name.Index];
        var trimmed = prefix.TrimEnd();
        if (trimmed.EndsWith('.') || trimmed.EndsWith("?.", StringComparison.Ordinal)) return null;
        if (Regex.IsMatch(prefix, @"=|\bnew\b|\breturn\b|\bawait\b|\bthrow\b|[(,!&|+\-*/%]")) return null;
        if (trimmed.Length == 0 && !allowEmptyPrefix) return null;
        // 括号配平到 )，看后面
        var open = m.Index + m.Length - 1;
        var depth = 0;
        var close = -1;
        for (var i = open; i < header.Length; i++)
        {
            if (header[i] == '(') depth++;
            else if (header[i] == ')' && --depth == 0) { close = i; break; }
        }
        if (close < 0) return null;
        var rest = header[(close + 1)..].Trim();
        if (rest.Length == 0) return name;
        if (lang == SourceLang.CSharp && Regex.IsMatch(rest, @"^(?::\s*(?:base|this)\s*\(.*\)\s*)?(?:where\b.*)?$", RegexOptions.Singleline)) return name;
        if (lang == SourceLang.Java && Regex.IsMatch(rest, @"^throws\b", RegexOptions.Singleline)) return name;
        if (lang == SourceLang.JavaScript && rest.StartsWith(':')) return name;
        return null;
    }

    private static MemberSpan? BodylessMember(string raw, int headerLine, SourceLang lang, Block? top)
    {
        var header = StripDecorations(raw, lang);
        if (header.Trim().Length == 0) return null;
        if (lang == SourceLang.JavaScript)
        {
            if (JsArrowConst.Match(header) is { Success: true } ac)
                return new MemberSpan(ac.Groups[1].Value, MemberKind.Method, LineAt(header, ac.Groups[1].Index, headerLine) + 1, headerLine + 1, 0, true, false);
            return null;
        }
        if (lang is not (SourceLang.CSharp or SourceLang.Java) || top is not { Kind: MemberKind.Type }) return null;
        var arrow = header.IndexOf("=>", StringComparison.Ordinal);
        var head = arrow >= 0 ? header[..arrow] : header;
        if (MethodHeader(head, lang, allowEmptyPrefix: true) is { } g)
            return new MemberSpan(g.Value, MemberKind.Method, LineAt(header, g.Index, headerLine) + 1, headerLine + 1, 0, IsPublicHeader(head[..g.Index], lang, top) || top is { IsPublic: true, Name: not null } && arrow < 0,
                Regex.IsMatch(head[..g.Index], @"\boverride\b"));
        // 表达式体属性：public int Count => _items.Count;
        if (arrow >= 0 && !head.Contains('=') && PropertyHeader.Match(head.Trim()) is { Success: true } pm && !Keywords.Contains(pm.Groups[1].Value) && head.Trim().Contains(' '))
        {
            var idx = head.LastIndexOf(pm.Groups[1].Value, StringComparison.Ordinal);
            return new MemberSpan(pm.Groups[1].Value, MemberKind.Property, LineAt(header, idx, headerLine) + 1, headerLine + 1, 0, IsPublicHeader(head, lang, top), false);
        }
        return null;
    }

    private static bool IsPublicHeader(string prefix, SourceLang lang, Block? top) => lang switch
    {
        SourceLang.CSharp or SourceLang.Java => Regex.IsMatch(prefix, @"\bpublic\b") || (top is { Kind: MemberKind.Type } && !Regex.IsMatch(prefix, @"\b(?:private|protected|internal)\b") && lang == SourceLang.Java),
        SourceLang.JavaScript => !Regex.IsMatch(prefix, @"\b(?:private|protected)\b|#"),
        _ => true,
    };

    private static List<MemberSpan> ParsePython(string[] code)
    {
        var spans = new List<MemberSpan>();
        for (var i = 0; i < code.Length; i++)
        {
            var def = PyDef.Match(code[i]);
            var cls = def.Success ? null : PyClass.Match(code[i]);
            var m = def.Success ? def : cls is { Success: true } ? cls : null;
            if (m is null) continue;
            var indent = m.Groups[1].Value.Length;
            var end = i;
            for (var k = i + 1; k < code.Length; k++)
            {
                if (code[k].Trim().Length == 0) continue;
                if (code[k].Length - code[k].TrimStart().Length <= indent) break;
                end = k;
            }
            var name = m.Groups[2].Value;
            spans.Add(new MemberSpan(name, def.Success ? MemberKind.Method : MemberKind.Type, i + 1, i + 1, end + 1, !name.StartsWith('_') || (name.StartsWith("__") && name.EndsWith("__")), false));
        }
        return spans;
    }
}
