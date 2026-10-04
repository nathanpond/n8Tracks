namespace n8Tracks.Api.Tests.Health;

/// <summary>A test that changes Unix file permissions. Skipped on Windows, which has none.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs Unix file permissions (chmod).";
        }
    }
}
