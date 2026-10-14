using Acme.Abstractions;
namespace Acme.Api;
public class Mt4Controller
{
    private readonly IDispatcher dispatcher;
    public Task<List<FeedDto>> ListFeeds(int serverId, CancellationToken ct)
        => dispatcher.QueryAsync(new ListFeedsQuery(serverId), ct);
}
