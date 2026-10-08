using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Search;

/// <summary>
/// <c>GET /api/v1/songs/{reference}/matches?search=</c> (#224): every place one Song matched, best first,
/// at most fifty, for the Songs table's "n more"; the refusals; and who may ask.
/// </summary>
public sealed class SongMatchesTests
{
    [Fact]
    public async Task ASongsMatchesStartWithTheListsThreeAndGoOnToFiftyWithTheirCount()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Zinnia", "zinnia field")).GetProperty("shortcode").GetString()!;
        var number = long.Parse(song[3..], CultureInfo.InvariantCulture);

        // 26 Versions with the word in their lyrics and styles: 52 rows, plus the title and Concept.
        for (var version = 2; version <= 27; version++)
        {
            _ = SongApi.AddVersionDirectly(factory.DataPath, number, version.ToString(CultureInfo.InvariantCulture), lyrics: "a zinnia verse", styles: "zinnia pop");
        }

        var listed = SearchApi.Item(await SearchApi.SearchAsync(client, "zinnia"), song);
        Assert.Equal(54, listed.GetProperty("matchCount").GetInt32());

        var all = await MatchesAsync(client, song, "zinnia");
        Assert.Equal(54, all.GetProperty("matchCount").GetInt32());
        var matches = all.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(50, matches.Count);

        // The first three are the list's, in its order: the best ranked.
        Assert.Equal(
            listed.GetProperty("matches").EnumerateArray().Select(static match => match.GetRawText()),
            matches.Take(3).Select(static match => match.GetRawText()));
        Assert.Equal(["title", "concept"], matches.Take(2).Select(static match => match.GetProperty("field").GetString()));
        Assert.All(matches.Skip(2), static match => Assert.Equal("version", match.GetProperty("owner").GetProperty("kind").GetString()));

        // By shortcode or by ID alike.
        var id = (await SearchApi.SongAsync(client, song)).GetProperty("id").GetString()!;
        Assert.Equal(all.GetRawText(), (await MatchesAsync(client, id, "zinnia")).GetRawText());
    }

    [Fact]
    public async Task ASongThatDoesNotMatchOrTextWithNoWordHasNoMatch()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Thistle")).GetProperty("shortcode").GetString()!;
        _ = await SongApi.CreateAsync(client, "Clover");

        Assert.Equal("""{"matches":[],"matchCount":0}""", (await MatchesAsync(client, song, "clover")).GetRawText());
        Assert.Equal("""{"matches":[],"matchCount":0}""", (await MatchesAsync(client, song, "( * )")).GetRawText());
        Assert.Equal("""{"matches":[],"matchCount":0}""", (await MatchesAsync(client, song, string.Empty)).GetRawText());

        // Complement: the word it has.
        Assert.Equal(1, (await MatchesAsync(client, song, "thistle")).GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task SearchIsRequiredOnceAndAnUnknownSongIsNotFound()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Bramble")).GetProperty("shortcode").GetString()!;

        using (var missing = await client.GetAsync(new Uri($"/api/v1/songs/{song}/matches", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(missing, HttpStatusCode.BadRequest, "invalid_request");
        }

        using (var twice = await client.GetAsync(new Uri($"/api/v1/songs/{song}/matches?search=a&search=b", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(twice, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var unknown = await client.GetAsync(new Uri("/api/v1/songs/n8-999/matches?search=bramble", UriKind.Relative));
        await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task MatchesNeedCatalogRead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, "Scoped")).GetProperty("shortcode").GetString()!;
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var uri = new Uri($"/api/v1/songs/{song}/matches?search=scoped", UriKind.Relative);

        using (var allowed = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, uri, reader))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal(1, (await SetupApi.JsonAsync(allowed)).GetProperty("matchCount").GetInt32());
        }

        using var refused = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, uri, writer);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>Invariant 6: the text searched for is not logged on this route either.</summary>
    [Fact]
    public async Task TheSearchTextNeverReachesTheLog()
    {
        const string Sentinel = "sentinelmatchesq4k8";
        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = (await SongApi.CreateAsync(client, $"Found {Sentinel}")).GetProperty("shortcode").GetString()!;
        var path = $"/api/v1/songs/{song}/matches";

        Assert.Equal(1, (await MatchesAsync(client, song, Sentinel)).GetProperty("matchCount").GetInt32());

        // Complement: the request's completion line was logged (path only), so the absence means something.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!factory.Lines().Any(line => line.GetProperty("properties").TryGetProperty("path", out var logged) && logged.GetString() == path))
        {
            Assert.True(DateTime.UtcNow < deadline, "The request's completion line was never logged.");
            await Task.Delay(25);
        }

        Assert.DoesNotContain(Sentinel, factory.CapturedText, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> MatchesAsync(HttpClient client, string reference, string search)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/{reference}/matches?search={Uri.EscapeDataString(search)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }
}
