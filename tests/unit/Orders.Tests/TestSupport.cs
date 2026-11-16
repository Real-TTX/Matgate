using System.Buffers.Binary;
using Matgate.Display.Rdp.Graphics;
using Xunit;

namespace Matgate.Display.Rdp.Orders.Tests;

// A framebuffer with guard bytes before and after it, every byte set to a value the decoder never writes (it
// always writes the X byte as 0xFF). So a test can tell exactly which pixels were written, and that nothing
// outside the framebuffer was.
internal sealed class GuardedFramebuffer
{
    public const uint Untouched = 0xA5A5A5A5;
    private const int GuardBytes = 256;
    private readonly byte[] _memory;

    public GuardedFramebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _memory = new byte[(width * height * 4) + (GuardBytes * 2)];
        _memory.AsSpan().Fill(0xA5);
    }

    public int Width { get; }

    public int Height { get; }

    public Span<byte> Pixels => _memory.AsSpan(GuardBytes, Width * Height * 4);

    // A pixel as 0xXXRRGGBB.
    public uint this[int x, int y]
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Pixels[(((y * Width) + x) * 4)..]);
        set => BinaryPrimitives.WriteUInt32LittleEndian(Pixels[(((y * Width) + x) * 4)..], value);
    }

    public void Fill(uint pixel)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                this[x, y] = pixel;
            }
        }
    }

    // Every pixel a different opaque colour, so that a copy from the wrong place shows.
    public void FillGradient()
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                this[x, y] = 0xFF000000u | (uint)((x * 7) & 0xFF) << 16 | (uint)((y * 13) & 0xFF) << 8 | (uint)((x + y) & 0xFF);
            }
        }
    }

    public uint[] Snapshot()
    {
        var copy = new uint[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                copy[(y * Width) + x] = this[x, y];
            }
        }

        return copy;
    }

    public void AssertGuardsIntact(string what = "")
    {
        Assert.True(_memory.AsSpan(0, GuardBytes).IndexOfAnyExcept((byte)0xA5) < 0, what + ": bytes before the framebuffer were written");
        Assert.True(_memory.AsSpan(GuardBytes + (Width * Height * 4)).IndexOfAnyExcept((byte)0xA5) < 0, what + ": bytes after the framebuffer were written");
    }

    // Every pixel inside the rectangle has `inside`, every other pixel is untouched.
    public void AssertOnly(int left, int top, int width, int height, uint inside, string what = "")
    {
        AssertGuardsIntact(what);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var wanted = x >= left && x < left + width && y >= top && y < top + height ? inside : Untouched;
                if (this[x, y] != wanted)
                {
                    Assert.Fail($"{what}: pixel ({x}, {y}) is {this[x, y]:X8}, expected {wanted:X8}");
                }
            }
        }
    }
}

internal static class TestData
{
    // A hex dump of the specification, as the files in TestData hold them.
    public static byte[] Load(string name) =>
        Hex(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name + ".hex")));

    public static byte[] Hex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(Uri.IsHexDigit)));

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    // The session palette for the tests: index i is a grey of i.
    public static uint[] GreyPalette()
    {
        var palette = new uint[256];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = 0xFF000000u | (uint)(i << 16) | (uint)(i << 8) | (uint)i;
        }

        return palette;
    }

    // An opaque pixel as the framebuffer holds it (0xXXRRGGBB, X = 0xFF).
    public static uint Rgb(int red, int green, int blue) => Bgrx.Opaque | (uint)(red << 16) | (uint)(green << 8) | (uint)blue;
}

// A decoder with a log that the tests can read.
internal sealed class TestDecoder
{
    public TestDecoder(int colorDepth = 32)
    {
        Decoder = new OrderDecoder(Log.Add);
        Decoder.Reset(colorDepth);
    }

    public OrderDecoder Decoder { get; }

    public List<string> Log { get; } = [];

    public uint[] Palette { get; } = TestData.GreyPalette();

    // Orders as a fast-path update would carry them, without the count in front.
    public bool Draw(GuardedFramebuffer framebuffer, int count, byte[] orders, out DrawnArea drawn) =>
        Decoder.Decode(count, orders, framebuffer.Pixels, framebuffer.Width, framebuffer.Height, Palette, out drawn);

    public bool Draw(GuardedFramebuffer framebuffer, int count, byte[] orders) => Draw(framebuffer, count, orders, out _);
}
