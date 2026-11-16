using Matgate.Display.Rdp.Graphics;
using Xunit;
using static Matgate.Display.Rdp.Orders.Tests.TestData;

namespace Matgate.Display.Rdp.Orders.Tests;

// Raster operations: the ternary operation index as a truth table, checked against the Boolean functions the
// specification lists for it (MS-RDPEGDI 2.2.2.2.1.1.1.7); then all 256 of them through every order that takes
// one, the fast paths included, against that truth table pixel by pixel.
public sealed class RasterOperationTests
{
    private static readonly int[] PatternRows = [0x81, 0xCC, 0x33, 0xF0, 0x0F, 0xAA, 0x55, 0x7E];

    // A sample of the specification's table, in its Reverse Polish Notation.
    public static TheoryData<int, string> Table => new()
    {
        { 0x00, "0" }, { 0x01, "DPSoon" }, { 0x05, "DPon" }, { 0x06, "PDSxnon" }, { 0x0A, "DPna" }, { 0x0F, "Pn" },
        { 0x11, "DSon" }, { 0x16, "PSDPSanaxx" }, { 0x1E, "PDSox" }, { 0x22, "DSna" }, { 0x33, "Sn" }, { 0x44, "SDna" },
        { 0x55, "Dn" }, { 0x5A, "DPx" }, { 0x66, "DSx" }, { 0x69, "PDSxxn" }, { 0x6A, "DPSax" }, { 0x88, "DSa" },
        { 0x8E, "SSDxPDxax" }, { 0x96, "DPSxx" }, { 0xA0, "DPa" }, { 0xAA, "D" }, { 0xB8, "PSDPxax" }, { 0xBB, "DSno" },
        { 0xC0, "PSa" }, { 0xCA, "DPSDxax" }, { 0xCC, "S" }, { 0xE2, "DSPDxax" }, { 0xE9, "DSPDSanaxxn" }, { 0xEE, "DSo" },
        { 0xF0, "P" }, { 0xFB, "DPSnoo" }, { 0xFF, "1" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void TheIndexIsTheTruthTableOfTheBooleanFunction(int rop, string rpn)
    {
        var table = 0;
        for (var index = 0; index < 8; index++)
        {
            var pattern = (index & 4) != 0 ? uint.MaxValue : 0;
            var source = (index & 2) != 0 ? uint.MaxValue : 0;
            var destination = (index & 1) != 0 ? uint.MaxValue : 0;
            if (Evaluate(rpn, pattern, source, destination) != 0)
            {
                table |= 1 << index;
            }
        }

        Assert.Equal(rop, table);

        // And Rop.Apply computes the same function on whole words.
        var random = new Random(rop);
        for (var i = 0; i < 1000; i++)
        {
            var p = (uint)random.NextInt64();
            var s = (uint)random.NextInt64();
            var d = (uint)random.NextInt64();
            Assert.Equal(Evaluate(rpn, p, s, d), Rop.Apply(rop, p, s, d));
        }
    }

    [Fact]
    public void BinaryRasterOperationsBecomeTernaryOnes()
    {
        Assert.Equal(0x00, Rop.FromRop2(1));
        Assert.Equal(0x05, Rop.FromRop2(2));
        Assert.Equal(0x0F, Rop.FromRop2(4));
        Assert.Equal(0x55, Rop.FromRop2(6));
        Assert.Equal(0x5A, Rop.FromRop2(7));
        Assert.Equal(0xA0, Rop.FromRop2(9));
        Assert.Equal(0xAA, Rop.FromRop2(11));
        Assert.Equal(0xF0, Rop.FromRop2(13));
        Assert.Equal(0xFA, Rop.FromRop2(15));
        Assert.Equal(0xFF, Rop.FromRop2(16));
    }

    [Fact]
    public void EveryOperationInDstBlt()
    {
        for (var rop = 0; rop < 256; rop++)
        {
            var (test, framebuffer, before) = Prepare();
            test.Draw(framebuffer, 1, [0x09, 0x00, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, (byte)rop]);
            AssertEverywhere(framebuffer, before, rop, (x, y) => Rop.Apply(rop, 0, 0, before[(y * 16) + x]));
        }
    }

    [Fact]
    public void EveryOperationInPatBltWithAPatternBrush()
    {
        for (var rop = 0; rop < 256; rop++)
        {
            var (test, framebuffer, before) = Prepare();
            var order = Concat([0x09, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, (byte)rop], BrushBytes());
            test.Draw(framebuffer, 1, order);
            AssertEverywhere(framebuffer, before, rop, (x, y) => Rop.Apply(rop, PatternAt(x, y), 0, before[(y * 16) + x]));
        }

        // A solid brush takes the fast paths of PATCOPY and PATINVERT.
        foreach (var rop in new[] { 0xF0, 0x5A, 0xA0 })
        {
            var (test, framebuffer, before) = Prepare();
            test.Draw(framebuffer, 1, [0x09, 0x01, 0x5F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x10, 0x00, (byte)rop, 0x11, 0x22, 0x33]);
            AssertEverywhere(framebuffer, before, rop, (x, y) => Rop.Apply(rop, Rgb(0x11, 0x22, 0x33), 0, before[(y * 16) + x]));
        }
    }

    [Fact]
    public void EveryOperationInScrBlt()
    {
        for (var rop = 0; rop < 256; rop++)
        {
            // The right half of the top rows onto the left half of the bottom rows: no overlap.
            var (test, framebuffer, before) = Prepare();
            test.Draw(framebuffer, 1, [0x09, 0x02, 0x7F, 0x00, 0x00, 0x08, 0x00, 0x08, 0x00, 0x08, 0x00, (byte)rop, 0x08, 0x00, 0x00, 0x00]);
            AssertInside(framebuffer, before, rop, 0, 8, (x, y) => Rop.Apply(rop, 0, before[((y - 8) * 16) + x + 8], before[(y * 16) + x]));
        }

        // Overlapping, every direction, SRCCOPY and an operation that reads the destination too.
        foreach (var rop in new[] { 0xCC, 0x66 })
        {
            foreach (var (dx, dy) in new[] { (3, 2), (-3, 2), (3, -2), (-3, -2), (0, 1), (1, 0) })
            {
                var (test, framebuffer, before) = Prepare();
                var order = new byte[] { 0x09, 0x02, 0x7F, 4, 0, 4, 0, 8, 0, 8, 0, (byte)rop, (byte)(4 + dx), 0, (byte)(4 + dy), 0 };
                test.Draw(framebuffer, 1, order);
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        var inside = x is >= 4 and < 12 && y is >= 4 and < 12;
                        var wanted = inside ? Rop.Apply(rop, 0, before[((y + dy) * 16) + x + dx], before[(y * 16) + x]) | Bgrx.Opaque : before[(y * 16) + x];
                        Assert.True(wanted == framebuffer[x, y], $"ScrBlt 0x{rop:X2} by ({dx}, {dy}): pixel ({x}, {y})");
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryOperationInMemBltAndMem3Blt()
    {
        for (var rop = 0; rop < 256; rop++)
        {
            var (test, framebuffer, before) = Prepare();
            test.Draw(framebuffer, 1, CachedBitmap());
            byte[] memBlt = [0x09, 0x0D, 0xFF, 0x01, 0x00, 0x00, 0x04, 0x00, 0x04, 0x00, 0x08, 0x00, 0x08, 0x00, (byte)rop, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00];
            test.Draw(framebuffer, 1, memBlt);
            AssertInside(framebuffer, before, rop, 4, 4, (x, y) => Rop.Apply(rop, 0, SourceAt(x - 4, y - 4), before[(y * 16) + x]), 8);

            (test, framebuffer, before) = Prepare();
            test.Draw(framebuffer, 1, CachedBitmap());
            var mem3Blt = Concat(
                [0x09, 0x0E, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x04, 0x00, 0x04, 0x00, 0x08, 0x00, 0x08, 0x00, (byte)rop, 0x00, 0x00, 0x00, 0x00],
                BrushBytes(),
                [0x05, 0x00]);
            test.Draw(framebuffer, 1, mem3Blt);
            AssertInside(framebuffer, before, rop, 4, 4, (x, y) => Rop.Apply(rop, PatternAt(x, y), SourceAt(x - 4, y - 4), before[(y * 16) + x]), 8);
        }
    }

    [Fact]
    public void EveryBinaryOperationInLineTo()
    {
        for (var rop2 = 1; rop2 <= 16; rop2++)
        {
            // A horizontal line from (0, 3) to (16, 3), so the whole row, pen colour 0x123456.
            var (test, framebuffer, before) = Prepare();
            byte[] lineTo = [0x09, 0x09, 0xFF, 0x03, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x10, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, (byte)rop2, 0x00, 0x01, 0x12, 0x34, 0x56];
            test.Draw(framebuffer, 1, lineTo);
            var rop = Rop.FromRop2(rop2);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var wanted = y == 3 && rop != Rop.Destination ? Rop.Apply(rop, Rgb(0x12, 0x34, 0x56), 0, before[(y * 16) + x]) | Bgrx.Opaque : before[(y * 16) + x];
                    Assert.True(wanted == framebuffer[x, y], $"LineTo R2 {rop2}: pixel ({x}, {y})");
                }
            }
        }
    }

    private static (TestDecoder Test, GuardedFramebuffer Framebuffer, uint[] Before) Prepare()
    {
        var test = new TestDecoder();
        var framebuffer = new GuardedFramebuffer(16, 16);
        framebuffer.FillGradient();
        return (test, framebuffer, framebuffer.Snapshot());
    }

    // BackColor 0x123456, ForeColor 0xABCDEF, origin (0, 0), BS_PATTERN, BrushHatch, BrushExtra.
    private static byte[] BrushBytes() =>
    [
        0x12, 0x34, 0x56, 0xAB, 0xCD, 0xEF, 0x00, 0x00, 0x03, (byte)PatternRows[7],
        (byte)PatternRows[6], (byte)PatternRows[5], (byte)PatternRows[4], (byte)PatternRows[3], (byte)PatternRows[2], (byte)PatternRows[1], (byte)PatternRows[0],
    ];

    private static uint PatternAt(int x, int y) =>
        ((PatternRows[y & 7] >> (7 - (x & 7))) & 1) != 0 ? Rgb(0x12, 0x34, 0x56) : Rgb(0xAB, 0xCD, 0xEF);

    private static uint SourceAt(int x, int y) => Rgb((x * 37) & 0xFF, (y * 59) & 0xFF, (x * y * 11) & 0xFF);

    // Cache Bitmap (revision 2), uncompressed, 32 bits, 8 x 8, into cache 0, entry 5; rows bottom-up.
    private static byte[] CachedBitmap()
    {
        var order = new List<byte> { 0x03, 0xFE, 0x00, 0x30, 0x00, 0x04, 0x08, 0x08, 0x41, 0x00, 0x05 };
        for (var row = 7; row >= 0; row--)
        {
            for (var x = 0; x < 8; x++)
            {
                var pixel = SourceAt(x, row);
                order.AddRange([(byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16), 0x00]);
            }
        }

        return order.ToArray();
    }

    private static void AssertEverywhere(GuardedFramebuffer framebuffer, uint[] before, int rop, Func<int, int, uint> expected) =>
        AssertInside(framebuffer, before, rop, 0, 0, expected, 16);

    private static void AssertInside(GuardedFramebuffer framebuffer, uint[] before, int rop, int left, int top, Func<int, int, uint> expected, int size = 8)
    {
        framebuffer.AssertGuardsIntact();
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var inside = x >= left && x < left + size && y >= top && y < top + size;
                var wanted = inside ? expected(x, y) | Bgrx.Opaque : before[(y * 16) + x];
                if (wanted != framebuffer[x, y])
                {
                    Assert.Fail($"raster operation 0x{rop:X2}: pixel ({x}, {y}) is {framebuffer[x, y]:X8}, expected {wanted:X8}");
                }
            }
        }
    }

    // Reverse Polish Notation as the specification writes it, on whole words.
    private static uint Evaluate(string rpn, uint pattern, uint source, uint destination)
    {
        var stack = new Stack<uint>();
        foreach (var token in rpn)
        {
            switch (token)
            {
                case 'P':
                    stack.Push(pattern);
                    break;
                case 'S':
                    stack.Push(source);
                    break;
                case 'D':
                    stack.Push(destination);
                    break;
                case '0':
                    stack.Push(0);
                    break;
                case '1':
                    stack.Push(uint.MaxValue);
                    break;
                case 'n':
                    stack.Push(~stack.Pop());
                    break;
                default:
                    var right = stack.Pop();
                    var left = stack.Pop();
                    stack.Push(token switch
                    {
                        'a' => left & right,
                        'o' => left | right,
                        _ => left ^ right,
                    });
                    break;
            }
        }

        Assert.Single(stack);
        return stack.Pop();
    }
}
