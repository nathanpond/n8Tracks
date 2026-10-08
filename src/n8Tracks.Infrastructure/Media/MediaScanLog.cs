using Microsoft.Extensions.Logging;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>
/// The scan's one Warning about the links it did not follow (#205): the count and at most ten paths,
/// relative to the media folder. Never an absolute path, and never where a link leads.
/// </summary>
internal sealed partial class MediaScanLog(ILogger<MediaScanLog> logger) : IMediaScanLog
{
    public void SkippedLinks(int count, IReadOnlyList<string> relativePaths) =>
        LogSkippedLinks(logger, count, string.Join(", ", relativePaths.Take(IMediaScanLog.MaximumPaths)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "The media scan did not follow {SkippedLinkCount} links that lead outside the media folder, to nothing, or round a cycle: {SkippedLinkPaths}")]
    private static partial void LogSkippedLinks(ILogger logger, int skippedLinkCount, string skippedLinkPaths);
}
