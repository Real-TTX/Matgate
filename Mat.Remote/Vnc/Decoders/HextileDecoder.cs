namespace Matgate.Display.Vnc;

// Hextile (5): tiles of 16 x 16 pixels, each either raw or a background with rectangles on it.
internal static class HextileDecoder
{
    private const int Tile = 16;
    private const int Raw = 1;
    private const int BackgroundSpecified = 2;
    private const int ForegroundSpecified = 4;
    private const int AnySubrects = 8;
    private const int SubrectsColoured = 16;

    public static void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        // Both colours stay as they are from tile to tile until a tile names new ones. A server has to name
        // the background before it relies on it; one that does not gets black.
        var background = VncPixel.Opaque;
        var foreground = VncPixel.Opaque;
        for (var top = 0; top < height; top += Tile)
        {
            var tileHeight = Math.Min(Tile, height - top);
            for (var left = 0; left < width; left += Tile)
            {
                var tileWidth = Math.Min(Tile, width - left);
                var kind = reader.ReadByte();
                if ((kind & Raw) != 0)
                {
                    var pixels = reader.Take(tileWidth * tileHeight * 4);
                    for (var row = 0; row < tileHeight; row++)
                    {
                        pixels.Slice(row * tileWidth * 4, tileWidth * 4).CopyTo(target.Bytes(x + left, y + top + row, tileWidth));
                        VncPixel.MakeOpaque(target.Row(x + left, y + top + row, tileWidth));
                    }

                    continue;
                }

                if ((kind & BackgroundSpecified) != 0)
                {
                    background = VncPixel.FromWire(reader.Take(4));
                }

                target.Fill(x + left, y + top, tileWidth, tileHeight, background);
                if ((kind & ForegroundSpecified) != 0)
                {
                    foreground = VncPixel.FromWire(reader.Take(4));
                }

                if ((kind & AnySubrects) == 0)
                {
                    continue;
                }

                int count = reader.ReadByte();
                var coloured = (kind & SubrectsColoured) != 0;
                var size = coloured ? 6 : 2;
                var subrects = reader.Take(count * size);
                for (var index = 0; index < count; index++)
                {
                    var subrect = subrects.Slice(index * size, size);
                    var colour = foreground;
                    if (coloured)
                    {
                        colour = VncPixel.FromWire(subrect);
                        subrect = subrect[4..];
                    }

                    // Position and size in half bytes: x and y, then width - 1 and height - 1.
                    var subLeft = subrect[0] >> 4;
                    var subTop = subrect[0] & 15;
                    var subWidth = (subrect[1] >> 4) + 1;
                    var subHeight = (subrect[1] & 15) + 1;
                    if (subLeft + subWidth > tileWidth || subTop + subHeight > tileHeight)
                    {
                        throw new VncException("The VNC server sent a picture that does not fit its own rectangle (Hextile).");
                    }

                    target.Fill(x + left + subLeft, y + top + subTop, subWidth, subHeight, colour);
                }
            }
        }
    }
}
