namespace n8Tracks.Api.Tests;

/// <summary>Throwaway: a failing unit test to prove the CI gate turns red. Never merged.</summary>
public sealed class GateBiteTests
{
    [Fact]
    public void ThisTestFailsOnPurpose()
    {
        Assert.Equal(4, "gate".Length + 1);
    }
}
