using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// What an application needs around the clipboard channel: text, file lists and pictures in Windows' formats.
public class ClipboardHelperTests
{
    // ---- CF_UNICODETEXT -------------------------------------------------------------------------------------------

    // MS-RDPECLIP 4.4.2: the data of the Format Data Response PDU is "hello world" with its terminator.
    [Fact]
    public void Text_is_utf16_with_a_terminator()
    {
        var data = ClipboardText.Encode("hello world");

        Hex.Same("68 00 65 00 6c 00 6c 00 6f 00 20 00 77 00 6f 00 72 00 6c 00 64 00 00 00", data);
        Assert.Equal("hello world", ClipboardText.Decode(data));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("\n", "\r\n")]
    [InlineData("\n\n", "\r\n\r\n")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\rb", "a\rb")]
    [InlineData("a\r\n\nb\n", "a\r\n\r\nb\r\n")]
    [InlineData("cut\0here", "cut")]
    [InlineData("Größe: 5 €, 日本語, 😀", "Größe: 5 €, 日本語, 😀")]
    public void Text_goes_out_the_way_Windows_keeps_it(string text, string windows)
    {
        var data = ClipboardText.Encode(text);

        Assert.Equal(windows + "\0", Encoding.Unicode.GetString(data));
    }

    [Theory]
    [InlineData("a\r\nb\0", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb\0", "a\rb")]
    [InlineData("a\nb\0", "a\nb")]
    [InlineData("first\0second\0", "first")]
    [InlineData("\0", "")]
    [InlineData("", "")]
    [InlineData("Größe: 5 €, 日本語, 😀\r\n\0", "Größe: 5 €, 日本語, 😀\n")]
    public void Text_comes_in_as_plain_lines(string windows, string text)
    {
        Assert.Equal(text, ClipboardText.Decode(Encoding.Unicode.GetBytes(windows)));
    }

    [Fact]
    public void Text_that_is_not_quite_text_still_decodes()
    {
        // Half a character at the end, half a surrogate pair in the middle, nothing at all.
        Assert.Equal("a", ClipboardText.Decode([0x61, 0x00, 0x62]));
        Assert.Equal("a\uFFFDb", ClipboardText.Decode([0x61, 0x00, 0x00, 0xD8, 0x62, 0x00]));
        Assert.Equal("", ClipboardText.Decode([]));
        Assert.Equal("", ClipboardText.Decode([0x41]));

        // And the other way round: what goes out is text too.
        Hex.Same("61 00 fd ff 62 00 00 00", ClipboardText.Encode("a\uD800b"));
        Assert.Throws<ArgumentNullException>(() => ClipboardText.Encode(null!));
    }

    [Fact]
    public void Text_survives_the_round_trip()
    {
        const string Alphabet = "ab \n\tä€ß😀";
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            // Pairs of characters, so the two halves of the emoji stay together.
            var text = string.Concat(Enumerable.Range(0, random.Next(0, 200)).Select(_ => Alphabet.Substring(random.Next(Alphabet.Length / 2) * 2, 2)));

            Assert.Equal(text, ClipboardText.Decode(ClipboardText.Encode(text)));
        }
    }

    // ---- FileGroupDescriptorW -------------------------------------------------------------------------------------

    [Fact]
    public void A_file_list_survives_the_round_trip()
    {
        var written = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1234567);
        ClipFile[] files =
        [
            new("Bericht.pdf", 123456, false, written),
            new("Ordner", 0, true, written),
            new("Ordner\\Unterordner", 0, true),
            new("Ordner\\Unterordner\\Größe.txt", 5L * 1024 * 1024 * 1024 + 1),
            new("leer", 0),
            new("versteckt.sys", 9, false, written) { Attributes = FileGroupDescriptor.AttributeHidden | FileGroupDescriptor.AttributeSystem | FileGroupDescriptor.AttributeReadOnly },
            new(new string('x', 255) + ".txt", long.MaxValue),
        ];

        var data = FileGroupDescriptor.Encode(files);

        Assert.Equal(4 + files.Length * 592, data.Length);
        Assert.Equal(files, FileGroupDescriptor.Decode(data));
    }

    [Fact]
    public void A_file_list_says_what_it_knows()
    {
        var local = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Local);
        var data = FileGroupDescriptor.Encode(
        [
            new ClipFile("unbekannt.bin", -1),
            new ClipFile("lokal.txt", 1, false, local),
            new ClipFile("ohne/Art\\der.txt", 2, false, new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Unspecified)),
            new ClipFile("/vorn.txt", 3, false, new DateTime(1500, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new ClipFile("Ordner", 77, true) { Attributes = FileGroupDescriptor.AttributeArchive },
        ]);

        // A size that is not known is not claimed; a time before 1601 cannot be said.
        Assert.Equal(0x4004u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(0x4064u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 592)));
        Assert.Equal(0x4044u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 3 * 592)));

        var files = FileGroupDescriptor.Decode(data)!;
        Assert.Equal(-1, files[0].Size);
        Assert.Equal(local.ToUniversalTime(), files[1].LastWriteTimeUtc);
        Assert.Equal(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc), files[2].LastWriteTimeUtc);
        Assert.Equal("ohne\\Art\\der.txt", files[2].Name);
        Assert.Equal("vorn.txt", files[3].Name);
        Assert.Null(files[3].LastWriteTimeUtc);

        // A folder has no size, and it is a folder whatever else its attributes say.
        Assert.Equal(0, files[4].Size);
        Assert.True(files[4].IsDirectory);
        Assert.Equal(FileGroupDescriptor.AttributeArchive | FileGroupDescriptor.AttributeDirectory, files[4].Attributes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    [InlineData("a\0b")]
    public void A_name_that_cannot_be_sent_is_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => FileGroupDescriptor.Encode([new ClipFile(name, 1)]));
    }

    [Fact]
    public void A_name_fills_259_characters_and_not_one_more()
    {
        var longest = new string('n', FileGroupDescriptor.MaxNameLength);

        Assert.Equal(longest, FileGroupDescriptor.Decode(FileGroupDescriptor.Encode([new ClipFile(longest, 1)]))![0].Name);
        Assert.Throws<ArgumentException>(() => FileGroupDescriptor.Encode([new ClipFile(longest + "n", 1)]));
        Assert.Throws<ArgumentNullException>(() => FileGroupDescriptor.Encode(null!));
    }

    [Fact]
    public void A_file_list_from_the_server_is_read_with_suspicion()
    {
        var one = FileGroupDescriptor.Encode([new ClipFile("a.txt", 5)]);

        // Too short for its count, a count beyond the data, a count that would be gigabytes.
        Assert.Null(FileGroupDescriptor.Decode([]));
        Assert.Null(FileGroupDescriptor.Decode([1, 0, 0]));
        Assert.Null(FileGroupDescriptor.Decode(one.AsSpan(0, one.Length - 1)));
        Assert.Null(FileGroupDescriptor.Decode([2, 0, 0, 0, .. one.AsSpan(4)]));
        Assert.Null(FileGroupDescriptor.Decode([0xFF, 0xFF, 0xFF, 0xFF, .. one.AsSpan(4)]));

        // No files, and bytes after the last file.
        Assert.Empty(FileGroupDescriptor.Decode([0, 0, 0, 0])!);
        Assert.Equal("a.txt", Assert.Single(FileGroupDescriptor.Decode([.. one, 1, 2, 3])!).Name);

        // An entry that claims nothing: no size, no time, no attributes - whatever its bytes are.
        var blank = (byte[])[1, 0, 0, 0, .. Enumerable.Repeat((byte)0xFF, 592)];
        blank.AsSpan(4, 4).Clear();
        var file = Assert.Single(FileGroupDescriptor.Decode(blank)!);
        Assert.Equal(-1, file.Size);
        Assert.Null(file.LastWriteTimeUtc);
        Assert.False(file.IsDirectory);
        Assert.Equal(0u, file.Attributes);

        // A name without its terminator ends where the field ends.
        Assert.Equal(new string('\uFFFF', 260), file.Name);

        // A size beyond what a file can have and a time beyond the calendar are not passed on.
        BinaryPrimitives.WriteUInt32LittleEndian(blank.AsSpan(4), 0x64);
        file = Assert.Single(FileGroupDescriptor.Decode(blank)!);
        Assert.Equal(-1, file.Size);
        Assert.Null(file.LastWriteTimeUtc);
        Assert.True(file.IsDirectory);
    }

    [Fact]
    public void The_file_list_format_is_found_by_its_name()
    {
        Assert.Equal(0xC123u, FileGroupDescriptor.FindFormat([new(13, null), new(0xC123, "FileGroupDescriptorW"), new(0xC124, "FileContents")]));
        Assert.Equal(0xC001u, FileGroupDescriptor.FindFormat([new(0xC001, "filegroupdescriptorw")]));
        Assert.Null(FileGroupDescriptor.FindFormat([new(13, null), new(0xC123, "FileGroupDescriptor")]));
        Assert.Null(FileGroupDescriptor.FindFormat([]));
        Assert.Equal(0xC0D0u, FileGroupDescriptor.DefaultFormatId);
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("Ordner\\a.txt", "Ordner|a.txt")]
    [InlineData("Ordner/Unter ordner\\ä.txt", "Ordner|Unter ordner|ä.txt")]
    [InlineData(".verborgen", ".verborgen")]
    [InlineData("console.log", "console.log")]
    public void A_plain_relative_path_has_parts(string name, string parts)
    {
        Assert.Equal(parts.Split('|'), FileGroupDescriptor.PathParts(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    [InlineData("a\\..\\b")]
    [InlineData("a\\.\\b")]
    [InlineData("\\a.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\\\b")]
    [InlineData("a\\")]
    [InlineData("C:\\Windows\\a.txt")]
    [InlineData("C:a.txt")]
    [InlineData("\\\\server\\share\\a.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("a?.txt")]
    [InlineData("a*.txt")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    [InlineData("a\u007Fb")]
    [InlineData("ende.")]
    [InlineData("ende ")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("Ordner\\COM1")]
    [InlineData("Ordner\\lpt9.log")]
    [InlineData("con .txt")]
    public void Anything_else_has_none(string name)
    {
        Assert.Null(FileGroupDescriptor.PathParts(name));
    }

    [Fact]
    public void A_path_longer_than_a_name_can_be_has_no_parts()
    {
        Assert.NotNull(FileGroupDescriptor.PathParts(new string('a', 259)));
        Assert.Null(FileGroupDescriptor.PathParts(new string('a', 260)));
    }

    // ---- CF_DIB ---------------------------------------------------------------------------------------------------

    // A BITMAPINFOHEADER (or a longer header of that family) for 4 x 4 pixels, and "rest" bytes of whatever
    // follows it. (The cross-check program builds the same bitmaps for WinPR.)
    internal static byte[] Dib(uint header, ushort bits, uint compression, uint used, int rest)
    {
        var dib = new byte[header + rest];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, header);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), used);
        for (var i = (int)header; i < dib.Length; i++)
        {
            dib[i] = (byte)(i * 7);
        }

        return dib;
    }

    [Theory]
    [InlineData(40u, 24, 0u, 0u, 48, 54)]               // true colour: the pixels follow the header
    [InlineData(40u, 32, 0u, 0u, 64, 54)]
    [InlineData(40u, 8, 0u, 0u, 1024 + 16, 54 + 1024)]  // 256 colours
    [InlineData(40u, 8, 0u, 16u, 64 + 16, 54 + 64)]     // of which only 16 are used
    [InlineData(40u, 4, 0u, 0u, 64 + 8, 54 + 64)]       // 16 colours
    [InlineData(40u, 1, 0u, 0u, 8 + 16, 54 + 8)]        // black and white
    [InlineData(40u, 32, 3u, 0u, 12 + 64, 54 + 12)]     // BI_BITFIELDS: three masks after a 40-byte header
    [InlineData(40u, 16, 3u, 0u, 12 + 32, 54 + 12)]
    [InlineData(40u, 32, 6u, 0u, 16 + 64, 54 + 16)]     // BI_ALPHABITFIELDS: four
    [InlineData(40u, 24, 0u, 3u, 12 + 48, 54 + 12)]     // true colour with a palette for poor displays
    [InlineData(108u, 32, 3u, 0u, 64, 14 + 108)]        // BITMAPV4HEADER: the masks are inside
    [InlineData(124u, 32, 3u, 0u, 64, 14 + 124)]        // BITMAPV5HEADER
    [InlineData(124u, 8, 0u, 0u, 1024 + 16, 14 + 124 + 1024)]
    public void A_bitmap_becomes_a_file_whose_header_points_at_the_pixels(uint header, int bits, uint compression, uint used, int rest, int pixels)
    {
        var dib = Dib(header, (ushort)bits, compression, used, rest);

        var bmp = DeviceIndependentBitmap.ToBmp(dib);

        Assert.NotNull(bmp);
        Assert.Equal(14 + dib.Length, bmp.Length);
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(6)));
        Assert.Equal((uint)pixels, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)));
        Hex.Same(dib, bmp.AsSpan(14));

        // And back.
        Hex.Same(dib, DeviceIndependentBitmap.FromBmp(bmp));
    }

    [Fact]
    public void Data_that_is_no_bitmap_becomes_no_file()
    {
        Assert.Null(DeviceIndependentBitmap.ToBmp([]));
        Assert.Null(DeviceIndependentBitmap.ToBmp(new byte[39]));
        Assert.Null(DeviceIndependentBitmap.ToBmp(new byte[100]));                           // a header of 0 bytes
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 24, 0, 0, 48).AsSpan(0, 39)));
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(12, 24, 0, 0, 100)));                  // BITMAPCOREHEADER
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(124, 24, 0, 0, 0).AsSpan(0, 100)));    // a header longer than the data
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 3, 0, 0, 48)));
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 0, 0, 0, 48)));
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 8, 0, 0, 1023)));                  // a palette that is not all there
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 8, 0, 257, 4096)));                // more colours than 8 bits have
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 24, 0, uint.MaxValue, 48)));
        Assert.Null(DeviceIndependentBitmap.ToBmp(Dib(40, 32, 3, 0, 11)));                   // masks that are not all there

        // A bitmap without a single pixel is still a bitmap.
        Assert.NotNull(DeviceIndependentBitmap.ToBmp(Dib(40, 24, 0, 0, 0)));
    }

    [Fact]
    public void A_file_becomes_a_bitmap_without_its_file_header()
    {
        var dib = Dib(40, 8, 0, 4, 16 + 32);
        var bmp = DeviceIndependentBitmap.ToBmp(dib)!;

        // A file that keeps its pixels further back than they have to be: without the file header nothing
        // would say where they are, so the gap is closed.
        var loose = (byte[])[.. bmp.AsSpan(0, 14 + 40 + 16), .. new byte[100], .. bmp.AsSpan(14 + 40 + 16)];
        BinaryPrimitives.WriteUInt32LittleEndian(loose.AsSpan(2), (uint)loose.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(loose.AsSpan(10), 14 + 40 + 16 + 100);
        Hex.Same(dib, DeviceIndependentBitmap.FromBmp(loose));

        // A file that does not say where its pixels are is taken at its word otherwise.
        var silent = (byte[])bmp.Clone();
        silent.AsSpan(10, 4).Clear();
        Hex.Same(dib, DeviceIndependentBitmap.FromBmp(silent));
    }

    [Fact]
    public void A_file_that_is_no_bitmap_becomes_no_data()
    {
        var bmp = DeviceIndependentBitmap.ToBmp(Dib(40, 8, 0, 4, 16 + 32))!;

        Assert.Null(DeviceIndependentBitmap.FromBmp([]));
        Assert.Null(DeviceIndependentBitmap.FromBmp(bmp.AsSpan(0, 13)));
        Assert.Null(DeviceIndependentBitmap.FromBmp(bmp.AsSpan(0, 53)));
        Assert.Null(DeviceIndependentBitmap.FromBmp([(byte)'P', (byte)'N', .. bmp.AsSpan(2)]));

        // Pixels that would start inside the palette, and pixels that would start behind the file.
        var inside = (byte[])bmp.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(inside.AsSpan(10), 14 + 40 + 15);
        Assert.Null(DeviceIndependentBitmap.FromBmp(inside));
        var beyond = (byte[])bmp.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(beyond.AsSpan(10), (uint)bmp.Length + 1);
        Assert.Null(DeviceIndependentBitmap.FromBmp(beyond));
        BinaryPrimitives.WriteUInt32LittleEndian(beyond.AsSpan(10), uint.MaxValue);
        Assert.Null(DeviceIndependentBitmap.FromBmp(beyond));
    }
}
