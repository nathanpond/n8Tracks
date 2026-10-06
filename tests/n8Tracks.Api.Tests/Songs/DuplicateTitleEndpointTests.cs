using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// The Songs list's exact-title filter (<c>title</c> and <c>excludeId</c>), which tells a Song which
/// others share its title: how titles match, its refusals, and the upgrade that keys existing Songs.
/// </summary>
public sealed class DuplicateTitleEndpointTests
{
    [Fact]
    public async Task TheTitleFilterMatchesIgnoringCaseAndSpacingAndLeavesOutTheAskingSong()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        var asking = await SongApi.CreateAsync(client, "Working Title");
        clock.Advance(TimeSpan.FromSeconds(1));
        await SongApi.CreateAsync(client, "working   TITLE");
        clock.Advance(TimeSpan.FromSeconds(1));
        await SongApi.CreateAsync(client, "WORKING TITLE");

        // Complement: a longer title that starts with it, a shorter one, and a different accent.
        await SongApi.CreateAsync(client, "Working Title Two");
        await SongApi.CreateAsync(client, "Working");
        await SongApi.CreateAsync(client, "Wörking Title");

        var id = asking.GetProperty("id").GetString()!;
        var others = await SongApi.ListAsync(client, $"title=%20working%20title%20&excludeId={id}");
        Assert.Equal(["n8-3", "n8-2"], SongApi.Shortcodes(others));
        Assert.Equal(2, others.GetProperty("total").GetInt32());

        // Without excludeId the asking Song is one of them; the other list parameters still apply.
        var all = await SongApi.ListAsync(client, "title=Working%20Title&direction=asc&pageSize=1");
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(["n8-1"], SongApi.Shortcodes(all));

        // Precomposed and decomposed letters are the same title.
        Assert.Equal(["n8-6"], SongApi.Shortcodes(await SongApi.ListAsync(client, "title=Wo%CC%88rking%20Title")));

        // A unique title has no others.
        var working = (await SongApi.ListAsync(client, "title=Working")).GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(0, (await SongApi.ListAsync(client, $"title=Working&excludeId={working}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task EditingATitleMovesTheSongToItsNewTitle()
    {
        using var factory = SongApi.Host(new TestClock());
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Working Title");
        await SongApi.CreateAsync(client, "Working Title");

        await SongApi.EditAsync(client, song.GetProperty("id").GetString()!, 1, """{"title":"  Finished   Title "}""");

        Assert.Equal(["n8-2"], SongApi.Shortcodes(await SongApi.ListAsync(client, "title=working%20title")));
        Assert.Equal(["n8-1"], SongApi.Shortcodes(await SongApi.ListAsync(client, "title=FINISHED%20TITLE")));
    }

    [Theory]
    [InlineData("title=", "title must not be blank.")]
    [InlineData("title=%20%09%20", "title must not be blank.")]
    [InlineData("title=A&excludeId=n8-1", "excludeId must be the ID of a Song.")]
    [InlineData("title=A&excludeId=", "excludeId must be the ID of a Song.")]
    [InlineData("title=A&title=B", "Only state, genre, tag, and artist may be given more than once.")]
    [InlineData("title=A&excludeId=01a10a6e-de01-7000-8000-000000000001&excludeId=01a10a6e-de01-7000-8000-000000000002", "Only state, genre, tag, and artist may be given more than once.")]
    public async Task ABlankTitleOrAMalformedExcludeIdIsRefused(string query, string title)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/songs?{query}", UriKind.Relative));

        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(title, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ACatalogReadTokenCanAskWhichSongsShareATitle()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Shared");
        await SongApi.CreateAsync(client, "shared");
        var token = await CredentialApi.CreateTokenAsync(factory, "catalog.read");

        using var anonymous = factory.CreateClient();
        using var response = await CredentialApi.SendAsync(anonymous, HttpMethod.Get, new Uri("/api/v1/songs?title=SHARED", UriKind.Relative), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await SetupApi.JsonAsync(response)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task UpgradingKeysEveryExistingSongByTheApplicationsRule()
    {
        using var directory = new TemporaryDirectory();
        SqliteDatabase.CreateIfMissing(TestDatabase.FilePath(directory.Path));
        var builder = new DbContextOptionsBuilder<N8TracksDbContext>();
        builder.UseN8TracksSqlite(TestDatabase.FilePath(directory.Path));
        var options = builder.Options;
        await using (var context = new N8TracksDbContext(options))
        {
            var before = context.Database.GetMigrations().Single(static id => id.EndsWith("_AddSongRelease", StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(before);
        }

        // Titles SQLite alone would key wrongly: non-ASCII case, inner spacing, decomposed letters.
        var titles = new[] { "été  sans   fin", "été Sans Fin", "Plain" };
        for (var index = 0; index < titles.Length; index++)
        {
            TestDatabase.Execute(
                directory.Path,
                $"""
                INSERT INTO songs (id, shortcode_number, title, title_sort_key, workflow_state_id, created_utc, updated_utc, revision)
                VALUES ('{Upper(Guid.CreateVersion7())}', {index + 1}, '{titles[index]}', 'x', '{Upper(DefaultWorkflowStates.Idea.Id)}', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
                """);
        }

        await using (var context = new N8TracksDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        var keys = TestDatabase.Rows(directory.Path, "SELECT title_key FROM songs ORDER BY shortcode_number;");
        Assert.Equal([.. titles.Select(SongRules.TitleKey)], keys);
        Assert.Equal("ÉTÉ SANS FIN", keys[0]);
        Assert.Equal(keys[0], keys[1]);
        Assert.Contains("ix_songs_title_key", TestDatabase.Rows(directory.Path, "SELECT name FROM sqlite_master WHERE type = 'index';"));
    }

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();
}
