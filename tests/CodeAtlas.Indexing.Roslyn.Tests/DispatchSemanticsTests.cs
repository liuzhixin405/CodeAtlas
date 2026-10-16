namespace CodeAtlas.Indexing.Roslyn.Tests;

/// <summary>
/// 分派语义（Fixtures/RoslynSample/Sample.Core/DispatchSemantics.cs）：handles 只认分派器的 Handler 接口；DI 闭合注册的开放泛型 Handler；
/// 泛型转发方法在调用处按实参类型分派；按基类型 Publish 连到子类消息的 Handler。
/// </summary>
public partial class AnalyzerTests
{
    private const string D = "Sample.Core.Dispatching.";

    [Fact]
    public void Handles_OnlyDispatcherHandlerInterfaces_NotJobsComparersOrCollections()
    {
        // 后台作业：HandleAsync 的参数是 IJobContext<TWork>，不是 TWork 本身 → 不经分派器
        Assert.DoesNotContain(fx.EdgesFrom(D + "ExportJobHandler", "dispatches"), e => e.Via == "handles");
        // 比较器：方法叫 Compare，不是 Handle
        Assert.DoesNotContain(fx.EdgesFrom(D + "ItemCompareHandler", "dispatches"), e => e.Via == "handles");
        // 消息是 List<string>：框架集合不是消息
        Assert.DoesNotContain(fx.EdgesFrom(D + "TagListHandler", "dispatches"), e => e.Via == "handles");
        // 开放泛型 Handler 的 TCommand 不是消息
        Assert.DoesNotContain(fx.EdgesFrom(D + "EnqueueHandler<TCommand>", "dispatches"), e => e.Via == "handles" && e.ToQualifiedName == "TCommand");
        // 普通的命令 / 事件 Handler 照常
        Assert.Equal(D + "ItemShipped", fx.EdgesFrom(D + "ItemShippedHandler", "dispatches").Single(e => e.Via == "handles").ToQualifiedName);
    }

    [Fact]
    public void ClosedDiRegistration_OfOpenGenericHandler_HandlesAndReceivesTheDispatch()
    {
        var handles = fx.EdgesFrom(D + "EnqueueHandler<TCommand>", "dispatches").Single(e => e.Via == "handles");
        Assert.Equal((D + "ExportAsyncCommand", false), (handles.ToQualifiedName, handles.ToIsExternal));
        Assert.Equal("Sample.Core/DispatchSemantics.cs", handles.Path);

        // SendAsync(new ExportAsyncCommand(id)) 只到 EnqueueHandler（入队），不到作业 Handler
        var dispatch = fx.EdgesFrom(D + "ExportController.Export(int, CancellationToken)", "dispatches").Single();
        Assert.Equal((D + "EnqueueHandler<TCommand>.HandleAsync(TCommand, CancellationToken)", "dispatch:ExportAsyncCommand", 1.0),
            (dispatch.ToQualifiedName, dispatch.Via, dispatch.Confidence));
    }

    [Fact]
    public void ForwardingMethod_DispatchesFromTheCallSite_ByTheArgumentType()
    {
        var replay = fx.EdgesFrom(D + "ItemReplayHandler.Replay(int, CancellationToken)", "dispatches").Single();
        Assert.Equal(("Sample.Core.DeleteItemCommandHandler.HandleAsync(DeleteItemCommand, CancellationToken)", "dispatch:DeleteItemCommand", 0.7),
            (replay.ToQualifiedName, replay.Via, replay.Confidence));
        // 转发方法本身照常是调用（它还做了别的事），不是 conduit
        Assert.Contains(fx.EdgesFrom(D + "ItemReplayHandler.Replay(int, CancellationToken)", "calls"),
            e => e.ToQualifiedName == D + "ReplayHandlerBase.SendReplayedAsync<T>(object, string, CancellationToken)" && e.Via is null);
        // 推不出实参类型：不硬连
        Assert.Empty(fx.EdgesFrom(D + "ItemReplayHandler.ReplayUnknown(object, CancellationToken)", "dispatches"));
        // 转发方法里 SendAsync(object) 自己不分派
        Assert.Empty(fx.EdgesFrom(D + "ReplayHandlerBase.SendReplayedAsync<T>(object, string, CancellationToken)", "dispatches"));
    }

    [Fact]
    public void PublishOfBaseTypedEvent_GoesToEveryHandlerOfItsSubtypes_WithLowerConfidence()
    {
        var dispatches = fx.EdgesFrom(D + "DomainEventDispatcher.DispatchAsync(IPublisher, IReadOnlyList<INotification>)", "dispatches")
            .OrderBy(e => e.Via, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [("dispatch:ItemCancelled", D + "ItemCancelledHandler.Handle(ItemCancelled, CancellationToken)", 0.5),
             ("dispatch:ItemShipped", D + "ItemShippedHandler.Handle(ItemShipped, CancellationToken)", 0.5)],
            dispatches.Select(e => (e.Via!, e.ToQualifiedName, e.Confidence)));
        // Publish 这一跳是管道细节
        Assert.Contains(fx.EdgesFrom(D + "DomainEventDispatcher.DispatchAsync(IPublisher, IReadOnlyList<INotification>)", "calls"),
            e => e.ToQualifiedName.StartsWith(D + "IPublisher.Publish", StringComparison.Ordinal) && e.Via == "conduit");
    }
}
