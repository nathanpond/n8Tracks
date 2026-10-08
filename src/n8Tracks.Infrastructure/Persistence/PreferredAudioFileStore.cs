using Microsoft.EntityFrameworkCore;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// The preferred audio files (#212) in <c>generation_preferred_audio_files</c> and
/// <c>song_preferred_audio_files</c>. A choice is the owner's: setting or clearing one raises the
/// Generation's or Song's revision in the same transaction (a Song's updated time too), and touches no
/// other column of it. The composite foreign keys refuse a choice of a file that is not the owner's.
/// </summary>
internal sealed class PreferredAudioFileStore(N8TracksDbContext context) : IPreferredAudioFileStore
{
    public async Task<bool> TrySetForGenerationAsync(Guid generationId, Guid? audioFileId, int revision, CancellationToken cancellationToken)
    {
        // The revision check and the raise are one conditional statement, so nothing slips between them.
        if (await context.Generations
                .Where(generation => generation.Id == generationId && generation.Revision == revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.Revision, static generation => generation.Revision + 1), cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            return false;
        }

        await context.GenerationPreferredAudioFiles.Where(row => row.GenerationId == generationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (audioFileId is { } fileId)
        {
            await AddAsync(new GenerationPreferredAudioFileRecord { GenerationId = generationId, AudioFileId = fileId }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> TrySetForSongAsync(Guid songId, Guid? audioFileId, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        if (await RaiseSongsAsync(song => song.Id == songId && song.Revision == revision, updatedUtc, cancellationToken).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await context.SongPreferredAudioFiles.Where(row => row.SongId == songId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (audioFileId is { } fileId)
        {
            await AddAsync(new SongPreferredAudioFileRecord { SongId = songId, AudioFileId = fileId }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<int> ClearForFileAsync(Guid audioFileId, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var generationIds = await context.GenerationPreferredAudioFiles.AsNoTracking()
            .Where(row => row.AudioFileId == audioFileId)
            .Select(static row => row.GenerationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var songIds = await context.SongPreferredAudioFiles.AsNoTracking()
            .Where(row => row.AudioFileId == audioFileId)
            .Select(static row => row.SongId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cleared = await context.GenerationPreferredAudioFiles.Where(row => row.AudioFileId == audioFileId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false)
            + await context.SongPreferredAudioFiles.Where(row => row.AudioFileId == audioFileId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (generationIds.Count > 0)
        {
            await context.Generations
                .Where(generation => generationIds.Contains(generation.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(static generation => generation.Revision, static generation => generation.Revision + 1), cancellationToken)
                .ConfigureAwait(false);
        }

        if (songIds.Count > 0)
        {
            await RaiseSongsAsync(song => songIds.Contains(song.Id), updatedUtc, cancellationToken).ConfigureAwait(false);
        }

        return cleared;
    }

    public async Task ReleaseAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        ArgumentNullException.ThrowIfNull(songIds);

        var generations = generationIds.ToList();
        var songs = songIds.ToList();
        var ofSongs = context.Generations.Where(generation => songs.Contains(generation.SongId)).Select(static generation => generation.Id);
        await context.GenerationPreferredAudioFiles
            .Where(row => generations.Contains(row.GenerationId) || ofSongs.Contains(row.GenerationId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await context.SongPreferredAudioFiles
            .Where(row => songs.Contains(row.SongId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<int> RaiseSongsAsync(System.Linq.Expressions.Expression<Func<SongRecord, bool>> which, DateTimeOffset updatedUtc, CancellationToken cancellationToken)
    {
        var updated = UtcText.From(updatedUtc);
        return context.Songs
            .Where(which)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(static song => song.UpdatedUtc, updated)
                    .SetProperty(static song => song.Revision, static song => song.Revision + 1),
                cancellationToken);
    }

    /// <summary>Inserts one row and stops tracking it, so a later read of the same key sees the table.</summary>
    private async Task AddAsync<TRecord>(TRecord record, CancellationToken cancellationToken)
        where TRecord : class
    {
        context.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(record).State = EntityState.Detached;
    }
}
