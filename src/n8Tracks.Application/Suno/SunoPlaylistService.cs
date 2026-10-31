using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>What reading a playlist report gave.</summary>
public abstract record SunoPlaylistReportReading
{
    private SunoPlaylistReportReading()
    {
    }

    /// <summary>The listings, one per Suno ID (a repeated ID keeps the last), in the order first named.</summary>
    public sealed record Read(IReadOnlyList<SunoPlaylistListing> Listings) : SunoPlaylistReportReading;

    /// <summary>The report cannot be kept; nothing is stored. Errors are keyed by field (<c>playlists[2].id</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SunoPlaylistReportReading;
}

/// <summary>What a playlist report changed: Suno IDs of the playlists added, renamed, and whose state changed.</summary>
public sealed record SunoPlaylistReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>
/// The user's Suno playlists as a sync reads them (#449): the extension reports the playlists of Suno's
/// playlist list (<c>GET /api/playlist/me</c>, on Library's Playlists tab), and each is kept in
/// <c>suno_playlists</c> by its Suno ID with its name, the song count Suno gives, and whether it is
/// Available. The rules mirror the voice list's (<see cref="SunoPersonaService"/>): only a complete list
/// changes availability. A playlist it leaves out, or names as trashed, becomes Unavailable, and one it
/// lists untrashed is Available again. Nothing is deleted, no Version changes (a Version keeps its own
/// snapshot of a playlist), and a playlist's members never change here: those are recorded only by an
/// import commit, from what the user confirmed in the review (#153, #449; invariant 3).
/// </summary>
public sealed class SunoPlaylistService(ISunoLibraryStore store, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The fields a report's errors are keyed by, as the API spells them.</summary>
    public const string CompleteField = "complete";
    public const string PlaylistsField = "playlists";

    /// <summary>
    /// Stores what a report names, in one transaction: each playlist is added or updated by
    /// <see cref="SunoPlaylistRules.Seen"/>, and when <paramref name="complete"/> every known playlist it
    /// leaves out becomes Unavailable.
    /// </summary>
    public Task<SunoPlaylistReport> ReportAsync(IReadOnlyList<SunoPlaylistListing> listings, bool complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listings);

        return transaction.RunAsync(ct => RecordAsync(listings, complete, ct), cancellationToken);
    }

    private async Task<SunoPlaylistReport> RecordAsync(IReadOnlyList<SunoPlaylistListing> listings, bool complete, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var known = (await store.PlaylistsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static playlist => playlist.SunoId, StringComparer.Ordinal);
        List<string> added = [], renamed = [], unavailable = [], available = [];
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listing in listings)
        {
            listed.Add(listing.SunoId);
            var before = known.GetValueOrDefault(listing.SunoId);
            var after = SunoPlaylistRules.Seen(before, listing, complete, now);
            await store.SavePlaylistAsync(after, cancellationToken).ConfigureAwait(false);
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
            foreach (var missing in known.Values.Where(playlist => !listed.Contains(playlist.SunoId)).ToList())
            {
                var after = SunoPlaylistRules.Unlisted(missing);
                if (after != missing)
                {
                    await store.SavePlaylistAsync(after, cancellationToken).ConfigureAwait(false);
                    Changed(missing, after, unavailable, available);
                }
            }
        }

        return new SunoPlaylistReport(added, renamed, unavailable, available);
    }

    /// <summary>
    /// Reads the <c>playlists</c> of a report: a list of Suno's playlist objects, of which <c>id</c> (text,
    /// 1 to <see cref="SunoPlaylistRules.SunoIdMaximumLength"/> characters), <c>name</c>, <c>is_trashed</c>,
    /// and <c>song_count</c> (else <c>num_total_results</c>) are read; every other field is ignored and
    /// nothing of it is kept. An entry without an ID refuses the whole report. A repeated ID keeps the last.
    /// </summary>
    public static SunoPlaylistReportReading ReadReport(JsonElement playlists)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (playlists.ValueKind != JsonValueKind.Array)
        {
            errors[PlaylistsField] = ["Send the playlists as a list of Suno's playlist objects."];
            return new SunoPlaylistReportReading.Invalid(errors);
        }

        if (playlists.GetArrayLength() > SunoPlaylistRules.MaximumReported)
        {
            errors[PlaylistsField] = [string.Create(CultureInfo.InvariantCulture, $"Report at most {SunoPlaylistRules.MaximumReported} playlists at once.")];
            return new SunoPlaylistReportReading.Invalid(errors);
        }

        var order = new List<string>();
        var listings = new Dictionary<string, SunoPlaylistListing>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in playlists.EnumerateArray())
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"{PlaylistsField}[{index++}]");
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
            ? new SunoPlaylistReportReading.Invalid(errors)
            : new SunoPlaylistReportReading.Read([.. order.Select(id => listings[id])]);
    }

    /// <summary>One playlist object, or null with its errors added under <paramref name="field"/>.</summary>
    private static SunoPlaylistListing? Read(JsonElement entry, string field, Dictionary<string, string[]> errors)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            errors[field] = ["Send each playlist as Suno's playlist object."];
            return null;
        }

        if (entry.GetRawText().Length > SunoPlaylistRules.RawJsonMaximumLength)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"A playlist object is at most {SunoPlaylistRules.RawJsonMaximumLength} characters.")];
            return null;
        }

        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || id.GetString()!.Length > SunoPlaylistRules.SunoIdMaximumLength)
        {
            errors[field + ".id"] = [string.Create(CultureInfo.InvariantCulture, $"Every playlist needs its Suno ID: text of 1 to {SunoPlaylistRules.SunoIdMaximumLength} characters.")];
            return null;
        }

        string? name = null;
        if (entry.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
        {
            name = nameValue.GetString()!;
            if (name.Length > SunoPlaylistRules.NameMaximumLength)
            {
                errors[field + ".name"] = [string.Create(CultureInfo.InvariantCulture, $"A playlist's name is at most {SunoPlaylistRules.NameMaximumLength} characters.")];
                return null;
            }
        }

        var trashed = entry.TryGetProperty("is_trashed", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new SunoPlaylistListing(id.GetString()!, name, trashed, Count(entry, "song_count") ?? Count(entry, "num_total_results"));
    }

    /// <summary>A whole, non-negative count the object gives under <paramref name="name"/>, or null.</summary>
    private static int? Count(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0
            ? count
            : null;

    private static void Changed(SunoPlaylist before, SunoPlaylist after, List<string> unavailable, List<string> available)
    {
        if (before.State == after.State)
        {
            return;
        }

        (after.State == SunoPlaylistState.Unavailable ? unavailable : available).Add(after.SunoId);
    }
}
