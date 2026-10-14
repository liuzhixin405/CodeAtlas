using System.Text;
using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Architecture;

/// <summary>规则种类（arch_rules.kind）。</summary>
public static class ArchRuleKind
{
    /// <summary>from 范围内的类型不得依赖 to。</summary>
    public const string ForbidDependency = "forbid-dependency";
    /// <summary>from 范围内的类型必须依赖 to。</summary>
    public const string RequireDependency = "require-dependency";
    /// <summary>实现接口 from 的类型应放在命名空间 to 下；只展示，不算违规。</summary>
    public const string Placement = "placement";
}

/// <summary>模式匹配方式（arch_rules.from_match / to_match）。</summary>
public static class ArchRuleMatch
{
    public const string Prefix = "prefix";
    public const string Contains = "contains";
    public const string Exact = "exact";
}

/// <summary>
/// 一条声明规则。来自测试项目里 NetArchTest 的 <c>Types.…</c> 链（Source 以 netarchtest 开头）或 architecture.yaml（Source = yaml）。
/// </summary>
/// <param name="FromPattern">主语范围：命名空间模式；placement 时是接口名；<c>*</c> 表示全部类型。</param>
/// <param name="ToPattern">目标命名空间模式。</param>
/// <param name="File">仓库相对路径。</param>
/// <param name="Line">链开始（<c>Types.</c>）所在行，1 起；yaml 规则为 0。</param>
/// <param name="TestName">所在测试方法名；yaml 规则为 null。</param>
/// <param name="Source">netarchtest / yaml；无法精确还原的地方在括号里注明。</param>
public sealed record ArchRule(string Kind, string FromPattern, string FromMatch, string? ToPattern, string? ToMatch, string File, int Line, string? TestName, string Source)
{
    public const string Any = "*";

    /// <summary>命名空间 ns 是否落在模式之内（ns 与模式同粒度或更细）。</summary>
    public static bool Matches(string ns, string pattern, string match)
    {
        if (pattern == Any) return true;
        return match switch
        {
            ArchRuleMatch.Contains => ns.Contains(pattern, StringComparison.Ordinal),
            ArchRuleMatch.Exact => ns == pattern,
            _ => ns == pattern || ns.StartsWith(pattern + ".", StringComparison.Ordinal),
        };
    }

    /// <summary>命名空间 ns 比 prefix 模式粗（模式在 ns 之下）：这一层判不了，要钻到更细才判。</summary>
    public static bool IsCoarserThan(string ns, string pattern, string match) =>
        match == ArchRuleMatch.Prefix && pattern != Any && pattern.StartsWith(ns + ".", StringComparison.Ordinal);
}

/// <summary>把 <c>typeof(X)</c> 里的类型名解析成命名空间 / 所在项目的主命名空间。解析不到返回 null。</summary>
public interface IArchTypeResolver
{
    /// <summary>类型所在的命名空间（<c>typeof(X).Namespace</c>）。</summary>
    string? NamespaceOf(string typeName);
    /// <summary>类型所在项目（程序集）的主命名空间（<c>typeof(X).Assembly</c>）。</summary>
    string? AssemblyRootOf(string typeName);
}

public sealed record ArchRuleParseResult(IReadOnlyList<ArchRule> Rules, int SkippedPredicates);

/// <summary>
/// 从一个 C# 测试文件里还原 NetArchTest.Rules 的链：<c>Types.InAssembly(…)/InNamespace(…).That().….Should()/ShouldNot().….GetResult()</c>。
/// 纯文本解析，不编译：链可能跨多行，字符串常量在同文件里先收集并替换。
/// </summary>
public static class ArchRuleParser
{
    private static readonly Regex ConstDecl = new(@"const\s+string\s+(\w+)\s*=\s*\$?""([^""]*)""\s*;", RegexOptions.Compiled);
    private static readonly Regex MethodDecl = new(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)\b[^;{(=]*?\b(\w+)\s*\([^()]*\)\s*\{?\s*$", RegexOptions.Compiled);
    private static readonly Regex UsingDecl = new(@"^\s*using\s+([\w.]+)\s*;", RegexOptions.Compiled);
    private static readonly Regex ChainStart = new(@"\bTypes\s*\.\s*In(Assembly|Namespace|CurrentDomain|Assemblies)\s*\(", RegexOptions.Compiled);

    public static ArchRuleParseResult Parse(string file, IReadOnlyList<string> lines, IArchTypeResolver? resolver = null)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        var usings = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = ConstDecl.Match(lines[i]);
            if (m.Success) constants[m.Groups[1].Value] = m.Groups[2].Value;
            var u = UsingDecl.Match(lines[i]);
            if (u.Success) usings.Add(u.Groups[1].Value);
        }
        // 常量里的插值（$"{ApplicationNamespace}.Behaviors"）再替换一轮
        foreach (var k in constants.Keys.ToList()) constants[k] = Interpolate(constants[k], constants);

        var rules = new List<ArchRule>();
        var skipped = 0;
        string? currentTest = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var md = MethodDecl.Match(lines[i].TrimEnd());
            if (md.Success && !lines[i].Contains("=>", StringComparison.Ordinal)) currentTest = md.Groups[1].Value;
            var start = ChainStart.Match(lines[i]);
            if (!start.Success || Rules.RuleContext.InCommentOrString(lines[i], start.Index)) continue;

            // 拼成一条语句：到第一个括号深度为 0 的分号为止
            var sb = new StringBuilder();
            var depth = 0;
            var end = -1;
            for (var j = i; j < lines.Count && end < 0; j++)
            {
                var text = j == i ? lines[i][start.Index..] : lines[j];
                var comment = text.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0 && !Rules.RuleContext.InCommentOrString(text, comment)) text = text[..comment];
                foreach (var ch in text)
                {
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    else if (ch == ';' && depth <= 0) { end = j; break; }
                    sb.Append(ch);
                }
                sb.Append(' ');
            }
            if (end < 0) continue;
            var statement = sb.ToString();
            var calls = ReadChain(statement);
            if (calls.Count == 0 || calls[^1].Name != "GetResult") { i = end; continue; }   // GetTypes() 这种枚举不是规则

            var (produced, skippedHere) = Interpret(calls, file, i + 1, currentTest, constants, usings, resolver);
            rules.AddRange(produced);
            skipped += skippedHere;
            i = end;
        }
        return new ArchRuleParseResult(rules, skipped);
    }

    private sealed record Call(string Name, string Args);

    /// <summary>把 <c>Types.A(x).B().C(y)</c> 读成调用列表；括号配平，参数原文保留。</summary>
    private static List<Call> ReadChain(string statement)
    {
        var calls = new List<Call>();
        var i = statement.IndexOf("Types", StringComparison.Ordinal);
        if (i < 0) return calls;
        i += "Types".Length;
        while (i < statement.Length)
        {
            while (i < statement.Length && (char.IsWhiteSpace(statement[i]) || statement[i] == '.')) i++;
            var nameStart = i;
            while (i < statement.Length && (char.IsLetterOrDigit(statement[i]) || statement[i] == '_')) i++;
            if (i == nameStart) break;
            var name = statement[nameStart..i];
            while (i < statement.Length && char.IsWhiteSpace(statement[i])) i++;
            if (i >= statement.Length || statement[i] != '(') { calls.Add(new Call(name, "")); continue; }   // .Assembly 这种属性
            var depth = 0;
            var argStart = i + 1;
            var inString = false;
            for (; i < statement.Length; i++)
            {
                var ch = statement[i];
                if (ch == '"' && (i == 0 || statement[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) break;
            }
            if (i >= statement.Length) break;
            calls.Add(new Call(name, statement[argStart..i].Trim()));
            i++;
        }
        return calls;
    }

    private static (List<ArchRule> Rules, int Skipped) Interpret(List<Call> calls, string file, int line, string? test, Dictionary<string, string> constants, List<string> usings, IArchTypeResolver? resolver)
    {
        var rules = new List<ArchRule>();
        var skipped = 0;
        var notes = new List<string>();

        // 主语范围
        string fromPattern = ArchRule.Any;
        var fromMatch = ArchRuleMatch.Prefix;
        var interfaces = new List<string>();
        bool? shouldPositive = null; // Should() = true，ShouldNot() = false

        string? Resolve(string arg)
        {
            var v = ResolveArg(arg, constants, resolver, usings, out var note);
            if (note is not null) notes.Add(note);
            return v;
        }

        void Narrow(string pattern, string match)
        {
            if (fromPattern == ArchRule.Any) { fromPattern = pattern; fromMatch = match; return; }
            // 已有前缀范围时，更具体的前缀覆盖；contains 这种拼不进单个模式的只注明
            if (match == ArchRuleMatch.Prefix && fromMatch == ArchRuleMatch.Prefix && pattern.StartsWith(fromPattern, StringComparison.Ordinal)) { fromPattern = pattern; return; }
            notes.Add($"主语另限 {match} {pattern}");
        }

        for (var idx = 0; idx < calls.Count; idx++)
        {
            var c = calls[idx];
            var args = SplitArgs(c.Args);
            switch (c.Name)
            {
                case "InAssembly":
                {
                    var typeName = TypeOfName(c.Args);
                    var root = typeName is null ? null : resolver?.AssemblyRootOf(typeName);
                    root ??= GuessAssemblyFromUsings(constants, usings);
                    if (root is null) notes.Add($"InAssembly({c.Args}) 的程序集无法解析，按全部类型");
                    else fromPattern = root;
                    break;
                }
                case "InNamespace":
                {
                    var ns = args.Count > 0 ? Resolve(args[0]) : null;
                    if (ns is null) notes.Add($"InNamespace({c.Args}) 无法解析，按全部类型");
                    else { fromPattern = ns; fromMatch = ArchRuleMatch.Prefix; }
                    break;
                }
                case "InCurrentDomain" or "InAssemblies":
                    notes.Add($"{c.Name} 按全部类型");
                    break;
                case "That" or "And" or "Or" or "GetResult":
                    break;
                case "Should": shouldPositive = true; break;
                case "ShouldNot": shouldPositive = false; break;
                default:
                    if (shouldPositive is null) { InterpretSubject(c, args); }
                    else { InterpretCondition(c, args); }
                    break;
            }
        }
        return (rules, skipped);

        void InterpretSubject(Call c, List<string> args)
        {
            var name = c.Name;
            var not = name.StartsWith("DoNot", StringComparison.Ordinal);
            if (not) name = name["DoNot".Length..];
            switch (name)
            {
                case "ImplementInterface":
                {
                    var t = TypeOfName(c.Args);
                    if (t is null) break;
                    if (not) notes.Add($"排除实现 {t} 的类型"); else interfaces.Add(t);
                    break;
                }
                case "ResideInNamespace" or "ResideInNamespaceStartingWith" or "ResideInNamespaceContaining" or "ResideInNamespaceEndingWith" or "ResideInNamespaceMatching":
                {
                    var v = args.Count > 0 ? Resolve(args[0]) : null;
                    if (v is null) break;
                    if (not) { notes.Add($"排除命名空间 {v}"); break; }
                    Narrow(v, name is "ResideInNamespace" or "ResideInNamespaceStartingWith" ? ArchRuleMatch.Prefix : ArchRuleMatch.Contains);
                    break;
                }
                default:
                    // HaveNameEndingWith / AreClasses / Inherit… 这类过滤只注明：命名空间粒度的 DSM 表达不了
                    notes.Add($"主语另限 {c.Name}");
                    break;
            }
        }

        void InterpretCondition(Call c, List<string> args)
        {
            var positive = shouldPositive!.Value;
            var name = c.Name;
            if (name.StartsWith("Not", StringComparison.Ordinal)) { positive = !positive; name = name[3..]; }
            switch (name)
            {
                case "HaveDependencyOn" or "HaveDependencyOnAny" or "HaveDependencyOnAll":
                {
                    var kind = positive ? ArchRuleKind.RequireDependency : ArchRuleKind.ForbidDependency;
                    var targets = args.Select(Resolve).Where(t => t is not null).Select(t => t!).ToList();
                    if (targets.Count == 0) { skipped++; break; }
                    foreach (var t in targets) rules.Add(new ArchRule(kind, fromPattern, fromMatch, t, ArchRuleMatch.Prefix, file, line, test, Source()));
                    break;
                }
                case "ResideInNamespace" or "ResideInNamespaceStartingWith" or "ResideInNamespaceContaining" or "ResideInNamespaceEndingWith":
                {
                    if (!positive) { skipped++; break; }   // ShouldNot().ResideInNamespace：命名空间“不该在哪”，DSM 不展示
                    var v = args.Count > 0 ? Resolve(args[0]) : null;
                    if (v is null) { skipped++; break; }
                    var match = name is "ResideInNamespace" or "ResideInNamespaceStartingWith" ? ArchRuleMatch.Prefix : ArchRuleMatch.Contains;
                    var subjects = interfaces.Count > 0 ? interfaces : [fromPattern];
                    foreach (var s in subjects)
                        rules.Add(new ArchRule(ArchRuleKind.Placement, s, interfaces.Count > 0 ? ArchRuleMatch.Exact : fromMatch, v, match, file, line, test, Source()));
                    break;
                }
                default:
                    skipped++;   // BeSealed / HaveNameEndingWith / OnlyHaveDependencyOn …
                    break;
            }
        }

        string Source()
        {
            var parts = new List<string>();
            if (interfaces.Count > 0) parts.Add("主语限于实现 " + string.Join(" / ", interfaces) + " 的类型");
            parts.AddRange(notes.Distinct());
            return parts.Count == 0 ? "netarchtest" : $"netarchtest（{string.Join("；", parts)}）";
        }
    }

    /// <summary>参数：字符串字面量 / 插值字符串 / 常量名 / typeof(X).Namespace。解析不到返回 null 并给出说明。</summary>
    private static string? ResolveArg(string arg, Dictionary<string, string> constants, IArchTypeResolver? resolver, List<string> usings, out string? note)
    {
        note = null;
        arg = arg.Trim();
        if (arg.StartsWith("$\"", StringComparison.Ordinal) && arg.EndsWith('"')) return Interpolate(arg[2..^1], constants);
        if (arg.StartsWith('"') && arg.EndsWith('"') && arg.Length >= 2) return arg[1..^1];
        if (constants.TryGetValue(arg, out var value)) return value;
        var t = TypeOfName(arg);
        if (t is not null && arg.EndsWith(".Namespace", StringComparison.Ordinal))
        {
            var ns = resolver?.NamespaceOf(t);
            if (ns is null) note = $"typeof({t}).Namespace 无法解析";
            return ns;
        }
        if (arg.Contains("nameof(", StringComparison.Ordinal))
        {
            var inner = arg[(arg.IndexOf('(') + 1)..arg.LastIndexOf(')')].Trim();
            return inner.Contains('.') ? inner[(inner.LastIndexOf('.') + 1)..] : inner;
        }
        note = $"参数 {arg} 无法解析";
        return null;
    }

    /// <summary><c>typeof(ICommandHandler&lt;,&gt;)</c> → ICommandHandler；带命名空间限定时取最后一段。</summary>
    internal static string? TypeOfName(string text)
    {
        var m = Regex.Match(text, @"typeof\s*\(\s*([\w.]+)\s*(?:<[^)]*>)?\s*\)");
        if (!m.Success) return null;
        var name = m.Groups[1].Value;
        return name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
    }

    /// <summary>解析不了程序集时的兜底：文件里恰有一个常量是某条 using 的前缀，就当它是被测程序集的根命名空间。</summary>
    private static string? GuessAssemblyFromUsings(Dictionary<string, string> constants, List<string> usings)
    {
        var hits = constants.Values.Distinct().Where(v => v.Length > 0 && usings.Any(u => u == v || u.StartsWith(v + ".", StringComparison.Ordinal))).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private static string Interpolate(string template, Dictionary<string, string> constants) =>
        Regex.Replace(template, @"\{(\w+)\}", m => constants.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>顶层逗号切参数（括号与字符串里的逗号不切）。</summary>
    internal static List<string> SplitArgs(string args)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return list;
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var ch = args[i];
            if (ch == '"' && (i == 0 || args[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (ch is '(' or '<') depth++;
            else if (ch is ')' or '>') depth--;
            else if (ch == ',' && depth == 0) { list.Add(args[start..i].Trim()); start = i + 1; }
        }
        list.Add(args[start..].Trim());
        return list;
    }
}

/// <summary>仓库级扫描：测试项目里的 NetArchTest 链 + architecture.yaml 的 layers / rules。</summary>
public static class ArchRuleScanner
{
    public sealed record Report(IReadOnlyList<ArchRule> Rules, int SkippedPredicates, int FilesScanned);

    public static Report Scan(ProjectInventory inventory, IArchTypeResolver? resolver, Func<string, string[]> readLines, ArchitectureDeclarations? declarations)
    {
        var rules = new List<ArchRule>();
        var skipped = 0;
        var files = 0;
        var testDirs = inventory.Projects.Where(p => p.IsTest).Select(p => p.Dir).ToList();
        foreach (var file in inventory.SourceFiles)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (!testDirs.Any(d => Rules.RuleContext.Under(file, d))) continue;
            var lines = readLines(file);
            if (!lines.Any(l => l.Contains("Types.", StringComparison.Ordinal))) continue;
            files++;
            var r = ArchRuleParser.Parse(file, lines, resolver);
            rules.AddRange(r.Rules);
            skipped += r.SkippedPredicates;
        }
        if (declarations is not null) rules.AddRange(declarations.DependencyRules());
        return new Report(rules, skipped, files);
    }
}
