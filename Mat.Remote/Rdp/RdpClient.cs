using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using Matgate.Display.Rdp.Channels;

namespace Matgate.Display.Rdp;

// Matgate's own RDP client, written from Microsoft's specification (MS-RDPBCGR): it connects to a remote
// desktop, keeps its picture in a framebuffer, and passes on the pointer and the virtual channels. What it
// asks the server for is deliberately little - bitmaps, the few drawing orders xrdp cannot do without
// (blits from a bitmap cache, rectangles), no bulk compression - because the picture is encoded again for
// the browser anyway and the server is usually on the same network.
//
// One read loop (RunAsync) handles everything the server sends; whatever goes to the server - input, channel
// data, answers - is queued and written by one task, so nobody who calls in ever waits for the network.
public sealed partial class RdpClient : IAsyncDisposable
{
    private readonly RdpOptions _options;
    private readonly IRdpHandler _handler;
    private readonly Action<string>? _log;
    private readonly List<StaticChannel> _channels = [];
    private readonly Dictionary<ushort, JoinedChannel> _channelsById = [];
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    // One message from the server at a time: a TPKT message has at most 65535 bytes, a fast-path one 32767.
    private readonly byte[] _pdu = new byte[65536];

    private TcpClient? _tcp;
    private Stream? _stream;
    private Task? _writer;
    private bool _channelsOpened;
    private int _disposed;

    public RdpClient(RdpOptions options, IRdpHandler handler, Action<string>? log = null)
    {
        _options = options;
        _handler = handler;
        _log = log;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    // BGRX: 4 bytes a pixel, B first, top-down, stride = Width * 4. Read it only while holding FrameLock; it is
    // replaced when the desktop gets another size.
    public byte[] Framebuffer { get; private set; } = [];

    public object FrameLock { get; } = new();

    // Why the server ended the session, when it said so.
    public string? EndReason { get; private set; }

    // The colour depth the server settled on.
    public int ColorDepth { get; private set; }

    // False while connecting and while the server takes the desktop apart to bring it back in another size.
    public bool IsActive => _active;

    // Bytes of RDP messages that came from the server.
    public long BytesReceived => Interlocked.Read(ref _received);

    // The connection to the server (for its round trip, see TcpRoundTrip).
    public Socket? Socket => _tcp?.Client;

    private long _received;

    private sealed class JoinedChannel(StaticChannel channel)
    {
        public StaticChannel Channel { get; } = channel;

        public ChannelReassembler Reassembler { get; } = new();
    }

    // Before ConnectAsync: the channels this session should have (clipboard, dynamic channels, sound).
    public void AddChannel(StaticChannel channel)
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException("Channels are announced while connecting; add them before.");
        }

        _channels.Add(channel);
    }

    // Connects and signs in; returns when the remote desktop is active. Throws RdpException.
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        try
        {
            await OpenTransportAsync(limit.Token);
            _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
            await McsConnectAsync(limit.Token);
            SendClientInfo();

            // The licence, the capabilities and the finishing messages come from the server in its own order;
            // the same code handles them later too, when the desktop is resized.
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            while (!_active)
            {
                await ReadAndProcessAsync(limit.Token);
            }
        }
        catch (Exception exception) when (exception is not RdpException)
        {
            throw Explain(exception, cancellationToken);
        }
    }

    // Handles what the server sends until it ends the session or the token is cancelled.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        try
        {
            while (true)
            {
                await ReadAndProcessAsync(limit.Token);
            }
        }
        catch (SessionEndedException)
        {
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or SocketException or ObjectDisposedException)
        {
            // The server hung up - after telling why (EndReason), or without a word.
        }
        catch (OperationCanceledException)
        {
        }
        catch (RdpProtocolException exception)
        {
            throw new RdpException("The RDP server sent something Matgate does not understand: " + exception.Message, false, exception);
        }
        finally
        {
            foreach (var channel in _channels)
            {
                try
                {
                    channel.Closed();
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _log?.Invoke("channel " + channel.Name + " failed while closing: " + exception.Message);
                }
            }
        }
    }

    // Tells the server this side is leaving, so the session there is disconnected at once instead of timing out.
    public void Disconnect()
    {
        if (_stream is not null)
        {
            // MCS Disconnect Provider Ultimatum, reason "user requested".
            Send([0x03, 0x00, 0x00, 0x09, 0x02, 0xF0, 0x80, 0x21, 0x80]);
        }

        _outgoing.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outgoing.Writer.TryComplete();
        if (_writer is not null)
        {
            // What is queued (the goodbye) gets a moment to leave.
            await Task.WhenAny(_writer, Task.Delay(500));
        }

        _closing.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        if (_writer is not null)
        {
            try
            {
                await _writer;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }

        _closing.Dispose();
    }

    private sealed class SessionEndedException : Exception;

    private void Send(byte[] message)
    {
        _outgoing.Writer.TryWrite(message);
    }

    private async Task WriteLoopAsync()
    {
        var stream = _stream!;
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_closing.Token))
            {
                await stream.WriteAsync(message, _closing.Token);
                if (!_outgoing.Reader.TryPeek(out _))
                {
                    await stream.FlushAsync(_closing.Token);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // Reads one message into _pdu and handles it.
    private async ValueTask ReadAndProcessAsync(CancellationToken cancellationToken)
    {
        var stream = _stream!;
        await stream.ReadExactlyAsync(_pdu.AsMemory(0, 2), cancellationToken);
        int length;
        if (_pdu[0] == 0x03)
        {
            // TPKT: version 3, a reserved byte, the length in two bytes.
            await stream.ReadExactlyAsync(_pdu.AsMemory(2, 2), cancellationToken);
            length = BinaryPrimitives.ReadUInt16BigEndian(_pdu.AsSpan(2));
            if (length < 7)
            {
                throw new RdpProtocolException("a message shorter than its own header");
            }

            await stream.ReadExactlyAsync(_pdu.AsMemory(4, length - 4), cancellationToken);
            Interlocked.Add(ref _received, length);
            SlowPath(_pdu.AsSpan(0, length));
            return;
        }

        // Fast-path: one byte of flags, then the length in one byte or - top bit set - in two.
        length = _pdu[1];
        var header = 2;
        if ((length & 0x80) != 0)
        {
            await stream.ReadExactlyAsync(_pdu.AsMemory(2, 1), cancellationToken);
            length = ((length & 0x7F) << 8) | _pdu[2];
            header = 3;
        }

        if (length < header)
        {
            throw new RdpProtocolException("a message shorter than its own header");
        }

        await stream.ReadExactlyAsync(_pdu.AsMemory(header, length - header), cancellationToken);
        Interlocked.Add(ref _received, length);
        FastPath(_pdu.AsSpan(0, length), header);
    }

    // Wraps data for one MCS channel: TPKT, the X.224 data header, and an MCS Send Data Request.
    private byte[] McsData(ushort channel, ReadOnlySpan<byte> payload)
    {
        var lengthBytes = payload.Length < 0x80 ? 1 : 2;
        var total = 4 + 3 + 6 + lengthBytes + payload.Length;
        if (payload.Length > 0x7FFF || total > 0xFFFF)
        {
            throw new InvalidOperationException("Too much for one RDP message.");
        }

        var message = new byte[total];
        message[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)total);
        message[4] = 0x02;
        message[5] = 0xF0;
        message[6] = 0x80;
        message[7] = 0x64;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)(_userChannel - 1001));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), channel);
        message[12] = 0x70;
        if (lengthBytes == 1)
        {
            message[13] = (byte)payload.Length;
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(13), (ushort)(payload.Length | 0x8000));
        }

        payload.CopyTo(message.AsSpan(13 + lengthBytes));
        return message;
    }

    // One complete message of a virtual channel, cut into the pieces the server takes.
    private void SendChannel(ushort channelId, StaticChannel channel, ReadOnlyMemory<byte> message)
    {
        // The pieces have to stay together and in order: nothing else of this channel may slip in between.
        lock (channel)
        {
            var showProtocol = (channel.Options & 0x00200000) != 0;
            ChannelChunks.Split(message.Span, _chunkSize, showProtocol, chunk => Send(McsData(channelId, chunk)));
        }
    }

    private RdpException Explain(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return new RdpException("The connection was cancelled.", false, exception);
            case OperationCanceledException:
                return new RdpException("The RDP server did not answer in time.", false, exception);
            case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                return new RdpException("The host name could not be resolved.", false, exception);
            case SocketException:
                return new RdpException("The RDP server could not be reached.", false, exception);
            case AuthenticationException:
                return new RdpException(
                    _certificateRefused
                        ? "The RDP server's certificate is not trusted. Allow it in the connection's settings (ignore certificate) if this server is yours."
                        : "The secure connection to the RDP server failed (certificate or TLS settings).",
                    false,
                    exception);
            case SessionEndedException:
                return new RdpException(EndReason ?? "The RDP server ended the connection.", false, exception);
            case EndOfStreamException or IOException or ObjectDisposedException:
                return new RdpException(EndReason ?? "The RDP server closed the connection.", false, exception);
            case RdpProtocolException:
                return new RdpException("The RDP server sent something Matgate does not understand: " + exception.Message, false, exception);
            default:
                return new RdpException("The connection to the RDP server failed: " + exception.Message, false, exception);
        }
    }
}
