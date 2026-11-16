namespace Matgate.Display.Vnc;

// The pointer's shape (pseudo-encoding -239): pixels in the framebuffer's format, then a bit for every pixel
// that says whether it belongs to the shape.
internal static class CursorDecoder
{
    // A pointer is 16 to 64 pixels across, 256 on a very dense screen. Anything above is not a pointer.
    public const int MaxSize = 256;

    // Fills `rgba` (width * height * 4 bytes: red, green, blue, alpha) and says whether any pixel of the shape
    // can be seen at all.
    public static bool Decode(VncReader reader, int width, int height, Span<byte> rgba)
    {
        for (var row = 0; row < height; row++)
        {
            var pixels = reader.Take(width * 4);
            var destination = rgba.Slice(row * width * 4, width * 4);
            for (var column = 0; column < width; column++)
            {
                destination[column * 4] = pixels[column * 4 + 2];
                destination[column * 4 + 1] = pixels[column * 4 + 1];
                destination[column * 4 + 2] = pixels[column * 4];
            }
        }

        var visible = false;
        var maskSize = (width + 7) / 8;
        for (var row = 0; row < height; row++)
        {
            // Every row starts at a byte; the leftmost pixel is the highest bit.
            var mask = reader.Take(maskSize);
            var destination = rgba.Slice(row * width * 4, width * 4);
            for (var column = 0; column < width; column++)
            {
                var shown = (mask[column >> 3] & (0x80 >> (column & 7))) != 0;
                destination[column * 4 + 3] = shown ? (byte)255 : (byte)0;
                visible |= shown;
            }
        }

        return visible;
    }

    // Takes a shape off the line that is not going to be shown.
    public static void Skip(VncReader reader, int width, int height)
    {
        reader.Skip((long)width * height * 4 + (long)((width + 7) / 8) * height);
    }
}
