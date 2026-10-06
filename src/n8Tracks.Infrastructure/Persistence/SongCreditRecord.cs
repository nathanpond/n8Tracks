namespace n8Tracks.Infrastructure.Persistence;

/// <summary>
/// One row of <c>song_artist_credits</c>: a Song is credited to an Artist (once), as its primary
/// Artist or as a featured Artist at a place in the featured list.
/// </summary>
public sealed class SongCreditRecord
{
    public required Guid SongId { get; set; }

    public required Guid ArtistId { get; set; }

    /// <summary><c>primary</c> or <c>featured</c> (<see cref="Domain.Catalog.SongCreditRules"/>).</summary>
    public required string Role { get; set; }

    /// <summary>0 for the primary Artist; a featured Artist's place in the list, from 0.</summary>
    public required int Position { get; set; }
}
