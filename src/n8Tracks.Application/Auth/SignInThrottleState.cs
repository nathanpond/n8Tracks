namespace n8Tracks.Application.Auth;

/// <summary>
/// The instance-wide sign-in throttle (there is one account). After <see cref="MaximumFailures"/>
/// failed attempts within <see cref="Window"/>, attempts are refused for <see cref="LockDuration"/>
/// from the last of them. Attempts made while refused are not checked and do not extend the
/// refusal. A successful sign-in clears it all.
/// </summary>
/// <param name="FailuresUtc">The failed attempts still inside the window, oldest first.</param>
/// <param name="LockedUntilUtc">Until when attempts are refused, or null.</param>
public sealed record SignInThrottleState(IReadOnlyList<DateTimeOffset> FailuresUtc, DateTimeOffset? LockedUntilUtc)
{
    public const int MaximumFailures = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public static SignInThrottleState Empty { get; } = new([], null);

    /// <summary>Whether there is nothing to remember: no failure and no refusal.</summary>
    public bool IsClear => FailuresUtc.Count == 0 && LockedUntilUtc is null;

    /// <summary>Until when an attempt made at <paramref name="nowUtc"/> is refused, or null when it may go ahead.</summary>
    public DateTimeOffset? RefusedUntil(DateTimeOffset nowUtc) =>
        LockedUntilUtc is { } until && nowUtc < until ? until : null;

    /// <summary>
    /// The state after a failed attempt at <paramref name="nowUtc"/>: failures outside the window are
    /// forgotten, and the one that reaches <see cref="MaximumFailures"/> starts the refusal and
    /// clears the count, so the next window starts afresh once the refusal is over.
    /// </summary>
    public SignInThrottleState WithFailure(DateTimeOffset nowUtc)
    {
        var failures = FailuresUtc.Where(failure => failure > nowUtc - Window).Append(nowUtc).ToList();

        return failures.Count >= MaximumFailures
            ? new SignInThrottleState([], nowUtc + LockDuration)
            : new SignInThrottleState(failures, null);
    }
}
