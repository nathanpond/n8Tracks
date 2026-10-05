namespace n8Tracks.Api.Auth;

/// <summary>
/// Remembers which password reset from the container this process has logged, so the server writes
/// the line once per reset (and once more after a restart) instead of at every sign-in.
/// </summary>
internal sealed class PasswordResetNotice
{
    private readonly Lock gate = new();
    private DateTimeOffset? reported;

    /// <summary>True the first time this process is shown a reset at <paramref name="resetUtc"/>.</summary>
    public bool TryClaim(DateTimeOffset resetUtc)
    {
        lock (gate)
        {
            if (reported == resetUtc)
            {
                return false;
            }

            reported = resetUtc;
            return true;
        }
    }
}
