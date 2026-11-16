using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

// A VNC server that does what a test tells it to: it listens on the loopback interface and runs the test's
// script for every connection. An assertion that fails in a script fails the test through FinishAsync.
internal sealed class ScriptedServer : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<ServerSide, int, Task> _script;
    private readonly List<Task> _scripts = [];
    private readonly Task _accepting;

    public ScriptedServer(Func<ServerSide, Task> script)
        : this((side, _) => script(side))
    {
    }

    // The script gets the number of the connection, counted from zero.
    public ScriptedServer(Func<ServerSide, int, Task> script)
    {
        _script = script;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    public int Port { get; }

    public int Connections
    {
        get
        {
            lock (_scripts)
            {
                return _scripts.Count;
            }
        }
    }

    public VncOptions Options(string? username = null, string? password = null, IReadOnlyList<VncEncoding>? encodings = null)
    {
        return new VncOptions { Host = "127.0.0.1", Port = Port, Username = username, Password = password, Encodings = encodings };
    }

    // Waits until the scripts of all connections have run to their end.
    public async Task FinishAsync()
    {
        Task[] scripts;
        lock (_scripts)
        {
            scripts = [.. _scripts];
        }

        await Task.WhenAll(scripts).WaitAsync(Patience);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _accepting;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                lock (_scripts)
                {
                    var index = _scripts.Count;
                    _scripts.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            await _script(new ServerSide(client.GetStream()), index);
                        }
                    }));
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The listener was stopped.
        }
    }
}

// A message of the client as the server reads it off the line.
internal sealed record ClientMessage(int Type, byte[] Bytes)
{
    public int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(offset));

    public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(offset));

    public string Hex => Convert.ToHexString(Bytes);
}

// The server's end of one connection.
internal sealed class ServerSide(Stream stream)
{
    public Stream Stream { get; set; } = stream;

    public Task SendAsync(Packet packet) => SendAsync(packet.ToArray());

    public async Task SendAsync(byte[] bytes)
    {
        await Stream.WriteAsync(bytes).AsTask().WaitAsync(ScriptedServer.Patience);
        await Stream.FlushAsync();
    }

    public async Task<byte[]> ReadAsync(int count)
    {
        var bytes = new byte[count];
        await Stream.ReadExactlyAsync(bytes).AsTask().WaitAsync(ScriptedServer.Patience);
        return bytes;
    }

    // True when the client has closed the connection (or does so within the patience of a test).
    public async Task<bool> ClosedAsync()
    {
        try
        {
            return await Stream.ReadAsync(new byte[1]).AsTask().WaitAsync(ScriptedServer.Patience) == 0;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return true;
        }
    }

    // The server's version; returns what the client answered.
    public async Task<string> GreetAsync(string version = "RFB 003.008\n")
    {
        await SendAsync(Encoding.ASCII.GetBytes(version));
        return Encoding.ASCII.GetString(await ReadAsync(12));
    }

    // The list of security types (3.7 and 3.8); returns the one the client chose.
    public async Task<int> OfferAsync(params int[] types)
    {
        var packet = new Packet().U8(types.Length);
        foreach (var type in types)
        {
            packet.U8(type);
        }

        await SendAsync(packet);
        return (await ReadAsync(1))[0];
    }

    // SecurityResult, with the reason a 3.8 server gives when it is no.
    public Task ResultAsync(uint result, string? reason = null)
    {
        var packet = new Packet().U32(result);
        if (reason is not null)
        {
            packet.U32((uint)reason.Length).Text(reason);
        }

        return SendAsync(packet);
    }

    // VNC authentication: a challenge, and whether the answer is the one this password gives.
    public async Task<bool> ChallengeAsync(string password)
    {
        var challenge = new byte[16];
        Random.Shared.NextBytes(challenge);
        await SendAsync(challenge);
        var expected = new byte[16];
        VncDes.Respond(Encoding.UTF8.GetBytes(password), challenge, expected);
        var answer = await ReadAsync(16);
        return expected.AsSpan().SequenceEqual(answer);
    }

    // ClientInit from the client (it has to ask for a shared session), ServerInit back.
    public async Task InitAsync(int width, int height, string name = "test desktop")
    {
        Assert.Equal(1, (await ReadAsync(1))[0]);
        var encoded = Encoding.UTF8.GetBytes(name);

        // The server's own pixel format - 16 bits, big-endian - is nothing the client may keep to.
        await SendAsync(new Packet().U16(width).U16(height)
            .U8(16).U8(16).U8(1).U8(1).U16(31).U16(63).U16(31).U8(11).U8(5).U8(0).Zeros(3)
            .U32((uint)encoded.Length).Bytes(encoded));
    }

    // A whole handshake without a password, and the three messages every session begins with. Returns the
    // encodings the client named.
    public async Task<int[]> AcceptAsync(int width, int height, string name = "test desktop")
    {
        Assert.Equal("RFB 003.008\n", await GreetAsync());
        Assert.Equal(1, await OfferAsync(1));
        await ResultAsync(0);
        await InitAsync(width, height, name);
        return await ReadSetupAsync(width, height);
    }

    // SetPixelFormat, SetEncodings and the first FramebufferUpdateRequest.
    public async Task<int[]> ReadSetupAsync(int width, int height)
    {
        // 32 bits a pixel, depth 24, little-endian, true colour, 255 each, red at 16, green at 8, blue at 0.
        var format = await ReadMessageAsync();
        Assert.Equal("00000000" + "20180001" + "00FF00FF00FF" + "100800" + "000000", format.Hex);

        var encodings = await ReadMessageAsync();
        Assert.Equal(2, encodings.Type);
        var count = encodings.U16(2);
        var list = new int[count];
        for (var index = 0; index < count; index++)
        {
            list[index] = (int)encodings.U32(4 + index * 4);
        }

        var request = await ReadMessageAsync();
        Assert.Equal(new Packet().U8(3).U8(0).U16(0).U16(0).U16(width).U16(height).ToArray(), request.Bytes);
        return list;
    }

    // The next message of the client, whole.
    public async Task<ClientMessage> ReadMessageAsync()
    {
        var type = (await ReadAsync(1))[0];
        byte[] rest;
        switch (type)
        {
            case 0:
                rest = await ReadAsync(19);
                break;
            case 2:
                var head = await ReadAsync(3);
                rest = [.. head, .. await ReadAsync(BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(1)) * 4)];
                break;
            case 3:
            case 150:
                rest = await ReadAsync(9);
                break;
            case 4:
                rest = await ReadAsync(7);
                break;
            case 5:
                rest = await ReadAsync(5);
                break;
            case 6:
                var text = await ReadAsync(7);
                var length = BinaryPrimitives.ReadInt32BigEndian(text.AsSpan(3));
                rest = [.. text, .. await ReadAsync(Math.Abs(length))];
                break;
            case 248:
                var fence = await ReadAsync(8);
                rest = [.. fence, .. await ReadAsync(fence[7])];
                break;
            case 251:
                var size = await ReadAsync(7);
                rest = [.. size, .. await ReadAsync(size[5] * 16)];
                break;
            default:
                throw new InvalidOperationException($"The client sent a message of type {type}, which the test server does not know.");
        }

        return new ClientMessage(type, [type, .. rest]);
    }

    // A FramebufferUpdate with these rectangles (each with its header, see Packet.Rectangle).
    public Task UpdateAsync(int rectangles, Packet content) => SendAsync(new Packet().U8(0).U8(0).U16(rectangles).Bytes(content));
}

// What the client reports, kept for the test to look at.
internal sealed class RecordingHandler : IVncHandler
{
    private readonly object _gate = new();

    public List<(int X, int Y, int Width, int Height)> Updates { get; } = [];

    public List<(int Width, int Height)> Sizes { get; } = [];

    public List<(int HotX, int HotY, int Width, int Height, byte[] Rgba)> Cursors { get; } = [];

    public List<string> Texts { get; } = [];

    public List<(bool NeedUsername, bool Failed)> Asked { get; } = [];

    // What to answer when the client asks for credentials; null gives up.
    public Func<bool, bool, VncCredentials?> Credentials { get; set; } = (_, _) => null;

    // Called with every update, on the read loop.
    public Action<int, int, int, int>? OnUpdated { get; set; }

    public int UpdateCount
    {
        get
        {
            lock (_gate)
            {
                return Updates.Count;
            }
        }
    }

    // When the picture last changed (Environment.TickCount64), and the box around everything that has changed
    // since the connection began or the desktop last changed its size.
    public long LastChange { get; private set; }

    public (int Left, int Top, int Right, int Bottom) Covered { get; private set; } = (int.MaxValue, int.MaxValue, 0, 0);

    public void Updated(int x, int y, int width, int height)
    {
        lock (_gate)
        {
            Updates.Add((x, y, width, height));
            LastChange = Environment.TickCount64;
            Covered = (Math.Min(Covered.Left, x), Math.Min(Covered.Top, y), Math.Max(Covered.Right, x + width), Math.Max(Covered.Bottom, y + height));
        }

        OnUpdated?.Invoke(x, y, width, height);
    }

    public void Resized(int width, int height)
    {
        lock (_gate)
        {
            Sizes.Add((width, height));
            LastChange = Environment.TickCount64;
            Covered = (int.MaxValue, int.MaxValue, 0, 0);
        }
    }

    public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
    {
        lock (_gate)
        {
            Cursors.Add((hotX, hotY, width, height, rgba.ToArray()));
        }
    }

    public void ClipboardText(string text)
    {
        lock (_gate)
        {
            Texts.Add(text);
        }
    }

    public Task<VncCredentials?> CredentialsAsync(bool needUsername, bool failed, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Asked.Add((needUsername, failed));
        }

        return Task.FromResult(Credentials(needUsername, failed));
    }

    // Waits until what the test expects has come about.
    public async Task UntilAsync(Func<RecordingHandler, bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            bool met;
            lock (_gate)
            {
                met = condition(this);
            }

            if (met)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Waited {seconds} seconds in vain for: {what}.");
            }

            await Task.Delay(10);
        }
    }

    public T Read<T>(Func<RecordingHandler, T> read)
    {
        lock (_gate)
        {
            return read(this);
        }
    }
}
