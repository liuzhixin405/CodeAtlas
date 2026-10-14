using Sample.Core;

namespace Sample.App;

public class Controller
{
    private readonly IRepo repo;
    private readonly IDispatcher dispatcher;

    public Controller(IRepo repo, IDispatcher dispatcher)
    {
        this.repo = repo;
        this.dispatcher = dispatcher;
    }

    public Item ById(int id) => repo.Find(id);

    public Item ByName(string name) => repo.Find(name);

    public Task<List<Item>> List(int serverId, CancellationToken ct)
        => dispatcher.QueryAsync<List<Item>>(new ListQuery(serverId), ct);

    public int Count() => repo.All().Count;

    // 构造泛型消息：只应分派到 CreateItemIdentifiedHandler，不是全部 IdentifiedHandler<,> 子类
    public Task<bool> Create(string name, CancellationToken ct)
        => dispatcher.SendAsync<bool>(new Identified<CreateItemCommand, bool>(new CreateItemCommand(name), Guid.NewGuid()), ct);

    // 没有具体子类处理的构造泛型消息：退回到开放泛型基 Handler
    public Task<bool> Archive(int id, CancellationToken ct)
        => dispatcher.SendAsync<bool>(new Identified<ArchiveItemCommand, bool>(new ArchiveItemCommand(id), Guid.NewGuid()), ct);
}
