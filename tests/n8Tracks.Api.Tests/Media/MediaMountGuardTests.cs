using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Health;
using n8Tracks.Api.Tests.Logging;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The guard of invariant 2 (#205): n8Tracks never writes under the media mount, and nothing outside
/// the mount is reached through it. Every member of <see cref="IMediaMount"/> is exercised on a tree
/// the operating system makes read-only; links that lead out (a file, a directory, a chain, a relative
/// target) are not followed and nothing behind them is cataloged or read; a link that stays inside is
/// followed and a cycle of links ends its branch; a cataloged file swapped for an escaping link is
/// refused at the open; the resolver refuses traversal by its text; and no API input is a path.
/// <c>MediaMountAccessTests</c> (architecture) proves that nothing but <c>MediaMountReader</c> touches
/// the mount, and that it only reads. Serving audio over HTTP (#217) is in the same cases: it reads a
/// read-only tree and changes nothing, a file swapped for a link out is 404 with no byte behind it,
/// and an audio file is named by its ID only.
/// </summary>
public sealed partial class MediaMountGuardTests
{
    /// <summary>
    /// Inputs named like a path that name nothing under the mount: the <c>/api/v1</c> fallback, which
    /// answers 404 to any route nothing else matched and never reads the value (<c>ApiProblem.MapApiNotFound</c>);
    /// and the preferred-file choices' <c>audioFile</c> (#212), an audio file's ID, read only as a UUID
    /// (anything else is a 422), never as text.
    /// </summary>
    private static readonly string[] NotAPath =
    [
        "* /api/v1/{**path}: path",
        "PUT /api/v1/generations/{reference}/preferred-audio-file: body field AudioFile",
        "PUT /api/v1/songs/{reference}/preferred-audio-file: body field AudioFile",
    ];

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task EveryMountOperationSucceedsOnATreeTheOperatingSystemMakesReadOnlyAndNothingChanges()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "album/one.mp3", "mp3");
        MediaApi.Place(factory, "album/deeper/two.flac", "flac");
        MediaApi.Place(factory, "three.ogg", "ogg");
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "alias.mp3"), MediaApi.FullPath(factory, "album/one.mp3"));
        MediaApi.Write(factory, "notes.txt", "not audio"u8.ToArray());

        MakeReadOnly(factory.MediaPath);
        try
        {
            // Complement: the tree really is read-only to this process, so success below is not a write that worked.
            if (Environment.IsPrivilegedProcess || CanWrite(factory.MediaPath))
            {
                // A privileged process writes anyway: the container's read-only mount (scripts/smoke-docker.sh) is the proof there.
                return;
            }

            var before = MediaApi.Listing(factory.MediaPath);

            var first = MediaApi.Result(await MediaApi.ScanAsync(client));
            Assert.Equal(4, first.GetProperty("seen").GetInt32());
            Assert.Equal(4, first.GetProperty("new").GetInt32());
            Assert.Equal(0, first.GetProperty("unreadable").GetInt32());
            var again = MediaApi.Result(await MediaApi.ScanAsync(client));
            Assert.Equal(4, again.GetProperty("unchanged").GetInt32());

            var mount = factory.Services.GetRequiredService<IMediaMount>();
            Assert.True(mount.Probe());
            Assert.NotEmpty(mount.List("album"));
            Assert.Equal(new FileInfo(MediaApi.Fixture("mp3")).Length, mount.Stat("alias.mp3")?.SizeBytes);
            var flac = await File.ReadAllBytesAsync(MediaApi.Fixture("flac"));
            await using (var stream = mount.OpenRead("album/deeper/two.flac"))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(flac, copy.ToArray());
            }

            await using (var opened = mount.OpenWithStat("alias.mp3"))
            {
                Assert.Equal(new FileInfo(MediaApi.Fixture("mp3")).Length, opened.Stat().SizeBytes);
                using var copy = new MemoryStream();
                await opened.Content.CopyToAsync(copy);
                Assert.Equal(await File.ReadAllBytesAsync(MediaApi.Fixture("mp3")), copy.ToArray());
            }

            // Over HTTP (#217): the whole file, a range, and HEAD, from the read-only tree.
            var (items, _) = await MediaApi.ListAsync(client);
            var content = Content(MediaApi.ByPath(items, "album/deeper/two.flac").GetProperty("id").GetGuid());
            using (var whole = await client.GetAsync(content))
            {
                Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
                Assert.Equal(flac, await whole.Content.ReadAsByteArrayAsync());
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, content))
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(100, null);
                using var ranged = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
                Assert.Equal(flac[100..], await ranged.Content.ReadAsByteArrayAsync());
            }

            using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, content)))
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            }

            Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
        }
        finally
        {
            MakeWritable(factory.MediaPath);
        }
    }

    [UnixFact]
    public async Task ALinkThatLeadsOutIsNotFollowedAndNothingBehindItIsCatalogedOrRead()
    {
        using var outside = new TemporaryDirectory();
        var sentinel = Path.Combine(outside.Path, "sentinel.mp3");
        File.Copy(MediaApi.Fixture("mp3"), sentinel);
        Directory.CreateDirectory(Path.Combine(outside.Path, "nested"));
        File.Copy(MediaApi.Fixture("ogg"), Path.Combine(outside.Path, "nested", "behind.ogg"));

        using var factory = new LoggingApiFactory { TestServices = MediaApi.UseCountingMount };
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "real.mp3", "mp3");
        MediaApi.Place(factory, "sub/kept.mp3", "mp3");

        // A file, a directory, a chain of two links, a target relative to the link that climbs out, and one nested deeper.
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "file-out.mp3"), sentinel);
        Directory.CreateSymbolicLink(MediaApi.FullPath(factory, "folder-out"), outside.Path);
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "sub/chain-2.mp3"), sentinel);
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "chain-1.mp3"), MediaApi.FullPath(factory, "sub/chain-2.mp3"));
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "sub/climb.mp3"), $"../../{Path.GetFileName(outside.Path)}/sentinel.mp3");

        // Complement: a link to nothing is skipped too, for its own reason.
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "gone.mp3"), MediaApi.FullPath(factory, "never-there.mp3"));

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(2, result.GetProperty("seen").GetInt32());
        Assert.Equal(6, result.GetProperty("skipped").GetInt32());
        Assert.Equal("""{"escaping":5,"cycle":0,"dangling":1}""", result.GetProperty("skippedLinks").GetRawText());
        using (var scope = factory.Services.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<MediaScanService>().LastScanAsync(CancellationToken.None);
            Assert.Equal(new MediaSkippedLinks(5, 0, 1), summary?.Counts.SkippedLinks);
        }

        var (items, body) = await MediaApi.ListAsync(client);
        Assert.Equal(["real.mp3", "sub/kept.mp3"], items.Select(static item => item.GetProperty("path").GetString()!));
        Assert.DoesNotContain("sentinel", body, StringComparison.Ordinal);
        Assert.DoesNotContain("behind", body, StringComparison.Ordinal);

        // Only the two real files were opened, once each; nothing went through a link out.
        var mount = MediaApi.Mount(factory);
        Assert.Equal(2, mount.Opens);
        Assert.Equal(1, mount.OpensOf("real.mp3"));
        Assert.Equal(1, mount.OpensOf("sub/kept.mp3"));

        // Asked directly, the mount refuses every way out, and reads no byte behind it.
        foreach (var path in new[] { "file-out.mp3", "chain-1.mp3", "sub/chain-2.mp3", "sub/climb.mp3", "folder-out/sentinel.mp3", "folder-out/nested/behind.ogg" })
        {
            Assert.Null(mount.Stat(path));
            Assert.Throws<MediaPathOutsideException>(() => mount.OpenRead(path));
        }

        Assert.Throws<MediaPathOutsideException>(() => mount.List("folder-out"));
        Assert.Throws<MediaPathOutsideException>(() => mount.List("folder-out/nested"));

        // One Warning for the scan: the count and the relative paths, never where a link leads.
        var warning = Assert.Single(factory.Lines(), static line => LoggingApiFactory.Property(line, "skippedLinkPaths") is not null);
        Assert.Equal("Warning", warning.GetProperty("level").GetString());
        Assert.Equal(6, warning.GetProperty("properties").GetProperty("skippedLinkCount").GetInt32());
        var paths = LoggingApiFactory.Property(warning, "skippedLinkPaths")!;
        Assert.Contains("folder-out", paths, StringComparison.Ordinal);
        Assert.Contains("sub/climb.mp3", paths, StringComparison.Ordinal);
        Assert.DoesNotContain(outside.Path, factory.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.MediaPath, factory.CapturedText, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task ALinkThatStaysInsideIsFollowedAndACycleEndsItsBranch()
    {
        using var factory = MediaApi.Host(new MediaScanOptions { DirectoryListTimeout = TimeSpan.FromSeconds(5) });
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "music/a.mp3", "mp3");
        Directory.CreateSymbolicLink(MediaApi.FullPath(factory, "alias"), MediaApi.FullPath(factory, "music"));
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "file-alias.mp3"), "music/a.mp3");

        // Back to the directory itself, and up to the root: each ends its branch, wherever it is met.
        Directory.CreateSymbolicLink(MediaApi.FullPath(factory, "music/back"), "../music");
        Directory.CreateSymbolicLink(MediaApi.FullPath(factory, "music/up"), "..");

        // Two links that lead to each other never resolve: a cycle as well.
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "ping.mp3"), "pong.mp3");
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "pong.mp3"), "ping.mp3");

        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal("""{"escaping":0,"cycle":6,"dangling":0}""", result.GetProperty("skippedLinks").GetRawText());
        Assert.Equal(3, result.GetProperty("seen").GetInt32());

        // Each path is its own record: the file under its own name, through the folder link, and through the file link.
        var (items, _) = await MediaApi.ListAsync(client);
        Assert.Equal(["alias/a.mp3", "file-alias.mp3", "music/a.mp3"], items.Select(static item => item.GetProperty("path").GetString()!));
        Assert.Equal(3, items.Select(static item => item.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.All(items, static item => Assert.True(item.GetProperty("metadataReadable").GetBoolean(), item.ToString()));
    }

    [UnixFact]
    public async Task ACatalogedFileSwappedForAnEscapingLinkIsRefusedAndNoByteBehindItIsRead()
    {
        using var outside = new TemporaryDirectory();
        var sentinel = Path.Combine(outside.Path, "swap.mp3");
        await File.WriteAllBytesAsync(sentinel, "SENTINEL: never read through the media folder"u8.ToArray());
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "swap.mp3", "mp3");
        MediaApi.Place(factory, "folder/swap.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (cataloged, _) = await MediaApi.ListAsync(client);
        var lastSeen = MediaApi.ByPath(cataloged, "swap.mp3").GetProperty("lastSeenAt").GetDateTime();

        // The file, and the folder holding the other, are replaced by links out after they were cataloged.
        File.Delete(MediaApi.FullPath(factory, "swap.mp3"));
        File.CreateSymbolicLink(MediaApi.FullPath(factory, "swap.mp3"), sentinel);
        Directory.Delete(MediaApi.FullPath(factory, "folder"), recursive: true);
        Directory.CreateSymbolicLink(MediaApi.FullPath(factory, "folder"), outside.Path);

        var mount = factory.Services.GetRequiredService<IMediaMount>();
        foreach (var path in new[] { "swap.mp3", "folder/swap.mp3" })
        {
            Assert.Throws<MediaPathOutsideException>(() => mount.OpenRead(path));
            Assert.Null(mount.Stat(path));
        }

        // Served over HTTP (#217): refused at the open, 404, and no byte behind the link in the answer.
        foreach (var path in new[] { "swap.mp3", "folder/swap.mp3" })
        {
            using var response = await client.GetAsync(Content(MediaApi.ByPath(cataloged, path).GetProperty("id").GetGuid()));
            var answer = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("\"audio_file_unavailable\"", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL", answer, StringComparison.Ordinal);
        }

        Assert.Equal(0, MediaApi.Mount(factory).OpenHandles);

        // The next scan does not see either file (#207 marks such records Missing) and reads nothing behind the links.
        MediaApi.Mount(factory).Reset();
        var result = MediaApi.Result(await MediaApi.ScanAsync(client));
        Assert.Equal(0, result.GetProperty("seen").GetInt32());
        Assert.Equal(2, result.GetProperty("skippedLinks").GetProperty("escaping").GetInt32());
        Assert.Equal(0, MediaApi.Mount(factory).Opens);
        var (items, body) = await MediaApi.ListAsync(client);
        Assert.Equal(lastSeen, MediaApi.ByPath(items, "swap.mp3").GetProperty("lastSeenAt").GetDateTime());
        Assert.DoesNotContain("SENTINEL", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../x.mp3")]
    [InlineData("a/../../x.mp3")]
    [InlineData("a/..")]
    [InlineData("./x.mp3")]
    [InlineData("a//x.mp3")]
    [InlineData("a/")]
    [InlineData("/etc/passwd")]
    [InlineData("a\0.mp3")]
    [InlineData("a\\..\\..\\x.mp3")]
    [InlineData("a/b\\c.mp3")]
    [InlineData("C:\\x.mp3")]
    public void TheResolverRefusesAPathThatIsNotPlainlyInsideByItsText(string path)
    {
        using var media = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(media.Path, "a"));
        var mount = Reader(media.Path);

        Assert.Throws<ArgumentException>(() => mount.OpenRead(path));
        Assert.Throws<ArgumentException>(() => mount.Stat(path));
        Assert.Throws<ArgumentException>(() => mount.List(path));
    }

    [UnixFact]
    public void TheResolverFollowsLinksAsTheOperatingSystemDoesAndRefusesWhereverItEndsOutside()
    {
        using var outside = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "secret.mp3"), "outside");
        Directory.CreateDirectory(Path.Combine(media.Path, "a", "b"));
        File.WriteAllText(Path.Combine(media.Path, "a", "b", "in.mp3"), "inside");

        // "..": after a link, from where the link really leads, not from the link's own folder.
        Directory.CreateSymbolicLink(Path.Combine(media.Path, "deep"), Path.Combine(media.Path, "a", "b"));
        File.CreateSymbolicLink(Path.Combine(media.Path, "a", "b", "up-and-out.mp3"), $"../../../{Path.GetFileName(outside.Path)}/secret.mp3");
        File.CreateSymbolicLink(Path.Combine(media.Path, "a", "b", "up-and-in.mp3"), "../b/in.mp3");
        var mount = Reader(media.Path);

        Assert.Equal(6, mount.Stat("deep/in.mp3")?.SizeBytes);
        Assert.Equal(6, mount.Stat("deep/up-and-in.mp3")?.SizeBytes);
        Assert.Null(mount.Stat("deep/up-and-out.mp3"));
        Assert.Throws<MediaPathOutsideException>(() => mount.OpenRead("a/b/up-and-out.mp3"));

        var entries = mount.List("deep").ToDictionary(static entry => entry.Name, StringComparer.Ordinal);
        Assert.Equal(MediaEntryKind.EscapingLink, entries["up-and-out.mp3"].Kind);
        Assert.Equal(MediaEntryKind.File, entries["up-and-in.mp3"].Kind);
        Assert.True(entries["up-and-in.mp3"].ViaLink);
        var root = mount.List(string.Empty).Single(static entry => entry.Name == "deep");
        Assert.Equal((MediaEntryKind.Directory, "a/b", true), (root.Kind, root.RealPath, root.ViaLink));

        // A root that is itself a link is the mount (the operator's choice); a path still may not leave it.
        using var linked = new TemporaryDirectory();
        var linkedRoot = Path.Combine(linked.Path, "media");
        Directory.CreateSymbolicLink(linkedRoot, media.Path);
        var throughLink = Reader(linkedRoot);
        Assert.True(throughLink.Probe());
        Assert.Equal(6, throughLink.Stat("a/b/in.mp3")?.SizeBytes);
        Assert.Throws<MediaPathOutsideException>(() => throughLink.OpenRead("a/b/up-and-out.mp3"));
    }

    [Fact]
    public void TheProbeAnswersWhetherTheRootCanBeListed()
    {
        using var parent = new TemporaryDirectory();
        Assert.True(Reader(parent.Path).Probe());
        Assert.False(Reader(Path.Combine(parent.Path, "not-mounted")).Probe());
        File.WriteAllText(Path.Combine(parent.Path, "file"), "x");
        Assert.False(Reader(Path.Combine(parent.Path, "file")).Probe());
    }

    /// <summary>
    /// What the reader hands out is not its <see cref="FileStream"/> (#386): it cannot write or change
    /// its length, and it has no name, so no caller learns the file's absolute path from it.
    /// </summary>
    [Fact]
    public async Task TheReaderHandsOutAStreamThatCannotWriteAndNamesNoPath()
    {
        using var media = new TemporaryDirectory();
        await File.WriteAllBytesAsync(Path.Combine(media.Path, "a.mp3"), "content"u8.ToArray());
        var mount = Reader(media.Path);

        await using var opened = mount.OpenWithStat("a.mp3");
        await using var read = mount.OpenRead("a.mp3");
        foreach (var stream in new[] { opened.Content, read })
        {
            Assert.IsNotAssignableFrom<FileStream>(stream);
            Assert.Null(stream.GetType().GetProperty("Name"));
            Assert.Null(stream.GetType().GetProperty("SafeFileHandle"));
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
            stream.Seek(2, SeekOrigin.Begin);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal("ntent"u8.ToArray(), copy.ToArray());
        }

        Assert.Equal("content"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(media.Path, "a.mp3")));
    }

    /// <summary>
    /// A cataloged file that is gone by the time it is opened stays gone (#386): the open fails, over
    /// HTTP too, and creates nothing, so no open mode the source scan misses (a numeric cast to
    /// <c>OpenOrCreate</c>) can write into the media folder unseen.
    /// </summary>
    [Fact]
    public async Task OpeningACatalogedFileThatVanishedFailsAndCreatesNothing()
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "kept.mp3", "mp3");
        var gone = MediaApi.Place(factory, "album/gone.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        var (items, _) = await MediaApi.ListAsync(client);
        File.Delete(gone);
        var before = MediaApi.Listing(factory.MediaPath);

        var mount = factory.Services.GetRequiredService<IMediaMount>();
        Assert.Null(mount.Stat("album/gone.mp3"));
        Assert.ThrowsAny<IOException>(() => mount.OpenRead("album/gone.mp3"));
        Assert.ThrowsAny<IOException>(() => mount.OpenWithStat("album/gone.mp3"));

        // Still reported Available (no scan since): the content route reaches the open, which fails.
        using (var response = await client.GetAsync(Content(MediaApi.ByPath(items, "album/gone.mp3").GetProperty("id").GetGuid())))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("\"audio_file_unavailable\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.False(File.Exists(gone));
        Assert.Equal(before, MediaApi.Listing(factory.MediaPath));
    }

    /// <summary>The check after the open asks the kernel where the open file is: a file outside the root fails it.</summary>
    [LinuxFact]
    public void OnLinuxAnOpenedFileIsCheckedAgainThroughProcSelfFd()
    {
        using var outside = new TemporaryDirectory();
        using var media = new TemporaryDirectory();
        var outsideFile = Path.Combine(outside.Path, "x.mp3");
        var insideFile = Path.Combine(media.Path, "x.mp3");
        File.WriteAllText(outsideFile, "x");
        File.WriteAllText(insideFile, "x");
        var realRoot = MediaMountReader.RealPath(media.Path)!;

        using (var stream = new FileStream(outsideFile, FileMode.Open, FileAccess.Read))
        {
            Assert.False(MediaMountReader.OpenedInside(stream, realRoot));
        }

        using (var stream = new FileStream(insideFile, FileMode.Open, FileAccess.Read))
        {
            Assert.True(MediaMountReader.OpenedInside(stream, realRoot));
        }
    }

    /// <summary>
    /// No endpoint takes a path: every handler parameter, and every field of a request body, is read,
    /// and one whose name says path, file, folder, directory, or mount fails the check (an audio file
    /// is named by its ID, which is a UUID, never a text). Routes under the media endpoints are UUIDs.
    /// This check is by name; the structural guard (#384) is <c>MediaMountAccessTests</c>' list of every
    /// call that hands the mount a path and of what the types holding the mount accept, which no input
    /// reaches whatever it is called.
    /// </summary>
    [Fact]
    public void NoEndpointTakesAPath()
    {
        using var factory = new N8TracksApiFactory();

        Assert.Equal(NotAPath, PathInputs(Endpoints(factory)));
    }

    /// <summary>Proves the check bites: a path in a route, a query, and a body is reported.</summary>
    [Fact]
    public void AnEndpointTakingAPathFailsTheCheck()
    {
        using var factory = TestEndpoints.Host(static endpoints =>
        {
            endpoints.MapGet("/api/v1/test/files/{*relativePath}", static (string relativePath) => relativePath);
            endpoints.MapGet("/api/v1/test/open", static (string? folder) => folder);
            endpoints.MapPost("/api/v1/test/by-body", static (PathBody body) => body.Location.FilePath);
        });

        Assert.Equal(
            [
                "GET /api/v1/test/files/{*relativePath}: relativePath",
                "GET /api/v1/test/open: folder",
                "POST /api/v1/test/by-body: body field Location.FilePath",
            ],
            PathInputs(Endpoints(factory)).Where(static violation => violation.Contains("/test/", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("/api/v1/audio-files/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/api/v1/audio-files/%2Fetc%2Fpasswd")]
    [InlineData("/api/v1/audio-files/real.mp3")]
    [InlineData("/api/v1/audio-files/..%2F..%2Fetc%2Fpasswd/content")]
    [InlineData("/api/v1/audio-files/%2Fetc%2Fpasswd/content")]
    [InlineData("/api/v1/audio-files/real.mp3/content")]
    [InlineData("/api/v1/audio-files/real.mp3%2Fcontent")]
    public async Task AnAudioFileNamedByAPathIsNotFound(string path)
    {
        using var factory = MediaApi.Host();
        using var client = await SessionApi.SignedInClientAsync(factory);
        MediaApi.Place(factory, "real.mp3", "mp3");
        MediaApi.Result(await MediaApi.ScanAsync(client));
        MediaApi.Mount(factory).Reset();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, MediaApi.Mount(factory).Opens);
    }

    private static Uri Content(Guid id) => new($"/api/v1/audio-files/{id}/content", UriKind.Relative);

    private static MediaMountReader Reader(string mediaPath) => new(new N8TracksOptions(
        Port: 8080,
        BaseUrl: new Uri("http://localhost:8080/"),
        PathBase: string.Empty,
        TimeZone: TimeZoneInfo.Utc,
        LogLevel: N8TracksLogLevel.Information,
        DataPath: mediaPath + "-data",
        MediaPath: mediaPath,
        BackupPath: mediaPath + "-backup"));

    private static List<RouteEndpoint> Endpoints(N8TracksApiFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    /// <summary>
    /// Every input whose name says it is a path: a route parameter, a handler parameter of a plain
    /// type (a query value or a header), and a field of the request body, however deep.
    /// </summary>
    private static List<string> PathInputs(IEnumerable<RouteEndpoint> endpoints)
    {
        var found = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var describe = $"{(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? string.Join(',', methods) : "*")} {endpoint.RoutePattern.RawText}";
            found.AddRange(endpoint.RoutePattern.Parameters
                .Where(static parameter => NamesAPath().IsMatch(parameter.Name))
                .Select(parameter => $"{describe}: {parameter.Name}"));

            if (endpoint.Metadata.GetMetadata<MethodInfo>() is { } handler)
            {
                found.AddRange(handler.GetParameters()
                    .Where(static parameter => IsPlain(parameter.ParameterType) && NamesAPath().IsMatch(parameter.Name!))
                    .Select(parameter => $"{describe}: {parameter.Name}"));
            }

            if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is { } body)
            {
                found.AddRange(Fields(body, string.Empty, depth: 0).Select(field => $"{describe}: body field {field}"));
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>The path-named properties of a request body type, and of the app's own types inside it.</summary>
    private static IEnumerable<string> Fields(Type type, string prefix, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        type = type.IsArray ? type.GetElementType()! : type;
        if (depth > 4 || type.Assembly.GetName().Name?.StartsWith("n8Tracks", StringComparison.Ordinal) != true)
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prefix + property.Name;
            if (NamesAPath().IsMatch(property.Name))
            {
                yield return name;
            }

            foreach (var nested in Fields(property.PropertyType, name + ".", depth + 1))
            {
                yield return nested;
            }
        }
    }

    /// <summary>A type a query value or a header binds to.</summary>
    private static bool IsPlain(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        type = type.IsArray ? type.GetElementType()! : type;
        return type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Microsoft.Extensions.Primitives.StringValues);
    }

    /// <summary>
    /// A name that says it holds a path: path, file, folder, directory, dir, or mount as a word of it
    /// (the first word in lower case, a later one capitalized), optionally plural or followed by "Name".
    /// </summary>
    [GeneratedRegex(@"(?:^(?:path|file|folder|directory|dir|mount)|(?:Path|File|Folder|Directory|Dir|Mount))(?:s|Names?)?(?:$|[A-Z_])")]
    private static partial Regex NamesAPath();

    [UnsupportedOSPlatform("windows")]
    private static void MakeReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is null)
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root).Reverse())
        {
            if (new DirectoryInfo(directory).LinkTarget is null)
            {
                File.SetUnixFileMode(directory, ReadOnlyDirectory);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeWritable(string root)
    {
        File.SetUnixFileMode(root, ReadOnlyDirectory | UnixFileMode.UserWrite);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (new DirectoryInfo(directory).LinkTarget is null)
            {
                File.SetUnixFileMode(directory, ReadOnlyDirectory | UnixFileMode.UserWrite);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is null)
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private const UnixFileMode ReadOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static bool CanWrite(string directory)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(directory, ".write-probe"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record PathBody(PathLocation Location);

    private sealed record PathLocation(string FilePath);
}

/// <summary>A test that needs Linux (its <c>/proc</c>). Skipped elsewhere.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux (/proc/self/fd).";
        }
    }
}
