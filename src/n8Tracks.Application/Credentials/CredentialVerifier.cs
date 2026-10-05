namespace n8Tracks.Application.Credentials;

/// <summary>A credential whose token a request presented, valid now.</summary>
/// <param name="Id">The credential's ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its kind; descriptive only.</param>
/// <param name="Scopes">What it may do.</param>
public sealed record VerifiedCredential(Guid Id, string Name, string Kind, IReadOnlyList<string> Scopes);

/// <summary>
/// Finds the credential a token belongs to. A token is looked up by its hash, and the stored hash is
/// compared with the computed one in constant time. A revoked credential is treated like an unknown
/// one. The last-used time is written at most once every <see cref="TouchInterval"/>, on any request
/// that presents a valid token, whatever that request is then allowed to do.
/// </summary>
public sealed class CredentialVerifier(ICredentialStore credentials, TimeProvider time)
{
    /// <summary>The last-used time is written at most this often, so most requests write nothing.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>The valid credential <paramref name="token"/> belongs to, or null for a malformed, unknown, or revoked one.</summary>
    public async Task<VerifiedCredential?> VerifyAsync(string? token, CancellationToken cancellationToken)
    {
        if (!CredentialToken.IsWellFormed(token))
        {
            return null;
        }

        var hash = CredentialToken.Hash(token!);
        var stored = await credentials.FindByTokenHashAsync(hash, cancellationToken).ConfigureAwait(false);
        if (stored is null || !CredentialToken.HashesEqual(stored.TokenHash, hash) || stored.RevokedUtc is not null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (stored.LastUsedUtc is not { } lastUsed || now - lastUsed >= TouchInterval)
        {
            await credentials.TouchAsync(stored.Id, now, cancellationToken).ConfigureAwait(false);
        }

        return new VerifiedCredential(stored.Id, stored.Name, stored.Kind, stored.Scopes);
    }
}
