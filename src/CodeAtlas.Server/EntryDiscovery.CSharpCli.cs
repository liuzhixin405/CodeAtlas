using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// C# 命令行框架的命令入口（kind = command，route = 命令路径，子命令用空格连：<c>remote add</c>；根命令 / 默认命令用容器名）：
/// System.CommandLine（<c>var x = new Command("index")</c> + <c>x.SetHandler(…)</c> / <c>SetAction(…)</c> / <c>x.Handler = CommandHandler.Create(…)</c>，
/// AddCommand / Subcommands.Add / 集合初始化器给出父子；<c>class X : Command</c> 子类的 <c>base("index")</c>）、
/// Spectre.Console.Cli（<c>AddCommand&lt;T&gt;("x")</c>、<c>AddBranch("x", b =&gt; …)</c> 前缀、<c>CommandApp&lt;T&gt;</c> / SetDefaultCommand 默认命令；
/// 没注册的 <c>Command&lt;TSettings&gt;</c> 子类按类名推命令名）、CommandLineParser（<c>[Verb("add")]</c> 选项类 → WithParsed / MapResult 里的处理方法，
/// 或参数是该选项类的方法）。处理器是方法组就落在那个方法上；是 lambda 就取 lambda 里第一个能在仓库里找到的方法调用，找不到退回包含它的方法。
/// 入口的 File / Line 是处理方法的位置。没有框架、手写 switch 分派的程序走 csharp.main。
/// </summary>
public static partial class EntryDiscovery
{
    // var index = new Command("index", "…")   index = new Command(name: "index")
    [GeneratedRegex(@"\b(\w+)\s*=\s*new\s+(?:System\.CommandLine\.)?Command\s*\(\s*(?:name\s*:\s*)?""([^""]+)""")]
    private static partial Regex ScCommandDecl();
    // Command index = new("index")
    [GeneratedRegex(@"\bCommand\s+(\w+)\s*=\s*new\s*\(\s*(?:name\s*:\s*)?""([^""]+)""")]
    private static partial Regex ScCommandTargetTyped();
    // var root = new RootCommand("描述")   RootCommand root = new("描述")（RootCommand 的字符串是描述，不是名字）
    [GeneratedRegex(@"\b(\w+)\s*=\s*new\s+(?:System\.CommandLine\.)?RootCommand\s*\(|\bRootCommand\s+(\w+)\s*=\s*new\s*\(")]
    private static partial Regex ScRootDecl();
    [GeneratedRegex(@"(?:\b(\w+)\s*\.\s*|(?<![\w.]))(SetHandler|SetAction)\s*\(")]
    private static partial Regex ScSetHandler();
    [GeneratedRegex(@"(?:\b(\w+)\s*\.\s*|(?<![\w.]))Handler\s*=\s*(?!=)")]
    private static partial Regex ScHandlerAssign();
    // root.AddCommand(index)  root.Subcommands.Add(index)  root.Add(index)
    [GeneratedRegex(@"\b(\w+)\s*\.\s*(?:AddCommand|Subcommands\s*\.\s*Add|Add)\s*\(\s*(\w+)\s*\)")]
    private static partial Regex ScAddChild();
    [GeneratedRegex(@"\bclass\s+(\w+)[^{:]*:\s*(?:System\.CommandLine\.)?(Root)?Command\b(?!\s*<)")]
    private static partial Regex ScCommandSubclass();
    [GeneratedRegex(@":\s*base\s*\(\s*(?:name\s*:\s*)?""([^""]+)""")]
    private static partial Regex ScBaseName();
    [GeneratedRegex(@"^CommandHandler\s*\.\s*Create\s*(?:<[^()]*>)?\s*\(")]
    private static partial Regex ScCommandHandlerCreate();

    // config.AddCommand<AddCommand>("add")   branch.AddCommand<X>("x")
    [GeneratedRegex(@"\b(\w+)\s*\.\s*AddCommand\s*<\s*(?:[\w.]+\.)?(\w+)\s*>\s*\(\s*""([^""]+)""")]
    private static partial Regex SpectreAddCommand();
    // config.AddBranch("remote", remote => {   config.AddBranch<RemoteSettings>("remote", r => {
    [GeneratedRegex(@"\b(\w+)\s*\.\s*AddBranch\s*(?:<[^>]*>)?\s*\(\s*""([^""]+)""\s*,\s*(\w+)\s*=>")]
    private static partial Regex SpectreAddBranch();
    [GeneratedRegex(@"\bnew\s+CommandApp\s*<\s*(?:[\w.]+\.)?(\w+)\s*>|\bSetDefaultCommand\s*<\s*(?:[\w.]+\.)?(\w+)\s*>")]
    private static partial Regex SpectreDefault();
    [GeneratedRegex(@"\bclass\s+(\w+)[^{:]*:\s*(?:Spectre\.Console\.Cli\.)?(?:Async)?Command\s*<")]
    private static partial Regex SpectreSubclass();

    [GeneratedRegex(@"\[\s*(?:CommandLine\.)?Verb\s*\(\s*""([^""]+)""")]
    private static partial Regex VerbAttr();

    // lambda 体里的调用：Foo.Bar(  Bar(  await Bar<T>(
    [GeneratedRegex(@"(?:\b(\w+)\s*\.\s*)?\b(\w+)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex CallSite();

    private static readonly HashSet<string> NotHandlerCalls = new(StringComparer.Ordinal)
    {
        "new", "await", "nameof", "typeof", "sizeof", "default", "return", "if", "while", "for", "foreach", "switch", "using", "lock", "catch",
        "GetValue", "GetValueForOption", "GetValueForArgument", "GetRequiredValue", "ToString", "FromResult", "CompletedTask", "WriteLine", "Write",
    };
    private static readonly HashSet<string> NotHandlerQualifiers = new(StringComparer.Ordinal) { "Task", "Console", "Results", "Math", "string", "String", "Environment", "AnsiConsole" };

    private static void DiscoverCSharpCli(Scan scan)
    {
        var cs = scan.Symbols.Where(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && s.Kind != SymbolKind.External && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)).ToList();
        if (cs.Count == 0) return;
        var files = cs.Select(s => s.Path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var callables = cs.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function).ToList();
        var byFile = callables.ToLookup(s => s.Path);
        var byName = callables.ToLookup(s => s.Name, StringComparer.Ordinal);
        var classes = cs.Where(s => s.Kind is SymbolKind.Class or SymbolKind.Struct).ToList();
        string? ParentName(MapSymbol s) => s.ParentId is { } p && scan.ById.TryGetValue(p, out var o) ? o.Name : null;
        bool SameContainer(string a, string b)
        {
            var keys = scan.ContainersOf(a).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            return scan.ContainersOf(b).Any(c => keys.Contains(c.Key));
        }

        MapSymbol? Enclosing(string file, int line) =>
            byFile[file].Where(s => s.StartLine <= line && line <= s.EndLine).OrderByDescending(s => s.StartLine).FirstOrDefault();

        MapSymbol? ResolveMethod(string? typeName, string name, string fromFile)
        {
            var candidates = byName[name].ToList();
            if (typeName is not null && candidates.Any(c => ParentName(c) == typeName)) candidates = candidates.Where(c => ParentName(c) == typeName).ToList();
            else if (typeName is not null && char.IsUpper(typeName[0]) && !typeName.Equals("this", StringComparison.Ordinal)) candidates = candidates.Where(c => c.Path == fromFile).ToList();
            return candidates.OrderBy(c => c.Path == fromFile ? 0 : SameContainer(c.Path, fromFile) ? 1 : 2)
                .ThenBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.StartLine).FirstOrDefault();
        }

        MapSymbol? ClassNamed(string name, string fromFile) =>
            classes.Where(c => c.Name == name).OrderBy(c => c.Path == fromFile ? 0 : SameContainer(c.Path, fromFile) ? 1 : 2).ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault();

        // 处理器表达式 → 符号：方法组 / CommandHandler.Create(方法组) / lambda（第一个能解析的调用）；都不行退回包含注册的方法
        MapSymbol? HandlerOf(string expr, string file, int line)
        {
            expr = expr.Trim().TrimEnd(';').Trim();
            if (ScCommandHandlerCreate().Match(expr) is { Success: true } create)
            {
                var (inner, _) = ArgParser.SplitArgs(expr, create.Index + create.Length - 1);
                if (inner.Count > 0) expr = inner[0];
            }
            var arrow = expr.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                foreach (Match call in CallSite().Matches(expr[(arrow + 2)..]))
                {
                    var name = call.Groups[2].Value;
                    var qualifier = call.Groups[1].Success ? call.Groups[1].Value : null;
                    if (NotHandlerCalls.Contains(name) || (qualifier is not null && NotHandlerQualifiers.Contains(qualifier))) continue;
                    if (ResolveMethod(qualifier, name, file) is { } m) return m;
                }
                return Enclosing(file, line);
            }
            var group = Regex.Match(expr, @"^(?:new\s+\w+\s*\(\s*\)\s*\.\s*)?(?:([\w.]+)\s*\.\s*)?(\w+)$");
            if (group.Success)
            {
                var qualifier = group.Groups[1].Success ? group.Groups[1].Value.Split('.')[^1] : null;
                if (ResolveMethod(qualifier, group.Groups[2].Value, file) is { } m) return m;
            }
            return Enclosing(file, line);
        }

        void AddCommand(MapSymbol target, string ruleId, string route)
        {
            foreach (var c in scan.ContainersOf(target.Path))
                scan.Add(new EntryPoint(target.Id, c.Key, ruleId, "command", route, null, target.Path, target.StartLine));
        }
        string RootRoute(string file) => scan.ContainersOf(file).FirstOrDefault()?.Name ?? MapSymbol.FileName(file);

        var spectreRegistered = new HashSet<string>(StringComparer.Ordinal);
        var spectreFiles = new List<string>();
        foreach (var file in files)
        {
            if (scan.ContainersOf(file).Count == 0) continue;
            var lines = scan.ReadLines(file);
            var text = string.Join('\n', lines);
            int LineOf(int index) => text.AsSpan(0, index).Count('\n') + 1;
            bool Skip(int index) => InJavaComment(lines, text, index);

            // ── System.CommandLine：变量形式 ──
            if (text.Contains("Command", StringComparison.Ordinal) && (text.Contains("SetHandler", StringComparison.Ordinal) || text.Contains("SetAction", StringComparison.Ordinal) || text.Contains("Handler", StringComparison.Ordinal)))
            {
                var names = new Dictionary<string, string?>(StringComparer.Ordinal); // 变量 → 命令名；RootCommand 为 null
                var declEnds = new List<(string Var, int End)>();
                foreach (var m in ScCommandDecl().Matches(text).Concat(ScCommandTargetTyped().Matches(text)))
                {
                    if (Skip(m.Index)) continue;
                    names[m.Groups[1].Value] = m.Groups[2].Value;
                    declEnds.Add((m.Groups[1].Value, m.Index + m.Length));
                }
                foreach (Match m in ScRootDecl().Matches(text))
                {
                    if (Skip(m.Index)) continue;
                    var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    names[v] = null;
                    declEnds.Add((v, m.Index + m.Length));
                }
                if (names.Count > 0)
                {
                    var parent = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (Match m in ScAddChild().Matches(text))
                        if (!Skip(m.Index) && names.ContainsKey(m.Groups[1].Value) && names.ContainsKey(m.Groups[2].Value)) parent[m.Groups[2].Value] = m.Groups[1].Value;
                    // 集合初始化器：new RootCommand("…") { index, serve }
                    foreach (var (v, end) in declEnds)
                    {
                        var open = text.LastIndexOf('(', Math.Min(end, text.Length - 1));
                        var close = open >= 0 ? ArgParser.MatchClose(text, open) : -1;
                        if (close < 0) continue;
                        var k = close + 1;
                        while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
                        if (k >= text.Length || text[k] != '{') continue;
                        var (items, _) = ArgParser.SplitArgs(text, k);
                        foreach (var item in items)
                            if (names.ContainsKey(item) && item != v) parent[item] = v;
                    }
                    string? PathOf(string v)
                    {
                        var parts = new List<string>();
                        for (var (cur, depth) = (v, 0); depth < 8; depth++)
                        {
                            if (!names.TryGetValue(cur, out var n) || n is null) break;
                            parts.Insert(0, n);
                            if (!parent.TryGetValue(cur, out var p)) break;
                            cur = p;
                        }
                        return parts.Count == 0 ? null : string.Join(' ', parts);
                    }
                    void Register(string v, string expr, int index)
                    {
                        if (!names.ContainsKey(v)) return;
                        var line = LineOf(index);
                        if (HandlerOf(expr, file, line) is not { } h) return;
                        AddCommand(h, "csharp.system-commandline", PathOf(v) ?? RootRoute(file));
                    }
                    foreach (Match m in ScSetHandler().Matches(text))
                    {
                        if (Skip(m.Index) || !m.Groups[1].Success) continue;
                        var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                        if (args.Count > 0) Register(m.Groups[1].Value, args[0], m.Index);
                    }
                    foreach (Match m in ScHandlerAssign().Matches(text))
                    {
                        if (Skip(m.Index) || !m.Groups[1].Success) continue;
                        var end = text.IndexOf(';', m.Index + m.Length);
                        if (end > 0) Register(m.Groups[1].Value, text[(m.Index + m.Length)..end], m.Index);
                    }
                }
            }

            // ── System.CommandLine：子类形式 class IndexCommand : Command { ctor : base("index") { SetHandler(…) } } ──
            foreach (Match m in ScCommandSubclass().Matches(text))
            {
                if (Skip(m.Index)) continue;
                var cls = classes.FirstOrDefault(c => c.Path == file && c.Name == m.Groups[1].Value);
                if (cls is null) continue;
                var start = text.IndexOf('{', m.Index);
                var end = start >= 0 ? ArgParser.MatchClose(text, start) : -1;
                if (end < 0) continue;
                var body = text[m.Index..end];
                var baseName = ScBaseName().Match(body);
                var route = m.Groups[2].Success || !baseName.Success ? RootRoute(file) : baseName.Groups[1].Value;
                MapSymbol? handler = null;
                foreach (Match h in ScSetHandler().Matches(body))
                {
                    if (h.Groups[1].Success && h.Groups[1].Value != "this") continue;
                    var (args, _) = ArgParser.SplitArgs(body, h.Index + h.Length - 1);
                    if (args.Count > 0) { handler = HandlerOf(args[0], file, LineOf(m.Index + h.Index)); break; }
                }
                if (handler is null && ScHandlerAssign().Match(body) is { Success: true } ha && (!ha.Groups[1].Success || ha.Groups[1].Value == "this"))
                {
                    var stop = body.IndexOf(';', ha.Index + ha.Length);
                    if (stop > 0) handler = HandlerOf(body[(ha.Index + ha.Length)..stop], file, LineOf(m.Index + ha.Index));
                }
                // 嵌套的 Handler 类（ICommandHandler.InvokeAsync）或类自己的 Invoke / Execute / Run
                handler ??= byFile[file].Where(s => s.StartLine >= cls.StartLine && s.EndLine <= cls.EndLine && s.Name is "InvokeAsync" or "Invoke" or "ExecuteAsync" or "Execute" or "RunAsync" or "Run" or "HandleAsync" or "Handle")
                    .OrderBy(s => s.StartLine).FirstOrDefault();
                AddCommand(handler ?? cls, "csharp.system-commandline", route);
            }

            // ── Spectre.Console.Cli：注册 ──
            if (text.Contains("AddCommand", StringComparison.Ordinal) || text.Contains("CommandApp", StringComparison.Ordinal) || text.Contains("SetDefaultCommand", StringComparison.Ordinal))
            {
                // AddBranch 的 lambda 体可能从下一行的 { 开始：Open = 已经进了那层大括号
                var scopes = new List<(string Param, string Prefix, int Depth, bool Open, int Line)>();
                var braces = 0;
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    var depthBefore = braces;
                    string ScopePrefix(string receiver) => scopes.LastOrDefault(s => s.Param == receiver).Prefix ?? "";
                    // 同一行上的 AddBranch("x", b => b.AddCommand<T>("y"))：单行 lambda 的参数也算前缀
                    var inline = SpectreAddBranch().Matches(line).Select(b => (b.Index, Param: b.Groups[3].Value, Prefix: JoinCommand(ScopePrefix(b.Groups[1].Value), b.Groups[2].Value))).ToList();
                    string PrefixOf(string receiver, int at = int.MaxValue) =>
                        inline.LastOrDefault(b => b.Param == receiver && b.Index < at).Prefix ?? ScopePrefix(receiver);
                    foreach (Match m in SpectreAddCommand().Matches(line))
                    {
                        if (RuleContext.InCommentOrString(line, m.Index)) continue;
                        var typeName = m.Groups[2].Value;
                        spectreRegistered.Add(typeName);
                        var cls = ClassNamed(typeName, file);
                        if (cls is null) continue;
                        AddCommand(SpectreExecute(cls) ?? cls, "csharp.spectre-cli", JoinCommand(PrefixOf(m.Groups[1].Value, m.Index), m.Groups[3].Value));
                    }
                    foreach (Match m in SpectreDefault().Matches(line))
                    {
                        if (RuleContext.InCommentOrString(line, m.Index)) continue;
                        var typeName = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        spectreRegistered.Add(typeName);
                        if (ClassNamed(typeName, file) is { } cls) AddCommand(SpectreExecute(cls) ?? cls, "csharp.spectre-cli", RootRoute(file));
                    }
                    braces += BraceDelta(line);
                    foreach (Match m in SpectreAddBranch().Matches(line))
                    {
                        if (RuleContext.InCommentOrString(line, m.Index)) continue;
                        scopes.Add((m.Groups[3].Value, JoinCommand(ScopePrefix(m.Groups[1].Value), m.Groups[2].Value), depthBefore, braces > depthBefore, i));
                    }
                    for (var k = 0; k < scopes.Count; k++)
                        if (!scopes[k].Open && braces > scopes[k].Depth) scopes[k] = scopes[k] with { Open = true };
                    scopes.RemoveAll(s => (s.Open && braces <= s.Depth) || (!s.Open && i > s.Line)); // 单行 lambda（没有大括号）不留作用域
                }
            }
            if (text.Contains("Spectre.Console.Cli", StringComparison.Ordinal)) spectreFiles.Add(file);

            // ── CommandLineParser：[Verb("add")] class AddOptions ──
            if (text.Contains("Verb", StringComparison.Ordinal))
            {
                foreach (var cls in classes.Where(c => c.Path == file))
                {
                    var verb = AttributesAbove(lines, cls.StartLine).Select(a => VerbAttr().Match(a)).FirstOrDefault(x => x.Success);
                    if (verb is null) continue;
                    var handler = VerbHandler(cls.Name, file) ?? cls;
                    AddCommand(handler, "csharp.commandlineparser", verb.Groups[1].Value);
                }
            }
        }

        // Spectre：没找到注册的 Command<TSettings> 子类，按类名推命令名（AddCommand → add；GreetCommand → greet）
        foreach (var file in spectreFiles)
        {
            var lines = scan.ReadLines(file);
            var text = string.Join('\n', lines);
            foreach (Match m in SpectreSubclass().Matches(text))
            {
                if (InJavaComment(lines, text, m.Index) || spectreRegistered.Contains(m.Groups[1].Value)) continue;
                var cls = classes.FirstOrDefault(c => c.Path == file && c.Name == m.Groups[1].Value);
                if (cls is null || scan.ContainersOf(file).Count == 0) continue;
                var name = cls.Name.EndsWith("Command", StringComparison.Ordinal) && cls.Name.Length > "Command".Length ? cls.Name[..^"Command".Length] : cls.Name;
                AddCommand(SpectreExecute(cls) ?? cls, "csharp.spectre-cli", Kebab(name));
            }
        }

        MapSymbol? SpectreExecute(MapSymbol cls) =>
            scan.Children[cls.Id].Where(s => s.Kind == SymbolKind.Method && s.Name is "ExecuteAsync" or "Execute").OrderBy(s => s.StartLine).FirstOrDefault();

        // [Verb] 选项类的处理方法：WithParsed<Opt>(X) / MapResult((Opt o) => X(o), …) / 参数类型是 Opt 的方法
        MapSymbol? VerbHandler(string optionsType, string verbFile)
        {
            var withParsed = new Regex($@"\bWithParsed(?:Async)?\s*<\s*(?:[\w.]+\.)?{Regex.Escape(optionsType)}\s*>\s*\(");
            // 括号前不能是名字：int Commit(CommitOptions o) => 0 是表达式体方法，不是 lambda
            var lambda = new Regex($@"(?<![\w>\]]\s*)\(\s*(?:[\w.]+\.)?{Regex.Escape(optionsType)}\s+\w+\s*\)\s*=>");
            foreach (var file in files)
            {
                var lines = scan.ReadLines(file);
                var text = string.Join('\n', lines);
                if (!text.Contains(optionsType, StringComparison.Ordinal)) continue;
                foreach (Match m in withParsed.Matches(text))
                {
                    if (InJavaComment(lines, text, m.Index)) continue;
                    var (args, _) = ArgParser.SplitArgs(text, m.Index + m.Length - 1);
                    if (args.Count > 0 && HandlerOf(args[0], file, text.AsSpan(0, m.Index).Count('\n') + 1) is { } h) return h;
                }
                foreach (Match m in lambda.Matches(text))
                {
                    if (InJavaComment(lines, text, m.Index)) continue;
                    // 到同层的 , 或 ) 为止
                    var depth = 0;
                    var k = m.Index + m.Length;
                    for (; k < text.Length; k++)
                    {
                        var ch = text[k];
                        if (ch is '(' or '[' or '{') depth++;
                        else if (ch is ')' or ']' or '}') { if (depth == 0) break; depth--; }
                        else if ((ch == ',' || ch == ';') && depth == 0) break;
                    }
                    var body = text[m.Index..k];
                    foreach (Match call in CallSite().Matches(body[(body.IndexOf("=>", StringComparison.Ordinal) + 2)..]))
                    {
                        if (NotHandlerCalls.Contains(call.Groups[2].Value) || (call.Groups[1].Success && NotHandlerQualifiers.Contains(call.Groups[1].Value))) continue;
                        if (ResolveMethod(call.Groups[1].Success ? call.Groups[1].Value : null, call.Groups[2].Value, file) is { } h) return h;
                    }
                }
            }
            var param = new Regex($@"\(\s*(?:[\w.]+\.)?{Regex.Escape(optionsType)}\s+\w+\s*[,)]");
            return callables.Where(s => SameContainer(s.Path, verbFile) && param.IsMatch(DeclarationText(scan.ReadLines(s.Path), s)))
                .OrderBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.StartLine).FirstOrDefault();
        }
    }

    /// <summary>PascalCase / camelCase → kebab-case（RemoteAdd → remote-add）。</summary>
    internal static string Kebab(string name) =>
        Regex.Replace(name, @"(?<=[a-z0-9])([A-Z])|(?<=[A-Z])([A-Z][a-z])", "-$1$2").ToLowerInvariant();
}
