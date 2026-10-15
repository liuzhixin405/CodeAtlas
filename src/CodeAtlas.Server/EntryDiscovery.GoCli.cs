using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Go 命令行框架的命令入口（kind = command，route = 子命令路径，空格连接；根命令自己有 Run 时用它的名字）：
/// cobra（<c>&amp;cobra.Command{Use: "serve [flags]", Run / RunE: f}</c>，父子来自 <c>parent.AddCommand(child, newXCmd())</c>，
/// 命令可以是包级变量，也可以在 <c>func newXCmd() *cobra.Command</c> 里构造后 return）、
/// urfave/cli（<c>Commands: []*cli.Command{{Name: "x", Action: f, Subcommands: …}}</c>，v1 的 <c>[]cli.Command</c> 与 <c>&amp;cli.App{Action: …}</c> 也算）。
/// 处理函数是名字（f / pkg.F）就按名字找；是 func 字面量就取它里面第一个能在仓库里找到的函数调用，找不到退回包含它的函数，再退回文件模块。
/// 入口的 File / Line 是处理函数的位置。
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"(?<!\*\s*)\bcobra\s*\.\s*Command\s*\{")]
    private static partial Regex CobraLiteral();
    // var serveCmd = &cobra.Command{   serveCmd := &cobra.Command{   cmd = &cobra.Command{
    [GeneratedRegex(@"(?:\bvar\s+)?\b(\w+)\s*(?::=|=)\s*$")]
    private static partial Regex GoAssignTail();
    [GeneratedRegex(@"\breturn\s*$")]
    private static partial Regex GoReturnTail();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*AddCommand\s*\(")]
    private static partial Regex CobraAddCommand();
    [GeneratedRegex(@"\bAction\s*:")]
    private static partial Regex UrfaveAction();
    [GeneratedRegex(@"\b(?:Sub)?[Cc]ommands\s*[:=]\s*\[\s*\]\s*\*?\s*cli\s*\.\s*Command\s*$")]
    private static partial Regex UrfaveCommandsSlice();
    [GeneratedRegex(@"cli\s*\.\s*(?:Command|App)\s*$")]
    private static partial Regex UrfaveTypedLiteral();
    [GeneratedRegex(@"(?:\b(\w+)\s*\.\s*)?\b(\w+)\s*\(")]
    private static partial Regex GoCallSite();

    private static readonly HashSet<string> GoNotHandlerCalls = new(StringComparer.Ordinal)
    {
        "func", "if", "for", "switch", "return", "len", "make", "append", "panic", "new", "Help", "Usage", "UsageString", "Println", "Printf", "Print", "Errorf", "Sprintf", "Exit", "Fatal", "Fatalf", "Flags", "Args", "String", "Bool", "Int", "Context",
    };

    private static void DiscoverGoCli(Scan scan, IReadOnlyList<string> files, Func<string, string, MapSymbol?> findFunc, Func<string, int, MapSymbol?> enclosing)
    {
        var modules = scan.Symbols.Where(s => s.Kind == SymbolKind.Module && IsGo(s.Path)).GroupBy(s => s.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 处理函数表达式 → 符号
        MapSymbol? HandlerOf(string expr, string file, int line, int bodyOffsetLine)
        {
            expr = expr.Trim();
            if (expr.StartsWith("func", StringComparison.Ordinal))
            {
                var open = expr.IndexOf('{');
                if (open >= 0)
                    foreach (Match call in GoCallSite().Matches(expr[(open + 1)..]))
                    {
                        if (GoNotHandlerCalls.Contains(call.Groups[2].Value)) continue;
                        if (findFunc(call.Groups[2].Value, file) is { } f) return f;
                    }
                return enclosing(file, bodyOffsetLine) ?? modules.GetValueOrDefault(file);
            }
            var name = Regex.Match(expr, @"(\w+)\s*$");
            if (name.Success && findFunc(name.Groups[1].Value, file) is { } h) return h;
            return null;
        }

        void AddCommand(MapSymbol target, string ruleId, string route)
        {
            foreach (var c in scan.ContainersOf(target.Path))
                scan.Add(new EntryPoint(target.Id, c.Key, ruleId, "command", route, null, target.Path, target.StartLine));
        }

        // ── cobra ──
        var cobra = new List<(string Key, string Name, string? Run, string File, int Line)>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); // "F()" → 返回的命令键
        var children = new Dictionary<string, string>(StringComparer.Ordinal); // 子 → 父
        var localKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            var text = string.Join('\n', lines);
            if (!text.Contains("cobra", StringComparison.Ordinal)) continue;
            int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
            foreach (Match m in CobraLiteral().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index)) continue;
                var open = m.Index + m.Length - 1;
                var (fields, _) = ArgParser.SplitArgs(text, open);
                var use = Field(fields, "Use") is { } u ? ArgParser.Literal(u) : null;
                if (use is null) continue;
                var name = use.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? use;
                var line = LineOf(m.Index);
                var fn = enclosing(file, line);
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var before = text[lineStart..m.Index].TrimEnd().TrimEnd('&').TrimEnd();
                string key;
                if (GoAssignTail().Match(before) is { Success: true } a)
                {
                    key = fn is null ? a.Groups[1].Value : fn.Name + "." + a.Groups[1].Value;
                    if (fn is not null)
                    {
                        localKeys.Add(key);
                        var body = string.Join('\n', lines[(fn.StartLine - 1)..Math.Min(lines.Length, fn.EndLine)]);
                        if (Regex.IsMatch(body, $@"\breturn\s+{Regex.Escape(a.Groups[1].Value)}\b")) aliases[fn.Name + "()"] = key;
                    }
                }
                else if (fn is not null && GoReturnTail().IsMatch(before)) { key = fn.Name + "()"; aliases[key] = key; }
                else key = file + ":" + line;
                cobra.Add((key, name, Field(fields, "RunE") ?? Field(fields, "Run"), file, line));
            }
            foreach (Match m in CobraAddCommand().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index)) continue;
                var fn = enclosing(file, LineOf(m.Index));
                string KeyOf(string ident) => fn is not null && localKeys.Contains(fn.Name + "." + ident) ? fn.Name + "." + ident : ident;
                var parent = KeyOf(m.Groups[1].Value);
                var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                foreach (var arg in args)
                {
                    var call = Regex.Match(arg, @"(\w+)\s*\([^()]*\)\s*$");
                    var child = call.Success ? call.Groups[1].Value + "()" : Regex.IsMatch(arg, @"^\w+$") ? KeyOf(arg) : null;
                    if (child is not null) children[child] = parent;
                }
            }
        }
        if (cobra.Count > 0)
        {
            string Canon(string k) => aliases.TryGetValue(k, out var v) ? v : k;
            var parentOf = children.ToDictionary(p => Canon(p.Key), p => Canon(p.Value), StringComparer.Ordinal);
            var nameOf = cobra.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            foreach (var c in cobra)
            {
                if (c.Run is null) continue;
                var parts = new List<string> { c.Name };
                var cur = c.Key;
                for (var depth = 0; depth < 8 && parentOf.TryGetValue(cur, out var p) && parentOf.ContainsKey(p); depth++)
                {
                    // 父命令还有父命令 → 它不是根，名字进路径；根的名字（程序名）不进
                    if (nameOf.TryGetValue(p, out var pn)) parts.Insert(0, pn);
                    cur = p;
                }
                var lines = scan.ReadLines(c.File);
                var runAt = Array.FindIndex(lines, c.Line - 1, l => Regex.IsMatch(l, @"\bRunE?\s*:"));
                if (HandlerOf(c.Run, c.File, c.Line, runAt >= 0 ? runAt + 1 : c.Line) is { } h) AddCommand(h, "go.cobra", string.Join(' ', parts));
            }
        }

        // ── urfave/cli ──
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            var text = string.Join('\n', lines);
            if (!text.Contains("urfave/cli", StringComparison.Ordinal)) continue;
            var spans = BraceSpans(text);
            int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
            (int Open, int Close)? Enclosing(int index) =>
                spans.Where(s => s.Open < index && index < s.Close).OrderByDescending(s => s.Open).Cast<(int, int)?>().FirstOrDefault();
            string? NameOf(int open)
            {
                var (fields, _) = ArgParser.SplitArgs(text, open);
                return Field(fields, "Name") is { } n ? ArgParser.Literal(n) : null;
            }
            // 命令字面量的父命令字面量：它所在的 Commands / Subcommands 切片在哪个字面量里
            int? ParentLiteral(int open)
            {
                var slice = Enclosing(open);
                if (slice is null) return null;
                var head = text[Math.Max(0, slice.Value.Open - 80)..slice.Value.Open];
                if (!UrfaveCommandsSlice().IsMatch(head)) return null;
                return Enclosing(slice.Value.Open)?.Open;
            }
            foreach (Match m in UrfaveAction().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index)) continue;
                var literal = Enclosing(m.Index);
                if (literal is null) continue;
                var open = literal.Value.Open;
                var (fields, _) = ArgParser.SplitArgs(text, open);
                if (Field(fields, "Action") is not { } action) continue;
                var head = text[Math.Max(0, open - 80)..open];
                var parent = ParentLiteral(open);
                if (parent is null && !UrfaveTypedLiteral().IsMatch(head.TrimEnd())) continue;
                var parts = new List<string>();
                if (parent is not null)
                {
                    parts.Add(NameOf(open) ?? "?");
                    for (var (p, depth) = (parent, 0); p is not null && depth < 8; depth++)
                    {
                        var grand = ParentLiteral(p.Value);
                        if (grand is null) break; // p 是根（App / 顶层 Command），程序名不进路径
                        parts.Insert(0, NameOf(p.Value) ?? "?");
                        p = grand;
                    }
                }
                else parts.Add(NameOf(open) ?? scan.ContainersOf(file).FirstOrDefault()?.Name ?? MapSymbol.FileName(file));
                var actionLine = LineOf(text.IndexOf("Action", m.Index, StringComparison.Ordinal));
                if (HandlerOf(action, file, actionLine, actionLine) is { } h) AddCommand(h, "go.urfave-cli", string.Join(' ', parts));
            }
        }
    }

    /// <summary>复合字面量 / 对象字面量的字段：Key: value 里 Key 的值原文；没有返回 null。</summary>
    private static string? Field(IReadOnlyList<string> fields, string key)
    {
        foreach (var f in fields)
        {
            var m = Regex.Match(f, $@"^\s*{Regex.Escape(key)}\s*:\s*");
            if (m.Success) return f[m.Length..].Trim();
        }
        return null;
    }

    /// <summary>文本里每对 { } 的下标（跳过 ' " ` 字符串与 // /* */ 注释）。</summary>
    private static List<(int Open, int Close)> BraceSpans(string text)
    {
        var result = new List<(int, int)>();
        var stack = new Stack<int>();
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is not null)
            {
                if (ch == '\\' && quote != '`') { i++; continue; }
                if (ch == quote) quote = null;
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/') { var nl = text.IndexOf('\n', i); if (nl < 0) break; i = nl; continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) break; i = end + 1; continue; }
            if (ch is '"' or '\'' or '`') quote = ch;
            else if (ch == '{') stack.Push(i);
            else if (ch == '}' && stack.Count > 0) result.Add((stack.Pop(), i));
        }
        return result;
    }
}
