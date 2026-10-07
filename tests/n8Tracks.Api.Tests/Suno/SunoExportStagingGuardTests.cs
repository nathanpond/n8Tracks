using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Suno.Import;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Guard for invariant 3, first half (#131, #138): receiving, classifying, and proposing a Suno export,
/// and changing its choices, never change catalog data. Every catalog table is snapshotted, whole rows,
/// before the first export is uploaded and after a second is classified and proposed (with every class
/// present, Conflict included, its records read, an image staged, choices changed by ID and by filter, the
/// review's summary, targets, and current export read (#139), the resolution choices of a Changed and a
/// Conflict record made (#141), a Suno state change set to Skip and back (#142), and the earlier export
/// discarded), and the two must be identical. The catalog tables are named explicitly, and so are the
/// tables that are not catalog data; a new table in neither list fails the guard until it is placed.
/// The commit story (#140) extends this to the commit itself.
/// <para>
/// Not catalog: the staging tables, the workspace records (<c>suno_workspaces</c>: what Suno says about
/// its own workspaces is provider state, applied at once from a complete list; which workspace a Song
/// is in is <c>songs.suno_workspace_id</c>, catalog data), stored image content (<c>assets</c>: only an
/// attachment or a Generation's image makes it part of the catalog), and the instance's own records
/// (sign-in, credentials, jobs, migrations).
/// </para>
/// </summary>
public sealed class SunoExportStagingGuardTests
{
    /// <summary>The catalog: everything an import may change only by the user's confirmed choice.</summary>
    public static readonly IReadOnlyList<string> CatalogTables =
    [
        "album_links", "album_songs", "albums", "artist_aliases", "artist_links", "artists", "artwork_attachments",
        "audio_files", "download_records", "editor_revisions", "external_suno_references", "generation_comments", "generation_event_links", "generation_events",
        "generations", "genres", "pending_file_deletions", "playlist_songs", "playlists", "provider_records",
        "provider_tombstones", "retention_groups", "retention_records", "settings", "shortcode_aliases", "shortcode_sequence",
        "song_artist_credits", "song_genres", "song_links", "song_relationship_types", "song_relationships", "song_tags", "songs",
        "suno_ignored_items", "suno_models", "suno_personas", "suno_playlists", "tags", "used_version_numbers",
        "version_file_inputs", "version_inspiration_playlists", "version_sources", "version_voices", "versions", "workflow_states",
    ];

    /// <summary>Not catalog data: staging, provider state, stored content, and the instance's own records.</summary>
    public static readonly IReadOnlyList<string> OtherTables =
    [
        "__EFMigrationsHistory", "__EFMigrationsLock", "administrators", "app_metadata", "assets", "credentials", "jobs", "sessions",
        "suno_export_parts", "suno_export_record_playlists", "suno_export_records", "suno_exports", "suno_generation_requests", "suno_workspaces",
    ];

    [Fact]
    public async Task EveryTableIsNamedAsCatalogOrNot()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        var tables = TestDatabase.Rows(factory.DataPath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        Assert.Empty(CatalogTables.Intersect(OtherTables, StringComparer.Ordinal));
        Assert.All(tables, table => Assert.True(
            CatalogTables.Contains(table, StringComparer.Ordinal) || OtherTables.Contains(table, StringComparer.Ordinal),
            $"Table {table} is in neither list of the invariant 3 guard: name it as catalog data or say why it is not."));
        Assert.All(CatalogTables, table => Assert.Contains(table, tables));
    }

    [Fact]
    public async Task ReceivingClassifyingProposingAndChoosingChangeNoCatalogTable()
    {
        using var factory = SongApi.Host();
        Assert.Empty(await ChangedTablesAsync(factory));
    }

    /// <summary>
    /// The guard bites: with the clip lookup made to write a provider record while it classifies, the
    /// same run reports <c>provider_records</c> as changed.
    /// </summary>
    [Fact]
    public async Task TheGuardFailsWhenClassifyingWritesAProviderRecord()
    {
        N8TracksApiFactory? created = null;
        using var factory = created = new N8TracksApiFactory
        {
            TestServices = services =>
            {
                var real = services.Single(static service => service.ServiceType == typeof(ISunoClipLookup));
                services.Remove(real);
                services.AddScoped<ISunoClipLookup>(provider => new WritingLookup((ISunoClipLookup)real.ImplementationFactory!(provider), created!.DataPath));
            },
        };

        Assert.Equal(["provider_records"], await ChangedTablesAsync(factory));
    }

    /// <summary>
    /// Every catalog table's rows (but <paramref name="leaveOut"/>), each table's as one text with its rows
    /// sorted: equal before and after means no catalog row was added, changed, or removed.
    /// </summary>
    public static SortedDictionary<string, string> Snapshot(string dataPath, params string[] leaveOut)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = TestDatabase.FilePath(dataPath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in CatalogTables.Except(leaveOut, StringComparer.Ordinal))
        {
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT * FROM \"{table}\";";
            using var reader = select.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var row = new StringBuilder();
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    row.Append(reader.GetName(column)).Append('=')
                        .Append(reader.IsDBNull(column) ? "<null>" : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture)).Append('|');
                }

                rows.Add(row.ToString());
            }

            rows.Sort(StringComparer.Ordinal);
            snapshot[table] = string.Join('\n', rows);
        }

        return snapshot;
    }

    /// <summary>
    /// Builds a catalog with something in every class's way (Generations, a tombstone, an ignored clip, a
    /// workspace a Song is in, a Generation whose clip is in Suno's Trash), snapshots it, then uploads an
    /// export, uploads, completes, reads, stages an image for, and makes every kind of choice on a second
    /// that replaces it; returns the catalog tables whose rows differ.
    /// </summary>
    private static async Task<List<string>> ChangedTablesAsync(N8TracksApiFactory factory)
    {
        using var client = await SessionApi.SignedInClientAsync(factory);
        var token = await SunoWorkspaceApi.ExtensionTokenAsync(factory);
        var library = SunoExportApi.LibraryClips();
        var trash = SunoExportApi.FixtureClips(SunoExportApi.TrashFixture);
        await SunoWorkspaceApi.ReportAsync(client, token, complete: true, SunoWorkspaceApi.Project("studio", "Studio"));
        var song = await SongApi.CreateAsync(client, "Already here");
        await SunoWorkspaceApi.AssociatedAsync(client, "n8-1", "studio");
        await ImportedVersions.AttachAsync(factory, song, "2", library[0]);
        await ImportedVersions.AttachAsync(factory, song, "3", library[1]);
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", library[2].ToJsonString());
        using (var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("/api/v1/generations/n8-1-v1-g1", UriKind.Relative)))
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
            Assert.True(request.Headers.TryAddWithoutValidation("If-Match", "\"1\""));
            using var deleted = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.OK, deleted.StatusCode);
        }

        SunoExportApi.Ignore(factory, SunoExportApi.IdOf(library[3]));

        // #141: a Generation whose clip now has other lyrics in Suno (Conflict). #142: one whose clip is in Suno's Trash.
        var conflict = ProposalApi.Clip("conflict-1", null, ProposalApi.At, 0, "Conflict words");
        await ImportedVersions.AttachAsync(factory, song, "4", conflict);
        var conflictNow = conflict.DeepClone();
        conflictNow["metadata"]!["prompt"] = "Other conflict words";
        var trashed = ProposalApi.Clip("trashed-1", null, ProposalApi.At.AddHours(1), 0, "Trashed words");
        await ImportedVersions.AttachAsync(factory, song, "5", trashed);

        // The catalog as it is before the first upload: a write every upload or classification makes the
        // same way would already be in place by a later snapshot.
        var before = Snapshot(factory.DataPath);
        var (earlier, _) = await SunoExportApi.UploadAsync(client, token, SunoExportApi.Header(), SunoExportApi.Part(1, [library[0]]));

        var retitled = library[1].DeepClone();
        retitled["title"] = "Changed in Suno";
        var (id, export) = await SunoExportApi.UploadAsync(
            client,
            token,
            SunoExportApi.Header(
                workspaces: [SunoWorkspaceApi.Project("studio", "Studio, renamed"), SunoWorkspaceApi.Project("elsewhere", "Elsewhere")],
                workspacesComplete: true,
                playlists: [SunoExportApi.Playlist("favourites", "Favourites", SunoExportApi.IdOf(library[0]))]),
            SunoExportApi.Part(2, [library[2], library[3]], [.. trash, trashed]),
            SunoExportApi.Part(1, [library[0], retitled, JsonNode.Parse(Clips.Minimal("brand-new"))!, conflictNow]));
        Assert.Equal("ready", export.GetProperty("state").GetString());
        foreach (var recordClass in new[] { "new", "linked", "changed", "conflict", "ignored", "deleted" })
        {
            Assert.True(SunoExportApi.Count(export, recordClass) > 0, recordClass);
        }

        Assert.Equal("discarded", (await SunoExportApi.GetAsync(client, token, earlier)).GetProperty("state").GetString());
        await SunoExportApi.RecordsAsync(client, id, "?class=changed");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(Assets.ArtworkImages.Solid(SkiaSharp.SKEncodedImageFormat.Png, 32, 32, Assets.ArtworkImages.Blue)), "file", "cover.png");
            using var request = new HttpRequestMessage(HttpMethod.Put, SunoExportApi.Export(id, "/artwork/" + SunoExportApi.IdOf(library[0]))) { Content = form };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var staged = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.OK, staged.StatusCode);
        }

        // Proposals were made (#138), and changing choices, even to targets in the catalog, writes none of it.
        Assert.Equal("import", (await SunoExportApi.RecordsByIdAsync(client, id))["brand-new"].GetProperty("choice").GetProperty("action").GetString());
        var newVersion = new JsonObject { ["kind"] = "newVersion", ["key"] = "new:50", ["song"] = "n8-1", ["parentVersion"] = "n8-1-v2", ["number"] = "2.1" };
        await ProposalApi.ChangedAsync(client, id, 1, ProposalApi.Change(ProposalApi.Import(newVersion), "brand-new"));
        await ProposalApi.ChangedAsync(client, id, 2, ProposalApi.Change(new JsonObject { ["action"] = "ignore" }, "brand-new"));

        // The review page's reads and its change by filter (#139) write none of it either.
        await ProposalApi.ChangedAsync(client, id, 3, ImportReviewApi.ByFilter(new JsonObject(), new JsonObject { ["action"] = "ignore" }));
        await ImportReviewApi.SummaryAsync(client, id);
        await ImportReviewApi.TargetsAsync(client, id, "brand-new", "?song=n8-1&parent=n8-1-v2");
        await ImportReviewApi.CurrentAsync(client);

        // The resolution choices of a Changed and a Conflict record (#141) write none of it.
        await ProposalApi.ChangedAsync(client, id, 4, ProposalApi.Change(new JsonObject { ["action"] = "apply", ["acceptFields"] = new JsonArray("title") }, SunoExportApi.IdOf(retitled)));
        await ProposalApi.ChangedAsync(client, id, 5, ProposalApi.Change(new JsonObject { ["action"] = "moveToNewVersion" }, "conflict-1"));
        await ProposalApi.ChangedAsync(client, id, 6, ProposalApi.Change(new JsonObject { ["action"] = "keep" }, "conflict-1"));

        // Nor does setting a Suno state change to Skip and back (#142).
        Assert.Equal("trashed", (await RemoteStateApi.RowsAsync(client, id))["trashed-1"].GetProperty("change").GetString());
        await RemoteStateApi.SetAsync(client, id, false, "trashed-1");
        await RemoteStateApi.SetAsync(client, id, true, "trashed-1");

        // Provider state did change (a complete list), so the run reached it; the catalog did not.
        Assert.Equal("Studio, renamed", (await SunoWorkspaceApi.OneAsync(client, "studio")).GetProperty("name").GetString());
        var after = Snapshot(factory.DataPath);
        return [.. before.Keys.Where(table => !string.Equals(before[table], after[table], StringComparison.Ordinal))];
    }

    /// <summary>A clip lookup that, as a classifier must never do, writes to the catalog while it looks up.</summary>
    private sealed class WritingLookup(ISunoClipLookup inner, string dataPath) : ISunoClipLookup
    {
        public async Task<IReadOnlyDictionary<string, LinkedClip>> LiveGenerationsAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken)
        {
            var found = await inner.LiveGenerationsAsync(sunoIds, cancellationToken);
            TestDatabase.Execute(dataPath, "UPDATE provider_records SET payload = payload || ' ';");
            return found;
        }

        public Task<IReadOnlySet<string>> IgnoredAsync(IReadOnlyCollection<string> sunoIds, CancellationToken cancellationToken) =>
            inner.IgnoredAsync(sunoIds, cancellationToken);
    }
}
