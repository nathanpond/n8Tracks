using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Jobs;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Api.Tests.Songs;
using n8Tracks.Application.Backups;
using n8Tracks.Infrastructure.Backups;
using n8Tracks.Infrastructure.Logging;
using n8Tracks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace n8Tracks.Api.Tests.Backups;

/// <summary>Calls the restore endpoints as the Backups page does, and builds archives to feed them.</summary>
internal static class RestoreApi
{
    public static readonly Uri Restores = new("/api/v1/restores", UriKind.Relative);
    public static readonly Uri Validate = new("/api/v1/restores/validate", UriKind.Relative);
    public static readonly Uri Uploads = new("/api/v1/restores/uploads", UriKind.Relative);
    public static readonly Uri Maintenance = new("/api/v1/maintenance", UriKind.Relative);

    /// <summary>
    /// A host with the scripted test job, backup hooks, restore options, and free space as given, and
    /// requests in flight given no grace at all (tests have none in flight when a restore begins).
    /// </summary>
    public static N8TracksApiFactory Host(
        BackupTestHooks? hooks = null,
        RestoreOptions? options = null,
        IDiskSpace? disk = null,
        Action<IServiceCollection>? more = null,
        string? dataPath = null,
        RestoreTestHooks? restoreHooks = null) =>
        new(dataPath is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["N8TRACKS_DATA_PATH"] = dataPath })
        {
            TestServices = services =>
            {
                TestJobs.Register(services);
                if (hooks is not null)
                {
                    services.RemoveAll<BackupTestHooks>();
                    services.AddSingleton(hooks);
                }

                if (restoreHooks is not null)
                {
                    services.RemoveAll<RestoreTestHooks>();
                    services.AddSingleton(restoreHooks);
                }

                services.RemoveAll<RestoreOptions>();
                services.AddSingleton(options ?? new RestoreOptions { DrainGrace = TimeSpan.FromSeconds(2) });
                if (disk is not null)
                {
                    services.RemoveAll<IDiskSpace>();
                    services.AddSingleton(disk);
                }

                more?.Invoke(services);
            },
        };

    /// <summary>POSTs the listed backup to validate; the caller reads the answer.</summary>
    public static Task<HttpResponseMessage> ValidateAsync(HttpClient client, string location, string name) =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, Validate, JsonSerializer.Serialize(new { location, name }));

    /// <summary>Uploads <paramref name="bytes"/> as a multipart file, with the anti-forgery header.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string fileName = "backup.zip")
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Uploads) { Content = content };
        request.Headers.Add(SessionApi.AntiforgeryHeader, "1");
        return await client.SendAsync(request);
    }

    /// <summary>Asserts 200 and returns the validation.</summary>
    public static async Task<JsonElement> ValidAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await SetupApi.JsonAsync(response);
        }
    }

    /// <summary>POSTs the confirmation; the caller reads the answer.</summary>
    public static Task<HttpResponseMessage> StartAsync(HttpClient client, string validationId, string confirmation = "RESTORE") =>
        SongApi.SendJsonAsync(client, HttpMethod.Post, Restores, JsonSerializer.Serialize(new { validationId, confirmation }));

    /// <summary>The maintenance status, as anyone reads it.</summary>
    public static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Maintenance);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await SetupApi.JsonAsync(response);
    }

    /// <summary>Waits until the maintenance status matches and returns it.</summary>
    public static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Func<JsonElement, bool> matches)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await StatusAsync(client);
            if (matches(status))
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The maintenance status never matched; last seen: {status}");
            await Task.Delay(25);
        }
    }

    /// <summary>Validates the listed backup, confirms, and waits for maintenance to end; returns the status.</summary>
    public static async Task<JsonElement> RestoreAsync(HttpClient client, string location, string name)
    {
        var validationId = (await ValidAsync(await ValidateAsync(client, location, name))).GetProperty("validationId").GetString()!;
        using (var started = await StartAsync(client, validationId))
        {
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        }

        return await WaitForEndAsync(client);
    }

    /// <summary>Uploads the archive, confirms, and waits until maintenance matches <paramref name="until"/> (by default, has ended).</summary>
    public static async Task<JsonElement> RestoreUploadAsync(HttpClient client, byte[] archive, Func<JsonElement, bool>? until = null)
    {
        var validationId = (await ValidAsync(await UploadAsync(client, archive))).GetProperty("validationId").GetString()!;
        using (var started = await StartAsync(client, validationId))
        {
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        }

        return await WaitForStatusAsync(client, until ?? (static status => !status.GetProperty("active").GetBoolean()));
    }

    /// <summary>The ID of the initial migration: an archive at it is the oldest schema there is.</summary>
    public const string InitialMigration = "20261004042959_InitialCreate";

    /// <summary>
    /// <paramref name="archive"/> with its database replaced by a new one migrated only as far as
    /// <paramref name="migration"/>, and its manifest saying so: what an older version would have made.
    /// </summary>
    public static byte[] AtMigration(byte[] archive, string migration)
    {
        var file = Path.Combine(Path.GetTempPath(), $"n8tracks-test-{Guid.NewGuid():N}.db");
        try
        {
            SqliteDatabase.CreateIfMissing(file);
            var builder = new DbContextOptionsBuilder<N8TracksDbContext>();
            builder.UseN8TracksSqlite(file);
            var options = builder.Options;
            using (var context = new N8TracksDbContext(options))
            {
                context.GetService<IMigrator>().Migrate(migration);
            }

            SqliteConnection.ClearAllPools();
            var database = ChangeDatabase(File.ReadAllBytes(file), "SELECT 1;");
            return Rebuild(archive, entries => entries["n8tracks.db"] = database, manifest => manifest["lastMigration"] = migration);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Rows of <paramref name="sql"/>'s first column, read from the live database without changing it.</summary>
    public static List<string> Column(string dataPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataPath, "n8tracks.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return values;
    }

    /// <summary>Every application log line a host writes, kept as text: add with <c>more: log.Register</c>.</summary>
    public sealed class LogCapture : IDisposable
    {
        private readonly StringWriter buffer = new();
        private readonly TextWriter captured;

        public LogCapture()
        {
            captured = TextWriter.Synchronized(buffer);
        }

        public string Text
        {
            get
            {
                lock (captured)
                {
                    return buffer.ToString();
                }
            }
        }

        public void Register(IServiceCollection services) => services.AddSingleton<Serilog.Core.ILogEventSink>(new JsonLinesSink(captured));

        public void Dispose()
        {
            captured.Dispose();
            buffer.Dispose();
        }
    }

    /// <summary>Waits until maintenance has ended and returns the status.</summary>
    public static async Task<JsonElement> WaitForEndAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await StatusAsync(client);
            if (!status.GetProperty("active").GetBoolean())
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Maintenance never ended; last seen: {status}");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Every row of every table but <c>sessions</c> (which each signed-in request touches) and
    /// <c>notifications</c> (#231: what the work under test recorded about itself, written as it ends),
    /// hashed: the same before and after means nothing in the catalog, settings, credentials, or jobs changed.
    /// </summary>
    public static string Fingerprint(string dataPath, params string[] alsoLeaveOut)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataPath, "n8tracks.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('sessions', 'notifications') ORDER BY name;";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                if (!alsoLeaveOut.Contains(reader.GetString(0), StringComparer.Ordinal))
                {
                    tables.Add(reader.GetString(0));
                }
            }
        }

        var text = new StringBuilder();
        foreach (var table in tables)
        {
            text.Append("# ").AppendLine(table);
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    text.Append(reader.IsDBNull(column) ? "<null>" : Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                }

                text.AppendLine();
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// A copy of <paramref name="archive"/> with its entries changed by <paramref name="change"/>
    /// and its manifest by <paramref name="manifest"/>. Unless <paramref name="keepChecksums"/>, the
    /// manifest's sizes and checksums are recomputed after the change, so only what the test means
    /// to break is broken.
    /// </summary>
    public static byte[] Rebuild(
        byte[] archive,
        Action<Dictionary<string, byte[]>>? change = null,
        Action<JsonObject>? manifest = null,
        bool keepChecksums = false)
    {
        var entries = BackupApi.Entries(archive);
        change?.Invoke(entries);

        var document = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        if (!keepChecksums)
        {
            foreach (var file in document["files"]!.AsArray())
            {
                var path = file!["path"]!.GetValue<string>();
                if (entries.TryGetValue(path, out var content))
                {
                    file["size"] = content.Length;
                    file["sha256"] = Convert.ToHexStringLower(SHA256.HashData(content));
                }
            }
        }

        manifest?.Invoke(document);
        entries["manifest.json"] = Encoding.UTF8.GetBytes(document.ToJsonString());

        return Zip(entries);
    }

    /// <summary>A ZIP of <paramref name="entries"/>.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (!name.EndsWith('/'))
                {
                    using var stream = entry.Open();
                    stream.Write(content);
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>The database bytes of an archive with <paramref name="sql"/> run on it.</summary>
    public static byte[] ChangeDatabase(byte[] database, string sql)
    {
        var file = Path.Combine(Path.GetTempPath(), $"n8tracks-test-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(file, database);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode = DELETE; " + sql;
                command.ExecuteNonQuery();
            }

            return File.ReadAllBytes(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Free space as a test says, whatever the disk has.</summary>
    public sealed class FixedDiskSpace(long bytes) : IDiskSpace
    {
        public long AvailableForData() => bytes;
    }
}
