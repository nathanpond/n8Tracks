using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Auth;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Keeps the time of the last password reset from the container as one row of <c>settings</c> (key
/// <see cref="Key"/>), its value a JSON string holding the time and nothing else. The reset command
/// writes it; the running server reads it.
/// </summary>
internal sealed class PasswordResetRecordStore(N8TracksDbContext context) : IPasswordResetRecord
{
    public const string Key = "account.lastPasswordReset";

    public async Task<DateTimeOffset?> GetLastAsync(CancellationToken cancellationToken)
    {
        var value = await context.Settings.AsNoTracking()
            .Where(setting => setting.Key == Key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (value is null)
        {
            return null;
        }

        // A row that cannot be read (written by hand, or by a later version) counts as no reset: it
        // only decides whether a log line is written.
        try
        {
            return JsonSerializer.Deserialize<string>(value) is { } text ? UtcText.Parse(text) : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    public async Task SetLastAsync(DateTimeOffset resetUtc, CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(UtcText.From(resetUtc));

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
}
