using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>How choosing or clearing a Song's Selected Generation ended. Only <see cref="Selected"/> may have stored anything.</summary>
public abstract record GenerationSelectionOutcome
{
    private GenerationSelectionOutcome()
    {
    }

    /// <summary>The Song as it is now: its revision raised when its selection changed, as it was otherwise.</summary>
    public sealed record Selected(SongSummary Song) : GenerationSelectionOutcome;

    /// <summary>The reference names no live Song.</summary>
    public sealed record SongNotFound : GenerationSelectionOutcome;

    /// <summary>The Generation named names no live Generation.</summary>
    public sealed record GenerationNotFound : GenerationSelectionOutcome;

    /// <summary>The Generation named is another Song's.</summary>
    public sealed record NotInSong(GenerationSummary Generation) : GenerationSelectionOutcome;

    /// <summary>No Generation was named; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationSelectionOutcome;

    /// <summary>The Song is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(SongSummary Current) : GenerationSelectionOutcome;
}

/// <summary>
/// A Song's Selected Generation (#120): the one Generation, of any of its Versions, the user marks as
/// the Song's chosen output. A Song has at most one; selecting another replaces it, and clearing
/// leaves it with none. The selection is the Song's: choosing, replacing, or clearing it raises the
/// Song's revision (checked against the one sent) and sets its updated time, and changes nothing
/// else: no Generation (not even the chosen one's revision or state), no Version, and no other field
/// of the Song. Any Generation of the Song may be chosen, whatever its state or Suno's (an Archived,
/// trashed, or missing one included); archiving it later leaves it selected. Whatever deletes or moves
/// a Generation resolves the selection first (Version and Song deletion retain it with the group).
/// </summary>
public sealed class GenerationSelectionService(
    ISongStore songs,
    GenerationService generations,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the chosen Generation is sent in, and its errors are reported under.</summary>
    public const string GenerationField = "generation";

    /// <summary>The problem code (422) for a Generation of another Song.</summary>
    public const string NotInSongCode = "generation_not_in_song";

    /// <summary>
    /// Makes the Generation <paramref name="generation"/> names (its ID or shortcode) the Selected
    /// Generation of the Song <paramref name="song"/> names, given the Song's revision. Choosing the
    /// Generation already selected stores nothing, but a stale revision is still a conflict.
    /// </summary>
    public Task<GenerationSelectionOutcome> SelectAsync(CatalogReference song, string? generation, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationSelectionOutcome>(
            async ct =>
            {
                if (await FindSongAsync(song, ct).ConfigureAwait(false) is not { } current)
                {
                    return new GenerationSelectionOutcome.SongNotFound();
                }

                if (string.IsNullOrWhiteSpace(generation))
                {
                    return new GenerationSelectionOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [GenerationField] = ["Name the Generation by its ID or shortcode."],
                    });
                }

                if (await generations.FindAsync(CatalogReference.Parse(generation.Trim()), ct).ConfigureAwait(false) is not { } chosen)
                {
                    return new GenerationSelectionOutcome.GenerationNotFound();
                }

                if (chosen.Generation.SongId != current.Id)
                {
                    return new GenerationSelectionOutcome.NotInSong(chosen);
                }

                return await WriteAsync(current, chosen.Generation.Id, revision, ct).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Leaves the Song <paramref name="song"/> names with no Selected Generation, given its revision.
    /// Clearing a Song with none stores nothing, but a stale revision is still a conflict.
    /// </summary>
    public Task<GenerationSelectionOutcome> ClearAsync(CatalogReference song, int revision, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationSelectionOutcome>(
            async ct => await FindSongAsync(song, ct).ConfigureAwait(false) is { } current
                ? await WriteAsync(current, null, revision, ct).ConfigureAwait(false)
                : new GenerationSelectionOutcome.SongNotFound(),
            cancellationToken);

    /// <summary>Inside the transaction: the revision check, then the write when the selection changes.</summary>
    private async Task<GenerationSelectionOutcome> WriteAsync(SongSummary current, Guid? generationId, int revision, CancellationToken cancellationToken)
    {
        if (current.Revision != revision)
        {
            return new GenerationSelectionOutcome.Conflict(current);
        }

        if (current.SelectedGeneration?.Id == generationId)
        {
            return new GenerationSelectionOutcome.Selected(current);
        }

        // Inside the transaction nothing can change the Song between the read and the write.
        if (!await songs.TrySelectGenerationAsync(current.Id, generationId, revision, time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Song just read changed inside the transaction.");
        }

        return new GenerationSelectionOutcome.Selected(
            await songs.FindAsync(current.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The Song just changed cannot be read back."));
    }

    private async Task<SongSummary?> FindSongAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        await ReferenceResolver.SongIdAsync(songs, reference, cancellationToken).ConfigureAwait(false) is { } id
            ? await songs.FindAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
}
