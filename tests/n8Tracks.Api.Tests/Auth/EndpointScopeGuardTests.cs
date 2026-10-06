using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// Every <c>/api/v1</c> endpoint declares what a token needs to call it: exactly one of
/// <c>RequireScope(...)</c>, <c>SessionOnly()</c>, or <c>AllowAnonymous()</c>. The one exception is
/// the API's own 404 fallback, marked <c>AnyCaller()</c>, and no other endpoint may carry that.
/// </summary>
public sealed class EndpointScopeGuardTests
{
    private const string ApiNotFoundPattern = "/api/v1/{**path}";

    [Fact]
    public void EveryApiEndpointDeclaresExactlyOneScopeMarker()
    {
        using var factory = new N8TracksApiFactory();
        var endpoints = ApiEndpoints(factory);

        Assert.Empty(Violations(endpoints));

        // Complement: the check looked at the endpoints there are, of each kind.
        var markers = endpoints.ToDictionary(Describe, Marker, StringComparer.Ordinal);
        Assert.Equal("anonymous", markers["GET /api/v1/setup/status"]);
        Assert.Equal("anonymous", markers["POST /api/v1/setup"]);
        Assert.Equal("anonymous", markers["POST /api/v1/session"]);
        Assert.Equal("session-only", markers["GET /api/v1/session"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/session"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/sessions"]);
        Assert.Equal("session-only", markers["POST /api/v1/account/password"]);
        Assert.Equal("scope", markers["GET /api/v1/jobs"]);
        Assert.Equal("scope", markers["GET /api/v1/jobs/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/songs"]);
        Assert.Equal("scope", markers["GET /api/v1/songs"]);
        Assert.Equal("scope", markers["GET /api/v1/songs/{reference}"]);
        Assert.Equal("scope", markers["PATCH /api/v1/songs/{reference}"]);
        Assert.Equal("scope", markers["GET /api/v1/languages"]);
        Assert.Equal("scope", markers["GET /api/v1/genres"]);
        Assert.Equal("scope", markers["POST /api/v1/genres"]);
        Assert.Equal("session-only", markers["PATCH /api/v1/genres/{id:guid}"]);
        Assert.Equal("session-only", markers["POST /api/v1/genres/{id:guid}/merge"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/genres/{id:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/tags"]);
        Assert.Equal("scope", markers["POST /api/v1/tags"]);
        Assert.Equal("session-only", markers["PATCH /api/v1/tags/{id:guid}"]);
        Assert.Equal("session-only", markers["POST /api/v1/tags/{id:guid}/merge"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/tags/{id:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/artists"]);
        Assert.Equal("scope", markers["GET /api/v1/artists/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/artists"]);
        Assert.Equal("scope", markers["PATCH /api/v1/artists/{id:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/albums"]);
        Assert.Equal("scope", markers["GET /api/v1/albums/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/albums"]);
        Assert.Equal("scope", markers["PATCH /api/v1/albums/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/albums/{id:guid}/tracks"]);
        Assert.Equal("scope", markers["DELETE /api/v1/albums/{id:guid}/tracks/{reference}"]);
        Assert.Equal("scope", markers["PUT /api/v1/albums/{id:guid}/tracks"]);
        Assert.Equal("scope", markers["GET /api/v1/playlists"]);
        Assert.Equal("scope", markers["GET /api/v1/playlists/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/playlists"]);
        Assert.Equal("scope", markers["PATCH /api/v1/playlists/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/playlists/{id:guid}/songs"]);
        Assert.Equal("scope", markers["DELETE /api/v1/playlists/{id:guid}/songs/{reference}"]);
        Assert.Equal("scope", markers["PUT /api/v1/playlists/{id:guid}/songs"]);
        Assert.Equal("scope", markers["GET /api/v1/relationship-types"]);
        Assert.Equal("session-only", markers["POST /api/v1/relationship-types"]);
        Assert.Equal("session-only", markers["PATCH /api/v1/relationship-types/{id:guid}"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/relationship-types/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/songs/{reference}/relationships"]);
        Assert.Equal("scope", markers["DELETE /api/v1/songs/{reference}/relationships/{id:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/artwork"]);
        Assert.Equal("scope", markers["GET /api/v1/artwork/{assetId:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/artwork/{assetId:guid}/{size}"]);
        Assert.Equal("scope", markers["GET /api/v1/artwork/{assetId:guid}/crops/{cropKey}/{size}"]);
        Assert.Equal("scope", markers["GET /api/v1/versions/{reference}/next-numbers"]);
        Assert.Equal("scope", markers["GET /api/v1/songs/{reference}/versions"]);
        Assert.Equal("scope", markers["POST /api/v1/songs/{reference}/versions"]);
        Assert.Equal("scope", markers["PUT /api/v1/songs/{reference}/current-version"]);
        Assert.Equal("scope", markers["PATCH /api/v1/versions/{reference}"]);
        Assert.Equal("scope", markers["GET /api/v1/versions/{reference}"]);
        Assert.Equal("scope", markers["POST /api/v1/versions/{reference}/snapshots"]);
        Assert.Equal("scope", markers["GET /api/v1/versions/{reference}/snapshots"]);
        Assert.Equal("scope", markers["GET /api/v1/versions/{reference}/snapshots/{snapshotId:guid}"]);
        Assert.Equal("scope", markers["POST /api/v1/versions/{reference}/snapshots/{snapshotId:guid}/restore"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/versions/{reference}/snapshots/{snapshotId:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/resolve/{reference}"]);
        Assert.Equal("scope", markers["GET /api/v1/suno/create-fields"]);
        Assert.Equal("scope", markers["GET /api/v1/suno/models"]);
        Assert.Equal("session-only", markers["POST /api/v1/suno/models"]);
        Assert.Equal("session-only", markers["PATCH /api/v1/suno/models/{id:guid}"]);
        Assert.Equal("session-only", markers["PUT /api/v1/suno/models/order"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/suno/models/{id:guid}"]);
        Assert.Equal("scope", markers["GET /api/v1/settings/version-defaults"]);
        Assert.Equal("session-only", markers["PUT /api/v1/settings/version-defaults"]);
        Assert.Equal("session-only", markers["GET /api/v1/settings/catalog"]);
        Assert.Equal("session-only", markers["PUT /api/v1/settings/catalog"]);
        Assert.Equal("scope", markers["PUT /api/v1/songs/{reference}/credits"]);
        Assert.Equal("scope", markers["GET /api/v1/workflow-states"]);
        Assert.Equal("session-only", markers["POST /api/v1/workflow-states"]);
        Assert.Equal("session-only", markers["PATCH /api/v1/workflow-states/{id:guid}"]);
        Assert.Equal("session-only", markers["PUT /api/v1/workflow-states/order"]);
        Assert.Equal("session-only", markers["DELETE /api/v1/workflow-states/{id:guid}"]);
        Assert.Equal("anonymous", markers["GET /api/v1/maintenance"]);
        Assert.Equal("session-only", markers["POST /api/v1/restores/validate"]);
        Assert.Equal("session-only", markers["POST /api/v1/restores/uploads"]);
        Assert.Equal("session-only", markers["POST /api/v1/restores"]);
        Assert.Equal("any-caller", markers["* " + ApiNotFoundPattern]);
    }

    /// <summary>Proves the check bites: an endpoint added to the test host without a marker fails it.</summary>
    [Fact]
    public void AnUnmarkedEndpointInTheTestHostFailsTheCheck()
    {
        using var factory = TestEndpoints.Host(withUnmarked: true);

        Assert.Equal(["GET /api/v1/test/unmarked: no scope marker"], Violations(ApiEndpoints(factory)));

        // Complement: the same host without it passes, its scoped endpoints included.
        using var marked = TestEndpoints.Host(withUnmarked: false);
        var endpoints = ApiEndpoints(marked);
        Assert.Contains(endpoints, static endpoint => endpoint.RoutePattern.RawText == TestEndpoints.BulkWrite.OriginalString);
        Assert.Empty(Violations(endpoints));
    }

    [Fact]
    public void TwoMarkersOnOneEndpointAndAnyCallerOutsideTheFallbackAreReported()
    {
        using var factory = TestEndpoints.Host(static endpoints =>
        {
            endpoints.MapGet("/api/v1/test/both", static () => "x").RequireScope(CredentialScopes.CatalogRead).SessionOnly();
            endpoints.MapGet("/api/v1/test/open", static () => "x").AnyCaller();
            endpoints.MapGet("/api/v1/test/fine", static () => "x").AllowAnonymous();
        });

        Assert.Equal(
            ["GET /api/v1/test/both: 2 scope markers", "GET /api/v1/test/open: AnyCaller() is for the API's 404 only"],
            Violations(ApiEndpoints(factory)));
    }

    [Fact]
    public void AScopeMarkerNeedsAtLeastOneKnownScope()
    {
        Assert.Throws<ArgumentException>(static () => new RequiredScopes([]));
        Assert.Throws<ArgumentException>(static () => new RequiredScopes(["catalog.write"]));
        Assert.Throws<ArgumentException>(static () => new RequiredScopes(["Catalog.Read"]));

        // Complement: known scopes are kept, each once.
        Assert.Equal(
            [CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite],
            new RequiredScopes([CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite, CredentialScopes.SongsWrite]).Scopes);
    }

    /// <summary>
    /// The same, from outside: a token holding every scope is refused every session-only endpoint
    /// with 403 <c>session_required</c>, and is ignored on every anonymous one.
    /// </summary>
    [Fact]
    public async Task ATokenWithEveryScopeIsRefusedEverySessionOnlyEndpointAndIgnoredOnAnonymousOnes()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        var sessionOnly = 0;
        foreach (var endpoint in ApiEndpoints(factory).Where(static endpoint => Marker(endpoint) == "session-only"))
        {
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                using var response = await CredentialApi.SendAsync(client, new HttpMethod(method), Concrete(endpoint), token);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {endpoint.RoutePattern.RawText}: {response.StatusCode}");
                await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, SessionOnlyMiddleware.RequiredCode);
                sessionOnly++;
            }
        }

        Assert.Equal(38, sessionOnly);

        // An anonymous endpoint answers as it would without the header, even to a token that is not one.
        using var status = await CredentialApi.SendRawAsync(client, HttpMethod.Get, SetupApi.Status, "Bearer not-a-token");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var signIn = await CredentialApi.SendAsync(client, HttpMethod.Post, SessionApi.Session, token);
        await SetupApi.ProblemAsync(signIn, HttpStatusCode.Forbidden, AntiforgeryHeaderMiddleware.RequiredCode);
    }

    /// <summary>The endpoint's route with each parameter filled in by a new UUID, which every one of them accepts.</summary>
    private static Uri Concrete(RouteEndpoint endpoint)
    {
        var segments = endpoint.RoutePattern.PathSegments.Select(static segment => string.Concat(segment.Parts.Select(static part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            _ => Guid.CreateVersion7().ToString(),
        })));

        return new Uri("/" + string.Join('/', segments), UriKind.Relative);
    }

    private static List<RouteEndpoint> ApiEndpoints(N8TracksApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)];

    private static List<string> Violations(IEnumerable<RouteEndpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var count = Markers(endpoint).Count;
            if (count == 0)
            {
                violations.Add($"{Describe(endpoint)}: no scope marker");
            }
            else if (count > 1)
            {
                violations.Add($"{Describe(endpoint)}: {count} scope markers");
            }
            else if (Marker(endpoint) == "any-caller" && endpoint.RoutePattern.RawText != ApiNotFoundPattern)
            {
                violations.Add($"{Describe(endpoint)}: AnyCaller() is for the API's 404 only");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    private static List<string> Markers(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var markers = new List<string>();
        if (metadata.GetMetadata<RequiredScopes>() is not null)
        {
            markers.Add("scope");
        }

        if (metadata.GetMetadata<SessionOnlyEndpoint>() is not null)
        {
            markers.Add("session-only");
        }

        if (metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            markers.Add("anonymous");
        }

        if (metadata.GetMetadata<AnyCallerEndpoint>() is not null)
        {
            markers.Add("any-caller");
        }

        return markers;
    }

    private static string Marker(RouteEndpoint endpoint) => string.Join('+', Markers(endpoint));

    private static string Describe(RouteEndpoint endpoint) =>
        $"{(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? string.Join(',', methods) : "*")} {endpoint.RoutePattern.RawText}";
}
