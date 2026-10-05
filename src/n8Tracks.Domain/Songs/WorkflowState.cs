namespace n8Tracks.Domain.Songs;

/// <summary>
/// A step in the Song workflow. Every Song is in exactly one. A hidden state is still a state: Songs
/// in it are listed like any other, but a new Song never starts in it.
/// </summary>
/// <param name="Id">A UUIDv7.</param>
/// <param name="Name">What the user calls it.</param>
/// <param name="Colour">The name of one of <see cref="StateColours.All"/>.</param>
/// <param name="Order">Its place in the workflow, from 1.</param>
/// <param name="Hidden">Whether it is left out of the choices offered for a new Song.</param>
public sealed record WorkflowState(Guid Id, string Name, string Colour, int Order, bool Hidden)
{
    /// <summary>The state a new Song starts in: the first one in order that is not hidden, or null when every state is.</summary>
    public static WorkflowState? Initial(IEnumerable<WorkflowState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        return states.Where(static state => !state.Hidden).OrderBy(static state => state.Order).FirstOrDefault();
    }
}

/// <summary>
/// The states an instance ships with, in order, all visible. Their IDs are fixed, so every instance
/// and every test database has the same ones until the user changes them.
/// </summary>
public static class DefaultWorkflowStates
{
    public static readonly WorkflowState Idea = new(new Guid("01a10a6e-dc80-7000-8000-000000000001"), "Idea", StateColours.Yellow, 1, Hidden: false);
    public static readonly WorkflowState Writing = new(new Guid("01a10a6e-dc81-7001-8000-000000000002"), "Writing", StateColours.Blue, 2, Hidden: false);
    public static readonly WorkflowState Generating = new(new Guid("01a10a6e-dc82-7002-8000-000000000003"), "Generating", StateColours.Violet, 3, Hidden: false);
    public static readonly WorkflowState Refining = new(new Guid("01a10a6e-dc83-7003-8000-000000000004"), "Refining", StateColours.Orange, 4, Hidden: false);
    public static readonly WorkflowState Final = new(new Guid("01a10a6e-dc84-7004-8000-000000000005"), "Final", StateColours.Green, 5, Hidden: false);
    public static readonly WorkflowState Released = new(new Guid("01a10a6e-dc85-7005-8000-000000000006"), "Released", StateColours.Teal, 6, Hidden: false);
    public static readonly WorkflowState Archived = new(new Guid("01a10a6e-dc86-7006-8000-000000000007"), "Archived", StateColours.Gray, 7, Hidden: false);

    public static IReadOnlyList<WorkflowState> All { get; } = [Idea, Writing, Generating, Refining, Final, Released, Archived];
}

/// <summary>One colour of the state palette: a value for light surfaces and one for dark surfaces.</summary>
/// <param name="Name">What a state stores.</param>
/// <param name="Light">A <c>#rrggbb</c> value with at least 4.5:1 contrast on the light scheme's surfaces.</param>
/// <param name="Dark">A <c>#rrggbb</c> value with at least 4.5:1 contrast on the dark scheme's surfaces.</param>
public sealed record StateColour(string Name, string Light, string Dark);

/// <summary>
/// The twelve colours a workflow state can have. A state stores the name; the screens draw the light
/// or dark value for the colour scheme in use. Each value is dark (or light) enough to be read as
/// text on the scheme's background, which the tests check.
/// </summary>
public static class StateColours
{
    public const string Gray = "gray";
    public const string Red = "red";
    public const string Pink = "pink";
    public const string Grape = "grape";
    public const string Violet = "violet";
    public const string Indigo = "indigo";
    public const string Blue = "blue";
    public const string Cyan = "cyan";
    public const string Teal = "teal";
    public const string Green = "green";
    public const string Yellow = "yellow";
    public const string Orange = "orange";

    /// <summary>Every colour, in the order a picker shows them.</summary>
    public static IReadOnlyList<StateColour> All { get; } =
    [
        new(Gray, "#495057", "#ced4da"),
        new(Red, "#c92a2a", "#ff8787"),
        new(Pink, "#a61e4d", "#f783ac"),
        new(Grape, "#862e9c", "#e599f7"),
        new(Violet, "#5f3dc4", "#b197fc"),
        new(Indigo, "#364fc7", "#91a7ff"),
        new(Blue, "#1864ab", "#74c0fc"),
        new(Cyan, "#0b7285", "#66d9e8"),
        new(Teal, "#087f5b", "#63e6be"),
        new(Green, "#2b7a37", "#8ce99a"),
        new(Yellow, "#8a5a00", "#ffd43b"),
        new(Orange, "#b43c0b", "#ffa94d"),
    ];

    /// <summary>Whether <paramref name="name"/> is the name of one of <see cref="All"/>, spelled exactly.</summary>
    public static bool IsKnown(string? name) => name is not null && All.Any(colour => string.Equals(colour.Name, name, StringComparison.Ordinal));
}
