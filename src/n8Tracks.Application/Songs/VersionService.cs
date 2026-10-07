using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.References;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;
using n8Tracks.Domain.Suno;

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
/// <paramref name="Lyrics"/> and <paramref name="Styles"/>, when given, replace the text copied from
/// the source (carrying text a frozen source could not take into the new Version); null copies it.
/// </summary>
public sealed record VersionCreateRequest(
    string? SourceVersionId,
    string? Number,
    string? Name,
    string? Lyrics = null,
    string? Styles = null);

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
/// (lyrics, styles, and its Suno options), each left alone when unsent. Name and notes are text or
/// null; lyrics and styles must be text (null is refused); <paramref name="Archived"/> is null when
/// unsent. <paramref name="Inputs"/> holds the options sent, by API name, as sent: merged key by key,
/// so options not sent are kept (<see cref="VersionInputRules"/>); null or empty sends none.
/// </summary>
public sealed record VersionEdit(
    SongEditField Name,
    SongEditField Notes,
    bool? Archived,
    SongEditField Lyrics,
    SongEditField Styles,
    IReadOnlyDictionary<string, JsonElement>? Inputs = null)
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

    /// <summary>
    /// A field is wrong. Nothing was changed. The errors are keyed by field name; <paramref name="Rules"/>
    /// names, under the same keys, the lineage rules (#122) a source or file input breaks.
    /// </summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors, IReadOnlyDictionary<string, string[]>? Rules = null) : VersionUpdateOutcome;

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
    ISunoModelList models,
    IRelationshipStore relationships,
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
    public const string InputsField = VersionInputRules.InputsField;

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
    /// with one of that source's options and holding a copy of its lyrics and styles (or the request's
    /// own, when it sends them, a frozen source included) and of every one of its Suno options, the
    /// ones that do not apply to its kind and mode included, and makes it the
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

        if (request.Lyrics is not null && VersionRules.LyricsErrors(request.Lyrics) is { Length: > 0 } lyricsErrors)
        {
            errors[LyricsField] = lyricsErrors;
        }

        if (request.Styles is not null && VersionRules.StylesErrors(request.Styles) is { Length: > 0 } stylesErrors)
        {
            errors[StylesField] = stylesErrors;
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
                var version = VersionRules.CreateFrom(source, Guid.CreateVersion7(now), number, request.Name, now, request.Lyrics, request.Styles);
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

    /// <summary>The Version with <paramref name="id"/>, with its lyrics, styles, and options; null when there is none.</summary>
    public Task<VersionDetail?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        versions.FindDetailAsync(id, cancellationToken);

    /// <summary>
    /// Edits the Version with <paramref name="id"/> (only the fields sent) if it is still at
    /// <paramref name="revision"/>. Name and notes are trimmed and blank is none; they may change
    /// whether or not the Version's inputs are frozen, as they are not creation inputs. Lyrics and
    /// styles are stored as written apart from line endings (<see cref="VersionRules.NormaliseInput"/>);
    /// the options sent are checked against the inventory and the model list and merged key by key
    /// (<see cref="VersionInputRules"/>), and switching the kind or mode clears nothing. Inputs are
    /// changed only through <see cref="StoreAsync"/>. Archiving and unarchiving change visibility
    /// only: never the number, the inputs, the descendants, or which Version is current. An edit that
    /// changes nothing leaves the revision alone. One wrong field refuses the whole edit. On a frozen
    /// Version (a Generation attached) an edit whose inputs are unchanged is a metadata edit; one that
    /// changes any of them is refused whole as <see cref="VersionUpdateOutcome.Frozen"/>, after the
    /// revision check.
    /// </summary>
    public Task<VersionUpdateOutcome> UpdateAsync(Guid id, VersionEdit edit, int revision, CancellationToken cancellationToken) =>
        UpdateAsync(id, edit, revision, VersionEditSource.Session, cancellationToken);

    /// <summary>
    /// As <see cref="UpdateAsync(Guid, VersionEdit, int, CancellationToken)"/>; when the edit comes
    /// from a <see cref="VersionEditSource.Credential"/> and changes the lyrics or styles, the text it
    /// replaces is snapshotted first (<see cref="EditorRevisionService"/>), in the same transaction.
    /// The history covers the lyrics and styles only, not the options.
    /// </summary>
    public async Task<VersionUpdateOutcome> UpdateAsync(Guid id, VersionEdit edit, int revision, VersionEditSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        // The lineage keys of inputs (#122) are read on their own; the rest are Suno options.
        var sentInputs = (edit.Inputs ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))
            .Where(static pair => !VersionLineageInputs.IsLineageKey(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var sentLineage = (edit.Inputs ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))
            .Where(static pair => VersionLineageInputs.IsLineageKey(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var rules = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var lineageEdit = VersionLineageInputs.Parse(sentLineage, errors, rules);
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

        if (sentInputs.Count > 0)
        {
            var modelList = await models.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (field, messages) in VersionInputRules.Errors(CreateFieldInventory.Embedded, [.. modelList], sentInputs))
            {
                errors[field] = messages;
            }
        }

        if (errors.Count > 0)
        {
            return new VersionUpdateOutcome.Invalid(errors, rules.Count > 0 ? rules : null);
        }

        return await transaction.RunAsync<VersionUpdateOutcome>(
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
                var text = new VersionText(
                    edit.Lyrics.IsSent ? VersionRules.NormaliseInput(edit.Lyrics.Value!) : current.Lyrics,
                    edit.Styles.IsSent ? VersionRules.NormaliseInput(edit.Styles.Value!) : current.Styles);
                var held = new VersionText(current.Lyrics, current.Styles);
                var inputs = VersionInputRules.Apply(current.Inputs, sentInputs);

                // The model list may have changed since the edit was checked (a model renamed or
                // deleted), so a model that changes is checked again inside the transaction, which
                // the list's own changes also take.
                if (inputs.Model != current.Inputs.Model || inputs.SoundsModel != current.Inputs.SoundsModel)
                {
                    var listed = await models.ListAsync(ct).ConfigureAwait(false);
                    if (VersionInputRules.Errors(CreateFieldInventory.Embedded, [.. listed], sentInputs) is { Count: > 0 } modelErrors)
                    {
                        return new VersionUpdateOutcome.Invalid(modelErrors);
                    }
                }

                // The lineage sent, looked up and checked against the rules for the kind and mode the
                // edit leaves the Version in; parts not sent are kept as they are.
                var lineage = current.Lineage.Lineage;
                var resolved = LineageResolution.Unchanged;
                if (lineageEdit.IsSent)
                {
                    resolved = await ResolveLineageAsync(lineageEdit, current, inputs, ct).ConfigureAwait(false);
                    if (resolved.Errors.Count > 0)
                    {
                        return new VersionUpdateOutcome.Invalid(resolved.Errors, resolved.Rules);
                    }

                    lineage = resolved.Lineage!;
                }

                var textChange = text != held;
                var lineageChange = lineage != current.Lineage.Lineage;
                var inputsChange = textChange || inputs != current.Inputs || lineageChange;
                if (!inputsChange && annotations == new VersionAnnotations(summary.Name, summary.Notes, summary.Archived))
                {
                    return new VersionUpdateOutcome.Updated(current);
                }

                var now = time.GetUtcNow();
                if (inputsChange)
                {
                    // A credential's edit keeps the text it replaces in the history first, once the
                    // edit is known to be allowed.
                    Func<CancellationToken, Task>? keepReplaced = source == VersionEditSource.Credential && textChange
                        ? token => EditorRevisionService.KeepAsync(revisions, id, held, now, now, token)
                        : null;
                    switch (await StoreAsync(current, annotations, text, inputs, lineage, resolved.Externals, keepReplaced, now, ct).ConfigureAwait(false))
                    {
                        case InputsWrite.Frozen:
                            return new VersionUpdateOutcome.Frozen(current);
                        case InputsWrite.Stale:
                            return await StaleAsync(id, ct).ConfigureAwait(false);
                    }

                    if (lineageChange)
                    {
                        await RelateSourcesAsync(summary.SongId, lineageEdit, lineage, resolved.Generations, now, ct).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inside the caller's transaction: stores <paramref name="text"/> on <paramref name="current"/>,
    /// keeping its annotations and options, through <see cref="StoreAsync"/> (restoring a snapshot),
    /// after running <paramref name="beforeWrite"/> once the write is known to be allowed.
    /// </summary>
    internal Task<InputsWrite> StoreInputsAsync(
        VersionDetail current,
        VersionText text,
        Func<CancellationToken, Task>? beforeWrite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        var summary = current.Summary;
        return StoreAsync(current, new VersionAnnotations(summary.Name, summary.Notes, summary.Archived), text, current.Inputs, current.Lineage.Lineage, [], beforeWrite, now, cancellationToken);
    }

    /// <summary>
    /// The one place a Version's creation inputs change after it is created, together with whatever
    /// annotations the same edit changes; restoring a snapshot comes through here too. Whether they may
    /// still change is the entity's rule (<see cref="SongVersion.WithInputs"/> and
    /// <see cref="SongVersion.WithLineage"/>, which refuse a frozen Version), applied to the Version as
    /// stored before anything is written, and so before <paramref name="beforeWrite"/> runs (a snapshot
    /// of the text being replaced). A changed lineage is written after the Version's row, with the
    /// external references it names (<paramref name="externals"/>) stored first.
    /// </summary>
    private async Task<InputsWrite> StoreAsync(
        VersionDetail current,
        VersionAnnotations annotations,
        VersionText text,
        VersionInputs inputs,
        VersionLineage lineage,
        IReadOnlyCollection<ExternalSunoReference> externals,
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
                .WithInputs(text.Lyrics, text.Styles, inputs)
                .WithLineage(lineage);
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
            new VersionText(changed.Lyrics, changed.Styles),
            changed.Inputs,
            summary.Revision,
            now,
            cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            return InputsWrite.Stale;
        }

        if (changed.Lineage != version.Lineage)
        {
            await versions.EnsureExternalReferencesAsync(externals, cancellationToken).ConfigureAwait(false);
            await versions.ReplaceLineageAsync(summary.Id, changed.Lineage, cancellationToken).ConfigureAwait(false);
        }

        return InputsWrite.Stored;
    }

    /// <summary>
    /// The lineage an edit leaves a Version with: each part sent, looked up (a source's type, and the
    /// Generation or Song it names), in place of the one held, and every rule checked for the kind and
    /// mode the edit leaves the Version in. A rule is reported only when a part it reads was sent, so
    /// a part kept as it is never refuses an edit of another (changing the kind or mode never refuses).
    /// </summary>
    private async Task<LineageResolution> ResolveLineageAsync(LineageEdit edit, VersionDetail current, VersionInputs inputs, CancellationToken cancellationToken)
    {
        var held = current.Lineage.Lineage;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var rules = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var generations = new Dictionary<Guid, SourceGenerationFacts>();
        var externals = new List<ExternalSunoReference>();

        var audio = edit.SourcesSent
            ? await ResolveSourcesAsync(edit.Sources, VersionSourceGroup.Audio, held.AudioSources, current.Summary, generations, externals, errors, rules, cancellationToken).ConfigureAwait(false)
            : held.AudioSources;
        var inspiration = edit.InspirationSent
            ? await ResolveSourcesAsync(edit.InspirationSources, VersionSourceGroup.Inspiration, held.InspirationSources, current.Summary, generations, externals, errors, rules, cancellationToken).ConfigureAwait(false)
            : held.InspirationSources;
        var lineage = new VersionLineage(
            audio,
            inspiration,
            edit.InspirationSent ? edit.Playlist : held.Playlist,
            edit.VoiceSent ? edit.Voice : held.Voice,
            edit.FileInputsSent ? edit.FileInputs : held.FileInputs);

        var broken = VersionLineageRules.Errors(
            lineage,
            inputs.Kind,
            inputs.SongMode,
            LineageCheck.Write,
            LineageOrigin.Edit,
            target => target.GenerationId is { } id && generations.TryGetValue(id, out var facts) ? facts.DurationSeconds : null,
            held.FileInputs);
        foreach (var error in broken.Where(error => Reads(error, edit)))
        {
            errors.TryAdd(error.Field, [error.Message]);
            rules.TryAdd(error.Field, [error.Rule]);
        }

        return new LineageResolution(errors.Count == 0 ? lineage : null, externals, generations, errors, rules);
    }

    /// <summary>Whether <paramref name="error"/> is about a part the edit sent (cross-part rules read both of theirs).</summary>
    private static bool Reads(LineageError error, LineageEdit edit)
    {
        bool Under(string field) => error.Field.StartsWith(field, StringComparison.Ordinal);
        return (edit.SourcesSent && Under(VersionLineageRules.SourcesField))
            || (edit.InspirationSent && Under(VersionLineageRules.InspirationField))
            || (edit.VoiceSent && Under(VersionLineageRules.VoiceField))
            || (edit.FileInputsSent && Under(VersionLineageRules.FileInputsField))
            || (edit.SourcesSent && error.Rule is VersionLineageRules.InspirationWithCover or VersionLineageRules.AudioSlotTaken);
    }

    /// <summary>
    /// Each source sent, looked up: an audio source's type must exist and stand for a Suno action (a
    /// user type with none is refused), an Inspiration source is of the Use as Inspiration type, and a
    /// Generation or Song must exist; a Generation of the Version itself is refused. A Generation or
    /// Song no longer in the catalog that the group already names by that ID is kept as it is (a
    /// source outlives its target), so a read sent back is no change. Errors are added under the
    /// source's field, and a source with one is left out.
    /// </summary>
    private async Task<IReadOnlyList<VersionSource>> ResolveSourcesAsync(
        IReadOnlyList<SourceRequest> requests,
        VersionSourceGroup group,
        IReadOnlyList<VersionSource> held,
        VersionSummary version,
        Dictionary<Guid, SourceGenerationFacts> generations,
        List<ExternalSunoReference> externals,
        Dictionary<string, string[]> errors,
        Dictionary<string, string[]> rules,
        CancellationToken cancellationToken)
    {
        void Refuse(string field, string rule, string message)
        {
            errors.TryAdd(field, [message]);
            rules.TryAdd(field, [rule]);
        }

        var resolved = new List<VersionSource>();
        foreach (var request in requests)
        {
            Guid typeId;
            string? action;
            if (group == VersionSourceGroup.Inspiration)
            {
                typeId = SystemRelationshipTypes.UseAsInspiration.Id;
                action = SystemRelationshipTypes.UseAsInspiration.SunoAction;
            }
            else if (await relationships.FindTypeAsync(request.TypeId!.Value, cancellationToken).ConfigureAwait(false) is not { } type)
            {
                Refuse(request.Field + ".typeId", VersionLineageRules.SourceTypeUnknown, "There is no relationship type with that ID.");
                continue;
            }
            else if (!type.Type.IsSystem && type.Type.SunoAction is null)
            {
                Refuse(request.Field + ".typeId", VersionLineageRules.SourceTypeNotMapped, $"'{type.Type.Name}' is not mapped to a Suno action, so it cannot be a source's type.");
                continue;
            }
            else
            {
                typeId = type.Type.Id;
                action = type.Type.SunoAction;
            }

            VersionSourceTarget target;
            if (request.Generation is { } generationReference)
            {
                var generationId = await GenerationIdAsync(CatalogReference.Parse(generationReference), cancellationToken).ConfigureAwait(false);
                if (generationId is { } missing
                    && held.Any(source => source.Target.GenerationId == missing)
                    && await versions.FindSourceGenerationAsync(missing, cancellationToken).ConfigureAwait(false) is null)
                {
                    target = VersionSourceTarget.OfGeneration(missing);
                }
                else if (generationId is null
                    || await versions.FindSourceGenerationAsync(generationId.Value, cancellationToken).ConfigureAwait(false) is not { } facts)
                {
                    Refuse(request.Field + ".generation", VersionLineageRules.SourceNotFound, "There is no such Generation.");
                    continue;
                }
                else if (facts.VersionId == version.Id)
                {
                    Refuse(request.Field + ".generation", VersionLineageRules.SourceIsOwnGeneration, "A Version cannot be made from its own Generation; create a new Version from it instead.");
                    continue;
                }
                else
                {
                    generations[facts.Id] = facts;
                    target = VersionSourceTarget.OfGeneration(facts.Id);
                }
            }
            else if (request.Song is { } songReference)
            {
                var songReferenceParsed = CatalogReference.Parse(songReference);
                if (await SongService.FindAsync(songs, songReference, cancellationToken).ConfigureAwait(false) is { } song)
                {
                    target = VersionSourceTarget.OfSong(song.Id);
                }
                else if (songReferenceParsed.Kind == ReferenceKind.Id && held.Any(source => source.Target.SongId == songReferenceParsed.Id))
                {
                    target = VersionSourceTarget.OfSong(songReferenceParsed.Id);
                }
                else
                {
                    Refuse(request.Field + ".song", VersionLineageRules.SourceNotFound, "There is no such Song.");
                    continue;
                }
            }
            else if (request.External!.SunoId is var sunoId
                && !held.Any(source => string.Equals(source.Target.ExternalSunoId, sunoId, StringComparison.Ordinal))
                && await versions.FindSourceGenerationBySunoIdAsync(sunoId, cancellationToken).ConfigureAwait(false) is { } imported
                && imported.VersionId != version.Id)
            {
                // A pasted Suno ID n8Tracks already has is that Generation (#125); one the group
                // already names as a Suno clip stays as it is, so a read sent back is no change.
                generations[imported.Id] = imported;
                target = VersionSourceTarget.OfGeneration(imported.Id);
            }
            else
            {
                var external = request.External!;
                externals.Add(external);
                target = VersionSourceTarget.OfExternal(external.SunoId);
            }

            resolved.Add(new VersionSource(typeId, action, target, request.ContinueAtSeconds, request.SecondaryIds));
        }

        return resolved;
    }

    /// <summary>
    /// For each source of a part the edit sent that points at another Song (through its Generation or
    /// directly), relates the Version's Song to it under the source's type, unless the two are related
    /// under that type already (either way round). Removing a source never removes a relationship.
    /// </summary>
    private async Task RelateSourcesAsync(
        Guid songId,
        LineageEdit edit,
        VersionLineage lineage,
        IReadOnlyDictionary<Guid, SourceGenerationFacts> generations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IEnumerable<VersionSource> sent = [.. edit.SourcesSent ? lineage.AudioSources : [], .. edit.InspirationSent ? lineage.InspirationSources : []];
        foreach (var source in sent)
        {
            var other = source.Target.GenerationId is { } generationId && generations.TryGetValue(generationId, out var facts)
                ? facts.SongId
                : source.Target.SongId;
            if (other is not { } otherSongId
                || otherSongId == songId
                || await relationships.ExistsAsync(source.TypeId, songId, otherSongId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await relationships.AddAsync(new StoredRelationship(Guid.CreateVersion7(now), source.TypeId, songId, otherSongId), now, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The Generation a reference (its ID or shortcode) names, if any; whether it exists is the caller's to check.</summary>
    private async Task<Guid?> GenerationIdAsync(CatalogReference reference, CancellationToken cancellationToken) =>
        reference.Kind switch
        {
            ReferenceKind.Id => reference.Id,
            ReferenceKind.Generation => await versions.FindGenerationIdByShortcodeAsync(
                    reference.SongShortcodeNumber,
                    reference.VersionNumber!.ToString(),
                    reference.GenerationOrdinal,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => null,
        };

    /// <summary>A lineage an edit sent, looked up: the lineage when valid, what it needs stored and read, and what is wrong.</summary>
    private sealed record LineageResolution(
        VersionLineage? Lineage,
        IReadOnlyCollection<ExternalSunoReference> Externals,
        IReadOnlyDictionary<Guid, SourceGenerationFacts> Generations,
        Dictionary<string, string[]> Errors,
        Dictionary<string, string[]> Rules)
    {
        public static LineageResolution Unchanged { get; } = new(null, [], new Dictionary<Guid, SourceGenerationFacts>(), [], []);
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
