using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Indexing.Roslyn;

/// <summary>
/// 分派器能找到的 Handler：消息键 → (Handler 类型, 入口方法)。第一遍收集（<see cref="RoslynAnalyzer"/>），逐文件输出时查（<see cref="DocumentWalker"/>）。
/// <list type="bullet">
/// <item>只收"分派器的 Handler 接口"（<see cref="RoslynAnalyzer.IsDispatchHandlerInterface"/>）上处理的、是消息的类型（<see cref="RoslynAnalyzer.IsMessageType"/>）。</item>
/// <item>DI 里按消息闭合的开放泛型注册（<c>AddScoped&lt;ICommandHandler&lt;X, R&gt;, EnqueueHandler&lt;X&gt;&gt;()</c>）也算：X 的 Handler 是 EnqueueHandler&lt;TCommand&gt;。</item>
/// <item>每条消息记下它的基类 / 接口键：<c>Publish(INotification e)</c> 这种按基类型分派时找子类消息的 Handler。</item>
/// <item>转发方法（参数原样交给分派方法的仓库内方法）按方法定义缓存：调用处的实参类型才是消息。</item>
/// </list>
/// 符号来自不同编译（项目），跨编译一律按 <see cref="SymbolKeys"/> 的字符串键比较。
/// </summary>
internal sealed class DispatchHandlers
{
    /// <summary>按基类型分派时最多连几个子类消息的 Handler；更多就是"什么都可能"，不连。</summary>
    public const int MaxPolymorphicTargets = 20;

    private readonly Dictionary<string, List<(INamedTypeSymbol Type, ISymbol Entry)>> _byMessage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ITypeSymbol Message, HashSet<string> Ancestors)> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ITypeSymbol>> _closedRegistrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<int>> _forwarded = new(StringComparer.Ordinal);

    public void Add(ITypeSymbol message, INamedTypeSymbol type, ISymbol entry)
    {
        var key = SymbolKeys.MessageKey(message);
        if (!_byMessage.TryGetValue(key, out var list)) _byMessage[key] = list = [];
        if (!list.Any(x => SymbolKeys.Key(x.Type) == SymbolKeys.Key(type))) list.Add((type, entry));
        if (!_messages.ContainsKey(key)) _messages[key] = (message, Ancestors(message));
    }

    /// <summary>DI 闭合注册：实现（开放泛型定义）处理 message。</summary>
    public void AddClosedRegistration(INamedTypeSymbol implDefinition, ITypeSymbol message, ISymbol entry)
    {
        Add(message, implDefinition, entry);
        var key = SymbolKeys.Key(implDefinition);
        if (!_closedRegistrations.TryGetValue(key, out var list)) _closedRegistrations[key] = list = [];
        if (!list.Any(m => SymbolKeys.MessageKey(m) == SymbolKeys.MessageKey(message))) list.Add(message);
    }

    /// <summary>这个开放泛型 Handler 经 DI 闭合注册处理的消息（没有返回空）。</summary>
    public IReadOnlyList<ITypeSymbol> ClosedRegistrationsOf(INamedTypeSymbol type) =>
        _closedRegistrations.TryGetValue(SymbolKeys.Key(type), out var list) ? list : [];

    /// <summary>先按构造键（含类型实参）找具体 Handler 子类，找不到再退回泛型定义键（开放泛型 Handler）。</summary>
    public bool TryGet(ITypeSymbol message, out List<(INamedTypeSymbol Type, ISymbol Entry)> list) =>
        _byMessage.TryGetValue(SymbolKeys.MessageKey(message), out list!) || _byMessage.TryGetValue(SymbolKeys.Key(message), out list!);

    /// <summary>基类型 baseType 的子类消息与它们的 Handler（不含 baseType 自己）；按消息键排序，结果稳定。</summary>
    public List<(ITypeSymbol Message, List<(INamedTypeSymbol Type, ISymbol Entry)> Handlers)> Subtypes(ITypeSymbol baseType)
    {
        var key = SymbolKeys.Key(baseType);
        return _messages.Where(m => m.Value.Ancestors.Contains(key) && m.Key != key)
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => (m.Value.Message, _byMessage[m.Key])).ToList();
    }

    /// <summary>
    /// method 的方法体里原样交给分派方法（Send / Publish …）的参数序号（按未约简的定义算，扩展方法的 this 是 0）。
    /// 只看仓库内、同一编译里能拿到语义模型的定义；按定义键缓存。
    /// </summary>
    public IReadOnlyList<int> ForwardedParameters(IMethodSymbol method, Compilation compilation, IReadOnlyList<string> repoRoot)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        var key = SymbolKeys.Key(definition);
        if (_forwarded.TryGetValue(key, out var hit)) return hit;
        var result = new List<int>();
        if (definition.Parameters.Length > 0 && SymbolKeys.IsInRepo(definition, repoRoot) && !RoslynAnalyzer.DispatcherMethod().IsMatch(definition.Name))
            foreach (var reference in definition.DeclaringSyntaxReferences)
            {
                if (!compilation.ContainsSyntaxTree(reference.SyntaxTree)) continue;
                var model = compilation.GetSemanticModel(reference.SyntaxTree);
                foreach (var inv in reference.GetSyntax().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol target || !RoslynAnalyzer.DispatcherMethod().IsMatch(target.Name)) continue;
                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        if (model.GetSymbolInfo(arg.Expression).Symbol is not IParameterSymbol p
                            || !SymbolEqualityComparer.Default.Equals(p.ContainingSymbol.OriginalDefinition, definition)) continue;
                        // 参数自己就是有 Handler 的消息：分派方法那一处已经直接连上了，不用到调用处再推
                        if (TryGet(p.Type, out _)) continue;
                        if (!result.Contains(p.Ordinal)) result.Add(p.Ordinal);
                    }
                }
            }
        return _forwarded[key] = result;
    }

    /// <summary>消息自己之外的全部基类与接口（泛型定义键）。</summary>
    private static HashSet<string> Ancestors(ITypeSymbol message)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var b = message.BaseType; b is { SpecialType: SpecialType.None }; b = b.BaseType) set.Add(SymbolKeys.Key(b));
        foreach (var i in message.AllInterfaces) set.Add(SymbolKeys.Key(i));
        return set;
    }
}
