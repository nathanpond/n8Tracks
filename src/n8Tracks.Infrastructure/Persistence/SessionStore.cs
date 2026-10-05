using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Auth;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SessionStore(N8TracksDbContext context) : ISessionStore, ISignInAccounts, IAccountPasswords
{
    public Task<SignInAccount?> FindByIdAsync(Guid administratorId, CancellationToken cancellationToken) =>
        context.Administrators.AsNoTracking()
            .Where(administrator => administrator.Id == administratorId)
            .Select(administrator => new SignInAccount(administrator.Id, administrator.Username, administrator.PasswordHash))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<SignInAccount?> FindAdministratorAsync(CancellationToken cancellationToken) =>
        context.Administrators.AsNoTracking()
            .Select(administrator => new SignInAccount(administrator.Id, administrator.Username, administrator.PasswordHash))
            .SingleOrDefaultAsync(cancellationToken);

    public Task SetPasswordHashAsync(Guid administratorId, string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);

        return context.Administrators
            .Where(administrator => administrator.Id == administratorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(administrator => administrator.PasswordHash, passwordHash), cancellationToken);
    }

    public async Task<SignInAccount?> FindByUsernameKeyAsync(string usernameKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usernameKey);

        return await context.Administrators.AsNoTracking()
            .Where(administrator => administrator.UsernameKey == usernameKey)
            .Select(administrator => new SignInAccount(administrator.Id, administrator.Username, administrator.PasswordHash))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CreateAsync(NewSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var created = UtcText.From(session.CreatedUtc);
        context.Sessions.Add(new SessionRecord
        {
            IdHash = session.IdHash,
            AdministratorId = session.AdministratorId,
            CreatedUtc = created,
            LastUsedUtc = created,
            UserAgent = session.UserAgent,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredSession?> FindAsync(string idHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idHash);

        var found = await context.Sessions.AsNoTracking()
            .Where(session => session.IdHash == idHash)
            .Join(
                context.Administrators,
                session => session.AdministratorId,
                administrator => administrator.Id,
                (session, administrator) => new { session.IdHash, session.AdministratorId, administrator.Username, session.LastUsedUtc })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : new StoredSession(found.IdHash, found.AdministratorId, found.Username, UtcText.Parse(found.LastUsedUtc));
    }

    public Task TouchAsync(string idHash, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken)
    {
        var lastUsed = UtcText.From(lastUsedUtc);

        return context.Sessions
            .Where(session => session.IdHash == idHash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.LastUsedUtc, lastUsed), cancellationToken);
    }

    public Task DeleteAsync(string idHash, CancellationToken cancellationToken) =>
        context.Sessions.Where(session => session.IdHash == idHash).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteAllAsync(Guid administratorId, CancellationToken cancellationToken) =>
        context.Sessions.Where(session => session.AdministratorId == administratorId).ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteAllExceptAsync(Guid administratorId, string keepIdHash, CancellationToken cancellationToken) =>
        context.Sessions
            .Where(session => session.AdministratorId == administratorId && session.IdHash != keepIdHash)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<int> DeleteUnusedSinceAsync(DateTimeOffset lastUsedBeforeUtc, CancellationToken cancellationToken)
    {
        // Text comparison is time comparison: every stored time has the same fixed-width format.
        var cutoff = UtcText.From(lastUsedBeforeUtc);

        return context.Sessions
            .Where(session => string.Compare(session.LastUsedUtc, cutoff) < 0)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
