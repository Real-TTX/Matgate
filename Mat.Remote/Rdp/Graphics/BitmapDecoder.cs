using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Graphics;

// Decodes the rectangles of an RDP bitmap update (TS_BITMAP_DATA, MS-RDPBCGR 2.2.9.1.1.3.1.2.2) into the one
// framebuffer a session keeps. Everything in the data was written by the remote server: no length, run or
// coordinate is trusted, and a malformed rectangle ends with false, never with an exception.
public static class BitmapDecoder
{
    // Width and height are 16 bits on the wire; anything beyond is not a bitmap update.
    private const int MaxDimension = ushort.MaxValue;

    // TS_CD_HEADER (MS-RDPBCGR 2.2.9.1.1.3.1.2.3): four 16-bit fields in front of the compressed data.
    private const int CompressionHeaderSize = 8;

    // Decodes one TS_BITMAP_DATA rectangle into a BGRX framebuffer (4 bytes a pixel, B first, top-down,
    // stride = framebufferWidth * 4; the X byte is written as 0xFF).
    //
    // data: the bitmapDataStream exactly as it is in the update, including the TS_CD_HEADER when hasCompressionHeader.
    // bitsPerPixel: 8, 15, 16, 24 or 32. Compressed data is Interleaved RLE up to 24 bits and RDP 6.0 planar at 32.
    // width/height: the size of the bitmap itself. It may be larger than the destination rectangle (servers pad the
    // width to a multiple of four); the bitmap's top-left pixel belongs at (destLeft, destTop) and only the part
    // inside the rectangle is written. The rectangle may reach outside the framebuffer (also to the left and above):
    // it is clipped. destRight/destBottom are inclusive, as on the wire.
    // palette: needed at 8 bits per pixel, 256 entries, each a framebuffer pixel as a uint (0xFFRRGGBB).
    //
    // Returns false when the data is malformed; part of the rectangle may have been written by then. Bitmap data
    // that ends before the bitmap is complete counts as malformed (the rows it did contain are written).
    public static bool Decode(ReadOnlySpan<byte> data, int bitsPerPixel, bool compressed, bool hasCompressionHeader,
        int width, int height, Span<byte> framebuffer, int framebufferWidth, int framebufferHeight,
        int destLeft, int destTop, int destRight, int destBottom, ReadOnlySpan<uint> palette = default)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            return false;
        }

        if (!BitmapTarget.TryCreate(framebuffer, framebufferWidth, framebufferHeight, width, height, destLeft, destTop, destRight, destBottom, out var target))
        {
            return false;
        }

        if (!compressed)
        {
            return UncompressedBitmap.Decode(data, bitsPerPixel, width, height, palette, target);
        }

        if (hasCompressionHeader)
        {
            if (data.Length < CompressionHeaderSize)
            {
                return false;
            }

            // cbCompMainBodySize is the length of the compressed data; the other three fields say nothing the
            // decoder does not find out by itself (and the first one must be zero anyway).
            var mainBodySize = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            if (mainBodySize > data.Length - CompressionHeaderSize)
            {
                return false;
            }

            data = data.Slice(CompressionHeaderSize, mainBodySize);
        }

        return bitsPerPixel == 32
            ? PlanarDecoder.Decode(data, width, height, target)
            : InterleavedRleDecoder.Decode(data, bitsPerPixel, width, height, palette, target);
    }
}
