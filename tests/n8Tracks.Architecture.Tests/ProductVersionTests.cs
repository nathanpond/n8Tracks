using System.Reflection;

namespace n8Tracks.Architecture.Tests;

/// <summary>Every component takes its version from the root <c>VERSION</c> file.</summary>
public class ProductVersionTests
{
    public static TheoryData<Type> ComponentMarkers =>
    [
        typeof(n8Tracks.Domain.AssemblyMarker),
        typeof(n8Tracks.Application.AssemblyMarker),
        typeof(n8Tracks.Infrastructure.AssemblyMarker),
        typeof(Program),
    ];

    // The shape Directory.Build.targets accepts: major.minor.patch, or that with a pre-release
    // suffix, which is how a release candidate (0.1.0-rc.1) is cut.
    private const string VersionPattern =
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9a-z-]+(\.[0-9a-z-]+)*)?$";

    [Fact]
    public void VersionFileHoldsAVersionWithAnOptionalPreRelease()
    {
        Assert.Matches(VersionPattern, ReadVersionFile());
    }

    [Fact]
    public void TheBuildGuardAcceptsTheSameShape()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "Directory.Build.targets"));

        Assert.Contains($"'{VersionPattern}'", targets, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("12.0.3")]
    [InlineData("0.1.0-rc.1")]
    [InlineData("1.5.0-beta")]
    public void AReleaseOrPreReleaseVersionIsAccepted(string version)
    {
        Assert.Matches(VersionPattern, version);
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("v0.1.0")]
    [InlineData("01.1.0")]
    [InlineData("0.1.0-")]
    [InlineData("0.1.0-rc..1")]
    [InlineData("0.1.0-RC.1")]
    [InlineData("0.1.0+build")]
    [InlineData("0.1.0.0")]
    public void AnythingElseIsRefused(string version)
    {
        Assert.DoesNotMatch(VersionPattern, version);
    }

    [Theory]
    [MemberData(nameof(ComponentMarkers))]
    public void ComponentIsCompatibleWithTheVersionFile(Type marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        // A build may override the version with -p:Version=, so the check is the compatibility rule
        // (major.minor match), not equality.
        var informational = marker.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.NotNull(informational);
        Assert.DoesNotContain('+', informational);
        Assert.Equal(MajorMinor(ReadVersionFile()), MajorMinor(informational));
    }

    private static string ReadVersionFile() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "VERSION")).Trim();

    private static string MajorMinor(string version) => string.Join('.', version.Split('.').Take(2));
}
