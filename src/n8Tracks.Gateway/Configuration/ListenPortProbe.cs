using System.Net;
using System.Net.Sockets;

namespace n8Tracks.Gateway.Configuration;

/// <summary>
/// Checks that the listen port can be bound before the host starts, so a port that is in use or not
/// permitted is reported as one clear line instead of the host's stack trace.
/// </summary>
internal static class ListenPortProbe
{
    /// <summary>Binds and releases the port the way Kestrel's any-IP endpoint does. Returns null when the bind worked.</summary>
    public static SocketError? TryBind(int port)
    {
        try
        {
            if (Socket.OSSupportsIPv6)
            {
                using var dualMode = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                dualMode.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            else
            {
                using var ipv4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                ipv4.Bind(new IPEndPoint(IPAddress.Any, port));
            }

            return null;
        }
        catch (SocketException exception)
        {
            return exception.SocketErrorCode;
        }
    }

    public static string Describe(SocketError error, int port) => error switch
    {
        SocketError.AddressAlreadyInUse => $"could not be bound: port {port} is already in use.",
        SocketError.AccessDenied => $"could not be bound: the gateway is not permitted to listen on port {port}.",
        _ => $"could not be bound: port {port} is unavailable ({error}).",
    };
}
