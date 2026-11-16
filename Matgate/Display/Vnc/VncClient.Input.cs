using System.Buffers.Binary;

namespace Matgate.Display.Vnc;

// What Matgate sends once it is signed in. All of it goes through the queue: any thread may call these, as
// often as it likes, and the messages leave in the order of the calls.
public sealed partial class VncClient
{
    // The order in which a server should pick an encoding when nothing else is asked for: Tight packs best
    // for the processor time it takes, ZRLE is what servers without Tight have, the rest is for the very old.
    private static readonly VncEncoding[] DefaultEncodings =
    [
        VncEncoding.Tight, VncEncoding.Zrle, VncEncoding.CopyRect, VncEncoding.Hextile, VncEncoding.Rre, VncEncoding.Raw,
    ];

    // How hard the server should compress, 0 to 9. A low level: the server is usually on the same network, and
    // its processor time is worth more than the line's.
    private const int CompressionLevel = 2;

    // The pointer is at (x, y) with these buttons down: bit 0 left, 1 middle, 2 right, 3 and 4 the wheel up
    // and down, 5 and 6 the wheel left and right.
    public void Pointer(int x, int y, int buttonMask)
    {
        var sent = _send.EnqueuePointer(
            (byte)buttonMask,
            (ushort)Math.Clamp(x, 0, Math.Max(0, _width - 1)),
            (ushort)Math.Clamp(y, 0, Math.Max(0, _height - 1)));
        if (!sent)
        {
            NotSent();
        }
    }

    // A key went down or up; keysym is the key's X11 name (VNC knows no scan codes).
    public void Key(uint keysym, bool down)
    {
        Span<byte> message = stackalloc byte[8];
        message.Clear();
        message[0] = 4;
        message[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(message[4..], keysym);
        Send(message);
    }

    // The viewer copied text: it becomes the clipboard of the remote desktop. Line breaks may be of any kind.
    public void SetClipboardText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _clipboardText = text;
        var caps = _clipboardCaps;
        byte[]? message;
        if ((caps & VncClipboard.Text) != 0 && (caps & VncClipboard.Notify) != 0)
        {
            // The server takes the text when somebody pastes it (it asks, see ReadCutText).
            message = VncClipboard.ActionMessage(VncClipboard.Notify | VncClipboard.Text);
        }
        else if ((caps & VncClipboard.Text) != 0 && (caps & VncClipboard.Provide) != 0)
        {
            message = VncClipboard.ProvideMessage(text);
        }
        else
        {
            // A server without the extension: Latin-1 is all it takes.
            message = VncClipboard.PlainMessage(text);
        }

        if (message is null)
        {
            Log($"VNC: a clipboard text of {text.Length} characters is too long to be sent to {Target}.");
            return;
        }

        Send(message);
    }

    // Asks the server to make its desktop this size. Nothing happens unless CanResize; whether the server
    // follows shows in Resized.
    public void RequestSize(int width, int height)
    {
        if (!_canResize)
        {
            return;
        }

        uint id;
        uint flags;
        lock (_layoutGate)
        {
            id = _screenId;
            flags = _screenFlags;
        }

        // SetDesktopSize with one screen that fills the desktop, under the id and with the flags the server
        // named for its own - TigerVNC refuses a layout whose screen it does not recognise or whose position
        // is not a real one.
        var wanted = (ushort)Math.Clamp(width, 1, VncFramebuffer.MaxSize);
        var wantedHeight = (ushort)Math.Clamp(height, 1, VncFramebuffer.MaxSize);
        Span<byte> message = stackalloc byte[24];
        message.Clear();
        message[0] = 251;
        BinaryPrimitives.WriteUInt16BigEndian(message[2..], wanted);
        BinaryPrimitives.WriteUInt16BigEndian(message[4..], wantedHeight);
        message[6] = 1;

        // The screen: its id, its place (the top left corner), its size, its flags.
        BinaryPrimitives.WriteUInt32BigEndian(message[8..], id);
        BinaryPrimitives.WriteUInt16BigEndian(message[16..], wanted);
        BinaryPrimitives.WriteUInt16BigEndian(message[18..], wantedHeight);
        BinaryPrimitives.WriteUInt32BigEndian(message[20..], flags);
        Send(message);
    }

    // Asks for the whole picture once more.
    internal void RequestRefresh() => RequestUpdate(incremental: false);

    private void Send(ReadOnlySpan<byte> message)
    {
        if (!_send.Enqueue(message))
        {
            NotSent();
        }
    }

    // A message that was not taken is nothing to speak of before the connection stands or after it is gone.
    // A full queue is: the server has stopped reading.
    private void NotSent()
    {
        if (_send.Overflowed && _failure is null)
        {
            Fail($"The VNC server at {Target} has stopped taking input.");
        }
    }

    // SetPixelFormat: 32 bits a pixel with blue in the lowest byte - blue, green, red, filler in memory, which
    // is what the framebuffer holds and the video encoder reads.
    private void SendPixelFormat()
    {
        Span<byte> message = stackalloc byte[20];
        message.Clear();
        message[4] = 32;
        message[5] = 24;
        message[6] = 0;
        message[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message[8..], 255);
        BinaryPrimitives.WriteUInt16BigEndian(message[10..], 255);
        BinaryPrimitives.WriteUInt16BigEndian(message[12..], 255);
        message[14] = 16;
        message[15] = 8;
        message[16] = 0;
        Send(message);
    }

    // SetEncodings: how the server may pack pictures, most wanted first, and what else Matgate understands.
    // No quality level is named - a server only uses JPEG for a client that names one.
    private void SendEncodings()
    {
        var encodings = new List<int>();
        foreach (var encoding in _options.Encodings ?? DefaultEncodings)
        {
            if (Enum.IsDefined(encoding) && !encodings.Contains((int)encoding))
            {
                encodings.Add((int)encoding);
            }
        }

        encodings.Add(EncodingCompressionLevel + CompressionLevel);
        encodings.Add(EncodingCursor);
        encodings.Add(EncodingDesktopSize);
        encodings.Add(EncodingExtendedDesktopSize);
        encodings.Add(EncodingLastRect);
        encodings.Add(EncodingContinuousUpdates);
        encodings.Add(EncodingFence);
        encodings.Add(EncodingExtendedClipboard);

        var message = new byte[4 + encodings.Count * 4];
        message[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)encodings.Count);
        for (var index = 0; index < encodings.Count; index++)
        {
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4 + index * 4), encodings[index]);
        }

        Send(message);
    }

    // FramebufferUpdateRequest for the whole desktop: what has changed since the last update, or everything.
    private void RequestUpdate(bool incremental)
    {
        Span<byte> message = stackalloc byte[10];
        message.Clear();
        message[0] = 3;
        message[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(message[6..], (ushort)_width);
        BinaryPrimitives.WriteUInt16BigEndian(message[8..], (ushort)_height);
        Send(message);
    }

    // EnableContinuousUpdates for the whole desktop: the server sends what changes without being asked.
    private void SendContinuousUpdates()
    {
        Span<byte> message = stackalloc byte[10];
        message.Clear();
        message[0] = 150;
        message[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message[6..], (ushort)_width);
        BinaryPrimitives.WriteUInt16BigEndian(message[8..], (ushort)_height);
        Send(message);
    }
}
