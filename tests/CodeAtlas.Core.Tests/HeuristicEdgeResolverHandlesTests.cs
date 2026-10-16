using CodeAtlas.Core.Model;
using CodeAtlas.Core.Resolution;
using CodeAtlas.Core.Storage;

namespace CodeAtlas.Core.Tests;

/// <summary>
/// tree-sitter 的 handles 把关：基类列表里写着 IXxxHandler&lt;M, …&gt;，但类自己的 HandleAsync 不以 M 为参数（后台作业拿的是 IJobContext&lt;M&gt;）→ 不是分派器的 Handler。
/// </summary>
public class HeuristicEdgeResolverHandlesTests
{
    [Fact]
    public async Task Handles_KeptOnlyWhenTheHandleMethodTakesTheMessage()
    {
        using var db = IndexDatabase.OpenInMemory();
        var writer = new IndexWriter(db);
        var reader = new IndexReader(db);
        (long File, IReadOnlyList<long> Ids) File(string path, params (SymbolKind Kind, string Name, string? Signature, int? Parent)[] symbols)
        {
            var fileId = writer.UpsertFile(path, "csharp", "h", 1, FileStatus.Ok);
            var list = new List<ParsedSymbol> { new(SymbolKind.Module, Path.GetFileName(path), path, null, 1, 1, 999, 1, null, null) };
            var line = 2;
            foreach (var (kind, name, signature, parent) in symbols)
            {
                var p = parent ?? 0;
                list.Add(new ParsedSymbol(kind, name, (p == 0 ? "" : list[p].QualifiedName + ".") + name, signature, line, 1, line + 1, 1, p, null));
                line += 2;
            }
            return (fileId, writer.WriteSymbols(fileId, list, IndexSource.TreeSitter));
        }

        var (_, msg) = File("ExportAsyncCommand.cs", (SymbolKind.Class, "ExportAsyncCommand", null, null));
        // 命令 Handler：HandleAsync(ExportAsyncCommand command, …)
        var (cmdFile, cmd) = File("ExportHandler.cs", (SymbolKind.Class, "ExportHandler", null, null),
            (SymbolKind.Method, "HandleAsync", "public Task<int> HandleAsync(Acme.Jobs.ExportAsyncCommand command, CancellationToken ct = default)", 1));
        // 后台作业：HandleAsync(IJobContext<ExportAsyncCommand, Progress> ctx, …)
        var (jobFile, job) = File("ExportJobHandler.cs", (SymbolKind.Class, "ExportJobHandler", null, null),
            (SymbolKind.Method, "HandleAsync", "public async Task<int> HandleAsync(IJobContext<ExportAsyncCommand, Progress> ctx, CancellationToken ct)", 1));
        // 消费者：Consume(ConsumeContext<ExportAsyncCommand> context)
        var (conFile, con) = File("ExportConsumer.cs", (SymbolKind.Class, "ExportConsumer", null, null),
            (SymbolKind.Method, "Consume", "public Task Consume(ConsumeContext<ExportAsyncCommand> context)", 1));
        // 子类没有自己的 Handle（逻辑在基类）；参数表跨行看不全：都照旧保留
        var (subFile, sub) = File("ExportIdentifiedHandler.cs", (SymbolKind.Class, "ExportIdentifiedHandler", null, null));
        var (wrapFile, wrap) = File("ExportWrappedHandler.cs", (SymbolKind.Class, "ExportWrappedHandler", null, null),
            (SymbolKind.Method, "HandleAsync", "public Task<int> HandleAsync(", 1));

        PendingReference Handles(long from, long file, string path) => new(from, file, path, "csharp", "ExportAsyncCommand", EdgeKind.Dispatches, 1, Via: "handles");
        await new HeuristicEdgeResolver().ResolveAsync(new ResolveContext(db,
            [Handles(cmd[1], cmdFile, "ExportHandler.cs"), Handles(job[1], jobFile, "ExportJobHandler.cs"), Handles(con[1], conFile, "ExportConsumer.cs"),
             Handles(sub[1], subFile, "ExportIdentifiedHandler.cs"), Handles(wrap[1], wrapFile, "ExportWrappedHandler.cs")]), CancellationToken.None);

        var handlers = reader.GetDispatchHandlers(msg[1]).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(["ExportConsumer", "ExportHandler", "ExportIdentifiedHandler", "ExportWrappedHandler"], handlers);
    }

    [Fact]
    public void ParameterTypes_StripsModifiersNamesAndDefaults()
    {
        Assert.Equal(["Acme.Cmd", "IJobContext<Cmd, P>", "CancellationToken"],
            HeuristicEdgeResolver.ParameterTypes("public Task HandleAsync(this Acme.Cmd c, in IJobContext<Cmd, P> ctx, CancellationToken ct = default) => x;", "HandleAsync"));
        Assert.Null(HeuristicEdgeResolver.ParameterTypes("public Task HandleAsync(", "HandleAsync"));
    }
}
