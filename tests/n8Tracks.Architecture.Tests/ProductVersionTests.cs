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

    [Fact]
    public void VersionFileHoldsAMajorMinorPatchVersion()
    {
        Assert.Matches(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", ReadVersionFile());
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
