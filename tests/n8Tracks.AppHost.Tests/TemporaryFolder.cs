namespace n8Tracks.AppHost.Tests;

/// <summary>An empty folder of the test's own, removed afterwards.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        // The real path: on macOS the temp directory sits behind a symbolic link.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "n8tracks-apphost-" + Guid.NewGuid().ToString("N"));
        Path = Directory.CreateDirectory(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file still held by a process that is shutting down.
        }
    }
}
