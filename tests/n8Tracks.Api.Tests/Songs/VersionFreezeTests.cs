using n8Tracks.Application.References;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// The freeze rule inside the Version entity: once a Generation is attached, every method that would
/// change a creation input throws, and the metadata stays editable. Generation ordinals and shortcodes.
/// </summary>
public sealed class VersionFreezeTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnAMutableVersionEveryInputChangingMethodSucceeds()
    {
        var version = Mutable();

        Assert.False(version.IsFrozen);
        version.EnsureMutable();
        var changed = version.WithInputs("[Chorus]\nNew", "new style");
        Assert.Equal("[Chorus]\nNew", changed.Lyrics);
        Assert.Equal("new style", changed.Styles);

        // The revision and updated time are the store's to move; nothing else changes.
        Assert.Equal(version.Revision, changed.Revision);
        Assert.Equal(version.UpdatedUtc, changed.UpdatedUtc);
        Assert.Equal(version.Name, changed.Name);
        Assert.False(changed.IsFrozen);
    }

    [Fact]
    public void OnAFrozenVersionEveryInputChangingMethodThrows()
    {
        var frozen = Frozen();

        Assert.True(frozen.IsFrozen);
        var ensure = Assert.Throws<VersionFrozenException>(frozen.EnsureMutable);
        Assert.Equal(frozen.Id, ensure.VersionId);
        Assert.Contains("Create a new Version from it", ensure.Message, StringComparison.Ordinal);
        Assert.Throws<VersionFrozenException>(() => frozen.WithInputs("changed", frozen.Styles));
        Assert.Throws<VersionFrozenException>(() => frozen.WithInputs(frozen.Lyrics, "changed"));
        Assert.Throws<VersionFrozenException>(() => frozen.WithInputs(string.Empty, string.Empty));

        // Byte for byte: a different line ending or a trailing space is a change.
        Assert.Throws<VersionFrozenException>(() => frozen.WithInputs(frozen.Lyrics.Replace("\n", "\r\n", StringComparison.Ordinal), frozen.Styles));
        Assert.Throws<VersionFrozenException>(() => frozen.WithInputs(frozen.Lyrics, frozen.Styles + " "));
    }

    [Fact]
    public void UnchangedInputsAreNoChangeAndAllowedOnAFrozenVersion()
    {
        var frozen = Frozen();

        Assert.Same(frozen, frozen.WithInputs(new string(frozen.Lyrics.AsSpan()), new string(frozen.Styles.AsSpan())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameNotesAndVisibilityChangeWhetherOrNotTheVersionIsFrozen(bool frozen)
    {
        var version = frozen ? Frozen() : Mutable();

        var changed = version.WithAnnotations("Renamed", "New notes", VersionVisibility.Archived);

        Assert.Equal("Renamed", changed.Name);
        Assert.Equal("New notes", changed.Notes);
        Assert.Equal(VersionVisibility.Archived, changed.Visibility);
        Assert.Equal(version.Lyrics, changed.Lyrics);
        Assert.Equal(version.Styles, changed.Styles);
        Assert.Equal(frozen, changed.IsFrozen);
        Assert.Equal(VersionVisibility.Active, changed.WithAnnotations(null, null, VersionVisibility.Active).Visibility);
    }

    [Fact]
    public void AttachingAGenerationFreezesTheVersionAndRaisesItsRevision()
    {
        var version = Mutable();
        var generationId = Guid.CreateVersion7();

        var (frozen, generation) = version.AttachGeneration(generationId, Now);

        Assert.True(frozen.IsFrozen);
        Assert.Equal(1, frozen.LastGenerationOrdinal);
        Assert.Equal(version.Revision + 1, frozen.Revision);
        Assert.Equal(Now, frozen.UpdatedUtc);
        Assert.Equal(version.Lyrics, frozen.Lyrics);
        Assert.Equal(version.Styles, frozen.Styles);
        Assert.Equal(version.Name, frozen.Name);
        Assert.Equal(new Generation(generationId, version.Id, version.SongId, 1, Now), generation);

        // The original is untouched: it is a value.
        Assert.False(version.IsFrozen);
    }

    [Fact]
    public void OrdinalsGoUpByOneFromTheLastEverGivenAndTheFreezeStays()
    {
        var (once, first) = Mutable().AttachGeneration(Guid.CreateVersion7(), Now);
        var (twice, second) = once.AttachGeneration(Guid.CreateVersion7(), Now);

        Assert.Equal(1, first.Ordinal);
        Assert.Equal(2, second.Ordinal);
        Assert.Equal(2, twice.LastGenerationOrdinal);
        Assert.True(twice.IsFrozen);

        // A Version read back after its Generations were removed still knows the last ordinal, so the
        // next one is never a reused one, and a recorded ordinal means frozen whatever the flag says.
        var reread = new SongVersion(
            twice.Id, twice.SongId, twice.Number, twice.Name, twice.Notes, twice.Visibility, twice.Lyrics, twice.Styles,
            twice.CreatedUtc, twice.UpdatedUtc, twice.Revision, IsFrozen: false, LastGenerationOrdinal: 2);
        Assert.True(reread.IsFrozen);
        Assert.Equal(3, reread.AttachGeneration(Guid.CreateVersion7(), Now).Generation.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongVersion(
            twice.Id, twice.SongId, twice.Number, null, null, VersionVisibility.Active, string.Empty, string.Empty,
            Created, Created, 1, LastGenerationOrdinal: -1));
    }

    [Fact]
    public void ANewVersionFromAFrozenOneIsMutableAndHoldsTheSameInputs()
    {
        var frozen = Frozen();

        var branch = VersionRules.CreateFrom(frozen, Guid.CreateVersion7(), VersionNumber.Parse("1.1"), null, Now);

        Assert.False(branch.IsFrozen);
        Assert.Equal(0, branch.LastGenerationOrdinal);
        Assert.Equal(frozen.Lyrics, branch.Lyrics);
        Assert.Equal(frozen.Styles, branch.Styles);
        Assert.Equal("edited", branch.WithInputs("edited", branch.Styles).Lyrics);
    }

    [Theory]
    [InlineData("n8-12-v1.1-g3", 12, "1.1", 3)]
    [InlineData("N8-12-V1-G1", 12, "1", 1)]
    [InlineData("n8-1-v2.10.3-g2147483647", 1, "2.10.3", int.MaxValue)]
    public void AGenerationShortcodeIsReadInAnyCase(string text, long song, string number, int ordinal)
    {
        Assert.True(Shortcodes.TryParseGeneration(text, out var songNumber, out var versionNumber, out var parsedOrdinal));
        Assert.Equal(song, songNumber);
        Assert.Equal(number, versionNumber.ToString());
        Assert.Equal(ordinal, parsedOrdinal);

        var reference = CatalogReference.Parse(text);
        Assert.Equal(ReferenceKind.Generation, reference.Kind);
        Assert.Equal(song, reference.SongShortcodeNumber);
        Assert.Equal(number, reference.VersionNumber!.ToString());
        Assert.Equal(ordinal, reference.GenerationOrdinal);
        Assert.Equal(text.ToLowerInvariant(), Shortcodes.ForGeneration(song, number, ordinal));
    }

    [Theory]
    [InlineData("n8-12-v1.1-g")]
    [InlineData("n8-12-v1.1-g0")]
    [InlineData("n8-12-v1.1-g01")]
    [InlineData("n8-12-v1.1-g2147483648")]
    [InlineData("n8-12-v1.1-g-1")]
    [InlineData("n8-12-v1.1-g1 ")]
    [InlineData("n8-12-g1")]
    [InlineData("n8-12-v-g1")]
    [InlineData("-g1")]
    [InlineData("n8-12-v1.1-g1-g2")]
    public void AnythingElseIsNoGenerationShortcode(string text)
    {
        Assert.False(Shortcodes.TryParseGeneration(text, out _, out _, out _));
        Assert.NotEqual(ReferenceKind.Generation, CatalogReference.Parse(text).Kind);
    }

    [Fact]
    public void AVersionShortcodeIsStillAVersionAndAGenerationShortcodeIsNot()
    {
        Assert.Equal(ReferenceKind.Version, CatalogReference.Parse("n8-12-v1.1").Kind);
        Assert.False(Shortcodes.TryParseVersion("n8-12-v1.1-g3", out _, out _));
    }

    private static SongVersion Mutable() =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "1",
            "First take",
            "Too slow.",
            VersionVisibility.Active,
            "[Verse]\nRun",
            "punk, fast",
            Created,
            Created,
            Revision: 3);

    private static SongVersion Frozen() => Mutable().AttachGeneration(Guid.CreateVersion7(), Now).Version;
}
