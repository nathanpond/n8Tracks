namespace n8Tracks.Application.Configuration;

/// <summary>One invalid setting: the variable it came from and why it was refused.</summary>
public sealed record ConfigurationError(string Variable, string Reason);
