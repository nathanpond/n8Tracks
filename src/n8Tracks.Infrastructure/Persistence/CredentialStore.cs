using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class CredentialStore(N8TracksDbContext context) : ICredentialStore
{
    private const char ScopeSeparator = ' ';

    public async Task CreateAsync(NewCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);

        context.Credentials.Add(new CredentialRecord
        {
            Id = credential.Id,
            Name = credential.Name,
            Kind = credential.Kind,
            Scopes = string.Join(ScopeSeparator, credential.Scopes),
            TokenHash = credential.TokenHash,
            CreatedUtc = UtcText.From(credential.CreatedUtc),
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
                found.Scopes.Split(ScopeSeparator, StringSplitOptions.RemoveEmptyEntries),
                found.TokenHash,
                found.LastUsedUtc is { } lastUsed ? UtcText.Parse(lastUsed) : null,
                found.RevokedUtc is { } revoked ? UtcText.Parse(revoked) : null);
    }

    public Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken)
    {
        var lastUsed = UtcText.From(lastUsedUtc);

        return context.Credentials
            .Where(credential => credential.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(credential => credential.LastUsedUtc, lastUsed), cancellationToken);
    }
}
