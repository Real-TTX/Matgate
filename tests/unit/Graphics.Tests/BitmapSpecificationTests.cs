using System.Buffers.Binary;
using Xunit;

namespace Matgate.Display.Rdp.Graphics.Tests;

// Cases worked out by hand from the specifications: the rules a differential test only meets by chance, the
// examples the documents give, and what a decoder has to refuse (FreeRDP's vectors are all well-formed).
public class BitmapSpecificationTests
{
    // Framebuffer pixels as the decoder writes them, read as 0xXXRRGGBB. A pixel that was never written is 0.
    private const uint Black = 0xFF000000;
    private const uint White = 0xFFFFFFFF;
    private const uint Red = 0xFFFF0000;
    private const uint Green = 0xFF00FF00;
    private const uint Blue = 0xFF0000FF;
    private const uint Magenta = 0xFFFF00FF;
    private const uint Cyan = 0xFF00FFFF;
    private const uint Unwritten = 0;

    // ---- Interleaved RLE (MS-RDPBCGR 2.2.9.1.1.3.1.2.4 and 3.1.9) -------------------------------------------

    // "When encountering back-to-back background runs, the decompressor MUST write a one-pixel foreground run
    // to the destination buffer before processing the second background run [...] This one-pixel foreground
    // run is counted in the length of the run."
    [Fact]
    public void TwoBackgroundRunsInARowHaveAForegroundPixelBetweenThem()
    {
        // 8 x 2. First scan line: 3 background, 2 background, 3 red. Second: 4 background, 4 background.
        var (ok, pixels) = Rle(24, 8, 2, 0x03, 0x02, 0x63, 0x00, 0x00, 0xFF, 0x04, 0x04);
        Assert.True(ok);
        Assert.Equal(
            [
                // The second scan line: the picture above, with above XOR white where the two runs meet.
                Black, Black, Black, White, White, Red, Red, Red,

                // The first scan line has nothing above it: background is black, the pixel between is white.
                Black, Black, Black, White, Black, Red, Red, Red,
            ],
            pixels);
    }

    // "(if the first run is on the first scanline, and the second run is on the second scanline, then a
    // one-pixel foreground run MUST NOT be written to the destination buffer)"
    [Fact]
    public void ABackgroundRunThatBeginsTheSecondLineHasNoForegroundPixel()
    {
        var (ok, pixels) = Rle(24, 4, 2, 0x04, 0x04);
        Assert.True(ok);
        Assert.Equal([Black, Black, Black, Black, Black, Black, Black, Black], pixels);
    }

    // The specification's decoder decides once per order whether it is on the first scan line. A run that
    // begins there and ends on the second line is first-line all the way: no look at the line above.
    [Fact]
    public void AnOrderThatBeginsOnTheFirstLineKeepsItsRulesOnTheSecond()
    {
        // 4 x 2: two red, then a foreground run of six that takes the rest of the first line and the second.
        var (ok, pixels) = Rle(24, 4, 2, 0x62, 0x00, 0x00, 0xFF, 0x26);
        Assert.True(ok);
        Assert.Equal([White, White, White, White, Red, Red, White, White], pixels);

        // The same with a background run: black all the way, not a copy of the line above.
        (ok, pixels) = Rle(24, 4, 2, 0x62, 0x00, 0x00, 0xFF, 0x06);
        Assert.True(ok);
        Assert.Equal([Black, Black, Black, Black, Red, Red, Black, Black], pixels);

        // And with a foreground/background image of six pixels, mask 0b101101.
        (ok, pixels) = Rle(24, 4, 2, 0x62, 0x00, 0x00, 0xFF, 0x40, 0x05, 0x2D);
        Assert.True(ok);
        Assert.Equal([White, White, Black, White, Red, Red, White, Black], pixels);
    }

    [Fact]
    public void AForegroundRunIsTheLineAboveXorTheForegroundColour()
    {
        // 2 x 2 at 16 bits: the pixels 0x1234 and 0xFFFF, then a run of two with the colour set to 0x00FF.
        var (ok, pixels) = Rle(16, 2, 2, 0x82, 0x34, 0x12, 0xFF, 0xFF, 0xC2, 0xFF, 0x00);
        Assert.True(ok);
        Assert.Equal([Rgb565(0x1234 ^ 0x00FF), Rgb565(0xFFFF ^ 0x00FF), Rgb565(0x1234), Rgb565(0xFFFF)], pixels);

        // The colour stays set for the orders that follow: a third line with a plain foreground run.
        (ok, pixels) = Rle(16, 2, 3, 0x82, 0x34, 0x12, 0xFF, 0xFF, 0xC2, 0xFF, 0x00, 0x22);
        Assert.True(ok);
        Assert.Equal([Rgb565(0x1234), Rgb565(0xFFFF), Rgb565(0x1234 ^ 0x00FF), Rgb565(0xFFFF ^ 0x00FF), Rgb565(0x1234), Rgb565(0xFFFF)], pixels);
    }

    // "The run length encodes the number of pixel-pairs in the run (not pixels)."
    [Fact]
    public void ADitheredRunCountsPairsAndCrossesTheEndOfALine()
    {
        var (ok, pixels) = Rle(24, 3, 2, 0xE3, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00);
        Assert.True(ok);
        Assert.Equal([Blue, Red, Blue, Red, Blue, Red], pixels);
    }

    // "The individual bitmasks MUST each be processed from the low-order bit to the high-order bit."
    [Fact]
    public void AForegroundBackgroundImageTakesItsBitsLowestFirst()
    {
        // 8 x 2. First line: mask 0xB1 with white. Second line: the colour set to green, mask 0x0F.
        var (ok, pixels) = Rle(24, 8, 2, 0x41, 0xB1, 0xD1, 0x00, 0xFF, 0x00, 0x0F);
        Assert.True(ok);
        Assert.Equal(
            [
                Magenta, Green, Green, Green, White, White, Black, White,
                White, Black, Black, Black, White, White, Black, White,
            ],
            pixels);
    }

    [Fact]
    public void TheSpecialOrdersAreImagesOfEightPixelsAndSinglePixels()
    {
        // 0xF9 is the mask 0x03, 0xFA the mask 0x05.
        var (ok, pixels) = Rle(24, 8, 2, 0xF9, 0xFA);
        Assert.True(ok);
        Assert.Equal(
            [
                Black, White, White, Black, Black, Black, Black, Black,
                White, White, Black, Black, Black, Black, Black, Black,
            ],
            pixels);

        (ok, pixels) = Rle(24, 2, 1, 0xFD, 0xFE);
        Assert.True(ok);
        Assert.Equal([White, Black], pixels);

        // Eight pixels do not fit into what is left of this bitmap.
        Assert.False(Rle(24, 7, 1, 0xF9).Ok);
        Assert.False(Rle(24, 4, 2, 0xF9, 0xFD).Ok);
    }

    // A run length of zero in the header means the length is in the next byte, counted on from the largest
    // length the header can hold - except for the images, where the header counts in eights and the byte in ones.
    [Theory]
    [InlineData(new byte[] { 0x60, 0x00, 0x00, 0x00, 0xFF }, 32)]
    [InlineData(new byte[] { 0x60, 0xFF, 0x00, 0x00, 0xFF }, 287)]
    [InlineData(new byte[] { 0x7F, 0x00, 0x00, 0xFF }, 31)]
    [InlineData(new byte[] { 0xC0, 0x00, 0x00, 0x00, 0xFF }, 16)]
    [InlineData(new byte[] { 0xC0, 0xFF, 0x00, 0x00, 0xFF }, 271)]
    [InlineData(new byte[] { 0xCF, 0x00, 0x00, 0xFF }, 15)]
    [InlineData(new byte[] { 0xF3, 0x34, 0x12, 0x00, 0x00, 0xFF }, 0x1234)]
    [InlineData(new byte[] { 0xF6, 0x00, 0x01, 0x00, 0x00, 0xFF }, 256)]
    public void RunLengths(byte[] data, int pixels)
    {
        // One scan line of exactly that many pixels is filled, one pixel more or less is not.
        var (ok, line) = Rle(24, pixels, 1, data);
        Assert.True(ok);
        Assert.All(line, pixel => Assert.Equal(Red, pixel));
        Assert.False(Rle(24, pixels - 1, 1, data).Ok);
        Assert.False(Rle(24, pixels + 1, 1, data).Ok);
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0xFF }, 8)]
    [InlineData(new byte[] { 0x40, 0x00, 0xFF }, 1)]
    [InlineData(new byte[] { 0x40, 0x08, 0xFF, 0xFF }, 9)]
    [InlineData(new byte[] { 0xD1, 0xFF, 0xFF, 0xFF, 0xFF }, 8)]
    [InlineData(new byte[] { 0xD0, 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 11)]
    [InlineData(new byte[] { 0xF2, 0x0C, 0x00, 0xFF, 0xFF }, 12)]
    [InlineData(new byte[] { 0xF7, 0x03, 0x00, 0xFF, 0xFF, 0xFF, 0xFF }, 3)]
    public void RunLengthsOfImages(byte[] data, int pixels)
    {
        var (ok, line) = Rle(24, pixels, 1, data);
        Assert.True(ok);
        Assert.All(line, pixel => Assert.Equal(White, pixel));
        Assert.False(Rle(24, pixels - 1, 1, data).Ok);
        Assert.False(Rle(24, pixels + 1, 1, data).Ok);
    }

    // The longest image a regular header can describe: 31 times eight pixels.
    [Fact]
    public void TheLongestRegularImage()
    {
        var data = new byte[32];
        data.AsSpan().Fill(0xFF);
        data[0] = 0x5F;
        var (ok, line) = Rle(24, 248, 1, data);
        Assert.True(ok);
        Assert.All(line, pixel => Assert.Equal(White, pixel));
        Assert.False(Rle(24, 247, 1, data).Ok);
        Assert.False(Rle(24, 249, 1, data).Ok);
        Assert.False(Rle(24, 248, 1, data[..31]).Ok);
    }

    [Theory]
    [InlineData(0xA0)]
    [InlineData(0xA5)]
    [InlineData(0xBF)]
    [InlineData(0xF5)]
    [InlineData(0xFB)]
    [InlineData(0xFC)]
    [InlineData(0xFF)]
    public void BytesThatAreNoOrderAreRefused(int code)
    {
        var header = (byte)code;
        Assert.False(Rle(24, 4, 1, header, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00).Ok);
        Assert.False(Rle(8, 4, 1, new uint[256], header, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00).Ok);

        // Also behind a bitmap that is already complete.
        Assert.False(Rle(24, 4, 1, 0x04, header).Ok);
    }

    // Data that ends before the bitmap is complete is refused; the lines it did contain are in the framebuffer.
    [Fact]
    public void DataThatEndsEarlyIsRefusedButItsLinesAreDrawn()
    {
        var (ok, pixels) = Rle(24, 4, 2, 0x64, 0x00, 0x00, 0xFF);
        Assert.False(ok);
        Assert.Equal([Unwritten, Unwritten, Unwritten, Unwritten, Red, Red, Red, Red], pixels);

        // Half a line is no line.
        (ok, pixels) = Rle(24, 4, 2, 0x62, 0x00, 0x00, 0xFF);
        Assert.False(ok);
        Assert.All(pixels, pixel => Assert.Equal(Unwritten, pixel));

        Assert.False(Rle(24, 4, 2).Ok);
    }

    [Fact]
    public void ARunBeyondTheEndOfTheBitmapIsRefused()
    {
        // four pixels fit, five do not; the run that does not fit is not drawn at all
        var (ok, pixels) = Rle(24, 4, 1, 0x65, 0x00, 0x00, 0xFF);
        Assert.False(ok);
        Assert.All(pixels, pixel => Assert.Equal(Unwritten, pixel));

        // complete, and then one pixel more
        Assert.False(Rle(24, 4, 1, 0x64, 0x00, 0x00, 0xFF, 0xFD).Ok);
        Assert.False(Rle(24, 4, 1, 0x64, 0x00, 0x00, 0xFF, 0x01).Ok);
        Assert.False(Rle(24, 3, 1, 0xE2, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00).Ok);
    }

    [Fact]
    public void OrdersThatAreCutOffAreRefused()
    {
        Assert.False(Rle(24, 40, 1, 0x60).Ok);                         // the length byte of a MEGA run
        Assert.False(Rle(24, 40, 1, 0xF3, 0x28).Ok);                   // half a 16-bit length
        Assert.False(Rle(24, 4, 1, 0x64, 0x00, 0x00).Ok);              // a colour of two bytes
        Assert.False(Rle(24, 4, 1, 0xC4, 0x00).Ok);                    // the new foreground colour
        Assert.False(Rle(24, 4, 1, 0xE2, 0x00, 0x00, 0xFF, 0xFF).Ok);  // the second colour of a dithered run
        Assert.False(Rle(24, 16, 1, 0x42, 0xFF).Ok);                   // the second mask byte
        Assert.False(Rle(24, 4, 1, 0x84, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).Ok);  // the last byte of four pixels
        Assert.False(Rle(16, 4, 1, 0x84, 0, 0, 0, 0, 0, 0, 0).Ok);
    }

    // An order without pixels does nothing - unless it is a background run right after a background run: the
    // foreground pixel between them is counted in a length that has no room for it.
    [Fact]
    public void OrdersWithoutPixels()
    {
        var (ok, pixels) = Rle(24, 2, 1, 0xF3, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xF1, 0x00, 0x00, 0xF4, 0x00, 0x00, 0xF8, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 0xF2, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x62, 0x00, 0x00, 0xFF);
        Assert.True(ok);
        Assert.Equal([Red, Red], pixels);

        Assert.False(Rle(24, 8, 1, 0x04, 0xF0, 0x00, 0x00, 0x64, 0x00, 0x00, 0xFF).Ok);

        // Behind a complete bitmap they are harmless as well.
        Assert.True(Rle(24, 2, 1, 0x62, 0x00, 0x00, 0xFF, 0xF3, 0x00, 0x00, 0x00, 0xFF, 0x00).Ok);
    }

    // At 8 bits the pixels are palette indices: black is index 0 and white - also the first foreground colour -
    // index 255, whatever colours the palette has there.
    [Fact]
    public void EightBitsGoThroughThePalette()
    {
        var palette = new uint[256];
        palette[0] = 0xFF102030;
        palette[255] = 0xFF405060;
        palette[7] = 0xFF708090;
        palette[7 ^ 255] = 0xFFA0B0C0;

        // 4 x 2. First line: black, white, a foreground pixel, index 7. Second line: a foreground run of four.
        var (ok, pixels) = Rle(8, 4, 2, palette, 0xFE, 0xFD, 0x21, 0x61, 0x07, 0x24);
        Assert.True(ok);
        Assert.Equal(
            [
                0xFF405060, 0xFF102030, 0xFF102030, 0xFFA0B0C0,
                0xFF102030, 0xFF405060, 0xFF405060, 0xFF708090,
            ],
            pixels);

        // The X byte of a palette entry does not get into the framebuffer.
        palette[0] = 0x00102030;
        (ok, pixels) = Rle(8, 1, 1, palette, 0xFE);
        Assert.True(ok);
        Assert.Equal([0xFF102030], pixels);

        Assert.False(Rle(8, 1, 1, palette.AsSpan(0, 255).ToArray(), 0xFE).Ok);
        Assert.False(Rle(8, 1, 1, 0xFE).Ok);
    }

    // White at 15 bits is 0x7FFF; the sixteenth bit of a pixel is not part of its colour.
    [Fact]
    public void FifteenBits()
    {
        var (ok, pixels) = Rle(15, 4, 1, 0xFD, 0x21, 0x62, 0x00, 0xFC);
        Assert.True(ok);
        Assert.Equal([White, White, Red, Red], pixels);

        (ok, pixels) = Rle(15, 2, 2, 0x82, 0x1F, 0x80, 0xE0, 0x83, 0x22);
        Assert.True(ok);
        Assert.Equal([0xFFFFFF00, 0xFFFF00FF, Blue, Green], pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(32)]
    [InlineData(0)]
    [InlineData(-24)]
    public void DepthsWithoutInterleavedRleAreRefused(int bitsPerPixel)
    {
        var framebuffer = new byte[64];

        // 32 bits is a depth of its own codec: the same bytes are no planar stream either.
        Assert.False(BitmapDecoder.Decode([0xFD], bitsPerPixel, true, false, 1, 1, framebuffer, 4, 4, 0, 0, 0, 0, new uint[256]));
    }

    // ---- The compressed data header (MS-RDPBCGR 2.2.9.1.1.3.1.2.3) -----------------------------------------

    [Fact]
    public void TheHeaderSaysHowLongTheCompressedDataIs()
    {
        byte[] stream = [0x64, 0x00, 0x00, 0xFF];
        var framebuffer = new byte[16];

        // cbCompFirstRowSize, cbCompMainBodySize, cbScanWidth, cbUncompressedSize
        byte[] header = [0, 0, 4, 0, 12, 0, 12, 0];
        Assert.True(BitmapDecoder.Decode([.. header, .. stream], 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));
        Assert.Equal([Red, Red, Red, Red], Pixels(framebuffer));

        // More bytes than the header counts are not looked at ...
        Assert.True(BitmapDecoder.Decode([.. header, .. stream, 0xFF, 0xFF], 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));

        // ... and fewer are an error, as is a header that is not all there.
        header[2] = 5;
        Assert.False(BitmapDecoder.Decode([.. header, .. stream], 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));
        header[2] = 3;
        Assert.False(BitmapDecoder.Decode([.. header, .. stream], 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(header.AsSpan(0, 7), 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(default, 24, true, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));

        // Without the flag the same bytes are orders (and not valid ones).
        header[2] = 4;
        Assert.False(BitmapDecoder.Decode([.. header, .. stream], 24, true, false, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));

        // An uncompressed bitmap has no such header, whatever the flag says.
        Assert.True(BitmapDecoder.Decode([0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0], 24, false, true, 4, 1, framebuffer, 4, 1, 0, 0, 3, 0));
        Assert.Equal([Red, Black, Blue, Green], Pixels(framebuffer));
    }

    // ---- Uncompressed bitmaps (MS-RDPBCGR 2.2.9.1.1.3.1.2.2) -----------------------------------------------

    // "Each row contains a multiple of four bytes (including up to three bytes of padding, as necessary)."
    [Fact]
    public void RowsArePaddedToFourBytes()
    {
        // 3 x 2 at 24 bits, bottom-up: a row has 9 bytes and 3 of padding.
        byte[] padded =
        [
            0, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0, 0xEE, 0xEE, 0xEE,
            0xFF, 0xFF, 0xFF, 0, 0, 0, 0xFF, 0, 0xFF, 0xEE, 0xEE, 0xEE,
        ];
        var (ok, pixels) = Raw(24, 3, 2, padded);
        Assert.True(ok);
        Assert.Equal([White, Black, Magenta, Red, Green, Blue], pixels);

        // The padding of the last row may be missing.
        (ok, pixels) = Raw(24, 3, 2, padded[..21]);
        Assert.True(ok);
        Assert.Equal([White, Black, Magenta, Red, Green, Blue], pixels);

        // Rows that follow each other without padding: how FreeRDP and rdesktop read a bitmap whose width is
        // not a multiple of four. The length of the data tells the two apart.
        byte[] packed = [0, 0, 0xFF, 0, 0xFF, 0, 0xFF, 0, 0, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0xFF, 0, 0xFF];
        (ok, pixels) = Raw(24, 3, 2, packed);
        Assert.True(ok);
        Assert.Equal([White, Black, Magenta, Red, Green, Blue], pixels);

        Assert.False(Raw(24, 3, 2, packed[..17]).Ok);
        Assert.False(Raw(24, 3, 2).Ok);
    }

    [Fact]
    public void TheFourthByteOf32BitsIsNotCopied()
    {
        var (ok, pixels) = Raw(32, 2, 1, 0x11, 0x22, 0x33, 0x00, 0x44, 0x55, 0x66, 0x80);
        Assert.True(ok);
        Assert.Equal([0xFF332211, 0xFF665544], pixels);
    }

    // Every value a 16-bit pixel can have, as 5-6-5 and as 5-5-5: five and six bits fill eight by repeating
    // their high bits, so that the brightest value is 255 and the steps are even.
    [Theory]
    [InlineData(16)]
    [InlineData(15)]
    public void EveryPixelValueIsExpandedByBitReplication(int bitsPerPixel)
    {
        var data = new byte[256 * 256 * 2];
        for (var value = 0; value < 0x10000; value++)
        {
            // bottom-up: the value's high byte is the row from the bottom, its low byte the column
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(value * 2), (ushort)value);
        }

        var (ok, pixels) = Raw(bitsPerPixel, 256, 256, data);
        Assert.True(ok);
        for (var value = 0; value < 0x10000; value++)
        {
            var pixel = pixels[((255 - (value >> 8)) * 256) + (value & 0xFF)];
            var expected = bitsPerPixel == 16 ? Rgb565(value) : Rgb555(value);
            if (pixel != expected)
            {
                Assert.Fail($"0x{value:X4} at {bitsPerPixel} bits is {pixel:X8}, expected {expected:X8}");
            }
        }

        // The ends of the range: nothing is lost at either.
        Assert.Equal(Black, bitsPerPixel == 16 ? Rgb565(0) : Rgb555(0));
        Assert.Equal(White, bitsPerPixel == 16 ? Rgb565(0xFFFF) : Rgb555(0x7FFF));
    }

    [Fact]
    public void EightBitsWithoutAFullPaletteAreRefused()
    {
        Assert.False(Raw(8, 4, 1, 1, 2, 3, 4).Ok);
        Assert.False(BitmapDecoder.Decode([1, 2, 3, 4], 8, false, false, 4, 1, new byte[16], 4, 1, 0, 0, 3, 0, new uint[255]));
        Assert.True(BitmapDecoder.Decode([1, 2, 3, 4], 8, false, false, 4, 1, new byte[16], 4, 1, 0, 0, 3, 0, new uint[256]));
    }

    // ---- RDP 6.0 planar (MS-RDPEGDI 2.2.2.5.1 and 3.1.9) ---------------------------------------------------

    // The example of MS-RDPEGDI 3.1.9.2.3: nineteen bytes that are a plane of 6 x 3.
    private static readonly byte[] ExamplePlane =
    [
        0x13, 0xFF, 0x20, 0xFE, 0xFD, 0x60, 0x01, 0x7D,
        0xF5, 0xC2, 0x9A, 0x38, 0x60, 0x01, 0x67, 0x8B,
        0xA3, 0x78, 0xAF,
    ];

    private static readonly byte[] ExamplePlaneDecoded =
    [
        255, 255, 255, 255, 254, 253,
        254, 192, 132, 96, 75, 25,
        253, 140, 62, 14, 135, 193,
    ];

    [Fact]
    public void TheRunLengthExampleOfTheSpecification()
    {
        // The plane as red, a plane of zeros as green and blue: 0x06 is a run of six without a raw value, and
        // what a run without a raw value repeats is zero - on the first line the value 0, afterwards no change.
        byte[] zeros = [0x06, 0x06, 0x06];
        var (ok, pixels) = Planar(6, 3, [0x30, .. ExamplePlane, .. zeros, .. zeros]);
        Assert.True(ok);

        // Bottom-up: the first scan line of the plane is the last row of the picture.
        for (var row = 0; row < 3; row++)
        {
            for (var x = 0; x < 6; x++)
            {
                Assert.Equal(Black | ((uint)ExamplePlaneDecoded[((2 - row) * 6) + x] << 16), pixels[(row * 6) + x]);
            }
        }

        // The same plane as green and as blue, and behind an alpha plane that is skipped.
        (ok, pixels) = Planar(6, 3, [0x10, .. zeros, .. zeros, .. ExamplePlane, .. ExamplePlane]);
        Assert.True(ok);
        Assert.Equal(Black | (192u << 8) | 192u, pixels[(1 * 6) + 1]);
        Assert.Equal(Black | (14u << 8) | 14u, pixels[(0 * 6) + 3]);
    }

    // MS-RDPEGDI 3.1.9.2.2: a hundred times the letter A is "0x1F, 0x41, 0xF2, 0x52" - one raw value with a run
    // of 15, a run of 47 and a run of 37.
    [Fact]
    public void TheLongRunExampleOfTheSpecification()
    {
        byte[] letters = [0x1F, 0x41, 0xF2, 0x52];
        var (ok, pixels) = Planar(100, 1, [0x30, .. letters, .. letters, .. letters]);
        Assert.True(ok);
        Assert.All(pixels, pixel => Assert.Equal(0xFF414141, pixel));

        Assert.False(Planar(99, 1, [0x30, .. letters, .. letters, .. letters]).Ok);
        Assert.False(Planar(101, 1, [0x30, .. letters, .. letters, .. letters]).Ok);
    }

    // The first example of MS-RDPEGDI 3.1.9.2: the lines 10..60, then twice 5..55. The second line is one delta
    // of -5 and a run of it, the third a run that repeats "no change".
    [Fact]
    public void DeltasAreRelativeToTheLineBefore()
    {
        byte[] plane = [0x60, 10, 20, 30, 40, 50, 60, 0x15, 0x09, 0x06];
        var (ok, pixels) = Planar(6, 3, [0x30, .. plane, .. plane, .. plane]);
        Assert.True(ok);
        uint[] first = [0xFF0A0A0A, 0xFF141414, 0xFF1E1E1E, 0xFF282828, 0xFF323232, 0xFF3C3C3C];
        uint[] others = [0xFF050505, 0xFF0F0F0F, 0xFF191919, 0xFF232323, 0xFF2D2D2D, 0xFF373737];
        Assert.Equal([.. others, .. others, .. first], pixels);
    }

    [Fact]
    public void ASegmentMustFitItsScanLineAndMustNotBeEmpty()
    {
        // 4 x 1: four raw values; five; three and a run of three (six in all); a control byte of zero.
        Assert.True(Planar(4, 1, [0x30, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4]).Ok);
        Assert.False(Planar(4, 1, [0x30, 0x50, 1, 2, 3, 4, 5, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4]).Ok);
        Assert.False(Planar(4, 1, [0x30, 0x33, 1, 2, 3, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4]).Ok);
        Assert.False(Planar(4, 1, [0x30, 0x00, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4]).Ok);
        Assert.False(Planar(4, 1, [0x30, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4, 0x20, 1, 2, 0x00, 0x20, 3, 4]).Ok);

        // The last plane ends early.
        Assert.False(Planar(4, 1, [0x30, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3]).Ok);
        Assert.False(Planar(4, 1, [0x30, 0x40, 1, 2, 3, 4, 0x40, 1, 2, 3, 4]).Ok);
        Assert.False(Planar(4, 1, [0x30]).Ok);
        Assert.False(Planar(4, 1, []).Ok);
    }

    [Fact]
    public void RawPlanesMayBeFollowedByAPaddingByte()
    {
        // 2 x 2, no alpha: red, green, blue, each bottom-up.
        byte[] planes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        uint[] picture = [0xFF03070B, 0xFF04080C, 0xFF010509, 0xFF02060A];
        var (ok, pixels) = Planar(2, 2, [0x20, .. planes]);
        Assert.True(ok);
        Assert.Equal(picture, pixels);

        (ok, pixels) = Planar(2, 2, [0x20, .. planes, 0x00]);
        Assert.True(ok);
        Assert.Equal(picture, pixels);

        // With an alpha plane in front, which the framebuffer has no use for.
        (ok, pixels) = Planar(2, 2, [0x00, 0x80, 0x00, 0x40, 0xFF, .. planes, 0x00]);
        Assert.True(ok);
        Assert.Equal(picture, pixels);

        Assert.False(Planar(2, 2, [0x20, .. planes[..11]]).Ok);
        Assert.False(Planar(2, 2, [0x00, .. planes, 0x00]).Ok);
    }

    // The two reserved bits of the format header "MUST be ignored"; chroma subsampling without a colour loss
    // level cannot be (there is no chroma in red, green and blue).
    [Fact]
    public void TheFormatHeader()
    {
        byte[] planes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var (ok, pixels) = Planar(2, 2, [0xE0, .. planes]);
        Assert.True(ok);
        Assert.Equal([0xFF03070B, 0xFF04080C, 0xFF010509, 0xFF02060A], pixels);

        Assert.False(Planar(2, 2, [0x28, .. planes]).Ok);
        Assert.False(Planar(2, 2, [0x38, .. planes]).Ok);
    }

    // MS-RDPEGDI 3.1.9.1.2: R = Y + Co/2 - Cg/2, G = Y + Cg/2, B = Y - Co/2 - Cg/2, with Co and Cg nine bits
    // wide and shifted down by the colour loss level to fit a byte. The same section says that servers get Co
    // wrong for bitmaps without alpha, and that clients have to swap red and blue for those.
    [Theory]
    [InlineData(1, 100, 0x10, 0xF0, 132, 84, 100)]     // Co/2 = 16, Cg/2 = -16
    [InlineData(2, 100, 0x08, 0x78, 132, 84, 100)]     // the same after losing two bits
    [InlineData(3, 100, 0x04, 0x3C, 132, 84, 100)]
    [InlineData(7, 100, 0x01, 0x03, 228, 36, 100)]     // 0x01 << 6 = 64, 0x03 << 6 = -64
    [InlineData(1, 128, 0x00, 0x00, 128, 128, 128)]    // no chroma: grey
    [InlineData(1, 250, 0x7F, 0x30, 255, 255, 75)]     // 329 and 298 are clamped
    [InlineData(1, 10, 0x80, 0x7F, 0, 137, 11)]        // -245 is clamped
    public void TheColourTransform(int colorLossLevel, int lumaValue, int orangeValue, int greenValue, int red, int greenOut, int blue)
    {
        var luma = (byte)lumaValue;
        var orange = (byte)orangeValue;
        var green = (byte)greenValue;

        // With an alpha plane: the transform as the specification writes it.
        var (ok, pixels) = Planar(1, 1, [(byte)colorLossLevel, 0xFF, luma, orange, green, 0x00]);
        Assert.True(ok);
        Assert.Equal([Black | ((uint)red << 16) | ((uint)greenOut << 8) | (uint)blue], pixels);

        // Without: red and blue change places.
        (ok, pixels) = Planar(1, 1, [(byte)(0x20 | colorLossLevel), luma, orange, green, 0x00]);
        Assert.True(ok);
        Assert.Equal([Black | ((uint)blue << 16) | ((uint)greenOut << 8) | (uint)red], pixels);

        // The same values run-length encoded.
        (ok, pixels) = Planar(1, 1, [(byte)(0x30 | colorLossLevel), 0x10, luma, 0x10, orange, 0x10, green]);
        Assert.True(ok);
        Assert.Equal([Black | ((uint)blue << 16) | ((uint)greenOut << 8) | (uint)red], pixels);
    }

    // "Super-sampling of a compressed chroma plane simply results in the expansion of a subsampled pixel into
    // a 2-by-2 grid of pixels"; with an odd width or height the last one stands for a single column or row.
    [Fact]
    public void SubsampledChromaCoversTwoByTwoPixels()
    {
        // 3 x 3, colour loss level 1, with alpha: luma 100 everywhere; four chroma values of which each gives
        // another colour. Co/2: 0, 20, -20, 0. Cg/2: 0, 0, 0, 30.
        byte[] luma = [100, 100, 100, 100, 100, 100, 100, 100, 100];
        byte[] alpha = [255, 255, 255, 255, 255, 255, 255, 255, 255];
        byte[] orange = [0, 20, 0xEC, 0];
        byte[] green = [0, 0, 0, 30];
        var a = Black | (100u << 16) | (100u << 8) | 100u;
        var b = Black | (120u << 16) | (100u << 8) | 80u;
        var c = Black | (80u << 16) | (100u << 8) | 120u;
        var d = Black | (70u << 16) | (130u << 8) | 70u;
        var (ok, pixels) = Planar(3, 3, [0x09, .. alpha, .. luma, .. orange, .. green, 0x00]);
        Assert.True(ok);

        // The planes are bottom-up: the first chroma line covers the two bottom rows, the second the top row.
        Assert.Equal(
            [
                c, c, d,
                a, a, b,
                a, a, b,
            ],
            pixels);

        // Run-length encoded, the chroma planes have their own, smaller scan lines.
        // The deltas of the second chroma line: -20 is written as 39, +30 as 60.
        (ok, pixels) = Planar(3, 3,
        [
            0x19,
            0x30, 255, 255, 255, 0x03, 0x03,
            0x30, 100, 100, 100, 0x03, 0x03,
            0x20, 0, 20, 0x20, 39, 39,
            0x20, 0, 0, 0x20, 0, 60,
        ]);
        Assert.True(ok);
        Assert.Equal(
            [
                c, c, d,
                a, a, b,
                a, a, b,
            ],
            pixels);
    }

    // ---- What the decoder is called with ---------------------------------------------------------------------

    [Fact]
    public void ImpossibleArgumentsAreRefused()
    {
        var framebuffer = new byte[4 * 4 * 4];
        byte[] data = [0x64, 0x00, 0x00, 0xFF];
        Assert.True(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 4, 4, 0, 0, 3, 0));

        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 0, 1, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 0, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, -4, 1, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, -1, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 65536, 1, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 65536, framebuffer, 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, int.MaxValue, int.MaxValue, framebuffer, 4, 4, 0, 0, 3, 0));

        // A framebuffer that is smaller than it is said to be, or of negative size.
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer.AsSpan(0, 63), 4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 5, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, -4, 4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 4, -4, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, int.MaxValue, int.MaxValue, 0, 0, 3, 0));

        // A rectangle whose right or bottom edge lies before its left or top edge.
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 4, 4, 2, 0, 0, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 4, 4, 0, 2, 3, 0));
        Assert.False(BitmapDecoder.Decode(data, 24, true, false, 4, 1, framebuffer, 4, 4, int.MaxValue, 0, int.MinValue, 0));
    }

    // A rectangle outside the framebuffer, or a framebuffer without pixels, is nothing to refuse: the data is
    // checked all the same and nothing is drawn.
    [Fact]
    public void ARectangleOutsideTheFramebufferOnlyChecksTheData()
    {
        var framebuffer = new byte[4 * 4 * 4];
        byte[] good = [0x64, 0x00, 0x00, 0xFF];
        byte[] bad = [0x65, 0x00, 0x00, 0xFF];
        (int Left, int Top)[] places = [(4, 0), (0, 4), (-4, 0), (0, -1), (1000, 1000), (-1000, -1000), (int.MaxValue - 3, 0), (int.MinValue, int.MinValue)];
        foreach (var (left, top) in places)
        {
            Assert.True(BitmapDecoder.Decode(good, 24, true, false, 4, 1, framebuffer, 4, 4, left, top, left + 3, top));
            Assert.False(BitmapDecoder.Decode(bad, 24, true, false, 4, 1, framebuffer, 4, 4, left, top, left + 3, top));
        }

        Assert.True(BitmapDecoder.Decode(good, 24, true, false, 4, 1, default, 0, 0, 0, 0, 3, 0));
        Assert.True(BitmapDecoder.Decode(good, 24, true, false, 4, 1, default, 0, 100, 0, 0, 3, 0));
        Assert.False(BitmapDecoder.Decode(bad, 24, true, false, 4, 1, default, 0, 0, 0, 0, 3, 0));
        Assert.All(framebuffer, value => Assert.Equal(0, value));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static (bool Ok, uint[] Pixels) Rle(int bitsPerPixel, int width, int height, params byte[] data) => Rle(bitsPerPixel, width, height, null, data);

    private static (bool Ok, uint[] Pixels) Rle(int bitsPerPixel, int width, int height, uint[]? palette, params byte[] data) =>
        Decode(bitsPerPixel, true, width, height, data, palette);

    private static (bool Ok, uint[] Pixels) Raw(int bitsPerPixel, int width, int height, params byte[] data) => Decode(bitsPerPixel, false, width, height, data, null);

    private static (bool Ok, uint[] Pixels) Planar(int width, int height, byte[] data) => Decode(32, true, width, height, data, null);

    private static (bool Ok, uint[] Pixels) Decode(int bitsPerPixel, bool compressed, int width, int height, byte[] data, uint[]? palette)
    {
        if (width <= 0)
        {
            return (false, []);
        }

        var framebuffer = new byte[width * height * 4];
        var ok = BitmapDecoder.Decode(data, bitsPerPixel, compressed, false, width, height, framebuffer, width, height, 0, 0, width - 1, height - 1, palette);
        return (ok, Pixels(framebuffer));
    }

    private static uint[] Pixels(byte[] framebuffer)
    {
        var pixels = new uint[framebuffer.Length / 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = BinaryPrimitives.ReadUInt32LittleEndian(framebuffer.AsSpan(i * 4));
        }

        return pixels;
    }

    private static uint Rgb565(int value)
    {
        var red = (value >> 11) & 0x1F;
        var green = (value >> 5) & 0x3F;
        var blue = value & 0x1F;
        return Black | (uint)(((red << 3) | (red >> 2)) << 16) | (uint)(((green << 2) | (green >> 4)) << 8) | (uint)((blue << 3) | (blue >> 2));
    }

    private static uint Rgb555(int value)
    {
        var red = (value >> 10) & 0x1F;
        var green = (value >> 5) & 0x1F;
        var blue = value & 0x1F;
        return Black | (uint)(((red << 3) | (red >> 2)) << 16) | (uint)(((green << 3) | (green >> 2)) << 8) | (uint)((blue << 3) | (blue >> 2));
    }
}
