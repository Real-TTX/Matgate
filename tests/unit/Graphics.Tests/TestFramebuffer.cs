using Xunit;

namespace Matgate.Display.Rdp.Graphics.Tests;

// A framebuffer for the decoder to draw into, with guard bytes before and after it and every pixel set to a
// value the decoder never writes (it always writes the X byte as 0xFF). So a test can tell exactly which pixels
// were written, and that nothing outside the framebuffer was.
internal sealed class TestFramebuffer
{
    public const byte Untouched = 0xA5;
    private const int GuardBytes = 256;
    private readonly byte[] _memory;

    public TestFramebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _memory = new byte[(width * height * 4) + (GuardBytes * 2)];
        Clear();
    }

    public int Width { get; }

    public int Height { get; }

    public Span<byte> Pixels => _memory.AsSpan(GuardBytes, Width * Height * 4);

    public void Clear() => _memory.AsSpan().Fill(Untouched);

    // Decodes a vector with its top-left corner at (left, top) and the destination rectangle given by its size.
    public bool Decode(Vector vector, ReadOnlySpan<byte> data, int left, int top, int rectangleWidth, int rectangleHeight, bool header = false) =>
        BitmapDecoder.Decode(data, vector.BitsPerPixel, vector.Compressed, header, vector.Width, vector.Height, Pixels, Width, Height,
            left, top, left + rectangleWidth - 1, top + rectangleHeight - 1, vector.Palette);

    public void AssertGuardsIntact(string what)
    {
        Assert.True(_memory.AsSpan(0, GuardBytes).IndexOfAnyExcept(Untouched) < 0, what + ": bytes before the framebuffer were written");
        Assert.True(_memory.AsSpan(GuardBytes + (Width * Height * 4)).IndexOfAnyExcept(Untouched) < 0, what + ": bytes after the framebuffer were written");
    }

    // Every pixel outside the rectangle (left, top, width, height) must be as Clear left it.
    public void AssertUntouchedOutside(int left, int top, int width, int height, string what)
    {
        AssertGuardsIntact(what);
        var pixels = Pixels;
        for (var y = 0; y < Height; y++)
        {
            var row = pixels.Slice(y * Width * 4, Width * 4);
            if (y < top || y >= top + height)
            {
                Assert.True(row.IndexOfAnyExcept(Untouched) < 0, $"{what}: row {y} outside the rectangle was written");
                continue;
            }

            var before = Math.Clamp(left, 0, Width);
            var after = Math.Clamp(left + width, 0, Width);
            Assert.True(row[..(before * 4)].IndexOfAnyExcept(Untouched) < 0, $"{what}: row {y} was written left of the rectangle");
            Assert.True(row[(after * 4)..].IndexOfAnyExcept(Untouched) < 0, $"{what}: row {y} was written right of the rectangle");
        }
    }

    // The part of the rectangle that lies inside the framebuffer must hold the expected picture, whose top-left
    // corner is at (left, top). expected: BGRX, expectedWidth pixels a row.
    public void AssertPicture(byte[] expected, int expectedWidth, int left, int top, int width, int height, string what)
    {
        var pixels = Pixels;
        for (var y = Math.Max(top, 0); y < Math.Min(top + height, Height); y++)
        {
            for (var x = Math.Max(left, 0); x < Math.Min(left + width, Width); x++)
            {
                var actual = pixels.Slice(((y * Width) + x) * 4, 4);
                var wanted = expected.AsSpan((((y - top) * expectedWidth) + (x - left)) * 4, 4);
                if (!actual.SequenceEqual(wanted))
                {
                    Assert.Fail($"{what}: pixel ({x - left}, {y - top}) of the bitmap is {Hex(actual)}, FreeRDP has {Hex(wanted)}");
                }
            }
        }
    }

    // True when no pixel of the rectangle was written.
    public bool IsUntouched(int left, int top, int width, int height)
    {
        var pixels = Pixels;
        for (var y = Math.Max(top, 0); y < Math.Min(top + height, Height); y++)
        {
            var from = Math.Clamp(left, 0, Width);
            var to = Math.Clamp(left + width, 0, Width);
            if (pixels.Slice(((y * Width) + from) * 4, (to - from) * 4).IndexOfAnyExcept(Untouched) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);
}
