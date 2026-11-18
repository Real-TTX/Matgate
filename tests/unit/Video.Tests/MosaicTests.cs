using Xunit;

namespace Matgate.Display.Video.Tests;

// The picture averaged over blocks, for the whole of it and for a rectangle: the rectangle comes out exactly
// as it does in the whole picture, and nothing outside of it is touched.
public sealed class MosaicTests
{
    // Neither side a multiple of 16: the last macroblocks are cut off by the edge.
    private const int Width = 88;
    private const int Height = 56;

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryBlockIsItsAverage(int block)
    {
        using var frame = Noise(1);
        using var mosaic = new I420Frame(Width, Height);
        Mosaic.Render(frame, mosaic, block);
        AssertAverages(frame.Y, mosaic.Y, frame.StrideY, Width, Height, block);
        AssertAverages(frame.U, mosaic.U, frame.StrideU, Width / 2, Height / 2, block / 2);
        AssertAverages(frame.V, mosaic.V, frame.StrideV, Width / 2, Height / 2, block / 2);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void ARectangleComesOutAsInTheWholePictureAndNothingElseIsTouched(int block)
    {
        using var frame = Noise(2);
        using var whole = new I420Frame(Width, Height);
        Mosaic.Render(frame, whole, block);
        foreach (var (left, top, right, bottom) in new[] { (0, 0, 16, 16), (16, 16, 64, 48), (80, 0, Width, 32), (0, 48, Width, Height), (48, 32, Width, Height), (0, 0, Width, Height) })
        {
            // The target holds something else, to tell what was written.
            using var target = Noise(3);
            using var before = new I420Frame(Width, Height);
            before.CopyFrom(target);
            Mosaic.Render(frame, target, block, left, top, right, bottom);
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var expected = x >= left && x < right && y >= top && y < bottom ? whole : before;
                    Assert.True(
                        target.Y[y * target.StrideY + x] == expected.Y[y * expected.StrideY + x]
                        && target.U[y / 2 * target.StrideU + x / 2] == expected.U[y / 2 * expected.StrideU + x / 2]
                        && target.V[y / 2 * target.StrideV + x / 2] == expected.V[y / 2 * expected.StrideV + x / 2],
                        $"Blocks of {block}, rectangle {left},{top}..{right},{bottom}: the pixel at {x},{y} is wrong.");
                }
            }
        }
    }

    [Fact]
    public void WhatIsNotARunOfWholeMacroblocksIsRefused()
    {
        using var frame = new I420Frame(Width, Height);
        using var target = new I420Frame(Width, Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => Mosaic.Render(frame, target, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mosaic.Render(frame, target, 12, 0, 0, 16, 16));
        foreach (var (left, top, right, bottom) in new[]
        {
            (8, 0, 32, 16), (0, 8, 32, 16), (0, 0, 24, 16), (0, 0, 16, 24), (16, 0, 16, 16), (0, 16, 16, 16),
            (-16, 0, 16, 16), (0, -16, 16, 16), (0, 0, Width + 8, 16), (0, 0, 16, Height + 8), (0, 0, 96, 16), (0, 0, 16, 64),
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Mosaic.Render(frame, target, 8, left, top, right, bottom));
        }

        using var other = new I420Frame(Width, Height + 16);
        Assert.Throws<ArgumentException>(() => Mosaic.Render(frame, other, 8));
    }

    private static I420Frame Noise(int seed)
    {
        var frame = new I420Frame(Width, Height);
        var random = new Random(seed);
        random.NextBytes(frame.Y);
        random.NextBytes(frame.U);
        random.NextBytes(frame.V);
        return frame;
    }

    private static void AssertAverages(Span<byte> source, Span<byte> mosaic, int stride, int width, int height, int block)
    {
        for (var top = 0; top < height; top += block)
        {
            for (var left = 0; left < width; left += block)
            {
                var rows = Math.Min(block, height - top);
                var columns = Math.Min(block, width - left);
                var total = 0;
                for (var y = top; y < top + rows; y++)
                {
                    for (var x = left; x < left + columns; x++)
                    {
                        total += source[y * stride + x];
                    }
                }

                var average = (byte)((total + rows * columns / 2) / (rows * columns));
                for (var y = top; y < top + rows; y++)
                {
                    for (var x = left; x < left + columns; x++)
                    {
                        Assert.Equal(average, mosaic[y * stride + x]);
                    }
                }
            }
        }
    }
}
