using System.Globalization;
using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>What creating a Song asks for. Either field may be missing.</summary>
public sealed record SongRequest(string? Title, string? Concept);

/// <summary>A list request as the caller sent it: every value unread text, any of them missing.</summary>
public sealed record SongListRequest(string? Sort, string? Direction, IReadOnlyList<string?> States, string? Page, string? PageSize);

/// <summary>How creating a Song ended.</summary>
public abstract record SongOutcome
{
    private SongOutcome()
    {
    }

    /// <summary>The Song was stored with its Version <c>1</c>.</summary>
    public sealed record Created(SongSummary Song) : SongOutcome;

    /// <summary>A field is missing or wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongOutcome;
}

/// <summary>One field of an edit: left as it is when not sent; when sent, its value, which may be null.</summary>
public readonly record struct SongEditField(bool IsSent, string? Value)
{
    /// <summary>A field the edit leaves alone.</summary>
    public static SongEditField Unsent => default;

    /// <summary>A field the edit sets to <paramref name="value"/>.</summary>
    public static SongEditField Of(string? value) => new(IsSent: true, value);
}

/// <summary>
/// An edit of a Song's details: a partial merge, so only the fields sent change. <c>StateId</c> is
/// the unread text of a workflow state's ID.
/// </summary>
public sealed record SongEdit(SongEditField Title, SongEditField Concept, SongEditField StateId);

/// <summary>How editing a Song ended.</summary>
public abstract record SongUpdateOutcome
{
    private SongUpdateOutcome()
    {
    }

    /// <summary>The Song as it is now: changed, at its next revision, or unchanged when the edit changed nothing.</summary>
    public sealed record Updated(SongSummary Song) : SongUpdateOutcome;

    /// <summary>There is no Song with that ID.</summary>
    public sealed record NotFound : SongUpdateOutcome;

    /// <summary>The Song is at another revision than the edit was based on. Nothing was changed.</summary>
    public sealed record Conflict(SongSummary Current) : SongUpdateOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SongUpdateOutcome;
}

/// <summary>How listing Songs ended.</summary>
public abstract record SongListOutcome
{
    private SongListOutcome()
    {
    }

    public sealed record Listed(SongPage Page) : SongListOutcome;

    /// <summary>A parameter is not one the list understands; <paramref name="Message"/> names it.</summary>
    public sealed record Invalid(string Message) : SongListOutcome;
}

/// <summary>
/// Creating, finding, and editing Songs. A Song needs only a title; it is created with a shortcode
/// from a sequence that never repeats, in the first visible workflow state, and with an empty,
/// mutable Version <c>1</c> as its current Version, all in one transaction. Its details are edited
/// against the revision the caller read, so a stale edit never overwrites a newer one.
/// </summary>
public sealed class SongService(ISongStore songs, IWorkflowStateStore states, ISunoModelList models, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string TitleField = "title";
    public const string ConceptField = "concept";
    public const string StateIdField = "stateId";

    /// <summary>The list parameters, as the API spells them.</summary>
    public const string SortParameter = "sort";
    public const string DirectionParameter = "direction";
    public const string StateParameter = "state";
    public const string PageParameter = "page";
    public const string PageSizeParameter = "pageSize";

    public const string SortUpdated = "updated";
    public const string SortTitle = "title";
    public const string Ascending = "asc";
    public const string Descending = "desc";

    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    /// <summary>
    /// Creates a Song. The title is required and the concept optional, by <see cref="SongRules"/>;
    /// when either is wrong nothing is stored and no shortcode number is used. Its Version 1 starts
    /// with the default options (<see cref="VersionInputRules.Defaults"/>), Suno's title pre-filled
    /// with the Song's, and the first model the model list offers as both the Song's and the Sound's
    /// model (none when it offers none).
    /// </summary>
    public async Task<SongOutcome> CreateAsync(SongRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { Count: > 0 } errors)
        {
            return new SongOutcome.Invalid(errors);
        }

        var id = await transaction.RunAsync(
            async ct =>
            {
                var initial = WorkflowState.Initial(await states.ListAsync(ct).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("Every workflow state is hidden, so a new Song has no state to start in.");
                var number = await songs.NextShortcodeNumberAsync(ct).ConfigureAwait(false);
                var now = time.GetUtcNow();
                var model = (await models.OfferedAsync(ct).ConfigureAwait(false)).FirstOrDefault();
                var inputs = VersionInputRules.Defaults(CreateFieldInventory.Embedded, SongRules.NormaliseTitle(request.Title!)) with
                {
                    Model = model,
                    SoundsModel = model,
                };
                var (song, version) = Song.Create(Guid.CreateVersion7(now), Guid.CreateVersion7(now), number, request.Title!, request.Concept, initial, inputs, now);
                await songs.AddAsync(song, version, ct).ConfigureAwait(false);

                return song.Id;
            },
            cancellationToken).ConfigureAwait(false);

        var created = await songs.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Song just created cannot be read back.");
        return new SongOutcome.Created(created);
    }

    /// <summary>
    /// The Song a reference names: its ID (hyphenated, any letter case) or its complete shortcode
    /// (any letter case). Null when it names none.
    /// </summary>
    public Task<SongSummary?> FindAsync(string? reference, CancellationToken cancellationToken) => FindAsync(songs, reference, cancellationToken);

    /// <summary>The Song a reference names in <paramref name="songs"/>, as <see cref="FindAsync(string?, CancellationToken)"/> reads it.</summary>
    internal static Task<SongSummary?> FindAsync(ISongStore songs, string? reference, CancellationToken cancellationToken)
    {
        var parsed = CatalogReference.Parse(reference);
        return parsed.Kind switch
        {
            ReferenceKind.Id => songs.FindAsync(parsed.Id, cancellationToken),
            ReferenceKind.Song => songs.FindByShortcodeNumberAsync(parsed.SongShortcodeNumber, cancellationToken),
            _ => Task.FromResult<SongSummary?>(null),
        };
    }

    /// <summary>
    /// Edits a Song's title, concept, or workflow state, given the revision the caller read. Only the
    /// fields sent change: the title follows the creation rule, a null or blank concept clears it, and
    /// the state may be any state, hidden ones included, so a Song can move from any state to any
    /// other. A stale revision (lower or higher) changes nothing and answers the Song as it is now.
    /// An edit that changes nothing once normalised is not written and answers the Song unchanged.
    /// </summary>
    public Task<SongUpdateOutcome> UpdateAsync(Guid id, SongEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        // Checked, compared, and written in one transaction, so the state chosen still exists when
        // the Song is moved into it.
        return transaction.RunAsync<SongUpdateOutcome>(
            async ct =>
            {
                var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (edit.Title.IsSent && SongRules.TitleErrors(edit.Title.Value) is { Length: > 0 } titleErrors)
                {
                    errors[TitleField] = titleErrors;
                }

                if (edit.Concept.IsSent && SongRules.ConceptErrors(edit.Concept.Value) is { Length: > 0 } conceptErrors)
                {
                    errors[ConceptField] = conceptErrors;
                }

                var stateId = Guid.Empty;
                if (edit.StateId.IsSent
                    && (!Guid.TryParseExact(edit.StateId.Value, "D", out stateId)
                        || !(await states.ListAsync(ct).ConfigureAwait(false)).Any(state => state.Id == stateId)))
                {
                    errors[StateIdField] = ["Choose one of the workflow states."];
                }

                if (errors.Count > 0)
                {
                    return new SongUpdateOutcome.Invalid(errors);
                }

                if (await songs.FindAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new SongUpdateOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new SongUpdateOutcome.Conflict(current);
                }

                var details = new SongDetails(
                    edit.Title.IsSent ? SongRules.NormaliseTitle(edit.Title.Value!) : current.Title,
                    edit.Concept.IsSent ? SongRules.NormaliseConcept(edit.Concept.Value) : current.Concept,
                    edit.StateId.IsSent ? stateId : current.State.Id);
                if (details == new SongDetails(current.Title, current.Concept, current.State.Id))
                {
                    return new SongUpdateOutcome.Updated(current);
                }

                if (!await songs.TryUpdateAsync(id, details, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return await songs.FindAsync(id, ct).ConfigureAwait(false) is { } changed
                        ? new SongUpdateOutcome.Conflict(changed)
                        : new SongUpdateOutcome.NotFound();
                }

                var updated = await songs.FindAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song just edited cannot be read back.");
                return new SongUpdateOutcome.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>
    /// A page of Songs. <c>sort</c> is <c>updated</c> (the default) or <c>title</c>; <c>direction</c>
    /// is <c>asc</c> or <c>desc</c> (by default newest first, and titles A to Z); each <c>state</c> is
    /// the ID of a workflow state; <c>page</c> counts from 1; <c>pageSize</c> is 1 to
    /// <see cref="MaximumPageSize"/>, <see cref="DefaultPageSize"/> by default.
    /// </summary>
    public async Task<SongListOutcome> ListAsync(SongListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SongSort sort;
        switch (request.Sort)
        {
            case null or SortUpdated:
                sort = SongSort.Updated;
                break;
            case SortTitle:
                sort = SongSort.Title;
                break;
            default:
                return Invalid($"{SortParameter} must be {SortUpdated} or {SortTitle}.");
        }

        bool descending;
        switch (request.Direction)
        {
            case null:
                descending = sort == SongSort.Updated;
                break;
            case Ascending or Descending:
                descending = request.Direction == Descending;
                break;
            default:
                return Invalid($"{DirectionParameter} must be {Ascending} or {Descending}.");
        }

        if (!TryReadWhole(request.Page, 1, int.MaxValue, 1, out var page))
        {
            return Invalid($"{PageParameter} must be a whole number from 1.");
        }

        if (!TryReadWhole(request.PageSize, 1, MaximumPageSize, DefaultPageSize, out var pageSize))
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"{PageSizeParameter} must be a whole number from 1 to {MaximumPageSize}."));
        }

        var stateIds = new List<Guid>();
        if (request.States.Count > 0)
        {
            var known = (await states.ListAsync(cancellationToken).ConfigureAwait(false)).Select(static state => state.Id).ToHashSet();
            foreach (var state in request.States)
            {
                if (!Guid.TryParseExact(state, "D", out var stateId) || !known.Contains(stateId))
                {
                    return Invalid($"Each {StateParameter} must be the ID of a workflow state.");
                }

                if (!stateIds.Contains(stateId))
                {
                    stateIds.Add(stateId);
                }
            }
        }

        var query = new SongListQuery(sort, descending, stateIds, page, pageSize);
        return new SongListOutcome.Listed(await songs.ListAsync(query, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The errors of a create request, keyed by field name; empty when it can be created.</summary>
    public static Dictionary<string, string[]> Validate(SongRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (SongRules.TitleErrors(request.Title) is { Length: > 0 } titleErrors)
        {
            errors[TitleField] = titleErrors;
        }

        if (SongRules.ConceptErrors(request.Concept) is { Length: > 0 } conceptErrors)
        {
            errors[ConceptField] = conceptErrors;
        }

        return errors;
    }

    private static SongListOutcome.Invalid Invalid(string message) => new(message);

    /// <summary>A whole number from <paramref name="minimum"/> to <paramref name="maximum"/>, written plainly; <paramref name="fallback"/> when missing.</summary>
    private static bool TryReadWhole(string? text, int minimum, int maximum, int fallback, out int value)
    {
        if (text is null)
        {
            value = fallback;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum;
    }
}
