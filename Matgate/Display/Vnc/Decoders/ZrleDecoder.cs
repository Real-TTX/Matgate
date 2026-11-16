namespace Matgate.Display.Vnc;

// ZRLE (16): tiles of 64 x 64 pixels - raw, one colour, a palette with packed indexes, or runs of a colour -
// all of it inside one zlib stream that lasts for the whole connection.
internal sealed class ZrleDecoder : IDisposable
{
    private const int Tile = 64;

    // With the pixel format Matgate asks for, a pixel in this encoding is three bytes: blue, green, red.
    private const int PixelSize = 3;

    private readonly VncInflater _stream = new();
    private readonly uint[] _palette = new uint[127];
    private readonly uint[] _tile = new uint[Tile * Tile];

    public void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        _stream.Begin(reader, reader.ReadUInt32());
        for (var top = 0; top < height; top += Tile)
        {
            var tileHeight = Math.Min(Tile, height - top);
            for (var left = 0; left < width; left += Tile)
            {
                DecodeTile(target, x + left, y + top, Math.Min(Tile, width - left), tileHeight);
            }
        }

        _stream.End();
    }

    public void Dispose() => _stream.Dispose();

    private void DecodeTile(VncFramebuffer target, int x, int y, int width, int height)
    {
        int kind = _stream.ReadByte();
        if (kind == 0)
        {
            for (var row = 0; row < height; row++)
            {
                var pixels = _stream.Take(width * PixelSize);
                var destination = target.Row(x, y + row, width);
                for (var column = 0; column < width; column++)
                {
                    destination[column] = VncPixel.FromBgr(pixels[column * 3], pixels[column * 3 + 1], pixels[column * 3 + 2]);
                }
            }

            return;
        }

        if (kind == 1)
        {
            target.Fill(x, y, width, height, ReadPixel());
            return;
        }

        if (kind <= 16)
        {
            ReadPalette(kind);
            DecodePacked(target, x, y, width, height, kind);
            return;
        }

        var pixelsInTile = width * height;
        var tile = _tile.AsSpan(0, pixelsInTile);
        if (kind == 128)
        {
            for (var position = 0; position < pixelsInTile;)
            {
                var colour = ReadPixel();
                var length = ReadRunLength(pixelsInTile - position);
                tile.Slice(position, length).Fill(colour);
                position += length;
            }
        }
        else if (kind >= 130)
        {
            var colours = kind - 128;
            ReadPalette(colours);
            for (var position = 0; position < pixelsInTile;)
            {
                int index = _stream.ReadByte();
                var length = 1;
                if ((index & 128) != 0)
                {
                    index &= 127;
                    length = ReadRunLength(pixelsInTile - position);
                }

                if (index >= colours)
                {
                    throw Broken();
                }

                tile.Slice(position, length).Fill(_palette[index]);
                position += length;
            }
        }
        else
        {
            // 17 to 127 and 129 mean nothing.
            throw Broken();
        }

        // Runs go on from one row into the next, so the tile is put together first and drawn then.
        for (var row = 0; row < height; row++)
        {
            tile.Slice(row * width, width).CopyTo(target.Row(x, y + row, width));
        }
    }

    private void DecodePacked(VncFramebuffer target, int x, int y, int width, int height, int colours)
    {
        var bits = colours == 2 ? 1 : colours <= 4 ? 2 : 4;
        var mask = (1 << bits) - 1;
        for (var row = 0; row < height; row++)
        {
            // Every row starts at a byte; the leftmost pixel is in the highest bits.
            var packed = _stream.Take((width * bits + 7) / 8);
            var destination = target.Row(x, y + row, width);
            for (var column = 0; column < width; column++)
            {
                var bit = column * bits;
                var index = (packed[bit >> 3] >> (8 - bits - (bit & 7))) & mask;
                if (index >= colours)
                {
                    throw Broken();
                }

                destination[column] = _palette[index];
            }
        }
    }

    private uint ReadPixel()
    {
        var pixel = _stream.Take(PixelSize);
        return VncPixel.FromBgr(pixel[0], pixel[1], pixel[2]);
    }

    private void ReadPalette(int colours)
    {
        var bytes = _stream.Take(colours * PixelSize);
        for (var index = 0; index < colours; index++)
        {
            _palette[index] = VncPixel.FromBgr(bytes[index * 3], bytes[index * 3 + 1], bytes[index * 3 + 2]);
        }
    }

    // One more than the sum of the bytes that follow; a byte below 255 is the last one.
    private int ReadRunLength(int most)
    {
        var length = 1;
        while (true)
        {
            int part = _stream.ReadByte();
            length += part;
            if (length > most)
            {
                // Checked with every byte, so a row of 255s cannot count up for ever.
                throw Broken();
            }

            if (part != 255)
            {
                return length;
            }
        }
    }

    private static VncException Broken() => new("The VNC server sent a picture that cannot be read (ZRLE).");
}
