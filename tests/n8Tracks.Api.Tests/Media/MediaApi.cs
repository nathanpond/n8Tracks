using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Configuration;
using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Media;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// Test helpers for the media scan (#203): a host whose media mount is wrapped in a
/// <see cref="CountingMount"/>, files placed in the host's (temporary) media folder, a scan run to its
/// end through the API, and a listing of the folder with content hashes to prove nothing changed.
/// </summary>
internal static class MediaApi
{
    public static readonly Uri Scans = new("/api/v1/media/scans", UriKind.Relative);
    public static readonly Uri AudioFiles = new("/api/v1/audio-files", UriKind.Relative);

    /// <summary>Where the audio fixtures (1.5 s tones tagged "Fixture Title" / "Fixture Artist", made with ffmpeg) are copied.</summary>
    public static string Fixture(string format) => Path.Combine(AppContext.BaseDirectory, "Media", "Fixtures", $"tone.{format}");

    /// <summary>A host with a counting mount and, optionally, the scan's limits changed.</summary>
    public static N8TracksApiFactory Host(MediaScanOptions? options = null, Action<IServiceCollection>? services = null) =>
        new()
        {
            TestServices = collection =>
            {
                UseCountingMount(collection);
                if (options is not null)
                {
                    collection.RemoveAll<MediaScanOptions>();
                    collection.AddSingleton(options);
                }

                services?.Invoke(collection);
            },
        };

    /// <summary>Wraps the host's real media mount in a <see cref="CountingMount"/>.</summary>
    public static void UseCountingMount(IServiceCollection collection)
    {
        collection.RemoveAll<IMediaMount>();
        collection.AddSingleton(static provider => new CountingMount(new MediaMountReader(provider.GetRequiredService<N8TracksOptions>())));
        collection.AddSingleton<IMediaMount>(static provider => provider.GetRequiredService<CountingMount>());
    }

    public static CountingMount Mount(N8TracksApiFactory factory) => factory.Services.GetRequiredService<CountingMount>();

    /// <summary>Copies the <paramref name="format"/> fixture to <paramref name="relativePath"/> under the media folder.</summary>
    public static string Place(N8TracksApiFactory factory, string relativePath, string format)
    {
        var path = FullPath(factory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Fixture(format), path, overwrite: true);
        return path;
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="relativePath"/> under the media folder.</summary>
    public static string Write(N8TracksApiFactory factory, string relativePath, byte[] bytes)
    {
        var path = FullPath(factory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string FullPath(N8TracksApiFactory factory, string relativePath) =>
        Path.Combine(factory.MediaPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Starts a scan as the signed-in session and waits for it to succeed or fail; the job as the session reads it.</summary>
    public static async Task<JsonElement> ScanAsync(HttpClient client)
    {
        using var response = await SessionApi.SendAsync(client, HttpMethod.Post, Scans);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await SetupApi.JsonAsync(response)).GetProperty("jobId").GetGuid();
        return await WaitAsync(client, id);
    }

    /// <summary>Waits for the job to end.</summary>
    public static Task<JsonElement> WaitAsync(HttpClient client, Guid id) =>
        TestJobs.WaitForAsync(client, id, static job => job.GetProperty("status").GetString() is "succeeded" or "failed");

    /// <summary>The job's result, after asserting it succeeded.</summary>
    public static JsonElement Result(JsonElement job)
    {
        Assert.True(job.GetProperty("status").GetString() == "succeeded", $"The scan did not succeed: {job}");
        return job.GetProperty("result");
    }

    /// <summary>Every audio file the list answers (one page of up to 200), and the raw body.</summary>
    public static async Task<(List<JsonElement> Items, string Body)> ListAsync(HttpClient client, string query = "")
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/audio-files{query}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return ([.. document.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.Clone())], body);
    }

    /// <summary>The listed file at <paramref name="path"/>.</summary>
    public static JsonElement ByPath(IEnumerable<JsonElement> items, string path) =>
        Assert.Single(items, item => item.GetProperty("path").GetString() == path);

    /// <summary>
    /// Every entry under <paramref name="root"/>: directories by path, files by path, size,
    /// last-modified time (to the tick), and a SHA-256 of their content. Sorted ordinally.
    /// </summary>
    public static List<string> Listing(string root)
    {
        var entries = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            entries.Add($"d {Path.GetRelativePath(root, directory)} {Directory.GetLastWriteTimeUtc(directory).Ticks}");
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            entries.Add($"f {Path.GetRelativePath(root, file)} {info.Length} {info.LastWriteTimeUtc.Ticks} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}");
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }
}

/// <summary>
/// The real mount, wrapped: counts every file opened (by relative path), and lets a test hold a
/// listing, slow an open, or make looking at a file throw.
/// </summary>
internal sealed class CountingMount(IMediaMount inner) : IMediaMount
{
    private readonly ConcurrentDictionary<string, int> opened = new(StringComparer.Ordinal);

    /// <summary>When set, every listing waits until it is set free (at most 30 s).</summary>
    public ManualResetEventSlim? HoldListings { get; set; }

    /// <summary>Opening a path in this set first sleeps for <see cref="SlowOpen"/>.</summary>
    public ConcurrentDictionary<string, bool> SlowPaths { get; } = new(StringComparer.Ordinal);

    public TimeSpan SlowOpen { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Looking at a path in this set throws.</summary>
    public ConcurrentDictionary<string, bool> FailingStats { get; } = new(StringComparer.Ordinal);

    /// <summary>Looking at a path in this set answers null, as the real reader does for a file it cannot look at.</summary>
    public ConcurrentDictionary<string, bool> NullStats { get; } = new(StringComparer.Ordinal);

    public int Opens => opened.Values.Sum();

    public int OpensOf(string relativePath) => opened.GetValueOrDefault(relativePath);

    public void Reset() => opened.Clear();

    public bool Probe() => inner.Probe();

    public IReadOnlyList<MediaEntry> List(string relativeDirectory)
    {
        HoldListings?.Wait(TimeSpan.FromSeconds(30));
        return inner.List(relativeDirectory);
    }

    public MediaFileStat? Stat(string relativePath) =>
        FailingStats.ContainsKey(relativePath) ? throw new InvalidOperationException("Stat failed on purpose.")
        : NullStats.ContainsKey(relativePath) ? null
        : inner.Stat(relativePath);

    public Stream OpenRead(string relativePath)
    {
        opened.AddOrUpdate(relativePath, 1, static (_, count) => count + 1);
        if (SlowPaths.ContainsKey(relativePath))
        {
            Thread.Sleep(SlowOpen);
        }

        return inner.OpenRead(relativePath);
    }

    /// <summary>The files opened with their stat (#217) and not yet closed.</summary>
    public int OpenHandles => Volatile.Read(ref openHandles);

    public OpenedMediaFile OpenWithStat(string relativePath)
    {
        opened.AddOrUpdate(relativePath, 1, static (_, count) => count + 1);
        var file = inner.OpenWithStat(relativePath);
        Interlocked.Increment(ref openHandles);
        return new OpenedMediaFile(new ClosingStream(file, () => Interlocked.Decrement(ref openHandles)), file.Stat);
    }

    private int openHandles;

    /// <summary>The opened file's stream, telling when it is closed (once).</summary>
    private sealed class ClosingStream(OpenedMediaFile file, Action closed) : Stream
    {
        private int disposed;

        public override bool CanRead => file.Content.CanRead;

        public override bool CanSeek => file.Content.CanSeek;

        public override bool CanWrite => false;

        public override long Length => file.Content.Length;

        public override long Position
        {
            get => file.Content.Position;
            set => file.Content.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => file.Content.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => file.Content.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            file.Content.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            file.Content.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => file.Content.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                file.Dispose();
                closed();
            }

            base.Dispose(disposing);
        }
    }
}
