using System.Text;
using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Architecture.Rules;
using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <summary>
/// C# 宿主注册：常驻服务 / Hub / Quartz 作业 / 共享库里的 Minimal API 映射，按"哪个容器的启动代码真的走到了注册调用"分配，
/// 不再按"类在不在容器的成员项目里"。
/// <para>
/// 注册点：<c>AddHostedService&lt;T&gt;()</c>、<c>AddHostedService(sp =&gt; new T(…))</c>、<c>Singleton&lt;IHostedService, T&gt;</c>、
/// <c>AddSingleton&lt;IHostedService&gt;(sp =&gt; (T)…)</c>、<c>MapHub&lt;T&gt;</c>、<c>AddJob&lt;T&gt;</c> / <c>ScheduleJob&lt;T&gt;</c> / <c>JobBuilder.Create&lt;T&gt;</c>。
/// 从注册点所在方法沿 calls 边往上找调用方，走到某个容器自己项目目录里的"顶"（容器目录里没有别的调用方的方法：顶级语句、Main、
/// Startup.ConfigureServices、IHostingStartup.Configure…）就算这个容器走得到。
/// </para>
/// <para>
/// 条件注册尽力判定：沿调用链把 bool 实参（字面量 true / false、或已知的上层 bool 形参）绑到下层方法的 bool 形参（含默认值），
/// 再看注册点 / 调用点外层的 <c>if (…)</c>（与前面的 <c>if (…) return</c>）在这组取值下是真、假还是判不出：
/// 假 → 这条链不注册；判不出 → 记为"条件注册"（入口照给，路由后缀写明）；全程为真 → 确定注册。
/// </para>
/// </summary>
public static partial class EntryDiscovery
{
    [GeneratedRegex(@"\bAddHostedService\s*<\s*(?:global::)?(?:[\w.]+\.)?(\w+)")]
    private static partial Regex HostedGenericSite();
    // AddHostedService(sp => new T(…)) / AddHostedService(sp => sp.GetRequiredService<T>())：实参里认得出的常驻服务类名
    [GeneratedRegex(@"\bAddHostedService\s*\(")]
    private static partial Regex HostedFactorySite();
    // ServiceDescriptor.Singleton<IHostedService, T>() / services.AddSingleton<IHostedService, T>() / TryAddEnumerable(…)
    [GeneratedRegex(@"<\s*(?:global::)?(?:[\w.]+\.)?IHostedService\s*,\s*(?:[\w.]+\.)?(\w+)")]
    private static partial Regex HostedDescriptorSite();
    // services.AddSingleton<IHostedService>(sp => (T)sp.GetRequiredService<IEventBus>())
    [GeneratedRegex(@"\b(?:Add|TryAdd)?(?:Singleton|Transient|Scoped)\s*<\s*(?:global::)?(?:[\w.]+\.)?IHostedService\s*>\s*\(")]
    private static partial Regex HostedServiceFactorySite();
    [GeneratedRegex(@"\bMapHub\s*<\s*(?:global::)?(?:[\w.]+\.)?(\w+)")]
    private static partial Regex MapHubSite();
    [GeneratedRegex(@"\b(?:AddJob|ScheduleJob|JobBuilder\s*\.\s*Create)\s*<\s*(?:global::)?(?:[\w.]+\.)?(\w+)")]
    private static partial Regex QuartzJobSite();
    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifier();

    /// <summary>注册点：Kind = hosted / hub / job；Index 是在文件文本里的下标（判断外层 if 用）。</summary>
    private sealed record HostSite(string Kind, string Type, string File, int Index, int Line);

    /// <summary>一个类型在各容器里的注册结论：Definite = 有一条全程为真的链；否则是"条件注册"。</summary>
    private enum HostReg { Conditional, Definite }

    /// <summary>
    /// 宿主注册扫描：注册点、反向调用链与条件判定。一次 Discover 建一个，按需扫文件。
    /// </summary>
    private sealed class CsHosts
    {
        private readonly IReadOnlyDictionary<long, MapSymbol> _byId;
        private readonly ILookup<long, MapEdge> _callers;
        private readonly ILookup<string, MapSymbol> _callablesByFile;
        private readonly List<ArchNode> _hosts;
        private readonly Func<string, string[]> _readLines;
        private readonly Dictionary<string, CsText> _texts = new(StringComparer.Ordinal);
        private readonly Dictionary<long, BoolParams> _params = [];
        private readonly Dictionary<(long, int), Dictionary<string, HostReg>> _reachCache = [];

        public readonly List<HostSite> Sites = [];

        public CsHosts(IReadOnlyList<MapSymbol> symbols, IReadOnlyList<MapEdge> edges, IReadOnlyList<ArchNode> containers,
            IReadOnlyDictionary<long, MapSymbol> byId, Func<string, string[]> readLines, IReadOnlyList<string> csFiles, Func<string, bool> isHostedName)
        {
            _byId = byId;
            _readLines = readLines;
            _hosts = containers.Where(c => c.Kind == ArchNodeKind.Container && c.Path is not null && HostSubKinds.Contains(c.SubKind)).ToList();
            var callables = symbols.Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function && s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                               && !PipelineBuilder.IsTest(s.Path, s.QualifiedName)).ToList();
            _callablesByFile = callables.ToLookup(s => s.Path, StringComparer.Ordinal);
            _callers = edges.Where(e => e.Kind == EdgeKind.Calls && e.FromId != e.ToId).ToLookup(e => e.ToId);

            foreach (var file in csFiles)
            {
                var lines = readLines(file);
                if (!Array.Exists(lines, l => l.Contains("HostedService", StringComparison.Ordinal) || l.Contains("MapHub", StringComparison.Ordinal)
                                               || l.Contains("AddJob", StringComparison.Ordinal) || l.Contains("ScheduleJob", StringComparison.Ordinal) || l.Contains("JobBuilder", StringComparison.Ordinal)))
                    continue;
                var t = Text(file);
                void Site(string kind, string type, int index) { if (type.Length > 0) Sites.Add(new HostSite(kind, type, file, index, t.LineOf(index))); }
                foreach (Match m in HostedGenericSite().Matches(t.Masked)) Site("hosted", m.Groups[1].Value, m.Index);
                foreach (Match m in HostedDescriptorSite().Matches(t.Masked)) Site("hosted", m.Groups[1].Value, m.Index);
                foreach (var factory in HostedFactorySite().Matches(t.Masked).Concat(HostedServiceFactorySite().Matches(t.Masked)))
                {
                    var open = factory.Index + factory.Length - 1;
                    var close = MatchMasked(t.Masked, open);
                    if (close < 0) continue;
                    foreach (Match id in Identifier().Matches(t.Masked[open..close]))
                        if (isHostedName(id.Value)) Site("hosted", id.Value, factory.Index);
                }
                foreach (Match m in MapHubSite().Matches(t.Masked)) Site("hub", m.Groups[1].Value, m.Index);
                foreach (Match m in QuartzJobSite().Matches(t.Masked)) Site("job", m.Groups[1].Value, m.Index);
            }
        }

        public CsText Text(string file)
        {
            if (_texts.TryGetValue(file, out var t)) return t;
            return _texts[file] = new CsText(string.Join('\n', _readLines(file)));
        }

        /// <summary>包含这一行的最内层方法 / 函数（顶级语句、局部函数都算）。</summary>
        public MapSymbol? Enclosing(string file, int line) =>
            _callablesByFile[file].Where(s => s.StartLine <= line && line <= s.EndLine).OrderByDescending(s => s.StartLine).ThenBy(s => s.EndLine).FirstOrDefault();

        public bool HasSites(string kind) => Sites.Exists(s => s.Kind == kind);

        /// <summary>
        /// 某类注册（kind）里点名 type 的注册点，汇总成"容器 → 结论"。注册点一个都没有返回 null；有注册点但没有容器走得到返回空表。
        /// </summary>
        public Dictionary<string, HostReg>? Registrations(string kind, string type)
        {
            Dictionary<string, HostReg>? result = null;
            foreach (var site in Sites.Where(s => s.Kind == kind && s.Type == type))
            {
                result ??= new Dictionary<string, HostReg>(StringComparer.Ordinal);
                if (Enclosing(site.File, site.Line) is not { } method) continue;
                foreach (var (key, reg) in Reaching(method, site.Index)) Merge(result, key, reg);
            }
            return result;
        }

        private static void Merge(Dictionary<string, HostReg> into, string key, HostReg reg)
        {
            if (!into.TryGetValue(key, out var old) || reg > old) into[key] = reg;
        }

        /// <summary>
        /// 哪些容器的启动代码走得到 method 里下标 pos 处的那句（注册 / 映射调用）。反向沿 calls 边找调用方（深 12 层、总步数有上限），
        /// 到每个容器目录里的"顶"就按整条链正向判一次条件。
        /// </summary>
        public Dictionary<string, HostReg> Reaching(MapSymbol method, int pos)
        {
            if (_reachCache.TryGetValue((method.Id, pos), out var cached)) return cached;
            var result = new Dictionary<string, HostReg>(StringComparer.Ordinal);
            var chain = new List<(MapSymbol Caller, MapEdge Edge)>(); // 自顶向下：Caller 经 Edge 调下一层
            var onPath = new HashSet<long> { method.Id };
            var budget = 5000;

            void Walk(MapSymbol x, int depth)
            {
                if (--budget < 0) return;
                var callers = _callers[x.Id]
                    .Select(e => (Edge: e, From: _byId.GetValueOrDefault(e.FromId)))
                    .Where(c => c.From is { Kind: SymbolKind.Method or SymbolKind.Function } f && !PipelineBuilder.IsTest(f.Path, f.QualifiedName))
                    .Select(c => (c.Edge, From: c.From!)).ToList();
                foreach (var host in _hosts)
                {
                    if (!RuleContext.Under(x.Path, host.Path!)) continue;
                    if (callers.Exists(c => RuleContext.Under(c.From.Path, host.Path!))) continue; // 容器目录里还有上层调用方：不是顶
                    if (Evaluate(x, chain, method, pos) is { } reg) Merge(result, host.Key, reg);
                }
                if (depth >= 12) return;
                foreach (var (edge, from) in callers)
                {
                    if (!onPath.Add(from.Id)) continue;
                    chain.Insert(0, (from, edge));
                    Walk(from, depth + 1);
                    chain.RemoveAt(0);
                    onPath.Remove(from.Id);
                }
            }

            Walk(method, 0);
            return _reachCache[(method.Id, pos)] = result;
        }

        /// <summary>顶 top 沿 chain 调到 method、在 pos 处注册：假 → null；判不出 → Conditional；全真 → Definite。</summary>
        private HostReg? Evaluate(MapSymbol top, List<(MapSymbol Caller, MapEdge Edge)> chain, MapSymbol method, int pos)
        {
            var env = new Dictionary<string, bool>(StringComparer.Ordinal);
            var uncertain = false;
            for (var i = 0; i < chain.Count; i++)
            {
                var (caller, edge) = chain[i];
                var callee = i + 1 < chain.Count ? chain[i + 1].Caller : method;
                var call = FindCall(caller, edge.Line, callee.Name);
                var guard = GuardValue(caller, call?.Name ?? Text(caller.Path).LineStart(edge.Line), env);
                if (guard == false) return null;
                if (guard is null) uncertain = true;
                env = call is { } c ? Bind(caller, env, callee, c) : [];
            }
            var site = GuardValue(method, pos, env);
            if (site == false) return null;
            return uncertain || site is null ? HostReg.Conditional : HostReg.Definite;
        }

        private readonly record struct CallAt(int Name, int Open);

        /// <summary>caller 里 line 行起（往下 4 行内）对 name 的调用：返回名字与左括号下标。泛型实参可以跨行。</summary>
        private CallAt? FindCall(MapSymbol caller, int line, string name)
        {
            var t = Text(caller.Path);
            var from = t.LineStart(Math.Max(line, 1));
            var to = t.LineStart(line + 5);
            var rx = new Regex(@"\b" + Regex.Escape(name) + @"\b");
            for (var m = rx.Match(t.Masked, from); m.Success && m.Index < to; m = m.NextMatch())
            {
                var k = SkipWs(t.Masked, m.Index + m.Length);
                if (k < t.Masked.Length && t.Masked[k] == '<')
                {
                    var close = MatchAngle(t.Masked, k);
                    if (close < 0) continue;
                    k = SkipWs(t.Masked, close + 1);
                }
                if (k < t.Masked.Length && t.Masked[k] == '(') return new CallAt(m.Index, k);
            }
            return null;
        }

        /// <summary>bool 形参：位置、名字、默认值（字面量 true / false / default；没有默认值为 null）；IsExtension = 第一个形参带 this。</summary>
        private sealed record BoolParams(bool IsExtension, List<(int Index, string Name, bool? Default)> Bools, string? Owner);

        private BoolParams ParamsOf(MapSymbol m)
        {
            if (_params.TryGetValue(m.Id, out var p)) return p;
            var owner = m.ParentId is { } pid && _byId.TryGetValue(pid, out var o) ? o.Name : null;
            var t = Text(m.Path);
            var decl = FindCall(m, m.StartLine, m.Name) ?? FindDeclaration(m, t);
            var result = new BoolParams(false, [], owner);
            if (decl is { } d)
            {
                var (args, _) = SplitMasked(t.Masked, d.Open);
                var ext = false;
                var bools = new List<(int, string, bool?)>();
                for (var i = 0; i < args.Count; i++)
                {
                    var a = Regex.Replace(args[i], @"^\s*(?:\[[^\]]*\]\s*)*", "");
                    if (i == 0 && Regex.IsMatch(a, @"^this\s")) ext = true;
                    a = Regex.Replace(a, @"^(?:(?:this|params|ref|out|in|scoped|readonly)\s+)*", "");
                    var eq = a.IndexOf('=');
                    var left = (eq >= 0 ? a[..eq] : a).Trim();
                    var def = eq >= 0 ? a[(eq + 1)..].Trim() : null;
                    var nm = Regex.Match(left, @"(@?\w+)\s*$");
                    if (!nm.Success) continue;
                    var type = left[..nm.Index].Trim();
                    if (type is not ("bool" or "bool?" or "Boolean" or "Boolean?" or "System.Boolean")) continue;
                    bools.Add((i, nm.Groups[1].Value.TrimStart('@'), def switch { "true" => true, "false" or "default" => false, _ => null }));
                }
                result = new BoolParams(ext, bools, owner);
            }
            return _params[m.Id] = result;
        }

        private CallAt? FindDeclaration(MapSymbol m, CsText t)
        {
            // 声明可能在符号起始行之后几行（特性、文档注释占了前面的行）
            for (var line = m.StartLine + 5; line <= Math.Min(m.EndLine, m.StartLine + 30); line += 5)
                if (FindCall(m, line, m.Name) is { } c) return c;
            return null;
        }

        /// <summary>调用点的实参绑到被调方法的 bool 形参（命名实参 / 位置实参，扩展方法以接收者形式调用时位置往后挪一位）；没给的取默认值。</summary>
        private Dictionary<string, bool> Bind(MapSymbol caller, Dictionary<string, bool> env, MapSymbol callee, CallAt call)
        {
            var p = ParamsOf(callee);
            var next = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (p.Bools.Count == 0) return next;
            var t = Text(caller.Path);
            var (args, _) = SplitMasked(t.Masked, call.Open);
            var offset = 0;
            if (p.IsExtension)
            {
                var k = call.Name - 1;
                while (k >= 0 && char.IsWhiteSpace(t.Masked[k])) k--;
                if (k >= 0 && t.Masked[k] == '.')
                {
                    var q = Regex.Match(t.Masked[..k], @"(\w+)\s*$");
                    if (!(q.Success && q.Groups[1].Value == p.Owner)) offset = 1; // services.AddX(a)：a 对的是第二个形参
                }
            }
            var given = new Dictionary<string, bool?>(StringComparer.Ordinal);
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i];
                var named = Regex.Match(a, @"^\s*@?(\w+)\s*:(?!:)");
                if (named.Success)
                {
                    if (p.Bools.Exists(b => b.Name == named.Groups[1].Value)) given[named.Groups[1].Value] = Eval(a[named.Length..], env);
                    continue;
                }
                var slot = p.Bools.FindIndex(b => b.Index == i + offset);
                if (slot >= 0) given[p.Bools[slot].Name] = Eval(a, env);
            }
            foreach (var b in p.Bools)
            {
                if (given.TryGetValue(b.Name, out var v)) { if (v is { } known) next[b.Name] = known; }
                else if (b.Default is { } d) next[b.Name] = d;
            }
            return next;
        }

        /// <summary>method 体里 pos 处那句的外层条件在 env 下的值（三值：真 / 假 / 判不出）。</summary>
        public bool? GuardValue(MapSymbol method, int pos, Dictionary<string, bool> env)
        {
            var t = Text(method.Path);
            var from = t.LineStart(method.StartLine);
            var to = Math.Min(t.Masked.Length, t.LineStart(method.EndLine + 1));
            if (pos < from || pos >= to) return true;
            var guards = new List<(string Cond, bool Negate)>();
            CollectGuards(t.Masked, from, to, pos, guards);
            bool? value = true;
            foreach (var (cond, negate) in guards)
            {
                var v = Eval(cond, env);
                if (negate) v = v is { } b ? !b : null;
                value = And(value, v);
                if (value == false) return false;
            }
            return value;
        }
    }

    /// <summary>源码文本与"注释、字符串内容涂成空格"的同长副本（换行保留），扫括号 / 关键字都在副本上做。</summary>
    private sealed class CsText
    {
        public readonly string Text;
        public readonly string Masked;
        private readonly List<int> _starts = [0];

        public CsText(string text)
        {
            Text = text;
            Masked = CsMask(text);
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1);
        }

        /// <summary>第 line 行（1 起）的起点下标；越界取文本末尾。</summary>
        public int LineStart(int line) => line <= 1 ? 0 : line - 1 < _starts.Count ? _starts[line - 1] : Text.Length;

        public int LineOf(int index)
        {
            var i = _starts.BinarySearch(index);
            return (i >= 0 ? i : ~i - 1) + 1;
        }
    }

    /// <summary>C# 文本里的注释与字符串 / 字符字面量内容换成空格（引号本身留着；换行留着，下标与行号不变）。</summary>
    public static string CsMask(string text)
    {
        var c = text.ToCharArray();
        void Blank(int from, int to) { for (var k = Math.Max(from, 0); k < to && k < c.Length; k++) if (c[k] is not ('\n' or '\r')) c[k] = ' '; }
        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            var ch = text[i];
            if (ch == '/' && i + 1 < n && text[i + 1] == '/')
            {
                var e = text.IndexOf('\n', i);
                if (e < 0) e = n;
                Blank(i, e);
                i = e;
                continue;
            }
            if (ch == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? n : e + 2;
                Blank(i, e);
                i = e;
                continue;
            }
            if (ch is '"' or '@' or '$')
            {
                var j = i;
                var verbatim = false;
                while (j < n && text[j] is '@' or '$') { if (text[j] == '@') verbatim = true; j++; }
                if (j >= n || text[j] != '"' || (j > i && i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))) { i = j > i ? j : i + 1; continue; }
                var quotes = 0;
                while (j + quotes < n && text[j + quotes] == '"') quotes++;
                if (quotes >= 3)
                {
                    // 原始字符串 """…"""：到下一串同样多的引号为止
                    var fence = new string('"', quotes);
                    var e = text.IndexOf(fence, j + quotes, StringComparison.Ordinal);
                    e = e < 0 ? n : e;
                    Blank(j + quotes, e);
                    i = Math.Min(n, e + quotes);
                    continue;
                }
                if (quotes == 2) { i = j + 2; continue; } // 空串 ""
                var k = j + 1;
                while (k < n)
                {
                    if (!verbatim && text[k] == '\\') { k += 2; continue; }
                    if (text[k] == '"')
                    {
                        if (verbatim && k + 1 < n && text[k + 1] == '"') { k += 2; continue; }
                        break;
                    }
                    if (!verbatim && text[k] == '\n') break; // 没收尾的普通串：到行尾为止
                    k++;
                }
                Blank(j + 1, k);
                i = k + 1;
                continue;
            }
            if (ch == '\'')
            {
                var k = i + 1;
                if (k < n && text[k] == '\\') k += 2; else k++;
                var limit = Math.Min(n, i + 12);
                while (k < limit && text[k] != '\'' && text[k] != '\n') k++;
                if (k < n && text[k] == '\'') { Blank(i + 1, k); i = k + 1; }
                else i++;
                continue;
            }
            i++;
        }
        return new string(c);
    }

    private static int SkipWs(string t, int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        return i;
    }

    /// <summary>在已涂掉字符串 / 注释的文本上配括号：t[open] 是 ( [ {，返回配对的右括号；不配对 -1。</summary>
    private static int MatchMasked(string t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Length; i++)
        {
            switch (t[i])
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

    /// <summary>t[open] 是 '&lt;'：配对的 '&gt;'（只认尖括号与逗号、点、空白、标识符、问号、方括号，遇到别的字符说明不是泛型实参）。</summary>
    private static int MatchAngle(string t, int open)
    {
        var depth = 0;
        for (var i = open; i < t.Length; i++)
        {
            var ch = t[i];
            if (ch == '<') depth++;
            else if (ch == '>') { if (--depth == 0) return i; }
            else if (!(char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch is ',' or '.' or '_' or '?' or '[' or ']' or '(' or ')' or ':')) return -1;
        }
        return -1;
    }

    /// <summary>t[open] 是 '('：顶层逗号切开的实参（原样，去首尾空白）与右括号下标。</summary>
    private static (List<string> Args, int Close) SplitMasked(string t, int open)
    {
        var close = MatchMasked(t, open);
        var end = close < 0 ? t.Length : close;
        var args = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var i = open + 1; i < end; i++)
        {
            switch (t[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case '<' when IsGenericOpen(t, i): var g = MatchAngle(t, i); if (g > 0) i = g; break;
                case ',' when depth == 0:
                    args.Add(t[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        var last = t[start..end].Trim();
        if (last.Length > 0 || args.Count > 0) args.Add(last);
        return (args, close);
    }

    // Func<A, B> x：尖括号前是标识符、尖括号能配上且里面像类型，才当泛型（a < b, c > d 这类比较不会配上）
    private static bool IsGenericOpen(string t, int i) => i > 0 && (char.IsLetterOrDigit(t[i - 1]) || t[i - 1] == '_') && MatchAngle(t, i) > 0;

    private static bool KeywordAt(string t, int i, string word) =>
        i >= 0 && i + word.Length <= t.Length && string.CompareOrdinal(t, i, word, 0, word.Length) == 0
        && (i == 0 || !(char.IsLetterOrDigit(t[i - 1]) || t[i - 1] is '_' or '.' or '@'))
        && (i + word.Length == t.Length || !(char.IsLetterOrDigit(t[i + word.Length]) || t[i + word.Length] == '_'));

    /// <summary>
    /// [i, end) 段里位置 pos 之前、包住 pos 的 if 条件（Negate=false），pos 落在 else 里的（Negate=true），
    /// 以及同一层前面的 <c>if (c) return / throw …</c>（之后的代码相当于 !c，Negate=true）。只往包住 pos 的块里走。
    /// </summary>
    private static void CollectGuards(string t, int i, int end, int pos, List<(string Cond, bool Negate)> acc)
    {
        while (i < end && i < pos)
        {
            var ch = t[i];
            if (ch == 'i' && KeywordAt(t, i, "if"))
            {
                var open = SkipWs(t, i + 2);
                if (open >= end || t[open] != '(') { i += 2; continue; }
                var close = MatchMasked(t, open);
                if (close < 0) return;
                var cond = t[(open + 1)..close];
                var (bStart, bEnd) = StatementExtent(t, close + 1, end);
                if (pos >= bStart && pos < bEnd)
                {
                    acc.Add((cond, false));
                    CollectGuards(t, t[bStart] == '{' ? bStart + 1 : bStart, bEnd, pos, acc);
                    return;
                }
                var k = SkipWs(t, bEnd);
                if (KeywordAt(t, k, "else"))
                {
                    var (eStart, eEnd) = StatementExtent(t, k + 4, end);
                    if (pos >= eStart && pos < eEnd)
                    {
                        acc.Add((cond, true));
                        CollectGuards(t, t[eStart] == '{' ? eStart + 1 : eStart, eEnd, pos, acc);
                        return;
                    }
                    i = eEnd;
                    continue;
                }
                if (pos >= bEnd && IsExit(t, bStart, bEnd)) acc.Add((cond, true));
                i = bEnd;
                continue;
            }
            if (ch == '{')
            {
                var close = MatchMasked(t, i);
                if (close < 0) return;
                if (pos > i && pos < close) { i++; continue; } // 进这个块
                i = close + 1;
                continue;
            }
            i++;
        }
    }

    /// <summary>从 k 起的一条语句（块 / if … else … / 到顶层分号）的范围 [start, end)。</summary>
    private static (int Start, int End) StatementExtent(string t, int k, int end)
    {
        k = SkipWs(t, k);
        if (k >= end) return (end, end);
        if (t[k] == '{')
        {
            var close = MatchMasked(t, k);
            return (k, close < 0 ? end : close + 1);
        }
        if (KeywordAt(t, k, "if"))
        {
            var open = SkipWs(t, k + 2);
            var close = open < end && t[open] == '(' ? MatchMasked(t, open) : -1;
            if (close < 0) return (k, end);
            var (_, thenEnd) = StatementExtent(t, close + 1, end);
            var j = SkipWs(t, thenEnd);
            if (KeywordAt(t, j, "else")) return (k, StatementExtent(t, j + 4, end).End);
            return (k, thenEnd);
        }
        var depth = 0;
        for (var i = k; i < end; i++)
        {
            switch (t[i])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}':
                    if (--depth < 0) return (k, i);
                    break;
                case ';' when depth == 0: return (k, i + 1);
            }
        }
        return (k, end);
    }

    /// <summary>语句是不是"离开方法"：单句 return / throw，或块的最后一句是 return / throw。</summary>
    private static bool IsExit(string t, int start, int end)
    {
        var body = t[start..end].Trim();
        if (body.StartsWith('{')) body = body.TrimStart('{').TrimEnd('}').Trim();
        if (Regex.IsMatch(body, @"^(?:return|throw)\b")) return true;
        return Regex.IsMatch(body, @"(?:^|[;{}])\s*(?:return|throw)\b[^;{}]*;\s*$");
    }

    /// <summary>条件表达式三值求值：true / false 字面量、env 里的 bool 形参、! &amp;&amp; || 与括号；别的（比较、方法调用、成员）判不出。</summary>
    public static bool? Eval(string expr, IReadOnlyDictionary<string, bool> env)
    {
        var s = expr;
        var i = 0;
        void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        bool At(string op) { Ws(); return string.CompareOrdinal(s, i, op, 0, op.Length) == 0; }
        bool? Or()
        {
            var v = And_();
            while (At("||")) { i += 2; v = OrV(v, And_()); }
            return v;
        }
        bool? And_()
        {
            var v = Unary();
            while (At("&&")) { i += 2; v = And(v, Unary()); }
            return v;
        }
        bool? Unary()
        {
            Ws();
            if (i < s.Length && s[i] == '!' && (i + 1 >= s.Length || s[i + 1] != '=')) { i++; var v = Unary(); return v is { } b ? !b : null; }
            if (i < s.Length && s[i] == '(')
            {
                var start = i;
                i++;
                var v = Or();
                Ws();
                if (i < s.Length && s[i] == ')') i++;
                // (x).Foo / (x) == y 这类：括号后面还跟着别的运算 → 判不出
                Ws();
                if (i < s.Length && !At("&&") && !At("||") && s[i] != ')') { Atom(); return null; }
                _ = start;
                return v;
            }
            return Atom();
        }
        bool? Atom()
        {
            Ws();
            var start = i;
            var depth = 0;
            while (i < s.Length)
            {
                if (depth == 0 && (At("&&") || At("||"))) break;
                var ch = s[i];
                if (ch == '(') depth++;
                else if (ch == ')') { if (depth == 0) break; depth--; }
                i++;
            }
            var atom = s[start..i].Trim();
            if (atom == "true") return true;
            if (atom == "false") return false;
            return Regex.IsMatch(atom, @"^@?\w+$") && env.TryGetValue(atom.TrimStart('@'), out var b) ? b : null;
        }
        var result = Or();
        Ws();
        return i < s.Length ? null : result;
    }

    private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a == true && b == true ? true : null;
    private static bool? OrV(bool? a, bool? b) => a == true || b == true ? true : a == false && b == false ? false : null;

    /// <summary>类声明（起始行起 3 行内）带 abstract。</summary>
    private static bool IsAbstract(string[] lines, MapSymbol type)
    {
        var from = Math.Max(type.StartLine - 1, 0);
        var to = Math.Min(from + 3, lines.Length);
        if (from >= to) return false;
        return Regex.IsMatch(CsMask(string.Join('\n', lines[from..to])), @"\babstract\b[^{;]*\bclass\s+" + Regex.Escape(type.Name) + @"\b");
    }

    /// <summary>类名在它自己的文件之外的代码里（不算注释、字符串、测试）作为标识符出现过。</summary>
    private static bool ReferencedElsewhere(CsHosts hosts, MapSymbol type, IReadOnlyList<string> csFiles, Func<string, string[]> readLines)
    {
        var rx = new Regex(@"\b" + Regex.Escape(type.Name) + @"\b");
        foreach (var file in csFiles)
        {
            if (file == type.Path) continue;
            if (!Array.Exists(readLines(file), l => l.Contains(type.Name, StringComparison.Ordinal))) continue;
            if (rx.IsMatch(hosts.Text(file).Masked)) return true;
        }
        return false;
    }

    /// <summary>route 后缀：条件注册 / 找不到注册的常驻服务、Hub、作业照给，但写明。</summary>
    private const string ConditionalSuffix = "（条件注册）";
    private const string UnconfirmedSuffix = "（未确认宿主注册）";
}
