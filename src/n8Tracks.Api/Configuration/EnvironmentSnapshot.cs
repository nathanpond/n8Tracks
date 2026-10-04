namespace n8Tracks.Api.Configuration;

/// <summary>
/// The environment the app was started with: every variable, and the working directory that relative
/// paths resolve against. Captured once, so settings are loaded from one place.
/// </summary>
internal sealed record EnvironmentSnapshot(IReadOnlyDictionary<string, string> Variables, string WorkingDirectory);
