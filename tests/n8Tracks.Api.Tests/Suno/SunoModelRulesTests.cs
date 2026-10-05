using n8Tracks.Application.Suno;
using n8Tracks.Domain.Suno;

namespace n8Tracks.Api.Tests.Suno;

/// <summary>The rules of the Suno model list: names, notes, name keys, and which model must stay offered.</summary>
public sealed class SunoModelRulesTests
{
    [Theory]
    [InlineData("v6", true)]
    [InlineData("  v7 beta  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("v7\tbeta", false)]
    [InlineData("v7\nbeta", false)]
    [InlineData("v7 🎵", true)]
    public void ANameIsOneLineOfOneToFiftyCharacters(string? name, bool valid)
    {
        Assert.Equal(valid, SunoModelRules.NameErrors(name).Length == 0);
    }

    [Fact]
    public void AnUnpairedSurrogateIsRefusedInANameAndANote()
    {
        // Built here: test data passed through attributes cannot carry a lone surrogate intact.
        var lone = "v7 " + '\ud800';

        Assert.Equal(["A name cannot contain unpaired surrogate characters."], SunoModelRules.NameErrors(lone));
        Assert.Equal(["A note cannot contain unpaired surrogate characters."], SunoModelRules.NoteErrors(lone));
    }

    [Fact]
    public void ANameIsMeasuredAfterTrimming()
    {
        Assert.Empty(SunoModelRules.NameErrors("  " + new string('a', SunoModelRules.NameMaximumLength) + "  "));
        Assert.Equal(["Use at most 50 characters."], SunoModelRules.NameErrors(new string('a', SunoModelRules.NameMaximumLength + 1)));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("   ", true, null)]
    [InlineData("  Pro plan  ", true, "Pro plan")]
    [InlineData("one\ntwo", false, null)]
    public void ANoteIsOptionalAndOneLine(string? note, bool valid, string? stored)
    {
        Assert.Equal(valid, SunoModelRules.NoteErrors(note).Length == 0);
        if (valid)
        {
            Assert.Equal(stored, SunoModelRules.NormaliseNote(note));
        }
    }

    [Fact]
    public void ANoteHasAtMostTwoHundredCharacters()
    {
        Assert.Empty(SunoModelRules.NoteErrors(new string('n', SunoModelRules.NoteMaximumLength)));
        Assert.NotEmpty(SunoModelRules.NoteErrors(new string('n', SunoModelRules.NoteMaximumLength + 1)));
    }

    [Fact]
    public void NamesAreComparedIgnoringCaseAndNormalisation()
    {
        // The label an imported clip reports matches the stored name.
        Assert.Equal(SunoModelRules.NameKey("v6-mini"), SunoModelRules.NameKey(" V6-MINI "));
        Assert.Equal(SunoModelRules.NameKey("café"), SunoModelRules.NameKey("CAFÉ"));
        Assert.NotEqual(SunoModelRules.NameKey("v6"), SunoModelRules.NameKey("v6-mini"));
    }

    [Fact]
    public void TheOnlyModelNotRetiredIsTheLastOffered()
    {
        var v6 = DefaultSunoModels.V6;
        var wild = DefaultSunoModels.V6Wild with { Retired = true };
        var mini = DefaultSunoModels.V6Mini with { Retired = true };

        Assert.True(SunoModelRules.IsLastOffered([v6, wild, mini], v6));
        Assert.False(SunoModelRules.IsLastOffered([v6, wild, mini], wild));
        Assert.False(SunoModelRules.IsLastOffered([v6, DefaultSunoModels.V6Wild, mini], v6));
        Assert.Equal([v6], SunoModel.Offered([mini, wild, v6]));
    }

    [Fact]
    public void TheShippedListIsTheInventoryModelsInOrder()
    {
        Assert.Equal(CreateFieldInventory.Embedded.Get("model").Values, DefaultSunoModels.All.Select(static model => model.Name));
        Assert.Equal(CreateFieldInventory.Embedded.Get("sounds_model").Values, DefaultSunoModels.All.Select(static model => model.Name));
        Assert.Equal([1, 2, 3], DefaultSunoModels.All.Select(static model => model.Order));
        Assert.All(DefaultSunoModels.All, static model => Assert.False(model.Retired || model.Discovered));
    }
}
