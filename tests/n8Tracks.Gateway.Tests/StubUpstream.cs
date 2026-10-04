using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace n8Tracks.Gateway.Tests;

/// <summary>A fake upstream: answers each request with whatever <see cref="Respond"/> currently returns.</summary>
internal sealed class StubUpstream : HttpMessageHandler
{
    /// <summary>The requests the gateway sent, in order.</summary>
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Changeable between requests. The token is cancelled when the gateway gives up.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Health("0.1.0"));

    /// <summary>What the app's health endpoint returns, cut down to what the gateway reads.</summary>
    public static HttpResponseMessage Health(string version, HttpStatusCode status = HttpStatusCode.OK) =>
        Json("{\"status\":\"healthy\",\"version\":\"" + version + "\",\"timeZone\":\"UTC\",\"components\":{}}", status);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public void RespondWith(Func<HttpResponseMessage> response) => Respond = (_, _) => Task.FromResult(response());

    public void Fail(Exception exception) => Respond = (_, _) => Task.FromException<HttpResponseMessage>(exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Respond(request, cancellationToken);
    }
}
