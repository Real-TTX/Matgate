using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Matgate.Agent;
using Matgate.Models;

namespace Matgate.Services;

// Connections into another network. A connection set to "via <tunnel>" is opened against a port on this machine
// (127.0.0.1) instead of its host, and whatever connects to that port is carried to the host - so every engine
// (RDP, VNC, SSH, files, databases) goes through the tunnel without knowing of it. Two kinds of tunnel:
//
// - a Matgate agent standing in that network: "tcp.open" to it, then "tcp.data" both ways over its connection;
// - WireGuard: wireproxy (a WireGuard client that runs as an ordinary program, no kernel module and no rights on
//   the host) is started with the tunnel's configuration and offers the network behind it as a SOCKS5 proxy on
//   127.0.0.1; a connection goes through that proxy.
//
// One port per tunnel and target, kept while it is used and closed after a while without; one wireproxy per
// WireGuard tunnel, started again when its configuration changes.
public sealed class TunnelService(AgentClientPool agents, JsonDataStore store, ILogger<TunnelService> logger)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);
    public const string WireProxyPath = "/usr/local/bin/wireproxy";
    private readonly ConcurrentDictionary<string, Forwarder> _forwarders = new();
    private readonly ConcurrentDictionary<Guid, WireProxy> _wireProxies = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static bool WireGuardAvailable => File.Exists(WireProxyPath);

    // The connection as its engine has to open it: through the tunnel when it has one, otherwise as it is.
    public async Task<ServerEndpoint> RouteAsync(ServerEndpoint server, CancellationToken cancellationToken)
    {
        if (server.TunnelId is not { } tunnelId || string.IsNullOrWhiteSpace(server.Host) || server.Port is < 1 or > 65535)
        {
            return server;
        }

        var tunnel = (await store.GetServersAsync(cancellationToken)).FirstOrDefault(candidate =>
                candidate.Id == tunnelId && candidate.Protocol is ServerProtocol.Agent or ServerProtocol.WireGuard)
            ?? throw new InvalidOperationException("The tunnel this connection goes through does not exist any more.");
        var port = await LocalPortAsync(tunnel, server.Host, server.Port, cancellationToken);
        var routed = server.Clone();
        routed.Host = IPAddress.Loopback.ToString();
        routed.Port = port;
        return routed;
    }

    private async Task<int> LocalPortAsync(ServerEndpoint tunnel, string host, int port, CancellationToken cancellationToken)
    {
        var key = $"{tunnel.Id}|{Fingerprint(tunnel)}|{host.ToLowerInvariant()}|{port}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_forwarders.TryGetValue(key, out var existing) && existing.IsListening)
            {
                existing.Touch();
                return existing.Port;
            }

            Func<NetworkStream, Task> carry = tunnel.Protocol == ServerProtocol.WireGuard
                ? local => ThroughWireGuardAsync(tunnel, host, port, local)
                : local => ThroughAgentAsync(tunnel, host, port, local);
            var forwarder = new Forwarder(key, carry, this, $"{host}:{port} through {tunnel.Name}", logger);
            _forwarders[key] = forwarder;
            return forwarder.Port;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Fingerprint(ServerEndpoint tunnel) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tunnel.Host}|{tunnel.Port}|{tunnel.Password}")))[..16];

    private void Forget(string key, Forwarder forwarder) => _forwarders.TryRemove(new KeyValuePair<string, Forwarder>(key, forwarder));

    // ---- through an agent ----------------------------------------------------------------------------------

    private async Task ThroughAgentAsync(ServerEndpoint agent, string host, int port, NetworkStream local)
    {
        var link = await agents.GetAsync(agent, CancellationToken.None);
        var opened = await link.RequestAsync("tcp.open", new JsonObject { ["host"] = host, ["port"] = port });
        var id = opened.Text("id") ?? throw new InvalidOperationException("The agent did not open the tunnel.");
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        link.ListenTunnel(id, new AgentStream(local, ended));
        try
        {
            var buffer = new byte[64 * 1024];
            var upstream = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var read = await local.ReadAsync(buffer);
                        if (read == 0)
                        {
                            break;
                        }

                        await link.SendEventAsync("tcp.data", new JsonObject { ["id"] = id }, buffer.AsMemory(0, read).ToArray());
                    }
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
            await Task.WhenAny(upstream, ended.Task);
        }
        finally
        {
            link.StopListeningTunnel(id);
            try
            {
                await link.SendEventAsync("tcp.close", new JsonObject { ["id"] = id });
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class AgentStream(NetworkStream local, TaskCompletionSource ended) : IAgentTunnelListener
    {
        public async Task OnDataAsync(ReadOnlyMemory<byte> data)
        {
            try
            {
                await local.WriteAsync(data);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                ended.TrySetResult();
            }
        }

        public void OnClosed() => ended.TrySetResult();
    }

    // ---- through WireGuard ---------------------------------------------------------------------------------

    private async Task ThroughWireGuardAsync(ServerEndpoint tunnel, string host, int port, NetworkStream local)
    {
        var socksPort = await WireProxyPortAsync(tunnel);
        using var proxy = new TcpClient { NoDelay = true };
        await proxy.ConnectAsync(IPAddress.Loopback, socksPort);
        var remote = proxy.GetStream();
        await Socks5ConnectAsync(remote, host, port);
        var down = remote.CopyToAsync(local);
        var up = local.CopyToAsync(remote);
        await Task.WhenAny(down, up);
    }

    // SOCKS5 CONNECT without authentication (RFC 1928): the proxy is wireproxy on 127.0.0.1, nobody else's.
    private static async Task Socks5ConnectAsync(NetworkStream stream, string host, int port)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var answer = new byte[2];
        await stream.ReadExactlyAsync(answer);
        if (answer[0] != 5 || answer[1] != 0)
        {
            throw new InvalidOperationException("The WireGuard tunnel did not accept the connection.");
        }

        var name = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + name.Length];
        request[0] = 5;
        request[1] = 1;
        request[3] = 3;
        request[4] = (byte)name.Length;
        name.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)port;
        await stream.WriteAsync(request);
        var head = new byte[4];
        await stream.ReadExactlyAsync(head);
        if (head[1] != 0)
        {
            throw new InvalidOperationException($"{host}:{port} cannot be reached through the WireGuard tunnel.");
        }

        // The address the proxy names: read and dropped.
        var rest = head[3] switch { 1 => 4, 4 => 16, 3 => stream.ReadByte(), _ => 0 } + 2;
        await stream.ReadExactlyAsync(new byte[rest]);
    }

    // wireproxy for one WireGuard tunnel: started with its configuration (only what wireproxy knows of it) and a
    // SOCKS5 proxy on a free port of 127.0.0.1; started again when the configuration changed or it ended.
    private async Task<int> WireProxyPortAsync(ServerEndpoint tunnel)
    {
        if (!WireGuardAvailable)
        {
            throw new InvalidOperationException("WireGuard tunnels need wireproxy, which this installation does not have.");
        }

        var fingerprint = Fingerprint(tunnel);
        if (_wireProxies.TryGetValue(tunnel.Id, out var running) && running.Fingerprint == fingerprint && !running.Process.HasExited)
        {
            return running.Port;
        }

        running?.Stop();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var folder = Directory.CreateTempSubdirectory("matgate-wg-");
        var file = Path.Combine(folder.FullName, "tunnel.conf");
        await File.WriteAllTextAsync(file, WireProxyConfig(tunnel.Password ?? "", port));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var process = Process.Start(new ProcessStartInfo(WireProxyPath, ["-c", file])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("wireproxy could not be started.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var proxy = new WireProxy(process, port, fingerprint, folder.FullName);
        _wireProxies[tunnel.Id] = proxy;

        // Ready when its proxy answers.
        for (var i = 0; i < 50; i++)
        {
            if (process.HasExited)
            {
                proxy.Stop();
                string said;
                lock (output)
                {
                    said = output.ToString().Trim();
                }

                logger.LogWarning("wireproxy for {Tunnel} ended: {Output}", tunnel.Name, said);
                throw new InvalidOperationException("The WireGuard tunnel could not be started - check its configuration.");
            }

            try
            {
                using var test = new TcpClient();
                await test.ConnectAsync(IPAddress.Loopback, port);
                logger.LogInformation("WireGuard tunnel {Tunnel} up (proxy on 127.0.0.1:{Port}).", tunnel.Name, port);
                return port;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }

        proxy.Stop();
        throw new InvalidOperationException("The WireGuard tunnel did not come up.");
    }

    // A WireGuard configuration as wg-quick takes it, cut down to what wireproxy understands, with its proxy added.
    public static string WireProxyConfig(string config, int socksPort)
    {
        var keep = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Interface"] = new(StringComparer.OrdinalIgnoreCase) { "PrivateKey", "Address", "DNS", "MTU", "ListenPort" },
            ["Peer"] = new(StringComparer.OrdinalIgnoreCase) { "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive" },
        };
        var lines = new List<string>();
        string? section = null;
        foreach (var raw in config.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (keep.ContainsKey(section))
                {
                    lines.Add("");
                    lines.Add($"[{section}]");
                }

                continue;
            }

            var equals = line.IndexOf('=');
            if (section is not null && equals > 0 && keep.TryGetValue(section, out var names) && names.Contains(line[..equals].Trim()))
            {
                // A DNS line with anything but addresses ("none", a search domain) is not for wireproxy: names are
                // then resolved where the connection names them.
                if (line[..equals].Trim().Equals("DNS", StringComparison.OrdinalIgnoreCase)
                    && line[(equals + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(item => !IPAddress.TryParse(item, out _)))
                {
                    continue;
                }

                lines.Add(line);
            }
        }

        lines.Add("");
        lines.Add("[Socks5]");
        lines.Add($"BindAddress = 127.0.0.1:{socksPort}");
        return string.Join("\n", lines).Trim() + "\n";
    }

    // The peer's address from a WireGuard configuration, for the connection's list entry.
    public static (string Host, int Port) Endpoint(string config)
    {
        foreach (var raw in config.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(equals + 1)..].Trim();
                var colon = value.LastIndexOf(':');
                if (colon > 0 && int.TryParse(value[(colon + 1)..], out var port))
                {
                    return (value[..colon].Trim('[', ']'), port);
                }

                return (value, 51820);
            }
        }

        return ("", 51820);
    }

    private sealed record WireProxy(Process Process, int Port, string Fingerprint, string Folder)
    {
        public void Stop()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- the port on 127.0.0.1 -----------------------------------------------------------------------------

    private sealed class Forwarder
    {
        private readonly string _key;
        private readonly Func<NetworkStream, Task> _carry;
        private readonly TunnelService _owner;
        private readonly string _what;
        private readonly ILogger _logger;
        private readonly TcpListener _listener;
        private int _open;
        private long _lastUse = Environment.TickCount64;

        public Forwarder(string key, Func<NetworkStream, Task> carry, TunnelService owner, string what, ILogger logger)
        {
            _key = key;
            _carry = carry;
            _owner = owner;
            _what = what;
            _logger = logger;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            IsListening = true;
            _ = Task.Run(AcceptLoopAsync);
            _ = Task.Run(IdleWatchAsync);
        }

        public int Port { get; }

        public bool IsListening { get; private set; }

        public void Touch() => Interlocked.Exchange(ref _lastUse, Environment.TickCount64);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (IsListening)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    Touch();
                    _ = Task.Run(() => CarryAsync(client));
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        private async Task CarryAsync(TcpClient client)
        {
            Interlocked.Increment(ref _open);
            client.NoDelay = true;
            try
            {
                await _carry(client.GetStream());
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or SocketException or TimeoutException or ObjectDisposedException or EndOfStreamException)
            {
                _logger.LogInformation("Tunnel to {What}: {Message}", _what, exception.Message);
            }
            finally
            {
                client.Dispose();
                Interlocked.Decrement(ref _open);
                Touch();
            }
        }

        private async Task IdleWatchAsync()
        {
            while (IsListening)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                if (Volatile.Read(ref _open) == 0 && Environment.TickCount64 - Interlocked.Read(ref _lastUse) > Idle.TotalMilliseconds)
                {
                    IsListening = false;
                    _owner.Forget(_key, this);
                    _listener.Stop();
                }
            }
        }
    }
}
