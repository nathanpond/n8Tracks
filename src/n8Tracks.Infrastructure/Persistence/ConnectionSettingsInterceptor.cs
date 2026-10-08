using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Applies the per-connection settings each time a connection opens: a busy timeout, so a writer
/// waits for a lock instead of failing at once, and <c>synchronous=NORMAL</c>, which is safe under
/// WAL. Neither is stored in the database file, so both are set on every connection. It also
/// registers <see cref="TitleKeyFunction"/>, so a migration can fill a stored key with the same
/// rule the application writes it by, and <see cref="LowerFunction"/>, so a query can compare text
/// without regard to case beyond ASCII.
/// </summary>
internal sealed class ConnectionSettingsInterceptor : DbConnectionInterceptor
{
    public static readonly ConnectionSettingsInterceptor Instance = new();

    private static readonly string Settings = string.Create(
        CultureInfo.InvariantCulture,
        $"PRAGMA busy_timeout = {SqliteDatabase.BusyTimeoutMilliseconds}; PRAGMA synchronous = NORMAL;");

    /// <summary>
    /// The SQL function <c>n8_title_key(title)</c>: <see cref="SongRules.TitleKey"/>, which SQLite's
    /// own functions cannot compute (its <c>upper()</c> folds ASCII only, and it has no NFC).
    /// </summary>
    public const string TitleKeyFunction = "n8_title_key";

    /// <summary>
    /// The SQL function <c>n8_lower(text)</c>: <see cref="string.ToLowerInvariant"/>, every letter that
    /// has a lower case (SQLite's own <c>lower()</c> and <c>LIKE</c> fold ASCII only, #389). Mapped in
    /// the model as <see cref="N8TracksDbContext.Lower"/>.
    /// </summary>
    public const string LowerFunction = "n8_lower";

    private ConnectionSettingsInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        AddFunctions(connection);

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

        AddFunctions(connection);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = Settings;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddFunctions(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
        {
            sqlite.CreateFunction<string?, string?>(
                TitleKeyFunction,
                static title => title is null ? null : SongRules.TitleKey(title),
                isDeterministic: true);
            sqlite.CreateFunction<string?, string?>(
                LowerFunction,
                static text => text?.ToLowerInvariant(),
                isDeterministic: true);
        }
    }
}
