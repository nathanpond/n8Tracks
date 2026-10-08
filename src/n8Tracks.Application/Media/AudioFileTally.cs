using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// How many local audio files are associated with one Generation (#211), as they report now: in all
/// (<see cref="Count"/>, Missing and Unavailable ones included), how many report Missing, how many
/// report Unavailable (every one while the media folder cannot be read), and their formats once each,
/// WAV, M4A, MP3, FLAC, OGG, Opus, AAC.
/// </summary>
public sealed record AudioFileTally(int Count, int Missing, int Unavailable, IReadOnlyList<string> Formats)
{
    /// <summary>No files.</summary>
    public static AudioFileTally None { get; } = new(0, 0, 0, []);

    /// <summary>The tally of <paramref name="files"/> (each one's stored status and format) while the media folder is <paramref name="mount"/>.</summary>
    public static AudioFileTally Of(IReadOnlyCollection<(AudioFileStatus Status, string Format)> files, MediaMountState mount)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return None;
        }

        var reported = files.Select(file => MediaAvailability.Reported(file.Status, mount)).ToList();
        return new(
            files.Count,
            reported.Count(static status => status == AudioFileReportedStatus.Missing),
            reported.Count(static status => status == AudioFileReportedStatus.Unavailable),
            AudioFormats.InRankOrder(files.Select(static file => file.Format)));
    }
}
