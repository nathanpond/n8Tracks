namespace n8Tracks.Architecture.Tests;

internal static class RepositoryRoot
{
    /// <summary>Walks up from the test output directory to the directory holding the solution file.</summary>
    public static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No .sln file was found in any directory above '{AppContext.BaseDirectory}'.");
    }
}
