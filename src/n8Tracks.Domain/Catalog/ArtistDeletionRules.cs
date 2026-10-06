namespace n8Tracks.Domain.Catalog;

/// <summary>A Song's credit as stored: its role (<see cref="SongCreditRules.PrimaryRole"/> or <see cref="SongCreditRules.FeaturedRole"/>) and its place.</summary>
public readonly record struct CreditPlace(string Role, int Position);

/// <summary>
/// What deleting an Artist does to its credits (#104). Reassigned, a credit goes to the target
/// Artist in the same role and place; a Song that already credits the target keeps one credit, in
/// the more senior of the two roles (primary over featured) and, while featured, the target's own
/// place. Removed, a credit simply goes, and the Song keeps its other credits as they are.
/// </summary>
public static class ArtistDeletionRules
{
    /// <summary>
    /// The target's credit on a Song once the deleted Artist's credit is reassigned to it:
    /// <paramref name="deleted"/> as it was when the Song did not credit the target
    /// (<paramref name="target"/> null); otherwise primary when either was, else the target's place.
    /// </summary>
    public static CreditPlace Reassigned(CreditPlace deleted, CreditPlace? target)
    {
        if (target is not { } existing)
        {
            return deleted;
        }

        return deleted.Role == SongCreditRules.PrimaryRole || existing.Role == SongCreditRules.PrimaryRole
            ? new CreditPlace(SongCreditRules.PrimaryRole, 0)
            : existing;
    }
}
