using n8Tracks.Application.Generations;

namespace n8Tracks.Application.Media;

/// <summary>
/// The playback reads (#212): what plays for a Generation, for a Song, and the marks on a Song's list,
/// each from the Song's files as they report now, through <see cref="PlaybackResolver"/>, so every part
/// of the product resolves the same Song or Generation to the same file; and the whole answer to Play
/// on a Song, with the chooser's candidates when nothing is selected (#219); and every source a Song's
/// player can switch to while comparing (#220). Reads only.
/// </summary>
public sealed class PlaybackService(AudioFileService files, IGenerationStore generations)
{
    /// <summary>What plays for the Generation <paramref name="generationId"/> of the Song <paramref name="songId"/>.</summary>
    public async Task<GenerationPlayback> ForGenerationAsync(Guid songId, Guid generationId, CancellationToken cancellationToken) =>
        PlaybackResolver.ForGeneration([.. (await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false))
            .Where(file => PlaybackResolver.GenerationIdOf(file) == generationId)]);

    /// <summary>What plays for the Song <paramref name="songId"/>, whose Selected Generation is <paramref name="selectedGenerationId"/> (null: none).</summary>
    public async Task<SongPlayback> ForSongAsync(Guid songId, Guid? selectedGenerationId, CancellationToken cancellationToken) =>
        PlaybackResolver.ForSong(await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false), selectedGenerationId);

    /// <summary>
    /// The whole answer to Play on the Song <paramref name="songId"/>, whose Selected Generation is
    /// <paramref name="selectedGenerationId"/> (null: none): <see cref="PlaybackResolver.ChoiceForSong"/>
    /// over its files and its Generations (Version tree order, then ordinal).
    /// </summary>
    public async Task<SongPlaybackChoice> ChoiceForSongAsync(Guid songId, Guid? selectedGenerationId, CancellationToken cancellationToken)
    {
        var songFiles = await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false);
        return PlaybackResolver.ChoiceForSong(songFiles, await GenerationsOfAsync(songId, cancellationToken).ConfigureAwait(false), selectedGenerationId);
    }

    /// <summary>
    /// Everything of the Song <paramref name="songId"/> the player can switch to while comparing
    /// (#220): <see cref="PlaybackResolver.SourcesForSong"/> over its files and its Generations
    /// (Version tree order, then ordinal).
    /// </summary>
    public async Task<SongPlaybackSources> SourcesForSongAsync(Guid songId, CancellationToken cancellationToken)
    {
        var songFiles = await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false);
        return PlaybackResolver.SourcesForSong(songFiles, await GenerationsOfAsync(songId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<SongPlaybackGeneration>> GenerationsOfAsync(Guid songId, CancellationToken cancellationToken) =>
        [.. (await generations.ForSongAsync(songId, cancellationToken).ConfigureAwait(false)).Select(static summary => new SongPlaybackGeneration(
            summary.Generation.Id,
            summary.Shortcode,
            summary.VersionNumber,
            summary.Generation.Rating,
            summary.Generation.Clip?.DurationSeconds,
            summary.Generation.State,
            summary.Generation.RemoteState,
            summary.Generation.SunoId)
        {
            Revision = summary.Generation.Revision,
        })];

    /// <summary>
    /// The Song's files (<see cref="AudioFileService.ListForSongAsync"/>), each marked with whether it
    /// plays now for its Generation and for the Song.
    /// </summary>
    public async Task<IReadOnlyList<ReportedAudioFile>> ListForSongAsync(Guid songId, Guid? selectedGenerationId, CancellationToken cancellationToken) =>
        PlaybackResolver.Mark(await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false), selectedGenerationId);
}
