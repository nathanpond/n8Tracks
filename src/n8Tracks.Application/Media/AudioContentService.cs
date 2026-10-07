using System.Globalization;
using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>
/// Serving a cataloged audio file's bytes (#217), as they are on disk: never transcoded, never copied,
/// and never written. The file is named only by its audio file ID; its stored relative path is handed
/// to the media mount, the one reader of the media folder (invariant 2), which refuses anything that
/// resolves outside it. Only a file that reports Available (#207) is opened, and the request never
/// changes the record, whatever the read finds. Every cataloged record can be served, whatever its
/// association.
/// </summary>
public sealed class AudioContentService(IAudioFileStore files, MediaAvailability availability, IMediaMount mount)
{
    /// <summary>
    /// The content of the audio file <paramref name="id"/>: <see cref="AudioContentOutcome.NotFound"/>
    /// when there is no such record; <see cref="AudioContentOutcome.Unavailable"/> when it reports
    /// Missing or Unavailable, or it cannot be opened now (gone since the last scan, unreadable, or
    /// swapped for a link that leaves the media folder); otherwise the opened content, which the caller
    /// disposes.
    /// </summary>
    public async Task<AudioContentOutcome> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await files.FindAsync(id, cancellationToken).ConfigureAwait(false) is not { } file)
        {
            return AudioContentOutcome.NotFound.Instance;
        }

        var state = (await availability.CurrentAsync(cancellationToken).ConfigureAwait(false)).State;
        if (MediaAvailability.Reported(file.Status, state) != AudioFileReportedStatus.Available)
        {
            return new AudioContentOutcome.Unavailable(OpenFailed: false);
        }

        OpenedMediaFile opened;
        MediaFileStat stat;
        try
        {
            opened = mount.OpenWithStat(file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file not found, a link out (MediaPathOutsideException), or one that may not be read:
            // the caller logs the ID only, since the exception's text may carry the path.
            return new AudioContentOutcome.Unavailable(OpenFailed: true);
        }

        try
        {
            stat = opened.Stat();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            return new AudioContentOutcome.Unavailable(OpenFailed: true);
        }

        return new AudioContentOutcome.Opened(new AudioContent(
            file.Id,
            file.FileName,
            AudioFormats.MediaType(file.Format),
            stat.SizeBytes,
            stat.ModifiedUtc,
            EntityTag(stat),
            new AudioContentStream(opened, stat)));
    }

    /// <summary>
    /// The strong entity tag of a file with <paramref name="stat"/>: its size and last-modified time
    /// (to the tick), quoted. A file changed on disk gets another tag.
    /// </summary>
    public static string EntityTag(MediaFileStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        return string.Create(CultureInfo.InvariantCulture, $"\"{stat.SizeBytes:x}-{stat.ModifiedUtc.UtcTicks:x}\"");
    }
}

/// <summary>What <see cref="AudioContentService.OpenAsync"/> found.</summary>
public abstract record AudioContentOutcome
{
    private AudioContentOutcome()
    {
    }

    /// <summary>There is no audio file with that ID.</summary>
    public sealed record NotFound : AudioContentOutcome
    {
        public static NotFound Instance { get; } = new();
    }

    /// <summary>
    /// The file is known but cannot be served: it reports Missing or Unavailable, or (when
    /// <paramref name="OpenFailed"/>) it reports Available but could not be opened.
    /// </summary>
    public sealed record Unavailable(bool OpenFailed) : AudioContentOutcome;

    /// <summary>The file is open.</summary>
    public sealed record Opened(AudioContent Content) : AudioContentOutcome;
}

/// <summary>
/// An opened audio file, ready to send: its own name (never its path), its media type, and the length,
/// last-modified time, and entity tag its open handle gave. <see cref="Content"/> reads only those
/// bytes: it fails with <see cref="AudioContentChangedException"/> when the file ends early or changes
/// while it is read. Disposing it closes the file.
/// </summary>
public sealed record AudioContent(
    Guid Id,
    string FileName,
    string MediaType,
    long Length,
    DateTimeOffset ModifiedUtc,
    string EntityTag,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>The file being sent ended early, or changed on disk, after its length and tag were sent.</summary>
public sealed class AudioContentChangedException : IOException
{
    public AudioContentChangedException()
        : base("The audio file changed while it was being sent.")
    {
    }

    public AudioContentChangedException(string message)
        : base(message)
    {
    }

    public AudioContentChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The opened file, read-only and seekable, held to what its handle said when it was opened: a read
/// that comes up short of that length, or that finds the handle's size or modified time changed,
/// throws <see cref="AudioContentChangedException"/>, so a response is never completed with bytes
/// that do not match its length and tag.
/// </summary>
internal sealed class AudioContentStream(OpenedMediaFile file, MediaFileStat opened) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => opened.SizeBytes;

    public override long Position
    {
        get => file.Content.Position;
        set => file.Content.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var start = file.Content.Position;
        return Checked(start, buffer.Length, file.Content.Read(buffer));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var start = file.Content.Position;
        return Checked(start, buffer.Length, await file.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    }

    public override long Seek(long offset, SeekOrigin origin) => file.Content.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            file.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await file.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="read"/> bytes came back for <paramref name="asked"/> at <paramref name="start"/>:
    /// none while the length says more remain is a short read; and the handle must still say what it
    /// said at the open.
    /// </summary>
    private int Checked(long start, int asked, int read)
    {
        if (asked > 0 && read == 0 && start < opened.SizeBytes)
        {
            throw new AudioContentChangedException("The audio file ended before its length.");
        }

        if (file.Stat() != opened)
        {
            throw new AudioContentChangedException();
        }

        return read;
    }
}
