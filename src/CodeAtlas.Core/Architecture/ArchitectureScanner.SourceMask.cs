using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 按整个文件剥注释与字符串：块注释、C# 逐字 / 原始字符串（"""…"""）、Java 文本块、Go 反引号、JS 模板串、Python 三引号都可以跨行，
/// 逐行判（<see cref="RuleContext.InCommentOrString"/>）认不出来。返回与原文同样行数、每行同样长度的文本：剥掉的字符换成空格，
/// 行号、列号都能和原文对上。
/// </summary>
internal static class SourceMask
{
    /// <summary>注释与字符串都剥掉（只剩代码）。</summary>
    public static string[] Code(IReadOnlyList<string> lines, SourceLang lang) => Mask(lines, lang, keepStrings: false);

    /// <summary>只剥注释，字符串原样保留（要读字符串实参时用）。</summary>
    public static string[] WithoutComments(IReadOnlyList<string> lines, SourceLang lang) => Mask(lines, lang, keepStrings: true);

    private static string[] Mask(IReadOnlyList<string> lines, SourceLang lang, bool keepStrings)
    {
        var text = string.Join('\n', lines);
        var output = text.ToCharArray();
        var n = text.Length;
        var i = 0;

        void Blank(int from, int to, bool isString)
        {
            if (isString && keepStrings) return;
            for (var k = from; k < to && k < n; k++) if (output[k] != '\n') output[k] = ' ';
        }

        // 普通引号字符串：到同一种未转义的引号为止；换行就结束（没闭合的字符串不往下吞）
        int QuotedEnd(int start, char quote, bool escapes)
        {
            var k = start + 1;
            while (k < n)
            {
                var c = text[k];
                if (c == '\n') return k;
                if (escapes && c == '\\') { k += 2; continue; }
                if (c == quote) return k + 1;
                k++;
            }
            return n;
        }

        while (i < n)
        {
            var c = text[i];
            var next = i + 1 < n ? text[i + 1] : '\0';

            // 注释
            if (lang == SourceLang.Python ? c == '#' : c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = n;
                Blank(i, end, isString: false);
                i = end;
                continue;
            }
            if (lang != SourceLang.Python && c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                Blank(i, end, isString: false);
                i = end;
                continue;
            }

            // 字符串
            var stringEnd = lang switch
            {
                SourceLang.CSharp => CSharpString(text, i),
                SourceLang.Java => JavaString(text, i),
                SourceLang.Go => GoString(text, i),
                SourceLang.JavaScript => JsString(text, i),
                SourceLang.Python => PythonString(text, i),
                _ => -1,
            };
            if (stringEnd > i)
            {
                Blank(i, stringEnd, isString: true);
                i = stringEnd;
                continue;
            }
            i++;
        }
        return new string(output).Split('\n');

        // ── 各语言的字符串起点：返回结束位置（不含），不是字符串起点返回 -1 ──

        int CSharpString(string t, int at)
        {
            var k = at;
            // 前缀：$ / $$ / @ / $@ / @$
            var dollars = 0;
            var verbatim = false;
            while (k < n && (t[k] == '$' || t[k] == '@'))
            {
                if (t[k] == '$') dollars++; else verbatim = true;
                k++;
            }
            if (k < n && t[k] == '\'' && k == at)
            {
                // 字符字面量 'x' / '\n' / '\''：最多看 8 个字符（'A'）
                var e = k + 1;
                if (e < n && t[e] == '\\') e += 2; else e++;
                while (e < n && e - k <= 8 && t[e] != '\'' && t[e] != '\n') e++;
                return e < n && t[e] == '\'' ? e + 1 : -1;
            }
            if (k >= n || t[k] != '"') return -1;
            if (k > at && !(dollars > 0 || verbatim)) return -1;
            // 前缀里的 $ / @ 前面不能紧挨着标识符字符（a$"x" 不存在，但 @"…" 前的 @ 本身就是前缀）
            var quotes = 0;
            while (k + quotes < n && t[k + quotes] == '"') quotes++;
            if (quotes >= 3)
            {
                // 原始字符串：以同样个数的引号结束
                var closing = new string('"', quotes);
                var e = t.IndexOf(closing, k + quotes, StringComparison.Ordinal);
                if (e < 0) return n;
                while (e + quotes < n && t[e + quotes] == '"') e++; // 结尾多出的引号属于内容
                return e + quotes;
            }
            if (verbatim)
            {
                // 逐字字符串："" 是转义的引号，可以跨行
                var e = k + 1;
                while (e < n)
                {
                    if (t[e] == '"')
                    {
                        if (e + 1 < n && t[e + 1] == '"') { e += 2; continue; }
                        return e + 1;
                    }
                    e++;
                }
                return n;
            }
            if (dollars > 0)
            {
                // 插值字符串：{ } 里是代码，里面可以再有简单字符串
                var e = k + 1;
                var depth = 0;
                while (e < n && t[e] != '\n')
                {
                    var ch = t[e];
                    if (depth == 0 && ch == '\\') { e += 2; continue; }
                    if (ch == '{') { if (depth == 0 && e + 1 < n && t[e + 1] == '{') { e += 2; continue; } depth++; }
                    else if (ch == '}' && depth > 0) depth--;
                    else if (ch == '"')
                    {
                        if (depth == 0) return e + 1;
                        e = QuotedEnd(e, '"', escapes: true);
                        continue;
                    }
                    e++;
                }
                return e;
            }
            return QuotedEnd(k, '"', escapes: true);
        }

        int JavaString(string t, int at)
        {
            if (t[at] == '"' && at + 2 < n && t[at + 1] == '"' && t[at + 2] == '"')
            {
                var e = t.IndexOf("\"\"\"", at + 3, StringComparison.Ordinal);
                return e < 0 ? n : e + 3;
            }
            if (t[at] == '"') return QuotedEnd(at, '"', escapes: true);
            if (t[at] == '\'') return QuotedEnd(at, '\'', escapes: true);
            return -1;
        }

        int GoString(string t, int at)
        {
            if (t[at] == '`')
            {
                var e = t.IndexOf('`', at + 1);
                return e < 0 ? n : e + 1;
            }
            if (t[at] == '"') return QuotedEnd(at, '"', escapes: true);
            if (t[at] == '\'') return QuotedEnd(at, '\'', escapes: true);
            return -1;
        }

        int JsString(string t, int at)
        {
            if (t[at] is '"' or '\'') return QuotedEnd(at, t[at], escapes: true);
            if (t[at] != '`') return -1;
            // 模板串：${ … } 里是代码，里面可以再嵌模板串
            var e = at + 1;
            var depth = 0;
            while (e < n)
            {
                var ch = t[e];
                if (ch == '\\') { e += 2; continue; }
                if (depth == 0 && ch == '`') return e + 1;
                if (ch == '$' && e + 1 < n && t[e + 1] == '{') { depth++; e += 2; continue; }
                if (depth > 0 && ch == '}') { depth--; e++; continue; }
                if (depth > 0 && ch is '"' or '\'') { e = QuotedEnd(e, ch, escapes: true); continue; }
                if (depth > 0 && ch == '`')
                {
                    var inner = t.IndexOf('`', e + 1);
                    e = inner < 0 ? n : inner + 1;
                    continue;
                }
                e++;
            }
            return n;
        }

        int PythonString(string t, int at)
        {
            var k = at;
            // 前缀 r / b / u / f 及组合，前面不能是标识符字符
            if (char.IsLetter(t[k]))
            {
                if (at > 0 && (char.IsLetterOrDigit(t[at - 1]) || t[at - 1] == '_')) return -1;
                while (k < n && k - at < 2 && "rRbBuUfF".Contains(t[k])) k++;
                if (k >= n || t[k] is not ('"' or '\'')) return -1;
            }
            if (t[k] is not ('"' or '\'')) return -1;
            var q = t[k];
            if (k + 2 < n && t[k + 1] == q && t[k + 2] == q)
            {
                var closing = new string(q, 3);
                var e = k + 3;
                while (e < n)
                {
                    if (t[e] == '\\') { e += 2; continue; }
                    if (string.CompareOrdinal(t, e, closing, 0, 3) == 0) return e + 3;
                    e++;
                }
                return n;
            }
            return QuotedEnd(k, q, escapes: true);
        }
    }
}
