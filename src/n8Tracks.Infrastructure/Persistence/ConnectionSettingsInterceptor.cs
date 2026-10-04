using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Applies the per-connection settings each time a connection opens: a busy timeout, so a writer
/// waits for a lock instead of failing at once, and <c>synchronous=NORMAL</c>, which is safe under
/// WAL. Neither is stored in the database file, so both are set on every connection.
/// </summary>
internal sealed class ConnectionSettingsInterceptor : DbConnectionInterceptor
{
    public static readonly ConnectionSettingsInterceptor Instance = new();

    private static readonly string Settings = string.Create(
        CultureInfo.InvariantCulture,
        $"PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMilliseconds}; PRAGMA synchronous = NORMAL;");

    private ConnectionSettingsInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = Settings;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Settings;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
