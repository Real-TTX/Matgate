using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels;

// CF_DIB - a picture on the Windows clipboard: a BITMAPINFOHEADER (or one of its longer successors), the
// colour masks or the palette when the format has them, then the pixels. A .bmp file is the same thing behind
// a file header of 14 bytes that says where the pixels start, and that is what a browser can decode.
public static class DeviceIndependentBitmap
{
    public const uint FormatId = ClipboardFormats.Dib;

    private const int FileHeaderLength = 14;
    private const int InfoHeaderLength = 40;
    private const uint BitFields = 3;
    private const uint AlphaBitFields = 6;

    // Clipboard data to a .bmp file, or null when the data is not a bitmap this understands.
    public static byte[]? ToBmp(ReadOnlySpan<byte> dib)
    {
        if (!TryPixelOffset(dib, out var pixels))
        {
            return null;
        }

        var bmp = new byte[FileHeaderLength + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)(FileHeaderLength + pixels));
        dib.CopyTo(bmp.AsSpan(FileHeaderLength));
        return bmp;
    }

    // A .bmp file to clipboard data, or null when it is not one. Without the file header nothing says where
    // the pixels start, so they have to follow the palette directly: a gap the file left there is closed.
    public static byte[]? FromBmp(ReadOnlySpan<byte> bmp)
    {
        if (bmp.Length < FileHeaderLength || bmp[0] != (byte)'B' || bmp[1] != (byte)'M')
        {
            return null;
        }

        var dib = bmp[FileHeaderLength..];
        if (!TryPixelOffset(dib, out var pixels))
        {
            return null;
        }

        var stated = BinaryPrimitives.ReadUInt32LittleEndian(bmp[10..]);
        if (stated == 0 || stated == (uint)(FileHeaderLength + pixels))
        {
            return dib.ToArray();
        }

        if (stated < (uint)(FileHeaderLength + pixels) || stated > (uint)bmp.Length)
        {
            return null;
        }

        var data = bmp[(int)stated..];
        var packed = new byte[pixels + data.Length];
        dib[..pixels].CopyTo(packed);
        data.CopyTo(packed.AsSpan(pixels));
        return packed;
    }

    // Where the pixels start, counted from the start of the header: after the header itself, the three colour
    // masks that follow a 40-byte header with BI_BITFIELDS (the longer headers hold them inside), and the
    // palette.
    private static bool TryPixelOffset(ReadOnlySpan<byte> dib, out int offset)
    {
        offset = 0;
        if (dib.Length < InfoHeaderLength)
        {
            return false;
        }

        var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var used = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (header < InfoHeaderLength || header > (uint)dib.Length || bits is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return false;
        }

        // A palette is as long as the header says, or - when it says nothing - as the bit depth allows.
        var colours = used != 0 ? used : bits <= 8 ? 1u << bits : 0;
        if (bits <= 8 && colours > 1u << bits)
        {
            return false;
        }

        long masks = header != InfoHeaderLength ? 0 : compression == BitFields ? 12 : compression == AlphaBitFields ? 16 : 0;
        var start = header + masks + colours * 4L;
        if (start > dib.Length)
        {
            return false;
        }

        offset = (int)start;
        return true;
    }
}
