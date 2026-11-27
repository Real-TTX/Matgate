using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using Matgate.Display;

namespace Matgate.Services;

// The relay of the app API: carries the bytes of one TCP connection over a WebSocket, both ways, as binary messages.
public static class AppRelay
{
    // The open relays: the TCP connection to the host, by the device and connection it is for - so the app can ask how far
    // the host is from Matgate (GET /app/api/relay/{id}/latency) next to how far Matgate is from itself.
    private static readonly ConcurrentDictionary<Socket, (Guid Device, Guid Server)> Open = new();

    // Keeps a relay's connection to the host until it is disposed.
    public static IDisposable Track(Socket socket, Guid device, Guid server)
    {
        Open[socket] = (device, server);
        return new Untrack(socket);
    }

    // The round trip from Matgate to the host of the newest open relay of this device to this connection (null: none open,
    // or the system does not tell).
    public static TimeSpan? HostRoundTrip(Guid device, Guid server) =>
        Open.Where(entry => entry.Value == (device, server)).Select(entry => TcpRoundTrip.Of(entry.Key)).LastOrDefault(roundTrip => roundTrip is not null);

    private sealed class Untrack(Socket socket) : IDisposable
    {
        public void Dispose() => Open.TryRemove(socket, out _);
    }

    // Carries bytes both ways until one side ends. Returns the bytes sent to the host and received from it.
    public static async Task<(long Up, long Down)> RelayAsync(WebSocket socket, Stream stream, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long up = 0;
        long down = 0;

        async Task Upstream()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), stop.Token);
                    up += result.Count;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        async Task Downstream()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, stop.Token);
                    down += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        var upstream = Upstream();
        var downstream = Downstream();
        try
        {
            await Task.WhenAny(upstream, downstream);

            // The WebSocket is closed with its handshake: cancelling a receive would abort it instead. When the host
            // ended, the app hears so and answers the close, which ends the upstream on its own.
            await CloseAsync(socket);
            await Task.WhenAny(upstream, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
        catch (Exception exception) when (IsGone(exception))
        {
            // One side went away; the other is being closed.
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(upstream, downstream);
            }
            catch (Exception exception) when (IsGone(exception))
            {
                // Ended by the cancel above.
            }
        }

        return (up, down);
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
        }
        catch (Exception exception) when (IsGone(exception))
        {
            // Gone already.
        }
    }

    private static bool IsGone(Exception exception) =>
        exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException or SocketException;
}
