using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Matgate.Display.Rdp.Graphics;

// The framebuffer's pixel: four bytes B, G, R, X. Inside the decoders a pixel is one uint with blue in the lowest
// byte (0xXXRRGGBB), which is the same four bytes on a little-endian machine.
//
// The loops over whole rows run several pixels at a time where the processor can (System.Numerics.Vector, which
// is safe code); the measurements are in the test project's SpeedTests.
internal static class Bgrx
{
    // The X byte is always written as 0xFF: the H.264 encoder and the browser take the buffer as opaque.
    public const uint Opaque = 0xFF000000;

    // The vector loops store their results as they are, which is only right where a uint is stored blue first.
    private static bool Vectorized => Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    // 5-6-5 as RDP sends it at 16 bits per pixel: red in the high bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromRgb565(uint pixel)
    {
        var red = (pixel >> 11) & 0x1F;
        var green = (pixel >> 5) & 0x3F;
        var blue = pixel & 0x1F;

        // Replicating the high bits into the low ones maps the full range onto 0..255: 0x1F becomes 0xFF, not 0xF8.
        return Opaque
            | (((red << 3) | (red >> 2)) << 16)
            | (((green << 2) | (green >> 4)) << 8)
            | ((blue << 3) | (blue >> 2));
    }

    // 5-5-5 as RDP sends it at 15 bits per pixel; the sixteenth bit means nothing.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromRgb555(uint pixel)
    {
        var red = (pixel >> 10) & 0x1F;
        var green = (pixel >> 5) & 0x1F;
        var blue = pixel & 0x1F;
        return Opaque
            | (((red << 3) | (red >> 2)) << 16)
            | (((green << 3) | (green >> 2)) << 8)
            | ((blue << 3) | (blue >> 2));
    }

    // A pixel value as it has to be stored through a uint view of the framebuffer so that the bytes come out as
    // B, G, R, X on any machine. The JIT folds the test away.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Store(uint pixel) => BitConverter.IsLittleEndian ? pixel : BinaryPrimitives.ReverseEndianness(pixel);

    // Pixels packed as on the wire (1, 2, 3 or 4 bytes each, little-endian) into framebuffer pixels.
    // The caller has checked the palette (256 entries at 8 bits per pixel) and the length of the source.
    public static void FromWire(ReadOnlySpan<byte> source, Span<uint> destination, int bitsPerPixel, ReadOnlySpan<uint> palette)
    {
        switch (bitsPerPixel)
        {
            case 8:
                source = source[..destination.Length];
                for (var i = 0; i < source.Length; i++)
                {
                    destination[i] = Store(palette[source[i]] | Opaque);
                }

                break;

            case 15:
            case 16:
            {
                var pixels = MemoryMarshal.Cast<byte, ushort>(source[..(destination.Length * 2)]);
                var i = 0;
                if (Vectorized)
                {
                    for (; i <= pixels.Length - Vector<ushort>.Count; i += Vector<ushort>.Count)
                    {
                        Vector.Widen(new Vector<ushort>(pixels[i..]), out var low, out var high);
                        Expand(low, bitsPerPixel).CopyTo(destination[i..]);
                        Expand(high, bitsPerPixel).CopyTo(destination[(i + Vector<uint>.Count)..]);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    var pixel = (uint)(BitConverter.IsLittleEndian ? pixels[i] : BinaryPrimitives.ReverseEndianness(pixels[i]));
                    destination[i] = Store(bitsPerPixel == 16 ? FromRgb565(pixel) : FromRgb555(pixel));
                }

                break;
            }

            case 24:
                PackedBgr(source[..(destination.Length * 3)], destination, Opaque);
                for (var i = 0; !BitConverter.IsLittleEndian && i < destination.Length; i++)
                {
                    destination[i] = BinaryPrimitives.ReverseEndianness(destination[i]);
                }

                break;

            case 32:
            {
                // The fourth byte of a 32-bit pixel is alpha or padding; neither belongs in the framebuffer.
                var pixels = MemoryMarshal.Cast<byte, uint>(source[..(destination.Length * 4)]);
                var opaque = Store(Opaque);
                var i = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    var opaqueVector = new Vector<uint>(opaque);
                    for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                    {
                        (new Vector<uint>(pixels[i..]) | opaqueVector).CopyTo(destination[i..]);
                    }
                }

                for (; i < pixels.Length; i++)
                {
                    destination[i] = pixels[i] | opaque;
                }

                break;
            }
        }
    }

    // A row the RLE decoder holds as one value per pixel (the wire value, not yet a colour) into framebuffer pixels.
    public static void FromValues(ReadOnlySpan<uint> source, Span<uint> destination, int bitsPerPixel, ReadOnlySpan<uint> palette)
    {
        source = source[..destination.Length];
        switch (bitsPerPixel)
        {
            case 8:
                for (var i = 0; i < source.Length; i++)
                {
                    destination[i] = Store(palette[(int)(source[i] & 0xFF)] | Opaque);
                }

                break;

            case 15:
            case 16:
            {
                var i = 0;
                if (Vectorized)
                {
                    for (; i <= source.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                    {
                        Expand(new Vector<uint>(source[i..]), bitsPerPixel).CopyTo(destination[i..]);
                    }
                }

                for (; i < source.Length; i++)
                {
                    destination[i] = Store(bitsPerPixel == 16 ? FromRgb565(source[i]) : FromRgb555(source[i]));
                }

                break;
            }

            case 24:
                // At 24 bits the decoder already keeps finished pixels.
                if (BitConverter.IsLittleEndian)
                {
                    source.CopyTo(destination);
                }
                else
                {
                    BinaryPrimitives.ReverseEndianness(source, destination);
                }

                break;
        }
    }

    // Three bytes a pixel, blue first, into pixel values (not yet in the framebuffer's byte order), each with
    // the given X byte.
    public static void PackedBgr(ReadOnlySpan<byte> source, Span<uint> destination, uint opaque)
    {
        // Four bytes are read at a time and the one that belongs to the next pixel is dropped; the last pixel
        // has no next one to read into.
        var i = 0;
        for (var offset = 0; offset + 4 <= source.Length; i++, offset += 3)
        {
            destination[i] = opaque | (BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]) & 0x00FFFFFF);
        }

        for (var offset = i * 3; offset + 3 <= source.Length; i++, offset += 3)
        {
            destination[i] = opaque | ((uint)source[offset + 2] << 16) | ((uint)source[offset + 1] << 8) | source[offset];
        }
    }

    // FromRgb565 or FromRgb555 for as many pixels as a vector holds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> Expand(Vector<uint> pixel, int bitsPerPixel)
    {
        var five = new Vector<uint>(0x1F);
        var blue = pixel & five;
        Vector<uint> red;
        Vector<uint> green;
        if (bitsPerPixel == 16)
        {
            red = (pixel >> 11) & five;
            green = (pixel >> 5) & new Vector<uint>(0x3F);
            green = (green << 2) | (green >> 4);
        }
        else
        {
            red = (pixel >> 10) & five;
            green = (pixel >> 5) & five;
            green = (green << 3) | (green >> 2);
        }

        return new Vector<uint>(Opaque) | (((red << 3) | (red >> 2)) << 16) | (green << 8) | ((blue << 3) | (blue >> 2));
    }
}
