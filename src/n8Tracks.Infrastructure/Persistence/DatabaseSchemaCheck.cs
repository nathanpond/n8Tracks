using n8Tracks.Application.Configuration;
using n8Tracks.Application.Persistence;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class DatabaseSchemaCheck(N8TracksOptions options, N8TracksDbContext context) : IDatabaseSchemaCheck
{
    public string DatabaseFile { get; } = SqliteDatabase.FilePath(options.DataPath);

    public async Task<DatabaseCondition> CheckWithoutChangingAsync(CancellationToken cancellationToken)
    {
        // Checked first: opening the context's connection would create a missing file.
        if (!File.Exists(DatabaseFile))
        {
            return DatabaseCondition.Missing;
        }

        return await DatabaseStartup.CheckWithoutChangingAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
