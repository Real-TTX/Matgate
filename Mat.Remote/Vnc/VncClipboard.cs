using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Matgate.Display.Vnc;

// The clipboard's text on its way to and from the server. The protocol's own messages carry Latin-1 and
// nothing else. The Extended Clipboard extension rides on the same two messages (a negative length marks it)
// and carries UTF-8: each side says what it can do ("caps"), announces that it has something ("notify"), asks
// for it ("request") and gets it zlib-compressed ("provide").
internal static class VncClipboard
{
    public const int MaxTextBytes = 16 * 1024 * 1024;

    // The formats (Matgate only takes text) and the actions, as bits of the flags.
    public const uint Text = 1;
    public const uint Formats = 0xFFFF;
    public const uint Caps = 1u << 24;
    public const uint Request = 1u << 25;
    public const uint Peek = 1u << 26;
    public const uint Notify = 1u << 27;
    public const uint Provide = 1u << 28;

    // What Matgate can do. The size it takes without being asked is zero: the server then announces a new
    // text, Matgate asks for it, and there is never a doubt about what a message that arrives means.
    public static byte[] CapsMessage()
    {
        var message = Message(Caps | Text | Request | Peek | Notify | Provide, 4);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(12), 0);
        return message;
    }

    // An action that carries nothing but its flags.
    public static byte[] ActionMessage(uint flags) => Message(flags, 0);

    // The text itself, as the extension wants it: UTF-8, lines ending with CR LF, a zero at the end, and its
    // length in front - all of that zlib-compressed. Null when the text is too long to be sent.
    public static byte[]? ProvideMessage(string text)
    {
        var encoded = Encoding.UTF8.GetBytes(WithCarriageReturns(text));
        if (encoded.Length >= MaxTextBytes)
        {
            return null;
        }

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)encoded.Length + 1);
            zlib.Write(size);
            zlib.Write(encoded);
            zlib.WriteByte(0);
        }

        var message = Message(Provide | Text, (int)packed.Length);
        packed.GetBuffer().AsSpan(0, (int)packed.Length).CopyTo(message.AsSpan(12));
        return message;
    }

    // The protocol's own message: Latin-1, lines ending with a line feed. What Latin-1 cannot say becomes a
    // question mark. Null when the text is too long to be sent.
    public static byte[]? PlainMessage(string text)
    {
        var encoded = Encoding.Latin1.GetBytes(WithLineFeeds(text));
        if (encoded.Length > MaxTextBytes)
        {
            return null;
        }

        var message = new byte[8 + encoded.Length];
        message[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), encoded.Length);
        encoded.CopyTo(message, 8);
        return message;
    }

    // The text out of a "provide" (the part after the flags). Null when there is none in it that can be used:
    // the message is already off the line then, so the picture goes on.
    public static string? ProvidedText(uint flags, byte[] packed)
    {
        if ((flags & Text) == 0)
        {
            return null;
        }

        try
        {
            // Text is the lowest format, so it comes first. The stream is read only as far as the text goes:
            // a server may or may not have closed it properly behind that.
            using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            Span<byte> header = stackalloc byte[4];
            if (zlib.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4)
            {
                return null;
            }

            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size > MaxTextBytes)
            {
                return null;
            }

            var text = new byte[size];
            if (zlib.ReadAtLeast(text, text.Length, throwOnEndOfStream: false) < text.Length)
            {
                return null;
            }

            return Received(Encoding.UTF8.GetString(text));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static string PlainText(ReadOnlySpan<byte> latin1) => Received(Encoding.Latin1.GetString(latin1));

    // Some servers send the terminating zero along; and whatever a server ends its lines with, a line ends
    // with a line feed for whoever gets the text.
    private static string Received(string text) => WithLineFeeds(text.TrimEnd('\0'));

    private static string WithLineFeeds(string text)
    {
        return text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    private static string WithCarriageReturns(string text)
    {
        return text.AsSpan().ContainsAny('\r', '\n') ? WithLineFeeds(text).Replace("\n", "\r\n") : text;
    }

    // A ClientCutText in its extended form: the length is negative and counts the flags and what follows.
    private static byte[] Message(uint flags, int dataLength)
    {
        var message = new byte[12 + dataLength];
        message[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), -(4 + dataLength));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), flags);
        return message;
    }
}
