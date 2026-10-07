using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// Reading the audio file catalog (#203): a page of files, or one file. Never an absolute path. Each
/// file carries the status it reports (#207): its stored status while the media folder is available,
/// and unavailable while it is not; the status filter and the total use the reported status.
/// </summary>
public sealed class AudioFileService(IAudioFileStore files, MediaAvailability availability)
{
    /// <summary>The most a page holds; a larger <c>limit</c> is refused. The public paging convention arrives in M7.</summary>
    public const int MaximumLimit = 200;

    /// <summary>One page of files, in path order.</summary>
    public async Task<ReportedAudioFilePage> ListAsync(AudioFileListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Limit, MaximumLimit);

        var mount = (await availability.CurrentAsync(cancellationToken).ConfigureAwait(false)).State;

        // While the folder is unavailable every file reports unavailable, and none available or
        // missing; while it is available, none reports unavailable.
        AudioFileStatus? stored;
        switch (request.Status)
        {
            case null:
                stored = null;
                break;
            case AudioFileReportedStatus.Unavailable when mount == MediaMountState.Unavailable:
                stored = null;
                break;
            case AudioFileReportedStatus.Available when mount == MediaMountState.Available:
                stored = AudioFileStatus.Available;
                break;
            case AudioFileReportedStatus.Missing when mount == MediaMountState.Available:
                stored = AudioFileStatus.Missing;
                break;
            default:
                return new ReportedAudioFilePage([], 0);
        }

        var page = await files.ListAsync(
            new AudioFileQuery(stored, request.Association, request.MetadataReadable, request.Offset, request.Limit),
            cancellationToken).ConfigureAwait(false);
        return new ReportedAudioFilePage([.. page.Items.Select(file => Report(file, mount))], page.Total);
    }

    /// <summary>The file with <paramref name="id"/>, or null.</summary>
    public async Task<ReportedAudioFile?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await files.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } file)
        {
            return null;
        }

        return Report(file, (await availability.CurrentAsync(cancellationToken).ConfigureAwait(false)).State);
    }

    private static ReportedAudioFile Report(AudioFile file, MediaMountState mount) => new(file, MediaAvailability.Reported(file.Status, mount));
}
