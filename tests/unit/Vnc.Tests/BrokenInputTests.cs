using Xunit;

namespace Matgate.Display.Vnc.Tests;

// What a server sends cannot be trusted. Whatever arrives - a rectangle that breaks off, one with bytes
// changed, plain noise - a decoder either draws something or ends with a VncException. Never with an index
// out of range, never with memory it cannot get, never without an end.
public class BrokenInputTests
{
    private const int Width = 90;
    private const int Height = 80;

    // One rectangle for every decoder and every path through it: its bytes, and how it is decoded.
    private sealed record Sample(string Name, byte[] Bytes, int X, int Y, int RectWidth, int RectHeight, Action<VncReader, VncFramebuffer, int, int, int, int> Decode);

    public static IEnumerable<object[]> Names() => Samples().Select(sample => new object[] { sample.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void The_sample_itself_is_decoded(string name)
    {
        var sample = Samples().Single(candidate => candidate.Name == name);
        var reader = new VncReader(new MemoryStream(sample.Bytes));
        sample.Decode(reader, new VncFramebuffer(Width, Height), sample.X, sample.Y, sample.RectWidth, sample.RectHeight);

        // And takes all of its bytes: the truncation test below relies on that.
        Assert.False(reader.TryReadByte(out _));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_rectangle_that_breaks_off_anywhere_ends_in_a_VncException(string name)
    {
        var sample = Samples().Single(candidate => candidate.Name == name);
        for (var length = 0; length < sample.Bytes.Length; length++)
        {
            var reader = new VncReader(new MemoryStream(sample.Bytes, 0, length));
            var framebuffer = new VncFramebuffer(Width, Height);
            try
            {
                sample.Decode(reader, framebuffer, sample.X, sample.Y, sample.RectWidth, sample.RectHeight);
                Assert.Fail($"{name}: decoded although only {length} of {sample.Bytes.Length} bytes were there.");
            }
            catch (VncException)
            {
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_rectangle_with_bytes_changed_is_drawn_or_ends_in_a_VncException(string name)
    {
        var sample = Samples().Single(candidate => candidate.Name == name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 12345);
        var framebuffer = new VncFramebuffer(Width, Height);
        for (var round = 0; round < 3000; round++)
        {
            var bytes = (byte[])sample.Bytes.Clone();
            var changes = random.Next(1, 4);
            for (var change = 0; change < changes; change++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            DecodeOrRefuse(sample, bytes, framebuffer);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Noise_is_drawn_or_ends_in_a_VncException(string name)
    {
        var sample = Samples().Single(candidate => candidate.Name == name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 54321);
        var framebuffer = new VncFramebuffer(Width, Height);
        for (var round = 0; round < 3000; round++)
        {
            // The first bytes of the sample (its control bytes, lengths, the head of its zlib stream) lead a
            // decoder some way in before the noise begins.
            var kept = random.Next(Math.Min(sample.Bytes.Length, 24));
            var bytes = new byte[kept + random.Next(3000)];
            random.NextBytes(bytes);
            sample.Bytes.AsSpan(0, kept).CopyTo(bytes);
            DecodeOrRefuse(sample, bytes, framebuffer);
        }
    }

    // Nothing may reach outside the framebuffer, wherever in it a rectangle lies - also in its last row and
    // column - and whatever is in the rectangle.
    [Fact]
    public void Noise_in_rectangles_of_every_shape()
    {
        var random = new Random(2024);
        var samples = Samples().ToArray();
        for (var round = 0; round < 6000; round++)
        {
            var sample = samples[random.Next(samples.Length)];
            var framebuffer = new VncFramebuffer(random.Next(1, 200), random.Next(1, 200));
            var width = random.Next(0, framebuffer.Width + 1);
            var height = random.Next(0, framebuffer.Height + 1);
            var shaped = sample with
            {
                X = random.Next(0, framebuffer.Width - width + 1),
                Y = random.Next(0, framebuffer.Height - height + 1),
                RectWidth = width,
                RectHeight = height,
            };
            var bytes = new byte[random.Next(4000)];
            random.NextBytes(bytes);
            if (random.Next(2) == 0)
            {
                // Mostly zeros and small numbers get further into a decoder than noise does.
                for (var index = 0; index < bytes.Length; index++)
                {
                    bytes[index] = (byte)(bytes[index] % 5 == 0 ? bytes[index] : bytes[index] & 3);
                }
            }

            DecodeOrRefuse(shaped, bytes, framebuffer);
        }
    }

    private static void DecodeOrRefuse(Sample sample, byte[] bytes, VncFramebuffer framebuffer)
    {
        try
        {
            sample.Decode(new VncReader(new MemoryStream(bytes)), framebuffer, sample.X, sample.Y, sample.RectWidth, sample.RectHeight);
        }
        catch (VncException)
        {
        }
        catch (Exception exception)
        {
            Assert.Fail($"{sample.Name}: {exception.GetType().Name} instead of a VncException: {exception.Message} - input {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 200)))}");
        }
    }

    private static IEnumerable<Sample> Samples()
    {
        // The decoders with zlib streams get a new one for every call: each input starts a stream of its own.
        static void Zrle(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
        {
            using var decoder = new ZrleDecoder();
            decoder.Decode(reader, target, x, y, width, height);
        }

        static void Tight(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
        {
            using var decoder = new TightDecoder();
            decoder.Decode(reader, target, x, y, width, height);
        }

        static void Cursor(VncReader reader, VncFramebuffer target, int x, int y, int width, int height)
        {
            // The client refuses larger ones before it gets here.
            var cursorWidth = Math.Min(width, CursorDecoder.MaxSize);
            var cursorHeight = Math.Min(height, CursorDecoder.MaxSize);
            CursorDecoder.Decode(reader, cursorWidth, cursorHeight, new byte[cursorWidth * cursorHeight * 4]);
        }

        var noise = Picture.Noise(40, 30, seed: 70);

        var raw = new Packet();
        SimpleDecoderTests.Pixels(raw, noise);
        yield return new Sample("raw", raw.ToArray(), 7, 9, 40, 30, RawDecoder.Decode);

        yield return new Sample("copyrect", new Packet().U16(3).U16(4).ToArray(), 7, 9, 40, 30, CopyRectDecoder.Decode);

        var rre = new Packet().U32(3).Pixel(0x808080)
            .Pixel(0xFF0000).U16(0).U16(0).U16(4).U16(2)
            .Pixel(0x0000FF).U16(2).U16(1).U16(5).U16(5)
            .Pixel(0xFFFFFF).U16(39).U16(29).U16(1).U16(1);
        yield return new Sample("rre", rre.ToArray(), 7, 9, 40, 30, RreDecoder.Decode);

        // 40 x 30: three tiles in a row, two rows.
        var hextile = new Packet().U8(1);
        SimpleDecoderTests.Pixels(hextile, Picture.Noise(16, 16, seed: 71));
        hextile.U8(2 | 4 | 8).Pixel(0x808080).Pixel(0xFF0000).U8(2).U8(0x00).U8(0xF0).U8(0x3F).U8(0x20);
        hextile.U8(8).U8(1).U8(0x11).U8(0x11);
        hextile.U8(8 | 16).U8(2).Pixel(0x0000FF).U8(0x00).U8(0xE3).Pixel(0xFFFFFF).U8(0xF1).U8(0x01);
        hextile.U8(2).Pixel(0xFF8000);
        hextile.U8(1);
        SimpleDecoderTests.Pixels(hextile, Picture.Noise(8, 14, seed: 72));
        yield return new Sample("hextile", hextile.ToArray(), 7, 9, 40, 30, HextileDecoder.Decode);

        // 70 x 66: four tiles (64 and 6 wide, 64 and 2 high), plus one sample for each kind of tile alone.
        var tiles = new Packet().U8(0);
        ZrleTests.Cpixels(tiles, Picture.Noise(64, 64, seed: 73));
        tiles.U8(128).Cpixel(0x00FF00).U8(255).U8(128); // 6 x 64 = 384 pixels in one run
        tiles.U8(2).Cpixel(0x808080).Cpixel(0xFFFFFF).U8(0xFF).Zeros(7).U8(0x0F).Zeros(7);
        tiles.U8(128 + 3).Cpixel(0xFF0000).Cpixel(0x00FF00).Cpixel(0x0000FF).U8(0).U8(128 + 1).U8(4).U8(128 + 2).U8(4).U8(2);
        using (var packer = new Packer())
        {
            yield return new Sample("zrle tiles", ZrleTests.Rectangle(packer, tiles).ToArray(), 7, 9, 70, 66, Zrle);
        }

        using (var packer = new Packer())
        {
            yield return new Sample("zrle solid", ZrleTests.Rectangle(packer, new Packet().U8(1).Cpixel(0x123456)).ToArray(), 7, 9, 40, 30, Zrle);
        }

        using (var packer = new Packer())
        {
            var sixteen = new Packet().U8(16);
            for (var colour = 0; colour < 16; colour++)
            {
                sixteen.Cpixel(colour * 0x101010);
            }

            for (var row = 0; row < 30; row++)
            {
                for (var pair = 0; pair < 20; pair++)
                {
                    sixteen.U8((row + pair) & 0xFF);
                }
            }

            yield return new Sample("zrle sixteen colours", ZrleTests.Rectangle(packer, sixteen).ToArray(), 7, 9, 40, 30, Zrle);
        }

        yield return new Sample("tight fill", new Packet().U8(0x80).Tpixel(0x123456).ToArray(), 7, 9, 40, 30, Tight);

        using (var packer = new Packer())
        {
            var copy = new Packet().U8(0x00);
            TightTests.Compressed(copy, packer, TightTests.Tpixels(noise));
            yield return new Sample("tight copy", copy.ToArray(), 7, 9, 40, 30, Tight);
        }

        yield return new Sample("tight small", new Packet().U8(0x00).Bytes(TightTests.Tpixels(Picture.Noise(3, 1, seed: 74))).ToArray(), 7, 9, 3, 1, Tight);

        using (var packer = new Packer())
        {
            var two = new Packet().U8(0x50).U8(1).U8(1).Tpixel(0x808080).Tpixel(0xFF0000);
            var bits = new byte[5 * 30];
            new Random(75).NextBytes(bits);
            TightTests.Compressed(two, packer, bits);
            yield return new Sample("tight two colours", two.ToArray(), 7, 9, 40, 30, Tight);
        }

        using (var packer = new Packer())
        {
            var five = new Packet().U8(0x60).U8(1).U8(4).Tpixel(1).Tpixel(2).Tpixel(3).Tpixel(4).Tpixel(5);
            var indexes = new byte[40 * 30];
            for (var index = 0; index < indexes.Length; index++)
            {
                indexes[index] = (byte)(index % 5);
            }

            TightTests.Compressed(five, packer, indexes);
            yield return new Sample("tight five colours", five.ToArray(), 7, 9, 40, 30, Tight);
        }

        using (var packer = new Packer())
        {
            var gradient = new Packet().U8(0x40).U8(2);
            TightTests.Compressed(gradient, packer, TightTests.Gradient(Picture.Smooth(40, 30)));
            yield return new Sample("tight gradient", gradient.ToArray(), 7, 9, 40, 30, Tight);
        }

        var plain = TightTests.Tpixels(noise);
        yield return new Sample("tight without zlib", new Packet().U8(0xA0).CompactLength(plain.Length).Bytes(plain).ToArray(), 7, 9, 40, 30, Tight);

        // 13 x 7: thirteen pixels a row, and two bytes of mask for each.
        var cursor = new Packet();
        SimpleDecoderTests.Pixels(cursor, Picture.Noise(13, 7, seed: 76));
        for (var row = 0; row < 7; row++)
        {
            cursor.U8(0xF0 + row).U8(0x18);
        }

        yield return new Sample("cursor", cursor.ToArray(), 0, 0, 13, 7, Cursor);
    }
}
