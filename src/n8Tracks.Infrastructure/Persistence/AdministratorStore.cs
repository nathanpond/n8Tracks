using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Setup;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class AdministratorStore(N8TracksDbContext context) : IAdministratorStore
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE: a unique index refused the row.</summary>
    private const int UniqueConstraintFailed = 2067;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
        context.Administrators.AsNoTracking().AnyAsync(cancellationToken);

    public async Task<bool> TryCreateAsync(NewAdministrator administrator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administrator);

        var record = new AdministratorRecord
        {
            Id = administrator.Id,
            Username = administrator.Username,
            UsernameKey = administrator.UsernameKey,
            PasswordHash = administrator.PasswordHash,
            CreatedUtc = administrator.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

        context.Administrators.Add(record);
        try
        {
            // One insert, in the caller's transaction if there is one (setup's, with the backup
            // schedule). A second writer waits for the first to commit (the busy timeout) and then
            // fails on the unique slot.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: UniqueConstraintFailed })
        {
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }
}
