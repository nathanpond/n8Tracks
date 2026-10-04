using System.Data.Common;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Makes connections to the database outside Entity Framework Core. The seam the health check goes
/// through, so a test host can make the database unreachable.
/// </summary>
internal interface IDatabaseConnectionFactory
{
    /// <summary>
    /// A closed connection of its own (not pooled) to the database file, read-write. Opening it fails
    /// when the file is missing: it never creates one.
    /// </summary>
    DbConnection CreateForExistingDatabase();
}
