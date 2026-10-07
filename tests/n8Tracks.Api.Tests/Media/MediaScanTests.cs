using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Health;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The media scan (#203) over the host's temporary media folder: what it catalogs, what it reads
/// again, what it never touches, and how it ends when the folder or a file misbehaves.
/// </summary>
public sealed class MediaScanTests
{
    private static readonly string[] Formats = ["wav", "mp3", "m4a", "flac", "ogg", "opus", "aac"];

    [Fact]
    public async Task EveryFormatHasItsHeaderRead()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var format in Formats)
        {
            MediaApi.Place(factory, $"tone.{format}", format);
        }

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(7, result.GetProperty("seen").GetInt32());
        Assert.Equal(0, result.GetProperty("unreadable").GetInt32());

        var (items, _) = await MediaApi.ListAsync(client);
        foreach (var format in Formats)
        {
            var file = MediaApi.ByPath(items, $"tone.{format}");
            Assert.Equal(format, file.GetProperty("format").GetString());
            Assert.True(file.GetProperty("metadataReadable").GetBoolean(), $"{format}: {file}");
            var seconds = file.GetProperty("durationSeconds").GetDecimal();
            // The tones are 1.5 s; a header estimate (MP3 without an exact parse) may count the encoder's padding.
            Assert.True(seconds is > 1.3m and < 2.0m, $"{format}: {seconds}");
        }

        // Every format but raw ADTS AAC, which has nowhere to keep tags, carries the title and artist.
        foreach (var format in Formats.Where(static format => format != "aac"))
        {
            var file = MediaApi.ByPath(items, $"tone.{format}");
            Assert.True(file.GetProperty("title").GetString() == "Fixture Title", $"{format}: {file}");
            Assert.Equal("Fixture Artist", file.GetProperty("artist").GetString());
        }

        var adts = MediaApi.ByPath(items, "tone.aac");
        Assert.Equal(JsonValueKind.Null, adts.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, adts.GetProperty("artist").ValueKind);
    }

    [Fact]
    public async Task AScanCatalogsEverySupportedFileWhereverItIsAndCountsTheRest()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "top.wav", "wav");
        MediaApi.Place(factory, "Album/Disc 1/01 First Song.MP3", "mp3");
        MediaApi.Place(factory, "deep/a/b/c/d/e/f/g/tone.Flac", "flac");
        MediaApi.Place(factory, "Ünïcödé/Śong — ✨.m4a", "m4a");
        MediaApi.Place(factory, ".hidden/.dot tone.OGG", "ogg");
        MediaApi.Place(factory, "with spaces/tone two.oPuS", "opus");
        MediaApi.Place(factory, "raw/tone.AAC", "aac");
        MediaApi.Write(factory, "Album/notes.txt", "liner notes"u8.ToArray());
        MediaApi.Write(factory, "Album/cover.jpg", [0xFF, 0xD8, 0xFF]);
        MediaApi.Write(factory, "Album/tone.mp3.part", [1, 2, 3]);
        MediaApi.Write(factory, "empty.mp3", []);
        var wav = File.ReadAllBytes(MediaApi.Fixture("wav"));
        MediaApi.Write(factory, "broken/truncated.wav", wav[..12]);

        var job = await MediaApi.ScanAsync(client);
        var result = MediaApi.Result(job);
        Assert.Equal("manual", result.GetProperty("trigger").GetString());
        Assert.Equal(9, result.GetProperty("seen").GetInt32());
        Assert.Equal(9, result.GetProperty("new").GetInt32());
        Assert.Equal(0, result.GetProperty("changed").GetInt32());
        Assert.Equal(0, result.GetProperty("unchanged").GetInt32());
        Assert.Equal(3, result.GetProperty("skipped").GetInt32());
        Assert.Equal(2, result.GetProperty("unreadable").GetInt32());
        Assert.Equal(0, result.GetProperty("unreadableDirectories").GetInt32());
        Assert.Equal("""{"escaping":0,"cycle":0,"dangling":0}""", result.GetProperty("skippedLinks").GetRawText());
        Assert.True(result.GetProperty("elapsedSeconds").GetDecimal() >= 0);
        Assert.Equal(100, job.GetProperty("progress").GetInt32());
        Assert.Contains("9 of 9 files", job.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (items, body) = await MediaApi.ListAsync(client);
        Assert.Equal(
            [".hidden/.dot tone.OGG", "Album/Disc 1/01 First Song.MP3", "broken/truncated.wav", "deep/a/b/c/d/e/f/g/tone.Flac", "empty.mp3", "raw/tone.AAC", "top.wav", "with spaces/tone two.oPuS", "Ünïcödé/Śong — ✨.m4a"],
            items.Select(static item => item.GetProperty("path").GetString()!));

        var song = MediaApi.ByPath(items, "Album/Disc 1/01 First Song.MP3");
        Assert.Equal("01 First Song.MP3", song.GetProperty("fileName").GetString());
        Assert.Equal("mp3", song.GetProperty("format").GetString());
        Assert.Equal(new FileInfo(MediaApi.Fixture("mp3")).Length, song.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("available", song.GetProperty("status").GetString());
        Assert.Equal("Fixture Title", song.GetProperty("title").GetString());
        Assert.Equal("Fixture Artist", song.GetProperty("artist").GetString());
        var modified = File.GetLastWriteTimeUtc(MediaApi.FullPath(factory, "Album/Disc 1/01 First Song.MP3"));
        Assert.Equal(modified.AddTicks(-(modified.Ticks % TimeSpan.TicksPerSecond)), song.GetProperty("modifiedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(song.GetProperty("firstSeenAt").GetDateTime(), song.GetProperty("lastSeenAt").GetDateTime());
        foreach (var association in new[] { "song", "generation", "associationOrigin", "unmatchedReason" })
        {
            Assert.Equal(JsonValueKind.Null, song.GetProperty(association).ValueKind);
        }

        // Unreadable files are still cataloged from their name and size, with no duration.
        foreach (var (path, size) in new[] { ("empty.mp3", 0L), ("broken/truncated.wav", 12L) })
        {
            var file = MediaApi.ByPath(items, path);
            Assert.Equal(size, file.GetProperty("sizeBytes").GetInt64());
            Assert.False(file.GetProperty("metadataReadable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, file.GetProperty("durationSeconds").ValueKind);
        }

        // The relative path only: the mount root appears nowhere in the answers.
        Assert.DoesNotContain(factory.MediaPath, body, StringComparison.Ordinal);
        using var one = await client.GetAsync(new Uri($"/api/v1/audio-files/{song.GetProperty("id").GetGuid()}", UriKind.Relative));
        var oneBody = await one.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.DoesNotContain(factory.MediaPath, oneBody, StringComparison.Ordinal);
        Assert.Equal("Album/Disc 1/01 First Song.MP3", JsonDocument.Parse(oneBody).RootElement.GetProperty("path").GetString());
        Assert.DoesNotContain(factory.MediaPath, job.ToString(), StringComparison.Ordinal);

        // The stored summary of the last scan.
        using var scope = factory.Services.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<MediaScanService>().LastScanAsync(CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(job.GetProperty("id").GetGuid(), summary.JobId);
        Assert.Equal(MediaScanTrigger.Manual, summary.Trigger);
        Assert.Equal(MediaScanOutcome.Succeeded, summary.Outcome);
        Assert.Equal(new MediaScanCounts(9, 9, 0, 0, 3, 2, 0), summary.Counts);
        Assert.Null(summary.Error);
    }

    [Fact]
    public async Task ASecondScanReadsOnlyTheFilesThatChanged()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var first = MediaApi.Place(factory, "a.mp3", "mp3");
        var second = MediaApi.Place(factory, "b/b.flac", "flac");
        MediaApi.Place(factory, "c.ogg", "ogg");
        MediaApi.Write(factory, "broken.wav", [1, 2, 3, 4]);
        var mount = MediaApi.Mount(factory);

        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(4, mount.Opens);
        var (before, _) = await MediaApi.ListAsync(client);

        // Nothing changed: no file is opened, an unreadable one included.
        mount.Reset();
        var unchanged = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, mount.Opens);
        Assert.Equal(4, unchanged.GetProperty("seen").GetInt32());
        Assert.Equal(4, unchanged.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, unchanged.GetProperty("new").GetInt32());
        Assert.Equal(0, unchanged.GetProperty("unreadable").GetInt32());

        // A change below the second is not a change: times compare to the whole second.
        var time = File.GetLastWriteTimeUtc(first);
        File.SetLastWriteTimeUtc(first, time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond)).AddMilliseconds(999));
        mount.Reset();
        Assert.Equal(4, MediaApi.Result(await MediaApi.ScanAsync(client)).GetProperty("unchanged").GetInt32());
        Assert.Equal(0, mount.Opens);

        // A new modified time on one file, and a new size on another: only those two are read again.
        File.SetLastWriteTimeUtc(first, time.AddMinutes(-5));
        await File.AppendAllBytesAsync(second, new byte[16]);
        mount.Reset();
        var changed = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, changed.GetProperty("changed").GetInt32());
        Assert.Equal(2, changed.GetProperty("unchanged").GetInt32());
        Assert.Equal(2, mount.Opens);
        Assert.Equal(1, mount.OpensOf("a.mp3"));
        Assert.Equal(1, mount.OpensOf("b/b.flac"));

        // Each keeps its record (ID and first-seen time) and gains the new size and time.
        var (after, _) = await MediaApi.ListAsync(client);
        foreach (var path in new[] { "a.mp3", "b/b.flac" })
        {
            var old = MediaApi.ByPath(before, path);
            var now = MediaApi.ByPath(after, path);
            Assert.Equal(old.GetProperty("id").GetGuid(), now.GetProperty("id").GetGuid());
            Assert.Equal(old.GetProperty("firstSeenAt").GetDateTime(), now.GetProperty("firstSeenAt").GetDateTime());
            Assert.True(now.GetProperty("lastSeenAt").GetDateTime() >= old.GetProperty("lastSeenAt").GetDateTime());
        }

        Assert.Equal(new FileInfo(second).Length, MediaApi.ByPath(after, "b/b.flac").GetProperty("sizeBytes").GetInt64());
        var reread = MediaApi.ByPath(after, "a.mp3").GetProperty("modifiedAt").GetDateTime().ToUniversalTime();
        Assert.Equal(time.AddMinutes(-5).AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond)), reread);
    }

    [Fact]
    public async Task AChangedFileWhoseHeaderCanNoLongerBeReadLosesItsMetadataButKeepsItsRecord()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var path = MediaApi.Place(factory, "song.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (before, _) = await MediaApi.ListAsync(client);
        Assert.True(MediaApi.ByPath(before, "song.mp3").GetProperty("metadataReadable").GetBoolean());

        await File.WriteAllBytesAsync(path, new byte[300]);
        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadable").GetInt32());

        var (after, _) = await MediaApi.ListAsync(client);
        var file = MediaApi.ByPath(after, "song.mp3");
        Assert.Equal(MediaApi.ByPath(before, "song.mp3").GetProperty("id").GetGuid(), file.GetProperty("id").GetGuid());
        Assert.False(file.GetProperty("metadataReadable").GetBoolean());
        Assert.Equal(300, file.GetProperty("sizeBytes").GetInt64());
        foreach (var cleared in new[] { "durationSeconds", "title", "artist" })
        {
            Assert.Equal(JsonValueKind.Null, file.GetProperty(cleared).ValueKind);
        }
    }

    [Fact]
    public async Task AScanChangesNothingInTheMediaFolder()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var format in Formats)
        {
            MediaApi.Place(factory, $"nested/{format}/tone.{format}", format);
        }

        MediaApi.Place(factory, "copy one.mp3", "mp3");
        MediaApi.Place(factory, "copy two.mp3", "mp3");
        MediaApi.Write(factory, "readme.txt", "hello"u8.ToArray());
        MediaApi.Write(factory, "empty.wav", []);
        MediaApi.Write(factory, ".hidden/.x.flac", [1, 2, 3]);
        var before = MediaApi.Listing(factory.MediaPath);

        MediaApi.Result(await MediaApi.ScanAsync(client));
        MediaApi.Result(await MediaApi.ScanAsync(client));

        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));

        // Complement: the unsupported file has no record, and two paths with the same bytes have two.
        var (items, _) = await MediaApi.ListAsync(client);
        Assert.DoesNotContain(items, static item => item.GetProperty("fileName").GetString() == "readme.txt");
        Assert.NotEqual(MediaApi.ByPath(items, "copy one.mp3").GetProperty("id").GetGuid(), MediaApi.ByPath(items, "copy two.mp3").GetProperty("id").GetGuid());
        Assert.Equal(11, items.Count);
    }

    [Fact]
    public async Task ASecondRequestWhileAScanIsQueuedOrRunningAnswersThatJob()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        using var hold = new ManualResetEventSlim(false);
        var mount = MediaApi.Mount(factory);
        mount.HoldListings = hold;

        using var first = await SessionApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var started = await SetupApi.JsonAsync(first);
        var id = started.GetProperty("jobId").GetGuid();
        Assert.False(started.GetProperty("alreadyInProgress").GetBoolean());
        Assert.EndsWith($"/api/v1/jobs/{id}", first.Headers.Location!.ToString(), StringComparison.Ordinal);

        for (var again = 0; again < 3; again++)
        {
            using var second = await SessionApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var answer = await SetupApi.JsonAsync(second);
            Assert.Equal(id, answer.GetProperty("jobId").GetGuid());
            Assert.True(answer.GetProperty("alreadyInProgress").GetBoolean());
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT COUNT(*) FROM jobs WHERE type = '{MediaScanService.JobType}';"));

        hold.Set();
        MediaApi.Result(await MediaApi.WaitAsync(client, id));

        // Once it has ended, the next request queues a new scan.
        mount.HoldListings = null;
        var next = await MediaApi.ScanAsync(client);
        Assert.NotEqual(id, next.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnAbsentMediaFolderFailsTheScanAndChangesNoRecord()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        MediaApi.Place(factory, "b/b.ogg", "ogg");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var rows = AudioRows(factory);

        var moved = factory.MediaPath + "-unplugged";
        Directory.Move(factory.MediaPath, moved);
        try
        {
            var job = await MediaApi.ScanAsync(client);
            Assert.Equal("failed", job.GetProperty("status").GetString());
            Assert.Contains(MediaFolderUnavailableException.Text, job.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Equal(rows, AudioRows(factory));

            using var scope = factory.Services.CreateScope();
            var summary = await scope.ServiceProvider.GetRequiredService<MediaScanService>().LastScanAsync(CancellationToken.None);
            Assert.NotNull(summary);
            Assert.Equal(job.GetProperty("id").GetGuid(), summary.JobId);
            Assert.Equal(MediaScanOutcome.Failed, summary.Outcome);
            Assert.Equal(MediaFolderUnavailableException.Text, summary.Error);
            Assert.Equal(MediaScanCounts.None, summary.Counts);
        }
        finally
        {
            Directory.Move(moved, factory.MediaPath);
        }
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task ASubdirectoryThatCannotBeListedIsCountedAndTheScanGoesOn()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "open/a.mp3", "mp3");
        MediaApi.Place(factory, "locked/b.mp3", "mp3");
        var locked = MediaApi.FullPath(factory, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (CanList(locked))
            {
                // A privileged process reads it anyway; the container's read-only mount is the proof there (#205).
                return;
            }

            var result = MediaApi.Result(await MediaApi.ScanAsync(client));
            Assert.Equal(1, result.GetProperty("unreadableDirectories").GetInt32());
            Assert.Equal(1, result.GetProperty("seen").GetInt32());
            var (items, _) = await MediaApi.ListAsync(client);
            Assert.Equal("open/a.mp3", Assert.Single(items).GetProperty("path").GetString());
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ASummaryWrittenBeforeLinksWereCountedReadsAsNoneSkipped()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        TestDatabase.Execute(
            factory.DataPath,
            """
            INSERT INTO settings (key, value) VALUES ('media.lastScan', '{"jobId":"0192f1a4-0000-7000-8000-000000000001","trigger":"manual","outcome":"succeeded","startedUtc":"2026-10-08T10:00:00.000Z","finishedUtc":"2026-10-08T10:00:01.000Z","counts":{"seen":1,"new":1,"changed":0,"unchanged":0,"skipped":2,"unreadable":0,"unreadableDirectories":0}}')
            ON CONFLICT (key) DO UPDATE SET value = excluded.value;
            """);

        using var scope = factory.Services.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<MediaScanService>().LastScanAsync(CancellationToken.None);
        Assert.Equal(new MediaScanCounts(1, 1, 0, 0, 2, 0, 0), summary?.Counts);
        Assert.Equal(MediaSkippedLinks.None, summary?.Counts.SkippedLinks);
    }

    [Fact]
    public async Task AHeaderThatTakesTooLongCountsAsUnreadableAndTheScanGoesOn()
    {
        using var factory = MediaApi.Host(new MediaScanOptions { HeaderReadTimeout = TimeSpan.FromMilliseconds(300) });
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "slow.mp3", "mp3");
        MediaApi.Place(factory, "quick.mp3", "mp3");
        var mount = MediaApi.Mount(factory);
        mount.SlowPaths["slow.mp3"] = true;

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("seen").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadable").GetInt32());
        var (items, _) = await MediaApi.ListAsync(client);
        Assert.False(MediaApi.ByPath(items, "slow.mp3").GetProperty("metadataReadable").GetBoolean());
        Assert.True(MediaApi.ByPath(items, "quick.mp3").GetProperty("metadataReadable").GetBoolean());
    }

    [Fact]
    public async Task AScanThatFailsPartWayKeepsWhatItWroteAndReportsHowFarItGot()
    {
        using var factory = MediaApi.Host(new MediaScanOptions { BatchSize = 1 });
        using var client = await SessionApi.SignedInClientAsync(factory);

        // Files at the root are looked at before those in a subdirectory.
        MediaApi.Place(factory, "1.mp3", "mp3");
        MediaApi.Place(factory, "2.ogg", "ogg");
        MediaApi.Place(factory, "later/boom.mp3", "mp3");
        MediaApi.Mount(factory).FailingStats["later/boom.mp3"] = true;

        var job = await MediaApi.ScanAsync(client);
        Assert.Equal("failed", job.GetProperty("status").GetString());
        Assert.Contains("2 of 3 files: 2 new", job.GetProperty("message").GetString(), StringComparison.Ordinal);

        var (items, _) = await MediaApi.ListAsync(client);
        Assert.Equal(["1.mp3", "2.ogg"], items.Select(static item => item.GetProperty("path").GetString()!));

        using var scope = factory.Services.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<MediaScanService>().LastScanAsync(CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(MediaScanOutcome.Failed, summary.Outcome);
        Assert.Equal(new MediaScanCounts(2, 2, 0, 0, 0, 0, 0), summary.Counts);
    }

    [Fact]
    public async Task ScanningIsSessionOnlyAndReadingNeedsCatalogRead()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "a.mp3", "mp3");
        Assert.Empty((await MediaApi.ListAsync(client)).Items);
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var fileId = Assert.Single((await MediaApi.ListAsync(client)).Items).GetProperty("id").GetGuid();
        var one = new Uri($"/api/v1/audio-files/{fileId}", UriKind.Relative);

        // A token, whatever its scopes, cannot start a scan.
        var everything = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Post, MediaApi.Scans, everything))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "session_required");
        }

        Assert.Equal("1", TestDatabase.Scalar(factory.DataPath, $"SELECT COUNT(*) FROM jobs WHERE type = '{MediaScanService.JobType}';"));

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        foreach (var uri in new[] { MediaApi.AudioFiles, one })
        {
            using var read = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, reader);
            var body = await read.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.DoesNotContain(factory.MediaPath, body, StringComparison.Ordinal);
        }

        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        foreach (var uri in new[] { MediaApi.AudioFiles, one })
        {
            using var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, others);
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }
    }

    [Fact]
    public async Task TheListFiltersAndPagesInPathOrder()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        foreach (var name in new[] { "c", "a", "e", "b", "d" })
        {
            MediaApi.Place(factory, $"{name}.mp3", "mp3");
        }

        MediaApi.Write(factory, "z-broken.flac", [1, 2, 3]);
        MediaApi.Result(await MediaApi.ScanAsync(client));

        using (var response = await client.GetAsync(new Uri("/api/v1/audio-files?offset=1&limit=2", UriKind.Relative)))
        {
            var page = await SetupApi.JsonAsync(response);
            Assert.Equal(6, page.GetProperty("total").GetInt32());
            Assert.Equal(1, page.GetProperty("offset").GetInt32());
            Assert.Equal(2, page.GetProperty("limit").GetInt32());
            Assert.Equal(["b.mp3", "c.mp3"], page.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("path").GetString()!));
        }

        using (var response = await client.GetAsync(MediaApi.AudioFiles))
        {
            var page = await SetupApi.JsonAsync(response);
            Assert.Equal(0, page.GetProperty("offset").GetInt32());
            Assert.Equal(200, page.GetProperty("limit").GetInt32());
        }

        Assert.Equal(6, (await MediaApi.ListAsync(client, "?status=available")).Items.Count);
        Assert.Empty((await MediaApi.ListAsync(client, "?status=missing")).Items);
        Assert.Equal(6, (await MediaApi.ListAsync(client, "?association=any")).Items.Count);
        Assert.Equal(6, (await MediaApi.ListAsync(client, "?association=none")).Items.Count);
        Assert.Empty((await MediaApi.ListAsync(client, "?association=associated")).Items);
        Assert.Equal("z-broken.flac", Assert.Single((await MediaApi.ListAsync(client, "?metadataReadable=false")).Items).GetProperty("path").GetString());
        Assert.Equal(5, (await MediaApi.ListAsync(client, "?metadataReadable=true&status=available&association=none")).Items.Count);
    }

    [Theory]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=ten", "limit")]
    [InlineData("offset=-1", "offset")]
    [InlineData("offset=1.5", "offset")]
    [InlineData("status=unavailable", "status")]
    [InlineData("status=AVAILABLE", "status")]
    [InlineData("association=some", "association")]
    [InlineData("metadataReadable=yes", "metadataReadable")]
    [InlineData("status=available&status=missing", "status")]
    public async Task AnUnknownFilterOrALimitOver200IsRefused(string query, string field)
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/audio-files?{query}", UriKind.Relative));
        var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task AnUnknownAudioFileIsNotFound()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using var response = await client.GetAsync(new Uri($"/api/v1/audio-files/{Guid.CreateVersion7()}", UriKind.Relative));
        await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    private static List<string> AudioRows(N8TracksApiFactory factory) =>
        TestDatabase.Rows(
            factory.DataPath,
            "SELECT id, path, size_bytes, modified_utc, first_seen_utc, last_seen_utc, status, metadata_readable, ifnull(duration_ms, ''), ifnull(title, ''), ifnull(artist, '') FROM audio_files ORDER BY path;");

    private static bool CanList(string directory)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(directory).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
