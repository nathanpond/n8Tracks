namespace n8Tracks.Domain.Suno;

/// <summary>
/// A Suno model a Version can name. Suno adds and retires models faster than n8Tracks is released, so
/// the list is the user's to keep. A retired model is not offered for a new choice, but a Version
/// that already names it keeps it.
/// </summary>
/// <param name="Id">A UUIDv7, or one of <see cref="DefaultSunoModels"/>' fixed IDs.</param>
/// <param name="Name">The model's name, as Suno shows it and as a Version stores it.</param>
/// <param name="Note">What the user wants to remember about it, or null.</param>
/// <param name="Order">Its place in the list, from 1.</param>
/// <param name="Retired">Whether it is left out of the models offered for a new choice.</param>
/// <param name="Discovered">Whether n8Tracks added it on its own, having found it on an imported clip (M4), rather than the user.</param>
/// <param name="ReportedAs">
/// The name Suno reports it by on a clip (such as <c>V6-MINI</c>), which import matches clips by
/// (#135); null when it is not known, and the model's <paramref name="Name"/> is matched instead.
/// </param>
public sealed record SunoModel(Guid Id, string Name, string? Note, int Order, bool Retired, bool Discovered, string? ReportedAs = null)
{
    /// <summary>The models offered for a new choice: the ones not retired, in order.</summary>
    public static IReadOnlyList<SunoModel> Offered(IEnumerable<SunoModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        return [.. models.Where(static model => !model.Retired).OrderBy(static model => model.Order)];
    }
}

/// <summary>
/// The models an instance ships with, in order: the ones the field inventory's <c>model</c> field
/// offers (a test checks they agree). Their IDs are fixed, so every instance and every test database
/// has the same ones until the user changes them.
/// </summary>
public static class DefaultSunoModels
{
    public static readonly SunoModel V6 = new(new Guid("01a10a6e-dd00-7000-8000-000000000001"), "v6", null, 1, Retired: false, Discovered: false);
    public static readonly SunoModel V6Wild = new(new Guid("01a10a6e-dd01-7001-8000-000000000002"), "v6-wild", null, 2, Retired: false, Discovered: false);
    /// <summary>The one model TS-003 saw Suno report, as <c>V6-MINI</c> (the clip's row badge).</summary>
    public static readonly SunoModel V6Mini = new(new Guid("01a10a6e-dd02-7002-8000-000000000003"), "v6-mini", null, 3, Retired: false, Discovered: false, ReportedAs: "V6-MINI");

    public static IReadOnlyList<SunoModel> All { get; } = [V6, V6Wild, V6Mini];
}
