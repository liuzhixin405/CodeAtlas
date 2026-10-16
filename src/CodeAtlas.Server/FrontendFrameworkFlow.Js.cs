using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

/// <summary>前端框架流程用的 JS / TS / JSX 文本小工具：去注释与字符串（模板字符串、JSX 文本里的撇号）、小型 JSX 解析、对象字面量的顶层键、模块顶层调用。</summary>
public static partial class FrontendFrameworkFlow
{
    /// <summary>一个源文件：原文 Raw（取字符串字面量的值用）与去掉注释 / 字符串内容的 S（认结构用），两者偏移一致。</summary>
    internal sealed class JsFile
    {
        public string Path { get; }
        public string Raw { get; }
        public string S { get; }
        private readonly List<int> lineStarts = [0];

        public JsFile(string path, string raw)
        {
            Path = path;
            Raw = raw;
            S = StripJs(raw);
            for (var i = 0; i < raw.Length; i++) if (raw[i] == '\n') lineStarts.Add(i + 1);
        }

        /// <summary>偏移所在的行（1 起）。</summary>
        public int LineOf(int pos)
        {
            var i = lineStarts.BinarySearch(pos);
            return i >= 0 ? i + 1 : ~i;
        }

        /// <summary>第 startLine..endLine 行（含）的 [起, 止)。</summary>
        public (int Start, int End) Span(int startLine, int endLine)
        {
            var s = startLine <= 1 ? 0 : startLine - 1 < lineStarts.Count ? lineStarts[startLine - 1] : Raw.Length;
            var e = endLine <= 0 || endLine >= lineStarts.Count ? Raw.Length : lineStarts[endLine];
            return (s, Math.Max(s, e));
        }

        /// <summary>原文里 [s, e) 的一段，空白压成一个空格、超长截断（写进 Note 用）。</summary>
        public string Snippet(int s, int e, int max = 60)
        {
            s = Math.Clamp(s, 0, Raw.Length);
            e = Math.Clamp(e, s, Raw.Length);
            var t = Regex.Replace(Raw[s..e], @"\s+", " ").Trim();
            return t.Length <= max ? t : t[..(max - 1)] + "…";
        }

        /// <summary>S 里 pos 处的字符串字面量（引号在 S 里保留）在原文里的内容；不是字符串返回 null。</summary>
        public string? StringAt(int pos)
        {
            pos = FrameworkFlowBuilder.SkipWs(S, pos);
            if (pos >= S.Length || S[pos] is not ('"' or '\'' or '`')) return null;
            var q = S[pos];
            var end = S.IndexOf(q, pos + 1);
            return end < 0 ? null : Raw[(pos + 1)..end];
        }
    }

    /// <summary>
    /// 注释与字符串内容换成空格（换行与引号保留，偏移不变）。与 C# 版的区别：模板字符串整段（含 ${…}）抹掉；
    /// 引号前紧挨字母 / 数字时当成 JSX 文本里的撇号（Don't），不算字符串；':' 后紧跟的 // 是网址（https://），不算注释。
    /// </summary>
    internal static string StripJs(string text)
    {
        var c = text.ToCharArray();
        void Blank(int from, int to) { for (var k = from; k < to && k < c.Length; k++) if (c[k] is not ('\n' or '\r')) c[k] = ' '; }
        var i = 0;
        while (i < c.Length)
        {
            var ch = text[i];
            var prev = i > 0 ? text[i - 1] : ' ';
            if (ch == '/' && i + 1 < c.Length && text[i + 1] == '/' && prev != ':')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(i, e); i = e; continue;
            }
            if (ch == '/' && i + 1 < c.Length && text[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 2;
                Blank(i, e); i = e; continue;
            }
            if (ch == '`')
            {
                var j = i + 1;
                var depth = 0;
                while (j < c.Length)
                {
                    if (text[j] == '\\') { j += 2; continue; }
                    if (depth == 0 && text[j] == '`') break;
                    if (text[j] == '$' && j + 1 < c.Length && text[j + 1] == '{') { depth++; j += 2; continue; }
                    if (depth > 0 && text[j] == '}') depth--;
                    j++;
                }
                Blank(i + 1, j); i = j + 1; continue;
            }
            if ((ch == '"' || ch == '\'') && !char.IsLetterOrDigit(prev))
            {
                var j = i + 1;
                while (j < c.Length && text[j] != ch && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                Blank(i + 1, j); i = j + 1; continue;
            }
            i++;
        }
        return new string(c);
    }

    /// <summary>s[open] 是开括号（( [ {），返回配对的闭括号；三种括号一起计深度。没配上返回 -1。</summary>
    internal static int MatchAny(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}':
                    if (--depth == 0) return i;
                    if (depth < 0) return -1;
                    break;
            }
        }
        return -1;
    }

    /// <summary>对象字面量 s[open..close]（open 是 '{'）的顶层项：键名、键的位置、值的 [起, 止)（简写 { a } 的值就是键本身）。</summary>
    internal static List<(string Key, int KeyPos, int ValueStart, int ValueEnd)> ObjectEntries(string s, int open, int close)
    {
        var result = new List<(string, int, int, int)>();
        if (open < 0 || close <= open || s[open] != '{') return result;
        var start = open + 1;
        var depth = 0;
        for (var i = open + 1; i <= close; i++)
        {
            var ch = s[i];
            if (i < close && ch is '(' or '[' or '{') { depth++; continue; }
            if (i < close && ch is ')' or ']' or '}') { depth--; continue; }
            if (i < close && !(ch == ',' && depth == 0)) continue;
            var a = FrameworkFlowBuilder.SkipWs(s, start);
            if (a < i)
            {
                var m = EntryKey().Match(s, a, i - a);
                if (m.Success && m.Index == a)
                {
                    var key = m.Groups["k"].Value;
                    var colon = m.Groups["c"].Success ? m.Index + m.Length : -1;
                    if (colon >= 0) { var vs = Math.Min(FrameworkFlowBuilder.SkipWs(s, colon), i); result.Add((key, a, vs, Math.Max(vs, TrimEnd(s, a, i)))); }
                    else result.Add((key, a, a, TrimEnd(s, a, i)));
                }
            }
            start = i + 1;
        }
        return result;
    }

    private static int TrimEnd(string s, int from, int to)
    {
        while (to > from && char.IsWhiteSpace(s[to - 1])) to--;
        return to;
    }

    [GeneratedRegex(@"(?:async\s+)?(?<k>[A-Za-z_$][\w$]*)\s*(?:(?<c>:)|(?=\()|(?=,|$|\s*$))")]
    private static partial Regex EntryKey();

    /// <summary>一个 JSX 元素：名字（片段为空串）、名字的位置、整个元素的 [Pos, End)、属性段、子元素、子表达式（{children}…）。</summary>
    internal sealed record JsxNode(string Name, int Pos, int End, int AttrStart, int AttrEnd, bool SelfClosing, List<JsxNode> Children, List<(string Text, int Pos)> Exprs)
    {
        /// <summary>小写开头的宿主元素（div / html / body）或片段：不是组件。</summary>
        public bool IsHost => Name.Length == 0 || char.IsLower(Name[0]) && !Name.Contains('.');
        /// <summary>有子内容（元素或 {表达式}）——包着别的东西的外壳。</summary>
        public bool Wraps => !SelfClosing && (Children.Count > 0 || Exprs.Count > 0);
        /// <summary>子内容里有 {children} / {props.children}：把页面原样包进去的外壳。</summary>
        public bool HasChildrenSlot => Exprs.Any(e => ChildrenSlot().IsMatch(e.Text)) || Children.Any(c => c.HasChildrenSlot);
    }

    [GeneratedRegex(@"^(?:this\s*\.\s*)?(?:props\s*\.\s*)?children$")]
    private static partial Regex ChildrenSlot();

    /// <summary>从 s[lt]（'&lt;'）开始解析一个 JSX 元素；不像 JSX（泛型、比较号）或没闭合返回 null。s 应是 <see cref="StripJs"/> 之后的文本。</summary>
    internal static JsxNode? ParseJsx(string s, int lt, int depth = 0)
    {
        if (depth > 60 || lt < 0 || lt >= s.Length || s[lt] != '<') return null;
        var i = lt + 1;
        var nameStart = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$' or '.' or ':' or '-')) i++;
        var name = s[nameStart..i];
        if (name.Length > 0 && !char.IsLetter(name[0]) && name[0] is not ('_' or '$')) return null;
        var attrStart = i;
        // 属性
        while (true)
        {
            if (i >= s.Length) return null;
            var ch = s[i];
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '>')
                return new JsxNode(name, nameStart, i + 2, attrStart, i, true, [], []);
            if (ch == '>') { i++; break; }
            if (ch == '{')
            {
                var close = MatchAny(s, i);
                if (close < 0) return null;
                i = close + 1;
                continue;
            }
            if (ch is '"' or '\'')
            {
                var close = s.IndexOf(ch, i + 1);
                if (close < 0) return null;
                i = close + 1;
                continue;
            }
            if (ch is ';' or '<' or ')' or '}') return null; // 不是 JSX（泛型实参、比较）
            i++;
        }
        var attrEnd = i - 1;
        var children = new List<JsxNode>();
        var exprs = new List<(string, int)>();
        while (i < s.Length)
        {
            var ch = s[i];
            if (ch == '<')
            {
                if (i + 1 < s.Length && s[i + 1] == '/')
                {
                    var gt = s.IndexOf('>', i);
                    if (gt < 0) return null;
                    var closing = s[(i + 2)..gt].Trim();
                    if (closing != name) return null; // 没配上：多半是误判成 JSX 的文本
                    return new JsxNode(name, nameStart, gt + 1, attrStart, attrEnd, false, children, exprs);
                }
                var child = ParseJsx(s, i, depth + 1);
                if (child is null) return null;
                children.Add(child);
                i = child.End;
                continue;
            }
            if (ch == '{')
            {
                var close = MatchAny(s, i);
                if (close < 0) return null;
                var inner = s[(i + 1)..close];
                if (inner.Trim().Length > 0) exprs.Add((inner.Trim(), i + 1));
                // {cond && <X/>}、{list.map(x => <Item/>)}：表达式里的 JSX 也算子元素
                foreach (var nested in JsxIn(s, i + 1, close, depth + 1)) children.Add(nested);
                i = close + 1;
                continue;
            }
            i++;
        }
        return null;
    }

    /// <summary>[start, end) 里能解析出来的 JSX 元素（不重叠，按位置）。</summary>
    internal static List<JsxNode> JsxIn(string s, int start, int end, int depth = 0)
    {
        var list = new List<JsxNode>();
        for (var i = start; i < end; i++)
        {
            if (s[i] != '<' || i + 1 >= end || !(char.IsLetter(s[i + 1]) || s[i + 1] == '>')) continue;
            // 前面是标识符（a<b、useState<T>）的不是 JSX
            var p = i - 1;
            while (p >= start && s[p] == ' ') p--;
            if (p >= 0 && (char.IsLetterOrDigit(s[p]) || s[p] is '_' or '$' or ')' or ']')) continue;
            var node = ParseJsx(s, i, depth);
            if (node is null || node.End > end) continue;
            list.Add(node);
            i = node.End - 1;
        }
        return list;
    }

    /// <summary>元素属性段里 name={…} 的表达式（去掉外层花括号、修剪）与位置；name="…" 给原文里的字符串；没有为 null。</summary>
    internal static (string Text, int Start, int End)? Attr(JsFile f, JsxNode el, string name)
    {
        var m = new Regex(@"(?<![\w$-])" + Regex.Escape(name) + @"\s*=\s*").Match(f.S, el.AttrStart, Math.Max(0, el.AttrEnd - el.AttrStart));
        if (!m.Success) return null;
        var v = m.Index + m.Length;
        if (v >= f.S.Length) return null;
        if (f.S[v] == '{')
        {
            var close = MatchAny(f.S, v);
            return close < 0 ? null : (f.S[(v + 1)..close].Trim(), v + 1, close);
        }
        if (f.S[v] is '"' or '\'') return f.StringAt(v) is { } str ? (str, v + 1, v + 1 + str.Length) : null;
        return null;
    }

    /// <summary>元素的属性名（不含 {...spread}）。</summary>
    internal static List<string> AttrNames(JsFile f, JsxNode el)
    {
        var names = new List<string>();
        var i = el.AttrStart;
        while (i < el.AttrEnd)
        {
            var ch = f.S[i];
            if (ch == '{') { var c = MatchAny(f.S, i); if (c < 0) break; i = c + 1; continue; }
            if (ch is '"' or '\'') { var c = f.S.IndexOf(ch, i + 1); if (c < 0) break; i = c + 1; continue; }
            if (char.IsLetter(ch) || ch == '_')
            {
                var s = i;
                while (i < el.AttrEnd && (char.IsLetterOrDigit(f.S[i]) || f.S[i] is '_' or '-' or ':')) i++;
                names.Add(f.S[s..i]);
                continue;
            }
            i++;
        }
        return names;
    }

    /// <summary>函数体 [start, end) 里组件返回的 JSX：return ( &lt;…&gt; ) 与箭头函数的 =&gt; &lt;…&gt; 都算；有多个时取带 {children} 的，再取最大的。</summary>
    internal static JsxNode? ReturnedJsx(JsFile f, int start, int end)
    {
        JsxNode? best = null;
        foreach (Match m in ReturnJsx().Matches(f.S[..Math.Min(end, f.S.Length)]))
        {
            if (m.Index < start) continue;
            var lt = m.Index + m.Length - 1;
            var node = ParseJsx(f.S, lt);
            if (node is null || node.End > end) continue;
            if (best is null) { best = node; continue; }
            var better = (node.HasChildrenSlot && !best.HasChildrenSlot) || (node.HasChildrenSlot == best.HasChildrenSlot && node.End - node.Pos > best.End - best.Pos);
            if (better) best = node;
        }
        return best;
    }

    [GeneratedRegex(@"(?:\breturn|=>)\s*\(?\s*<")]
    private static partial Regex ReturnJsx();

    /// <summary>一处模块顶层的调用：callee 文本（client.setConfig / new QueryClient / i18n.use().init）、位置、最后一次调用的实参 [起, 止)、赋给的变量。</summary>
    internal sealed record TopCall(string Callee, int Pos, int ArgStart, int ArgEnd, string? AssignedTo);

    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
        { "if", "for", "while", "switch", "catch", "function", "return", "typeof", "import", "super", "yield", "await", "void", "delete", "in", "of", "instanceof", "case", "async", "export", "declare", "satisfies", "as", "else", "do", "try", "throw" };

    /// <summary>
    /// 模块顶层（括号深度 0）的调用语句，[start, end) 内按位置：a.b(...)、new X(...)、a.b(...).c(...)（连着的算一处）。
    /// 跳过 function 声明、顶层箭头函数的表达式体（=&gt; 之后到行尾）。
    /// </summary>
    internal static List<TopCall> TopLevelCalls(string s, int start, int end)
    {
        var result = new List<TopCall>();
        end = Math.Min(end, s.Length);
        var depth = 0;
        var skipLine = false;
        var i = 0;
        while (i < end)
        {
            var ch = s[i];
            if (ch == '\n') { skipLine = false; i++; continue; }
            if (ch is '(' or '[' or '{') { depth++; i++; continue; }
            if (ch is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); i++; continue; }
            if (depth == 0 && ch == '=' && i + 1 < end && s[i + 1] == '>') { skipLine = true; i += 2; continue; }
            if (!(char.IsLetter(ch) || ch is '_' or '$') || (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] is '_' or '$' or '.')))
            {
                i++;
                continue;
            }
            var wordEnd = i;
            while (wordEnd < end && (char.IsLetterOrDigit(s[wordEnd]) || s[wordEnd] is '_' or '$')) wordEnd++;
            if (depth != 0 || skipLine || i < start || NotCalls.Contains(s[i..wordEnd]) || PrevWord(s, i) is "function" or "class" or "interface" or "type")
            {
                i = wordEnd;
                continue;
            }
            // a.b.c
            var j = wordEnd;
            while (true)
            {
                var k = FrameworkFlowBuilder.SkipWs(s, j);
                if (k < end && s[k] == '.' && k + 1 < end)
                {
                    var n = FrameworkFlowBuilder.SkipWs(s, k + 1);
                    var ne = n;
                    while (ne < end && (char.IsLetterOrDigit(s[ne]) || s[ne] is '_' or '$')) ne++;
                    if (ne == n) break;
                    j = ne;
                    continue;
                }
                break;
            }
            var callee = Regex.Replace(s[i..j], @"\s+", "");
            var p = FrameworkFlowBuilder.SkipWs(s, j);
            if (p < end && s[p] == '<') { var g = FrameworkFlowBuilder.MatchBracket(s, p, '<', '>'); if (g > 0) p = FrameworkFlowBuilder.SkipWs(s, g + 1); }
            if (p >= end || s[p] != '(') { i = j; continue; }
            var close = MatchAny(s, p);
            if (close < 0) { i = j; continue; }
            var argStart = p + 1;
            var argEnd = close;
            // 连着的 .c(...)
            var q = FrameworkFlowBuilder.SkipWs(s, close + 1);
            while (q < end && s[q] == '.')
            {
                var n = FrameworkFlowBuilder.SkipWs(s, q + 1);
                var ne = n;
                while (ne < end && (char.IsLetterOrDigit(s[ne]) || s[ne] is '_' or '$')) ne++;
                var pp = FrameworkFlowBuilder.SkipWs(s, ne);
                if (ne == n || pp >= end || s[pp] != '(') break;
                var cc = MatchAny(s, pp);
                if (cc < 0) break;
                callee += "()." + s[n..ne];
                argStart = pp + 1;
                argEnd = cc;
                close = cc;
                q = FrameworkFlowBuilder.SkipWs(s, cc + 1);
            }
            var isNew = PrevWord(s, i) == "new";
            var assigned = AssignedName(s, isNew ? PrevWordStart(s, i) : i);
            result.Add(new TopCall((isNew ? "new " : "") + callee, i, argStart, argEnd, assigned));
            i = close + 1;
        }
        return result;
    }

    private static string PrevWord(string s, int i)
    {
        var e = i - 1;
        while (e >= 0 && s[e] is ' ' or '\t') e--;
        var b = e;
        while (b >= 0 && (char.IsLetterOrDigit(s[b]) || s[b] is '_' or '$')) b--;
        return e < 0 ? "" : s[(b + 1)..(e + 1)];
    }

    private static int PrevWordStart(string s, int i)
    {
        var e = i - 1;
        while (e >= 0 && s[e] is ' ' or '\t') e--;
        var b = e;
        while (b >= 0 && (char.IsLetterOrDigit(s[b]) || s[b] is '_' or '$')) b--;
        return b + 1;
    }

    /// <summary>const x = / let x: T = / x = 之后紧跟的调用：返回 x。</summary>
    private static string? AssignedName(string s, int i)
    {
        var lineStart = s.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
        var m = AssignPrefix().Match(s[lineStart..i]);
        return m.Success ? m.Groups["n"].Value : null;
    }

    [GeneratedRegex(@"(?:(?:export\s+)?(?:const|let|var)\s+)?(?<n>[A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:await\s+)?$")]
    private static partial Regex AssignPrefix();

    /// <summary>import 语句：本地名 → (模块说明符, 是否默认导入, 导入的原名)。</summary>
    internal static Dictionary<string, (string Spec, bool Default, string Imported)> Imports(JsFile f)
    {
        var map = new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal);
        foreach (Match m in ImportFrom().Matches(f.S))
        {
            var spec = f.StringAt(m.Groups["q"].Index);
            if (spec is null) continue;
            var clause = m.Groups["c"].Value.Trim();
            if (clause.StartsWith("type ", StringComparison.Ordinal)) continue;
            var brace = clause.IndexOf('{');
            var head = (brace < 0 ? clause : clause[..brace]).Trim().TrimEnd(',').Trim();
            if (head.StartsWith("* as ", StringComparison.Ordinal)) map[head[5..].Trim()] = (spec, false, "*");
            else if (head.Length > 0 && Regex.IsMatch(head, @"^[A-Za-z_$][\w$]*$")) map[head] = (spec, true, "default");
            if (brace >= 0)
            {
                var close = clause.IndexOf('}', brace);
                foreach (var part in clause[(brace + 1)..(close < 0 ? clause.Length : close)].Split(','))
                {
                    var p = part.Trim();
                    if (p.StartsWith("type ", StringComparison.Ordinal)) continue;
                    var asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                    var imported = (asAt < 0 ? p : p[..asAt]).Trim();
                    var local = (asAt < 0 ? p : p[(asAt + 4)..]).Trim();
                    if (local.Length > 0) map[local] = (spec, false, imported);
                }
            }
        }
        return map;
    }

    [GeneratedRegex(@"\bimport\s+(?<c>[^;'""]*?)\s+from\s+(?<q>[""'])")]
    private static partial Regex ImportFrom();
}
