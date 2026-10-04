using System.Net;
using System.Net.Sockets;
using n8Tracks.Api.Configuration;

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

    [Fact]
    public void TheProbeReportsNothingForAFreePortAndInUseForATakenOne()
    {
        using var occupied = TcpListener.Create(0);
        occupied.Start();
        var taken = ((IPEndPoint)occupied.LocalEndpoint).Port;

        Assert.Equal(SocketError.AddressAlreadyInUse, ListenPortProbe.TryBind(taken));

        occupied.Stop();
        Assert.Null(ListenPortProbe.TryBind(taken));
    }
}
