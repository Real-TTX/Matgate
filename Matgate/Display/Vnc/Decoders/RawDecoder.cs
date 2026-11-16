using System.Runtime.InteropServices;

namespace Matgate.Display.Vnc;

// Raw (0): the pixels as they are, row after row - already in the framebuffer's own layout, so they are read
// from the line straight into it.
internal static class RawDecoder
{
    public static void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        if (x == 0 && width == target.Width)
        {
            // Rows that span the desktop lie one behind the other in memory: one read for all of them.
            var block = target.Pixels.AsSpan(y * width * 4, width * height * 4);
            reader.ReadExactly(block);
            VncPixel.MakeOpaque(MemoryMarshal.Cast<byte, uint>(block));
            return;
        }

        for (var row = 0; row < height; row++)
        {
            reader.ReadExactly(target.Bytes(x, y + row, width));
            VncPixel.MakeOpaque(target.Row(x, y + row, width));
        }
    }
}
