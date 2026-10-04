using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>Where the database file is and how every connection to it is set up.</summary>
public static class SqliteDatabase
{
    public const string FileName = "n8tracks.db";

    /// <summary>How long a connection waits on a lock held by another before it gives up.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    /// <summary>The database file under the data path.</summary>
    public static string FilePath(string dataPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataPath);

        return Path.Combine(dataPath, FileName);
    }

    /// <summary>The connection string: the file is created when missing, and foreign keys are enforced.</summary>
    public static string ConnectionString(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>The one way the context is configured, for the app and for the design-time tools alike.</summary>
    public static DbContextOptionsBuilder UseN8TracksSqlite(this DbContextOptionsBuilder options, string filePath)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options
            .UseSqlite(ConnectionString(filePath))

            // EF Core logs a failed command or connection at Error and then throws. The exception
            // reaches a caller that logs it once (database startup, or the request's exception
            // handler), so EF's own line would be a second Error for the same failure.
            .ConfigureWarnings(static warnings => warnings.Log(
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug)))
            .AddInterceptors(ConnectionSettingsInterceptor.Instance);
    }
}
