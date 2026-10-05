namespace n8Tracks.Application.Credentials;

/// <summary>A credential to store. The token itself is never stored, only its hash.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">Trimmed.</param>
/// <param name="NameKey">What names are compared by (<see cref="CredentialService.NameKey"/>).</param>
/// <param name="Kind">One of <see cref="CredentialKinds.All"/>.</param>
/// <param name="Scopes">At least one of <see cref="CredentialScopes.All"/>, each once, in that list's order.</param>
/// <param name="TokenHash">The hash of the token (<see cref="CredentialToken.Hash"/>).</param>
/// <param name="CreatedUtc">When it was created.</param>
public sealed record NewCredential(Guid Id, string Name, string NameKey, string Kind, IReadOnlyList<string> Scopes, string TokenHash, DateTimeOffset CreatedUtc);

/// <summary>A stored credential, as verification sees it.</summary>
public sealed record StoredCredential(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Scopes,
    string TokenHash,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc);

/// <summary>A credential as the administrator sees it: everything but the token and its hash.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">Trimmed, unique ignoring case among credentials that are not revoked.</param>
/// <param name="Kind">One of <see cref="CredentialKinds.All"/>; descriptive only.</param>
/// <param name="Scopes">What it may do, in the order of <see cref="CredentialScopes.All"/>.</param>
/// <param name="CreatedUtc">When it was created.</param>
/// <param name="LastUsedUtc">When its token was last used (to the minute), or null if never.</param>
/// <param name="RevokedUtc">When it was revoked, or null while it is in force.</param>
/// <param name="Revision">Starts at 1 and goes up by one on each rename or revocation.</param>
public sealed record CredentialSummary(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc,
    int Revision);

/// <summary>How a conditional rename in the store ended.</summary>
public enum CredentialRenameResult
{
    /// <summary>The name was changed and the revision went up by one.</summary>
    Renamed,

    /// <summary>Nothing was changed: the credential is gone, revoked, or not at the given revision.</summary>
    NotApplied,

    /// <summary>Nothing was changed: another credential that is not revoked has the name.</summary>
    NameTaken,
}

/// <summary>Where credentials are kept.</summary>
public interface ICredentialStore
{
    /// <summary>Stores the credential; false, storing nothing, when a credential that is not revoked has its name key.</summary>
    Task<bool> TryCreateAsync(NewCredential credential, CancellationToken cancellationToken);

    /// <summary>
    /// Every credential, revoked or not, or only those of <paramref name="kind"/>: those in force
    /// first, newest first within each group.
    /// </summary>
    Task<IReadOnlyList<CredentialSummary>> ListAsync(string? kind, CancellationToken cancellationToken);

    /// <summary>The credential with <paramref name="id"/>, revoked or not, or null.</summary>
    Task<CredentialSummary?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The credential whose token hash is <paramref name="tokenHash"/>, revoked or not, or null.</summary>
    Task<StoredCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the credential if it is not revoked and is at <paramref name="revision"/>, raising the
    /// revision by one, in one statement.
    /// </summary>
    Task<CredentialRenameResult> TryRenameAsync(Guid id, string name, string nameKey, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the credential revoked at <paramref name="revokedUtc"/> and raises its revision by one,
    /// unless it is already revoked, which leaves it as it is.
    /// </summary>
    Task RevokeAsync(Guid id, DateTimeOffset revokedUtc, CancellationToken cancellationToken);

    /// <summary>Records that the credential was used at <paramref name="lastUsedUtc"/>.</summary>
    Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken);
}
