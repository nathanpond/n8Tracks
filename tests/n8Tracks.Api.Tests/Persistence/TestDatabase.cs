using Microsoft.Data.Sqlite;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Persistence;

/// <summary>Reads and prepares a database file directly, outside the app, on an unpooled connection.</summary>
internal static class TestDatabase
{
    public static string FilePath(string dataPath) => Path.Combine(dataPath, "n8tracks.db");

    /// <summary>A host whose data path is <paramref name="dataPath"/>, so several hosts can share one database.</summary>
    public static N8TracksApiFactory Host(string dataPath) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["N8TRACKS_DATA_PATH"] = dataPath });

    public static void Execute(string dataPath, string sql)
    {
        using var connection = Open(dataPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every row of a query, each as its columns joined with <c>|</c>.</summary>
    public static List<string> Rows(string dataPath, string sql)
    {
        using var connection = Open(dataPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)));
        }

        return rows;
    }

    public static string Scalar(string dataPath, string sql) => Assert.Single(Rows(dataPath, sql));

    public static List<string> History(string dataPath) =>
        Rows(dataPath, "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\";");

    public static string SchemaInitializedUtc(string dataPath) =>
        Scalar(dataPath, $"SELECT value FROM app_metadata WHERE key = '{AppMetadataEntry.SchemaInitializedUtcKey}';");

    private static SqliteConnection Open(string dataPath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = FilePath(dataPath), Pooling = false }.ToString());
        connection.Open();

        return connection;
    }
}
