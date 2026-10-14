using System.Text.Json;
using CodeAtlas.Core.Architecture;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Cli.Commands;

/// <summary>codeatlas arch &lt;repo&gt; [--db] [--json] [--no-store]：扫描容器图事实并打印；默认也写进索引库并接着识别入口。--dsm / --entries / --flow 读库里现成的事实打印。</summary>
internal static class ArchCommand
{
    // 中文原样输出（只给人看 / 管道给 jq），不转义成 \uXXXX
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Positional.Count < 1) { stderr.WriteLine("用法：codeatlas arch <repo> [--db <path>] [--json] [--no-store] | --dsm <容器键> [--root <组件键>] | --entries <容器键> [--kind k] [--q 文本] | --flow <入口符号 id> [--depth N] | --narrate <container|component|flow> <键> [--force] [--facts] | --narrate-all [--flows N] [--force] [--ai-provider openai|anthropic] [--ai-endpoint <url>] [--ai-model <名>] [--ai-timeout <秒>] [--ai-concurrency N] [--ai-no-source] [--no-ai]"); return 2; }
        var repo = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(repo)) { stderr.WriteLine($"目录不存在：{repo}"); return 2; }

        if (args.Has("--narrate-all")) return NarrateAll(repo, args, stdout, stderr);
        if (args.Value("--narrate") is { } narrateKind) return Narrate(repo, narrateKind, args, stdout, stderr);
        if (args.Value("--dsm") is { } containerKey) return PrintDsm(repo, containerKey, args.Value("--root") ?? "", args, stdout, stderr);
        if (args.Value("--entries") is { } entriesContainer) return PrintEntries(repo, entriesContainer, args, stdout, stderr);
        if (args.Value("--flow") is { } flowArg) return PrintFlow(repo, flowArg, args, stdout, stderr);

        ArchModel model;
        ArchReport report;
        CodeAtlas.Server.PostProcessReport? post = null;
        if (args.Has("--no-store"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            model = ArchitectureScanner.Scan(repo);
            report = new ArchReport(model.Nodes.Count(n => n.Kind == ArchNodeKind.Container), model.Nodes.Count(n => n.Kind == ArchNodeKind.External), model.Edges.Count, model.Outbound.Count, 0, model.Conflicts.Count, sw.Elapsed);
        }
        else
        {
            using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
            (model, report) = ArchitectureScanner.ScanAndStore(repo, db);
            // 写库时顺带识别入口、匹配路由并把对上的边升级：这样 arch 之后入口表总是新的
            post = CodeAtlas.Server.ArchitecturePostProcess.Run(repo, db);
            model = ArchStorage.Read(db);
        }

        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(model, Json)); return 0; }
        Print(model, report, stdout);
        if (post is not null) { stdout.WriteLine(IndexCommand.FormatEntries(post)); stdout.WriteLine(post.FormatRules()); }
        return 0;
    }

    /// <summary>
    /// 批量预生成（本地慢模型夜里跑）：全部容器 + 每个容器项目层的组件 + 每个容器前 N 个 HTTP 入口的流程（--flows，默认 0）。
    /// 已有且没过期的跳过（--force 全部重算）；每条生成完就落库，中途 Ctrl+C 再跑会接着做。端点连不上 / Key 不对就停。
    /// </summary>
    private static int NarrateAll(string repo, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        var ai = AiOptions.Parse(args, stderr, out var aiOk);
        if (!aiOk) return 2;
        if (!ai.Enabled) { stderr.WriteLine("--narrate-all 与 --no-ai 不能一起用"); return 2; }
        var flows = 0;
        if (args.Value("--flows") is { } f && (!int.TryParse(f, out flows) || flows < 0)) { stderr.WriteLine($"--flows 需要非负整数，不是 {f}"); return 2; }
        var dbPath = CommonOptions.DbPath(repo, args);
        if (!File.Exists(dbPath)) { stderr.WriteLine($"索引库不存在：{dbPath}。先运行 codeatlas index {repo}"); return 2; }
        var serverOptions = new CodeAtlas.Server.ServerOptions(repo, dbPath) { Narration = ai, Roslyn = false };
        using var index = new CodeAtlas.Server.IndexService(serverOptions);
        using var narration = CodeAtlas.Server.Narration.NarrationService.Create(serverOptions, index);

        var targets = new List<(string Kind, string Key, string Label)>();
        var containers = index.Architecture().Nodes.Where(n => n.Kind == "container").ToList();
        foreach (var c in containers) targets.Add(("container", c.Key, c.Name));
        foreach (var c in containers)
            if (index.Dsm(c.Key, "") is { } dsm)
                foreach (var comp in dsm.Components) targets.Add(("component", $"{c.Key}|{comp.Key}", $"{c.Name} / {comp.Label}"));
        if (flows > 0)
            foreach (var c in containers)
                foreach (var e in index.Entries(c.Key, "http", null).Take(flows))
                    targets.Add(("flow", e.SymbolId.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{c.Name} {e.HttpMethod} {e.Route ?? e.Name}"));

        var force = args.Has("--force");
        var todo = targets.Where(t => force || narration.Peek(t.Kind, t.Key) is not { Status: "ready", Stale: false }).ToList();
        if (ai.RemoteWarning is { } warn) stderr.WriteLine($"注意：{warn}");
        stdout.WriteLine($"[narrate-all] {ai.Provider} {ai.Endpoint} {ai.Model}  对象 {targets.Count} 个，已有且未过期 {targets.Count - todo.Count} 个，要生成 {todo.Count} 个  缓存 {CodeAtlas.Core.Storage.NarrativeCache.PathBeside(dbPath)}");
        var total = System.Diagnostics.Stopwatch.StartNew();
        int ok = 0, failed = 0;
        for (var i = 0; i < todo.Count; i++)
        {
            var (kind, key, label) = todo[i];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = narration.GenerateAsync(kind, key, force: true, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();
            stdout.WriteLine($"  [{i + 1}/{todo.Count}] {kind,-9} {label}  {r.Dto.Status}  {sw.Elapsed.TotalSeconds:F0}s{(r.Dto.Error is null ? "" : "  " + r.Dto.Error)}");
            if (r.Dto.Status == "ready") ok++;
            else if (r.Dto.Status == "unavailable") { stderr.WriteLine("端点不可用，停止（已生成的都已存好，修好后重跑会接着做）"); break; }
            else failed++;
        }
        stdout.WriteLine($"完成 {ok} 个，失败 {failed} 个，用时 {total.Elapsed.TotalMinutes:F1} 分钟");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>同步生成一条本地 AI 叙述并打印（验收用）：summary、keyPoints（带 evidence 与 inferred 标记）、risks、layout、耗时、tok/s。</summary>
    private static int Narrate(string repo, string kind, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (!CodeAtlas.Server.Narration.NarrativePresets.IsValidKind(kind)) { stderr.WriteLine($"--narrate 的类型只能是 container / component / flow，不是 {kind}"); return 2; }
        if (args.Positional.Count < 2) { stderr.WriteLine("--narrate 需要对象键：container = 容器键；component = <容器键>|<组件键>；flow = 入口符号 id"); return 2; }
        var key = args.Positional[1];
        var ai = AiOptions.Parse(args, stderr, out var aiOk);
        if (!aiOk) return 2;
        var dbPath = CommonOptions.DbPath(repo, args);
        if (!File.Exists(dbPath)) { stderr.WriteLine($"索引库不存在：{dbPath}。先运行 codeatlas index {repo}"); return 2; }

        var serverOptions = new CodeAtlas.Server.ServerOptions(repo, dbPath) { Narration = ai, Roslyn = false };
        using var index = new CodeAtlas.Server.IndexService(serverOptions);
        if (args.Has("--facts"))
        {
            // 只看喂给模型的事实：核对叙述有没有编造时用
            var facts = new CodeAtlas.Server.Narration.IndexFactsSource(index, ai.IncludeSource).Resolve(kind, key);
            if (facts is null) { stderr.WriteLine($"没有这个对象：{kind} {key}"); return 2; }
            stdout.WriteLine($"# {facts.Title}  事实 {facts.Ids.Count} 条带 id，{facts.Text.Length} 字符{(facts.Omitted > 0 ? $"，省略 {facts.Omitted} 条" : "")}  指纹 {facts.Fingerprint[..12]}  相关文件 {facts.Sources.Count} 个  稳定键 {facts.StableKey}");
            stdout.Write(facts.Text);
            return 0;
        }
        using var narration = CodeAtlas.Server.Narration.NarrationService.Create(serverOptions, index);
        if (ai.RemoteWarning is { } warn) stderr.WriteLine($"注意：{warn}");
        stderr.WriteLine($"[narrate] {kind} {key}  协议 {ai.Provider}  端点 {ai.Endpoint}  模型 {ai.Model}  API Key {(ai.ApiKey is null ? "无" : "已设置")}{(ai.IncludeSource ? "" : "  不发源码")}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = narration.GenerateAsync(kind, key, args.Has("--force"), CancellationToken.None).GetAwaiter().GetResult();
        sw.Stop();
        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(result.Dto, Json)); return result.Dto.Status == "ready" ? 0 : 1; }

        var d = result.Dto;
        stdout.WriteLine($"状态 {d.Status}{(d.Error is null ? "" : "  " + d.Error)}  模型 {d.Model ?? ai.Model}  耗时 {sw.Elapsed.TotalSeconds:F1}s{(result.Attempts > 1 ? $"  尝试 {result.Attempts} 次" : "")}{(result.Chat is null ? (result.Attempts == 0 && d.Status == "ready" ? "  （缓存命中）" : "") : "")}");
        stdout.WriteLine($"缓存 生成于 {d.GeneratedAt ?? "-"}  commit {d.GitCommit ?? "-"}  模型 {d.Model ?? "-"}{(d.Stale ? $"  可能过期：{d.StaleReason}" : "  未过期")}");
        if (result.Facts is { } f) stdout.WriteLine($"事实 {f.Ids.Count} 条带 id，{f.Text.Length} 字符{(f.Omitted > 0 ? $"，省略 {f.Omitted} 条" : "")}  hash {d.Hash?[..12]}");
        if (result.Chat is { } c)
            stdout.WriteLine($"端点 {c.Elapsed.TotalSeconds:F1}s  prompt {c.PromptTokens?.ToString() ?? "?"} tok  completion {c.CompletionTokens?.ToString() ?? "?"} tok{(c.TokensPerSecond is { } tps ? $"  {tps:F1} tok/s" : "")}");
        if (d.Status != "ready") return 1;
        stdout.WriteLine();
        stdout.WriteLine($"摘要：{d.Summary}");
        stdout.WriteLine($"版式：{d.Layout ?? "（未选 / 非法）"}");
        stdout.WriteLine("要点：");
        foreach (var p in d.KeyPoints) stdout.WriteLine($"  - {p.Text}{(p.Inferred ? "  [推断]" : "")}  ← {string.Join(", ", p.Evidence)}");
        stdout.WriteLine(d.Risks.Count == 0 ? "风险：（无）" : "风险：");
        foreach (var p in d.Risks) stdout.WriteLine($"  - {p.Text}{(p.Inferred ? "  [推断]" : "")}  ← {string.Join(", ", p.Evidence)}");
        return 0;
    }

    /// <summary>读库里的入口表，按容器 / kind / 文本过滤后打印成表。</summary>
    private static int PrintEntries(string repo, string containerKey, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
        var container = ArchStorage.Read(db).Nodes.FirstOrDefault(n => n.Key == containerKey && n.Kind == ArchNodeKind.Container);
        if (container is null) { stderr.WriteLine($"没有这个容器：{containerKey}（先跑 codeatlas index 或 codeatlas arch）"); return 2; }
        var (entries, _) = EntryStorage.Read(db);
        var reader = new IndexReader(db);
        var kind = args.Value("--kind");
        var q = args.Value("--q");
        var rows = new List<CodeAtlas.Server.EntryDto>();
        foreach (var e in entries.Where(e => e.ContainerKey == containerKey))
        {
            if (kind is not null && !string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            var s = reader.GetSymbol(e.SymbolId);
            var name = s?.Name ?? "?";
            var qualified = s?.QualifiedName ?? "?";
            if (!string.IsNullOrWhiteSpace(q)
                && !(e.Route?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                && !name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !qualified.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new CodeAtlas.Server.EntryDto(e.SymbolId, e.Kind, e.RuleId, e.Route, e.HttpMethod, name, qualified, e.File, e.Line));
        }
        rows = rows.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(rows, Json)); return 0; }

        var byKind = rows.GroupBy(r => r.Kind).Select(g => $"{g.Key} {g.Count()}");
        stdout.WriteLine($"容器 {container.Name}  入口 {rows.Count}  ({string.Join(", ", byKind)})");
        if (rows.Count == 0) return 0;
        var routeWidth = Math.Clamp(rows.Max(r => (r.Route ?? "").Length), 8, 60);
        var nameWidth = Math.Clamp(rows.Max(r => r.Name.Length), 4, 40);
        stdout.WriteLine($"{"id",8}  {"kind",-9} {"method",-7} {"route".PadRight(routeWidth)}  {"name".PadRight(nameWidth)}  file:line");
        foreach (var r in rows)
            stdout.WriteLine($"{r.SymbolId,8}  {r.Kind,-9} {r.HttpMethod ?? "",-7} {Truncate(r.Route ?? "", routeWidth).PadRight(routeWidth)}  {Truncate(r.Name, nameWidth).PadRight(nameWidth)}  {r.File}:{r.Line}");
        return 0;
    }

    /// <summary>现算一个入口的时序树并缩进打印（验收用）。</summary>
    private static int PrintFlow(string repo, string symbolIdArg, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        if (!long.TryParse(symbolIdArg, out var symbolId)) { stderr.WriteLine($"--flow 需要入口符号 id（整数），不是 {symbolIdArg}"); return 2; }
        int? depth = null;
        if (args.Value("--depth") is { } d)
        {
            if (!int.TryParse(d, out var parsed)) { stderr.WriteLine($"--depth 需要整数，不是 {d}"); return 2; }
            depth = parsed;
        }
        using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
        var (entries, matches) = EntryStorage.Read(db);
        var entry = entries.FirstOrDefault(e => e.SymbolId == symbolId);
        if (entry is null) { stderr.WriteLine($"没有这个入口：{symbolId}（用 --entries <容器键> 看入口清单）"); return 2; }
        var containers = ArchStorage.Read(db).Nodes.Where(n => n.Kind == ArchNodeKind.Container).ToList();
        var (symbols, edges) = CodeAtlas.Server.MapData.Load(new IndexReader(db));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var flow = CodeAtlas.Server.FlowBuilder.Build(symbols, edges, entry, containers, matches, entries, Math.Clamp(depth ?? CodeAtlas.Server.FlowBuilder.DefaultMaxDepth, 1, 20));
        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(flow, Json)); return 0; }

        var containerName = containers.FirstOrDefault(c => c.Key == flow.ContainerKey)?.Name ?? flow.ContainerKey;
        stdout.WriteLine($"入口 {flow.EntryLabel}{(entry.HttpMethod is null ? "" : "  " + entry.HttpMethod)}{(flow.Route is null ? "" : "  路由 " + flow.Route)}  容器 {containerName}  节点 {flow.Nodes}{(flow.Truncated ? "（已截断）" : "")}  {sw.Elapsed.TotalMilliseconds:F0}ms{(flow.Note is null ? "" : "  " + flow.Note)}");
        stdout.WriteLine($"参与者 ({flow.Actors.Count}): {string.Join(", ", flow.Actors.Select(a => $"{a.Label} [{a.Kind}]"))}");
        if (flow.Behaviors.Count > 0) stdout.WriteLine($"管道行为: {string.Join(", ", flow.Behaviors)}");
        stdout.WriteLine();
        var actorLabels = flow.Actors.ToDictionary(a => a.Key, a => a.Label, StringComparer.Ordinal);
        PrintNode(flow.Root, 0, actorLabels, stdout);
        return 0;
    }

    private static void PrintNode(CodeAtlas.Server.FlowNodeDto n, int indent, IReadOnlyDictionary<string, string> actorLabels, TextWriter w)
    {
        var pad = new string(' ', indent * 2);
        var actor = actorLabels.GetValueOrDefault(n.Actor, n.Actor);
        var via = n.Via is null ? "" : $" via {n.Via}";
        var count = n.Count > 1 ? $" ×{n.Count}" : "";
        var note = n.Note is null ? "" : $"  {n.Note}";
        var hidden = n.Kind == "more" ? $"（折叠 {n.Hidden} 个分支）" : "";
        w.WriteLine($"{pad}{n.Label}  [{actor}]  {n.Kind}{via} {n.Confidence:F1}{count}{hidden}{note}");
        foreach (var c in n.Children) PrintNode(c, indent + 1, actorLabels, w);
    }

    private static string Truncate(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    /// <summary>读库里已有的容器与 git 统计，现算 DSM 并按矩阵打印（验收用）。</summary>
    private static int PrintDsm(string repo, string containerKey, string root, ParsedArgs args, TextWriter stdout, TextWriter stderr)
    {
        using var db = IndexDatabase.Open(CommonOptions.DbPath(repo, args));
        var container = ArchStorage.Read(db).Nodes.FirstOrDefault(n => n.Key == containerKey && n.Kind == ArchNodeKind.Container);
        if (container is null) { stderr.WriteLine($"没有这个容器：{containerKey}（先跑 codeatlas index 或 codeatlas arch）"); return 2; }
        var (symbols, edges) = CodeAtlas.Server.MapData.Load(new IndexReader(db));
        var git = GitStorage.Read(db);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var d = CodeAtlas.Server.DsmBuilder.Build(symbols, edges, container, root, git.Files.Count > 0 ? git : null, rules: CodeAtlas.Server.DsmBuilder.LoadRules(db));
        if (args.Has("--json")) { stdout.WriteLine(JsonSerializer.Serialize(d, Json)); return 0; }
        stdout.WriteLine($"容器 {container.Name}  root=\"{d.Root}\"  组件 {d.Components.Count}  格子 {d.Cells.Count}  环 {d.Cycles.Count}  隐性耦合 {d.Coupling.Count}  git {(d.GitAvailable ? "有" : "无")}  {sw.Elapsed.TotalMilliseconds:F0}ms{(d.Note is null ? "" : "  " + d.Note)}");
        var n = d.Components.Count;
        var cells = d.Cells.ToDictionary(c => (c.Row, c.Col), c => c.Count);
        stdout.WriteLine("     " + string.Join("", Enumerable.Range(0, n).Select(i => $"{i,5}")));
        for (var r = 0; r < n; r++)
        {
            var c = d.Components[r];
            var row = string.Join("", Enumerable.Range(0, n).Select(col => col == r ? "    ·" : cells.TryGetValue((r, col), out var v) ? $"{v,5}" : "     "));
            stdout.WriteLine($"{r,3}  {row}   {c.Label} ({c.Symbols} 符号, {c.Files} 文件{(c.HasChildren ? ", 可钻" : "")}{(c.Hot is null ? "" : $", 修订 {c.Hot.Revisions}, {c.Hot.AgeMonths} 月前")}){(d.Cycles.Any(cy => cy.Contains(c.Key)) ? "  ⟲ 环" : "")}");
        }
        foreach (var cy in d.Cycles) stdout.WriteLine("环: " + string.Join(" ↔ ", cy));
        foreach (var cp in d.Coupling) stdout.WriteLine($"隐性耦合: {d.Components[cp.A].Label} ~ {d.Components[cp.B].Label}  共同修订 {cp.Shared}  耦合度 {cp.Degree:F2}");
        return 0;
    }

    public static void Print(ArchModel model, ArchReport report, TextWriter w)
    {
        w.WriteLine($"容器 {report.Containers}，外部系统 {report.Externals}，边 {report.Edges}，出口 {report.OutboundTotal}（未对上容器 {report.OutboundUnmatched}），冲突 {report.Conflicts}，{report.Elapsed.TotalSeconds:F1}s");
        w.WriteLine();
        w.WriteLine("== 容器");
        foreach (var n in model.Nodes.Where(n => n.Kind == ArchNodeKind.Container))
            w.WriteLine($"  {n.Name,-32} {n.SubKind,-14} 端口 [{string.Join(",", n.Ports)}]  成员 {n.Members.Count}  证据 {string.Join("; ", n.Evidence.Select(e => e.File + (e.Note is null ? "" : " (" + e.Note + ")")))}");
        w.WriteLine("== 外部系统");
        foreach (var n in model.Nodes.Where(n => n.Kind != ArchNodeKind.Container))
            w.WriteLine($"  {n.Name,-32} {n.SubKind,-14} {n.Source}  证据 {n.Evidence.Count} 处，首处 {n.Evidence[0].File}{(n.Evidence[0].Line is { } l ? ":" + l : "")}");
        w.WriteLine("== 边");
        var names = model.Nodes.ToDictionary(n => n.Key, n => n.Name, StringComparer.Ordinal);
        foreach (var e in model.Edges)
        {
            var first = e.Evidence[0];
            w.WriteLine($"  {names.GetValueOrDefault(e.FromKey, e.FromKey),-28} → {names.GetValueOrDefault(e.ToKey, e.ToKey),-28} {e.Protocol,-14} {e.Confidence:F1} {e.Source,-10} {first.File}{(first.Line is { } l ? ":" + l : "")}{(e.ConfigKey is null ? "" : "  配置 " + e.ConfigKey)}{(e.Url is null ? "" : "  " + e.Url)}");
        }
        if (model.Conflicts.Count > 0)
        {
            w.WriteLine("== 声明与发现的冲突");
            foreach (var c in model.Conflicts) w.WriteLine($"  [{c.Kind}] {c.Message}");
        }
    }
}
