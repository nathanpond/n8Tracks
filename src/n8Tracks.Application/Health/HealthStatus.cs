namespace n8Tracks.Application.Health;

/// <summary>How a component, or the instance as a whole, is doing. Ordered from best to worst.</summary>
public enum HealthStatus
{
    /// <summary>Working.</summary>
    Healthy = 0,

    /// <summary>Not working, but the app can still do most of its job (a missing media mount).</summary>
    Degraded = 1,

    /// <summary>Not working, and the app cannot do its job (an unreachable database).</summary>
    Unhealthy = 2,
}
