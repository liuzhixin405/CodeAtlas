using Acme.Queries;
namespace Acme.Handlers;
public sealed class ListFeedsHandler : IQueryHandler<ListFeedsQuery, List<FeedDto>>
{
    private readonly IFeedRepository repo;
    public Task<List<FeedDto>> HandleAsync(ListFeedsQuery query, CancellationToken ct) => repo.ListFeedsAsync(query.ServerId, ct);
}
