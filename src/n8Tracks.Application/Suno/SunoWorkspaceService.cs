using System.Globalization;
using System.Text.Json;
using n8Tracks.Application.Auth;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno;

/// <summary>A workspace and how many live Songs are in it.</summary>
public sealed record SunoWorkspaceUsage(SunoWorkspace Workspace, int SongCount);

/// <summary>Where Suno workspace records are kept (<c>suno_workspaces</c>).</summary>
public interface ISunoWorkspaceStore
{
    /// <summary>Every workspace record, in no particular order.</summary>
    Task<IReadOnlyList<SunoWorkspace>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The workspace with Suno ID <paramref name="sunoId"/> (compared exactly), or null.</summary>
    Task<SunoWorkspace?> FindAsync(string sunoId, CancellationToken cancellationToken);

    /// <summary>How many live Songs each workspace holds, by Suno ID; a workspace with none is left out.</summary>
    Task<IReadOnlyDictionary<string, int>> SongCountsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="workspace"/>, adding it or replacing the record with its Suno ID, with
    /// <paramref name="rawJson"/> as its raw project (null keeps the one stored). Inside the caller's transaction.
    /// </summary>
    Task SaveAsync(SunoWorkspace workspace, string? rawJson, CancellationToken cancellationToken);
}

/// <summary>What reading a workspace report gave.</summary>
public abstract record SunoWorkspaceReportReading
{
    private SunoWorkspaceReportReading()
    {
    }

    /// <summary>The sightings, one per Suno ID (a repeated ID keeps the last), in the order first named.</summary>
    public sealed record Read(IReadOnlyList<SunoWorkspaceSighting> Sightings) : SunoWorkspaceReportReading;

    /// <summary>The report cannot be kept; nothing is stored. Errors are keyed by field (<c>workspaces[2].id</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SunoWorkspaceReportReading;
}

/// <summary>What a report changed: Suno IDs of the workspaces added, renamed, and whose state changed.</summary>
public sealed record SunoWorkspaceReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>
/// The Suno workspaces n8Tracks knows (#129). The extension reports the user's workspace list; each
/// workspace is kept by its Suno ID with its latest name, when it was last seen, and whether it is
/// Available. Only a complete list changes availability: a workspace it leaves out, or names as
/// trashed, becomes Unavailable, and one it lists untrashed is Available again. Nothing about Songs
/// changes when a workspace becomes Unavailable. Imports (#140) record the workspaces their clips name
/// as an incomplete report. The user never deletes or renames a record here.
/// </summary>
public sealed class SunoWorkspaceService(ISunoWorkspaceStore store, IExclusiveTransaction transaction, TimeProvider time)
{
    /// <summary>The fields a report's errors are keyed by, as the API spells them.</summary>
    public const string CompleteField = "complete";
    public const string WorkspacesField = "workspaces";

    /// <summary>
    /// Every workspace with its live Song count, sorted by the name it is shown by (ignoring case;
    /// blank names as <see cref="SunoWorkspaceRules.UnnamedLabel"/>), then by Suno ID.
    /// </summary>
    public async Task<IReadOnlyList<SunoWorkspaceUsage>> ListAsync(CancellationToken cancellationToken = default)
    {
        var workspaces = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await store.SongCountsAsync(cancellationToken).ConfigureAwait(false);
        return [.. workspaces
            .OrderBy(SunoWorkspaceRules.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(static workspace => workspace.SunoId, StringComparer.Ordinal)
            .Select(workspace => new SunoWorkspaceUsage(workspace, counts.GetValueOrDefault(workspace.SunoId)))];
    }

    /// <summary>
    /// Stores what a report names, in one transaction: each workspace is added or updated by
    /// <see cref="SunoWorkspaceRules.Seen"/>, and when <paramref name="complete"/> every known workspace
    /// it leaves out becomes Unavailable.
    /// </summary>
    public Task<SunoWorkspaceReport> ReportAsync(IReadOnlyList<SunoWorkspaceSighting> sightings, bool complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sightings);

        return transaction.RunAsync(ct => RecordAsync(sightings, complete, ct), cancellationToken);
    }

    /// <summary>The same inside the caller's transaction (an import's commit, #140).</summary>
    internal async Task<SunoWorkspaceReport> RecordAsync(IReadOnlyList<SunoWorkspaceSighting> sightings, bool complete, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var known = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static workspace => workspace.SunoId, StringComparer.Ordinal);
        List<string> added = [], renamed = [], unavailable = [], available = [];
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sighting in sightings)
        {
            listed.Add(sighting.SunoId);
            var before = known.GetValueOrDefault(sighting.SunoId);
            var after = SunoWorkspaceRules.Seen(before, sighting, complete, now);
            await store.SaveAsync(after, sighting.RawJson, cancellationToken).ConfigureAwait(false);
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
            foreach (var missing in known.Values.Where(workspace => !listed.Contains(workspace.SunoId)).ToList())
            {
                var after = SunoWorkspaceRules.Unlisted(missing);
                if (after != missing)
                {
                    await store.SaveAsync(after, rawJson: null, cancellationToken).ConfigureAwait(false);
                    Changed(missing, after, unavailable, available);
                }
            }
        }

        return new SunoWorkspaceReport(added, renamed, unavailable, available);
    }

    /// <summary>
    /// Reads the <c>workspaces</c> of a report: a list of Suno's raw project objects, of which
    /// <c>id</c> (text, 1 to <see cref="SunoWorkspaceRules.SunoIdMaximumLength"/> characters),
    /// <c>name</c>, <c>description</c>, and <c>is_trashed</c> are read; any other field is kept only in
    /// the raw project. An entry without an ID refuses the whole report. A repeated ID keeps the last.
    /// </summary>
    public static SunoWorkspaceReportReading ReadReport(JsonElement workspaces)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (workspaces.ValueKind != JsonValueKind.Array)
        {
            errors[WorkspacesField] = ["Send the workspaces as a list of Suno's project objects."];
            return new SunoWorkspaceReportReading.Invalid(errors);
        }

        if (workspaces.GetArrayLength() > SunoWorkspaceRules.MaximumReported)
        {
            errors[WorkspacesField] = [string.Create(CultureInfo.InvariantCulture, $"Report at most {SunoWorkspaceRules.MaximumReported} workspaces at once.")];
            return new SunoWorkspaceReportReading.Invalid(errors);
        }

        var order = new List<string>();
        var sightings = new Dictionary<string, SunoWorkspaceSighting>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in workspaces.EnumerateArray())
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"{WorkspacesField}[{index++}]");
            if (Read(entry, field, errors) is not { } sighting)
            {
                continue;
            }

            if (!sightings.ContainsKey(sighting.SunoId))
            {
                order.Add(sighting.SunoId);
            }

            sightings[sighting.SunoId] = sighting;
        }

        return errors.Count > 0
            ? new SunoWorkspaceReportReading.Invalid(errors)
            : new SunoWorkspaceReportReading.Read([.. order.Select(id => sightings[id])]);
    }

    /// <summary>One raw project, or null with its errors added under <paramref name="field"/>.</summary>
    private static SunoWorkspaceSighting? Read(JsonElement entry, string field, Dictionary<string, string[]> errors)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            errors[field] = ["Send each workspace as Suno's project object."];
            return null;
        }

        var raw = entry.GetRawText();
        if (raw.Length > SunoWorkspaceRules.RawJsonMaximumLength)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"A workspace's project object is at most {SunoWorkspaceRules.RawJsonMaximumLength} characters.")];
            return null;
        }

        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || id.GetString()!.Length > SunoWorkspaceRules.SunoIdMaximumLength)
        {
            errors[field + ".id"] = [string.Create(CultureInfo.InvariantCulture, $"Every workspace needs its Suno ID: text of 1 to {SunoWorkspaceRules.SunoIdMaximumLength} characters.")];
            return null;
        }

        var name = Text(entry, "name", SunoWorkspaceRules.NameMaximumLength, field, errors);
        var description = Text(entry, "description", SunoWorkspaceRules.DescriptionMaximumLength, field, errors);
        var trashed = entry.TryGetProperty("is_trashed", out var flag) && flag.ValueKind == JsonValueKind.True;
        return errors.ContainsKey(field + ".name") || errors.ContainsKey(field + ".description")
            ? null
            : new SunoWorkspaceSighting(id.GetString()!, name, description, trashed, raw);
    }

    /// <summary>A text field of a project: null when missing or not text; an error when longer than <paramref name="limit"/>.</summary>
    private static string? Text(JsonElement entry, string name, int limit, string field, Dictionary<string, string[]> errors)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()!;
        if (text.Length > limit)
        {
            errors[$"{field}.{name}"] = [string.Create(CultureInfo.InvariantCulture, $"A workspace's {name} is at most {limit} characters.")];
            return null;
        }

        return text;
    }

    private static void Changed(SunoWorkspace before, SunoWorkspace after, List<string> unavailable, List<string> available)
    {
        if (before.State == after.State)
        {
            return;
        }

        (after.State == SunoWorkspaceState.Unavailable ? unavailable : available).Add(after.SunoId);
    }
}
