using System.Reflection;
using System.Text.RegularExpressions;
using n8Tracks.Domain.Suno;
using NetArchTest.Rules;

namespace n8Tracks.Architecture.Tests;

/// <summary>
/// The server and the Suno audio hosts (#221). The browser streams a Generation's audio from Suno's
/// host; the server only answers the stored address. Beside the rule that no server type makes an
/// outbound request (<see cref="LayeringTests.TheServerMakesNoOutboundRequestButItsOwnHealthCheck"/>,
/// #319), which already keeps any request to a Suno host out: the host list is named in one source
/// file and read only by the types that answer or police the address, none of which can reach the
/// network; and every listed host is one the extension's adapter lists as a Suno audio host.
/// </summary>
public sealed partial class SunoAudioHostTests
{
    private static readonly Assembly[] Server =
    [
        typeof(n8Tracks.Domain.AssemblyMarker).Assembly,
        typeof(n8Tracks.Application.AssemblyMarker).Assembly,
        typeof(n8Tracks.Infrastructure.AssemblyMarker).Assembly,
        typeof(Program).Assembly,
    ];

    /// <summary>Who reads the list: the address check itself, the stream rule, and the Content Security Policy.</summary>
    private static readonly string[] HostListReaders =
    [
        "n8Tracks.Api.Frontend.BrowserPolicyMiddleware",
        "n8Tracks.Application.Media.SunoStream",
        "n8Tracks.Domain.Suno.SunoAudioHosts",
    ];

    [Fact]
    public void EachListedHostIsNamedInOneServerSourceFileOnly()
    {
        var root = RepositoryRoot.Find();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        foreach (var host in SunoAudioHosts.Hosts)
        {
            var naming = sources
                .Where(path => File.ReadAllText(path).Contains(host, StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .ToList();

            Assert.Equal(["src/n8Tracks.Domain/Suno/SunoAudioHosts.cs"], naming);
        }
    }

    [Fact]
    public void OnlyTheAddressCheckTheStreamRuleAndThePolicyReadTheListAndNoneReachesTheNetwork()
    {
        var readers = Types.InAssemblies(Server)
            .That()
            .HaveDependencyOnAny([typeof(SunoAudioHosts).FullName!])
            .GetTypes()
            .Select(static type => type.FullName!.Split('+', '/')[0])
            .Append(typeof(SunoAudioHosts).FullName!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(HostListReaders, readers);

        var networked = Types.InAssemblies(Server)
            .That()
            .HaveDependencyOnAny(["System.Net.Http", "System.Net.WebSockets"])
            .GetTypes()
            .Select(static type => type.FullName!.Split('+', '/')[0])
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(HostListReaders, reader => Assert.DoesNotContain(reader, networked));

        // Complement: the network rule sees the one client that exists, so the answer above is not blindness.
        Assert.Contains("n8Tracks.Api.Endpoints.HealthCheckCommand", networked);
    }

    [Fact]
    public void EveryListedHostIsASunoAudioHostTheExtensionsAdapterLists()
    {
        var addresses = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "extension", "src", "adapter", "addresses.ts"));
        var list = AdapterAudioHosts().Match(addresses);
        Assert.True(list.Success, "The adapter's SUNO_AUDIO_HOSTS list was not found.");
        var adapterHosts = Quoted().Matches(list.Groups["hosts"].Value).Select(static match => match.Groups["host"].Value).ToList();

        // Complement: the adapter lists the signed-download host too, which the server leaves out.
        Assert.Contains("suno-data-uploads.s3.amazonaws.com", adapterHosts);
        Assert.NotEmpty(SunoAudioHosts.Hosts);
        Assert.All(SunoAudioHosts.Hosts, host => Assert.Contains(host, adapterHosts));
    }

    [GeneratedRegex(@"SUNO_AUDIO_HOSTS\s*:\s*readonly string\[\]\s*=\s*\[(?<hosts>[^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterAudioHosts();

    [GeneratedRegex(@"'(?<host>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();
}
