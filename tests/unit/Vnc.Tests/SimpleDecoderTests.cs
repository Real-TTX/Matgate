using Xunit;

namespace Matgate.Display.Vnc.Tests;

// Raw, CopyRect, RRE and Hextile: the encodings without compression. Every rectangle is written out byte by
// byte here, as the specification describes it, and the framebuffer afterwards compared with the picture it
// should show.
public class SimpleDecoderTests
{
    private const int Red = 0xFF0000;
    private const int Green = 0x00FF00;
    private const int Blue = 0x0000FF;
    private const int White = 0xFFFFFF;
    private const int Grey = 0x808080;
    private const int Orange = 0xFF8000;

    [Fact]
    public void Raw_puts_the_pixels_where_they_belong_and_makes_them_opaque()
    {
        var framebuffer = new VncFramebuffer(5, 4);
        var packet = new Packet()
            .Pixel(Red).Pixel(Green, filler: 0).Pixel(Blue, filler: 0xFF)
            .Pixel(White).Pixel(Grey).Pixel(Orange);
        RawDecoder.Decode(packet.Reader(), framebuffer, 1, 1, 3, 2);

        var expected = new Picture(5, 4);
        expected[1, 1] = Red;
        expected[2, 1] = Green;
        expected[3, 1] = Blue;
        expected[1, 2] = White;
        expected[2, 2] = Grey;
        expected[3, 2] = Orange;
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Raw_takes_rows_that_span_the_desktop_in_one_piece()
    {
        var picture = Picture.Noise(37, 9, seed: 1);
        var framebuffer = new VncFramebuffer(37, 11);
        var packet = new Packet();
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                packet.Pixel(picture[x, y]);
            }
        }

        RawDecoder.Decode(packet.Reader(), framebuffer, 0, 2, 37, 9);

        var expected = new Picture(37, 11);
        expected.Draw(picture, 0, 2);
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Raw_reads_a_picture_larger_than_the_readers_buffer()
    {
        // 300 x 200 pixels are 240000 bytes - several times what the reader holds at once.
        var picture = Picture.Noise(300, 200, seed: 2);
        var packet = new Packet();
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                packet.Pixel(picture[x, y]);
            }
        }

        var whole = new VncFramebuffer(300, 200);
        RawDecoder.Decode(packet.Reader(), whole, 0, 0, 300, 200);
        picture.AssertShownBy(whole);

        var part = new VncFramebuffer(310, 205);
        RawDecoder.Decode(packet.Reader(), part, 10, 5, 300, 200);
        var expected = new Picture(310, 205);
        expected.Draw(picture, 10, 5);
        expected.AssertShownBy(part);
    }

    [Theory]
    [InlineData(2, 1, 5, 4)] // down and to the right: source and destination overlap
    [InlineData(5, 4, 2, 1)] // up and to the left
    [InlineData(2, 3, 4, 3)] // within the same rows
    [InlineData(4, 3, 2, 3)]
    [InlineData(0, 0, 10, 6)] // no overlap at all
    public void CopyRect_moves_a_part_of_the_picture(int sourceX, int sourceY, int x, int y)
    {
        const int Width = 8;
        const int Height = 6;
        var before = Picture.Noise(20, 14, seed: 3);
        var framebuffer = Show(before);

        CopyRectDecoder.Decode(new Packet().U16(sourceX).U16(sourceY).Reader(), framebuffer, x, y, Width, Height);

        var expected = new Picture(20, 14);
        expected.Draw(before, 0, 0);
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                expected[x + column, y + row] = before[sourceX + column, sourceY + row];
            }
        }

        expected.AssertShownBy(framebuffer);
    }

    [Theory]
    [InlineData(13, 0)]
    [InlineData(0, 9)]
    [InlineData(65535, 65535)]
    public void CopyRect_from_outside_the_desktop_is_refused(int sourceX, int sourceY)
    {
        var framebuffer = new VncFramebuffer(20, 14);
        Assert.Throws<VncException>(() => CopyRectDecoder.Decode(new Packet().U16(sourceX).U16(sourceY).Reader(), framebuffer, 0, 0, 8, 6));
    }

    [Fact]
    public void Rre_draws_rectangles_on_a_background()
    {
        var framebuffer = new VncFramebuffer(12, 10);
        var packet = new Packet()
            .U32(3).Pixel(Grey)
            .Pixel(Red).U16(0).U16(0).U16(4).U16(2)
            .Pixel(Blue).U16(2).U16(1).U16(5).U16(5) // over a part of the red one
            .Pixel(White).U16(9).U16(7).U16(1).U16(1); // the last pixel of the rectangle
        RreDecoder.Decode(packet.Reader(), framebuffer, 1, 1, 10, 8);

        var expected = new Picture(12, 10);
        expected.Fill(1, 1, 10, 8, Grey);
        expected.Fill(1, 1, 4, 2, Red);
        expected.Fill(3, 2, 5, 5, Blue);
        expected[10, 8] = White;
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Rre_with_more_rectangles_than_fit_into_one_read()
    {
        // 3000 one-pixel rectangles: the decoder takes them in batches.
        var picture = Picture.Noise(60, 50, seed: 4);
        var packet = new Packet().U32(3000).Pixel(0);
        for (var index = 0; index < 3000; index++)
        {
            packet.Pixel(picture[index % 60, index / 60]).U16(index % 60).U16(index / 60).U16(1).U16(1);
        }

        var framebuffer = new VncFramebuffer(60, 50);
        RreDecoder.Decode(packet.Reader(), framebuffer, 0, 0, 60, 50);
        picture.AssertShownBy(framebuffer);
    }

    [Theory]
    [InlineData(7, 0, 4, 1)] // too far right
    [InlineData(0, 7, 1, 2)] // too far down
    [InlineData(0, 0, 65535, 1)]
    public void Rre_with_a_rectangle_outside_its_own_is_refused(int x, int y, int width, int height)
    {
        var framebuffer = new VncFramebuffer(12, 10);
        var packet = new Packet().U32(1).Pixel(Grey).Pixel(Red).U16(x).U16(y).U16(width).U16(height);
        Assert.Throws<VncException>(() => RreDecoder.Decode(packet.Reader(), framebuffer, 1, 1, 10, 8));
    }

    // 40 x 20 pixels are three tiles in a row (16, 16 and 8 wide) and two rows of them (16 and 4 high).
    [Fact]
    public void Hextile_with_each_kind_of_tile()
    {
        var rawTile = Picture.Noise(16, 16, seed: 5);
        var smallRaw = Picture.Noise(8, 4, seed: 6);
        var packet = new Packet();

        // Tile 1: raw.
        packet.U8(1);
        Pixels(packet, rawTile);

        // Tile 2: a background, a foreground and two rectangles in the foreground colour.
        packet.U8(2 | 4 | 8).Pixel(Grey).Pixel(Red).U8(2)
            .U8(0x00).U8(0xF0) // at 0, 0: 16 wide, 1 high
            .U8(0x3F).U8(0x20); // at 3, 15: 3 wide, 1 high

        // Tile 3 (8 wide): nothing named - background and foreground are still those of tile 2.
        packet.U8(8).U8(1).U8(0x11).U8(0x11); // at 1, 1: 2 wide, 2 high

        // Tile 4 (4 high): still that background, rectangles with a colour each.
        packet.U8(8 | 16).U8(2)
            .Pixel(Blue).U8(0x00).U8(0xE3) // at 0, 0: 15 wide, 4 high
            .Pixel(White).U8(0xF1).U8(0x01); // at 15, 1: 1 wide, 2 high

        // Tile 5: only a new background.
        packet.U8(2).Pixel(Orange);

        // Tile 6 (8 x 4): raw again.
        packet.U8(1);
        Pixels(packet, smallRaw);

        var framebuffer = new VncFramebuffer(44, 23);
        HextileDecoder.Decode(packet.Reader(), framebuffer, 2, 3, 40, 20);

        var expected = new Picture(44, 23);
        expected.Draw(rawTile, 2, 3);
        expected.Fill(18, 3, 16, 16, Grey);
        expected.Fill(18, 3, 16, 1, Red);
        expected.Fill(21, 18, 3, 1, Red);
        expected.Fill(34, 3, 8, 16, Grey);
        expected.Fill(35, 4, 2, 2, Red);
        expected.Fill(2, 19, 16, 4, Grey);
        expected.Fill(2, 19, 15, 4, Blue);
        expected.Fill(17, 20, 1, 2, White);
        expected.Fill(18, 19, 16, 4, Orange);
        expected.Draw(smallRaw, 34, 19);
        expected.AssertShownBy(framebuffer);
    }

    [Theory]
    [InlineData(0xF0, 0x10)] // at 15, 0 and 2 wide: one pixel too far to the right
    [InlineData(0x0F, 0x01)] // at 0, 15 and 2 high: one pixel too far down
    public void Hextile_with_a_rectangle_outside_its_tile_is_refused(int position, int size)
    {
        var framebuffer = new VncFramebuffer(16, 16);
        var packet = new Packet().U8(2 | 4 | 8).Pixel(Grey).Pixel(Red).U8(1).U8(position).U8(size);
        Assert.Throws<VncException>(() => HextileDecoder.Decode(packet.Reader(), framebuffer, 0, 0, 16, 16));
    }

    [Fact]
    public void Hextile_in_a_tile_smaller_than_sixteen_keeps_to_the_tile()
    {
        // The tile is 5 x 3: a rectangle that would be fine in a full tile is outside of this one.
        var framebuffer = new VncFramebuffer(5, 3);
        var packet = new Packet().U8(2 | 4 | 8).Pixel(Grey).Pixel(Red).U8(1).U8(0x40).U8(0x10);
        Assert.Throws<VncException>(() => HextileDecoder.Decode(packet.Reader(), framebuffer, 0, 0, 5, 3));
    }

    [Fact]
    public void Rectangles_without_pixels_take_nothing_but_their_headers()
    {
        var framebuffer = new VncFramebuffer(10, 10);
        var raw = new Packet().U8(0x77).Reader();
        RawDecoder.Decode(raw, framebuffer, 5, 5, 0, 3);
        HextileDecoder.Decode(raw, framebuffer, 5, 5, 3, 0);
        Assert.Equal(0x77, raw.ReadByte());

        var rre = new Packet().U32(0).Pixel(Red).U8(0x77).Reader();
        RreDecoder.Decode(rre, framebuffer, 10, 10, 0, 0);
        Assert.Equal(0x77, rre.ReadByte());
        new Picture(10, 10).AssertShownBy(framebuffer);
    }

    internal static void Pixels(Packet packet, Picture picture)
    {
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                packet.Pixel(picture[x, y]);
            }
        }
    }

    // A framebuffer that shows the picture - put there the plain way, not through a decoder.
    private static VncFramebuffer Show(Picture picture)
    {
        var framebuffer = new VncFramebuffer(picture.Width, picture.Height);
        picture.ToBgrx().CopyTo(framebuffer.Pixels, 0);
        return framebuffer;
    }
}
