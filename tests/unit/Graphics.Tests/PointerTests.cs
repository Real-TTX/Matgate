using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Graphics.Tests;

public class PointerTests(ITestOutputHelper output)
{
    // The vectors of TestData/pointers.bin.br carry what FreeRDP's freerdp_image_copy_from_pointer_data makes
    // of the masks, as RGBA.
    //
    // At 1, 24 and 32 bits the pictures are FreeRDP's byte for byte. At 8 and 16 bits one case is decided
    // differently on purpose: a pixel whose AND bit is set and whose colour is neither black nor white. FreeRDP
    // makes it transparent at 24 bits but draws the colour at 8 and 16; here it is transparent at every depth
    // without alpha of its own - the AND bit says "keep the screen", and what a server leaves in the XOR mask
    // under it is not meant to be seen. No vector but the "noise" ones has such pixels.
    [Fact]
    public void EveryVectorDecodesToThePictureOfFreeRdp()
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var decidedDifferently = 0L;
        foreach (var vector in Vectors.Pointers)
        {
            var pixels = PointerDecoder.Decode(vector.BitsPerPixel, vector.Width, vector.Height, vector.Data, vector.Mask, vector.Palette);
            Assert.NotNull(pixels);
            Assert.Equal(vector.Expected.Length, pixels.Length);
            var andStride = (vector.Width + 15) / 16 * 2;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var x = i / 4 % vector.Width;
                var y = i / 4 / vector.Width;
                var actual = pixels.AsSpan(i, 4);
                var freerdp = vector.Expected.AsSpan(i, 4);
                if (actual.SequenceEqual(freerdp))
                {
                    continue;
                }

                // The one case: AND bit set (bottom-up mask), FreeRDP shows an opaque colour that is neither
                // black nor white, at 8 or 16 bits.
                var andBit = (vector.Mask[((vector.Height - 1 - y) * andStride) + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                var colour = freerdp[3] == 0xFF && !freerdp[..3].SequenceEqual((byte[])[0, 0, 0]) && !freerdp[..3].SequenceEqual((byte[])[255, 255, 255]);
                if (vector.BitsPerPixel is 8 or 16 && andBit && colour && vector.Name.Contains("/noise/", StringComparison.Ordinal))
                {
                    Assert.True(actual.SequenceEqual((byte[])[0, 0, 0, 0]), $"{vector.Name}: pixel ({x}, {y}) is {Convert.ToHexString(actual)}, expected transparent");
                    decidedDifferently++;
                    continue;
                }

                Assert.Fail($"{vector.Name}: pixel ({x}, {y}) is {Convert.ToHexString(actual)}, FreeRDP has {Convert.ToHexString(freerdp)}");
            }

            counts[vector.Group] = counts.GetValueOrDefault(vector.Group) + 1;
        }

        foreach (var (group, count) in counts)
        {
            output.WriteLine($"{group,-20} {count,5}");
        }

        output.WriteLine($"{"all",-20} {Vectors.Pointers.Count,5}");
        output.WriteLine($"{decidedDifferently} pixels under the AND mask transparent where FreeRDP draws a colour");
        Assert.True(Vectors.Pointers.Count > 100);
        Assert.True(decidedDifferently > 0);
    }

    [Fact]
    public void EveryVectorCutAtEveryLengthIsRefusedOrComplete()
    {
        long decodes = 0;
        Parallel.ForEach(Vectors.Pointers.Where(vector => vector.Data.Length < 40000), vector =>
        {
            // Both masks have exactly the length the size asks for: anything shorter is refused - except an AND
            // mask of no length at all, which means there is none.
            for (var length = 0; length < vector.Data.Length; length++)
            {
                Assert.Null(PointerDecoder.Decode(vector.BitsPerPixel, vector.Width, vector.Height, vector.Data.AsSpan(0, length), vector.Mask, vector.Palette));
            }

            for (var length = 1; length < vector.Mask.Length; length++)
            {
                Assert.Null(PointerDecoder.Decode(vector.BitsPerPixel, vector.Width, vector.Height, vector.Data, vector.Mask.AsSpan(0, length), vector.Palette));
            }

            Assert.NotNull(PointerDecoder.Decode(vector.BitsPerPixel, vector.Width, vector.Height, vector.Data, default, vector.Palette));
            Interlocked.Add(ref decodes, vector.Data.Length + vector.Mask.Length);
        });

        output.WriteLine($"{decodes} truncated masks");
        Assert.True(decodes > 100_000);
    }

    [Fact]
    public void RandomMasksNeverThrow()
    {
        var random = new Random(384);
        int[] depths = [0, 1, 2, 4, 8, 15, 16, 24, 32, 33, -1, 1000];
        var palette = new uint[256];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = (uint)random.Next() | 0xFF000000;
        }

        var decoded = 0;
        for (var round = 0; round < 50000; round++)
        {
            var depth = depths[random.Next(depths.Length)];
            var width = random.Next(10) == 0 ? random.Next(-5, 70000) : random.Next(0, 40);
            var height = random.Next(10) == 0 ? random.Next(-5, 70000) : random.Next(0, 40);
            // The lengths the size asks for, give or take two bytes (long: the sizes may be anything).
            var xorMask = new byte[random.Next(3) == 0 ? random.Next(0, 6000) : (int)Math.Clamp(((((long)width * Math.Max(depth, 0)) + 15) / 16 * 2 * height) + random.Next(-2, 3), 0, 100000)];
            var andMask = new byte[random.Next(3) == 0 ? random.Next(0, 200) : (int)Math.Clamp((((long)width + 15) / 16 * 2 * height) + random.Next(-2, 3), 0, 100000)];
            random.NextBytes(xorMask);
            random.NextBytes(andMask);
            var colors = random.Next(4) switch { 0 => default, 1 => palette.AsSpan(0, random.Next(0, 256)), _ => palette.AsSpan() };

            byte[]? pixels;
            try
            {
                pixels = PointerDecoder.Decode(depth, width, height, xorMask, andMask, colors);
            }
            catch (Exception exception)
            {
                Assert.Fail($"round {round}: {depth} bits, {width}x{height}, masks of {xorMask.Length} and {andMask.Length} bytes: {exception}");
                return;
            }

            if (pixels is not null)
            {
                Assert.Equal(width * height * 4, pixels.Length);
                decoded++;
            }
        }

        output.WriteLine($"50000 random pointers, {decoded} decoded to some picture");
        Assert.True(decoded > 1000);
    }

    // The examples of MS-RDPBCGR 2.2.9.1.1.4.4: a scan line of the XOR mask of a 3 x 3 pointer at 24 bits has
    // 10 bytes, a scan line of the AND mask of a 7 x 7 pointer has 2.
    [Fact]
    public void ScanLinesArePaddedToTwoBytes()
    {
        var xorMask = new byte[30];
        var andMask = new byte[6];
        for (var line = 0; line < 3; line++)
        {
            for (var x = 0; x < 3; x++)
            {
                // blue = column, green = line of the mask (counted from the bottom of the picture), red = 0x80
                xorMask[(line * 10) + (x * 3)] = (byte)(x + 1);
                xorMask[(line * 10) + (x * 3) + 1] = (byte)(line + 1);
                xorMask[(line * 10) + (x * 3) + 2] = 0x80;
            }

            // the padding byte is not zero: it must not show up anywhere
            xorMask[(line * 10) + 9] = 0xEE;
            andMask[(line * 2) + 1] = 0xFF;
        }

        var pixels = PointerDecoder.Decode(24, 3, 3, xorMask, andMask);
        Assert.NotNull(pixels);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                // bottom-up: the first line of the mask is the last row of the picture
                Assert.Equal((byte[])[0x80, (byte)(3 - y), (byte)(x + 1), 0xFF], pixels.AsSpan(((y * 3) + x) * 4, 4).ToArray());
            }
        }

        Assert.Null(PointerDecoder.Decode(24, 3, 3, xorMask.AsSpan(0, 29), andMask));
        Assert.Null(PointerDecoder.Decode(24, 3, 3, xorMask, andMask.AsSpan(0, 5)));
        Assert.NotNull(PointerDecoder.Decode(1, 7, 7, new byte[14], new byte[14]));
        Assert.Null(PointerDecoder.Decode(1, 7, 7, new byte[13], new byte[14]));
    }

    // The four combinations of a monochrome pointer: AND 0 / XOR 0 black, AND 0 / XOR 1 white, AND 1 / XOR 0
    // transparent, AND 1 / XOR 1 inverting. Both masks top-down, the leftmost pixel in the highest bit.
    [Fact]
    public void AMonochromePointerIsTopDownAndItsInvertingPixelsBecomeAChecker()
    {
        // 4 x 2; first line: black, white, transparent, inverting; second line: all inverting
        byte[] xorMask = [0b0101_0000, 0, 0b1111_0000, 0];
        byte[] andMask = [0b0011_0000, 0, 0b1111_0000, 0];
        var pixels = PointerDecoder.Decode(1, 4, 2, xorMask, andMask);
        Assert.Equal(
            (byte[])
            [
                0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 255,
                0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255,
            ],
            pixels);
    }

    // The checker starts with white at the top-left corner and is opaque: an inverting pixel has to be visible
    // on any background.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void WhiteUnderTheAndMaskInvertsAndBecomesAChecker(int bitsPerPixel)
    {
        const int Size = 5;
        var xorStride = ((Size * bitsPerPixel) + 15) / 16 * 2;
        var xorMask = new byte[xorStride * Size];
        xorMask.AsSpan().Fill(0xFF);
        if (bitsPerPixel == 16)
        {
            // 5-5-5: the sixteenth bit is not part of the colour, white is white without it
            for (var i = 1; i < xorMask.Length; i += 4)
            {
                xorMask[i] = 0x7F;
            }
        }

        var andMask = new byte[2 * Size];
        andMask.AsSpan().Fill(0xFF);
        var palette = new uint[256];
        palette[255] = 0xFFFFFFFF;
        palette[15] = 0xFFFFFFFF;
        var pixels = PointerDecoder.Decode(bitsPerPixel, Size, Size, xorMask, andMask, palette);
        Assert.NotNull(pixels);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var value = (byte)(((x + y) & 1) == 0 ? 255 : 0);
                Assert.Equal((byte[])[value, value, value, 255], pixels.AsSpan(((y * Size) + x) * 4, 4).ToArray());
            }
        }
    }

    // Without alpha of its own a shape is transparent wherever the AND bit is set, whatever the XOR mask has
    // there (unless it is white). With 32 bits the XOR mask decides, and only black under the mask is nothing.
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void AColourUnderTheAndMask(int bitsPerPixel)
    {
        // one pixel: red (palette entry 9), AND bit set
        byte[] xorMask = bitsPerPixel switch
        {
            4 => [0x90, 0],
            8 => [9, 0],
            16 => [0x00, 0x7C],
            24 => [0, 0, 0xFF, 0],
            _ => [0, 0, 0xFF, 0xFF],
        };
        var palette = new uint[256];
        palette[9] = 0xFFFF0000;
        var masked = PointerDecoder.Decode(bitsPerPixel, 1, 1, xorMask, (byte[])[0x80, 0], palette);
        var plain = PointerDecoder.Decode(bitsPerPixel, 1, 1, xorMask, (byte[])[0x00, 0], palette);
        Assert.Equal((byte[])[255, 0, 0, 255], plain);
        Assert.Equal(bitsPerPixel == 32 ? [255, 0, 0, 255] : (byte[])[0, 0, 0, 0], masked);
    }

    [Fact]
    public void ThirtyTwoBitsCarryTheirOwnAlpha()
    {
        // 2 x 1: half-transparent green, then opaque black under the AND mask (nothing)
        byte[] xorMask = [0x00, 0xFF, 0x00, 0x80, 0x00, 0x00, 0x00, 0xFF];
        Assert.Equal((byte[])[0, 255, 0, 0x80, 0, 0, 0, 0], PointerDecoder.Decode(32, 2, 1, xorMask, (byte[])[0x40, 0]));
        Assert.Equal((byte[])[0, 255, 0, 0x80, 0, 0, 0, 255], PointerDecoder.Decode(32, 2, 1, xorMask, default));
    }

    // "For 4 bpp, there are two palette indices per byte" (MS-RDPBCGR 2.2.9.1.1.4.5): the left pixel in the high
    // bits. FreeRDP does not decode this depth, so there is no vector for it.
    [Fact]
    public void FourBitsAreTwoPaletteIndicesPerByte()
    {
        var palette = new uint[16];
        for (var i = 0; i < 16; i++)
        {
            palette[i] = 0xFF000000 | (uint)(i * 0x110000) | (uint)((15 - i) * 0x11);
        }

        // 3 x 2, bottom-up: mask lines "1 2 3" and "4 5 6", each padded to two bytes
        byte[] xorMask = [0x12, 0x3F, 0x45, 0x6F];
        var pixels = PointerDecoder.Decode(4, 3, 2, xorMask, default, palette);
        Assert.Equal(
            (byte[])
            [
                0x44, 0, 0xBB, 255, 0x55, 0, 0xAA, 255, 0x66, 0, 0x99, 255,
                0x11, 0, 0xEE, 255, 0x22, 0, 0xDD, 255, 0x33, 0, 0xCC, 255,
            ],
            pixels);

        // A palette that does not have the index: malformed.
        Assert.Null(PointerDecoder.Decode(4, 3, 2, xorMask, default, palette.AsSpan(0, 6)));

        // No palette at all: the sixteen colours of a 4-bit Windows bitmap.
        Assert.Equal(
            (byte[])[0x80, 0, 0, 255, 0, 0x80, 0, 255, 0x80, 0x80, 0, 255, 0x80, 0, 0x80, 255, 0, 0x80, 0x80, 255, 0xC0, 0xC0, 0xC0, 255],
            PointerDecoder.Decode(4, 3, 2, (byte[])[0x56, 0x70, 0x12, 0x30], default));
    }

    [Fact]
    public void EightBitsNeedAPaletteThatHasTheIndex()
    {
        var palette = new uint[256];
        palette[200] = 0xFF102030;
        Assert.Equal((byte[])[0x10, 0x20, 0x30, 255], PointerDecoder.Decode(8, 1, 1, (byte[])[200, 0], default, palette));
        Assert.Null(PointerDecoder.Decode(8, 1, 1, (byte[])[200, 0], default));
        Assert.Null(PointerDecoder.Decode(8, 1, 1, (byte[])[200, 0], default, palette.AsSpan(0, 200)));
        Assert.NotNull(PointerDecoder.Decode(8, 1, 1, (byte[])[199, 0], default, palette.AsSpan(0, 200)));
    }

    // A 16-bit XOR mask is 5-5-5, whatever the session's colour depth: the sixteenth bit is ignored and five
    // bits fill eight by repeating their high bits.
    [Fact]
    public void SixteenBitsAreFiveFiveFive()
    {
        for (var value = 0; value < 0x10000; value += 7)
        {
            var pixels = PointerDecoder.Decode(16, 1, 1, (byte[])[(byte)(value & 0xFF), (byte)(value >> 8)], default);
            var red = (value >> 10) & 0x1F;
            var green = (value >> 5) & 0x1F;
            var blue = value & 0x1F;
            Assert.Equal((byte[])[(byte)((red << 3) | (red >> 2)), (byte)((green << 3) | (green >> 2)), (byte)((blue << 3) | (blue >> 2)), 255], pixels);
        }
    }

    [Fact]
    public void SizesBeyondALargePointerAreRefused()
    {
        var xorMask = new byte[385 * 4 * 385];
        var andMask = new byte[50 * 385];
        Assert.NotNull(PointerDecoder.Decode(32, 384, 384, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, 385, 384, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, 384, 385, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, -1, 4, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, 4, -1, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, 65535, 65535, xorMask, andMask));
        Assert.Null(PointerDecoder.Decode(32, int.MaxValue, int.MaxValue, xorMask, andMask));
    }

    [Theory]
    [InlineData(24, 0, 0)]
    [InlineData(32, 0, 16)]
    [InlineData(1, 16, 0)]
    public void AShapeWithoutPixelsIsAnEmptyPicture(int bitsPerPixel, int width, int height)
    {
        var pixels = PointerDecoder.Decode(bitsPerPixel, width, height, default, default);
        Assert.NotNull(pixels);
        Assert.Empty(pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(12)]
    [InlineData(48)]
    [InlineData(-8)]
    public void OtherDepthsAreRefused(int bitsPerPixel)
    {
        Assert.Null(PointerDecoder.Decode(bitsPerPixel, 4, 4, new byte[1000], new byte[100], new uint[256]));
        Assert.Null(PointerDecoder.Decode(bitsPerPixel, 0, 0, default, default));
    }
}
