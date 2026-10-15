using CodeAtlas.Core.Model;

namespace CodeAtlas.Server;

/// <param name="Externals">返回框架 / 第三方调用（kind=external）。false 时去掉，挂在外部 Send 下的 dispatch 提成独立一步。</param>
/// <param name="Tests">保留落到测试代码里的目标。当前方法本身在测试代码里时不论这个开关都保留（测试方法调的辅助方法也在测试里）。</param>
public sealed record OutlineOptions(bool Externals = true, bool Tests = false);

/// <summary>
/// 调用大纲：一个方法里的调用按源码顺序排成一层步骤（GET /api/symbols/{id}/outline）。纯函数，数据由 <see cref="OutlineQuery"/> 取。
/// <list type="number">
/// <item>取 calls（confidence ≥ 0.4，via 不是 conduit）与 dispatches（via = dispatch:*）；0.3 档只在"同一处多个候选"时合成 ambiguous。
/// conduit 只去掉构造消息那条（new T），分发方法本身（mediator.Send）保留，dispatch 挂在它下面。</item>
/// <item>所有步骤都有 pos 时按 pos 排；否则按（行，pos，边 id）排。</item>
/// <item>via=impl 挂到同一处接口调用下面；dispatch 挂到同一行的分发调用下面；找不到就单独成一步。</item>
/// <item>相邻的同一目标、同一 ctx 合并成一步（Repeat 累加）；超过 <see cref="MaxSteps"/> 截断。</item>
/// </list>
/// </summary>
public static class OutlineBuilder
{
    public const int MaxSteps = 300;

    private sealed class Step
    {
        public required EdgeRecord Edge;
        public required string Kind;
        public required string Label;
        public SymbolRecord? Target;
        public bool Expandable;
        public bool Conduit;
        public int Repeat = 1;
        public List<Step> Children = [];
        public int Line => Edge.Line;
        public int? Pos => Edge.Pos;
    }

    /// <param name="root">要列大纲的符号。</param>
    /// <param name="edges">root 的出边（可以多给，非 root 出发的、不相干种类的会被过滤掉）。</param>
    /// <param name="symbols">边的目标与它们的父符号（拼"类型.方法"用）、<paramref name="constructors"/> 里的构造函数。</param>
    /// <param name="paths">文件 id → 路径。</param>
    /// <param name="hasCalls">自己还有满足第 1 条的出边的符号 id（决定 Expandable）。</param>
    /// <param name="constructors">类型 id → 它唯一的构造函数 id：new T 的 Target 换成构造函数，展开就是构造函数体里的调用。多个重载时不给（不知道是哪个）。</param>
    public static OutlineDto Build(
        SymbolDto root, IReadOnlyList<EdgeRecord> edges, IReadOnlyDictionary<long, SymbolRecord> symbols, IReadOnlyDictionary<long, string> paths,
        IReadOnlySet<long> hasCalls, IReadOnlyDictionary<long, long>? constructors, OutlineOptions options, int maxSteps = MaxSteps)
    {
        var keepTests = options.Tests || PipelineBuilder.IsTest(root.Path, root.QualifiedName);
        string PathOf(SymbolRecord s) => paths.GetValueOrDefault(s.FileId, Core.Storage.IndexWriter.ExternalFilePath);
        MapSymbol Map(SymbolRecord s) => new(s.Id, s.Kind, s.QualifiedName, PathOf(s), s.ParentId);

        string Label(SymbolRecord s) => SymbolLabel(s, symbols, paths);

        bool Expandable(SymbolRecord s) => s.Kind is SymbolKind.Method or SymbolKind.Function && hasCalls.Contains(s.Id);

        Step Make(EdgeRecord e, SymbolRecord t, string kind)
        {
            if (kind == "call" && t.Kind == SymbolKind.External) kind = "external";
            else if (kind == "call" && (IsType(t.Kind) || IsConstructorSymbol(t))) kind = "new";
            var target = t;
            if (kind == "new" && constructors is not null && constructors.TryGetValue(t.Id, out var ctorId) && symbols.TryGetValue(ctorId, out var ctor)) target = ctor;
            return new Step
            {
                Edge = e, Kind = kind, Label = Label(t), Target = target,
                Expandable = kind != "external" && Expandable(target),
            };
        }

        bool IsConstructorSymbol(SymbolRecord s) =>
            s.Kind is SymbolKind.Method or SymbolKind.Function && s.ParentId is { } p && symbols.TryGetValue(p, out var o) && IsType(o.Kind) && IsConstructor(s, o);

        // ---- 1. 过滤 ----
        var calls = new List<Step>();
        var impls = new List<Step>();
        var dispatches = new List<Step>();
        var lowConfidence = new List<(EdgeRecord Edge, SymbolRecord Target)>();
        foreach (var e in edges)
        {
            if (e.FromId != root.Id || e.ToId is not { } toId || !symbols.TryGetValue(toId, out var t)) continue;
            if (!keepTests && t.Kind != SymbolKind.External && PipelineBuilder.IsTest(PathOf(t), t.QualifiedName)) continue;
            if (e.Kind == EdgeKind.Dispatches)
            {
                if (e.Via is { } dv && dv.StartsWith("dispatch:", StringComparison.Ordinal)) dispatches.Add(Make(e, t, "dispatch"));
                continue;
            }
            if (e.Kind != EdgeKind.Calls) continue;
            if (e.Via == "conduit")
            {
                // 消息的构造是管道细节；分发调用本身（Send / Publish）就是那一步
                if (IsType(t.Kind)) continue;
                var send = Make(e, t, "call");
                send.Conduit = true;
                calls.Add(send);
                continue;
            }
            if (e.Confidence >= 0.4)
            {
                if (e.Via == "impl") impls.Add(Make(e, t, "impl"));
                else calls.Add(Make(e, t, "call"));
            }
            else if (e.Confidence >= 0.25) lowConfidence.Add((e, t));
        }

        // ---- 0.3 档多候选：同一处（行、偏移）、同一短名的多个目标合成一步 ----
        foreach (var g in lowConfidence.GroupBy(x => (x.Edge.Line, x.Edge.Pos, Name: Map(x.Target).Name)))
        {
            var candidates = g.DistinctBy(x => x.Target.Id).ToList();
            if (candidates.Count < 2) continue; // 单个 0.3 不到阈值
            var children = candidates.Select(x => Make(x.Edge, x.Target, "call")).ToList();
            foreach (var c in children) if (c.Kind == "new") c.Kind = "call"; // 候选一律 call（DTO 约定）
            calls.Add(new Step
            {
                Edge = candidates.OrderBy(x => x.Edge.Id).First().Edge, Kind = "ambiguous",
                Label = $"{candidates.Count} 个可能目标：{g.Key.Name}", Target = null, Children = children,
            });
        }

        // ---- 2. impl 挂到接口调用下面 ----
        foreach (var impl in impls)
        {
            var implName = Map(impl.Target!).Name;
            var parent = calls.FirstOrDefault(c => c.Kind == "call" && c.Line == impl.Line && c.Pos is not null && c.Pos == impl.Pos)
                         ?? calls.FirstOrDefault(c => c.Kind == "call" && c.Line == impl.Line && c.Target is { } ct && Map(ct).Name == implName);
            if (parent is null) { calls.Add(impl); continue; }
            if (parent.Children.All(c => c.Target?.Id != impl.Target!.Id)) parent.Children.Add(impl);
        }

        // ---- 3. dispatch 挂到分发调用下面：同一行的 conduit 调用，tree-sitter 的分派边记在 new T 那一行，允许差一行；再退到同一行的任意调用 ----
        foreach (var d in dispatches)
        {
            var parent = calls.FirstOrDefault(c => c.Conduit && c.Line == d.Line)
                         ?? calls.Where(c => c.Conduit && Math.Abs(c.Line - d.Line) <= 1).OrderBy(c => Math.Abs(c.Line - d.Line)).FirstOrDefault()
                         ?? Sort(calls.Where(c => c.Line == d.Line && c.Kind is "call" or "external")).FirstOrDefault();
            if (parent is null)
            {
                // 同一个处理器重复分派（Roslyn 的基类分派与方法自己的分派）只留一步
                if (calls.Any(c => c.Kind == "dispatch" && c.Line == d.Line && c.Target?.Id == d.Target?.Id)) continue;
                calls.Add(d);
                continue;
            }
            if (parent.Children.All(c => c.Target?.Id != d.Target?.Id)) parent.Children.Add(d);
        }

        // ---- 4. externals=false：去掉外部调用，它下面的 dispatch 提出来 ----
        if (!options.Externals)
        {
            var kept = new List<Step>();
            foreach (var s in calls)
            {
                if (s.Kind != "external") { kept.Add(s); continue; }
                kept.AddRange(s.Children.Where(c => c.Kind == "dispatch"));
            }
            calls = kept;
            foreach (var s in calls.Where(s => s.Kind == "ambiguous")) s.Children.RemoveAll(c => c.Kind == "external");
        }

        // ---- 5. 排序、相邻合并、截断、编号 ----
        var ordered = Sort(calls).ToList();
        foreach (var s in ordered) s.Children = Sort(s.Children).ToList();
        var merged = new List<Step>();
        foreach (var s in ordered)
        {
            if (merged.Count > 0 && SameStep(merged[^1], s)) merged[^1].Repeat += s.Repeat;
            else merged.Add(s);
        }
        var truncated = merged.Count > maxSteps;
        if (truncated) merged = merged.Take(maxSteps).ToList();

        SymbolDto Dto(SymbolRecord s) => new(s.Id, DbNames.ToDb(s.Kind), s.Name, s.QualifiedName, s.Signature, PathOf(s),
            s.StartLine, s.StartCol, s.EndLine, s.EndCol, s.Visibility);

        IReadOnlyList<OutlineStepDto> ToDtos(List<Step> steps) => steps.Select((s, i) => new OutlineStepDto(
            i + 1, s.Kind, s.Label, s.Target is { } t ? Dto(t) : null, s.Line, s.Pos, s.Edge.Ctx, s.Edge.Confidence,
            s.Expandable, s.Repeat, ToDtos(s.Children))).ToList();

        return new OutlineDto(root, ToDtos(merged), truncated);
    }

    /// <summary>
    /// 显示名"类型.方法"（去掉泛型参数与参数列表）：构造函数只显示类型名，类型 / 模块显示自己的短名，外部符号取最后两段，
    /// Go 方法用接收者类型。父符号要在 <paramref name="symbols"/> 里，否则只有方法名。调用大纲与影响范围共用。
    /// </summary>
    internal static string SymbolLabel(SymbolRecord s, IReadOnlyDictionary<long, SymbolRecord> symbols, IReadOnlyDictionary<long, string> paths)
    {
        MapSymbol Map(SymbolRecord x) => new(x.Id, x.Kind, x.QualifiedName, paths.GetValueOrDefault(x.FileId, Core.Storage.IndexWriter.ExternalFilePath), x.ParentId);
        if (s.Kind == SymbolKind.External) return ExternalLabel(s.QualifiedName);
        var name = Map(s).Name;
        if (IsType(s.Kind) || s.Kind == SymbolKind.Module) return name;
        if (s.ParentId is { } pid && symbols.TryGetValue(pid, out var owner) && IsType(owner.Kind))
        {
            var ownerName = Map(owner).Name;
            return IsConstructor(s, owner) ? ownerName : ownerName + "." + name;
        }
        return FlowBuilder.GoReceiver(Map(s)) is { } recv ? recv + "." + name : name;
    }

    /// <summary>都有 pos 时按 pos；否则行优先、同一行内 pos（没有 pos 的排在该行后面），再按边 id。</summary>
    private static IEnumerable<Step> Sort(IEnumerable<Step> steps)
    {
        var list = steps.ToList();
        if (list.All(s => s.Pos is not null))
            return list.OrderBy(s => s.Pos!.Value).ThenBy(s => s.Line).ThenBy(s => s.Edge.Id);
        return list.OrderBy(s => s.Line).ThenBy(s => s.Pos ?? int.MaxValue).ThenBy(s => s.Edge.Id);
    }

    private static bool SameStep(Step a, Step b) =>
        a.Kind == b.Kind && a.Edge.Ctx == b.Edge.Ctx && a.Label == b.Label && a.Target?.Id == b.Target?.Id
        && a.Children.Select(c => c.Target?.Id).SequenceEqual(b.Children.Select(c => c.Target?.Id));

    internal static bool IsType(SymbolKind k) => k is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum;

    internal static bool IsConstructor(SymbolRecord method, SymbolRecord owner) =>
        method.Name is "constructor" or "__init__" || MapBuilder.StripGenericArgs(method.Name) == MapBuilder.StripGenericArgs(owner.Name);

    /// <summary>外部名的最后两段：external:Microsoft.Extensions.Logging.ILogger.LogInformation(string) → ILogger.LogInformation；Go 的 gorm.io/gorm.Where → gorm.Where。</summary>
    internal static string ExternalLabel(string qualifiedName)
    {
        var q = qualifiedName.StartsWith("external:", StringComparison.Ordinal) ? qualifiedName["external:".Length..] : qualifiedName;
        var paren = q.IndexOf('(');
        if (paren >= 0) q = q[..paren];
        q = MapBuilder.StripGenericArgs(q);
        var slash = q.LastIndexOf('/');
        if (slash >= 0) q = q[(slash + 1)..];
        var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => q, 1 => parts[0], _ => parts[^2] + "." + parts[^1] };
    }
}
