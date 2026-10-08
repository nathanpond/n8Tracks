using System.Net;
using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Infrastructure.Logging;

namespace n8Tracks.Api.Tests.Search;

/// <summary>
/// <c>GET /api/v1/songs?search=</c> (#223): what a word, a phrase, and several words match, how results
/// are ordered and excerpted, what a query the search engine could not parse does, and who may search.
/// </summary>
public sealed class SearchQueryTests
{
    [Fact]
    public async Task WordsMatchWholeWordsAndWordBeginningsIgnoringCaseAndDiacriticsAndPhrasesMatchInOrder()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var nocturne = await CreateAsync(client, "Café Nocturne", lyrics: "the quick brown fox");
        var cafeteria = await CreateAsync(client, "Cafeteria Blues", concept: "A slow fox at closing time");
        var planX = await CreateAsync(client, "Plan X");
        var xylophone = await CreateAsync(client, "Xylophone");

        // Diacritics and case either way, and word beginnings.
        Assert.Equal([nocturne, cafeteria], Sorted(await SearchApi.FoundAsync(client, "cafe")));
        Assert.Equal([nocturne, cafeteria], Sorted(await SearchApi.FoundAsync(client, "CAFÉ")));
        Assert.Equal([nocturne], await SearchApi.FoundAsync(client, "noct"));
        Assert.Empty(await SearchApi.FoundAsync(client, "octurne"));

        // A word of one character matches whole words only.
        Assert.Equal([planX], await SearchApi.FoundAsync(client, "x"));
        Assert.Equal([xylophone], await SearchApi.FoundAsync(client, "xy"));

        // A phrase matches its words in order, the last one whole.
        Assert.Equal([nocturne], await SearchApi.FoundAsync(client, "\"brown fox\""));
        Assert.Empty(await SearchApi.FoundAsync(client, "\"fox brown\""));
        Assert.Empty(await SearchApi.FoundAsync(client, "\"brown fo\""));

        // Every word must match, in any field of the same Song.
        Assert.Equal([nocturne], await SearchApi.FoundAsync(client, "nocturne fox"));
        Assert.Equal([cafeteria], await SearchApi.FoundAsync(client, "fox blues"));
        Assert.Empty(await SearchApi.FoundAsync(client, "quick blues"));
    }

    [Fact]
    public async Task ResultsAreRankedTitleThenConceptThenElsewhereAndSayWhereTheyMatched()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var inLyrics = await CreateAsync(client, "Night Drive", lyrics: "a lantern swinging in the dark");
        var inConcept = await CreateAsync(client, "Harbour", concept: "Lantern light on the water");
        var inTitle = await CreateAsync(client, "Lantern");
        var everywhere = await CreateAsync(client, "Lantern Song", concept: "lantern", lyrics: "lantern verse", styles: "lantern pop");
        await SearchApi.EditVersionAsync(client, $"{everywhere}-v1", JsonSerializer.Serialize(new { name = "lantern take", notes = "lantern notes" }));

        var list = await SearchApi.SearchAsync(client, "lantern");
        var found = SongApi.Shortcodes(list);
        Assert.Equal(4, found.Count);
        Assert.Equal([inConcept, inLyrics], found[2..]);
        Assert.Equal(Sorted([everywhere, inTitle]), Sorted(found[..2]));

        // At most three matches, best first (the title), with how many there are in all.
        var all = SearchApi.Item(list, everywhere);
        Assert.Equal(6, all.GetProperty("matchCount").GetInt32());
        Assert.Equal(3, all.GetProperty("matches").GetArrayLength());
        Assert.Equal(["title", "concept"], SearchApi.Fields(all)[..2]);
        Assert.False(list.GetProperty("indexRebuilding").GetBoolean());

        // The excerpt marks the matched word by offsets.
        var lyrics = SearchApi.Item(list, inLyrics).GetProperty("matches")[0];
        Assert.Equal("lyrics", lyrics.GetProperty("field").GetString());
        Assert.Equal("a lantern swinging in the dark", lyrics.GetProperty("excerpt").GetProperty("text").GetString());
        Assert.Equal("""[{"start":2,"length":7}]""", lyrics.GetProperty("excerpt").GetProperty("highlights").GetRawText());
        Assert.Equal("version", lyrics.GetProperty("owner").GetProperty("kind").GetString());
        Assert.Equal($"{inLyrics}-v1", lyrics.GetProperty("owner").GetProperty("reference").GetString());
        Assert.Equal("v1", lyrics.GetProperty("owner").GetProperty("label").GetString());

        // A sort chosen wins over relevance.
        Assert.Equal([inLyrics, inConcept, inTitle, everywhere], SongApi.Shortcodes(await SearchApi.SearchAsync(client, "lantern", "&sort=updated&direction=asc")));

        // Without search, the list has none of the search fields.
        var plain = await SongApi.ListAsync(client);
        Assert.False(plain.TryGetProperty("indexRebuilding", out _));
        Assert.All(plain.GetProperty("items").EnumerateArray(), static item => Assert.False(item.TryGetProperty("matches", out _) || item.TryGetProperty("matchCount", out _)));
    }

    [Fact]
    public async Task ALongFieldIsExcerptedAroundItsFirstMatchAndArchivedAndTrashedOwnersAreMarked()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var lyrics = string.Join(' ', Enumerable.Repeat("la", 150)) + " marigold " + string.Join(' ', Enumerable.Repeat("da", 150));
        var song = await CreateAsync(client, "Long one", lyrics: lyrics);

        var excerpt = SearchApi.Item(await SearchApi.SearchAsync(client, "marigold"), song).GetProperty("matches")[0].GetProperty("excerpt");
        var text = excerpt.GetProperty("text").GetString()!;
        Assert.Equal(160, text.Length);
        var highlight = Assert.Single(excerpt.GetProperty("highlights").EnumerateArray());
        Assert.Equal("marigold", text.Substring(highlight.GetProperty("start").GetInt32(), highlight.GetProperty("length").GetInt32()));
        Assert.InRange(highlight.GetProperty("start").GetInt32(), 60, 90);

        // Everything live is searched, archived and trashed text included, and marked. Both are
        // written straight into the database, outside the unit of work: a search still finds them.
        var archivedSong = await CreateAsync(client, "Archived one");
        _ = SongApi.AddVersionDirectly(factory.DataPath, long.Parse(archivedSong[3..], System.Globalization.CultureInfo.InvariantCulture), "2", visibility: "archived", lyrics: "Bluebell hollow");
        var archived = SearchApi.Item(await SearchApi.SearchAsync(client, "bluebell"), archivedSong).GetProperty("matches")[0].GetProperty("owner");
        Assert.Equal($"{archivedSong}-v2", archived.GetProperty("reference").GetString());
        Assert.Equal("archived", archived.GetProperty("state").GetString());

        var trashed = await CreateAsync(client, "Trashed one");
        _ = await SongApi.AttachGenerationAsync(factory, $"{trashed}-v1", SearchApi.Clip("search-trashed-1", title: "Periwinkle"));
        Persistence.TestDatabase.Execute(factory.DataPath, "UPDATE generations SET remote_state = 'trashed', state = 'archived', archived_by = 'sync' WHERE suno_id = 'search-trashed-1';");
        var owner = SearchApi.Item(await SearchApi.SearchAsync(client, "periwinkle"), trashed).GetProperty("matches")[0].GetProperty("owner");
        Assert.Equal("generation", owner.GetProperty("kind").GetString());
        Assert.Equal($"{trashed}-v1-g1", owner.GetProperty("reference").GetString());
        Assert.Equal("v1-g1", owner.GetProperty("label").GetString());
        Assert.Equal("trashed", owner.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("(")]
    [InlineData("*")]
    [InlineData("-")]
    [InlineData("^")]
    [InlineData("\"\"")]
    [InlineData("( ) * : ^ + -")]
    public async Task AQueryWithNoWordListsEverySongWithoutAnError(string query)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = await CreateAsync(client, "Alpha");
        var two = await CreateAsync(client, "Beta");

        var list = await SearchApi.SearchAsync(client, query);
        Assert.Equal([one, two], Sorted(SongApi.Shortcodes(list)));
        Assert.All(list.GetProperty("items").EnumerateArray(), static item => Assert.False(item.TryGetProperty("matches", out _)));
        Assert.False(list.GetProperty("indexRebuilding").GetBoolean());
    }

    [Theory]
    [InlineData("AND", "Rock and roll")]
    [InlineData("a OR", "Either a or b")]
    [InlineData("NEAR(stone", "Near stone circles")]
    [InlineData("\"unbalanced stone", "An unbalanced stone")]
    [InlineData("stone*", "Stonehenge")]
    [InlineData("title:stone", "Title stone")]
    [InlineData("{stone}", "Rolling stone")]
    [InlineData("-stone", "Stone cold")]
    [InlineData("stone)", "Stone cold")]
    public async Task OperatorsAndPunctuationAreSearchedAsPlainWords(string query, string title)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var match = await CreateAsync(client, title);
        _ = await CreateAsync(client, "Unrelated");

        var found = await SearchApi.FoundAsync(client, query);
        Assert.Contains(match, found);
        Assert.Single(found);
    }

    [Fact]
    public async Task AVeryLongQueryIsCutAndNeverErrs()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await CreateAsync(client, "Ember");

        var other = await CreateAsync(client, "Other");

        // 2,000 characters: the first 200 are read, so a word past them is not searched.
        var cut = "ember" + new string(' ', 200) + string.Concat(Enumerable.Repeat("zz ", 600));
        Assert.Equal(2_005, cut.Length);
        Assert.Equal([song], await SearchApi.FoundAsync(client, cut));
        Assert.Empty(await SearchApi.FoundAsync(client, string.Concat(Enumerable.Repeat("ember zz ", 223))));
        Assert.Equal(Sorted([song, other]), Sorted(await SearchApi.FoundAsync(client, string.Concat(Enumerable.Repeat("(\"* ", 500)))));
    }

    [Fact]
    public async Task SearchAndTheTitleLookupAreNotCombinedAndSearchIsGivenOnce()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var both = await client.GetAsync(new Uri("/api/v1/songs?search=a&q=a", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(both, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var twice = await client.GetAsync(new Uri("/api/v1/songs?search=a&search=b", UriKind.Relative));
        await SetupApi.ProblemAsync(twice, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task SearchNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        _ = await CreateAsync(client, "Scoped");
        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var writer = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.SongsWrite);
        var uri = new Uri("/api/v1/songs?search=scoped", UriKind.Relative);

        using (var allowed = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, uri, reader))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Single((await SetupApi.JsonAsync(allowed)).GetProperty("items").EnumerateArray());
        }

        using var refused = await CredentialApi.SendAsync(factory.CreateClient(), HttpMethod.Get, uri, writer);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>Invariant 6: what is searched for is never written to the log, at any level.</summary>
    [Fact]
    public async Task TheSearchTextNeverReachesTheLog()
    {
        const string Sentinel = "sentinelsearchq7f2";
        using var factory = new LoggingApiFactory("Debug");
        using var client = await SessionApi.SignedInClientAsync(factory);
        _ = await CreateAsync(client, $"Found {Sentinel}");

        // Found by the word (so the search ran), then a query no index could parse, then a refused one.
        Assert.Single(await SearchApi.FoundAsync(client, Sentinel));
        _ = await SearchApi.SearchAsync(client, $"\"{Sentinel} (");
        using (var refused = await client.GetAsync(new Uri($"/api/v1/songs?search={Sentinel}&q={Sentinel}", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        // Complement: the three requests' completion lines were logged (path only), so the absence means something.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (factory.Lines().Count(static line => line.GetProperty("properties").TryGetProperty("path", out var path) && path.GetString() == "/api/v1/songs") < 4)
        {
            Assert.True(DateTime.UtcNow < deadline, "The requests' completion lines were never logged.");
            await Task.Delay(25);
        }

        Assert.DoesNotContain(Sentinel, factory.CapturedText, StringComparison.Ordinal);
        Assert.True(RedactionPolicy.IsSensitive("search"));
        Assert.True(RedactionPolicy.IsSensitive("Search"));
        Assert.Equal($"search={RedactionPolicy.Redacted}", RedactionPolicy.ScrubText($"search={Sentinel}"));
    }

    /// <summary>Creates a Song with the text given in its first Version; its shortcode.</summary>
    private static async Task<string> CreateAsync(HttpClient client, string title, string? concept = null, string? lyrics = null, string? styles = null)
    {
        var song = await SongApi.CreateAsync(client, title, concept);
        var shortcode = song.GetProperty("shortcode").GetString()!;
        if (lyrics is not null || styles is not null)
        {
            await SearchApi.EditVersionAsync(client, $"{shortcode}-v1", JsonSerializer.Serialize(new { lyrics = lyrics ?? string.Empty, styles = styles ?? string.Empty }));
        }

        return shortcode;
    }

    private static List<string> Sorted(IEnumerable<string> shortcodes) => [.. shortcodes.Order(StringComparer.Ordinal)];
}
