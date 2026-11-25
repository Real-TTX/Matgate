using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Matgate.Display.Vnc;

// The read loop: the server's messages, one after the other, in a thread of their own.
public sealed partial class VncClient
{
    private const int EncodingCursor = -239;
    private const int EncodingDesktopSize = -223;
    private const int EncodingLastRect = -224;
    private const int EncodingCompressionLevel = -256;
    private const int EncodingExtendedDesktopSize = -308;
    private const int EncodingFence = -312;
    private const int EncodingContinuousUpdates = -313;
    private const int EncodingExtendedClipboard = unchecked((int)0xC0A1E5CE);

    private const uint FenceBlockBefore = 1;
    private const uint FenceBlockAfter = 2;
    private const uint FenceRequest = 0x80000000;

    // An update of more rectangles than this is not kept rectangle by rectangle: the rest becomes one box.
    private const int MaxAreas = 256;

    private readonly TightDecoder _tight = new();
    private readonly ZrleDecoder _zrle = new();
    private readonly Area[] _changed = new Area[MaxAreas];
    private readonly int[] _rectangles = new int[17];
    private readonly object _layoutGate = new();
    private VncReader? _reader;
    private VncFramebuffer _back = new(0, 0);
    private int _changedCount;
    private long _lastUpdateTicks;
    private long _lastUpdateWaitTicks;
    private bool _continuous;
    private bool _refresh;
    private bool _resizeRefused;
    private bool _cursorTooLarge;
    private uint _screenId;
    private uint _screenFlags;
    private volatile bool _canResize;
    private volatile uint _clipboardCaps;
    private volatile string? _clipboardText;

    private readonly record struct Area(int X, int Y, int Width, int Height);

    // The server announced ExtendedDesktopSize, so RequestSize can work.
    public bool CanResize => _canResize;

    // How many rectangles of an encoding the server has sent - for the tests, and for the log.
    internal int Rectangles(VncEncoding encoding) => Volatile.Read(ref _rectangles[(int)encoding]);

    // The server sends updates without being asked for each (for the tests).
    internal bool ContinuousUpdates => Volatile.Read(ref _continuous);

    // The server speaks the Extended Clipboard, so text is not limited to Latin-1 (for the tests).
    internal bool ExtendedClipboard => _clipboardCaps != 0;

    // How long the last update took from its first byte to the picture being there, and how much of that
    // went into reading from the line - which is waiting for the server more than anything else. The rest is
    // the unpacking. (For the tests, which measure with it.)
    internal TimeSpan LastUpdateTime => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _lastUpdateTicks));

    internal TimeSpan LastUpdateWait => Stopwatch.GetElapsedTime(0, Volatile.Read(ref _lastUpdateWaitTicks));

    private void ReadLoop(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _readThread, Environment.CurrentManagedThreadId);
        using var stop = cancellationToken.UnsafeRegister(static client => ((VncClient)client!).CutLine(), this);
        VncException? error = null;
        try
        {
            var reader = _reader!;

            // Checked before every message: what is still in the reader's buffer after a dispose from inside a
            // callback must not reach the decoders, which are gone by then.
            while (Volatile.Read(ref _disposed) == 0 && !cancellationToken.IsCancellationRequested && reader.TryReadByte(out var type))
            {
                ReadMessage(reader, type);
            }
        }
        catch (VncException exception)
        {
            error = exception;
        }
        finally
        {
            CloseLine();
            Volatile.Write(ref _state, Closed);
            Volatile.Write(ref _readThread, 0);
            _tight.Dispose();
            _zrle.Dispose();

            // The writer has nothing left to wait for.
            _closing.Cancel();
        }

        // Cut on purpose: whatever the reader made of that is no error.
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Log($"VNC: the connection to {Target} has ended{(error is null ? "" : ": " + error.Message)} (rectangles: {Summary()}).");
        if (_failure is { } failure)
        {
            throw new VncException(failure);
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }

    private string Summary()
    {
        var parts = Enum.GetValues<VncEncoding>()
            .Where(encoding => _rectangles[(int)encoding] > 0)
            .Select(encoding => $"{encoding} {_rectangles[(int)encoding]}");
        return string.Join(", ", parts) is { Length: > 0 } text ? text : "none";
    }

    private void ReadMessage(VncReader reader, byte type)
    {
        switch (type)
        {
            case 0:
                var started = Stopwatch.GetTimestamp();
                var waited = reader.Waited;
                ReadUpdate(reader);
                Volatile.Write(ref _lastUpdateTicks, Stopwatch.GetTimestamp() - started);
                Volatile.Write(ref _lastUpdateWaitTicks, reader.Waited - waited);
                break;
            case 1:
                // SetColourMapEntries: the colours of a palette. Matgate asks for true colour, so there is none;
                // the message is taken off the line and forgotten.
                reader.Skip(3);
                reader.Skip(reader.ReadUInt16() * 6L);
                break;
            case 2:
                // Bell.
                break;
            case 3:
                ReadCutText(reader);
                break;
            case 150:
                // EndOfContinuousUpdates. The first one is the server's way of saying that it can send updates
                // without being asked for each; that is switched on then, and stays on.
                if (!_continuous)
                {
                    _continuous = true;
                    SendContinuousUpdates();
                }

                break;
            case 248:
                ReadFence(reader);
                break;
            default:
                throw new VncException($"The VNC server sent a message Matgate does not know (type {type}).");
        }
    }

    // FramebufferUpdate: a number of rectangles, each packed in its own way.
    private void ReadUpdate(VncReader reader)
    {
        reader.Skip(1);
        var count = reader.ReadUInt16();
        for (var index = 0; index < count; index++)
        {
            var header = reader.Take(12);
            int x = BinaryPrimitives.ReadUInt16BigEndian(header);
            int y = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            int width = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
            int height = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
            var encoding = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
            if (encoding == EncodingLastRect)
            {
                // A server that does not know the number of rectangles in advance names 65535 and ends with this.
                break;
            }

            ReadRectangle(reader, x, y, width, height, encoding);
        }

        Present();

        // Without continuous updates the server sends the next update only when asked. With them a request is
        // only needed where the picture has to come anew as a whole.
        if (!_continuous || _refresh)
        {
            RequestUpdate(incremental: !_refresh);
        }

        _refresh = false;
    }

    private void ReadRectangle(VncReader reader, int x, int y, int width, int height, int encoding)
    {
        switch (encoding)
        {
            case EncodingCursor:
                ReadCursor(reader, x, y, width, height);
                return;
            case EncodingDesktopSize:
                // The plain form of a new size says nothing about the picture: it is asked for as a whole.
                if (Resize(width, height))
                {
                    _refresh = true;
                }

                return;
            case EncodingExtendedDesktopSize:
                ReadLayout(reader, x, y, width, height);
                return;
            case (int)VncEncoding.Raw or (int)VncEncoding.CopyRect or (int)VncEncoding.Rre or (int)VncEncoding.Hextile or (int)VncEncoding.Tight or (int)VncEncoding.Zrle:
                break;
            default:
                // Its length is unknown, so nothing behind it can be read either.
                throw new VncException($"The VNC server sent a picture packed in a way Matgate did not ask for (encoding {encoding}).");
        }

        var target = _back;
        if (!target.Contains(x, y, width, height))
        {
            throw new VncException($"The VNC server sent a picture for a place outside the desktop ({width} x {height} at {x}, {y} on {target.Width} x {target.Height}).");
        }

        try
        {
            switch ((VncEncoding)encoding)
            {
                case VncEncoding.Raw:
                    RawDecoder.Decode(reader, target, x, y, width, height);
                    break;
                case VncEncoding.CopyRect:
                    CopyRectDecoder.Decode(reader, target, x, y, width, height);
                    break;
                case VncEncoding.Rre:
                    RreDecoder.Decode(reader, target, x, y, width, height);
                    break;
                case VncEncoding.Hextile:
                    HextileDecoder.Decode(reader, target, x, y, width, height);
                    break;
                case VncEncoding.Tight:
                    _tight.Decode(reader, target, x, y, width, height);
                    break;
                default:
                    _zrle.Decode(reader, target, x, y, width, height);
                    break;
            }
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or ArithmeticException)
        {
            // The decoders check what the server sends before they use it. Should one of them have missed
            // something, it still ends as what it is: a server that sent nonsense.
            throw new VncException("The VNC server sent a picture that cannot be read.", exception);
        }

        _rectangles[encoding]++;
        if (width == 0 || height == 0)
        {
            return;
        }

        if (_changedCount < MaxAreas)
        {
            _changed[_changedCount++] = new Area(x, y, width, height);
            return;
        }

        var last = _changed[MaxAreas - 1];
        var left = Math.Min(last.X, x);
        var top = Math.Min(last.Y, y);
        _changed[MaxAreas - 1] = new Area(left, top, Math.Max(last.X + last.Width, x + width) - left, Math.Max(last.Y + last.Height, y + height) - top);
    }

    // The update is complete: what it changed goes into the picture others read, in one piece.
    private void Present()
    {
        if (_changedCount == 0)
        {
            return;
        }

        var back = _back;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = 0;
        var bottom = 0;
        lock (_frameLock)
        {
            var front = _front;
            for (var index = 0; index < _changedCount; index++)
            {
                var area = _changed[index];
                if (area.X == 0 && area.Width == back.Width)
                {
                    var start = area.Y * back.Width * 4;
                    back.Pixels.AsSpan(start, area.Width * area.Height * 4).CopyTo(front.AsSpan(start));
                }
                else
                {
                    for (var row = 0; row < area.Height; row++)
                    {
                        var start = ((area.Y + row) * back.Width + area.X) * 4;
                        back.Pixels.AsSpan(start, area.Width * 4).CopyTo(front.AsSpan(start));
                    }
                }

                left = Math.Min(left, area.X);
                top = Math.Min(top, area.Y);
                right = Math.Max(right, area.X + area.Width);
                bottom = Math.Max(bottom, area.Y + area.Height);
            }
        }

        _changedCount = 0;
        _handler.Updated(left, top, right - left, bottom - top);
    }

    // The desktop has another size. What it showed stays where both sizes overlap - the server sends what is
    // new. False when the size is the one there already is.
    private bool Resize(int width, int height)
    {
        var old = _back;
        if (width == old.Width && height == old.Height)
        {
            return false;
        }

        var back = new VncFramebuffer(width, height);
        back.CopyFrom(old);
        var front = back.Snapshot();
        _back = back;
        _changedCount = 0;
        lock (_frameLock)
        {
            _front = front;
            _width = width;
            _height = height;
        }

        Log($"VNC: the desktop of {Target} is {width} x {height} now.");
        _handler.Resized(width, height);
        if (_continuous)
        {
            // Continuous updates are for an area; the server goes on with the old one otherwise.
            SendContinuousUpdates();
        }

        return true;
    }

    // ExtendedDesktopSize: the desktop's size together with the screens it is made of. Sent when the size
    // changes, as the answer to RequestSize, and once at the beginning - which is how Matgate learns that the
    // server can change its size on request at all.
    private void ReadLayout(VncReader reader, int reason, int status, int width, int height)
    {
        int count = reader.ReadByte();
        reader.Skip(3);
        uint id = 0;
        uint flags = 0;
        for (var index = 0; index < count; index++)
        {
            var screen = reader.Take(16);
            if (index == 0)
            {
                id = BinaryPrimitives.ReadUInt32BigEndian(screen);
                flags = BinaryPrimitives.ReadUInt32BigEndian(screen[12..]);
            }
        }

        if (reason == 1 && status != 0)
        {
            // The answer to a RequestSize the server did not follow. Size and screens mean nothing in it.
            if (status == 1)
            {
                // "Prohibited": the server is set up not to do this, and asking again will not change it.
                _resizeRefused = true;
                _canResize = false;
            }

            var why = status switch
            {
                1 => "it is not allowed to",
                2 => "it is out of resources",
                3 => "it does not accept the layout",
                _ => $"status {status}",
            };
            Log($"VNC: {Target} did not change the size of its desktop ({why}).");
            return;
        }

        if (count > 0)
        {
            lock (_layoutGate)
            {
                _screenId = id;
                _screenFlags = flags;
            }
        }

        _canResize = !_resizeRefused;
        Resize(width, height);
    }

    private void ReadCursor(VncReader reader, int hotX, int hotY, int width, int height)
    {
        if (width > CursorDecoder.MaxSize || height > CursorDecoder.MaxSize)
        {
            // Not a pointer anybody could show; it has to leave the line all the same.
            CursorDecoder.Skip(reader, width, height);
            if (!_cursorTooLarge)
            {
                _cursorTooLarge = true;
                Log($"VNC: {Target} sent a pointer of {width} x {height} pixels, which is not shown.");
            }

            return;
        }

        var rgba = new byte[width * height * 4];
        if (CursorDecoder.Decode(reader, width, height, rgba))
        {
            _handler.Cursor(Math.Min(hotX, width - 1), Math.Min(hotY, height - 1), width, height, rgba);
        }
        else
        {
            // No pixel of it can be seen: that is how a server hides the pointer.
            _handler.Cursor(0, 0, 0, 0, default);
        }
    }

    // ServerFence: a mark in the stream. The server sends one with "request" set to learn when Matgate has got
    // this far - TigerVNC measures the line that way and slows down to a crawl without an answer. The answer
    // is the same mark back; it is queued behind everything this loop has answered so far, which is all the
    // ordering the flags can ask of a client.
    private void ReadFence(VncReader reader)
    {
        reader.Skip(3);
        var flags = reader.ReadUInt32();
        int length = reader.ReadByte();
        if (length > 64)
        {
            throw new VncException("The VNC server sent a message that is longer than the protocol allows (fence).");
        }

        var payload = reader.Take(length);
        if ((flags & FenceRequest) == 0)
        {
            return;
        }

        Span<byte> answer = stackalloc byte[9 + length];
        answer.Clear();
        answer[0] = 248;
        BinaryPrimitives.WriteUInt32BigEndian(answer[4..], flags & (FenceBlockBefore | FenceBlockAfter));
        answer[8] = (byte)length;
        payload.CopyTo(answer[9..]);
        Send(answer);
    }

    // ServerCutText: text was copied on the remote desktop. In the protocol's own form the text follows, in
    // Latin-1. A negative length is the Extended Clipboard instead (see VncClipboard).
    private void ReadCutText(VncReader reader)
    {
        reader.Skip(3);
        var length = reader.ReadInt32();
        if (length >= 0)
        {
            if (length > VncClipboard.MaxTextBytes)
            {
                reader.Skip(length);
                Log($"VNC: {Target} sent a clipboard text of {length} bytes, which is too long to be taken.");
                return;
            }

            var text = new byte[length];
            reader.ReadExactly(text);
            _handler.ClipboardText(VncClipboard.PlainText(text));
            return;
        }

        var size = -(long)length;
        if (size < 4)
        {
            throw new VncException("The VNC server sent a clipboard message that is too short to be one.");
        }

        var flags = reader.ReadUInt32();
        size -= 4;
        if ((flags & VncClipboard.Caps) != 0)
        {
            // What the server can do; a number follows for every format it names, which Matgate has no use for.
            reader.Skip(size);
            _clipboardCaps = flags;
            Send(VncClipboard.CapsMessage());
        }
        else if ((flags & VncClipboard.Provide) != 0)
        {
            // Compressed, so a little more than the text itself may arrive.
            if (size > VncClipboard.MaxTextBytes + 65536)
            {
                reader.Skip(size);
                Log($"VNC: {Target} sent a clipboard text of {size} bytes, which is too long to be taken.");
                return;
            }

            var packed = new byte[size];
            reader.ReadExactly(packed);
            if (VncClipboard.ProvidedText(flags, packed) is { } text)
            {
                _handler.ClipboardText(text);
            }
        }
        else
        {
            reader.Skip(size);
            if ((flags & VncClipboard.Notify) != 0)
            {
                // Something new is on the server's clipboard; if it is text, Matgate wants it.
                if ((flags & VncClipboard.Text) != 0)
                {
                    Send(VncClipboard.ActionMessage(VncClipboard.Request | VncClipboard.Text));
                }
            }
            else if ((flags & VncClipboard.Request) != 0)
            {
                // Somebody pastes on the remote desktop: now the text Matgate announced is wanted.
                if ((flags & VncClipboard.Text) != 0 && _clipboardText is { } text && VncClipboard.ProvideMessage(text) is { } message)
                {
                    Send(message);
                }
            }
            else if ((flags & VncClipboard.Peek) != 0)
            {
                Send(VncClipboard.ActionMessage(VncClipboard.Notify | (_clipboardText is null ? 0 : VncClipboard.Text)));
            }
        }
    }
}
