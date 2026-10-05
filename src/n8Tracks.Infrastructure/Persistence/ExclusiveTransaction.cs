using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Auth;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One transaction on the request's context. SQLite transactions from Microsoft.Data.Sqlite begin
/// <c>IMMEDIATE</c>, taking the write lock at once, so a second one waits (up to the busy timeout)
/// until the first commits, and never reads what the first is about to change.
/// </summary>
internal sealed class ExclusiveTransaction(N8TracksDbContext context) : IExclusiveTransaction
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
    }
}
