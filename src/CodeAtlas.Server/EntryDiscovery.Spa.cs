using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// 单页应用的路由表（前端容器里 Next.js 文件约定之外的页面入口；kind page，rule frontend.route，File / Line 是路由声明处，入口落在路由指向的页面组件上）：
/// <list type="bullet">
/// <item>TanStack Router 文件路由：<c>createFileRoute("/_layout/items")({ component: Items })</c>（也认 createLazyFileRoute）。
/// 路由去掉 <c>_</c> 开头的无路径布局段，<c>$id</c> → <c>{id}</c>、<c>$</c> → <c>{*}</c>；<c>__root</c> 与最后一段是 <c>_xxx</c> 的布局路由不算页面。</item>
/// <item>React Router JSX：<c>&lt;Route path="orders" element={&lt;Orders /&gt;} /&gt;</c>（也认 Component={X} / component={X}），按嵌套拼父路径，
/// <c>index</c> 路由取父路径；只算没有子路由的那些（有子路由的是布局）。</item>
/// <item>React Router 对象：<c>createBrowserRouter / createHashRouter / createMemoryRouter / useRoutes([{ path, element: &lt;X /&gt;, children: [...] }])</c>，规则同上。</item>
/// </list>
/// 页面组件按名字找：同文件 → 同容器里唯一一个导出的同名组件；找不到就不登记（不退回路由文件，免得一个文件里的几十条路由叠成一个入口）。
/// <c>:id</c> → <c>{id}</c>、<c>*</c> → <c>{*}</c>，与 Next.js 的 <c>[id]</c> 同一种写法。
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"\bcreate(?:Lazy)?FileRoute\s*\(\s*([""'`])(?<path>[^""'`]*)\1\s*\)\s*\(")]
    private static partial Regex TanStackFileRoute();

    [GeneratedRegex(@"\bcomponent\s*:\s*(?<name>[A-Z]\w*)")]
    private static partial Regex TanStackComponent();

    [GeneratedRegex(@"<\s*Route\b|<\s*/\s*Route\s*>")]
    private static partial Regex JsxRouteTag();

    [GeneratedRegex(@"\b(?:createBrowserRouter|createHashRouter|createMemoryRouter|useRoutes)\s*\(\s*\[")]
    private static partial Regex RouterObjects();

    [GeneratedRegex(@"\bpath\s*=\s*(?:\{\s*)?([""'`])(?<path>[^""'`]*)\1")]
    private static partial Regex JsxPathAttr();

    [GeneratedRegex(@"\b(?:element\s*=\s*\{\s*<\s*(?<el>[A-Z][\w.]*)|(?:Component|component)\s*=\s*\{\s*(?<comp>[A-Z][\w.]*)\s*\})")]
    private static partial Regex JsxElementAttr();

    [GeneratedRegex(@"(?:^|[\s{,])index(?:\s*(?:=\s*\{\s*true\s*\})?)(?=[\s/>,}]|$)")]
    private static partial Regex IndexAttr();

    private static readonly string[] SpaExtensions = [".tsx", ".jsx", ".ts", ".js"];

    /// <summary>一个前端模块里的 SPA 路由（TanStack 文件路由 / React Router JSX / React Router 对象）：(路由, 声明所在行, 组件名)。text 已去掉注释。</summary>
    internal static List<(string Route, int Line, string Component)> SpaRoutes(string text)
    {
        var found = new List<(string, int, string)>();
        int LineAt(int pos) { var n = 1; for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') n++; return n; }

        foreach (Match m in TanStackFileRoute().Matches(text))
        {
            var raw = m.Groups["path"].Value;
            if (TanStackRoute(raw) is not { } route) continue;
            var close = FrameworkFlowBuilder.MatchBracket(text, m.Index + m.Length - 1, '(', ')');
            var body = text[(m.Index + m.Length)..(close < 0 ? Math.Min(text.Length, m.Index + m.Length + 2000) : close)];
            var comp = TanStackComponent().Match(body);
            if (comp.Success) found.Add((route, LineAt(m.Index), comp.Groups["name"].Value));
        }

        // JSX：按 <Route 与 </Route> 的出现顺序维护父路径栈
        // 栈里每层记着自己那条候选（下标，没有为 -1）；关闭时有子路由 → 这一层是布局，把它那条候选作废
        var stack = new Stack<(string Path, bool HasChild, int Pending)>();
        var pending = new List<(string Route, int Line, string Component)?>();
        foreach (Match t in JsxRouteTag().Matches(text))
        {
            if (t.Value.Contains('/'))
            {
                if (stack.Count == 0) continue;
                var (_, hasChild, own) = stack.Pop();
                if (hasChild && own >= 0) pending[own] = null;
                continue;
            }
            var end = SpaTagEnd(text, t.Index + t.Length);
            if (end < 0) continue;
            var tag = text[t.Index..(end + 1)];
            var selfClosing = text[end - 1] == '/';
            var parent = stack.Count > 0 ? stack.Peek().Path : "";
            if (stack.Count > 0) { var top = stack.Pop(); stack.Push(top with { HasChild = true }); }
            var pathAttr = JsxPathAttr().Match(tag);
            var isIndex = IndexAttr().IsMatch(tag);
            var path = pathAttr.Success ? SpaJoinRoute(parent, pathAttr.Groups["path"].Value) : parent;
            var el = JsxElementAttr().Match(tag);
            var comp = el.Success ? (el.Groups["el"].Success ? el.Groups["el"].Value : el.Groups["comp"].Value) : null;
            var index = -1;
            if (comp is not null && (pathAttr.Success || isIndex)) { pending.Add((path, LineAt(t.Index), SpaLastName(comp))); index = pending.Count - 1; }
            if (!selfClosing) stack.Push((path, false, index));
        }
        found.AddRange(pending.Where(p => p is not null).Select(p => (SpaNormalize(p!.Value.Route), p.Value.Line, p.Value.Component)));

        foreach (Match m in RouterObjects().Matches(text))
        {
            var open = m.Index + m.Length - 1;
            var close = FrameworkFlowBuilder.MatchBracket(text, open, '[', ']');
            if (close < 0) continue;
            SpaRouteObjects(text, open, close, "", found, LineAt);
        }
        return found;
    }

    private static void SpaRouteObjects(string text, int open, int close, string parent, List<(string, int, string)> found, Func<int, int> lineAt)
    {
        for (var i = open + 1; i < close; i++)
        {
            if (text[i] is '"' or '\'' or '`') { i = SpaSkipString(text, i); continue; }
            if (text[i] != '{') continue;
            var end = FrameworkFlowBuilder.MatchBracket(text, i, '{', '}');
            if (end < 0 || end > close) return;
            var obj = text[(i + 1)..end];
            var top = SpaTopLevel(obj);
            var path = Regex.Match(top, @"\bpath\s*:\s*([""'`])(?<p>[^""'`]*)\1");
            var isIndex = Regex.IsMatch(top, @"\bindex\s*:\s*true\b");
            var route = path.Success ? SpaJoinRoute(parent, path.Groups["p"].Value) : parent;
            var kids = Regex.Match(top, @"\bchildren\s*:\s*\[");
            if (kids.Success)
            {
                // children 在 TopLevel 里被抹成空格，位置不变：回到原文按位置找配对的 ]
                var kOpen = i + 1 + kids.Index + kids.Length - 1;
                var kClose = FrameworkFlowBuilder.MatchBracket(text, kOpen, '[', ']');
                if (kClose > 0) SpaRouteObjects(text, kOpen, kClose, route, found, lineAt);
            }
            else if (path.Success || isIndex)
            {
                var el = Regex.Match(top, @"\b(?:element\s*:\s*<\s*(?<n>[A-Z][\w.]*)|Component\s*:\s*(?<n>[A-Z][\w.]*))");
                if (el.Success) found.Add((SpaNormalize(route), lineAt(i), SpaLastName(el.Groups["n"].Value)));
            }
            i = end;
        }
    }

    /// <summary>对象字面量里第一层的文本：嵌套的 {…} / […] / (…) 内容换成空格（长度不变），避免把 children 里的 path 当成自己的。</summary>
    private static string SpaTopLevel(string obj)
    {
        var sb = new StringBuilder(obj);
        var depth = 0;
        for (var i = 0; i < obj.Length; i++)
        {
            var ch = obj[i];
            if (ch is '"' or '\'' or '`') { var e = SpaSkipString(obj, i); if (depth > 0) for (var k = i; k <= e && k < obj.Length; k++) sb[k] = ' '; i = e; continue; }
            if (ch is '{' or '[' or '(') { if (depth > 0) sb[i] = ' '; depth++; continue; }
            if (ch is '}' or ']' or ')') { depth--; if (depth > 0) sb[i] = ' '; continue; }
            if (depth > 0 && ch != '\n') sb[i] = ' ';
        }
        return sb.ToString();
    }

    private static int SpaSkipString(string s, int i)
    {
        var q = s[i];
        for (var k = i + 1; k < s.Length; k++)
        {
            if (s[k] == '\\') { k++; continue; }
            if (s[k] == q) return k;
        }
        return s.Length - 1;
    }

    /// <summary>JSX 开始标签的结尾 '>'（跳过属性里的 {…} 与字符串）。</summary>
    private static int SpaTagEnd(string text, int i)
    {
        var depth = 0;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '"' or '\'' or '`') { i = SpaSkipString(text, i); continue; }
            if (ch == '{') depth++;
            else if (ch == '}') depth--;
            else if (ch == '>' && depth == 0) return i;
        }
        return -1;
    }

    private static string SpaLastName(string dotted) => dotted[(dotted.LastIndexOf('.') + 1)..];

    private static string SpaJoinRoute(string parent, string path) =>
        path.StartsWith('/') ? path : (parent.TrimEnd('/') + "/" + path).Replace("//", "/");

    /// <summary>React Router 的 :id / * 换成与 Next.js 同一种 {id} / {*}；去掉末尾斜杠，空的当根。</summary>
    private static string SpaNormalize(string route)
    {
        var segs = route.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s == "*" ? "{*}" : s.StartsWith(':') ? "{" + s[1..].TrimEnd('?') + "}" : s);
        return "/" + string.Join('/', segs);
    }

    /// <summary>TanStack 文件路由的路径：__root 与布局路由（最后一段 _xxx、不以 / 结尾）返回 null。</summary>
    public static string? TanStackRoute(string raw)
    {
        if (raw.Contains("__root", StringComparison.Ordinal)) return null;
        var segs = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length > 0 && segs[^1].StartsWith('_') && !raw.EndsWith('/')) return null;
        var kept = segs.Where(s => !s.StartsWith('_'))
            .Select(s => s == "$" ? "{*}" : s.StartsWith('$') ? "{" + s[1..] + "}" : s);
        return "/" + string.Join('/', kept);
    }

    /// <summary>去掉 // 与 /* */ 注释（字符串、模板字符串里的不动；长度不变，换行保留）。</summary>
    internal static string StripJsComments(string text)
    {
        var sb = new StringBuilder(text);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is '"' or '\'' or '`') { i = SpaSkipString(text, i); continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // 正则字面量 / URL 里的 // 在字符串里，已跳过；这里是行注释
                for (; i < text.Length && text[i] != '\n'; i++) sb[i] = ' ';
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                for (; i < stop; i++) if (text[i] != '\n') sb[i] = ' ';
                i--;
            }
        }
        return sb.ToString();
    }
}
