using System.Buffers.Binary;
using System.Text;

namespace Matgate.Display.Rdp.Channels.Tests;

// The annotated examples of MS-RDPECLIP section 4: what a server sends is decoded and compared field by field,
// what a client sends is produced and compared byte by byte.
public class ClipboardSpecTests
{
    // 4.1.1 Server Clipboard Capabilities PDU: version 2, long format names, stream file clipboard, no file paths.
    private const string ServerCapabilities = "07 00 00 00 10 00 00 00 01 00 00 00 01 00 0c 00 02 00 00 00 0e 00 00 00";

    // 4.1.2 Server Monitor Ready PDU.
    private const string MonitorReady = "01 00 00 00 00 00 00 00";

    // 4.1.3 Client Clipboard Capabilities PDU.
    private const string ClientCapabilities = "07 00 00 00 10 00 00 00 01 00 00 00 01 00 0c 00 02 00 00 00 0e 00 00 00";

    // 4.1.5 Format List PDU: "Native" (0xc004), then 3, 8 and 17 without names.
    private const string InitialFormatList = """
        02 00 00 00 24 00 00 00 04 c0 00 00 4e 00 61 00
        74 00 69 00 76 00 65 00 00 00 03 00 00 00 00 00
        08 00 00 00 00 00 11 00 00 00 00 00
        """;

    // 4.1.6, 4.2.2 and 4.5.2 Format List Response PDU.
    private const string FormatListResponse = "03 00 01 00 00 00 00 00";

    // 4.2.1 Format List PDU of the copy sequence.
    private const string CopyFormatList = """
        02 00 00 00 e0 00 00 00 8a c0 00 00 52 00 69 00
        63 00 68 00 20 00 54 00 65 00 78 00 74 00 20 00
        46 00 6f 00 72 00 6d 00 61 00 74 00 00 00 45 c1
        00 00 52 00 69 00 63 00 68 00 20 00 54 00 65 00
        78 00 74 00 20 00 46 00 6f 00 72 00 6d 00 61 00
        74 00 20 00 57 00 69 00 74 00 68 00 6f 00 75 00
        74 00 20 00 4f 00 62 00 6a 00 65 00 63 00 74 00
        73 00 00 00 43 c1 00 00 52 00 54 00 46 00 20 00
        41 00 73 00 20 00 54 00 65 00 78 00 74 00 00 00
        01 00 00 00 00 00 0d 00 00 00 00 00 04 c0 00 00
        4e 00 61 00 74 00 69 00 76 00 65 00 00 00 0e c0
        00 00 4f 00 62 00 6a 00 65 00 63 00 74 00 20 00
        44 00 65 00 73 00 63 00 72 00 69 00 70 00 74 00
        6f 00 72 00 00 00 03 00 00 00 00 00 10 00 00 00
        00 00 07 00 00 00 00 00
        """;

    private static readonly ClipFormat[] CopyFormats =
    [
        new(0xC08A, "Rich Text Format"),
        new(0xC145, "Rich Text Format Without Objects"),
        new(0xC143, "RTF As Text"),
        new(1, null),
        new(13, null),
        new(0xC004, "Native"),
        new(0xC00E, "Object Descriptor"),
        new(3, null),
        new(16, null),
        new(7, null),
    ];

    // 4.3.1 Lock Clipboard Data PDU and 4.3.2 Unlock Clipboard Data PDU, both for clipDataId 8.
    private const string LockData = "0a 00 00 00 04 00 00 00 08 00 00 00";
    private const string UnlockData = "0b 00 00 00 04 00 00 00 08 00 00 00";

    // 4.4.1 Format Data Request PDU for CF_UNICODETEXT.
    private const string TextRequest = "04 00 00 00 04 00 00 00 0d 00 00 00";

    // 4.4.2 Format Data Response PDU: "hello world".
    private const string TextResponse = """
        05 00 01 00 18 00 00 00 68 00 65 00 6c 00 6c 00
        6f 00 20 00 77 00 6f 00 72 00 6c 00 64 00 00 00
        """;

    // 4.4.3.1 Requesting the Size of a File: stream 2, file 1.
    private const string SizeRequest = """
        08 00 00 00 18 00 00 00 02 00 00 00 01 00 00 00
        01 00 00 00 00 00 00 00 00 00 00 00 08 00 00 00
        """;

    // 4.4.3.2 Requesting the Contents of a File: stream 2, file 1, 65536 bytes from the start.
    private const string RangeRequest = """
        08 00 00 00 18 00 00 00 02 00 00 00 01 00 00 00
        02 00 00 00 00 00 00 00 00 00 00 00 00 00 01 00
        """;

    // 4.4.4.1 Sending the Size of a File: stream 2, 44 bytes.
    private const string SizeResponse = """
        09 00 01 00 0c 00 00 00 02 00 00 00 2c 00 00 00
        00 00 00 00
        """;

    // 4.4.4.2 Sending the Contents of a File: stream 2.
    private const string RangeResponse = """
        09 00 01 00 30 00 00 00 02 00 00 00 54 68 65 20
        71 75 69 63 6b 20 62 72 6f 77 6e 20 66 6f 78 20
        6a 75 6d 70 73 20 6f 76 65 72 20 74 68 65 20 6c
        61 7a 79 20 64 6f 67 2e
        """;

    private const string Fox = "The quick brown fox jumps over the lazy dog.";

    // 4.5.1 Format List PDU that offers a file list under the id 0xc079.
    private const string FileFormatList = """
        02 00 00 00 2e 00 00 00 79 c0 00 00 46 00 69 00
        6c 00 65 00 47 00 72 00 6f 00 75 00 70 00 44 00
        65 00 73 00 63 00 72 00 69 00 70 00 74 00 6f 00
        72 00 57 00 00 00
        """;

    // 4.5.3 Format Data Request PDU for the file list.
    private const string FileListRequest = "04 00 00 00 04 00 00 00 79 c0 00 00";

    // What both sides of the examples announced: 0x0e, as in 4.1.1 and 4.1.3.
    private static ClipClient StartedLikeTheExamples()
    {
        var client = new ClipClient();
        client.Channel.Receive(Hex.Bytes(ServerCapabilities));
        client.Channel.Receive(Hex.Bytes(MonitorReady));
        client.Channel.Receive(Hex.Bytes(FormatListResponse));
        client.Wire.Take();
        client.Events.Clear();
        return client;
    }

    // 4.1 Annotated Initialization Sequence: 4.1.1 and 4.1.2 from the server, 4.1.3 and 4.1.5 from the client,
    // 4.1.6 from the server. (4.1.4, the temporary directory, is optional and not sent.)
    [Fact]
    public void Initialization_sequence_with_the_formats_announced_when_the_server_is_ready()
    {
        var client = new ClipClient();
        client.Channel.Ready += () => client.Channel.AnnounceFormats([new(0xC004, "Native"), new(3, null), new(8, null), new(17, null)]);

        client.Channel.Receive(Hex.Bytes(ServerCapabilities));
        client.Wire.Nothing();
        Assert.False(client.Channel.IsReady);

        client.Channel.Receive(Hex.Bytes(MonitorReady));
        var sent = client.Wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same(ClientCapabilities, sent[0]);
        Hex.Same(InitialFormatList, sent[1]);
        Assert.True(client.Channel.IsReady);
        Assert.True(client.Channel.CanTransferFiles);
        Assert.False(client.Channel.CanLock);
        Assert.False(client.Channel.SupportsHugeFiles);

        client.Channel.Receive(Hex.Bytes(FormatListResponse));
        client.Wire.Nothing();
        Assert.Equal(["ready", "acknowledged True"], client.TakeEvents());
        Assert.Empty(client.Wire.Log);
    }

    // The same sequence when the application announced its formats before the session was up.
    [Fact]
    public void Initialization_sequence_with_the_formats_announced_beforehand()
    {
        var client = new ClipClient();
        client.Channel.AnnounceFormats([new(0xC004, "Native"), new(3, null), new(8, null), new(17, null)]);
        client.Wire.Nothing();

        client.Channel.Receive(Hex.Bytes(ServerCapabilities));
        client.Channel.Receive(Hex.Bytes(MonitorReady));

        var sent = client.Wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same(ClientCapabilities, sent[0]);
        Hex.Same(InitialFormatList, sent[1]);
    }

    // 4.1.4 Client Temporary Directory PDU: a client's message. This client never sends one, and one that
    // comes from a server is not acted on.
    [Fact]
    public void A_temporary_directory_is_neither_sent_nor_taken()
    {
        var client = StartedLikeTheExamples();

        client.Channel.Receive(Hex.Resource("rdpeclip-4.1.4.txt"));

        client.Wire.Nothing();
        Assert.Empty(client.TakeEvents());
        Assert.Single(client.Wire.Log);
    }

    // 4.2.1 Format List PDU and 4.2.2 Format List Response PDU, the server copying.
    [Fact]
    public void Copy_sequence_from_the_server()
    {
        var client = StartedLikeTheExamples();

        client.Channel.Receive(Hex.Bytes(CopyFormatList));

        Hex.Same(FormatListResponse, client.Wire.One());
        Assert.Equal(CopyFormats, Assert.Single(client.Remote));
    }

    // 4.2.1 and 4.2.2 the other way round, the client copying.
    [Fact]
    public void Copy_sequence_from_the_client()
    {
        var client = StartedLikeTheExamples();

        client.Channel.AnnounceFormats(CopyFormats);
        Hex.Same(CopyFormatList, client.Wire.One());

        client.Channel.Receive(Hex.Bytes(FormatListResponse));
        Assert.Equal(["acknowledged True"], client.TakeEvents());
    }

    // 4.3.1 Lock Clipboard Data PDU and 4.3.2 Unlock Clipboard Data PDU, from the server.
    [Fact]
    public void Lock_and_unlock_from_the_server()
    {
        var client = ClipClient.Started();

        client.Channel.Receive(Hex.Bytes(LockData));
        client.Channel.Receive(Hex.Bytes(UnlockData));

        Assert.Equal(["locked 8", "unlocked 8"], client.TakeEvents());
        client.Wire.Nothing();
    }

    // 4.3.1 and 4.3.2 from the client.
    [Fact]
    public void Lock_and_unlock_from_the_client()
    {
        var client = ClipClient.Started();

        Assert.True(client.Channel.Lock(8));
        Hex.Same(LockData, client.Wire.One());

        Assert.True(client.Channel.Unlock(8));
        Hex.Same(UnlockData, client.Wire.One());
    }

    // 4.4.1 Format Data Request PDU and 4.4.2 Format Data Response PDU, the client pasting.
    [Fact]
    public void Paste_sequence_on_the_client()
    {
        var client = StartedLikeTheExamples();
        client.Channel.Receive(Hex.Bytes(CopyFormatList));
        client.Wire.Take();

        client.Channel.RequestData(13);
        Hex.Same(TextRequest, client.Wire.One());

        client.Channel.Receive(Hex.Bytes(TextResponse));
        Assert.Equal("hello world", ClipboardText.Decode(Assert.Single(client.Data)));
    }

    // 4.4.1 and 4.4.2, the server pasting.
    [Fact]
    public void Paste_sequence_on_the_server()
    {
        var client = StartedLikeTheExamples();
        client.Channel.AnnounceFormats([new(13, null)]);
        client.Channel.Receive(Hex.Bytes(FormatListResponse));
        client.Wire.Take();
        client.Events.Clear();

        client.Channel.Receive(Hex.Bytes(TextRequest));
        Assert.Equal(["requested 13"], client.TakeEvents());
        client.Wire.Nothing();

        client.Channel.SendData(ClipboardText.Encode("hello world"));
        Hex.Same(TextResponse, client.Wire.One());
    }

    // 4.4.3.1 and 4.4.4.1: the client asks for the size of a file.
    [Fact]
    public void File_size_asked_by_the_client()
    {
        var client = StartedLikeTheExamples();

        client.Channel.RequestFileSize(2, 1);
        Hex.Same(SizeRequest, client.Wire.One());

        client.Channel.Receive(Hex.Bytes(SizeResponse));
        var (stream, data) = Assert.Single(client.Files);
        Assert.Equal(2u, stream);
        Assert.Equal(44L, ClipboardChannel.FileSize(data));
    }

    // 4.4.3.2 and 4.4.4.2: the client asks for the contents of a file.
    [Fact]
    public void File_contents_asked_by_the_client()
    {
        var client = StartedLikeTheExamples();

        client.Channel.RequestFileRange(2, 1, 0, 65536);
        Hex.Same(RangeRequest, client.Wire.One());

        client.Channel.Receive(Hex.Bytes(RangeResponse));
        var (stream, data) = Assert.Single(client.Files);
        Assert.Equal(2u, stream);
        Assert.Equal(Fox, Encoding.ASCII.GetString(data!));
    }

    // 4.4.3.1 and 4.4.4.1: the server asks for the size of a file.
    [Fact]
    public void File_size_asked_by_the_server()
    {
        var client = StartedLikeTheExamples();

        client.Channel.Receive(Hex.Bytes(SizeRequest));
        Assert.Equal(new FileContentsRequest(2, 1, true, 0, 8, null), Assert.Single(client.FileRequests));

        client.Channel.SendFileSize(2, 44);
        Hex.Same(SizeResponse, client.Wire.One());
    }

    // 4.4.3.2 and 4.4.4.2: the server asks for the contents of a file.
    [Fact]
    public void File_contents_asked_by_the_server()
    {
        var client = StartedLikeTheExamples();

        client.Channel.Receive(Hex.Bytes(RangeRequest));
        Assert.Equal(new FileContentsRequest(2, 1, false, 0, 65536, null), Assert.Single(client.FileRequests));

        client.Channel.SendFileContents(2, Encoding.ASCII.GetBytes(Fox));
        Hex.Same(RangeResponse, client.Wire.One());
    }

    // 4.4.5 Metafile Data Contained in a Format Data Response PDU. The channel does not look into the data of
    // a format: the Packed Metafile Payload arrives as the server sent it.
    [Fact]
    public void Metafile_data_arrives_untouched()
    {
        var example = Hex.Resource("rdpeclip-4.4.5.txt");
        var client = StartedLikeTheExamples();
        client.Channel.Receive(Hex.Bytes(CopyFormatList));

        client.Channel.RequestData(ClipboardFormats.MetafilePicture);
        client.Channel.Receive(example);

        var data = Assert.Single(client.Data)!;
        Assert.Equal(2586, data.Length);
        Hex.Same(example.AsSpan(8), data);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(556u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(423u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
    }

    // 4.4.6 Palette Data Contained in a Format Data Response PDU: 216 palette entries of four bytes.
    [Fact]
    public void Palette_data_arrives_untouched()
    {
        var example = Hex.Resource("rdpeclip-4.4.6.txt");
        var client = StartedLikeTheExamples();
        client.Channel.Receive(ClipServer.LongFormatList((ClipboardFormats.Palette, null)));

        client.Channel.RequestData(ClipboardFormats.Palette);
        client.Channel.Receive(example);

        var data = Assert.Single(client.Data)!;
        Assert.Equal(216 * 4, data.Length);
        Hex.Same(example.AsSpan(8), data);
        Hex.Same("33 00 00 00", data.AsSpan(4, 4));
        Hex.Same("ff ff ff 00", data.AsSpan(215 * 4, 4));
    }

    // 4.5 Retrieving a File List: 4.5.1 and 4.5.4 from the server, 4.5.2 and 4.5.3 from the client.
    [Fact]
    public void File_list_retrieved_by_the_client()
    {
        var client = StartedLikeTheExamples();

        client.Channel.Receive(Hex.Bytes(FileFormatList));
        Hex.Same(FormatListResponse, client.Wire.One());
        var formats = Assert.Single(client.Remote);
        Assert.Equal([new ClipFormat(0xC079, "FileGroupDescriptorW")], formats);
        Assert.Equal(0xC079u, FileGroupDescriptor.FindFormat(formats));

        client.Channel.RequestData(0xC079);
        Hex.Same(FileListRequest, client.Wire.One());

        client.Channel.Receive(Hex.Resource("rdpeclip-4.5.4.txt"));
        var files = FileGroupDescriptor.Decode(Assert.Single(client.Data));

        // The annotation of the example reads the time as 25 October 2009, 21:17 - on a clock in Redmond,
        // seven hours behind UTC.
        var written = DateTime.FromFileTimeUtc(129010042240261384);
        Assert.Equal(new DateTime(2009, 10, 26, 4, 17, 4, DateTimeKind.Utc), written.AddTicks(-(written.Ticks % TimeSpan.TicksPerSecond)));
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);
        Assert.Equal(new ClipFile("File1.txt", 44, false, written) { Attributes = FileGroupDescriptor.AttributeArchive }, files[0]);
        Assert.Equal(new ClipFile("File2.txt", 10, false, written) { Attributes = FileGroupDescriptor.AttributeArchive }, files[1]);
    }

    // 4.5.1 to 4.5.4 the other way round: the client offers the two files and the server fetches the list.
    [Fact]
    public void File_list_retrieved_by_the_server()
    {
        var example = Hex.Resource("rdpeclip-4.5.4.txt");
        var written = DateTime.FromFileTimeUtc(129010042240261384);
        var client = StartedLikeTheExamples();

        client.Channel.AnnounceFormats([new(0xC079, FileGroupDescriptor.FormatName)]);
        Hex.Same(FileFormatList, client.Wire.One());
        client.Channel.Receive(Hex.Bytes(FormatListResponse));

        client.Channel.Receive(Hex.Bytes(FileListRequest));
        client.Channel.SendData(FileGroupDescriptor.Encode(
        [
            new ClipFile("File1.txt", 44, false, written) { Attributes = FileGroupDescriptor.AttributeArchive },
            new ClipFile("File2.txt", 10, false, written) { Attributes = FileGroupDescriptor.AttributeArchive },
        ]));

        Hex.Same(example, client.Wire.One());
    }

    // Every example a server can send, cut off after each of its bytes: nothing is thrown, nothing is answered
    // that was not asked, and the complete message still works afterwards.
    [Fact]
    public void Truncated_examples_are_dropped()
    {
        string[] examples =
        [
            ServerCapabilities, MonitorReady, InitialFormatList, FormatListResponse, CopyFormatList, LockData, UnlockData,
            TextRequest, TextResponse, SizeRequest, RangeRequest, SizeResponse, RangeResponse, FileFormatList, FileListRequest,
        ];
        var messages = examples.Select(Hex.Bytes)
            .Append(Hex.Resource("rdpeclip-4.4.5.txt"))
            .Append(Hex.Resource("rdpeclip-4.4.6.txt"))
            .Append(Hex.Resource("rdpeclip-4.5.4.txt"))
            .ToList();

        foreach (var message in messages)
        {
            var client = StartedLikeTheExamples();
            for (var length = 0; length < message.Length; length++)
            {
                client.Channel.Receive(message.AsSpan(0, length));

                // A header that promises more than there is: the message is dropped whole.
                Assert.Empty(client.TakeEvents());
                client.Wire.Nothing();
            }

            Assert.Equal(message.Length, client.Wire.Log.Count);
            client.Channel.Receive(message);
        }
    }
}
