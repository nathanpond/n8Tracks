using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Catalog;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// Genres, the user's own list: every Genre with its Song count (<c>catalog.read</c>), and creating
/// one (<c>songs.write</c>), which the Song page's picker does as the user types a new name. A Song's
/// Genres are set through the Song's own edit (<c>genreIds</c> in <see cref="SongsEndpoints"/>).
/// Every answer is <c>no-store</c>.
/// </summary>
internal static class GenresEndpoints
{
    public const string GenresPath = ApiProblem.VersionPrefix + "/genres";

    public static IEndpointRouteBuilder MapGenres(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(GenresPath, ListAsync)
            .WithName("ListGenres")
            .WithSummary("Every Genre, alphabetically and unpaged, each with how many Songs have it.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<GenreListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(GenresPath, CreateAsync)
            .WithName("CreateGenre")
            .WithSummary("Creates a Genre (201), or answers the one that already has the name in any letter case (200). Assign it to a Song with the Song's genreIds.")
            .RequireScope(CredentialScopes.SongsWrite)
            .Produces<GenreResponse>(StatusCodes.Status201Created)
            .Produces<GenreResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>200 with every Genre.</summary>
    private static async Task<Ok<GenreListResponse>> ListAsync(GenreService genres, HttpContext context, CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var list = await genres.ListAsync(cancellationToken);
        return TypedResults.Ok(new GenreListResponse([.. list.Select(static usage => GenreResponse.From(usage.Genre, usage.SongCount))]));
    }

    /// <summary>
    /// 201 with the new Genre; 200 with the existing one when its name differs only in letter case or
    /// spacing; 422 <c>validation_failed</c> on a wrong name, storing nothing.
    /// </summary>
    private static async Task<Results<Created<GenreResponse>, Ok<GenreResponse>, ProblemHttpResult>> CreateAsync(
        CreateGenreRequest? request,
        GenreService genres,
        HttpContext context,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        switch (await genres.CreateAsync(request?.Name, cancellationToken))
        {
            case GenreCreateOutcome.Created created:
                loggers.CreateLogger(typeof(GenresEndpoints)).LogInformation("Genre created: {GenreId}", created.Genre.Id);
                return TypedResults.Created($"{context.Request.PathBase}{GenresPath}/{created.Genre.Id}", GenreResponse.From(created.Genre, songCount: 0));

            case GenreCreateOutcome.Existing existing:
                var count = (await genres.ListAsync(cancellationToken)).Single(usage => usage.Genre.Id == existing.Genre.Id).SongCount;
                return TypedResults.Ok(GenreResponse.From(existing.Genre, count));

            case GenreCreateOutcome.Invalid invalid:
                return ApiProblem.ValidationFailed(context, invalid.Errors);

            default:
                throw new InvalidOperationException("Unknown Genre outcome.");
        }
    }
}

/// <summary>The create form: the name, which may be missing.</summary>
internal sealed record CreateGenreRequest(string? Name);

/// <summary>A Genre as the API shows it, with how many Songs have it.</summary>
internal sealed record GenreResponse(Guid Id, string Name, int SongCount)
{
    public static GenreResponse From(Genre genre, int songCount)
    {
        ArgumentNullException.ThrowIfNull(genre);

        return new(genre.Id, genre.Name, songCount);
    }
}

/// <summary>Every Genre, alphabetically.</summary>
internal sealed record GenreListResponse(GenreResponse[] Items);
