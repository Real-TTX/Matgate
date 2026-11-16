using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Matgate.Display.Vnc.Tests;

// Puts a message together byte by byte, the way the specification writes it down: big-endian numbers, and
// pixels in the three forms the encodings use. Colours are written 0xRRGGBB in the tests.
internal sealed class Packet
{
    private readonly MemoryStream _bytes = new();

    public int Length => (int)_bytes.Length;

    public Packet U8(int value)
    {
        _bytes.WriteByte((byte)value);
        return this;
    }

    public Packet U16(int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        _bytes.Write(bytes);
        return this;
    }

    public Packet U32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _bytes.Write(bytes);
        return this;
    }

    public Packet S32(int value) => U32(unchecked((uint)value));

    public Packet Bytes(params byte[] bytes)
    {
        _bytes.Write(bytes);
        return this;
    }

    public Packet Bytes(Packet other) => Bytes(other.ToArray());

    public Packet Zeros(int count) => Bytes(new byte[count]);

    public Packet Text(string text) => Bytes(Encoding.Latin1.GetBytes(text));

    // A pixel in the format the client sets: blue, green, red and a byte that means nothing. The filler is
    // not zero on purpose - the client must not let it through.
    public Packet Pixel(int rgb, int filler = 0x5A) => U8(rgb).U8(rgb >> 8).U8(rgb >> 16).U8(filler);

    // ZRLE's short pixel: blue, green, red.
    public Packet Cpixel(int rgb) => U8(rgb).U8(rgb >> 8).U8(rgb >> 16);

    // Tight's short pixel: red, green, blue.
    public Packet Tpixel(int rgb) => U8(rgb >> 16).U8(rgb >> 8).U8(rgb);

    // The header of a rectangle in a FramebufferUpdate.
    public Packet Rectangle(int x, int y, int width, int height, int encoding) => U16(x).U16(y).U16(width).U16(height).S32(encoding);

    // Tight's length in one to three bytes.
    public Packet CompactLength(int length)
    {
        if (length < 128)
        {
            return U8(length);
        }

        if (length < 16384)
        {
            return U8((length & 0x7F) | 0x80).U8(length >> 7);
        }

        return U8((length & 0x7F) | 0x80).U8(((length >> 7) & 0x7F) | 0x80).U8(length >> 14);
    }

    public byte[] ToArray() => _bytes.ToArray();

    public VncReader Reader() => new(new MemoryStream(ToArray()));
}

// The server's end of a zlib stream that lasts for the whole connection: every call packs the next piece and
// returns exactly the bytes that piece added to the stream.
internal sealed class Packer : IDisposable
{
    private readonly MemoryStream _packed = new();
    private readonly ZLibStream _zlib;

    public Packer(CompressionLevel level = CompressionLevel.Fastest)
    {
        _zlib = new ZLibStream(_packed, level, leaveOpen: true);
    }

    public byte[] Pack(byte[] data)
    {
        _packed.SetLength(0);
        _zlib.Write(data);

        // A sync flush: everything written so far can be unpacked, and the stream goes on.
        _zlib.Flush();
        return _packed.ToArray();
    }

    public void Dispose() => _zlib.Dispose();
}
