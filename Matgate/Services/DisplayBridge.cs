using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Matgate.Agent;

namespace Matgate.Services;

// Matgate's own engine for RDP and VNC: the display engine (display/, a container of its own with FreeRDP and
// libvncclient) connects to the remote desktop and speaks the screen protocol of the Matgate agent - H.264
// pictures, the pointer, sound, a clipboard with files - so the browser shows it with the same page.
//
// One TCP connection per viewer; every message is [4 bytes length, little endian][agent message]. The engine
// only answers Matgate: the first message carries the key both share through the secrets volume.
public sealed class DisplayBridge
{
    private readonly ILogger<DisplayBridge> _logger;

    public DisplayBridge(IConfiguration configuration, ILogger<DisplayBridge> logger)
    {
        _logger = logger;
        Address = Environment.GetEnvironmentVariable("MATGATE_DISPLAY_ADDRESS") ?? configuration["Display:Address"] ?? "";
        Available = !string.IsNullOrWhiteSpace(Address);
    }

    // Set at start-up: whether RDP and VNC can use Matgate's own engine at all (otherwise Guacamole shows them).
    public static bool Available { get; private set; }

    public string Address { get; }

    // Open screens by session id - a download of a file copied on the remote desktop finds its connection here.
    public ConcurrentDictionary<string, DisplayConnection> Live { get; } = new();

    public static string Key()
    {
        var direct = Environment.GetEnvironmentVariable("MATGATE_DISPLAY_KEY");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        var path = Environment.GetEnvironmentVariable("MATGATE_DISPLAY_KEY_FILE") ?? "/run/matgate-secrets/display.key";
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    public async Task<DisplayConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var (host, port) = HostAndPort(Address);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            client.Dispose();
            _logger.LogWarning("The display engine at {Address} cannot be reached: {Message}", Address, exception.Message);
            throw new InvalidOperationException("Matgate's display engine cannot be reached.");
        }

        return new DisplayConnection(client);
    }

    private static (string Host, int Port) HostAndPort(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], out var port)
            ? (address[..colon], port)
            : (address, 8092);
    }

    // Windows' keyboard layout ids (KLID) for the layouts Matgate's connection form offers.
    public static uint KeyboardLayoutId(string? layout) => (layout ?? "").Trim().ToLowerInvariant() switch
    {
        "de-de-qwertz" => 0x00000407,
        "de-ch-qwertz" => 0x00000807,
        "en-us-qwerty" => 0x00000409,
        "en-gb-qwerty" => 0x00000809,
        "fr-fr-azerty" => 0x0000040C,
        "fr-be-azerty" => 0x0000080C,
        "fr-ch-qwertz" => 0x0000100C,
        "it-it-qwerty" => 0x00000410,
        "es-es-qwerty" => 0x0000040A,
        "es-latam-qwerty" => 0x0000080A,
        "pt-br-qwerty" => 0x00000416,
        "sv-se-qwerty" => 0x0000041D,
        "da-dk-qwerty" => 0x00000406,
        "nb-no-qwerty" => 0x00000414,
        "hu-hu-qwertz" => 0x0000040E,
        "tr-tr-qwerty" => 0x0000041F,
        "ja-jp-qwerty" => 0x00000411,
        _ => 0x00000407,
    };
}

public sealed class DisplayConnection : IAsyncDisposable
{
    private const int MaxFrame = 16 * 1024 * 1024;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AgentFrame>> _pending = new();
    private readonly Task _reader;
    private long _nextId;

    public DisplayConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = Task.Run(ReadAsync);
    }

    // Everything the engine says that is not an answer: pictures, sound, clipboard, status.
    public Func<AgentFrame, Task>? OnEvent { get; set; }

    public Guid UserId { get; set; }

    // Ends when the engine closed the connection (the remote desktop went away).
    public Task Completion => _reader;

    public async Task<AgentFrame> RequestAsync(string op, JsonObject data, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<AgentFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(new AgentFrame { Id = id, Kind = "req", Op = op, Data = data });
            var response = await answer.Task.WaitAsync(timeout, cancellationToken);
            return response.Error is { } error ? throw new InvalidOperationException(error) : response;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The display engine did not answer.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendEventAsync(string op, JsonObject data, ReadOnlyMemory<byte> payload = default) =>
        SendAsync(new AgentFrame { Kind = "evt", Op = op, Data = data, Payload = payload });

    private async Task SendAsync(AgentFrame frame)
    {
        var body = AgentWire.Encode(frame);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
        await _write.WaitAsync();
        try
        {
            await _stream.WriteAsync(length);
            await _stream.WriteAsync(body);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            throw new InvalidOperationException("The connection to the display engine was lost.");
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is < 6 or > MaxFrame)
                {
                    break;
                }

                var body = new byte[length];
                await _stream.ReadExactlyAsync(body);
                var frame = AgentWire.Decode(body);
                if (frame.Kind == "res")
                {
                    if (_pending.TryRemove(frame.Id, out var waiting))
                    {
                        waiting.TrySetResult(frame);
                    }

                    continue;
                }

                if (OnEvent is { } onEvent)
                {
                    await onEvent(frame);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ObjectDisposedException or SocketException or InvalidDataException or System.Text.Json.JsonException)
        {
        }
        finally
        {
            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetException(new InvalidOperationException("The connection to the display engine was lost."));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        try
        {
            await _reader;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        _write.Dispose();
    }
}
