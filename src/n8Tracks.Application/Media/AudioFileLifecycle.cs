namespace n8Tracks.Application.Media;

/// <summary>
/// What happens to audio file associations when the catalog records they name go away. Associations are
/// not kept in the retention store: a deletion removes them inside its own transaction, before
/// retention captures the rows (the database refuses to remove a Song or Generation a file still
/// names), and the next scan associates again any file whose name carries a Suno ID that is live again.
/// The files themselves are never touched (invariant 2). #206 brings the minimum every deletion needs;
/// #213 adds the counts, moves, and restore notes.
/// </summary>
public sealed class AudioFileLifecycle(IAudioFileStore files)
{
    /// <summary>
    /// Inside the deleting transaction: the files of the Generations <paramref name="generationIds"/>
    /// become unmatched with the reason "its Generation was deleted", and every file of the Songs
    /// <paramref name="songIds"/> (Song-level ones included) with "its Song was deleted".
    /// </summary>
    internal Task<int> ReleaseAsync(IReadOnlyCollection<Guid> generationIds, IReadOnlyCollection<Guid> songIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generationIds);
        ArgumentNullException.ThrowIfNull(songIds);

        return generationIds.Count == 0 && songIds.Count == 0
            ? Task.FromResult(0)
            : files.UnassociateAsync(generationIds, songIds, cancellationToken);
    }
}
