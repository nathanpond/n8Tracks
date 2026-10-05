using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Auth;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Keeps the sign-in throttle as one row of <c>settings</c> (key <see cref="Key"/>), so the running
/// app, a restart, and the container's reset command all see the same state, and deleting the row
/// clears it.
/// </summary>
internal sealed class SignInThrottleStore(N8TracksDbContext context) : ISignInThrottleStore
{
    public const string Key = "signIn.throttle";

    public async Task<SignInThrottleState> GetAsync(CancellationToken cancellationToken)
    {
        var value = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == Key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (value is null)
        {
            return SignInThrottleState.Empty;
        }

        // A row that cannot be read (written by hand, or by a later version) counts as no failures:
        // the throttle slows guessing down, and a broken row must not lock the owner out for good.
        try
        {
            var stored = JsonSerializer.Deserialize<StoredThrottle>(value);
            return stored is null
                ? SignInThrottleState.Empty
                : new SignInThrottleState(
                    [.. (stored.FailuresUtc ?? []).Select(UtcText.Parse)],
                    stored.LockedUntilUtc is { } until ? UtcText.Parse(until) : null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return SignInThrottleState.Empty;
        }
    }

    public async Task SaveAsync(SignInThrottleState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.IsClear)
        {
            await context.Settings.Where(setting => setting.Key == Key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var value = JsonSerializer.Serialize(new StoredThrottle(
            [.. state.FailuresUtc.Select(UtcText.From)],
            state.LockedUntilUtc is { } until ? UtcText.From(until) : null));

        var updated = await context.Settings
            .Where(setting => setting.Key == Key)
            .ExecuteUpdateAsync(setters => setters.SetProperty(setting => setting.Value, value), cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            context.Settings.Add(new SettingRecord { Key = Key, Value = value });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record StoredThrottle(string[]? FailuresUtc, string? LockedUntilUtc);
}
