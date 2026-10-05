using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Tags, the user's own coloured labels: every Tag with its colour and Song count
/// (<c>catalog.read</c>), and creating one (<c>songs.write</c>), which the Song page's picker does
/// as the user types a new name; the colour is chosen then. A Song's Tags are set through the
/// Song's own edit (<c>tagIds</c> in <see cref="SongsEndpoints"/>). Every answer is <c>no-store</c>.
/// </summary>
internal static class TagsEndpoints
{
    public const string TagsPath = ApiProblem.VersionPrefix + "/tags";

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
}

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
