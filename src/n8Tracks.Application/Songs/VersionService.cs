using n8Tracks.Application.Auth;
using n8Tracks.Application.References;
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
/// What creating a Version from another asks for, as the caller sent it: the source's ID or
/// shortcode and the chosen number as unread text, and an optional name. Any of them may be missing.
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
/// An edit of a Version: any of its annotations (name, notes, archived flag) and its creation inputs
/// (lyrics, styles), each left alone when unsent. Name and notes are text or null; lyrics and styles
/// must be text (null is refused); <paramref name="Archived"/> is null when unsent.
/// </summary>
public sealed record VersionEdit(SongEditField Name, SongEditField Notes, bool? Archived, SongEditField Lyrics, SongEditField Styles)
{
    /// <summary>An edit of the annotations only.</summary>
    public VersionEdit(SongEditField Name, SongEditField Notes, bool? Archived)
        : this(Name, Notes, Archived, SongEditField.Unsent, SongEditField.Unsent)
    {
    }
}

/// <summary>Who an edit of a Version comes from, which decides whether the text it replaces is snapshotted.</summary>
public enum VersionEditSource
{
    /// <summary>The web editor (a browser session), whose own idle and leave snapshots cover its edits.</summary>
    Session,

    /// <summary>A tool calling the API with a credential: the lyrics and styles it replaces are snapshotted first.</summary>
    Credential,
}

/// <summary>How an edit of a Version ended.</summary>
public abstract record VersionUpdateOutcome
{
    private VersionUpdateOutcome()
    {
    }

    /// <summary>The Version as it is now: edited, or unchanged when the edit changed nothing.</summary>
    public sealed record Updated(VersionDetail Version) : VersionUpdateOutcome;

    /// <summary>The Version is at another revision than the one the edit was based on. Nothing was changed.</summary>
    public sealed record Conflict(VersionDetail Current) : VersionUpdateOutcome;

    /// <summary>A field is wrong. Nothing was changed. The errors are keyed by field name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : VersionUpdateOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : VersionUpdateOutcome;

    /// <summary>
    /// The edit would change a creation input of a Version a Generation is attached to. Nothing was
    /// changed, metadata included. <paramref name="Version"/> is the Version as it is.
    /// </summary>
    public sealed record Frozen(VersionDetail Version) : VersionUpdateOutcome;
}

/// <summary>How the one write of a Version's creation inputs ended.</summary>
internal enum InputsWrite
{
    /// <summary>Written, and the revision raised by one.</summary>
    Stored,

    /// <summary>The Version is gone or no longer at the revision read. Nothing was written.</summary>
    Stale,

    /// <summary>A Generation is attached, so the inputs may not change. Nothing was written.</summary>
    Frozen,
}

/// <summary>
/// Versions: the numbers a new Version may take when it branches from an existing one, by
/// <see cref="VersionNumbering"/>; a Song's Versions as a flat list the tree is drawn from; creating
/// a Version from any other, which becomes the current one; choosing the current one; and editing a
/// Version's annotations (name, notes, archived), given the revision it was read at. Making a
/// Version current is a command, not an edit of content someone may have changed, so it carries no
/// revision and leaves the Song's alone: the last request wins.
/// </summary>
public sealed class VersionService(
    IVersionStore versions,
    ISongStore songs,
    IEditorRevisionStore revisions,
    IExclusiveTransaction transaction,
    TimeProvider time)
{
    /// <summary>The field names validation errors are keyed by, as the API spells them.</summary>
    public const string SourceVersionIdField = "sourceVersionId";
    public const string NumberField = "number";
    public const string NameField = "name";
    public const string VersionIdField = "versionId";
    public const string NotesField = "notes";
    public const string ArchivedField = "archived";
    public const string LyricsField = "lyrics";
    public const string StylesField = "styles";

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
        var sourceReference = CatalogReference.Parse(request.SourceVersionId);
        if (sourceReference.Kind is not (ReferenceKind.Id or ReferenceKind.Version))
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

                if (await ReferenceResolver.VersionIdAsync(versions, sourceReference, ct).ConfigureAwait(false) is not { } sourceId
                    || await versions.FindAsync(sourceId, ct).ConfigureAwait(false) is not { } source
                    || source.SongId != songId)
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
    /// Makes a Version (archived or not), named by its ID or shortcode, the current working Version of
    /// the Song a reference names. A Version of another Song is refused as a field error. No
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

                if (await ReferenceResolver.VersionIdAsync(versions, CatalogReference.Parse(versionId), ct).ConfigureAwait(false) is not { } id
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

    /// <summary>The Version with <paramref name="id"/>, with its lyrics and styles; null when there is none.</summary>
    public Task<VersionDetail?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        versions.FindDetailAsync(id, cancellationToken);

    /// <summary>
    /// Edits the Version with <paramref name="id"/> (only the fields sent) if it is still at
    /// <paramref name="revision"/>. Name and notes are trimmed and blank is none; they may change
    /// whether or not the Version's inputs are frozen, as they are not creation inputs. Lyrics and
    /// styles are stored as written apart from line endings (<see cref="VersionRules.NormaliseInput"/>)
    /// and are changed only through <see cref="StoreAsync"/>. Archiving and unarchiving change
    /// visibility only: never the number, lyrics, styles, the descendants, or which Version is current.
    /// An edit that changes nothing leaves the revision alone. One wrong field refuses the whole edit.
    /// On a frozen Version (a Generation attached) an edit whose lyrics and styles are unchanged is a
    /// metadata edit; one that changes either is refused whole as <see cref="VersionUpdateOutcome.Frozen"/>,
    /// after the revision check.
    /// </summary>
    public Task<VersionUpdateOutcome> UpdateAsync(Guid id, VersionEdit edit, int revision, CancellationToken cancellationToken) =>
        UpdateAsync(id, edit, revision, VersionEditSource.Session, cancellationToken);

    /// <summary>
    /// As <see cref="UpdateAsync(Guid, VersionEdit, int, CancellationToken)"/>; when the edit comes
    /// from a <see cref="VersionEditSource.Credential"/> and changes the lyrics or styles, the text it
    /// replaces is snapshotted first (<see cref="EditorRevisionService"/>), in the same transaction.
    /// </summary>
    public Task<VersionUpdateOutcome> UpdateAsync(Guid id, VersionEdit edit, int revision, VersionEditSource source, CancellationToken cancellationToken)
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

        if (edit.Lyrics.IsSent && VersionRules.LyricsErrors(edit.Lyrics.Value) is { Length: > 0 } lyricsErrors)
        {
            errors[LyricsField] = lyricsErrors;
        }

        if (edit.Styles.IsSent && VersionRules.StylesErrors(edit.Styles.Value) is { Length: > 0 } stylesErrors)
        {
            errors[StylesField] = stylesErrors;
        }

        if (errors.Count > 0)
        {
            return Task.FromResult<VersionUpdateOutcome>(new VersionUpdateOutcome.Invalid(errors));
        }

        return transaction.RunAsync<VersionUpdateOutcome>(
            async ct =>
            {
                if (await versions.FindDetailAsync(id, ct).ConfigureAwait(false) is not { } current)
                {
                    return new VersionUpdateOutcome.NotFound();
                }

                var summary = current.Summary;
                if (summary.Revision != revision)
                {
                    return new VersionUpdateOutcome.Conflict(current);
                }

                var annotations = new VersionAnnotations(
                    edit.Name.IsSent ? VersionRules.NormaliseName(edit.Name.Value) : summary.Name,
                    edit.Notes.IsSent ? VersionRules.NormaliseNotes(edit.Notes.Value) : summary.Notes,
                    edit.Archived ?? summary.Archived);
                var inputs = new VersionInputs(
                    edit.Lyrics.IsSent ? VersionRules.NormaliseInput(edit.Lyrics.Value!) : current.Lyrics,
                    edit.Styles.IsSent ? VersionRules.NormaliseInput(edit.Styles.Value!) : current.Styles);
                var inputsChange = inputs != new VersionInputs(current.Lyrics, current.Styles);
                if (!inputsChange && annotations == new VersionAnnotations(summary.Name, summary.Notes, summary.Archived))
                {
                    return new VersionUpdateOutcome.Updated(current);
                }

                var now = time.GetUtcNow();
                if (inputsChange)
                {
                    // A credential's edit keeps the text it replaces in the history first, once the
                    // edit is known to be allowed.
                    Func<CancellationToken, Task>? keepReplaced = source == VersionEditSource.Credential
                        ? token => EditorRevisionService.KeepAsync(revisions, id, new VersionInputs(current.Lyrics, current.Styles), now, now, token)
                        : null;
                    switch (await StoreAsync(current, annotations, inputs, keepReplaced, now, ct).ConfigureAwait(false))
                    {
                        case InputsWrite.Frozen:
                            return new VersionUpdateOutcome.Frozen(current);
                        case InputsWrite.Stale:
                            return await StaleAsync(id, ct).ConfigureAwait(false);
                    }
                }
                else if (!await versions.TryUpdateAnnotationsAsync(id, annotations, revision, now, ct).ConfigureAwait(false))
                {
                    return await StaleAsync(id, ct).ConfigureAwait(false);
                }

                var updated = await versions.FindDetailAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Version just edited cannot be read back.");
                return new VersionUpdateOutcome.Updated(updated);
            },
            cancellationToken);
    }

    /// <summary>
    /// Inside the caller's transaction: stores <paramref name="inputs"/> on <paramref name="current"/>,
    /// keeping its annotations, through <see cref="StoreAsync"/> (restoring a snapshot), after running
    /// <paramref name="beforeWrite"/> once the write is known to be allowed.
    /// </summary>
    internal Task<InputsWrite> StoreInputsAsync(
        VersionDetail current,
        VersionInputs inputs,
        Func<CancellationToken, Task>? beforeWrite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        var summary = current.Summary;
        return StoreAsync(current, new VersionAnnotations(summary.Name, summary.Notes, summary.Archived), inputs, beforeWrite, now, cancellationToken);
    }

    /// <summary>
    /// The one place a Version's creation inputs change after it is created, together with whatever
    /// annotations the same edit changes; restoring a snapshot comes through here too. Whether they may
    /// still change is the entity's rule (<see cref="SongVersion.WithInputs"/>, which refuses a frozen
    /// Version), applied to the Version as stored before anything is written, and so before
    /// <paramref name="beforeWrite"/> runs (a snapshot of the text being replaced).
    /// </summary>
    private async Task<InputsWrite> StoreAsync(
        VersionDetail current,
        VersionAnnotations annotations,
        VersionInputs inputs,
        Func<CancellationToken, Task>? beforeWrite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var summary = current.Summary;
        if (await versions.FindAsync(summary.Id, cancellationToken).ConfigureAwait(false) is not { } version
            || version.Revision != summary.Revision)
        {
            return InputsWrite.Stale;
        }

        SongVersion changed;
        try
        {
            changed = version
                .WithAnnotations(annotations.Name, annotations.Notes, annotations.Archived ? VersionVisibility.Archived : VersionVisibility.Active)
                .WithInputs(inputs.Lyrics, inputs.Styles);
        }
        catch (VersionFrozenException)
        {
            return InputsWrite.Frozen;
        }

        if (beforeWrite is not null)
        {
            await beforeWrite(cancellationToken).ConfigureAwait(false);
        }

        var stored = await versions.TryUpdateInputsAsync(
            summary.Id,
            new VersionAnnotations(changed.Name, changed.Notes, changed.Visibility == VersionVisibility.Archived),
            new VersionInputs(changed.Lyrics, changed.Styles),
            summary.Revision,
            now,
            cancellationToken).ConfigureAwait(false);
        return stored ? InputsWrite.Stored : InputsWrite.Stale;
    }

    /// <summary>After a write matched nothing: the Version as it is now, as a conflict, or not found.</summary>
    private async Task<VersionUpdateOutcome> StaleAsync(Guid id, CancellationToken cancellationToken) =>
        await versions.FindDetailAsync(id, cancellationToken).ConfigureAwait(false) is { } changed
            ? new VersionUpdateOutcome.Conflict(changed)
            : new VersionUpdateOutcome.NotFound();

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
