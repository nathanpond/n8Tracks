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

    /// <summary>
    /// The connection string of the context: read-write, with foreign keys enforced, and never creating
    /// the file. Only database startup makes a new database (<see cref="CreateIfMissing"/>): a file
    /// deleted while the app runs stays missing, so the health check reports it, instead of the next
    /// background poll or request putting an empty file in its place.
    /// </summary>
    public static string ConnectionString(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>Creates an empty database file when there is none; a file already there is left as it is.</summary>
    public static void CreateIfMissing(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        if (File.Exists(filePath))
        {
            return;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
    }

    /// <summary>
    /// The connection string of a connection that must find the file already there: read-write,
    /// never creating it, and outside the connection pool, so that a pooled handle on a file that
    /// has since been deleted cannot answer for it.
    /// </summary>
    public static string ExistingDatabaseConnectionString(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        return new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
    }

    /// <summary>The one way the context is configured, for the app and for the design-time tools alike.</summary>
    public static DbContextOptionsBuilder UseN8TracksSqlite(this DbContextOptionsBuilder options, string filePath)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options
            .UseSqlite(ConnectionString(filePath))

            // EF Core logs a failed command, connection, or save at Error and then throws. The
            // exception reaches a caller that either handles it as an expected outcome (the loser of
            // the race to create the administrator) or logs it once (database startup, or the
            // request's exception handler), so EF's own line would be a false or a second Error.
            .ConfigureWarnings(static warnings => warnings.Log(
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug),
                (CoreEventId.SaveChangesFailed, LogLevel.Debug)))
            .AddInterceptors(ConnectionSettingsInterceptor.Instance);
    }
}
