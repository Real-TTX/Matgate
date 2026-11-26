using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Matgate.Display;

// What only an app on the viewer's own computer (ThixGate) brings to a screen session: its monitors, its folders as
// drives, its clipboard by file path, and the numbers of the connection. Matgate's server never uses any of it - its
// disks and monitors are not the user's, and a browser never names a path on the server.

// One monitor of the viewer's computer, in its pixels; Left and Top place it among the others (any origin).
public sealed record ScreenMonitor(int Left, int Top, int Width, int Height, bool Primary);

// A folder of the viewer's computer, shown on the remote desktop as a drive with this name.
public sealed record ScreenDrive(string Name, string Path);

// How the connection to the remote desktop is doing.
public interface IScreenMeter
{
    // Bytes that came from the server since the session started.
    long BytesReceived { get; }

    // The network's round trip to the server as the computer's TCP stack measures it; null where it cannot be read.
    TimeSpan? RoundTrip { get; }
}

// The clipboard of the viewer's computer by file path, without copying the files first.
public interface IScreenLocalClipboard
{
    // Puts these files (and folders, as their files) on the remote clipboard; paste: press Ctrl+V there once it has them.
    void ClipLocalFiles(IReadOnlyList<string> paths, bool paste);
}

// The round trip of a TCP connection as the operating system keeps it (smoothed, from the acknowledgements).
public static class TcpRoundTrip
{
    // Windows: WSAIoctl SIO_TCP_INFO with TCP_INFO_v0 (RttUs at offset 20).
    private const int WindowsTcpInfo = unchecked((int)0xD8000027);

    // macOS: TCP_CONNECTION_INFO, tcp_connection_info.tcpi_srtt in milliseconds at offset 44.
    private const int MacConnectionInfo = 0x106;

    // Linux: TCP_INFO, tcp_info.tcpi_rtt in microseconds at offset 68.
    private const int LinuxTcpInfo = 11;

    public static TimeSpan? Of(Socket? socket)
    {
        if (socket is null || !socket.Connected)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var output = new byte[88];
                socket.IOControl(WindowsTcpInfo, BitConverter.GetBytes(0), output);
                var microseconds = BitConverter.ToUInt32(output, 20);
                return TimeSpan.FromMicroseconds(microseconds);
            }

            if (OperatingSystem.IsMacOS())
            {
                Span<byte> info = stackalloc byte[128];
                var length = socket.GetRawSocketOption((int)SocketOptionLevel.Tcp, MacConnectionInfo, info);
                return length >= 48 ? TimeSpan.FromMilliseconds(MemoryMarshal.Read<uint>(info[44..])) : null;
            }

            if (OperatingSystem.IsLinux())
            {
                Span<byte> info = stackalloc byte[232];
                var length = socket.GetRawSocketOption((int)SocketOptionLevel.Tcp, LinuxTcpInfo, info);
                return length >= 72 ? TimeSpan.FromMicroseconds(MemoryMarshal.Read<uint>(info[68..])) : null;
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or PlatformNotSupportedException or NotSupportedException)
        {
        }

        return null;
    }
}
