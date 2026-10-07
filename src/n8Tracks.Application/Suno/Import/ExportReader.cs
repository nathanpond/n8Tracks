using System.Globalization;
using System.Text.Json;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>A playlist as an export names it: Suno's ID, its name, and the Suno IDs of its clips.</summary>
public sealed record ExportPlaylist(string Id, string? Name, IReadOnlyList<string> ClipIds);

/// <summary>What reading an export's header gave.</summary>
public abstract record ExportHeaderReading
{
    private ExportHeaderReading()
    {
    }

    /// <summary>A header n8Tracks can keep, and the workspaces it names (applied at once only when the list is complete).</summary>
    public sealed record Read(SunoExportHeader Header, IReadOnlyList<SunoWorkspaceSighting> Workspaces) : ExportHeaderReading;

    /// <summary>A <c>formatVersion</c> this n8Tracks does not understand.</summary>
    public sealed record UnsupportedFormat(int FormatVersion) : ExportHeaderReading;

    /// <summary>Not a header n8Tracks can keep; errors are keyed by field.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ExportHeaderReading;
}

/// <summary>What reading one part of an export gave.</summary>
public abstract record ExportPartReading
{
    private ExportPartReading()
    {
    }

    /// <summary>The part: its number, its clips (library copies first, then Trash copies, each list in order), and its playlists.</summary>
    public sealed record Read(int PartNumber, IReadOnlyList<StagedClip> Clips, IReadOnlyList<ExportPlaylist> Playlists) : ExportPartReading;

    /// <summary>More clips than one part may carry.</summary>
    public sealed record TooManyClips(int Count) : ExportPartReading;

    /// <summary>Not a part n8Tracks can keep; errors are keyed by field (<c>clips[3].id</c>).</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ExportPartReading;
}

/// <summary>
/// Reads an export's header and parts as the extension sends them (<c>docs/suno-integration.md</c>,
/// Export format). Raw clips and projects are kept as the text received, never re-serialised. No field
/// value is ever quoted in an error, so nothing a clip holds can reach a log or an answer that way.
/// </summary>
public static class ExportReader
{
    public const string FormatField = "format";
    public const string FormatVersionField = "formatVersion";
    public const string ExtensionVersionField = "extensionVersion";
    public const string AdapterVersionField = "adapterVersion";
    public const string CapturedAtField = "capturedAt";
    public const string ScopeField = "scope";
    public const string LibraryCompleteField = "libraryComplete";
    public const string TrashedCompleteField = "trashedComplete";
    public const string WorkspacesCompleteField = "workspacesComplete";
    public const string WorkspacesField = "workspaces";
    public const string PlaylistsField = "playlists";
    public const string ClipsField = "clips";
    public const string TrashedClipsField = "trashedClips";
    public const string PartNumberField = "partNumber";

    /// <summary>The longest version text kept.</summary>
    public const int VersionMaximumLength = 100;

    /// <summary>The most IDs a scope may name, and the most clip IDs one playlist may list.</summary>
    public const int MaximumIds = SunoExportRules.MaximumRecords;

    /// <summary>The most playlists one header or part may name.</summary>
    public const int MaximumPlaylists = 1_000;

    /// <summary>The longest playlist name kept.</summary>
    public const int PlaylistNameMaximumLength = 500;

    /// <summary>Reads an export's header.</summary>
    public static ExportHeaderReading ReadHeader(JsonElement root)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send the export's header as a JSON object."];
            return new ExportHeaderReading.Invalid(errors);
        }

        if (!root.TryGetProperty(FormatField, out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != SunoExportRules.Format)
        {
            errors[FormatField] = [$"Send \"{SunoExportRules.Format}\"."];
        }

        if (!root.TryGetProperty(FormatVersionField, out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var formatVersion))
        {
            errors[FormatVersionField] = ["Send the export format's version as a whole number."];
        }
        else if (formatVersion != SunoExportRules.FormatVersion && errors.Count == 0)
        {
            return new ExportHeaderReading.UnsupportedFormat(formatVersion);
        }

        var extensionVersion = VersionText(root, ExtensionVersionField, errors);
        var adapterVersion = VersionText(root, AdapterVersionField, errors);

        DateTimeOffset captured = default;
        if (!root.TryGetProperty(CapturedAtField, out var capturedAt) || capturedAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(capturedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out captured))
        {
            errors[CapturedAtField] = ["Send when the records were captured, as an ISO 8601 time."];
        }

        var (scope, scopeIds) = ReadScope(root, errors);
        var libraryComplete = Flag(root, LibraryCompleteField, required: true, errors);
        var trashedComplete = Flag(root, TrashedCompleteField, required: true, errors);
        var workspacesComplete = Flag(root, WorkspacesCompleteField, required: false, errors);

        IReadOnlyList<SunoWorkspaceSighting> sightings = [];
        var workspacesJson = "[]";
        if (root.TryGetProperty(WorkspacesField, out var workspaces) && workspaces.ValueKind != JsonValueKind.Null)
        {
            switch (SunoWorkspaceService.ReadReport(workspaces))
            {
                case SunoWorkspaceReportReading.Read read:
                    sightings = read.Sightings;
                    workspacesJson = workspaces.GetRawText();
                    break;
                case SunoWorkspaceReportReading.Invalid invalid:
                    foreach (var (field, messages) in invalid.Errors)
                    {
                        errors[field] = messages;
                    }

                    break;
            }
        }

        var playlists = ReadPlaylists(root, string.Empty, errors);
        foreach (var list in new[] { ClipsField, TrashedClipsField })
        {
            if (root.TryGetProperty(list, out var clips) && !(clips.ValueKind is JsonValueKind.Null || (clips.ValueKind == JsonValueKind.Array && clips.GetArrayLength() == 0)))
            {
                errors[list] = ["Send the clips in parts (POST /api/v1/suno/exports/{id}/parts), not with the header."];
            }
        }

        if (errors.Count > 0)
        {
            return new ExportHeaderReading.Invalid(errors);
        }

        return new ExportHeaderReading.Read(
            new SunoExportHeader(
                extensionVersion,
                adapterVersion,
                captured.ToUniversalTime(),
                scope!,
                scopeIds,
                libraryComplete,
                trashedComplete,
                workspacesComplete,
                workspacesJson,
                PlaylistsJson(playlists)),
            sightings);
    }

    /// <summary>
    /// Reads a part: <c>{ partNumber, clips?, trashedClips?, playlists? }</c>. Each clip must be an object
    /// with a string <c>id</c> (read by <see cref="ClipReader"/>); one that is not refuses the whole part.
    /// </summary>
    public static ExportPartReading ReadPart(JsonElement root)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send the part as a JSON object."];
            return new ExportPartReading.Invalid(errors);
        }

        var partNumber = 0;
        if (!root.TryGetProperty(PartNumberField, out var number) || number.ValueKind != JsonValueKind.Number
            || !number.TryGetInt32(out partNumber) || partNumber < 1 || partNumber > SunoExportRules.MaximumPartNumber)
        {
            errors[PartNumberField] = [string.Create(CultureInfo.InvariantCulture, $"Send the part's number, a whole number from 1 to {SunoExportRules.MaximumPartNumber}.")];
        }

        var count = 0;
        foreach (var list in new[] { ClipsField, TrashedClipsField })
        {
            if (root.TryGetProperty(list, out var clips) && clips.ValueKind == JsonValueKind.Array)
            {
                count += clips.GetArrayLength();
            }
        }

        if (count > SunoExportRules.MaximumClipsPerPart)
        {
            return new ExportPartReading.TooManyClips(count);
        }

        var staged = new List<StagedClip>();
        ReadClips(root, ClipsField, trashed: false, staged, errors);
        ReadClips(root, TrashedClipsField, trashed: true, staged, errors);
        var playlists = ReadPlaylists(root, string.Empty, errors);

        return errors.Count > 0
            ? new ExportPartReading.Invalid(errors)
            : new ExportPartReading.Read(partNumber, staged, playlists);
    }

    /// <summary>The playlists of a stored header's <see cref="SunoExportHeader.PlaylistsJson"/>.</summary>
    public static IReadOnlyList<ExportPlaylist> PlaylistsOf(string playlistsJson)
    {
        using var document = JsonDocument.Parse(playlistsJson);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        return ReadPlaylistList(document.RootElement, PlaylistsField, errors);
    }

    /// <summary>The playlists as stored: <c>[{ id, name, clipIds }]</c>.</summary>
    public static string PlaylistsJson(IReadOnlyList<ExportPlaylist> playlists) =>
        JsonSerializer.Serialize(playlists.Select(static playlist => new { id = playlist.Id, name = playlist.Name, clipIds = playlist.ClipIds }));

    private static void ReadClips(JsonElement root, string list, bool trashed, List<StagedClip> staged, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(list, out var clips) || clips.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (clips.ValueKind != JsonValueKind.Array)
        {
            errors[list] = ["Send a list of Suno's raw clip objects."];
            return;
        }

        var index = 0;
        foreach (var clip in clips.EnumerateArray())
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"{list}[{index++}]");
            if (clip.ValueKind != JsonValueKind.Object)
            {
                errors[field] = ["Send each clip as Suno's raw clip object."];
                continue;
            }

            if (!clip.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                errors[field + ".id"] = ["Every clip needs its Suno ID: a string \"id\"."];
                continue;
            }

            switch (ClipReader.Read(clip.GetRawText()))
            {
                case ClipReading.Read read:
                    staged.Add(new StagedClip(read.Fields.SunoId, read.Raw, trashed, read.Fields, UnknownKind: !ClipInputMapper.KindOf(clip).Determined));
                    break;
                case ClipReading.Invalid invalid:
                    errors[field] = [invalid.Reason];
                    break;
            }
        }
    }

    private static List<ExportPlaylist> ReadPlaylists(JsonElement root, string prefix, Dictionary<string, string[]> errors) =>
        root.TryGetProperty(PlaylistsField, out var playlists) && playlists.ValueKind != JsonValueKind.Null
            ? ReadPlaylistList(playlists, prefix + PlaylistsField, errors)
            : [];

    private static List<ExportPlaylist> ReadPlaylistList(JsonElement playlists, string field, Dictionary<string, string[]> errors)
    {
        var read = new List<ExportPlaylist>();
        if (playlists.ValueKind != JsonValueKind.Array || playlists.GetArrayLength() > MaximumPlaylists)
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"Send at most {MaximumPlaylists} playlists, each {{ id, name, clipIds }}.")];
            return read;
        }

        var index = 0;
        foreach (var playlist in playlists.EnumerateArray())
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{field}[{index++}]");
            if (playlist.ValueKind != JsonValueKind.Object
                || !playlist.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 and <= ClipReader.MaximumIdLength } playlistId)
            {
                errors[at + ".id"] = [string.Create(CultureInfo.InvariantCulture, $"Every playlist needs its Suno ID: text of 1 to {ClipReader.MaximumIdLength} characters.")];
                continue;
            }

            string? name = null;
            if (playlist.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                name = nameValue.GetString();
                if (name!.Length > PlaylistNameMaximumLength)
                {
                    errors[at + ".name"] = [string.Create(CultureInfo.InvariantCulture, $"A playlist's name is at most {PlaylistNameMaximumLength} characters.")];
                    continue;
                }
            }

            if (Ids(playlist, "clipIds", at + ".clipIds", errors) is { } clipIds)
            {
                read.Add(new ExportPlaylist(playlistId, name, clipIds));
            }
        }

        return read;
    }

    private static (string? Scope, IReadOnlyList<string> Ids) ReadScope(JsonElement root, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(ScopeField, out var scope) || scope.ValueKind != JsonValueKind.Object
            || !scope.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !SunoExportRules.Scopes.Contains(kind.GetString()!, StringComparer.Ordinal))
        {
            errors[ScopeField + ".kind"] = [$"Send what was read: one of {string.Join(", ", SunoExportRules.Scopes)}."];
            return (null, []);
        }

        return (kind.GetString(), Ids(scope, "ids", ScopeField + ".ids", errors) ?? []);
    }

    /// <summary>An optional list of Suno IDs; null with an error when it is not one.</summary>
    private static List<string>? Ids(JsonElement owner, string name, string field, Dictionary<string, string[]> errors)
    {
        if (!owner.TryGetProperty(name, out var ids) || ids.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MaximumIds
            || ids.EnumerateArray().Any(static id => id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= ClipReader.MaximumIdLength }))
        {
            errors[field] = [string.Create(CultureInfo.InvariantCulture, $"Send a list of at most {MaximumIds} Suno IDs, each text of 1 to {ClipReader.MaximumIdLength} characters.")];
            return null;
        }

        return [.. ids.EnumerateArray().Select(static id => id.GetString()!)];
    }

    private static bool Flag(JsonElement root, string name, bool required, Dictionary<string, string[]> errors)
    {
        if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (required || (root.TryGetProperty(name, out var present) && present.ValueKind != JsonValueKind.Null))
        {
            errors[name] = ["Send true or false."];
        }

        return false;
    }

    /// <summary>A version as text (the adapter's is a number); null when absent; an error when too long or of another type.</summary>
    private static string? VersionText(JsonElement root, string name, Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        if (text is null || text.Length > VersionMaximumLength)
        {
            errors[name] = [string.Create(CultureInfo.InvariantCulture, $"Send the version as text of at most {VersionMaximumLength} characters.")];
            return null;
        }

        return text;
    }
}
