using System.Text.RegularExpressions;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// Guard for invariant 2 (#205), at the source: only <c>MediaMountReader</c> touches the media mount,
/// and it only reads. The technique is <see cref="EnvironmentReadGuardTests"/>'s: the code of every
/// project under <c>src/</c> that ends up in an image is read line by line (comment lines are not
/// code) and compared with exact lists, so a new line that would reach the mount fails here until
/// someone has looked at it and added it.
/// <list type="bullet">
/// <item>The media mount setting (<c>N8TRACKS_MEDIA_PATH</c>, <c>MediaPath</c>, a <c>mediaPath</c>
/// local) is named only by the options loader, the options record, <c>MediaMountReader</c>, and the
/// backup code's <c>IsInside</c> comparisons, which refuse a backup path inside the mount and touch
/// nothing there.</item>
/// <item>File-system APIs appear only in a fixed list of files (database, data folder, backups,
/// assets, setup, the frontend's files, and <c>MediaMountReader</c>).</item>
/// <item>Inside <c>MediaMountReader</c>, nothing creates, writes, appends, moves, copies, renames,
/// replaces, or deletes, or sets an attribute, a mode, or a time; every <c>FileStream</c> names
/// <c>FileAccess.Read</c>, and no other access, mode, or sharing appears.</item>
/// <item><c>IMediaMount</c> has a fixed set of users; health and setup only probe through it.</item>
/// <item>The tag library is given a stream, never a path, and never saves.</item>
/// </list>
/// Not covered: a path to the mount that reaches code without naming the setting (the options
/// record's deconstruction, reflection, a value typed by hand); a path handed to a library that
/// opens files itself (such as a <c>StreamReader</c> built from a path); and whether the operating
/// system lets a write through, which <c>MediaMountGuardTests</c> and <c>scripts/smoke-docker.sh</c>
/// test on a read-only tree.
/// </summary>
public partial class MediaMountAccessTests
{
    private const string Reader = "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs";

    /// <summary>Every line of code that names the media mount setting.</summary>
    private static readonly string[] AllowedMediaPathLines =
    [
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: public const string MediaPath = \"N8TRACKS_MEDIA_PATH\";",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: public const string DefaultMediaPath = \"/media\";",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: Port, BaseUrl, TimeZone, LogLevel, DataPath, MediaPath, BackupPath, EnableTestSeeding,",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: var mediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory);",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: return new N8TracksOptions(port, baseUrl!, pathBase, timeZone!, logLevel, dataPath, mediaPath, backupPath);",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: ResolvePath(DefaultMediaPath, environment.WorkingDirectory),",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: MediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory),",
        "src/n8Tracks.Application/Backups/OfflineRestoreService.cs: if (IsInside(fullPath, options.MediaPath))",
        "src/n8Tracks.Application/Configuration/N8TracksOptions.cs: string MediaPath,",
        "src/n8Tracks.Infrastructure/Backups/BackupFolders.cs: private bool MountIsUsable() => Directory.Exists(options.BackupPath) && !IsInside(options.BackupPath, options.MediaPath);",
        "src/n8Tracks.Infrastructure/Backups/BackupFolders.cs: && !IsInside(FallbackPath, options.MediaPath))",
        "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs: private string Root() => Path.TrimEndingDirectorySeparator(options.MediaPath);",
    ];

    /// <summary>The files that may use a file-system API: none of them names the media mount setting but the lines above.</summary>
    private static readonly string[] AllowedFileSystemFiles =
    [
        "src/n8Tracks.Api/Cli/RestoreCommand.cs",
        "src/n8Tracks.Api/Cli/SeedGenerationCommand.cs",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs",
        "src/n8Tracks.Api/Configuration/ProcessEnvironment.cs",
        "src/n8Tracks.Application/Backups/OfflineRestoreService.cs",
        "src/n8Tracks.Infrastructure/Assets/ManagedAssetStore.cs",
        "src/n8Tracks.Infrastructure/Backups/BackupFolders.cs",
        "src/n8Tracks.Infrastructure/Backups/BackupWriter.cs",
        "src/n8Tracks.Infrastructure/Backups/DataPathLockFile.cs",
        "src/n8Tracks.Infrastructure/Backups/LastRestoreFile.cs",
        "src/n8Tracks.Infrastructure/Backups/LiveDataReplacement.cs",
        "src/n8Tracks.Infrastructure/Backups/RestoreArchives.cs",
        "src/n8Tracks.Infrastructure/Backups/UpgradeMarkerFile.cs",
        "src/n8Tracks.Infrastructure/Backups/UpgradeSafetyRestore.cs",
        "src/n8Tracks.Infrastructure/Maintenance/MaintenanceStateFile.cs",
        "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs",
        "src/n8Tracks.Infrastructure/Persistence/DatabaseSchemaCheck.cs",
        "src/n8Tracks.Infrastructure/Persistence/SqliteDatabase.cs",
        "src/n8Tracks.Infrastructure/Retention/ManagedFiles.cs",
        "src/n8Tracks.Infrastructure/Setup/SetupChecks.cs",
    ];

    /// <summary>
    /// The files that name <c>IMediaMount</c>: its declaration, its one implementation, its
    /// registration, the scan (which names only paths it listed), and health and setup (which only probe).
    /// #217 adds the audio content service, which names only stored paths.
    /// </summary>
    private static readonly string[] MediaMountUsers =
    [
        "src/n8Tracks.Application/Media/MediaPorts.cs",
        "src/n8Tracks.Application/Media/MediaScanService.cs",
        "src/n8Tracks.Infrastructure/DependencyInjection.cs",
        "src/n8Tracks.Infrastructure/Health/HealthService.cs",
        "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs",
        "src/n8Tracks.Infrastructure/Setup/SetupChecks.cs",
    ];

    /// <summary>The users of <c>IMediaMount</c> that may only call <c>Probe</c>.</summary>
    private static readonly string[] ProbeOnlyUsers =
    [
        "src/n8Tracks.Infrastructure/Health/HealthService.cs",
        "src/n8Tracks.Infrastructure/Setup/SetupChecks.cs",
    ];

    [Theory]
    [InlineData("var root = options.MediaPath;")]
    [InlineData("var (port, url, pathBase, zone, level, data, media, backup) = (options.Port, 1, 2, 3, 4, 5, options.MediaPath, 6);")]
    [InlineData("var mediaPath = options.MediaPath;")]
    [InlineData("Directory.EnumerateFiles(mediaPath);")]
    [InlineData("var root = environment.Variables[\"N8TRACKS_MEDIA_PATH\"];")]
    [InlineData("var root = \"/media\";")]
    [InlineData("var root = \"/media/\";")]
    [InlineData("var root = EnvironmentOptionsLoader.DefaultMediaPath;")]
    public void TheRuleFindsTheMediaSettingBeingNamed(string line)
    {
        Assert.Matches(NamesMediaSetting(), line);
    }

    [Theory]
    [InlineData("public const string ScansPath = ApiProblem.VersionPrefix + \"/media/scans\";")]
    [InlineData("var root = options.DataPath;")]
    [InlineData("var mediaType = \"audio/mpeg\";")]
    [InlineData("IMediaMount mount,")]
    public void TheMediaSettingRuleAllows(string line)
    {
        Assert.DoesNotMatch(NamesMediaSetting(), line);
    }

    [Theory]
    [InlineData("File.Exists(path)")]
    [InlineData("System.IO.File.ReadAllBytes(path)")]
    [InlineData("Directory.EnumerateFiles(root)")]
    [InlineData("using var stream = new FileStream(path, FileMode.Open);")]
    [InlineData("var info = new FileInfo(path);")]
    [InlineData("var info = new DirectoryInfo(path);")]
    [InlineData("using var handle = File.OpenHandle(path);")]
    [InlineData("RandomAccess.Read(handle, buffer, 0);")]
    [InlineData("ZipFile.ExtractToDirectory(archive, target);")]
    [InlineData("var full = Path.GetFullPath(relative);")]
    [InlineData("using var watcher = new FileSystemWatcher(root);")]
    public void TheRuleFindsAFileSystemApi(string line)
    {
        Assert.Matches(UsesFileSystem(), line);
    }

    [Theory]
    [InlineData("var full = Path.Combine(root, name);")]
    [InlineData("var name = Path.GetFileName(path);")]
    [InlineData("var file = new AudioFile(id, path);")]
    [InlineData("using var reader = new StreamReader(stream);")]
    public void TheFileSystemRuleAllows(string line)
    {
        Assert.DoesNotMatch(UsesFileSystem(), line);
    }

    [Theory]
    [InlineData("File.Delete(path);")]
    [InlineData("File.WriteAllBytes(path, bytes);")]
    [InlineData("File.AppendAllText(path, text);")]
    [InlineData("File.Move(from, to);")]
    [InlineData("File.Copy(from, to);")]
    [InlineData("File.Replace(from, to, backup);")]
    [InlineData("File.SetLastWriteTimeUtc(path, now);")]
    [InlineData("File.SetUnixFileMode(path, UnixFileMode.None);")]
    [InlineData("File.SetAttributes(path, FileAttributes.ReadOnly);")]
    [InlineData("File.CreateSymbolicLink(path, target);")]
    [InlineData("using var stream = File.OpenWrite(path);")]
    [InlineData("using var stream = File.Open(path, FileMode.Open);")]
    [InlineData("using var handle = File.OpenHandle(path);")]
    [InlineData("Directory.CreateDirectory(path);")]
    [InlineData("Directory.Delete(path, recursive: true);")]
    [InlineData("Directory.Move(from, to);")]
    [InlineData("info.Delete();")]
    [InlineData("info.MoveTo(target);")]
    [InlineData("info.CopyTo(target);")]
    [InlineData("info.Create();")]
    [InlineData("directory.CreateSubdirectory(\"x\");")]
    [InlineData("info.Attributes = FileAttributes.Normal;")]
    [InlineData("info.LastWriteTimeUtc = now;")]
    [InlineData("info.UnixFileMode = mode;")]
    [InlineData("info.IsReadOnly = false;")]
    [InlineData("stream.Write(buffer);")]
    [InlineData("stream.SetLength(0);")]
    [InlineData("Mode = FileMode.OpenOrCreate,")]
    [InlineData("new FileStream(path, FileMode.Create, FileAccess.Write)")]
    [InlineData("Access = FileAccess.ReadWrite,")]
    [InlineData("Share = FileShare.Delete,")]
    [InlineData("Options = FileOptions.DeleteOnClose,")]
    [InlineData("using var writer = new StreamWriter(stream);")]
    public void TheRuleFindsAWriteInTheReader(string line)
    {
        Assert.Matches(Writes(), line);
    }

    [Theory]
    [InlineData("if (!Directory.Exists(root))")]
    [InlineData("using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();")]
    [InlineData("var target = new FileInfo(next).LinkTarget;")]
    [InlineData("return new MediaFileStat(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));")]
    [InlineData("Mode = FileMode.Open,")]
    [InlineData("Access = FileAccess.Read,")]
    [InlineData("Share = FileShare.Read,")]
    [InlineData("Options = FileOptions.SequentialScan,")]
    [InlineData("return File.Exists(resolved)")]
    public void TheWriteRuleAllows(string line)
    {
        Assert.DoesNotMatch(Writes(), line);
    }

    [Fact]
    public void OnlyTheLoaderTheReaderAndTheBackupComparisonsNameTheMediaSetting()
    {
        var found = Find(NamesMediaSetting());
        var unexpected = found.Except(AllowedMediaPathLines, StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "Only MediaMountReader touches the media mount (invariant 2); nothing else may build a path from its setting:" + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected));

        // The list stays exact: a line that has gone must leave it.
        Assert.Equal(AllowedMediaPathLines, found);
    }

    [Fact]
    public void OnlyTheListedFilesUseAFileSystemApi()
    {
        var found = Find(UsesFileSystem()).Select(static line => line[..line.IndexOf(": ", StringComparison.Ordinal)]).Distinct().ToList();
        var unexpected = found.Except(AllowedFileSystemFiles, StringComparer.Ordinal).ToList();

        Assert.True(
            unexpected.Count == 0,
            "A new file uses the file system; check that it never touches the media mount (invariant 2), then add it to the list:" + Environment.NewLine
            + string.Join(Environment.NewLine, unexpected));
        Assert.Equal(AllowedFileSystemFiles, found);
    }

    [Fact]
    public void TheReaderNeverCreatesWritesMovesOrDeletes()
    {
        var found = Find(Writes()).Where(static line => line.StartsWith(Reader + ": ", StringComparison.Ordinal)).ToList();

        Assert.True(found.Count == 0, "MediaMountReader only reads (invariant 2):" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    /// <summary>Every <c>FileStream</c> the reader makes names <c>FileAccess.Read</c> (one without an access opens for writing too).</summary>
    [Fact]
    public void EveryStreamTheReaderOpensIsReadOnly()
    {
        var code = Code(Path.Combine(RepositoryRoot.Find(), Reader));
        var streams = OpensAStream().Matches(code);

        Assert.NotEmpty(streams);
        Assert.All(streams, static stream =>
            Assert.True(stream.Value.Contains("FileAccess.Read", StringComparison.Ordinal), $"Not read-only: {stream.Value}"));
    }

    [Theory]
    [InlineData("var stream = new FileStream(path, FileMode.Open);", false)]
    [InlineData("var stream = new FileStream(path, FileMode.Open, FileAccess.Read);", true)]
    [InlineData("var stream = new FileStream(\n    path,\n    new FileStreamOptions\n    {\n        Access = FileAccess.Read,\n    });", true)]
    [InlineData("var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open });", false)]
    public void TheStreamRuleReadsTheWholeStatement(string code, bool readOnly)
    {
        var stream = Assert.Single(OpensAStream().Matches(code));
        Assert.Equal(readOnly, stream.Value.Contains("FileAccess.Read", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheListedTypesUseTheMediaMountAndHealthAndSetupOnlyProbe()
    {
        var users = Find(NamesMediaMount()).Select(static line => line[..line.IndexOf(": ", StringComparison.Ordinal)]).Distinct().ToList();
        Assert.Equal(MediaMountUsers, users);

        var calls = Find(CallsBeyondProbe()).Where(static line => ProbeOnlyUsers.Any(file => line.StartsWith(file + ": ", StringComparison.Ordinal))).ToList();
        Assert.True(calls.Count == 0, "Health and setup only probe the media mount:" + Environment.NewLine + string.Join(Environment.NewLine, calls));
    }

    /// <summary>The tag library is given the stream the reader opened, never a path, and the media adapters never save tags.</summary>
    [Fact]
    public void TheTagLibraryIsGivenAStreamAndNeverSaves()
    {
        var made = Find(MakesATrack());
        Assert.Equal(["src/n8Tracks.Infrastructure/Media/AtlAudioMetadataReader.cs: var track = new Track(stream, \".\" + format);"], made);

        var saves = Find(SavesATrack()).Where(static line => line.StartsWith("src/n8Tracks.Infrastructure/Media/", StringComparison.Ordinal)).ToList();
        Assert.True(saves.Count == 0, "Nothing in the media adapters saves tags:" + Environment.NewLine + string.Join(Environment.NewLine, saves));
    }

    /// <summary>The media mount setting by any of its names, or the default mount path as a literal.</summary>
    [GeneratedRegex(@"(?i)\w*media_?path\b|""/media/?""")]
    private static partial Regex NamesMediaSetting();

    /// <summary>The .NET APIs that reach the file system (not <c>Path</c>'s string helpers, which touch nothing).</summary>
    [GeneratedRegex(
        @"\b(File|Directory)\s*\.\s*[A-Z]\w*"
        + @"|\b(FileStream|FileStreamOptions|FileInfo|DirectoryInfo|FileSystemInfo|DriveInfo|FileSystemWatcher|RandomAccess|SafeFileHandle|ZipFile)\b"
        + @"|\bPath\s*\.\s*(GetFullPath|GetTempPath|GetTempFileName)\b")]
    private static partial Regex UsesFileSystem();

    /// <summary>
    /// What creates, writes, appends, moves, copies, renames, replaces, or deletes, or sets an
    /// attribute, a mode, or a time; and any file mode, access, sharing, or option but reading.
    /// </summary>
    [GeneratedRegex(
        @"\b(File|Directory)\s*\.\s*(Create\w*|Write\w*|Append\w*|Move|Copy|Replace|Delete|Set\w*|Encrypt|Decrypt|Open|OpenWrite|OpenHandle)\b"
        + @"|\.\s*(Create|CreateSubdirectory|CreateText|AppendText|OpenWrite|Delete|MoveTo|CopyTo|Replace|Encrypt|Decrypt|CreateAsSymbolicLink|SetLength|Write\w*|Flush\w*)\s*\("
        + @"|\.\s*(Attributes|CreationTime\w*|LastAccessTime\w*|LastWriteTime\w*|UnixFileMode|IsReadOnly)\s*=(?!=)"
        + @"|\bFileMode\s*\.\s*(?!Open\b)\w+|\bFileAccess\s*\.\s*(?!Read\b)\w+|\bFileShare\s*\.\s*(?!Read\b)\w+"
        + @"|\bFileOptions\s*\.\s*(DeleteOnClose|WriteThrough)\b"
        + @"|\b(StreamWriter|BinaryWriter|TextWriter|ZipFile|Process)\b")]
    private static partial Regex Writes();

    /// <summary>A <c>FileStream</c> being made, up to the end of its statement.</summary>
    [GeneratedRegex(@"new\s+FileStream\s*\([^;]*;")]
    private static partial Regex OpensAStream();

    [GeneratedRegex(@"\bIMediaMount\b")]
    private static partial Regex NamesMediaMount();

    [GeneratedRegex(@"\.\s*(List|Stat|OpenRead)\s*\(")]
    private static partial Regex CallsBeyondProbe();

    [GeneratedRegex(@"\bnew\s+(ATL\s*\.\s*)?Track\s*\(")]
    private static partial Regex MakesATrack();

    [GeneratedRegex(@"\.\s*(Save|SaveAsync|Remove|RemoveAsync|CopyMetadataTo|CopyMetadataToAsync)\s*\(")]
    private static partial Regex SavesATrack();

    /// <summary>Every matching line of code, as <c>path: line</c>, in path order. Comment lines are not code.</summary>
    private static List<string> Find(Regex rule) =>
    [
        .. SourceFiles()
            .SelectMany(file => File.ReadLines(file)
                .Select(static line => line.Trim())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && rule.IsMatch(line))
                .Select(line => $"{Relative(file)}: {line}")),
    ];

    /// <summary>A file's code with its comment lines left out.</summary>
    private static string Code(string file) =>
        string.Join('\n', File.ReadLines(file).Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>The same projects <see cref="EnvironmentReadGuardTests"/> reads: every one under <c>src/</c> but the AppHost (in no image).</summary>
    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateDirectories(Path.Combine(RepositoryRoot.Find(), "src"))
            .Where(static directory => Path.GetFileName(directory) != "n8Tracks.AppHost")
            .Where(static directory => Directory.EnumerateFiles(directory, "*.csproj").Any())
            .SelectMany(static project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(static file =>
            {
                var relative = Relative(file);
                return !relative.Contains("/bin/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal);

    private static string Relative(string file) =>
        Path.GetRelativePath(RepositoryRoot.Find(), file).Replace(Path.DirectorySeparatorChar, '/');
}
