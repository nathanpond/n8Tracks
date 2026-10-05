using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Endpoints;
using n8Tracks.Api.Problems;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Domain.Songs;
using n8Tracks.Infrastructure.Persistence;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// <c>GET /api/v1/versions/{id}/next-numbers</c>: the numbers a new Version may take when it branches
/// from an existing one, and the used-numbers table that keeps every number ever assigned out of
/// those options.
/// </summary>
public sealed class NextNumbersEndpointTests
{
    [Fact]
    public async Task BranchingFromVersionOneProposesTwoWhileItIsFreeAndOnePointOneOnceItIsNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Branches");
        var versionOne = CurrentVersionId(song);

        using (var response = await client.GetAsync(NextNumbers(versionOne)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var body = await SetupApi.JsonAsync(response);
            Assert.Equal(
                """{"options":[{"number":"2","kind":"sibling","proposed":true},{"number":"1.1","kind":"child","proposed":false}]}""",
                body.GetRawText());
        }

        AddVersionDirectly(factory.DataPath, 1, "2");

        Assert.Equal(["1.1 child proposed", "3 sibling"], await OptionsAsync(client, versionOne));
    }

    [Fact]
    public async Task TheOptionsFollowTheTreeFromAnyBranchPoint()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Tree");
        AddVersionDirectly(factory.DataPath, 1, "1.3");
        var deep = AddVersionDirectly(factory.DataPath, 1, "1.3.1");
        var two = AddVersionDirectly(factory.DataPath, 1, "2");
        AddVersionDirectly(factory.DataPath, 1, "2.1");
        AddVersionDirectly(factory.DataPath, 1, "2.2");

        Assert.Equal(["1.3.2 sibling proposed", "1.3.1.1 child"], await OptionsAsync(client, deep));
        Assert.Equal(["3 sibling proposed", "2.3 child"], await OptionsAsync(client, two));

        AddVersionDirectly(factory.DataPath, 1, "1.3.2");
        AddVersionDirectly(factory.DataPath, 1, "3");

        Assert.Equal(["1.3.1.1 child proposed", "1.3.3 sibling"], await OptionsAsync(client, deep));
        Assert.Equal(["2.3 child proposed", "4 sibling"], await OptionsAsync(client, two));
    }

    [Fact]
    public async Task ArchivedAndRemovedVersionsNumbersAreNeverOfferedAgain()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var versionOne = CurrentVersionId(await SongApi.CreateAsync(client, "Never again"));
        var archived = AddVersionDirectly(factory.DataPath, 1, "1.1", VersionRecord.Archived);
        AddVersionDirectly(factory.DataPath, 1, "2");

        // An archived Version holds its number, and is a branch point like any other.
        Assert.Equal(["1.2 child proposed", "3 sibling"], await OptionsAsync(client, versionOne));
        Assert.Equal(["1.2 sibling proposed", "1.1.1 child"], await OptionsAsync(client, archived));

        // Removed outright, as a permanent deletion would: its number stays used.
        TestDatabase.Execute(factory.DataPath, "DELETE FROM versions WHERE number IN ('1.1', '2');");
        Assert.Equal(["1.2 child proposed", "3 sibling"], await OptionsAsync(client, versionOne));
        Assert.Equal(
            ["1", "1.1", "2"],
            TestDatabase.Rows(factory.DataPath, "SELECT number FROM used_version_numbers ORDER BY number;"));

        // A removed Version is no branch point.
        using var gone = await client.GetAsync(NextNumbers(archived));
        await SetupApi.ProblemAsync(gone, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task TheDatabaseRefusesANumberUsedBeforeAndAnyRenumbering()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "First");
        await SongApi.CreateAsync(client, "Second");

        // Song creation records Version 1 of each Song as used.
        Assert.Equal(
            ["1|1", "2|1"],
            TestDatabase.Rows(factory.DataPath, "SELECT s.shortcode_number, u.number FROM used_version_numbers u JOIN songs s ON s.id = u.song_id ORDER BY s.shortcode_number;"));

        AddVersionDirectly(factory.DataPath, 1, "2");
        TestDatabase.Execute(factory.DataPath, "DELETE FROM versions WHERE number = '2';");

        // A number once used, even by a Version since removed, cannot be assigned again in that Song.
        var reused = Assert.Throws<SqliteException>(() => AddVersionDirectly(factory.DataPath, 1, "2"));
        Assert.Equal(19, reused.SqliteErrorCode);

        // Another Song's numbers are its own.
        AddVersionDirectly(factory.DataPath, 2, "2");

        // A Version is never renumbered, nor moved to another Song.
        var renumbered = Assert.Throws<SqliteException>(() => TestDatabase.Execute(factory.DataPath, "UPDATE versions SET number = '7', number_sort_key = '0000000007' WHERE number = '1';"));
        Assert.Contains("A Version number never changes.", renumbered.Message, StringComparison.Ordinal);
        var moved = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            factory.DataPath,
            "UPDATE versions SET song_id = (SELECT id FROM songs WHERE shortcode_number = 1) WHERE number = '2';"));
        Assert.Contains("A Version number never changes.", moved.Message, StringComparison.Ordinal);

        // Complement: every other change to a Version still goes through.
        TestDatabase.Execute(factory.DataPath, "UPDATE versions SET lyrics = '[Verse]', updated_utc = updated_utc WHERE number = '1';");
        Assert.Equal(["1", "1", "2"], TestDatabase.Rows(factory.DataPath, "SELECT number FROM versions ORDER BY number;"));
    }

    [Fact]
    public async Task WhenBothOptionsWouldBeTooLongTheAnswerIs409()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Deep");

        // 64 characters ending in 99: the next sibling and the child would both be longer.
        var deepest = AddVersionDirectly(factory.DataPath, 1, string.Join('.', Enumerable.Repeat("1", 31)) + ".99");
        using (var response = await client.GetAsync(NextNumbers(deepest)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.Conflict, VersionsEndpoints.TooDeepCode);
        }

        // Only the child too long: the sibling is offered alone, as the proposal.
        var sixtyThree = string.Join('.', Enumerable.Repeat("1", 32));
        var deep = AddVersionDirectly(factory.DataPath, 1, sixtyThree);
        Assert.Equal([sixtyThree[..^1] + "2 sibling proposed"], await OptionsAsync(client, deep));
    }

    [Fact]
    public async Task AnUnknownVersionIs404()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var song = await SongApi.CreateAsync(client, "Known");

        using (var unknown = await client.GetAsync(NextNumbers(Guid.CreateVersion7())))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // A Song's ID is not a Version's.
        using (var songId = await client.GetAsync(NextNumbers(song.GetProperty("id").GetGuid())))
        {
            await SetupApi.ProblemAsync(songId, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        // Nor is a Song's shortcode, or a Version shortcode the Song does not have.
        using (var songShortcode = await client.GetAsync(new Uri("/api/v1/versions/n8-1/next-numbers", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(songShortcode, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
        }

        using var unknownShortcode = await client.GetAsync(new Uri("/api/v1/versions/n8-1-v9/next-numbers", UriKind.Relative));
        await SetupApi.ProblemAsync(unknownShortcode, HttpStatusCode.NotFound, ApiProblem.NotFoundCode);
    }

    [Fact]
    public async Task TheEndpointNeedsCatalogRead()
    {
        using var factory = SongApi.Host();
        using var setUp = await SessionApi.SignedInClientAsync(factory);
        var uri = NextNumbers(CurrentVersionId(await SongApi.CreateAsync(setUp, "Scoped")));
        using var client = factory.CreateClient();

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var allowed = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var everythingButRead = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, everythingButRead))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        using var anonymous = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task UpgradingRecordsTheNumberOfEveryExistingVersionAsUsed()
    {
        using var directory = new TemporaryDirectory();
        var builder = new DbContextOptionsBuilder<N8TracksDbContext>();
        builder.UseN8TracksSqlite(TestDatabase.FilePath(directory.Path));
        var options = builder.Options;
        await using (var context = new N8TracksDbContext(options))
        {
            var before = context.Database.GetMigrations().Single(static id => id.EndsWith("_AddSongs", StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(before);
        }

        // Two Songs with Versions as the previous schema stored them.
        var songs = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        TestDatabase.Execute(
            directory.Path,
            $"""
            INSERT INTO songs (id, shortcode_number, title, title_sort_key, workflow_state_id, created_utc, updated_utc, revision)
            VALUES ('{Upper(songs[0])}', 1, 'One', 'one', '{Upper(DefaultWorkflowStates.Idea.Id)}', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1),
                   ('{Upper(songs[1])}', 2, 'Two', 'two', '{Upper(DefaultWorkflowStates.Idea.Id)}', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
            {VersionInsert(songs[0], "1")}
            {VersionInsert(songs[0], "1.1")}
            {VersionInsert(songs[0], "2")}
            {VersionInsert(songs[1], "1")}
            """);

        await using (var context = new N8TracksDbContext(options))
        {
            await context.Database.MigrateAsync();
        }

        Assert.Equal(
            ["1|1", "1|1.1", "1|2", "2|1"],
            TestDatabase.Rows(directory.Path, "SELECT s.shortcode_number, u.number FROM used_version_numbers u JOIN songs s ON s.id = u.song_id ORDER BY s.shortcode_number, u.number;"));
    }

    /// <summary>The next-numbers URI of a Version.</summary>
    private static Uri NextNumbers(Guid versionId) => new($"/api/v1/versions/{versionId}/next-numbers", UriKind.Relative);

    private static Guid CurrentVersionId(JsonElement song) => song.GetProperty("currentVersion").GetProperty("id").GetGuid();

    /// <summary>The options for a Version, each as <c>number kind[ proposed]</c>, asserting 200.</summary>
    private static async Task<List<string>> OptionsAsync(HttpClient client, Guid versionId)
    {
        using var response = await client.GetAsync(NextNumbers(versionId));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await SetupApi.JsonAsync(response);

        return [.. body.GetProperty("options").EnumerateArray().Select(static option =>
            $"{option.GetProperty("number").GetString()} {option.GetProperty("kind").GetString()}{(option.GetProperty("proposed").GetBoolean() ? " proposed" : string.Empty)}")];
    }

    private static Guid AddVersionDirectly(string dataPath, long shortcodeNumber, string number, string visibility = VersionRecord.Active) =>
        SongApi.AddVersionDirectly(dataPath, shortcodeNumber, number, visibility);

    private static string VersionInsert(Guid songId, string number) =>
        $"""
        INSERT INTO versions (id, song_id, number, number_sort_key, visibility, lyrics, styles, created_utc, updated_utc, revision)
        VALUES ('{Upper(Guid.CreateVersion7())}', '{Upper(songId)}', '{number}', '{VersionNumbers.SortKey(number)}', 'active', '', '', '2026-10-01T09:00:00.000Z', '2026-10-01T09:00:00.000Z', 1);
        """;

    private static string Upper(Guid id) => id.ToString().ToUpperInvariant();
}
