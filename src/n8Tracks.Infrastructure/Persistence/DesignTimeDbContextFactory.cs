using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tools (for example <c>dotnet ef migrations add</c>), which need a
/// context without starting the app. The path is relative to the directory the tool runs in and is
/// git-ignored; adding a migration never opens the file.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<N8TracksDbContext>
{
    public N8TracksDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<N8TracksDbContext>();
        options.UseN8TracksSqlite(Path.Combine(".localdata", SqliteDatabase.FileName));

        return new N8TracksDbContext(options.Options);
    }
}
