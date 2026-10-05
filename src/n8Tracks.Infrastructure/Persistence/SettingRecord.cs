namespace n8Tracks.Infrastructure.Persistence;

/// <summary>One row of <c>settings</c>: an instance-wide setting, by key, with a JSON value.</summary>
public sealed class SettingRecord
{
    public required string Key { get; set; }

    /// <summary>A JSON document; the table refuses anything else.</summary>
    public required string Value { get; set; }
}
