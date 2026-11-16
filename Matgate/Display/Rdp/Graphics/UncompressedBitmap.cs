namespace Matgate.Display.Rdp.Graphics;

// Bitmap data without BITMAP_COMPRESSION (MS-RDPBCGR 2.2.9.1.1.3.1.2.2): rows from the bottom up, every pixel a
// whole number of bytes.
internal static class UncompressedBitmap
{
    public static bool Decode(ReadOnlySpan<byte> data, int bitsPerPixel, int width, int height, ReadOnlySpan<uint> palette, BitmapTarget target)
    {
        int bytesPerPixel;
        switch (bitsPerPixel)
        {
            case 8:
                if (palette.Length < 256)
                {
                    return false;
                }

                bytesPerPixel = 1;
                break;

            case 15:
            case 16:
                bytesPerPixel = 2;
                break;

            case 24:
                bytesPerPixel = 3;
                break;

            case 32:
                bytesPerPixel = 4;
                break;

            default:
                return false;
        }

        // The specification pads every row to a multiple of four bytes. Servers pad the width itself to a multiple
        // of four, which makes the two the same; a sender that does neither is read the way FreeRDP and rdesktop
        // read it, with rows that follow each other directly. The length of the data tells which one it is.
        var rowBytes = (long)width * bytesPerPixel;
        var stride = (rowBytes + 3) & ~3L;
        if (data.Length < ((height - 1) * stride) + rowBytes)
        {
            stride = rowBytes;
            if (data.Length < height * stride)
            {
                return false;
            }
        }

        // Rows that are not visible need no look: nothing in an uncompressed row depends on another row.
        for (var row = target.FirstRow; row < target.FirstRow + target.RowCount; row++)
        {
            var destination = target.Row(row);
            var offset = ((height - 1 - row) * stride) + ((long)target.FirstColumn * bytesPerPixel);
            Bgrx.FromWire(data.Slice((int)offset, destination.Length * bytesPerPixel), destination, bitsPerPixel, palette);
        }

        return true;
    }
}
