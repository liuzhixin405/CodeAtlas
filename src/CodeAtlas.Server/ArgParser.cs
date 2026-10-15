namespace CodeAtlas.Server;

/// <summary>
/// 源码文本里的调用实参切分（Go / Python 入口规则与出口识别用）：认括号嵌套与 ' " ` 三种引号（反斜杠转义），
/// 不做完整词法——Python 三引号串、Go 的 rune 字面量 '(' 这类少见情形会切歪，规则按"拿不到就不算"处理。
/// </summary>
internal static class ArgParser
{
    /// <summary>text[open] 是 '(' / '[' / '{'：返回与之配对的右括号下标；没有返回 -1。</summary>
    public static int MatchClose(string text, int open)
    {
        if (open < 0 || open >= text.Length) return -1;
        var depth = 0;
        char? quote = null;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is not null)
            {
                if (ch == '\\' && quote != '`') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            switch (ch)
            {
                case '\'' or '"' or '`': quote = ch; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0) return i;
                    if (depth < 0) return -1;
                    break;
            }
        }
        return -1;
    }

    /// <summary>text[open] 是 '('：顶层逗号分开的实参（去掉首尾空白）与右括号下标；括号不配对时 Close = -1，实参切到文本末尾。</summary>
    public static (List<string> Args, int Close) SplitArgs(string text, int open)
    {
        var args = new List<string>();
        var close = MatchClose(text, open);
        var end = close < 0 ? text.Length : close;
        var depth = 0;
        char? quote = null;
        var start = open + 1;
        for (var i = open + 1; i < end; i++)
        {
            var ch = text[i];
            if (quote is not null)
            {
                if (ch == '\\' && quote != '`') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            switch (ch)
            {
                case '\'' or '"' or '`': quote = ch; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    args.Add(text[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        var last = text[start..Math.Min(end, text.Length)].Trim();
        if (last.Length > 0 || args.Count > 0) args.Add(last);
        return (args, close);
    }

    /// <summary>实参里的关键字参数值：prefix="/items" → "/items"（原文，含引号）；没有返回 null。</summary>
    public static string? Keyword(IReadOnlyList<string> args, string name)
    {
        foreach (var a in args)
        {
            var eq = a.IndexOf('=');
            if (eq <= 0 || (eq + 1 < a.Length && a[eq + 1] == '=')) continue;
            if (a[..eq].Trim() == name) return a[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>实参是不是关键字参数（name=value）。</summary>
    public static bool IsKeyword(string arg)
    {
        var eq = arg.IndexOf('=');
        if (eq <= 0 || (eq + 1 < arg.Length && arg[eq + 1] == '=')) return false;
        var name = arg[..eq].Trim();
        return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>一个实参若整个是字符串字面量（可带 r / b / u / f 前缀，Go 的原始串），返回其内容；否则 null。</summary>
    public static string? Literal(string arg)
    {
        var a = arg.Trim();
        var i = 0;
        while (i < a.Length && i < 2 && "rRbBuUfF".Contains(a[i])) i++;
        if (a.Length - i < 2) return null;
        var q = a[i];
        if (q is not ('\'' or '"' or '`') || a[^1] != q) return null;
        var body = a[(i + 1)..^1];
        // "a" + "b" 这类拼接不是单个字面量
        return body.Contains(q) && q != '`' && !body.Contains("\\" + q) ? null : body;
    }

    /// <summary>实参里出现的最后一个字符串字面量的内容（baseUrl+"/cart" → /cart）；没有返回 null。</summary>
    public static string? LastLiteral(string arg)
    {
        string? found = null;
        for (var i = 0; i < arg.Length; i++)
        {
            var q = arg[i];
            if (q is not ('\'' or '"' or '`')) continue;
            var j = i + 1;
            while (j < arg.Length && arg[j] != q) { if (arg[j] == '\\' && q != '`') j++; j++; }
            if (j >= arg.Length) break;
            found = arg[(i + 1)..j];
            i = j;
        }
        return found;
    }
}
