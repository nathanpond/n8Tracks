using n8Tracks.Application.Auth;
using n8Tracks.Application.Generations;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Application.Media;

/// <summary>
/// Where the preferred audio files are kept (#212): one per Generation and one per Song at most, each a
/// file associated with its owner (the database refuses any other). Every write runs inside the caller's
/// exclusive transaction.
/// </summary>
public interface IPreferredAudioFileStore
{
    /// <summary>
    /// One conditional write: while the Generation <paramref name="generationId"/> is at
    /// <paramref name="revision"/>, raises its revision and makes <paramref name="audioFileId"/> its
    /// preferred file (null: none), touching no other column of it. False when it is gone or at another
    /// revision (nothing is written).
    /// </summary>
    Task<bool> TrySetForGenerationAsync(Guid generationId, Guid? audioFileId, int revision, CancellationToken cancellationToken);

    /// <summary>
    /// One conditional write: while the Song <paramref name="songId"/> is at <paramref name="revision"/>,
    /// raises its revision, sets its updated time, and makes <paramref name="audioFileId"/> its preferred
    /// Song-level file (null: none). False when it is gone or at another revision (nothing is written).
    /// </summary>
    Task<bool> TrySetForSongAsync(Guid songId, Guid? audioFileId, int revision, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Clears any choice of the file <paramref name="audioFileId"/>, raising the revision of the
    /// Generation or Song that had chosen it (and a Song's updated time). Returns how many were cleared.
    /// </summary>
    Task<int> ClearForFileAsync(Guid audioFileId, DateTimeOffset updatedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Inside a deleting transaction: removes the choices of the Generations <paramref name="generationIds"/>
    /// and of the Songs <paramref name="songIds"/> (theirs and their Generations'), with no revision
    /// raised (the owners are going). A restore does not bring them back.
    /// </summary>
    Task ReleaseAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken);
}

/// <summary>How setting or clearing a preferred file ended; <typeparamref name="TOwner"/> is the Generation or the Song. Only <see cref="Done"/> may have stored anything.</summary>
public abstract record PreferredAudioFileOutcome<TOwner>
{
    private PreferredAudioFileOutcome()
    {
    }

    /// <summary>The owner as it is now: its revision raised when its choice changed.</summary>
    public sealed record Done(TOwner Owner) : PreferredAudioFileOutcome<TOwner>;

    /// <summary>The reference names no live Generation or Song.</summary>
    public sealed record OwnerNotFound : PreferredAudioFileOutcome<TOwner>;

    /// <summary>There is no such audio file.</summary>
    public sealed record FileNotFound : PreferredAudioFileOutcome<TOwner>;

    /// <summary>The file may not be chosen for this owner: the problem code (422) and why.</summary>
    public sealed record Refused(string Code, string Detail) : PreferredAudioFileOutcome<TOwner>;

    /// <summary>The owner is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(TOwner Current) : PreferredAudioFileOutcome<TOwner>;
}

/// <summary>
/// The user's choice of which local file plays (#212): a Generation's preferred file, any file
/// associated with that Generation, and a Song's preferred Song-level file, any file associated with the
/// Song and no Generation. A file may be chosen whatever its status (Missing or Unavailable included):
/// the choice is remembered while the file is away and plays again once it is back, with no action
/// (<see cref="PlaybackResolver"/>). Each write is the owner's: checked against and raising its revision
/// (a Song's also sets its updated time), and changing nothing else: no audio file record, no file in
/// the media folder (invariant 2), no Version (invariant 1). The owner's revision is checked before the
/// file. Choosing the current choice again, or clearing when there is none, stores nothing, though a
/// stale revision is still a conflict. A choice
/// is cleared, raising its owner's revision, when its file's association changes
/// (<see cref="AudioFileAssociationService"/>), and removed with its owner on deletion
/// (<see cref="AudioFileLifecycle"/>). Changing the Song's Selected Generation changes no choice.
/// </summary>
public sealed class PreferredAudioFileService(
    IAudioFileStore files,
    IPreferredAudioFileStore preferences,
    ISongStore songs,
    GenerationService generations,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the file is sent in, and its errors are reported under.</summary>
    public const string AudioFileField = "audioFile";

    /// <summary>The problem code (422) for a Generation's choice of a file not associated with it.</summary>
    public const string NotInGenerationCode = "audio_file_not_in_generation";

    /// <summary>The problem code (422) for a Song's choice of one of its files that belongs to a Generation.</summary>
    public const string NotSongLevelCode = "audio_file_not_song_level";

    /// <summary>The problem code (422) for a Song's choice of a file not associated with it.</summary>
    public const string NotInSongCode = "audio_file_not_in_song";

    /// <summary>Makes the file <paramref name="audioFileId"/> the preferred file of the Generation <paramref name="generation"/> names (its ID or shortcode), given its revision.</summary>
    public Task<PreferredAudioFileOutcome<GenerationSummary>> SetForGenerationAsync(string generation, Guid audioFileId, int revision, CancellationToken cancellationToken) =>
        WriteForGenerationAsync(generation, audioFileId, revision, cancellationToken);

    /// <summary>Leaves the Generation <paramref name="generation"/> names with no preferred file, given its revision.</summary>
    public Task<PreferredAudioFileOutcome<GenerationSummary>> ClearForGenerationAsync(string generation, int revision, CancellationToken cancellationToken) =>
        WriteForGenerationAsync(generation, null, revision, cancellationToken);

    /// <summary>Makes the Song-level file <paramref name="audioFileId"/> the preferred file of the Song <paramref name="song"/> names (its ID or shortcode), given its revision.</summary>
    public Task<PreferredAudioFileOutcome<SongSummary>> SetForSongAsync(string song, Guid audioFileId, int revision, CancellationToken cancellationToken) =>
        WriteForSongAsync(song, audioFileId, revision, cancellationToken);

    /// <summary>Leaves the Song <paramref name="song"/> names with no preferred Song-level file, given its revision.</summary>
    public Task<PreferredAudioFileOutcome<SongSummary>> ClearForSongAsync(string song, int revision, CancellationToken cancellationToken) =>
        WriteForSongAsync(song, null, revision, cancellationToken);

    private Task<PreferredAudioFileOutcome<GenerationSummary>> WriteForGenerationAsync(string reference, Guid? audioFileId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<PreferredAudioFileOutcome<GenerationSummary>>(
            async ct =>
            {
                if (await generations.FindAsync(CatalogReference.Parse(reference), ct).ConfigureAwait(false) is not { } owner)
                {
                    return new PreferredAudioFileOutcome<GenerationSummary>.OwnerNotFound();
                }

                if (owner.Generation.Revision != revision)
                {
                    return new PreferredAudioFileOutcome<GenerationSummary>.Conflict(owner);
                }

                var id = owner.Generation.Id;
                var current = (await files.ListForSongAsync(owner.Generation.SongId, ct).ConfigureAwait(false))
                    .FirstOrDefault(file => file.Preferred && file.Link?.Generation?.Id == id)?.Id;
                if (audioFileId is { } chosen)
                {
                    if (await files.FindAsync(chosen, ct).ConfigureAwait(false) is not { } file)
                    {
                        return new PreferredAudioFileOutcome<GenerationSummary>.FileNotFound();
                    }

                    if (file.Link?.Generation?.Id != id)
                    {
                        return new PreferredAudioFileOutcome<GenerationSummary>.Refused(
                            NotInGenerationCode,
                            $"{file.FileName} is not associated with Generation {owner.Shortcode}: choose one of its own files.");
                    }
                }

                if (current == audioFileId)
                {
                    return new PreferredAudioFileOutcome<GenerationSummary>.Done(owner);
                }

                return await preferences.TrySetForGenerationAsync(id, audioFileId, revision, ct).ConfigureAwait(false)
                    ? new PreferredAudioFileOutcome<GenerationSummary>.Done(
                        await generations.FindAsync(CatalogReference.Parse(id.ToString()), ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The Generation just changed cannot be read back."))
                    : throw new InvalidOperationException("The Generation just read changed inside the transaction.");
            },
            cancellationToken);

    private Task<PreferredAudioFileOutcome<SongSummary>> WriteForSongAsync(string reference, Guid? audioFileId, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<PreferredAudioFileOutcome<SongSummary>>(
            async ct =>
            {
                if (await SongService.FindAsync(songs, reference, ct).ConfigureAwait(false) is not { } owner)
                {
                    return new PreferredAudioFileOutcome<SongSummary>.OwnerNotFound();
                }

                if (owner.Revision != revision)
                {
                    return new PreferredAudioFileOutcome<SongSummary>.Conflict(owner);
                }

                var current = (await files.ListForSongAsync(owner.Id, ct).ConfigureAwait(false))
                    .FirstOrDefault(static file => file.Preferred && file.Link is { Generation: null })?.Id;
                if (audioFileId is { } chosen)
                {
                    if (await files.FindAsync(chosen, ct).ConfigureAwait(false) is not { } file)
                    {
                        return new PreferredAudioFileOutcome<SongSummary>.FileNotFound();
                    }

                    if (file.Link?.Song.Id != owner.Id)
                    {
                        return new PreferredAudioFileOutcome<SongSummary>.Refused(
                            NotInSongCode,
                            $"{file.FileName} is not associated with this Song: choose one of its own Song-level files.");
                    }

                    if (file.Link.Generation is { } generation)
                    {
                        return new PreferredAudioFileOutcome<SongSummary>.Refused(
                            NotSongLevelCode,
                            $"{file.FileName} belongs to Generation {generation.Shortcode}: a Song's preferred file is one of its Song-level files.");
                    }
                }

                if (current == audioFileId)
                {
                    return new PreferredAudioFileOutcome<SongSummary>.Done(owner);
                }

                return await preferences.TrySetForSongAsync(owner.Id, audioFileId, revision, time.GetUtcNow(), ct).ConfigureAwait(false)
                    ? new PreferredAudioFileOutcome<SongSummary>.Done(
                        await songs.FindAsync(owner.Id, ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("The Song just changed cannot be read back."))
                    : throw new InvalidOperationException("The Song just read changed inside the transaction.");
            },
            cancellationToken);
}
