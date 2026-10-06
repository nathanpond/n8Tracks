using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.References;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// How Songs relate. Relationship types: every type, system types first (<c>catalog.read</c>), and
/// adding, renaming, and deleting the user's own (Settings → Relationships, session-only, each under
/// the type's revision in <c>If-Match</c>); a system type refuses both with 409 <c>system_type</c>.
/// A Song's relationships are embedded in the Song (<see cref="SongResponse"/>) and are added and
/// removed from either Song (<c>songs.write</c>); neither moves a Song's revision, only both Songs'
/// last-updated times, so no revision is sent. Every answer is <c>no-store</c>.
/// </summary>
internal static class RelationshipsEndpoints
{
    public const string TypesPath = ApiProblem.VersionPrefix + "/relationship-types";
    public const string TypePath = TypesPath + "/{id:guid}";
    public const string SongRelationshipsPath = SongsEndpoints.SongPath + "/relationships";
    public const string SongRelationshipPath = SongRelationshipsPath + "/{id:guid}";

    public const string SystemTypeCode = "system_type";
    public const string InUseCode = "relationship_type_in_use";
    public const string ExistsCode = "relationship_exists";

    public static IEndpointRouteBuilder MapRelationships(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(TypesPath, ListTypesAsync)
            .WithName("ListRelationshipTypes")
            .WithSummary("Every relationship type with its forward and reverse names and how many relationships use it: the system types in their order, then the user's alphabetically.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<RelationshipTypeListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(TypesPath, CreateTypeAsync)
            .WithName("CreateRelationshipType")
            .WithSummary("Adds a relationship type: name and reverseName (equal for a symmetric type), each 1 to 50 characters, neither used by another type in either direction.")
            .SessionOnly()
            .Produces<RelationshipTypeResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(TypePath, UpdateTypeAsync)
            .WithName("UpdateRelationshipType")
            .WithSummary("Renames a user-defined relationship type (name and/or reverseName), given its revision in If-Match. System types are 409 system_type. No Song changes.")
            .SessionOnly()
            .Produces<RelationshipTypeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(TypePath, DeleteTypeAsync)
            .WithName("DeleteRelationshipType")
            .WithSummary("Deletes a user-defined relationship type, given its revision in If-Match. One in use needs removeRelationships=true, and its relationships go with it. System types are 409 system_type.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(SongRelationshipsPath, RelateAsync)
            .WithName("RelateSongs")
            .WithSummary("Relates this Song to otherSong (its ID or shortcode) under typeId, read from this Song in direction forward or reverse. Answers this Song with its relationships. Moves both Songs' last-updated times, not their revisions.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapDelete(SongRelationshipPath, UnrelateAsync)
            .WithName("RemoveSongRelationship")
            .WithSummary("Removes one of this Song's relationships (from either of its Songs). Answers this Song. Moves both Songs' last-updated times, not their revisions.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<SongResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>200 with every type.</summary>
    private static async Task<Ok<RelationshipTypeListResponse>> ListTypesAsync(RelationshipService relationships, HttpContext context, CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await relationships.ListTypesAsync(cancellationToken);
        return TypedResults.Ok(new RelationshipTypeListResponse([.. list.Select(RelationshipTypeResponse.From)]));
    }

    /// <summary>201 with the new type; 422 <c>validation_failed</c> on a wrong or taken name, storing nothing.</summary>
    private static async Task<Results<Created<RelationshipTypeResponse>, ProblemHttpResult>> CreateTypeAsync(
        RelationshipTypeRequest? request,
        RelationshipService relationships,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = Text(request?.Name, RelationshipService.NameField, errors);
        var reverseName = Text(request?.ReverseName, RelationshipService.ReverseNameField, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await relationships.CreateTypeAsync(name, reverseName, cancellationToken);
        if (outcome is RelationshipTypeOutcome.Saved saved)
        {
            Log(loggers).LogInformation("Relationship type created: {RelationshipTypeId}", saved.Type.Type.Id);
            Revisions.SetETag(context, saved.Type.Revision);
            return TypedResults.Created($"{context.Request.PathBase}{TypesPath}/{saved.Type.Type.Id}", RelationshipTypeResponse.From(saved.Type));
        }

        return TypeRefusal(context, outcome);
    }

    /// <summary>
    /// 200 with the type renamed (unchanged when nothing differs); 404 when there is no such type;
    /// 409 <c>system_type</c> for a system type, or <c>revision_conflict</c> with <c>current</c>; 422
    /// on a wrong or taken name, or when neither name is sent.
    /// </summary>
    private static async Task<Results<Ok<RelationshipTypeResponse>, ProblemHttpResult>> UpdateTypeAsync(
        Guid id,
        RelationshipTypeRequest? request,
        RelationshipService relationships,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = Text(request?.Name, RelationshipService.NameField, errors);
        var reverseName = Text(request?.ReverseName, RelationshipService.ReverseNameField, errors);
        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var outcome = await relationships.UpdateTypeAsync(id, name, reverseName, revision!.Value, cancellationToken);
        if (outcome is RelationshipTypeOutcome.Saved saved)
        {
            if (saved.Type.Revision != revision)
            {
                Log(loggers).LogInformation("Relationship type renamed: {RelationshipTypeId}", id);
            }

            Revisions.SetETag(context, saved.Type.Revision);
            return TypedResults.Ok(RelationshipTypeResponse.From(saved.Type));
        }

        return TypeRefusal(context, outcome);
    }

    /// <summary>
    /// 204 when the type is deleted; 404 when there is no such type; 409 <c>system_type</c>,
    /// <c>revision_conflict</c> with <c>current</c>, or <c>relationship_type_in_use</c> with
    /// <c>relationshipCount</c> when relationships use it and <c>removeRelationships=true</c> was not
    /// sent; 422 on a wrong <c>removeRelationships</c>.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTypeAsync(
        Guid id,
        [FromQuery] string? removeRelationships,
        RelationshipService relationships,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var (revision, problem) = Revisions.Read(context);
        if (problem is not null)
        {
            return problem;
        }

        bool remove;
        switch (removeRelationships)
        {
            case null or "false":
                remove = false;
                break;
            case "true":
                remove = true;
                break;
            default:
                return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [RelationshipService.RemoveRelationshipsField] = ["Send true or false."] });
        }

        var outcome = await relationships.DeleteTypeAsync(id, revision!.Value, remove, cancellationToken);
        if (outcome is RelationshipTypeOutcome.Deleted deleted)
        {
            Log(loggers).LogInformation("Relationship type deleted: {RelationshipTypeId} with {RelationshipCount} relationships", id, deleted.RelationshipsRemoved);
            return TypedResults.NoContent();
        }

        return TypeRefusal(context, outcome);
    }

    /// <summary>
    /// 201 with this Song and its relationships; 404 when there is no such Song; 409
    /// <c>relationship_exists</c> with <c>current</c> when the pair is related under the type already,
    /// either way round; 422 <c>validation_failed</c> on a wrong field, an unknown type or other Song,
    /// or the Song itself as the other Song.
    /// </summary>
    private static async Task<Results<Created<SongResponse>, ProblemHttpResult>> RelateAsync(
        CatalogReference reference,
        RelateSongsRequest? request,
        RelationshipService relationships,
        ReferenceResolver references,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.SongIdAsync(reference, cancellationToken) is not { } songId)
        {
            return SongsEndpoints.NoSuchSong(context);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var typeId = Text(request?.TypeId, RelationshipService.TypeIdField, errors);
        var direction = Text(request?.Direction, RelationshipService.DirectionField, errors);
        var otherText = request?.OtherSong is { ValueKind: JsonValueKind.String } other ? other.GetString() : null;
        if (otherText is null)
        {
            errors[RelationshipService.OtherSongField] = ["Send the other Song's ID or shortcode."];
        }

        if (errors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, errors);
        }

        var otherId = CatalogReference.TryParse(otherText, out var otherReference)
            ? await references.SongIdAsync(otherReference, cancellationToken)
            : null;
        switch (await relationships.RelateAsync(songId, typeId, direction, otherId, cancellationToken))
        {
            case RelationshipOutcome.Changed changed:
                Log(loggers).LogInformation("Songs related: {SongId} and {OtherSongId} ({RelationshipId})", songId, otherId, changed.RelationshipId);
                Revisions.SetETag(context, changed.Song.Revision);
                return TypedResults.Created(
                    $"{context.Request.PathBase}{SongsEndpoints.SongsPath}/{songId}/relationships/{changed.RelationshipId}",
                    SongResponse.From(changed.Song, context.Request.PathBase));

            case RelationshipOutcome.Exists exists:
                return ApiProblem.For(
                    context,
                    StatusCodes.Status409Conflict,
                    ExistsCode,
                    "These Songs are already related under this type.",
                    [new("current", SongResponse.From(exists.Current, context.Request.PathBase))]);

            case RelationshipOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            case RelationshipOutcome.NoSuchSong:
                return SongsEndpoints.NoSuchSong(context);

            default:
                throw new InvalidOperationException("Unknown relationship outcome.");
        }
    }

    /// <summary>200 with this Song without the relationship; 404 when there is no such Song, or it has no such relationship.</summary>
    private static async Task<Results<Ok<SongResponse>, ProblemHttpResult>> UnrelateAsync(
        CatalogReference reference,
        Guid id,
        RelationshipService relationships,
        ReferenceResolver references,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        if (await references.SongIdAsync(reference, cancellationToken) is not { } songId)
        {
            return SongsEndpoints.NoSuchSong(context);
        }

        switch (await relationships.UnrelateAsync(songId, id, cancellationToken))
        {
            case RelationshipOutcome.Changed changed:
                Log(loggers).LogInformation("Song relationship removed: {RelationshipId} from {SongId}", id, songId);
                Revisions.SetETag(context, changed.Song.Revision);
                return TypedResults.Ok(SongResponse.From(changed.Song, context.Request.PathBase));

            case RelationshipOutcome.NotFound:
                return ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "This Song has no such relationship.");

            case RelationshipOutcome.NoSuchSong:
                return SongsEndpoints.NoSuchSong(context);

            default:
                throw new InvalidOperationException("Unknown relationship outcome.");
        }
    }

    /// <summary>A text field: null when not sent (or null); a type error recorded otherwise.</summary>
    private static string? Text(JsonElement? value, string field, Dictionary<string, string[]> errors)
    {
        switch (value)
        {
            case { ValueKind: JsonValueKind.String } text:
                return text.GetString();
            case null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }:
                return null;
            default:
                errors[field] = ["Send text."];
                return null;
        }
    }

    /// <summary>The problem for every type outcome but a save or a delete.</summary>
    private static ProblemHttpResult TypeRefusal(HttpContext context, RelationshipTypeOutcome outcome) =>
        outcome switch
        {
            RelationshipTypeOutcome.Conflict conflict => Revisions.Conflict(context, RelationshipTypeResponse.From(conflict.Current)),
            RelationshipTypeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            RelationshipTypeOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such relationship type."),
            RelationshipTypeOutcome.System system => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                SystemTypeCode,
                "System relationship types cannot be renamed or deleted.",
                [new("current", RelationshipTypeResponse.From(system.Type))]),
            RelationshipTypeOutcome.InUse inUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "Relationships use this type. Confirm that they are removed with it (removeRelationships=true) to delete it.",
                [new("relationshipCount", inUse.RelationshipCount)]),
            _ => throw new InvalidOperationException("Unknown relationship type outcome."),
        };

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(RelationshipsEndpoints));
}

/// <summary>A new type, or a rename: each name read as raw JSON, so a wrong type is a field error.</summary>
internal sealed record RelationshipTypeRequest(JsonElement? Name, JsonElement? ReverseName);

/// <summary>Relating Songs: the type's ID, the direction read from this Song, and the other Song's ID or shortcode, each read as raw JSON.</summary>
internal sealed record RelateSongsRequest(JsonElement? TypeId, JsonElement? Direction, JsonElement? OtherSong);

/// <summary>
/// A relationship type as the API shows it: both names (equal for a symmetric type), whether it is
/// a system type, the Suno action a system type stands for, how many relationships use it, and its revision.
/// </summary>
internal sealed record RelationshipTypeResponse(
    Guid Id,
    string Name,
    string ReverseName,
    bool System,
    bool Symmetric,
    string? SunoAction,
    int RelationshipCount,
    int Revision)
{
    public static RelationshipTypeResponse From(RelationshipTypeUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var type = usage.Type;
        return new(type.Id, type.Name, type.ReverseName, type.IsSystem, type.IsSymmetric, type.SunoAction, usage.RelationshipCount, usage.Revision);
    }
}

/// <summary>Every relationship type: system types first, then the user's alphabetically.</summary>
internal sealed record RelationshipTypeListResponse(RelationshipTypeResponse[] Items);

/// <summary>
/// A relationship as a Song shows it: its ID, its type, the type's name read from this Song, the
/// direction that is (<c>forward</c> or <c>reverse</c>), and the other Song.
/// </summary>
internal sealed record SongRelationshipResponse(Guid Id, Guid TypeId, string Name, string Direction, RelatedSongResponse Song)
{
    public static SongRelationshipResponse From(SongRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        return new(
            relation.Id,
            relation.TypeId,
            relation.Name,
            relation.Direction == RelationshipDirection.Forward ? RelationshipService.ForwardValue : RelationshipService.ReverseValue,
            new RelatedSongResponse(relation.Song.Id, relation.Song.Shortcode, relation.Song.Title));
    }
}

/// <summary>The other Song of a relationship: its ID, shortcode, and title.</summary>
internal sealed record RelatedSongResponse(Guid Id, string Shortcode, string Title);
