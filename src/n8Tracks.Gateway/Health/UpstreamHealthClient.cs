using System.Net.Http.Headers;
using System.Text.Json;

namespace n8Tracks.Gateway.Health;

/// <summary>
/// The gateway's HTTP client for n8Tracks: its base address is <c>N8TRACKS_API_URL</c>. In this story
/// it only asks for the health document.
/// </summary>
internal sealed class UpstreamHealthClient(HttpClient http)
{
    /// <summary>The time allowed for the whole response: headers and body.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>The most that is read of a response body; a longer body has no readable version.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>Relative, so a sub-path in the base address is kept.</summary>
    private static readonly Uri HealthPath = new("health", UriKind.Relative);

    /// <summary>
    /// Asks the upstream for its health afresh. Any HTTP response counts as reachable, whatever its
    /// status. Throws only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<UpstreamProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HealthPath);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);

            var body = await ReadBodyAsync(response, budget.Token).ConfigureAwait(false);

            return UpstreamProbe.Answered(body is null ? null : ReadVersion(body));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpstreamProbe.Failed("timed out");
        }
        catch (HttpRequestException exception)
        {
            // The category only: the exception's message can name the host.
            return UpstreamProbe.Failed(exception.HttpRequestError.ToString());
        }
        catch (IOException)
        {
            return UpstreamProbe.Failed("connection lost");
        }
    }

    /// <summary>The body, or null when it is longer than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            // One byte more than the limit shows that the body is too long without reading the rest.
            var buffer = new byte[MaxBodyBytes + 1];
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            return length > MaxBodyBytes ? null : buffer[..length];
        }
    }

    private static string? ReadVersion(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
