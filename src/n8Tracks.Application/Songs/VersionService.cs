using n8Tracks.Application.Auth;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>How asking for a new Version's numbers ended.</summary>
public abstract record NextNumbersOutcome
{
    private NextNumbersOutcome()
    {
    }

    /// <summary>The numbers a new Version from the source may take, the proposal first.</summary>
    public sealed record Found(IReadOnlyList<VersionNumberOption> Options) : NextNumbersOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : NextNumbersOutcome;

    /// <summary>Both options would be longer than a number may be, so nothing can branch from the source.</summary>
    public sealed record TooDeep : NextNumbersOutcome;
}

/// <summary>
/// What creating a Version from another asks for, as the caller sent it: the source's ID and the
/// chosen number as unread text, and an optional name. Any of them may be missing.
/// </summary>
public sealed record VersionCreateRequest(string? SourceVersionId, string? Number, string? Name);

/// <summary>How creating a Version from another ended.</summary>
public abstract record VersionCreateOutcome
{
    private VersionCreateOutcome()
    {
    }

    /// <summary>The new Version, now its Song's current one.</summary>
    public sealed record Created(VersionSummary Version) : VersionCreateOutcome;

    /// <summary>There is no Song with that ID.</summary>
    public sealed record SongNotFound : VersionCreateOutcome;

    /// <summary>A field is missing or wrong. Nothing was stored. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : VersionCreateOutcome;

    /// <summary>The number is not one of the options for the source. Nothing was stored.</summary>
    public sealed record NotOffered(IReadOnlyList<VersionNumberOption> Options) : VersionCreateOutcome;

    /// <summary>
    /// The number would have been an option, but some Version of the Song has taken it since the
    /// options were read. Nothing was stored.
    /// </summary>
    public sealed record Taken(IReadOnlyList<VersionNumberOption> Options) : VersionCreateOutcome;
}

/// <summary>How making a Version current ended.</summary>
public abstract record SetCurrentOutcome
{
    private SetCurrentOutcome()
    {
    }

    /// <summary>The Song, with the Version as its current one.</summary>
    public sealed record Updated(SongSummary Song) : SetCurrentOutcome;

    /// <summary>The reference names no Song.</summary>
    public sealed record SongNotFound : SetCurrentOutcome;

    /// <summary>The Version is missing or not the Song's. Nothing was changed.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SetCurrentOutcome;
}

/// <summary>
/// An edit of a Version's annotations: any of its name, notes, and archived flag, each left alone
/// when unsent. Name and notes are text or null; <paramref name="Archived"/> is null when unsent.
/// </summary>
public sealed record VersionEdit(SongEditField Name, SongEditField Notes, bool? Archived);

/// <summary>How an edit of a Version's annotations ended.</summary>
public abstract record VersionUpdateOutcome
{
    private VersionUpdateOutcome()
    {
    }

    /// <summary>The Version as it is now: edited, or unchanged when the edit changed nothing.</summary>
    public sealed record Updated(VersionSummary Version) : VersionUpdateOutcome;

    /// <summary>The Version is at another revision than the one the edit was based on. Nothing was changed.</summary>
    public sealed record Conflict(VersionSummary Current) : VersionUpdateOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : VersionUpdateOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : VersionUpdateOutcome;
}

/// <summary>
/// Versions: the numbers a new Version may take when it branches from an existing one, by
/// <see cref="VersionNumbering"/>; a Song's Versions as a flat list the tree is drawn from; creating
/// a Version from any other, which becomes the current one; choosing the current one; and editing a
/// Version's annotations (name, notes, archived), given the revision it was read at. Making a
/// Version current is a command, not an edit of content someone may have changed, so it carries no
/// revision and leaves the Song's alone: the last request wins.
/// </summary>
public sealed class VersionService(IVersionStore versions, ISongStore songs, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string SourceVersionIdField = "sourceVersionId";
    public const string NumberField = "number";
    public const string NameField = "name";
    public const string VersionIdField = "versionId";
    public const string NotesField = "notes";
    public const string ArchivedField = "archived";

    /// <summary>
    /// The valid numbers for a new Version created from the Version with <paramref name="id"/>
    /// (archived ones included): normally the next sibling and the child, the proposal first. Never a
    /// number any Version of the Song has or ever had.
    /// </summary>
    public async Task<NextNumbersOutcome> NextNumbersAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await versions.FindNumberingAsync(id, cancellationToken).ConfigureAwait(false) is not { } facts)
        {
            return new NextNumbersOutcome.NotFound();
        }

        var options = Options(facts);
        return options.Count == 0 ? new NextNumbersOutcome.TooDeep() : new NextNumbersOutcome.Found(options);
    }

    /// <summary>
    /// Every Version of the Song a reference (ID or shortcode) names, archived ones included, in tree
    /// order; null when it names none.
    /// </summary>
    public async Task<IReadOnlyList<VersionSummary>?> ListAsync(string? reference, CancellationToken cancellationToken)
    {
        if (await SongService.FindAsync(songs, reference, cancellationToken).ConfigureAwait(false) is not { } song)
        {
            return null;
        }

        return await versions.ListAsync(song.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a Version of the Song with <paramref name="songId"/> from one of its Versions, numbered
    /// with one of that source's options and holding a copy of its lyrics and styles, and makes it the
    /// Song's current Version. The number is checked against the options inside the transaction that
    /// stores it, so a number taken meanwhile is refused (with the options as they are now) and nothing
    /// is stored. The source is not changed.
    /// </summary>
    public async Task<VersionCreateOutcome> CreateFromAsync(Guid songId, VersionCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!Guid.TryParseExact(request.SourceVersionId, "D", out var sourceId))
        {
            errors[SourceVersionIdField] = ["Choose a Version of this Song to create from."];
        }

        if (string.IsNullOrEmpty(request.Number))
        {
            errors[NumberField] = ["Choose one of the offered numbers."];
        }

        if (VersionRules.NameErrors(request.Name) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (errors.Count > 0)
        {
            return new VersionCreateOutcome.Invalid(errors);
        }

        return await transaction.RunAsync<VersionCreateOutcome>(
            async ct =>
            {
                if (await songs.FindAsync(songId, ct).ConfigureAwait(false) is null)
                {
                    return new VersionCreateOutcome.SongNotFound();
                }

                if (await versions.FindAsync(sourceId, ct).ConfigureAwait(false) is not { } source || source.SongId != songId)
                {
                    return new VersionCreateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [SourceVersionIdField] = ["Choose a Version of this Song to create from."],
                    });
                }

                var facts = await versions.FindNumberingAsync(sourceId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The source Version was just read.");
                var options = Options(facts);
                VersionNumber.TryParse(request.Number, out var number);
                if (number is null || !options.Any(option => option.Number == number))
                {
                    return IsTakenOption(number, facts)
                        ? new VersionCreateOutcome.Taken(options)
                        : new VersionCreateOutcome.NotOffered(options);
                }

                var now = time.GetUtcNow();
                var version = VersionRules.CreateFrom(source, Guid.CreateVersion7(now), number, request.Name, now);
                await versions.AddAsync(version, ct).ConfigureAwait(false);
                await versions.SetCurrentAsync(songId, version.Id, now, ct).ConfigureAwait(false);

                return new VersionCreateOutcome.Created(await SummaryAsync(songId, version.Id, ct).ConfigureAwait(false));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a Version (archived or not) the current working Version of the Song a reference names. No
    /// revision is needed and the Song's is not changed; making the current Version current again
    /// changes nothing.
    /// </summary>
    public Task<SetCurrentOutcome> SetCurrentAsync(string? reference, string? versionId, CancellationToken cancellationToken) =>
        transaction.RunAsync<SetCurrentOutcome>(
            async ct =>
            {
                if (await SongService.FindAsync(songs, reference, ct).ConfigureAwait(false) is not { } song)
                {
                    return new SetCurrentOutcome.SongNotFound();
                }

                if (!Guid.TryParseExact(versionId, "D", out var id)
                    || await versions.FindAsync(id, ct).ConfigureAwait(false) is not { } version
                    || version.SongId != song.Id)
                {
                    return new SetCurrentOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        [VersionIdField] = ["Choose a Version of this Song."],
                    });
                }

                if (song.CurrentVersion.Id == id)
                {
                    return new SetCurrentOutcome.Updated(song);
                }

                await versions.SetCurrentAsync(song.Id, id, time.GetUtcNow(), ct).ConfigureAwait(false);
                var updated = await songs.FindAsync(song.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Song just changed cannot be read back.");
                return new SetCurrentOutcome.Updated(updated);
            },
            cancellationToken);

    /// <summary>
    /// Edits the annotations of the Version with <paramref name="id"/> (only the fields sent) if it
    /// is still at <paramref name="revision"/>. Name and notes are trimmed and blank is none; they may
    /// change whether or not the Version's inputs are frozen, as they are not creation inputs.
    /// Archiving and unarchiving change visibility only: never the number, lyrics, styles, the
    /// descendants, or which Version is current. An edit that changes nothing leaves the revision
    /// alone.
    /// </summary>
    public Task<VersionUpdateOutcome> UpdateAsync(Guid id, VersionEdit edit, int revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (edit.Name.IsSent && VersionRules.NameErrors(edit.Name.Value) is { Length: > 0 } nameErrors)
        {
            errors[NameField] = nameErrors;
        }

        if (edit.Notes.IsSent && VersionRules.NotesErrors(edit.Notes.Value) is { Length: > 0 } notesErrors)
        {
            errors[NotesField] = notesErrors;
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<VersionUpdateOutcome>(new VersionUpdateOutcome.Invalid(errors));
        }

        return transaction.RunAsync<VersionUpdateOutcome>(
            async ct =>
            {
                if (await versions.FindSummaryAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new VersionUpdateOutcome.NotFound();
                }

                if (current.Revision != revision)
                {
                    return new VersionUpdateOutcome.Conflict(current);
                }

                var annotations = new VersionAnnotations(
                    edit.Name.IsSent ? VersionRules.NormaliseName(edit.Name.Value) : current.Name,
                    edit.Notes.IsSent ? VersionRules.NormaliseNotes(edit.Notes.Value) : current.Notes,
                    edit.Archived ?? current.Archived);
                if (annotations == new VersionAnnotations(current.Name, current.Notes, current.Archived))
                {
                    return new VersionUpdateOutcome.Updated(current);
                }

                if (!await versions.TryUpdateAnnotationsAsync(id, annotations, revision, time.GetUtcNow(), ct).ConfigureAwait(false))
                {
                    return await versions.FindSummaryAsync(id, ct).ConfigureAwait(false) is { } changed
                        ? new VersionUpdateOutcome.Conflict(changed)
                        : new VersionUpdateOutcome.NotFound();
                }

                var updated = await versions.FindSummaryAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Version just edited cannot be read back.");
                return new VersionUpdateOutcome.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>The options for the source <paramref name="facts"/> describe. Stored numbers were valid when assigned, so each parses.</summary>
    private static IReadOnlyList<VersionNumberOption> Options(VersionNumberingFacts facts) =>
        VersionNumbering.Options(VersionNumber.Parse(facts.Number), facts.UsedNumbers.Select(VersionNumber.Parse));

    /// <summary>
    /// Whether <paramref name="number"/> is used and would be a sibling after the source or a child of
    /// it: a number the caller may have been offered before another Version took it.
    /// </summary>
    private static bool IsTakenOption(VersionNumber? number, VersionNumberingFacts facts)
    {
        if (number is null || !facts.UsedNumbers.Contains(number.ToString(), StringComparer.Ordinal))
        {
            return false;
        }

        var source = VersionNumber.Parse(facts.Number);
        var isLaterSibling = number.Depth == source.Depth && number.Parent == source.Parent && number.Last > source.Last;
        var isChild = number.Parent == source;
        return isLaterSibling || isChild;
    }

    private async Task<VersionSummary> SummaryAsync(Guid songId, Guid versionId, CancellationToken cancellationToken) =>
        (await versions.ListAsync(songId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(version => version.Id == versionId)
            ?? throw new InvalidOperationException("The Version just created cannot be read back.");
}
