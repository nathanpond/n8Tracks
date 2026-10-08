using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// #226: the Songs list's six sorts, both directions, over one catalog with ties and missing values,
/// asserting the whole order; paging that never skips or repeats; relevance with and without a
/// search; the title order key; and the upgrade and retention shape that carry it.
/// </summary>
public sealed class SongSortTests
{
    /// <summary>
    /// The six keys of #226, each with the whole order of <see cref="SortCatalog"/> ascending and
    /// descending (shortcode numbers). A key added to the story without a row here, or a
    /// <see cref="SongSort"/> member that is neither here nor in <see cref="CoveredElsewhere"/>, fails
    /// <see cref="EveryKeyOfTheStoryIsAcceptedAndHasACase"/>.
    /// </summary>
    private static readonly Dictionary<string, (int[] Ascending, int[] Descending)> Keys = new(StringComparer.Ordinal)
    {
        // "Apple" (quoted), banana, Éclair = eclair (tie), song 2, Song 10: case, accents, quotes, digits.
        ["title"] = ([4, 6, 3, 5, 2, 1], [1, 2, 3, 5, 6, 4]),

        // 2 on 01-01; 4 and 6 tie on 01-02; 1 and 3 tie on 01-03; 5 on 01-04.
        ["created"] = ([2, 4, 6, 1, 3, 5], [5, 1, 3, 4, 6, 2]),

        // 1 and 3 tie on 02-01; 4 and 6 tie on 02-02; 2 on 02-03; 5 on 02-04.
        ["updated"] = ([1, 3, 4, 6, 2, 5], [5, 2, 4, 6, 1, 3]),

        // 1 and 4 rated 4, 2 rated 5; 3 and 6 have no Generation, 5 one never rated: last both ways.
        ["rating"] = ([1, 4, 2, 3, 5, 6], [2, 1, 4, 3, 5, 6]),

        // Idea (2, 3), Writing (1, 5), Final (4), Archived (6): the states' order, not their names'.
        ["state"] = ([2, 3, 1, 5, 4, 6], [6, 4, 1, 5, 2, 3]),

        // 5 on 03-01 (attached; no Suno date), 1 and 4 tie on 03-05 (Suno's), 2 on 03-07 (its later
        // Generation's Suno date is earlier, so its earlier one wins); 3 and 6 have none: last both ways.
        ["lastGeneration"] = ([5, 1, 4, 2, 3, 6], [2, 1, 4, 5, 3, 6]),
    };

    /// <summary>The <see cref="SongSort"/> members whose orders other tests assert: #211's audio files and the search's relevance.</summary>
    private static readonly SongSort[] CoveredElsewhere = [SongSort.AudioFiles, SongSort.Relevance];

    [Fact]
    public void EveryKeyOfTheStoryIsAcceptedAndHasACase()
    {
        Assert.Equal(["created", "lastGeneration", "rating", "state", "title", "updated"], Keys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [SongService.SortCreated, SongService.SortLastGeneration, SongService.SortRating, SongService.SortState, SongService.SortTitle, SongService.SortUpdated],
            Keys.Keys.Order(StringComparer.Ordinal));

        // Every member of the query's sort is walked here or named as covered elsewhere.
        var walked = Keys.Keys.Select(static key => Enum.Parse<SongSort>(key, ignoreCase: true)).ToHashSet();
        Assert.All(Enum.GetValues<SongSort>(), sort => Assert.True(walked.Contains(sort) || CoveredElsewhere.Contains(sort), $"{sort} is not walked."));
    }

    [Fact]
    public async Task EachKeyOrdersTheWholeCatalogInBothDirectionsWithTiesByShortcodeAndMissingValuesLast()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);

        foreach (var (key, order) in Keys)
        {
            Assert.True(Codes(order.Ascending).SequenceEqual(SongApi.Shortcodes(await SongApi.ListAsync(client, $"sort={key}&direction=asc"))), $"{key} ascending");
            Assert.True(Codes(order.Descending).SequenceEqual(SongApi.Shortcodes(await SongApi.ListAsync(client, $"sort={key}&direction=desc"))), $"{key} descending");
        }
    }

    [Fact]
    public async Task EachKeyStartsInItsOwnDirectionAndARequestAlwaysGivesTheSameOrder()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);

        // Titles A to Z and states in the user's order; dates newest first and ratings highest first.
        Assert.Equal(Codes(Keys["title"].Ascending), SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=title")));
        Assert.Equal(Codes(Keys["state"].Ascending), SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=state")));
        foreach (var key in new[] { "created", "updated", "rating", "lastGeneration" })
        {
            Assert.Equal(Codes(Keys[key].Descending), SongApi.Shortcodes(await SongApi.ListAsync(client, $"sort={key}")));
        }

        // No sort: updated, newest first (#59).
        Assert.Equal(Codes(Keys["updated"].Descending), SongApi.Shortcodes(await SongApi.ListAsync(client)));

        // The same request, again: the same order.
        foreach (var key in Keys.Keys)
        {
            Assert.Equal(SongApi.Shortcodes(await SongApi.ListAsync(client, $"sort={key}")), SongApi.Shortcodes(await SongApi.ListAsync(client, $"sort={key}")));
        }
    }

    [Fact]
    public async Task EveryPageInTurnListsEachSongExactlyOnceForEveryKeyAndAPagePastTheEndIsEmptyWithTheTotal()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);

        foreach (var (key, order) in Keys)
        {
            foreach (var (direction, expected) in new[] { ("asc", order.Ascending), ("desc", order.Descending) })
            {
                foreach (var pageSize in new[] { 1, 2, 4 })
                {
                    var walked = new List<string>();
                    for (var page = 1; ; page++)
                    {
                        var list = await SongApi.ListAsync(client, $"sort={key}&direction={direction}&pageSize={pageSize}&page={page}");
                        Assert.Equal(6, list.GetProperty("total").GetInt32());
                        var codes = SongApi.Shortcodes(list);
                        if (codes.Count == 0)
                        {
                            break;
                        }

                        walked.AddRange(codes);
                    }

                    Assert.True(Codes(expected).SequenceEqual(walked), $"{key} {direction} by {pageSize}: {string.Join(", ", walked)}");
                }
            }
        }

        // Past the end: an empty page with the right total, not an error.
        var past = await SongApi.ListAsync(client, "sort=rating&page=9&pageSize=2");
        Assert.Empty(SongApi.Shortcodes(past));
        Assert.Equal(6, past.GetProperty("total").GetInt32());
        Assert.Equal(9, past.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task SortingByStateFollowsTheOrderTheUserGaveTheStates()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);

        // Final moved to the front: its Song comes first, then Idea's, then Writing's, Archived last.
        var ids = DefaultWorkflowStates.All.Select(static state => state.Id).ToList();
        List<Guid> reordered = [DefaultWorkflowStates.Final.Id, .. ids.Where(static id => id != DefaultWorkflowStates.Final.Id)];
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/workflow-states/order", UriKind.Relative)))
        {
            request.Content = new StringContent(JsonSerializer.Serialize(new { ids = reordered }), System.Text.Encoding.UTF8, "application/json");
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(1));
            using var response = await client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(Codes([4, 2, 3, 1, 5, 6]), SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=state")));
        Assert.Equal(Codes([6, 1, 5, 2, 3, 4]), SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=state&direction=desc")));
    }

    [Fact]
    public async Task RelevanceIsTheSearchsDefaultAnyChosenSortReplacesItAndWithoutASearchItIsTheDefaultOrder()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);
        var updated = Codes(Keys["updated"].Descending);

        // Every Song's concept holds "harbour".
        var byRelevance = SongApi.Shortcodes(await SongApi.ListAsync(client, "search=harbour"));
        Assert.Equal(6, byRelevance.Count);

        // "relevance" named is the same; a direction with it is ignored.
        Assert.Equal(byRelevance, SongApi.Shortcodes(await SongApi.ListAsync(client, "search=harbour&sort=relevance")));
        Assert.Equal(byRelevance, SongApi.Shortcodes(await SongApi.ListAsync(client, "search=harbour&sort=relevance&direction=asc")));

        // A chosen sort replaces it, whole and in order.
        Assert.Equal(updated, SongApi.Shortcodes(await SongApi.ListAsync(client, "search=harbour&sort=updated")));
        Assert.Equal(Codes(Keys["rating"].Ascending), SongApi.Shortcodes(await SongApi.ListAsync(client, "search=harbour&sort=rating&direction=asc")));

        // Without a search, relevance is the default order, whatever direction is sent.
        Assert.Equal(updated, SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=relevance")));
        Assert.Equal(updated, SongApi.Shortcodes(await SongApi.ListAsync(client, "sort=relevance&direction=asc")));
    }

    [Theory]
    [InlineData("sort=bogus")]
    [InlineData("sort=Rating")]
    [InlineData("sort=lastgeneration")]
    [InlineData("sort=state&direction=down")]
    [InlineData("sort=relevance&direction=sideways")]
    public async Task AnUnknownSortKeyOrDirectionIs400NamingTheKeys(string query)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));

        var problem = (await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, ApiProblem.InvalidRequestCode)).ToString();
        Assert.True(problem.Contains("sort", StringComparison.Ordinal) || problem.Contains("direction", StringComparison.Ordinal), problem);
    }

    [Fact]
    public async Task ASongAnswersItsHighestRating()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SortCatalog.SeedAsync(factory, client);

        var ratings = (await SongApi.ListAsync(client, "sort=title&direction=desc")).GetProperty("items").EnumerateArray()
            .Select(static song => song.GetProperty("highestRating") is { ValueKind: JsonValueKind.Number } rating ? rating.GetInt32() : (int?)null)
            .ToList();

        // 1, 2, 3, 5, 6, 4 (title descending).
        Assert.Equal(new int?[] { 4, 5, null, null, null, 4 }, ratings);
    }

    [Theory]
    [InlineData("Song 2", "song 0012")]
    [InlineData("Song 10", "song 00210")]
    [InlineData("Track 007", "track 0017")]
    [InlineData("  \"Éclair\"  au   Café ", "eclair\" au cafe")]
    [InlineData("‘Hello’", "hello")]
    [InlineData("Why?", "why")]
    [InlineData("...And Then", "and then")]
    [InlineData("The Road", "the road")]
    [InlineData("ÅNGSTRÖM", "angstrom")]
    [InlineData("!!!", "!!!")]
    [InlineData("0", "0010")]
    public void TheTitleOrderKeyFoldsCaseAndAccentsIgnoresLeadingPunctuationAndOrdersDigitsNaturally(string title, string key)
    {
        Assert.Equal(key, SongRules.TitleOrderKey(title));
    }

    [Fact]
    public void TitleOrderKeysSortNaturally()
    {
        string[] titles = ["Song 10", "song 2", "Song 1", "\"Apple\"", "apple pie", "Éclair", "eclair 3", "Zebra", "10 Years", "9 Lives"];

        var ordered = titles.OrderBy(SongRules.TitleOrderKey, StringComparer.Ordinal).ToList();

        Assert.Equal(["9 Lives", "10 Years", "\"Apple\"", "apple pie", "Éclair", "eclair 3", "Song 1", "song 2", "Song 10", "Zebra"], ordered);
    }

    [Fact]
    public async Task ATitleEditRewritesTheOrderKey()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Song 9");
        Assert.Equal("song 0019", TestDatabase.Scalar(factory.DataPath, "SELECT title_order_key FROM songs;"));

        await SongApi.EditAsync(client, song.GetProperty("id").GetString()!, 1, """{"title":"“Ölmez” 12"}""");

        Assert.Equal("olmez” 00212", TestDatabase.Scalar(factory.DataPath, "SELECT title_order_key FROM songs;"));
    }

    [Fact]
    public async Task UpgradingKeysEveryExistingSongByTheApplicationsRuleAndAddsTheIndexes()
    {
        using var directory = new TemporaryDirectory();
        SqliteDatabase.CreateIfMissing(TestDatabase.FilePath(directory.Path));
        var builder = new DbContextOptionsBuilder<N8TracksDbContext>();
        builder.UseN8TracksSqlite(TestDatabase.FilePath(directory.Path));
        var options = builder.Options;
        await using (var context = new N8TracksDbContext(options))
        {
            var before = context.Database.GetMigrations().Single(static id => id.EndsWith("_AddSongFilterIndexes", StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(before);
        }

        var titles = new[] { "Song 10", "« Été »", "song 2" };
        for (var index = 0; index < titles.Length; index++)
        {
            TestDatabase.Execute(
                directory.Path,
                $"""
                INSERT INTO songs (id, shortcode_number, title, title_sort_key, title_key, workflow_state_id, created_utc, updated_utc, revision)
                VALUES ('{Guid.CreateVersion7().ToString().ToUpperInvariant()}', {index + 1}, '{titles[index]}', 'x', 'x', '{DefaultWorkflowStates.Idea.Id.ToString().ToUpperInvariant()}', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
                """);
        }

        await using (var context = new N8TracksDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        Assert.Equal([.. titles.Select(SongRules.TitleOrderKey)], TestDatabase.Rows(directory.Path, "SELECT title_order_key FROM songs ORDER BY shortcode_number;"));
        Assert.Equal(["n8-2", "n8-3", "n8-1"], TestDatabase.Rows(directory.Path, "SELECT 'n8-' || shortcode_number FROM songs ORDER BY title_order_key, shortcode_number;"));
        var indexes = TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'index';");
        Assert.Contains("ix_songs_title_order_key_shortcode_number", indexes);
        Assert.Contains("ix_generations_song_id_rating", indexes);
        Assert.Contains("ix_generations_song_id_suno_created_utc_created_utc", indexes);

        // No table was rebuilt: the search triggers on songs and generations are still there.
        var triggers = TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'trigger';");
        Assert.Contains("tr_songs_search_update", triggers);
        Assert.Contains("tr_generations_search_update", triggers);
    }

    [Fact]
    public void ASongRetainedBeforeTheOrderKeyRestoresWithTheKeyOfItsTitle()
    {
        var shape3 = new JsonObject { ["id"] = "A", ["title"] = "Song 10", ["suno_workspace_id"] = null };

        var shape4 = RetainedTypes.SongShape3To4(shape3);

        Assert.Equal("song 00210", shape4["title_order_key"]?.GetValue<string>());
        Assert.Equal(4, RetainedTypes.Song.ShapeVersion);
        Assert.True(RetainedTypes.Song.Upgraders.ContainsKey(3));
    }

    private static List<string> Codes(int[] numbers) => [.. numbers.Select(static number => $"n8-{number}")];

    /// <summary>
    /// Six Songs whose values are set by hand, so every key has a tie and the rating and last
    /// Generation keys have Songs with no value (see <see cref="Keys"/>).
    /// </summary>
    private static class SortCatalog
    {
        private static readonly (string Title, string Created, string Updated, WorkflowState State, (int? Rating, string? SunoCreated, string Attached)[] Generations)[] Songs =
        [
            ("Song 10", "2026-01-03", "2026-02-01", DefaultWorkflowStates.Writing, [(4, "2026-03-05", "2026-04-01")]),
            ("song 2", "2026-01-01", "2026-02-03", DefaultWorkflowStates.Idea, [(5, null, "2026-03-07"), (null, "2026-03-02", "2026-03-09")]),
            ("Éclair", "2026-01-03", "2026-02-01", DefaultWorkflowStates.Idea, []),
            ("\"Apple\"", "2026-01-02", "2026-02-02", DefaultWorkflowStates.Final, [(4, "2026-03-05", "2026-04-02")]),
            ("eclair", "2026-01-04", "2026-02-04", DefaultWorkflowStates.Writing, [(null, null, "2026-03-01")]),
            ("banana", "2026-01-02", "2026-02-02", DefaultWorkflowStates.Archived, []),
        ];

        public static async Task SeedAsync(N8TracksApiFactory factory, HttpClient client)
        {
            var statements = new List<string>();
            for (var index = 0; index < Songs.Length; index++)
            {
                var (title, created, updated, state, generations) = Songs[index];
                var concept = index == 1 ? "harbour harbour" : "harbour";
                var song = await SongApi.CreateAsync(client, title, concept);
                var id = Upper(song.GetProperty("id").GetString()!);
                var version = song.GetProperty("currentVersion").GetProperty("shortcode").GetString()!;
                foreach (var (rating, sunoCreated, attached) in generations)
                {
                    var generation = Upper((await SongApi.AttachGenerationAsync(factory, version)).Generation.Id.ToString());
                    statements.Add(
                        $"UPDATE generations SET rating = {(rating is { } stars ? stars.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")}, "
                        + $"suno_created_utc = {(sunoCreated is null ? "NULL" : $"'{sunoCreated}T12:00:00.000Z'")}, created_utc = '{attached}T12:00:00.000Z' WHERE id = '{generation}';");
                }

                // After the Generations, whose attach moves the Song's updated time.
                statements.Add($"UPDATE songs SET workflow_state_id = '{Upper(state.Id.ToString())}', created_utc = '{created}T12:00:00.000Z' WHERE id = '{id}';");
                statements.Add($"UPDATE songs SET updated_utc = '{updated}T12:00:00.000Z' WHERE id = '{id}';");
            }

            TestDatabase.Execute(factory.DataPath, string.Join('\n', statements));
        }

        private static string Upper(string id) => id.ToUpperInvariant();
    }
}
