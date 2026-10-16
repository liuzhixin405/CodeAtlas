namespace CodeAtlas.Server;

public static partial class FrameworkFlowBuilder
{
    /// <summary>
    /// 阶段计数与树一致（各语言收尾都走这里）：阶段头上显示的个数是 Symbols 的条数（命令行「中间件(5)」、页面「中间件 5」），
    /// 以前只收仓库里的符号——外部的中间件（UseCors、Django 内置、CORSMiddleware）一个都不算，于是「中间件(0)」下面列着 5 个；
    /// Handler 阶段干脆给空表（「Handler(0)」对着「Handler ×557」）；控制器按符号去重（一个 lambda 宿主方法上 23 条路由 → 1）。
    /// 现在按树数：进入这一阶段的行（父节点是别的阶段，或直接挂在根下）各算一条，嵌在里面但本来就列为本阶段成员的也算，占位节点（控制器 / Handler）算它的 Count，
    /// 「还有 N 个」算 N。符号按树里的行序：对得上的用原来的符号，树里有、表里没有的行补一条（外部的行 Id = 0、Kind = external），
    /// 表里有、树里没画的不再算；占位节点用剩下的符号、不够时循环补齐（同一个宿主方法上的多条路由）。树里一行都没有的阶段原样不动。
    /// </summary>
    internal static List<FrameworkStageDto> AlignStageCounts(IReadOnlyList<FrameworkStageDto> stages, FlowNodeDto root)
    {
        var rows = new Dictionary<string, List<(FlowNodeDto Node, bool Entering, bool InGroup)>>(StringComparer.Ordinal);
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(FlowNodeDto n, string? parentActor, bool parentIsGroup)
        {
            // 分组用的起点（"HTTP 请求" 一组、常驻服务一组）不算行，它下面的照根下面的算
            if (n.Kind == "entry") { foreach (var c in n.Children) Walk(c, null, false); return; }
            var entering = parentActor is null || n.Actor != parentActor;
            if (entering && n.Kind == "more") hidden[n.Actor] = hidden.GetValueOrDefault(n.Actor) + n.Hidden;
            else if (entering && n.Kind is "handlers" or "controllers") slots[n.Actor] = slots.GetValueOrDefault(n.Actor) + n.Count;
            else if (n.Kind is not ("more" or "handlers" or "controllers"))
            {
                if (!rows.TryGetValue(n.Actor, out var list)) rows[n.Actor] = list = [];
                list.Add((n, entering, !entering && parentIsGroup));
            }
            foreach (var c in n.Children) Walk(c, n.Actor, n.SymbolId is null && n.Kind is not ("controllers" or "handlers")); // 占位下面挂的是它的明细，不是分组
        }
        foreach (var c in root.Children) Walk(c, null, false);

        var result = new List<FrameworkStageDto>(stages.Count);
        foreach (var st in stages)
        {
            var candidates = rows.GetValueOrDefault(st.Key) ?? [];
            // 行 = 进入这一阶段的节点，加上嵌在分组行（没有符号的：Spring Security 过滤器链）里、构建时就列为本阶段成员的（链里的 JwtAuthFilter）；
            // 挂在有符号的行下面的不算（并成一行的分派器下面各方法那几行）
            var own = candidates.Where(c => c.Entering || c.InGroup && IsMember(c.Node)).Select(c => c.Node).ToList();
            // 嵌着的只认同一个符号（类的方法不算：自研中间件 InvokeAsync 下面它自己的 Resolve 不是另一个中间件）
            bool IsMember(FlowNodeDto n) => n.SymbolId is { } id && st.Symbols.Any(s => s.Id == id);
            var slot = slots.GetValueOrDefault(st.Key);
            var target = own.Count + slot + hidden.GetValueOrDefault(st.Key);
            if (target == 0 || st.Symbols.Count == target) { result.Add(st); continue; }
            // 按树里的行序：每行对上表里的符号（同 id，或行是表里那个类的方法：TimingMiddleware → TimingMiddleware.dispatch），对不上的补一条
            var used = new bool[st.Symbols.Count];
            int Match(FlowNodeDto n)
            {
                for (var i = 0; i < st.Symbols.Count; i++)
                    if (!used[i] && n.SymbolId is { } id && st.Symbols[i].Id == id) return i;
                for (var i = 0; i < st.Symbols.Count; i++)
                    if (!used[i] && st.Symbols[i].Id != 0 && n.QualifiedName.StartsWith(st.Symbols[i].QualifiedName + ".", StringComparison.Ordinal)) return i;
                return -1;
            }
            var symbols = new List<SymbolDto>(target);
            foreach (var n in own)
            {
                var i = Match(n);
                if (i >= 0) { used[i] = true; symbols.Add(st.Symbols[i]); }
                else symbols.Add(RowSymbol(n));
            }
            // 占位与「还有 N 个」：先用表里没对上的符号（按原顺序），不够再循环（同一个宿主方法上的多条路由）
            var rest = st.Symbols.Where((_, i) => !used[i]).ToList();
            var pool = rest.Count > 0 ? rest : st.Symbols.Count > 0 ? st.Symbols.ToList() : own.Select(RowSymbol).ToList();
            if (pool.Count == 0) pool = [new SymbolDto(0, "external", st.Label, st.Label, null, "", 0, 0, 0, 0, null)];
            for (var i = 0; symbols.Count < target; i++) symbols.Add(pool[i % pool.Count]);
            result.Add(st with { Symbols = symbols });
        }
        return result;
    }

    private static SymbolDto RowSymbol(FlowNodeDto n) =>
        new(n.SymbolId ?? 0, n.SymbolId is null ? "external" : "method", n.Label, n.QualifiedName.Length > 0 ? n.QualifiedName : n.Label, null,
            n.Path ?? "", n.Line ?? 0, 0, n.Line ?? 0, 0, null);
}
