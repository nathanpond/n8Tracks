using System.Net;
using System.Net.Sockets;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests;

/// <summary>
/// The port numbers the process tests hand to the services: what makes two test projects running at
/// the same time unable to be given the same one.
/// </summary>
public sealed class TestPortsTests
{
    [Fact]
    public void APortIsNeverHandedOutTwice()
    {
        var ports = Enumerable.Range(0, 50).Select(static _ => TestPorts.Next()).ToList();

        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    /// <summary>Linux assigns from 32768, macOS and Windows from 49152: a port below both is never the answer to "any free port".</summary>
    [Fact]
    public void APortIsBelowTheRangeTheOperatingSystemAssignsFrom()
    {
        Assert.True(TestPorts.Last < 32768);
        Assert.InRange(TestPorts.Next(), TestPorts.First, TestPorts.Last);
    }

    /// <summary>This is what another test process meets when it tries the same number: the open fails and it moves on.</summary>
    [Fact]
    public void APortsClaimCannotBeTakenWhileThisProcessLives()
    {
        var port = TestPorts.Next();

        Assert.ThrowsAny<IOException>(() =>
        {
            using var second = new FileStream(TestPorts.ClaimPath(port), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        });
    }

    [Fact]
    public void APortCanBeBoundOnEveryAddressTheWayTheServicesBind()
    {
        using var everyAddress = TcpListener.Create(TestPorts.Next());
        everyAddress.Start();
    }

    [Fact]
    public void APortSomethingListensOnIsNotHandedOut()
    {
        // Take the next few numbers for a listener of this test's own, the way a program outside the
        // tests would hold them, then ask: none of them may come back.
        var first = TestPorts.Next();
        var listeners = new List<TcpListener>();
        try
        {
            for (var port = first + 1; port <= Math.Min(first + 5, TestPorts.Last); port++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    listeners.Add(listener);
                }
                catch (SocketException)
                {
                    // Something else has it: equally not to be handed out.
                    listener.Dispose();
                }
            }

            var held = listeners.Select(static listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToList();
            var next = Enumerable.Range(0, 10).Select(static _ => TestPorts.Next()).ToList();

            Assert.NotEmpty(held);
            Assert.Empty(next.Intersect(held));
        }
        finally
        {
            listeners.ForEach(static listener => listener.Dispose());
        }
    }
}
