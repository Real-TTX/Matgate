using System.Buffers.Binary;
using Matgate.Display.Rdp.Graphics;
using Xunit;
using static Matgate.Display.Rdp.Orders.Tests.TestData;

namespace Matgate.Display.Rdp.Orders.Tests;

// The annotated examples of MS-RDPEGDI section 4: each one decoded, its fields compared with the annotation, then
// drawn and the pixels compared with what the order says.
public sealed class SpecificationExampleTests
{
    private const uint Black = 0xFF000000;
    private const uint White = 0xFFFFFFFF;

    [Fact]
    public void DstBltExample()
    {
        // 4.1.1: fields 3 and 4 present; nLeftRect, nTopRect and bRop keep their initial zero (BLACKNESS).
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(100, 320);
        Assert.True(test.Draw(framebuffer, 1, Load("dstblt"), out var drawn));
        var order = test.Decoder.DstBltHistory;
        Assert.Equal((0, 0, 72, 311, 0x00), (order.Left, order.Top, order.Width, order.Height, order.Rop));
        Assert.Equal(0x00, test.Decoder.LastOrderType);
        Assert.Equal(new DrawnArea(0, 0, 72, 311), drawn);
        framebuffer.AssertOnly(0, 0, 72, 311, Black, "DstBlt");
    }

    [Fact]
    public void PatBltExampleWithItsCachedBrushIsReadButNotDrawn()
    {
        // 4.1.3: a cached brush (BMF_1BPP | TS_CACHED_BRUSH), which this client never announces.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(64, 480);
        Assert.False(test.Draw(framebuffer, 1, Load("patblt")));
        var order = test.Decoder.PatBltHistory;
        Assert.Equal((26, 451, 13, 13, 0xF0), (order.Left, order.Top, order.Width, order.Height, order.Rop));
        Assert.Equal(0x00FFFF, order.BackColor);
        Assert.Equal(0x00EF5B, order.ForeColor);
        Assert.Equal((0, 0, 0x81, 0), (order.Brush.OriginX, order.Brush.OriginY, order.Brush.Style, order.Brush.Hatch));
        framebuffer.AssertOnly(0, 0, 0, 0, 0, "PatBlt with a cached brush");
        Assert.Contains(test.Log, line => line.Contains("cached brush"));
    }

    [Fact]
    public void PatBltExampleWithASolidBrushFillsTheForegroundColour()
    {
        // The same order with BrushStyle BS_SOLID: PATCOPY with ForeColor, red 0x5B, green 0xEF, blue 0x00.
        var bytes = Load("patblt");
        bytes[^1] = 0x00;
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(64, 480);
        Assert.True(test.Draw(framebuffer, 1, bytes));
        framebuffer.AssertOnly(26, 451, 13, 13, Rgb(0x5B, 0xEF, 0x00), "PatBlt");
    }

    [Fact]
    public void OpaqueRectExample()
    {
        // 4.1.5. The annotation does not match its own dump: it reads red 0x74 where the dump holds 0x73, and it
        // reads a Blue byte although fieldFlags 0x3C mark fields 3 to 6 only. The flags decide: red 0x73, green
        // 0x02, blue keeps its initial zero, and the last byte of the dump belongs to no field of this order.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(1024, 768);
        var bytes = Load("opaquerect");
        Assert.True(test.Draw(framebuffer, 1, bytes));
        var order = test.Decoder.OpaqueRectHistory;
        Assert.Equal((0, 0, 1024, 768, 0x000273), (order.Left, order.Top, order.Width, order.Height, order.Color));
        framebuffer.AssertOnly(0, 0, 1024, 768, Rgb(0x73, 0x02, 0x00), "OpaqueRect");

        // With field 7 marked as well (0x7C), the dump is the order the annotation describes.
        bytes[2] = 0x7C;
        Assert.True(test.Draw(framebuffer, 1, bytes));
        Assert.Equal(0x060273, test.Decoder.OpaqueRectHistory.Color);
        Assert.Equal(Rgb(0x73, 0x02, 0x06), framebuffer[1023, 767]);
    }

    [Fact]
    public void MultiOpaqueRectExample()
    {
        // 4.1.6: four delta rectangles, the outline of a box; Blue is not present and stays zero.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(700, 400);
        Assert.True(test.Draw(framebuffer, 1, Load("multiopaquerect"), out var drawn));
        var order = test.Decoder.MultiOpaqueRectHistory;
        Assert.Equal((391, 284, 241, 18, 0x00EF5C, 4), (order.Left, order.Top, order.Width, order.Height, order.Color, order.Count));
        Assert.Equal(22, order.DeltasLength);
        (int X, int Y, int Width, int Height)[] rectangles = [(391, 284, 241, 1), (391, 285, 1, 16), (631, 285, 1, 16), (391, 301, 241, 1)];
        var color = Rgb(0x5C, 0xEF, 0x00);
        framebuffer.AssertGuardsIntact();
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var inside = rectangles.Any(r => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height);
                Assert.Equal(inside ? color : GuardedFramebuffer.Untouched, framebuffer[x, y]);
            }
        }

        Assert.Equal(new DrawnArea(391, 284, 241, 18), drawn);
    }

    [Fact]
    public void ScrBltExampleCopiesOverlappingRegions()
    {
        // 4.1.7. The dump ends one byte early: nYSrc is 0x008E, whose high byte is not printed.
        var bytes = Concat(Load("scrblt"), [0x00]);
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(800, 600);
        framebuffer.FillGradient();
        var before = framebuffer.Snapshot();
        Assert.True(test.Draw(framebuffer, 1, bytes));
        var order = test.Decoder.ScrBltHistory;
        Assert.Equal((7, 0, 417, 241, 0xCC, 303, 142), (order.Left, order.Top, order.Width, order.Height, order.Rop, order.SourceX, order.SourceY));
        framebuffer.AssertGuardsIntact();
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var inside = x >= 7 && x < 7 + 417 && y < 241;
                var wanted = inside ? before[((y + 142) * 800) + x - 7 + 303] : before[(y * 800) + x];
                Assert.Equal(wanted, framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void MemBltExampleFromAnOffscreenBitmapIsReadButNotDrawn()
    {
        // 4.1.9: TS_ZERO_FIELD_BYTE_BIT0 leaves the second field byte out; cache 0xFF is the offscreen cache.
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(64, 300);
        Assert.False(test.Draw(framebuffer, 1, Load("memblt")));
        var order = test.Decoder.MemBltHistory;
        Assert.Equal((0x00FF, 2, 262, 0, 21), (order.CacheId, order.Left, order.Top, order.Width, order.Height));
        Assert.Equal((0, 0, 21, 0), (order.Rop, order.SourceX, order.SourceY, order.CacheIndex));
        Assert.Equal(0x0D, test.Decoder.LastOrderType);
        framebuffer.AssertOnly(0, 0, 0, 0, 0, "MemBlt from offscreen");
        Assert.Contains(test.Log, line => line.Contains("offscreen"));
    }

    [Fact]
    public void LineToExampleWithDeltaCoordinatesAndDeltaBounds()
    {
        // 4.1.11 refers to the previous LineTo and bounds: nXStart 0x33A, nYStart 0x15E, nXEnd 0x33D, nYEnd
        // 0x15B, bounds top 0xE7 and bottom 0xF3. A LineTo with absolute values sets them first (R2_COPYPEN).
        byte[] previous =
        [
            0x0D, 0x09, 0x5E, 0x00,
            0x0F, 0x00, 0x03, 0xE7, 0x00, 0x60, 0x03, 0xF3, 0x00,
            0x3A, 0x03, 0x5E, 0x01, 0x3D, 0x03, 0x5B, 0x01, 0x0D,
        ];
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(900, 300);
        test.Draw(framebuffer, 1, previous);
        Assert.Equal((0x300, 0xE7, 0x360, 0xF3), test.Decoder.LastBounds);
        framebuffer = new GuardedFramebuffer(900, 300);
        Assert.True(test.Draw(framebuffer, 1, Load("lineto")));
        Assert.Equal((829, 257, 842, 270), test.Decoder.LastBounds);
        var order = test.Decoder.LineToHistory;
        Assert.Equal((829, 271, 843, 257), (order.StartX, order.StartY, order.EndX, order.EndY));
        Assert.Equal((0x00EF5B, 0x0D), (order.PenColor, order.Rop2));

        // A diagonal from (829, 271) up to (843, 257), the end point left out, cut by the bounds (bottom 270,
        // right 842): the points (829 + k, 271 - k) for k = 1 to 13.
        framebuffer.AssertGuardsIntact();
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var k = x - 829;
                var onLine = k is >= 1 and <= 13 && y == 271 - k;
                Assert.Equal(onLine ? Rgb(0x5B, 0xEF, 0x00) : GuardedFramebuffer.Untouched, framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void GlyphIndexExampleOneStoresItsFragment()
    {
        // 4.1.13.1: BkRight and 39 bytes of glyphs, ending with ADD into fragment 21 (the 36 bytes before it).
        var bytes = Load("glyphindex1");
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(700, 100);
        test.Draw(framebuffer, 1, bytes);
        var order = test.Decoder.GlyphIndexHistory;
        Assert.Equal(0x1B, test.Decoder.LastOrderType);
        Assert.Equal(618, order.BackgroundRight);
        Assert.Equal(39, order.DataLength);
        Assert.Equal(bytes.AsSpan(8, 36).ToArray(), test.Decoder.Glyphs.GetFragment(21));

        // No glyph was ever cached: nothing is drawn, and the log says why.
        framebuffer.AssertOnly(0, 0, 0, 0, 0, "GlyphIndex without glyphs");
        Assert.Contains(test.Log, line => line.Contains("glyph that was never sent"));
    }

    [Fact]
    public void GlyphIndexExampleTwoFillsItsOpaqueRectangle()
    {
        // 4.1.13.2: an opaque rectangle (521, 366) to (758, 379) in white, then fragment 4 (never stored).
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(800, 400);
        Assert.True(test.Draw(framebuffer, 1, Load("glyphindex2")));
        var order = test.Decoder.GlyphIndexHistory;
        Assert.Equal((0, 0xFFFFFF), (order.OpaqueRedundant, order.ForeColor));
        Assert.Equal((524, 366, 589, 379), (order.BackgroundLeft, order.BackgroundTop, order.BackgroundRight, order.BackgroundBottom));
        Assert.Equal((521, 366, 758, 379), (order.OpaqueLeft, order.OpaqueTop, order.OpaqueRight, order.OpaqueBottom));
        Assert.Equal((524, 377, 3), (order.X, order.Y, order.DataLength));
        framebuffer.AssertOnly(521, 366, 758 - 521 + 1, 379 - 366 + 1, White, "GlyphIndex opaque rectangle");
        Assert.Contains(test.Log, line => line.Contains("fragment that was never sent"));
    }

    [Fact]
    public void CacheBitmapRevisionOneExample()
    {
        // 4.2.1: cache 0, entry 0, 16 x 1 at 8 bits, compressed without a header: one background run of 16.
        var test = new TestDecoder(8);
        var framebuffer = new GuardedFramebuffer(32, 8);
        Assert.False(test.Draw(framebuffer, 1, Load("cachebitmap")));
        var bitmap = test.Decoder.Bitmaps.Get(0, 0);
        Assert.NotNull(bitmap);
        Assert.Equal((16, 1, true), (bitmap.Width, bitmap.Height, bitmap.Indexed));
        Assert.All(bitmap.Pixels, pixel => Assert.Equal(0u, pixel & 0xFF));

        // Drawn with a MemBlt through the session palette (index 0 is black here).
        byte[] memBlt = [0x09, 0x0D, 0xFF, 0x01, 0x00, 0x00, 0x05, 0x00, 0x03, 0x00, 0x10, 0x00, 0x01, 0x00, 0xCC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.True(test.Draw(framebuffer, 1, memBlt));
        framebuffer.AssertOnly(5, 3, 16, 1, Black, "MemBlt of the 8-bit bitmap");
    }

    [Fact]
    public void CacheBitmapRevisionTwoExampleAndAMemBltOfIt()
    {
        // 4.2.2: cache 1, 16 bits, height = width = 32, no compression header, CBR2_DO_NOT_CACHE (so the waiting
        // list entry, 0x7FFF). The decompressed data of the annotation is the bitmap, rows bottom-up.
        var test = new TestDecoder(16);
        var framebuffer = new GuardedFramebuffer(64, 80);
        Assert.False(test.Draw(framebuffer, 1, Load("cachebitmap2")));
        var bitmap = test.Decoder.Bitmaps.Get(1, BitmapCache.WaitingListIndex);
        Assert.NotNull(bitmap);
        Assert.Equal((32, 32, false), (bitmap.Width, bitmap.Height, bitmap.Indexed));
        Assert.Equal(Load("cachebitmap2-1"), Load("cachebitmap2").AsSpan(11).ToArray());
        var decompressed = Load("cachebitmap2-2");
        uint Expected(int x, int y) => Bgrx.FromRgb565(BinaryPrimitives.ReadUInt16LittleEndian(decompressed.AsSpan((((31 - y) * 32) + x) * 2)));
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(Expected(x, y), bitmap.Pixels[(y * 32) + x]);
            }
        }

        // MemBlt of its lower right quarter (source 16, 16) to (40, 50); nothing else changes.
        byte[] memBlt = [0x09, 0x0D, 0xFF, 0x01, 0x01, 0x00, 0x28, 0x00, 0x32, 0x00, 0x10, 0x00, 0x10, 0x00, 0xCC, 0x10, 0x00, 0x10, 0x00, 0xFF, 0x7F];
        Assert.True(test.Draw(framebuffer, 1, memBlt, out var drawn));
        Assert.Equal(new DrawnArea(40, 50, 16, 16), drawn);
        framebuffer.AssertGuardsIntact();
        for (var y = 0; y < framebuffer.Height; y++)
        {
            for (var x = 0; x < framebuffer.Width; x++)
            {
                var inside = x >= 40 && x < 56 && y >= 50 && y < 66;
                Assert.Equal(inside ? Expected(x - 40 + 16, y - 50 + 16) : GuardedFramebuffer.Untouched, framebuffer[x, y]);
            }
        }
    }

    [Fact]
    public void CacheColorTableExample()
    {
        // 4.2.3: the annotation labels the four bytes R G B P, but they are blue, green, red, pad (TS_COLOR_QUAD):
        // entry 1 of the Windows default palette is dark red, entry 4 dark blue, entry 9 sky blue (0xA6CAF0).
        var test = new TestDecoder(8);
        test.Draw(new GuardedFramebuffer(8, 8), 1, Load("cachecolortable"));
        var table = test.Decoder.ColorTable(0);
        Assert.Equal(256, table.Length);
        Assert.Equal(Rgb(0, 0, 0), table[0]);
        Assert.Equal(Rgb(0x80, 0, 0), table[1]);
        Assert.Equal(Rgb(0x80, 0x80, 0), table[3]);
        Assert.Equal(Rgb(0, 0, 0x80), table[4]);
        Assert.Equal(Rgb(0xA6, 0xCA, 0xF0), table[9]);
        Assert.Equal(Rgb(0xFF, 0xFF, 0xFF), table[255]);

        // An 8-bit cached bitmap drawn with table 0: index 1 is dark red, not the session's grey.
        byte[] cache = [0x03, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x08, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01];
        byte[] memBlt = [0x09, 0x0D, 0xFF, 0x01, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x04, 0x00, 0x01, 0x00, 0xCC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var framebuffer = new GuardedFramebuffer(8, 8);
        Assert.True(test.Draw(framebuffer, 2, Concat(cache, memBlt)));
        framebuffer.AssertOnly(2, 2, 4, 1, Rgb(0x80, 0, 0), "MemBlt through colour table 0");
    }

    [Fact]
    public void CacheGlyphExamplesAndTheirText()
    {
        // 4.2.4: three glyphs into cache 7; 4.2.5: one more in the compact encoding.
        var test = new TestDecoder();
        test.Draw(new GuardedFramebuffer(8, 8), 2, Concat(Load("cacheglyph"), Load("cacheglyph2")));
        var glyphs = test.Decoder.Glyphs;
        AssertGlyph(glyphs.Get(7, 49), 0, -8, 5, 8, [0xC0, 0xC0, 0xC0, 0xC0, 0xC0, 0xC0, 0xC0, 0xF8]);
        AssertGlyph(glyphs.Get(7, 50), 0, -6, 6, 8, [0x7C, 0xCC, 0xCC, 0xCC, 0xCC, 0x7C, 0x0C, 0x78]);
        AssertGlyph(glyphs.Get(7, 51), 0, -8, 7, 8, [0x7C, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C]);
        AssertGlyph(glyphs.Get(7, 52), 0, -9, 3, 3, [0xA0, 0xA0, 0xA0]);

        // "LgO" with a GlyphIndex: cache 7, horizontal, deltas 0, 7 and 8 from (10, 20), text colour 0x102030,
        // background rectangle (0, 0) to (99, 29), no opaque rectangle.
        byte[] text =
        [
            0x09, 0x1B, 0xD3, 0x03, 0x38,
            0x07, 0x03, 0x30, 0x20, 0x10,
            0x00, 0x00, 0x00, 0x00, 0x63, 0x00, 0x1D, 0x00,
            0x0A, 0x00, 0x14, 0x00,
            0x06, 0x31, 0x00, 0x32, 0x07, 0x33, 0x08,
        ];
        var framebuffer = new GuardedFramebuffer(100, 30);
        Assert.True(test.Draw(framebuffer, 1, text));
        var expected = new uint[100 * 30];
        Array.Fill(expected, GuardedFramebuffer.Untouched);
        Paint(expected, 10, 12, glyphs.Get(7, 49)!);
        Paint(expected, 17, 14, glyphs.Get(7, 50)!);
        Paint(expected, 25, 12, glyphs.Get(7, 51)!);
        framebuffer.AssertGuardsIntact();
        Assert.Equal(expected, framebuffer.Snapshot());

        void Paint(uint[] pixels, int left, int top, Glyph glyph)
        {
            for (var y = 0; y < glyph.Height; y++)
            {
                for (var x = 0; x < glyph.Width; x++)
                {
                    if ((glyph.Bits[(y * glyph.Stride) + (x / 8)] & (0x80 >> (x % 8))) != 0)
                    {
                        pixels[((top + y) * 100) + left + x] = Rgb(0x30, 0x20, 0x10);
                    }
                }
            }
        }
    }

    private static void AssertGlyph(Glyph? glyph, int x, int y, int width, int height, byte[] bits)
    {
        Assert.NotNull(glyph);
        Assert.Equal((x, y, width, height), (glyph.X, glyph.Y, glyph.Width, glyph.Height));
        Assert.Equal(bits, glyph.Bits);
    }
}
