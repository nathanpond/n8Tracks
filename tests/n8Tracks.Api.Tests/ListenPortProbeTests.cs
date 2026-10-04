using System.Net.Sockets;
using n8Tracks.Api.Configuration;
using n8Tracks.TestSupport;

namespace n8Tracks.Api.Tests;

/// <summary>
/// What the one startup error line says for each way a port can fail to bind. A bind that is refused
/// for lack of permission cannot be produced on every machine the tests run on (macOS lets any user
/// bind a low port; on Linux it depends on the user and on <c>ip_unprivileged_port_start</c>), so
/// that branch is tested here, on the description, and not by a real refused bind.
/// </summary>
public sealed class ListenPortProbeTests
{
    [Fact]
    public void APortThatIsNotPermittedIsDescribedAsNotPermitted()
    {
        Assert.Equal(
            "could not be bound: n8Tracks is not permitted to listen on port 80.",
            ListenPortProbe.Describe(SocketError.AccessDenied, 80));
    }

    [Fact]
    public void APortInUseIsDescribedAsInUse()
    {
        Assert.Equal(
            "could not be bound: port 8787 is already in use.",
            ListenPortProbe.Describe(SocketError.AddressAlreadyInUse, 8787));
    }

    [Fact]
    public void AnyOtherFailureNamesThePortAndTheError()
    {
        Assert.Equal(
            "could not be bound: port 8787 is unavailable (AddressNotAvailable).",
            ListenPortProbe.Describe(SocketError.AddressNotAvailable, 8787));
    }

    [Fact]
    public void EachFailureHasADescriptionOfItsOwn()
    {
        string[] descriptions =
        [
            ListenPortProbe.Describe(SocketError.AccessDenied, 80),
            ListenPortProbe.Describe(SocketError.AddressAlreadyInUse, 80),
            ListenPortProbe.Describe(SocketError.AddressNotAvailable, 80),
        ];

        Assert.Equal(3, descriptions.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Each port is one only this test can be given (see <see cref="TestPorts"/>) and was never bound
    /// before: the free one stays free, and the taken one is taken by a listener the test holds.
    /// It does not release a port and ask again: a port from the operating system's pool can be
    /// handed to another process the moment it is released, and any released port stays taken for
    /// as long as a child process another test is starting still holds its copy of the socket.
    /// </summary>
    [Fact]
    public void TheProbeReportsNothingForAFreePortAndInUseForATakenOne()
    {
        Assert.Null(ListenPortProbe.TryBind(TestPorts.Next()));

        // Dual-mode, any address: the same socket the probe asks for.
        var taken = TestPorts.Next();
        using var occupied = TcpListener.Create(taken);
        occupied.Start();

        Assert.Equal(SocketError.AddressAlreadyInUse, ListenPortProbe.TryBind(taken));
    }
}
