using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Songs;

namespace n8Tracks.Infrastructure.Persistence;

internal sealed class VersionStore(N8TracksDbContext context) : IVersionStore
{
    public async Task<VersionNumberingFacts?> FindNumberingAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await context.Versions.AsNoTracking()
            .Where(version => version.Id == id)
            .Select(static version => new { version.SongId, version.Number })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        // The used-numbers table is the one source of "used": it keeps the numbers of Versions since removed.
        var used = await context.UsedVersionNumbers.AsNoTracking()
            .Where(number => number.SongId == source.SongId)
            .Select(static number => number.Number)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new VersionNumberingFacts(source.SongId, source.Number, used);
    }
}
