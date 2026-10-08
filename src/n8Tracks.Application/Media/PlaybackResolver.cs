using n8Tracks.Domain.Media;

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

        return files
            .Where(IsAvailable)
            .OrderBy(static file => Recorded(file).Format, Comparer<string>.Create(AudioFormats.CompareByRank))
            .ThenBy(static file => Recorded(file).FirstSeenUtc)
            .ThenBy(static file => Recorded(file).Path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

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
