using n8Tracks.Application.Retention;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// The local audio files a deletion would leave unassociated (#213), as its confirmation counts them.
/// Missing files are counted.
/// </summary>
/// <param name="Total">Every file associated with what is deleted.</param>
/// <param name="HandAssociated">
/// Those associated by the user that no scan would associate again after a restore: their names carry
/// no Suno ID, or the user turned automatic matching off for them (#210).
/// </param>
/// <param name="SongLevel">Those associated with the Song itself rather than one of its Generations; a Song deletion only.</param>
public sealed record LocalAudioFileCounts(int Total, int HandAssociated, int SongLevel)
{
    /// <summary>No files.</summary>
    public static LocalAudioFileCounts None { get; } = new(0, 0, 0);
}

/// <summary>
/// What happens to audio file associations when the catalog records they name are deleted, moved, or
/// restored (#206, #213). Associations are not kept in the retention store: a deletion removes them
/// inside its own transaction, before retention captures the rows (the database refuses to remove a
/// Song or Generation a file still names), and the next scan associates again any file whose name
/// carries a Suno ID that is live again. What a deletion released is remembered with its group
/// (#388), and a restore of that group clears those files' reason ("its Song was deleted", "its
/// Generation was deleted"), so a file the scan does not match again is plainly unmatched. A move needs nothing here: the files of a Generation follow it
/// to its new Song through the database's composite key (its update cascades), and its Preferred Audio
/// File choice is keyed by the Generation (#212). The files themselves are never touched (invariant 2).
/// </summary>
public sealed class AudioFileLifecycle(IAudioFileStore files, IPreferredAudioFileStore preferences) : IRetentionRestoreParticipant
{
    /// <summary>
    /// What the <c>restore-deleted</c> command says after restoring a Song, Version, or Generation. It is
    /// general, because associations are not retained and so cannot be counted.
    /// </summary>
    public const string RestoreNote =
        "Local audio file associations are not restored. The next media scan associates again each file whose name carries the Suno ID of a restored Generation; files associated by hand without a Suno ID in their name, and Song-level files, must be associated again in Unmatched Files. No Preferred Audio File choice is restored.";

    /// <summary>
    /// Inside a restore's transaction: <see cref="RestoreNote"/> when the restore put back a Song or a
    /// Generation (<paramref name="restoredSongOrGeneration"/>) and the library holds any audio file;
    /// null otherwise, so a restore in a library without local files says nothing about them.
    /// </summary>
    internal async Task<string?> RestoreNoteAsync(bool restoredSongOrGeneration, CancellationToken cancellationToken) =>
        restoredSongOrGeneration && (await files.CountsAsync(cancellationToken).ConfigureAwait(false)).Total > 0
            ? RestoreNote
            : null;

    /// <summary>
    /// The files of the Song <paramref name="songId"/> that deleting it (<paramref name="wholeSong"/>) or
    /// its Generations <paramref name="generationIds"/> would leave unassociated.
    /// </summary>
    internal async Task<LocalAudioFileCounts> CountAsync(
        Guid songId,
        IReadOnlyCollection<Guid> generationIds,
        bool wholeSong,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);

        if (!wholeSong && generationIds.Count == 0)
        {
            return LocalAudioFileCounts.None;
        }

        var affected = (await files.ListForSongAsync(songId, cancellationToken).ConfigureAwait(false))
            .Where(file => wholeSong || (file.Link?.Generation is { } generation && generationIds.Contains(generation.Id)))
            .ToList();
        return affected.Count == 0
            ? LocalAudioFileCounts.None
            : new LocalAudioFileCounts(
                affected.Count,
                affected.Count(static file => IsHandAssociated(file)),
                affected.Count(static file => file.Link is { Generation: null }));
    }

    /// <summary>
    /// Inside the deleting transaction: the files of the Generations <paramref name="generationIds"/>
    /// become unmatched with the reason "its Generation was deleted", and every file of the Songs
    /// <paramref name="songIds"/> (Song-level ones included) with "its Song was deleted". The preferred
    /// file choices of those Generations and Songs go first (#212); they are not retained, so a restore
    /// does not bring them back. The caller hands what was released to <see cref="RememberAsync"/> once
    /// its retention group exists.
    /// </summary>
    internal async Task<IReadOnlyList<ReleasedAudioFile>> ReleaseAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        ArgumentNullException.ThrowIfNull(songIds);

        if (generationIds.Count == 0 && songIds.Count == 0)
        {
            return [];
        }

        await preferences.ReleaseAsync(generationIds, songIds, cancellationToken).ConfigureAwait(false);
        return await files.UnassociateAsync(generationIds, songIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inside the deleting transaction: keeps <paramref name="released"/> with the deletion's retention group <paramref name="groupId"/> (#388).</summary>
    internal Task RememberAsync(Guid groupId, IReadOnlyCollection<ReleasedAudioFile> released, CancellationToken cancellationToken) =>
        files.RememberReleaseAsync(groupId, released, cancellationToken);

    /// <summary>
    /// Inside a restore's transaction (#388): the files the group's deletion released lose the reason
    /// it gave them, where they still have it and are still unassociated. A file associated since, one
    /// the user unassociated, and one released by another deletion are left as they are; the automatic
    /// match block (#210) is never touched. The next scan matches by Suno ID as always.
    /// </summary>
    public Task RestoringAsync(Guid groupId, CancellationToken cancellationToken) =>
        files.ClearReleaseReasonsAsync(groupId, cancellationToken);

    /// <summary>Whether the user associated <paramref name="file"/> and no scan would associate it again by itself.</summary>
    private static bool IsHandAssociated(AudioFile file) =>
        file.Link is { Origin: AssociationOrigin.User }
        && (file.AutoMatchBlocked || SunoIdMatcher.FindIds(file.FileName).Count == 0);
}
