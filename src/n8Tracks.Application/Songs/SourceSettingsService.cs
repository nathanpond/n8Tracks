using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.References;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Songs;

/// <summary>
/// What taking a source for a Cover, Extend, or Remix would do to a Version's creation settings
/// (#431), worked out against the Version as it is at <paramref name="Revision"/>.
/// <paramref name="Action"/> is the source's action (<c>cover</c>, <c>extend</c>, or <c>remix</c>);
/// null when the sources sent are not one of those, and then nothing is filled.
/// </summary>
/// <param name="Action">The action whose source the settings come from; null when none applies.</param>
/// <param name="Revision">The Version's revision the plan was worked out at: an apply sends it back.</param>
/// <param name="Fills">Settings empty on the Version that the source fills, with no question.</param>
/// <param name="Conflicts">Settings the Version holds that differ from the source's: each is the user's choice.</param>
/// <param name="Unknown">Settings the source's are not known for, and why: these are left as they are.</param>
public sealed record SourceSettingsPlan(
    string? Action,
    int Revision,
    IReadOnlyList<SourceSettingFill> Fills,
    IReadOnlyList<SourceSettingConflict> Conflicts,
    IReadOnlyList<SourceSettingUnknown> Unknown);

/// <summary>A setting the source fills because the Version's is empty: its key, as the API spells it, and the source's value.</summary>
public sealed record SourceSettingFill(string Key, JsonNode? SourceValue);

/// <summary>A setting the Version holds that differs from the source's: its key, the Version's value, and the source's.</summary>
public sealed record SourceSettingConflict(string Key, JsonNode? VersionValue, JsonNode? SourceValue);

/// <summary>A setting whose source value is not known (<see cref="SourceSettingsService"/>'s reasons), so it is left as it is.</summary>
public sealed record SourceSettingUnknown(string Key, string Reason);

/// <summary>How a preview of a source's settings ended.</summary>
public abstract record SourceSettingsPreviewOutcome
{
    private SourceSettingsPreviewOutcome()
    {
    }

    /// <summary>The plan: what would be filled, what differs, and what is not known.</summary>
    public sealed record Planned(SourceSettingsPlan Plan) : SourceSettingsPreviewOutcome;

    /// <summary>There is no Version with that ID.</summary>
    public sealed record NotFound : SourceSettingsPreviewOutcome;

    /// <summary>A Generation is attached, so the Version's settings can no longer change (invariant 1).</summary>
    public sealed record Frozen(VersionDetail Version) : SourceSettingsPreviewOutcome;

    /// <summary>The sources or the choices sent are not well formed. Nothing was changed.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors, IReadOnlyDictionary<string, string[]>? Rules = null) : SourceSettingsPreviewOutcome;
}

/// <summary>
/// A Version's creation settings from the source chosen for its Cover, Extend, or Remix (#431). The
/// source's settings are what produced it: the creation inputs of the Version its Generation belongs
/// to that applied to that Version's kind and mode (<see cref="VersionInputRules.Effective"/>, so what
/// Suno was sent), and its Voice; never the Suno title, the kind, or the modes. A setting the Version
/// leaves empty (blank text, no choice, no Voice) is filled; one it holds that differs is a conflict
/// the user decides; one the source's value is not known for is left as it is and reported:
/// <list type="bullet">
/// <item><see cref="NotImportedReason"/>: a Suno clip n8Tracks has not imported, so none is known;</item>
/// <item><see cref="SongOnlyReason"/>: the source is a Song, not one of its Generations;</item>
/// <item><see cref="DeletedReason"/>: the source Generation is no longer in the catalog;</item>
/// <item><see cref="NotReturnedReason"/>: an imported source whose clip Suno did not return it for;</item>
/// <item><see cref="NotValidReason"/>: a value n8Tracks would refuse (outside its limits, or a model no longer listed).</item>
/// </list>
/// Previewing changes nothing. Applying takes the sources, every fill, and the conflicts the user
/// chose in one edit of the Version (<see cref="VersionService.UpdateAsync(Guid, VersionEdit, int, VersionEditSource, CancellationToken)"/>),
/// so they are written together or not at all, and a frozen Version is refused as any edit is.
/// </summary>
public sealed class SourceSettingsService(
    VersionService service,
    IVersionStore versions,
    IRelationshipStore relationships,
    ISunoModelList models)
{
    /// <summary>The field the sources are sent in, as an edit's <c>inputs.sources</c>.</summary>
    public const string SourcesField = "sources";

    /// <summary>The field the conflicting settings the user takes from the source are sent in.</summary>
    public const string TakeField = "take";

    public const string NotImportedReason = "not_imported";
    public const string SongOnlyReason = "song_only";
    public const string DeletedReason = "deleted";
    public const string NotReturnedReason = "not_returned";
    public const string NotValidReason = "not_valid";

    /// <summary>The actions whose source fills the settings, as the plan names them.</summary>
    public const string CoverAction = SunoActions.Cover;
    public const string ExtendAction = SunoActions.Extend;
    public const string RemixAction = "remix";

    private const string LyricsKey = VersionInputRules.LyricsField;
    private const string StylesKey = VersionInputRules.StylesField;
    private const string VoiceKey = VersionLineageInputs.VoiceKey;

    /// <summary>The options that choose the form or name the result, never copied from a source.</summary>
    private static readonly HashSet<string> NeverCopied = new(StringComparer.Ordinal)
    {
        VersionInputRules.KindKey,
        VersionInputRules.SongModeKey,
        VersionInputRules.SpeechModeKey,
        "title",
    };

    /// <summary>Every setting a source can fill, as the API spells it: the lyrics, the styles, each copied option, and the Voice.</summary>
    public static IReadOnlyList<string> SettingKeys { get; } =
        [LyricsKey, StylesKey, .. VersionInputRules.Keys.Where(static key => !NeverCopied.Contains(key)), VoiceKey];

    /// <summary>
    /// What choosing <paramref name="sources"/> (an edit's <c>inputs.sources</c>, as the Version's whole
    /// audio sources) would do to the settings of the Version with <paramref name="id"/>. Changes nothing.
    /// </summary>
    public async Task<SourceSettingsPreviewOutcome> PreviewAsync(Guid id, JsonElement sources, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var rules = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var edit = VersionLineageInputs.Parse(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [SourcesField] = sources }, errors, rules);
        if (errors.Count > 0)
        {
            return new SourceSettingsPreviewOutcome.Invalid(errors, rules.Count > 0 ? rules : null);
        }

        if (await versions.FindDetailAsync(id, cancellationToken).ConfigureAwait(false) is not { } version)
        {
            return new SourceSettingsPreviewOutcome.NotFound();
        }

        if (version.Summary.IsFrozen)
        {
            return new SourceSettingsPreviewOutcome.Frozen(version);
        }

        return new SourceSettingsPreviewOutcome.Planned(await PlanAsync(version, edit.Sources, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sets <paramref name="sources"/> on the Version with <paramref name="id"/>, if it is still at
    /// <paramref name="revision"/>, with every empty setting filled from the source and each
    /// conflicting one named in <paramref name="take"/> taken from it (the rest keep the Version's
    /// value), in one edit. A key in <paramref name="take"/> that is not a setting is refused; one that
    /// is no longer a conflict is ignored. Sources that are not a Cover, Extend, or Remix are set as an
    /// edit of the sources alone would set them.
    /// </summary>
    public async Task<VersionUpdateOutcome> ApplyAsync(
        Guid id,
        JsonElement sources,
        IReadOnlyCollection<string> take,
        int revision,
        VersionEditSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(take);

        var unknownKeys = take.Where(static key => !SettingKeys.Contains(key, StringComparer.Ordinal)).ToList();
        if (unknownKeys.Count > 0)
        {
            return new VersionUpdateOutcome.Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [TakeField] = [.. unknownKeys.Select(static key => $"'{key}' is not a setting a source can fill.")],
            });
        }

        SourceSettingsPlan plan;
        switch (await PreviewAsync(id, sources, cancellationToken).ConfigureAwait(false))
        {
            case SourceSettingsPreviewOutcome.Planned planned:
                plan = planned.Plan;
                break;
            case SourceSettingsPreviewOutcome.Invalid invalid:
                return new VersionUpdateOutcome.Invalid(invalid.Errors, invalid.Rules);
            case SourceSettingsPreviewOutcome.Frozen frozen:
                return new VersionUpdateOutcome.Frozen(frozen.Version);
            default:
                return new VersionUpdateOutcome.NotFound();
        }

        var taken = new HashSet<string>(take, StringComparer.Ordinal);
        var values = plan.Fills.Select(static fill => (fill.Key, fill.SourceValue))
            .Concat(plan.Conflicts.Where(conflict => taken.Contains(conflict.Key)).Select(static conflict => (conflict.Key, conflict.SourceValue)));

        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [VersionLineageInputs.SourcesKey] = sources.Clone() };
        var lyrics = SongEditField.Unsent;
        var styles = SongEditField.Unsent;
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case LyricsKey:
                    lyrics = SongEditField.Of((string?)value ?? string.Empty);
                    break;
                case StylesKey:
                    styles = SongEditField.Of((string?)value ?? string.Empty);
                    break;
                default:
                    inputs[key] = JsonSerializer.SerializeToElement(value);
                    break;
            }
        }

        // The plan was worked out at the revision the Version had then: the caller must have read that
        // one, and the edit is refused as stale if the Version has changed since.
        if (plan.Revision != revision)
        {
            return await versions.FindDetailAsync(id, cancellationToken).ConfigureAwait(false) is { } current
                ? new VersionUpdateOutcome.Conflict(current)
                : new VersionUpdateOutcome.NotFound();
        }

        var edit = new VersionEdit(SongEditField.Unsent, SongEditField.Unsent, null, lyrics, styles, inputs);
        return await service.UpdateAsync(id, edit, plan.Revision, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The plan for <paramref name="version"/> taking <paramref name="sources"/> as its audio sources.</summary>
    private async Task<SourceSettingsPlan> PlanAsync(VersionDetail version, IReadOnlyList<SourceRequest> sources, CancellationToken cancellationToken)
    {
        var revision = version.Summary.Revision;
        var none = new SourceSettingsPlan(null, revision, [], [], []);
        if (sources.Count == 0 || sources[0].TypeId is not { } typeId
            || await relationships.FindTypeAsync(typeId, cancellationToken).ConfigureAwait(false) is not { } usage)
        {
            return none;
        }

        var type = usage.Type;
        var action = type.Id == SystemRelationshipTypes.Remix.Id ? RemixAction
            : type.SunoAction is CoverAction or ExtendAction ? type.SunoAction
            : null;
        if (action is null)
        {
            return none;
        }

        var current = Settings(version);
        var (sourceVersion, reason) = await SourceVersionAsync(sources[0], version.Summary.Id, cancellationToken).ConfigureAwait(false);
        if (sourceVersion is null)
        {
            if (reason is null)
            {
                return none;
            }

            // Nothing is known of the source: every setting the Version has is left as it is.
            return new SourceSettingsPlan(action, revision, [], [], [.. current.Keys.Select(key => new SourceSettingUnknown(key, reason))]);
        }

        var offered = Settings(sourceVersion);
        var unknown = new List<SourceSettingUnknown>();
        if (sourceVersion.Imported is { } marks)
        {
            foreach (var key in offered.Keys.ToList())
            {
                if (marks.NotReturned.Contains(key, StringComparer.Ordinal))
                {
                    offered.Remove(key);
                    unknown.Add(new(key, NotReturnedReason));
                }
                else if (marks.OutOfRange.Contains(key, StringComparer.Ordinal) || marks.RawValues.ContainsKey(key))
                {
                    offered.Remove(key);
                    unknown.Add(new(key, NotValidReason));
                }
            }
        }

        var listed = await models.ListAsync(cancellationToken).ConfigureAwait(false);
        var fills = new List<SourceSettingFill>();
        var conflicts = new List<SourceSettingConflict>();
        foreach (var (key, value) in offered)
        {
            if (!IsValid(key, value, listed))
            {
                unknown.Add(new(key, NotValidReason));
                continue;
            }

            var held = current.GetValueOrDefault(key);
            if (Same(key, held, value))
            {
                continue;
            }

            if (IsEmpty(held))
            {
                fills.Add(new(key, value?.DeepClone()));
            }
            else
            {
                conflicts.Add(new(key, held?.DeepClone(), value?.DeepClone()));
            }
        }

        return new SourceSettingsPlan(action, revision, fills, conflicts, [.. unknown.OrderBy(static item => IndexOf(item.Key))]);
    }

    /// <summary>
    /// The Version whose settings produced the source <paramref name="request"/> names, or, when there is
    /// none, why its settings are not known (null when the source cannot be one at all: the Version's
    /// own Generation, which the edit refuses).
    /// </summary>
    private async Task<(VersionDetail? Version, string? Reason)> SourceVersionAsync(SourceRequest request, Guid versionId, CancellationToken cancellationToken)
    {
        SourceGenerationFacts? facts;
        if (request.Generation is { } generationReference)
        {
            var reference = CatalogReference.Parse(generationReference);
            var generationId = reference.Kind switch
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
            facts = generationId is { } found ? await versions.FindSourceGenerationAsync(found, cancellationToken).ConfigureAwait(false) : null;
            if (facts is null)
            {
                return (null, DeletedReason);
            }
        }
        else if (request.Song is not null)
        {
            return (null, SongOnlyReason);
        }
        else
        {
            facts = request.External is { } external
                ? await versions.FindSourceGenerationBySunoIdAsync(external.SunoId, cancellationToken).ConfigureAwait(false)
                : null;
            if (facts is null)
            {
                return (null, NotImportedReason);
            }
        }

        if (facts.VersionId == versionId)
        {
            return (null, null);
        }

        var detail = await versions.FindDetailAsync(facts.VersionId, cancellationToken).ConfigureAwait(false);
        return detail is null ? (null, DeletedReason) : (detail, null);
    }

    /// <summary>
    /// A Version's settings a source can fill, by key in <see cref="SettingKeys"/> order: the ones
    /// that apply to its kind and mode (what Suno was sent), and its Voice when it has one.
    /// </summary>
    private static Dictionary<string, JsonNode?> Settings(VersionDetail version)
    {
        var effective = VersionInputRules.Effective(CreateFieldInventory.Embedded, version.Inputs, version.Lyrics, version.Styles);
        var settings = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in SettingKeys)
        {
            if (key == VoiceKey)
            {
                if (VersionLineageRules.AppliesToSong(version.Inputs.Kind))
                {
                    var voice = version.Lineage.Lineage.Voice;
                    settings[key] = voice is null ? null : new JsonObject { ["personaId"] = voice.PersonaId, ["name"] = voice.Name };
                }
            }
            else if (effective.TryGetPropertyValue(key, out var value))
            {
                settings[key] = value?.DeepClone();
            }
        }

        return settings;
    }

    /// <summary>Whether n8Tracks would take <paramref name="value"/> for the setting <paramref name="key"/>.</summary>
    private static bool IsValid(string key, JsonNode? value, IReadOnlyList<string> listed) => key switch
    {
        LyricsKey => value is JsonValue lyrics && lyrics.TryGetValue<string>(out var text) && VersionRules.LyricsErrors(text).Length == 0,
        StylesKey => value is JsonValue styles && styles.TryGetValue<string>(out var text) && VersionRules.StylesErrors(text).Length == 0,
        VoiceKey => true,
        _ => VersionInputRules.Errors(
            CreateFieldInventory.Embedded,
            [.. listed],
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [key] = JsonSerializer.SerializeToElement(value) }).Count == 0,
    };

    /// <summary>Whether a setting holds the same on both sides: the Voice by its persona, the rest by value.</summary>
    private static bool Same(string key, JsonNode? held, JsonNode? offered) =>
        key == VoiceKey
            ? string.Equals((string?)held?["personaId"], (string?)offered?["personaId"], StringComparison.Ordinal)
            : JsonNode.DeepEquals(held, offered);

    /// <summary>Whether a Version's setting is empty: no value, no choice, or blank text. A number or a switch always holds a value.</summary>
    private static bool IsEmpty(JsonNode? value) =>
        value is null || (value is JsonValue text && text.TryGetValue<string>(out var written) && string.IsNullOrWhiteSpace(written));

    private static int IndexOf(string key)
    {
        for (var index = 0; index < SettingKeys.Count; index++)
        {
            if (string.Equals(SettingKeys[index], key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return SettingKeys.Count;
    }
}
