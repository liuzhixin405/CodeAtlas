using Microsoft.Extensions.DependencyInjection;

namespace Sample.Core.Dispatching;

// ---- 不经分派器的 *Handler：后台作业（作业引擎执行，参数是包着工作项的上下文）、比较器（方法不是 Handle）、消息是框架集合 ----

public interface IJobContext<TWork>
{
    TWork Work { get; }
}

public interface IJobHandler<TWork, TResult>
{
    Task<TResult> HandleAsync(IJobContext<TWork> context, CancellationToken ct);
}

public interface ICompareHandler<T>
{
    void Compare(T left, T right);
}

public interface IListHandler<T>
{
    Task HandleAsync(T items, CancellationToken ct);
}

public sealed record JobAccepted(Guid Id);

// 异步命令：既是分派器的命令（经 EnqueueHandler 入队），又是作业的工作项（ExportJobHandler 在作业引擎里执行）
public sealed record ExportAsyncCommand(int Id);

public sealed class ExportJobHandler : IJobHandler<ExportAsyncCommand, int>
{
    public Task<int> HandleAsync(IJobContext<ExportAsyncCommand> context, CancellationToken ct) => Task.FromResult(context.Work.Id);
}

public sealed class ItemCompareHandler : ICompareHandler<Item>
{
    public void Compare(Item left, Item right) { }
}

public sealed class TagListHandler : IListHandler<List<string>>
{
    public Task HandleAsync(List<string> items, CancellationToken ct) => Task.CompletedTask;
}

// ---- 开放泛型 Handler：类型声明看不出处理谁，DI 按消息闭合注册 ----

public sealed class EnqueueHandler<TCommand> : ICommandHandler<TCommand, JobAccepted>
{
    public Task<JobAccepted> HandleAsync(TCommand command, CancellationToken ct) => Task.FromResult(new JobAccepted(Guid.NewGuid()));
}

public static class DispatchRegistration
{
    public static IServiceCollection AddDispatching(this IServiceCollection services) =>
        services.AddScoped<ICommandHandler<ExportAsyncCommand, JobAccepted>, EnqueueHandler<ExportAsyncCommand>>();
}

public sealed class ExportController(IDispatcher dispatcher)
{
    public Task<JobAccepted> Export(int id, CancellationToken ct) => dispatcher.SendAsync<JobAccepted>(new ExportAsyncCommand(id), ct);
}

// ---- 泛型转发：基类方法把参数原样交给分派器，消息类型在调用处才确定 ----

public abstract class ReplayHandlerBase(IDispatcher dispatcher)
{
    protected Task<T> SendReplayedAsync<T>(object command, string actor, CancellationToken ct) => dispatcher.SendAsync<T>(command, ct);
}

public sealed class ItemReplayHandler(IDispatcher dispatcher) : ReplayHandlerBase(dispatcher)
{
    public Task<bool> Replay(int id, CancellationToken ct)
    {
        var command = new DeleteItemCommand(id);
        return SendReplayedAsync<bool>(command, "someone", ct);
    }

    // 实参类型推不出 Handler（object）：不连
    public Task<bool> ReplayUnknown(object payload, CancellationToken ct) => SendReplayedAsync<bool>(payload, "someone", ct);
}

// ---- 按基类型发布：领域事件列表的元素类型是 INotification ----

public interface INotification { }

public interface INotificationHandler<TNotification> where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken ct);
}

public interface IPublisher
{
    Task Publish(object notification, CancellationToken ct = default);
}

public sealed record ItemShipped(int Id) : INotification;

public sealed record ItemCancelled(int Id) : INotification;

public sealed class ItemShippedHandler : INotificationHandler<ItemShipped>
{
    public Task Handle(ItemShipped notification, CancellationToken ct) => Task.CompletedTask;
}

public sealed class ItemCancelledHandler : INotificationHandler<ItemCancelled>
{
    public Task Handle(ItemCancelled notification, CancellationToken ct) => Task.CompletedTask;
}

public static class DomainEventDispatcher
{
    public static async Task DispatchAsync(this IPublisher publisher, IReadOnlyList<INotification> events)
    {
        foreach (var domainEvent in events)
            await publisher.Publish(domainEvent);
    }
}
