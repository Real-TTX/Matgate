namespace Matgate.Display.Rdp.Channels.Tests;

// Dynamic virtual channels: the annotated examples of MS-RDPEDYC section 4, whole conversations, and what a
// server can get wrong.
public class DynamicChannelTests
{
    // 4.1.1 DVC Capabilities Request (Version2) PDU, with the Sp bits Windows leaves as they were.
    private const string CapabilitiesRequest = "58 00 02 00 33 33 11 11 3d 0a a7 04";

    // 4.1.2 DVC Capabilities Response PDU.
    private const string CapabilitiesResponse = "50 00 02 00";

    // 4.2.1 DVC Create Request PDU: channel 3, "testdvc".
    private const string CreateRequest = "10 03 74 65 73 74 64 76 63 00";

    // 4.2.2 DVC Create Response PDU.
    private const string CreateResponse = "10 03 00 00 00 00";

    // 4.4.1 DVC Close PDU.
    private const string ClosePdu = "40 03";

    // 4.3.3 DVC Data First Compressed PDU and the first PDU of 4.3.4 DVC Data Compressed PDU.
    private const string DataFirstCompressed = "64 03 7b 0c e0 26 38 c4 3f f4 74 01";
    private const string DataCompressed = "70 03 e0 26 88 7f e8 f4 02";

    // The message of 4.3: 3195 bytes of 'q' on channel 3.
    private static readonly byte[] Queues = Enumerable.Repeat((byte)0x71, 3195).ToArray();

    // 4.3.1 DVC Data First PDU: the length in two bytes, then 1600 - 4 bytes of the message.
    private static readonly byte[] DataFirstExample = [0x24, 0x03, 0x7b, 0x0c, .. Queues.AsSpan(0, 1596)];

    // 4.3.2 DVC Data PDU: 1600 - 2 bytes, and the last packet with the one byte that is left. The example's
    // header byte is 0x34 - the Sp bits are not initialised by Windows (MS-RDPEDYC <8>).
    private static readonly byte[] DataExample = [0x34, 0x03, .. Queues.AsSpan(0, 1598)];
    private static readonly byte[] LastDataExample = [0x34, 0x03, 0x71];

    private static (DynamicChannels Channels, Wire Wire, RecordingChannel Test) Started(uint id = 3, int idWidth = 0)
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        channels.Register(test);
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(id, "testdvc", idWidth));
        wire.Take();
        test.Events.Clear();
        return (channels, wire, test);
    }

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13 + (i >> 9))).ToArray();

    // 4.1 Annotated Initializing DVCs.
    [Fact]
    public void Capabilities_as_in_the_specification()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);

        channels.Receive(Hex.Bytes(CapabilitiesRequest));

        Hex.Same(CapabilitiesResponse, wire.One());
        Assert.Equal(2, channels.Version);
        Assert.Empty(wire.Log);
    }

    [Theory]
    [InlineData(1, "50 00 01 00")]
    [InlineData(2, "50 00 02 00")]
    [InlineData(3, "50 00 02 00")]
    [InlineData(0xFFFF, "50 00 02 00")]
    public void The_answer_never_goes_beyond_version_2(int serverVersion, string answer)
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);

        channels.Receive(DvcServer.Capabilities((ushort)serverVersion));

        Hex.Same(answer, wire.One());
        Assert.Equal(Math.Min(serverVersion, 2), channels.Version);
    }

    // 4.2 Annotated Opening a DVC.
    [Fact]
    public void A_channel_somebody_listens_for_is_opened()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        channels.Register(test);
        channels.Receive(Hex.Bytes(CapabilitiesRequest));
        wire.Take();
        Assert.Null(test.Send);

        channels.Receive(Hex.Bytes(CreateRequest));

        Hex.Same(CreateResponse, wire.One());
        Assert.Equal(["opened"], test.TakeEvents());
        Assert.NotNull(test.Send);
        Assert.Empty(wire.Log);
    }

    [Fact]
    public void A_channel_nobody_listens_for_is_refused()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        channels.Register(new RecordingChannel("other"));
        channels.Receive(Hex.Bytes(CapabilitiesRequest));
        wire.Take();

        // Windows asks for the sound over a dynamic channel first, and falls back to "rdpsnd" when refused.
        channels.Receive(DvcServer.Create(3, "AUDIO_PLAYBACK_DVC"));

        // A negative HRESULT.
        Hex.Same("10 03 01 00 00 c0", wire.One());
        Assert.Contains("AUDIO_PLAYBACK_DVC", Assert.Single(wire.Log));

        // The id is free again: the server may use it for the next channel (MS-RDPEDYC 3.2.3.2.1).
        channels.Receive(DvcServer.Create(3, "other"));
        Hex.Same("10 03 00 00 00 00", wire.One());
    }

    // 4.3.1 and 4.3.2, from the server: three PDUs are one message of 3195 bytes.
    [Fact]
    public void A_fragmented_message_from_the_server_is_put_together()
    {
        var (channels, wire, test) = Started();

        channels.Receive(DataFirstExample);
        channels.Receive(DataExample);
        Assert.Empty(test.Messages);
        channels.Receive(LastDataExample);

        Hex.Same(Queues, Assert.Single(test.Messages));
        wire.Nothing();
        Assert.Empty(wire.Log);
    }

    // 4.3.1 and 4.3.2, from the client: the same message leaves as the same three PDUs.
    [Fact]
    public void A_long_message_to_the_server_is_fragmented()
    {
        var (_, wire, test) = Started();

        test.Send!(Queues);

        var sent = wire.Take();
        Assert.Equal(3, sent.Length);
        Hex.Same(DataFirstExample, sent[0]);

        // Byte for byte except for the two bits the specification leaves to chance and asks to be zero.
        Assert.Equal(0x30, sent[1][0]);
        Hex.Same(DataExample.AsSpan(1), sent[1].AsSpan(1));
        Assert.Equal(0x30, sent[2][0]);
        Hex.Same(LastDataExample.AsSpan(1), sent[2].AsSpan(1));
    }

    // 4.3.3 and 4.3.4: compressed data belongs to version 3, which this client never agrees to.
    [Fact]
    public void Compressed_data_is_dropped()
    {
        var (channels, wire, test) = Started();

        channels.Receive(DataFirstExample);
        channels.Receive(Hex.Bytes(DataFirstCompressed));
        channels.Receive(Hex.Bytes(DataCompressed));

        Assert.Empty(test.Messages);
        Assert.Equal(2, wire.Log.Count);

        // The message that was under way is gone with it; the next one is whole.
        channels.Receive(DvcServer.Data(3, [1, 2, 3]));
        Hex.Same("01 02 03", Assert.Single(test.Messages));
    }

    // 4.4 Annotated Closing a DVC, by the server: the client answers with the same PDU.
    [Fact]
    public void The_server_closes_a_channel()
    {
        var (channels, wire, test) = Started();
        var send = test.Send!;

        channels.Receive(Hex.Bytes(ClosePdu));

        Hex.Same(ClosePdu, wire.One());
        Assert.Equal(["closed"], test.TakeEvents());
        Assert.Null(test.Send);

        // A message for a channel that is gone goes nowhere.
        send(new byte[] { 1 });
        wire.Nothing();
        Assert.Single(wire.Log);

        // A close for a channel that is not open is not answered (MS-RDPEDYC 3.2.5.2).
        channels.Receive(Hex.Bytes(ClosePdu));
        channels.Receive(DvcServer.Close(99));
        wire.Nothing();
        Assert.Empty(test.TakeEvents());

        // The server may open the channel again.
        channels.Receive(Hex.Bytes(CreateRequest));
        Hex.Same(CreateResponse, wire.One());
        Assert.Equal(["opened"], test.TakeEvents());
    }

    // 4.4, by the client: an unsolicited close, which the server does not answer.
    [Fact]
    public void The_client_closes_a_channel()
    {
        var (channels, wire, test) = Started();

        Assert.True(channels.Close(test));

        Hex.Same(ClosePdu, wire.One());
        Assert.Equal(["closed"], test.TakeEvents());
        Assert.Null(test.Send);
        Assert.False(channels.Close(test));
        wire.Nothing();

        // Data the server sent before it heard of the close finds no channel.
        channels.Receive(DvcServer.Data(3, [1]));
        Assert.Empty(test.Messages);
    }

    [Fact]
    public void A_hundred_kilobytes_in_both_directions()
    {
        var (channels, wire, test) = Started();
        var down = Pattern(100_000);
        var up = Pattern(100_001).AsEnumerable().Reverse().ToArray();

        // From the server: Data First and 62 Data PDUs.
        var pdus = DvcServer.Message(3, down);
        Assert.Equal(63, pdus.Count);
        foreach (var pdu in pdus)
        {
            channels.Receive(pdu);
        }

        Hex.Same(down, Assert.Single(test.Messages));

        // To the server: no PDU longer than 1600 bytes, all but the last one filled to the brim, and the
        // pieces in order.
        test.Send!(up);
        var sent = wire.Take();
        Assert.Equal(63, sent.Length);
        Assert.All(sent.Take(62), pdu => Assert.Equal(1600, pdu.Length));
        Assert.Equal(0x28, sent[0][0]);
        Assert.Equal(100_001u, BitConverter.ToUInt32(sent[0], 2));
        Hex.Same(up, Assert.Single(DvcServer.Messages(sent, 3)));

        // Closed in the end.
        channels.Receive(DvcServer.Close(3));
        Hex.Same(ClosePdu, wire.One());
        Assert.Equal(["message 100000", "closed"], test.TakeEvents());
        Assert.Empty(wire.Log);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1590)]
    [InlineData(1591)]
    [InlineData(1594)]
    [InlineData(1596)]
    [InlineData(1597)]
    [InlineData(1598)]
    [InlineData(1599)]
    [InlineData(3194)]
    [InlineData(3195)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void Messages_of_any_length_leave_in_pdus_of_at_most_1600_bytes(int length)
    {
        var (_, wire, test) = Started();
        var message = Pattern(length);

        test.Send!(message);

        var sent = wire.Take();
        Hex.Same(message, Assert.Single(DvcServer.Messages(sent, 3)));

        // Up to 1590 bytes a message is a single Data PDU; a longer one begins with Data First
        // (MS-RDPEDYC 3.1.5.1.1), even where it would still fit into one PDU.
        Assert.Equal(length <= 1590 ? 0x30 : length <= 0xFFFF ? 0x24 : 0x28, sent[0][0]);
        Assert.All(sent.Skip(1), pdu => Assert.Equal(0x30, pdu[0]));
    }

    [Theory]
    [InlineData(0x03u, 0, "30 03")]
    [InlineData(0xFFu, 0, "30 ff")]
    [InlineData(0x100u, 1, "31 00 01")]
    [InlineData(0xFFFFu, 1, "31 ff ff")]
    [InlineData(0x10000u, 2, "32 00 00 01 00")]
    [InlineData(0xFFFFFFFFu, 2, "32 ff ff ff ff")]
    public void A_channel_id_takes_as_many_bytes_as_it_needs(uint id, int requestWidth, string header)
    {
        var (channels, wire, test) = Started(id, requestWidth);

        test.Send!(new byte[] { 0xAA, 0xBB });
        Hex.Same(header + " aa bb", wire.One());

        // A long message: every PDU carries the id in the same width and is filled to 1600 bytes.
        test.Send!(Pattern(5000));
        var sent = wire.Take();
        Assert.All(sent.Take(sent.Length - 1), pdu => Assert.Equal(1600, pdu.Length));
        Hex.Same(Pattern(5000), Assert.Single(DvcServer.Messages(sent, id)));

        channels.Receive(DvcServer.Close(id, requestWidth));
        Hex.Same("4" + header[1..], wire.One());
    }

    // The server may write a channel id and a length wider than they need to be.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void Every_width_of_channel_id_and_length_is_understood(int idWidth, int lengthWidth)
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        channels.Register(test);
        channels.Receive(DvcServer.Capabilities(2));
        wire.Take();

        // The answer to the create request carries the id in the width the request had.
        channels.Receive(DvcServer.Create(7, "testdvc", idWidth));
        Hex.Same((byte[])[(byte)(0x10 | idWidth), .. DvcServer.Variable(7, idWidth), 0, 0, 0, 0], wire.One());

        // A message in two pieces, one in a single piece, and one whose Data First holds all of it.
        channels.Receive(DvcServer.DataFirst(7, 200, Pattern(150), idWidth, lengthWidth));
        channels.Receive(DvcServer.Data(7, Pattern(200).AsSpan(150), idWidth));
        channels.Receive(DvcServer.Data(7, [9, 8, 7], idWidth));
        channels.Receive(DvcServer.DataFirst(7, 5, [1, 2, 3, 4, 5], idWidth, lengthWidth));

        Assert.Equal(3, test.Messages.Count);
        Hex.Same(Pattern(200), test.Messages[0]);
        Hex.Same("09 08 07", test.Messages[1]);
        Hex.Same("01 02 03 04 05", test.Messages[2]);

        channels.Receive(DvcServer.Close(7, idWidth));
        Hex.Same("40 07", wire.One());
        Assert.Empty(wire.Log);
    }

    [Fact]
    public void The_fourth_width_is_none()
    {
        var (channels, wire, test) = Started();

        // cbId = 3 in a create request, in data and in a close; Len = 3 in a Data First.
        channels.Receive([0x13, 0x03, 0x00, 0x00, 0x00, (byte)'x', 0x00]);
        channels.Receive([0x33, 0x03, 0x00, 0x00, 0x00, 0x01]);
        channels.Receive([0x43, 0x03, 0x00, 0x00, 0x00]);
        channels.Receive([0x2C, 0x03, 0x05, 0x00, 0x00, 0x00, 0x01]);

        wire.Nothing();
        Assert.Empty(test.TakeEvents());
        Assert.Equal(4, wire.Log.Count);
    }

    [Fact]
    public void Truncated_and_unknown_messages_are_dropped()
    {
        var (channels, wire, test) = Started(0x12345, 2);

        // Each kind of message, and how many bytes it takes before its fixed fields are all there.
        (byte[] Pdu, int Fixed)[] examples =
        [
            (Hex.Bytes(CapabilitiesRequest), 4),
            (DvcServer.Create(0x23456, "testdvc", 2), 5),
            (DvcServer.DataFirst(0x12345, 100000, [1, 2, 3], 2, 2), 9),
            (DvcServer.Data(0x12345, [1, 2, 3], 2), 5),
            (DvcServer.Close(0x12345, 2), 5),
        ];

        var dropped = 0;
        foreach (var (pdu, minimum) in examples)
        {
            for (var length = 0; length < minimum; length++)
            {
                channels.Receive(pdu.AsSpan(0, length));
                dropped++;
            }
        }

        wire.Nothing();
        Assert.Empty(test.TakeEvents());
        Assert.Equal(dropped, wire.Log.Count);

        // A create request cut inside its name can be answered, and is: with a refusal.
        var create = DvcServer.Create(0x23456, "testdvc", 2);
        for (var length = 5; length < create.Length; length++)
        {
            channels.Receive(create.AsSpan(0, length));
            Hex.Same("12 56 34 02 00 01 00 00 c0", wire.One());
        }

        // Commands nobody defined, and the two of the soft sync this client has no use for.
        wire.Log.Clear();
        foreach (var command in new byte[] { 0x00, 0x80, 0x90, 0xA0, 0xF0 })
        {
            channels.Receive([command, 0x03, 0x00, 0x00, 0x00, 0x00]);
        }

        wire.Nothing();
        Assert.Equal(5, wire.Log.Count);

        // The channel is as it was.
        channels.Receive(DvcServer.Data(0x12345, [4, 2], 2));
        Hex.Same("04 02", Assert.Single(test.Messages));
    }

    [Fact]
    public void Data_that_does_not_fit_its_message_is_dropped()
    {
        var (channels, wire, test) = Started();

        // More in the first piece than the message is long.
        channels.Receive(DvcServer.DataFirst(3, 2, [1, 2, 3]));

        // More in a later piece than is left of the message.
        channels.Receive(DvcServer.DataFirst(3, 4, [1, 2]));
        channels.Receive(DvcServer.Data(3, [3, 4, 5]));

        // A new message before the one under way was finished.
        channels.Receive(DvcServer.DataFirst(3, 4, [1, 2]));
        channels.Receive(DvcServer.DataFirst(3, 3, [7, 8]));
        channels.Receive(DvcServer.Data(3, [9]));

        // Data for channels that are not open.
        channels.Receive(DvcServer.Data(4, [1]));
        channels.Receive(DvcServer.DataFirst(4, 100, [1]));

        Hex.Same("07 08 09", Assert.Single(test.Messages));
        Assert.Equal(5, wire.Log.Count);
        wire.Nothing();
    }

    [Fact]
    public void A_message_longer_than_16_MB_passes_by_whole()
    {
        var (channels, wire, test) = Started();

        // Announced with a byte too many, and then really sent: its pieces must not be taken for messages of
        // their own.
        var total = DynamicChannels.MaxMessageLength + 1;
        var first = DvcServer.DataFirst(3, (uint)total, new byte[1594], 0, 2);
        var full = DvcServer.Data(3, new byte[1598]);
        var rest = (total - 1594) % 1598;
        var last = DvcServer.Data(3, new byte[rest]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        channels.Receive(first);
        for (var i = 0; i < (total - 1594) / 1598; i++)
        {
            channels.Receive(full);
        }

        channels.Receive(last);

        // Nothing of it was kept, not even for a moment.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
        Assert.Empty(test.Messages);
        Assert.Single(wire.Log);

        // What comes after it is a message again.
        channels.Receive(DvcServer.Data(3, [1, 2, 3]));
        Hex.Same("01 02 03", Assert.Single(test.Messages));

        // The longest message that is taken.
        var longest = new byte[DynamicChannels.MaxMessageLength];
        longest[^1] = 0x55;
        channels.Receive(DvcServer.DataFirst(3, (uint)longest.Length, longest.AsSpan(0, 1594), 0, 2));
        for (var sent = 1594; sent < longest.Length; sent += 1598)
        {
            channels.Receive(DvcServer.Data(3, longest.AsSpan(sent, Math.Min(1598, longest.Length - sent))));
        }

        Assert.Equal(2, test.Messages.Count);
        Assert.Equal(longest.Length, test.Messages[1].Length);
        Assert.Equal(0x55, test.Messages[1][^1]);
    }

    [Fact]
    public void An_announced_length_reserves_nothing_until_the_bytes_arrive()
    {
        var (channels, _, test) = Started();
        var before = GC.GetAllocatedBytesForCurrentThread();

        // A server that announces 16 MB a thousand times and sends a byte each time.
        for (var i = 0; i < 1000; i++)
        {
            channels.Receive(DvcServer.DataFirst(3, DynamicChannels.MaxMessageLength, [1], 0, 2));
        }

        // A thousand buffers of 64 KB at the most, and nothing like a thousand times 16 MB.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1000 * 80 * 1024);
        Assert.Empty(test.Messages);
    }

    [Fact]
    public void A_create_request_that_cannot_be_served_is_refused()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        channels.Register(test);
        channels.Receive(DvcServer.Capabilities(2));
        wire.Take();

        // A name without its terminator, no name at all, a name longer than any listener's.
        channels.Receive([0x10, 0x05, (byte)'t', (byte)'e']);
        Hex.Same("10 05 01 00 00 c0", wire.One());
        channels.Receive([0x10, 0x05, 0x00]);
        Hex.Same("10 05 01 00 00 c0", wire.One());
        channels.Receive(DvcServer.Create(5, new string('n', 257)));
        Hex.Same("10 05 01 00 00 c0", wire.One());

        // The name is compared as it is written.
        channels.Receive(DvcServer.Create(5, "TESTDVC"));
        Hex.Same("10 05 01 00 00 c0", wire.One());

        // A listener serves one channel at a time.
        channels.Receive(DvcServer.Create(5, "testdvc"));
        Hex.Same("10 05 00 00 00 00", wire.One());
        channels.Receive(DvcServer.Create(6, "testdvc"));
        Hex.Same("10 06 01 00 00 c0", wire.One());

        Assert.Equal(["opened"], test.TakeEvents());
        Assert.Equal(5, wire.Log.Count);
    }

    [Fact]
    public void An_id_the_server_uses_again_closes_what_had_it()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var first = new RecordingChannel("first");
        var second = new RecordingChannel("second");
        channels.Register(first);
        channels.Register(second);
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(3, "first"));
        channels.Receive(DvcServer.DataFirst(3, 10, [1, 2]));
        wire.Take();

        // The server would not hand out an id twice: it has forgotten the first channel.
        channels.Receive(DvcServer.Create(3, "second"));
        Hex.Same("10 03 00 00 00 00", wire.One());
        Assert.Equal(["opened", "closed"], first.TakeEvents());
        Assert.Equal(["opened"], second.TakeEvents());
        Assert.Null(first.Send);

        // Nothing of the half message of the first channel reaches the second.
        channels.Receive(DvcServer.Data(3, [5, 6]));
        Hex.Same("05 06", Assert.Single(second.Messages));

        // The same listener under the same id: closed and opened again.
        channels.Receive(DvcServer.Create(3, "second"));
        Hex.Same("10 03 00 00 00 00", wire.One());
        Assert.Equal(["message 2", "closed", "opened"], second.TakeEvents());
        second.Send!(new byte[] { 1 });
        Hex.Same("30 03 01", wire.One());
    }

    // FreeRDP's client answers with its capabilities when a server opens a channel without having asked.
    [Fact]
    public void A_server_that_opens_channels_without_asking_for_capabilities_gets_them_anyway()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        channels.Register(new RecordingChannel("testdvc"));

        channels.Receive(Hex.Bytes(CreateRequest));

        var sent = wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same(CapabilitiesResponse, sent[0]);
        Hex.Same(CreateResponse, sent[1]);

        // Asked later after all, the client answers as usual.
        channels.Receive(DvcServer.Capabilities(1));
        Hex.Same("50 00 01 00", wire.One());
    }

    [Fact]
    public void The_session_ending_closes_every_channel()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var first = new RecordingChannel("first");
        var second = new RecordingChannel("second");
        var third = new RecordingChannel("third");
        channels.Register(first);
        channels.Register(second);
        channels.Register(third);
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(1, "first"));
        channels.Receive(DvcServer.Create(2, "second"));
        wire.Take();

        channels.Closed();

        Assert.Equal(["opened", "closed"], first.TakeEvents());
        Assert.Equal(["opened", "closed"], second.TakeEvents());
        Assert.Empty(third.TakeEvents());
        Assert.Null(first.Send);
        Assert.Equal(0, channels.Version);
        wire.Nothing();

        // The same object serves the next session.
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(1, "third"));
        Assert.Equal(2, wire.Take().Length);
        Assert.Equal(["opened"], third.TakeEvents());
    }

    private sealed class Faulty : DynamicChannel
    {
        public override string Name => "faulty";

        public override void Receive(ReadOnlySpan<byte> message) => throw new InvalidOperationException("cannot read");

        public override void Opened() => throw new InvalidOperationException("cannot open");

        public override void Closed() => throw new InvalidOperationException("cannot close");
    }

    [Fact]
    public void A_listener_that_throws_is_reported_and_takes_nothing_with_it()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        channels.Register(new Faulty());
        channels.Register(test);
        channels.Receive(DvcServer.Capabilities(2));

        channels.Receive(DvcServer.Create(1, "faulty"));
        channels.Receive(DvcServer.Data(1, [1]));
        channels.Receive(DvcServer.Close(1));
        channels.Receive(DvcServer.Create(2, "testdvc"));
        channels.Receive(DvcServer.Data(2, [1]));

        Assert.Equal(["cannot open", "cannot read", "cannot close"], wire.Log.Select(line => line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]));
        Assert.Equal(["opened", "message 1"], test.TakeEvents());
        Assert.Equal(4, wire.Take().Length);
    }

    [Fact]
    public void A_listener_logs_through_the_channel_it_is_registered_with()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var display = new DisplayControlChannel();
        var own = new List<string>();
        var other = new OwnLogChannel(own);
        channels.Register(display);
        channels.Register(other);

        display.Receive([1, 2, 3]);
        other.Receive([1, 2, 3]);

        // A listener without a log gets the one of the static channel; one that has its own keeps it.
        Assert.StartsWith(DisplayControlChannel.ChannelName + ": ", Assert.Single(wire.Log));
        Assert.Equal("own: got 3", Assert.Single(own));
    }

    private sealed class OwnLogChannel : DynamicChannel
    {
        public OwnLogChannel(List<string> log)
        {
            Log = log.Add;
        }

        public override string Name => "own";

        public override void Receive(ReadOnlySpan<byte> message) => Report("got " + message.Length);
    }

    [Fact]
    public void Listeners_need_a_name_of_their_own()
    {
        var channels = new DynamicChannels();
        channels.Register(new RecordingChannel("testdvc"));

        Assert.Throws<ArgumentException>(() => channels.Register(new RecordingChannel("testdvc")));
        Assert.Throws<ArgumentException>(() => channels.Register(new RecordingChannel("")));
        Assert.Throws<ArgumentException>(() => channels.Register(new RecordingChannel("a\0b")));
        Assert.Throws<ArgumentException>(() => channels.Register(new RecordingChannel("Grüße €")));
        Assert.Throws<ArgumentException>(() => channels.Register(new RecordingChannel(new string('n', 257))));
        Assert.Throws<ArgumentNullException>(() => channels.Register(null!));
        Assert.Throws<ArgumentNullException>(() => channels.Close(null!));
        channels.Register(new RecordingChannel(new string('n', 256)));
    }

    [Fact]
    public void The_options_ask_for_no_compression()
    {
        var channels = new DynamicChannels();

        Assert.Equal("drdynvc", channels.Name);
        Assert.Equal(0xC0000000, channels.Options);
    }

    // The listener's Send is called from several threads at once while the session's thread takes fragmented
    // data for the same channel. The fragments of one message must stay together, and nothing may hang.
    [Fact]
    public async Task Sends_from_many_threads_do_not_mix_their_fragments()
    {
        const int Workers = 6;
        const int Rounds = 150;
        var channels = new DynamicChannels();
        var sent = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        var test = new RecordingChannel("testdvc");
        channels.Send = message => sent.Enqueue(message.ToArray());
        channels.Register(test);
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(3, "testdvc"));
        sent.Clear();

        var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                // Every message says who sent it, in every byte.
                test.Send?.Invoke(Enumerable.Repeat((byte)(worker + 1), 2000 + worker * 700).ToArray());
            }
        })).ToArray();
        var session = Task.Run(() =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var pdu in DvcServer.Message(3, Pattern(5000)))
                {
                    channels.Receive(pdu);
                }
            }
        });

        await Task.WhenAll([.. workers, session]).WaitAsync(TimeSpan.FromSeconds(60));

        var messages = DvcServer.Messages(sent, 3);
        Assert.Equal(Workers * Rounds, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(2000 + (message[0] - 1) * 700, message.Length);
            Assert.All(message, value => Assert.Equal(message[0], value));
        });
        Assert.Equal(Rounds, test.Messages.Count);
        Assert.All(test.Messages, message => Hex.Same(Pattern(5000), message));
    }
}
