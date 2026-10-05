using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using n8Tracks.Api.Tests.Setup;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// Nobody can see or change the catalog without signing in: every mapped endpoint needs a session
/// except the few on <see cref="AllowList"/>. An endpoint is anonymous only by an explicit
/// <c>AllowAnonymous</c>; anything else falls under the fallback policy.
/// </summary>
public sealed class AnonymousEndpointGuardTests
{
    /// <summary>The only endpoints open without a session, as "METHOD pattern".</summary>
    private static readonly HashSet<string> AllowList = new(StringComparer.Ordinal)
    {
        "GET /health",
        "HEAD /health",
        "GET /api/v1/setup/status",
        "POST /api/v1/setup",
        "POST /api/v1/session",
        "GET /api/v1/maintenance",
    };

    [Fact]
    public void OnlyTheAllowListedEndpointsPermitAnonymousAccessAndTheFallbackPolicyRequiresASession()
    {
        using var factory = new N8TracksApiFactory();
        var endpoints = Endpoints(factory);

        Assert.Empty(AnonymousOutsideTheAllowList(endpoints));

        // Complement: the allow-list is not stale; every entry is a mapped, anonymous endpoint.
        Assert.Equal(AllowList.Order(StringComparer.Ordinal), Anonymous(endpoints).Order(StringComparer.Ordinal));

        var fallback = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;
        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>Proves the check bites: an extra anonymous endpoint is reported.</summary>
    [Fact]
    public void AnAnonymousEndpointOutsideTheAllowListIsReported()
    {
        var sneaky = new RouteEndpointBuilder(static _ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/songs"), 0);
        sneaky.Metadata.Add(new HttpMethodMetadata(["GET"]));
        sneaky.Metadata.Add(new AllowAnonymousAttribute());
        var guarded = new RouteEndpointBuilder(static _ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/songs/{id}"), 0);
        guarded.Metadata.Add(new HttpMethodMetadata(["GET"]));

        Assert.Equal(["GET /api/v1/songs"], AnonymousOutsideTheAllowList([(RouteEndpoint)sneaky.Build(), (RouteEndpoint)guarded.Build()]));
    }

    /// <summary>
    /// The same, from outside: after setup, every mapped endpoint and method not on the allow-list
    /// answers an anonymous request (anti-forgery header included) with 401 <c>not_authenticated</c>.
    /// </summary>
    [Fact]
    public async Task EveryOtherEndpointAnswersAnAnonymousRequestWith401()
    {
        using var factory = new N8TracksApiFactory();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        var checkedRoutes = 0;
        foreach (var endpoint in Endpoints(factory))
        {
            foreach (var method in Methods(endpoint))
            {
                if (AllowList.Contains($"{method} {endpoint.RoutePattern.RawText}"))
                {
                    continue;
                }

                using var response = await SessionApi.SendAsync(client, new HttpMethod(method), new Uri(Concrete(endpoint.RoutePattern), UriKind.Relative));
                Assert.True(
                    response.StatusCode == HttpStatusCode.Unauthorized,
                    $"{method} {endpoint.RoutePattern.RawText} answered {response.StatusCode} without a session.");
                checkedRoutes++;
            }
        }

        // Complement: the session endpoints and the API's own 404 were among them.
        Assert.True(checkedRoutes >= 4, $"Only {checkedRoutes} endpoints were checked.");
    }

    private static List<RouteEndpoint> Endpoints(N8TracksApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static List<string> Anonymous(IEnumerable<RouteEndpoint> endpoints) =>
        [.. endpoints
            .Where(static endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(static endpoint => Methods(endpoint).Select(method => $"{method} {endpoint.RoutePattern.RawText}"))];

    private static List<string> AnonymousOutsideTheAllowList(IEnumerable<RouteEndpoint> endpoints) =>
        [.. Anonymous(endpoints).Where(static entry => !AllowList.Contains(entry))];

    /// <summary>The methods an endpoint answers; one with no method metadata answers them all, checked as GET and POST.</summary>
    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? [.. methods] : ["GET", "POST"];

    /// <summary>A path the pattern matches, with every parameter given a value.</summary>
    private static string Concrete(RoutePattern pattern) =>
        "/" + string.Join('/', pattern.PathSegments.Select(static segment => string.Concat(segment.Parts.Select(static part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart => "0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
            _ => string.Empty,
        }))));
}
