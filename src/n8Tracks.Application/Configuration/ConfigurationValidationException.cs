namespace n8Tracks.Application.Configuration;

/// <summary>Thrown at startup when one or more settings are invalid. Carries every failure found.</summary>
public sealed class ConfigurationValidationException : Exception
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
        : base(Describe(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<ConfigurationError> Errors { get; }

    private static string Describe(IReadOnlyList<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return "Invalid configuration: " + string.Join("; ", errors.Select(error => $"{error.Variable} {error.Reason}"));
    }
}
