using System.Net.Sockets;

namespace Matgate.Display.Vnc;

// A client for VNC servers (the RFB protocol, versions 3.3, 3.7 and 3.8): it signs in, keeps the desktop's
// picture in memory, and passes on pointer, keyboard, clipboard text and the wish for another desktop size.
//
// Three things run side by side. ConnectAsync does the handshake. RunAsync starts the read loop in a thread
// of its own, which reads the server's messages one after the other and draws them - with plain blocking
// reads, because unpacking a picture is one long sequence of small reads. And whatever is sent after the
// handshake goes through a queue with one writer, so that Pointer, Key, SetClipboardText and RequestSize can
// be called from anywhere, any number of times a second, and never wait for the server.
//
// The picture exists twice. The decoders draw into one copy; when an update of the server is complete, what
// it changed is copied into the other one - Framebuffer - under FrameLock. Whoever reads Framebuffer under
// that lock therefore never sees half an update.
public sealed partial class VncClient : IAsyncDisposable
{
    private const int Created = 0;
    private const int Connecting = 1;
    private const int Connected = 2;
    private const int Running = 3;
    private const int Closed = 4;

    private readonly VncOptions _options;
    private readonly IVncHandler _handler;
    private readonly Action<string>? _log;
    private readonly object _frameLock = new();
    private readonly VncSendQueue _send = new();
    private readonly CancellationTokenSource _closing = new();
    private Socket? _socket;
    private Stream? _stream;
    private Task _writing = Task.CompletedTask;
    private Task _reading = Task.CompletedTask;
    private int _state;
    private int _disposed;
    private int _readThread;
    private volatile string? _failure;
    private volatile byte[] _front = [];
    private volatile int _width;
    private volatile int _height;
    private volatile string _name = "";

    public VncClient(VncOptions options, IVncHandler handler, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        _options = options;
        _handler = handler;
        _log = log;
    }

    // The desktop's size. Together with Framebuffer it only changes under FrameLock.
    public int Width => _width;

    public int Height => _height;

    // The name the server gives its desktop.
    public string Name => _name;

    // BGRX: 4 bytes a pixel, B first, top-down, stride = Width * 4; the fourth byte is always 255. Read it only
    // while holding FrameLock; it is replaced on a resize.
    public byte[] Framebuffer => _front;

    public object FrameLock => _frameLock;

    // TCP, version handshake, security, ClientInit/ServerInit, pixel format, encodings. Throws VncException;
    // an OperationCanceledException when the token is cancelled.
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, Connecting, Created) != Created)
        {
            throw new InvalidOperationException("A VncClient connects once.");
        }

        try
        {
            await SignInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CloseLine();
            Volatile.Write(ref _state, Closed);
            throw;
        }

        _send.Open();
        _writing = Task.Run(WriteLoopAsync, CancellationToken.None);
        SendPixelFormat();
        SendEncodings();
        RequestUpdate(incremental: false);
        Volatile.Write(ref _state, Connected);
    }

    // The read loop. Returns when the server closes the connection or the token is cancelled; throws
    // VncException on a protocol error, and when the connection breaks off.
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, Running, Connected) != Connected)
        {
            throw new InvalidOperationException("RunAsync needs the connection ConnectAsync makes, and runs once.");
        }

        // A thread of its own: the loop blocks on the socket for as long as the session lasts.
        _reading = Task.Factory.StartNew(
            () => ReadLoop(cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        return _reading;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing.Cancel();
        CutLine();

        // Disposed from inside a callback of the handler, the read loop is the caller itself: it ends as soon
        // as the callback returns, and waiting for it here would wait for ever.
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _readThread))
        {
            try
            {
                await _reading.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Whoever awaits RunAsync gets it.
            }
        }

        await _writing.ConfigureAwait(false);
        CloseLine();
        Volatile.Write(ref _state, Closed);
        _tight.Dispose();
        _zrle.Dispose();
    }

    private string Target => $"{_options.Host}:{_options.Port}";

    private void Log(string message) => _log?.Invoke(message);

    // Ends the connection from wherever: a read that is blocked in another thread comes back at once. The
    // streams themselves are left to those who use them.
    private void CutLine()
    {
        _send.Close();
        var socket = Volatile.Read(ref _socket);
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }

        socket.Close();
    }

    // The same, and gives the streams back - for the one who is reading (or when nobody is).
    private void CloseLine()
    {
        CutLine();
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        Interlocked.Exchange(ref _socket, null)?.Dispose();
    }

    // The connection has to end for a reason of Matgate's own; RunAsync reports it.
    private void Fail(string reason)
    {
        _failure ??= reason;
        Log(reason);
        CutLine();
    }

    private async Task WriteLoopAsync()
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return;
        }

        try
        {
            await _send.RunAsync(stream, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Fail("The connection to the VNC server was lost.");
            }
        }
    }
}
