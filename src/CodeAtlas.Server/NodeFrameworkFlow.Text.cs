using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>Node 框架流程的源码文本工具：JS / TS 的注释、字符串、模板串、正则字面量处理，函数参数表，调用所在的控制结构。</summary>
public static partial class NodeFrameworkFlow
{
    /// <summary>
    /// 一个 JS / TS 文件的三种视图，偏移与行号完全一致：Raw 原文；NoComments 注释换成空格（字符串保留，取字面量用）；
    /// Code 再把字符串、模板串、正则字面量的内容也换成空格（引号留着，扫描调用、配括号用）。
    /// </summary>
    internal sealed class JsSource
    {
        public string Raw { get; }
        public string NoComments { get; }
        public string Code { get; }
        private readonly int[] _lineStarts;

        public JsSource(string raw)
        {
            Raw = raw;
            (NoComments, Code) = StripJs(raw);
            var starts = new List<int> { 0 };
            for (var i = 0; i < raw.Length; i++) if (raw[i] == '\n') starts.Add(i + 1);
            _lineStarts = [.. starts];
        }

        /// <summary>偏移所在的行（1 起）。</summary>
        public int LineAt(int pos)
        {
            var i = Array.BinarySearch(_lineStarts, Math.Max(0, pos));
            return (i >= 0 ? i : ~i - 1) + 1;
        }

        /// <summary>第 line 行（1 起）开头的偏移；超出范围给文本末尾。</summary>
        public int LineStart(int line) => line <= 1 ? 0 : line - 1 < _lineStarts.Length ? _lineStarts[line - 1] : Raw.Length;

        /// <summary>第 startLine..endLine 行（含）的 [起，止)。</summary>
        public (int Start, int End) Span(int startLine, int endLine)
        {
            var s = LineStart(startLine);
            var e = endLine <= 0 ? Raw.Length : LineStart(endLine + 1);
            return (s, Math.Max(s, e));
        }
    }

    private static readonly HashSet<string> RegexKeywords = new(StringComparer.Ordinal)
        { "return", "typeof", "case", "do", "else", "in", "of", "new", "delete", "void", "throw", "yield", "await" };

    /// <summary>
    /// 注释换空格（NoComments），注释与字符串 / 模板串（含 ${…}）/ 正则字面量的内容换空格（Code）。换行保留。
    /// 正则与除号按前一个有效记号区分：前面是标识符、数字、')' ']' 是除号，其余（含 return / typeof 一类关键字）是正则。
    /// </summary>
    internal static (string NoComments, string Code) StripJs(string text)
    {
        var nc = text.ToCharArray();
        var code = text.ToCharArray();
        static void Blank(char[] a, int from, int to)
        {
            for (var k = Math.Max(0, from); k < to && k < a.Length; k++) if (a[k] is not ('\n' or '\r')) a[k] = ' ';
        }
        var i = 0;
        var prev = '\0';     // 前一个有效记号的末字符；标识符 / 数字记作 'a'
        var prevWord = "";
        while (i < text.Length)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (ch == '/' && next == '/')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(nc, i, e); Blank(code, i, e); i = e; continue;
            }
            if (ch == '/' && next == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 2;
                Blank(nc, i, e); Blank(code, i, e); i = e; continue;
            }
            if (ch is '\'' or '"')
            {
                var j = SkipQuote(text, i);
                Blank(code, i + 1, j - 1); i = j; prev = ch; prevWord = ""; continue;
            }
            if (ch == '`')
            {
                var j = SkipTemplate(text, i);
                Blank(code, i + 1, j - 1); i = j; prev = '`'; prevWord = ""; continue;
            }
            if (ch == '/' && (prev == '\0' || "(,=:[!&|?{};+-*%<>~^".Contains(prev) || prev == 'a' && RegexKeywords.Contains(prevWord)))
            {
                var j = SkipRegex(text, i);
                if (j > 0) { Blank(code, i + 1, j - 1); i = j; prev = 'a'; prevWord = ""; continue; }
            }
            if (IsIdentChar(ch))
            {
                var s = i;
                while (i < text.Length && IsIdentChar(text[i])) i++;
                prev = 'a'; prevWord = text[s..i];
                continue;
            }
            if (!char.IsWhiteSpace(ch)) { prev = ch; prevWord = ""; }
            i++;
        }
        return (new string(nc), new string(code));
    }

    internal static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>text[i] 是 ' 或 "：返回收尾引号之后的位置（没收尾到行末）。</summary>
    private static int SkipQuote(string text, int i)
    {
        var q = text[i];
        var j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == '\\') { j += 2; continue; }
            if (text[j] == q) return j + 1;
            if (text[j] == '\n') return j;
            j++;
        }
        return text.Length;
    }

    /// <summary>text[i] 是 `：返回收尾反引号之后的位置；${…} 里可以再有字符串与模板串。</summary>
    private static int SkipTemplate(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length)
        {
            var c = text[j];
            if (c == '\\') { j += 2; continue; }
            if (c == '`') return j + 1;
            if (c == '$' && j + 1 < text.Length && text[j + 1] == '{')
            {
                var depth = 1;
                j += 2;
                while (j < text.Length && depth > 0)
                {
                    var d = text[j];
                    if (d is '\'' or '"') { j = SkipQuote(text, j); continue; }
                    if (d == '`') { j = SkipTemplate(text, j); continue; }
                    if (d == '{') depth++;
                    else if (d == '}') depth--;
                    j++;
                }
                continue;
            }
            j++;
        }
        return text.Length;
    }

    /// <summary>text[i] 是正则开头的 '/'：返回标志位之后的位置；同一行里没收尾返回 -1（那就是除号）。</summary>
    private static int SkipRegex(string text, int i)
    {
        var j = i + 1;
        var inClass = false;
        while (j < text.Length)
        {
            var c = text[j];
            if (c == '\n') return -1;
            if (c == '\\') { j += 2; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                j++;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                return j;
            }
            j++;
        }
        return -1;
    }

    internal static int SkipWs(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static int SkipWsBack(string text, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        return i;
    }

    /// <summary>text[open] 是 ( [ {：配对的闭括号位置；没配上 -1。用在 Code 视图上（字符串已经清空）。</summary>
    internal static int MatchClose(string text, int open)
    {
        if (open < 0 || open >= text.Length) return -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
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

    /// <summary>text[close] 是 ) ] }：往回配对的开括号位置；没配上 -1。</summary>
    private static int MatchOpen(string text, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            switch (text[i])
            {
                case ')' or ']' or '}': depth++; break;
                case '(' or '[' or '{':
                    if (--depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    /// <summary>位置 i 之前（跳过空白）紧挨着的标识符；start 是它的起点。没有返回 ""。</summary>
    private static string WordBefore(string text, int i, out int start)
    {
        var k = SkipWsBack(text, i - 1);
        var e = k + 1;
        while (k >= 0 && IsIdentChar(text[k])) k--;
        start = k + 1;
        return text[start..e];
    }

    private static string WordAt(string text, int i)
    {
        var e = i;
        while (e < text.Length && IsIdentChar(text[e])) e++;
        return text[i..e];
    }

    /// <summary>
    /// 一段实参（text[open] 是 '('）：按顶层逗号切，给每个实参在文件里的 [起，止)（去掉首尾空白）。用 Code 视图切，字符串里的逗号不干扰。
    /// </summary>
    internal static (List<(int Start, int End)> Args, int Close) SplitArgsAt(string code, int open)
    {
        var list = new List<(int, int)>();
        var close = MatchClose(code, open);
        if (close < 0) return (list, -1);
        var depth = 0;
        var start = open + 1;
        void Push(int s, int e)
        {
            while (s < e && char.IsWhiteSpace(code[s])) s++;
            while (e > s && char.IsWhiteSpace(code[e - 1])) e--;
            if (e > s) list.Add((s, e));
        }
        for (var i = open + 1; i < close; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: Push(start, i); start = i + 1; break;
            }
        }
        Push(start, close);
        return (list, close);
    }

    /// <summary>链上的一次调用：方法名、名字的位置、'(' 的位置。</summary>
    internal sealed record ChainCall(string Name, int Pos, int Open);

    /// <summary>
    /// receiver 开头的调用链（app.use(a).use(b)、换行续写的 consumer.apply(…).forRoutes(…)）：[start, end) 里每处 receiver（前面不是标识符或 '.'），
    /// 依次读 .name&lt;T&gt;(…)；遇到不带括号的成员（app.locals.x）就停。用在 Code 视图上。
    /// </summary>
    internal static List<ChainCall> ReceiverCalls(string code, string receiver, int start, int end)
    {
        var result = new List<ChainCall>();
        var rx = new System.Text.RegularExpressions.Regex(@"(?<![\w$.])" + System.Text.RegularExpressions.Regex.Escape(receiver) + @"\s*[?!]?\s*\.");
        end = Math.Min(end, code.Length);
        for (var m = rx.Match(code, Math.Max(0, start)); m.Success && m.Index < end; m = m.NextMatch())
            result.AddRange(ChainFrom(code, m.Index + m.Length));
        return result;
    }

    /// <summary>i 指向 '.' 之后：读 name(…).name2(…)… 直到链断。</summary>
    internal static List<ChainCall> ChainFrom(string code, int i)
    {
        var list = new List<ChainCall>();
        while (true)
        {
            i = SkipWs(code, i);
            var nameStart = i;
            while (i < code.Length && IsIdentChar(code[i])) i++;
            if (i == nameStart) break;
            var name = code[nameStart..i];
            i = SkipWs(code, i);
            if (i < code.Length && code[i] == '<')
            {
                var close = FrameworkFlowBuilder.MatchBracket(code, i, '<', '>');
                if (close < 0) break;
                i = SkipWs(code, close + 1);
            }
            if (i >= code.Length || code[i] != '(') break;
            var closeParen = MatchClose(code, i);
            if (closeParen < 0) break;
            list.Add(new ChainCall(name, nameStart, i));
            i = SkipWs(code, closeParen + 1);
            if (i < code.Length && code[i] is '?' or '!') i = SkipWs(code, i + 1);
            if (i >= code.Length || code[i] != '.') break;
            i++;
        }
        return list;
    }

    /// <summary>一个函数：所在文件、函数文本的 [起，止)、体的起点（'{' 或 =&gt; 之后的表达式）、参数名（解构参数记作 "{}"）。</summary>
    internal sealed record JsFunction(string Path, int Start, int End, int BodyStart, IReadOnlyList<string> Params);

    /// <summary>
    /// 从 start 起找函数的参数表：第一个后面跟着 =&gt; / { / ": 返回类型 {" 的括号组（const x = wrap(async (req, res) =&gt; …) 会跳过 wrap 的括号），
    /// 或单参数箭头 x =&gt;。end 之后不找。体是块时 End 收到配对的 '}' 之后。
    /// </summary>
    internal static JsFunction? FunctionAt(string path, string code, int start, int end, int searchLimit = 600)
    {
        end = Math.Min(end, code.Length);
        var limit = Math.Min(end, start + searchLimit);
        for (var i = start; i < limit; i++)
        {
            var ch = code[i];
            if (ch == '(')
            {
                var close = MatchClose(code, i);
                if (close < 0 || close >= end) continue;
                var j = SkipWs(code, close + 1);
                int body;
                if (j + 1 < code.Length && code[j] == '=' && code[j + 1] == '>') body = SkipWs(code, j + 2);
                else if (j < code.Length && code[j] == '{') body = j;
                else if (j < code.Length && code[j] == ':')
                {
                    // TS 返回类型：): Promise<void> { / ): boolean =>
                    var k = j + 1;
                    var found = -1;
                    for (; k < end; k++)
                    {
                        if (code[k] == ';') break;
                        if (code[k] == '{') { found = k; break; }
                        if (code[k] == '=' && k + 1 < end && code[k + 1] == '>') { found = SkipWs(code, k + 2); break; }
                    }
                    if (found < 0) continue;
                    body = found;
                }
                else continue;
                var fnEnd = body < code.Length && code[body] == '{' && MatchClose(code, body) is var bc and >= 0 ? Math.Min(bc + 1, end) : end;
                return new JsFunction(path, i, fnEnd, body, ParamNames(code[(i + 1)..close]));
            }
            if (IsIdentChar(ch) && (i == 0 || !IsIdentChar(code[i - 1])))
            {
                // 单参数箭头：ctx => …
                var w = WordAt(code, i);
                var j = SkipWs(code, i + w.Length);
                if (w.Length > 0 && w != "async" && j + 1 < code.Length && code[j] == '=' && code[j + 1] == '>')
                {
                    var body = SkipWs(code, j + 2);
                    var fnEnd = body < code.Length && code[body] == '{' && MatchClose(code, body) is var bc and >= 0 ? Math.Min(bc + 1, end) : end;
                    return new JsFunction(path, i, fnEnd, body, [w]);
                }
                i += Math.Max(0, w.Length - 1);
            }
        }
        return null;
    }

    /// <summary>参数表文本 → 参数名：去掉 ...、TS 的访问修饰符、类型标注与默认值；解构参数记作 "{}"。</summary>
    internal static List<string> ParamNames(string list)
    {
        var names = new List<string>();
        if (list.Trim().Length == 0) return names;
        foreach (var raw in FrameworkFlowBuilder.SplitTopLevel(list))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (p.StartsWith("...", StringComparison.Ordinal)) p = p[3..].TrimStart();
            foreach (var mod in new[] { "public ", "private ", "protected ", "readonly " })
                while (p.StartsWith(mod, StringComparison.Ordinal)) p = p[mod.Length..].TrimStart();
            if (p.StartsWith('{') || p.StartsWith('[')) { names.Add("{}"); continue; }
            var w = WordAt(p, 0);
            names.Add(w.Length == 0 ? "?" : w);
        }
        return names;
    }

    /// <summary>
    /// 调用（偏移 pos）在函数体（从 bodyStart 起）里所处的控制结构：if / else / switch / 三元 → cond，for / while / do → loop，catch → catch，
    /// 嵌套的函数 / 箭头（回调）→ callback；try / finally / 普通块 / 对象字面量透明，往外接着找；没有返回 null。
    /// 内联函数的调用归在外层符号上，索引器给的 ctx 是相对外层的（全是 callback），这里按内联函数自己的体重算。
    /// </summary>
    internal static string? CtxAt(string code, int pos, int bodyStart)
    {
        var paren = 0;
        var brace = 0;
        var bracket = 0;
        var stmtChecked = false;
        // 函数体自己的 '{' 不算（那是函数，不是回调）
        var floor = bodyStart < code.Length && code[bodyStart] == '{' ? bodyStart + 1 : bodyStart;
        for (var i = Math.Min(pos, code.Length) - 1; i >= floor; i--)
        {
            var ch = code[i];
            var top = paren == 0 && brace == 0 && bracket == 0;
            if (top && !stmtChecked && ch is ';' or '}' or '{')
            {
                stmtChecked = true;
                if (StatementCtx(code, i + 1, pos) is { } s) return s;
            }
            switch (ch)
            {
                case ')': paren++; break;
                case ']': bracket++; break;
                case '}': brace++; break;
                case '[': if (bracket > 0) bracket--; break;
                case '(':
                    if (paren > 0) { paren--; break; }
                    {
                        var kw = WordBefore(code, i, out var kwStart);
                        if (kw is "if" or "while" or "for" or "switch" or "catch")
                        {
                            // 在条件 / 循环头里：不算分支；这条语句本身已经看过
                            stmtChecked = true;
                            i = kwStart;
                            break;
                        }
                        if (HasFunctionBetween(code, i + 1, pos)) return CallContext.Callback;
                    }
                    break;
                case '{':
                    if (brace > 0) { brace--; break; }
                    switch (BlockKind(code, i))
                    {
                        case "cond": return CallContext.Cond;
                        case "loop": return CallContext.Loop;
                        case "catch": return CallContext.Catch;
                        case "fn": return CallContext.Callback;
                        default: stmtChecked = false; break; // try / 普通块 / 对象字面量：往外看包住它的那条语句
                    }
                    break;
            }
        }
        return stmtChecked ? null : StatementCtx(code, floor, pos);
    }

    /// <summary>从 s 起的那条语句（pos 在里面）：if (…) 之后 / else 之后 / 三元的两支 → cond；for / while (…) 之后 → loop。</summary>
    private static string? StatementCtx(string code, int s, int pos)
    {
        var j = SkipWs(code, s);
        if (j >= pos) return null;
        var w = WordAt(code, j);
        if (w == "else") return CallContext.Cond;
        if (w is "if" or "while" or "for" or "switch")
        {
            var open = SkipWs(code, j + w.Length);
            if (open < code.Length && code[open] == '(' && MatchClose(code, open) is var close and >= 0 && pos > close)
                return w is "if" or "switch" ? CallContext.Cond : CallContext.Loop;
            return null;
        }
        if (w == "do") return CallContext.Loop;
        // 三元：顶层的 ?（不是 ?. 与 ??）
        var depth = 0;
        for (var k = j; k < pos && k < code.Length; k++)
        {
            var c = code[k];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == '?')
            {
                var n = k + 1 < code.Length ? code[k + 1] : '\0';
                if (n == '.' || n == '?') { k++; if (n == '?') return CallContext.Cond; continue; }
                return CallContext.Cond;
            }
        }
        return null;
    }

    /// <summary>[from, to) 里有没有 =&gt; 或 function 关键字（实参里的回调）。</summary>
    private static bool HasFunctionBetween(string code, int from, int to)
    {
        for (var k = from; k + 1 < to && k + 1 < code.Length; k++)
        {
            if (code[k] == '=' && code[k + 1] == '>') return true;
            if (code[k] == 'f' && (k == 0 || !IsIdentChar(code[k - 1])) && string.CompareOrdinal(code, k, "function", 0, 8) == 0
                && (k + 8 >= code.Length || !IsIdentChar(code[k + 8]))) return true;
        }
        return false;
    }

    /// <summary>code[b] 是 '{'：它开的是什么块。</summary>
    private static string BlockKind(string code, int b)
    {
        var k = SkipWsBack(code, b - 1);
        if (k < 0) return "block";
        if (code[k] == '>' && k > 0 && code[k - 1] == '=') return "fn";
        if (code[k] == ')')
        {
            var open = MatchOpen(code, k);
            if (open < 0) return "block";
            var w = WordBefore(code, open, out var ws);
            switch (w)
            {
                case "if" or "switch": return "cond";
                case "for" or "while": return "loop";
                case "catch": return "catch";
                case "function": return "fn";
                case "": return "block";
            }
            return "fn"; // function name(…) { / 方法简写 name(…) {
        }
        var word = WordBefore(code, b, out _);
        return word switch
        {
            "else" => "cond",
            "do" => "loop",
            "try" or "finally" => "try",
            _ => "block",
        };
    }
}
