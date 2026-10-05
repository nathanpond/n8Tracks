using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class CredentialStore(N8TracksDbContext context) : ICredentialStore
{
    private const char ScopeSeparator = ' ';

    /// <summary>SQLITE_CONSTRAINT_UNIQUE: a unique index refused the row.</summary>
    private const int UniqueConstraintFailed = 2067;

    public async Task<bool> TryCreateAsync(NewCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var record = new CredentialRecord
        {
            Id = credential.Id,
            Name = credential.Name,
            NameKey = credential.NameKey,
            Kind = credential.Kind,
            Scopes = string.Join(ScopeSeparator, credential.Scopes),
            TokenHash = credential.TokenHash,
            CreatedUtc = UtcText.From(credential.CreatedUtc),
        };

        context.Credentials.Add(record);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception) when (IsNameTaken(exception))
        {
            context.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(string? kind, CancellationToken cancellationToken)
    {
        var query = context.Credentials.AsNoTracking();
        if (kind is not null)
        {
            query = query.Where(credential => credential.Kind == kind);
        }

        // The times are fixed-width UTC text, so text order is time order; the ID (a UUIDv7) breaks ties.
        var records = await query
            .OrderBy(credential => credential.RevokedUtc != null)
            .ThenByDescending(credential => credential.CreatedUtc)
            .ThenByDescending(credential => credential.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. records.Select(Summary)];
    }

    public async Task<CredentialSummary?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var found = await context.Credentials.AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.Id == id, cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summary(found);
    }

    public async Task<StoredCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        var found = await context.Credentials.AsNoTracking()
            .SingleOrDefaultAsync(credential => credential.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);

        return found is null
            ? null
            : new StoredCredential(
                found.Id,
                found.Name,
                found.Kind,
                SplitScopes(found.Scopes),
                found.TokenHash,
                found.LastUsedUtc is { } lastUsed ? UtcText.Parse(lastUsed) : null,
                found.RevokedUtc is { } revoked ? UtcText.Parse(revoked) : null);
    }

    public async Task<CredentialRenameResult> TryRenameAsync(Guid id, string name, string nameKey, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(nameKey);

        try
        {
            // One conditional statement: the revision check and the write cannot be split by another writer.
            var updated = await context.Credentials
                .Where(credential => credential.Id == id && credential.RevokedUtc == null && credential.Revision == revision)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(credential => credential.Name, name)
                        .SetProperty(credential => credential.NameKey, nameKey)
                        .SetProperty(credential => credential.Revision, credential => credential.Revision + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            return updated == 1 ? CredentialRenameResult.Renamed : CredentialRenameResult.NotApplied;
        }
        catch (Exception exception) when (IsNameTaken(exception))
        {
            return CredentialRenameResult.NameTaken;
        }
    }

    public Task RevokeAsync(Guid id, DateTimeOffset revokedUtc, CancellationToken cancellationToken)
    {
        var revoked = UtcText.From(revokedUtc);

        return context.Credentials
            .Where(credential => credential.Id == id && credential.RevokedUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(credential => credential.RevokedUtc, revoked)
                    .SetProperty(credential => credential.Revision, credential => credential.Revision + 1),
                cancellationToken);
    }

    public Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken)
    {
        var lastUsed = UtcText.From(lastUsedUtc);

        return context.Credentials
            .Where(credential => credential.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(credential => credential.LastUsedUtc, lastUsed), cancellationToken);
    }

    private static CredentialSummary Summary(CredentialRecord record) =>
        new(
            record.Id,
            record.Name,
            record.Kind,
            SplitScopes(record.Scopes),
            UtcText.Parse(record.CreatedUtc),
            record.LastUsedUtc is { } lastUsed ? UtcText.Parse(lastUsed) : null,
            record.RevokedUtc is { } revoked ? UtcText.Parse(revoked) : null,
            record.Revision);

    private static string[] SplitScopes(string scopes) => scopes.Split(ScopeSeparator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The name index, the only unique index a new row can collide on by anything but chance.</summary>
    private static bool IsNameTaken(Exception exception) =>
        (exception as SqliteException ?? exception.InnerException as SqliteException) is { SqliteExtendedErrorCode: UniqueConstraintFailed } sqlite
        && sqlite.Message.Contains("name_key", StringComparison.Ordinal);
}
