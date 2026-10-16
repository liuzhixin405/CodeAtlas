using System.Text.RegularExpressions;
using CodeAtlas.Core.Architecture.Rules;

namespace CodeAtlas.Core.Architecture;

/// <summary>
/// 共享库里的出口归给谁：规则按"文件属于哪些容器"把出口挂到每个引用了这个库的容器上，可库里"能发 / 能收"不等于每个容器都在发 / 收
/// （eShop 的 EventBusRabbitMQ 被 7 个容器引用，只有 4 个调用 PublishAsync、6 个加了订阅）。这里顺着库内调用往上找，直到碰到容器自己的代码：
/// <list type="bullet">
/// <item>出口那行所在的方法名就是"用法"；生命周期方法（构造函数、StartAsync / 重写的 ExecuteAsync、Handle…，框架去调的）改用所在类型名；</item>
/// <item>在容器们的成员文件里找用到它的地方：落在某个容器自己的代码里 = 该容器用到了；落在别的共享库里 = 取那一处所在的方法 / 类型继续往上找（最多 <see cref="MaxDepth"/> 层）。</item>
/// </list>
/// 结论：找到本容器的调用 → 保留，证据补上本容器那一行；本容器没找到、别的容器经无歧义的名字找到了 → 去掉（库的能力本容器没用）；
/// 谁都没找到（经分派 / 反射 / 框架回调）→ 保留但降置信度，证据写"来自共享库"。
/// 消费类出口（*-consume）若是经生命周期（托管服务）连上的，再看订阅：库链上声明了订阅 API（AddSubscription / Subscribe… / Add…Consumer），
/// 别的容器调了、本容器没调 → 去掉（OrderProcessor 起了 RabbitMQ 事件总线却没订阅任何事件）。
/// 包引用边（csproj 在共享库里）：这个库对同一外部系统的代码出口在本容器全被去掉了，包引用边也去掉（Worker 只是引用了带 Kafka 消费者的库）。
/// 只看共享库：只属于一个容器的成员（含它自己的项目目录）就是它自己的代码，不动。
/// </summary>
public static class SharedLibraryAttribution
{
    public const int MaxDepth = 5;
    private const int MaxTokens = 60;
    /// <summary>找不到本容器调用时的置信度（低于"只知道客户端"的 0.5）。</summary>
    public const double Unsure = 0.3;

    /// <summary>框架去调的方法：用法看所在类型被谁注册 / 创建，而不是方法名。</summary>
    private static readonly HashSet<string> LifecycleNames = new(StringComparer.Ordinal)
    {
        "StartAsync", "StopAsync", "Dispose", "DisposeAsync", "Main", "Configure", "ConfigureServices", "Invoke", "InvokeAsync", "Handle", "HandleAsync",
        "Consume", "OnMessage", "OnMessageAsync", "run", "main", "init", "ServeHTTP", "handle", "Execute",
    };

    /// <summary>太常见、到处都有同名调用的方法名：用它连上的不算"无歧义"。</summary>
    private static readonly HashSet<string> CommonNames = new(StringComparer.Ordinal)
    {
        "Create", "Get", "GetAsync", "Set", "Add", "Remove", "Send", "SendAsync", "Run", "RunAsync", "Execute", "ExecuteAsync", "Process", "ProcessAsync", "Build",
        "Start", "Stop", "Publish", "Write", "Read", "Load", "Save", "Update", "Delete", "Find", "Call", "Post", "Put", "Init", "Initialize", "Connect", "Open", "Close",
        "ToString", "Equals", "GetHashCode", "Apply", "Map", "Register", "Handle", "HandleAsync", "Invoke", "InvokeAsync", "New", "Do", "get", "set", "send", "run",
    };

    private static readonly Regex SubscriptionApi = new(@"^(?:Add\w*Subscription\w*|AddSubscription|Subscribe\w*|Add\w*Consumer\w*|Add\w*Listener\w*|Register\w*Handler\w*|Add\w*EventHandler\w*)$", RegexOptions.Compiled);

    private sealed record Token(string Name, MemberKind Kind, bool Strong);
    private sealed record Hit(string File, int Line);
    private sealed record Reach(Evidence Usage, string Token, bool Strong);
    private sealed record Resolution(Dictionary<string, Reach> Reached, bool ViaLifecycle);

    public static void Apply(RuleContext ctx)
    {
        var shared = new Dictionary<(string File, int Line), Resolution?>();
        var index = new SourceIndex(ctx);
        var dropped = new List<Outbound>();
        var codeOutbound = ctx.Outbound.ToList();
        foreach (var o in codeOutbound)
        {
            var owners = ctx.ContainersOf(o.File);
            if (owners.Count < 2) continue;
            var container = owners.FirstOrDefault(c => c.Key == o.ContainerKey);
            if (container is null || index.IsOwnCode(container, o.File, owners)) continue;
            var lang = PolyText.LangOf(o.File);
            if (lang is null) continue;
            if (!shared.TryGetValue((o.File, o.Line), out var res)) shared[(o.File, o.Line)] = res = Resolve(index, o.File, o.Line, lang.Value, owners);
            if (res is null) continue;
            var library = index.LibraryOf(container, o.File);

            if (res.Reached.TryGetValue(container.Key, out var reach))
            {
                if (o.Protocol.EndsWith("-consume", StringComparison.Ordinal) && res.ViaLifecycle && !Subscribed(index, container, library, owners, lang.Value))
                {
                    dropped.Add(o);
                    continue;
                }
                ctx.SharedUses[o] = new SharedLibraryUse(library, reach.Usage, reach.Token, Certain: true);
                continue;
            }
            if (res.Reached.Values.Any(r => r.Strong)) { dropped.Add(o); continue; }
            ctx.SharedUses[o] = new SharedLibraryUse(library, null, null, Certain: false);
        }
        foreach (var o in dropped) ctx.Outbound.Remove(o);

        // 包引用边：同一库对同一外部系统的代码出口在本容器全被去掉了 → 包引用边也去掉
        if (dropped.Count == 0) return;
        ctx.DirectEdges.RemoveAll(d =>
        {
            var container = ctx.Containers.FirstOrDefault(c => c.Key == d.ContainerKey);
            if (container is null) return false;
            var owners = ctx.ContainersOf(d.Evidence.File);
            if (owners.Count < 2 || index.IsOwnCode(container, d.Evidence.File, owners)) return false;
            var library = index.LibraryOf(container, d.Evidence.File);
            var mine = codeOutbound.Where(o => o.ContainerKey == d.ContainerKey && o.TargetKey == d.ExternalKey && RuleContext.Under(o.File, library)).ToList();
            return mine.Count > 0 && mine.All(dropped.Contains);
        });
    }

    /// <summary>从出口那一行往上找：哪些容器自己的代码用到了它。</summary>
    private static Resolution? Resolve(SourceIndex index, string file, int line, SourceLang lang, IReadOnlyList<ArchNode> owners)
    {
        var (start, viaLifecycle) = index.EntryTokens(file, line, lang, strong: true);
        if (start.Count == 0) return null;
        var universe = index.Universe(owners, lang);
        var reached = new Dictionary<string, Reach>(StringComparer.Ordinal);
        var seen = new HashSet<string>(start.Select(t => t.Name), StringComparer.Ordinal);
        var frontier = start;
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0 && seen.Count <= MaxTokens; depth++)
        {
            var next = new List<Token>();
            foreach (var token in frontier)
            {
                var strong = token.Strong && !index.IsAmbiguous(token, lang);
                foreach (var hit in index.Usages(token, lang))
                {
                    if (!universe.Contains(hit.File)) continue;
                    var host = owners.FirstOrDefault(c => index.IsOwnCode(c, hit.File, owners));
                    if (host is not null)
                    {
                        var usage = new Evidence(hit.File, hit.Line, index.LineText(hit.File, hit.Line));
                        if (!reached.TryGetValue(host.Key, out var r) || (!r.Strong && strong)) reached[host.Key] = new Reach(usage, token.Name, strong);
                        continue;
                    }
                    foreach (var t in index.EntryTokens(hit.File, hit.Line, lang, strong).Tokens)
                        if (seen.Add(t.Name)) next.Add(t);
                }
            }
            frontier = next;
        }
        return new Resolution(reached, viaLifecycle);
    }

    /// <summary>库链（库 + 它传递引用的项目）上声明了订阅 API 时，本容器自己的代码要调用其中之一；没有订阅 API 或谁都没调时不判（返回 true）。</summary>
    private static bool Subscribed(SourceIndex index, ArchNode container, string library, IReadOnlyList<ArchNode> owners, SourceLang lang)
    {
        var dirs = index.LibraryChain(library);
        var apis = index.PublicMethods(dirs, lang).Where(n => SubscriptionApi.IsMatch(n)).ToHashSet(StringComparer.Ordinal);
        if (apis.Count == 0) return true;
        var subscribers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var api in apis)
            foreach (var hit in index.Usages(new Token(api, MemberKind.Method, true), lang))
                foreach (var c in owners)
                    if (index.IsOwnCode(c, hit.File, owners)) subscribers.Add(c.Key);
        return subscribers.Count == 0 || subscribers.Contains(container.Key);
    }

    /// <summary>源码的懒缓存：剥过注释字符串的代码行、成员切分、名字的用法与声明。</summary>
    private sealed class SourceIndex(RuleContext ctx)
    {
        private readonly Dictionary<string, string[]> _code = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MemberSpan>> _members = new(StringComparer.Ordinal);
        private readonly Dictionary<(SourceLang, string, MemberKind), List<Hit>> _usages = [];
        private readonly Dictionary<SourceLang, Dictionary<string, HashSet<string>>> _declarations = [];
        private readonly Dictionary<SourceLang, List<string>> _files = [];
        private readonly HashSet<SourceLang> _warmed = [];
        private readonly List<string> _projectDirs = ctx.Inventory.Projects.Select(p => p.Dir).ToList();

        public IReadOnlyList<string> Files(SourceLang lang)
        {
            if (_files.TryGetValue(lang, out var list)) return list;
            return _files[lang] = ctx.Inventory.SourceFiles.Where(f => PolyText.LangOf(f) == lang && !RuleContext.IsTestFile(f) && !ProjectInventory.IsGeneratedSource(f, ctx.Lines(f))).ToList();
        }

        /// <summary>某语言全部文件的剥注释代码与成员切分并行算好（Usages / Declarations 都要扫全部文件，逐个算是 ccs-backend 上 2s 的大头）。</summary>
        private void Warm(SourceLang lang)
        {
            if (!_warmed.Add(lang)) return;
            var todo = Files(lang).Where(f => !_members.ContainsKey(f)).ToList();
            var results = new (string[] Code, List<MemberSpan> Members)[todo.Count];
            Parallel.For(0, todo.Count, i =>
            {
                var code = _code.TryGetValue(todo[i], out var c) ? c : SourceMask.Code(ctx.Lines(todo[i]), lang);
                results[i] = (code, MemberIndex.Parse(code, lang));
            });
            for (var i = 0; i < todo.Count; i++) { _code[todo[i]] = results[i].Code; _members[todo[i]] = results[i].Members; }
        }

        public HashSet<string> Universe(IReadOnlyList<ArchNode> owners, SourceLang lang) =>
            Files(lang).Where(f => owners.Any(c => c.Members.Any(m => RuleContext.Under(f, m)))).ToHashSet(StringComparer.Ordinal);

        public string[] Code(string file)
        {
            if (_code.TryGetValue(file, out var c)) return c;
            return _code[file] = PolyText.LangOf(file) is { } lang ? SourceMask.Code(ctx.Lines(file), lang) : [];
        }

        public List<MemberSpan> Members(string file)
        {
            if (_members.TryGetValue(file, out var m)) return m;
            return _members[file] = PolyText.LangOf(file) is { } lang ? MemberIndex.Parse(Code(file), lang) : [];
        }

        public string? LineText(string file, int line)
        {
            var lines = ctx.Lines(file);
            return line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : null;
        }

        /// <summary>容器自己的代码：最近的成员目录是容器自己的项目目录，或这个文件只属于它一个容器。</summary>
        public bool IsOwnCode(ArchNode c, string file, IReadOnlyList<ArchNode> owners)
        {
            if (!c.Members.Any(m => RuleContext.Under(file, m))) return false;
            var nearest = c.Members.Where(m => RuleContext.Under(file, m)).Concat(_projectDirs.Where(d => RuleContext.Under(file, d)))
                .OrderByDescending(d => d.Length).First();
            if (c.Path is not null && nearest == c.Path) return true;
            return !owners.Any(o => o.Key != c.Key && o.Members.Any(m => RuleContext.Under(file, m)));
        }

        /// <summary>出口文件所在的共享库目录（容器成员里最长的那个）。</summary>
        public string LibraryOf(ArchNode c, string file) =>
            c.Members.Where(m => RuleContext.Under(file, m)).OrderByDescending(m => m.Length).FirstOrDefault() ?? "";

        /// <summary>库 + 它传递引用的项目目录（.NET 的 ProjectReference）；别的语言只有库自己。</summary>
        public List<string> LibraryChain(string library)
        {
            var refs = ctx.Inventory.Projects.GroupBy(p => p.Dir, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.SelectMany(p => p.ProjectRefs).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            return ContainerDiscovery.Closure(library, refs);
        }

        public IEnumerable<string> PublicMethods(IReadOnlyList<string> dirs, SourceLang lang) =>
            Files(lang).Where(f => dirs.Any(d => RuleContext.Under(f, d))).SelectMany(f => Members(f)).Where(m => m.Kind == MemberKind.Method && m.IsPublic).Select(m => m.Name).Distinct(StringComparer.Ordinal);

        /// <summary>
        /// 这一行"代表"的用法：所在方法名；生命周期方法 / 构造函数 / 不在方法里 → 所在类型名（viaLifecycle）。
        /// Python 模块顶层代码 → 模块名（文件名）。都没有 → 空。
        /// </summary>
        public (List<Token> Tokens, bool ViaLifecycle) EntryTokens(string file, int line, SourceLang lang, bool strong)
        {
            var spans = Members(file).Where(s => s.StartLine <= line && line <= s.EndLine).ToList();
            var method = spans.Where(s => s.Kind is MemberKind.Method or MemberKind.Property).OrderBy(s => s.EndLine - s.StartLine).FirstOrDefault();
            var type = spans.Where(s => s.Kind == MemberKind.Type).OrderBy(s => s.EndLine - s.StartLine).FirstOrDefault();
            if (method is not null && !IsLifecycle(method, type)) return ([new Token(method.Name, MemberKind.Method, strong)], false);
            if (type is not null) return ([new Token(type.Name, MemberKind.Type, strong)], true);
            if (method is not null) return ([new Token(method.Name, MemberKind.Method, strong)], true);
            if (lang == SourceLang.Python) return ([new Token(System.IO.Path.GetFileNameWithoutExtension(file), MemberKind.Module, strong)], false);
            return ([], false);
        }

        private static bool IsLifecycle(MemberSpan m, MemberSpan? type)
        {
            if (type is not null && m.Name == type.Name) return true;   // 构造函数
            if (m.Name.StartsWith("__", StringComparison.Ordinal) && m.Name.EndsWith("__", StringComparison.Ordinal)) return true;
            if (m.Name == "ExecuteAsync" && m.IsOverride) return true;   // BackgroundService
            return LifecycleNames.Contains(m.Name);
        }

        /// <summary>名字有歧义：太常见、太短，或在三个以上的类型里都声明过同名成员（接口 + 实现只算两个）。</summary>
        public bool IsAmbiguous(Token token, SourceLang lang)
        {
            if (token.Name.Length <= 3 || (token.Kind == MemberKind.Method && CommonNames.Contains(token.Name))) return true;
            var decl = Declarations(lang);
            return decl.TryGetValue(token.Name, out var files) && files.Count > 2;
        }

        private Dictionary<string, HashSet<string>> Declarations(SourceLang lang)
        {
            if (_declarations.TryGetValue(lang, out var d)) return d;
            Warm(lang);
            d = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var f in Files(lang))
                foreach (var m in Members(f))
                {
                    if (!d.TryGetValue(m.Name, out var set)) d[m.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(f);
                }
            return _declarations[lang] = d;
        }

        /// <summary>
        /// 名字被用到的地方（剥过注释与字符串的代码里整词出现）：去掉它自己的声明行；类型名还去掉类型体内部（自引用、构造函数）。
        /// 模块名（Python 模块顶层代码的记号）只认 import 行与"模块名."的写法。
        /// </summary>
        public List<Hit> Usages(Token token, SourceLang lang)
        {
            if (_usages.TryGetValue((lang, token.Name, token.Kind), out var cached)) return cached;
            Warm(lang);
            var hits = new List<Hit>();
            var word = new Regex(@"(?<![\w$])" + Regex.Escape(token.Name) + @"(?![\w$])");
            var pyModule = token.Kind == MemberKind.Module
                ? new Regex(@"^\s*(?:from|import)\b.*(?<![\w$])" + Regex.Escape(token.Name) + @"(?![\w$])|(?<![\w$.])" + Regex.Escape(token.Name) + @"\.")
                : null;
            foreach (var f in Files(lang))
            {
                var code = Code(f);
                List<MemberSpan>? spans = null;
                for (var i = 0; i < code.Length; i++)
                {
                    if (!code[i].Contains(token.Name, StringComparison.Ordinal) || !word.IsMatch(code[i])) continue;
                    spans ??= Members(f);
                    var line = i + 1;
                    if (spans.Any(s => s.Name == token.Name && s.NameLine == line)) continue;   // 声明本身
                    if (token.Kind == MemberKind.Type && spans.Any(s => s.Kind == MemberKind.Type && s.Name == token.Name && s.StartLine <= line && line <= s.EndLine)) continue;
                    if (pyModule is not null && !pyModule.IsMatch(code[i])) continue;
                    hits.Add(new Hit(f, line));
                }
            }
            return _usages[(lang, token.Name, token.Kind)] = hits;
        }
    }
}
