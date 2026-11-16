using Xunit;

namespace Matgate.Display.Vnc.Tests;

// ZRLE: tiles of 64 x 64 inside one zlib stream. The tiles are written out by hand here; only the zlib around
// them comes from the library - as one stream that goes on from rectangle to rectangle, like a server's.
public class ZrleTests
{
    private const int Red = 0xFF0000;
    private const int Green = 0x00FF00;
    private const int Blue = 0x0000FF;
    private const int White = 0xFFFFFF;
    private const int Grey = 0x808080;

    [Fact]
    public void A_raw_tile()
    {
        var picture = Picture.Noise(10, 6, seed: 10);
        var tile = new Packet().U8(0);
        Cpixels(tile, picture);
        picture.AssertShownBy(Decode(10, 6, tile));
    }

    [Fact]
    public void A_tile_of_one_colour()
    {
        var expected = new Picture(64, 64, Blue);
        expected.AssertShownBy(Decode(64, 64, new Packet().U8(1).Cpixel(Blue)));
    }

    [Fact]
    public void A_palette_of_two_takes_a_bit_a_pixel()
    {
        // 10 pixels a row: two bytes, the last six bits unused.
        var tile = new Packet().U8(2).Cpixel(Grey).Cpixel(Red)
            .U8(0b1011_0001).U8(0b1000_0000)
            .U8(0b0000_0000).U8(0b0100_0000)
            .U8(0b1111_1111).U8(0b1100_0000);

        var expected = new Picture(10, 3, Grey);
        foreach (var x in new[] { 0, 2, 3, 7, 8 })
        {
            expected[x, 0] = Red;
        }

        expected[9, 1] = Red;
        expected.Fill(0, 2, 10, 1, Red);
        expected.AssertShownBy(Decode(10, 3, tile));
    }

    [Fact]
    public void A_palette_of_three_or_four_takes_two_bits_a_pixel()
    {
        // 5 pixels a row: 0 1 2 3 1, then 3 3 0 0 2.
        var four = new Packet().U8(4).Cpixel(Grey).Cpixel(Red).Cpixel(Green).Cpixel(Blue)
            .U8(0b00_01_10_11).U8(0b01_000000)
            .U8(0b11_11_00_00).U8(0b10_000000);
        var expected = new Picture(5, 2);
        Row(expected, 0, Grey, Red, Green, Blue, Red);
        Row(expected, 1, Blue, Blue, Grey, Grey, Green);
        expected.AssertShownBy(Decode(5, 2, four));

        var three = new Packet().U8(3).Cpixel(Grey).Cpixel(Red).Cpixel(Green)
            .U8(0b00_01_10_00).U8(0b01_000000);
        expected = new Picture(5, 1);
        Row(expected, 0, Grey, Red, Green, Grey, Red);
        expected.AssertShownBy(Decode(5, 1, three));
    }

    [Fact]
    public void A_palette_of_up_to_sixteen_takes_four_bits_a_pixel()
    {
        var colours = Enumerable.Range(0, 16).Select(index => index * 0x101010 + 5).ToArray();
        var tile = new Packet().U8(16);
        foreach (var colour in colours)
        {
            tile.Cpixel(colour);
        }

        // 3 pixels a row: 10 5 15, then 0 1 2.
        tile.U8(0xA5).U8(0xF0).U8(0x01).U8(0x20);
        var expected = new Picture(3, 2);
        Row(expected, 0, colours[10], colours[5], colours[15]);
        Row(expected, 1, colours[0], colours[1], colours[2]);
        expected.AssertShownBy(Decode(3, 2, tile));

        // Five colours are the fewest that need four bits.
        var five = new Packet().U8(5).Cpixel(colours[0]).Cpixel(colours[1]).Cpixel(colours[2]).Cpixel(colours[3]).Cpixel(colours[4])
            .U8(0x43).U8(0x20);
        expected = new Picture(3, 1);
        Row(expected, 0, colours[4], colours[3], colours[2]);
        expected.AssertShownBy(Decode(3, 1, five));
    }

    [Fact]
    public void Runs_of_a_colour_go_on_into_the_next_row()
    {
        // 20 x 30 = 600 pixels: 300 red (255 + 44 + 1), 1 green, 256 blue (255 + 0 + 1), 43 white.
        var tile = new Packet().U8(128)
            .Cpixel(Red).U8(255).U8(44)
            .Cpixel(Green).U8(0)
            .Cpixel(Blue).U8(255).U8(0)
            .Cpixel(White).U8(42);

        var expected = new Picture(20, 30);
        Run(expected, 0, 300, Red);
        Run(expected, 300, 1, Green);
        Run(expected, 301, 256, Blue);
        Run(expected, 557, 43, White);
        expected.AssertShownBy(Decode(20, 30, tile));
    }

    [Fact]
    public void Runs_of_a_palette_colour()
    {
        // 20 x 30 = 600 pixels: one of colour 0, five of colour 1, 260 of colour 2, one of colour 2, 333 of colour 0.
        var tile = new Packet().U8(128 + 3).Cpixel(Red).Cpixel(Green).Cpixel(Blue)
            .U8(0)
            .U8(128 + 1).U8(4)
            .U8(128 + 2).U8(255).U8(4)
            .U8(2)
            .U8(128 + 0).U8(255).U8(77);

        var expected = new Picture(20, 30);
        Run(expected, 0, 1, Red);
        Run(expected, 1, 5, Green);
        Run(expected, 6, 261, Blue);
        Run(expected, 267, 333, Red);
        expected.AssertShownBy(Decode(20, 30, tile));
    }

    [Fact]
    public void The_largest_palette_with_runs_has_127_colours()
    {
        var tile = new Packet().U8(255);
        for (var index = 0; index < 127; index++)
        {
            tile.Cpixel(index * 2);
        }

        tile.U8(126).U8(128 + 126).U8(1).U8(0);
        var expected = new Picture(4, 1);
        Row(expected, 0, 252, 252, 252, 0);
        expected.AssertShownBy(Decode(4, 1, tile));
    }

    // 130 x 70 pixels: three tiles in a row (64, 64, 2 wide), two rows of them (64 and 6 high).
    [Fact]
    public void Tiles_go_from_left_to_right_and_from_top_to_bottom()
    {
        var first = Picture.Noise(64, 64, seed: 11);
        var last = Picture.Noise(2, 6, seed: 12);
        var tiles = new Packet().U8(0);
        Cpixels(tiles, first);
        tiles.U8(1).Cpixel(Red);
        tiles.U8(128).Cpixel(Green).U8(127); // 2 x 64 = 128 pixels in one run
        tiles.U8(2).Cpixel(Grey).Cpixel(White);
        for (var row = 0; row < 6; row++)
        {
            tiles.U8(0xFF).Zeros(7); // 64 pixels a row: the first eight white
        }

        tiles.U8(1).Cpixel(Blue);
        tiles.U8(0);
        Cpixels(tiles, last);

        var expected = new Picture(134, 75);
        expected.Draw(first, 3, 4);
        expected.Fill(67, 4, 64, 64, Red);
        expected.Fill(131, 4, 2, 64, Green);
        expected.Fill(3, 68, 64, 6, Grey);
        expected.Fill(3, 68, 8, 6, White);
        expected.Fill(67, 68, 64, 6, Blue);
        expected.Draw(last, 131, 68);

        var framebuffer = new VncFramebuffer(134, 75);
        using var decoder = new ZrleDecoder();
        using var packer = new Packer();
        decoder.Decode(Rectangle(packer, tiles).Reader(), framebuffer, 3, 4, 130, 70);
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void The_zlib_stream_goes_on_from_rectangle_to_rectangle()
    {
        var picture = Picture.Noise(40, 30, seed: 13);
        var tile = new Packet().U8(0);
        Cpixels(tile, picture);

        var framebuffer = new VncFramebuffer(90, 30);
        using var decoder = new ZrleDecoder();
        using var packer = new Packer();
        var stream = new Packet()
            .Bytes(Rectangle(packer, tile))
            .Bytes(Rectangle(packer, new Packet().U8(1).Cpixel(Red)))
            .Bytes(Rectangle(packer, tile)) // the same bytes again: packed as "what was there before"
            .U8(0x77);
        var reader = stream.Reader();
        decoder.Decode(reader, framebuffer, 0, 0, 40, 30);
        decoder.Decode(reader, framebuffer, 40, 0, 10, 30);
        decoder.Decode(reader, framebuffer, 50, 0, 40, 30);

        // Each rectangle took exactly its own bytes.
        Assert.Equal(0x77, reader.ReadByte());
        var expected = new Picture(90, 30);
        expected.Draw(picture, 0, 0);
        expected.Fill(40, 0, 10, 30, Red);
        expected.Draw(picture, 50, 0);
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void A_rectangle_without_pixels_still_takes_its_piece_of_the_stream()
    {
        var framebuffer = new VncFramebuffer(8, 8);
        using var decoder = new ZrleDecoder();
        using var packer = new Packer();
        var stream = new Packet()
            .Bytes(Rectangle(packer, new Packet().U8(1).Cpixel(Red)))
            .Bytes(Rectangle(packer, new Packet())) // a flush and nothing else
            .Bytes(Rectangle(packer, new Packet().U8(1).Cpixel(Blue)))
            .U8(0x77);
        var reader = stream.Reader();
        decoder.Decode(reader, framebuffer, 0, 0, 4, 4);
        decoder.Decode(reader, framebuffer, 4, 4, 0, 0);
        decoder.Decode(reader, framebuffer, 4, 4, 4, 4);
        Assert.Equal(0x77, reader.ReadByte());

        var expected = new Picture(8, 8);
        expected.Fill(0, 0, 4, 4, Red);
        expected.Fill(4, 4, 4, 4, Blue);
        expected.AssertShownBy(framebuffer);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(127)]
    [InlineData(129)]
    public void A_kind_of_tile_that_does_not_exist_is_refused(int kind)
    {
        Assert.Throws<VncException>(() => Decode(4, 4, new Packet().U8(kind).Zeros(400)));
    }

    [Fact]
    public void An_index_beyond_the_palette_is_refused()
    {
        // Three colours take two bits, which can also say "3".
        var packed = new Packet().U8(3).Cpixel(Grey).Cpixel(Red).Cpixel(Green).U8(0b00_01_10_11);
        Assert.Throws<VncException>(() => Decode(4, 1, packed));

        var runs = new Packet().U8(128 + 3).Cpixel(Grey).Cpixel(Red).Cpixel(Green).U8(0).U8(3).U8(0).U8(0);
        Assert.Throws<VncException>(() => Decode(4, 1, runs));

        var longRun = new Packet().U8(128 + 3).Cpixel(Grey).Cpixel(Red).Cpixel(Green).U8(128 + 5).U8(3);
        Assert.Throws<VncException>(() => Decode(4, 1, longRun));
    }

    [Fact]
    public void A_run_longer_than_its_tile_is_refused()
    {
        Assert.Throws<VncException>(() => Decode(4, 4, new Packet().U8(128).Cpixel(Red).U8(16)));
        Assert.Throws<VncException>(() => Decode(4, 4, new Packet().U8(128).Cpixel(Red).U8(14).Cpixel(Blue).U8(1)));

        // A length that never ends: 255 for ever.
        var endless = new Packet().U8(128).Cpixel(Red);
        for (var index = 0; index < 100000; index++)
        {
            endless.U8(255);
        }

        Assert.Throws<VncException>(() => Decode(64, 64, endless));
    }

    [Fact]
    public void Less_data_than_the_tiles_need_is_refused()
    {
        var picture = Picture.Noise(10, 6, seed: 14);
        var tile = new Packet().U8(0);
        Cpixels(tile, picture);

        // The stream is complete; the picture in it is two rows short.
        Assert.Throws<VncException>(() => Decode(10, 8, tile));
    }

    private static VncFramebuffer Decode(int width, int height, Packet tiles)
    {
        var framebuffer = new VncFramebuffer(width, height);
        using var decoder = new ZrleDecoder();
        using var packer = new Packer();
        decoder.Decode(Rectangle(packer, tiles).Reader(), framebuffer, 0, 0, width, height);
        return framebuffer;
    }

    // What follows the rectangle's header: the length of the compressed piece, and the piece.
    internal static Packet Rectangle(Packer packer, Packet tiles)
    {
        var packed = packer.Pack(tiles.ToArray());
        return new Packet().U32((uint)packed.Length).Bytes(packed);
    }

    internal static void Cpixels(Packet packet, Picture picture)
    {
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                packet.Cpixel(picture[x, y]);
            }
        }
    }

    private static void Row(Picture picture, int y, params int[] colours)
    {
        for (var x = 0; x < colours.Length; x++)
        {
            picture[x, y] = colours[x];
        }
    }

    // Pixels counted along the rows, as a run goes.
    private static void Run(Picture picture, int start, int length, int colour)
    {
        for (var index = start; index < start + length; index++)
        {
            picture[index % picture.Width, index / picture.Width] = colour;
        }
    }
}
