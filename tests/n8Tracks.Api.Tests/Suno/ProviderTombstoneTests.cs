using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Backups;
using n8Tracks.Api.Tests.Generations;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Generations;
using n8Tracks.Application.Retention;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;
using static n8Tracks.Api.Tests.Retention.RetentionApi;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>
/// Provider tombstones (#130): every Generation deleted from n8Tracks, alone (#124), with its Version
/// (#101), or with its Song (#102), leaves a tombstone of its Suno ID, written in the deletion's
/// transaction, so the attach service (the only way an import creates a Generation) refuses the clip
/// with <c>suno_id_tombstoned</c> unless the user chose Reimport for it (invariant 3). A Reimport
/// attaches it and removes the tombstone; a restore removes exactly the tombstones of the Generations
/// it brings back; the 30-day prune of the deleted Generation leaves the tombstone where it is.
/// </summary>
public sealed class ProviderTombstoneTests
{
    private const string Origin = "n8-1";
    private const string Elsewhere = "n8-2";
    private const string ElsewhereVersion = "n8-2-v1";

    [Theory]
    [InlineData("generation")]
    [InlineData("version")]
    [InlineData("song")]
    public async Task AfterEachKindOfDeletionTheAttachServiceRefusesTheSunoIdUntilItIsReimported(string deletion)
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SongApi.CreateAsync(client, "Elsewhere");

        await DeleteAsync(client, deletion);

        // One tombstone per Generation with a Suno ID that went; the seeded Generation without one writes none.
        string[] expected = deletion == "generation" ? ["clip-one"] : ["clip-one", "clip-two"];
        Assert.Equal(expected, TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));
        foreach (var sunoId in expected)
        {
            var tombstone = await FindAsync(factory, sunoId);
            Assert.NotNull(tombstone);
            Assert.Equal(new ProviderTombstone(sunoId, ProviderTombstoneKind.Clip, clock.GetUtcNow(), "Minimal clip"), tombstone);
        }

        // The attach service refuses each of them, changing nothing.
        foreach (var sunoId in expected)
        {
            var before = RestoreApi.Fingerprint(factory.DataPath);
            var refused = Assert.IsType<GenerationAttachOutcome.SunoIdTombstoned>(await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal(sunoId)));
            Assert.Equal(sunoId, refused.Tombstone.SunoId);
            Assert.Equal(before, RestoreApi.Fingerprint(factory.DataPath));
        }

        // Complement: an unrelated Suno ID attaches normally.
        Assert.Equal("n8-2-v1-g1", (await SongApi.AttachGenerationAsync(factory, ElsewhereVersion, Clips.Minimal("clip-unrelated"))).Shortcode);

        // With the reimport option it attaches, and its tombstone (only its own) is gone.
        var reimported = Assert.IsType<GenerationAttachOutcome.Attached>(
            await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal("clip-one"), new GenerationAttachOptions(Reimport: true)));
        Assert.Equal("n8-2-v1-g2", reimported.Generation.Shortcode);
        Assert.Null(await FindAsync(factory, "clip-one"));
        Assert.Equal(expected.Skip(1), TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));

        // A clip that is live again is refused as one a Generation holds, tombstone or not.
        Assert.IsType<GenerationAttachOutcome.SunoIdExists>(
            await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal("clip-one"), new GenerationAttachOptions(Reimport: true)));
    }

    [Fact]
    public async Task TheReimportOptionOnAClipThatWasNeverDeletedAttachesItAsUsual()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await SongApi.CreateAsync(client, "Elsewhere");

        Assert.IsType<GenerationAttachOutcome.Attached>(
            await SongApi.AttachAsync(factory, "n8-1-v1", Clips.Minimal("clip-fresh"), new GenerationAttachOptions(Reimport: true)));
        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM provider_tombstones;"));
    }

    [Fact]
    public async Task ATombstoneOutlivesThePruneOfWhatWasDeleted()
    {
        var clock = new TestClock();
        using var factory = SongApi.Host(clock);
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SongApi.CreateAsync(client, "Elsewhere");
        await DeleteAsync(client, "generation");
        var tombstones = TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || kind || '|' || deleted_utc || '|' || title FROM provider_tombstones;");

        clock.Advance(RetentionService.RetentionPeriod);
        Assert.Equal(1, (await PruneAsync(factory)).GroupsPruned);

        Assert.Equal("0", TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM retention_groups;"));
        Assert.Equal(tombstones, TestDatabase.Rows(factory.DataPath, "SELECT suno_id || '|' || kind || '|' || deleted_utc || '|' || title FROM provider_tombstones;"));
        Assert.IsType<GenerationAttachOutcome.SunoIdTombstoned>(await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal("clip-one")));
    }

    [Theory]
    [InlineData("generation", "n8-1-v1-g1")]
    [InlineData("version", "n8-1-v1")]
    [InlineData("song", "n8-1")]
    public async Task ARestoreRemovesExactlyTheTombstonesOfTheGenerationsItBringsBack(string deletion, string restored)
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await SongApi.CreateAsync(client, "Elsewhere");
        await SongApi.AttachGenerationAsync(factory, ElsewhereVersion, Clips.Minimal("clip-other"));

        // Deleted in groups of their own first, so not in the group restored: clip-two and another Song's clip.
        await DeleteGenerationAsync(client, "n8-2-v1-g1");
        await DeleteGenerationAsync(client, "n8-1-v1-g2");
        await DeleteAsync(client, deletion);
        Assert.Equal(["clip-one", "clip-other", "clip-two"], TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));

        Assert.IsType<DeletedItemRestoreOutcome.Restored>(await RestoreAsync(factory, restored));

        Assert.Equal(["clip-other", "clip-two"], TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));
        Assert.Equal("n8-1-v1-g1", (await SetupApi.JsonAsync(await client.GetAsync(Uri("generations/n8-1-v1-g1")))).GetProperty("shortcode").GetString());
        Assert.IsType<GenerationAttachOutcome.SunoIdExists>(await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal("clip-one")));
        Assert.IsType<GenerationAttachOutcome.SunoIdTombstoned>(await SongApi.AttachAsync(factory, ElsewhereVersion, Clips.Minimal("clip-two")));

        // Restoring the Generation deleted alone brings its own tombstone's removal, and only that.
        Assert.IsType<DeletedItemRestoreOutcome.Restored>(await RestoreAsync(factory, "n8-1-v1-g2"));
        Assert.Equal(["clip-other"], TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));
    }

    [Fact]
    public async Task ARefusedRestoreLeavesTheTombstone()
    {
        using var factory = SongApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        await OriginAsync(factory, client);
        await DeleteGenerationAsync(client, "n8-1-v1-g1");
        await DeleteAsync(client, "version");

        // The Generation's Version is gone, so its restore is refused, and the clip stays tombstoned.
        Assert.IsType<DeletedItemRestoreOutcome.Refused>(await RestoreAsync(factory, "n8-1-v1-g1"));
        Assert.Equal(["clip-one", "clip-two"], TestDatabase.Rows(factory.DataPath, "SELECT suno_id FROM provider_tombstones ORDER BY suno_id;"));
    }

    /// <summary>
    /// Song n8-1 ("Origin") with three Generations on its Version 1: n8-1-v1-g1 and -g2 hold Suno IDs
    /// clip-one and clip-two; -g3 is seeded without Suno data.
    /// </summary>
    private static async Task OriginAsync(N8TracksApiFactory factory, HttpClient client)
    {
        await SongApi.CreateAsync(client, "Origin");
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-one"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1", Clips.Minimal("clip-two"));
        await SongApi.AttachGenerationAsync(factory, "n8-1-v1");
    }

    /// <summary>Deletes from Origin: its Generation n8-1-v1-g1 alone, its Version 1 (with all three), or the Song.</summary>
    private static async Task DeleteAsync(HttpClient client, string deletion)
    {
        switch (deletion)
        {
            case "generation":
                await DeleteGenerationAsync(client, "n8-1-v1-g1");
                break;

            case "version":
                var version = await SetupApi.JsonAsync(await client.GetAsync(Uri("versions/n8-1-v1")));
                using (var deleted = await SendAsync(client, HttpMethod.Delete, "versions/n8-1-v1", version.GetProperty("revision").GetInt32()))
                {
                    Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
                }

                break;

            case "song":
                var song = await SetupApi.JsonAsync(await client.GetAsync(SongApi.Song(Origin)));
                using (var deleted = await SendAsync(client, HttpMethod.Delete, $"songs/{Origin}", song.GetProperty("revision").GetInt32(), JsonSerializer.Serialize(new { confirmTitle = "Origin" })))
                {
                    Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(deletion), deletion, "Not a kind of deletion.");
        }
    }

    private static async Task DeleteGenerationAsync(HttpClient client, string shortcode)
    {
        var generation = await SetupApi.JsonAsync(await client.GetAsync(Uri($"generations/{shortcode}")));
        using var deleted = await SendAsync(client, HttpMethod.Delete, $"generations/{shortcode}", generation.GetProperty("revision").GetInt32());
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
    }

    private static async Task<ProviderTombstone?> FindAsync(N8TracksApiFactory factory, string sunoId)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<TombstoneService>().FindAsync(sunoId, CancellationToken.None);
        }
    }

    private static async Task<DeletedItemRestoreOutcome> RestoreAsync(N8TracksApiFactory factory, string reference)
    {
        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<DeletedItemsService>().RestoreAsync(reference, CancellationToken.None);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, int revision, string? json = null)
    {
        using var request = new HttpRequestMessage(method, Uri(path));
        if (json is not null)
        {
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        Assert.True(request.Headers.TryAddWithoutValidation("If-Match", SongApi.Quoted(revision)));
        return await client.SendAsync(request);
    }

    private static Uri Uri(string path) => new($"/api/v1/{path}", UriKind.Relative);
}
