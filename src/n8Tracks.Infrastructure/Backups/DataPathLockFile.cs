using n8Tracks.Application.Backups;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Infrastructure.Backups;

/// <summary>
/// <c>.n8tracks.lock</c> under the data path, opened with no sharing: on Linux and macOS .NET takes
/// an exclusive <c>flock</c> on it, which the kernel releases when the process ends, however it
/// ends. The file is never deleted, and holds nothing; only the lock on it counts. It is opened for
/// reading only, so a lock file some other user created does not stop the app as long as it can be
/// read. Released when the container that made this is disposed.
/// </summary>
internal sealed class DataPathLockFile(N8TracksOptions options) : IDataPathLock, IDisposable
{
    public const string FileName = ".n8tracks.lock";

    private readonly Lock gate = new();
    private FileStream? held;

    public string LockFile { get; } = Path.Combine(options.DataPath, FileName);

    public bool TryAcquire()
    {
        lock (gate)
        {
            if (held is not null)
            {
                return true;
            }

            try
            {
                held = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None, bufferSize: 1);
                return true;
            }
            catch (IOException exception) when (exception is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
            {
                // Sharing violation: another process, or another host in this one, holds it.
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            held?.Dispose();
            held = null;
        }
    }
}
