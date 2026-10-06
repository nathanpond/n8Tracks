using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// What a client needs to offer a Version's Suno options without restating Suno's rules
/// (<c>catalog.read</c>): the fields of Suno's Create screen as the embedded inventory records them,
/// each with the Version option it is stored in and Suno's own explanation, and the models offered for
/// a new choice. The model list itself is managed under <see cref="SunoModelsEndpoints"/>.
/// </summary>
internal static class SunoEndpoints
{
    public const string CreateFieldsPath = ApiProblem.VersionPrefix + "/suno/create-fields";
    public const string PlaylistsPath = ApiProblem.VersionPrefix + "/suno/playlists";
    public const string PersonasPath = ApiProblem.VersionPrefix + "/suno/personas";

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
            .WithSummary("The Suno playlists n8Tracks has seen in imports, by name, unpaged: what the Sources editor offers as Inspiration. Empty until an import has run.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoPlaylistListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(PersonasPath, GetPersonasAsync)
            .WithName("GetSunoPersonas")
            .WithSummary("The Suno personas (Voices) n8Tracks has seen in imported clips, by name, unpaged: what the Sources editor offers as a Voice. Empty until an import has run.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<SunoPersonaListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapSunoModels();

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
            playlist.LastSeen))]));
    }

    /// <summary>200 with every persona seen, by name.</summary>
    private static async Task<Ok<SunoPersonaListResponse>> GetPersonasAsync(
        SunoLibraryService library,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);
        var personas = await library.PersonasAsync(cancellationToken);
        return TypedResults.Ok(new SunoPersonaListResponse([.. personas.Select(static persona => new SunoPersonaResponse(persona.SunoId, persona.Name))]));
    }
}

/// <summary>The Suno playlists seen in imports, by name.</summary>
internal sealed record SunoPlaylistListResponse(IReadOnlyList<SunoPlaylistResponse> Items);

/// <summary>
/// A Suno playlist seen in an import: its Suno ID (<c>id</c>), its name as last seen, how many clips
/// it held then (<c>memberCount</c>) and their IDs in order (<c>clipIds</c>, the snapshot a Version
/// keeps when it uses the playlist as Inspiration), and when it was last seen (UTC).
/// </summary>
internal sealed record SunoPlaylistResponse(string Id, string Name, int MemberCount, IReadOnlyList<string> ClipIds, DateTimeOffset LastSeen);

/// <summary>The Suno personas seen in imported clips, by name.</summary>
internal sealed record SunoPersonaListResponse(IReadOnlyList<SunoPersonaResponse> Items);

/// <summary>A Suno persona (a Voice) seen in an imported clip: its Suno ID (<c>id</c>) and its name as last seen.</summary>
internal sealed record SunoPersonaResponse(string Id, string Name);

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
