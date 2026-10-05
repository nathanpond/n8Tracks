using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Application.Auth;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Suno;

/// <summary>
/// The user's defaults as stored: each option the user set, by its API name, and the revision of the
/// record as a whole (1 until the first change).
/// </summary>
public sealed record StoredVersionDefaults(int Revision, JsonObject Values);

/// <summary>
/// The user's defaults as shown: the revision, each default the user set (<see cref="Defaults"/>; an
/// option missing from it uses Suno's default), and each one no longer valid (<see cref="Ignored"/>,
/// its key and why), which is kept but not applied until it is valid again. <see cref="Offered"/> is
/// the models offered, in order: what a model default may now be.
/// </summary>
public sealed record VersionDefaultsView(int Revision, JsonObject Defaults, IReadOnlyDictionary<string, string> Ignored, IReadOnlyList<string> Offered);

/// <summary>How replacing the defaults ended.</summary>
public abstract record VersionDefaultsOutcome
{
    private VersionDefaultsOutcome()
    {
    }

    /// <summary>The defaults as they are now: replaced, at the next revision, or unchanged when the request changed nothing.</summary>
    public sealed record Updated(VersionDefaultsView Defaults) : VersionDefaultsOutcome;

    /// <summary>The defaults are at another revision than the request was based on. Nothing was changed.</summary>
    public sealed record Conflict(VersionDefaultsView Current) : VersionDefaultsOutcome;

    /// <summary>A default is wrong. Nothing was changed. The errors are keyed <c>defaults.&lt;key&gt;</c>, or <c>defaults</c>.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : VersionDefaultsOutcome;
}

/// <summary>Where the user's defaults are kept. Every write is made inside the caller's transaction, which has checked the revision first.</summary>
public interface IVersionDefaultsStore
{
    /// <summary>The stored defaults, or null when the user has set none yet.</summary>
    Task<StoredVersionDefaults?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Writes the defaults, replacing any.</summary>
    Task WriteAsync(StoredVersionDefaults defaults, CancellationToken cancellationToken);
}

/// <summary>
/// The user's own starting values for a new Song's Version 1, over Suno's defaults from the
/// inventory. One record holds them, with one revision. A default may be set for each option of
/// <see cref="VersionInputRules.DefaultableKeys"/> (the kind, both modes, both models, and every
/// choice, toggle, range, and number of the three kinds); an option without one uses the inventory's
/// default, and the models the first model offered. A value is checked as a Version's option is,
/// except that a model must be offered (not retired). A default that is no longer valid (a model
/// retired since, a value a re-captured inventory no longer allows) is kept, reported, and skipped
/// when a Song is created, until it is valid again. Defaults are applied once, when a Song is created,
/// so changing them never changes an existing Version.
/// </summary>
public sealed class VersionDefaultsService(IVersionDefaultsStore store, ISunoModelList models, IExclusiveTransaction transaction)
{
    /// <summary>The field a request sends the defaults in, and the prefix of each default's error key.</summary>
    public const string DefaultsField = "defaults";

    /// <summary>The defaults, every one checked: those no longer valid are in <see cref="VersionDefaultsView.Ignored"/>.</summary>
    public async Task<VersionDefaultsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the defaults with <paramref name="defaults"/>, given the revision the caller read. A key
    /// left out goes back to Suno's default. Each key must take a default and each value be valid, by
    /// the inventory and the offered models, except a value the record already holds, which is kept as
    /// it is even when no longer valid (so saving other changes never drops it). A stale revision
    /// changes nothing and answers the defaults as they are now; a request that changes nothing is not
    /// written.
    /// </summary>
    public Task<VersionDefaultsOutcome> UpdateAsync(IReadOnlyDictionary<string, JsonElement>? defaults, int revision, CancellationToken cancellationToken)
    {
        if (defaults is null)
        {
            return Task.FromResult<VersionDefaultsOutcome>(new VersionDefaultsOutcome.Invalid(
                new Dictionary<string, string[]>(StringComparer.Ordinal) { [DefaultsField] = ["Send an object of defaults, keyed by option."] }));
        }

        return transaction.RunAsync<VersionDefaultsOutcome>(
            async ct =>
            {
                var current = await ReadAsync(ct).ConfigureAwait(false);
                if (current.Revision != revision)
                {
                    return new VersionDefaultsOutcome.Conflict(await ViewAsync(current, ct).ConfigureAwait(false));
                }

                var offered = await models.OfferedAsync(ct).ConfigureAwait(false);
                var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
                var values = new JsonObject();
                foreach (var key in VersionInputRules.DefaultableKeys.Concat(defaults.Keys.Where(static key => !VersionInputRules.DefaultableKeys.Contains(key, StringComparer.Ordinal))))
                {
                    if (!defaults.TryGetValue(key, out var value))
                    {
                        continue;
                    }

                    if (!VersionInputRules.DefaultableKeys.Contains(key, StringComparer.Ordinal))
                    {
                        errors[FieldName(key)] = [VersionInputRules.Keys.Contains(key, StringComparer.Ordinal)
                            ? $"'{key}' has no default: text and the Version's own sections are written for each Version."
                            : $"'{key}' is not an option of a Version."];
                        continue;
                    }

                    var node = Node(value);
                    var kept = current.Values.TryGetPropertyValue(key, out var held) && JsonNode.DeepEquals(held, node);
                    if (!kept && Problems(key, value, offered) is { Length: > 0 } problems)
                    {
                        errors[FieldName(key)] = problems;
                        continue;
                    }

                    values[key] = node;
                }

                if (errors.Count > 0)
                {
                    return new VersionDefaultsOutcome.Invalid(errors);
                }

                if (JsonNode.DeepEquals(values, current.Values))
                {
                    return new VersionDefaultsOutcome.Updated(await ViewAsync(current, ct).ConfigureAwait(false));
                }

                var replaced = new StoredVersionDefaults(current.Revision + 1, values);
                await store.WriteAsync(replaced, ct).ConfigureAwait(false);
                return new VersionDefaultsOutcome.Updated(await ViewAsync(replaced, ct).ConfigureAwait(false));
            },
            cancellationToken);
    }

    /// <summary>
    /// The options a new Song's Version 1 starts with, for every kind: the inventory's defaults (with
    /// Suno's title pre-filled with <paramref name="songTitle"/>), the first model offered as both
    /// models, then each of the user's defaults that is valid. Runs inside the caller's transaction.
    /// </summary>
    public async Task<VersionInputs> NewVersionInputsAsync(string songTitle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(songTitle);

        var offered = await models.OfferedAsync(cancellationToken).ConfigureAwait(false);
        var first = offered.FirstOrDefault();
        var inputs = VersionInputRules.Defaults(CreateFieldInventory.Embedded, songTitle) with
        {
            Model = first,
            SoundsModel = first,
        };

        var stored = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var valid = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, node) in stored.Values)
        {
            var value = Element(node);
            if (VersionInputRules.DefaultableKeys.Contains(key, StringComparer.Ordinal) && Problems(key, value, offered).Length == 0)
            {
                valid[key] = value;
            }
        }

        return VersionInputRules.Apply(inputs, valid);
    }

    private async Task<StoredVersionDefaults> ReadAsync(CancellationToken cancellationToken) =>
        await store.FindAsync(cancellationToken).ConfigureAwait(false) ?? new StoredVersionDefaults(1, new JsonObject());

    private async Task<VersionDefaultsView> ViewAsync(StoredVersionDefaults stored, CancellationToken cancellationToken)
    {
        var offered = await models.OfferedAsync(cancellationToken).ConfigureAwait(false);
        var ignored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in stored.Values)
        {
            if (!VersionInputRules.DefaultableKeys.Contains(key, StringComparer.Ordinal))
            {
                ignored[key] = "This option no longer takes a default, so new Songs do not use it.";
            }
            else if (Problems(key, Element(node), offered) is { Length: > 0 } problems)
            {
                ignored[key] = VersionInputRules.IsModel(key)
                    ? $"{(node is JsonValue text && text.TryGetValue<string>(out var name) ? name : node?.ToJsonString())} is retired or no longer on the model list, so new Songs start with the first model offered."
                    : $"No longer valid ({string.Join(" ", problems)}), so new Songs use Suno's default.";
            }
        }

        return new VersionDefaultsView(stored.Revision, (JsonObject)stored.Values.DeepClone(), ignored, offered);
    }

    /// <summary>What is wrong with <paramref name="value"/> as the default of <paramref name="key"/>; a model must be one of <paramref name="offered"/>.</summary>
    private static string[] Problems(string key, JsonElement value, IReadOnlyList<string> offered) =>
        VersionInputRules.Errors(
                CreateFieldInventory.Embedded,
                [.. offered],
                new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [key] = value })
            .Values.SelectMany(static messages => messages).ToArray();

    private static string FieldName(string key) => DefaultsField + "." + key;

    private static JsonNode? Node(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(element.GetRawText());

    private static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(node);
}
