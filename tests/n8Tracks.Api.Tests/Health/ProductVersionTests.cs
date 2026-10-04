using System.Reflection;
using n8Tracks.Api.Endpoints;

namespace n8Tracks.Api.Tests.Health;

public class ProductVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0+3e8585a", "0.1.0")]
    [InlineData("1.2.3-rc.1+abc.def", "1.2.3-rc.1")]
    [InlineData(" 0.1.0 ", "0.1.0")]
    [InlineData(null, ProductVersion.Fallback)]
    [InlineData("", ProductVersion.Fallback)]
    [InlineData("   ", ProductVersion.Fallback)]
    [InlineData("+3e8585a", ProductVersion.Fallback)]
    public void TheSourceRevisionSuffixIsStrippedAndAMissingVersionFallsBack(string? informational, string expected)
    {
        Assert.Equal(expected, ProductVersion.Parse(informational));
    }

    [Fact]
    public void TheCurrentVersionIsTheOneTheBuildStampedOnTheApi()
    {
        var stamped = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(stamped.Split('+')[0], ProductVersion.Current);
        Assert.NotEqual(ProductVersion.Fallback, ProductVersion.Current);
        Assert.Matches(@"^\d+\.\d+\.\d+", ProductVersion.Current);
    }

    [Fact]
    public void AnAssemblyWithoutAnInformationalVersionFallsBack()
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NoVersion"),
            System.Reflection.Emit.AssemblyBuilderAccess.Run);

        Assert.Equal(ProductVersion.Fallback, ProductVersion.From(assembly));
    }
}
