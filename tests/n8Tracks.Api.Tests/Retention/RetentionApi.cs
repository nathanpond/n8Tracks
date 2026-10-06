using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Retention;
using n8Tracks.Infrastructure.Retention;

namespace n8Tracks.Api.Tests.Retention;

/// <summary>
/// Helpers for the retention tests: a host with a controllable clock and the test-only retained types
/// below, a Version with snapshots to retain, and the retention service called in a scope of its own.
/// </summary>
internal static class RetentionApi
{
    /// <summary>
    /// A test-only retained type that owns a managed file: <c>test_artwork</c>, a child of a Version
    /// (cascading), with a revision. Its shape is version 2: version 1 called <c>file</c> <c>path</c>
    /// and had no <c>caption</c>, which its upgrader fills in.
    /// </summary>
    public static readonly RetainedType TestArtwork = new("test-artwork", "test_artwork", "test artwork", ShapeVersion: 2)
    {
        Upgraders = new Dictionary<int, Func<JsonObject, JsonObject>>
        {
            [1] = static document =>
            {
                var path = document["path"]?.DeepClone();
                document.Remove("path");
                document["file"] = path;
                document["caption"] = null;
                return document;
            },
        },
    };

    /// <summary>A test-only child of test artwork (cascading), retained only when a test registers it.</summary>
    public static readonly RetainedType TestArtworkNote = new("test-artwork-note", "test_artwork_notes", "test artwork note", ShapeVersion: 1);

    /// <summary>The test tables, created after the host has migrated the database.</summary>
    public const string TestTables =
        """
        CREATE TABLE test_artwork (
            id TEXT NOT NULL PRIMARY KEY,
            version_id TEXT NOT NULL REFERENCES versions (id) ON DELETE CASCADE,
            file TEXT NOT NULL,
            caption TEXT NULL,
            revision INTEGER NOT NULL);
        CREATE TABLE test_artwork_notes (
            artwork_id TEXT NOT NULL REFERENCES test_artwork (id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            text TEXT NOT NULL,
            PRIMARY KEY (artwork_id, position));
        """;

    /// <summary>
    /// A host on <paramref name="clock"/> with the test artwork type registered (and its note type when
    /// <paramref name="withNotes"/>), whose live test artwork rows count as using their files.
    /// </summary>
    public static N8TracksApiFactory Host(TimeProvider clock, bool withNotes = false)
    {
        N8TracksApiFactory? factory = null;
        factory = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                services.AddSingleton(TestArtwork);
                if (withNotes)
                {
                    services.AddSingleton(TestArtworkNote);
                }

                services.AddSingleton<ILiveFileReferences>(_ => new LiveTestArtwork(factory!.DataPath));
            },
        };
        return factory;
    }

    /// <summary>Creates the test tables; call after the first request, once the database is migrated.</summary>
    public static void CreateTestTables(N8TracksApiFactory factory) => TestDatabase.Execute(factory.DataPath, TestTables);

    /// <summary>A new Song's Version 1 with the given snapshots, oldest first; the Version's ID and the snapshots' IDs.</summary>
    public static async Task<(Guid Version, string Shortcode, List<Guid> Snapshots)> VersionWithSnapshotsAsync(HttpClient client, string title, params string[] lyrics)
    {
        var song = await SongApi.CreateAsync(client, title);
        var version = song.GetProperty("currentVersion");
        var id = version.GetProperty("id").GetGuid();
        var snapshots = new List<Guid>();
        foreach (var text in lyrics)
        {
            snapshots.Add(await SnapshotAsync(client, id, text));
        }

        return (id, version.GetProperty("shortcode").GetString()!, snapshots);
    }

    /// <summary>Takes a snapshot of <paramref name="lyrics"/> through the API; its ID.</summary>
    public static async Task<Guid> SnapshotAsync(HttpClient client, Guid version, string lyrics)
    {
        using var response = await SongApi.SendJsonAsync(
            client,
            HttpMethod.Post,
            new Uri($"/api/v1/versions/{version}/snapshots", UriKind.Relative),
            JsonSerializer.Serialize(new { lyrics, styles = "kept" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await SetupApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The IDs the History list shows for a Version, newest first.</summary>
    public static async Task<List<Guid>> HistoryAsync(HttpClient client, Guid version)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{version}/snapshots", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await SetupApi.JsonAsync(response)).GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("id").GetGuid())];
    }

    /// <summary>The status of a GET of one snapshot.</summary>
    public static async Task<HttpStatusCode> ReadSnapshotAsync(HttpClient client, Guid version, Guid snapshot)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/versions/{version}/snapshots/{snapshot}", UriKind.Relative));
        return response.StatusCode;
    }

    /// <summary>A request retaining one snapshot, as the history-entry deletion will.</summary>
    public static RetentionRequest Snapshot(Guid snapshot, string shortcode = "n8-1-v1", params string[] files) =>
        new(RetainedRecordTypes.EditorSnapshot, $"History entry of {shortcode}", null, [new RetainedRoot(RetainedRecordTypes.EditorSnapshot, snapshot)], files);

    /// <summary>Calls the retention service in a scope of its own.</summary>
    public static async Task<T> WithServiceAsync<T>(N8TracksApiFactory factory, Func<RetentionService, Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(call);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await call(scope.ServiceProvider.GetRequiredService<RetentionService>());
        }
    }

    public static Task<RetentionGroup> RetainAsync(N8TracksApiFactory factory, RetentionRequest request) =>
        WithServiceAsync(factory, service => service.RetainAsync(request, CancellationToken.None));

    public static Task<RetentionRestoreOutcome> RestoreAsync(N8TracksApiFactory factory, Guid group) =>
        WithServiceAsync(factory, service => service.RestoreAsync(group, CancellationToken.None));

    public static Task<RetentionPruneSummary> PruneAsync(N8TracksApiFactory factory) =>
        WithServiceAsync(factory, static service => service.PruneAsync(CancellationToken.None));

    /// <summary>Adds a live test artwork row of <paramref name="version"/> using <paramref name="file"/>; its ID.</summary>
    public static Guid AddTestArtwork(N8TracksApiFactory factory, Guid version, string file, int revision = 1)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var id = Guid.CreateVersion7();
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO test_artwork (id, version_id, file, caption, revision) VALUES ('{Upper(id)}', '{Upper(version)}', '{file}', 'Cover', {revision});");
        return id;
    }

    /// <summary>Writes a managed file under the data path's assets folder; its full path.</summary>
    public static string WriteManagedFile(N8TracksApiFactory factory, string path, string content = "image bytes")
    {
        ArgumentNullException.ThrowIfNull(factory);

        var full = Path.Combine(factory.DataPath, "assets", path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Every column of every row of <paramref name="table"/> (nulls as <c>∅</c>), in rowid order: a snapshot to compare.</summary>
    public static List<string> Dump(N8TracksApiFactory factory, string table)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var columns = TestDatabase.Rows(factory.DataPath, $"SELECT name FROM pragma_table_info('{table}');");
        return TestDatabase.Rows(factory.DataPath, $"SELECT {string.Join(", ", columns.Select(static column => $"COALESCE(quote({column}), '∅')"))} FROM {table} ORDER BY rowid;");
    }

    public static string Upper(Guid id) => id.ToString().ToUpperInvariant();

    /// <summary>The live side of the test type: a live <c>test_artwork</c> row uses its file.</summary>
    private sealed class LiveTestArtwork(string dataPath) : ILiveFileReferences
    {
        public Task<bool> IsReferencedAsync(string path, CancellationToken cancellationToken)
        {
            var exists = TestDatabase.Scalar(dataPath, "SELECT count(*) FROM sqlite_master WHERE name = 'test_artwork';") == "1"
                && TestDatabase.Scalar(dataPath, $"SELECT count(*) FROM test_artwork WHERE file = '{path.Replace("'", "''", StringComparison.Ordinal)}';") != "0";
            return Task.FromResult(exists);
        }
    }
}
