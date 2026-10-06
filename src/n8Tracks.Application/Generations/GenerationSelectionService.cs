using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>
/// What becomes of a Song's selection when its Selected Generation leaves it (moved away, #123; or
/// deleted, #124), as sent: another of its Generations to select instead (its ID or shortcode), or a
/// workflow state (its ID) to move the Song to, left with no Selected Generation. Exactly one is
/// required when the leaving Generation is selected, and neither is accepted when it is not.
/// </summary>
public sealed record SelectionChoice(string? ReplacementGeneration, string? WorkflowState)
{
    /// <summary>Neither choice sent.</summary>
    public static SelectionChoice None { get; } = new(null, null);
}

/// <summary>A <see cref="SelectionChoice"/> once checked: what to write on the Song, if anything.</summary>
/// <param name="ReplacementId">The Generation to select instead; null when none.</param>
/// <param name="StateId">The workflow state to move the Song to, with no Selected Generation; null when none.</param>
internal sealed record CheckedSelectionChoice(Guid? ReplacementId, Guid? StateId)
{
    /// <summary>The leaving Generation is not selected: nothing changes on the Song.</summary>
    public static CheckedSelectionChoice Nothing { get; } = new(null, null);
}

/// <summary>How checking a <see cref="SelectionChoice"/> ended.</summary>
internal abstract record SelectionChoiceCheck
{
    private SelectionChoiceCheck()
    {
    }

    public sealed record Valid(CheckedSelectionChoice Choice) : SelectionChoiceCheck;

    /// <summary>The leaving Generation is selected and no choice was sent (<see cref="GenerationSelectionService.SelectionChoiceRequiredCode"/>).</summary>
    public sealed record Required : SelectionChoiceCheck;

    /// <summary>A choice is wrong, or not wanted; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SelectionChoiceCheck;
}

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
    IWorkflowStateStore states,
    GenerationService generations,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the chosen Generation is sent in, and its errors are reported under.</summary>
    public const string GenerationField = "generation";

    /// <summary>The problem code (422) for a Generation of another Song.</summary>
    public const string NotInSongCode = "generation_not_in_song";

    /// <summary>
    /// The problem code (422) for taking a Song's Selected Generation away (moving it, #123; deleting
    /// it, #124) without saying what the Song selects instead (<see cref="SelectionChoice"/>).
    /// </summary>
    public const string SelectionChoiceRequiredCode = "selection_choice_required";

    /// <summary>The field a <see cref="SelectionChoice"/>'s replacement Generation is sent in.</summary>
    public const string ReplacementGenerationField = "replacementGeneration";

    /// <summary>The field a <see cref="SelectionChoice"/>'s workflow state is sent in.</summary>
    public const string WorkflowStateField = "workflowState";

    /// <summary>
    /// Inside the caller's transaction: checks what the Song <paramref name="song"/> selects once
    /// <paramref name="leaving"/>, one of its Generations, leaves it. When it is not the Song's
    /// Selected Generation nothing changes and no choice is accepted; when it is, exactly one is
    /// required: a replacement, any other Generation of the Song (whatever its state), or a workflow
    /// state that exists.
    /// </summary>
    internal async Task<SelectionChoiceCheck> CheckChoiceAsync(
        SongSummary song,
        GenerationSummary leaving,
        SelectionChoice choice,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(leaving);
        ArgumentNullException.ThrowIfNull(choice);

        var replacementSent = !string.IsNullOrWhiteSpace(choice.ReplacementGeneration);
        var stateSent = !string.IsNullOrWhiteSpace(choice.WorkflowState);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (song.SelectedGeneration?.Id != leaving.Generation.Id)
        {
            const string NotWanted = "Only send this when the Generation is the Song's Selected Generation.";
            if (replacementSent)
            {
                errors[ReplacementGenerationField] = [NotWanted];
            }

            if (stateSent)
            {
                errors[WorkflowStateField] = [NotWanted];
            }

            return errors.Count > 0 ? new SelectionChoiceCheck.Invalid(errors) : new SelectionChoiceCheck.Valid(CheckedSelectionChoice.Nothing);
        }

        if (!replacementSent && !stateSent)
        {
            return new SelectionChoiceCheck.Required();
        }

        if (replacementSent && stateSent)
        {
            errors[ReplacementGenerationField] = ["Choose another Generation or a workflow state, not both."];
            return new SelectionChoiceCheck.Invalid(errors);
        }

        if (replacementSent)
        {
            var replacement = await generations.FindAsync(CatalogReference.Parse(choice.ReplacementGeneration!.Trim()), cancellationToken).ConfigureAwait(false);
            if (replacement is null || replacement.Generation.SongId != song.Id || replacement.Generation.Id == leaving.Generation.Id)
            {
                errors[ReplacementGenerationField] = ["Choose another Generation of this Song."];
                return new SelectionChoiceCheck.Invalid(errors);
            }

            return new SelectionChoiceCheck.Valid(new CheckedSelectionChoice(replacement.Generation.Id, null));
        }

        if (!Guid.TryParseExact(choice.WorkflowState!.Trim(), "D", out var stateId)
            || !(await states.ListAsync(cancellationToken).ConfigureAwait(false)).Any(state => state.Id == stateId))
        {
            errors[WorkflowStateField] = ["Choose one of the workflow states."];
            return new SelectionChoiceCheck.Invalid(errors);
        }

        return new SelectionChoiceCheck.Valid(new CheckedSelectionChoice(null, stateId));
    }

    /// <summary>
    /// Inside the caller's transaction, before the leaving Generation goes: writes a checked choice on
    /// the Song (as it is now): the replacement selected, or the selection cleared and the Song moved
    /// to the state, each raising its revision and setting its updated time. A choice of nothing
    /// writes nothing. True when it wrote something.
    /// </summary>
    internal async Task<bool> ApplyChoiceAsync(SongSummary song, CheckedSelectionChoice choice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(choice);

        if (choice.ReplacementId is { } replacement)
        {
            return await songs.TrySelectGenerationAsync(song.Id, replacement, song.Revision, now, cancellationToken).ConfigureAwait(false)
                ? true
                : throw new InvalidOperationException("The Song just read changed inside the transaction.");
        }

        if (choice.StateId is not { } stateId)
        {
            return false;
        }

        var details = new SongDetails(song.Title, song.Concept, stateId, song.Notes, song.Release);
        if (!await songs.TrySelectGenerationAsync(song.Id, null, song.Revision, now, cancellationToken).ConfigureAwait(false)
            || !await songs.TryUpdateAsync(song.Id, details, song.Revision + 1, now, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Song just read changed inside the transaction.");
        }

        return true;
    }

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
