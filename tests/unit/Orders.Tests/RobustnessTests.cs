using Xunit;
using static Matgate.Display.Rdp.Orders.Tests.TestData;

namespace Matgate.Display.Rdp.Orders.Tests;

// Everything in an orders update comes from the server. Whatever it sends - cut short, made up, or bent to point
// outside the framebuffer and the caches - no exception may escape, no byte outside the framebuffer may be
// written, and what is reported as drawn lies inside the framebuffer.
public sealed class RobustnessTests
{
    private static readonly string[] Examples =
    [
        "dstblt", "patblt", "opaquerect", "multiopaquerect", "scrblt", "memblt", "lineto", "glyphindex1", "glyphindex2",
        "cachebitmap", "cachebitmap2", "cachecolortable", "cacheglyph", "cacheglyph2",
    ];

    // All examples in one update, the caches first so that the primary orders find something. Two dumps need
    // their known slips mended to be whole orders: OpaqueRect gets the field flag for its Blue byte, ScrBlt the
    // high byte of nYSrc.
    private static byte[] Stream(out int count)
    {
        string[] order = ["cachebitmap", "cachebitmap2", "cachecolortable", "cacheglyph", "cacheglyph2", "dstblt", "patblt",
            "opaquerect", "multiopaquerect", "scrblt", "memblt", "lineto", "glyphindex1", "glyphindex2"];
        count = order.Length;
        return Concat(order.Select(name => name switch
        {
            "opaquerect" => Mend(Load(name), 2, 0x7C),
            "scrblt" => Concat(Load(name), [0x00]),
            _ => Load(name),
        }).ToArray());

        static byte[] Mend(byte[] bytes, int at, byte value)
        {
            bytes[at] = value;
            return bytes;
        }
    }

    [Fact]
    public void TheWholeStreamDecodesCompletely()
    {
        var test = new TestDecoder(16);
        var framebuffer = new GuardedFramebuffer(800, 600);
        var bytes = Stream(out var count);
        Assert.True(test.Draw(framebuffer, count, bytes));
        Assert.Equal(
            "DstBlt x1, PatBlt x1, ScrBlt x1, LineTo x1, OpaqueRect x1, MemBlt x1, MultiOpaqueRect x1, GlyphIndex x2, "
            + "CacheColorTable x1, CacheBitmapCompressed x1, CacheGlyph x2, CacheBitmapCompressedRev2 x1",
            test.Decoder.TotalSummary());

        // What the examples themselves leave undrawable, and nothing else.
        string[] expected = ["cached brush", "offscreen", "binary raster operation 0", "glyph that was never sent", "fragment that was never sent"];
        Assert.Equal(expected, test.Log.Select(line => expected.FirstOrDefault(line.Contains) ?? line));
    }

    [Fact]
    public void EveryExampleCutShortAtEveryLength()
    {
        foreach (var name in Examples)
        {
            var bytes = Load(name);
            foreach (var depth in new[] { 8, 16, 32 })
            {
                for (var length = 0; length <= bytes.Length; length++)
                {
                    Check(new TestDecoder(depth), new GuardedFramebuffer(64, 48), 1, bytes.AsSpan(0, length).ToArray(), $"{name} cut to {length}");
                }
            }
        }
    }

    [Fact]
    public void TheWholeStreamCutShortAtEveryLength()
    {
        var bytes = Stream(out var count);
        var test = new TestDecoder(16);
        for (var length = 0; length <= bytes.Length; length++)
        {
            // One decoder for all: whatever a broken update leaves in the history must not trip the next one.
            Check(test, new GuardedFramebuffer(800, 600), count, bytes.AsSpan(0, length).ToArray(), $"stream cut to {length}");
        }
    }

    [Fact]
    public void RandomBuffers()
    {
        var random = new Random(20261116);
        var depths = new[] { 8, 15, 16, 24, 32 };
        var test = new TestDecoder(32);
        var framebuffer = new GuardedFramebuffer(64, 48);
        for (var i = 0; i < 100_000; i++)
        {
            if (i % 2_000 == 0)
            {
                test = new TestDecoder(depths[random.Next(depths.Length)]);
            }

            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);

            // Random bytes rarely look like an order; steer the control bytes toward the kinds there are.
            if (bytes.Length > 1 && random.Next(3) != 0)
            {
                bytes[0] = (byte)(random.Next(4) switch
                {
                    0 => 0x09 | (random.Next(8) << 4),
                    1 => 0x03,
                    2 => (random.Next(64) << 2) | 0x02,
                    _ => 0x01 | (random.Next(0x80) << 1),
                });
                bytes[1] = (byte)new[] { 0x00, 0x01, 0x02, 0x09, 0x0A, 0x0D, 0x0E, 0x12, 0x1B, random.Next(256) }[random.Next(10)];
            }

            Check(test, framebuffer, random.Next(0, 20), bytes, $"random buffer {i}");
        }
    }

    [Fact]
    public void MutatedStreams()
    {
        var random = new Random(4242);
        var original = Stream(out var count);
        var test = new TestDecoder(16);
        var framebuffer = new GuardedFramebuffer(800, 600);
        var complete = 0;
        for (var i = 0; i < 50_000; i++)
        {
            var bytes = (byte[])original.Clone();
            var changes = random.Next(1, 8);
            for (var change = 0; change < changes; change++)
            {
                var at = random.Next(bytes.Length);
                bytes[at] = random.Next(3) switch
                {
                    0 => (byte)random.Next(256),
                    1 => (byte)(bytes[at] ^ (1 << random.Next(8))),
                    _ => (byte)(random.Next(2) == 0 ? 0xFF : 0x00),
                };
            }

            Check(test, framebuffer, count, bytes, $"mutated stream {i}");
            if (test.Decoder.TakeSummary().Contains("GlyphIndex x2"))
            {
                complete++;
            }

            if (i % 5_000 == 0)
            {
                test = new TestDecoder(new[] { 8, 15, 16, 24, 32 }[random.Next(5)]);
                framebuffer = new GuardedFramebuffer(800, 600);
            }
        }

        // Most mutations leave the stream readable to its end, so the orders deep in it are exercised too.
        Assert.True(complete > 25_000, $"only {complete} mutated streams were read to their end");
    }

    [Fact]
    public void HostileValuesAreRefusedOrClipped()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(32, 32);

        // Cache Bitmap revision 2, uncompressed: an entry beyond the announced 600, a cache that was never
        // announced, a bitmap far larger than the cache's tiles.
        Check(test, framebuffer, 1, CacheBitmap(0, 0x7FFE, 8, 8), "entry beyond the cache");
        Check(test, framebuffer, 1, CacheBitmap(5, 1, 8, 8), "cache never announced");
        Check(test, framebuffer, 1, CacheBitmap(0, 1, 64, 64), "bitmap larger than its tiles");
        Assert.Null(test.Decoder.Bitmaps.Get(0, 1));
        Assert.Null(test.Decoder.Bitmaps.Get(5, 1));
        Check(test, framebuffer, 1, CacheBitmap(2, 1023, 64, 64), "the last entry of the largest cache");
        Assert.NotNull(test.Decoder.Bitmaps.Get(2, 1023));
        Assert.Null(test.Decoder.Bitmaps.Get(2, 1024));

        // MemBlt and ScrBlt whose sources lie far outside: nothing is drawn.
        byte[] memBlt = [0x09, 0x0D, 0xFF, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x7F, 0xFF, 0x7F, 0xCC, 0x00, 0x80, 0x00, 0x80, 0xFF, 0x03];
        Assert.False(Check(test, framebuffer, 1, memBlt, "MemBlt from far outside its bitmap"));
        byte[] scrBlt = [0x09, 0x02, 0x7F, 0x00, 0x80, 0x00, 0x80, 0xFF, 0x7F, 0xFF, 0x7F, 0xCC, 0xFF, 0x7F, 0xFF, 0x7F];
        Assert.False(Check(test, framebuffer, 1, scrBlt, "ScrBlt from far outside the screen"));

        // Rectangles of the largest and the most negative size, bounds of any kind.
        Check(test, framebuffer, 1, [0x09, 0x0A, 0x7F, 0x00, 0x80, 0x00, 0x80, 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3], "OpaqueRect with negative sizes");
        Check(test, framebuffer, 1, [0x0D, 0x0A, 0x7F, 0x0F, 0xFF, 0x7F, 0xFF, 0x7F, 0x00, 0x80, 0x00, 0x80, 0x00, 0x80, 0x00, 0x80, 0xFF, 0x7F, 0xFF, 0x7F, 1, 2, 3], "OpaqueRect with inverted bounds");

        // A line across the whole coordinate space.
        byte[] lineTo = [0x09, 0x09, 0xFF, 0x03, 0x01, 0x00, 0x00, 0x80, 0x00, 0x80, 0xFF, 0x7F, 0xFF, 0x7F, 0x00, 0x00, 0x00, 0x0D, 0x00, 0x01, 0xFF, 0xFF, 0xFF];
        Assert.True(Check(test, framebuffer, 1, lineTo, "LineTo corner to corner"));

        // MultiOpaqueRect claiming 255 rectangles; a list longer than any valid one.
        Check(test, framebuffer, 1, [0x09, 0x12, 0xFF, 0x01, 0, 0, 0, 0, 1, 0, 1, 0, 1, 2, 3, 0xFF, 0x02, 0x00, 0x00, 0x00], "MultiOpaqueRect with 255 rectangles");
        Assert.Contains(test.Log, line => line.Contains("MultiOpaqueRect"));
        var longList = Concat([0x09, 0x12, 0x80, 0x01, 0x04, 0x00, 0x10], new byte[0x1000]);
        Check(test, framebuffer, 1, longList, "MultiOpaqueRect with a 4096-byte list");

        // Glyphs: larger than any glyph cell, a cache id beyond the sixteen kept; a colour table of 255 colours.
        Check(test, framebuffer, 1, Concat([0x03, 0x05, 0x20, 0x00, 0x00, 0x03, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01], new byte[8192]), "a glyph of 256 x 256");
        Assert.Null(test.Decoder.Glyphs.Get(0, 1));
        Check(test, framebuffer, 1, [0x03, 0x02, 0x00, 0x2F, 0x01, 0x03, 0x09, 0x00, 0x00, 0x08, 0x01, 0xFF, 0x00, 0x00, 0x00], "glyph cache 15");
        Assert.NotNull(test.Decoder.Glyphs.Get(15, 9));
        Check(test, framebuffer, 1, Concat([0x03, 0xF8, 0x03, 0x00, 0x00, 0x01, 0x00, 0xFF, 0x00], new byte[1020]), "a colour table of 255 colours");
        Assert.Empty(test.Decoder.ColorTable(0));

        // Secondary orders longer than their update, or of negative length.
        Assert.False(Check(test, framebuffer, 2, [0x03, 0x00, 0x10, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00], "secondary order too long"));
        Assert.False(Check(test, framebuffer, 2, [0x03, 0xF0, 0xFF, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00], "secondary order of negative length"));
        framebuffer.AssertGuardsIntact();
    }

    [Fact]
    public void AFramebufferSmallerThanItsSizeIsNotDrawnOn()
    {
        // Width and height that the buffer cannot hold: the orders are read (the cache fills), nothing is drawn.
        var test = new TestDecoder(16);
        var framebuffer = new GuardedFramebuffer(16, 16);
        var bytes = Stream(out var count);
        Assert.False(test.Decoder.Decode(count, bytes, framebuffer.Pixels, 800, 600, test.Palette, out _));
        Assert.NotNull(test.Decoder.Bitmaps.Get(1, BitmapCache.WaitingListIndex));
        Assert.True(framebuffer.Snapshot().All(pixel => pixel == GuardedFramebuffer.Untouched));
        framebuffer.AssertGuardsIntact();
        Assert.False(test.Decoder.Decode(count, bytes, framebuffer.Pixels, -5, 16, test.Palette, out _));
    }

    // Cache Bitmap (revision 2), uncompressed, 32 bits.
    private static byte[] CacheBitmap(int cacheId, int index, int width, int height)
    {
        var data = new byte[width * height * 4];
        var body = new List<byte>();
        body.AddRange(Unsigned2(width));
        body.AddRange(Unsigned2(height));
        body.AddRange([(byte)(0x80 | (data.Length >> 16)), (byte)(data.Length >> 8), (byte)data.Length]);
        body.AddRange(Unsigned2(index));
        body.AddRange(data);
        var length = body.Count - 7;
        var extraFlags = cacheId | (6 << 3);
        return Concat([0x03, (byte)length, (byte)(length >> 8), (byte)extraFlags, (byte)(extraFlags >> 8), 0x04], body.ToArray());

        static byte[] Unsigned2(int value) => value < 0x80 ? [(byte)value] : [(byte)(0x80 | (value >> 8)), (byte)value];
    }

    private static bool Check(TestDecoder test, GuardedFramebuffer framebuffer, int count, byte[] bytes, string what)
    {
        bool drawn;
        DrawnArea area;
        try
        {
            drawn = test.Draw(framebuffer, count, bytes, out area);
        }
        catch (Exception exception)
        {
            Assert.Fail($"{what}: {exception.GetType().Name}: {exception.Message}\n{Convert.ToHexString(bytes)}");
            throw;
        }

        framebuffer.AssertGuardsIntact(what);
        if (drawn)
        {
            Assert.True(area.X >= 0 && area.Y >= 0 && area.Width > 0 && area.Height > 0
                && area.X + area.Width <= framebuffer.Width && area.Y + area.Height <= framebuffer.Height, $"{what}: drawn area {area} outside the framebuffer");
        }

        return drawn;
    }
}
