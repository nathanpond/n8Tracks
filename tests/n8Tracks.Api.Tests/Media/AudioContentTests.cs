using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;
using n8Tracks.Application.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Serving a cataloged audio file's bytes (#217): ranges, caching by a tag of the file as opened,
/// media types, who may read it, the cases where it cannot be served (and that the request never
/// changes the record), what the response never carries, and how a send ends when the client goes or
/// the file changes. Reading through the media mount's escape checks on a read-only tree is in
/// <see cref="MediaMountGuardTests"/>.
/// </summary>
public sealed class AudioContentTests
{
    [Fact]
    public async Task TheWholeFileIsServedWithItsTypeLengthTagAndOwnName()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "album/One Song.mp3", "mp3");
        var bytes = await File.ReadAllBytesAsync(MediaApi.Fixture("mp3"));

        using var response = await client.GetAsync(Content(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        AssertRevalidated(response);
        Assert.NotNull(response.Content.Headers.LastModified);

        var tag = response.Headers.ETag;
        Assert.NotNull(tag);
        Assert.False(tag.IsWeak);
        var stat = new FileInfo(MediaApi.FullPath(factory, "album/One Song.mp3"));
        Assert.Equal(AudioContentService.EntityTag(new MediaFileStat(stat.Length, new DateTimeOffset(stat.LastWriteTimeUtc, TimeSpan.Zero))), tag.Tag);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("inline", disposition?.DispositionType);
        Assert.Equal("One Song.mp3", disposition?.FileNameStar);

        // Nothing answered names where the file is: not the mount, not its folder.
        var headers = Headers(response);
        Assert.DoesNotContain(factory.MediaPath, headers, StringComparison.Ordinal);
        Assert.DoesNotContain("album", headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameOutsideAsciiHasAnAsciiFallbackAndItsOwnFormInRfc5987()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "Café Ünïcode.flac", "flac");

        using var response = await client.GetAsync(Content(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var header = string.Join(", ", response.Content.Headers.GetValues("Content-Disposition"));
        Assert.StartsWith("inline;", header, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''Caf%C3%A9%20%C3%9Cn%C3%AFcode.flac", header, StringComparison.Ordinal);
        Assert.Matches("filename=\"?Caf_ _n_code.flac\"?", header);
        Assert.Equal("Café Ünïcode.flac", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task ByteRangesAreHonoured()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.wav", "wav");
        var bytes = await File.ReadAllBytesAsync(MediaApi.Fixture("wav"));
        var length = bytes.Length;

        // The first 100 bytes, from 100 to the end, and the last 50.
        foreach (var (range, from, to) in new[] { ("bytes=0-99", 0, 99), ("bytes=100-", 100, length - 1), ("bytes=-50", length - 50, length - 1) })
        {
            using var response = await GetAsync(client, id, request => request.Headers.Range = RangeHeaderValue.Parse(range));
            Assert.True(response.StatusCode == HttpStatusCode.PartialContent, range);
            Assert.Equal(bytes[from..(to + 1)], await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(new ContentRangeHeaderValue(from, to, length), response.Content.Headers.ContentRange);
            Assert.Equal(to - from + 1, response.Content.Headers.ContentLength);
            Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        }

        // A range past the end.
        using (var past = await GetAsync(client, id, request => request.Headers.Range = RangeHeaderValue.Parse($"bytes={length}-")))
        {
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, past.StatusCode);
            Assert.Equal($"bytes */{length}", past.Content.Headers.ContentRange?.ToString());
        }

        // Several ranges: the whole file, which browsers accept.
        using (var several = await GetAsync(client, id, request => request.Headers.Range = RangeHeaderValue.Parse("bytes=0-9,20-29")))
        {
            Assert.Equal(HttpStatusCode.OK, several.StatusCode);
            Assert.Equal(bytes, await several.Content.ReadAsByteArrayAsync());
        }

        // If-Range: the current tag gets the range; another tag gets the whole file.
        using var whole = await client.GetAsync(Content(id));
        var tag = whole.Headers.ETag!;
        using (var current = await GetAsync(client, id, request =>
        {
            request.Headers.Range = RangeHeaderValue.Parse("bytes=0-9");
            request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }))
        {
            Assert.Equal(HttpStatusCode.PartialContent, current.StatusCode);
            Assert.Equal(bytes[..10], await current.Content.ReadAsByteArrayAsync());
        }

        using (var stale = await GetAsync(client, id, request =>
        {
            request.Headers.Range = RangeHeaderValue.Parse("bytes=0-9");
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"0-0\""));
        }))
        {
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            Assert.Equal(bytes, await stale.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task TheCurrentTagIsNotModifiedAndAFileChangedOnDiskIsServedAfresh()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");

        using var first = await client.GetAsync(Content(id));
        var tag = first.Headers.ETag!;

        using (var again = await GetAsync(client, id, request => request.Headers.IfNoneMatch.Add(tag)))
        {
            Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
            Assert.Empty(await again.Content.ReadAsByteArrayAsync());
            Assert.Equal(tag, again.Headers.ETag);
            AssertRevalidated(again);
        }

        // Changed on disk, not rescanned: the tag of the file as opened now is another, and the new bytes come.
        var changed = await File.ReadAllBytesAsync(MediaApi.Fixture("ogg"));
        MediaApi.Write(factory, "a.mp3", changed);
        File.SetLastWriteTimeUtc(MediaApi.FullPath(factory, "a.mp3"), DateTime.UtcNow.AddMinutes(1));
        using (var fresh = await GetAsync(client, id, request => request.Headers.IfNoneMatch.Add(tag)))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal(changed, await fresh.Content.ReadAsByteArrayAsync());
            Assert.NotEqual(tag, fresh.Headers.ETag);
        }
    }

    [Fact]
    public async Task HeadIsAnsweredWithoutABodyAndOtherMethodsAreNotAllowed()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");
        var length = new FileInfo(MediaApi.Fixture("mp3")).Length;

        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Content(id))))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(length, head.Content.Headers.ContentLength);
            Assert.Equal("audio/mpeg", head.Content.Headers.ContentType?.MediaType);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var refused = await SessionApi.SendAsync(client, method, Content(id));
            await SetupApi.ProblemAsync(refused, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
            Assert.Equal(["GET", "HEAD"], refused.Content.Headers.Allow);
        }

        Assert.True(File.Exists(MediaApi.FullPath(factory, "a.mp3")));
    }

    [Fact]
    public async Task EachFormatIsServedWithItsMediaType()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wav"] = "audio/wav",
            ["m4a"] = "audio/mp4",
            ["mp3"] = "audio/mpeg",
            ["flac"] = "audio/flac",
            ["ogg"] = "audio/ogg",
            ["opus"] = "audio/ogg",
            ["aac"] = "audio/aac",
        };
        foreach (var format in expected.Keys)
        {
            MediaApi.Place(factory, $"tone.{format}", format);
        }

        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (items, _) = await MediaApi.ListAsync(client);
        Assert.Equal(7, items.Count);
        foreach (var item in items)
        {
            var format = item.GetProperty("format").GetString()!;
            using var response = await client.GetAsync(Content(item.GetProperty("id").GetGuid()));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected[format], response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(await File.ReadAllBytesAsync(MediaApi.Fixture(format)), await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task ASessionAndACatalogReadTokenMayReadAndNoOneElse()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");
        var bytes = await File.ReadAllBytesAsync(MediaApi.Fixture("mp3"));
        MediaApi.Mount(factory).Reset();

        using (var anonymous = factory.CreateClient())
        using (var refused = await anonymous.GetAsync(Content(id)))
        {
            await SetupApi.ProblemAsync(refused, HttpStatusCode.Unauthorized, "not_authenticated");
        }

        var others = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using (var refused = await CredentialApi.SendAsync(client, HttpMethod.Get, Content(id), others))
        {
            var problem = await SetupApi.ProblemAsync(refused, HttpStatusCode.Forbidden, "insufficient_scope");
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }

        // Neither refusal opened the file.
        Assert.Equal(0, MediaApi.Mount(factory).Opens);

        var reader = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using (var read = await CredentialApi.SendAsync(client, HttpMethod.Get, Content(id), reader))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task AnUnknownOrMalformedIdIsNotFound()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);

        using (var unknown = await client.GetAsync(Content(Guid.NewGuid())))
        {
            await SetupApi.ProblemAsync(unknown, HttpStatusCode.NotFound, "not_found");
        }

        using (var malformed = await client.GetAsync(new Uri("/api/v1/audio-files/not-an-id/content", UriKind.Relative)))
        {
            await SetupApi.ProblemAsync(malformed, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal(0, MediaApi.Mount(factory).Opens);
    }

    /// <summary>The stored status decides first: a Missing record is not served even when its file is back on disk, and is not changed.</summary>
    [Fact]
    public async Task AMissingFileIsUnavailableAndItsRecordIsUnchanged()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");
        MediaApi.Place(factory, "keep.mp3", "mp3");
        File.Delete(MediaApi.FullPath(factory, "a.mp3"));
        MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal("missing", Row(factory, "a.mp3").Split('|')[0]);

        // Back on disk, not yet rescanned.
        MediaApi.Place(factory, "a.mp3", "mp3");
        var before = Row(factory, "a.mp3");
        MediaApi.Mount(factory).Reset();

        using (var response = await client.GetAsync(Content(id)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, AudioContentUnavailable);
        }

        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Content(id))))
        {
            Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        }

        Assert.Equal(0, MediaApi.Mount(factory).Opens);
        Assert.Equal(before, Row(factory, "a.mp3"));
    }

    [Fact]
    public async Task EveryFileIsUnavailableWhileTheMediaFolderIsAndNoRecordChanges()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaAvailability>().RecordAsync(readable: false, CancellationToken.None);
        }

        var before = Row(factory, "a.mp3");
        MediaApi.Mount(factory).Reset();

        using (var response = await client.GetAsync(Content(id)))
        {
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, AudioContentUnavailable);
        }

        Assert.Equal(0, MediaApi.Mount(factory).Opens);
        Assert.Equal(before, Row(factory, "a.mp3"));

        // Complement: the same file once the folder is back.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaAvailability>().RecordAsync(readable: true, CancellationToken.None);
        }

        using var served = await client.GetAsync(Content(id));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    /// <summary>The live read decides for a record that reports Available: a file gone since the last scan is 404, logged by ID only, and the record stays as it was.</summary>
    [Fact]
    public async Task AFileGoneSinceTheLastScanIsUnavailableLoggedByIdOnlyAndItsRecordIsUnchanged()
    {
        using var factory = new LoggingApiFactory { TestServices = MediaApi.UseCountingMount };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "Private Name/Secret Title.mp3", "mp3");
        File.Delete(MediaApi.FullPath(factory, "Private Name/Secret Title.mp3"));
        var before = Row(factory, "Private Name/Secret Title.mp3");

        using (var response = await client.GetAsync(Content(id)))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Secret", body, StringComparison.Ordinal);
            await SetupApi.ProblemAsync(response, HttpStatusCode.NotFound, AudioContentUnavailable);
        }

        Assert.Equal(before, Row(factory, "Private Name/Secret Title.mp3"));
        Assert.Equal("available", before.Split('|')[0]);
        Assert.Equal(0, MediaApi.Mount(factory).OpenHandles);

        var warning = Assert.Single(factory.Lines(), line => line.GetProperty("level").GetString() == "Warning" && line.ToString().Contains(id.ToString(), StringComparison.Ordinal));
        Assert.Equal(id.ToString(), LoggingApiFactory.Property(warning, "audioFileId"));
        Assert.DoesNotContain("Secret", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Name", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.MediaPath, factory.CapturedText, StringComparison.Ordinal);
    }

    /// <summary>A record is served whatever its association: here one with none.</summary>
    [Fact]
    public async Task ServingNeverWritesTheRecordOrTheFolder()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");
        var before = Row(factory, "a.mp3");
        var listing = MediaApi.Listing(factory.MediaPath);

        for (var i = 0; i < 3; i++)
        {
            using var response = await GetAsync(client, id, request => request.Headers.Range = RangeHeaderValue.Parse("bytes=10-"));
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        }

        Assert.Equal(before, Row(factory, "a.mp3"));
        Assert.Equal(listing, MediaApi.Listing(factory.MediaPath));
        Assert.Equal(0, MediaApi.Mount(factory).OpenHandles);
    }

    [Fact]
    public async Task AClientThatGoesAwayStopsTheReadAndTheFileIsClosed()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogLargeAsync(factory, client, "large.wav");
        var mount = MediaApi.Mount(factory);

        using (var cancel = new CancellationTokenSource())
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Content(id)), HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var body = await response.Content.ReadAsStreamAsync(cancel.Token);
            var buffer = new byte[4096];
            Assert.True(await body.ReadAsync(buffer, cancel.Token) > 0);
            Assert.Equal(1, mount.OpenHandles);

            await cancel.CancelAsync();
        }

        await WaitUntilAsync(() => mount.OpenHandles == 0, "the file was never closed after the client went away");
    }

    [Fact]
    public async Task ServingDoesNotHoldUpOtherRequests()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogLargeAsync(factory, client, "large.wav");
        var mount = MediaApi.Mount(factory);

        // Several players that read nothing more after the headers.
        var stalled = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                stalled.Add(await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Content(id)), HttpCompletionOption.ResponseHeadersRead));
            }

            Assert.Equal(8, mount.OpenHandles);
            var watch = Stopwatch.StartNew();
            var (items, _) = await MediaApi.ListAsync(client);
            using (var another = await GetAsync(client, id, request => request.Headers.Range = RangeHeaderValue.Parse("bytes=0-9")))
            {
                Assert.Equal(HttpStatusCode.PartialContent, another.StatusCode);
            }

            Assert.Single(items);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Other requests waited {watch.Elapsed}.");
        }
        finally
        {
            foreach (var response in stalled)
            {
                response.Dispose();
            }
        }

        await WaitUntilAsync(() => mount.OpenHandles == 0, "a stalled send kept its file open");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileChangedWhileItIsSentCutsTheConnectionAndIsLoggedByIdOnly(bool truncate)
    {
        using var factory = new LoggingApiFactory { TestServices = MediaApi.UseCountingMount };
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogLargeAsync(factory, client, "Secret Large.wav");
        var expected = new FileInfo(MediaApi.FullPath(factory, "Secret Large.wav")).Length;

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Content(id)), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        var received = (long)await body.ReadAsync(buffer);

        // Another program appends to the file, or cuts it short, while it is being sent.
        await using (var other = new FileStream(MediaApi.FullPath(factory, "Secret Large.wav"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            if (truncate)
            {
                other.SetLength(1024 * 1024);
            }
            else
            {
                other.Seek(0, SeekOrigin.End);
                await other.WriteAsync(new byte[1024]);
            }
        }

        var cut = await Record.ExceptionAsync(async () =>
        {
            int read;
            while ((read = await body.ReadAsync(buffer)) > 0)
            {
                received += read;
            }
        });

        Assert.True(cut is not null || received < expected, "The whole, changed file was sent as if it were the one the headers described.");
        Assert.True(received < expected + 1024);
        await WaitUntilAsync(() => MediaApi.Mount(factory).OpenHandles == 0, "the file was never closed after the send was cut");
        await WaitUntilAsync(() => factory.CapturedText.Contains("changed or ended early", StringComparison.Ordinal), "the cut send was not logged");

        var warning = Assert.Single(factory.Lines(), static line => line.ToString().Contains("changed or ended early", StringComparison.Ordinal));
        Assert.Equal("Warning", warning.GetProperty("level").GetString());
        Assert.Equal(id.ToString(), LoggingApiFactory.Property(warning, "audioFileId"));
        Assert.DoesNotContain("Secret", factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.MediaPath, factory.CapturedText, StringComparison.Ordinal);
    }

    /// <summary>A paused player keeps its connection: the minimum response data rate is off for this endpoint, and only here.</summary>
    [Fact]
    public async Task TheMinimumResponseDataRateIsOffForAudioOnly()
    {
        var rates = new Dictionary<string, MinDataRate?>(StringComparer.Ordinal);
        using var factory = MediaApi.Host(services: services => services.AddSingleton<IStartupFilter>(new RateFeatureFilter(rates)));
        using var client = await SessionApi.SignedInClientAsync(factory);
        var id = await CatalogAsync(factory, client, "a.mp3", "mp3");

        using (var response = await client.GetAsync(Content(id)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var list = await client.GetAsync(MediaApi.AudioFiles))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        lock (rates)
        {
            Assert.Null(rates[Content(id).ToString()]);
            Assert.NotNull(rates[MediaApi.AudioFiles.ToString()]);
        }
    }

    private const string AudioContentUnavailable = "audio_file_unavailable";

    /// <summary>Kept for the session only, and checked by tag before each use.</summary>
    private static void AssertRevalidated(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache.Private && cache.MustRevalidate && cache.MaxAge == TimeSpan.Zero && !cache.NoStore, cache.ToString());
    }

    private static Uri Content(Guid id) => new($"/api/v1/audio-files/{id}/content", UriKind.Relative);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Guid id, Action<HttpRequestMessage> change)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Content(id));
        change(request);
        return await client.SendAsync(request);
    }

    /// <summary>Places the fixture at <paramref name="relativePath"/>, scans, and answers the file's ID.</summary>
    private static async Task<Guid> CatalogAsync(N8TracksApiFactory factory, HttpClient client, string relativePath, string format)
    {
        MediaApi.Place(factory, relativePath, format);
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (items, _) = await MediaApi.ListAsync(client);
        return MediaApi.ByPath(items, relativePath).GetProperty("id").GetGuid();
    }

    /// <summary>A WAV fixture followed by 16 MB, far more than a response buffers, cataloged.</summary>
    private static async Task<Guid> CatalogLargeAsync(N8TracksApiFactory factory, HttpClient client, string relativePath)
    {
        var bytes = (await File.ReadAllBytesAsync(MediaApi.Fixture("wav"))).Concat(new byte[16 * 1024 * 1024]).ToArray();
        MediaApi.Write(factory, relativePath, bytes);
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (items, _) = await MediaApi.ListAsync(client);
        return MediaApi.ByPath(items, relativePath).GetProperty("id").GetGuid();
    }

    /// <summary>The stored fields a request could change, joined: status, revision, last seen, size, modified time, association.</summary>
    private static string Row(N8TracksApiFactory factory, string path) => TestDatabase.Scalar(
        factory.DataPath,
        $"SELECT status || '|' || revision || '|' || last_seen_utc || '|' || size_bytes || '|' || modified_utc || '|' || coalesce(song_id, '-') || '|' || coalesce(unmatched_reason, '-') FROM audio_files WHERE path = '{path}';");

    private static string Headers(HttpResponseMessage response) =>
        string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(static header => $"{header.Key}: {string.Join(", ", header.Value)}"));

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(50);
        }
    }

    /// <summary>Gives each request a minimum-data-rate feature, as Kestrel does, and records what the endpoint left it as, by path.</summary>
    private sealed class RateFeatureFilter(Dictionary<string, MinDataRate?> rates) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                var feature = new RateFeature();
                context.Features.Set<IHttpMinResponseDataRateFeature>(feature);
                await inner(context);
                lock (rates)
                {
                    rates[context.Request.Path.Value!] = feature.MinDataRate;
                }
            });
            next(app);
        };
    }

    private sealed class RateFeature : IHttpMinResponseDataRateFeature
    {
        public MinDataRate? MinDataRate { get; set; } = new(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
    }
}
