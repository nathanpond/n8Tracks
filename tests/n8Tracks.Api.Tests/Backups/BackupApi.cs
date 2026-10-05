using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Configuration;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Infrastructure.Backups;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>Calls the backup endpoints as the Backups page does.</summary>
internal static class BackupApi
{
    public static readonly Uri Backups = new("/api/v1/backups", UriKind.Relative);

    /// <summary>A host with the given test hooks and variables (the backup path, say).</summary>
    public static N8TracksApiFactory Host(BackupTestHooks? hooks = null, IReadOnlyDictionary<string, string>? variables = null) =>
        new(variables ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            TestServices = services =>
            {
                if (hooks is not null)
                {
                    services.RemoveAll<BackupTestHooks>();
                    services.AddSingleton(hooks);
                }
            },
        };

    /// <summary>A host whose backup path is <paramref name="backupPath"/>.</summary>
    public static N8TracksApiFactory HostWithBackupPath(string backupPath, BackupTestHooks? hooks = null) =>
        Host(hooks, new Dictionary<string, string>(StringComparer.Ordinal) { [EnvironmentOptionsLoader.BackupPath] = backupPath });

    public static Uri Archive(string location, string name) => new($"/api/v1/backups/{location}/{name}", UriKind.Relative);

    /// <summary>The list, asserting 200.</summary>
    public static async Task<JsonElement> ListAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Backups);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>POSTs "Back up now"; the caller reads the answer.</summary>
    public static Task<HttpResponseMessage> StartAsync(HttpClient client) => SessionApi.SendAsync(client, HttpMethod.Post, Backups);

    /// <summary>Starts a backup, asserting 202, and returns its job ID.</summary>
    public static async Task<Guid> StartJobAsync(HttpClient client)
    {
        using var response = await StartAsync(client);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await SetupApi.JsonAsync(response);
        var id = body.GetProperty("jobId").GetGuid();
        Assert.Equal($"/api/v1/jobs/{id}", response.Headers.Location?.OriginalString);
        return id;
    }

    /// <summary>Starts a backup and waits for it to succeed; returns the job's result.</summary>
    public static async Task<JsonElement> BackUpAsync(HttpClient client)
    {
        var id = await StartJobAsync(client);
        var job = await TestJobs.WaitForAsync(client, id, static job => job.GetProperty("status").GetString() is "succeeded" or "failed");
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.ToString());
        return job.GetProperty("result");
    }

    /// <summary>Downloads an archive, asserting 200, and returns its bytes.</summary>
    public static async Task<byte[]> DownloadAsync(HttpClient client, string location, string name)
    {
        using var response = await client.GetAsync(Archive(location, name));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(name, response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>The archive's entries by name, read whole.</summary>
    public static Dictionary<string, byte[]> Entries(byte[] archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(
            static entry => entry.FullName,
            static entry =>
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            },
            StringComparer.Ordinal);
    }

    /// <summary>The names directly in <paramref name="folder"/>, or none when it does not exist.</summary>
    public static List<string> Names(string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFileSystemEntries(folder).Select(static path => Path.GetFileName(path)).Order(StringComparer.Ordinal)] : [];
}
