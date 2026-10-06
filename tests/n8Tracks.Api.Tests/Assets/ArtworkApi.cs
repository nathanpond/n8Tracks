using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Retention;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Assets;

namespace n8Tracks.Api.Tests.Assets;

/// <summary>
/// Helpers for the artwork store tests: a host with a controllable clock and a stand-in for the
/// attachments the later artwork stories add, uploads through the API, and the store's files on disk.
/// </summary>
internal static class ArtworkApi
{
    public static readonly Uri Artwork = new("/api/v1/artwork", UriKind.Relative);

    /// <summary>
    /// A host on <paramref name="clock"/> (the real clock when null), with <paramref name="attachments"/>
    /// as the one source of attachments, and the retention tests' test artwork type registered.
    /// </summary>
    public static N8TracksApiFactory Host(TimeProvider? clock = null, TestAttachments? attachments = null) =>
        new()
        {
            TestServices = services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }

                services.AddSingleton<IArtworkAttachments>(attachments ?? new TestAttachments());
                services.AddSingleton(RetentionApi.TestArtwork);
            },
        };

    /// <summary>
    /// Uploads <paramref name="content"/> as a multipart file part with the given name and declared type:
    /// with <paramref name="token"/> as a Bearer credential, or else as the signed-in browser does.
    /// </summary>
    public static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        byte[] content,
        string fileName = "cover.png",
        string contentType = "image/png",
        string? token = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Artwork) { Content = form };
        if (token is null)
        {
            request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Uploads and asserts it was stored (201, or 200 for known bytes); the answer's body.</summary>
    public static async Task<JsonElement> StoreAsync(HttpClient client, byte[] content, string? token = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await UploadAsync(client, content, token: token);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    /// <summary>A GET of <paramref name="path"/> (relative to the site), with <paramref name="token"/> as a Bearer credential when given.</summary>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The asset's ID from an upload's answer.</summary>
    public static Guid IdOf(JsonElement asset) => asset.GetProperty("id").GetGuid();

    /// <summary>The URL an upload's answer gives for <paramref name="key"/> (<c>original</c>, <c>96</c>, …).</summary>
    public static string UrlOf(JsonElement asset, string key) => asset.GetProperty("urls").GetProperty(key).GetString()!;

    /// <summary>The SHA-256 of <paramref name="content"/>, as the store names it.</summary>
    public static string Hash(byte[] content) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    /// <summary>The full path of an artwork file, relative to the managed-assets folder.</summary>
    public static string FullPath(N8TracksApiFactory factory, string relative) =>
        Path.Combine(factory.DataPath, "assets", relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every file under the managed-assets folder, relative to it, sorted.</summary>
    public static List<string> StoredFiles(N8TracksApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var root = Path.Combine(factory.DataPath, "assets");
        return Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>How many rows <c>assets</c> holds.</summary>
    public static int AssetRows(N8TracksApiFactory factory) =>
        int.Parse(TestDatabase.Scalar(factory.DataPath, "SELECT count(*) FROM assets;"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Calls the artwork service in a scope of its own.</summary>
    public static async Task<T> WithServiceAsync<T>(N8TracksApiFactory factory, Func<ArtworkService, Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(call);

        var scope = factory.Services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await call(scope.ServiceProvider.GetRequiredService<ArtworkService>());
        }
    }

    public static Task<ArtworkSweepSummary> SweepAsync(N8TracksApiFactory factory) =>
        WithServiceAsync(factory, static service => service.SweepAsync(CancellationToken.None));
}

/// <summary>A stand-in for the attachments (#98, #100): the assets in <see cref="Attached"/> count as attached to a live record.</summary>
internal sealed class TestAttachments : IArtworkAttachments
{
    public ConcurrentDictionary<Guid, bool> Attached { get; } = new();

    public void Attach(Guid assetId) => Attached[assetId] = true;

    public void Detach(Guid assetId) => Attached.TryRemove(assetId, out _);

    public Task<bool> IsAttachedAsync(Guid assetId, CancellationToken cancellationToken) => Task.FromResult(Attached.ContainsKey(assetId));
}
