using n8Tracks.Gateway.Health;

namespace n8Tracks.Gateway.Tests;

public class ProductVersionTests
{
    [Fact]
    public void TheGatewayVersionIsTheRootVersionFile()
    {
        var expected = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "VERSION")).Trim();

        Assert.Equal(expected, ProductVersion.Current);
    }

    [Theory]
    [InlineData("0.1.0", 0, 1)]
    [InlineData("0.1", 0, 1)]
    [InlineData("12.34.56", 12, 34)]
    [InlineData("0.1.7-edge.abc1234", 0, 1)]
    [InlineData("2.0-rc1", 2, 0)]
    [InlineData("1.2+build", 1, 2)]
    public void MajorAndMinorComeFromTheNumericPrefix(string version, int major, int minor)
    {
        Assert.True(ProductVersion.TryReadMajorMinor(version, out var actualMajor, out var actualMinor));
        Assert.Equal((major, minor), (actualMajor, actualMinor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("v0.1.0")]
    [InlineData("one.two")]
    [InlineData("-1.2")]
    [InlineData(" 0.1.0")]
    [InlineData("1.x")]
    [InlineData("9999999999.1")]
    [InlineData("1.9999999999")]
    public void AVersionWithoutANumericMajorAndMinorIsUnreadable(string? version)
    {
        Assert.False(ProductVersion.TryReadMajorMinor(version, out _, out _));
    }

    [Theory]
    [InlineData("0.1.0+abc123", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData(null, "0.0.0-dev")]
    [InlineData(" ", "0.0.0-dev")]
    public void TheSourceRevisionSuffixIsDropped(string? informational, string expected)
    {
        Assert.Equal(expected, ProductVersion.Parse(informational));
    }
}
