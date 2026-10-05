using System.Globalization;
using System.Net;
using System.Text.Json;
using n8Tracks.Api.Auth;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Api.Tests.Persistence;
using n8Tracks.Api.Tests.Setup;
using n8Tracks.Application.Credentials;

namespace n8Tracks.Api.Tests.Jobs;

/// <summary>
/// <c>GET /api/v1/jobs</c> and <c>GET /api/v1/jobs/{id}</c>: who may read them, and what they show.
/// </summary>
public sealed class JobEndpointTests
{
    private static readonly Uri Jobs = new("/api/v1/jobs", UriKind.Relative);

    private static readonly string[] SessionFields =
        ["id", "type", "status", "progress", "message", "createdUtc", "startedUtc", "finishedUtc", "error", "result"];

    [Fact]
    public async Task ASessionSeesEveryFieldAndTheResultButNeverThePayload()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);
        var id = await TestJobs.EnqueueAsync(factory, new { name = "A", secretPlan = "payload-sentinel", result = new { files = 2 } });
        await TestJobs.WaitForStatusAsync(client, id, "succeeded");

        using var one = await client.GetAsync(new Uri($"/api/v1/jobs/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal("no-store", one.Headers.CacheControl?.ToString());
        var body = await one.Content.ReadAsStringAsync();
        var job = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(SessionFields, job.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(id, job.GetProperty("id").GetGuid());
        Assert.Equal(2, job.GetProperty("result").GetProperty("files").GetInt32());
        Assert.DoesNotContain("payload-sentinel", body, StringComparison.Ordinal);
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z$", job.GetProperty("createdUtc").GetString());

        using var list = await client.GetAsync(Jobs);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        var listed = Assert.Single(JsonSerializer.Deserialize<JsonElement>(listBody).EnumerateArray());
        Assert.Equal(SessionFields, listed.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(2, listed.GetProperty("result").GetProperty("files").GetInt32());
        Assert.DoesNotContain("payload-sentinel", listBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenWithCatalogReadSeesTheJobsWithoutAResult()
    {
        using var factory = TestJobs.Host();
        using var session = await TestJobs.ClientAsync(factory);
        var id = await TestJobs.EnqueueAsync(factory, new { name = "A", result = new { files = 2 } });
        await TestJobs.WaitForStatusAsync(session, id, "succeeded");

        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        using var client = factory.CreateClient();

        using var one = await CredentialApi.SendAsync(client, HttpMethod.Get, new Uri($"/api/v1/jobs/{id}", UriKind.Relative), token);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var job = await SetupApi.JsonAsync(one);
        Assert.Equal(SessionFields[..^1], job.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        Assert.Equal(100, job.GetProperty("progress").GetInt32());

        using var list = await CredentialApi.SendAsync(client, HttpMethod.Get, Jobs, token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = Assert.Single((await SetupApi.JsonAsync(list)).EnumerateArray());
        Assert.False(listed.TryGetProperty("result", out _));
        Assert.Equal(id, listed.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ATokenWithoutCatalogReadIsRefusedBothEndpoints()
    {
        using var factory = TestJobs.Host();
        using var setUp = factory.CreateClient();
        await SetupApi.CompleteAsync(setUp);
        var token = await CredentialApi.CreateTokenAsync(factory, [.. CredentialScopes.All.Where(static scope => scope != CredentialScopes.CatalogRead)]);
        using var client = factory.CreateClient();

        foreach (var uri in new[] { Jobs, new Uri($"/api/v1/jobs/{Guid.CreateVersion7()}", UriKind.Relative) })
        {
            using var response = await CredentialApi.SendAsync(client, HttpMethod.Get, uri, token);
            var problem = await SetupApi.ProblemAsync(response, HttpStatusCode.Forbidden, ScopeMiddleware.InsufficientScopeCode);
            Assert.Equal(CredentialScopes.CatalogRead, problem.GetProperty("requiredScope").GetString());
        }
    }

    [Fact]
    public async Task NoSessionAndNoTokenIsUnauthenticated()
    {
        using var factory = TestJobs.Host();
        using var client = factory.CreateClient();
        await SetupApi.CompleteAsync(client);

        using var list = await client.GetAsync(Jobs);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        using var one = await client.GetAsync(new Uri($"/api/v1/jobs/{Guid.CreateVersion7()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, one.StatusCode);
    }

    [Fact]
    public async Task AnUnknownJobIsNotFound()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);
        var token = await CredentialApi.CreateTokenAsync(factory, CredentialScopes.CatalogRead);
        var uri = new Uri($"/api/v1/jobs/{Guid.CreateVersion7()}", UriKind.Relative);

        using var bySession = await client.GetAsync(uri);
        await SetupApi.ProblemAsync(bySession, HttpStatusCode.NotFound, "not_found");
        using var tokenClient = factory.CreateClient();
        using var byToken = await CredentialApi.SendAsync(tokenClient, HttpMethod.Get, uri, token);
        await SetupApi.ProblemAsync(byToken, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task TheListHoldsTheFiftyMostRecentlyEnqueuedNewestFirst()
    {
        using var factory = TestJobs.Host();
        using var client = await TestJobs.ClientAsync(factory);

        // 55 finished jobs, enqueued in sequence order, all at the same millisecond.
        var created = DateTimeOffset.UtcNow.AddMinutes(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var ids = Enumerable.Range(1, 55).Select(static _ => Guid.CreateVersion7()).ToList();
        var values = string.Join(
            ",\n",
            ids.Select((id, index) => $"('{id.ToString().ToUpperInvariant()}', {index + 1}, '{TestJobs.Scripted}', 'succeeded', 100, '{created}', '{created}', '{created}')"));
        TestDatabase.Execute(
            factory.DataPath,
            $"INSERT INTO jobs (id, sequence, type, status, progress, created_utc, started_utc, finished_utc) VALUES {values};");

        using var list = await client.GetAsync(Jobs);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = (await SetupApi.JsonAsync(list)).EnumerateArray().Select(static job => job.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(Enumerable.Reverse(ids).Take(50), listed);
    }
}
