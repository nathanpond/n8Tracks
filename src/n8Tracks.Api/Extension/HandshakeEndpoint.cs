using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Extension;

/// <summary>
/// <c>GET /api/v1/extension/handshake</c>: how the browser extension checks its token and learns
/// which n8Tracks it reached. Any valid token may call it, whatever its scopes; it is the one
/// endpoint that needs no particular scope. A revoked or unknown token gets the usual 401
/// <c>invalid_token</c>. A browser session has no credential to report, so it is refused with 403
/// <c>credential_required</c>.
/// </summary>
internal static class HandshakeEndpoint
{
    public const string Path = ApiProblem.VersionPrefix + "/extension/handshake";

    /// <summary>The product version of the extension making the handshake.</summary>
    public const string ExtensionVersionHeader = "X-N8Tracks-Extension-Version";

    /// <summary>The extension's Suno adapter version; recorded only.</summary>
    public const string AdapterVersionHeader = "X-N8Tracks-Adapter-Version";

    public const string CredentialRequiredCode = "credential_required";

    public static IEndpointRouteBuilder MapExtensionHandshake(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Path, HandshakeAsync)
            .WithName("ExtensionHandshake")
            .WithSummary("Checks an extension's token: answers the application version, the credential's name and scopes, and whether the extension's version (X-N8Tracks-Extension-Version) is compatible, and records the reported extension and adapter versions on the credential.")
            .AnyCaller()
            .Produces<HandshakeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>200 with the handshake. The token itself is never logged or echoed.</summary>
    private static async Task<Results<Ok<HandshakeResponse>, ProblemHttpResult>> HandshakeAsync(
        ExtensionHandshakeService handshakes,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SessionEndpoints.NoStore(context);

        var user = context.User;
        if (!CredentialPrincipal.IsCredential(user))
        {
            return ApiProblem.For(
                context,
                StatusCodes.Status403Forbidden,
                CredentialRequiredCode,
                "The handshake is for a credential's token, not a browser session.");
        }

        // FindFirst, not FindFirstValue: that extension lives in Microsoft.Extensions.Identity.Core,
        // and the server takes no dependency on ASP.NET Core Identity (SetupEndpointTests guards it).
        var credential = new VerifiedCredential(
            Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value, CultureInfo.InvariantCulture),
            user.FindFirst(ClaimTypes.Name)!.Value,
            user.FindFirst(BearerAuthenticationHandler.KindClaim)!.Value,
            [.. CredentialScopes.All.Where(CredentialPrincipal.Scopes(user).Contains)]);

        var handshake = await handshakes.HandshakeAsync(
            credential,
            Single(context.Request.Headers[ExtensionVersionHeader]),
            Single(context.Request.Headers[AdapterVersionHeader]),
            ProductVersion.Current,
            cancellationToken);

        return TypedResults.Ok(new HandshakeResponse(handshake.ApplicationVersion, handshake.CredentialName, handshake.Scopes, handshake.Compatible));
    }

    /// <summary>The header's one value; null when it is missing or repeated.</summary>
    private static string? Single(Microsoft.Extensions.Primitives.StringValues values) => values.Count == 1 ? values[0] : null;
}

/// <summary>The handshake answer.</summary>
internal sealed record HandshakeResponse(string ApplicationVersion, string CredentialName, IReadOnlyList<string> Scopes, bool Compatible);
