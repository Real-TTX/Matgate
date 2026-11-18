namespace Matgate.Display.Video.Tests;

// Made-up screens in BGRX (four bytes a pixel, a row is width * 4 bytes): the kinds of picture a remote desktop
// shows, each of them the same on every run.
internal static class Pictures
{
    private const int CellWidth = 8;
    private const int LineHeight = 16;

    // A page of something like text: dark glyphs of thin strokes on white, in words and lines, a grey margin
    // with line numbers, here and there a coloured line - sharp edges everywhere, which is what makes text
    // expensive for a video codec and blur easy to see.
    public static byte[] TextPage(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        var glyphs = new byte[64][];
        for (var i = 0; i < glyphs.Length; i++)
        {
            glyphs[i] = Glyph(random);
        }

        var page = new byte[width * height * 4];
        page.AsSpan().Fill(255);
        var columns = width / CellWidth;
        for (var line = 0; line * LineHeight + LineHeight <= height; line++)
        {
            var top = line * LineHeight;

            // The margin: a light grey strip with a "number" in it.
            FillRectangle(page, width, 0, top, Math.Min(48, width), LineHeight, 236, 236, 236);
            for (var digit = 0; digit < 3 && 8 + digit * CellWidth + CellWidth <= width; digit++)
            {
                DrawGlyph(page, width, 8 + digit * CellWidth, top, glyphs[(line / (digit == 0 ? 100 : digit == 1 ? 10 : 1)) % 10], 128, 128, 128);
            }

            // Every now and then an empty line or a heading in another colour.
            var roll = random.Next(16);
            if (roll == 0)
            {
                continue;
            }

            var (red, green, blue) = roll switch
            {
                1 => ((byte)0, (byte)90, (byte)200),
                2 => ((byte)160, (byte)30, (byte)30),
                3 => ((byte)20, (byte)120, (byte)40),
                _ => ((byte)24, (byte)24, (byte)24),
            };
            var column = 8 + random.Next(4) * 2;
            var end = columns - 2 - random.Next(Math.Max(1, columns / 3));
            while (column < end)
            {
                var word = 2 + random.Next(8);
                for (var letter = 0; letter < word && column < end; letter++, column++)
                {
                    DrawGlyph(page, width, column * CellWidth, top, glyphs[10 + random.Next(54)], red, green, blue);
                }

                column++;
            }
        }

        return page;
    }

    // What is seen of the page when it is scrolled down by offset rows; the page starts over at its end.
    public static void Scroll(byte[] page, byte[] target, int width, int height, int offset)
    {
        var stride = width * 4;
        var rows = page.Length / stride;
        for (var y = 0; y < height; y++)
        {
            var from = ((y + offset) % rows + rows) % rows;
            page.AsSpan(from * stride, stride).CopyTo(target.AsSpan(y * stride, stride));
        }
    }

    // The same for one part of the screen - a window with a page in it: the page is as wide as that part, and
    // nothing outside of it is drawn.
    public static void ScrollPart(byte[] page, byte[] target, int width, int left, int top, int partWidth, int partHeight, int offset)
    {
        var stride = partWidth * 4;
        var rows = page.Length / stride;
        for (var y = 0; y < partHeight; y++)
        {
            var from = ((y + offset) % rows + rows) % rows;
            page.AsSpan(from * stride, stride).CopyTo(target.AsSpan(((top + y) * width + left) * 4, stride));
        }
    }

    // A desktop background: a soft gradient, which a codec renders with visible steps if it is too coarse.
    public static byte[] Desktop(int width, int height)
    {
        var picture = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = picture.AsSpan(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = (byte)(120 + 80 * x / width);
                row[x * 4 + 1] = (byte)(60 + 90 * y / height);
                row[x * 4 + 2] = (byte)(40 + 50 * (x + y) / (width + height));
                row[x * 4 + 3] = 255;
            }
        }

        return picture;
    }

    // A window on top of the background: a title bar, a border and a piece of the text page as its content.
    public static void Window(byte[] background, byte[] content, byte[] target, int width, int height, int left, int top, int windowWidth, int windowHeight)
    {
        background.AsSpan().CopyTo(target);
        var stride = width * 4;
        FillRectangle(target, width, left, top, windowWidth, 24, 40, 80, 160);
        FillRectangle(target, width, left, top + 24, windowWidth, windowHeight - 24, 90, 90, 90);
        for (var y = 25; y < windowHeight - 1; y++)
        {
            content.AsSpan(y * stride, (windowWidth - 2) * 4).CopyTo(target.AsSpan((top + y) * stride + (left + 1) * 4));
        }
    }

    // Every pixel new and unrelated to its neighbours: the worst a codec can be given.
    public static void Noise(byte[] target, Random random) => random.NextBytes(target);

    // A text cursor: a bar two pixels wide and one line high.
    public static void Cursor(byte[] target, int width, int x, int y, bool visible)
    {
        var shade = visible ? (byte)0 : (byte)255;
        FillRectangle(target, width, x, y, 2, LineHeight, shade, shade, shade);
    }

    public static void FillRectangle(byte[] target, int width, int left, int top, int rectangleWidth, int rectangleHeight, byte red, byte green, byte blue)
    {
        for (var y = top; y < top + rectangleHeight; y++)
        {
            var row = target.AsSpan((y * width + left) * 4, rectangleWidth * 4);
            for (var x = 0; x < rectangleWidth; x++)
            {
                row[x * 4] = blue;
                row[x * 4 + 1] = green;
                row[x * 4 + 2] = red;
                row[x * 4 + 3] = 255;
            }
        }
    }

    // A few strokes in a box of six by ten pixels, one bit a pixel, one byte a row.
    private static byte[] Glyph(Random random)
    {
        var rows = new byte[LineHeight];
        var strokes = 2 + random.Next(3);
        for (var stroke = 0; stroke < strokes; stroke++)
        {
            var x = 1 + random.Next(6);
            var y = 3 + random.Next(10);
            var length = 3 + random.Next(6);
            var (stepX, stepY) = random.Next(4) switch
            {
                0 => (1, 0),
                1 => (0, 1),
                2 => (1, 1),
                _ => (1, -1),
            };
            for (var i = 0; i < length; i++, x += stepX, y += stepY)
            {
                if (x is >= 1 and <= 6 && y is >= 3 and <= 12)
                {
                    rows[y] |= (byte)(0x80 >> x);
                }
            }
        }

        return rows;
    }

    private static void DrawGlyph(byte[] page, int width, int left, int top, byte[] glyph, byte red, byte green, byte blue)
    {
        for (var y = 0; y < LineHeight; y++)
        {
            var bits = glyph[y];
            for (var x = 0; x < CellWidth; x++)
            {
                if ((bits & (0x80 >> x)) != 0)
                {
                    var at = ((top + y) * width + left + x) * 4;
                    page[at] = blue;
                    page[at + 1] = green;
                    page[at + 2] = red;
                }
            }
        }
    }
}
