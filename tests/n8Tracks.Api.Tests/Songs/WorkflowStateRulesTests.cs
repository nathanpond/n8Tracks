using n8Tracks.Domain.Songs;

namespace n8Tracks.Api.Tests.Songs;

/// <summary>The domain rules for managing workflow states: names, the colour a new state gets, and the last visible state.</summary>
public sealed class WorkflowStateRulesTests
{
    [Fact]
    public void ANameIsTrimmedAndUpToFiftyCodeUnits()
    {
        Assert.Empty(WorkflowStateRules.NameErrors("  Mixing "));
        Assert.Equal("Mixing", WorkflowStateRules.NormaliseName("  Mixing "));
        Assert.Empty(WorkflowStateRules.NameErrors(" " + new string('a', 50) + " "));

        // Complement: missing, blank, too long, more than one line, and a broken surrogate are refused.
        Assert.Equal(["Enter a name."], WorkflowStateRules.NameErrors(null));
        Assert.Equal(["Enter a name."], WorkflowStateRules.NameErrors(" \t "));
        Assert.Equal(["Use at most 50 characters."], WorkflowStateRules.NameErrors(new string('a', 51)));
        Assert.Equal(["A name is one line, with no control characters."], WorkflowStateRules.NameErrors("Two\nlines"));
        Assert.Equal(["A name cannot contain unpaired surrogate characters."], WorkflowStateRules.NameErrors("Bad \ud800"));

        // The limit counts UTF-16 code units: 25 emoji are 50 units, 26 are too many.
        Assert.Empty(WorkflowStateRules.NameErrors(string.Concat(Enumerable.Repeat("🎵", 25))));
        Assert.NotEmpty(WorkflowStateRules.NameErrors(string.Concat(Enumerable.Repeat("🎵", 26))));
    }

    [Fact]
    public void NamesAreComparedIgnoringCaseAfterNfc()
    {
        Assert.Equal(WorkflowStateRules.NameKey("Mixing"), WorkflowStateRules.NameKey(" MIXING "));
        Assert.Equal(WorkflowStateRules.NameKey("Café"), WorkflowStateRules.NameKey("café"));

        // Complement: different names have different keys.
        Assert.NotEqual(WorkflowStateRules.NameKey("Mixing"), WorkflowStateRules.NameKey("Mixed"));
    }

    [Fact]
    public void ANewStateGetsTheFirstColourNotInUseThenTheFirstColour()
    {
        // The seven defaults use yellow, blue, violet, orange, green, teal, and gray: red is the first free one.
        Assert.Equal(StateColours.Red, WorkflowStateRules.NextColour(DefaultWorkflowStates.All));
        Assert.Equal(StateColours.Gray, WorkflowStateRules.NextColour([]));

        var everyColour = StateColours.All.Select((colour, index) => new WorkflowState(Guid.CreateVersion7(), $"S{index}", colour.Name, index + 1, Hidden: false));
        Assert.Equal(StateColours.All[0].Name, WorkflowStateRules.NextColour(everyColour));
    }

    [Fact]
    public void TheOnlyVisibleStateIsTheLastVisible()
    {
        var idea = DefaultWorkflowStates.Idea;
        var hidden = DefaultWorkflowStates.All.Skip(1).Select(static state => state with { Hidden = true }).ToList();

        Assert.True(WorkflowStateRules.IsLastVisible([idea, .. hidden], idea));

        // Complement: with another visible state it is not, and a hidden state never is.
        Assert.False(WorkflowStateRules.IsLastVisible(DefaultWorkflowStates.All, idea));
        Assert.False(WorkflowStateRules.IsLastVisible([idea, .. hidden], hidden[0]));
    }
}
