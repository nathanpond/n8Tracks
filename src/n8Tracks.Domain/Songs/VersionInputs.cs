namespace n8Tracks.Domain.Songs;

/// <summary>
/// Every option a Version holds for what Suno will create from it, beyond its lyrics and styles (which
/// stay properties of the Version itself): what it creates (<see cref="Kind"/>), the form each kind is
/// filled in with (<see cref="SongMode"/>, <see cref="SpeechMode"/>), and one typed property per field
/// of Suno's Create screen, named by <see cref="SunoFieldAttribute"/> after its key in the field
/// inventory (<c>docs/suno-create-field-inventory.json</c>). Every option is a creation input, applicable
/// to the current kind and mode or not: values are kept when the kind or mode changes, so switching
/// back finds them again. Which values are allowed, and the defaults, are the inventory's, read by the
/// application layer; this type only holds them. A choice is held as the inventory spells it.
/// </summary>
/// <param name="Kind">What the Version creates.</param>
/// <param name="SongMode">How a Song is described: one prompt (Simple) or lyrics, styles, and More Options (Advanced).</param>
/// <param name="SpeechMode">How a Speech is described, by the same two forms.</param>
/// <param name="Model">The Suno model, from the model list; null when none is chosen.</param>
/// <param name="SimplePrompt">The Simple form's song description.</param>
/// <param name="SimpleLyricsAdded">Whether the Simple form has its lyrics section added (the Version's lyrics).</param>
/// <param name="SimpleStylesAdded">Whether the Simple form has its styles section added (the Version's styles).</param>
/// <param name="ExcludeStyles">Styles to keep out.</param>
/// <param name="VocalGender">A value of the inventory's list, or null for none.</param>
/// <param name="DurationMode">Auto, or custom (<see cref="DurationSeconds"/>).</param>
/// <param name="DurationSeconds">The custom length, used only when <see cref="DurationMode"/> is custom.</param>
/// <param name="MaxMode">Whether Max Mode is on.</param>
/// <param name="Weirdness">A percentage.</param>
/// <param name="StyleInfluence">A percentage.</param>
/// <param name="Variety">A step of the inventory's list.</param>
/// <param name="Personalize">Whether Variety follows the user's taste.</param>
/// <param name="Title">The title Suno gives what it creates; independent of the Song's title once set.</param>
public sealed record VersionInputs(
    VersionKind Kind,
    CreationMode SongMode,
    CreationMode SpeechMode,
    [property: SunoField("model")] string? Model,
    [property: SunoField("simple_prompt")] string SimplePrompt,
    [property: SunoField("simple_add_lyrics")] bool SimpleLyricsAdded,
    [property: SunoField("simple_add_styles")] bool SimpleStylesAdded,
    [property: SunoField("exclude_styles")] string ExcludeStyles,
    [property: SunoField("vocal_gender")] string? VocalGender,
    [property: SunoField("duration_mode")] string DurationMode,
    [property: SunoField("duration_seconds")] int DurationSeconds,
    [property: SunoField("max_mode")] bool MaxMode,
    [property: SunoField("weirdness")] int Weirdness,
    [property: SunoField("style_influence")] int StyleInfluence,
    [property: SunoField("variety")] string Variety,
    [property: SunoField("personalize")] bool Personalize,
    [property: SunoField("title")] string Title);

/// <summary>What a Version creates: one of the three tabs of Suno's Create screen.</summary>
public enum VersionKind
{
    Song,
    Speech,
    Sound,
}

/// <summary>Which of a kind's two forms a Version is filled in with. A Sound has no mode.</summary>
public enum CreationMode
{
    Simple,
    Advanced,
}

/// <summary>Names the field of Suno's Create screen a <see cref="VersionInputs"/> property holds, by its inventory key.</summary>
/// <param name="key">The field's <c>key</c> in <c>docs/suno-create-field-inventory.json</c>.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class SunoFieldAttribute(string key) : Attribute
{
    /// <summary>The field's key in the inventory.</summary>
    public string Key { get; } = key;
}
