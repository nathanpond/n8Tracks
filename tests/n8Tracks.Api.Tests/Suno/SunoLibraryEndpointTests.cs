using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// The Suno playlists and personas the Sources editor offers (#125): read models over imported data,
/// empty until an import fills them (#137, #153), each answered by name, unpaged, to
/// <c>catalog.read</c>.
/// </summary>
public sealed class SunoLibraryEndpointTests
{
    private static readonly Uri Playlists = new("/api/v1/suno/playlists", UriKind.Relative);
    private static readonly Uri Personas = new("/api/v1/suno/personas", UriKind.Relative);

    [Fact]
    public async Task BothListsAreEmptyUntilAnImportHasRun()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        Assert.Equal("""{"items":[]}""", (await ReadAsync(client, Playlists)).GetRawText());
        Assert.Equal("""{"items":[]}""", (await ReadAsync(client, Personas)).GetRawText());
    }

    [Fact]
    public async Task PlaylistsAndPersonasSeenAreAnsweredByNameWithTheirMembers()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(factory.DataPath, """
            INSERT INTO suno_playlists (suno_id, name, clip_ids, last_seen_utc) VALUES
                ('pl-2', 'road trip', '["c1","c2","c3"]', '2026-10-01T10:00:00.000Z'),
                ('pl-1', 'Ambient', '[]', '2026-10-02T11:30:00.000Z'),
                ('pl-3', 'Zebra', '["c9"]', '2026-09-30T08:00:00.000Z');
            INSERT INTO suno_personas (suno_id, name, last_seen_utc) VALUES
                ('pe-2', 'Smoky', '2026-10-01T10:00:00.000Z'),
                ('pe-1', 'airy', '2026-10-01T10:00:00.000Z');
            """);

        var playlists = (await ReadAsync(client, Playlists)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Ambient", "road trip", "Zebra"], playlists.Select(static playlist => playlist.GetProperty("name").GetString()));
        var roadTrip = playlists[1];
        Assert.Equal("pl-2", roadTrip.GetProperty("id").GetString());
        Assert.Equal(3, roadTrip.GetProperty("memberCount").GetInt32());
        Assert.Equal(["c1", "c2", "c3"], roadTrip.GetProperty("clipIds").EnumerateArray().Select(static clip => clip.GetString()));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), roadTrip.GetProperty("lastSeen").GetDateTimeOffset());
        Assert.Equal(0, playlists[0].GetProperty("memberCount").GetInt32());

        Assert.Equal(
            """{"items":[{"id":"pe-1","name":"airy"},{"id":"pe-2","name":"Smoky"}]}""",
            (await ReadAsync(client, Personas)).GetRawText());
    }

    [Fact]
    public async Task ReadingNeedsCatalogReadAndASignedOutCallerIsRefused()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        using var client = factory.CreateClient();
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);

        foreach (var path in new[] { Playlists, Personas })
        {
            using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, path, reader))
            {
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            }

            // Complement: every other scope together is not enough, and signed out is refused.
            using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, path, others))
            {
                await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            }

            using (var signedOut = await client.GetAsync(path))
            {
                await SetupApi.ProblemAsync(signedOut, HttpStatusCode.Unauthorized, "not_authenticated");
            }
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Uri path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }
}
