using System.Globalization;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// What a Version is made from, beyond its text and options: its sources (other Generations or
/// Songs, or Suno clips known only by ID), Inspiration (individual sources or one Suno playlist), its
/// Voice, and the files it needs attached by hand in Suno. All of it is a creation input: frozen with
/// the Version once a Generation is attached (<see cref="SongVersion.WithLineage"/>), copied by Create
/// New Version From, and kept when the kind or mode changes (what applies is worked out when it is
/// sent to Suno). Spike TS-002 describes each Suno action. What each part may hold is
/// <see cref="VersionLineageRules"/>'.
/// </summary>
/// <param name="AudioSources">
/// The sources of its audio action, in order: one for Cover, Extend, Sample This Song, or Reuse
/// Prompt, two for Mashup; or, on an imported Version only, any number of the general Remix type.
/// </param>
/// <param name="InspirationSources">Up to four individual Inspiration sources, in order; none when a playlist is the Inspiration.</param>
/// <param name="Playlist">The Suno playlist used as Inspiration, with a snapshot of its clips; null for none.</param>
/// <param name="Voice">The Suno persona used as the Voice; null for none.</param>
/// <param name="FileInputs">The files to attach by hand in Suno, at most one of each kind. n8Tracks keeps only the note, not the file.</param>
public sealed record VersionLineage(
    IReadOnlyList<VersionSource> AudioSources,
    IReadOnlyList<VersionSource> InspirationSources,
    InspirationPlaylist? Playlist,
    VersionVoice? Voice,
    IReadOnlyList<VersionFileInput> FileInputs)
{
    /// <summary>No sources, no Inspiration, no Voice, and no file inputs: what a new Song's Version 1 has.</summary>
    public static VersionLineage None { get; } = new([], [], null, null, []);

    public IReadOnlyList<VersionSource> AudioSources { get; } = AudioSources ?? throw new ArgumentNullException(nameof(AudioSources));

    public IReadOnlyList<VersionSource> InspirationSources { get; } = InspirationSources ?? throw new ArgumentNullException(nameof(InspirationSources));

    public InspirationPlaylist? Playlist { get; } = Playlist;

    public VersionVoice? Voice { get; } = Voice;

    public IReadOnlyList<VersionFileInput> FileInputs { get; } = FileInputs ?? throw new ArgumentNullException(nameof(FileInputs));

    /// <summary>Whether it holds nothing at all.</summary>
    public bool IsEmpty => AudioSources.Count == 0 && InspirationSources.Count == 0 && Playlist is null && Voice is null && FileInputs.Count == 0;

    /// <summary>
    /// The audio action its audio sources stand for (<see cref="SunoActions"/>), taken from the first
    /// one that has one; null when there are no audio sources or only general Remix ones.
    /// </summary>
    public string? AudioAction => AudioSources.Select(static source => source.SunoAction).FirstOrDefault(static action => action is not null);

    /// <summary>Equal when every part is, the lists item by item in order.</summary>
    public bool Equals(VersionLineage? other) =>
        other is not null
        && AudioSources.SequenceEqual(other.AudioSources)
        && InspirationSources.SequenceEqual(other.InspirationSources)
        && Equals(Playlist, other.Playlist)
        && Equals(Voice, other.Voice)
        && FileInputs.SequenceEqual(other.FileInputs);

    public override int GetHashCode() => HashCode.Combine(AudioSources.Count, InspirationSources.Count, Playlist, Voice, FileInputs.Count);
}

/// <summary>Which list of a Version a source is in. Positions count from 0 within each.</summary>
public enum VersionSourceGroup
{
    Audio,
    Inspiration,
}

/// <summary>
/// One source: what it is (its relationship type, and the Suno action that type stood for when it
/// was written, so a later remapping of a user type cannot change a Version's action), what it points
/// at, and for Extend the position the new audio continues from.
/// </summary>
/// <param name="TypeId">Its relationship type: a system type, or (#126) a user type mapped to a Suno action.</param>
/// <param name="SunoAction">The type's Suno action key when written (<see cref="SunoActions"/>); null for the general Remix type.</param>
/// <param name="Target">What it points at: exactly one Generation, Song, or Suno clip.</param>
/// <param name="ContinueAtSeconds">Extend only: where the new audio continues from, in seconds, at most two decimals; null otherwise.</param>
/// <param name="SecondaryIds">
/// Further identifiers Suno supplied for it (the lineage story's, such as <c>edited_clip_id</c>), as a
/// JSON object of text values with its keys in order; null for none.
/// </param>
public sealed record VersionSource(Guid TypeId, string? SunoAction, VersionSourceTarget Target, decimal? ContinueAtSeconds = null, string? SecondaryIds = null);

/// <summary>
/// What a source points at: a Generation (followed if it moves), a Song when the exact Generation is
/// not known, or a Suno clip known only by its Suno ID (an external reference, shared by Suno ID).
/// Exactly one is set; <see cref="VersionLineageRules"/> refuses anything else.
/// </summary>
public sealed record VersionSourceTarget(Guid? GenerationId, Guid? SongId, string? ExternalSunoId)
{
    public static VersionSourceTarget OfGeneration(Guid id) => new(id, null, null);

    public static VersionSourceTarget OfSong(Guid id) => new(null, id, null);

    public static VersionSourceTarget OfExternal(string sunoId) => new(null, null, sunoId);

    /// <summary>How many of the three are set; a valid target has exactly one.</summary>
    public int Count => (GenerationId is null ? 0 : 1) + (SongId is null ? 0 : 1) + (ExternalSunoId is null ? 0 : 1);
}

/// <summary>A Suno playlist used as Inspiration: its ID, its name (blank shows the ID), and the clip IDs it held when chosen.</summary>
public sealed record InspirationPlaylist(string SunoPlaylistId, string Name, IReadOnlyList<string> ClipIds)
{
    public IReadOnlyList<string> ClipIds { get; } = ClipIds ?? throw new ArgumentNullException(nameof(ClipIds));

    public bool Equals(InspirationPlaylist? other) =>
        other is not null
        && string.Equals(SunoPlaylistId, other.SunoPlaylistId, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && ClipIds.SequenceEqual(other.ClipIds, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(SunoPlaylistId, Name, ClipIds.Count);
}

/// <summary>The Suno persona a Version uses as its Voice: its ID and name (blank shows the ID).</summary>
public sealed record VersionVoice(string PersonaId, string Name);

/// <summary>A file a Version needs attached by hand in Suno: which slot, and what to attach.</summary>
public sealed record VersionFileInput(VersionFileInputKind Kind, string Description);

/// <summary>Suno's form slots for a file: Audio (either mode), and Image and Video (a Simple-mode Song only).</summary>
public enum VersionFileInputKind
{
    Audio,
    Image,
    Video,
}

/// <summary>When <see cref="VersionLineageRules.Errors"/> is asked.</summary>
public enum LineageCheck
{
    /// <summary>When the lineage is written: partial work is allowed (one source of a Mashup, an Extend without its position).</summary>
    Write,

    /// <summary>When a Generation is attached or Suno is asked to generate: the set must also be complete.</summary>
    Complete,
}

/// <summary>Where a lineage being checked comes from.</summary>
public enum LineageOrigin
{
    /// <summary>The user's edit (the editor or the API): the general Remix type cannot be chosen.</summary>
    Edit,

    /// <summary>An import from Suno, which may carry Remix sources for an action n8Tracks does not recognise.</summary>
    Import,
}

/// <summary>A rule a lineage breaks: the field it is reported under, the rule's code, and a message for the user.</summary>
public sealed record LineageError(string Field, string Rule, string Message);

/// <summary>
/// The rules of a Version's lineage (<see cref="VersionLineage"/>), each with a stable code an error
/// names it by. Rules that need a complete set (Mashup's two sources, Extend's position) are checked
/// only with <see cref="LineageCheck.Complete"/>, so the editor can hold partial work; the others
/// whenever it is written. Changing the kind or mode never refuses: what no longer applies is kept and
/// left out of what is sent to Suno (<see cref="Applies(VersionSourceGroup, VersionKind, CreationMode)"/>).
/// </summary>
public static class VersionLineageRules
{
    public const int InspirationMaximum = 4;
    public const int PlaylistClipMaximum = 500;
    public const int NameMaximumLength = 200;
    public const int DescriptionMaximumLength = 500;
    public const int SecondaryIdsMaximum = 10;

    /// <summary>The field names lineage errors are keyed by, as the API spells them.</summary>
    public const string SourcesField = "inputs.sources";
    public const string InspirationField = "inputs.inspiration";
    public const string VoiceField = "inputs.voice";
    public const string FileInputsField = "inputs.fileInputs";

    // The codes each rule is named by.
    public const string OneTarget = "one_target";
    public const string SunoIdInvalid = "suno_id_invalid";
    public const string SourceTypeNotAudioAction = "source_type_not_audio_action";
    public const string SourceTypeImportOnly = "source_type_import_only";
    public const string RemixWithAudioAction = "remix_with_audio_action";
    public const string OneAudioAction = "one_audio_action";
    public const string AudioSourceCount = "audio_source_count";
    public const string ContinueAtOnlyExtend = "continue_at_only_extend";
    public const string ContinueAtInvalid = "continue_at_invalid";
    public const string ContinueAtRequired = "continue_at_required";
    public const string ContinueAtBeyondSource = "continue_at_beyond_source";
    public const string InspirationType = "inspiration_type";
    public const string InspirationCount = "inspiration_count";
    public const string InspirationBothForms = "inspiration_both_forms";
    public const string InspirationWithCover = "inspiration_with_cover";
    public const string PlaylistIdInvalid = "playlist_id_invalid";
    public const string PlaylistClipCount = "playlist_clip_count";
    public const string NameLength = "name_length";
    public const string PersonaIdInvalid = "persona_id_invalid";
    public const string OneVoice = "one_voice";
    public const string FileInputRepeated = "file_input_repeated";
    public const string FileInputDescription = "file_input_description";
    public const string FileInputSimpleOnly = "file_input_simple_only";
    public const string AudioSlotTaken = "audio_slot_taken";
    public const string SecondaryIdsInvalid = "secondary_ids_invalid";

    // Rules only the application layer can check, needing the catalog.
    public const string SourceNotFound = "source_not_found";
    public const string SourceIsOwnGeneration = "source_is_own_generation";
    public const string SourceTypeUnknown = "source_type_unknown";
    public const string SourceTypeNotMapped = "source_type_not_mapped";

    /// <summary>The five audio actions, at most one of which a Version has (TS-002).</summary>
    public static IReadOnlySet<string> AudioActions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SunoActions.Cover,
        SunoActions.Extend,
        SunoActions.Mashup,
        SunoActions.Sample,
        SunoActions.ReusePrompt,
    };

    /// <summary>
    /// Every rule <paramref name="lineage"/> breaks, for a Version of <paramref name="kind"/> in Song
    /// mode <paramref name="songMode"/>; empty when it is valid. <paramref name="durationOf"/> gives
    /// a source Generation's length in seconds when known, for Extend's position.
    /// <paramref name="keptFileInputs"/> are the file inputs the Version already holds: one sent back
    /// exactly as held is not checked against the mode (#320), so an image or video note kept, and
    /// hidden, from Simple mode never refuses an Advanced-mode edit of the audio note beside it.
    /// </summary>
    public static IReadOnlyList<LineageError> Errors(
        VersionLineage lineage,
        VersionKind kind,
        CreationMode songMode,
        LineageCheck check,
        LineageOrigin origin,
        Func<VersionSourceTarget, double?>? durationOf = null,
        IReadOnlyCollection<VersionFileInput>? keptFileInputs = null)
    {
        ArgumentNullException.ThrowIfNull(lineage);

        var errors = new List<LineageError>();
        AudioErrors(lineage, check, origin, durationOf, errors);
        InspirationErrors(lineage, errors);
        VoiceErrors(lineage.Voice, errors);
        FileInputErrors(lineage, kind, songMode, keptFileInputs ?? [], errors);
        return errors;
    }

    /// <summary>
    /// Whether a part of a lineage applies to a Version of <paramref name="kind"/> in Song mode
    /// <paramref name="songMode"/>: audio actions and the Voice to a Song in either mode; individual
    /// Inspiration sources to an Advanced-mode Song only.
    /// </summary>
    public static bool Applies(VersionSourceGroup group, VersionKind kind, CreationMode songMode) =>
        kind == VersionKind.Song && (group == VersionSourceGroup.Audio || songMode == CreationMode.Advanced);

    /// <summary>Whether a playlist used as Inspiration, or a Voice, applies: to a Song in either mode.</summary>
    public static bool AppliesToSong(VersionKind kind) => kind == VersionKind.Song;

    /// <summary>Whether a file input applies: an audio file to a Song in either mode, an image or video to a Simple-mode Song only.</summary>
    public static bool Applies(VersionFileInputKind fileKind, VersionKind kind, CreationMode songMode) =>
        kind == VersionKind.Song && (fileKind == VersionFileInputKind.Audio || songMode == CreationMode.Simple);

    /// <summary>Whether a source is of the general Remix type, which only an import may carry and which is never automated.</summary>
    public static bool IsRemix(VersionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.TypeId == SystemRelationshipTypes.Remix.Id;
    }

    /// <summary>A position as stored: whole hundredths of a second.</summary>
    public static long ToHundredths(decimal seconds) => (long)decimal.Round(seconds * 100m, MidpointRounding.ToEven);

    /// <summary>A stored position in seconds.</summary>
    public static decimal FromHundredths(long hundredths) => hundredths / 100m;

    private static void AudioErrors(
        VersionLineage lineage,
        LineageCheck check,
        LineageOrigin origin,
        Func<VersionSourceTarget, double?>? durationOf,
        List<LineageError> errors)
    {
        var sources = lineage.AudioSources;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var field = Item(SourcesField, index);
            TargetErrors(source.Target, field, errors);
            if (IsRemix(source))
            {
                if (origin == LineageOrigin.Edit)
                {
                    errors.Add(new(field, SourceTypeImportOnly, "The general Remix type is kept for imported clips whose Suno action n8Tracks does not recognise; choose the action instead."));
                }
            }
            else if (source.SunoAction is null || !AudioActions.Contains(source.SunoAction))
            {
                errors.Add(new(field, SourceTypeNotAudioAction, "An audio source's type must stand for Cover, Extend, Mashup, Sample This Song, or Reuse Prompt."));
            }

            ContinueAtErrors(source, field, check, durationOf, errors);
            if (source.SecondaryIds is not null && !IsSecondaryIds(source.SecondaryIds))
            {
                errors.Add(new(field + ".secondaryIds", SecondaryIdsInvalid, string.Create(CultureInfo.InvariantCulture, $"Secondary identifiers are up to {SecondaryIdsMaximum} named Suno IDs.")));
            }
        }

        var actions = sources.Where(static source => !IsRemix(source)).Select(static source => source.SunoAction).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (actions.Count > 1)
        {
            errors.Add(new(SourcesField, OneAudioAction, "A Version has at most one audio action: Cover, Extend, Mashup, Sample This Song, or Reuse Prompt."));
            return;
        }

        var remixes = sources.Count(IsRemix);
        if (remixes > 0 && remixes < sources.Count)
        {
            errors.Add(new(SourcesField, RemixWithAudioAction, "Sources of the general Remix type cannot be combined with an audio action."));
            return;
        }

        if (actions.Count == 0)
        {
            return;
        }

        var needed = actions[0] == SunoActions.Mashup ? 2 : 1;
        if (sources.Count > needed || (check == LineageCheck.Complete && sources.Count < needed))
        {
            errors.Add(new(
                SourcesField,
                AudioSourceCount,
                needed == 2 ? "A Mashup has exactly two sources." : "This audio action has exactly one source."));
        }

        if (actions[0] == SunoActions.Cover && (lineage.InspirationSources.Count > 0 || lineage.Playlist is not null))
        {
            errors.Add(new(InspirationField, InspirationWithCover, "Inspiration cannot be combined with Cover."));
        }

        if (lineage.FileInputs.Any(static file => file.Kind == VersionFileInputKind.Audio))
        {
            errors.Add(new(FileInputsField, AudioSlotTaken, "Suno's form has one Audio slot: an audio file cannot go with an audio action."));
        }
    }

    private static void ContinueAtErrors(
        VersionSource source,
        string field,
        LineageCheck check,
        Func<VersionSourceTarget, double?>? durationOf,
        List<LineageError> errors)
    {
        var positionField = field + ".continueAtSeconds";
        if (source.SunoAction != SunoActions.Extend || IsRemix(source))
        {
            if (source.ContinueAtSeconds is not null)
            {
                errors.Add(new(positionField, ContinueAtOnlyExtend, "Only Extend continues from a position."));
            }

            return;
        }

        if (source.ContinueAtSeconds is not { } seconds)
        {
            if (check == LineageCheck.Complete)
            {
                errors.Add(new(positionField, ContinueAtRequired, "Extend needs the position, in seconds, it continues from."));
            }

            return;
        }

        if (seconds < 0 || decimal.Round(seconds, 2) != seconds || seconds > 100_000m)
        {
            errors.Add(new(positionField, ContinueAtInvalid, "The position is zero or more seconds, to at most two decimals."));
            return;
        }

        if (durationOf?.Invoke(source.Target) is { } duration && (double)seconds > duration)
        {
            errors.Add(new(positionField, ContinueAtBeyondSource, string.Create(CultureInfo.InvariantCulture, $"The source is {duration:0.##} seconds long, so Extend cannot continue from later.")));
        }
    }

    private static void InspirationErrors(VersionLineage lineage, List<LineageError> errors)
    {
        var sources = lineage.InspirationSources;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var field = Item(InspirationField + ".sources", index);
            TargetErrors(source.Target, field, errors);
            if (source.TypeId != SystemRelationshipTypes.UseAsInspiration.Id || source.ContinueAtSeconds is not null)
            {
                errors.Add(new(field, InspirationType, "An Inspiration source is of the Use as Inspiration type, with no position."));
            }
        }

        if (sources.Count > InspirationMaximum)
        {
            errors.Add(new(InspirationField, InspirationCount, string.Create(CultureInfo.InvariantCulture, $"Inspiration takes up to {InspirationMaximum} individual sources.")));
        }

        if (sources.Count > 0 && lineage.Playlist is not null)
        {
            errors.Add(new(InspirationField, InspirationBothForms, "Inspiration is either individual sources or one playlist, never both."));
        }

        if (lineage.Playlist is { } playlist)
        {
            if (!ExternalSunoReferenceRules.IsSunoId(playlist.SunoPlaylistId))
            {
                errors.Add(new(InspirationField + ".playlist.sunoPlaylistId", PlaylistIdInvalid, "Give the playlist's Suno ID."));
            }

            if (!IsName(playlist.Name))
            {
                errors.Add(new(InspirationField + ".playlist.name", NameLength, string.Create(CultureInfo.InvariantCulture, $"A playlist's name is one line of up to {NameMaximumLength} characters.")));
            }

            if (playlist.ClipIds.Count > PlaylistClipMaximum || !playlist.ClipIds.All(ExternalSunoReferenceRules.IsSunoId))
            {
                errors.Add(new(InspirationField + ".playlist.clipIds", PlaylistClipCount, string.Create(CultureInfo.InvariantCulture, $"A playlist's snapshot holds 0 to {PlaylistClipMaximum} clip IDs.")));
            }
        }
    }

    private static void VoiceErrors(VersionVoice? voice, List<LineageError> errors)
    {
        if (voice is null)
        {
            return;
        }

        if (!ExternalSunoReferenceRules.IsSunoId(voice.PersonaId))
        {
            errors.Add(new(VoiceField + ".personaId", PersonaIdInvalid, "Give the persona's Suno ID."));
        }

        if (!IsName(voice.Name))
        {
            errors.Add(new(VoiceField + ".name", NameLength, string.Create(CultureInfo.InvariantCulture, $"A persona's name is one line of up to {NameMaximumLength} characters.")));
        }
    }

    private static void FileInputErrors(VersionLineage lineage, VersionKind kind, CreationMode songMode, IReadOnlyCollection<VersionFileInput> kept, List<LineageError> errors)
    {
        var files = lineage.FileInputs;
        if (files.GroupBy(static file => file.Kind).Any(static kind => kind.Count() > 1))
        {
            errors.Add(new(FileInputsField, FileInputRepeated, "A Version notes at most one file of each kind: audio, image, and video."));
        }

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var field = Item(FileInputsField, index);
            if (string.IsNullOrWhiteSpace(file.Description) || file.Description.Length > DescriptionMaximumLength || file.Description.Contains('\0', StringComparison.Ordinal))
            {
                errors.Add(new(field + ".description", FileInputDescription, string.Create(CultureInfo.InvariantCulture, $"Describe the file in 1 to {DescriptionMaximumLength} characters.")));
            }

            if (file.Kind != VersionFileInputKind.Audio && !Applies(file.Kind, kind, songMode) && !kept.Contains(file))
            {
                errors.Add(new(field, FileInputSimpleOnly, "Suno takes an image or a video for a Simple-mode Song only."));
            }
        }
    }

    private static void TargetErrors(VersionSourceTarget target, string field, List<LineageError> errors)
    {
        if (target.Count != 1)
        {
            errors.Add(new(field, OneTarget, "A source points at exactly one Generation, Song, or Suno clip."));
        }
        else if (target.ExternalSunoId is { } sunoId && !ExternalSunoReferenceRules.IsSunoId(sunoId))
        {
            errors.Add(new(field, SunoIdInvalid, "Give the clip's Suno ID."));
        }
    }

    /// <summary>A playlist's or persona's name: may be blank (the ID is shown instead), one line of up to <see cref="NameMaximumLength"/> characters.</summary>
    private static bool IsName(string name) => name is not null && name.Length <= NameMaximumLength && !name.Any(char.IsControl);

    /// <summary>A JSON object of up to <see cref="SecondaryIdsMaximum"/> Suno IDs, each named by a key of up to 50 characters.</summary>
    private static bool IsSecondaryIds(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.EnumerateObject().Count() <= SecondaryIdsMaximum
                && root.EnumerateObject().All(static property =>
                    property.Name.Length is > 0 and <= 50
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && ExternalSunoReferenceRules.IsSunoId(property.Value.GetString()));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Item(string field, int index) => string.Create(CultureInfo.InvariantCulture, $"{field}[{index}]");
}
