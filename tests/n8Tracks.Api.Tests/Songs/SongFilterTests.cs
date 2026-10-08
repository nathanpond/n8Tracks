using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Media;
using n8Tracks.Application.Songs;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// #225: the Songs list's eleven filters, alone, in pairs, all together, and with a search. One
/// catalog holds a Song matching every filter ("all") and, for each filter, a Song matching every
/// filter but that one, so each filter is shown keeping one Song and dropping another, and the
/// complement (all but one is not enough) is shown for every filter.
/// </summary>
public sealed class SongFilterTests
{
    /// <summary>
    /// The eleven filters of #225, each with the <see cref="SongListQuery"/> members that carry it and
    /// the query string that keeps the "all" Song and drops the Song named after it. A filter added
    /// to the story without a row here, or a row whose members are not on the query, fails
    /// <see cref="EveryOneOfTheElevenFiltersIsOnTheQueryAndHasACase"/>.
    /// </summary>
    private static readonly Dictionary<string, (string[] Members, string Query)> Filters = new(StringComparer.Ordinal)
    {
        ["state"] = (["StateIds"], $"state={DefaultWorkflowStates.Writing.Id}&state={DefaultWorkflowStates.Archived.Id}"),
        ["archived"] = (["Archived"], "archived=active"),
        ["genre"] = (["GenreIds", "NoGenre"], "genre={folk}"),
        ["tag"] = (["TagIds", "NoTag", "AllTags"], "tag={night}&tag={road}"),
        ["album"] = (["AlbumId"], "album={album}"),
        ["playlist"] = (["PlaylistId"], "playlist={playlist}"),
        ["created"] = (["CreatedFrom", "CreatedBefore"], "createdFrom=2026-01-10&createdTo=2026-01-20"),
        ["model"] = (["Models"], "model=v4.5&model=v5"),
        ["rating"] = (["MinRating", "Unrated"], "minRating=4"),
        ["selected"] = (["HasSelectedGeneration"], "selected=yes"),
        ["audio"] = (["Audio", "MediaUnavailable"], "audio=available"),
    };

    [Fact]
    public void EveryOneOfTheElevenFiltersIsOnTheQueryAndHasACase()
    {
        Assert.Equal(
            ["album", "archived", "audio", "created", "genre", "model", "playlist", "rating", "selected", "state", "tag"],
            Filters.Keys.Order(StringComparer.Ordinal));
        var members = typeof(SongListQuery).GetProperties().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, filter) in Filters)
        {
            Assert.All(filter.Members, member => Assert.True(members.Contains(member), $"{name}: SongListQuery has no {member}."));
        }
    }

    [Fact]
    public async Task EachFilterAloneKeepsTheSongMatchingItAndDropsTheOneThatDoesNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        foreach (var (name, filter) in Filters)
        {
            var titles = await catalog.TitlesAsync(filter.Query);
            Assert.True(titles.Contains("all"), $"{name} dropped the Song matching it: {string.Join(", ", titles)}");
            Assert.False(titles.Contains($"not {name}"), $"{name} kept the Song not matching it.");
        }
    }

    [Fact]
    public async Task ASongMatchingEveryFilterButOneIsLeftOutForEveryFilter()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        var all = string.Join('&', Filters.Values.Select(static filter => filter.Query));
        Assert.Equal(["all"], await catalog.TitlesAsync(all));
        Assert.Equal(1, (await catalog.ListAsync(all)).GetProperty("total").GetInt32());

        // Each filter left out lets exactly its own Song back in.
        foreach (var (name, _) in Filters)
        {
            var others = string.Join('&', Filters.Where(other => other.Key != name).Select(static other => other.Value.Query));
            Assert.Equal(["all", $"not {name}"], await catalog.TitlesAsync(others));
        }
    }

    [Fact]
    public async Task PairsOfFiltersCombineByAnd()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        // Each filter alone keeps eleven of the twelve Songs; two together drop both of their Songs.
        foreach (var (first, second) in new[] { ("state", "genre"), ("model", "rating"), ("album", "audio"), ("created", "selected"), ("tag", "playlist"), ("archived", "audio") })
        {
            var titles = await catalog.TitlesAsync($"{Filters[first].Query}&{Filters[second].Query}");
            Assert.Equal(10, titles.Count);
            Assert.DoesNotContain($"not {first}", titles);
            Assert.DoesNotContain($"not {second}", titles);
        }
    }

    [Fact]
    public async Task ASearchAndTwoFiltersCombineAndTheTotalCountsSongsMatchingAll()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);
        await SongApi.CreateAsync(client, "unfiltered lantern");

        var searched = await catalog.ListAsync("search=lantern");
        Assert.Equal(13, searched.GetProperty("total").GetInt32());

        var both = await catalog.ListAsync($"search=lantern&{Filters["genre"].Query}&{Filters["selected"].Query}");
        Assert.Equal(10, both.GetProperty("total").GetInt32());
        var titles = Titles(both);
        Assert.DoesNotContain("not genre lantern", titles);
        Assert.DoesNotContain("not selected lantern", titles);
        Assert.Contains("all lantern", titles);
        Assert.DoesNotContain("unfiltered lantern", titles);
        Assert.All(both.GetProperty("items").EnumerateArray(), static song => Assert.True(song.GetProperty("matchCount").GetInt32() > 0));
    }

    [Fact]
    public async Task TagsMatchAllOfThemByDefaultOrAnyOfThemAndNoneOnlyWithAny()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);
        var night = catalog.Night;
        var road = catalog.Road;

        // "not tag" has only one of the two: all of them drops it, any of them keeps it.
        Assert.DoesNotContain("not tag", await catalog.TitlesAsync($"tag={night}&tag={road}&tagMode=all"));
        Assert.Contains("not tag", await catalog.TitlesAsync($"tag={night}&tag={road}&tagMode=any"));

        // No Tags beside other Tags is any-of; with all-of it would match nothing and is refused.
        await SongApi.CreateAsync(client, "no tags");
        Assert.Contains("no tags", await catalog.TitlesAsync($"tag=none&tag={road}&tagMode=any"));
        Assert.Equal(["no tags"], await catalog.TitlesAsync("tag=none"));
        var refused = await catalog.RefusedAsync($"tag=none&tag={road}");
        Assert.Contains("tagMode", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedSongsAreListedUnlessFilteredOutAndTheArchivedStateIsKnownByItsId()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        Assert.Contains("not archived", await catalog.TitlesAsync(string.Empty));
        Assert.Contains("not archived", await catalog.TitlesAsync("archived=both"));
        Assert.Equal(["not archived"], await catalog.TitlesAsync("archived=archived"));
        Assert.DoesNotContain("not archived", await catalog.TitlesAsync("archived=active"));

        // Renamed, it is still the Archived state.
        TestDatabase.Execute(factory.DataPath, $"UPDATE workflow_states SET name = 'Shelved' WHERE id = '{Upper(DefaultWorkflowStates.Archived.Id)}';");
        Assert.Equal(["not archived"], await catalog.TitlesAsync("archived=archived"));

        // Active only with the Archived state chosen is simply empty.
        Assert.Empty(await catalog.TitlesAsync($"archived=active&state={DefaultWorkflowStates.Archived.Id}"));
    }

    [Fact]
    public async Task TheRatingFilterReadsTheHighestRatingOrNoRatedGenerationOrEither()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);
        await catalog.SongAsync("unrated generation", static song => song with { Rating = null });
        await catalog.SongAsync("no generation", static song => song with { Generation = false });

        // "not rating" is rated 3: excluded at 4, kept at 3; the unrated Songs never by a minimum.
        Assert.DoesNotContain("not rating", await catalog.TitlesAsync("minRating=4"));
        Assert.Contains("not rating", await catalog.TitlesAsync("minRating=3"));
        Assert.DoesNotContain("unrated generation", await catalog.TitlesAsync("minRating=1"));

        // No rated Generation excludes every rated Song.
        Assert.Equal(["no generation", "unrated generation"], await catalog.TitlesAsync("rated=none"));

        // Both: either.
        var either = await catalog.TitlesAsync("minRating=4&rated=none");
        Assert.Contains("no generation", either);
        Assert.Contains("unrated generation", either);
        Assert.Contains("all", either);
        Assert.DoesNotContain("not rating", either);

        // An archived Generation's rating counts.
        TestDatabase.Execute(factory.DataPath, "UPDATE generations SET state = 'archived', archived_by = 'user' WHERE rating = 3;");
        Assert.Contains("not rating", await catalog.TitlesAsync("minRating=3"));
    }

    [Fact]
    public async Task TheSelectedAndModelFiltersReadEveryLiveGeneration()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        Assert.Equal(["not selected"], await catalog.TitlesAsync("selected=no"));
        Assert.Equal(["not model"], await catalog.TitlesAsync("model=v3"));
        Assert.Empty(await catalog.TitlesAsync("model=v9"));

        // A second Generation reporting another model: the Song matches either model.
        await catalog.SongAsync("two models", static song => song with { SecondModel = "v3" });
        Assert.Contains("two models", await catalog.TitlesAsync("model=v3"));
        Assert.Contains("two models", await catalog.TitlesAsync("model=v4.5"));
    }

    [Fact]
    public async Task TheAudioFilterReadsTheReportedAvailabilityOfEveryAssociatedFile()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);
        await catalog.SongAsync("no audio", static song => song with { Audio = null });

        Assert.Equal(["not audio"], await catalog.TitlesAsync("audio=unavailable"));
        Assert.Equal(["no audio"], await catalog.TitlesAsync("audio=none"));

        // While the media folder is unavailable every file reports so: no Song has an available file.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaAvailability>().RecordAsync(readable: false, CancellationToken.None);
        }

        Assert.Empty(await catalog.TitlesAsync("audio=available"));
        Assert.Equal(12, (await catalog.TitlesAsync("audio=unavailable")).Count);
        Assert.Equal(["no audio"], await catalog.TitlesAsync("audio=none"));
    }

    [Fact]
    public async Task CreationDatesAreWholeDaysInTheConfiguredZoneWithBothEndsIncluded()
    {
        using var factory = new N8TracksApiFactory(new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.TimeZone] = "Pacific/Auckland" });
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = new FilterCatalog(factory, client);

        // 2026-01-20 in Auckland (UTC+13 in January) is 2026-01-19T11:00Z to 2026-01-20T11:00Z.
        await catalog.SongAsync("before", static song => song with { Created = "2026-01-19T10:59:59.999Z" });
        await catalog.SongAsync("first instant", static song => song with { Created = "2026-01-19T11:00:00.000Z" });
        await catalog.SongAsync("last instant", static song => song with { Created = "2026-01-20T10:59:59.999Z" });
        await catalog.SongAsync("after", static song => song with { Created = "2026-01-20T11:00:00.000Z" });

        Assert.Equal(["first instant", "last instant"], await catalog.TitlesAsync("createdFrom=2026-01-20&createdTo=2026-01-20"));
        Assert.Equal(["after", "first instant", "last instant"], await catalog.TitlesAsync("createdFrom=2026-01-20"));
        Assert.Equal(["before", "first instant", "last instant"], await catalog.TitlesAsync("createdTo=2026-01-20"));
    }

    [Fact]
    public async Task AnAlbumPlaylistGenreOrTagThatDoesNotExistMatchesNothing()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = await FilterCatalog.SeedAsync(factory, client);

        foreach (var parameter in new[] { "album", "playlist", "genre", "tag" })
        {
            Assert.Empty(await catalog.TitlesAsync($"{parameter}={Guid.CreateVersion7()}"));
        }

        // An ID of another kind is just as unknown.
        Assert.Empty(await catalog.TitlesAsync($"album={catalog.Playlist}"));
    }

    [Theory]
    [InlineData("state=01a10a6e-dc86-7006-8000-0000000000ff", "state")]
    [InlineData("state=writing", "state")]
    [InlineData("archived=yes", "archived")]
    [InlineData("genre=folk", "genre")]
    [InlineData("tag=night", "tag")]
    [InlineData("tagMode=some", "tagMode")]
    [InlineData("album=first", "album")]
    [InlineData("playlist=", "playlist")]
    [InlineData("model=", "model")]
    [InlineData("model=%20", "model")]
    [InlineData("createdFrom=2026-13-01", "createdFrom")]
    [InlineData("createdFrom=20260101", "createdFrom")]
    [InlineData("createdTo=yesterday", "createdTo")]
    [InlineData("createdFrom=2026-01-21&createdTo=2026-01-20", "createdFrom")]
    [InlineData("minRating=0", "minRating")]
    [InlineData("minRating=6", "minRating")]
    [InlineData("minRating=4.5", "minRating")]
    [InlineData("rated=yes", "rated")]
    [InlineData("selected=true", "selected")]
    [InlineData("audio=missing", "audio")]
    [InlineData("album=0199b1a0-0000-7000-8000-000000000001&album=0199b1a0-0000-7000-8000-000000000002", "more than once")]
    [InlineData("minRating=3&minRating=4", "more than once")]
    public async Task ABadFilterValueIs400NamingTheParameter(string query, string named)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var catalog = new FilterCatalog(factory, client);

        Assert.Contains(named, await catalog.RefusedAsync(query), StringComparison.Ordinal);
    }

    /// <summary>
    /// The seeded catalog: "all" matches every filter in <see cref="Filters"/>; each "not &lt;name&gt;"
    /// matches every one but that one. Every Song's title ends in "lantern" for the search.
    /// </summary>
    internal sealed class FilterCatalog(N8TracksApiFactory factory, HttpClient client)
    {
        private int count;

        public string Folk { get; private set; } = string.Empty;

        public string Rock { get; private set; } = string.Empty;

        public string Night { get; private set; } = string.Empty;

        public string Road { get; private set; } = string.Empty;

        public string Album { get; private set; } = string.Empty;

        public string Playlist { get; private set; } = string.Empty;

        public static async Task<FilterCatalog> SeedAsync(N8TracksApiFactory factory, HttpClient client)
        {
            var catalog = new FilterCatalog(factory, client);
            await catalog.SongAsync("all", static song => song);
            await catalog.SongAsync("not state", static song => song with { State = DefaultWorkflowStates.Idea.Id });
            await catalog.SongAsync("not archived", static song => song with { State = DefaultWorkflowStates.Archived.Id });
            await catalog.SongAsync("not genre", static song => song with { Genre = Rocky });
            await catalog.SongAsync("not tag", static song => song with { BothTags = false });
            await catalog.SongAsync("not album", static song => song with { OnAlbum = false });
            await catalog.SongAsync("not playlist", static song => song with { OnPlaylist = false });
            await catalog.SongAsync("not created", static song => song with { Created = "2026-01-21T00:00:00.000Z" });
            await catalog.SongAsync("not model", static song => song with { Model = "v3" });
            await catalog.SongAsync("not rating", static song => song with { Rating = 3 });
            await catalog.SongAsync("not selected", static song => song with { Selected = false });
            await catalog.SongAsync("not audio", static song => song with { Audio = "missing" });
            return catalog;
        }

        /// <summary>A marker for the other Genre: <see cref="SongSpec.Genre"/> holds which one.</summary>
        public const string Rocky = "rock";

        /// <summary>Creates a Song (titled <paramref name="title"/>) like "all", changed by <paramref name="change"/>.</summary>
        public async Task SongAsync(string title, Func<SongSpec, SongSpec> change)
        {
            EnsureCollections();
            var spec = change(new SongSpec());
            var song = await SongApi.CreateAsync(client, title + " lantern");
            var id = song.GetProperty("id").GetGuid();
            var version = song.GetProperty("currentVersion").GetProperty("shortcode").GetString()!;
            count++;

            var statements = new List<string>
            {
                $"UPDATE songs SET workflow_state_id = '{Upper(spec.State)}', created_utc = '{spec.Created}' WHERE id = '{Upper(id)}';",
                $"INSERT INTO song_genres (song_id, genre_id) VALUES ('{Upper(id)}', '{Upper(spec.Genre == Rocky ? Rock : Folk)}');",
                $"INSERT INTO song_tags (song_id, tag_id) VALUES ('{Upper(id)}', '{Upper(Night)}');",
            };
            if (spec.BothTags)
            {
                statements.Add($"INSERT INTO song_tags (song_id, tag_id) VALUES ('{Upper(id)}', '{Upper(Road)}');");
            }

            if (spec.OnAlbum)
            {
                statements.Add(string.Create(CultureInfo.InvariantCulture, $"INSERT INTO album_songs (album_id, song_id, disc, track) VALUES ('{Upper(Album)}', '{Upper(id)}', 1, {count});"));
            }

            if (spec.OnPlaylist)
            {
                statements.Add(string.Create(CultureInfo.InvariantCulture, $"INSERT INTO playlist_songs (playlist_id, song_id, position) VALUES ('{Upper(Playlist)}', '{Upper(id)}', {count});"));
            }

            if (spec.Generation)
            {
                var generation = (await SongApi.AttachGenerationAsync(factory, version)).Generation.Id;
                var rating = spec.Rating is { } stars ? stars.ToString(CultureInfo.InvariantCulture) : "NULL";
                statements.Add($"UPDATE generations SET model_version = '{spec.Model}', rating = {rating} WHERE id = '{Upper(generation)}';");
                if (spec.Selected)
                {
                    statements.Add($"UPDATE songs SET selected_generation_id = '{Upper(generation)}' WHERE id = '{Upper(id)}';");
                }

                if (spec.SecondModel is { } second)
                {
                    var other = (await SongApi.AttachGenerationAsync(factory, version)).Generation.Id;
                    statements.Add($"UPDATE generations SET model_version = '{second}' WHERE id = '{Upper(other)}';");
                }
            }

            if (spec.Audio is { } status)
            {
                statements.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"""
                    INSERT INTO audio_files (id, path, file_name, format, size_bytes, modified_utc, first_seen_utc, last_seen_utc, status, metadata_readable, song_id, association_origin, revision, auto_match_blocked)
                    VALUES ('{Upper(Guid.CreateVersion7())}', 'song-{count}.mp3', 'song-{count}.mp3', 'mp3', 1, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', '{status}', 0, '{Upper(id)}', 'user', 1, 0);
                    """));
            }

            TestDatabase.Execute(factory.DataPath, string.Join('\n', statements));
        }

        public async Task<JsonElement> ListAsync(string query) =>
            await SongApi.ListAsync(client, $"pageSize=100&{query.Replace("{folk}", Folk, StringComparison.Ordinal).Replace("{night}", Night, StringComparison.Ordinal).Replace("{road}", Road, StringComparison.Ordinal).Replace("{album}", Album, StringComparison.Ordinal).Replace("{playlist}", Playlist, StringComparison.Ordinal)}");

        /// <summary>The titles listed for <paramref name="query"/>, ordered.</summary>
        public async Task<List<string>> TitlesAsync(string query) =>
            [.. Titles(await ListAsync(query)).Select(static title => title.EndsWith(" lantern", StringComparison.Ordinal) ? title[..^" lantern".Length] : title).Order(StringComparer.Ordinal)];

        /// <summary>The 400 <c>invalid_request</c> answer to <paramref name="query"/>, as text.</summary>
        public async Task<string> RefusedAsync(string query)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));
            return (await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request")).ToString();
        }

        private void EnsureCollections()
        {
            if (Folk.Length > 0)
            {
                return;
            }

            Folk = Guid.CreateVersion7().ToString();
            Rock = Guid.CreateVersion7().ToString();
            Night = Guid.CreateVersion7().ToString();
            Road = Guid.CreateVersion7().ToString();
            Album = Guid.CreateVersion7().ToString();
            Playlist = Guid.CreateVersion7().ToString();
            TestDatabase.Execute(
                factory.DataPath,
                $"""
                INSERT INTO genres (id, name, name_key, revision) VALUES ('{Upper(Folk)}', 'Folk', 'folk', 1), ('{Upper(Rock)}', 'Rock', 'rock', 1);
                INSERT INTO tags (id, name, name_key, colour, revision) VALUES ('{Upper(Night)}', 'night', 'night', 'teal', 1), ('{Upper(Road)}', 'road', 'road', 'blue', 1);
                INSERT INTO albums (id, title, title_key, created_utc, updated_utc, revision) VALUES ('{Upper(Album)}', 'First Album', 'first album', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 1);
                INSERT INTO playlists (id, title, title_key, created_utc, updated_utc, revision) VALUES ('{Upper(Playlist)}', 'Road Trip', 'road trip', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 1);
                """);
        }
    }

    /// <summary>How a seeded Song differs from "all".</summary>
    internal sealed record SongSpec
    {
        public Guid State { get; init; } = DefaultWorkflowStates.Writing.Id;

        public string Genre { get; init; } = "folk";

        public bool BothTags { get; init; } = true;

        public bool OnAlbum { get; init; } = true;

        public bool OnPlaylist { get; init; } = true;

        public string Created { get; init; } = "2026-01-15T12:00:00.000Z";

        public bool Generation { get; init; } = true;

        public string Model { get; init; } = "v4.5";

        public string? SecondModel { get; init; }

        public int? Rating { get; init; } = 5;

        public bool Selected { get; init; } = true;

        public string? Audio { get; init; } = "available";
    }

    /// <summary>The titles of a list body's items, in order.</summary>
    private static List<string> Titles(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Select(static song => song.GetProperty("title").GetString()!)];

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    private static string Upper(string id) => id.ToUpperInvariant();
}
