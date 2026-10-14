namespace Acme.Infrastructure;
public sealed class FeedRepository : IFeedRepository
{
    public Task<List<FeedDto>> ListFeedsAsync(int serverId, CancellationToken ct) => db.Query(serverId);
}
