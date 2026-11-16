using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// Where the specifications' examples are thin, the bytes of a real implementation stand in: FreeRDP 3.15.0 and
// WinPR 3.15.0 as Debian trixie ships them (freerdp3-dev).
//
// For the channels, FreeRDP's own client plugins ("cliprdr", "disp") were hosted by a small C program that
// plays the channel underneath them: it printed every message the plugin wrote, and fed it messages built by
// hand to see what the plugin read from them. The messages below are those outputs, byte for byte. A test
// that produces the same bytes shows that this client says what FreeRDP's client says; a test that feeds them
// in as the server's shows that it reads what FreeRDP's client writes. For the clipboard helpers the functions
// of the libraries were called directly (cliprdr_serialize_file_list_ex, ClipboardSetData/ClipboardGetData).
public class FreeRdpCrossCheckTests
{
    private const uint AllFlags = 0x3E;

    // ---- clipboard: a server with every capability ---------------------------------------------------------------

    // ClientCapabilities with generalFlags 0x3e.
    private const string Capabilities = "07 00 00 00 10 00 00 00 01 00 00 00 01 00 0c 00 02 00 00 00 3e 00 00 00";

    // ClientFormatList: 13, 0xC0D0 "FileGroupDescriptorW", 0xC001 "Rich Text Format".
    private const string LongFormatList = """
        02 00 00 00 5a 00 00 00 0d 00 00 00 00 00 d0 c0
        00 00 46 00 69 00 6c 00 65 00 47 00 72 00 6f 00
        75 00 70 00 44 00 65 00 73 00 63 00 72 00 69 00
        70 00 74 00 6f 00 72 00 57 00 00 00 01 c0 00 00
        52 00 69 00 63 00 68 00 20 00 54 00 65 00 78 00
        74 00 20 00 46 00 6f 00 72 00 6d 00 61 00 74 00
        00 00
        """;

    private const string ListAccepted = "03 00 01 00 00 00 00 00";
    private const string ListRefused = "03 00 02 00 00 00 00 00";
    private const string Lock77 = "0a 00 00 00 04 00 00 00 4d 00 00 00";
    private const string Unlock77 = "0b 00 00 00 04 00 00 00 4d 00 00 00";

    // ClientFileContentsRequest: the size of file 0 on stream 1 with clipDataId 77, and without one.
    private const string SizeRequestLocked = """
        08 00 00 00 1c 00 00 00 01 00 00 00 00 00 00 00
        01 00 00 00 00 00 00 00 00 00 00 00 08 00 00 00
        4d 00 00 00
        """;

    private const string SizeRequest = """
        08 00 00 00 18 00 00 00 01 00 00 00 00 00 00 00
        01 00 00 00 00 00 00 00 00 00 00 00 08 00 00 00
        """;

    // 65536 bytes of file 0 at 0x100001000 on stream 3 with clipDataId 77; 4096 bytes of file 5 from its start
    // on stream 2 without one.
    private const string RangeRequestLocked = """
        08 00 00 00 1c 00 00 00 03 00 00 00 00 00 00 00
        02 00 00 00 00 10 00 00 01 00 00 00 00 00 01 00
        4d 00 00 00
        """;

    private const string RangeRequest = """
        08 00 00 00 18 00 00 00 02 00 00 00 05 00 00 00
        02 00 00 00 00 00 00 00 00 00 00 00 00 10 00 00
        """;

    private const string TextRequest = "04 00 00 00 04 00 00 00 0d 00 00 00";
    private const string FileListRequest = "04 00 00 00 04 00 00 00 bc c0 00 00";

    // ClientFormatDataResponse with "a\r\nb" and with a failure.
    private const string TextResponse = "05 00 01 00 0a 00 00 00 61 00 0d 00 0a 00 62 00 00 00";
    private const string DataRefused = "05 00 02 00 00 00 00 00";

    // ClientFileContentsResponse: six bytes on stream 13, a size (4 GB + 10) on stream 11, a failure on stream 3.
    private const string ContentsResponse = "09 00 01 00 0a 00 00 00 0d 00 00 00 01 02 03 04 05 06";
    private const string SizeResponse = "09 00 01 00 0c 00 00 00 0b 00 00 00 0a 00 00 00 01 00 00 00";
    private const string ContentsRefused = "09 00 02 00 04 00 00 00 03 00 00 00";

    // ---- clipboard: a server that sends no capabilities ------------------------------------------------------------

    // ClientFormatList in short names: 13, 0xC0D0 "FileGroupDescriptorW" (cut to 15 characters), 0xC001 "Kurz".
    private const string ShortFormatList = """
        02 00 00 00 6c 00 00 00 0d 00 00 00 00 00 00 00
        00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
        00 00 00 00 00 00 00 00 00 00 00 00 d0 c0 00 00
        46 00 69 00 6c 00 65 00 47 00 72 00 6f 00 75 00
        70 00 44 00 65 00 73 00 63 00 72 00 69 00 00 00
        01 c0 00 00 4b 00 75 00 72 00 7a 00 00 00 00 00
        00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
        00 00 00 00
        """;

    [Fact]
    public void Clipboard_messages_are_the_ones_FreeRDP_sends()
    {
        var client = new ClipClient();
        client.Channel.Receive(ClipServer.Capabilities(AllFlags));
        client.Channel.Receive(ClipServer.MonitorReady());

        // Capabilities (and the first, empty format list).
        Hex.Same(Capabilities, client.Wire.Take()[0]);

        client.Channel.AnnounceFormats([new(13, null), new(0xC0D0, "FileGroupDescriptorW"), new(0xC001, "Rich Text Format")]);
        Hex.Same(LongFormatList, client.Wire.One());

        // The answers to a format list of the server.
        client.Channel.Receive(ClipServer.LongFormatList((13, null), (0xC0BC, "FileGroupDescriptorW"), (0xC0BD, "FileContents")));
        Hex.Same(ListAccepted, client.Wire.One());
        client.Channel.Receive(ClipServer.Message(ClipServer.FormatListType, 0, [1, 2, 3]));
        Hex.Same(ListRefused, client.Wire.One());

        Assert.True(client.Channel.Lock(77));
        Hex.Same(Lock77, client.Wire.One());
        client.Channel.RequestFileSize(1, 0, 77);
        Hex.Same(SizeRequestLocked, client.Wire.One());
        client.Channel.Receive(ClipServer.FileResponse(1, ClipServer.U64(1)));
        client.Channel.RequestFileSize(1, 0);
        Hex.Same(SizeRequest, client.Wire.One());
        client.Channel.RequestFileRange(3, 0, 0x100001000, 65536, 77);
        Hex.Same(RangeRequestLocked, client.Wire.One());
        client.Channel.RequestFileRange(2, 5, 0, 4096);
        Hex.Same(RangeRequest, client.Wire.One());
        Assert.True(client.Channel.Unlock(77));
        Hex.Same(Unlock77, client.Wire.One());

        client.Channel.RequestData(13);
        Hex.Same(TextRequest, client.Wire.One());
        client.Channel.Receive(ClipServer.DataResponse([]));
        client.Channel.RequestData(0xC0BC);
        Hex.Same(FileListRequest, client.Wire.One());

        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataRequest(13));
        client.Channel.SendData(ClipboardText.Encode("a\nb"));
        Hex.Same(TextResponse, client.Wire.One());
        client.Channel.Receive(ClipServer.DataRequest(13));
        client.Channel.SendData(null);
        Hex.Same(DataRefused, client.Wire.One());

        client.Channel.Receive(ClipServer.FileRangeRequest(13, 0, 0, 6));
        client.Channel.SendFileContents(13, [1, 2, 3, 4, 5, 6]);
        Hex.Same(ContentsResponse, client.Wire.One());
        client.Channel.Receive(ClipServer.FileSizeRequest(11, 0));
        client.Channel.SendFileSize(11, 0x10000000A);
        Hex.Same(SizeResponse, client.Wire.One());
        client.Channel.Receive(ClipServer.FileRangeRequest(3, 0, 0, 6));
        client.Channel.SendFileContents(3, null);
        Hex.Same(ContentsRefused, client.Wire.One());
        Assert.Single(client.Wire.Log);
    }

    // The same messages the other way round: FreeRDP's plugin reads them as noted, and so does this client.
    [Fact]
    public void Clipboard_messages_FreeRDP_sends_are_read_the_way_FreeRDP_reads_them()
    {
        var client = ClipClient.Started();

        // "server formats flags=0000 count=3: 13=[] 49360=[FileGroupDescriptorW] 49153=[Rich Text Format]"
        client.Channel.Receive(Hex.Bytes(LongFormatList));
        Assert.Equal([new(13, null), new(0xC0D0, "FileGroupDescriptorW"), new(0xC001, "Rich Text Format")], client.Remote[0]);

        // "server lock 77", "server unlock 77"
        client.Channel.Receive(Hex.Bytes(Lock77));
        client.Channel.Receive(Hex.Bytes(Unlock77));

        // "server file request stream=1 index=0 flags=1 low=0 high=0 count=8 clip=77" and so on.
        client.Channel.AnnounceFormats([new(13, null), new(0xC0D0, "FileGroupDescriptorW")]);
        client.Channel.Receive(Hex.Bytes(ListAccepted));
        client.Channel.Receive(Hex.Bytes(SizeRequestLocked));
        client.Channel.Receive(Hex.Bytes(SizeRequest));
        client.Channel.Receive(Hex.Bytes(RangeRequestLocked));
        client.Channel.Receive(Hex.Bytes(RangeRequest));
        Assert.Equal(
            [
                new FileContentsRequest(1, 0, true, 0, 8, 77),
                new FileContentsRequest(1, 0, true, 0, 8, null),
                new FileContentsRequest(3, 0, false, 0x100001000, 65536, 77),
                new FileContentsRequest(2, 5, false, 0, 4096, null),
            ],
            client.FileRequests);

        // "server data request format=13"
        client.Channel.Receive(Hex.Bytes(TextRequest));

        // "server data response flags=0001 length=10 data 61 00 0d 00 0a 00 62 00 00 00" and "flags=0002 length=0"
        client.Channel.Receive(ClipServer.LongFormatList((13, null)));
        client.Channel.RequestData(13);
        client.Channel.Receive(Hex.Bytes(TextResponse));
        client.Channel.RequestData(13);
        client.Channel.Receive(Hex.Bytes(DataRefused));
        Assert.Equal("a\nb", ClipboardText.Decode(client.Data[0]));
        Assert.Null(client.Data[1]);

        // "server file response stream=13 ... length=6", "stream=11 flags=0001 length=8 data 0a 00 00 00 01 00 00 00"
        // and "stream=3 flags=0002 length=0".
        client.Channel.RequestFileRange(13, 0, 0, 6);
        client.Channel.Receive(Hex.Bytes(ContentsResponse));
        client.Channel.RequestFileSize(11, 0);
        client.Channel.Receive(Hex.Bytes(SizeResponse));
        client.Channel.RequestFileRange(3, 0, 0, 6);
        client.Channel.Receive(Hex.Bytes(ContentsRefused));
        Hex.Same("01 02 03 04 05 06", client.Files[0].Data);
        Assert.Equal(0x10000000AL, ClipboardChannel.FileSize(client.Files[1].Data));
        Assert.Null(client.Files[2].Data);

        // "server list response flags=0002"
        client.Channel.Receive(Hex.Bytes(ListRefused));

        Assert.Equal(
            [
                "formats 13,49360=FileGroupDescriptorW,49153=Rich Text Format", "locked 77", "unlocked 77", "acknowledged True",
                "file 1 size of 0", "file 1 size of 0", "file 3 range of 0 at 4294971392 for 65536", "file 2 range of 5 at 0 for 4096",
                "requested 13", "formats 13", "data 10", "data refused", "contents 13 6", "contents 11 8", "contents 3 refused",
            ],
            client.TakeEvents());
        Assert.Single(client.Wire.Log);
    }

    [Fact]
    public void Short_format_names_are_the_ones_FreeRDP_sends_and_reads()
    {
        var client = new ClipClient();
        client.Channel.Receive(ClipServer.MonitorReady());
        client.Wire.Take();

        client.Channel.AnnounceFormats([new(13, null), new(0xC0D0, "FileGroupDescriptorW"), new(0xC001, "Kurz")]);
        Hex.Same(ShortFormatList, client.Wire.One());

        // FreeRDP reads its own list as "13=[] 49360=[FileGroupDescri] 49153=[Kurz]".
        client.Channel.Receive(Hex.Bytes(ShortFormatList));
        Assert.Equal([new(13, null), new(0xC0D0, "FileGroupDescri"), new(0xC001, "Kurz")], client.Remote[0]);

        // A list in UTF-16 with a name that fills its field ("13=[] 49163=[Rich Text Format] 49164=[Ein Name]").
        client.Channel.Receive(ClipServer.ShortFormatList(false, (13, null), (0xC00B, "Rich Text Format"), (0xC00C, "Ein Name")));
        Assert.Equal([new(13, null), new(0xC00B, "Rich Text Format"), new(0xC00C, "Ein Name")], client.Remote[1]);

        // A list in ASCII ("1=[] 49165=[A name of thirty-two characters] 49166=[HTML Format]"). Where FreeRDP
        // keeps 31 characters of a name that fills all 32 bytes, this client keeps the 32nd as well.
        client.Channel.Receive(ClipServer.ShortFormatList(true, (1, null), (0xC00D, "A name of thirty-two characters!"), (0xC00E, "HTML Format")));
        Assert.Equal([new(1, null), new(0xC00D, "A name of thirty-two characters!"), new(0xC00E, "HTML Format")], client.Remote[2]);
    }

    // FreeRDP announces "cliprdr" with the options 0xc0a00000; this client leaves out the compression
    // (0x00800000), which its core does not do.
    [Fact]
    public void The_clipboard_options_are_FreeRDPs_without_compression()
    {
        Assert.Equal(0xC0A00000 & ~StaticChannelOptions.CompressRdp, new ClipboardChannel().Options);
    }

    // ---- display control -----------------------------------------------------------------------------------------

    // What FreeRDP's "disp" plugin writes for one primary monitor at 0,0 with no physical size, no rotation and
    // scale factors of 100: SendMonitorLayout with the width and height before the arrow. It rounds an odd
    // width down and keeps both sides between 200 and 8192, as this client does.
    [Theory]
    [InlineData(1920, 1080, "80 07 00 00 38 04 00 00")]
    [InlineData(1024, 768, "00 04 00 00 00 03 00 00")]
    [InlineData(8192, 8192, "00 20 00 00 00 20 00 00")]
    [InlineData(200, 200, "c8 00 00 00 c8 00 00 00")]
    [InlineData(1921, 1080, "80 07 00 00 38 04 00 00")]
    [InlineData(100, 100, "c8 00 00 00 c8 00 00 00")]
    [InlineData(10000, 10000, "00 20 00 00 00 20 00 00")]
    public void The_monitor_layout_is_the_one_FreeRDP_sends(int width, int height, string size)
    {
        var display = new DisplayControlChannel();
        byte[]? sent = null;
        display.Send = message => sent = message.ToArray();

        // FreeRDP reads this as "caps 16 8192 8192".
        display.Receive(Hex.Bytes("05 00 00 00 14 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00"));
        Assert.Equal((16, 8192, 8192), (display.MaxMonitors, display.MaxAreaFactorA, display.MaxAreaFactorB));

        Assert.True(display.SetSize(width, height));

        Hex.Same(
            "02 00 00 00 38 00 00 00 28 00 00 00 01 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00 "
            + size
            + " 00 00 00 00 00 00 00 00 00 00 00 00 64 00 00 00 64 00 00 00",
            sent);
    }

    [Fact]
    public void Capabilities_are_read_the_way_FreeRDP_reads_them()
    {
        var display = new DisplayControlChannel();

        // "caps 1 4096 2048"
        display.Receive(Hex.Bytes("05 00 00 00 14 00 00 00 01 00 00 00 00 10 00 00 00 08 00 00"));
        Assert.Equal((1, 4096, 2048), (display.MaxMonitors, display.MaxAreaFactorA, display.MaxAreaFactorB));

        // With two bytes more than the length counts: "caps 16 8192 8192".
        display.Receive(Hex.Bytes("05 00 00 00 14 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00 aa bb"));
        Assert.Equal((16, 8192, 8192), (display.MaxMonitors, display.MaxAreaFactorA, display.MaxAreaFactorB));

        // FreeRDP 3.15 reads capabilities of 14 bytes as "caps 16 8192 0" - the third number from beyond the
        // message - and a header alone as "caps 0 0 0". This client does not take either for capabilities.
        var strict = new DisplayControlChannel();
        strict.Receive(Hex.Bytes("05 00 00 00 14 00 00 00 10 00 00 00 00 20"));
        strict.Receive(Hex.Bytes("05 00 00 00 14 00 00 00"));
        Assert.False(strict.IsReady);
    }

    // ---- the file list -------------------------------------------------------------------------------------------

    // cliprdr_serialize_file_list_ex for three FILEDESCRIPTORW: "Bericht.pdf" (1234 bytes, written 2026-11-16
    // 09:30:00 UTC, FILE_ATTRIBUTE_NORMAL), the folder "Fotos" and "Fotos\Hafen.jpg" (4 GB + 10 bytes), with
    // the flags FreeRDP gives the files it offers.
    [Fact]
    public void A_file_list_is_the_one_FreeRDP_serialises()
    {
        var expected = Hex.Resource("freerdp-filelist.txt");

        var data = FileGroupDescriptor.Encode(
        [
            new ClipFile("Bericht.pdf", 1234, false, new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc)),
            new ClipFile("Fotos", 0, true),
            new ClipFile("Fotos/Hafen.jpg", 0x10000000A),
        ]);

        Hex.Same(expected, data);
    }

    // cliprdr_parse_file_list on the same bytes:
    //   file 0 flags 00004064 attributes 00000080 size 00000000000004d2 time 01dd75ad0d111c00 name Bericht.pdf
    //   file 1 flags 00004044 attributes 00000010 size 0000000000000000 time 0000000000000000 name Fotos
    //   file 2 flags 00004044 attributes 00000080 size 000000010000000a time 0000000000000000 name Fotos\Hafen.jpg
    [Fact]
    public void A_file_list_is_read_the_way_FreeRDP_parses_it()
    {
        var files = FileGroupDescriptor.Decode(Hex.Resource("freerdp-filelist.txt"));

        Assert.NotNull(files);
        Assert.Equal(
            [
                new ClipFile("Bericht.pdf", 0x4D2, false, DateTime.FromFileTimeUtc(0x01DD75AD0D111C00)) { Attributes = 0x80 },
                new ClipFile("Fotos", 0, true) { Attributes = 0x10 },
                new ClipFile("Fotos\\Hafen.jpg", 0x10000000A) { Attributes = 0x80 },
            ],
            files);
    }

    // ---- text ----------------------------------------------------------------------------------------------------

    // WinPR's clipboard turning CF_UNICODETEXT into "text/plain" (UTF-8): the bytes set, the bytes got.
    [Theory]
    [InlineData("61 00 0d 00 0a 00 62 00 00 00", "61 0a 62")]
    [InlineData("61 00 0d 00 0a 00 62 00", "61 0a 62")]
    [InlineData("61 00 00 00 62 00 00 00", "61")]
    [InlineData("47 00 72 00 fc 00 df 00 65 00 20 00 ac 20 20 00 3d d8 00 de 00 00", "47 72 c3 bc c3 9f 65 20 e2 82 ac 20 f0 9f 98 80")]
    [InlineData("00 00", "")]
    [InlineData("61 00 0d 00 0a 00 0d 00 0a 00 00 00", "61 0a 0a")]
    public void Text_from_the_clipboard_is_what_WinPR_makes_of_it(string clipboard, string utf8)
    {
        Hex.Same(utf8, Encoding.UTF8.GetBytes(ClipboardText.Decode(Hex.Bytes(clipboard))));
    }

    // And "text/plain" into CF_UNICODETEXT.
    [Theory]
    [InlineData("61 0a 62", "61 00 0d 00 0a 00 62 00 00 00")]
    [InlineData("61 0a 62 00", "61 00 0d 00 0a 00 62 00 00 00")]
    [InlineData("61 0d 0a 62 0a", "61 00 0d 00 0a 00 62 00 0d 00 0a 00 00 00")]
    [InlineData("47 72 c3 bc c3 9f 65 20 e2 82 ac 20 f0 9f 98 80", "47 00 72 00 fc 00 df 00 65 00 20 00 ac 20 20 00 3d d8 00 de 00 00")]
    public void Text_for_the_clipboard_is_what_WinPR_makes_of_it(string utf8, string clipboard)
    {
        Hex.Same(clipboard, ClipboardText.Encode(Encoding.UTF8.GetString(Hex.Bytes(utf8))));
    }

    // Where the two differ, on purpose: WinPR takes a carriage return on its own for the end of a line, in both
    // directions; this client changes CR LF and LF and nothing else.
    [Fact]
    public void A_lone_carriage_return_stays_one()
    {
        // WinPR: "61 0a 62 0a 63".
        Hex.Same("61 0d 62 0a 63", Encoding.UTF8.GetBytes(ClipboardText.Decode(Hex.Bytes("61 00 0d 00 62 00 0a 00 63 00 00 00"))));

        // WinPR: "61 00 0d 00 0a 00 62 00 00 00".
        Hex.Same("61 00 0d 00 62 00 00 00", ClipboardText.Encode("a\rb"));
    }

    // ---- pictures ------------------------------------------------------------------------------------------------

    // WinPR's clipboard turning CF_DIB into "image/bmp": the file header it puts in front of a bitmap with a
    // 40-byte header, true colour and no compression - and nothing else changes.
    [Theory]
    [InlineData(24, 48, "42 4d 66 00 00 00 00 00 00 00 36 00 00 00")]
    [InlineData(32, 64, "42 4d 76 00 00 00 00 00 00 00 36 00 00 00")]
    [InlineData(24, 0, "42 4d 36 00 00 00 00 00 00 00 36 00 00 00")]
    public void A_true_colour_bitmap_gets_the_file_header_WinPR_gives_it(int bits, int rest, string header)
    {
        var dib = ClipboardHelperTests.Dib(40, (ushort)bits, 0, 0, rest);

        var bmp = DeviceIndependentBitmap.ToBmp(dib);

        Assert.NotNull(bmp);
        Hex.Same(header, bmp.AsSpan(0, 14));
        Hex.Same(dib, bmp.AsSpan(14));

        // And back, WinPR hands out the file without its header.
        Hex.Same(dib, DeviceIndependentBitmap.FromBmp(bmp));
    }

    // Where the two differ, on purpose: WinPR 3.15 writes 54 as the place of the pixels whatever lies between
    // the header and them, and refuses bitmaps with colour masks. A palette or masks move the pixels back, and
    // the file header has to say so for a decoder to find them.
    [Theory]
    [InlineData(40u, 8, 0u, 0u, 1040, 54 + 1024)]      // WinPR: 54
    [InlineData(40u, 8, 0u, 16u, 80, 54 + 64)]         // WinPR: 54
    [InlineData(124u, 8, 0u, 0u, 1040, 138 + 1024)]    // WinPR: 54
    [InlineData(40u, 32, 3u, 0u, 76, 54 + 12)]         // WinPR: no file
    [InlineData(124u, 32, 3u, 0u, 64, 138)]            // WinPR: no file
    public void A_palette_or_masks_move_the_pixels(uint header, int bits, uint compression, uint used, int rest, int pixels)
    {
        var bmp = DeviceIndependentBitmap.ToBmp(ClipboardHelperTests.Dib(header, (ushort)bits, compression, used, rest));

        Assert.NotNull(bmp);
        Assert.Equal((uint)pixels, BitConverter.ToUInt32(bmp, 10));
    }
}
