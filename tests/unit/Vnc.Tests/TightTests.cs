using System.IO.Compression;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

// Tight without JPEG. A rectangle starts with a control byte: the low four bits reset zlib streams, the high
// four say what follows. The bytes are written out by hand here; only the zlib comes from the library - as
// streams that go on from rectangle to rectangle, like a server's.
public class TightTests
{
    private const int Red = 0xFF0000;
    private const int Green = 0x00FF00;
    private const int Blue = 0x0000FF;
    private const int White = 0xFFFFFF;
    private const int Grey = 0x808080;

    private const int Fill = 0x80;
    private const int Jpeg = 0x90;
    private const int ExplicitFilter = 0x40;
    private const int CopyFilter = 0;
    private const int PaletteFilter = 1;
    private const int GradientFilter = 2;

    [Fact]
    public void A_fill_is_one_colour()
    {
        var framebuffer = new VncFramebuffer(30, 20);
        using var decoder = new TightDecoder();
        decoder.Decode(new Packet().U8(Fill).Tpixel(0x123456).Reader(), framebuffer, 5, 6, 20, 10);

        var expected = new Picture(30, 20);
        expected.Fill(5, 6, 20, 10, 0x123456);
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Pixels_as_they_are_are_compressed()
    {
        var picture = Picture.Noise(21, 9, seed: 20);
        using var packer = new Packer();
        var rectangle = new Packet().U8(0x00);
        Compressed(rectangle, packer, Tpixels(picture));

        picture.AssertShownBy(Decode(21, 9, rectangle));
    }

    [Theory]
    [InlineData(3, 1)] // 9 bytes
    [InlineData(1, 3)]
    [InlineData(1, 1)]
    public void Fewer_than_twelve_bytes_are_not_compressed(int width, int height)
    {
        var picture = Picture.Noise(width, height, seed: 21);
        var rectangle = new Packet().U8(0x00).Bytes(Tpixels(picture)).U8(0x77);

        var framebuffer = new VncFramebuffer(width, height);
        using var decoder = new TightDecoder();
        var reader = rectangle.Reader();
        decoder.Decode(reader, framebuffer, 0, 0, width, height);
        Assert.Equal(0x77, reader.ReadByte());
        picture.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Twelve_bytes_are_compressed()
    {
        // 4 x 1 pixels are exactly twelve bytes: the first size that comes with a length and zlib.
        var picture = Picture.Noise(4, 1, seed: 22);
        using var packer = new Packer();
        var rectangle = new Packet().U8(0x00);
        Compressed(rectangle, packer, Tpixels(picture));
        picture.AssertShownBy(Decode(4, 1, rectangle));
    }

    [Fact]
    public void The_copy_filter_can_be_named()
    {
        var picture = Picture.Noise(7, 5, seed: 23);
        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter).U8(CopyFilter);
        Compressed(rectangle, packer, Tpixels(picture));
        picture.AssertShownBy(Decode(7, 5, rectangle));
    }

    [Fact]
    public void A_palette_of_two_takes_a_bit_a_pixel()
    {
        // 10 pixels a row: two bytes, the last six bits unused. Six rows are twelve bytes: compressed.
        var bits = new Packet()
            .U8(0b1011_0001).U8(0b1000_0000)
            .U8(0b0000_0000).U8(0b0100_0000)
            .U8(0b1111_1111).U8(0b1100_0000)
            .Zeros(6);
        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter | 0x10).U8(PaletteFilter).U8(1).Tpixel(Grey).Tpixel(Red);
        Compressed(rectangle, packer, bits.ToArray());

        var expected = new Picture(10, 6, Grey);
        foreach (var x in new[] { 0, 2, 3, 7, 8 })
        {
            expected[x, 0] = Red;
        }

        expected[9, 1] = Red;
        expected.Fill(0, 2, 10, 1, Red);
        expected.AssertShownBy(Decode(10, 6, rectangle));
    }

    [Fact]
    public void A_small_palette_of_two_is_not_compressed()
    {
        // 16 x 2 pixels: four bytes.
        var rectangle = new Packet().U8(ExplicitFilter).U8(PaletteFilter).U8(1).Tpixel(Blue).Tpixel(White)
            .U8(0xF0).U8(0x0F).U8(0x00).U8(0x01);

        var expected = new Picture(16, 2, Blue);
        expected.Fill(0, 0, 4, 1, White);
        expected.Fill(12, 0, 4, 1, White);
        expected[15, 1] = White;
        expected.AssertShownBy(Decode(16, 2, rectangle));
    }

    [Fact]
    public void A_larger_palette_takes_a_byte_a_pixel()
    {
        int[] colours = [Red, Green, Blue, White, Grey];
        var picture = new Picture(9, 4);
        var indexes = new byte[9 * 4];
        for (var index = 0; index < indexes.Length; index++)
        {
            indexes[index] = (byte)((index * 7 + index / 5) % colours.Length);
            picture[index % 9, index / 9] = colours[indexes[index]];
        }

        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter | 0x20).U8(PaletteFilter).U8(colours.Length - 1);
        foreach (var colour in colours)
        {
            rectangle.Tpixel(colour);
        }

        Compressed(rectangle, packer, indexes);
        picture.AssertShownBy(Decode(9, 4, rectangle));
    }

    [Fact]
    public void A_palette_of_256()
    {
        var picture = new Picture(64, 8);
        var indexes = new byte[64 * 8];
        var rectangle = new Packet().U8(ExplicitFilter).U8(PaletteFilter).U8(255);
        for (var colour = 0; colour < 256; colour++)
        {
            rectangle.Tpixel((colour * 0x010203) & 0xFFFFFF);
        }

        for (var index = 0; index < indexes.Length; index++)
        {
            indexes[index] = (byte)(255 - index % 256);
            picture[index % 64, index / 64] = (indexes[index] * 0x010203) & 0xFFFFFF;
        }

        using var packer = new Packer();
        Compressed(rectangle, packer, indexes);
        picture.AssertShownBy(Decode(64, 8, rectangle));
    }

    [Theory]
    [InlineData(40, 25)]
    [InlineData(1, 30)] // a single column: only the pixel above predicts
    [InlineData(30, 1)] // a single row: only the pixel to the left predicts
    public void The_gradient_filter_sends_the_difference_to_a_prediction(int width, int height)
    {
        var picture = Picture.Smooth(width, height);
        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter).U8(GradientFilter);
        Compressed(rectangle, packer, Gradient(picture));
        picture.AssertShownBy(Decode(width, height, rectangle));
    }

    [Fact]
    public void The_gradient_filter_with_pixels_that_jump()
    {
        // Noise makes the prediction leave the range of a byte in both directions, where it is cut off.
        var picture = Picture.Noise(33, 17, seed: 24);
        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter).U8(GradientFilter);
        Compressed(rectangle, packer, Gradient(picture));
        picture.AssertShownBy(Decode(33, 17, rectangle));
    }

    [Fact]
    public void A_small_gradient_is_not_compressed()
    {
        var picture = Picture.Noise(3, 1, seed: 25);
        var rectangle = new Packet().U8(ExplicitFilter).U8(GradientFilter).Bytes(Gradient(picture));
        picture.AssertShownBy(Decode(3, 1, rectangle));
    }

    [Fact]
    public void Four_streams_each_go_on_by_themselves()
    {
        var pictures = Enumerable.Range(0, 4).Select(index => Picture.Noise(20, 10, seed: 30 + index)).ToArray();
        var packers = Enumerable.Range(0, 4).Select(_ => new Packer()).ToArray();
        var stream = new Packet();

        // Three rounds through the four streams: a stream's second piece only unpacks with its first.
        for (var round = 0; round < 3; round++)
        {
            for (var id = 0; id < 4; id++)
            {
                stream.U8(id << 4);
                Compressed(stream, packers[id], Tpixels(pictures[id]));
            }
        }

        stream.U8(0x77);
        var framebuffer = new VncFramebuffer(80, 30);
        using var decoder = new TightDecoder();
        var reader = stream.Reader();
        for (var round = 0; round < 3; round++)
        {
            for (var id = 0; id < 4; id++)
            {
                decoder.Decode(reader, framebuffer, id * 20, round * 10, 20, 10);
            }
        }

        Assert.Equal(0x77, reader.ReadByte());
        var expected = new Picture(80, 30);
        for (var round = 0; round < 3; round++)
        {
            for (var id = 0; id < 4; id++)
            {
                expected.Draw(pictures[id], id * 20, round * 10);
            }
        }

        expected.AssertShownBy(framebuffer);
        foreach (var packer in packers)
        {
            packer.Dispose();
        }
    }

    [Fact]
    public void A_reset_bit_starts_a_stream_anew()
    {
        var first = Picture.Noise(20, 10, seed: 40);
        var second = Picture.Noise(20, 10, seed: 41);
        using var old = new Packer();
        using var fresh = new Packer();
        var stream = new Packet();

        // Stream 2, twice; then the server starts it anew (bit 2 of the control byte) and goes on with it.
        stream.U8(0x20);
        Compressed(stream, old, Tpixels(first));
        stream.U8(0x20);
        Compressed(stream, old, Tpixels(second));
        stream.U8(0x20 | 0x04);
        Compressed(stream, fresh, Tpixels(second));
        stream.U8(0x20);
        Compressed(stream, fresh, Tpixels(first));

        // A reset can also come with a rectangle that does not use the stream at all.
        using var third = new Packer();
        stream.U8(Fill | 0x04).Tpixel(Red);
        stream.U8(0x20);
        Compressed(stream, third, Tpixels(second));

        var framebuffer = new VncFramebuffer(20, 60);
        using var decoder = new TightDecoder();
        var reader = stream.Reader();
        for (var row = 0; row < 6; row++)
        {
            decoder.Decode(reader, framebuffer, 0, row * 10, 20, 10);
        }

        var expected = new Picture(20, 60);
        expected.Draw(first, 0, 0);
        expected.Draw(second, 0, 10);
        expected.Draw(second, 0, 20);
        expected.Draw(first, 0, 30);
        expected.Fill(0, 40, 20, 10, Red);
        expected.Draw(second, 0, 50);
        expected.AssertShownBy(framebuffer);
    }

    [Fact]
    public void Without_the_reset_bit_a_new_stream_cannot_be_read()
    {
        // The counterpart of the test above: the server starts its stream anew and does not say so.
        var picture = Picture.Noise(20, 10, seed: 42);
        using var old = new Packer();
        using var fresh = new Packer();
        var stream = new Packet().U8(0x20);
        Compressed(stream, old, Tpixels(picture));
        stream.U8(0x20);
        Compressed(stream, fresh, Tpixels(picture));

        var framebuffer = new VncFramebuffer(20, 20);
        using var decoder = new TightDecoder();
        var reader = stream.Reader();
        decoder.Decode(reader, framebuffer, 0, 0, 20, 10);
        Assert.Throws<VncException>(() => decoder.Decode(reader, framebuffer, 0, 10, 20, 10));
    }

    [Theory]
    [InlineData(5, 4, 1)] // 60 bytes of noise: a length in one byte
    [InlineData(30, 20, 2)] // 1800 bytes: two
    [InlineData(100, 70, 3)] // 21000 bytes: three
    public void The_length_takes_one_to_three_bytes(int width, int height, int lengthBytes)
    {
        var picture = Picture.Noise(width, height, seed: 43);
        using var packer = new Packer(CompressionLevel.NoCompression);
        var packed = packer.Pack(Tpixels(picture));
        Assert.Equal(lengthBytes, new Packet().CompactLength(packed.Length).Length);

        var rectangle = new Packet().U8(0x00).CompactLength(packed.Length).Bytes(packed);
        picture.AssertShownBy(Decode(width, height, rectangle));
    }

    [Fact]
    public void The_length_as_the_specification_writes_it()
    {
        // 10000 is 0x90 0x4E in the specification's own example.
        Assert.Equal(new byte[] { 0x90, 0x4E }, new Packet().CompactLength(10000).ToArray());
    }

    [Fact]
    public void A_server_told_not_to_compress_sends_the_data_as_it_is()
    {
        // Control 0xA0: as 0x00, but without zlib - the length is that of the data itself.
        var picture = Picture.Noise(21, 9, seed: 44);
        var data = Tpixels(picture);
        var plain = new Packet().U8(0xA0).CompactLength(data.Length).Bytes(data);
        picture.AssertShownBy(Decode(21, 9, plain));

        // Control 0xE0: the same with a filter.
        var filtered = new Packet().U8(0xE0).U8(GradientFilter).CompactLength(data.Length).Bytes(Gradient(picture));
        picture.AssertShownBy(Decode(21, 9, filtered));

        // And a rectangle of less than twelve bytes has no length at all.
        var small = Picture.Noise(2, 1, seed: 45);
        small.AssertShownBy(Decode(2, 1, new Packet().U8(0xA0).Bytes(Tpixels(small))));
    }

    [Fact]
    public void A_wide_rectangle_is_read_row_by_row()
    {
        // 3000 pixels a row are 9000 bytes: several rows do not fit into what the decoder unpacks at once.
        var picture = Picture.Noise(3000, 12, seed: 46);
        using var packer = new Packer();
        var rectangle = new Packet().U8(0x00);
        Compressed(rectangle, packer, Tpixels(picture));
        picture.AssertShownBy(Decode(3000, 12, rectangle));
    }

    [Fact]
    public void Jpeg_is_refused_because_it_was_not_asked_for()
    {
        var exception = Assert.Throws<VncException>(() => Decode(8, 8, new Packet().U8(Jpeg).CompactLength(4).Zeros(4)));
        Assert.Contains("JPEG", exception.Message);
    }

    [Theory]
    [InlineData(0xB0)]
    [InlineData(0xC0)]
    [InlineData(0xD0)]
    [InlineData(0xF0)]
    public void A_control_byte_that_means_nothing_is_refused(int control)
    {
        Assert.Throws<VncException>(() => Decode(8, 8, new Packet().U8(control).Zeros(300)));
    }

    [Fact]
    public void A_filter_that_does_not_exist_is_refused()
    {
        Assert.Throws<VncException>(() => Decode(8, 8, new Packet().U8(ExplicitFilter).U8(3).Zeros(300)));
    }

    [Fact]
    public void An_index_beyond_the_palette_is_refused()
    {
        using var packer = new Packer();
        var rectangle = new Packet().U8(ExplicitFilter).U8(PaletteFilter).U8(2).Tpixel(Red).Tpixel(Green).Tpixel(Blue);
        var indexes = new byte[16];
        indexes[9] = 3;
        Compressed(rectangle, packer, indexes);
        Assert.Throws<VncException>(() => Decode(4, 4, rectangle));
    }

    [Fact]
    public void Less_data_than_the_rectangle_needs_is_refused()
    {
        var picture = Picture.Noise(10, 6, seed: 47);
        using var packer = new Packer();
        var rectangle = new Packet().U8(0x00);
        Compressed(rectangle, packer, Tpixels(picture));

        // The stream is complete; the picture in it is two rows short.
        Assert.Throws<VncException>(() => Decode(10, 8, rectangle));
    }

    [Fact]
    public void A_length_that_is_not_that_of_the_data_is_refused_where_nothing_is_compressed()
    {
        var data = Tpixels(Picture.Noise(21, 9, seed: 48));
        Assert.Throws<VncException>(() => Decode(21, 9, new Packet().U8(0xA0).CompactLength(data.Length - 1).Bytes(data)));
    }

    private static VncFramebuffer Decode(int width, int height, Packet rectangle)
    {
        var framebuffer = new VncFramebuffer(width, height);
        using var decoder = new TightDecoder();
        decoder.Decode(rectangle.Reader(), framebuffer, 0, 0, width, height);
        return framebuffer;
    }

    internal static void Compressed(Packet packet, Packer packer, byte[] data)
    {
        var packed = packer.Pack(data);
        packet.CompactLength(packed.Length).Bytes(packed);
    }

    internal static byte[] Tpixels(Picture picture)
    {
        var packet = new Packet();
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                packet.Tpixel(picture[x, y]);
            }
        }

        return packet.ToArray();
    }

    // The gradient filter as a server applies it: for red, green and blue of every pixel, the difference
    // between what it is and what its neighbours predict - left plus above minus above-left, cut off at 0
    // and 255, with nothing (zero) outside the rectangle.
    internal static byte[] Gradient(Picture picture)
    {
        var data = new byte[picture.Width * picture.Height * 3];
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                for (var colour = 0; colour < 3; colour++)
                {
                    var shift = 16 - colour * 8;
                    var left = x > 0 ? (picture[x - 1, y] >> shift) & 0xFF : 0;
                    var above = y > 0 ? (picture[x, y - 1] >> shift) & 0xFF : 0;
                    var aboveLeft = x > 0 && y > 0 ? (picture[x - 1, y - 1] >> shift) & 0xFF : 0;
                    var predicted = Math.Clamp(left + above - aboveLeft, 0, 255);
                    data[(y * picture.Width + x) * 3 + colour] = (byte)(((picture[x, y] >> shift) & 0xFF) - predicted);
                }
            }
        }

        return data;
    }
}
