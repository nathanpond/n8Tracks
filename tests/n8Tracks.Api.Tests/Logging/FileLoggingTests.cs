using System.Text.Json;
using n8Tracks.Api.Tests.Auth;
using n8Tracks.Application.Logging;
using n8Tracks.Infrastructure.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace n8Tracks.Api.Tests.Logging;

/// <summary>
/// The application's log files (#234) on a temporary data folder and a clock the test moves: daily
/// and size rolls, retention by the date in the name, the cap kept oldest first, nothing deleted
/// before the saved limits are in effect, an unwritable folder, and invariant 2 (a log folder in the
/// media folder is never written, and a link with a log file's name is never written through).
/// </summary>
public sealed class FileLoggingTests : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    private readonly TemporaryDirectory data = new();
    private readonly TemporaryDirectory media = new();
    private readonly TestClock clock = new();

    private string Folder => Path.Combine(data.Path, FileLogging.FolderName);

    public void Dispose()
    {
        data.Dispose();
        media.Dispose();
    }

    [Fact]
    public void FilesAreNamedByTheUtcDateAndStartAfreshEachDay()
    {
        using var files = Started();

        Emit(files, "first day");
        clock.Advance(TimeSpan.FromHours(15)); // 2026-10-02 00:00 UTC
        Emit(files, "second day");

        Assert.Equal(["n8tracks-20261001.jsonl", "n8tracks-20261002.jsonl"], Names());
        Assert.Contains("first day", File.ReadAllText(Path.Combine(Folder, "n8tracks-20261001.jsonl")), StringComparison.Ordinal);
        Assert.Contains("second day", File.ReadAllText(Path.Combine(Folder, "n8tracks-20261002.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileRollsAtAQuarterOfASmallCapAndAt20MegabytesOtherwise()
    {
        using var files = Started();
        files.Configure(new LogFileLimits(14, 10));
        Assert.Equal(10 * Megabyte / 4, files.RollBytes);

        // Three lines of a megabyte (the text is in the message and in the properties): the third
        // would take the first file past 2.5 MB.
        Emit(files, Text(Megabyte / 2));
        Emit(files, Text(Megabyte / 2));
        Emit(files, Text(Megabyte / 2));

        Assert.Equal(["n8tracks-20261001.jsonl", "n8tracks-20261001_1.jsonl"], Names());
        Assert.Equal(2, File.ReadAllLines(Path.Combine(Folder, "n8tracks-20261001.jsonl")).Length);

        files.Configure(new LogFileLimits(14, 5120));
        Assert.Equal(FileLogging.MaximumRollBytes, files.RollBytes);
    }

    [Fact]
    public void TodaysNewestFileIsAppendedToAfterARestartWhileItHasRoom()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "n8tracks-20261001.jsonl"), "{\"before\":true}\n");

        using var files = Started();
        Emit(files, "after the restart");

        Assert.Equal(["n8tracks-20261001.jsonl"], Names());
        Assert.Equal(2, File.ReadAllLines(Path.Combine(Folder, "n8tracks-20261001.jsonl")).Length);
    }

    [Fact]
    public void RetentionGoesByTheDateInTheNameKeepingAFileExactlyThatOldAndIgnoringOtherFiles()
    {
        Directory.CreateDirectory(Folder);
        foreach (var name in new[] { "n8tracks-20260916.jsonl", "n8tracks-20260916_1.jsonl", "n8tracks-20260917.jsonl", "n8tracks-20260918.jsonl", "notes.txt", "n8tracks-old.jsonl", "n8tracks-20260101.log" })
        {
            File.WriteAllText(Path.Combine(Folder, name), "x");
        }

        using var files = Started();
        files.Configure(new LogFileLimits(14, 200));
        var deleted = files.Sweep();

        // 2026-10-01 less 14 days is 2026-09-17: kept; the 16th is 15 days old.
        Assert.Equal(new LogFileDeletion(2, 2), deleted);
        Assert.Equal(
            ["n8tracks-20260101.log", "n8tracks-20260917.jsonl", "n8tracks-20260918.jsonl", "n8tracks-20261001.jsonl", "n8tracks-old.jsonl", "notes.txt"],
            Names());
    }

    [Fact]
    public void ASweepDeletesTheOldestFirstUntilTheFilesFitUnderTheCap()
    {
        Directory.CreateDirectory(Folder);
        WriteFile("n8tracks-20260929.jsonl", 4 * Megabyte);
        WriteFile("n8tracks-20260930.jsonl", 4 * Megabyte);
        WriteFile("n8tracks-20260930_1.jsonl", 4 * Megabyte);

        using var files = Started();
        Emit(files, "today");
        files.Configure(new LogFileLimits(14, 10));
        var deleted = files.Sweep();

        Assert.Equal(new LogFileDeletion(1, 4 * Megabyte), deleted);
        Assert.Equal(["n8tracks-20260930.jsonl", "n8tracks-20260930_1.jsonl", "n8tracks-20261001.jsonl"], Names());
        Assert.True(TotalBytes() <= 10 * Megabyte);
    }

    [Fact]
    public void TheCapIsNeverPassedByMoreThanOneLineWhateverIsWritten()
    {
        using var files = Started();
        files.Configure(new LogFileLimits(14, 10));
        var line = Text(Megabyte / 2);

        long largest = 0;
        for (var index = 0; index < 60; index++)
        {
            Emit(files, line);
            largest = Math.Max(largest, TotalBytes());
        }

        Assert.True(largest <= 10 * Megabyte, $"The files reached {largest} bytes, over the 10 MB cap.");
        Assert.True(Names().Count > 3, "The files never rolled: the cap was not exercised.");
    }

    [Fact]
    public void NothingIsDeletedBeforeTheSavedLimitsAreInEffect()
    {
        Directory.CreateDirectory(Folder);
        WriteFile("n8tracks-20260101.jsonl", 1);

        using var files = Started();
        Assert.Equal(LogFileDeletion.None, files.Sweep());
        Assert.Contains("n8tracks-20260101.jsonl", Names());

        files.Configure(new LogFileLimits(90, 200));
        Assert.Equal(new LogFileDeletion(1, 1), files.Sweep());
    }

    [Fact]
    public void APreviewCountsWhatTheNewLimitsWouldDeleteBeyondTheCurrentOnesAndDeletesNothing()
    {
        Directory.CreateDirectory(Folder);
        WriteFile("n8tracks-20260911.jsonl", 100);
        WriteFile("n8tracks-20260921.jsonl", 300);
        WriteFile("n8tracks-20260925.jsonl", 500);

        using var files = Started();
        files.Configure(new LogFileLimits(30, 200));

        Assert.Equal(new LogFileDeletion(2, 400), files.Preview(new LogFileLimits(30, 200), new LogFileLimits(7, 200)));
        Assert.Equal(LogFileDeletion.None, files.Preview(new LogFileLimits(30, 200), new LogFileLimits(60, 5120)));
        Assert.Equal(4, Names().Count);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenStopsTheFilesNotTheLogAndIsTriedAgainAtTheNextSweep()
    {
        // A file where the folder should be: the folder cannot be created.
        File.WriteAllText(Folder, "in the way");

        using var files = Started();
        Emit(files, "lost from the files");

        Assert.NotNull(files.Problem);
        Assert.DoesNotContain(data.Path, files.Problem, StringComparison.Ordinal);

        File.Delete(Folder);
        files.Configure(LogFileLimits.Default);
        files.Sweep();
        Emit(files, "written again");

        Assert.Null(files.Problem);
        Assert.Contains("written again", File.ReadAllText(Path.Combine(Folder, "n8tracks-20261001.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void ALogFolderThatIsTheMediaFolderIsNeverWritten()
    {
        // The media folder mounted where the log folder is: the data folder itself is outside it.
        Directory.CreateDirectory(Folder);
        using var files = new FileLogging(data.Path, Folder, clock);
        files.Start();
        Emit(files, "never in the media folder");

        Assert.Contains("media folder", files.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Folder));
    }

    [Fact]
    public void ALogFolderInsideTheMediaFolderIsNeverCreated()
    {
        using var files = new FileLogging(media.Path, media.Path, clock);
        files.Start();
        Emit(files, "never in the media folder");

        Assert.Contains("media folder", files.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(media.Path));
    }

    [Fact]
    public void ALinkUnderALogFilesNameIsNeitherWrittenThroughNorDeleted()
    {
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(media.Path, "song.mp3");
        File.WriteAllText(target, "audio");
        File.CreateSymbolicLink(Path.Combine(Folder, "n8tracks-20261001.jsonl"), target);
        File.CreateSymbolicLink(Path.Combine(Folder, "n8tracks-20260101.jsonl"), target);

        using var files = Started();
        Emit(files, "beside the link");
        files.Configure(new LogFileLimits(1, 10));
        files.Sweep();

        Assert.Equal("audio", File.ReadAllText(target));
        Assert.Contains("beside the link", File.ReadAllText(Path.Combine(Folder, "n8tracks-20261001_1.jsonl")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Folder, "n8tracks-20260101.jsonl")));
    }

    [Fact]
    public void NothingIsWrittenBeforeStartOrWithoutFiles()
    {
        using (var files = new FileLogging(data.Path, media.Path, clock))
        {
            Emit(files, "before the lock is held");
        }

        Assert.False(Directory.Exists(Folder));

        using var off = FileLogging.Off(clock);
        off.Start();
        Emit(off, "no files");
        Assert.Null(off.Folder);
        Assert.Equal(LogFileDeletion.None, off.Sweep());
    }

    [Fact]
    public void ALineIsTheJsonFormattersLine()
    {
        using var files = Started();
        Emit(files, "formatted");

        var line = JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(Path.Combine(Folder, "n8tracks-20261001.jsonl"))));
        Assert.Equal("Information", line.GetProperty("level").GetString());
        Assert.Equal("formatted", line.GetProperty("message").GetString());
    }

    private FileLogging Started()
    {
        var files = new FileLogging(data.Path, media.Path, clock);
        files.Start();
        return files;
    }

    private void Emit(FileLogging files, string text) =>
        files.Emit(new LogEvent(
            clock.GetUtcNow(),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("{Text}"),
            [new LogEventProperty("Text", new ScalarValue(text))]));

    private static string Text(long length) => new('x', (int)length);

    private void WriteFile(string name, long length)
    {
        using var stream = File.Create(Path.Combine(Folder, name));
        stream.SetLength(length);
    }

    private List<string> Names() =>
        [.. Directory.EnumerateFileSystemEntries(Folder).Select(static entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    private long TotalBytes() =>
        Directory.EnumerateFiles(Folder).Sum(static file => new FileInfo(file).Length);
}
