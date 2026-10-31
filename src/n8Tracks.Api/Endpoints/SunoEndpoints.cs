using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// What a client needs to offer a Version's Suno options without restating Suno's rules
/// (<c>catalog.read</c>): the fields of Suno's Create screen as the embedded inventory records them,
/// each with the Version option it is stored in and Suno's own explanation, and the models offered for
/// a new choice. The model list itself is managed under <see cref="SunoModelsEndpoints"/>. Also the
/// Suno playlists and personas n8Tracks knows, and the user's voice list and playlist list as a sync
/// reports them (#439, #449, <c>suno.sync</c>), with the slim list of known playlists the sync panel
/// offers (#449, <c>suno.sync</c>: names and states only, never a playlist's members).
/// </summary>
internal static class SunoEndpoints
{
    public const string CreateFieldsPath = ApiProblem.VersionPrefix + "/suno/create-fields";
    public const string PlaylistsPath = ApiProblem.VersionPrefix + "/suno/playlists";
    public const string PersonasPath = ApiProblem.VersionPrefix + "/suno/personas";
    public const string DiscoveredPersonasPath = PersonasPath + "/discovered";
    public const string DiscoveredPlaylistsPath = PlaylistsPath + "/discovered";

    /// <summary>Each inventory key's Version option, as the API spells it (the inverse of <see cref="VersionInputRules.InventoryKey"/>).</summary>
    private static readonly Dictionary<string, string> OptionsByKey = VersionInputRules.Keys
        .Select(static option => (Option: option, Key: VersionInputRules.InventoryKey(option)))
        .Where(static pair => pair.Key is not null)
        .ToDictionary(static pair => pair.Key!, static pair => pair.Option, StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapSuno(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(CreateFieldsPath, GetCreateFieldsAsync)
            .WithName("GetSunoCreateFields")
            .WithSummary("The fields of Suno's Create screen, as the field inventory records them (limits, ranges, value lists, defaults, tabs, modes), each with the Version option it is stored in (option) and Suno's explanation (help), and the models offered for a new choice (not retired, in the list's order).")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<CreateFieldsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(PlaylistsPath, GetPlaylistsAsync)
            .WithName("GetSunoPlaylists")
            .WithSummary("The Suno playlists n8Tracks knows, by name, unpaged: those seen in imports and those a sync read from the user's playlist list (#449), each with its members as an import last recorded them (clipIds, memberCount; empty until one did), songCount (Suno's own count as last listed; null when only an import saw it), and state (available, or unavailable when the last complete playlist list left it out or named it trashed). What the Sources editor offers as Inspiration (the available ones). Empty until an import or a sync has run.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoPlaylistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(DiscoveredPlaylistsPath, GetKnownPlaylistsAsync)
            .WithName("GetKnownSunoPlaylists")
            .WithSummary("The Suno playlists n8Tracks knows, by name, unpaged, for the extension's sync panel (#449): each playlist's id, name, state, and songCount only, never its members. Read by the extension's sync (suno.sync).")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoKnownPlaylistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(DiscoveredPlaylistsPath, ReportPlaylistsAsync)
            .WithName("ReportSunoPlaylists")
            .WithSummary("The extension reports the user's Suno playlists, as a sync reads them (#449): { complete, playlists: [<Suno's playlist object>] }. Each is kept by its id, with its name (a blank one never overwrites a known name), is_trashed, and song_count (else num_total_results); nothing else of the object is kept, and a playlist's members never change here (only an import commit records them). Only a complete list changes availability: a known playlist it leaves out, or names with is_trashed, becomes unavailable, and one it lists untrashed becomes available again. Nothing is deleted and no Version changes. An entry without an id refuses the whole report; a repeated id keeps the last. Answers the known playlists as GET .../discovered does. Taken from a sync (suno.sync).")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoPlaylistReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapGet(PersonasPath, GetPersonasAsync)
            .WithName("GetSunoPersonas")
            .WithSummary("The Suno personas (Voices) n8Tracks knows, by name, unpaged: those seen in imported clips and those a sync read from the user's voice list (#439), each with its description, state (available, or unavailable when the last complete voice list left it out or named it trashed), and owned (Suno's is_owned; null when only a clip named it). What the Sources editor offers as a Voice (the available ones). Empty until an import or a sync has run.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoPersonaListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut(DiscoveredPersonasPath, ReportPersonasAsync)
            .WithName("ReportSunoPersonas")
            .WithSummary("The extension reports the user's Suno voices, as a sync reads them (#439): { complete, voices: [<Suno's persona object>] }. Each is kept by its id, with its name (a blank one never overwrites a known name), description, is_trashed, and is_owned; nothing else of the object is kept. Only a complete list changes availability: a known persona it leaves out, or names with is_trashed, becomes unavailable, and one it lists untrashed becomes available again. Nothing is deleted and no Version changes. An entry without an id refuses the whole report; a repeated id keeps the last. Taken from a sync (suno.sync).")
            .RequireScope(CredentialScopes.SunoSync)
            .Produces<SunoPersonaReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapSunoModels();
        endpoints.MapSunoWorkspaces();
        endpoints.MapSunoExports();
        endpoints.MapSunoIgnored();
        endpoints.MapSunoGenerationRequests();
        endpoints.MapSunoRemoteStates();
        endpoints.MapSunoExportPlaylists();
        endpoints.MapSunoClipLookup();

        return endpoints;
    }

    /// <summary>200 with the inventory's fields, in its order, and the models offered for a new choice.</summary>
    private static async Task<Ok<CreateFieldsResponse>> GetCreateFieldsAsync(
        ISunoModelList models,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var inventory = CreateFieldInventory.Embedded;
        var captured = JsonNode.Parse(inventory.Json)!.AsObject();
        var fields = new JsonArray();
        foreach (var node in captured["fields"]!.AsArray())
        {
            var field = node!.DeepClone().AsObject();
            var key = (string)field["key"]!;
            field["option"] = OptionsByKey.GetValueOrDefault(key);
            field["help"] = inventory.Get(key).Help;
            fields.Add(field);
        }

        return TypedResults.Ok(new CreateFieldsResponse(
            (string?)captured["capturedOn"],
            fields,
            await models.OfferedAsync(cancellationToken)));
    }

    /// <summary>200 with every playlist seen, by name.</summary>
    private static async Task<Ok<SunoPlaylistListResponse>> GetPlaylistsAsync(
        SunoLibraryService library,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);
        var playlists = await library.PlaylistsAsync(cancellationToken);
        return TypedResults.Ok(new SunoPlaylistListResponse([.. playlists.Select(static playlist => new SunoPlaylistResponse(
            playlist.SunoId,
            playlist.Name,
            playlist.ClipIds.Count,
            playlist.ClipIds,
            playlist.LastSeen,
            SunoPlaylistRules.NameOf(playlist.State),
            playlist.SongCount))]));
    }

    /// <summary>200 with every playlist known, by name, without its members.</summary>
    private static async Task<Ok<SunoKnownPlaylistListResponse>> GetKnownPlaylistsAsync(
        SunoLibraryService library,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);
        return TypedResults.Ok(SunoKnownPlaylistListResponse.From(await library.PlaylistsAsync(cancellationToken)));
    }

    /// <summary>
    /// 200 with every playlist as it is now (without its members) and what the report changed; 422
    /// <c>validation_failed</c>, storing nothing, when <c>complete</c> is not true or false or an entry
    /// cannot be read. Logs counts only: never a name or anything else of Suno's playlist (invariant 6).
    /// </summary>
    private static async Task<Results<Ok<SunoPlaylistReportResponse>, ProblemHttpResult>> ReportPlaylistsAsync(
        SunoPlaylistReportRequest? request,
        SunoPlaylistService playlists,
        SunoLibraryService library,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var complete = request?.Complete.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
        var reading = SunoPlaylistService.ReadReport(request?.Playlists ?? default);
        var errors = reading is SunoPlaylistReportReading.Invalid invalid
            ? new Dictionary<string, string[]>(invalid.Errors, StringComparer.Ordinal)
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (complete is null)
        {
            errors[SunoPlaylistService.CompleteField] = ["Send whether this is the complete list of playlists: true or false."];
        }

        if (errors.Count > 0 || reading is not SunoPlaylistReportReading.Read read)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var report = await playlists.ReportAsync(read.Listings, complete!.Value, cancellationToken);
        loggers.CreateLogger(typeof(SunoEndpoints)).LogInformation(
            "Suno playlists reported: {PlaylistCount} named, complete {Complete}; {AddedCount} added, {RenamedCount} renamed, {UnavailableCount} now unavailable, {AvailableCount} available again",
            read.Listings.Count,
            complete.Value,
            report.Added.Count,
            report.Renamed.Count,
            report.BecameUnavailable.Count,
            report.BecameAvailable.Count);

        var list = SunoKnownPlaylistListResponse.From(await library.PlaylistsAsync(cancellationToken));
        return TypedResults.Ok(new SunoPlaylistReportResponse(list.Items, report.Added, report.Renamed, report.BecameUnavailable, report.BecameAvailable));
    }

    /// <summary>200 with every persona seen, by name.</summary>
    private static async Task<Ok<SunoPersonaListResponse>> GetPersonasAsync(
        SunoLibraryService library,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);
        var personas = await library.PersonasAsync(cancellationToken);
        return TypedResults.Ok(SunoPersonaListResponse.From(personas));
    }

    /// <summary>
    /// 200 with every persona as it is now and what the report changed; 422 <c>validation_failed</c>,
    /// storing nothing, when <c>complete</c> is not true or false or an entry cannot be read. Logs counts
    /// only: never a name, a description, or anything else of Suno's persona (invariant 6).
    /// </summary>
    private static async Task<Results<Ok<SunoPersonaReportResponse>, ProblemHttpResult>> ReportPersonasAsync(
        SunoPersonaReportRequest? request,
        SunoPersonaService personas,
        SunoLibraryService library,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var complete = request?.Complete.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
        var reading = SunoPersonaService.ReadReport(request?.Voices ?? default);
        var errors = reading is SunoPersonaReportReading.Invalid invalid
            ? new Dictionary<string, string[]>(invalid.Errors, StringComparer.Ordinal)
            : new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (complete is null)
        {
            errors[SunoPersonaService.CompleteField] = ["Send whether this is the complete list of voices: true or false."];
        }

        if (errors.Count > 0 || reading is not SunoPersonaReportReading.Read read)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var report = await personas.ReportAsync(read.Listings, complete!.Value, cancellationToken);
        loggers.CreateLogger(typeof(SunoEndpoints)).LogInformation(
            "Suno voices reported: {VoiceCount} named, complete {Complete}; {AddedCount} added, {RenamedCount} renamed, {UnavailableCount} now unavailable, {AvailableCount} available again",
            read.Listings.Count,
            complete.Value,
            report.Added.Count,
            report.Renamed.Count,
            report.BecameUnavailable.Count,
            report.BecameAvailable.Count);

        var list = SunoPersonaListResponse.From(await library.PersonasAsync(cancellationToken));
        return TypedResults.Ok(new SunoPersonaReportResponse(list.Items, report.Added, report.Renamed, report.BecameUnavailable, report.BecameAvailable));
    }
}

/// <summary>The Suno playlists n8Tracks knows, by name.</summary>
internal sealed record SunoPlaylistListResponse(IReadOnlyList<SunoPlaylistResponse> Items);

/// <summary>
/// A Suno playlist seen in an import or the user's playlist list: its Suno ID (<c>id</c>), its name as
/// last seen, how many clips it held when an import last recorded its members (<c>memberCount</c>) and
/// their IDs in order (<c>clipIds</c>, the snapshot a Version keeps when it uses the playlist as
/// Inspiration; empty until an import recorded them), when it was last seen (UTC), <c>state</c>
/// (<c>available</c> or <c>unavailable</c>, #449), and <c>songCount</c> (Suno's own count as last listed;
/// null when only an import saw it).
/// </summary>
internal sealed record SunoPlaylistResponse(string Id, string Name, int MemberCount, IReadOnlyList<string> ClipIds, DateTimeOffset LastSeen, string State, int? SongCount);

/// <summary>The Suno playlists n8Tracks knows, by name, without their members (#449).</summary>
internal sealed record SunoKnownPlaylistListResponse(IReadOnlyList<SunoKnownPlaylistResponse> Items)
{
    public static SunoKnownPlaylistListResponse From(IReadOnlyList<SunoPlaylist> playlists) =>
        new([.. playlists.Select(static playlist => new SunoKnownPlaylistResponse(
            playlist.SunoId,
            playlist.Name,
            SunoPlaylistRules.NameOf(playlist.State),
            playlist.SongCount))]);
}

/// <summary>A Suno playlist as the sync panel is told of it (#449): its Suno ID, name, state, and Suno's song count.</summary>
internal sealed record SunoKnownPlaylistResponse(string Id, string Name, string State, int? SongCount);

/// <summary>A playlist report as sent, read as raw JSON: <c>complete</c> (true or false) and <c>playlists</c> (Suno's playlist objects).</summary>
internal sealed record SunoPlaylistReportRequest(JsonElement Complete, JsonElement Playlists);

/// <summary>Every playlist as it is after a playlist report, without its members, and the Suno IDs the report added, renamed, made unavailable, and made available again.</summary>
internal sealed record SunoPlaylistReportResponse(
    IReadOnlyList<SunoKnownPlaylistResponse> Items,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>The Suno personas n8Tracks knows, by name.</summary>
internal sealed record SunoPersonaListResponse(IReadOnlyList<SunoPersonaResponse> Items)
{
    public static SunoPersonaListResponse From(IReadOnlyList<SunoPersona> personas) =>
        new([.. personas.Select(static persona => new SunoPersonaResponse(
            persona.SunoId,
            persona.Name,
            persona.Description,
            SunoPersonaRules.NameOf(persona.State),
            persona.IsOwned))]);
}

/// <summary>
/// A Suno persona (a Voice) seen in an imported clip or the user's voice list: its Suno ID (<c>id</c>),
/// its name and description as last seen (either may be empty), <c>state</c> (<c>available</c> or
/// <c>unavailable</c>, #439), and <c>owned</c> (Suno's <c>is_owned</c>; null when only a clip named it).
/// </summary>
internal sealed record SunoPersonaResponse(string Id, string Name, string Description, string State, bool? Owned);

/// <summary>A voice report as sent, read as raw JSON: <c>complete</c> (true or false) and <c>voices</c> (Suno's persona objects).</summary>
internal sealed record SunoPersonaReportRequest(JsonElement Complete, JsonElement Voices);

/// <summary>Every persona as it is after a voice report, and the Suno IDs the report added, renamed, made unavailable, and made available again.</summary>
internal sealed record SunoPersonaReportResponse(
    IReadOnlyList<SunoPersonaResponse> Items,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> BecameUnavailable,
    IReadOnlyList<string> BecameAvailable);

/// <summary>
/// Suno's Create-screen fields. Each field is the inventory's own record (<c>key</c>, <c>label</c>,
/// <c>tab</c>, <c>modes</c>, <c>type</c>, and, where recorded, <c>values</c>, <c>default</c>,
/// <c>maxLength</c>, <c>min</c>, <c>max</c>, <c>unit</c>, <c>condition</c>, <c>notes</c>) plus
/// <c>option</c>, the key of the Version's <c>inputs</c> it is stored in (null for a field no option
/// stores, and for lyrics and styles, which are the Version's own fields), and <c>help</c>, Suno's
/// explanation (null when none was captured). <c>models</c> are the models offered for a new choice:
/// the list's models that are not retired, in its order. A Version's model is checked against the
/// whole list, retired models included, so a picker shows a Version's retired model as well.
/// </summary>
internal sealed record CreateFieldsResponse(string? CapturedOn, JsonArray Fields, IReadOnlyList<string> Models);
