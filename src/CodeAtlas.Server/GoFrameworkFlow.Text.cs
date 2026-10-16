using System.Text.RegularExpressions;

namespace CodeAtlas.Server;

/// <summary>框架流程 · Go 的源码文本工具：剥注释与字符串（含反引号原始字符串）、行号 / 字节偏移换算、import 表、func 的参数表与函数体、实参切分。</summary>
public static partial class GoFrameworkFlow
{
    /// <summary>
    /// Go 源码的注释、"…" 字符串、'…' 字符、`…` 原始字符串的内容换成空格（引号、换行保留，偏移与行号不变）。
    /// 共用的 <see cref="FrameworkFlowBuilder.StripCommentsAndStrings"/> 不认反引号：结构体标签 `json:"x"`、原始字符串里的 // 会把后面的代码吃掉。
    /// </summary>
    public static string StripGo(string text)
    {
        var chars = text.ToCharArray();
        void Blank(int from, int to) { for (var k = from; k < to && k < chars.Length; k++) if (chars[k] is not ('\n' or '\r')) chars[k] = ' '; }
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = text.Length;
                Blank(i, e); i = e; continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? text.Length : e + 2;
                Blank(i, e); i = e; continue;
            }
            if (ch == '`')
            {
                var e = text.IndexOf('`', i + 1);
                if (e < 0) e = text.Length;
                Blank(i + 1, e); i = e + 1; continue;
            }
            if (ch is '"' or '\'')
            {
                var j = i + 1;
                while (j < text.Length && text[j] != ch && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                Blank(i + 1, Math.Min(j, text.Length)); i = j + 1; continue;
            }
            i++;
        }
        return new string(chars);
    }

    /// <summary>一个 Go 源文件：原文（取字符串字面量用）、剥过的代码（扫描用，偏移与原文一致）、行首表、字节偏移表（调用的 Pos 是 UTF-8 字节偏移）、import 表。</summary>
    private sealed class GoText
    {
        public string Raw { get; }
        public string Code { get; }
        /// <summary>导入名 → 导入路径（没写别名的取路径最后一段，/v4 这类版本段取前一段）。</summary>
        public Dictionary<string, string> Imports { get; }
        private readonly List<int> lineStarts = [0];
        private int[]? bytes;

        public GoText(string raw)
        {
            Raw = raw;
            Code = StripGo(raw);
            for (var i = 0; i < raw.Length; i++) if (raw[i] == '\n') lineStarts.Add(i + 1);
            Imports = ReadImports();
        }

        public int LineAt(int pos)
        {
            var i = lineStarts.BinarySearch(Math.Clamp(pos, 0, Code.Length));
            return (i >= 0 ? i : ~i - 1) + 1;
        }

        public int LineStart(int line) => line <= 1 ? 0 : line - 1 < lineStarts.Count ? lineStarts[line - 1] : Code.Length;

        /// <summary>第 startLine..endLine 行（含）的 [起，止)。</summary>
        public (int Start, int End) Span(int startLine, int endLine) => (LineStart(startLine), endLine <= 0 ? Code.Length : Math.Max(LineStart(startLine), LineStart(endLine + 1)));

        private int[] Bytes()
        {
            if (bytes is not null) return bytes;
            var b = new int[Raw.Length + 1];
            for (var i = 0; i < Raw.Length; i++)
            {
                var c = Raw[i];
                b[i + 1] = b[i] + (c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsHighSurrogate(c) ? 4 : char.IsLowSurrogate(c) ? 0 : 3);
            }
            return bytes = b;
        }

        /// <summary>字符偏移 → UTF-8 字节偏移。</summary>
        public int Byte(int charPos) => Bytes()[Math.Clamp(charPos, 0, Raw.Length)];

        /// <summary>UTF-8 字节偏移 → 字符偏移。</summary>
        public int Char(int bytePos)
        {
            var b = Bytes();
            var i = Array.BinarySearch(b, bytePos);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, Raw.Length);
        }

        /// <summary>调用在文件里的字符偏移：有 Pos（字节偏移）用 Pos，没有用所在行行首。</summary>
        public int PosOf(FrameworkCall c) => c.Pos is { } p ? Char(p) : LineStart(c.Line);

        private Dictionary<string, string> ReadImports()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(int from, int to)
            {
                for (var q = Code.IndexOf('"', from); q >= 0 && q < to; q = Code.IndexOf('"', q + 1))
                {
                    var q2 = Code.IndexOf('"', q + 1);
                    if (q2 < 0 || q2 > to) break;
                    var path = Raw[(q + 1)..q2];
                    var lineStart = Code.LastIndexOf('\n', q) + 1;
                    var before = Code[Math.Max(lineStart, from)..q].Trim();
                    if (before.StartsWith("import", StringComparison.Ordinal)) before = before[6..].Trim();
                    var alias = before.Length > 0 ? before : DefaultAlias(path);
                    if (alias is not ("_" or ".")) map[alias] = path;
                    q = q2;
                }
            }
            foreach (Match m in ImportKeyword().Matches(Code))
            {
                var i = FrameworkFlowBuilder.SkipWs(Code, m.Index + m.Length);
                if (i < Code.Length && Code[i] == '(')
                {
                    var close = MatchPair(Code, i, '(', ')');
                    if (close > 0) Add(i + 1, close);
                }
                else
                {
                    var eol = Code.IndexOf('\n', i);
                    Add(m.Index, eol < 0 ? Code.Length : eol);
                }
            }
            return map;
        }
    }

    /// <summary>导入路径的默认包名：最后一段；/v2、/v5 这类版本段取前一段。</summary>
    private static string DefaultAlias(string importPath)
    {
        var parts = importPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return importPath;
        var last = parts[^1];
        if (parts.Length > 1 && Regex.IsMatch(last, @"^v\d+$")) last = parts[^2];
        return last;
    }

    /// <summary>text[open] 是开括号，返回配对的闭括号；配不上 -1。只用在剥过字符串的代码上。</summary>
    private static int MatchPair(string code, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == o) depth++;
            else if (code[i] == c && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>一个 func（声明或字面量）：参数表的括号、函数体的花括号（偏移）。</summary>
    private readonly record struct FuncSpan(int Start, int ParamsOpen, int ParamsClose, int BodyOpen, int BodyClose);

    /// <summary>
    /// code[funcPos..] 以 func 开头：声明（func (r *T) Name[T any](…) … {）或字面量（func(…) … {）。
    /// 返回类型里的 interface{} / struct{…} 跳过；Go 的函数体花括号必须和签名末尾在同一行，换行前没找到就算没有（函数类型 / 接口方法）。
    /// </summary>
    private static FuncSpan? ParseFunc(string code, int funcPos)
    {
        var i = FrameworkFlowBuilder.SkipWs(code, funcPos + 4);
        if (i >= code.Length) return null;
        if (code[i] == '(')
        {
            var close = MatchPair(code, i, '(', ')');
            if (close < 0) return null;
            var j = FrameworkFlowBuilder.SkipWs(code, close + 1);
            if (j < code.Length && (char.IsLetter(code[j]) || code[j] == '_'))
            {
                // 方法声明：接收者之后是名字；也可能是字面量的返回类型（func(x) error {）——名字后面紧跟 ( 或 [ 才是方法名，否则 i 仍指着字面量的参数表
                var k = j;
                while (k < code.Length && (char.IsLetterOrDigit(code[k]) || code[k] == '_')) k++;
                var after = FrameworkFlowBuilder.SkipWs(code, k);
                if (after < code.Length && code[after] is '(' or '[' && !IsTypeKeyword(code[j..k]))
                {
                    i = after;
                    if (code[i] == '[') { var g = MatchPair(code, i, '[', ']'); if (g < 0) return null; i = FrameworkFlowBuilder.SkipWs(code, g + 1); }
                    if (i >= code.Length || code[i] != '(') return null;
                }
            }
        }
        else
        {
            // 函数声明：func Name[T any](…)
            var k = i;
            while (k < code.Length && (char.IsLetterOrDigit(code[k]) || code[k] == '_')) k++;
            if (k == i) return null;
            i = FrameworkFlowBuilder.SkipWs(code, k);
            if (i < code.Length && code[i] == '[') { var g = MatchPair(code, i, '[', ']'); if (g < 0) return null; i = FrameworkFlowBuilder.SkipWs(code, g + 1); }
            if (i >= code.Length || code[i] != '(') return null;
        }
        var paramsClose = MatchPair(code, i, '(', ')');
        if (paramsClose < 0) return null;
        for (var k = paramsClose + 1; k < code.Length; k++)
        {
            var ch = code[k];
            if (ch == '\n') return null;
            if (ch != '{') continue;
            var w = k - 1;
            while (w >= 0 && code[w] == ' ') w--;
            var we = w + 1;
            while (w >= 0 && (char.IsLetterOrDigit(code[w]) || code[w] == '_')) w--;
            if (code[(w + 1)..we] is "interface" or "struct")
            {
                var skip = MatchPair(code, k, '{', '}');
                if (skip < 0) return null;
                k = skip;
                continue;
            }
            var end = MatchPair(code, k, '{', '}');
            return end < 0 ? null : new FuncSpan(funcPos, i, paramsClose, k, end);
        }
        return null;
    }

    private static bool IsTypeKeyword(string word) => word is "func" or "map" or "chan" or "interface" or "struct";

    /// <summary>[start, end) 里按顶层逗号切实参（圆括号、方括号、花括号里的不切），返回每段的（起，止）（去掉两端空白）。</summary>
    private static List<(int Start, int End)> SplitArgs(string code, int start, int end)
    {
        var list = new List<(int, int)>();
        var depth = 0;
        var s = start;
        void Push(int to)
        {
            var a = s;
            var b = to;
            while (a < b && char.IsWhiteSpace(code[a])) a++;
            while (b > a && char.IsWhiteSpace(code[b - 1])) b--;
            if (b > a) list.Add((a, b));
        }
        for (var i = start; i < end; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: Push(i); s = i + 1; break;
            }
        }
        Push(end);
        return list;
    }

    /// <summary>参数表文本（不含括号）→ 每个参数的（名字，类型）；a, b int 这种共用类型的展开。</summary>
    private static List<(string Name, string Type)> ParamList(string paramsText)
    {
        var result = new List<(string, string)>();
        var pending = new List<string>();
        foreach (var (s, e) in SplitArgs(paramsText, 0, paramsText.Length))
        {
            var p = paramsText[s..e];
            var m = Regex.Match(p, @"^(?<n>[A-Za-z_]\w*)\s+(?<t>.+)$", RegexOptions.Singleline);
            if (!m.Success)
            {
                // 只有名字（等着后面的类型）或只有类型（匿名参数）
                if (Regex.IsMatch(p, @"^[A-Za-z_]\w*$")) pending.Add(p);
                else { foreach (var n in pending) result.Add((n, p)); pending.Clear(); result.Add(("", p)); }
                continue;
            }
            var type = m.Groups["t"].Value.Trim();
            foreach (var n in pending) result.Add((n, type));
            pending.Clear();
            result.Add((m.Groups["n"].Value, type));
        }
        foreach (var n in pending) result.Add(("", n)); // 全是匿名参数：func(http.Handler)
        return result;
    }

    /// <summary>语句的结尾：从 start 起第一个顶层换行（括号里的换行不算），或 end。</summary>
    private static int StatementEnd(string code, int start, int end)
    {
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            switch (code[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}':
                    if (--depth < 0) return i;
                    break;
                case '\n' when depth == 0: return i;
                case ';' when depth == 0: return i;
            }
        }
        return end;
    }

    /// <summary>字符串字面量的内容（原文里取）；不是单个字面量（baseUrl + "/x"）给原样的表达式。</summary>
    private static string LiteralOrExpr(GoText t, int start, int end)
    {
        var raw = t.Raw[start..end].Trim();
        if (raw.Length >= 2 && raw[0] is '"' or '`' && raw[^1] == raw[0] && raw.IndexOf(raw[0], 1) == raw.Length - 1) return raw[1..^1];
        return Collapse(raw);
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string JoinPrefix(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a.TrimEnd('/') + "/" + b.TrimStart('/');
    }

    [GeneratedRegex(@"(?<![\w.])import\b")]
    private static partial Regex ImportKeyword();
}
