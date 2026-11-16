using System.Numerics;
using System.Runtime.CompilerServices;

namespace Matgate.Display.Video;

// From the frame buffer of a remote desktop (BGRX, four bytes a pixel, blue first, top row first) to the I420
// picture the encoder takes: BT.601, limited range (luma 16..235, chroma 16..240), each chroma sample the
// average of a block of two by two pixels. The stream says so in its header (see H264Encoder), which makes the
// browser turn it back into the very same colours.
public static unsafe class ColorConverter
{
    // Converts the given rectangle of the picture and nothing else, so a small change on a large screen costs
    // little. The rectangle is widened to even coordinates (a chroma sample belongs to two columns and two
    // rows) and cut to the picture; stride is the number of bytes from one source row to the next.
    //
    // The frame only counts as changed when a pixel really came out different: a remote desktop that sends
    // the same region again leaves the picture what it was, and the encoder treats it as standing still.
    public static void BgrxToI420(ReadOnlySpan<byte> bgrx, int stride, I420Frame target, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(target.IsDisposed, target);
        if (stride < target.Width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "A source row holds at least four bytes for every pixel of the picture.");
        }

        if (!Clip(target, x, y, width, height, out var left, out var top, out var right, out var bottom))
        {
            return;
        }

        // The last row that is read ends with the last pixel of the rectangle, not with a full stride.
        if (bgrx.Length < (long)(bottom - 1) * stride + (long)right * 4)
        {
            throw new ArgumentException("The source is too small for the rectangle.", nameof(bgrx));
        }

        bool changed;
        fixed (byte* source = bgrx)
        {
            changed = Vector.IsHardwareAccelerated
                ? ConvertVector(source, stride, target, left, top, right, bottom)
                : ConvertScalar(source, stride, target, left, top, right, bottom);
        }

        if (changed)
        {
            target.MarkChanged(right - left, bottom - top);
        }
    }

    // The rectangle with even edges, inside the picture. False when nothing of it is left.
    internal static bool Clip(I420Frame target, int x, int y, int width, int height, out int left, out int top, out int right, out int bottom)
    {
        left = (int)Math.Max(0L, x) & ~1;
        top = (int)Math.Max(0L, y) & ~1;
        right = (int)Math.Min(target.Width, ((long)x + width + 1) & ~1L);
        bottom = (int)Math.Min(target.Height, ((long)y + height + 1) & ~1L);
        return width > 0 && height > 0 && left < right && top < bottom;
    }

    // One pixel after the other - the arithmetic of the C engine this replaces. Also what a rectangle gets
    // that is narrower than two vectors. True when it wrote anything that was not there before.
    internal static bool ConvertScalar(byte* source, int stride, I420Frame target, int left, int top, int right, int bottom)
    {
        var difference = 0;
        for (var y = top; y < bottom; y += 2)
        {
            var row0 = source + (nint)y * stride;
            var row1 = row0 + stride;
            var outY0 = target.PointerY + (nint)y * target.StrideY;
            var outY1 = outY0 + target.StrideY;
            var outU = target.PointerU + (nint)(y / 2) * target.StrideU;
            var outV = target.PointerV + (nint)(y / 2) * target.StrideV;
            for (var x = left; x < right; x += 2)
            {
                var pixel0 = row0 + x * 4;
                var pixel1 = row1 + x * 4;
                difference |= Store(outY0 + x, Luma(pixel0[2], pixel0[1], pixel0[0]));
                difference |= Store(outY0 + x + 1, Luma(pixel0[6], pixel0[5], pixel0[4]));
                difference |= Store(outY1 + x, Luma(pixel1[2], pixel1[1], pixel1[0]));
                difference |= Store(outY1 + x + 1, Luma(pixel1[6], pixel1[5], pixel1[4]));
                var blue = (pixel0[0] + pixel0[4] + pixel1[0] + pixel1[4]) / 4;
                var green = (pixel0[1] + pixel0[5] + pixel1[1] + pixel1[5]) / 4;
                var red = (pixel0[2] + pixel0[6] + pixel1[2] + pixel1[6]) / 4;
                difference |= Store(outU + x / 2, (byte)(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128));
                difference |= Store(outV + x / 2, (byte)(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128));
            }
        }

        return difference != 0;
    }

    // The same numbers, two vectors of pixels at a time. A rectangle whose width is not a multiple of that
    // gets its last step moved left to end exactly at the right edge: those pixels are converted twice, to
    // the same values, and nothing outside the rectangle is touched.
    internal static bool ConvertVector(byte* source, int stride, I420Frame target, int left, int top, int right, int bottom)
    {
        var half = Vector<byte>.Count;
        var step = 2 * half;
        if (right - left < step)
        {
            return ConvertScalar(source, stride, target, left, top, right, bottom);
        }

        var difference = Vector<byte>.Zero;
        for (var y = top; y < bottom; y += 2)
        {
            var row0 = source + (nint)y * stride;
            var row1 = row0 + stride;
            var outY0 = target.PointerY + (nint)y * target.StrideY;
            var outY1 = outY0 + target.StrideY;
            var outU = target.PointerU + (nint)(y / 2) * target.StrideU;
            var outV = target.PointerV + (nint)(y / 2) * target.StrideV;
            for (var next = left; next < right; next += step)
            {
                var x = Math.Min(next, right - step);
                RowPair(row0 + x * 4, row1 + x * 4, outY0 + x, outY1 + x, out var u0, out var v0, ref difference);
                RowPair(row0 + (x + half) * 4, row1 + (x + half) * 4, outY0 + x + half, outY1 + x + half, out var u1, out var v1, ref difference);
                Store(outU + x / 2, Vector.Narrow(u0, u1), ref difference);
                Store(outV + x / 2, Vector.Narrow(v0, v1), ref difference);
            }
        }

        return difference != Vector<byte>.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luma(int red, int green, int blue) => (byte)(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);

    // Writes the value and says in which bits it differs from what was there.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Store(byte* target, byte value)
    {
        var difference = *target ^ value;
        *target = value;
        return difference;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(byte* target, Vector<byte> value, ref Vector<byte> difference)
    {
        difference |= Unsafe.ReadUnaligned<Vector<byte>>(target) ^ value;
        Unsafe.WriteUnaligned(target, value);
    }

    // Writes one vector of luma for each of the two rows and hands back the chroma of the blocks they form
    // (half a vector of samples, in 16-bit lanes - the caller joins two of them).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RowPair(byte* row0, byte* row1, byte* outY0, byte* outY1, out Vector<ushort> u, out Vector<ushort> v, ref Vector<byte> difference)
    {
        Split(row0, out var blueA0, out var greenA0, out var redA0, out var blueB0, out var greenB0, out var redB0);
        Split(row1, out var blueA1, out var greenA1, out var redA1, out var blueB1, out var greenB1, out var redB1);
        Store(outY0, Vector.Narrow(Luma(redA0, greenA0, blueA0), Luma(redB0, greenB0, blueB0)), ref difference);
        Store(outY1, Vector.Narrow(Luma(redA1, greenA1, blueA1), Luma(redB1, greenB1, blueB1)), ref difference);

        var blue = Vector.AsVectorInt16(Vector.Narrow(Average(blueA0 + blueA1), Average(blueB0 + blueB1)));
        var green = Vector.AsVectorInt16(Vector.Narrow(Average(greenA0 + greenA1), Average(greenB0 + greenB1)));
        var red = Vector.AsVectorInt16(Vector.Narrow(Average(redA0 + redA1), Average(redB0 + redB1)));
        var rounding = new Vector<short>(128);
        u = Vector.AsVectorUInt16(Vector.ShiftRightArithmetic(blue * 112 - red * 38 - green * 74 + rounding, 8) + rounding);
        v = Vector.AsVectorUInt16(Vector.ShiftRightArithmetic(red * 112 - green * 94 - blue * 18 + rounding, 8) + rounding);
    }

    // One vector's worth of pixels (four vectors of bytes) as blue, green and red in 16-bit lanes: the first
    // half of the pixels in the A vectors, the second half in the B vectors.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Split(
        byte* pixels,
        out Vector<ushort> blueA, out Vector<ushort> greenA, out Vector<ushort> redA,
        out Vector<ushort> blueB, out Vector<ushort> greenB, out Vector<ushort> redB)
    {
        var size = Vector<byte>.Count;
        var pixels0 = Unsafe.ReadUnaligned<Vector<uint>>(pixels);
        var pixels1 = Unsafe.ReadUnaligned<Vector<uint>>(pixels + size);
        var pixels2 = Unsafe.ReadUnaligned<Vector<uint>>(pixels + 2 * size);
        var pixels3 = Unsafe.ReadUnaligned<Vector<uint>>(pixels + 3 * size);
        var low = new Vector<ushort>(0x00FF);

        // A pixel is the 32-bit number X R G B: its lower half holds green and blue, its upper half red.
        var greenBlueA = Vector.Narrow(pixels0, pixels1);
        var greenBlueB = Vector.Narrow(pixels2, pixels3);
        blueA = greenBlueA & low;
        blueB = greenBlueB & low;
        greenA = Vector.ShiftRightLogical(greenBlueA, 8);
        greenB = Vector.ShiftRightLogical(greenBlueB, 8);
        redA = Vector.Narrow(Vector.ShiftRightLogical(pixels0, 16), Vector.ShiftRightLogical(pixels1, 16)) & low;
        redB = Vector.Narrow(Vector.ShiftRightLogical(pixels2, 16), Vector.ShiftRightLogical(pixels3, 16)) & low;
    }

    // At most 56228, so it fits into the 16 bits of a lane.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<ushort> Luma(Vector<ushort> red, Vector<ushort> green, Vector<ushort> blue) =>
        Vector.ShiftRightLogical(red * 66 + green * 129 + blue * 25 + new Vector<ushort>(128), 8) + new Vector<ushort>(16);

    // The lanes hold one channel of two rows added up; two neighbouring lanes are one block of four pixels.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> Average(Vector<ushort> columns)
    {
        var pairs = Vector.AsVectorUInt32(columns);
        return Vector.ShiftRightLogical((pairs & new Vector<uint>(0xFFFF)) + Vector.ShiftRightLogical(pairs, 16), 2);
    }
}
