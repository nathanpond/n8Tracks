using System.Text.Json;
using System.Xml.Linq;

namespace n8Tracks.AppHost.Tests;

/// <summary>
/// The AppHost is local development tooling and is in neither Docker image. Checked here from the
/// files a build reads: the two build-context lists, the two Dockerfiles, the project files, and the
/// dependency graphs NuGet resolved for the app and the gateway. <c>scripts/smoke-docker.sh</c>
/// checks the built images themselves.
/// </summary>
public class ImageIsolationGuardTests
{
    private const string AppHost = "n8Tracks.AppHost";

    /// <summary>The projects an image is published from.</summary>
    public static TheoryData<string> ShippedProjects => ["n8Tracks.Api", "n8Tracks.Gateway"];

    public static TheoryData<string> Dockerfiles => ["Dockerfile", "src/n8Tracks.Gateway/Dockerfile"];

    [Fact]
    public void TheAppImagesBuildContextLeavesTheAppHostOut()
    {
        var lines = Lines(".dockerignore");

        Assert.Contains("src/n8Tracks.AppHost/", lines);
        Assert.Contains("tests/", lines);

        // Nothing brings it back: the file has no re-include line at all.
        Assert.DoesNotContain(lines, static line => line.StartsWith('!'));
    }

    [Fact]
    public void TheGatewayImagesBuildContextLeavesTheAppHostOut()
    {
        var lines = Lines("src/n8Tracks.Gateway/Dockerfile.dockerignore");

        // Everything is left out first, and only named paths come back.
        Assert.Equal("*", lines[0]);

        var included = lines.Where(static line => line.StartsWith('!')).Select(static line => line[1..]).ToList();

        Assert.NotEmpty(included);
        Assert.DoesNotContain(included, static path => path.Contains("AppHost", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(included, static path => path.Contains("tests", StringComparison.OrdinalIgnoreCase));

        // No re-include is wide enough to take the AppHost with it.
        Assert.DoesNotContain(included, static path => path.TrimEnd('/') is "src" or "." or "*" or "**" || path.Contains('*', StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public void NoDockerfileNamesTheAppHostOrBuildsTheSolution(string dockerfile)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), dockerfile));

        Assert.DoesNotContain("AppHost.csproj", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppHost, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aspire", text, StringComparison.OrdinalIgnoreCase);

        // A solution-wide restore or publish would build every project, the AppHost included.
        Assert.DoesNotContain(".sln", text, StringComparison.OrdinalIgnoreCase);

        // Complement: the file read is a Dockerfile that publishes one named project.
        Assert.Contains("dotnet publish src/n8Tracks.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOtherProjectReferencesTheAppHostOrAnAspireHostingPackage()
    {
        var src = Path.Combine(RepositoryRoot.Find(), "src");
        var projects = Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => Path.GetFileNameWithoutExtension(path) != AppHost)
            .ToList();

        // Complement: the projects were found, the AppHost's own file among their neighbours.
        Assert.True(projects.Count >= 6, $"Only {projects.Count} project files were found under '{src}'.");
        Assert.True(File.Exists(Path.Combine(src, AppHost, AppHost + ".csproj")));

        foreach (var project in projects)
        {
            var csproj = XDocument.Load(project);
            var references = csproj.Descendants()
                .Where(static element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(static element => (string?)element.Attribute("Include") ?? string.Empty)
                .ToList();

            Assert.DoesNotContain(references, static reference => reference.Contains(AppHost, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, static reference => reference.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Aspire.AppHost.Sdk", (string?)csproj.Root?.Attribute("Sdk") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(ShippedProjects))]
    public void TheResolvedDependencyGraphOfAShippedProjectHoldsNoAspirePackageAndNoAppHost(string project)
    {
        var path = Path.Combine(RepositoryRoot.Find(), "src", project, "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"'{path}' was not found: restore the solution (dotnet restore) before running this test.");

        using var assets = JsonDocument.Parse(File.ReadAllText(path));
        var root = assets.RootElement;

        // The graph is the project's own, and it was resolved: otherwise an empty list would prove nothing.
        Assert.Equal(project, root.GetProperty("project").GetProperty("restore").GetProperty("projectName").GetString());

        var resolved = root.GetProperty("libraries").EnumerateObject()
            .Select(static library => library.Name.Split('/')[0])
            .ToList();

        Assert.Contains("n8Tracks.ServiceDefaults", resolved);
        Assert.DoesNotContain(resolved, static name => name.StartsWith("Aspire.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resolved, static name => name.Contains("AppHost", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Lines(string relativePath) =>
        [
            .. File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), relativePath))
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith('#')),
        ];
}
