using n8Tracks.Application.Artwork;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>What deleting a Generation would take with it, as its confirmation states it.</summary>
/// <param name="Generation">The Generation, as it is now (its rating, comments, and image included).</param>
/// <param name="Replacements">
/// When it is its Song's Selected Generation, the Song's other live Generations (whatever their
/// state), any of which the Song may select instead; empty otherwise, or when it has none.
/// </param>
/// <param name="SourceVersionCount">The Versions that use it as a source, which will show it as Deleted.</param>
public sealed record GenerationDeletionImpact(GenerationSummary Generation, IReadOnlyList<GenerationSummary> Replacements, int SourceVersionCount)
{
    /// <summary>Whether it is its Song's Selected Generation, so the user must say what the Song selects instead.</summary>
    public bool IsSelected => Generation.IsSelected;

    /// <summary>Its comments, deleted with it.</summary>
    public int CommentCount => Generation.Comments.Count;

    /// <summary>Its own Suno artwork (none or one image), deleted with it; the Song's own artwork stays.</summary>
    public int ArtworkCount => Generation.Artwork is null ? 0 : 1;
}

/// <summary>How asking what deleting a Generation would do ended.</summary>
public abstract record GenerationDeletionImpactOutcome
{
    private GenerationDeletionImpactOutcome()
    {
    }

    /// <summary>What deleting it would do now.</summary>
    public sealed record Found(GenerationDeletionImpact Impact) : GenerationDeletionImpactOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record NotFound : GenerationDeletionImpactOutcome;
}

/// <summary>How deleting a Generation ended. Only <see cref="Deleted"/> changed anything.</summary>
public abstract record GenerationDeleteOutcome
{
    private GenerationDeleteOutcome()
    {
    }

    /// <summary>The Generation is in <paramref name="Group"/>; <paramref name="Song"/> is its Song as it is now.</summary>
    public sealed record Deleted(RetentionGroup Group, SongSummary Song) : GenerationDeleteOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record NotFound : GenerationDeleteOutcome;

    /// <summary>The Generation is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(GenerationSummary Current) : GenerationDeleteOutcome;

    /// <summary>
    /// The Generation is its Song's Selected Generation and no <see cref="SelectionChoice"/> was sent
    /// (<see cref="GenerationSelectionService.SelectionChoiceRequiredCode"/>).
    /// </summary>
    public sealed record SelectionChoiceRequired(GenerationSummary Generation) : GenerationDeleteOutcome;

    /// <summary>
    /// The choice is wrong (both sent, a Generation that is not another of the Song's, a state that
    /// does not exist) or not wanted (the Generation is not selected); errors by field
    /// (<see cref="GenerationDeletionService.InvalidReplacementCode"/>).
    /// </summary>
    public sealed record InvalidReplacement(IReadOnlyDictionary<string, string[]> Errors) : GenerationDeleteOutcome;
}

/// <summary>
/// Deleting one Generation from n8Tracks (#124), web UI only. Nothing in Suno is touched: this service
/// reaches only the catalog and retention, never the extension or the network. The Generation goes
/// into retention for <see cref="RetentionService.RetentionPeriod"/> as one group, with its comments,
/// provider record, and event link (by cascade) and the files of its own Suno artwork, which the group
/// keeps; the Song's own artwork, even when it is a copy of this image, is untouched. Its Version
/// stays frozen and keeps its last ordinal, so neither the ordinal nor the shortcode is given out
/// again. Sources of other Versions that pointed at it keep pointing at its Suno ID, as an external
/// reference labelled Deleted (#122); no frozen Version's sources are changed otherwise.
/// <para>
/// When it is its Song's Selected Generation, the user says first what the Song selects instead
/// (<see cref="SelectionChoice"/>, shared with the move of #123): another of its Generations, which
/// leaves the workflow state as it is, or a workflow state, which clears the selection and moves the
/// Song to it. The selection is resolved before the Generation goes, so a restore (the container
/// command <c>restore-deleted</c>, <see cref="DeletedItemsService"/>) brings back the Generation with its
/// rating, comments, and image, but never the selection or the Song's state. A restore whose Version or
/// Song is gone is refused.
/// </para>
/// </summary>
public sealed class GenerationDeletionService(
    ISongStore songs,
    IVersionStore versions,
    GenerationService generations,
    GenerationSelectionService selection,
    IGenerationStore generationRows,
    GenerationArtworkService artwork,
    RetentionService retention,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>
    /// The problem code (422) for a <see cref="SelectionChoice"/> that is wrong or not wanted: both
    /// choices sent, a replacement that is not another Generation of the Song, or a state that does not exist.
    /// </summary>
    public const string InvalidReplacementCode = "invalid_replacement";

    /// <summary>What deleting the Generation <paramref name="reference"/> names (its ID, shortcode, or an alias) would do now.</summary>
    public Task<GenerationDeletionImpactOutcome> ImpactAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        transaction.RunAsync<GenerationDeletionImpactOutcome>(
            async ct =>
            {
                if (await generations.FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationDeletionImpactOutcome.NotFound();
                }

                IReadOnlyList<GenerationSummary> replacements = generation.IsSelected
                    ? [.. (await generationRows.ForSongAsync(generation.Generation.SongId, ct).ConfigureAwait(false)).Where(other => other.Generation.Id != generation.Generation.Id)]
                    : [];
                var sources = await generationRows.SourceVersionCountAsync(generation.Generation.Id, ct).ConfigureAwait(false);
                return new GenerationDeletionImpactOutcome.Found(new GenerationDeletionImpact(generation, replacements, sources));
            },
            cancellationToken);

    /// <summary>
    /// Deletes the Generation <paramref name="reference"/> names if it is still at
    /// <paramref name="revision"/>, resolving its Song's selection by <paramref name="choice"/> first
    /// when it is selected. See the class summary for what goes and what stays.
    /// </summary>
    public Task<GenerationDeleteOutcome> DeleteAsync(
        CatalogReference reference,
        SelectionChoice choice,
        int revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choice);

        return transaction.RunAsync<GenerationDeleteOutcome>(
            async ct =>
            {
                if (await generations.FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationDeleteOutcome.NotFound();
                }

                if (generation.Generation.Revision != revision)
                {
                    return new GenerationDeleteOutcome.Conflict(generation);
                }

                var song = await songs.FindAsync(generation.Generation.SongId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A live Generation's Song cannot be read.");
                CheckedSelectionChoice checkedChoice;
                switch (await selection.CheckChoiceAsync(song, generation, choice, ct).ConfigureAwait(false))
                {
                    case SelectionChoiceCheck.Valid valid:
                        checkedChoice = valid.Choice;
                        break;
                    case SelectionChoiceCheck.Required:
                        return new GenerationDeleteOutcome.SelectionChoiceRequired(generation);
                    case SelectionChoiceCheck.Invalid invalid:
                        return new GenerationDeleteOutcome.InvalidReplacement(invalid.Errors);
                    default:
                        throw new InvalidOperationException("Unknown selection choice check.");
                }

                // The selection is resolved before the Generation goes, so the group never holds it.
                var now = time.GetUtcNow();
                if (!await selection.ApplyChoiceAsync(song, checkedChoice, now, ct).ConfigureAwait(false))
                {
                    await generationRows.TouchSongAsync(song.Id, now, ct).ConfigureAwait(false);
                }

                var ids = new[] { generation.Generation.Id };
                var files = await artwork.RetainedFilesAsync(ids, ct).ConfigureAwait(false);

                // Sources of other Versions that point at it keep its Suno ID (#122).
                await versions.RewriteSourcesOfDeletedGenerationsAsync(ids, [], now, ct).ConfigureAwait(false);
                var group = await retention.RetainWithinAsync(
                    new RetentionRequest(
                        RetainedRecordTypes.Generation,
                        Label(generation.Shortcode),
                        generation.Shortcode,
                        [new RetainedRoot(RetainedRecordTypes.Generation, generation.Generation.Id)],
                        files),
                    ct).ConfigureAwait(false);

                return new GenerationDeleteOutcome.Deleted(
                    group,
                    await songs.FindAsync(song.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The Song just changed cannot be read back."));
            },
            cancellationToken);
    }

    /// <summary>How the recovery listing names a deleted Generation: "Generation n8-4-v1.1-g2".</summary>
    internal static string Label(string generationShortcode) => $"Generation {generationShortcode}";
}
