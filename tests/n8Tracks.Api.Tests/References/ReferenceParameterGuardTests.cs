using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.References;
using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.References;

/// <summary>
/// Every <c>/api/v1</c> endpoint that names a Song or a Version accepts its shortcode as well as its
/// stable ID. Two checks: one reads the endpoints (a Song or Version in a route is bound as a
/// <see cref="CatalogReference"/>, and no handler or request body takes one as a bare UUID), and one
/// calls every endpoint that binds a reference, by ID and by shortcode, and fails if a new one has
/// no call here.
/// </summary>
public sealed class ReferenceParameterGuardTests
{
    /// <summary>UUID parameters that name something other than a Song or a Version.</summary>
    private static readonly HashSet<string> OtherIds = new(StringComparer.Ordinal)
    {
        "GET /api/v1/jobs/{id:guid}: id",
        "PATCH /api/v1/credentials/{id:guid}: id",
        "POST /api/v1/credentials/{id:guid}/revoke: id",
        "PATCH /api/v1/workflow-states/{id:guid}: id",
        "DELETE /api/v1/workflow-states/{id:guid}: id",
        "PATCH /api/v1/suno/models/{id:guid}: id",
        "DELETE /api/v1/suno/models/{id:guid}: id",
        "PATCH /api/v1/genres/{id:guid}: id",
        "POST /api/v1/genres/{id:guid}/merge: id",
        "DELETE /api/v1/genres/{id:guid}: id",
        "PATCH /api/v1/tags/{id:guid}: id",
        "POST /api/v1/tags/{id:guid}/merge: id",
        "DELETE /api/v1/tags/{id:guid}: id",
        "GET /api/v1/artists/{id:guid}: id",
        "PATCH /api/v1/artists/{id:guid}: id",
        "GET /api/v1/albums/{id:guid}: id",
        "PATCH /api/v1/albums/{id:guid}: id",
        "GET /api/v1/playlists/{id:guid}: id",
        "PATCH /api/v1/playlists/{id:guid}: id",
        "POST /api/v1/playlists/{id:guid}/songs: id",
        "DELETE /api/v1/playlists/{id:guid}/songs/{reference}: id",
        "PUT /api/v1/playlists/{id:guid}/songs: id",
        "POST /api/v1/albums/{id:guid}/tracks: id",
        "DELETE /api/v1/albums/{id:guid}/tracks/{reference}: id",
        "PUT /api/v1/albums/{id:guid}/tracks: id",
        "PATCH /api/v1/relationship-types/{id:guid}: id",
        "DELETE /api/v1/relationship-types/{id:guid}: id",
        "DELETE /api/v1/songs/{reference}/relationships/{id:guid}: id",
        "GET /api/v1/versions/{reference}/snapshots/{snapshotId:guid}: snapshotId",
        "POST /api/v1/versions/{reference}/snapshots/{snapshotId:guid}/restore: snapshotId",
        "DELETE /api/v1/versions/{reference}/snapshots/{snapshotId:guid}: snapshotId",
        "GET /api/v1/artwork/{assetId:guid}: assetId",
        "GET /api/v1/artwork/{assetId:guid}/{size}: assetId",
        "GET /api/v1/artwork/{assetId:guid}/crops/{cropKey}/{size}: assetId",
    };

    /// <summary>
    /// One call per endpoint that binds a reference, given a Song reference and a Version reference
    /// in the same form, which every one of them answers the same way each time it is called.
    /// </summary>
    private static readonly Dictionary<string, Func<Context, string, string, Task<HttpResponseMessage>>> Calls = new(StringComparer.Ordinal)
    {
        ["GET /api/v1/songs/{reference}"] = static (c, song, _) => c.SendAsync(HttpMethod.Get, $"songs/{song}"),
        ["PATCH /api/v1/songs/{reference}"] = static async (c, song, _) =>
            await c.SendAsync(HttpMethod.Patch, $"songs/{song}", "{}", await c.SongRevisionAsync()),
        ["GET /api/v1/songs/{reference}/versions"] = static (c, song, _) => c.SendAsync(HttpMethod.Get, $"songs/{song}/versions"),

        // 200: the Song is credited to no one already, so nothing changes.
        ["PUT /api/v1/songs/{reference}/credits"] = static async (c, song, _) =>
            await c.SendAsync(HttpMethod.Put, $"songs/{song}/credits", """{"primaryArtistId":null,"featuredArtistIds":[]}""", await c.SongRevisionAsync()),

        // 422 validation_failed: the Song was found, and is named as its own other Song.
        ["POST /api/v1/songs/{reference}/relationships"] = static (c, song, _) =>
            c.SendAsync(HttpMethod.Post, $"songs/{song}/relationships", $$"""{"typeId":"{{SystemRelationshipTypes.Cover.Id}}","direction":"forward","otherSong":"{{song}}"}"""),

        // 200: a relationship made for the call (by ID, so it is there whatever the reference) is removed.
        ["DELETE /api/v1/songs/{reference}/relationships/{id:guid}"] = static async (c, song, _) =>
            await c.SendAsync(HttpMethod.Delete, $"songs/{song}/relationships/{await c.RelationshipAsync()}"),

        // 422 version_number_not_offered: the Song and the source were both found, and nothing is stored.
        ["POST /api/v1/songs/{reference}/versions"] = static (c, song, version) =>
            c.SendAsync(HttpMethod.Post, $"songs/{song}/versions", $$"""{"sourceVersionId":"{{version}}","number":"99"}"""),
        ["PUT /api/v1/songs/{reference}/current-version"] = static (c, song, version) =>
            c.SendAsync(HttpMethod.Put, $"songs/{song}/current-version", $$"""{"versionId":"{{version}}"}"""),
        ["GET /api/v1/versions/{reference}"] = static (c, _, version) => c.SendAsync(HttpMethod.Get, $"versions/{version}"),
        ["PATCH /api/v1/versions/{reference}"] = static async (c, _, version) =>
            await c.SendAsync(HttpMethod.Patch, $"versions/{version}", "{}", await c.VersionRevisionAsync()),
        ["GET /api/v1/versions/{reference}/next-numbers"] = static (c, _, version) => c.SendAsync(HttpMethod.Get, $"versions/{version}/next-numbers"),

        // 200: the text is the newest snapshot's already, so nothing new is kept.
        ["POST /api/v1/versions/{reference}/snapshots"] = static (c, _, version) =>
            c.SendAsync(HttpMethod.Post, $"versions/{version}/snapshots", """{"lyrics":"","styles":""}"""),
        ["GET /api/v1/versions/{reference}/snapshots"] = static (c, _, version) => c.SendAsync(HttpMethod.Get, $"versions/{version}/snapshots"),
        ["GET /api/v1/versions/{reference}/snapshots/{snapshotId:guid}"] = static (c, _, version) =>
            c.SendAsync(HttpMethod.Get, $"versions/{version}/snapshots/{c.SnapshotId}"),
        ["POST /api/v1/versions/{reference}/snapshots/{snapshotId:guid}/restore"] = static async (c, _, version) =>
            await c.SendAsync(HttpMethod.Post, $"versions/{version}/snapshots/{c.SnapshotId}/restore", "{}", await c.VersionRevisionAsync()),

        // 204: a history entry taken for the call (by ID, so it is there whatever the reference) is deleted.
        ["DELETE /api/v1/versions/{reference}/snapshots/{snapshotId:guid}"] = static async (c, _, version) =>
            await c.SendAsync(HttpMethod.Delete, $"versions/{version}/snapshots/{await c.HistoryEntryAsync()}"),
        ["GET /api/v1/resolve/{reference}"] = static (c, _, version) => c.SendAsync(HttpMethod.Get, $"resolve/{version}"),

        // 200: the Song is not on the Playlist, so nothing changes.
        ["DELETE /api/v1/playlists/{id:guid}/songs/{reference}"] = static async (c, song, _) =>
            await c.SendAsync(HttpMethod.Delete, $"playlists/{c.PlaylistId}/songs/{song}", revision: await c.PlaylistRevisionAsync()),

        // 200: the Song is not on the Album, so nothing changes.
        ["DELETE /api/v1/albums/{id:guid}/tracks/{reference}"] = static async (c, song, _) =>
            await c.SendAsync(HttpMethod.Delete, $"albums/{c.AlbumId}/tracks/{song}", revision: await c.AlbumRevisionAsync()),
    };

    [Fact]
    public void EverySongOrVersionParameterIsBoundAsAReference()
    {
        using var factory = new N8TracksApiFactory();

        Assert.Empty(Violations(ApiEndpoints(factory)));
    }

    /// <summary>Proves the structural check bites: a Version taken as a bare UUID is reported, in a route and in a body.</summary>
    [Fact]
    public void AnEndpointTakingAVersionAsAUuidFailsTheCheck()
    {
        using var factory = TestEndpoints.Host(static endpoints =>
        {
            endpoints.MapGet("/api/v1/versions/{id:guid}/test", static (Guid id) => id.ToString());
            endpoints.MapPost("/api/v1/test/by-body", static (UuidBody body) => body.VersionId.ToString());
            endpoints.MapGet("/api/v1/songs/{title}/test", static (string title) => title);
        });

        Assert.Equal(
            [
                "GET /api/v1/songs/{title}/test: title is not bound as a CatalogReference",
                "GET /api/v1/versions/{id:guid}/test: id is a UUID",
                "GET /api/v1/versions/{id:guid}/test: id is not bound as a CatalogReference",
                "POST /api/v1/test/by-body: body field VersionId is a UUID",
            ],
            Violations(ApiEndpoints(factory)).Where(static violation => violation.Contains("test", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EveryEndpointTakingAReferenceAnswersAShortcodeExactlyAsItsId()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Referenced");
        await SongApi.CreateAsync(client, "Another");
        var songId = song.GetProperty("id").GetString()!;
        var versionId = song.GetProperty("currentVersion").GetProperty("id").GetString()!;
        var context = new Context(client, songId, versionId);
        using (var first = await context.SendAsync(HttpMethod.Post, $"versions/{versionId}/snapshots", """{"lyrics":"","styles":""}"""))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            context.SnapshotId = (await SetupApi.JsonAsync(first)).GetProperty("id").GetString()!;
        }

        using (var playlist = await context.SendAsync(HttpMethod.Post, "playlists", """{"title":"Referenced"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, playlist.StatusCode);
            context.PlaylistId = (await SetupApi.JsonAsync(playlist)).GetProperty("id").GetString()!;
        }

        using (var album = await context.SendAsync(HttpMethod.Post, "albums", """{"title":"Referenced"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, album.StatusCode);
            context.AlbumId = (await SetupApi.JsonAsync(album)).GetProperty("id").GetString()!;
        }

        // Every endpoint that binds a reference has a call here, and every call is to such an endpoint.
        var bound = ApiEndpoints(factory).Where(static endpoint => Handler(endpoint)?.GetParameters()
            .Any(static parameter => parameter.ParameterType == typeof(CatalogReference)) == true)
            .Select(Describe)
            .Order(StringComparer.Ordinal);
        Assert.Equal(bound, Calls.Keys.Order(StringComparer.Ordinal));

        foreach (var (endpoint, call) in Calls)
        {
            using var byId = await call(context, songId, versionId);
            using var byShortcode = await call(context, "N8-1", "N8-1-V1");
            Assert.True(byId.StatusCode != HttpStatusCode.NotFound, $"{endpoint} by ID: {await byId.Content.ReadAsStringAsync()}");
            Assert.True(
                byShortcode.StatusCode == byId.StatusCode,
                $"{endpoint}: {byId.StatusCode} by ID, {byShortcode.StatusCode} by shortcode: {await byShortcode.Content.ReadAsStringAsync()}");

            // Complement: a shortcode of the other kind, or an unknown Version shortcode, is not found, never a wrong-entity result.
            if (endpoint.Contains("/resolve/", StringComparison.Ordinal))
            {
                continue;
            }

            using var wrongKind = await call(context, "n8-1-v1", "n8-1");
            await SetupApi.ProblemAsync(wrongKind, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
            if (endpoint.Contains("/versions/", StringComparison.Ordinal))
            {
                using var otherSongs = await call(context, songId, "n8-2-v9");
                await SetupApi.ProblemAsync(otherSongs, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
            }
        }
    }

    [Fact]
    public async Task ABodyFieldNamingAVersionOfAnotherKindOrSongIsAFieldError()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Mine");
        await SongApi.CreateAsync(client, "Theirs");

        foreach (var reference in new[] { "n8-1", "n8-2-v1", "n8-1-v9", "nonsense" })
        {
            using var current = await SongApi.SendJsonAsync(client, HttpMethod.Put, new Uri("/api/v1/songs/n8-1/current-version", UriKind.Relative), $$"""{"versionId":"{{reference}}"}""");
            var problem = await SetupApi.ProblemAsync(current, HttpStatusCode.UnprocessableEntity, ApiProblem.ValidationFailedCode);
            Assert.True(problem.GetProperty("errors").TryGetProperty("versionId", out _), reference);
        }

        // Complement: the Song's own Version, by shortcode in any case, is taken.
        using var own = await SongApi.SendJsonAsync(client, HttpMethod.Put, new Uri("/api/v1/songs/n8-1/current-version", UriKind.Relative), """{"versionId":"N8-1-v1"}""");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    private static List<RouteEndpoint> ApiEndpoints(N8TracksApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)];

    private static MethodInfo? Handler(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<MethodInfo>();

    private static List<string> Violations(IEnumerable<RouteEndpoint> endpoints)
    {
        var violations = new List<string>();
        foreach (var endpoint in endpoints)
        {
            if (Handler(endpoint) is not { } handler)
            {
                continue;
            }

            var describe = Describe(endpoint);
            var parameters = handler.GetParameters();
            foreach (var parameter in parameters)
            {
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                if (type == typeof(Guid) && !OtherIds.Contains($"{describe}: {parameter.Name}"))
                {
                    violations.Add($"{describe}: {parameter.Name} is a UUID");
                }

                if (type.Assembly != typeof(Guid).Assembly && type.IsClass)
                {
                    violations.AddRange(type.GetProperties()
                        .Where(static property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid)
                            && (property.Name.Contains("Song", StringComparison.Ordinal) || property.Name.Contains("Version", StringComparison.Ordinal)))
                        .Select(property => $"{describe}: body field {property.Name} is a UUID"));
                }
            }

            // The first parameter under /songs/ or /versions/ names the Song or the Version.
            var path = endpoint.RoutePattern.RawText!;
            if ((path.StartsWith("/api/v1/songs/", StringComparison.Ordinal) || path.StartsWith("/api/v1/versions/", StringComparison.Ordinal))
                && endpoint.RoutePattern.Parameters.FirstOrDefault() is { } named
                && parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, named.Name, StringComparison.OrdinalIgnoreCase))?.ParameterType != typeof(CatalogReference))
            {
                violations.Add($"{describe}: {named.Name} is not bound as a CatalogReference");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? string.Join(',', methods) : "*")} {endpoint.RoutePattern.RawText}";

    /// <summary>A body that names a Version by a bare UUID: what the check must refuse.</summary>
    private sealed record UuidBody(Guid VersionId);

    /// <summary>The signed-in client and the records the calls work on.</summary>
    private sealed class Context(HttpClient client, string songId, string versionId)
    {
        public string SnapshotId { get; set; } = string.Empty;

        public string PlaylistId { get; set; } = string.Empty;

        public string AlbumId { get; set; } = string.Empty;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, int? revision = null)
        {
            using var request = new HttpRequestMessage(method, new Uri("/api/v1/" + path, UriKind.Relative));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            if (revision is { } value)
            {
                Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(value)));
            }

            return await client.SendAsync(request);
        }

        public Task<int> SongRevisionAsync() => RevisionAsync($"songs/{songId}");

        /// <summary>Relates the Song to the second one under Cover, by ID, and answers the relationship's ID.</summary>
        public async Task<string> RelationshipAsync()
        {
            using var response = await SendAsync(HttpMethod.Post, $"songs/{songId}/relationships", $$"""{"typeId":"{{SystemRelationshipTypes.Cover.Id}}","direction":"forward","otherSong":"n8-2"}""");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await SetupApi.JsonAsync(response)).GetProperty("relationships")[0].GetProperty("id").GetString()!;
        }

        /// <summary>Takes a snapshot of the Version, by ID, and answers its ID (the existing one when the text is the newest's).</summary>
        public async Task<string> HistoryEntryAsync()
        {
            using var response = await SendAsync(HttpMethod.Post, $"versions/{versionId}/snapshots", """{"lyrics":"Deleted by the guard","styles":""}""");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await SetupApi.JsonAsync(response)).GetProperty("id").GetString()!;
        }

        public Task<int> VersionRevisionAsync() => RevisionAsync($"versions/{versionId}");

        public Task<int> PlaylistRevisionAsync() => RevisionAsync($"playlists/{PlaylistId}");

        public Task<int> AlbumRevisionAsync() => RevisionAsync($"albums/{AlbumId}");

        private async Task<int> RevisionAsync(string path)
        {
            using var response = await SendAsync(HttpMethod.Get, path);
            return (await SetupApi.JsonAsync(response)).GetProperty("revision").GetInt32();
        }
    }
}
