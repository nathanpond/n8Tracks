namespace n8Tracks.Gateway.Configuration;

/// <summary>One invalid setting: the variable and a reason that reads after its name.</summary>
internal sealed record ConfigurationError(string Variable, string Reason);

/// <summary>Thrown when one or more settings are invalid. Every failure is listed.</summary>
internal sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException()
        : this([])
    {
    }

    public ConfigurationValidationException(string message)
        : base(message)
    {
        Errors = [];
    }

    public ConfigurationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    public ConfigurationValidationException(IReadOnlyList<ConfigurationError> errors)
        : base("The gateway configuration is invalid: " + string.Join(" ", (errors ?? []).Select(error => $"{error.Variable} {error.Reason}")))
    {
        Errors = errors ?? [];
    }

    public IReadOnlyList<ConfigurationError> Errors { get; }
}
