using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// The tunnel: on Matgate's request a TCP connection from this machine to another one in its network, whose bytes
// go over the connection to Matgate - "tcp.open" (host, port; answered with the stream's id), then "tcp.data"
// events both ways with the bytes as payload, and "tcp.close" from whichever side ends it. One per connection to
// Matgate: when that goes, its streams go with it.
public sealed class TunnelPlugin(AgentConfig config)
{
    private const int ChunkSize = 64 * 1024;
    private readonly ConcurrentDictionary<string, TcpClient> _streams = new();

    public bool IsAvailable => config.Tunnel;

    public async Task<AgentFrame> OpenAsync(AgentFrame request, Func<AgentFrame, Task> send)
    {
        if (!config.Tunnel)
        {
            throw new InvalidOperationException("The tunnel is switched off on this machine.");
        }

        var host = (request.Text("host") ?? "").Trim();
        var port = request.Int("port");
        if (host.Length == 0 || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("The tunnel needs a host and a port.");
        }

        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host);
        if (!Allowed(host, addresses))
        {
            AgentLog.Write($"Tunnel to {host}:{port} refused (not in TunnelAllow)");
            throw new InvalidOperationException($"This machine does not let the tunnel lead to {host}.");
        }

        var client = new TcpClient { NoDelay = true };
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                await client.ConnectAsync(addresses, port, timeout.Token);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                client.Dispose();
                throw new InvalidOperationException($"{host}:{port} cannot be reached from this machine.");
            }
        }

        var id = Guid.NewGuid().ToString("N");
        _streams[id] = client;
        _ = Task.Run(() => PumpAsync(id, client, send));
        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = new JsonObject { ["id"] = id } };
    }

    // Bytes from Matgate for the machine behind the tunnel.
    public async Task DataAsync(AgentFrame frame)
    {
        if (_streams.TryGetValue(frame.Text("id") ?? "", out var client))
        {
            try
            {
                await client.GetStream().WriteAsync(frame.Payload);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Close(frame.Text("id") ?? "");
            }
        }
    }

    public void Close(string id)
    {
        if (_streams.TryRemove(id, out var client))
        {
            client.Dispose();
        }
    }

    public void CloseAll()
    {
        foreach (var id in _streams.Keys)
        {
            Close(id);
        }
    }

    // What the machine behind the tunnel sends, as it comes; its end is the stream's end.
    private async Task PumpAsync(string id, TcpClient client, Func<AgentFrame, Task> send)
    {
        var buffer = new byte[ChunkSize];
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                await send(new AgentFrame { Kind = "evt", Op = "tcp.data", Data = new JsonObject { ["id"] = id }, Payload = buffer.AsSpan(0, read).ToArray() });
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
        }
        finally
        {
            if (_streams.ContainsKey(id))
            {
                Close(id);
                try
                {
                    await send(new AgentFrame { Kind = "evt", Op = "tcp.close", Data = new JsonObject { ["id"] = id } });
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
                {
                }
            }
        }
    }

    // Whether the tunnel may lead there: everything when nothing is listed; otherwise a listed name, address or
    // network ("10.0.0.0/8").
    private bool Allowed(string host, IPAddress[] addresses)
    {
        if (config.TunnelAllow.Count == 0)
        {
            return true;
        }

        foreach (var rule in config.TunnelAllow.Select(rule => rule.Trim()).Where(rule => rule.Length > 0))
        {
            if (string.Equals(rule, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPNetwork.TryParse(rule.Contains('/') ? rule : rule + (rule.Contains(':') ? "/128" : "/32"), out var network)
                && addresses.Any(address => network.Contains(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)))
            {
                return true;
            }
        }

        return false;
    }
}
