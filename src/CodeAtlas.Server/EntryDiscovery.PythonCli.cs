using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// Python 命令行框架的命令入口（kind = command，route = 子命令路径，空格连接；根 group / 根 Typer 应用的名字不进路径）：
/// click（<c>@click.command()</c> / <c>@cli.command("x")</c> / <c>@grp.group()</c> 嵌套，<c>cli.add_command(f, name=…)</c>；
/// 没写名字时用函数名，下划线换成 -，去掉 _command / _cmd 后缀——click 8.2 的规则）、
/// typer（<c>app = typer.Typer()</c> + <c>@app.command()</c>，<c>app.add_typer(sub, name="users")</c> 前缀；<c>typer.run(main)</c>）、
/// argparse（<c>sub = parser.add_subparsers()</c>、<c>p = sub.add_parser("serve")</c>、<c>p.set_defaults(func=serve)</c>，按出现顺序处理变量复用，嵌套 subparsers 拼路径）。
/// 入口的 File / Line 是处理函数的位置。group 函数本身不是入口（它只在子命令前跑）。
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"^\s*(\w+)\s*(?::\s*[\w.]+\s*)?=\s*(?:typer\s*\.\s*)?Typer\s*\(")]
    private static partial Regex PyTyperDecl();
    [GeneratedRegex(@"^@\s*([\w.]+?)\s*\.\s*(command|group)\s*\(")]
    private static partial Regex PyCliDecorator();
    [GeneratedRegex(@"\b([\w.]+?)\s*\.\s*(add_typer|add_command)\s*\(")]
    private static partial Regex PyCliMount();
    [GeneratedRegex(@"\btyper\s*\.\s*run\s*\(\s*(\w+)\s*\)")]
    private static partial Regex PyTyperRun();
    [GeneratedRegex(@"(?:\b(\w+)\s*=\s*)?\b(\w+)\s*\.\s*add_subparsers\s*\(")]
    private static partial Regex PyAddSubparsers();
    [GeneratedRegex(@"(?:\b(\w+)\s*=\s*)?\b(\w+)\s*\.\s*add_parser\s*\(")]
    private static partial Regex PyAddParser();
    [GeneratedRegex(@"\b(\w+)\s*\.\s*set_defaults\s*\(")]
    private static partial Regex PySetDefaults();

    private static void DiscoverPythonCli(
        Scan scan, IReadOnlyList<string> files, IReadOnlyDictionary<(string, int), MapSymbol> atLine,
        Func<string, string, (string File, string Name)> resolveRef, Func<string, string?, string, MapSymbol?> functionNamed)
    {
        void AddCommand(MapSymbol target, string ruleId, string route)
        {
            foreach (var c in scan.ContainersOf(target.Path))
                scan.Add(new EntryPoint(target.Id, c.Key, ruleId, "command", route, null, target.Path, target.StartLine));
        }
        static string ClickName(string fn)
        {
            foreach (var suffix in new[] { "_command", "_cmd", "_group", "_grp" })
                if (fn.EndsWith(suffix, StringComparison.Ordinal) && fn.Length > suffix.Length) { fn = fn[..^suffix.Length]; break; }
            return fn.Replace('_', '-');
        }
        string? ExplicitName(string decoratorText, int open)
        {
            var (args, _) = ArgParser.SplitArgs(decoratorText, open);
            return args.Where(a => !ArgParser.IsKeyword(a)).Select(ArgParser.Literal).FirstOrDefault(l => l is not null)
                   ?? (ArgParser.Keyword(args, "name") is { } n ? ArgParser.Literal(n) : null);
        }

        // ── 第一遍：Typer 应用、click group、挂载关系 ──
        var typerApps = new HashSet<(string, string)>();
        var groups = new Dictionary<(string File, string Name), (string Name, (string, string)? Parent)>(); // click group 函数 → (命令名, 父 group)
        var mounts = new Dictionary<(string, string), ((string, string) Parent, string? Name)>();          // 子（Typer 应用 / click 命令或 group）→ 父 + 名字
        var decorated = new List<(string File, MapSymbol Fn, string Text, Match M)>();
        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            if (!Array.Exists(lines, l => l.Contains("click", StringComparison.Ordinal) || l.Contains("typer", StringComparison.OrdinalIgnoreCase) || l.Contains(".command(", StringComparison.Ordinal))) continue;
            for (var i = 0; i < lines.Length; i++)
            {
                if (PyTyperDecl().Match(lines[i]) is { Success: true } t) typerApps.Add((file, t.Groups[1].Value));
                foreach (Match mount in PyCliMount().Matches(lines[i]))
                {
                    var hash = RouteLinker.PyCommentStart(lines[i]);
                    if (hash >= 0 && hash < mount.Index) continue;
                    var chunk = string.Join('\n', lines[i..Math.Min(lines.Length, i + 6)]);
                    var (args, _) = ArgParser.SplitArgs(chunk, mount.Index + mount.Length - 1);
                    var positional = args.Where(a => !ArgParser.IsKeyword(a)).ToList();
                    if (positional.Count == 0) continue;
                    var name = ArgParser.Keyword(args, "name") is { } n ? ArgParser.Literal(n) : positional.Count > 1 ? ArgParser.Literal(positional[1]) : null;
                    mounts[resolveRef(file, positional[0])] = (resolveRef(file, mount.Groups[1].Value), name);
                }
            }
            foreach (var (defLine, decorators) in Decorated(lines))
            {
                if (atLine.GetValueOrDefault((file, defLine)) is not { } fn) continue;
                foreach (var (text, _) in decorators)
                {
                    var m = PyCliDecorator().Match(text);
                    if (!m.Success) continue;
                    decorated.Add((file, fn, text, m));
                    if (m.Groups[2].Value == "group")
                    {
                        var receiver = m.Groups[1].Value;
                        (string, string)? parent = receiver == "click" ? null : resolveRef(file, receiver);
                        groups[(file, fn.Name)] = (ExplicitName(text, m.Index + m.Length - 1) ?? ClickName(fn.Name), parent);
                    }
                }
            }
        }

        // click：group 的路径（根 group 不进）
        string ClickPrefix((string, string)? group, int depth = 0)
        {
            if (group is not { } g || depth > 8) return "";
            (string, string)? parent = groups.TryGetValue(g, out var info) ? info.Parent : null;
            if (mounts.TryGetValue(g, out var mount)) parent = mount.Parent;
            if (parent is null) return ""; // 根
            var name = mounts.TryGetValue(g, out var mm) && mm.Name is not null ? mm.Name : info.Name ?? ClickName(g.Item2);
            return Join(ClickPrefix(parent, depth + 1), name);
        }
        // typer：应用的路径（根应用不进；add_typer 没写 name 的不加段）
        string TyperPrefix((string, string) app, int depth = 0)
        {
            if (depth > 8 || !mounts.TryGetValue(app, out var mount)) return "";
            return Join(TyperPrefix(mount.Parent, depth + 1), mount.Name ?? "");
        }
        static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

        foreach (var (file, fn, text, m) in decorated)
        {
            if (m.Groups[2].Value != "command") continue;
            var receiver = m.Groups[1].Value;
            var key = resolveRef(file, receiver);
            var lines = scan.ReadLines(file);
            var isTyper = typerApps.Contains(key) || (receiver != "click" && !groups.ContainsKey(key) && Array.Exists(lines, l => l.Contains("typer", StringComparison.Ordinal)));
            var explicitName = ExplicitName(text, m.Index + m.Length - 1);
            if (isTyper)
            {
                AddCommand(fn, "python.typer", Join(TyperPrefix(key), explicitName ?? fn.Name.Replace('_', '-')));
                continue;
            }
            var own = (file, fn.Name);
            var name = mounts.TryGetValue(own, out var mount) && mount.Name is not null ? mount.Name : explicitName ?? ClickName(fn.Name);
            (string, string)? parent = receiver == "click" ? (mounts.TryGetValue(own, out var mo) ? mo.Parent : null) : key;
            AddCommand(fn, "python.click", Join(ClickPrefix(parent), name));
        }

        foreach (var file in files)
        {
            var lines = scan.ReadLines(file);
            // ── typer.run(main) ──
            for (var i = 0; i < lines.Length; i++)
                if (PyTyperRun().Match(lines[i]) is { Success: true } run && functionNamed(run.Groups[1].Value, file, file) is { } f)
                    AddCommand(f, "python.typer", f.Name.Replace('_', '-'));

            // ── argparse：按出现顺序处理（同一个变量名常被复用给每个子命令）──
            if (!Array.Exists(lines, l => l.Contains("add_parser", StringComparison.Ordinal))) continue;
            var text = string.Join('\n', lines.Select(l => RouteLinker.PyCommentStart(l) is var h and >= 0 ? l[..h] : l));
            var events = new List<(int Index, int Kind, Match M)>();
            foreach (Match m in PyAddSubparsers().Matches(text)) events.Add((m.Index, 0, m));
            foreach (Match m in PyAddParser().Matches(text)) events.Add((m.Index, 1, m));
            foreach (Match m in PySetDefaults().Matches(text)) events.Add((m.Index, 2, m));
            var subparsersOf = new Dictionary<string, string>(StringComparer.Ordinal); // subparsers 变量 → 所属 parser 变量
            var parserPath = new Dictionary<string, string>(StringComparer.Ordinal);   // parser 变量 → 命令路径（根 parser 没有）
            foreach (var (index, kind, m) in events.OrderBy(e => e.Index))
            {
                if (kind == 0)
                {
                    if (m.Groups[1].Success) subparsersOf[m.Groups[1].Value] = m.Groups[2].Value;
                    continue;
                }
                var (args, close) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                if (kind == 1)
                {
                    var name = args.Where(a => !ArgParser.IsKeyword(a)).Select(ArgParser.Literal).FirstOrDefault(l => l is not null);
                    if (name is null) continue;
                    var owner = subparsersOf.GetValueOrDefault(m.Groups[2].Value);
                    var path = Join(owner is not null ? parserPath.GetValueOrDefault(owner) ?? "" : "", name);
                    if (m.Groups[1].Success) parserPath[m.Groups[1].Value] = path;
                    // sub.add_parser("x").set_defaults(func=f) 链在同一个表达式上
                    if (close >= 0 && Regex.Match(text[(close + 1)..], @"^\s*\.\s*set_defaults\s*\(") is { Success: true } chained)
                    {
                        var (dargs, _) = ArgParser.SplitArgs(text, close + 1 + chained.Length - 1);
                        AddDefaults(dargs, path, file);
                    }
                    continue;
                }
                if (parserPath.TryGetValue(m.Groups[1].Value, out var p)) AddDefaults(args, p, file);
            }
        }

        void AddDefaults(List<string> args, string path, string file)
        {
            foreach (var a in args.Where(ArgParser.IsKeyword))
            {
                var value = a[(a.IndexOf('=') + 1)..].Trim();
                if (!Regex.IsMatch(value, @"^[A-Za-z_][\w.]*$") || value is "True" or "False" or "None") continue;
                var (defFile, name) = resolveRef(file, value);
                if (functionNamed(name, defFile, file) is { } f) { AddCommand(f, "python.argparse", path); return; }
            }
        }
    }
}
