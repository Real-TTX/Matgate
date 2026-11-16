using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Matgate.Display.Rdp.Graphics;

// RDP 6.0 bitmap compression (MS-RDPEGDI 2.2.2.5.1 and 3.1.9), the codec of compressed bitmaps with 32 bits per
// pixel. A format header byte, then the colour planes one after the other - alpha (optional), red, green, blue,
// or alpha, luma, orange chroma, green chroma - each either raw or run-length encoded line by line. As in every
// bitmap update the scan lines come bottom-up.
//
// The planes of one scan line are decoded side by side, so the decoder keeps one line per plane and writes every
// finished line straight into the framebuffer.
internal static class PlanarDecoder
{
    // The format header: colour loss level in the three low bits, then one flag per bit; the two high bits are reserved.
    private const int ColorLossLevelMask = 0x07;
    private const int ChromaSubsampling = 0x08;
    private const int RunLengthEncoded = 0x10;
    private const int NoAlpha = 0x20;

    public static bool Decode(ReadOnlySpan<byte> data, int width, int height, BitmapTarget target)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        var header = data[0];
        var colors = new ColorFormat(header & ColorLossLevelMask, (header & ChromaSubsampling) != 0, (header & NoAlpha) == 0);
        if (colors.Subsampled && colors.ColorLossLevel == 0)
        {
            // Only chroma can be subsampled, and without a colour loss level the planes are red, green and blue.
            return false;
        }

        // A subsampled chroma plane has one value for two columns and two lines; an odd last one stands alone.
        var chromaWidth = colors.Subsampled ? (width + 1) / 2 : width;
        var chromaHeight = colors.Subsampled ? (height + 1) / 2 : height;
        return (header & RunLengthEncoded) != 0
            ? DecodeRunLengthPlanes(data, width, height, chromaWidth, chromaHeight, colors, target)
            : DecodeRawPlanes(data, width, height, chromaWidth, chromaHeight, colors, target);
    }

    private static bool DecodeRawPlanes(ReadOnlySpan<byte> data, int width, int height, int chromaWidth, int chromaHeight, ColorFormat colors, BitmapTarget target)
    {
        var planeSize = (long)width * height;
        var chromaSize = (long)chromaWidth * chromaHeight;

        // The alpha plane comes first and is not needed: the framebuffer has no alpha.
        var lumaOrRed = 1 + (colors.HasAlpha ? planeSize : 0);
        var orangeOrGreen = lumaOrRed + planeSize;
        var greenOrBlue = orangeOrGreen + chromaSize;

        // One byte of padding may follow the planes; it is ignored like anything else after them.
        if (greenOrBlue + chromaSize > data.Length)
        {
            return false;
        }

        for (var row = target.FirstRow; row < target.FirstRow + target.RowCount; row++)
        {
            var line = height - 1 - row;
            var chromaLine = colors.Subsampled ? line / 2 : line;
            WriteRow(
                data.Slice((int)(lumaOrRed + ((long)line * width)), width),
                data.Slice((int)(orangeOrGreen + ((long)chromaLine * chromaWidth)), chromaWidth),
                data.Slice((int)(greenOrBlue + ((long)chromaLine * chromaWidth)), chromaWidth),
                target.Row(row),
                target.FirstColumn,
                colors);
        }

        return true;
    }

    private static bool DecodeRunLengthPlanes(ReadOnlySpan<byte> data, int width, int height, int chromaWidth, int chromaHeight, ColorFormat colors, BitmapTarget target)
    {
        // The planes follow each other without a length: where one begins is only known once the one before it
        // has been read to its end.
        var position = 1;
        if (colors.HasAlpha && !SkipPlane(data, ref position, width, height))
        {
            return false;
        }

        var lumaOrRedPosition = position;
        if (!SkipPlane(data, ref position, width, height))
        {
            return false;
        }

        var orangeOrGreenPosition = position;
        if (!SkipPlane(data, ref position, chromaWidth, chromaHeight))
        {
            return false;
        }

        var greenOrBluePosition = position;

        // One scan line per plane, and one more for the values of a line as they are written in the data.
        var lines = ArrayPool<byte>.Shared.Rent((width * 2) + (chromaWidth * 2));
        try
        {
            var lumaOrRed = lines.AsSpan(0, width);
            var orangeOrGreen = lines.AsSpan(width, chromaWidth);
            var greenOrBlue = lines.AsSpan(width + chromaWidth, chromaWidth);
            var written = lines.AsSpan(width + (chromaWidth * 2), width);
            for (var line = 0; line < height; line++)
            {
                if (!DecodeLine(data, ref lumaOrRedPosition, lumaOrRed, written, line == 0))
                {
                    return false;
                }

                if (!colors.Subsampled || (line & 1) == 0)
                {
                    if (!DecodeLine(data, ref orangeOrGreenPosition, orangeOrGreen, written, line == 0)
                        || !DecodeLine(data, ref greenOrBluePosition, greenOrBlue, written, line == 0))
                    {
                        return false;
                    }
                }

                var destination = target.Row(height - 1 - line);
                if (!destination.IsEmpty)
                {
                    WriteRow(lumaOrRed, orangeOrGreen, greenOrBlue, destination, target.FirstColumn, colors);
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lines);
        }
    }

    // Decodes one scan line of a run-length encoded plane (MS-RDPEGDI 3.1.9.2). On entry the line holds the scan
    // line before it: the values of every line but the first are deltas to that one. written is room for one
    // scan line.
    private static bool DecodeLine(ReadOnlySpan<byte> data, ref int position, Span<byte> line, Span<byte> written, bool first)
    {
        // First the segments are laid out as the values they stand for, then the deltas are added to the line
        // before in one go: a segment has fifteen raw values at most, too few to be worth a loop of their own.
        var values = first ? line : written[..line.Length];
        var x = 0;

        // What a run repeats: the last raw value of this scan line, zero as long as there was none.
        byte last = 0;
        while (x < values.Length)
        {
            if (!ReadSegment(data, ref position, values.Length - x, out var rawCount, out var runLength))
            {
                return false;
            }

            if (rawCount > 0)
            {
                data.Slice(position, rawCount).CopyTo(values[x..]);
                position += rawCount;
                x += rawCount;
                last = values[x - 1];
            }

            values.Slice(x, runLength).Fill(last);
            x += runLength;
        }

        if (!first)
        {
            AddDeltas(values, line);
        }

        return true;
    }

    // Adds a scan line of deltas, as they are written in the data, to the scan line before it.
    private static void AddDeltas(ReadOnlySpan<byte> written, Span<byte> line)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= line.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                // Delta, for every byte at once; the arithmetic of bytes wraps around as it has to.
                var value = new Vector<byte>(written[i..]);
                var delta = (value >> 1) ^ (Vector<byte>.Zero - (value & Vector<byte>.One));
                (new Vector<byte>(line[i..]) + delta).CopyTo(line[i..]);
            }
        }

        for (; i < line.Length; i++)
        {
            line[i] = unchecked((byte)(line[i] + Delta(written[i])));
        }
    }

    // A delta as it is written: its sign is the lowest bit, so that small deltas of either sign are small bytes.
    // An even byte is half of itself, an odd one minus (half of itself plus one).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Delta(int written) => (written >> 1) ^ -(written & 1);

    // Finds the end of a run-length encoded plane without decoding it.
    private static bool SkipPlane(ReadOnlySpan<byte> data, ref int position, int width, int height)
    {
        for (var line = 0; line < height; line++)
        {
            for (var x = 0; x < width;)
            {
                if (!ReadSegment(data, ref position, width - x, out var rawCount, out var runLength))
                {
                    return false;
                }

                position += rawCount;
                x += rawCount + runLength;
            }
        }

        return true;
    }

    // Reads the control byte of a segment (MS-RDPEGDI 2.2.2.5.1.2) and checks that the segment fits into what is
    // left of the scan line and that its raw values are there. On return position is at the raw values.
    private static bool ReadSegment(ReadOnlySpan<byte> data, ref int position, int room, out int rawCount, out int runLength)
    {
        rawCount = 0;
        runLength = 0;
        if (position >= data.Length)
        {
            return false;
        }

        var control = data[position++];
        runLength = control & 0x0F;
        rawCount = control >> 4;

        // A run is at least three long, which leaves the lengths 1 and 2 free to mean a long run without raw
        // values: 16 or 32 plus what would otherwise be the raw count.
        if (runLength == 1)
        {
            runLength = 16 + rawCount;
            rawCount = 0;
        }
        else if (runLength == 2)
        {
            runLength = 32 + rawCount;
            rawCount = 0;
        }

        // A zero control byte is forbidden; it would also be a segment that never gets anywhere.
        return control != 0 && rawCount + runLength <= room && rawCount <= data.Length - position;
    }

    // Combines one scan line of the three colour planes into framebuffer pixels. The chroma lines are narrower
    // than the luma line when the chroma is subsampled.
    private static void WriteRow(ReadOnlySpan<byte> lumaOrRed, ReadOnlySpan<byte> orangeOrGreen, ReadOnlySpan<byte> greenOrBlue,
        Span<uint> destination, int firstColumn, ColorFormat colors)
    {
        if (colors.ColorLossLevel == 0)
        {
            var red = lumaOrRed.Slice(firstColumn, destination.Length);
            var green = orangeOrGreen.Slice(firstColumn, destination.Length);
            var blue = greenOrBlue.Slice(firstColumn, destination.Length);
            var i = 0;
            if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
            {
                // A vector of bytes from each plane makes four vectors of pixels.
                var opaque = new Vector<uint>(Bgrx.Opaque);
                var step = Vector<uint>.Count;
                for (; i <= destination.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                {
                    Widen(new Vector<byte>(red[i..]), out var red0, out var red1, out var red2, out var red3);
                    Widen(new Vector<byte>(green[i..]), out var green0, out var green1, out var green2, out var green3);
                    Widen(new Vector<byte>(blue[i..]), out var blue0, out var blue1, out var blue2, out var blue3);
                    (opaque | (red0 << 16) | (green0 << 8) | blue0).CopyTo(destination[i..]);
                    (opaque | (red1 << 16) | (green1 << 8) | blue1).CopyTo(destination[(i + step)..]);
                    (opaque | (red2 << 16) | (green2 << 8) | blue2).CopyTo(destination[(i + (step * 2))..]);
                    (opaque | (red3 << 16) | (green3 << 8) | blue3).CopyTo(destination[(i + (step * 3))..]);
                }
            }

            for (; i < destination.Length; i++)
            {
                destination[i] = Bgrx.Store(Bgrx.Opaque | ((uint)red[i] << 16) | ((uint)green[i] << 8) | blue[i]);
            }

            return;
        }

        // AYCoCg (MS-RDPEGDI 3.1.9.1.2): R = Y + Co/2 - Cg/2, G = Y + Cg/2, B = Y - Co/2 - Cg/2, where Co and Cg
        // are 9-bit values that lost their low bits (the colour loss level) to fit a byte and to compress better.
        // Shifting back by one bit less gives Co/2 and Cg/2 directly, as a signed byte.
        var shift = colors.ColorLossLevel - 1;
        var chromaShift = colors.Subsampled ? 1 : 0;

        // Windows servers compute Co the wrong way round when the bitmap has no alpha, and the specification
        // tells clients to make up for it by swapping red and blue (note in 3.1.9.1.2). With an alpha plane the
        // transform is the one above.
        var orangeSign = colors.HasAlpha ? 1 : -1;
        for (var i = 0; i < destination.Length; i++)
        {
            var column = firstColumn + i;
            var chromaColumn = column >> chromaShift;
            int luma = lumaOrRed[column];
            var orange = unchecked((sbyte)(orangeOrGreen[chromaColumn] << shift)) * orangeSign;
            int green = unchecked((sbyte)(greenOrBlue[chromaColumn] << shift));
            var rest = luma - green;
            destination[i] = Bgrx.Store(Bgrx.Opaque | (Clamp(rest + orange) << 16) | (Clamp(luma + green) << 8) | Clamp(rest - orange));
        }
    }

    private static void Widen(Vector<byte> bytes, out Vector<uint> first, out Vector<uint> second, out Vector<uint> third, out Vector<uint> fourth)
    {
        Vector.Widen(bytes, out Vector<ushort> low, out Vector<ushort> high);
        Vector.Widen(low, out first, out second);
        Vector.Widen(high, out third, out fourth);
    }

    // The transform leaves the range of a byte for colours the colour loss moved: those take the nearest value.
    private static uint Clamp(int value) => (uint)Math.Clamp(value, 0, 255);

    private readonly record struct ColorFormat(int ColorLossLevel, bool Subsampled, bool HasAlpha);
}
