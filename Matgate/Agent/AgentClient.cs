using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Matgate.Models;

namespace Matgate.Agent;

// What the agent said about itself when the connection was made.
public sealed record AgentHello(string Name, string Version, string Machine, string User, string Os, IReadOnlyList<AgentPlugin> Plugins, bool AutoUpdate, bool CanUpdate)
{
    // Which build the agent is: win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64. An agent from before
    // there were others does not say - it is the Windows one.
    public string Platform { get; init; } = "win-x64";
}

public sealed record AgentPlugin(string Id, string Kind, string Title, IReadOnlyList<AgentPluginVariant> Variants)
{
    // For the screen: whether the machine can take the browser's microphone and its game controllers
    // (it needs a virtual audio cable for the one and the ViGEmBus driver for the other).
    public bool Microphone { get; init; }

    public bool Gamepad { get; init; }
}

// Another way to start the same program - for Claude Code "continue the last conversation".
public sealed record AgentPluginVariant(string Id, string Title);

// Matgate's connections to its agents - one WebSocket per agent, opened when first needed and kept
// while it lives. Matgate connects to the agent (host, port, token), like it connects to an RDP server.
public sealed class AgentClientPool(ILogger<AgentClientPool> logger)
{
    private readonly ConcurrentDictionary<Guid, AgentLink> _links = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _connecting = new();

    // Connections the agents opened themselves (an agent with Matgate's address set, see /api/agent/attach): used
    // first, for a machine Matgate could not reach.
    private readonly ConcurrentDictionary<Guid, AgentLink> _attached = new();

    public void Attach(Guid serverId, AgentLink link)
    {
        if (_attached.TryGetValue(serverId, out var before) && !ReferenceEquals(before, link))
        {
            before.Dispose();
        }

        _attached[serverId] = link;
    }

    public void Detach(Guid serverId, AgentLink link) => _attached.TryRemove(new KeyValuePair<Guid, AgentLink>(serverId, link));

    public bool IsAttached(Guid serverId) => _attached.TryGetValue(serverId, out var link) && link.IsOpen;

    public async Task<AgentLink> GetAsync(ServerEndpoint server, CancellationToken cancellationToken)
    {
        if (server.Protocol != ServerProtocol.Agent)
        {
            throw new InvalidOperationException("This connection is not an agent.");
        }

        if (_attached.TryGetValue(server.Id, out var attached) && attached.IsOpen)
        {
            return attached;
        }

        var fingerprint = Fingerprint(server);
        if (_links.TryGetValue(server.Id, out var existing) && existing.IsOpen && existing.Fingerprint == fingerprint)
        {
            return existing;
        }

        var gate = _connecting.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_links.TryGetValue(server.Id, out existing) && existing.IsOpen && existing.Fingerprint == fingerprint)
            {
                return existing;
            }

            existing?.Dispose();
            var link = await AgentLink.ConnectAsync(server, fingerprint, logger, cancellationToken);
            _links[server.Id] = link;
            return link;
        }
        finally
        {
            gate.Release();
        }
    }

    // Changing host, port, token or the certificate setting makes the next use connect afresh.
    private static string Fingerprint(ServerEndpoint server)
    {
        var text = $"{server.Host}|{server.Port}|{server.Password}|{server.IgnoreCertificate}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

// Something on the gateway that shows a screen session (the browser's WebSocket): every screen.* event of it.
public interface IAgentScreenListener
{
    Task OnFrameAsync(AgentFrame frame);

    Task OnClosedAsync();
}

// One stream of the tunnel on the gateway: the bytes the machine behind the agent sends, and its end.
public interface IAgentTunnelListener
{
    Task OnDataAsync(ReadOnlyMemory<byte> data);

    void OnClosed();
}

// Something on the gateway that shows a terminal session (the browser's WebSocket).
public interface IAgentTerminalListener
{
    Task OnDataAsync(ReadOnlyMemory<byte> data);

    Task OnExitAsync(int? code);
}

public sealed class AgentLink : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AgentFrame>> _pending = new();
    private readonly ConcurrentDictionary<string, IAgentTerminalListener> _terminals = new();
    private readonly ConcurrentDictionary<string, IAgentScreenListener> _screens = new();
    private readonly ConcurrentDictionary<string, IAgentTunnelListener> _tunnels = new();
    private readonly TaskCompletionSource<AgentHello> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private long _nextId;

    private AgentLink(WebSocket socket, string fingerprint, ILogger logger)
    {
        _socket = socket;
        Fingerprint = fingerprint;
        _logger = logger;
    }

    public string Fingerprint { get; }

    public bool IsOpen => _socket.State == WebSocketState.Open && !_closed.IsCancellationRequested;

    public AgentHello Hello => _hello.Task.Result;

    public static async Task<AgentLink> ConnectAsync(ServerEndpoint server, string fingerprint, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server.Host))
        {
            throw new InvalidOperationException("The agent connection has no host.");
        }

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // Trimmed: pasted tokens often bring a space or a line break along.
        socket.Options.SetRequestHeader("Authorization", "Bearer " + (server.Password ?? "").Trim());
        if (server.IgnoreCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var host = server.Host.Contains(':') && !server.Host.StartsWith('[') ? $"[{server.Host}]" : server.Host;
        var uri = new Uri($"wss://{host}:{(server.Port is > 0 and <= 65535 ? server.Port : 7488)}/matgate");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            socket.Dispose();
            var text = exception.ToString();
            if (text.Contains("401", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The agent refused the token - check the connection password.");
            }

            if (text.Contains("RemoteCertificate", StringComparison.OrdinalIgnoreCase) || text.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The agent's certificate is not trusted - tick \"Ignore certificate\" for a self-signed one.");
            }

            throw new InvalidOperationException($"The agent at {server.Host}:{server.Port} cannot be reached.");
        }

        var link = new AgentLink(socket, fingerprint, logger);
        _ = Task.Run(link.ReceiveLoopAsync, CancellationToken.None);
        var hello = await link._hello.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        logger.LogInformation("Agent {Name} ({Os}, {Version}) connected with {Count} plugins.", hello.Name, hello.Os, hello.Version, hello.Plugins.Count);
        return link;
    }

    // A connection the agent opened itself (it knows Matgate's address): the same as one Matgate opens, once the
    // agent has introduced itself.
    public static async Task<AgentLink> AttachAsync(WebSocket socket, ILogger logger, CancellationToken cancellationToken)
    {
        var link = new AgentLink(socket, "attached", logger);
        _ = Task.Run(link.ReceiveLoopAsync, CancellationToken.None);
        var hello = await link._hello.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        logger.LogInformation("Agent {Name} ({Os}, {Version}) connected itself with {Count} plugins.", hello.Name, hello.Os, hello.Version, hello.Plugins.Count);
        return link;
    }

    // Ends when the connection does.
    public Task Completion => Task.Delay(Timeout.Infinite, _closed.Token).ContinueWith(_ => { }, TaskScheduler.Default);

    public void ListenTunnel(string id, IAgentTunnelListener listener) => _tunnels[id] = listener;

    public void StopListeningTunnel(string id) => _tunnels.TryRemove(id, out _);

    public async Task<AgentFrame> RequestAsync(string op, JsonObject? data = null, ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<AgentFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(new AgentFrame { Id = id, Kind = "req", Op = op, Data = data, Payload = payload }, cancellationToken);
            var response = await answer.Task.WaitAsync(RequestTimeout, cancellationToken);
            if (response.Error is not null)
            {
                throw new InvalidOperationException(response.Error);
            }

            return response;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The agent did not answer in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendEventAsync(string op, JsonObject data, ReadOnlyMemory<byte> payload = default)
    {
        return SendAsync(new AgentFrame { Kind = "evt", Op = op, Data = data, Payload = payload }, CancellationToken.None);
    }

    public void Listen(string session, IAgentTerminalListener listener) => _terminals[session] = listener;

    public void StopListening(string session, IAgentTerminalListener listener)
    {
        _terminals.TryRemove(new KeyValuePair<string, IAgentTerminalListener>(session, listener));
    }

    public void ListenScreen(string session, IAgentScreenListener listener) => _screens[session] = listener;

    public void StopListeningScreen(string session, IAgentScreenListener listener)
    {
        _screens.TryRemove(new KeyValuePair<string, IAgentScreenListener>(session, listener));
    }

    private async Task SendAsync(AgentFrame frame, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("The connection to the agent is closed.");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await AgentWire.SendAsync(_socket, frame, cancellationToken);
        }
        catch (WebSocketException)
        {
            throw new InvalidOperationException("The connection to the agent was lost.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var frame = await AgentWire.ReceiveAsync(_socket, _closed.Token);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Kind)
                {
                    case "res" when _pending.TryGetValue(frame.Id, out var answer):
                        answer.TrySetResult(frame);
                        break;
                    case "evt" when frame.Op == "hello":
                        _hello.TrySetResult(ParseHello(frame));
                        break;
                    case "evt" when frame.Op == "pty.data" && _terminals.TryGetValue(frame.Text("session") ?? "", out var listener):
                        await listener.OnDataAsync(frame.Payload);
                        break;
                    case "evt" when frame.Op == "pty.exit" && _terminals.TryRemove(frame.Text("session") ?? "", out var ended):
                        await ended.OnExitAsync(frame.Data?["code"]?.GetValue<int>());
                        break;
                    case "evt" when frame.Op == "tcp.data" && _tunnels.TryGetValue(frame.Text("id") ?? "", out var stream):
                        await stream.OnDataAsync(frame.Payload);
                        break;
                    case "evt" when frame.Op == "tcp.close" && _tunnels.TryRemove(frame.Text("id") ?? "", out var gone):
                        gone.OnClosed();
                        break;
                    case "evt" when frame.Op.StartsWith("screen.", StringComparison.Ordinal) && _screens.TryGetValue(frame.Text("session") ?? "", out var screen):
                        // Queued by the listener: a slow browser must not hold up this connection.
                        await screen.OnFrameAsync(frame);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidDataException)
        {
            _logger.LogInformation("Agent connection ended: {Message}", exception.Message);
        }
        finally
        {
            _closed.Cancel();
            _hello.TrySetException(new InvalidOperationException("The agent closed the connection before introducing itself."));
            foreach (var answer in _pending.Values)
            {
                answer.TrySetException(new InvalidOperationException("The connection to the agent was lost."));
            }

            foreach (var listener in _terminals.Values)
            {
                _ = listener.OnExitAsync(null);
            }

            _terminals.Clear();
            foreach (var screen in _screens.Values)
            {
                _ = screen.OnClosedAsync();
            }

            _screens.Clear();
            foreach (var stream in _tunnels.Values)
            {
                stream.OnClosed();
            }

            _tunnels.Clear();
        }
    }

    private static AgentHello ParseHello(AgentFrame frame)
    {
        var plugins = new List<AgentPlugin>();
        if (frame.Data?["plugins"] is JsonArray list)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                var variants = (item["variants"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(variant => new AgentPluginVariant(variant["id"]?.GetValue<string>() ?? "", variant["title"]?.GetValue<string>() ?? ""))
                    .Where(variant => variant.Id.Length > 0)
                    .ToList();
                plugins.Add(new AgentPlugin(
                    item["id"]?.GetValue<string>() ?? "",
                    item["kind"]?.GetValue<string>() ?? "",
                    item["title"]?.GetValue<string>() ?? "",
                    variants)
                {
                    Microphone = item["microphone"]?.GetValue<bool>() ?? false,
                    Gamepad = item["gamepad"]?.GetValue<bool>() ?? false,
                });
            }
        }

        return new AgentHello(
            frame.Text("name") ?? "",
            frame.Text("version") ?? "",
            frame.Text("machine") ?? "",
            frame.Text("user") ?? "",
            frame.Text("os") ?? "",
            plugins,
            frame.Data?["autoUpdate"]?.GetValue<bool>() ?? false,
            frame.Data?["canUpdate"]?.GetValue<bool>() ?? false)
        {
            Platform = frame.Text("platform") is { Length: > 0 } platform ? platform : "win-x64",
        };
    }

    public void Dispose()
    {
        _closed.Cancel();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Dispose();
    }
}
