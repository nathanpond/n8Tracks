using System.Data.Common;
using Microsoft.Data.Sqlite;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class SqliteConnectionFactory(N8TracksOptions options) : IDatabaseConnectionFactory
{
    private readonly string connectionString = SqliteDatabase.ExistingDatabaseConnectionString(SqliteDatabase.FilePath(options.DataPath));

    public DbConnection CreateForExistingDatabase() => new SqliteConnection(connectionString);
}
