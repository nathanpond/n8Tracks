using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using n8Tracks.Application.Configuration;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Frontend;

/// <summary>The directory the built frontend is served from: the web root (<c>wwwroot</c>), which may be empty.</summary>
internal sealed record FrontendFiles(IFileProvider Provider);

/// <summary>
/// Serves the built frontend under the path base: its static files, and its <c>index.html</c> (the
/// shell) for the app's own URL and for any deep link, so client-side routes survive a reload. One
/// build works at the root of a hostname and under any sub-path, because the shell is given a
/// <c>&lt;base href&gt;</c> naming the configured path and the build refers to its files relatively.
/// </summary>
internal static partial class FrontendHosting
{
    /// <summary>What a built <c>index.html</c> carries inside <c>&lt;head&gt;</c>, replaced with the base tag.</summary>
    public const string BasePlaceholder = "<!--n8tracks-base-->";

    public const string ShellFile = "index.html";

    public const string ShellCacheControl = "no-cache";

    /// <summary>Files under <c>assets/</c> have a content hash in their name, so a given URL never changes.</summary>
    public const string AssetCacheControl = "public, max-age=31536000, immutable";

    public const string NotBuiltMessage =
        "The n8Tracks frontend is not built: the web root has no index.html. The API and the health endpoint still work.";

    private const string AssetsSegment = "assets";

    /// <summary>First path segments that belong to the backend (or to hashed files) and never get the shell.</summary>
    private static readonly string[] ReservedSegments = ["api", "health", "openapi", AssetsSegment];

    private static readonly PathString AssetsPath = new("/" + AssetsSegment);

    private static readonly PathString ShellFilePath = new("/" + ShellFile);

    public static IServiceCollection AddFrontend(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton(static provider =>
            new FrontendFiles(provider.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider));
    }

    /// <summary>
    /// Serves the shell for every GET and HEAD that no endpoint answers and that does not name a file,
    /// and the files of the web root. Call it after the endpoints are mapped and inside the path
    /// base: the API and health endpoints always win, because the shell only answers a request that
    /// routing matched to nothing. The shell is read and given its base tag once, here, and served
    /// from memory.
    /// </summary>
    public static IApplicationBuilder UseFrontend(this IApplicationBuilder app, N8TracksOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        var files = app.ApplicationServices.GetRequiredService<FrontendFiles>().Provider;
        var shell = LoadShell(files, options.PathBase);

        // The shell goes first so that "index.html" is never served raw, without its base tag. It takes
        // nothing from the files: a shell request has no file extension, and those are not served.
        app.Use((context, next) =>
            context.GetEndpoint() is null && IsShellRequest(context.Request.Method, context.Request.Path)
                ? ServeShellAsync(context, shell)
                : next(context));

        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = static file =>
                file.Context.Response.Headers[HeaderNames.CacheControl] =
                    file.Context.Request.Path.StartsWithSegments(AssetsPath, StringComparison.OrdinalIgnoreCase)
                        ? AssetCacheControl
                        : ShellCacheControl,
        });
    }

    /// <summary>
    /// True for the requests the shell answers: a GET or HEAD whose first segment is not reserved and
    /// whose last segment has no file extension (<c>index.html</c> itself excepted), or is a Version
    /// number after a <c>v</c> segment (<c>/songs/n8-1/v/1.1</c>) or the reference of a <c>/go/</c> link
    /// (<c>/go/n8-1-v1.1</c>), whose dots are not an extension.
    /// </summary>
    internal static bool IsShellRequest(string method, PathString path)
    {
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value == "/")
        {
            return true;
        }

        if (path.Equals(ShellFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        // Routing matches in any letter case, so the reserved names are refused in any letter case too.
        if (ReservedSegments.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length >= 2 && segments[^2] == "v" && VersionNumber.TryParse(segments[^1], out _))
        {
            return true;
        }

        // A /go/ link's reference may be a Version shortcode (/go/n8-1-v1.1), whose dots are not an extension.
        if (segments.Length == 2 && string.Equals(segments[0], "go", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !segments[^1].Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Puts <c>&lt;base href="<paramref name="pathBase"/>/"&gt;</c> in place of the placeholder, or
    /// directly after <c>&lt;head&gt;</c> when there is no placeholder. A document with neither is
    /// returned unchanged.
    /// </summary>
    internal static string InjectBase(string html, string pathBase)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pathBase);

        var tag = $"<base href=\"{HtmlEncoder.Default.Encode(pathBase + "/")}\">";

        var placeholder = html.IndexOf(BasePlaceholder, StringComparison.Ordinal);
        if (placeholder >= 0)
        {
            return string.Concat(html.AsSpan(0, placeholder), tag, html.AsSpan(placeholder + BasePlaceholder.Length));
        }

        var head = HeadTag().Match(html);
        return head.Success ? html.Insert(head.Index + head.Length, tag) : html;
    }

    private static byte[]? LoadShell(IFileProvider files, string pathBase)
    {
        var file = files.GetFileInfo(ShellFile);
        if (!file.Exists || file.IsDirectory)
        {
            return null;
        }

        using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);

        return Encoding.UTF8.GetBytes(InjectBase(reader.ReadToEnd(), pathBase));
    }

    private static Task ServeShellAsync(HttpContext context, byte[]? shell)
    {
        var request = context.Request;
        var response = context.Response;

        // "<base path>" with no slash: relative URLs in the shell would resolve against the parent path.
        if (!request.Path.HasValue && request.PathBase.HasValue)
        {
            response.StatusCode = StatusCodes.Status308PermanentRedirect;
            response.Headers[HeaderNames.Location] = request.PathBase.Add("/").ToUriComponent() + request.QueryString.ToUriComponent();
            return Task.CompletedTask;
        }

        if (shell is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            response.Headers[HeaderNames.CacheControl] = "no-store";
            return WriteAsync(context, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(NotBuiltMessage));
        }

        response.Headers[HeaderNames.CacheControl] = ShellCacheControl;
        return WriteAsync(context, "text/html; charset=utf-8", shell);
    }

    private static Task WriteAsync(HttpContext context, string contentType, byte[] body)
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = body.Length;

        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadTag();
}
