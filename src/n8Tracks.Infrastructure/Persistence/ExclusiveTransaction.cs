using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Search;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One transaction on the request's context. SQLite transactions from Microsoft.Data.Sqlite begin
/// <c>IMMEDIATE</c>, taking the write lock at once, so a second one waits (up to the busy timeout)
/// until the first commits, and never reads what the first is about to change. Just before it commits,
/// the search index is brought up to date with what the work wrote (#223: the Songs the tables'
/// triggers recorded), so the index changes in the same transaction as the catalog and no service can
/// forget it.
/// </summary>
internal sealed class ExclusiveTransaction(N8TracksDbContext context, SearchIndexer indexer) : IExclusiveTransaction
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            await indexer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
    }
}
