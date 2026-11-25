namespace Matgate.Display.Rdp.Graphics;

// Turns the two masks of an RDP pointer shape into a picture a browser can show as a cursor: the colour pointer
// (MS-RDPBCGR 2.2.9.1.1.4.4, always a 24-bit XOR mask), the new pointer (2.2.9.1.1.4.5, any depth) and the large
// pointer (2.2.9.1.2.1.11, up to 384 x 384) all carry the same pair - an XOR mask with the colours and a 1-bit AND
// mask. Windows draws them as "screen AND and-mask, XOR xor-mask"; a picture with alpha can say all of that except
// the inverting pixel.
public static class PointerDecoder
{
    // The largest shape the protocol knows (LARGE_POINTER_FLAG_384x384). Sizes are 16 bits on the wire, and
    // nothing bigger than this is a pointer.
    private const int MaxSize = 384;

    private const uint OpaqueBlack = 0xFF000000;
    private const uint OpaqueWhite = 0xFFFFFFFF;

    // The sixteen colours of a 4-bit Windows bitmap, for a 4-bit shape that comes without a palette.
    private static ReadOnlySpan<uint> StandardColors =>
    [
        0xFF000000, 0xFF800000, 0xFF008000, 0xFF808000, 0xFF000080, 0xFF800080, 0xFF008080, 0xFFC0C0C0,
        0xFF808080, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00, 0xFF0000FF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF,
    ];

    // A pointer shape as RGBA (R first, straight alpha), top-down, width * height * 4 bytes; null when malformed.
    // A shape without pixels (width or height zero) is an empty array.
    //
    // xorBitsPerPixel: 1, 4, 8, 16, 24 or 32 (24 for the colour pointer). Masks as on the wire, every scan line
    // padded to two bytes, bottom-up - except at 1 bit per pixel, where servers send both masks top-down.
    // andMask may be empty: the shape is then opaque wherever the XOR mask does not say otherwise.
    // palette: for 8 and 4 bits per pixel, each entry a pixel as a uint (0xFFRRGGBB). Without a palette a 4-bit
    // shape is read with the sixteen standard colours and an 8-bit shape is malformed.
    //
    // 32 bits carry their own alpha. For the other depths a set AND bit makes the pixel transparent, except over a
    // white XOR pixel: that one inverts the screen, which no picture can do - it becomes a black and white checker
    // pattern, visible on any background, as other clients draw it.
    public static byte[]? Decode(int xorBitsPerPixel, int width, int height, ReadOnlySpan<byte> xorMask, ReadOnlySpan<byte> andMask,
        ReadOnlySpan<uint> palette = default)
    {
        if (width < 0 || height < 0 || width > MaxSize || height > MaxSize)
        {
            return null;
        }

        if (xorBitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return null;
        }

        if (width == 0 || height == 0)
        {
            return [];
        }

        var xorStride = ((width * xorBitsPerPixel) + 15) / 16 * 2;
        var andStride = (width + 15) / 16 * 2;
        if (xorMask.Length < xorStride * height || (!andMask.IsEmpty && andMask.Length < andStride * height))
        {
            return null;
        }

        if (xorBitsPerPixel == 4 && palette.IsEmpty)
        {
            palette = StandardColors;
        }

        // The specification calls every mask bottom-up. Windows sends the masks of a monochrome pointer top-down,
        // and FreeRDP and IronRDP read them so.
        var topDown = xorBitsPerPixel == 1;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var line = topDown ? y : height - 1 - y;
            var xorLine = xorMask.Slice(line * xorStride, xorStride);
            var andLine = andMask.IsEmpty ? default : andMask.Slice(line * andStride, andStride);
            var row = pixels.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                // 0xAARRGGBB
                uint color;
                switch (xorBitsPerPixel)
                {
                    case 1:
                        color = (xorLine[x >> 3] & (0x80 >> (x & 7))) != 0 ? OpaqueWhite : OpaqueBlack;
                        break;

                    case 4:
                    case 8:
                    {
                        // Two palette indices per byte at 4 bits, the left pixel in the high bits.
                        var index = xorBitsPerPixel == 8 ? xorLine[x] : (x & 1) == 0 ? xorLine[x >> 1] >> 4 : xorLine[x >> 1] & 0x0F;
                        if (index >= palette.Length)
                        {
                            return null;
                        }

                        color = palette[index] | Bgrx.Opaque;
                        break;
                    }

                    case 16:
                        // A 16-bit XOR mask is 5-5-5 like a 16-bit Windows bitmap, not the 5-6-5 of a 16-bit session.
                        color = Bgrx.FromRgb555((uint)(xorLine[x * 2] | (xorLine[(x * 2) + 1] << 8)));
                        break;

                    case 24:
                        color = Bgrx.Opaque | ((uint)xorLine[(x * 3) + 2] << 16) | ((uint)xorLine[(x * 3) + 1] << 8) | xorLine[x * 3];
                        break;

                    default:
                        color = ((uint)xorLine[(x * 4) + 3] << 24) | ((uint)xorLine[(x * 4) + 2] << 16) | ((uint)xorLine[(x * 4) + 1] << 8) | xorLine[x * 4];
                        break;
                }

                if (!andLine.IsEmpty && (andLine[x >> 3] & (0x80 >> (x & 7))) != 0)
                {
                    if (color == OpaqueWhite)
                    {
                        color = ((x + y) & 1) == 0 ? OpaqueWhite : OpaqueBlack;
                    }
                    else if (xorBitsPerPixel != 32 || color == OpaqueBlack)
                    {
                        // "Keep the screen, XOR nothing onto it". With alpha of its own a shape says with the
                        // XOR mask what it wants to show, so only black is taken as nothing there.
                        color = 0;
                    }
                }

                row[x * 4] = (byte)((color >> 16) & 0xFF);
                row[(x * 4) + 1] = (byte)((color >> 8) & 0xFF);
                row[(x * 4) + 2] = (byte)(color & 0xFF);
                row[(x * 4) + 3] = (byte)(color >> 24);
            }
        }

        return pixels;
    }
}
