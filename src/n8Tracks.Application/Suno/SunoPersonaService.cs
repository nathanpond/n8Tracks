using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>What reading a voice report gave.</summary>
public abstract record SunoPersonaReportReading
{
    private SunoPersonaReportReading()
    {
    }

    /// <summary>The listings, one per Suno ID (a repeated ID keeps the last), in the order first named.</summary>
    public sealed record Read(IReadOnlyList<SunoPersonaListing> Listings) : SunoPersonaReportReading;

    /// <summary>The report cannot be kept; nothing is stored. Errors are keyed by field (<c>voices[2].id</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SunoPersonaReportReading;
}

/// <summary>What a voice report changed: Suno IDs of the personas added, renamed, and whose state changed.</summary>
public sealed record SunoPersonaReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>
/// The user's Suno voices as a sync reads them (#439): the extension reports the personas of Suno's voice
/// list (<c>GET /api/persona/get-personas/</c>), and each is kept in <c>suno_personas</c> by its Suno ID
/// with its name, description, ownership, and whether it is Available. The rules mirror the workspace
/// list's (<see cref="SunoWorkspaceService"/>): only a complete list changes availability. A persona it
/// leaves out, or names as trashed, becomes Unavailable, and one it lists untrashed is Available again.
/// Nothing is deleted, and no Version changes: a Version's voice keeps its persona ID and name. These are
/// Suno's own records, a read model, not catalog data: no choice is asked of the user (invariant 3 is about
/// the catalog), and imports keep adding the personas their clips used as before.
/// </summary>
public sealed class SunoPersonaService(ISunoLibraryStore store, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The fields a report's errors are keyed by, as the API spells them.</summary>
    public const string CompleteField = "complete";
    public const string VoicesField = "voices";

    /// <summary>
    /// Stores what a report names, in one transaction: each persona is added or updated by
    /// <see cref="SunoPersonaRules.Seen"/>, and when <paramref name="complete"/> every known persona it
    /// leaves out becomes Unavailable.
    /// </summary>
    public Task<SunoPersonaReport> ReportAsync(IReadOnlyList<SunoPersonaListing> listings, bool complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listings);

        return transaction.RunAsync(ct => RecordAsync(listings, complete, ct), cancellationToken);
    }

    private async Task<SunoPersonaReport> RecordAsync(IReadOnlyList<SunoPersonaListing> listings, bool complete, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var known = (await store.PersonasAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static persona => persona.SunoId, StringComparer.Ordinal);
        List<string> added = [], renamed = [], unavailable = [], available = [];
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            listed.Add(listing.SunoId);
            var before = known.GetValueOrDefault(listing.SunoId);
            var after = SunoPersonaRules.Seen(before, listing, complete, now);
            await store.SavePersonaAsync(after, cancellationToken).ConfigureAwait(false);
            known[after.SunoId] = after;
            if (before is null)
            {
                added.Add(after.SunoId);
                continue;
            }

            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            {
                renamed.Add(after.SunoId);
            }

            Changed(before, after, unavailable, available);
        }

        if (complete)
        {
            foreach (var missing in known.Values.Where(persona => !listed.Contains(persona.SunoId)).ToList())
            {
                var after = SunoPersonaRules.Unlisted(missing);
                if (after != missing)
                {
                    await store.SavePersonaAsync(after, cancellationToken).ConfigureAwait(false);
                    Changed(missing, after, unavailable, available);
                }
            }
        }

        return new SunoPersonaReport(added, renamed, unavailable, available);
    }

    /// <summary>
    /// Reads the <c>voices</c> of a report: a list of Suno's persona objects, of which <c>id</c> (text, 1
    /// to <see cref="SunoPersonaRules.SunoIdMaximumLength"/> characters), <c>name</c>, <c>description</c>,
    /// <c>is_trashed</c>, and <c>is_owned</c> are read; every other field is ignored and nothing of it is
    /// kept. An entry without an ID refuses the whole report. A repeated ID keeps the last.
    /// </summary>
    public static SunoPersonaReportReading ReadReport(JsonElement voices)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (voices.ValueKind != JsonValueKind.Array)
        {
            errors[VoicesField] = ["Send the voices as a list of Suno's persona objects."];
            return new SunoPersonaReportReading.Invalid(errors);
        }

        if (voices.GetArrayLength() > SunoPersonaRules.MaximumReported)
        {
            errors[VoicesField] = [string.Create(CultureInfo.InvariantCulture, $"Report at most {SunoPersonaRules.MaximumReported} voices at once.")];
            return new SunoPersonaReportReading.Invalid(errors);
        }

        var order = new List<string>();
        var listings = new Dictionary<string, SunoPersonaListing>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in voices.EnumerateArray())
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"{VoicesField}[{index++}]");
            if (Read(entry, field, errors) is not { } listing)
            {
                continue;
            }

            if (!listings.ContainsKey(listing.SunoId))
            {
                order.Add(listing.SunoId);
            }

            listings[listing.SunoId] = listing;
        }

        return errors.Count > 0
            ? new SunoPersonaReportReading.Invalid(errors)
            : new SunoPersonaReportReading.Read([.. order.Select(id => listings[id])]);
    }

    /// <summary>One persona object, or null with its errors added under <paramref name="field"/>.</summary>
    private static SunoPersonaListing? Read(JsonElement entry, string field, Dictionary<string, string[]> errors)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            errors[field] = ["Send each voice as Suno's persona object."];
            return null;
        }

        if (entry.GetRawText().Length > SunoPersonaRules.RawJsonMaximumLength)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"A voice's persona object is at most {SunoPersonaRules.RawJsonMaximumLength} characters.")];
            return null;
        }

        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || id.GetString()!.Length > SunoPersonaRules.SunoIdMaximumLength)
        {
            errors[field + ".id"] = [string.Create(CultureInfo.InvariantCulture, $"Every voice needs its Suno ID: text of 1 to {SunoPersonaRules.SunoIdMaximumLength} characters.")];
            return null;
        }

        var name = Text(entry, "name", SunoPersonaRules.NameMaximumLength, field, errors);
        var description = Text(entry, "description", SunoPersonaRules.DescriptionMaximumLength, field, errors);
        var trashed = entry.TryGetProperty("is_trashed", out var flag) && flag.ValueKind == JsonValueKind.True;
        bool? owned = entry.TryGetProperty("is_owned", out var own) && own.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? own.ValueKind == JsonValueKind.True
            : null;
        return errors.ContainsKey(field + ".name") || errors.ContainsKey(field + ".description")
            ? null
            : new SunoPersonaListing(id.GetString()!, name, description, trashed, owned);
    }

    /// <summary>A text field of a persona: null when missing or not text; an error when longer than <paramref name="limit"/>.</summary>
    private static string? Text(JsonElement entry, string name, int limit, string field, Dictionary<string, string[]> errors)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()!;
        if (text.Length > limit)
        {
            errors[$"{field}.{name}"] = [string.Create(CultureInfo.InvariantCulture, $"A voice's {name} is at most {limit} characters.")];
            return null;
        }

        return text;
    }

    private static void Changed(SunoPersona before, SunoPersona after, List<string> unavailable, List<string> available)
    {
        if (before.State == after.State)
        {
            return;
        }

        (after.State == SunoPersonaState.Unavailable ? unavailable : available).Add(after.SunoId);
    }
}
