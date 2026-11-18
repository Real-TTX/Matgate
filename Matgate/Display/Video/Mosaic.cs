using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Matgate.Display.Video;

// The rate controller's last resort: the picture with every block of n by n pixels replaced by its average.
// A picture like that costs a fraction of the bits whatever is in it - which a coarser quantiser does not
// achieve for everything (see RateController). It is ugly, and it is only shown while the screen changes faster
// than the connection can carry, and only where it changes (see H264Encoder); the moment it stands still the
// real picture is encoded again.
internal static unsafe class Mosaic
{
    // The grid every rectangle is rendered on: the encoder's macroblocks, which the blocks of every size
    // divide evenly.
    public const int Grid = 16;

    // block is 4, 8 or 16 luma pixels; a chroma sample covers two of them, so its blocks are half as wide.
    public static void Render(I420Frame source, I420Frame target, int block) =>
        Render(source, target, block, 0, 0, source.Width, source.Height);

    // The same for one rectangle of the picture; whatever of the target lies outside it stays what it is. The
    // rectangle begins on the grid and ends on it or at the edge of the picture, so a block comes out the
    // same whichever rectangle it is rendered with.
    public static void Render(I420Frame source, I420Frame target, int block, int left, int top, int right, int bottom)
    {
        if (block is not (4 or 8 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A mosaic has blocks of 4, 8 or 16 pixels.");
        }

        if (target.Width != source.Width || target.Height != source.Height)
        {
            throw new ArgumentException($"The picture is {source.Width} x {source.Height}, the target {target.Width} x {target.Height}.", nameof(target));
        }

        if (left < 0 || left >= right || right > source.Width || left % Grid != 0 || (right % Grid != 0 && right != source.Width))
        {
            throw new ArgumentOutOfRangeException(nameof(left), $"{left}..{right} is not a run of whole macroblocks in a picture {source.Width} wide.");
        }

        if (top < 0 || top >= bottom || bottom > source.Height || top % Grid != 0 || (bottom % Grid != 0 && bottom != source.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(top), $"{top}..{bottom} is not a run of whole macroblocks in a picture {source.Height} high.");
        }

        // The sides of a picture are even, so half of every edge is where the chroma planes have it.
        var width = right - left;
        var height = bottom - top;
        Plane(
            source.PointerY + (nint)top * source.StrideY + left, source.StrideY,
            target.PointerY + (nint)top * target.StrideY + left, target.StrideY, width, height, block);
        Plane(
            source.PointerU + (nint)(top / 2) * source.StrideU + left / 2, source.StrideU,
            target.PointerU + (nint)(top / 2) * target.StrideU + left / 2, target.StrideU, width / 2, height / 2, block / 2);
        Plane(
            source.PointerV + (nint)(top / 2) * source.StrideV + left / 2, source.StrideV,
            target.PointerV + (nint)(top / 2) * target.StrideV + left / 2, target.StrideV, width / 2, height / 2, block / 2);
    }

    private static void Plane(byte* source, int sourceStride, byte* target, int targetStride, int width, int height, int block)
    {
        // The columns of a row of blocks added up: at most 16 rows of 255, which fits into 16 bits.
        var rented = ArrayPool<ushort>.Shared.Rent(width);
        fixed (ushort* sums = rented)
        {
            for (var top = 0; top < height; top += block)
            {
                var rows = Math.Min(block, height - top);
                new Span<ushort>(sums, width).Clear();
                for (var row = 0; row < rows; row++)
                {
                    Add(source + (nint)(top + row) * sourceStride, sums, width);
                }

                var line = target + (nint)top * targetStride;
                for (var left = 0; left < width; left += block)
                {
                    var columns = Math.Min(block, width - left);
                    var total = 0;
                    for (var i = 0; i < columns; i++)
                    {
                        total += sums[left + i];
                    }

                    var count = rows * columns;
                    new Span<byte>(line + left, columns).Fill((byte)((total + count / 2) / count));
                }

                // All rows of a block are the same.
                for (var row = 1; row < rows; row++)
                {
                    Buffer.MemoryCopy(line, line + (nint)row * targetStride, width, width);
                }
            }
        }

        ArrayPool<ushort>.Shared.Return(rented);
    }

    private static void Add(byte* row, ushort* sums, int width)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var half = Vector<ushort>.Count;
            for (; x + Vector<byte>.Count <= width; x += Vector<byte>.Count)
            {
                Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(row + x), out var low, out var high);
                Unsafe.WriteUnaligned(sums + x, Unsafe.ReadUnaligned<Vector<ushort>>(sums + x) + low);
                Unsafe.WriteUnaligned(sums + x + half, Unsafe.ReadUnaligned<Vector<ushort>>(sums + x + half) + high);
            }
        }

        for (; x < width; x++)
        {
            sums[x] += row[x];
        }
    }
}
