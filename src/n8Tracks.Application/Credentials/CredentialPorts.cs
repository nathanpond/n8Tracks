namespace n8Tracks.Application.Credentials;

/// <summary>A credential to store. The token itself is never stored, only its hash.</summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">Trimmed.</param>
/// <param name="Kind">One of <see cref="CredentialKinds.All"/>.</param>
/// <param name="Scopes">At least one of <see cref="CredentialScopes.All"/>, each once, in that list's order.</param>
/// <param name="TokenHash">The hash of the token (<see cref="CredentialToken.Hash"/>).</param>
/// <param name="CreatedUtc">When it was created.</param>
public sealed record NewCredential(Guid Id, string Name, string Kind, IReadOnlyList<string> Scopes, string TokenHash, DateTimeOffset CreatedUtc);

/// <summary>A stored credential, as verification sees it.</summary>
public sealed record StoredCredential(
    Guid Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Scopes,
    string TokenHash,
    DateTimeOffset? LastUsedUtc,
    DateTimeOffset? RevokedUtc);

/// <summary>Where credentials are kept.</summary>
public interface ICredentialStore
{
    Task CreateAsync(NewCredential credential, CancellationToken cancellationToken);

    /// <summary>The credential whose token hash is <paramref name="tokenHash"/>, revoked or not, or null.</summary>
    Task<StoredCredential?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Records that the credential was used at <paramref name="lastUsedUtc"/>.</summary>
    Task TouchAsync(Guid id, DateTimeOffset lastUsedUtc, CancellationToken cancellationToken);
}
