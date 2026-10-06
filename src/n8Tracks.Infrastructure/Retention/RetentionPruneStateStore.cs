using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Infrastructure.Retention;

/// <summary>The retention prune's record in the <c>settings</c> row <see cref="Key"/>: <c>{"armedUtc", "lastStartedUtc", "lastFinishedUtc"}</c>.</summary>
internal sealed class RetentionPruneStateStore(N8TracksDbContext context) : IRetentionPruneStateStore
{
    public const string Key = "retention.prune";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<RetentionPruneState?> FindAsync(CancellationToken cancellationToken)
    {
        var text = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == Key)
            .Select(static setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<StateValue>(text, Json);
        if (value?.ArmedUtc is not { } armed)
        {
            throw new InvalidOperationException($"The settings row {Key} cannot be read.");
        }

        return new RetentionPruneState(
            UtcText.Parse(armed),
            value.LastStartedUtc is { } started ? UtcText.Parse(started) : null,
            value.LastFinishedUtc is { } finished ? UtcText.Parse(finished) : null);
    }

    public Task WriteAsync(RetentionPruneState state, CancellationToken cancellationToken)
    {
        var value = Serialize(state);
        return context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            cancellationToken);
    }

    public async Task<bool> TryAddAsync(RetentionPruneState state, CancellationToken cancellationToken)
    {
        var value = Serialize(state);
        return await context.Database
            .ExecuteSqlInterpolatedAsync($"INSERT INTO settings (key, value) VALUES ({Key}, {value}) ON CONFLICT (key) DO NOTHING;", cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    private static string Serialize(RetentionPruneState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return JsonSerializer.Serialize(
            new StateValue(
                UtcText.From(state.ArmedUtc),
                state.LastStartedUtc is { } started ? UtcText.From(started) : null,
                state.LastFinishedUtc is { } finished ? UtcText.From(finished) : null),
            Json);
    }

    private sealed record StateValue(string? ArmedUtc, string? LastStartedUtc, string? LastFinishedUtc);
}
