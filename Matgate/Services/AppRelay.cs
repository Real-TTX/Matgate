using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Matgate.Services;

// The relay of the app API: carries the bytes of one TCP connection over a WebSocket, both ways, as binary messages.
public static class AppRelay
{
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
