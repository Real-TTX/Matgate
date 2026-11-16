using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// Whole clipboard conversations through the public API, against a server that says what the test scripts.
public class ClipboardConversationTests
{
    private const uint Text = 13;
    private const uint Dib = 8;
    private const uint AllFlags = 0x3E;
    private const long FourGigabytes = 4L * 1024 * 1024 * 1024;

    // A FILEDESCRIPTORW written out by hand: 592 bytes, the fields where MS-RDPECLIP 2.2.5.2.3.1 puts them.
    private static byte[] Descriptor(string name, ulong size, uint attributes, uint flags = 0x4044)
    {
        var entry = new byte[592];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, flags);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(36), attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(64), (uint)(size >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(68), (uint)size);
        Encoding.Unicode.GetBytes(name).CopyTo(entry, 72);
        return entry;
    }

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

    [Fact]
    public void Text_copied_on_the_server_and_pasted_here()
    {
        var client = ClipClient.Started();

        client.Channel.Receive(ClipServer.LongFormatList((Text, null), (16, null), (1, null), (7, null)));
        var (type, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal((ClipServer.FormatListResponseType, ClipServer.Ok, 0), (type, flags, body.Length));
        Assert.Equal(["formats 13,16,1,7"], client.TakeEvents());

        client.Channel.RequestData(Text);
        (type, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal((ClipServer.DataRequestType, (ushort)0), (type, flags));
        Assert.Equal(Text, U32(body, 0));

        client.Channel.Receive(ClipServer.DataResponse(Encoding.Unicode.GetBytes("Grüße aus Köln\r\nzweite Zeile €\0")));
        Assert.Equal("Grüße aus Köln\nzweite Zeile €", ClipboardText.Decode(Assert.Single(client.Data)));
        client.Wire.Nothing();
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void Text_copied_here_and_pasted_on_the_server()
    {
        var client = ClipClient.Started();

        client.Channel.AnnounceFormats([new(Text, null)]);
        var (type, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal((ClipServer.FormatListType, (ushort)0), (type, flags));
        Hex.Same("0d 00 00 00 00 00", body);

        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        Assert.Equal(["acknowledged True", "requested 13"], client.TakeEvents());

        client.Channel.SendData(ClipboardText.Encode("eine Zeile\nnoch eine"));
        (type, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal((ClipServer.DataResponseType, ClipServer.Ok), (type, flags));
        Assert.Equal("eine Zeile\r\nnoch eine\0", Encoding.Unicode.GetString(body));
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void A_picture_in_both_directions()
    {
        // 2 x 2 pixels, 24 bits, bottom-up, rows padded to four bytes.
        var dib = Hex.Bytes("""
            28 00 00 00 02 00 00 00 02 00 00 00 01 00 18 00
            00 00 00 00 10 00 00 00 13 0b 00 00 13 0b 00 00
            00 00 00 00 00 00 00 00
            ff 00 00 00 ff 00 00 00
            00 00 ff ff ff ff 00 00
            """);
        var client = ClipClient.Started();

        // From the server: the data becomes a .bmp file whose header points behind the 40 bytes of the DIB's.
        client.Channel.Receive(ClipServer.LongFormatList((Dib, null), (17, null), (2, null)));
        client.Channel.RequestData(Dib);
        client.Channel.Receive(ClipServer.DataResponse(dib));
        var bmp = DeviceIndependentBitmap.ToBmp(Assert.Single(client.Data));
        Assert.NotNull(bmp);
        Hex.Same("42 4d 46 00 00 00 00 00 00 00 36 00 00 00", bmp.AsSpan(0, 14));
        Hex.Same(dib, bmp.AsSpan(14));
        client.Wire.Take();

        // To the server: the .bmp file a browser made loses its file header again.
        client.Channel.AnnounceFormats([new(Dib, null)]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataRequest(Dib));
        client.Wire.Take();
        client.Channel.SendData(DeviceIndependentBitmap.FromBmp(bmp));
        var (_, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal(ClipServer.Ok, flags);
        Hex.Same(dib, body);
    }

    [Fact]
    public void Files_copied_on_the_server_are_read_here()
    {
        var client = ClipClient.Started();
        var size = FourGigabytes + 5000;
        var list = (byte[])[.. ClipServer.U32(3),
            .. Descriptor("big.iso", (ulong)size, 0x20),
            .. Descriptor("Folder", 0, 0x10),
            .. Descriptor("Folder\\note.txt", 12, 0x80)];

        // The server offers files: the list is the data of the format named FileGroupDescriptorW, whatever id
        // the server gave it.
        client.Channel.Receive(ClipServer.LongFormatList((0xC0BC, "FileGroupDescriptorW"), (0xC0BD, "FileContents"), (0xC0AA, "Preferred DropEffect")));
        var format = FileGroupDescriptor.FindFormat(Assert.Single(client.Remote));
        Assert.Equal(0xC0BCu, format);
        client.Channel.RequestData(format.GetValueOrDefault());
        Assert.Equal(0xC0BCu, U32(ClipServer.Parse(client.Wire.Take()[1]).Body, 0));
        client.Channel.Receive(ClipServer.DataResponse(list));

        var files = FileGroupDescriptor.Decode(Assert.Single(client.Data));
        Assert.NotNull(files);
        Assert.Equal(["big.iso", "Folder", "Folder\\note.txt"], files.Select(file => file.Name));
        Assert.Equal([size, 0, 12], files.Select(file => file.Size));
        Assert.Equal([false, true, false], files.Select(file => file.IsDirectory));
        Assert.Equal(["Folder", "note.txt"], FileGroupDescriptor.PathParts(files[2].Name));

        // The list is locked so the files stay readable when the server's clipboard moves on.
        Assert.True(client.Channel.Lock(77));
        Hex.Same("0a 00 00 00 04 00 00 00 4d 00 00 00", client.Wire.One());

        // The size of the first file.
        client.Channel.RequestFileSize(1, 0, 77);
        Hex.Same("""
            08 00 00 00 1c 00 00 00 01 00 00 00 00 00 00 00
            01 00 00 00 00 00 00 00 00 00 00 00 08 00 00 00
            4d 00 00 00
            """, client.Wire.One());
        client.Channel.Receive(ClipServer.FileResponse(1, ClipServer.U64((ulong)size)));
        Assert.Equal(size, ClipboardChannel.FileSize(client.Files[0].Data));

        // Its start.
        client.Channel.RequestFileRange(2, 0, 0, 65536, 77);
        var body = ClipServer.Parse(client.Wire.One()).Body;
        Assert.Equal([2u, 0u, 2u, 0u, 0u, 65536u, 77u], Enumerable.Range(0, 7).Select(i => U32(body, i * 4)));
        client.Channel.Receive(ClipServer.FileResponse(2, new byte[65536]));
        Assert.Equal(65536, client.Files[1].Data!.Length);

        // Beyond four gigabytes the position needs both of its halves.
        var offset = FourGigabytes + 4096;
        client.Channel.RequestFileRange(3, 0, offset, 65536, 77);
        body = ClipServer.Parse(client.Wire.One()).Body;
        Assert.Equal([3u, 0u, 2u, 4096u, 1u, 65536u, 77u], Enumerable.Range(0, 7).Select(i => U32(body, i * 4)));

        // The file ends 904 bytes after that position: the answer is as short as that.
        var tail = Enumerable.Range(0, (int)(size - offset)).Select(i => (byte)i).ToArray();
        client.Channel.Receive(ClipServer.FileResponse(3, tail));
        Hex.Same(tail, client.Files[2].Data);
        Assert.Equal(904, tail.Length);

        // A file inside the copied folder.
        client.Channel.RequestFileRange(4, 2, 0, 12, 77);
        client.Wire.Take();
        client.Channel.Receive(ClipServer.FileResponse(4, "Hello, note!"u8.ToArray()));
        Assert.Equal("Hello, note!", Encoding.ASCII.GetString(client.Files[3].Data!));

        Assert.True(client.Channel.Unlock(77));
        Hex.Same("0b 00 00 00 04 00 00 00 4d 00 00 00", client.Wire.One());
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void Files_copied_here_are_read_by_the_server()
    {
        var client = ClipClient.Started();
        var size = FourGigabytes + 10;
        var written = new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc);
        ClipFile[] files =
        [
            new("Bericht.pdf", 1234, false, written),
            new("Fotos", 0, true),
            new("Fotos/Hafen.jpg", size),
        ];

        client.Channel.AnnounceFormats([new(FileGroupDescriptor.DefaultFormatId, FileGroupDescriptor.FormatName)]);
        Hex.Same(ClipServer.LongFormatList((0xC0D0, "FileGroupDescriptorW")), client.Wire.One());
        client.Channel.Receive(ClipServer.FormatListResponse(true));

        // The server pastes: first the list ...
        client.Channel.Receive(ClipServer.DataRequest(0xC0D0));
        Assert.Equal(["acknowledged True", "requested 49360"], client.TakeEvents());
        client.Channel.SendData(FileGroupDescriptor.Encode(files));
        var list = ClipServer.Parse(client.Wire.One()).Body;
        Assert.Equal(4 + 3 * 592, list.Length);
        Assert.Equal(3u, U32(list, 0));

        // ... which says, entry by entry: attributes, size and progress - and the time where there is one.
        Assert.Equal([0x4064u, 0x4044u, 0x4044u], Enumerable.Range(0, 3).Select(i => U32(list, 4 + i * 592)));
        Assert.Equal([0x80u, 0x10u, 0x80u], Enumerable.Range(0, 3).Select(i => U32(list, 4 + i * 592 + 36)));
        Assert.Equal(written.ToFileTimeUtc(), BinaryPrimitives.ReadInt64LittleEndian(list.AsSpan(4 + 56)));
        Assert.Equal((0u, 1234u), (U32(list, 4 + 64), U32(list, 4 + 68)));
        Assert.Equal((1u, 10u), (U32(list, 4 + 2 * 592 + 64), U32(list, 4 + 2 * 592 + 68)));
        Assert.Equal("Fotos\\Hafen.jpg", Encoding.Unicode.GetString(list.AsSpan(4 + 2 * 592 + 72, 30)));
        Assert.All(list.AsSpan(4 + 2 * 592 + 72 + 30, 520 - 30).ToArray(), value => Assert.Equal(0, value));

        // ... then the lock, a size and two ranges, one of them behind the fourth gigabyte.
        client.Channel.Receive(ClipServer.Lock(5));
        client.Channel.Receive(ClipServer.FileSizeRequest(11, 2, 5));
        client.Channel.Receive(ClipServer.FileRangeRequest(12, 0, 0, 1234, 5));
        client.Channel.Receive(ClipServer.FileRangeRequest(13, 2, (ulong)FourGigabytes + 4, 65536, 5));
        Assert.Equal(
            [
                "locked 5",
                "file 11 size of 2",
                "file 12 range of 0 at 0 for 1234",
                "file 13 range of 2 at 4294967300 for 65536",
            ],
            client.TakeEvents());
        Assert.Equal(new FileContentsRequest(13, 2, false, FourGigabytes + 4, 65536, 5), client.FileRequests[2]);
        client.Wire.Nothing();

        // The answers may come in any order: the stream id says which request each belongs to.
        client.Channel.SendFileContents(13, [1, 2, 3, 4, 5, 6]);
        Hex.Same("09 00 01 00 0a 00 00 00 0d 00 00 00 01 02 03 04 05 06", client.Wire.One());
        client.Channel.SendFileSize(11, size);
        Hex.Same("09 00 01 00 0c 00 00 00 0b 00 00 00 0a 00 00 00 01 00 00 00", client.Wire.One());
        client.Channel.SendFileContents(12, new byte[1234]);
        Assert.Equal(8 + 4 + 1234, client.Wire.One().Length);

        client.Channel.Receive(ClipServer.Unlock(5));
        Assert.Equal(["unlocked 5"], client.TakeEvents());
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void Refusals_in_both_directions()
    {
        var client = ClipClient.Started();

        // The server refuses to deliver.
        client.Channel.Receive(ClipServer.LongFormatList((Text, null), (0xC0BC, "FileGroupDescriptorW")));
        client.Channel.RequestData(Text);
        client.Channel.Receive(ClipServer.DataResponse(null));
        client.Channel.RequestFileRange(9, 0, 0, 100);
        client.Channel.Receive(ClipServer.FileResponse(9, null));
        Assert.Equal(["formats 13,49340=FileGroupDescriptorW", "data refused", "contents 9 refused"], client.TakeEvents());
        client.Wire.Take();

        // This side refuses to deliver: a refusal carries no data.
        client.Channel.AnnounceFormats([new(Text, null), new(0xC0D0, "FileGroupDescriptorW")]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Wire.Take();
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Channel.SendData(null);
        Hex.Same("05 00 02 00 00 00 00 00", client.Wire.One());
        client.Channel.Receive(ClipServer.FileRangeRequest(3, 0, 0, 100));
        client.Channel.SendFileContents(3, null);
        Hex.Same("09 00 02 00 04 00 00 00 03 00 00 00", client.Wire.One());
        client.Channel.Receive(ClipServer.FileSizeRequest(4, 0));
        client.Channel.SendFileContents(4, null);
        Hex.Same("09 00 02 00 04 00 00 00 04 00 00 00", client.Wire.One());
    }

    // MS-RDPECLIP 3.1.5.2.4: after a format list the server refused, its requests are answered with a failure.
    [Fact]
    public void A_refused_format_list_refuses_what_the_server_asks_afterwards()
    {
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(Text, null)]);
        client.Wire.Take();

        client.Channel.Receive(ClipServer.FormatListResponse(false));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Channel.Receive(ClipServer.FileSizeRequest(1, 0));
        var sent = client.Wire.Take();
        Hex.Same("05 00 02 00 00 00 00 00", sent[0]);
        Hex.Same("09 00 02 00 04 00 00 00 01 00 00 00", sent[1]);
        Assert.Equal(["acknowledged False"], client.TakeEvents());

        // A list the server takes puts things right again.
        client.Channel.AnnounceFormats([new(Text, null)]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        Assert.Equal(["acknowledged True", "requested 13"], client.TakeEvents());
    }

    [Fact]
    public void Only_one_data_request_is_on_the_wire_and_the_answers_come_in_order()
    {
        var client = ClipClient.Started();
        client.Channel.Receive(ClipServer.LongFormatList((Text, null), (Dib, null), (1, null)));
        client.Wire.Take();

        client.Channel.RequestData(Text);
        client.Channel.RequestData(Dib);
        client.Channel.RequestData(1);
        Assert.Equal(Text, U32(ClipServer.Parse(client.Wire.One()).Body, 0));

        client.Channel.Receive(ClipServer.DataResponse([1]));
        Assert.Equal(Dib, U32(ClipServer.Parse(client.Wire.One()).Body, 0));

        client.Channel.Receive(ClipServer.DataResponse([1, 2]));
        Assert.Equal(1u, U32(ClipServer.Parse(client.Wire.One()).Body, 0));

        client.Channel.Receive(ClipServer.DataResponse([1, 2, 3]));
        client.Wire.Nothing();
        Assert.Equal(["formats 13,8,1", "data 1", "data 2", "data 3"], client.TakeEvents());

        // An answer nobody waits for is not taken for one.
        client.Channel.Receive(ClipServer.DataResponse([9, 9]));
        Assert.Empty(client.TakeEvents());
        Assert.Single(client.Wire.Log);
    }

    [Fact]
    public void Requests_for_what_the_server_no_longer_offers_are_answered_without_asking()
    {
        var client = ClipClient.Started();
        client.Channel.Receive(ClipServer.LongFormatList((Text, null), (Dib, null)));
        client.Wire.Take();
        client.Events.Clear();

        client.Channel.RequestData(Text);
        client.Channel.RequestData(Dib);
        client.Channel.RequestData(4711);
        client.Wire.Take();

        // The server copies something else while the first request is under way; the application asks for
        // the new text at once.
        client.Channel.Receive(ClipServer.LongFormatList((Text, null)));
        client.Channel.RequestData(Text);
        client.Wire.Take();

        // The first request still gets its answer. The two that waited meant the old announcement (and a
        // format that was never offered): they are refused, in their turn, and the last one goes out.
        client.Channel.Receive(ClipServer.DataResponse([7]));
        Assert.Equal(Text, U32(ClipServer.Parse(client.Wire.One()).Body, 0));
        client.Channel.Receive(ClipServer.DataResponse([8, 8]));
        Assert.Equal(["formats 13", "data 1", "data refused", "data refused", "data 2"], client.TakeEvents());

        // With nothing under way a request for a format that is not on offer is refused on the spot.
        client.Channel.RequestData(Dib);
        client.Wire.Nothing();
        Assert.Equal(["data refused"], client.TakeEvents());
    }

    [Fact]
    public void More_waiting_requests_than_allowed_are_refused()
    {
        var client = ClipClient.Started();
        client.Channel.Receive(ClipServer.LongFormatList((Text, null)));
        client.Wire.Take();
        client.Events.Clear();

        for (var i = 0; i < ClipboardChannel.MaxWaitingRequests + 6; i++)
        {
            client.Channel.RequestData(Text);
        }

        // One on the wire, 256 waiting, five refused.
        Assert.Single(client.Wire.Take());
        Assert.Equal(Enumerable.Repeat("data refused", 5), client.TakeEvents());
        Assert.Equal(5, client.Wire.Log.Count);
    }

    // Without a Clipboard Capabilities PDU from the server the defaults apply (MS-RDPECLIP 2.2.2.1.1.1): short
    // format names, no files, no locks - and the client does not send capabilities either (3.2.5.1.2).
    [Fact]
    public void A_server_without_capabilities_gets_short_format_names()
    {
        var client = new ClipClient();
        client.Channel.Receive(ClipServer.MonitorReady());

        // The first format list, empty and on its own.
        Hex.Same("02 00 00 00 00 00 00 00", client.Wire.One());
        Assert.False(client.Channel.CanTransferFiles);
        Assert.False(client.Channel.CanLock);
        Assert.False(client.Channel.Lock(1));

        // Names in UTF-16 fill at most 15 characters and a terminator.
        client.Channel.AnnounceFormats([new(Text, null), new(0xC0D0, "FileGroupDescriptorW"), new(0xC001, "Kurz")]);
        var (_, flags, body) = ClipServer.Parse(client.Wire.One());
        Assert.Equal(0, flags);
        Assert.Equal(3 * 36, body.Length);
        Assert.All(body.AsSpan(4, 32).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0xC0D0u, U32(body, 36));
        Assert.Equal("FileGroupDescri\0", Encoding.Unicode.GetString(body.AsSpan(40, 32)));
        Assert.Equal("Kurz\0", Encoding.Unicode.GetString(body.AsSpan(76, 10)));

        // What the server lists in UTF-16 ...
        client.Channel.Receive(ClipServer.ShortFormatList(false, (Text, null), (0xC00B, "Rich Text Format"), (0xC00C, "Ein Name")));
        Assert.Equal([new(Text, null), new(0xC00B, "Rich Text Format"), new(0xC00C, "Ein Name")], client.Remote[0]);

        // ... or in ASCII, when the header says so.
        client.Channel.Receive(ClipServer.ShortFormatList(true, (1, null), (0xC00D, "A name of thirty-two characters!"), (0xC00E, "HTML Format")));
        Assert.Equal([new(1, null), new(0xC00D, "A name of thirty-two characters!"), new(0xC00E, "HTML Format")], client.Remote[1]);

        // A list that is not a row of 36-byte entries is refused.
        client.Wire.Take();
        client.Channel.Receive(ClipServer.Message(ClipServer.FormatListType, 0, new byte[37]));
        Hex.Same("03 00 02 00 00 00 00 00", client.Wire.One());

        // Files need the stream capability on both sides.
        client.Channel.RequestFileSize(1, 0);
        client.Wire.Nothing();
        Assert.Equal("contents 1 refused", client.Events[^1]);
    }

    [Fact]
    public void A_server_with_few_capabilities_is_offered_no_more_than_it_has()
    {
        var client = new ClipClient();
        client.Channel.Receive(ClipServer.Capabilities(ClipboardChannel.LongFormatNames | 0x8000));
        client.Channel.Receive(ClipServer.MonitorReady());

        var capabilities = ClipServer.Parse(client.Wire.Take()[0]).Body;
        Assert.Equal(ClipboardChannel.LongFormatNames, U32(capabilities, 12));
        Assert.False(client.Channel.CanTransferFiles);

        // Capabilities without a general set: the defaults again.
        client = new ClipClient();
        client.Channel.Receive(ClipServer.Message(ClipServer.CapabilitiesType, 0, ClipServer.U16(1), ClipServer.U16(0), ClipServer.U16(9), ClipServer.U16(8), new byte[4]));
        client.Channel.Receive(ClipServer.MonitorReady());
        Assert.Equal(0u, U32(ClipServer.Parse(client.Wire.Take()[0]).Body, 12));
    }

    [Fact]
    public void A_server_that_becomes_ready_again_starts_over()
    {
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(Text, null)]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.LongFormatList((Text, null), (0xC0BC, "FileGroupDescriptorW")));
        client.Channel.RequestData(Text);
        client.Channel.RequestData(0xC0BC);
        client.Channel.RequestFileRange(5, 0, 0, 10);
        Assert.True(client.Channel.Lock(3));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Wire.Take();
        client.Events.Clear();

        // The clipboard process of the server was restarted: capabilities and monitor ready come again.
        client.Channel.Receive(ClipServer.Capabilities(AllFlags));
        client.Channel.Receive(ClipServer.MonitorReady());

        // What was asked of the old one is over, and the application hears of it before it hears "ready".
        Assert.Equal(["data refused", "data refused", "contents 5 refused", "ready"], client.TakeEvents());

        // The remote side had copied last, so this side's old formats are not announced again.
        var sent = client.Wire.Take();
        Assert.Equal(2, sent.Length);
        Assert.Equal(ClipServer.CapabilitiesType, ClipServer.Parse(sent[0]).Type);
        Hex.Same("02 00 00 00 00 00 00 00", sent[1]);

        // An answer to the old server's question would be an answer to nothing.
        client.Channel.SendData([1, 2]);
        client.Wire.Nothing();
        Assert.False(client.Channel.Unlock(3));

        // What this side copied last, on the other hand, is announced to a server that starts over.
        client.Channel.AnnounceFormats([new(Dib, null)]);
        client.Wire.Take();
        client.Channel.Receive(ClipServer.MonitorReady());
        Hex.Same("02 00 00 00 06 00 00 00 08 00 00 00 00 00", client.Wire.Take()[1]);
    }

    [Fact]
    public void The_session_ending_answers_what_was_under_way()
    {
        var client = ClipClient.Started();
        client.Channel.Receive(ClipServer.LongFormatList((Text, null)));
        client.Channel.RequestData(Text);
        client.Channel.RequestData(Text);
        client.Channel.RequestFileSize(8, 0);
        client.Wire.Take();
        client.Events.Clear();

        client.Channel.Closed();

        Assert.Equal(["data refused", "data refused", "contents 8 refused"], client.TakeEvents());
        Assert.False(client.Channel.IsReady);
        client.Wire.Nothing();

        // Nothing goes out any more, and every request is answered at once.
        client.Channel.RequestData(Text);
        client.Channel.RequestFileRange(9, 0, 0, 1);
        client.Channel.SendData([1]);
        client.Channel.AnnounceFormats([new(Text, null)]);
        client.Wire.Nothing();
        Assert.Equal(["data refused", "contents 9 refused"], client.TakeEvents());

        // The same object serves the next session; what was announced meanwhile goes out with it.
        client.Channel.Receive(ClipServer.Capabilities(AllFlags));
        client.Channel.Receive(ClipServer.MonitorReady());
        var sent = client.Wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same("02 00 00 00 06 00 00 00 0d 00 00 00 00 00", sent[1]);
    }

    [Fact]
    public void Format_lists_that_do_not_hold_together_are_answered_with_a_failure()
    {
        var client = ClipClient.Started();
        byte[][] broken =
        [
            [13, 0, 0],                                   // not even an id
            [13, 0, 0, 0],                                // an id without a name
            [13, 0, 0, 0, 65, 0],                         // a name without its terminator
            [13, 0, 0, 0, 65, 0, 0],                      // half a terminator
            [13, 0, 0, 0, 0, 0, 14],                      // a stray byte after the last entry
            new byte[ClipboardChannel.MaxFormatListLength + 2],
        ];

        foreach (var body in broken)
        {
            client.Channel.Receive(ClipServer.Message(ClipServer.FormatListType, 0, body));
            Hex.Same("03 00 02 00 00 00 00 00", client.Wire.One());
        }

        Assert.Empty(client.TakeEvents());
        Assert.Equal(broken.Length, client.Wire.Log.Count);

        // The longest list that is still taken: a megabyte of formats without names.
        client.Channel.Receive(ClipServer.Message(ClipServer.FormatListType, 0, new byte[ClipboardChannel.MaxFormatListLength - 4]));
        Hex.Same("03 00 01 00 00 00 00 00", client.Wire.One());
        Assert.Equal((ClipboardChannel.MaxFormatListLength - 4) / 6, Assert.Single(client.Remote).Count);
    }

    [Fact]
    public void Requests_of_the_server_that_make_no_sense_are_refused_before_the_application_sees_them()
    {
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(Text, null), new(0xC0D0, "FileGroupDescriptorW")]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Wire.Take();
        client.Events.Clear();

        // A format that was never announced, and a request too short to name one.
        client.Channel.Receive(ClipServer.DataRequest(Dib));
        client.Channel.Receive(ClipServer.Message(ClipServer.DataRequestType, 0, [13, 0]));
        Assert.All(client.Wire.Take(), message => Hex.Same("05 00 02 00 00 00 00 00", message));

        // File requests: a negative index, neither size nor range, both at once, a position no file has,
        // more than is served at a time, and a request that ends after its stream id.
        const string Refused = "09 00 02 00 04 00 00 00 07 00 00 00";
        client.Channel.Receive(ClipServer.FileSizeRequest(7, -1));
        Hex.Same(Refused, client.Wire.One());
        client.Channel.Receive(ClipServer.Message(ClipServer.FileRequestType, 0, ClipServer.U32(7), new byte[4], ClipServer.U32(0), new byte[12]));
        Hex.Same(Refused, client.Wire.One());
        client.Channel.Receive(ClipServer.Message(ClipServer.FileRequestType, 0, ClipServer.U32(7), new byte[4], ClipServer.U32(3), new byte[12]));
        Hex.Same(Refused, client.Wire.One());
        client.Channel.Receive(ClipServer.FileRangeRequest(7, 0, 0x8000000000000000, 10));
        Hex.Same(Refused, client.Wire.One());
        client.Channel.Receive(ClipServer.FileRangeRequest(7, 0, 0, ClipboardChannel.MaxRangeServed + 1));
        Hex.Same(Refused, client.Wire.One());
        client.Channel.Receive(ClipServer.Message(ClipServer.FileRequestType, 0, ClipServer.U32(7), new byte[10]));
        Hex.Same(Refused, client.Wire.One());

        // Without a stream id there is nobody to answer.
        client.Channel.Receive(ClipServer.Message(ClipServer.FileRequestType, 0, [7, 0]));
        client.Wire.Nothing();

        Assert.Empty(client.TakeEvents());
        Assert.Equal(9, client.Wire.Log.Count);

        // The largest range that is passed on.
        client.Channel.Receive(ClipServer.FileRangeRequest(7, 0, long.MaxValue, ClipboardChannel.MaxRangeServed));
        Assert.Equal(new FileContentsRequest(7, 0, false, long.MaxValue, ClipboardChannel.MaxRangeServed, null), Assert.Single(client.FileRequests));
    }

    [Fact]
    public void A_flood_of_requests_stops_at_the_application()
    {
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(Text, null), new(0xC0D0, "FileGroupDescriptorW")]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Wire.Take();
        client.Events.Clear();

        for (var i = 0; i < 1000; i++)
        {
            client.Channel.Receive(ClipServer.DataRequest(Text));
            client.Channel.Receive(ClipServer.FileSizeRequest((uint)(i % 10), 0));
        }

        // 16 data requests and 64 file requests wait for the application; the rest got its refusal at once.
        Assert.Equal(16 + ClipboardChannel.MaxFileRequests, client.TakeEvents().Length);
        Assert.Equal(2000 - 16 - ClipboardChannel.MaxFileRequests, client.Wire.Take().Length);

        // Answering makes room again.
        client.Channel.SendData(null);
        client.Channel.SendFileSize(3, 1);
        client.Wire.Take();
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Channel.Receive(ClipServer.FileSizeRequest(3, 0));
        Assert.Equal(["requested 13", "file 3 size of 0"], client.TakeEvents());
        client.Wire.Nothing();
    }

    [Fact]
    public void Answers_the_application_gets_wrong_do_not_reach_the_server_as_they_are()
    {
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(0xC0D0, "FileGroupDescriptorW")]);
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Wire.Take();

        // Nothing was asked.
        client.Channel.SendData([1]);
        client.Channel.SendFileContents(1, [1]);
        client.Channel.SendFileSize(1, 5);
        client.Wire.Nothing();

        // Contents for a size request and a size for a range request would be misread: refused instead.
        client.Channel.Receive(ClipServer.FileSizeRequest(1, 0));
        client.Channel.SendFileContents(1, [1, 2, 3]);
        Hex.Same("09 00 02 00 04 00 00 00 01 00 00 00", client.Wire.One());
        client.Channel.Receive(ClipServer.FileRangeRequest(2, 0, 0, 4));
        client.Channel.SendFileSize(2, 99);
        Hex.Same("09 00 02 00 04 00 00 00 02 00 00 00", client.Wire.One());

        // More than the server asked for is cut to what it asked for.
        client.Channel.Receive(ClipServer.FileRangeRequest(3, 0, 0, 4));
        client.Channel.SendFileContents(3, [1, 2, 3, 4, 5, 6]);
        Hex.Same("09 00 01 00 08 00 00 00 03 00 00 00 01 02 03 04", client.Wire.One());

        Assert.Throws<ArgumentOutOfRangeException>(() => client.Channel.SendFileSize(4, -1));
        Assert.Equal(6, client.Wire.Log.Count);
    }

    [Fact]
    public void File_requests_this_side_cannot_make_are_answered_at_once()
    {
        // A server without huge-file support and without locks.
        var client = ClipClient.Started(ClipboardChannel.LongFormatNames | ClipboardChannel.StreamFileClip | ClipboardChannel.NoFilePaths);

        client.Channel.RequestFileRange(1, -1, 0, 10);
        client.Channel.RequestFileRange(2, 0, -1, 10);
        client.Channel.RequestFileRange(3, 0, 0, 0);
        client.Channel.RequestFileRange(4, 0, 0, ClipboardChannel.MaxRangeRequest + 1);
        client.Channel.RequestFileRange(5, 0, 0x80000000, 10);
        client.Channel.RequestFileRange(6, 0, 0, 10, 99);
        client.Wire.Nothing();
        Assert.Equal(Enumerable.Range(1, 6).Select(stream => $"contents {stream} refused"), client.TakeEvents());

        // Below two gigabytes such a server is asked.
        client.Channel.RequestFileRange(7, 0, 0x7FFFFFFF, ClipboardChannel.MaxRangeRequest);
        Assert.Equal(32, client.Wire.One().Length);

        // A stream id that is taken would make two answers indistinguishable.
        Assert.Throws<ArgumentException>(() => client.Channel.RequestFileSize(7, 0));

        // No more than 64 at a time.
        for (var stream = 100u; stream < 100 + ClipboardChannel.MaxFileRequests; stream++)
        {
            client.Channel.RequestFileSize(stream, 0);
        }

        Assert.Equal(ClipboardChannel.MaxFileRequests - 1, client.Wire.Take().Length);
        Assert.Equal(["contents 163 refused"], client.TakeEvents());
    }

    [Fact]
    public void Answers_to_file_requests_are_checked_against_what_was_asked()
    {
        var client = ClipClient.Started();

        // More bytes than asked for, a size that is not one, an answer for a stream nobody opened.
        client.Channel.RequestFileRange(1, 0, 0, 4);
        client.Channel.Receive(ClipServer.FileResponse(1, new byte[5]));
        client.Channel.RequestFileSize(2, 0);
        client.Channel.Receive(ClipServer.FileResponse(2, new byte[7]));
        client.Channel.Receive(ClipServer.FileResponse(3, new byte[4]));
        client.Channel.Receive(ClipServer.Message(ClipServer.FileResponseType, ClipServer.Ok, [1, 0]));
        Assert.Equal(["contents 1 refused", "contents 2 refused"], client.TakeEvents());
        Assert.Equal(4, client.Wire.Log.Count);

        // Fewer bytes are what the end of a file looks like; an empty answer is one too.
        client.Channel.RequestFileRange(4, 0, 0, 4);
        client.Channel.Receive(ClipServer.FileResponse(4, [1, 2]));
        client.Channel.RequestFileRange(5, 0, 0, 4);
        client.Channel.Receive(ClipServer.FileResponse(5, []));
        Assert.Equal(["contents 4 2", "contents 5 0"], client.TakeEvents());

        // The stream id is free again once its answer is there.
        client.Channel.RequestFileSize(4, 0);
        client.Channel.Receive(ClipServer.FileResponse(4, ClipServer.U64(123)));
        Assert.Equal(123L, ClipboardChannel.FileSize(client.Files[^1].Data));
        Assert.Null(ClipboardChannel.FileSize([1, 2, 3]));
        Assert.Null(ClipboardChannel.FileSize(ClipServer.U64(ulong.MaxValue)));
    }

    [Fact]
    public void Locks_are_kept_track_of()
    {
        var client = ClipClient.Started();

        Assert.True(client.Channel.Lock(1));
        Assert.False(client.Channel.Lock(1));
        Assert.False(client.Channel.Unlock(2));
        Assert.True(client.Channel.Unlock(1));
        Assert.False(client.Channel.Unlock(1));
        Assert.Equal(2, client.Wire.Take().Length);

        // The server's: a second lock of the same id and an unlock of an unknown one change nothing.
        client.Channel.Receive(ClipServer.Lock(4));
        client.Channel.Receive(ClipServer.Lock(4));
        client.Channel.Receive(ClipServer.Unlock(5));
        client.Channel.Receive(ClipServer.Unlock(4));
        client.Channel.Receive(ClipServer.Unlock(4));
        Assert.Equal(["locked 4", "unlocked 4"], client.TakeEvents());

        // No more than 256 ids at a time, on either side.
        for (var id = 0u; id < ClipboardChannel.MaxLocks + 10; id++)
        {
            client.Channel.Receive(ClipServer.Lock(1000 + id));
            Assert.Equal(id < ClipboardChannel.MaxLocks, client.Channel.Lock(id));
        }

        Assert.Equal(ClipboardChannel.MaxLocks, client.TakeEvents().Length);
        Assert.Equal(ClipboardChannel.MaxLocks, client.Wire.Take().Length);
    }

    // MS-RDPECLIP <1>: Windows Server 2003 to 2012 R2 append four bytes that the header does not count.
    [Fact]
    public void Four_bytes_after_the_message_are_ignored()
    {
        var client = ClipClient.Started();

        client.Channel.Receive([.. ClipServer.LongFormatList((Text, null)), 0xDE, 0xAD, 0xBE, 0xEF]);
        client.Channel.RequestData(Text);
        client.Channel.Receive([.. ClipServer.DataResponse(Encoding.Unicode.GetBytes("x\0")), 0, 0, 0, 0]);

        Assert.Equal(["formats 13", "data 4"], client.TakeEvents());
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void Messages_out_of_place_are_ignored()
    {
        var client = new ClipClient();

        // Before the server is ready nothing is expected from it but its capabilities.
        client.Channel.Receive(ClipServer.LongFormatList((Text, null)));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Channel.Receive(ClipServer.FileSizeRequest(1, 0));
        client.Channel.Receive(ClipServer.Lock(1));
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataResponse([1]));
        client.Channel.Receive(ClipServer.FileResponse(1, [1]));
        client.Channel.Receive(ClipServer.Message(99, 0, [1, 2, 3]));

        client.Wire.Nothing();
        Assert.Empty(client.TakeEvents());
        Assert.Equal(8, client.Wire.Log.Count);
    }

    [Fact]
    public void Formats_that_cannot_be_sent_are_the_callers_mistake()
    {
        var client = ClipClient.Started();

        Assert.Throws<ArgumentNullException>(() => client.Channel.AnnounceFormats(null!));
        Assert.Throws<ArgumentException>(() => client.Channel.AnnounceFormats([new(1, "a\0b")]));
        Assert.Throws<ArgumentException>(() => client.Channel.AnnounceFormats([new(1, null), null!]));
        Assert.Throws<ArgumentException>(() => client.Channel.AnnounceFormats([new(1, new string('x', 600_000))]));
        client.Wire.Nothing();
    }

    [Fact]
    public void Handlers_may_call_back_into_the_channel()
    {
        var client = ClipClient.Started();
        client.Channel.RemoteFormats += formats => client.Channel.RequestData(formats[0].Id);
        client.Channel.DataRequested += format => client.Channel.SendData([(byte)format]);
        client.Channel.FileContentsRequested += request => client.Channel.SendFileSize(request.StreamId, 42);
        client.Channel.DataReceived += _ => client.Channel.AnnounceFormats([new(Text, null), new(0xC0D0, "FileGroupDescriptorW")]);

        client.Channel.Receive(ClipServer.LongFormatList((Dib, null)));
        client.Channel.Receive(ClipServer.DataResponse([1, 2, 3]));
        client.Channel.Receive(ClipServer.FormatListResponse(true));
        client.Channel.Receive(ClipServer.DataRequest(Text));
        client.Channel.Receive(ClipServer.FileSizeRequest(6, 0));

        // The answer to the format list goes out before the request the handler made from inside the event.
        Assert.Equal(
            [
                ClipServer.FormatListResponseType, ClipServer.DataRequestType, ClipServer.FormatListType,
                ClipServer.DataResponseType, ClipServer.FileResponseType,
            ],
            client.Wire.Take().Select(message => ClipServer.Parse(message).Type));
        Assert.Empty(client.Wire.Log);
    }

    [Fact]
    public void A_handler_that_throws_is_reported_and_changes_nothing()
    {
        var client = ClipClient.Started();
        client.Channel.RemoteFormats += _ => throw new InvalidOperationException("the handler broke");
        client.Channel.DataReceived += _ => throw new InvalidOperationException("the handler broke again");

        client.Channel.Receive(ClipServer.LongFormatList((Text, null)));
        client.Channel.RequestData(Text);
        client.Channel.Receive(ClipServer.DataResponse([1]));
        client.Channel.RequestData(Text);

        Assert.Equal(3, client.Wire.Take().Length);
        Assert.Equal(2, client.Wire.Log.Count);
        Assert.All(client.Wire.Log, line => Assert.Contains("the handler broke", line));
    }

    [Fact]
    public void A_channel_without_a_way_to_send_reports_what_it_drops()
    {
        var channel = new ClipboardChannel();
        var log = new List<string>();
        channel.Log = log.Add;

        channel.Receive(ClipServer.Capabilities(AllFlags));
        channel.Receive(ClipServer.MonitorReady());

        Assert.Equal(2, log.Count);
        Assert.True(channel.IsReady);
    }

    [Fact]
    public void A_send_that_fails_is_reported_and_the_channel_goes_on()
    {
        var channel = new ClipboardChannel();
        var log = new List<string>();
        var sent = 0;
        channel.Log = log.Add;
        channel.Send = _ =>
        {
            sent++;
            throw new IOException("the connection is gone");
        };

        channel.Receive(ClipServer.Capabilities(AllFlags));
        channel.Receive(ClipServer.MonitorReady());
        channel.AnnounceFormats([new(Text, null)]);

        Assert.Equal(3, sent);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void The_options_ask_for_the_chunk_headers_and_for_no_compression()
    {
        var channel = new ClipboardChannel();

        Assert.Equal("cliprdr", channel.Name);
        Assert.Equal(0xC0200000, channel.Options);
        Assert.NotEqual(0u, channel.Options & StaticChannelOptions.ShowProtocol);
        Assert.Equal(0u, channel.Options & (StaticChannelOptions.Compress | StaticChannelOptions.CompressRdp));
    }

    // Many threads ask, announce and answer while the session's thread takes the server's messages. Nothing
    // may hang or throw, every request must get exactly one answer with its data, and a data request must
    // never be on the wire together with another.
    [Fact]
    public async Task Calls_from_many_threads_keep_the_conversation_straight()
    {
        const int Workers = 8;
        const int Rounds = 400;
        var channel = new ClipboardChannel();
        var toServer = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var dataAnswers = 0;
        var fileAnswers = 0;
        var refusals = 0;
        var together = 0;
        var pasted = 0;

        // As many requests under way as the channel takes without refusing one.
        using var dataRoom = new SemaphoreSlim(ClipboardChannel.MaxWaitingRequests);
        using var fileRoom = new SemaphoreSlim(ClipboardChannel.MaxFileRequests);
        channel.Log = notes.Enqueue;
        channel.Send = message => toServer.Enqueue(message.ToArray());
        channel.DataReceived += data =>
        {
            Interlocked.Add(ref refusals, data is null ? 1 : 0);
            Interlocked.Increment(ref dataAnswers);
            dataRoom.Release();
        };
        channel.FileContentsReceived += (_, data) =>
        {
            Interlocked.Add(ref refusals, data is null ? 1 : 0);
            Interlocked.Increment(ref fileAnswers);
            fileRoom.Release();
        };
        channel.DataRequested += _ => channel.SendData([1]);
        channel.Receive(ClipServer.Capabilities(AllFlags));
        channel.Receive(ClipServer.MonitorReady());
        channel.Receive(ClipServer.LongFormatList((Text, null)));

        // What the start put on the wire is not part of the count.
        toServer.Clear();

        using var stop = new CancellationTokenSource();
        var server = Task.Run(() =>
        {
            // The session's thread: plays the server for whatever the client sent.
            while (!stop.IsCancellationRequested || !toServer.IsEmpty)
            {
                if (!toServer.TryDequeue(out var message))
                {
                    Thread.Yield();
                    continue;
                }

                var (type, flags, body) = ClipServer.Parse(message);
                switch (type)
                {
                    case ClipServer.DataRequestType:
                        // The next one may only leave the client after this one was answered.
                        together += toServer.Count(other => ClipServer.Parse(other).Type == ClipServer.DataRequestType);
                        channel.Receive(ClipServer.DataResponse([1, 2, 3]));
                        break;
                    case ClipServer.FileRequestType:
                        channel.Receive(ClipServer.FileResponse(U32(body, 0), [1]));
                        break;
                    case ClipServer.FormatListType:
                        channel.Receive(ClipServer.FormatListResponse(true));
                        channel.Receive(ClipServer.DataRequest(Text));
                        break;
                    case ClipServer.DataResponseType:
                        pasted += flags == ClipServer.Ok ? 1 : 0;
                        break;
                }
            }
        });

        var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                await dataRoom.WaitAsync();
                channel.RequestData(Text);
                await fileRoom.WaitAsync();
                channel.RequestFileRange((uint)(worker * Rounds + round), 0, 0, 1);
                if (round % 16 == 0)
                {
                    channel.AnnounceFormats([new(Text, null)]);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));
        SpinWait.SpinUntil(() => Volatile.Read(ref dataAnswers) == Workers * Rounds && Volatile.Read(ref fileAnswers) == Workers * Rounds, TimeSpan.FromSeconds(60));
        stop.Cancel();
        await server.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(Workers * Rounds, dataAnswers);
        Assert.Equal(Workers * Rounds, fileAnswers);
        Assert.Equal(0, refusals);
        Assert.Equal(0, together);
        Assert.Equal(Workers * Rounds / 16, pasted);
        Assert.Empty(notes);
    }
}
