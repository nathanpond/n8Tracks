namespace n8Tracks.Gateway.Configuration;

/// <summary>The environment variables the gateway was started with, captured once so settings are loaded from one place.</summary>
internal sealed record EnvironmentSnapshot(IReadOnlyDictionary<string, string> Variables);
