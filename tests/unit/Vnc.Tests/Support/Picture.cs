using Xunit;

namespace Matgate.Display.Vnc.Tests;

// What a framebuffer should show, written as 0xRRGGBB values, and the comparison with what it does show.
internal sealed class Picture
{
    private readonly int[] _pixels;

    public Picture(int width, int height, int colour = 0)
    {
        Width = width;
        Height = height;
        _pixels = new int[width * height];
        Array.Fill(_pixels, colour);
    }

    public int Width { get; }

    public int Height { get; }

    public int this[int x, int y]
    {
        get => _pixels[y * Width + x];
        set => _pixels[y * Width + x] = value;
    }

    // A picture in which no two neighbours are alike, the same for the same seed.
    public static Picture Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var picture = new Picture(width, height);
        for (var index = 0; index < picture._pixels.Length; index++)
        {
            picture._pixels[index] = random.Next(0x1000000);
        }

        return picture;
    }

    // Soft transitions, as a photograph has them - what the gradient filter is for.
    public static Picture Smooth(int width, int height)
    {
        var picture = new Picture(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                picture[x, y] = (Math.Min(255, x * 7 + y * 3) << 16) | (Math.Min(255, 250 - x * 2) << 8) | ((y * 9 + x) & 0xFF);
            }
        }

        return picture;
    }

    public void Fill(int x, int y, int width, int height, int colour)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                this[column, row] = colour;
            }
        }
    }

    // Draws another picture into this one.
    public void Draw(Picture part, int x, int y)
    {
        for (var row = 0; row < part.Height; row++)
        {
            for (var column = 0; column < part.Width; column++)
            {
                this[x + column, y + row] = part[column, row];
            }
        }
    }

    // The bytes a framebuffer of this picture consists of: blue, green, red, 255.
    public byte[] ToBgrx()
    {
        var bytes = new byte[_pixels.Length * 4];
        for (var index = 0; index < _pixels.Length; index++)
        {
            bytes[index * 4] = (byte)_pixels[index];
            bytes[index * 4 + 1] = (byte)(_pixels[index] >> 8);
            bytes[index * 4 + 2] = (byte)(_pixels[index] >> 16);
            bytes[index * 4 + 3] = 255;
        }

        return bytes;
    }

    public void AssertShownBy(VncFramebuffer framebuffer) => AssertShownBy(framebuffer.Pixels, framebuffer.Width, framebuffer.Height);

    public void AssertShownBy(byte[] bgrx, int width, int height)
    {
        Assert.Equal(Width, width);
        Assert.Equal(Height, height);
        var expected = ToBgrx();
        Assert.Equal(expected.Length, bgrx.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != bgrx[index])
            {
                var pixel = index / 4;
                Assert.Fail($"The pixel at {pixel % Width}, {pixel / Width} is {bgrx[pixel * 4 + 2]:X2}{bgrx[pixel * 4 + 1]:X2}{bgrx[pixel * 4]:X2} (filler {bgrx[pixel * 4 + 3]:X2}) instead of {_pixels[pixel]:X6} (filler FF).");
            }
        }
    }
}
