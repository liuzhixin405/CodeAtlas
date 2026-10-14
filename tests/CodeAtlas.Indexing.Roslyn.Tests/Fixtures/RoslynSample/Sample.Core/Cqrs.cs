namespace Sample.Core;

public interface IQueryHandler<TQuery, TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
}

public interface IDispatcher
{
    Task<T> QueryAsync<T>(object query, CancellationToken ct);
    Task<T> SendAsync<T>(object command, CancellationToken ct);
}

public sealed class Dispatcher : IDispatcher
{
    public Task<T> QueryAsync<T>(object query, CancellationToken ct) => Task.FromResult(default(T)!);
    public Task<T> SendAsync<T>(object command, CancellationToken ct) => Task.FromResult(default(T)!);
}

// ---- 泛型包装命令（eShop 的 IdentifiedCommand<T, R> 形态）：一个开放泛型基 Handler，两个具体子类 ----

public interface ICommandHandler<TCommand, TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}

public sealed record Identified<TCommand, TResult>(TCommand Command, Guid Id);

public sealed record CreateItemCommand(string Name);

public sealed record DeleteItemCommand(int Id);

public sealed record ArchiveItemCommand(int Id);

public abstract class IdentifiedHandler<TCommand, TResult> : ICommandHandler<Identified<TCommand, TResult>, TResult>
{
    protected readonly IDispatcher dispatcher = new Dispatcher();

    // 基类把内层命令再分派出去：参数类型是 TCommand，只有按子类的类型实参代换后才知道去哪
    public Task<TResult> HandleAsync(Identified<TCommand, TResult> command, CancellationToken ct) => dispatcher.SendAsync<TResult>(command.Command!, ct);
}

public sealed class CreateItemIdentifiedHandler : IdentifiedHandler<CreateItemCommand, bool> { }

public sealed class DeleteItemIdentifiedHandler : IdentifiedHandler<DeleteItemCommand, bool> { }

// 内层命令的具体 Handler：代换后的再分派目标
public sealed class CreateItemCommandHandler : ICommandHandler<CreateItemCommand, bool>
{
    public Task<bool> HandleAsync(CreateItemCommand command, CancellationToken ct) => Task.FromResult(true);
}

public sealed class DeleteItemCommandHandler : ICommandHandler<DeleteItemCommand, bool>
{
    public Task<bool> HandleAsync(DeleteItemCommand command, CancellationToken ct) => Task.FromResult(true);
}

// ---- MassTransit 形态的消费者：接口名以 Consumer 结尾，也算 Handler ----

public interface IConsumer<TMessage>
{
    Task Consume(TMessage message);
}

public sealed record OrderPlaced(int Id);

public sealed class OrderPlacedConsumer : IConsumer<OrderPlaced>
{
    public Task Consume(OrderPlaced message) => Task.CompletedTask;
}

public sealed record ListQuery(int ServerId);

public sealed class ListQueryHandler : IQueryHandler<ListQuery, List<Item>>
{
    private readonly IRepo repo;

    public ListQueryHandler(IRepo repo) => this.repo = repo;

    public Task<List<Item>> HandleAsync(ListQuery query, CancellationToken ct) => Task.FromResult(repo.All());
}
