using CodeAtlas.Core.Roslyn;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeAtlas.Indexing.Roslyn;

/// <summary>
/// 用 Roslyn 语义模型产出精确的符号与边。两遍：先收集所有 Handler&lt;T&gt;，再逐文件输出。
/// 不依赖进程，测试直接调用；worker 把结果序列化成 JSON Lines。
/// </summary>
public static partial class RoslynAnalyzer
{
    private static readonly object MsBuildGate = new();

    public static async IAsyncEnumerable<RoslynRecord> AnalyzeAsync(string solutionOrProjectPath, string repoRoot, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureMsBuild();
        var channel = Channel.CreateUnbounded<RoslynRecord>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try { await AnalyzeCoreAsync(solutionOrProjectPath, Path.GetFullPath(repoRoot), r => channel.Writer.TryWrite(r), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { channel.Writer.TryWrite(new ErrorRec("analyzer", ex.ToString())); channel.Writer.TryWrite(new DoneRec(0, 0, 1)); }
            finally { channel.Writer.Complete(); }
        }, ct);
        await foreach (var r in channel.Reader.ReadAllAsync(ct)) yield return r;
        await producer;
    }

    private static void EnsureMsBuild()
    {
        lock (MsBuildGate)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    // ---- 以下方法只在 MSBuild 注册之后才会被 JIT，避免提前加载 Microsoft.Build 程序集 ----

    private static async Task AnalyzeCoreAsync(string path, string repoRoot, Action<RoslynRecord> emit, CancellationToken ct)
    {
        var errors = 0;
        using var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) { errors++; emit(new ErrorRec("workspace", e.Diagnostic.Message)); }
        };

        Solution solution;
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct);
        else
        {
            await workspace.OpenProjectAsync(path, cancellationToken: ct);
            solution = workspace.CurrentSolution;
        }

        // 多目标框架的项目会出现多次，只取第一个
        var projects = solution.Projects.GroupBy(p => p.FilePath ?? p.Name).Select(g => g.First()).ToList();
        var compilations = new List<(Project Project, Compilation Compilation)>();
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) { errors++; emit(new ErrorRec(project.Name, "没有拿到编译")); continue; }
            compilations.Add((project, compilation));
        }

        // 第一遍：收集 Handler<T>。键用 MessageKey：闭合构造泛型消息（IdentifiedCommand<CreateOrderCommand, bool>）带类型实参，
        // 开放泛型基 Handler 的消息（IdentifiedCommand<T, R>）落在泛型定义键上；查找时先按构造键，再退回定义键（见 DocumentWalker.EmitInvocation）
        var handlers = new Dictionary<string, List<(INamedTypeSymbol Type, ISymbol Entry)>>(StringComparer.Ordinal);
        foreach (var (_, compilation) in compilations)
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace, repoRoot))
                foreach (var (message, entry) in HandledMessages(type))
                {
                    var key = SymbolKeys.MessageKey(message);
                    if (!handlers.TryGetValue(key, out var list)) handlers[key] = list = [];
                    if (!list.Any(x => SymbolEqualityComparer.Default.Equals(x.Type, type))) list.Add((type, entry));
                }

        // 第二遍：逐文件输出
        var documents = compilations
            .SelectMany(c => c.Project.Documents.Select(d => (c.Compilation, Document: d)))
            .Where(x => x.Document.FilePath is { } fp && fp.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && fp.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase) && !SymbolKeys.IsGeneratedPath(fp))
            .GroupBy(x => x.Document.FilePath!, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();
        var done = 0;
        foreach (var (compilation, document) in documents)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(repoRoot, document.FilePath!).Replace('\\', '/');
            try
            {
                var model = await document.GetSemanticModelAsync(ct);
                var root = await document.GetSyntaxRootAsync(ct);
                if (model is null || root is null) continue;
                emit(new FileRec(relative));
                new DocumentWalker(model, root, relative, repoRoot, handlers, emit).Run();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                emit(new ErrorRec(relative, ex.Message));
            }
            done++;
            emit(new ProgressRec("roslyn", done, documents.Count, relative));
        }
        emit(new DoneRec(compilations.Count, documents.Count, errors));
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns, string repoRoot)
    {
        foreach (var t in ns.GetTypeMembers())
            foreach (var nested in SelfAndNested(t))
                if (SymbolKeys.IsInRepo(nested, repoRoot)) yield return nested;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in SourceTypes(child, repoRoot)) yield return t;
    }

    private static IEnumerable<INamedTypeSymbol> SelfAndNested(INamedTypeSymbol t)
    {
        yield return t;
        foreach (var n in t.GetTypeMembers()) foreach (var x in SelfAndNested(n)) yield return x;
    }

    /// <summary>类型实现的 IXxxHandler&lt;T, …&gt; / IXxxConsumer&lt;T&gt;：返回 (消息类型, 入口方法或类型本身)。</summary>
    internal static IEnumerable<(ITypeSymbol Message, ISymbol Entry)> HandledMessages(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.TypeArguments.Length == 0 || !HandlerInterface().IsMatch(iface.Name)) continue;
            var entry = type.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && HandlerEntry().IsMatch(m.Name)) as ISymbol ?? type;
            yield return (iface.TypeArguments[0], entry);
        }
    }

    [GeneratedRegex("(Handler|Consumer)$")]
    private static partial Regex HandlerInterface();

    [GeneratedRegex("^(Handle|Consume|Execute)")]
    internal static partial Regex HandlerEntry();

    [GeneratedRegex("^(Send|Query|Dispatch|Publish|Execute|Handle|Process|Mediate|Request)(Async)?$")]
    internal static partial Regex DispatcherMethod();

    internal static readonly HashSet<string> DiMethods = new(StringComparer.Ordinal)
    {
        "AddScoped", "AddSingleton", "AddTransient", "TryAddScoped", "TryAddSingleton", "TryAddTransient",
    };
}
