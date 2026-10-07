using System.Reflection;
using NetArchTest.Rules;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// #204 AC 5: nothing depends on file-change notifications. The media folder may be a network share
/// or a bind mount where they are unreliable, so a file added while the application runs is found by
/// the next scan and by no other means. Two checks: no type of the server's assemblies depends on
/// <see cref="FileSystemWatcher"/>, and no source file of any project under <c>src/</c> names it (a
/// reflection or a project the references above do not reach).
/// <para>
/// Not covered: a watcher the framework creates for itself (a configuration file reloaded on change;
/// the server builds its host from an empty builder with no file source), and other notification
/// APIs (<c>IFileProvider.Watch</c>, inotify through P/Invoke).
/// </para>
/// </summary>
public class NoFileWatchingTests
{
    private const string Watcher = "System.IO.FileSystemWatcher";

    private static readonly Assembly[] Server =
    [
        typeof(n8Tracks.Domain.AssemblyMarker).Assembly,
        typeof(n8Tracks.Application.AssemblyMarker).Assembly,
        typeof(n8Tracks.Infrastructure.AssemblyMarker).Assembly,
        typeof(Program).Assembly,
    ];

    [Fact]
    public void NoServerTypeDependsOnAFileSystemWatcher()
    {
        var watching = Types.InAssemblies(Server).That().HaveDependencyOnAny([Watcher]).GetTypes().Select(static type => type.FullName).ToList();

        Assert.Empty(watching);

        // Complement: the rule sees a watcher where there is one, so the empty answer above is not blindness.
        var sample = Types.InAssembly(typeof(NoFileWatchingTests).Assembly).That().HaveDependencyOnAny([Watcher]).GetTypes().Select(static type => type.FullName);
        Assert.Contains(typeof(WatchingSample).FullName!.Replace('+', '/'), sample);
    }

    [Fact]
    public void NoSourceFileUnderSrcNamesAFileSystemWatcher()
    {
        var root = RepositoryRoot.Find();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        // Complement: the scan reads the scheduler that replaces watching, so an empty answer means something.
        Assert.Contains(files, static file => file.EndsWith("MediaScanScheduler.cs", StringComparison.Ordinal));

        var naming = files
            .Where(static file => File.ReadAllText(file).Contains(nameof(FileSystemWatcher), StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();
        Assert.Empty(naming);
    }

    /// <summary>A type that depends on a watcher, for the complement only.</summary>
    private sealed class WatchingSample
    {
        public static FileSystemWatcher Create() => new();
    }
}
