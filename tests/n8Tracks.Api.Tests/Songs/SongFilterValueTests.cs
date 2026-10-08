using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>#225: <c>GET /api/v1/songs/filter-values</c>, the values the filter pickers offer.</summary>
public sealed class SongFilterValueTests
{
    [Fact]
    public async Task EachKindOffersTheValuesThatExistByNameWithTagColours()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await SongFilterTests.FilterCatalog.SeedAsync(factory, client);

        Assert.Equal(["Folk", "Rock"], Names(await ValuesAsync(client, "kind=genre")));
        var tags = await ValuesAsync(client, "kind=tag");
        Assert.Equal(["night", "road"], Names(tags));
        Assert.Equal(["teal", "blue"], tags.Select(static tag => tag.GetProperty("colour").GetString()!));
        Assert.Equal([catalog.Night, catalog.Road], tags.Select(static tag => tag.GetProperty("id").GetString()!));
        Assert.Equal(["First Album"], Names(await ValuesAsync(client, "kind=album")));
        Assert.Equal(["Road Trip"], Names(await ValuesAsync(client, "kind=playlist")));

        // A Genre has no colour, so none is sent.
        Assert.False((await ValuesAsync(client, "kind=genre"))[0].TryGetProperty("colour", out _));
    }

    [Fact]
    public async Task ModelsAreThoseLiveGenerationsReportNamedByTheModelListWhenOneMatches()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongFilterTests.FilterCatalog.SeedAsync(factory, client);

        // v3 and v4.5 are reported; the model list names a model reported as v4.5.
        TestDatabase.Execute(factory.DataPath, "UPDATE suno_models SET reported_as = 'v4.5' WHERE position = 1;");
        var named = TestDatabase.Scalar(factory.DataPath, "SELECT name FROM suno_models WHERE position = 1;");

        var models = await ValuesAsync(client, "kind=model");
        var byId = models.ToDictionary(static model => model.GetProperty("id").GetString()!, static model => model.GetProperty("name").GetString()!);
        Assert.Equal(["v3", "v4.5"], byId.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("v3", byId["v3"]);
        Assert.Equal(named, byId["v4.5"]);
    }

    [Fact]
    public async Task AQueryMatchesWordBeginningsIgnoringCaseAndDiacritics()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            """
            INSERT INTO genres (id, name, name_key, revision) VALUES
              ('01A10E00-0000-7000-8000-000000000001', 'Indie Folk', 'indie folk', 1),
              ('01A10E00-0000-7000-8000-000000000002', 'Folktronica', 'folktronica', 1),
              ('01A10E00-0000-7000-8000-000000000003', 'Neofolk', 'neofolk', 1),
              ('01A10E00-0000-7000-8000-000000000004', 'Électro-pop', 'électro-pop', 1);
            """);

        Assert.Equal(["Folktronica", "Indie Folk"], Names(await ValuesAsync(client, "kind=genre&query=FOLK")));
        Assert.Equal(["Indie Folk"], Names(await ValuesAsync(client, "kind=genre&query=fo%20in")));
        Assert.Equal(["Électro-pop"], Names(await ValuesAsync(client, "kind=genre&query=electro%20pop")));
        Assert.Empty(await ValuesAsync(client, "kind=genre&query=olk"));
        Assert.Equal(4, (await ValuesAsync(client, "kind=genre&query=%20")).Count);
    }

    [Fact]
    public async Task AtMostFiftyAreOfferedSoALargeCatalogIsSearchedOnTheServer()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            "INSERT INTO tags (id, name, name_key, colour, revision) VALUES "
                + string.Join(", ", Enumerable.Range(1, 60).Select(static n => string.Create(CultureInfo.InvariantCulture, $"('01A10E00-0000-7000-8000-{n:D12}', 'tag {n:D2}', 'tag {n:D2}', 'gray', 1)")))
                + ";");

        var first = await ValuesAsync(client, "kind=tag");
        Assert.Equal(SongFilterValueService.Limit, first.Count);
        Assert.Equal("tag 01", Names(first)[0]);
        Assert.Equal(["tag 60"], Names(await ValuesAsync(client, "kind=tag&query=60")));
    }

    [Fact]
    public async Task IdsNameTheValuesThatStillExistAndLeaveOutDeletedOnes()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await SongFilterTests.FilterCatalog.SeedAsync(factory, client);
        var gone = Guid.CreateVersion7();

        Assert.Equal(["Folk", "Rock"], Names(await ValuesAsync(client, $"kind=genre&ids={catalog.Rock}&ids={catalog.Folk}&ids={gone}")));
        Assert.Equal(["First Album"], Names(await ValuesAsync(client, $"kind=album&ids={catalog.Album}")));
        Assert.Empty(await ValuesAsync(client, $"kind=playlist&ids={gone}"));
        Assert.Equal(["v3"], Names(await ValuesAsync(client, "kind=model&ids=v3&ids=v9")));
    }

    [Theory]
    [InlineData("", "kind")]
    [InlineData("kind=state", "kind")]
    [InlineData("kind=genre&kind=tag", "more than once")]
    [InlineData("kind=genre&query=a&query=b", "more than once")]
    [InlineData("kind=genre&ids=folk", "ids")]
    [InlineData("kind=model&ids=", "ids")]
    [InlineData("kind=genre&ids=01a10e00-0000-7000-8000-000000000001&query=f", "ids")]
    public async Task ABadRequestIs400NamingTheParameter(string query, string named)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs/filter-values?{query}", UriKind.Relative));
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Contains(named, problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WordsAreFoldedAndMatchedOnTheirBeginnings()
    {
        Assert.Equal(["electro", "pop", "2"], SongFilterValueService.WordsOf("Électro-Pop 2"));
        Assert.True(SongFilterValueService.Matches("Indie Folk", ["fo", "ind"]));
        Assert.False(SongFilterValueService.Matches("Indie Folk", ["olk"]));
        Assert.True(SongFilterValueService.Matches("anything", []));
    }

    private static async Task<List<JsonElement>> ValuesAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/songs/filter-values?{query}", UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static List<string> Names(List<JsonElement> values) =>
        [.. values.Select(static value => value.GetProperty("name").GetString()!)];
}
