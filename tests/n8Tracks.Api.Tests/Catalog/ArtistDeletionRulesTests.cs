using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>Where a deleted Artist's credit goes when it is reassigned (#104).</summary>
public sealed class ArtistDeletionRulesTests
{
    private const string Primary = SongCreditRules.PrimaryRole;
    private const string Featured = SongCreditRules.FeaturedRole;

    [Theory]
    [InlineData(Primary, 0)]
    [InlineData(Featured, 3)]
    public void OnASongNotCreditingTheTargetTheCreditKeepsItsRoleAndPlace(string role, int position)
    {
        var deleted = new CreditPlace(role, position);

        Assert.Equal(deleted, ArtistDeletionRules.Reassigned(deleted, target: null));
    }

    [Theory]
    [InlineData(Primary, 0, Featured, 2, Primary, 0)]
    [InlineData(Featured, 1, Primary, 0, Primary, 0)]
    [InlineData(Featured, 0, Featured, 4, Featured, 4)]
    [InlineData(Featured, 5, Featured, 1, Featured, 1)]
    public void OnASongAlreadyCreditingTheTargetOneCreditStaysInTheMoreSeniorRoleAndTheTargetsPlace(
        string deletedRole,
        int deletedPosition,
        string targetRole,
        int targetPosition,
        string role,
        int position)
    {
        var kept = ArtistDeletionRules.Reassigned(new CreditPlace(deletedRole, deletedPosition), new CreditPlace(targetRole, targetPosition));

        Assert.Equal(new CreditPlace(role, position), kept);
    }
}
