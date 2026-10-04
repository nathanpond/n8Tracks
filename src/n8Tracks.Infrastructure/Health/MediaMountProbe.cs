namespace n8Tracks.Infrastructure.Health;

internal sealed class MediaMountProbe : IMediaMountProbe
{
    public bool IsReadable(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        // Asking for the first entry is what proves the directory can be read; an empty one is fine.
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        _ = entries.MoveNext();

        return true;
    }
}
