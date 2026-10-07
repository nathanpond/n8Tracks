using n8Tracks.Domain.Media;

namespace n8Tracks.Application.Media;

/// <summary>Reading the audio file catalog (#203): a page of files, or one file. Never an absolute path.</summary>
public sealed class AudioFileService(IAudioFileStore files)
{
    /// <summary>The most a page holds; a larger <c>limit</c> is refused. The public paging convention arrives in M7.</summary>
    public const int MaximumLimit = 200;

    /// <summary>One page of files, in path order.</summary>
    public Task<AudioFilePage> ListAsync(AudioFileQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumLimit);

        return files.ListAsync(query, cancellationToken);
    }

    /// <summary>The file with <paramref name="id"/>, or null.</summary>
    public Task<AudioFile?> FindAsync(Guid id, CancellationToken cancellationToken) => files.FindAsync(id, cancellationToken);
}
