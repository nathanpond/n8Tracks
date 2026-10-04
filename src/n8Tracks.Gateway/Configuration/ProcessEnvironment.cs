using System.Collections;

namespace n8Tracks.Gateway.Configuration;

/// <summary>The only place gateway code reads the process environment.</summary>
internal static class ProcessEnvironment
{
    public static EnvironmentSnapshot Read()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                variables[name] = value;
            }
        }

        return new EnvironmentSnapshot(variables);
    }
}
