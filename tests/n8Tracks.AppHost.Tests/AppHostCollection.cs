namespace n8Tracks.AppHost.Tests;

/// <summary>
/// Every test that builds the app model. The AppHost reads the developer's environment, which one
/// test changes for the whole process, so these tests run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppHostCollection
{
    public const string Name = "AppHost";
}
