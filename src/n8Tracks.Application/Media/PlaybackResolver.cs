using n8Tracks.Domain.Media;
using n8Tracks.Domain.Songs;

namespace n8Tracks.Application.Media;

/// <summary>Why a Generation or a Song resolves to the file it does, or to nothing (#212).</summary>
public enum PlaybackReason
{
    /// <summary>The Song's preferred Song-level file is available.</summary>
    SongPreferred,

    /// <summary>The Generation's preferred file is available.</summary>
    GenerationPreferred,

    /// <summary>No file was chosen: the highest-ranked available file (<see cref="AudioFormats.CompareByRank"/>).</summary>
    FormatOrder,

    /// <summary>The chosen file is Missing: the highest-ranked available alternative plays instead.</summary>
    PreferredMissingFallback,

    /// <summary>The chosen file is Unavailable (the media folder cannot be read): the highest-ranked available alternative plays instead.</summary>
    PreferredUnavailableFallback,

    /// <summary>The Song has no available preferred file and no Selected Generation.</summary>
    NoSelectedGeneration,

    /// <summary>No file that could play is available.</summary>
    NothingAvailable,

    /// <summary>The Song has no Generations and no available Song-level file (#219).</summary>
    NoGenerations,
}

/// <summary>
/// What pressing Play on a Song does (#219): <see cref="Ready"/> plays the Song's choice (its preferred
/// Song-level file, or its Selected Generation's file); <see cref="NeedsChoice"/> asks which Generation
/// or Song-level file to play, since nothing is selected; <see cref="SelectedUnplayable"/> says the
/// Selected Generation has nothing to play (the user chose it, so nothing else is offered);
/// <see cref="None"/> has nothing to offer, and only it disables Play.
/// </summary>
public enum SongPlaybackState
{
    Ready,
    NeedsChoice,
    SelectedUnplayable,
    None,
}

/// <summary>The codes of <see cref="SongPlaybackState"/>, as the API answers them.</summary>
public static class SongPlaybackStates
{
    public static string Text(SongPlaybackState state) => state switch
    {
        SongPlaybackState.Ready => "ready",
        SongPlaybackState.NeedsChoice => "needs-choice",
        SongPlaybackState.SelectedUnplayable => "selected-unplayable",
        SongPlaybackState.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Song playback state."),
    };
}

/// <summary>
/// What pressing Play on a Song does (#219), without naming the file: its <see cref="State"/>, and
/// <see cref="Reason"/>, null when it is ready, and why not otherwise. Song lists carry it, so each
/// row's Play control knows whether it is disabled without asking per row.
/// </summary>
public sealed record SongPlayability(SongPlaybackState State, PlaybackReason? Reason)
{
    /// <summary>A Song with no Generations and no files: nothing to play.</summary>
    public static SongPlayability NoGenerations { get; } = new(SongPlaybackState.None, PlaybackReason.NoGenerations);
}

/// <summary>
/// One file of a Song, as far as the Song's <see cref="SongPlayability"/> needs it: the Generation it
/// belongs to (null at Song level), how it reports now, and whether it is its owner's preferred file.
/// </summary>
public readonly record struct SongFileFact(Guid? GenerationId, AudioFileReportedStatus Status, bool Preferred);

/// <summary>
/// One of a Song's Generations as the chooser lists it (#219): what it is called, its Version number,
/// the user's rating, Suno's duration, its states, and its Suno ID (for Open in Suno), in the Song's
/// Version tree order, then ordinal.
/// </summary>
public sealed record SongPlaybackGeneration(
    Guid Id,
    string Shortcode,
    string VersionNumber,
    int? Rating,
    double? DurationSeconds,
    GenerationState State,
    GenerationRemoteState RemoteState,
    string? SunoId)
{
    /// <summary>Active and still listed by Suno: listed first by the chooser; the rest follow with a badge.</summary>
    public bool IsCurrent => State == GenerationState.Active && RemoteState == GenerationRemoteState.Present;
}

/// <summary>What a chooser candidate is (#219): one of the Song's Generations, or one of its Song-level files.</summary>
public enum SongPlaybackCandidateKind
{
    Generation,
    File,
}

/// <summary>
/// One entry of the chooser (#219): a Generation (<see cref="Generation"/>, with whether it can play
/// and why not, as <see cref="PlaybackResolver.PlayabilityOf"/> decides) or an available Song-level
/// file (<see cref="SongFile"/>, always playable).
/// </summary>
public sealed record SongPlaybackCandidate(
    SongPlaybackCandidateKind Kind,
    SongPlaybackGeneration? Generation,
    ReportedAudioFile? SongFile,
    GenerationPlayability Playability);

/// <summary>
/// The whole answer to Play on a Song (#219): what plays (<see cref="Played"/>, by
/// <see cref="PlaybackResolver.ForSong"/>), the <see cref="Playability"/> state, the chooser's
/// <see cref="Candidates"/> (only when it needs a choice; empty otherwise), and the Selected
/// Generation when it has one.
/// </summary>
public sealed record SongPlaybackChoice(
    SongPlayback Played,
    SongPlayability Playability,
    IReadOnlyList<SongPlaybackCandidate> Candidates,
    SongPlaybackGeneration? Selected)
{
    /// <summary>Why it plays what it does, or, when nothing plays, why not: the state's reason (no Generations, nothing available, no selection).</summary>
    public PlaybackReason Reason => Played.Played is null && Playability.Reason is { } why ? why : Played.Reason;
}

/// <summary>The codes of <see cref="PlaybackReason"/>, as the API answers them.</summary>
public static class PlaybackReasons
{
    public static string Text(PlaybackReason reason) => reason switch
    {
        PlaybackReason.SongPreferred => "song_preferred",
        PlaybackReason.GenerationPreferred => "generation_preferred",
        PlaybackReason.FormatOrder => "format_order",
        PlaybackReason.PreferredMissingFallback => "preferred_missing_fallback",
        PlaybackReason.PreferredUnavailableFallback => "preferred_unavailable_fallback",
        PlaybackReason.NoSelectedGeneration => "no_selected_generation",
        PlaybackReason.NothingAvailable => "nothing_available",
        PlaybackReason.NoGenerations => "no_generations",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown playback reason."),
    };
}

/// <summary>What plays for a Generation: a file (<see cref="Played"/>), or none, and why.</summary>
public sealed record GenerationPlayback(ReportedAudioFile? Played, PlaybackReason Reason);

/// <summary>
/// What plays for a Song: a file (<see cref="Played"/>), or none, and why; <see cref="GenerationId"/> is the Generation the
/// file came from (the Selected Generation), or null for the Song's own preferred file or nothing.
/// </summary>
public sealed record SongPlayback(ReportedAudioFile? Played, PlaybackReason Reason, Guid? GenerationId);

/// <summary>
/// Whether a Generation has something to play (#218), without naming the file: <see cref="Reason"/> is
/// null when it has, and why not otherwise. The list answers carry it so each row's Play control knows
/// whether it can play without asking per row.
/// </summary>
public sealed record GenerationPlayability(bool Playable, PlaybackReason? Reason)
{
    /// <summary>A Generation with no file that could play.</summary>
    public static GenerationPlayability NothingAvailable { get; } = new(false, PlaybackReason.NothingAvailable);

    /// <summary>A Generation that plays a file.</summary>
    public static GenerationPlayability Available { get; } = new(true, null);
}

/// <summary>Whether a file of a Song's list plays now for its Generation, and for the Song (#212).</summary>
public sealed record PlaybackMarks(bool PlaysForGeneration, bool PlaysForSong);

/// <summary>
/// The one rule from a Song or a Generation to the local file that plays for it (#212); every part of
/// the product asks it (the playback reads, the Audio Files lists, the player). It reads the files as
/// they report now (<see cref="ReportedAudioFile"/>) and the choices they carry
/// (<see cref="AudioFile.Preferred"/>), and decides nothing else, so the same input always resolves
/// to the same file.
/// <list type="bullet">
/// <item>A Generation plays its preferred file while that file is available. When the preferred file
/// is Missing or Unavailable, or nothing was chosen, it plays its highest-ranked available file: WAV,
/// M4A, MP3, FLAC, OGG, Opus, AAC (<see cref="AudioFormats.CompareByRank"/>), the earliest first seen
/// among files of one format, then the path. The choice is never changed by falling back, so the
/// preferred file plays again once it is back.</item>
/// <item>A Song plays its preferred Song-level file while it is available, even when it has a Selected
/// Generation; otherwise its Selected Generation's file (any state of that Generation), never another
/// Song-level file (other Song-level files are other masters, not equivalents); otherwise nothing local.</item>
/// <item>Every scanned format counts as playable; one the browser cannot decode is the player's error.</item>
/// </list>
/// </summary>
public static class PlaybackResolver
{
    /// <summary>What plays for a Generation, given every file associated with it.</summary>
    public static GenerationPlayback ForGeneration(IReadOnlyCollection<ReportedAudioFile> generationFiles)
    {
        ArgumentNullException.ThrowIfNull(generationFiles);

        var preferred = generationFiles.FirstOrDefault(static file => Recorded(file).Preferred);
        if (preferred is not null && IsAvailable(preferred))
        {
            return new GenerationPlayback(preferred, PlaybackReason.GenerationPreferred);
        }

        var best = BestAvailable(generationFiles);
        if (best is null)
        {
            return new GenerationPlayback(null, PlaybackReason.NothingAvailable);
        }

        return new GenerationPlayback(best, preferred is null ? PlaybackReason.FormatOrder : FallbackOf(preferred));
    }

    /// <summary>
    /// Whether a Generation whose files report <paramref name="generationFileStatuses"/> plays a file:
    /// exactly when <see cref="ForGeneration"/> would name one, which is when one of them is available
    /// (a preferred file plays only while available, and otherwise the best available one does).
    /// </summary>
    public static GenerationPlayability PlayabilityOf(IEnumerable<AudioFileReportedStatus> generationFileStatuses)
    {
        ArgumentNullException.ThrowIfNull(generationFileStatuses);

        return generationFileStatuses.Any(static status => status == AudioFileReportedStatus.Available)
            ? GenerationPlayability.Available
            : GenerationPlayability.NothingAvailable;
    }

    /// <summary>
    /// What plays for a Song, given every file associated with it (Song-level and through its
    /// Generations) and its Selected Generation, if any.
    /// </summary>
    public static SongPlayback ForSong(IReadOnlyCollection<ReportedAudioFile> songFiles, Guid? selectedGenerationId)
    {
        ArgumentNullException.ThrowIfNull(songFiles);

        var preferred = songFiles.FirstOrDefault(static file => Recorded(file) is { Preferred: true, Link.Generation: null });
        if (preferred is not null && IsAvailable(preferred))
        {
            return new SongPlayback(preferred, PlaybackReason.SongPreferred, null);
        }

        if (selectedGenerationId is not { } selected)
        {
            return new SongPlayback(null, PlaybackReason.NoSelectedGeneration, null);
        }

        var generation = ForGeneration([.. songFiles.Where(file => GenerationIdOf(file) == selected)]);
        if (generation.Played is null)
        {
            return new SongPlayback(null, PlaybackReason.NothingAvailable, selected);
        }

        return new SongPlayback(generation.Played, preferred is null ? generation.Reason : FallbackOf(preferred), selected);
    }

    /// <summary>
    /// What Play on a Song does (#219), from its files (<see cref="SongFileFact"/>), its Selected
    /// Generation, and whether it has any Generation; it agrees with <see cref="ForSong"/> on what plays:
    /// <list type="bullet">
    /// <item>Ready when the preferred Song-level file is available, or the Selected Generation has a
    /// file to play.</item>
    /// <item>Selected-unplayable when the Selected Generation has nothing to play: the user chose it,
    /// so nothing else is offered.</item>
    /// <item>With no Selected Generation, needs-choice when any Song-level file is available or any
    /// Generation has a file to play (<see cref="PlayabilityOf"/>): n8Tracks never picks one itself.</item>
    /// <item>Otherwise none: <see cref="PlaybackReason.NoGenerations"/> without Generations,
    /// <see cref="PlaybackReason.NothingAvailable"/> with them.</item>
    /// </list>
    /// </summary>
    public static SongPlayability StateOfSong(IReadOnlyCollection<SongFileFact> songFiles, Guid? selectedGenerationId, bool hasGenerations)
    {
        ArgumentNullException.ThrowIfNull(songFiles);

        if (songFiles.Any(static file => file is { GenerationId: null, Preferred: true, Status: AudioFileReportedStatus.Available }))
        {
            return new SongPlayability(SongPlaybackState.Ready, null);
        }

        if (selectedGenerationId is { } selected)
        {
            return PlayabilityOf(songFiles.Where(file => file.GenerationId == selected).Select(static file => file.Status)).Playable
                ? new SongPlayability(SongPlaybackState.Ready, null)
                : new SongPlayability(SongPlaybackState.SelectedUnplayable, PlaybackReason.NothingAvailable);
        }

        var anySongLevel = songFiles.Any(static file => file is { GenerationId: null, Status: AudioFileReportedStatus.Available });
        var anyGeneration = songFiles
            .Where(static file => file.GenerationId is not null)
            .GroupBy(static file => file.GenerationId)
            .Any(static group => PlayabilityOf(group.Select(static file => file.Status)).Playable);
        if (anySongLevel || anyGeneration)
        {
            return new SongPlayability(SongPlaybackState.NeedsChoice, PlaybackReason.NoSelectedGeneration);
        }

        return new SongPlayability(SongPlaybackState.None, hasGenerations ? PlaybackReason.NothingAvailable : PlaybackReason.NoGenerations);
    }

    /// <summary>
    /// The whole answer to Play on a Song (#219), given every file associated with it, its Generations
    /// in Version tree order then ordinal, and its Selected Generation: what plays
    /// (<see cref="ForSong"/>), the state (<see cref="StateOfSong"/>), and, when it needs a choice,
    /// the chooser's candidates: the available Song-level files first (in <see cref="BestAvailable"/>
    /// order), then the Active Generations still listed by Suno, then the others (Archived, in Suno's
    /// Trash, or no longer listed), each with whether it can play.
    /// </summary>
    public static SongPlaybackChoice ChoiceForSong(
        IReadOnlyCollection<ReportedAudioFile> songFiles,
        IReadOnlyList<SongPlaybackGeneration> generations,
        Guid? selectedGenerationId)
    {
        ArgumentNullException.ThrowIfNull(songFiles);
        ArgumentNullException.ThrowIfNull(generations);

        var played = ForSong(songFiles, selectedGenerationId);
        var playability = StateOfSong([.. songFiles.Select(FactOf)], selectedGenerationId, generations.Count > 0);
        var selected = selectedGenerationId is { } id ? generations.FirstOrDefault(generation => generation.Id == id) : null;
        if (playability.State != SongPlaybackState.NeedsChoice)
        {
            return new SongPlaybackChoice(played, playability, [], selected);
        }

        var files = InRankOrder(songFiles.Where(static file => GenerationIdOf(file) is null && IsAvailable(file)))
            .Select(static file => new SongPlaybackCandidate(SongPlaybackCandidateKind.File, null, file, GenerationPlayability.Available));
        var statuses = songFiles
            .Where(static file => GenerationIdOf(file) is not null)
            .ToLookup(static file => GenerationIdOf(file)!.Value, static file => file.Status);
        var ofGenerations = generations
            .Where(static generation => generation.IsCurrent)
            .Concat(generations.Where(static generation => !generation.IsCurrent))
            .Select(generation => new SongPlaybackCandidate(SongPlaybackCandidateKind.Generation, generation, null, PlayabilityOf(statuses[generation.Id])));
        return new SongPlaybackChoice(played, playability, [.. files, .. ofGenerations], selected);
    }

    /// <summary>What <see cref="StateOfSong"/> needs of one file of a Song's list.</summary>
    public static SongFileFact FactOf(ReportedAudioFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new SongFileFact(GenerationIdOf(file), file.Status, Recorded(file).Preferred);
    }

    /// <summary>
    /// The marks of each file of a Song's list: whether it plays for its Generation (each Generation
    /// resolved on its own), and whether it plays for the Song. Song-level files never play for a
    /// Generation.
    /// </summary>
    public static IReadOnlyList<ReportedAudioFile> Mark(IReadOnlyList<ReportedAudioFile> songFiles, Guid? selectedGenerationId)
    {
        ArgumentNullException.ThrowIfNull(songFiles);

        var forSong = IdOf(ForSong(songFiles, selectedGenerationId).Played);
        var forGenerations = songFiles
            .Where(static file => GenerationIdOf(file) is not null)
            .GroupBy(static file => GenerationIdOf(file))
            .Select(group => IdOf(ForGeneration([.. group]).Played))
            .OfType<Guid>()
            .ToHashSet();
        return [.. songFiles.Select(file => file with { Marks = new PlaybackMarks(forGenerations.Contains(Recorded(file).Id), forSong == Recorded(file).Id) })];
    }

    /// <summary>The highest-ranked available file: by format rank, then earliest first seen, then path (ordinal); null when none is available.</summary>
    public static ReportedAudioFile? BestAvailable(IEnumerable<ReportedAudioFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return InRankOrder(files.Where(IsAvailable)).FirstOrDefault();
    }

    /// <summary>By format rank, then earliest first seen, then path (ordinal).</summary>
    private static IOrderedEnumerable<ReportedAudioFile> InRankOrder(IEnumerable<ReportedAudioFile> files) =>
        files
            .OrderBy(static file => Recorded(file).Format, Comparer<string>.Create(AudioFormats.CompareByRank))
            .ThenBy(static file => Recorded(file).FirstSeenUtc)
            .ThenBy(static file => Recorded(file).Path, StringComparer.Ordinal);

    /// <summary>The Generation a file of a Song's list belongs to, or null for a Song-level file.</summary>
    public static Guid? GenerationIdOf(ReportedAudioFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return Recorded(file).Link?.Generation?.Id;
    }

    /// <summary>The file's record (named apart, so member chains stay readable).</summary>
    private static AudioFile Recorded(ReportedAudioFile reported) => reported.File;

    private static Guid? IdOf(ReportedAudioFile? reported) => reported is null ? null : Recorded(reported).Id;

    private static bool IsAvailable(ReportedAudioFile file) => file.Status == AudioFileReportedStatus.Available;

    private static PlaybackReason FallbackOf(ReportedAudioFile preferred) =>
        preferred.Status == AudioFileReportedStatus.Unavailable ? PlaybackReason.PreferredUnavailableFallback : PlaybackReason.PreferredMissingFallback;
}
