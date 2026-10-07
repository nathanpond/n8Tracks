namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>suno_models</c>: a model of the Suno model list.</summary>
public sealed class SunoModelRecord
{
    public required Guid Id { get; set; }

    /// <summary>The name, as Suno shows it and as a Version's <c>model</c> or <c>soundsModel</c> stores it.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// What names are compared by: NFC-normalised and upper-cased invariantly; unique. A model an
    /// imported clip reports (such as <c>V6-MINI</c>) is matched by this key.
    /// </summary>
    public required string NameKey { get; set; }

    /// <summary>The user's note, or null.</summary>
    public string? Note { get; set; }

    /// <summary>The model's place in the list, from 1; unique.</summary>
    public required int Position { get; set; }

    /// <summary>Whether it is left out of the models offered for a new choice.</summary>
    public required bool Retired { get; set; }

    /// <summary>Whether n8Tracks added it on its own, from an imported clip, rather than the user.</summary>
    public required bool Discovered { get; set; }

    /// <summary>
    /// The name Suno reports the model by on a clip (such as <c>V6-MINI</c>), which import matches
    /// clips by (#135), ignoring case; null when it is not known, and the model's name is matched instead.
    /// </summary>
    public string? ReportedAs { get; set; }
}
