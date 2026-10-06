using n8Tracks.Domain.Catalog;

namespace n8Tracks.Api.Tests.Catalog;

/// <summary>The relationship type rules: the system types, names, symmetric types, and name clashes in either direction.</summary>
public sealed class RelationshipRulesTests
{
    [Fact]
    public void TheSystemTypesAreTheSevenSunoActionsThenRemixThenDerivedFrom()
    {
        Assert.Equal(
            ["Cover", "Extend", "Reuse Prompt", "Mashup", "Sample This Song", "Use as Inspiration", "Voice", "Remix", "Derived From"],
            SystemRelationshipTypes.All.Select(static type => type.Name));
        Assert.Equal(
            ["Covered by", "Extended by", "Prompt reused by", "Used in mashup", "Sampled by", "Inspired", "Voice used by", "Remixed by", "Source of"],
            SystemRelationshipTypes.All.Select(static type => type.ReverseName));
        Assert.Equal(
            ["cover", "extend", "reuse_prompt", "mashup", "sample", "inspiration", "voice", null, null],
            SystemRelationshipTypes.All.Select(static type => type.SunoAction));
        Assert.All(SystemRelationshipTypes.All, static type => Assert.True(type.IsSystem));
        Assert.Equal(SystemRelationshipTypes.All.Count, SystemRelationshipTypes.All.Select(static type => type.Id).Distinct().Count());
        Assert.Equal(SystemRelationshipTypes.All.Count * 2, SystemRelationshipTypes.All.SelectMany(RelationshipRules.Keys).Distinct().Count());
        Assert.Equal(7, SystemRelationshipTypes.PlaceOf(SystemRelationshipTypes.Remix.Id));
        Assert.Equal(-1, SystemRelationshipTypes.PlaceOf(Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(null, "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("Sequel\tto", null)]
    [InlineData("Sequel\u0007to", "A name is one line, with no control characters.")]
    public void ANameFollowsTheGenreRule(string? name, string? error)
    {
        Assert.Equal(error is null ? [] : [error], RelationshipRules.NameErrors(name));
    }

    [Fact]
    public void ANameIsOneToFiftyCharacters()
    {
        Assert.Empty(RelationshipRules.NameErrors(new string('a', 50)));
        Assert.Equal(["Use at most 50 characters."], RelationshipRules.NameErrors(new string('a', 51)));
    }

    [Fact]
    public void AReverseNameEqualToTheNameIgnoringCaseMakesASymmetricType()
    {
        Assert.Equal(("Sibling of", "Sibling of"), RelationshipRules.Normalise("  Sibling  of ", "SIBLING OF"));
        Assert.Equal(("Sequel to", "Has sequel"), RelationshipRules.Normalise("Sequel  to", " Has sequel"));

        var symmetric = new RelationshipType(Guid.CreateVersion7(), "Sibling of", "Sibling of", IsSystem: false, SunoAction: null);
        Assert.True(symmetric.IsSymmetric);
        Assert.Equal(["SIBLING OF"], RelationshipRules.Keys(symmetric));
        Assert.Equal("Sibling of", symmetric.NameFrom(RelationshipDirection.Reverse));
        Assert.False(SystemRelationshipTypes.Cover.IsSymmetric);
        Assert.Equal("Covered by", SystemRelationshipTypes.Cover.NameFrom(RelationshipDirection.Reverse));
        Assert.Equal("Cover", SystemRelationshipTypes.Cover.NameFrom(RelationshipDirection.Forward));
    }

    [Fact]
    public void ANameClashesWithAnyTypesForwardOrReverseNameIgnoringCase()
    {
        var sequel = new RelationshipType(Guid.CreateVersion7(), "Sequel to", "Has sequel", IsSystem: false, SunoAction: null);
        List<RelationshipType> others = [.. SystemRelationshipTypes.All, sequel];

        Assert.Equal(SystemRelationshipTypes.Cover, RelationshipRules.Holder("cover", others));
        Assert.Equal(SystemRelationshipTypes.Cover, RelationshipRules.Holder(" COVERED  by", others));
        Assert.Equal(sequel, RelationshipRules.Holder("has SEQUEL", others));
        Assert.Null(RelationshipRules.Holder("Prequel to", others));
        Assert.Null(RelationshipRules.Holder("Sequel", others));
    }
}
