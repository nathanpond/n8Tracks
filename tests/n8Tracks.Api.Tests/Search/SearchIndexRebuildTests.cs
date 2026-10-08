using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Jobs;
using n8Tracks.Application.Search;

namespace n8Tracks.Api.Tests.Search;

/// <summary>
/// Rebuilding the search index (#223): <c>POST /api/v1/diagnostics/search-index/rebuild</c>, session-only,
/// queues a job (or answers the one queued or running); a rebuild restores what the index lost, keeps
/// what was written while it ran, and is started by the app itself when the index needs it.
/// </summary>
public sealed class SearchIndexRebuildTests
{
    private static readonly Uri Rebuild = new("/api/v1/diagnostics/search-index/rebuild", UriKind.Relative);

    [Fact]
    public async Task ARebuildRestoresAnIndexThatLostItsRows()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var one = (await SongApi.CreateAsync(client, "Granite song")).GetProperty("shortcode").GetString()!;
        var two = (await SongApi.CreateAsync(client, "Basalt song", "granite and basalt")).GetProperty("shortcode").GetString()!;
        Assert.Equal([one, two], await SearchApi.FoundAsync(client, "granite"));

        // The index loses its rows: nothing is found.
        TestDatabase.Execute(factory.DataPath, "DELETE FROM search_index; DELETE FROM search_rows;");
        Assert.Empty(await SearchApi.FoundAsync(client, "granite"));

        var job = await StartAsync(client, HttpStatusCode.Accepted);
        Assert.False(job.GetProperty("alreadyInProgress").GetBoolean());
        var finished = await TestJobs.WaitForStatusAsync(client, job.GetProperty("jobId").GetGuid(), "succeeded");
        Assert.Equal("search-index-rebuild", finished.GetProperty("type").GetString());

        Assert.Equal([one, two], await SearchApi.FoundAsync(client, "granite"));
        Assert.Equal([two], await SearchApi.FoundAsync(client, "basalt"));
        Assert.False((await SearchApi.SearchAsync(client, "granite")).GetProperty("indexRebuilding").GetBoolean());
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'search_%next%';"));

        // The format version is the current one, and needed no row for it.
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM settings WHERE key = 'search.index';"));
    }

    [Fact]
    public async Task WritesWhileARebuildRunsAreInTheIndexItSwapsInAndASecondRequestAnswersTheSameJob()
    {
        using var gate = new RebuildGate();
        using var factory = new N8TracksApiFactory { TestServices = services => services.AddSingleton(gate).AddKeyedScoped<IJobHandler, GatedRebuildHandler>(SearchIndexRebuild.JobType) };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var kept = await SongApi.CreateAsync(client, "Quartz kept");
        var edited = await SongApi.CreateAsync(client, "Quartz edited");
        var deleted = await SongApi.CreateAsync(client, "Quartz deleted");

        var job = (await StartAsync(client, HttpStatusCode.Accepted)).GetProperty("jobId").GetGuid();
        Assert.True(gate.Filled.Wait(TimeSpan.FromSeconds(10)), "The rebuild never filled its first batch.");

        // Mid-rebuild: the next index exists, search answers from the current one and says so.
        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM sqlite_master WHERE name = 'search_index_next';"));
        var during = await SearchApi.SearchAsync(client, "quartz");
        Assert.True(during.GetProperty("indexRebuilding").GetBoolean());
        Assert.Equal(3, during.GetProperty("total").GetInt32());

        var again = await StartAsync(client, HttpStatusCode.OK);
        Assert.Equal(job, again.GetProperty("jobId").GetGuid());
        Assert.True(again.GetProperty("alreadyInProgress").GetBoolean());

        // Writes now go to both indexes.
        _ = await SongApi.EditAsync(client, edited.GetProperty("id").GetString()!, 1, """{"title":"Quartz renamed obsidian"}""");
        var added = (await SongApi.CreateAsync(client, "Quartz added")).GetProperty("shortcode").GetString()!;
        using (var removed = await SearchApi.SendAsync(client, HttpMethod.Delete, $"/api/v1/songs/{deleted.GetProperty("shortcode").GetString()}", 1, """{"confirmTitle":"Quartz deleted"}"""))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        gate.Release.Set();
        _ = await TestJobs.WaitForStatusAsync(client, job, "succeeded");

        var after = await SearchApi.SearchAsync(client, "quartz");
        Assert.False(after.GetProperty("indexRebuilding").GetBoolean());
        Assert.Equal(
            new[] { kept.GetProperty("shortcode").GetString()!, edited.GetProperty("shortcode").GetString()!, added }.Order(StringComparer.Ordinal),
            SongApi.Shortcodes(after).Order(StringComparer.Ordinal));
        Assert.Equal([edited.GetProperty("shortcode").GetString()!], await SearchApi.FoundAsync(client, "obsidian"));
        Assert.Empty(await SearchApi.FoundAsync(client, "deleted"));
    }

    [Fact]
    public async Task OnlyASignedInSessionCanRebuild()
    {
        using var factory = SongApi.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);

        using (var bearer = await CredentialApi.SendAsync(client, HttpMethod.Post, Rebuild, token))
        {
            await SetupApi.ProblemAsync(bearer, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM jobs WHERE type = 'search-index-rebuild';"));
    }

    [Fact]
    public async Task TheAppRebuildsAnEmptyIndexOfACatalogWithSongsOnceItIsServing()
    {
        using var data = new TemporaryDirectory();
        string song;
        using (var first = Host(data.Path))
        {
            using var client = await SessionApi.SignedInClientAsync(first);
            song = (await SongApi.CreateAsync(client, "Upgraded catalogue")).GetProperty("shortcode").GetString()!;
        }

        // As a catalog upgraded to the migration that added the index finds it: empty.
        TestDatabase.Execute(data.Path, "DELETE FROM search_index; DELETE FROM search_rows;");

        using var second = Host(data.Path);
        using var signedIn = await SignInAsync(second);
        var job = await WaitForRebuildAsync(second);
        Assert.Equal("succeeded", job.Status.ToString().ToLowerInvariant());
        Assert.Equal([song], await SearchApi.FoundAsync(signedIn, "catalogue"));
    }

    [Fact]
    public async Task TheAppRebuildsAnIndexOfAnotherFormatVersionAndRecordsTheCurrentOne()
    {
        using var data = new TemporaryDirectory();
        string song;
        using (var first = Host(data.Path))
        {
            using var client = await SessionApi.SignedInClientAsync(first);
            song = (await SongApi.CreateAsync(client, "Versioned catalogue")).GetProperty("shortcode").GetString()!;

            // Complement: a start with nothing to do queues no rebuild.
            await Task.Delay(500);
            Assert.Equal("0", TestDatabase.Scalar(data.Path, "SELECT count(*) FROM jobs WHERE type = 'search-index-rebuild';"));
        }

        // A migration says the index must be rebuilt by writing an older format version; a rebuild left
        // half-done by a stopped process is dropped.
        TestDatabase.Execute(data.Path, "INSERT INTO settings (key, value) VALUES ('search.index', '{\"version\":0}'); CREATE TABLE search_rows_next (row_id INTEGER PRIMARY KEY, song_id TEXT NOT NULL); CREATE VIRTUAL TABLE search_index_next USING fts5(text);");

        using var second = Host(data.Path);
        using var signedIn = await SignInAsync(second);
        _ = await WaitForRebuildAsync(second);
        Assert.Equal($"{{\"version\":{SearchIndexRebuild.CurrentVersion}}}", TestDatabase.Scalar(data.Path, "SELECT value FROM settings WHERE key = 'search.index';"));
        Assert.Equal("0", TestDatabase.Scalar(data.Path, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'search_%next%';"));
        Assert.Equal([song], await SearchApi.FoundAsync(signedIn, "catalogue"));
    }

    private static N8TracksApiFactory Host(string dataPath) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.DataPath] = dataPath });

    private static async Task<HttpClient> SignInAsync(N8TracksApiFactory factory)
    {
        var client = factory.CreateClient();
        using var signIn = await SessionApi.SignInAsync(client, SetupApi.TestUsername, SetupApi.TestPassword);
        Assert.True(signIn.IsSuccessStatusCode, await signIn.Content.ReadAsStringAsync());
        return client;
    }

    /// <summary>The rebuild the app queued by itself, once it has finished.</summary>
    private static async Task<JobSummary> WaitForRebuildAsync(N8TracksApiFactory factory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var scope = factory.Services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var jobs = scope.ServiceProvider.GetRequiredService<IJobStore>();
                if (await jobs.FindLatestFinishedAsync(SearchIndexRebuild.JobType, CancellationToken.None) is { } finished)
                {
                    return finished;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "The app never rebuilt the search index.");
            await Task.Delay(50);
        }
    }

    private static async Task<JsonElement> StartAsync(HttpClient client, HttpStatusCode expected)
    {
        using var response = await SearchApi.SendAsync(client, HttpMethod.Post, Rebuild.OriginalString, revision: null);
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Holds a rebuild after its first batch until the test lets it go on.</summary>
    internal sealed class RebuildGate : IDisposable
    {
        public ManualResetEventSlim Filled { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Filled.Dispose();
            Release.Dispose();
        }
    }

    /// <summary>The real rebuild, held by <see cref="RebuildGate"/> after it reports its first batch.</summary>
    internal sealed class GatedRebuildHandler(SearchIndexRebuild rebuild, RebuildGate gate) : IJobHandler
    {
        public async Task<JsonElement?> RunAsync(IJobContext context, CancellationToken cancellationToken)
        {
            _ = await rebuild.RunAsync(
                (_, _) =>
                {
                    gate.Filled.Set();
                    gate.Release.Wait(TimeSpan.FromSeconds(30), cancellationToken);
                },
                cancellationToken);
            return null;
        }
    }
}
