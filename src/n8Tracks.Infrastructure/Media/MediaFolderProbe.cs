using n8Tracks.Application.Media;
using n8Tracks.Infrastructure.Health;

namespace n8Tracks.Infrastructure.Media;

/// <summary>
/// The one probe of the media folder (#207): <see cref="IMediaMount.Probe"/> run as a
/// <see cref="DeadlineCheck"/> with the health check's 2-second deadline. It only probes: it never
/// lists, looks at, or opens anything under the mount.
/// </summary>
internal sealed class MediaFolderProbe : IMediaFolderProbe
{
    private readonly DeadlineCheck check;

    public MediaFolderProbe(IMediaMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);

        check = new DeadlineCheck(_ => mount.Probe(), HealthService.CheckTimeout);
    }

    public TimeSpan Deadline => HealthService.CheckTimeout;

    public async Task<MediaProbeOutcome> ProbeAsync(CancellationToken cancellationToken)
    {
        var outcome = await check.RunAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            CheckResult.Passed => new MediaProbeOutcome(MediaProbeResult.Readable),
            CheckResult.TimedOut => new MediaProbeOutcome(MediaProbeResult.TimedOut, outcome.Exception),
            _ => new MediaProbeOutcome(MediaProbeResult.Unreadable, outcome.Exception),
        };
    }
}
