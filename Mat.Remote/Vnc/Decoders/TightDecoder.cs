namespace Matgate.Display.Vnc;

// Tight (7) without JPEG: a rectangle is one colour, or its pixels - as they are, as indexes into a palette,
// or as the difference to what their neighbours predict - packed with one of four zlib streams that last for
// the whole connection. The server only uses JPEG for a client that names a quality level, which Matgate
// does not: the picture is re-encoded for the browser, and a lossy step before that would only cost quality.
internal sealed class TightDecoder : IDisposable
{
    // Below this many bytes the server does not bother to compress.
    private const int MinToCompress = 12;

    // A pixel is three bytes here - red, green, blue - with the pixel format Matgate asks for.
    private const int PixelSize = 3;

    private const int FilterCopy = 0;
    private const int FilterPalette = 1;
    private const int FilterGradient = 2;

    private readonly VncInflater?[] _streams = new VncInflater?[4];
    private readonly uint[] _palette = new uint[256];
    private byte[] _above = [];

    public void Decode(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
    {
        int control = reader.ReadByte();
        for (var stream = 0; stream < 4; stream++)
        {
            if ((control & (1 << stream)) != 0)
            {
                _streams[stream]?.Reset();
            }
        }

        var method = control >> 4;
        if (method == 8)
        {
            var colour = reader.Take(PixelSize);
            target.Fill(x, y, width, height, VncPixel.FromBgr(colour[2], colour[1], colour[0]));
            return;
        }

        if (method == 9)
        {
            throw new VncException("The VNC server sent a JPEG picture although Matgate did not ask for any.");
        }

        // The highest bit clear: packed with the zlib stream the next two bits name. 10 and 14 are the same
        // without zlib (a server that was told not to compress).
        bool compressed;
        bool filtered;
        if ((method & 8) == 0)
        {
            compressed = true;
            filtered = (method & 4) != 0;
        }
        else if (method is 10 or 14)
        {
            compressed = false;
            filtered = method == 14;
        }
        else
        {
            throw Broken();
        }

        var filter = filtered ? reader.ReadByte() : FilterCopy;
        var colours = 0;
        int rowSize;
        switch (filter)
        {
            case FilterCopy:
            case FilterGradient:
                rowSize = width * PixelSize;
                break;
            case FilterPalette:
                colours = reader.ReadByte() + 1;
                var entries = reader.Take(colours * PixelSize);
                for (var index = 0; index < colours; index++)
                {
                    _palette[index] = VncPixel.FromBgr(entries[index * 3 + 2], entries[index * 3 + 1], entries[index * 3]);
                }

                // Two colours take a bit a pixel, more take a byte.
                rowSize = colours <= 2 ? (width + 7) / 8 : width;
                break;
            default:
                throw Broken();
        }

        var size = (long)rowSize * height;
        VncInflater? inflater = null;
        if (size >= MinToCompress)
        {
            var length = ReadCompactLength(reader);
            if (compressed)
            {
                inflater = _streams[method & 3] ??= new VncInflater();
                inflater.Begin(reader, length);
            }
            else if (length != size)
            {
                throw Broken();
            }
        }

        if (filter == FilterGradient)
        {
            if (_above.Length < rowSize)
            {
                _above = new byte[rowSize];
            }

            _above.AsSpan(0, rowSize).Clear();
        }

        for (var row = 0; row < height; row++)
        {
            var data = inflater is null ? reader.Take(rowSize) : inflater.Take(rowSize);
            var destination = target.Row(x, y + row, width);
            switch (filter)
            {
                case FilterCopy:
                    for (var column = 0; column < width; column++)
                    {
                        destination[column] = VncPixel.FromBgr(data[column * 3 + 2], data[column * 3 + 1], data[column * 3]);
                    }

                    break;
                case FilterPalette when colours <= 2:
                    for (var column = 0; column < width; column++)
                    {
                        // The leftmost pixel is the highest bit.
                        var index = (data[column >> 3] >> (7 - (column & 7))) & 1;
                        if (index >= colours)
                        {
                            throw Broken();
                        }

                        destination[column] = _palette[index];
                    }

                    break;
                case FilterPalette:
                    for (var column = 0; column < width; column++)
                    {
                        int index = data[column];
                        if (index >= colours)
                        {
                            throw Broken();
                        }

                        destination[column] = _palette[index];
                    }

                    break;
                default:
                    Gradient(data, _above, destination);
                    break;
            }
        }

        inflater?.End();
    }

    public void Dispose()
    {
        foreach (var stream in _streams)
        {
            stream?.Dispose();
        }
    }

    // The server sent, for every colour of every pixel, how far it is from what the pixels to the left, above
    // and above-left predict (left + above - above-left, kept within 0 and 255). Outside the rectangle there
    // is nothing: zero. `above` holds the row before as red, green, blue and is replaced by this one.
    private static void Gradient(ReadOnlySpan<byte> differences, Span<byte> above, Span<uint> destination)
    {
        Span<int> left = stackalloc int[3];
        Span<int> aboveLeft = stackalloc int[3];
        left.Clear();
        aboveLeft.Clear();
        for (var column = 0; column < destination.Length; column++)
        {
            for (var colour = 0; colour < 3; colour++)
            {
                var offset = column * 3 + colour;
                var up = above[offset];
                var predicted = Math.Clamp(left[colour] + up - aboveLeft[colour], 0, 255);
                var value = (byte)(differences[offset] + predicted);
                aboveLeft[colour] = up;
                left[colour] = value;
                above[offset] = value;
            }

            destination[column] = VncPixel.FromBgr(above[column * 3 + 2], above[column * 3 + 1], above[column * 3]);
        }
    }

    // A length in one to three bytes: seven bits each, the lowest first, the top bit saying that more follow;
    // the third byte counts in full.
    private static int ReadCompactLength(VncReader reader)
    {
        int part = reader.ReadByte();
        var length = part & 0x7F;
        if ((part & 0x80) != 0)
        {
            part = reader.ReadByte();
            length |= (part & 0x7F) << 7;
            if ((part & 0x80) != 0)
            {
                length |= reader.ReadByte() << 14;
            }
        }

        return length;
    }

    private static VncException Broken() => new("The VNC server sent a picture that cannot be read (Tight).");
}
