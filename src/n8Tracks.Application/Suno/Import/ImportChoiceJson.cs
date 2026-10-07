using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Application.Suno.Import;

/// <summary>
/// A choice as the user sends it (#138), before the Song and Versions it names are resolved: the action
/// and, to import, the target with each reference as text (an ID or a shortcode, or a temporary key for
/// a new Song).
/// </summary>
public sealed record ImportChoiceRequest(ImportAction Action, ImportTargetRequest? Target);

/// <summary>A target as sent: its kind (<c>newSong</c>, <c>newVersion</c>, or <c>version</c>) and the fields that kind takes.</summary>
public sealed record ImportTargetRequest(
    string Kind,
    string? Key = null,
    string? Title = null,
    string? WorkspaceId = null,
    string? Song = null,
    string? ParentVersion = null,
    string? Number = null,
    string? Version = null);

/// <summary>
/// A change of choices by filter (#139, "select all that match"): the records matching the filters
/// (class, workspace, playlist, and text in the Suno title), except those named in <paramref name="Except"/>.
/// The server resolves it to Suno IDs, so a selection across pages need not send them.
/// </summary>
public sealed record ChoiceFilter(SunoRecordClass? Class, string? WorkspaceId, string? PlaylistId, string? Search, IReadOnlyList<string> Except);

/// <summary>What reading a change of choices found: the request, or the errors by field.</summary>
public abstract record ChoiceChangeReading
{
    private ChoiceChangeReading()
    {
    }

    /// <summary>
    /// The records named (distinct, in order) and the choice for them; or, when <paramref name="Filter"/>
    /// is set (#139), no IDs and the filter the records are chosen by on the server.
    /// </summary>
    public sealed record Read(IReadOnlyList<string> SunoIds, ImportChoiceRequest Choice, ChoiceFilter? Filter = null) : ChoiceChangeReading;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ChoiceChangeReading;
}

/// <summary>
/// The JSON of choices and proposals, as stored in <c>suno_export_records.choice_json</c> and
/// <c>proposal_json</c> and answered by the records list:
/// <list type="bullet">
/// <item><c>{ "action": "skip" }</c> and <c>{ "action": "ignore" }</c>;</item>
/// <item><c>{ "action": "import", "target": { "kind": "newSong", "key": "new:1", "title", "workspaceId" } }</c>;</item>
/// <item><c>{ "action": "import", "target": { "kind": "newVersion", "key": "new:2", "song": "&lt;Song ID&gt;" | "new:1", "parentVersion": "&lt;Version ID&gt;" | null, "number": "3" } }</c>;</item>
/// <item><c>{ "action": "import", "target": { "kind": "version", "version": "&lt;Version ID&gt;" } }</c>.</item>
/// </list>
/// A proposal is <c>{ "choice", "basis", "group", "freezesVersion" }</c>. A change of choices is
/// <c>{ "sunoIds": [...], "choice": { ... } }</c>, where a Song or Version may also be named by shortcode.
/// </summary>
public static class ImportChoiceJson
{
    public const string ImportName = "import";
    public const string SkipName = "skip";
    public const string IgnoreName = "ignore";
    public const string NewSongKind = "newSong";
    public const string NewVersionKind = "newVersion";
    public const string VersionKind = "version";

    /// <summary>The stored JSON of <paramref name="choice"/>.</summary>
    public static string Write(ImportChoice choice) => Node(choice).ToJsonString();

    /// <summary>The stored JSON of <paramref name="proposal"/>.</summary>
    public static string Write(ImportProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        return new JsonObject
        {
            ["choice"] = Node(proposal.Choice),
            ["basis"] = proposal.Basis,
            ["group"] = proposal.Group,
            ["freezesVersion"] = proposal.FreezesVersion,
        }.ToJsonString();
    }

    /// <summary>The group (Create request) number of a stored proposal (#138); null for a clip alone, or for none.</summary>
    public static int? GroupOf(string? proposalJson)
    {
        if (proposalJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(proposalJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("group", out var group)
            && group.ValueKind == JsonValueKind.Number
            && group.TryGetInt32(out var number)
                ? number
                : null;
    }

    /// <summary>A stored choice; null for none, or for text that is not one (never written by n8Tracks).</summary>
    public static ImportChoice? ReadStored(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ActionOf(root) is not { } action)
        {
            return null;
        }

        if (action != ImportAction.Import)
        {
            return new ImportChoice(action, null);
        }

        var target = root.TryGetProperty("target", out var value) ? value : default;
        ImportTarget? stored = Text(target, "kind") switch
        {
            NewSongKind when Text(target, "key") is { } key && Text(target, "title") is { } title =>
                new ImportTarget.NewSong(key, title, Text(target, "workspaceId")),
            NewVersionKind when Text(target, "key") is { } key && Text(target, "song") is { } song && Text(target, "number") is { } number =>
                new ImportTarget.NewVersion(
                    key,
                    ImportChoiceRules.IsKey(song) ? null : Guid.Parse(song, CultureInfo.InvariantCulture),
                    ImportChoiceRules.IsKey(song) ? song : null,
                    Text(target, "parentVersion") is { } parent ? Guid.Parse(parent, CultureInfo.InvariantCulture) : null,
                    number),
            VersionKind when Text(target, "version") is { } version => new ImportTarget.ExistingVersion(Guid.Parse(version, CultureInfo.InvariantCulture)),
            _ => null,
        };
        return stored is null ? null : ImportChoice.To(stored);
    }

    /// <summary>
    /// Reads a change of choices: <c>{ sunoIds, choice }</c>, with 1 to
    /// <see cref="ImportChoiceRules.MaximumRecordsPerChange"/> Suno IDs (repeats are taken once), or
    /// <c>{ filter: { class?, workspace?, playlist?, q? }, except?, choice }</c> (#139), with at most as many
    /// Suno IDs in <c>except</c>. Unknown members are refused, so a misspelt one is never ignored.
    /// </summary>
    public static ChoiceChangeReading ReadChange(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body", "Send an object: { sunoIds, choice } or { filter, except, choice }.");
        }

        var byFilter = body.TryGetProperty("filter", out var filterElement);
        Unknown(body, "", errors, byFilter ? ["filter", "except", "choice"] : ["sunoIds", "choice"]);
        var ids = new List<string>();
        ChoiceFilter? filter = null;
        if (byFilter)
        {
            filter = ReadFilter(filterElement, body, errors);
        }
        else if (IdList(body, "sunoIds", errors) is { } named)
        {
            ids = named;
            if (ids.Count == 0 || ids.Count > ImportChoiceRules.MaximumRecordsPerChange)
            {
                errors["sunoIds"] = [string.Create(CultureInfo.InvariantCulture, $"Name 1 to {ImportChoiceRules.MaximumRecordsPerChange:N0} records.")];
            }
        }

        ImportChoiceRequest? choice = null;
        if (!body.TryGetProperty("choice", out var choiceElement) || choiceElement.ValueKind != JsonValueKind.Object)
        {
            errors["choice"] = ["Send the choice: { action, target }."];
        }
        else
        {
            choice = ReadChoice(choiceElement, errors);
        }

        return errors.Count == 0 ? new ChoiceChangeReading.Read(ids, choice!, filter) : new ChoiceChangeReading.Invalid(errors);
    }

    /// <summary>The <c>filter</c> and <c>except</c> of a change by filter; null with errors when they are not well formed.</summary>
    private static ChoiceFilter? ReadFilter(JsonElement filter, JsonElement body, Dictionary<string, string[]> errors)
    {
        if (filter.ValueKind != JsonValueKind.Object)
        {
            errors["filter"] = ["Send the filter as an object: { class, workspace, playlist, q }, each optional."];
            return null;
        }

        var before = errors.Count;
        Unknown(filter, "filter.", errors, "class", "workspace", "playlist", "q");
        foreach (var member in filter.EnumerateObject().Where(static member => member.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            errors["filter." + member.Name] = ["Send text, or null."];
        }

        SunoRecordClass? recordClass = null;
        if (Text(filter, "class") is { } className)
        {
            recordClass = SunoExportRules.ClassOf(className);
            if (recordClass is null)
            {
                errors["filter.class"] = ["class is one of new, linked, changed, conflict, ignored, deleted."];
            }
        }

        var except = body.TryGetProperty("except", out _) ? IdList(body, "except", errors) : [];
        if (except is { Count: > ImportChoiceRules.MaximumRecordsPerChange })
        {
            errors["except"] = [string.Create(CultureInfo.InvariantCulture, $"Leave out at most {ImportChoiceRules.MaximumRecordsPerChange:N0} records.")];
        }

        return errors.Count == before
            ? new ChoiceFilter(recordClass, Blank(Text(filter, "workspace")), Blank(Text(filter, "playlist")), Blank(Text(filter, "q")), except!)
            : null;
    }

    /// <summary>A list of Suno IDs (distinct, in order); null with an error when it is not one.</summary>
    private static List<string>? IdList(JsonElement owner, string name, Dictionary<string, string[]> errors)
    {
        if (!owner.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            errors[name] = ["Name the records: a list of Suno IDs."];
            return null;
        }

        var ids = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                errors[name] = ["Each Suno ID is a text that is not empty."];
                return null;
            }

            if (!ids.Contains(item.GetString()!, StringComparer.Ordinal))
            {
                ids.Add(item.GetString()!);
            }
        }

        return ids;
    }

    private static string? Blank(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static ImportChoiceRequest? ReadChoice(JsonElement choice, Dictionary<string, string[]> errors)
    {
        Unknown(choice, "choice.", errors, "action", "target");
        var action = ActionOf(choice);
        if (action is null)
        {
            errors["choice.action"] = ["action is import, skip, or ignore."];
            return null;
        }

        var hasTarget = choice.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null;
        if (action != ImportAction.Import)
        {
            if (hasTarget)
            {
                errors["choice.target"] = ["Only an import has a target."];
            }

            return new ImportChoiceRequest(action.Value, null);
        }

        if (!hasTarget || target.ValueKind != JsonValueKind.Object)
        {
            errors["choice.target"] = ["An import needs a target: { kind, ... }."];
            return null;
        }

        var before = errors.Count;
        var kind = Text(target, "kind");
        string[] members = kind switch
        {
            NewSongKind => ["kind", "key", "title", "workspaceId"],
            NewVersionKind => ["kind", "key", "song", "parentVersion", "number"],
            VersionKind => ["kind", "version"],
            _ => [],
        };
        if (kind is null || members.Length == 0)
        {
            errors["choice.target.kind"] = ["kind is newSong, newVersion, or version."];
            return null;
        }

        Unknown(target, "choice.target.", errors, members);
        foreach (var member in members.Where(static member => member != "kind"))
        {
            if (target.TryGetProperty(member, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                errors["choice.target." + member] = ["Send text, or null."];
            }
        }

        string? Required(string member, string message)
        {
            if (Text(target, member) is { Length: > 0 } text)
            {
                return text;
            }

            errors["choice.target." + member] = [message];
            return null;
        }

        var request = kind switch
        {
            NewSongKind => new ImportTargetRequest(kind, Key: Required("key", "A new Song has a temporary key, new:<n>."), Title: Required("title", "Enter a title."), WorkspaceId: Text(target, "workspaceId")),
            NewVersionKind => new ImportTargetRequest(
                kind,
                Key: Required("key", "A new Version has a temporary key, new:<n>."),
                Song: Required("song", "Name the Song: an ID, a shortcode, or a new Song's key."),
                ParentVersion: Text(target, "parentVersion"),
                Number: Required("number", "Choose the new Version's number.")),
            _ => new ImportTargetRequest(kind, Version: Required("version", "Name the Version: an ID or a shortcode.")),
        };
        if (request.Key is { } key && !ImportChoiceRules.IsKey(key))
        {
            errors["choice.target.key"] = ["A temporary key is new:<n>, with n a whole number from 1."];
        }

        return errors.Count == before ? new ImportChoiceRequest(ImportAction.Import, request) : null;
    }

    private static JsonObject Node(ImportChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var node = new JsonObject { ["action"] = NameOf(choice.Action) };
        if (choice.Target is { } target)
        {
            node["target"] = target switch
            {
                ImportTarget.NewSong song => new JsonObject
                {
                    ["kind"] = NewSongKind,
                    ["key"] = song.Key,
                    ["title"] = song.Title,
                    ["workspaceId"] = song.WorkspaceId,
                },
                ImportTarget.NewVersion version => new JsonObject
                {
                    ["kind"] = NewVersionKind,
                    ["key"] = version.Key,
                    ["song"] = version.NewSongKey ?? IdText(version.SongId!.Value),
                    ["parentVersion"] = version.ParentVersionId is { } parent ? IdText(parent) : null,
                    ["number"] = version.Number,
                },
                ImportTarget.ExistingVersion existing => new JsonObject { ["kind"] = VersionKind, ["version"] = IdText(existing.VersionId) },
                _ => throw new InvalidOperationException("Unknown target."),
            };
        }

        return node;
    }

    private static string NameOf(ImportAction action) => action switch
    {
        ImportAction.Import => ImportName,
        ImportAction.Skip => SkipName,
        _ => IgnoreName,
    };

    private static ImportAction? ActionOf(JsonElement choice) => Text(choice, "action") switch
    {
        ImportName => ImportAction.Import,
        SkipName => ImportAction.Skip,
        IgnoreName => ImportAction.Ignore,
        _ => null,
    };

    private static string IdText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Unknown(JsonElement element, string prefix, Dictionary<string, string[]> errors, params string[] known)
    {
        foreach (var property in element.EnumerateObject().Where(property => !known.Contains(property.Name, StringComparer.Ordinal)))
        {
            errors[prefix + property.Name] = ["Not a member here."];
        }
    }

    private static ChoiceChangeReading.Invalid Invalid(string field, string message) =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
