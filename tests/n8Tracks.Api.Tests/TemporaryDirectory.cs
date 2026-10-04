namespace n8Tracks.Api.Tests;

/// <summary>A directory under the system temp path, removed on dispose.</summary>
public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("n8tracks-test-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
