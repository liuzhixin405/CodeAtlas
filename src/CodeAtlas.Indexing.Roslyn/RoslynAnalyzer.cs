using CodeAtlas.Core.Roslyn;
using CodeAtlas.Core.Workspace;
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

    public static IAsyncEnumerable<RoslynRecord> AnalyzeAsync(string solutionOrProjectPath, string repoRoot, CancellationToken ct) =>
        AnalyzeAsync(solutionOrProjectPath, [repoRoot], repoRoot, ct);

    /// <summary>
    /// 多根：repoRoots（主根 + 外部根）之下的源码文件才输出、定义在它们之下的符号才算仓库内；
    /// 输出路径相对 basePath（主根），外部根的文件是 ../other/…，嵌套仓库的文件带子目录前缀，与 tree-sitter 入库路径一致。
    /// <para>
    /// only（绝对路径）：增量时只输出这些文件，外加"受新 Handler 影响的发送方"——only 里的文件声明的 Handler（含 DI 按消息闭合的注册）所处理的消息，
    /// 其简单名出现在文本里的文档也一起走（新增 Handler 时，发送该消息的文件要补上分派边）。打开、编译、收集 Handler 照常全量做：分派要完整的 Handler 表。
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<RoslynRecord> AnalyzeAsync(string solutionOrProjectPath, IReadOnlyList<string> repoRoots, string basePath, [EnumeratorCancellation] CancellationToken ct,
        IReadOnlyCollection<string>? only = null)
    {
        EnsureMsBuild();
        var roots = repoRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToList();
        var channel = Channel.CreateUnbounded<RoslynRecord>(new UnboundedChannelOptions { SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try { await AnalyzeCoreAsync(solutionOrProjectPath, roots, Path.GetFullPath(basePath), only, r => channel.Writer.TryWrite(r), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { channel.Writer.TryWrite(new ErrorRec("analyzer", ex.ToString())); channel.Writer.TryWrite(new DoneRec(0, 0, 1)); }
            finally { channel.Writer.Complete(); }
        }, ct);
        await foreach (var r in channel.Reader.ReadAllAsync(ct)) yield return r;
        await producer;
    }

    /// <summary>
    /// 探测本机能不能用 MSBuild：返回找到的 SDK 描述（版本 + 路径），找不到返回 null。
    /// worker 的 --probe 用它；不注册，所以之后照样能 AnalyzeAsync。
    /// </summary>
    public static string? ProbeMsBuild()
    {
        var instance = MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();
        return instance is null ? null : $"{instance.Version} {instance.MSBuildPath}";
    }

    private static void EnsureMsBuild()
    {
        lock (MsBuildGate)
        {
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        }
    }

    // ---- 以下方法只在 MSBuild 注册之后才会被 JIT，避免提前加载 Microsoft.Build 程序集 ----

    private static async Task AnalyzeCoreAsync(string path, IReadOnlyList<string> repoRoot, string basePath, IReadOnlyCollection<string>? only, Action<RoslynRecord> emit, CancellationToken ct)
    {
        var onlySet = only is null ? null : new HashSet<string>(only.Select(Path.GetFullPath), ProjectRoots.PathComparer);
        // only 里的文件声明 / 注册的 Handler 所处理的消息简单名：文本里出现它们的文档也要重走
        var affectedMessages = new HashSet<string>(StringComparer.Ordinal);
        bool InOnly(string? filePath) => onlySet is not null && filePath is not null && onlySet.Contains(filePath);
        var errors = 0;
        var lap = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string phase) { emit(new TimingRec(phase, lap.Elapsed.TotalSeconds)); lap.Restart(); }
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
        Lap("open");

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
        Lap("compile");

        // 第一遍：收集 Handler<T>。键用 MessageKey：闭合构造泛型消息（IdentifiedCommand<CreateOrderCommand, bool>）带类型实参，
        // 开放泛型基 Handler 的消息（IdentifiedCommand<T, R>）落在泛型定义键上；查找时先按构造键，再退回定义键（见 DocumentWalker.EmitInvocation）
        var handlers = new DispatchHandlers();
        foreach (var (_, compilation) in compilations)
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace, repoRoot))
            {
                var declaredInOnly = onlySet is not null && type.DeclaringSyntaxReferences.Any(r => InOnly(r.SyntaxTree.FilePath));
                foreach (var (message, entry) in HandledMessages(type))
                {
                    handlers.Add(message, type, entry);
                    if (declaredInOnly) affectedMessages.Add(message.Name);
                }
            }
        // DI 里按消息闭合的开放泛型 Handler（AddScoped<ICommandHandler<X, R>, EnqueueHandler<X>>()）：类型声明上看不出它处理谁，只有注册处知道
        foreach (var (_, compilation) in compilations)
            foreach (var (impl, message, registeredIn) in ClosedHandlerRegistrations(compilation, repoRoot))
            {
                handlers.AddClosedRegistration(impl.OriginalDefinition, message, EntryOf(impl.OriginalDefinition));
                if (InOnly(registeredIn) || impl.OriginalDefinition.DeclaringSyntaxReferences.Any(r => InOnly(r.SyntaxTree.FilePath))) affectedMessages.Add(message.Name);
            }
        Lap("handlers");

        // 第二遍：逐文件输出
        var documents = compilations
            .SelectMany(c => c.Project.Documents.Select(d => (c.Compilation, Document: d)))
            .Where(x => x.Document.FilePath is { } fp && fp.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        && SymbolKeys.IsUnderAny(fp, repoRoot) && !SymbolKeys.IsGeneratedPath(fp))
            // 同一文件被多个项目（多目标框架、链接文件）包含时只出一次；大小写按平台——Linux 上 Program.cs 与 program.cs 是两个文件
            .GroupBy(x => x.Document.FilePath!, ProjectRoots.PathComparer).Select(g => g.First())
            .ToList();
        if (onlySet is not null)
        {
            var mentions = affectedMessages.Count == 0 ? null
                : new Regex(@"\b(?:" + string.Join("|", affectedMessages.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
            var selected = new List<(Compilation, Document)>();
            foreach (var x in documents)
            {
                if (InOnly(x.Document.FilePath)) { selected.Add(x); continue; }
                if (mentions is not null && mentions.IsMatch((await x.Document.GetTextAsync(ct)).ToString())) selected.Add(x);
            }
            documents = selected;
        }
        var done = 0;
        foreach (var (compilation, document) in documents)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(basePath, document.FilePath!).Replace('\\', '/');
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
        Lap("walk");
        emit(new DoneRec(compilations.Count, documents.Count, errors));
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol ns, IReadOnlyList<string> repoRoot)
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

    /// <summary>类型实现的分派器 Handler 接口（<see cref="IsDispatchHandlerInterface"/>）：返回 (消息类型, 入口方法或类型本身)。消息必须是 <see cref="IsMessageType"/>。</summary>
    internal static IEnumerable<(ITypeSymbol Message, ISymbol Entry)> HandledMessages(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (!IsDispatchHandlerInterface(iface) || !IsMessageType(iface.TypeArguments[0])) continue;
            yield return (iface.TypeArguments[0], EntryOf(type));
        }
    }

    /// <summary>Handler 的入口：自己声明的 Handle* / Consume* / Execute* 方法；没有（处理逻辑在基类里）就是类型本身。</summary>
    internal static ISymbol EntryOf(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && HandlerEntry().IsMatch(m.Name)) as ISymbol ?? type;

    /// <summary>
    /// 分派器解析的 Handler 接口：名字以 Handler / Consumer 结尾的泛型接口，且它有一个 Handle* / Consume* / Execute* 方法
    /// 直接以第一个类型参数为参数——<c>ICommandHandler&lt;TCommand, R&gt;.HandleAsync(TCommand)</c>、MediatR 的 <c>IRequestHandler.Handle(TRequest)</c>、
    /// <c>INotificationHandler.Handle(TNotification)</c>。Consumer 接口的参数可以是包着它的上下文（MassTransit <c>Consume(ConsumeContext&lt;T&gt;)</c>）。
    /// 不算：后台作业 <c>IJobHandler&lt;TWork, …&gt;.HandleAsync(IJobContext&lt;TWork&gt;)</c>（作业引擎执行，不经分派器）、
    /// 比较器 <c>ICompareHandler&lt;T&gt;.Compare(T, T)</c>（方法不是 Handle）。
    /// </summary>
    internal static bool IsDispatchHandlerInterface(INamedTypeSymbol iface)
    {
        if (iface.TypeArguments.Length == 0 || !HandlerInterface().IsMatch(iface.Name)) return false;
        var definition = iface.OriginalDefinition;
        var message = definition.TypeParameters[0];
        var consumer = definition.Name.EndsWith("Consumer", StringComparison.Ordinal);
        bool Takes(ITypeSymbol t) => SymbolEqualityComparer.Default.Equals(t, message)
            || (consumer && t is INamedTypeSymbol n && n.TypeArguments.Any(a => SymbolEqualityComparer.Default.Equals(a, message)));
        var methods = definition.GetMembers().OfType<IMethodSymbol>().ToList();
        if (methods.Any(m => HandlerEntry().IsMatch(m.Name) && m.Parameters.Any(p => Takes(p.Type)))) return true;
        // 标记接口：IXxxHandler<T> : IRequestHandler<T, Unit> { }——自己没有方法，看继承的接口里以同一个类型参数为消息的那个
        return methods.Count == 0
               && definition.AllInterfaces.Any(i => i.TypeArguments.Length > 0 && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], message) && IsDispatchHandlerInterface(i));
    }

    /// <summary>
    /// 能当消息的类型：不是类型参数（开放泛型 Handler 的 TCommand）、内置类型、数组、元组、委托，也不是 System / Microsoft 命名空间下的框架类型（List / IList / IReadOnlyList……）。
    /// 外部包里定义的契约类型（NuGet 里的集成事件）照常算。
    /// </summary>
    internal static bool IsMessageType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || type.SpecialType != SpecialType.None || named.IsTupleType || type.TypeKind is TypeKind.Error or TypeKind.Delegate) return false;
        var ns = named.ContainingNamespace?.ToDisplayString() ?? "";
        return !(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    /// <summary>
    /// 编译里 DI 按消息闭合开放泛型 Handler 的注册：<c>AddScoped&lt;ICommandHandler&lt;X, R&gt;, EnqueueHandler&lt;X&gt;&gt;()</c> 与 <c>AddScoped(typeof(…), typeof(…))</c>。
    /// 返回 (闭合的实现, 消息, 注册所在文件)。实现本身不是泛型的不用管：它的类型声明上已经写了处理谁。先按方法名在语法上筛，只给命中的调用绑定语义。
    /// </summary>
    internal static IEnumerable<(INamedTypeSymbol Impl, ITypeSymbol Message, string FilePath)> ClosedHandlerRegistrations(Compilation compilation, IReadOnlyList<string> repoRoot)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(tree.FilePath) || !SymbolKeys.IsUnderAny(tree.FilePath, repoRoot) || SymbolKeys.IsGeneratedPath(tree.FilePath)) continue;
            SemanticModel? model = null;
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = inv.Expression switch { MemberAccessExpressionSyntax m => m.Name.Identifier.Text, SimpleNameSyntax s => s.Identifier.Text, _ => null };
                if (name is null || !DiMethods.Contains(name)) continue;
                model ??= compilation.GetSemanticModel(tree);
                if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method) continue;
                var (service, impl) = DiTypes(inv, method, model);
                if (service is not INamedTypeSymbol iface || impl is not INamedTypeSymbol { IsGenericType: true } closed) continue;
                if (SymbolEqualityComparer.Default.Equals(closed, closed.OriginalDefinition) || SymbolKeys.ContainsTypeParameter(closed) || !SymbolKeys.IsInRepo(closed, repoRoot)) continue;
                if (!IsDispatchHandlerInterface(iface) || !SymbolKeys.IsClosedConstructedGeneric(iface) || !IsMessageType(iface.TypeArguments[0])) continue;
                // 实现（闭合后）确实实现了这个接口
                if (!closed.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iface))) continue;
                yield return (closed, iface.TypeArguments[0], tree.FilePath);
            }
        }
    }

    /// <summary>DI 注册调用里的 (服务, 实现)：泛型两参 AddScoped&lt;I, T&gt;() 或 AddScoped(typeof(I), typeof(T))。</summary>
    internal static (ITypeSymbol? Service, ITypeSymbol? Impl) DiTypes(InvocationExpressionSyntax inv, IMethodSymbol method, SemanticModel model)
    {
        if (method.TypeArguments.Length == 2) return (method.TypeArguments[0], method.TypeArguments[1]);
        var typeOfs = inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
        return typeOfs.Count == 2 ? (model.GetTypeInfo(typeOfs[0].Type).Type, model.GetTypeInfo(typeOfs[1].Type).Type) : (null, null);
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
