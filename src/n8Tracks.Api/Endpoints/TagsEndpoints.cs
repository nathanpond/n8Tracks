using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Tags, the user's own coloured labels: every Tag with its colour and Song count
/// (<c>catalog.read</c>), and creating one (<c>songs.write</c>), which the Song page's picker does
/// as the user types a new name; the colour is chosen then. A Song's Tags are set through the
/// Song's own edit (<c>tagIds</c> in <see cref="SongsEndpoints"/>). Renaming, recolouring,
/// merging, and deleting Tags (Settings → Tags) is session-only, each under the Tag's own revision
/// in <c>If-Match</c>, with the Genre management routes and refusals (<see cref="GenresEndpoints"/>),
/// except that a Tag in use is only ever removed from its Songs, never reassigned. Every answer is
/// <c>no-store</c>.
/// </summary>
internal static class TagsEndpoints
{
    public const string TagsPath = ApiProblem.VersionPrefix + "/tags";
    public const string TagPath = TagsPath + "/{id:guid}";
    public const string MergePath = TagPath + "/merge";

    public const string NameTakenCode = "tag_name_taken";
    public const string InUseCode = "tag_in_use";

    private const string ReassignToField = "reassignTo";

    public static IEndpointRouteBuilder MapTags(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(TagsPath, ListAsync)
            .WithName("ListTags")
            .WithSummary("Every Tag, alphabetically and unpaged, each with its colour and how many Songs have it.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<TagListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(TagsPath, CreateAsync)
            .WithName("CreateTag")
            .WithSummary("Creates a Tag in the next palette colour (201), or answers the one that already has the name in any letter case (200). Assign it to a Song with the Song's tagIds.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<TagResponse>(StatusCodes.Status201Created)
            .Produces<TagResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPatch(TagPath, UpdateAsync)
            .WithName("UpdateTag")
            .WithSummary("Renames and/or recolours a Tag (only the fields sent), given its revision in If-Match. A name another Tag has is 409 tag_name_taken with that Tag's ID, so it can be merged instead.")
            .SessionOnly()
            .Produces<TagResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapPost(MergePath, MergeAsync)
            .WithName("MergeTags")
            .WithSummary("Merges the Tags sourceIds into this one, given its revision in If-Match: their Songs get this Tag (once), it keeps its colour, and they are removed.")
            .SessionOnly()
            .Produces<TagResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        endpoints.MapDelete(TagPath, DeleteAsync)
            .WithName("DeleteTag")
            .WithSummary("Deletes a Tag, given its revision in If-Match. One in use needs removeFromSongs=true (merge it to give its Songs another Tag); an unused one takes nothing.")
            .SessionOnly()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    /// <summary>200 with every Tag.</summary>
    private static async Task<Ok<TagListResponse>> ListAsync(TagService tags, HttpContext context, CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await tags.ListAsync(cancellationToken);
        return TypedResults.Ok(new TagListResponse([.. list.Select(TagResponse.From)]));
    }

    /// <summary>
    /// 201 with the new Tag and its colour; 200 with the existing one when its name differs only in
    /// letter case or spacing; 422 <c>validation_failed</c> on a wrong name, storing nothing.
    /// </summary>
    private static async Task<Results<Created<TagResponse>, Ok<TagResponse>, ProblemHttpResult>> CreateAsync(
        CreateTagRequest? request,
        TagService tags,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await tags.CreateAsync(request?.Name, cancellationToken))
        {
            case TagCreateOutcome.Created created:
                loggers.CreateLogger(typeof(TagsEndpoints)).LogInformation("Tag created: {TagId} ({Colour})", created.Tag.Id, created.Tag.Colour);
                return TypedResults.Created($"{context.Request.PathBase}{TagsPath}/{created.Tag.Id}", TagResponse.From(new TagUsage(created.Tag, 0, 1)));

            case TagCreateOutcome.Existing existing:
                var usage = await tags.FindAsync(existing.Tag.Id, cancellationToken) ?? new TagUsage(existing.Tag, 0, 1);
                return TypedResults.Ok(TagResponse.From(usage));

            case TagCreateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown Tag outcome.");
        }
    }

    /// <summary>
    /// 200 with the Tag renamed and/or recoloured (unchanged when nothing differs); 404 when there is
    /// no such Tag; 409 <c>revision_conflict</c> with <c>current</c>; 409 <c>tag_name_taken</c> with
    /// <c>tagId</c> and <c>tag</c> when another Tag has the name; 422 on a wrong name or colour, or
    /// when neither is sent.
    /// </summary>
    private static async Task<Results<Ok<TagResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateTagRequest? request,
        TagService tags,
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

        var typeErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = Text(request?.Name, TagService.NameField, typeErrors);
        var colour = Text(request?.Colour, TagService.ColourField, typeErrors);
        if (typeErrors.Count > 0)
        {
            return ApiProblem.ValidationFailed(context, typeErrors);
        }

        var outcome = await tags.UpdateAsync(id, name, colour, revision!.Value, cancellationToken);
        if (outcome is TagChangeOutcome.Changed changed)
        {
            if (changed.Tag.Revision != revision)
            {
                Log(loggers).LogInformation("Tag changed: {TagId} ({Colour})", id, changed.Tag.Tag.Colour);
            }

            return Answer(context, changed.Tag);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 200 with this Tag (its own name and colour), its new Song count, and its new revision; 409
    /// <c>revision_conflict</c> with <c>current</c>; 422 when a source is wrong or missing, is this
    /// Tag, or this Tag does not exist (nothing is merged).
    /// </summary>
    private static async Task<Results<Ok<TagResponse>, ProblemHttpResult>> MergeAsync(
        Guid id,
        MergeTagsRequest? request,
        TagService tags,
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

        List<string?>? sources = null;
        if (request?.SourceIds is { ValueKind: JsonValueKind.Array } array)
        {
            sources = [.. array.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)];
        }
        else if (request?.SourceIds is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [TagService.SourceIdsField] = ["Send a list of Tag IDs."] });
        }

        var outcome = await tags.MergeAsync(id, sources, revision!.Value, cancellationToken);
        if (outcome is TagChangeOutcome.Changed changed)
        {
            Log(loggers).LogInformation("Tags merged into {TagId}: {SongCount} Songs changed", id, changed.SongsChanged);
            return Answer(context, changed.Tag);
        }

        return Refusal(context, outcome);
    }

    /// <summary>
    /// 204 when the Tag is deleted; 404 when there is no such Tag; 409 <c>revision_conflict</c> with
    /// <c>current</c>; 409 <c>tag_in_use</c> with <c>songCount</c> when Songs have it and
    /// <c>removeFromSongs=true</c> was not sent; 422 on <c>reassignTo</c> (Tags are merged, not
    /// reassigned), a wrong <c>removeFromSongs</c>, or <c>removeFromSongs=true</c> for a Tag no Song has.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        [FromQuery] string? reassignTo,
        [FromQuery] string? removeFromSongs,
        TagService tags,
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

        if (reassignTo is not null)
        {
            return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [ReassignToField] = ["A Tag's Songs are not reassigned. Merge the Tag into another instead."] });
        }

        bool remove;
        switch (removeFromSongs)
        {
            case null or "false":
                remove = false;
                break;
            case "true":
                remove = true;
                break;
            default:
                return ApiProblem.ValidationFailed(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [TagService.RemoveFromSongsField] = ["Send true or false."] });
        }

        var outcome = await tags.DeleteAsync(id, revision!.Value, remove, cancellationToken);
        if (outcome is TagChangeOutcome.Deleted deleted)
        {
            Log(loggers).LogInformation("Tag deleted: {TagId}: {SongCount} Songs changed", id, deleted.SongsChanged);
            return TypedResults.NoContent();
        }

        return Refusal(context, outcome);
    }

    /// <summary>A text field of the edit: null when not sent (or null); a type error recorded otherwise.</summary>
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

    private static Ok<TagResponse> Answer(HttpContext context, TagUsage tag)
    {
        Revisions.SetETag(context, tag.Revision);
        return TypedResults.Ok(TagResponse.From(tag));
    }

    /// <summary>The problem for every outcome but a change.</summary>
    private static ProblemHttpResult Refusal(HttpContext context, TagChangeOutcome outcome) =>
        outcome switch
        {
            TagChangeOutcome.Conflict conflict => Revisions.Conflict(context, TagResponse.From(conflict.Current)),
            TagChangeOutcome.Invalid invalid => ApiProblem.ValidationFailed(context, invalid.Errors),
            TagChangeOutcome.NotFound => ApiProblem.For(context, StatusCodes.Status404NotFound, ApiProblem.NotFoundCode, "There is no such Tag."),
            TagChangeOutcome.NameTaken taken => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                NameTakenCode,
                "Another Tag already has this name. Merge this Tag into it instead.",
                [new("tagId", taken.Other.Tag.Id), new("tag", TagResponse.From(taken.Other))]),
            TagChangeOutcome.InUse inUse => ApiProblem.For(
                context,
                StatusCodes.Status409Conflict,
                InUseCode,
                "Songs have this Tag. Remove it from them to delete it, or merge it into another Tag.",
                [new("songCount", inUse.SongCount)]),
            _ => throw new InvalidOperationException("Unknown Tag outcome."),
        };

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger(typeof(TagsEndpoints));
}

/// <summary>A rename and/or recolour: each field read as raw JSON, so a wrong type is a field error.</summary>
internal sealed record UpdateTagRequest(JsonElement? Name, JsonElement? Colour);

/// <summary>A merge: the IDs of the Tags merged into this one, read as raw JSON so a wrong type is a field error.</summary>
internal sealed record MergeTagsRequest(JsonElement? SourceIds);

/// <summary>The create form: the name, which may be missing. The colour is chosen by the server.</summary>
internal sealed record CreateTagRequest(string? Name);

/// <summary>A Tag as the API shows it: its palette colour's name, how many Songs have it, and its revision.</summary>
internal sealed record TagResponse(Guid Id, string Name, string Colour, int SongCount, int Revision)
{
    public static TagResponse From(TagUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new(usage.Tag.Id, usage.Tag.Name, usage.Tag.Colour, usage.SongCount, usage.Revision);
    }
}

/// <summary>Every Tag, alphabetically.</summary>
internal sealed record TagListResponse(TagResponse[] Items);
