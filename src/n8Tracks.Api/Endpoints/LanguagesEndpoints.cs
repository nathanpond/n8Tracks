using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Endpoints;

/// <summary>
/// The languages a Song's release details can name (<c>catalog.read</c>): the one bundled list
/// (<see cref="Languages"/>) that the Song edit also checks codes against, so a picker built from
/// it offers exactly what is accepted. Every answer is <c>no-store</c>.
/// </summary>
internal static class LanguagesEndpoints
{
    public const string LanguagesPath = ApiProblem.VersionPrefix + "/languages";

    public static IEndpointRouteBuilder MapLanguages(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(LanguagesPath, List)
            .WithName("ListLanguages")
            .WithSummary("Every language a Song's release details can name, by English name: each ISO 639-1 code (a BCP 47 primary language subtag) and zxx, No linguistic content.")
            .RequireScope(CredentialScopes.CatalogRead)
            .Produces<LanguageListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>200 with every language, by name.</summary>
    private static Ok<LanguageListResponse> List(HttpContext context)
    {
        SessionEndpoints.NoStore(context);

        return TypedResults.Ok(new LanguageListResponse([.. Languages.All.Select(static language => new LanguageResponse(language.Code, language.Name))]));
    }
}

/// <summary>Every language, by name.</summary>
internal sealed record LanguageListResponse(LanguageResponse[] Items);

/// <summary>A language: its code (as a Song stores it) and its English name.</summary>
internal sealed record LanguageResponse(string Code, string Name);
