using System.Text.RegularExpressions;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// Guard for invariant 2 (#205), at the source: only <c>MediaMountReader</c> touches the media mount,
/// and it only reads. The technique is <see cref="EnvironmentReadGuardTests"/>'s: the code of every
/// project under <c>src/</c> that ends up in an image is read (comment lines are not code) and
/// compared with exact lists, so new code that would reach the mount fails here until someone has
/// looked at it and added it. The file-system, write, and native-code rules read each file's code as
/// a whole, so a call split across lines is seen (#386).
/// <list type="bullet">
/// <item>The media mount setting (<c>N8TRACKS_MEDIA_PATH</c>, <c>MediaPath</c>, a <c>mediaPath</c>
/// local) is named only by the options loader, the options record, <c>MediaMountReader</c>, the
/// backup code's <c>IsInside</c> comparisons, which refuse a backup path inside the mount and touch
/// nothing there, the media status (#208), which only answers the configured path as text for the
/// Media page to show, and the startup overlap check (#387), which compares the configured paths.</item>
/// <item>File-system APIs appear only in a fixed list of files (database, data folder, backups,
/// assets, setup, the frontend's files, the startup overlap check, and <c>MediaMountReader</c>); a
/// <c>System.IO</c> file-system type named in full counts, so an alias (<c>using IOFile =
/// System.IO.File;</c>) or a <c>using static</c> is seen.</item>
/// <item>Inside <c>MediaMountReader</c>, nothing creates, writes, appends, moves, copies, renames,
/// replaces, or deletes, or sets an attribute, a mode, or a time; every <c>FileStream</c> names
/// <c>FileAccess.Read</c>; <c>FileMode</c>, <c>FileAccess</c>, <c>FileShare</c>, and <c>FileOptions</c>
/// appear only as their named reading members, so a numeric cast, a parse, or arithmetic on them
/// fails; and no <c>System.IO</c> type is aliased or imported statically.</item>
/// <item>Nothing under <c>src/</c> calls native code (<c>DllImport</c>, <c>LibraryImport</c>,
/// <c>extern</c>, <c>NativeLibrary</c>, function pointers) or uses <c>dynamic</c>.</item>
/// <item><c>IMediaMount</c> has a fixed set of users; the media probe and setup only probe through it.</item>
/// <item>The tag library is given a stream, never a path, and never saves.</item>
/// </list>
/// Not covered: a path to the mount that reaches code without naming the setting (the options
/// record's deconstruction, reflection, a value typed by hand); a path handed to a library that
/// opens files itself (such as a <c>StreamReader</c> built from a path); a <c>FileMode</c> or
/// <c>FileAccess</c> value carried in a variable and changed there (<c>mode++</c>) inside the reader,
/// which the runtime check that the reader never creates a file stands behind
/// (<c>MediaMountGuardTests</c>); reflection into the stream the reader hands out; and whether the
/// operating system lets a write through, which <c>MediaMountGuardTests</c> and
/// <c>scripts/smoke-docker.sh</c> test on a read-only tree. Shell code (<c>docker/entrypoint.sh</c>,
/// <c>docker/n8tracks</c>) and the <c>Dockerfile</c> are not read here.
/// </summary>
public partial class MediaMountAccessTests
{
    private const string Reader = "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs";

    /// <summary>Every line of code that names the media mount setting.</summary>
    private static readonly string[] AllowedMediaPathLines =
    [
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: public const string MediaPath = \"N8TRACKS_MEDIA_PATH\";",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: public const string DefaultMediaPath = \"/media\";",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: Port, BaseUrl, TimeZone, LogLevel, DataPath, MediaPath, BackupPath, SunoAudioHosts, EnableTestSeeding,",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: var mediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory);",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: return new N8TracksOptions(port, baseUrl!, pathBase, timeZone!, logLevel, dataPath, mediaPath, backupPath)",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: ResolvePath(DefaultMediaPath, environment.WorkingDirectory),",
        "src/n8Tracks.Api/Configuration/EnvironmentOptionsLoader.cs: MediaPath = ResolvePath(Value(variables, MediaPath) ?? DefaultMediaPath, environment.WorkingDirectory),",
        "src/n8Tracks.Api/Endpoints/MediaEndpoints.cs: return TypedResults.Ok(MediaStatusResponse.From(status, options.MediaPath));",
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
    /// registration, the scan (which names only paths it listed), the one media probe (#207, which
    /// health, the availability monitor, and the scheduler ask instead of the mount), and setup (which
    /// only probes), and the audio content service (#217), which opens only the stored path of a
    /// cataloged file named by its ID.
    /// </summary>
    private static readonly string[] MediaMountUsers =
    [
        "src/n8Tracks.Application/Media/AudioContentService.cs",
        "src/n8Tracks.Application/Media/MediaPorts.cs",
        "src/n8Tracks.Application/Media/MediaScanService.cs",
        "src/n8Tracks.Infrastructure/DependencyInjection.cs",
        "src/n8Tracks.Infrastructure/Media/MediaFolderProbe.cs",
        "src/n8Tracks.Infrastructure/Media/MediaMountReader.cs",
        "src/n8Tracks.Infrastructure/Setup/SetupChecks.cs",
    ];

    /// <summary>The users of <c>IMediaMount</c> that may only call <c>Probe</c>.</summary>
    private static readonly string[] ProbeOnlyUsers =
    [
        "src/n8Tracks.Infrastructure/Media/MediaFolderProbe.cs",
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
    [InlineData("using IOFile = System.IO.File;")]
    [InlineData("using static System.IO.File;")]
    [InlineData("global using Dir = global::System.IO.Directory;")]
    [InlineData("System.IO.File\n    .Delete(path);")]
    [InlineData("File\n    .Delete(path);")]
    public void TheRuleFindsAFileSystemApi(string line)
    {
        Assert.Matches(UsesFileSystem(), line);
    }

    [Theory]
    [InlineData("var full = Path.Combine(root, name);")]
    [InlineData("var name = Path.GetFileName(path);")]
    [InlineData("var file = new AudioFile(id, path);")]
    [InlineData("using var reader = new StreamReader(stream);")]
    [InlineData("using System.IO;")]
    [InlineData("var file = files.First();\nvar path = file.Path;")]
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
    [InlineData("Mode = (FileMode)4,")]
    [InlineData("Mode = ( System.IO.FileMode ) 4,")]
    [InlineData("Access = FileAccess.Read | (FileAccess)2,")]
    [InlineData("Access = FileAccess.Read + 1,")]
    [InlineData("Access = ~FileAccess.Read,")]
    [InlineData("Mode = Enum.Parse<FileMode>(\"OpenOrCreate\"),")]
    [InlineData("Share = (FileShare)7,")]
    [InlineData("Options = (FileOptions)0x4000000,")]
    [InlineData("using IOFile = System.IO.File;")]
    [InlineData("using static System.IO.File;")]
    [InlineData("File\n    .Delete(path);")]
    [InlineData("_ = IOFile.Delete(path);")]
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
    [InlineData("Mode = FileMode.Open,\nAccess = FileAccess.Read,\nShare = FileShare.Read,")]
    [InlineData("var stream = new FileStream(path, new FileStreamOptions { Access = FileAccess.Read });")]
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
        var found = FindInCode(UsesFileSystem()).Select(static line => line[..line.IndexOf(": ", StringComparison.Ordinal)]).Distinct().ToList();
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
        var found = FindInCode(Writes()).Where(static line => line.StartsWith(Reader + ": ", StringComparison.Ordinal)).ToList();

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

    /// <summary>Nothing under <c>src/</c> calls native code or uses <c>dynamic</c> (#386): a P/Invoke or a late-bound call would pass every rule above.</summary>
    [Fact]
    public void NothingCallsNativeCodeOrIsLateBound()
    {
        var found = FindInCode(NativeOrDynamic());

        Assert.True(found.Count == 0, "Native or late-bound code reaches past every check of invariant 2:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Theory]
    [InlineData("[DllImport(\"libc\", SetLastError = true)]")]
    [InlineData("[System.Runtime.InteropServices.LibraryImport(\"libc\")]")]
    [InlineData("private static extern int unlink(string path);")]
    [InlineData("var handle = NativeLibrary.Load(\"libc\");")]
    [InlineData("delegate* unmanaged<string, int> unlink;")]
    [InlineData("var name = ((dynamic)opened.Content).Name;")]
    [InlineData("[UnmanagedCallersOnly]")]
    public void TheNativeRuleFindsNativeAndLateBoundCode(string code)
    {
        Assert.Matches(NativeOrDynamic(), code);
    }

    [Theory]
    [InlineData("var external = true;")]
    [InlineData("var dynamicRange = 3;")]
    [InlineData("var importer = new LibraryImporter();")]
    public void TheNativeRuleAllows(string code)
    {
        Assert.DoesNotMatch(NativeOrDynamic(), code);
    }

    [Theory]
    [InlineData("var entries = mount.List(string.Empty);", true)]
    [InlineData("var stat = mount.Stat(path);", true)]
    [InlineData("using var stream = mount.OpenRead(path);", true)]
    [InlineData("await using var file = mount.OpenWithStat(path);", true)]
    [InlineData("return mount.Probe();", false)]
    public void TheProbeOnlyRuleFindsEveryOtherMember(string line, bool found)
    {
        Assert.Equal(found, CallsBeyondProbe().IsMatch(line));
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

    /// <summary>
    /// The .NET APIs that reach the file system (not <c>Path</c>'s string helpers, which touch
    /// nothing), and any <c>System.IO</c> file-system type named in full, as an alias or a
    /// <c>using static</c> names it.
    /// </summary>
    [GeneratedRegex(
        @"\b(File|Directory)\s*\.\s*[A-Z]\w*"
        + @"|\b(FileStream|FileStreamOptions|FileInfo|DirectoryInfo|FileSystemInfo|DriveInfo|FileSystemWatcher|RandomAccess|SafeFileHandle|ZipFile)\b"
        + @"|\bPath\s*\.\s*(GetFullPath|GetTempPath|GetTempFileName)\b"
        + @"|\bSystem\s*\.\s*IO\s*\.\s*(File|Directory|FileSystem|Enumeration)\w*")]
    private static partial Regex UsesFileSystem();

    /// <summary>
    /// What creates, writes, appends, moves, copies, renames, replaces, or deletes, or sets an
    /// attribute, a mode, or a time; and any file mode, access, sharing, or option but reading.
    /// </summary>
    [GeneratedRegex(
        @"\b(File|Directory)\s*\.\s*(Create\w*|Write\w*|Append\w*|Move|Copy|Replace|Delete|Set\w*|Encrypt|Decrypt|Open|OpenWrite|OpenHandle)\b"
        + @"|\.\s*(Create|CreateSubdirectory|CreateText|AppendText|OpenWrite|Delete|MoveTo|CopyTo|Replace|Encrypt|Decrypt|CreateAsSymbolicLink|SetLength|Write\w*|Flush\w*)\s*\("
        + @"|\.\s*(Attributes|CreationTime\w*|LastAccessTime\w*|LastWriteTime\w*|UnixFileMode|IsReadOnly)\s*=(?!=)"
        + @"|\bFileMode\b(?!\s*\.\s*Open\b)|\bFileAccess\b(?!\s*\.\s*Read\b)|\bFileShare\b(?!\s*\.\s*Read\b)"
        + @"|\bFileOptions\b(?!\s*\.\s*(None|SequentialScan|RandomAccess|Asynchronous)\b)"
        + @"|\b(FileMode|FileAccess|FileShare|FileOptions)\s*\.\s*\w+\s*[-+*/%|&^<>]|[~-]\s*\(?\s*(FileMode|FileAccess|FileShare|FileOptions)\b"
        + @"|\busing\s+(static\s+|\w+\s*=\s*)(global::)?System\s*\.\s*IO\b"
        + @"|\b(StreamWriter|BinaryWriter|TextWriter|ZipFile|Process)\b")]
    private static partial Regex Writes();

    /// <summary>A <c>FileStream</c> being made, up to the end of its statement.</summary>
    [GeneratedRegex(@"new\s+FileStream\s*\([^;]*;")]
    private static partial Regex OpensAStream();

    [GeneratedRegex(@"\bIMediaMount\b")]
    private static partial Regex NamesMediaMount();

    /// <summary>Native code (a P/Invoke, a function pointer, a loaded library) and late binding.</summary>
    [GeneratedRegex(@"\b(DllImport|LibraryImport|NativeLibrary|UnmanagedCallersOnly)\b|\bextern\s+\w|\bdelegate\s*\*\s*unmanaged\b|\bdynamic\b|\bMarshal\s*\.\s*GetDelegateForFunctionPointer\b")]
    private static partial Regex NativeOrDynamic();

    [GeneratedRegex(@"\.\s*(List|Stat|OpenRead|OpenWithStat)\s*\(")]
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

    /// <summary>
    /// Every match in the code of every file, a statement split across lines included, as
    /// <c>path: match</c> with its whitespace collapsed, in path order. Comment lines are not code.
    /// </summary>
    private static List<string> FindInCode(Regex rule) =>
    [
        .. SourceFiles().SelectMany(file => rule.Matches(Code(file)).Select(match => $"{Relative(file)}: {Normalize(match.Value)}")),
    ];

    /// <summary>The text with every run of whitespace made one space, and none around a parenthesis or a dot.</summary>
    private static string Normalize(string text) =>
        SpacesAroundPunctuation().Replace(Spaces().Replace(text.Trim(), " "), "$1");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*([().])\s*")]
    private static partial Regex SpacesAroundPunctuation();

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
