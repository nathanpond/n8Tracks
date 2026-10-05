using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Auth;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Auth;

/// <summary>
/// Creates credentials through the application service, as the management screens will, and calls
/// the API with their tokens. Later stories use <see cref="CreateTokenAsync"/> for a token holding
/// the scopes their endpoint needs.
/// </summary>
internal static class CredentialApi
{
    /// <summary>Creates a credential of kind <c>api</c> with <paramref name="scopes"/> and returns its token.</summary>
    public static async Task<string> CreateTokenAsync(N8TracksApiFactory factory, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var outcome = await CreateAsync(factory, new CredentialRequest("test " + Guid.NewGuid().ToString("N")[..8], CredentialKinds.Api, scopes));
        return Assert.IsType<CredentialOutcome.Created>(outcome).Token;
    }

    public static async Task<CredentialOutcome> CreateAsync(N8TracksApiFactory factory, CredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CredentialService>().CreateAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// A request with <c>Authorization: Bearer <paramref name="token"/></c> and no anti-forgery header.
    /// A POST, PUT, or PATCH carries an empty JSON object, so it reaches an endpoint that reads a body.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Uri uri, string token)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);

        using var request = new HttpRequestMessage(method, uri);
        if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
        {
            request.Content = JsonContent.Create(new { });
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>A request with exactly the <c>Authorization</c> value given, unchecked.</summary>
    public static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, Uri uri, string authorization)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, uri);
        Assert.True(request.Headers.TryAddWithoutValidation("Authorization", authorization));
        return await client.SendAsync(request);
    }
}

/// <summary>
/// Endpoints the test host adds under <c>/api/v1/test/</c>, behind the real pipeline, because no
/// real endpoint needs a scope yet. Each answers 200 (201 for a write) with the caller's name.
/// </summary>
internal static class TestEndpoints
{
    public static readonly Uri Read = new("/api/v1/test/read", UriKind.Relative);
    public static readonly Uri Write = new("/api/v1/test/songs", UriKind.Relative);
    public static readonly Uri BulkWrite = new("/api/v1/test/songs/bulk", UriKind.Relative);
    public static readonly Uri Unmarked = new("/api/v1/test/unmarked", UriKind.Relative);

    /// <summary>The key under which routing keeps the app's endpoint route builder (internal to the framework).</summary>
    private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

    /// <summary>A host with the scoped test endpoints and, when <paramref name="withUnmarked"/>, one with no marker.</summary>
    public static N8TracksApiFactory Host(bool withUnmarked = true, TimeProvider? clock = null) =>
        new()
        {
            TestServices = services =>
            {
                Register(services, withUnmarked);
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
            },
        };

    /// <summary>Adds the scoped test endpoints to a host's services (and the unmarked one when <paramref name="withUnmarked"/>).</summary>
    public static void Register(IServiceCollection services, bool withUnmarked = true) =>
        services.AddSingleton<IStartupFilter>(new Mapper(endpoints => Map(endpoints, withUnmarked)));

    /// <summary>A host with only <paramref name="map"/>'s endpoints added.</summary>
    public static N8TracksApiFactory Host(Action<IEndpointRouteBuilder> map) =>
        new() { TestServices = services => services.AddSingleton<IStartupFilter>(new Mapper(map)) };

    private static void Map(IEndpointRouteBuilder endpoints, bool withUnmarked)
    {
        endpoints.MapGet(Read.OriginalString, Caller).RequireScope(CredentialScopes.CatalogRead);
        endpoints.MapPost(Write.OriginalString, Created).RequireScope(CredentialScopes.SongsWrite);
        endpoints.MapPost(BulkWrite.OriginalString, Created).RequireScope(CredentialScopes.SongsWrite, CredentialScopes.CatalogBulkWrite);
        if (withUnmarked)
        {
            endpoints.MapGet(Unmarked.OriginalString, Caller);
        }
    }

    private static IResult Caller(HttpContext context) => Results.Ok(new { caller = context.User.Identity?.Name });

    /// <summary>A write answers with the record it wrote, as a real write endpoint does.</summary>
    private static IResult Created(HttpContext context) => Results.Created((string?)null, new { title = "A song", caller = context.User.Identity?.Name });

    /// <summary>
    /// Adds endpoints to the application's own route builder once its pipeline is configured and
    /// before the first request, so they are matched and enumerated like the app's own.
    /// </summary>
    private sealed class Mapper(Action<IEndpointRouteBuilder> map) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                next(app);
                var endpoints = Assert.IsAssignableFrom<IEndpointRouteBuilder>(app.Properties[EndpointRouteBuilderKey]);
                map(endpoints);
            };
    }
}
