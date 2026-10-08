using ATL;
using n8Tracks.Application.Media;

namespace n8Tracks.Infrastructure.Media;

/// <summary>
/// Reads an audio header with ATL (<c>z440.atl.core</c>), a managed tag library, from a stream the
/// caller opened read-only: ATL is never given a path, and nothing here saves. Only the duration,
/// title, and artist are taken; lyrics, comments, and pictures are never read out.
/// </summary>
internal sealed class AtlAudioMetadataReader : IAudioMetadataReader
{
    static AtlAudioMetadataReader()
    {
        // Process-wide ATL settings. An absent tag is null, not "", and the title is never made up
        // from a file name (there is none: ATL gets a stream). Frames ATL does not describe are not
        // read, and its stack traces stay out of the console (the job records the failure).
        Settings.NullAbsentValues = true;
        Settings.UseFileNameWhenNoTitle = false;
        Settings.ReadAllMetaFrames = false;
        Settings.OutputStacktracesToConsole = false;
    }

    public AudioMetadata Read(Stream stream, string format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        var track = new Track(stream, "." + format);
        var duration = track.DurationMs is > 0 and var milliseconds && double.IsFinite(milliseconds)
            ? TimeSpan.FromMilliseconds(Math.Round(milliseconds))
            : (TimeSpan?)null;

        return new AudioMetadata(duration, track.Title, track.Artist);
    }
}
