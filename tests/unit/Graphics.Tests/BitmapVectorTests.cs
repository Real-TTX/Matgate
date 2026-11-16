using System.Buffers.Binary;
using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Graphics.Tests;

// The differential tests: every vector in TestData/bitmaps.bin.br carries the picture FreeRDP's decoder makes
// of its bytes, and the decoder here must make the same picture, byte for byte.
public class BitmapVectorTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryVectorDecodesToThePictureOfFreeRdp()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var vector in Vectors.Bitmaps)
        {
            var framebuffer = new TestFramebuffer(vector.Width, vector.Height);
            Assert.True(framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height), vector.Name + ": not decoded");
            framebuffer.AssertGuardsIntact(vector.Name);
            framebuffer.AssertPicture(vector.Expected, vector.Width, 0, 0, vector.Width, vector.Height, vector.Name);
            counts[vector.Group] = counts.GetValueOrDefault(vector.Group) + 1;
        }

        foreach (var (group, count) in counts)
        {
            output.WriteLine($"{group,-20} {count,5}");
        }

        output.WriteLine($"{"all",-20} {Vectors.Bitmaps.Count,5}");
        Assert.True(Vectors.Bitmaps.Count > 2000);
    }

    // The comparison itself: one byte of one pixel that differs is noticed, in every codec.
    [Fact]
    public void TheComparisonNoticesASingleByte()
    {
        foreach (var name in new[] { "rle16/encoder/text/64x64", "rle24/encoder/noise/123x45", "planar/encoder/rle/gradient/17x9", "raw8/padded/lines/5x4" })
        {
            var vector = Vectors.Bitmaps.Single(vector => vector.Name == name);
            var framebuffer = new TestFramebuffer(vector.Width, vector.Height);
            Assert.True(framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height));
            for (var position = 0; position < vector.Expected.Length; position += 97)
            {
                var changed = (byte[])vector.Expected.Clone();
                changed[position] ^= 0x01;
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => framebuffer.AssertPicture(changed, vector.Width, 0, 0, vector.Width, vector.Height, name));
            }
        }
    }

    // A real update: the rectangle is as wide as the picture, the bitmap as wide as the padding makes it, and it
    // lands somewhere in a larger framebuffer. Nothing but the rectangle may change.
    [Fact]
    public void OnlyTheDestinationRectangleIsWritten()
    {
        foreach (var vector in Vectors.Bitmaps)
        {
            var framebuffer = new TestFramebuffer(vector.Width + 9, vector.Height + 7);
            Assert.True(framebuffer.Decode(vector, vector.Data, 5, 3, vector.ContentWidth, vector.ContentHeight), vector.Name + ": not decoded");
            framebuffer.AssertUntouchedOutside(5, 3, vector.ContentWidth, vector.ContentHeight, vector.Name);
            framebuffer.AssertPicture(vector.Expected, vector.Width, 5, 3, vector.ContentWidth, vector.ContentHeight, vector.Name);
        }
    }

    // The same data behind a TS_CD_HEADER, as a server sends it to a client that did not ask to have it left out.
    [Fact]
    public void TheCompressionHeaderIsSkipped()
    {
        foreach (var vector in Vectors.Bitmaps.Where(vector => vector.Compressed))
        {
            if (vector.Data.Length > ushort.MaxValue)
            {
                continue;
            }

            var data = new byte[vector.Data.Length + 8 + 3];
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)vector.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(vector.Width * ((vector.BitsPerPixel + 7) / 8)));
            vector.Data.CopyTo(data, 8);

            // Three bytes follow the compressed data that the header does not count: they are not part of it.
            data.AsSpan(8 + vector.Data.Length).Fill(0xF5);

            var framebuffer = new TestFramebuffer(vector.Width, vector.Height);
            Assert.True(framebuffer.Decode(vector, data, 0, 0, vector.Width, vector.Height, header: true), vector.Name + ": not decoded");
            framebuffer.AssertPicture(vector.Expected, vector.Width, 0, 0, vector.Width, vector.Height, vector.Name);
        }
    }

    // FreeRDP expands the six bits of green at 16 bits per pixel as (g << 2) + (g >> 3), which is not bit
    // replication and brightens green by up to 4. The vectors therefore carry two pictures: FreeRDP's decoding
    // expanded by bit replication (the expected one) and FreeRDP's own expansion. This pins the difference
    // down: red and blue are identical, and green differs by exactly the two formulas.
    [Fact]
    public void SixteenBitPicturesDifferFromFreeRdpOnlyInTheExpansionOfGreen()
    {
        var vectors = 0;
        foreach (var vector in Vectors.Bitmaps.Where(vector => vector.BitsPerPixel == 16))
        {
            Assert.NotNull(vector.Reference);
            var framebuffer = new TestFramebuffer(vector.Width, vector.Height);
            Assert.True(framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height));
            var pixels = framebuffer.Pixels;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var green = pixels[i + 1] >> 2;
                Assert.Equal((green << 2) | (green >> 4), pixels[i + 1]);
                Assert.Equal(Math.Min(255, (green << 2) + (green >> 3)), vector.Reference[i + 1]);
                Assert.Equal(vector.Reference[i], pixels[i]);
                Assert.Equal(vector.Reference[i + 2], pixels[i + 2]);
                Assert.Equal(0xFF, pixels[i + 3]);
            }

            vectors++;
        }

        Assert.True(vectors > 100);
    }

    // The six streams of the IronRDP project come with the pictures IronRDP expects of them: a third opinion,
    // and it agrees with FreeRDP and with the decoder here.
    [Fact]
    public void TheStreamsOfIronRdpDecodeToThePicturesIronRdpExpects()
    {
        var vectors = Vectors.Bitmaps.Where(vector => vector.Group == "planar/ironrdp").ToList();
        Assert.Equal(6, vectors.Count);
        foreach (var vector in vectors)
        {
            Assert.NotNull(vector.Reference);
            var framebuffer = new TestFramebuffer(vector.Width, vector.Height);
            Assert.True(framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height), vector.Name);
            framebuffer.AssertPicture(vector.Reference, vector.Width, 0, 0, vector.Width, vector.Height, vector.Name);
        }
    }

    // The destination rectangle hangs over every edge and every corner of the framebuffer, lies outside it
    // altogether, and is smaller than the bitmap: what is visible must be the right part of the picture, and
    // nothing else may be written.
    [Fact]
    public void TheRectangleIsClippedAtEveryEdgeOfTheFramebuffer()
    {
        var checkedPlacements = 0;
        foreach (var vector in Vectors.Bitmaps)
        {
            // Every codec and mode, but not every picture of it: the placements multiply the work.
            if (vector.Width > 200 || vector.Name.GetHashCode(StringComparison.Ordinal) % 3 != 0)
            {
                continue;
            }

            var width = vector.Width;
            var height = vector.Height;
            var framebufferWidth = width + 2;
            var framebufferHeight = height + 2;
            var framebuffer = new TestFramebuffer(framebufferWidth, framebufferHeight);
            var halfWidth = Math.Max(1, width / 2);
            var halfHeight = Math.Max(1, height / 2);
            (int Left, int Top)[] placements =
            [
                (-halfWidth, 1), (framebufferWidth - halfWidth, 1), (1, -halfHeight), (1, framebufferHeight - halfHeight),
                (-halfWidth, -halfHeight), (framebufferWidth - halfWidth, -halfHeight),
                (-halfWidth, framebufferHeight - halfHeight), (framebufferWidth - halfWidth, framebufferHeight - halfHeight),
                (-1, -1), (framebufferWidth - 1, framebufferHeight - 1),
                (-width, 0), (0, -height), (framebufferWidth, 0), (0, framebufferHeight), (-30000, -30000), (30000, 30000),
            ];

            foreach (var (left, top) in placements)
            {
                // The whole bitmap, and a rectangle that shows only a part of it.
                foreach (var (rectangleWidth, rectangleHeight) in new[] { (width, height), (halfWidth, halfHeight) })
                {
                    var what = $"{vector.Name} at ({left}, {top}), rectangle {rectangleWidth}x{rectangleHeight}";
                    framebuffer.Clear();
                    Assert.True(framebuffer.Decode(vector, vector.Data, left, top, rectangleWidth, rectangleHeight), what + ": not decoded");
                    framebuffer.AssertUntouchedOutside(left, top, rectangleWidth, rectangleHeight, what);
                    framebuffer.AssertPicture(vector.Expected, vector.Width, left, top, rectangleWidth, rectangleHeight, what);
                    checkedPlacements++;
                }
            }
        }

        output.WriteLine($"{checkedPlacements} placements");
        Assert.True(checkedPlacements > 10000);
    }

    // A rectangle larger than its bitmap gets the pixels there are and nothing more.
    [Fact]
    public void ARectangleLargerThanTheBitmapShowsTheBitmap()
    {
        var vector = Vectors.Bitmaps.First(vector => vector.Name == "rle24/encoder/gradient/17x9");
        var framebuffer = new TestFramebuffer(64, 32);
        Assert.True(framebuffer.Decode(vector, vector.Data, 4, 4, 50, 20));
        framebuffer.AssertUntouchedOutside(4, 4, vector.Width, vector.Height, vector.Name);
        framebuffer.AssertPicture(vector.Expected, vector.Width, 4, 4, vector.Width, vector.Height, vector.Name);
    }
}
