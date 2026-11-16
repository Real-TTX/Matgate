using System.Numerics;
using Xunit;

namespace Matgate.Display.Video.Tests;

// The converter against the arithmetic of the C engine it replaces (convert() in display/src/video.c), written
// here once more in the plainest way. Both of its ways through the pixels are held against it: one pixel at a
// time and whole vectors. How wide a vector is depends on the processor and on the runtime's settings - the
// script that runs the suite in the container repeats these tests with narrower vectors and with none.
public sealed unsafe class ColorConverterTests
{
    public enum Path
    {
        // What the caller gets.
        Public,
        Scalar,
        Vector,
    }

    public static TheoryData<Path> Paths() => new(Path.Public, Path.Scalar, Path.Vector);

    public static TheoryData<Path, int, int> Sizes()
    {
        var data = new TheoryData<Path, int, int>();
        foreach (var path in new[] { Path.Public, Path.Scalar, Path.Vector })
        {
            foreach (var (width, height) in new[] { (2, 2), (4, 6), (16, 16), (30, 18), (32, 2), (34, 34), (62, 30), (64, 64), (66, 66), (126, 10), (130, 66), (322, 200), (1920, 1080) })
            {
                data.Add(path, width, height);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AWholePictureIsConvertedLikeTheReference(Path path, int width, int height)
    {
        var random = new Random(width * 31 + height);

        // A row that is longer than the picture is wide: the bytes behind it are not pixels.
        var stride = width * 4 + random.Next(4) * 4;
        var source = new byte[stride * height];
        random.NextBytes(source);
        using var actual = Garbage(width, height, 1);
        using var expected = Garbage(width, height, 1);
        Convert(path, source, stride, actual, 0, 0, width, height);
        Reference(source, stride, expected, 0, 0, width, height);
        AssertSame(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void AnyRectangleIsConvertedLikeTheReferenceAndNothingElseIsTouched(Path path)
    {
        var random = new Random(42);
        foreach (var (width, height) in new[] { (200, 120), (70, 34), (514, 130) })
        {
            var source = new byte[width * 4 * height];
            random.NextBytes(source);
            using var actual = Garbage(width, height, 2);
            using var expected = Garbage(width, height, 2);
            for (var i = 0; i < 400; i++)
            {
                // Odd corners, odd sizes, single pixels, the edges, and rectangles that reach beyond the picture.
                var x = random.Next(-6, width + 4);
                var y = random.Next(-6, height + 4);
                var rectangleWidth = random.Next(5) == 0 ? 1 : random.Next(0, width + 8);
                var rectangleHeight = random.Next(5) == 0 ? 1 : random.Next(0, height / 2);

                // What is in the frame buffer changes between two conversions.
                source[random.Next(source.Length)] ^= 0xFF;
                random.NextBytes(source.AsSpan(random.Next(source.Length - 64), 64));
                Convert(path, source, width * 4, actual, x, y, rectangleWidth, rectangleHeight);
                Reference(source, width * 4, expected, x, y, rectangleWidth, rectangleHeight);
                AssertSame(expected, actual, $"{width}x{height}, rectangle {i}: {x},{y} {rectangleWidth}x{rectangleHeight}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void TheRectangleIsWidenedToEvenCoordinates(Path path)
    {
        const int Width = 96;
        const int Height = 32;
        var source = new byte[Width * 4 * Height];
        source.AsSpan().Fill(255);
        using var frame = new I420Frame(Width, Height);

        // One pixel at an odd position: the block of two by two it belongs to is converted, nothing more.
        Convert(path, source, Width * 4, frame, 5, 3, 1, 1);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var inside = x is 4 or 5 && y is 2 or 3;
                Assert.Equal(inside ? 235 : 16, frame.Y[y * frame.StrideY + x]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void KnownColoursGetTheValuesOfBt601LimitedRange(Path path)
    {
        // Blue, green, red in the frame buffer; luma, U, V expected.
        var colours = new (byte Blue, byte Green, byte Red, byte Y, byte U, byte V)[]
        {
            (0, 0, 0, 16, 128, 128),
            (255, 255, 255, 235, 128, 128),
            (0, 0, 255, 82, 90, 240),
            (0, 255, 0, 144, 54, 34),
            (255, 0, 0, 41, 240, 110),
            (128, 128, 128, 126, 128, 128),
        };
        const int Width = 128;
        const int Height = 4;
        foreach (var colour in colours)
        {
            var source = new byte[Width * 4 * Height];
            for (var i = 0; i < source.Length; i += 4)
            {
                source[i] = colour.Blue;
                source[i + 1] = colour.Green;
                source[i + 2] = colour.Red;

                // The fourth byte means nothing.
                source[i + 3] = (byte)i;
            }

            using var frame = new I420Frame(Width, Height);
            Convert(path, source, Width * 4, frame, 0, 0, Width, Height);
            for (var y = 0; y < Height; y++)
            {
                Assert.All(frame.Y.Slice(y * frame.StrideY, Width).ToArray(), value => Assert.Equal(colour.Y, value));
            }

            for (var y = 0; y < Height / 2; y++)
            {
                Assert.All(frame.U.Slice(y * frame.StrideU, Width / 2).ToArray(), value => Assert.Equal(colour.U, value));
                Assert.All(frame.V.Slice(y * frame.StrideV, Width / 2).ToArray(), value => Assert.Equal(colour.V, value));
            }
        }
    }

    [Fact]
    public void ChromaIsTheAverageOfFourPixelsRoundedDown()
    {
        // Blue only. The four pixels of the first block add up to 7 - an average of 1.75, which the engine
        // takes as 1 - and those of the second to 8.
        var source = new byte[4 * 4 * 2];
        source[0] = 7;
        source[8] = 8;
        using var frame = new I420Frame(4, 2);
        ColorConverter.BgrxToI420(source, 16, frame, 0, 0, 4, 2);
        Assert.Equal(128, frame.U[0]);
        Assert.Equal(129, frame.U[1]);
        Assert.Equal(17, frame.Y[0]);
        Assert.Equal(16, frame.Y[1]);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void TheFrameCountsWhatReallyChanged(Path path)
    {
        const int Width = 160;
        const int Height = 32;
        var source = new byte[Width * 4 * Height];
        new Random(9).NextBytes(source);
        using var frame = new I420Frame(Width, Height);
        var version = frame.Version;
        var changed = frame.ChangedPixels;
        ColorConverter.BgrxToI420(source, Width * 4, frame, 3, 3, 10, 2);
        Assert.Equal(version + 1, frame.Version);

        // 3..13 becomes 2..14, 3..5 becomes 2..6.
        Assert.Equal(changed + 12 * 4, frame.ChangedPixels);

        // Nothing of the rectangle is in the picture: nothing happened.
        ColorConverter.BgrxToI420(source, Width * 4, frame, Width, 0, 10, 10);
        ColorConverter.BgrxToI420(source, Width * 4, frame, 0, 0, 0, 10);
        ColorConverter.BgrxToI420(source, Width * 4, frame, -20, -20, 10, 10);
        Assert.Equal(version + 1, frame.Version);

        // "Everything from here on", however large the numbers.
        ColorConverter.BgrxToI420(source, Width * 4, frame, 32, 16, int.MaxValue, int.MaxValue);
        Assert.Equal(version + 2, frame.Version);
        Assert.Equal(changed + 12 * 4 + (Width - 32) * 16, frame.ChangedPixels);

        // The same pixels once more: the picture is what it was, so it does not count as a change - whether
        // the whole picture is converted again or a part of it, by whichever way through the pixels.
        Convert(path, source, Width * 4, frame, 0, 0, Width, Height);
        version = frame.Version;
        Convert(path, source, Width * 4, frame, 0, 0, Width, Height);
        Convert(path, source, Width * 4, frame, 7, 5, 130, 9);
        ColorConverter.BgrxToI420(source, Width * 4, frame, 0, 0, Width, Height);
        Assert.Equal(version, frame.Version);

        // The fourth byte of a pixel is not part of the picture.
        source[3] ^= 0xFF;
        ColorConverter.BgrxToI420(source, Width * 4, frame, 0, 0, Width, Height);
        Assert.Equal(version, frame.Version);

        // One pixel, in each of the planes' reach: the last row, the last column.
        source[(Height - 1) * Width * 4 + (Width - 1) * 4 + 1] ^= 0x80;
        ColorConverter.BgrxToI420(source, Width * 4, frame, 0, 0, Width, Height);
        Assert.Equal(version + 1, frame.Version);
    }

    [Fact]
    public void ACallerThatWritesThePlanesItselfSaysSo()
    {
        using var frame = new I420Frame(64, 32);
        var version = frame.Version;
        frame.Y[5] = 200;
        frame.MarkChanged();
        Assert.Equal(version + 1, frame.Version);
        Assert.Equal(64 * 32, frame.ChangedPixels);
    }

    [Fact]
    public void WhatCannotBeConvertedIsRefused()
    {
        using var frame = new I420Frame(64, 32);
        var source = new byte[64 * 4 * 32];
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorConverter.BgrxToI420(source, 64 * 4 - 4, frame, 0, 0, 64, 32));
        Assert.Throws<ArgumentException>(() => ColorConverter.BgrxToI420(source.AsSpan(0, source.Length - 1), 64 * 4, frame, 0, 0, 64, 32));
        Assert.Throws<ArgumentNullException>(() => ColorConverter.BgrxToI420(source, 64 * 4, null!, 0, 0, 64, 32));

        // The last row may end with its last pixel: a source without the padding of a final stride is fine.
        var padded = new byte[(64 * 4 + 12) * 31 + 64 * 4];
        ColorConverter.BgrxToI420(padded, 64 * 4 + 12, frame, 0, 0, 64, 32);

        // A rectangle that does not reach the bottom only needs the rows it covers.
        ColorConverter.BgrxToI420(source.AsSpan(0, 64 * 4 * 8), 64 * 4, frame, 0, 0, 64, 8);

        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ColorConverter.BgrxToI420(source, 64 * 4, frame, 0, 0, 64, 32));
    }

    [Fact]
    public void AFrameHasEvenSizesAndStartsBlack()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new I420Frame(63, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new I420Frame(64, 31));
        Assert.Throws<ArgumentOutOfRangeException>(() => new I420Frame(0, 32));
        using var frame = new I420Frame(70, 34);
        Assert.Equal(70, frame.Width);
        Assert.Equal(34, frame.Height);
        Assert.True(frame.StrideY >= 70 && frame.StrideU >= 35 && frame.StrideV >= 35);
        Assert.Equal(frame.StrideY * 34, frame.Y.Length);
        Assert.Equal(frame.StrideU * 17, frame.U.Length);
        Assert.All(frame.Y.ToArray(), value => Assert.Equal(16, value));
        Assert.All(frame.U.ToArray(), value => Assert.Equal(128, value));
        Assert.All(frame.V.ToArray(), value => Assert.Equal(128, value));
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Y.Length);
    }

    [Fact]
    public void TheReportSaysWhichVectorsWereTested()
    {
        Report.Line($"ColorConverter: vectors of {Vector<byte>.Count} bytes, hardware accelerated: {Vector.IsHardwareAccelerated}");
    }

    private static void Convert(Path path, byte[] source, int stride, I420Frame target, int x, int y, int width, int height)
    {
        if (path == Path.Public)
        {
            ColorConverter.BgrxToI420(source, stride, target, x, y, width, height);
            return;
        }

        if (!ColorConverter.Clip(target, x, y, width, height, out var left, out var top, out var right, out var bottom))
        {
            return;
        }

        fixed (byte* pixels = source)
        {
            if (path == Path.Scalar)
            {
                ColorConverter.ConvertScalar(pixels, stride, target, left, top, right, bottom);
            }
            else
            {
                ColorConverter.ConvertVector(pixels, stride, target, left, top, right, bottom);
            }
        }
    }

    // display/src/video.c, convert(): every pixel on its own, in the order of the C code, for the blocks of
    // two by two pixels the rectangle touches.
    private static void Reference(byte[] source, int stride, I420Frame target, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var left = Math.Max(0, x) / 2 * 2;
        var top = Math.Max(0, y) / 2 * 2;
        var right = Math.Min(target.Width, (x + width + 1) / 2 * 2);
        var bottom = Math.Min(target.Height, (y + height + 1) / 2 * 2);
        var planeY = target.Y;
        var planeU = target.U;
        var planeV = target.V;
        for (var row = top; row < bottom; row += 2)
        {
            for (var column = left; column < right; column += 2)
            {
                var sumRed = 0;
                var sumGreen = 0;
                var sumBlue = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var pixel = (row + dy) * stride + (column + dx) * 4;
                        int blue = source[pixel];
                        int green = source[pixel + 1];
                        int red = source[pixel + 2];
                        planeY[(row + dy) * target.StrideY + column + dx] = (byte)(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
                        sumRed += red;
                        sumGreen += green;
                        sumBlue += blue;
                    }
                }

                sumRed /= 4;
                sumGreen /= 4;
                sumBlue /= 4;
                planeU[row / 2 * target.StrideU + column / 2] = (byte)(((-38 * sumRed - 74 * sumGreen + 112 * sumBlue + 128) >> 8) + 128);
                planeV[row / 2 * target.StrideV + column / 2] = (byte)(((112 * sumRed - 94 * sumGreen - 18 * sumBlue + 128) >> 8) + 128);
            }
        }
    }

    // A frame whose planes - padding included - hold arbitrary bytes, the same for the same seed: whatever
    // the converter writes outside the rectangle shows.
    private static I420Frame Garbage(int width, int height, int seed)
    {
        var frame = new I420Frame(width, height);
        var random = new Random(seed);
        random.NextBytes(frame.Y);
        random.NextBytes(frame.U);
        random.NextBytes(frame.V);
        return frame;
    }

    private static void AssertSame(I420Frame expected, I420Frame actual, string what = "")
    {
        Assert.True(expected.Y.SequenceEqual(actual.Y), "The luma plane differs. " + what + FirstDifference(expected.Y, actual.Y, expected.StrideY));
        Assert.True(expected.U.SequenceEqual(actual.U), "The U plane differs. " + what + FirstDifference(expected.U, actual.U, expected.StrideU));
        Assert.True(expected.V.SequenceEqual(actual.V), "The V plane differs. " + what + FirstDifference(expected.V, actual.V, expected.StrideV));
    }

    private static string FirstDifference(Span<byte> expected, Span<byte> actual, int stride)
    {
        var at = expected.CommonPrefixLength(actual);
        return at == expected.Length ? "" : $" First at x={at % stride}, y={at / stride}: {actual[at]} instead of {expected[at]}.";
    }
}
