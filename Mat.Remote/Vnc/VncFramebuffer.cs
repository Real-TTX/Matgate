using System.Numerics;
using System.Runtime.InteropServices;

namespace Matgate.Display.Vnc;

// A pixel as it lies in the framebuffer - blue, green, red, filler - held in one 32-bit value, so a decoder
// stores a pixel with one write. The value is in the machine's byte order: the bytes in memory are the same
// everywhere. The filler is always 255, which makes the picture an opaque BGRA one as well, whatever the
// server left in that byte.
internal static class VncPixel
{
    public static readonly uint Opaque = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;

    public static uint FromBgr(byte blue, byte green, byte red)
    {
        return BitConverter.IsLittleEndian
            ? 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue
            : ((uint)blue << 24) | ((uint)green << 16) | ((uint)red << 8) | 0xFF;
    }

    // Four bytes as the server sends a pixel in the format Matgate asked for.
    public static uint FromWire(ReadOnlySpan<byte> pixel) => MemoryMarshal.Read<uint>(pixel) | Opaque;

    public static void MakeOpaque(Span<uint> pixels)
    {
        var index = 0;
        if (Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
        {
            var filler = new Vector<uint>(Opaque);
            for (; index <= pixels.Length - Vector<uint>.Count; index += Vector<uint>.Count)
            {
                var piece = pixels.Slice(index, Vector<uint>.Count);
                (new Vector<uint>(piece) | filler).CopyTo(piece);
            }
        }

        for (; index < pixels.Length; index++)
        {
            pixels[index] |= Opaque;
        }
    }
}

// The desktop's picture the decoders draw into: four bytes a pixel, rows from the top, no gaps between rows.
// Coordinates are checked by the caller against Contains before anything is drawn - a rectangle from the
// network never reaches the methods below unchecked.
internal sealed class VncFramebuffer
{
    public const int MaxSize = 16384;

    public VncFramebuffer(int width, int height)
    {
        if (width < 0 || height < 0 || width > MaxSize || height > MaxSize)
        {
            throw new VncException($"The VNC server announced a desktop of {width} x {height} pixels; the largest Matgate takes is {MaxSize} x {MaxSize}.");
        }

        Width = width;
        Height = height;
        try
        {
            Pixels = GC.AllocateUninitializedArray<byte>(width * height * 4);
        }
        catch (OutOfMemoryException)
        {
            throw new VncException($"There is not enough memory for a desktop of {width} x {height} pixels.");
        }

        // Black, until the server has sent the picture.
        MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan()).Fill(VncPixel.Opaque);
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public bool Contains(int x, int y, int width, int height)
    {
        return x >= 0 && y >= 0 && width >= 0 && height >= 0 && x + width <= Width && y + height <= Height;
    }

    // A copy of the picture as it is now.
    public byte[] Snapshot()
    {
        try
        {
            return (byte[])Pixels.Clone();
        }
        catch (OutOfMemoryException)
        {
            throw new VncException($"There is not enough memory for a desktop of {Width} x {Height} pixels.");
        }
    }

    public Span<byte> Bytes(int x, int y, int width) => Pixels.AsSpan((y * Width + x) * 4, width * 4);

    public Span<uint> Row(int x, int y, int width) => MemoryMarshal.Cast<byte, uint>(Bytes(x, y, width));

    public void Fill(int x, int y, int width, int height, uint pixel)
    {
        for (var row = 0; row < height; row++)
        {
            Row(x, y + row, width).Fill(pixel);
        }
    }

    // Moves a part of the picture; the two places may overlap.
    public void Copy(int sourceX, int sourceY, int width, int height, int x, int y)
    {
        if (y <= sourceY)
        {
            for (var row = 0; row < height; row++)
            {
                Bytes(sourceX, sourceY + row, width).CopyTo(Bytes(x, y + row, width));
            }

            return;
        }

        // Downwards: from the last row up, or a row would be overwritten before it is read.
        for (var row = height - 1; row >= 0; row--)
        {
            Bytes(sourceX, sourceY + row, width).CopyTo(Bytes(x, y + row, width));
        }
    }

    // What another picture shows in the corner both have - a desktop that changes its size keeps what was there.
    public void CopyFrom(VncFramebuffer other)
    {
        var width = Math.Min(Width, other.Width);
        var height = Math.Min(Height, other.Height);
        for (var row = 0; row < height; row++)
        {
            other.Bytes(0, row, width).CopyTo(Bytes(0, row, width));
        }
    }
}
