using n8Tracks.Application.Songs;
using n8Tracks.Application.Suno;
using n8Tracks.Domain.Catalog;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>
/// The domain rules of a Version's lineage (#122): each rule and its refusal, named by its code, and
/// the complement (each valid combination accepted, partial work allowed until the set must be complete).
/// </summary>
public sealed class VersionLineageRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoAudioActionsAreRefused()
    {
        var lineage = Lineage(audio: [Audio(SystemRelationshipTypes.Cover), Audio(SystemRelationshipTypes.SampleThisSong)]);

        Assert.Contains(VersionLineageRules.OneAudioAction, Rules(lineage));
    }

    [Theory]
    [InlineData(1, LineageCheck.Write, false)]
    [InlineData(1, LineageCheck.Complete, true)]
    [InlineData(2, LineageCheck.Write, false)]
    [InlineData(2, LineageCheck.Complete, false)]
    [InlineData(3, LineageCheck.Write, true)]
    [InlineData(3, LineageCheck.Complete, true)]
    public void AMashupHasExactlyTwoSourcesOnceComplete(int count, LineageCheck check, bool refused)
    {
        var lineage = Lineage(audio: [.. Enumerable.Range(0, count).Select(index => Audio(SystemRelationshipTypes.Mashup, "mashup-" + index))]);

        Assert.Equal(refused, Rules(lineage, check: check).Contains(VersionLineageRules.AudioSourceCount));
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("extend")]
    [InlineData("sample")]
    [InlineData("reuse_prompt")]
    public void EveryOtherAudioActionHasOneSource(string action)
    {
        var type = SystemRelationshipTypes.All.Single(type => type.SunoAction == action);

        Assert.DoesNotContain(VersionLineageRules.AudioSourceCount, Rules(Lineage(audio: [Audio(type, "one", action == SunoActions.Extend ? 1m : null)]), check: LineageCheck.Complete));
        Assert.Contains(VersionLineageRules.AudioSourceCount, Rules(Lineage(audio: [Audio(type, "one"), Audio(type, "two")])));
    }

    [Fact]
    public void FiveInspirationSourcesAreRefusedAndFourAreAccepted()
    {
        Assert.Contains(VersionLineageRules.InspirationCount, Rules(Lineage(inspiration: [.. Enumerable.Range(0, 5).Select(Inspiration)])));
        Assert.Empty(Rules(Lineage(inspiration: [.. Enumerable.Range(0, 4).Select(Inspiration)])));
    }

    [Fact]
    public void APlaylistTogetherWithIndividualSourcesIsRefused()
    {
        var lineage = Lineage(inspiration: [Inspiration(0)], playlist: Playlist());

        Assert.Contains(VersionLineageRules.InspirationBothForms, Rules(lineage));
        Assert.Empty(Rules(Lineage(playlist: Playlist())));
    }

    [Fact]
    public void InspirationWithCoverIsRefusedInEitherForm()
    {
        Assert.Contains(VersionLineageRules.InspirationWithCover, Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Cover)], inspiration: [Inspiration(0)])));
        Assert.Contains(VersionLineageRules.InspirationWithCover, Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Cover)], playlist: Playlist())));

        // With any other action Inspiration is fine.
        Assert.Empty(Rules(Lineage(audio: [Audio(SystemRelationshipTypes.SampleThisSong)], inspiration: [Inspiration(0)])));
    }

    [Fact]
    public void VoiceAccompaniesInspirationCoverAndEveryAudioAction()
    {
        var voice = new VersionVoice("persona-1", "Persona");

        Assert.Empty(Rules(Lineage(inspiration: [Inspiration(0), Inspiration(1)], voice: voice)));
        Assert.Empty(Rules(Lineage(playlist: Playlist(), voice: voice)));
        foreach (var type in new[] { SystemRelationshipTypes.Cover, SystemRelationshipTypes.Extend, SystemRelationshipTypes.ReusePrompt, SystemRelationshipTypes.SampleThisSong })
        {
            Assert.Empty(Rules(Lineage(audio: [Audio(type, continueAt: type == SystemRelationshipTypes.Extend ? 3m : null)], voice: voice), check: LineageCheck.Complete));
        }

        Assert.Empty(Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Mashup, "a"), Audio(SystemRelationshipTypes.Mashup, "b")], voice: voice), check: LineageCheck.Complete));
        Assert.Contains(VersionLineageRules.PersonaIdInvalid, Rules(Lineage(voice: new VersionVoice(" ", "Blank"))));
        Assert.Contains(VersionLineageRules.NameLength, Rules(Lineage(voice: new VersionVoice("persona", new string('x', 201)))));
    }

    [Theory]
    [InlineData(VersionFileInputKind.Image)]
    [InlineData(VersionFileInputKind.Video)]
    public void AnImageOrVideoIsForASimpleModeSongOnly(VersionFileInputKind kind)
    {
        var lineage = Lineage(files: [new VersionFileInput(kind, "A file")]);

        Assert.Contains(VersionLineageRules.FileInputSimpleOnly, Rules(lineage, songMode: CreationMode.Advanced));
        Assert.Contains(VersionLineageRules.FileInputSimpleOnly, Rules(lineage, kind: VersionKind.Speech, songMode: CreationMode.Simple));
        Assert.Empty(Rules(lineage, songMode: CreationMode.Simple));
    }

    [Fact]
    public void AnAudioFileTakesTheOneAudioSlot()
    {
        var file = new VersionFileInput(VersionFileInputKind.Audio, "My demo recording");

        Assert.Empty(Rules(Lineage(files: [file]), songMode: CreationMode.Advanced));
        Assert.Contains(VersionLineageRules.AudioSlotTaken, Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Cover)], files: [file])));
        Assert.Empty(Rules(
            Lineage(files: [file, new VersionFileInput(VersionFileInputKind.Image, "Cover art"), new VersionFileInput(VersionFileInputKind.Video, "Clip")]),
            songMode: CreationMode.Simple));
        Assert.Contains(VersionLineageRules.FileInputRepeated, Rules(Lineage(files: [file, file])));
        Assert.Contains(VersionLineageRules.FileInputDescription, Rules(Lineage(files: [new VersionFileInput(VersionFileInputKind.Audio, " ")])));
        Assert.Contains(VersionLineageRules.FileInputDescription, Rules(Lineage(files: [new VersionFileInput(VersionFileInputKind.Audio, new string('x', 501))])));
        Assert.Empty(Rules(Lineage(files: [new VersionFileInput(VersionFileInputKind.Audio, new string('x', 500))])));
    }

    [Fact]
    public void ExtendsPositionIsRequiredOnceCompleteAndCheckedWheneverGiven()
    {
        var extend = SystemRelationshipTypes.Extend;
        Assert.Empty(Rules(Lineage(audio: [Audio(extend)])));
        Assert.Contains(VersionLineageRules.ContinueAtRequired, Rules(Lineage(audio: [Audio(extend)]), check: LineageCheck.Complete));
        Assert.Empty(Rules(Lineage(audio: [Audio(extend, continueAt: 0m)]), check: LineageCheck.Complete));
        Assert.Empty(Rules(Lineage(audio: [Audio(extend, continueAt: 61.25m)]), check: LineageCheck.Complete));
        Assert.Contains(VersionLineageRules.ContinueAtInvalid, Rules(Lineage(audio: [Audio(extend, continueAt: -1m)])));
        Assert.Contains(VersionLineageRules.ContinueAtInvalid, Rules(Lineage(audio: [Audio(extend, continueAt: 1.125m)])));
        Assert.Contains(VersionLineageRules.ContinueAtOnlyExtend, Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Cover, continueAt: 5m)])));

        // At most the source's length, when it is known.
        var source = Audio(extend, continueAt: 120.5m);
        var lineage = Lineage(audio: [source]);
        Assert.Contains(VersionLineageRules.ContinueAtBeyondSource, Rules(lineage, durationOf: _ => 120.0));
        Assert.Empty(Rules(lineage, durationOf: _ => 120.5));
        Assert.Empty(Rules(lineage, durationOf: _ => null));
    }

    [Fact]
    public void ASourcePointsAtExactlyOneTarget()
    {
        var none = new VersionSource(SystemRelationshipTypes.Cover.Id, SunoActions.Cover, new VersionSourceTarget(null, null, null));
        var two = new VersionSource(SystemRelationshipTypes.Cover.Id, SunoActions.Cover, new VersionSourceTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), null));

        Assert.Contains(VersionLineageRules.OneTarget, Rules(Lineage(audio: [none])));
        Assert.Contains(VersionLineageRules.OneTarget, Rules(Lineage(audio: [two])));
        Assert.Contains(VersionLineageRules.SunoIdInvalid, Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Cover, "has space")])));
        Assert.Empty(Rules(Lineage(audio: [new VersionSource(SystemRelationshipTypes.Cover.Id, SunoActions.Cover, VersionSourceTarget.OfGeneration(Guid.CreateVersion7()))])));
        Assert.Empty(Rules(Lineage(audio: [new VersionSource(SystemRelationshipTypes.Cover.Id, SunoActions.Cover, VersionSourceTarget.OfSong(Guid.CreateVersion7()))])));
    }

    [Fact]
    public void AnAudioSourcesTypeStandsForAnAudioAction()
    {
        foreach (var type in new[] { SystemRelationshipTypes.UseAsInspiration, SystemRelationshipTypes.Voice, SystemRelationshipTypes.DerivedFrom })
        {
            Assert.Contains(VersionLineageRules.SourceTypeNotAudioAction, Rules(Lineage(audio: [Audio(type)])));
        }

        // An Inspiration source is of the Use as Inspiration type.
        Assert.Contains(VersionLineageRules.InspirationType, Rules(Lineage(inspiration: [Audio(SystemRelationshipTypes.Cover)])));
    }

    [Fact]
    public void RemixSourcesComeOnlyFromAnImportInAnyNumberAndNeverWithAnAction()
    {
        var remixes = Lineage(audio: [.. Enumerable.Range(0, 5).Select(index => Audio(SystemRelationshipTypes.Remix, "remix-" + index))]);

        Assert.Empty(Rules(remixes, origin: LineageOrigin.Import, check: LineageCheck.Complete));
        Assert.Contains(VersionLineageRules.SourceTypeImportOnly, Rules(remixes, origin: LineageOrigin.Edit));
        Assert.Contains(
            VersionLineageRules.RemixWithAudioAction,
            Rules(Lineage(audio: [Audio(SystemRelationshipTypes.Remix), Audio(SystemRelationshipTypes.Cover)]), origin: LineageOrigin.Import));
    }

    [Fact]
    public void APlaylistSnapshotHoldsUpTo500ClipIds()
    {
        Assert.Empty(Rules(Lineage(playlist: new InspirationPlaylist("playlist", "", [.. Enumerable.Range(0, 500).Select(static index => "clip-" + index)]))));
        Assert.Contains(VersionLineageRules.PlaylistClipCount, Rules(Lineage(playlist: new InspirationPlaylist("playlist", "", [.. Enumerable.Range(0, 501).Select(static index => "clip-" + index)]))));
        Assert.Contains(VersionLineageRules.PlaylistIdInvalid, Rules(Lineage(playlist: new InspirationPlaylist("", "Name", []))));
        Assert.Contains(VersionLineageRules.NameLength, Rules(Lineage(playlist: new InspirationPlaylist("playlist", new string('x', 201), []))));
    }

    [Fact]
    public void WhatAppliesFollowsTheKindAndMode()
    {
        Assert.True(VersionLineageRules.Applies(VersionSourceGroup.Audio, VersionKind.Song, CreationMode.Simple));
        Assert.True(VersionLineageRules.Applies(VersionSourceGroup.Inspiration, VersionKind.Song, CreationMode.Advanced));
        Assert.False(VersionLineageRules.Applies(VersionSourceGroup.Inspiration, VersionKind.Song, CreationMode.Simple));
        Assert.False(VersionLineageRules.Applies(VersionSourceGroup.Audio, VersionKind.Speech, CreationMode.Advanced));
        Assert.True(VersionLineageRules.Applies(VersionFileInputKind.Audio, VersionKind.Song, CreationMode.Advanced));
        Assert.False(VersionLineageRules.Applies(VersionFileInputKind.Image, VersionKind.Song, CreationMode.Advanced));
        Assert.True(VersionLineageRules.Applies(VersionFileInputKind.Video, VersionKind.Song, CreationMode.Simple));
        Assert.False(VersionLineageRules.AppliesToSong(VersionKind.Sound));
    }

    [Fact]
    public void ALineageChangesOnlyOnAMutableVersionAndAnEqualOneIsNoChange()
    {
        var mutable = new SongVersion(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", null, null, VersionVisibility.Active, "", "",
            VersionInputRules.Defaults(CreateFieldInventory.Embedded, "Lineage"), Now, Now, 1, Inventory.LineageValues.Held);
        var frozen = mutable.AttachGeneration(Guid.CreateVersion7(), Now).Version;

        // The same lineage, built afresh, is equal (lists item by item) and allowed even when frozen.
        var same = new VersionLineage(
            [.. Inventory.LineageValues.Held.AudioSources],
            [],
            new InspirationPlaylist("held-playlist", "Held", ["held-1", "held-2"]),
            new VersionVoice("held-persona", "Held"),
            [new VersionFileInput(VersionFileInputKind.Image, "Held image")]);
        Assert.Equal(Inventory.LineageValues.Held, same);
        Assert.Same(frozen, frozen.WithLineage(same));

        foreach (var (part, change) in Inventory.LineageValues.EachPartChanged)
        {
            var changed = change(mutable.Lineage);
            Assert.NotEqual(mutable.Lineage, changed);
            Assert.Equal(changed, mutable.WithLineage(changed).Lineage);
            Assert.Throws<VersionFrozenException>(() => frozen.WithLineage(changed));
            Assert.True(part.Length > 0);
        }
    }

    private static IReadOnlyList<string> Rules(
        VersionLineage lineage,
        VersionKind kind = VersionKind.Song,
        CreationMode songMode = CreationMode.Advanced,
        LineageCheck check = LineageCheck.Write,
        LineageOrigin origin = LineageOrigin.Edit,
        Func<VersionSourceTarget, double?>? durationOf = null) =>
        [.. VersionLineageRules.Errors(lineage, kind, songMode, check, origin, durationOf).Select(static error => error.Rule)];

    private static VersionLineage Lineage(
        IReadOnlyList<VersionSource>? audio = null,
        IReadOnlyList<VersionSource>? inspiration = null,
        InspirationPlaylist? playlist = null,
        VersionVoice? voice = null,
        IReadOnlyList<VersionFileInput>? files = null) =>
        new(audio ?? [], inspiration ?? [], playlist, voice, files ?? []);

    private static VersionSource Audio(RelationshipType type, string sunoId = "source-clip", decimal? continueAt = null) =>
        new(type.Id, type.SunoAction, VersionSourceTarget.OfExternal(sunoId), continueAt);

    private static VersionSource Inspiration(int index) =>
        new(SystemRelationshipTypes.UseAsInspiration.Id, SunoActions.Inspiration, VersionSourceTarget.OfExternal("inspiration-" + index));

    private static InspirationPlaylist Playlist() => new("playlist-1", "Road trip", ["clip-1", "clip-2"]);
}
