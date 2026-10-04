namespace n8Tracks.Infrastructure.Health;

/// <summary>Looks at the media mount. It only ever reads: nothing under the mount is created, changed, or removed.</summary>
internal interface IMediaMountProbe
{
    /// <summary>
    /// Whether <paramref name="path"/> is a directory whose entries can be listed. False when nothing
    /// is there or it is a file; may throw when the directory cannot be read.
    /// </summary>
    bool IsReadable(string path);
}
