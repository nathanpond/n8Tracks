using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.References;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Generations;

/// <summary>
/// What creating a new Song from a Generation asks for: the new Song's title (the Song title rules,
/// required), and, when the Generation is its Song's Selected Generation, what that Song selects
/// instead (<see cref="SelectionChoice"/>).
/// </summary>
public sealed record GenerationMoveRequest(string? Title, SelectionChoice Choice);

/// <summary>How creating a new Song from a Generation ended. Only <see cref="Moved"/> stored anything.</summary>
public abstract record GenerationMoveOutcome
{
    private GenerationMoveOutcome()
    {
    }

    /// <summary>
    /// The new Song (with its Selected Generation), its Version 1, the Generation where it is now, and
    /// the alias its old shortcode became.
    /// </summary>
    public sealed record Moved(SongSummary Song, VersionSummary Version, GenerationSummary Generation, string Alias) : GenerationMoveOutcome;

    /// <summary>The reference names no live Generation.</summary>
    public sealed record NotFound : GenerationMoveOutcome;

    /// <summary>The Generation is not at the revision sent: it as it is now.</summary>
    public sealed record Conflict(GenerationSummary Current) : GenerationMoveOutcome;

    /// <summary>A field is wrong; errors by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : GenerationMoveOutcome;

    /// <summary>
    /// The Generation is its Song's Selected Generation and no <see cref="SelectionChoice"/> was sent
    /// (<see cref="GenerationSelectionService.SelectionChoiceRequiredCode"/>).
    /// </summary>
    public sealed record SelectionChoiceRequired(GenerationSummary Generation) : GenerationMoveOutcome;
}

/// <summary>
/// Moving a Generation (#123; #141 reuses <see cref="MoveWithinAsync"/>): the one way a Generation
/// changes its Version, Song, or ordinal. It is moved, never copied or shared: it takes its new
/// Version's next ordinal and so a new shortcode, and its old shortcode becomes a permanent
/// <see cref="ShortcodeAlias"/> that still finds it and is never given to another Generation. Its
/// rating, comments, image, states, Suno data, provider record, and event link go with it unchanged;
/// its revision goes up by one. The Version it leaves stays frozen, keeps its other Generations, and
/// never gives its ordinal out again; no Version's inputs or lineage change (invariant 1), the new
/// Version's being written once, before the Generation freezes it.
/// <para>
/// Creating a new Song from a Generation: a new Song (the title given; default workflow state; no
/// Genres, Tags, credits, memberships, or artwork) whose Version 1 holds a copy of the Generation's
/// Version's kind, options, lyrics, styles, name, and lineage, with notes naming that Version, then
/// a Derived From relationship from the new Song to the old one, then the move, in one transaction.
/// The Generation becomes the new Song's Selected Generation. When it was its old Song's, the user
/// says first what that Song selects instead, as when deleting it (<see cref="SelectionChoice"/>).
/// </para>
/// </summary>
public sealed class GenerationMoveService(
    ISongStore songs,
    IVersionStore versions,
    IWorkflowStateStore states,
    IRelationshipStore relationships,
    GenerationService generations,
    GenerationSelectionService selection,
    IGenerationStore generationRows,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field the new Song's title is sent in, and its errors are reported under.</summary>
    public const string TitleField = "title";

    /// <summary>
    /// Moves the Generation <paramref name="reference"/> names (its ID, shortcode, or an alias) out of
    /// its Song into a new Song created for it, given the Generation's revision.
    /// </summary>
    public async Task<GenerationMoveOutcome> MoveToNewSongAsync(
        CatalogReference reference,
        GenerationMoveRequest request,
        int revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var choice = request.Choice ?? SelectionChoice.None;
        if (SongRules.TitleErrors(request.Title) is { Length: > 0 } titleErrors)
        {
            return new GenerationMoveOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [TitleField] = titleErrors });
        }

        return await transaction.RunAsync<GenerationMoveOutcome>(
            async ct =>
            {
                if (await generations.FindAsync(reference, ct).ConfigureAwait(false) is not { } generation)
                {
                    return new GenerationMoveOutcome.NotFound();
                }

                if (generation.Generation.Revision != revision)
                {
                    return new GenerationMoveOutcome.Conflict(generation);
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
                        return new GenerationMoveOutcome.SelectionChoiceRequired(generation);
                    case SelectionChoiceCheck.Invalid invalid:
                        return new GenerationMoveOutcome.Invalid(invalid.Errors);
                    default:
                        throw new InvalidOperationException("Unknown selection choice check.");
                }

                var source = await versions.FindAsync(generation.Generation.VersionId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A live Generation's Version cannot be read.");
                var initial = WorkflowState.Initial(await states.ListAsync(ct).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("Every workflow state is hidden, so a new Song has no state to start in.");
                var number = await songs.NextShortcodeNumberAsync(ct).ConfigureAwait(false);
                var now = time.GetUtcNow();

                // The new Song and its Version 1, a copy of the Generation's Version's creation inputs,
                // written while still mutable; the move then freezes it.
                var (created, blank) = Song.Create(Guid.CreateVersion7(now), Guid.CreateVersion7(now), number, request.Title!, null, initial, source.Inputs, now);
                var sourceShortcode = Shortcodes.ForVersion(generation.SongShortcodeNumber, source.Number);
                var copy = new SongVersion(
                    blank.Id,
                    created.Id,
                    blank.Number,
                    source.Name,
                    string.Create(CultureInfo.InvariantCulture, $"Created from Version {sourceShortcode} when Generation {generation.Shortcode} moved to this Song."),
                    VersionVisibility.Active,
                    source.Lyrics,
                    source.Styles,
                    source.Inputs,
                    now,
                    now,
                    Revision: 1,
                    source.Lineage,
                    Imported: source.Imported);
                await songs.AddAsync(created, copy, ct).ConfigureAwait(false);
                if (copy.Lineage != VersionLineage.None)
                {
                    await versions.ReplaceLineageAsync(copy.Id, copy.Lineage, ct).ConfigureAwait(false);
                }

                await relationships.AddAsync(
                    new StoredRelationship(Guid.CreateVersion7(now), SystemRelationshipTypes.DerivedFrom.Id, created.Id, song.Id),
                    now,
                    ct).ConfigureAwait(false);

                // The old Song's selection is resolved before the Generation leaves it.
                if (!await selection.ApplyChoiceAsync(song, checkedChoice, now, ct).ConfigureAwait(false))
                {
                    await generationRows.TouchSongAsync(song.Id, now, ct).ConfigureAwait(false);
                }

                var target = await versions.FindAsync(copy.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Version just created cannot be read back.");
                var leaving = await generationRows.FindAsync(generation.Generation.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Generation just read cannot be read again.");
                var (moved, alias) = await MoveWithinAsync(leaving, target, now, ct).ConfigureAwait(false);

                var newSong = await songs.FindAsync(created.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song just created cannot be read back.");
                if (!await songs.TrySelectGenerationAsync(newSong.Id, moved.Generation.Id, newSong.Revision, now, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The Song just created changed inside the transaction.");
                }

                return new GenerationMoveOutcome.Moved(
                    await songs.FindAsync(created.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The Song just created cannot be read back."),
                    await versions.FindSummaryAsync(copy.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The Version just created cannot be read back."),
                    await generationRows.FindAsync(moved.Generation.Id, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("The Generation just moved cannot be read back."),
                    alias.Alias);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inside the caller's transaction: moves <paramref name="generation"/> (as just read) to
    /// <paramref name="target"/> (as just read; another Version, which it freezes), leaving its old
    /// shortcode as an alias. The caller has resolved the old Song's selection when the Song changes:
    /// a Generation still selected there is refused. Shared with the Changed/Conflict review (#141),
    /// which moves a Generation to a new child Version of its own Song.
    /// </summary>
    internal async Task<(GenerationSummary Generation, ShortcodeAlias Alias)> MoveWithinAsync(
        GenerationSummary generation,
        SongVersion target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(target);

        if (generation.IsSelected && target.SongId != generation.Generation.SongId)
        {
            throw new InvalidOperationException("A Generation leaving its Song must not be that Song's Selected Generation: resolve the selection first.");
        }

        var alias = ShortcodeAlias.Leaving(generation.Shortcode, generation.Generation.Id, now);
        var (frozen, moved) = target.ReceiveGeneration(generation.Generation, now);
        if (!await versions.TryMoveGenerationAsync(frozen, moved, target.Revision, generation.Generation.Revision, alias, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The Generation or the Version it moves to changed inside the transaction.");
        }

        var stored = await generationRows.FindAsync(moved.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Generation just moved cannot be read back.");
        return (stored, alias);
    }
}
