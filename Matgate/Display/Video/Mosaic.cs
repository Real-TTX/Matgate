using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Matgate.Display.Video;

// The rate controller's last resort: the picture with every block of n by n pixels replaced by its average.
// A picture like that costs a fraction of the bits whatever is in it - which a coarser quantiser does not
// achieve for everything (see RateController). It is ugly, and it is only shown while the screen changes faster
// than the connection can carry; the moment it stands still the real picture is encoded again.
internal static unsafe class Mosaic
{
    // block is 4, 8 or 16 luma pixels; a chroma sample covers two of them, so its blocks are half as wide.
    public static void Render(I420Frame source, I420Frame target, int block)
    {
        Plane(source.PointerY, source.StrideY, target.PointerY, target.StrideY, source.Width, source.Height, block);
        Plane(source.PointerU, source.StrideU, target.PointerU, target.StrideU, source.Width / 2, source.Height / 2, block / 2);
        Plane(source.PointerV, source.StrideV, target.PointerV, target.StrideV, source.Width / 2, source.Height / 2, block / 2);
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
