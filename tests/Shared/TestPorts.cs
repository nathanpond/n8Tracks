using System.Net;
using System.Net.Sockets;

namespace n8Tracks.TestSupport;

/// <summary>
/// The only place a test gets a port number to give to something else (a service started as its own
/// process, an <see cref="HttpListener"/>, a port that must have nothing listening on it). "Bind
/// port 0, read the number, release it" is not safe for that: the operating system hands the same
/// number to the next process that asks, and test projects run at the same time.
/// <para>
/// A port from here cannot be given to anything else while the tests run:
/// </para>
/// <list type="bullet">
/// <item>It comes from <see cref="First"/> to <see cref="Last"/>, which is below the range every
/// operating system assigns from (Linux 32768 to 60999, macOS and Windows 49152 to 65535). Nothing
/// that binds port 0 or opens an outgoing connection is ever given one of these.</item>
/// <item>It is claimed with a lock file that this process holds open, exclusively, until it exits.
/// Another test process asking for the same number cannot open the file and moves on. The operating
/// system drops the lock when the process ends, however it ends.</item>
/// <item>It is handed out once per process, so a test never meets a port an earlier test used.</item>
/// <item>It is skipped when something already accepts connections on it (a program on the machine
/// that listens on that number by name).</item>
/// </list>
/// <para>
/// The check connects; it does not bind. A test process that starts other processes must not bind a
/// port and release it to see whether it is free: a child being started at that moment holds a copy
/// of every open socket until it has replaced itself with its program, so the port stays taken for a
/// moment after the release, and the next bind in this process fails. For the same reason a test
/// that needs a port to be free again after closing a listener of its own has no way to be sure of
/// it; tests here use a new port instead.
/// </para>
/// <para>
/// What is left: a program that is not one of these tests and starts listening on that exact number,
/// by name, between the check and the use.
/// </para>
/// </summary>
internal static class TestPorts
{
    public const int First = 20000;
    public const int Last = 29999;

    private static readonly Lock Gate = new();

    /// <summary>The lock files, open for the life of the process: closing one would release its port.</summary>
    private static readonly List<FileStream> Claims = [];

    private static readonly string ClaimDirectory = Path.Combine(Path.GetTempPath(), "n8tracks-test-ports");

    // Each process starts somewhere else in the range, so two of them rarely ask for the same number.
    private static int candidate = Random.Shared.Next(First, Last + 1);

    /// <summary>A port nothing listens on, that no other caller in this or any other test process is given.</summary>
    public static int Next()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(ClaimDirectory);

            for (var tried = 0; tried <= Last - First; tried++)
            {
                var port = candidate;
                candidate = candidate == Last ? First : candidate + 1;

                // The claim is kept even when the port turns out to be in use: this process will not ask for it again.
                if (TryClaim(port) && !SomethingListens(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException($"No port from {First} to {Last} is free and unclaimed.");
    }

    /// <summary>The lock file whose holder has the port.</summary>
    internal static string ClaimPath(int port) =>
        Path.Combine(ClaimDirectory, port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".lock");

    /// <summary>
    /// Opens the port's lock file for this process alone. The file is never deleted: removing it while
    /// another process holds it would let a third create and lock a new file of the same name.
    /// </summary>
    private static bool TryClaim(int port)
    {
        try
        {
            Claims.Add(new FileStream(ClaimPath(port), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another test process holds it.
            return false;
        }
    }

    /// <summary>Whether anything accepts a connection on the port, on either loopback address.</summary>
    private static bool SomethingListens(int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Connect(address, port);
                return true;
            }
            catch (SocketException)
            {
                // Refused, or the address family is not available here.
            }
        }

        return false;
    }
}
