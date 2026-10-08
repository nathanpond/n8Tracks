using n8Tracks.Api.Configuration;
using n8Tracks.Application.Configuration;

namespace n8Tracks.Api.Tests.Media;

/// <summary>
/// The startup check of invariant 2 (#387): a data or backup folder that is the media folder or lies
/// inside it is refused before anything is written there, by real path and, on Linux, by where it
/// really is in the mount table, so one host folder mounted at two places is seen. The media folder
/// inside the data folder (the AppHost's own layout) is allowed: n8Tracks writes only its own named
/// files there.
/// </summary>
public sealed class MediaFolderOverlapTests : IDisposable
{
    /// <summary>The mount table of a container with one host folder at /media (read-only), its subfolder at /backup, and a volume at /data.</summary>
    private const string DockerMountInfo =
        "189 170 0:40 / / rw,relatime master:1 - overlay overlay rw\n"
        + "198 189 0:46 /host/music/sub /backup rw,nosuid,nodev,relatime - virtiofs virtiofs2 rw\n"
        + "199 189 254:1 /docker/volumes/data/_data /data rw,relatime master:2 - ext4 /dev/vda1 rw\n"
        + "200 189 0:46 /host/music /media ro,nosuid,nodev,relatime - virtiofs virtiofs2 rw\n"
        + "201 189 0:46 /host/my\\040music /spaced ro,relatime - virtiofs virtiofs2 rw\n";

    private readonly TemporaryDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Theory]
    [InlineData("/media", "0:46", "/host/music")]
    [InlineData("/media/album/a.mp3", "0:46", "/host/music/album/a.mp3")]
    [InlineData("/backup", "0:46", "/host/music/sub")]
    [InlineData("/data/n8tracks.db", "254:1", "/docker/volumes/data/_data/n8tracks.db")]
    [InlineData("/etc/hostname", "0:40", "/etc/hostname")]
    [InlineData("/spaced", "0:46", "/host/my music")]
    [InlineData("/mediax", "0:40", "/mediax")]
    public void TheMountTableTellsWhereAPathReallyIs(string path, string device, string inFilesystem)
    {
        Assert.Equal((device, inFilesystem), MediaFolderOverlap.Locate(path, DockerMountInfo));
    }

    [Fact]
    public void OneHostFolderMountedTwiceIsSeenAndSeparateFoldersAreNot()
    {
        var media = MediaFolderOverlap.Locate("/media", DockerMountInfo)!.Value;
        var backup = MediaFolderOverlap.Locate("/backup", DockerMountInfo)!.Value;
        var data = MediaFolderOverlap.Locate("/data", DockerMountInfo)!.Value;

        Assert.Equal(media.Device, backup.Device);
        Assert.True(MediaFolderOverlap.Within(backup.Path, media.Path, StringComparison.Ordinal));
        Assert.NotEqual(media.Device, data.Device);
        Assert.False(MediaFolderOverlap.Within("/host/musical", "/host/music", StringComparison.Ordinal));
        Assert.True(MediaFolderOverlap.Within("/anything", "/", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderInsideTheMediaFolderIsSeenByItsRealPathThroughLinksToo()
    {
        var media = Folder("music");
        var inside = Folder("music/sub");
        var sibling = Folder("elsewhere");
        var link = Path.Combine(directory.Path, "link-to-sub");
        Directory.CreateSymbolicLink(link, inside);

        Assert.True(MediaFolderOverlap.IsInside(media, media));
        Assert.True(MediaFolderOverlap.IsInside(inside, media));
        Assert.True(MediaFolderOverlap.IsInside(link, media));
        Assert.True(MediaFolderOverlap.IsInside(inside + Path.DirectorySeparatorChar, media));
        Assert.False(MediaFolderOverlap.IsInside(sibling, media));
        Assert.False(MediaFolderOverlap.IsInside(media, inside));

        // A folder that is not there holds nothing, and a media folder that is not there has nothing to protect.
        Assert.False(MediaFolderOverlap.IsInside(Path.Combine(media, "absent"), media));
        Assert.False(MediaFolderOverlap.IsInside(inside, Path.Combine(directory.Path, "absent")));
    }

    [Fact]
    public void ADataFolderInsideTheMediaFolderIsRefusedBeforeAnythingIsWrittenThere()
    {
        var media = Folder("music");
        var data = Folder("music/n8tracks");

        var error = Assert.Single(Assert.Throws<ConfigurationValidationException>(() => Load(data, media, backup: null)).Errors);

        Assert.Equal("N8TRACKS_DATA_PATH", error.Variable);
        Assert.Contains("inside the media folder", error.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(data));
    }

    [Fact]
    public void ABackupFolderThatIsOrIsInsideTheMediaFolderIsRefused()
    {
        var media = Folder("music");
        var data = Folder("data");

        foreach (var backup in new[] { media, Folder("music/backups") })
        {
            var error = Assert.Single(Assert.Throws<ConfigurationValidationException>(() => Load(data, media, backup)).Errors);
            Assert.Equal("N8TRACKS_BACKUP_PATH", error.Variable);
            Assert.Contains("inside the media folder", error.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SeparateFoldersAndTheMediaFolderInsideTheDataFolderAreAllowed()
    {
        var data = Folder("data");
        var options = Load(data, Folder("data/media"), Folder("backup"));

        Assert.Equal(data, options.DataPath);

        // A backup folder that is not mounted is not checked: there is nothing there.
        Assert.Equal(Path.Combine(directory.Path, "absent"), Load(Folder("data2"), Folder("music"), Path.Combine(directory.Path, "absent")).BackupPath);
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(directory.Path, relative)).FullName;

    private N8TracksOptions Load(string data, string media, string? backup)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["N8TRACKS_DATA_PATH"] = data,
            ["N8TRACKS_MEDIA_PATH"] = media,
        };
        if (backup is not null)
        {
            variables["N8TRACKS_BACKUP_PATH"] = backup;
        }

        return EnvironmentOptionsLoader.Load(new EnvironmentSnapshot(variables, directory.Path));
    }
}
