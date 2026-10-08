namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>generation_preferred_audio_files</c> (#212): the audio file the user chose to play for
/// a Generation. One per Generation at most (its key). The file must be associated with that Generation:
/// a composite foreign key <c>(audio_file_id, generation_id)</c> to <c>audio_files (id, generation_id)</c>,
/// written by hand in the migration, refuses anything else, and refuses changing the file's association
/// while the row names it. Kept apart from <c>generations</c>, a retained table with triggers, so neither
/// its retained shape nor its table changes.
/// </summary>
public sealed class GenerationPreferredAudioFileRecord
{
    public required Guid GenerationId { get; set; }

    public required Guid AudioFileId { get; set; }
}

/// <summary>
/// One row of <c>song_preferred_audio_files</c> (#212): the Song-level audio file the user chose to play
/// for a Song (its "primary local audio file"). One per Song at most (its key). The file must be
/// associated with that Song: a composite foreign key <c>(audio_file_id, song_id)</c> to
/// <c>audio_files (id, song_id)</c>, written by hand in the migration; that it is Song-level (no
/// Generation) is the service's rule.
/// </summary>
public sealed class SongPreferredAudioFileRecord
{
    public required Guid SongId { get; set; }

    public required Guid AudioFileId { get; set; }
}
