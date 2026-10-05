using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.References;

/// <summary>
/// <c>GET /api/v1/resolve/{reference}</c>: a stable ID or a complete shortcode, in any letter case,
/// answered with what it names; anything else is 404 <c>reference_not_found</c>.
/// </summary>
public sealed class ResolveEndpointTests
{
    [Fact]
    public async Task ASongShortcodeOrIdResolvesToTheSong()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Resolved");
        var id = song.GetProperty("id").GetString()!;

        foreach (var reference in new[] { "n8-1", "N8-1", id, id.ToUpperInvariant() })
        {
            using var response = await client.GetAsync(Resolve(reference));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(
                ["entityType", "id", "shortcode", "status"],
                body.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal("song", body.GetProperty("entityType").GetString());
            Assert.Equal(id, body.GetProperty("id").GetString());
            Assert.Equal("n8-1", body.GetProperty("shortcode").GetString());
            Assert.Equal("active", body.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task AVersionShortcodeOrIdResolvesToTheVersionWithItsSongAndStatus()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Branches");
        var songId = song.GetProperty("id").GetString()!;
        var one = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        var archived = SongApi.AddVersionDirectly(factory.DataPath, 1, "1.1", VersionRecord.Archived).ToString();

        foreach (var reference in new[] { "n8-1-v1", "N8-1-V1", one, one.ToUpperInvariant() })
        {
            var body = await ResolvedAsync(client, reference);
            Assert.Equal("version", body.GetProperty("entityType").GetString());
            Assert.Equal(one, body.GetProperty("id").GetString());
            Assert.Equal("n8-1-v1", body.GetProperty("shortcode").GetString());
            Assert.Equal("active", body.GetProperty("status").GetString());
            Assert.Equal(songId, body.GetProperty("song").GetProperty("id").GetString());
            Assert.Equal("n8-1", body.GetProperty("song").GetProperty("shortcode").GetString());
        }

        foreach (var reference in new[] { "n8-1-v1.1", "N8-1-v1.1", archived })
        {
            var body = await ResolvedAsync(client, reference);
            Assert.Equal(archived, body.GetProperty("id").GetString(), ignoreCase: true);
            Assert.Equal("n8-1-v1.1", body.GetProperty("shortcode").GetString());
            Assert.Equal("archived", body.GetProperty("status").GetString());
        }
    }

    [Theory]
    [InlineData("n8-9999")]
    [InlineData("n8-1-v9")]
    [InlineData("n8-2-v1")]
    [InlineData("n8-012")]
    [InlineData("n8-")]
    [InlineData("n8-0")]
    [InlineData("n8-1-v")]
    [InlineData("n8-1-v1.0")]
    [InlineData("x8-1")]
    [InlineData("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b")]
    [InlineData("1")]
    public async Task AnUnknownOrMalformedReferenceIsReferenceNotFound(string reference)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Only one");

        using var response = await client.GetAsync(Resolve(reference));

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, ResolveEndpoints.ReferenceNotFoundCode);
        Assert.Equal("Nothing has that ID or shortcode.", problem.GetProperty("title").GetString());

        // Complement: the Song that exists resolves on the same host.
        Assert.Equal("n8-1", (await ResolvedAsync(client, "n8-1")).GetProperty("shortcode").GetString());
    }

    [Fact]
    public async Task ResolvingNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(setUp, "Scoped");
        using var client = factory.CreateClient();

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var allowed = await CredentialApi.SendAsync(client, HttpMethod.Get, Resolve("n8-1-v1"), reader))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Resolve("n8-1-v1"), others);
        var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
        Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
    }

    private static Uri Resolve(string reference) => new($"/api/v1/resolve/{reference}", UriKind.Relative);

    private static async Task<JsonElement> ResolvedAsync(HttpClient client, string reference)
    {
        using var response = await client.GetAsync(Resolve(reference));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{reference}: {await response.Content.ReadAsStringAsync()}");
        return await SetupApi.JsonAsync(response);
    }
}
