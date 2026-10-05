using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace n8Tracks.Api.Tests.Setup;

/// <summary>Calls the setup endpoints the way a client does. Later stories use <see cref="CompleteAsync"/> to get past setup.</summary>
internal static class SetupApi
{
    public const string TestUsername = "owner";
    public const string TestPassword = "correct horse battery";

    public static readonly Uri Status = new("/api/v1/setup/status", UriKind.Relative);
    public static readonly Uri Submit = new("/api/v1/setup", UriKind.Relative);

    public static Task<HttpResponseMessage> SubmitAsync(HttpClient client, string? username, string? password, string? confirmation) =>
        client.PostAsJsonAsync(Submit, new { username, password, passwordConfirmation = confirmation });

    /// <summary>Completes setup with the fixed test credentials and fails the test unless it was created.</summary>
    public static async Task CompleteAsync(HttpClient client)
    {
        using var response = await SubmitAsync(client, TestUsername, TestPassword, TestPassword);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    /// <summary>Asserts a Problem Details response with the status and code, and returns its body.</summary>
    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("requestId").GetString()));

        return problem;
    }
}
