using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Frontend;

/// <summary>
/// The browser policies every response carries (#221).
/// <list type="bullet">
/// <item>A Content Security Policy with one directive, <c>media-src</c>: audio and video load only from
/// the app itself (<c>'self'</c>, its own audio files, #217) and from exactly the listed Suno audio
/// hosts as configured (<see cref="SunoAudioHosts"/>, the setting <c>N8TRACKS_SUNO_AUDIO_HOSTS</c>), each over HTTPS, so the player can stream a Generation from
/// Suno and from nowhere else. Every other directive is left to the audit milestone's full policy.</item>
/// <item>A Referrer Policy of <c>strict-origin-when-cross-origin</c> (the browsers' own default, made
/// explicit): a request to another site, such as Suno's audio host, names at most n8Tracks' origin,
/// never a path. The audio element asks for no referrer at all where the browser supports it.</item>
/// </list>
/// </summary>
internal sealed class BrowserPolicyMiddleware(RequestDelegate next, SunoAudioHosts hosts)
{
    public const string ContentSecurityPolicyHeader = "Content-Security-Policy";
    public const string ReferrerPolicyHeader = "Referrer-Policy";
    public const string ReferrerPolicy = "strict-origin-when-cross-origin";

    /// <summary>The Content Security Policy this middleware sends.</summary>
    public string ContentSecurityPolicy { get; } = ContentSecurityPolicyFor(hosts);

    /// <summary>The Content Security Policy for <paramref name="hosts"/>: <c>media-src 'self'</c> and each listed Suno audio host as an HTTPS origin.</summary>
    public static string ContentSecurityPolicyFor(SunoAudioHosts hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        return "media-src 'self' " + string.Join(' ', hosts.Hosts.Select(static host => "https://" + host));
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Set when the response starts, so it survives a handler that resets the headers.
        context.Response.OnStarting(
            static state =>
            {
                var (response, policy) = ((HttpResponse, string))state;
                response.Headers[ContentSecurityPolicyHeader] = policy;
                response.Headers[ReferrerPolicyHeader] = ReferrerPolicy;
                return Task.CompletedTask;
            },
            (context.Response, ContentSecurityPolicy));
        return next(context);
    }
}
