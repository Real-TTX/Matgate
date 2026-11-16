using Xunit;
using static Matgate.Display.Rdp.Orders.Tests.TestData;

namespace Matgate.Display.Rdp.Orders.Tests;

// The field encoding of primary orders (MS-RDPEGDI 2.2.2.2.1.1.2) at its edges: delta coordinates, field flag
// bytes left out, bounds kept and reused, the order type and the fields remembered per kind; the compact
// encodings of secondary orders; the framing; orders skipped by their length.
public sealed class FieldEncodingTests
{
    private const uint Black = 0xFF000000;
    private const uint White = 0xFFFFFFFF;

    // DstBlt with all five fields: left 100, top 50, 20 x 10, BLACKNESS.
    private static readonly byte[] DstBlt = [0x09, 0x00, 0x1F, 0x64, 0x00, 0x32, 0x00, 0x14, 0x00, 0x0A, 0x00, 0x00];

    [Fact]
    public void DeltaCoordinatesAddToThePreviousValues()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(200, 100);

        // The same order type again (no TS_TYPE_CHANGE), TS_DELTA_COORDINATES: left - 5, top + 3.
        Assert.True(test.Draw(framebuffer, 2, Concat(DstBlt, [0x11, 0x03, 0xFB, 0x03])));
        var order = test.Decoder.DstBltHistory;
        Assert.Equal((95, 53, 20, 10), (order.Left, order.Top, order.Width, order.Height));
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var inside = (x >= 100 && x < 120 && y >= 50 && y < 60) || (x >= 95 && x < 115 && y >= 53 && y < 63);
                Assert.Equal(inside ? Black : GuardedFramebuffer.Untouched, framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void CoordinatesStaySixteenBitValues()
    {
        var test = new TestDecoder();
        test.Draw(new GuardedFramebuffer(8, 8), 2, [0x09, 0x00, 0x01, 0xFF, 0x7F, 0x11, 0x01, 0x01]);
        Assert.Equal(-32768, test.Decoder.DstBltHistory.Left);
    }

    [Fact]
    public void ZeroFieldByteFlagsLeaveOutTrailingFieldBytes()
    {
        var test = new TestDecoder();

        // Mem3Blt has three field bytes; TS_ZERO_FIELD_BYTE_BIT1 says two of them are zero and not sent.
        test.Draw(new GuardedFramebuffer(8, 8), 1, [0x89, 0x0E, 0x20, 0x55]);
        Assert.Equal(0x55, test.Decoder.Mem3BltHistory.Rop);
        Assert.Equal(0x0E, test.Decoder.LastOrderType);

        // Both bits: no field byte at all, so no field either - and the next order follows directly.
        test.Draw(new GuardedFramebuffer(8, 8), 2, Concat([0xC9, 0x0E], [0x09, 0x00, 0x01, 0x07, 0x00]));
        Assert.Equal(7, test.Decoder.DstBltHistory.Left);

        // More zero bytes than the order has field bytes: none is read.
        var framebuffer = new GuardedFramebuffer(200, 100);
        Assert.True(test.Draw(framebuffer, 2, Concat(DstBlt, [0xC1])));
        framebuffer.AssertOnly(100, 50, 20, 10, Black, "DstBlt repeated without fields");
    }

    [Fact]
    public void BoundsAreKeptReusedAndUpdatedByDeltas()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(64, 64);

        // OpaqueRect over the whole framebuffer, bounded to (10, 10)-(19, 19) inclusive.
        byte[] first = [0x0D, 0x0A, 0x7F, 0x0F, 0x0A, 0x00, 0x0A, 0x00, 0x13, 0x00, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x40, 0x00, 0x11, 0x22, 0x33];
        Assert.True(test.Draw(framebuffer, 1, first, out var drawn));
        Assert.Equal(new DrawnArea(10, 10, 10, 10), drawn);
        framebuffer.AssertOnly(10, 10, 10, 10, Rgb(0x11, 0x22, 0x33), "bounded OpaqueRect");

        // TS_ZERO_BOUNDS_DELTAS: the same bounds again, only the red changes.
        test.Draw(framebuffer, 1, [0x25, 0x10, 0x44]);
        framebuffer.AssertOnly(10, 10, 10, 10, Rgb(0x44, 0x22, 0x33), "bounds reused");

        // Delta left and top (+5): (15, 15)-(19, 19).
        test.Draw(framebuffer, 1, [0x05, 0x10, 0x30, 0x05, 0x05, 0x55]);
        Assert.Equal((15, 15, 19, 19), test.Decoder.LastBounds);
        Assert.Equal(Rgb(0x55, 0x22, 0x33), framebuffer[15, 15]);
        Assert.Equal(Rgb(0x44, 0x22, 0x33), framebuffer[14, 14]);

        // Absolute and delta flag for the same side: the absolute one is ignored, one byte is read.
        test.Draw(framebuffer, 1, [0x05, 0x00, 0x11, 0xFE]);
        Assert.Equal((13, 15, 19, 19), test.Decoder.LastBounds);

        // Without TS_BOUNDS the kept bounds do not clip.
        framebuffer = new GuardedFramebuffer(64, 64);
        test.Draw(framebuffer, 1, [0x01, 0x10, 0x66]);
        framebuffer.AssertOnly(0, 0, 64, 64, Rgb(0x66, 0x22, 0x33), "unbounded OpaqueRect");
        Assert.Equal((13, 15, 19, 19), test.Decoder.LastBounds);
    }

    [Fact]
    public void TheFirstOrderTypeIsPatBlt()
    {
        // No TS_TYPE_CHANGE in the very first order: it is a PatBlt (solid brush, foreground zero, PATCOPY).
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(32, 32);
        Assert.True(test.Draw(framebuffer, 1, [0x01, 0x1F, 0x00, 0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00, 0xF0]));
        Assert.Equal(0x01, test.Decoder.LastOrderType);
        framebuffer.AssertOnly(5, 6, 7, 8, Black, "first order as PatBlt");
    }

    [Fact]
    public void EveryOrderTypeKeepsItsOwnFields()
    {
        var test = new TestDecoder();
        byte[] dstBlt = [0x09, 0x00, 0x1F, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00, 0xFF];
        byte[] opaqueRect = [0x09, 0x0A, 0x0F, 0x10, 0x00, 0x10, 0x00, 0x05, 0x00, 0x05, 0x00];
        test.Draw(new GuardedFramebuffer(32, 32), 2, Concat(dstBlt, opaqueRect));

        // A DstBlt with no fields at all is the last DstBlt again, whatever came in between.
        var framebuffer = new GuardedFramebuffer(32, 32);
        Assert.True(test.Draw(framebuffer, 1, [0x09, 0x00, 0x00]));
        framebuffer.AssertOnly(1, 2, 3, 4, White, "DstBlt from its history");
        Assert.Equal((16, 16, 5, 5), (test.Decoder.OpaqueRectHistory.Left, test.Decoder.OpaqueRectHistory.Top, test.Decoder.OpaqueRectHistory.Width, test.Decoder.OpaqueRectHistory.Height));
    }

    [Fact]
    public void ResetForgetsHistoryAndCaches()
    {
        var test = new TestDecoder();
        test.Draw(new GuardedFramebuffer(8, 8), 2, Concat(DstBlt, Load("cachebitmap2")));
        Assert.NotNull(test.Decoder.Bitmaps.Get(1, BitmapCache.WaitingListIndex));
        test.Decoder.Reset(32);
        Assert.Null(test.Decoder.Bitmaps.Get(1, BitmapCache.WaitingListIndex));
        Assert.Equal((0, 0, 0, 0), (test.Decoder.DstBltHistory.Left, test.Decoder.DstBltHistory.Top, test.Decoder.DstBltHistory.Width, test.Decoder.DstBltHistory.Height));
        Assert.Equal(0x01, test.Decoder.LastOrderType);
        Assert.Equal((0, 0, 0, 0), test.Decoder.LastBounds);
    }

    [Fact]
    public void CompactEncodingsOfSecondaryOrders()
    {
        Assert.Equal(127, Read([0x7F], (ref OrderReader r) => r.TwoByteUnsigned()));
        Assert.Equal(0x0102, Read([0x81, 0x02], (ref OrderReader r) => r.TwoByteUnsigned()));
        Assert.Equal(0x7FFF, Read([0xFF, 0xFF], (ref OrderReader r) => r.TwoByteUnsigned()));
        Assert.Equal(63, Read([0x3F], (ref OrderReader r) => r.FourByteUnsigned()));
        Assert.Equal(220, Read([0x40, 0xDC], (ref OrderReader r) => r.FourByteUnsigned()));
        Assert.Equal(0x0102, Read([0x80, 0x01, 0x02], (ref OrderReader r) => r.FourByteUnsigned()));
        Assert.Equal(0x01020304, Read([0xC1, 0x02, 0x03, 0x04], (ref OrderReader r) => r.FourByteUnsigned()));
        Assert.Equal(16, Read([0x10], (ref OrderReader r) => r.DeltaValue()));
        Assert.Equal(-1, Read([0x7F], (ref OrderReader r) => r.DeltaValue()));
        Assert.Equal(-64, Read([0x40], (ref OrderReader r) => r.DeltaValue()));
        Assert.Equal(391, Read([0x81, 0x87], (ref OrderReader r) => r.DeltaValue()));
        Assert.Equal(-240, Read([0xFF, 0x10], (ref OrderReader r) => r.DeltaValue()));
        Assert.Equal(-16384, Read([0xC0, 0x00], (ref OrderReader r) => r.DeltaValue()));

        // Reading past the end: zeros, and Failed.
        var reader = new OrderReader([0x81]);
        Assert.Equal(0x0100, reader.TwoByteUnsigned());
        Assert.True(reader.Failed);
        Assert.Equal(0u, reader.U32());
    }

    [Fact]
    public void TwoByteSignedEncodingOfGlyphOrigins()
    {
        // The specification's own examples: -0x1A1B is { 0xDA, 0x1B }, -2 is { 0x42 }. A Cache Glyph (revision 2)
        // order with one 8 x 1 glyph at x = -0x1A1B, y = -2 into cache 3, entry 9.
        byte[] order = [0x03, 0x03, 0x00, 0x23, 0x01, 0x03, 0x09, 0xDA, 0x1B, 0x42, 0x08, 0x01, 0xFF, 0x00, 0x00, 0x00];
        var test = new TestDecoder();
        test.Draw(new GuardedFramebuffer(8, 8), 1, order);
        var glyph = test.Decoder.Glyphs.Get(3, 9);
        Assert.NotNull(glyph);
        Assert.Equal((-0x1A1B, -2, 8, 1), (glyph.X, glyph.Y, glyph.Width, glyph.Height));
    }

    [Fact]
    public void FastPathAndSlowPathFraming()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(200, 100);
        Assert.True(test.Decoder.DecodeFastPath(Concat([0x01, 0x00], DstBlt), framebuffer.Pixels, 200, 100, test.Palette, out var drawn));
        Assert.Equal(new DrawnArea(100, 50, 20, 10), drawn);

        test.Decoder.Reset(32);
        framebuffer = new GuardedFramebuffer(200, 100);
        Assert.True(test.Decoder.DecodeSlowPath(Concat([0x00, 0x00, 0x01, 0x00, 0x00, 0x00], DstBlt), framebuffer.Pixels, 200, 100, test.Palette, out drawn));
        Assert.Equal(new DrawnArea(100, 50, 20, 10), drawn);
        framebuffer.AssertOnly(100, 50, 20, 10, Black, "slow-path DstBlt");

        // Too short for its own header.
        Assert.False(test.Decoder.DecodeFastPath([0x01], framebuffer.Pixels, 200, 100, test.Palette, out _));
        Assert.False(test.Decoder.DecodeSlowPath([0x00, 0x00, 0x01], framebuffer.Pixels, 200, 100, test.Palette, out _));
    }

    [Fact]
    public void SecondaryOrdersOfOtherKindsAreSkippedByTheirLength()
    {
        // A Cache Brush (never announced): five bytes of body, then the DstBlt is drawn as usual.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(200, 100);
        Assert.True(test.Draw(framebuffer, 2, Concat([0x03, 0xFE, 0xFF, 0x00, 0x00, 0x07, 1, 2, 3, 4, 5], DstBlt)));
        framebuffer.AssertOnly(100, 50, 20, 10, Black, "DstBlt after a skipped Cache Brush");
        Assert.Contains(test.Log, line => line.Contains("0x07"));
    }

    [Fact]
    public void FrameMarkersAreSkippedAndOffscreenSurfacesAreNotDrawnOn()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(200, 100);
        byte[] frameStart = [0x36, 0x00, 0x00, 0x00, 0x00];
        byte[] toOffscreen = [0x02, 0x01, 0x00];
        byte[] toScreen = [0x02, 0xFF, 0xFF];
        byte[] otherDstBlt = [0x09, 0x00, 0x01, 0x00, 0x00];
        Assert.False(test.Draw(framebuffer, 4, Concat(frameStart, toOffscreen, DstBlt, toScreen)));
        Assert.True(framebuffer.Snapshot().All(pixel => pixel == GuardedFramebuffer.Untouched));
        Assert.True(test.Draw(framebuffer, 1, otherDstBlt));
        framebuffer.AssertOnly(0, 50, 20, 10, Black, "DstBlt after switching back to the screen");
    }

    [Fact]
    public void AnUnknownPrimaryOrderEndsItsUpdate()
    {
        // A Polyline (0x16) cannot be read, so neither can anything after it; what came before is drawn.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(200, 100);
        byte[] otherDstBlt = [0x09, 0x00, 0x01, 0x00, 0x00];
        Assert.True(test.Draw(framebuffer, 3, Concat(DstBlt, [0x09, 0x16, 0x01, 0x00, 0x00], otherDstBlt)));
        framebuffer.AssertOnly(100, 50, 20, 10, Black, "DstBlt before the Polyline");
        Assert.Contains(test.Log, line => line.Contains("type 0x16"));
    }

    [Fact]
    public void HatchedBrushes()
    {
        // HS_VERTICAL: the line in column 4 of every eight in ForeColor (red), the rest in BackColor (blue),
        // anchored at the brush origin (3, 0).
        byte[] patBlt =
        [
            0x09, 0x01, 0xFF, 0x07,
            0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x08, 0x00, 0xF0,
            0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x03, 0x00, 0x02, 0x01,
        ];
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(32, 8);
        Assert.True(test.Draw(framebuffer, 1, patBlt));
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(((x - 3) & 7) == 4 ? Rgb(0xFF, 0, 0) : Rgb(0, 0, 0xFF), framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void PatternBrushes()
    {
        // The specification's own example: a line sloping down from the left, BrushHatch 0x01 the bottom row,
        // BrushExtra the seven rows above from the bottom up. A set bit is BackColor (white), a clear one
        // ForeColor (black), the brush anchored at (0, 0).
        byte[] patBlt =
        [
            0x09, 0x01, 0xFF, 0x0F,
            0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, 0xF0,
            0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80,
        ];
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(16, 16);
        Assert.True(test.Draw(framebuffer, 1, patBlt));
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                Assert.Equal((x & 7) == (y & 7) ? White : Black, framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void ColoursAtEveryDepth()
    {
        // OpaqueRect with the colour bytes 0x1F 0xF8 0x80: red, green, blue at 24 and 32 bits; a 5-6-5 or 5-5-5
        // pixel value at 16 and 15 bits; a palette index at 8 bits (the test palette is grey).
        byte[] opaqueRect = [0x09, 0x0A, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x1F, 0xF8, 0x80];
        (int Depth, uint Pixel)[] cases =
        [
            (32, Rgb(0x1F, 0xF8, 0x80)),
            (24, Rgb(0x1F, 0xF8, 0x80)),
            (16, Graphics.Bgrx.FromRgb565(0xF81F)),
            (15, Graphics.Bgrx.FromRgb555(0xF81F)),
            (8, Rgb(0x1F, 0x1F, 0x1F)),
        ];
        foreach (var (depth, pixel) in cases)
        {
            var test = new TestDecoder(depth);
            var framebuffer = new GuardedFramebuffer(2, 2);
            test.Draw(framebuffer, 1, opaqueRect);
            Assert.Equal(pixel, framebuffer[0, 0]);
        }
    }

    private delegate int ReadFunction(ref OrderReader reader);

    private static int Read(byte[] bytes, ReadFunction read)
    {
        var reader = new OrderReader(bytes);
        var value = read(ref reader);
        Assert.False(reader.Failed);
        Assert.Equal(0, reader.Remaining);
        return value;
    }
}
