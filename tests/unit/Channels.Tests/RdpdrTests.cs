namespace Matgate.Display.Rdp.Channels.Tests;

// The device redirection channel without devices: the annotated examples of MS-RDPEFS section 4, whole
// handshakes, the bytes FreeRDP 3.15.0's own rdpdr client sends in the same situation, and random input.
public class RdpdrTests
{
    // 4.2 Server Device Announce Response: device 1, STATUS_SUCCESS.
    private const string DeviceReply = "72 44 72 64 01 00 00 00 00 00 00 00";

    // 4.3 Server Announce Request: version 1.12, client id 1.
    private const string ServerAnnounce = "72 44 6e 49 01 00 0c 00 01 00 00 00";

    // 4.4 Client Announce Reply: version 1.12, client id 1 (the server's, as from version 12 on).
    private const string ClientAnnounceReply = "72 44 43 43 01 00 0c 00 01 00 00 00";

    // 4.5 Client Name Request: Unicode, code page 0, 30 bytes of "TSDEV-SELFHOST" and its terminator.
    private const string ClientNameRequest = """
        72 44 4e 43 01 00 00 00 00 00 00 00 1e 00 00 00
        54 00 53 00 44 00 45 00 56 00 2d 00 53 00 45 00
        4c 00 46 00 48 00 4f 00 53 00 54 00 00 00
        """;

    // 4.6 Server User Logged On.
    private const string UserLoggedOn = "72 44 4c 55";

    // 4.7 Server Client ID Confirm: version 1.12, client id 1.
    private const string ClientIdConfirm = "72 44 43 43 01 00 0c 00 01 00 00 00";

    // 4.8 Server Core Capability Request: general (version 2, protocol 1.12, ioCode1 0xffff, extendedPDU 7,
    // two special devices), printer, port, drive (version 2) and smart card.
    private const string ServerCapabilities = """
        72 44 50 53 05 00 00 00 01 00 2c 00 02 00 00 00
        02 00 00 00 00 00 00 00 01 00 0c 00 ff ff 00 00
        00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00
        02 00 00 00 02 00 08 00 01 00 00 00 03 00 08 00
        01 00 00 00 04 00 08 00 02 00 00 00 05 00 08 00
        01 00 00 00
        """;

    // 4.12 Server Create Drive Request for device 3 - a device this client never announced.
    private const string CreateRequest = """
        72 44 52 49 03 00 00 00 00 00 00 00 01 00 00 00
        00 00 00 00 00 00 00 00 80 00 00 00 00 00 00 00
        00 00 00 00 00 00 00 00 07 00 00 00 01 00 00 00
        01 00 00 00 00 00 00 00
        """;

    // What this client answers 4.8 with: the general set alone - version 2, osType and osVersion 0, protocol
    // 1.12, ioCode1 as the server has it, ioCode2 0, extendedPDU 7, extraFlags1 ENABLE_ASYNCIO, no special
    // devices. FreeRDP 3.15.0 without devices answers the same request with these bytes.
    private const string ClientCapabilities = """
        72 44 50 43 01 00 00 00 01 00 2c 00 02 00 00 00
        00 00 00 00 00 00 00 00 01 00 0c 00 ff ff 00 00
        00 00 00 00 07 00 00 00 01 00 00 00 00 00 00 00
        00 00 00 00
        """;

    // An empty Client Device List Announce Request.
    private const string EmptyDeviceList = "72 44 41 44 00 00 00 00";

    // FreeRDP's Client Name Request for "Matgate".
    private const string MatgateName = "72 44 4e 43 01 00 00 00 00 00 00 00 10 00 00 00 4d 00 61 00 74 00 67 00 61 00 74 00 65 00 00 00";

    private static (RdpdrChannel Channel, Wire Wire) Create(string name = "Matgate", bool announceEmpty = true)
    {
        var channel = new RdpdrChannel(name, announceEmpty);
        return (channel, new Wire(channel));
    }

    private static string[] Texts(byte[][] messages) => messages.Select(message => Hex.Text(message)).ToArray();

    // 4.3 to 4.8 and 4.6, in the order of 4.1: announce, name, capabilities, client id, user logged on.
    [Fact]
    public void The_handshake_as_in_the_specification()
    {
        var (channel, wire) = Create("TSDEV-SELFHOST");

        channel.Receive(Hex.Bytes(ServerAnnounce));
        var sent = wire.Take();
        Assert.Equal(2, sent.Length);
        Hex.Same(ClientAnnounceReply, sent[0]);
        Hex.Same(ClientNameRequest, sent[1]);
        Assert.Equal(12, channel.VersionMinor);

        channel.Receive(Hex.Bytes(ServerCapabilities));
        Hex.Same(ClientCapabilities, wire.One());
        Assert.False(channel.IsReady);

        channel.Receive(Hex.Bytes(ClientIdConfirm));
        wire.Nothing();
        Assert.True(channel.IsReady);
        Assert.Equal(1u, channel.ClientId);

        channel.Receive(Hex.Bytes(UserLoggedOn));
        Hex.Same(EmptyDeviceList, wire.One());

        // 4.2 and 4.12: answers about and requests for devices that were never announced are ignored
        // (MS-RDPEFS 3.1.5.2).
        channel.Receive(Hex.Bytes(DeviceReply));
        channel.Receive(Hex.Bytes(CreateRequest));
        wire.Nothing();
        Assert.Equal(2, wire.Log.Count);

        // A second "logged on" does not announce the list twice.
        channel.Receive(Hex.Bytes(UserLoggedOn));
        wire.Nothing();
    }

    // The order Windows uses after a logon: the user logged on may come before the capabilities.
    [Fact]
    public void Logged_on_before_the_capabilities()
    {
        var (channel, wire) = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        wire.Take();

        channel.Receive(Hex.Bytes(UserLoggedOn));
        Hex.Same(EmptyDeviceList, wire.One());
        channel.Receive(Hex.Bytes(ServerCapabilities));
        Hex.Same(ClientCapabilities, wire.One());
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        wire.Nothing();
        Assert.True(channel.IsReady);
    }

    [Fact]
    public void The_empty_list_can_be_left_out_as_FreeRDP_and_IronRDP_do()
    {
        var (channel, wire) = Create(announceEmpty: false);

        channel.Receive(Hex.Bytes(ServerAnnounce));
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));

        Assert.Equal(3, wire.Take().Length);
        Assert.True(channel.IsReady);
    }

    [Theory]
    [InlineData(0x000D, "0d 00")]
    [InlineData(0x000C, "0c 00")]
    [InlineData(0x000E, "0d 00")]
    [InlineData(0xFFFF, "0d 00")]
    public void From_version_12_on_the_server_s_client_id_is_kept(int minor, string answered)
    {
        var (channel, wire) = Create();

        channel.Receive([0x72, 0x44, 0x6e, 0x49, 0x01, 0x00, (byte)minor, (byte)(minor >> 8), 0x78, 0x56, 0x34, 0x12]);

        Hex.Same("72 44 43 43 01 00 " + answered + " 78 56 34 12", wire.Take()[0]);
    }

    // Before version 12 the client draws its own id (MS-RDPEFS 3.2.5.1.3), and never 0, 0xffffffff or the
    // server's. (FreeRDP 3.15.0 echoes the server's here as well.)
    [Theory]
    [InlineData(0x000A, "0a 00")]
    [InlineData(0x0005, "05 00")]
    [InlineData(0x0007, "05 00")]
    [InlineData(0x0002, "02 00")]
    [InlineData(0x0000, "02 00")]
    public void Before_version_12_the_client_draws_its_id(int minor, string answered)
    {
        var (channel, wire) = Create();
        var draws = new Queue<uint>([0, 0xFFFFFFFF, 0x12345678, 0xCAFEF00D]);
        channel.RandomId = draws.Dequeue;

        channel.Receive([0x72, 0x44, 0x6e, 0x49, 0x01, 0x00, (byte)minor, (byte)(minor >> 8), 0x78, 0x56, 0x34, 0x12]);

        Hex.Same("72 44 43 43 01 00 " + answered + " 0d f0 fe ca", wire.Take()[0]);
        Assert.Empty(draws);

        // The id the server confirms is the one that counts.
        channel.Receive([0x72, 0x44, 0x43, 0x43, 0x01, 0x00, (byte)minor, 0x00, 0x0d, 0xf0, 0xfe, 0xca]);
        Assert.Equal(0xCAFEF00Du, channel.ClientId);
    }

    // A server of version 5 never says that the user is logged on: the devices are announced as soon as
    // capabilities and client id are settled (MS-RDPEFS 3.2.5.1.5) - and the same with a server that leaves
    // RDPDR_USER_LOGGEDON_PDU out of its capabilities.
    [Theory]
    [InlineData("72 44 6e 49 01 00 05 00 09 00 00 00", "72 44 50 53 01 00 00 00 01 00 28 00 01 00 00 00 02 00 00 00 00 00 00 00 01 00 05 00 ff ff 00 00 00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00")]
    [InlineData("72 44 6e 49 01 00 0d 00 07 00 00 00", "72 44 50 53 01 00 00 00 01 00 2c 00 02 00 00 00 02 00 00 00 00 00 00 00 01 00 0d 00 ff 3f 00 00 00 00 00 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")]
    public void Without_a_logon_message_the_list_follows_the_handshake(string announce, string capabilities)
    {
        var (channel, wire) = Create();
        channel.RandomId = () => 0x11111111;
        channel.Receive(Hex.Bytes(announce));
        var id = wire.Take()[0].AsSpan(8, 4).ToArray();

        channel.Receive(Hex.Bytes(capabilities));
        Assert.Single(wire.Take());
        channel.Receive([0x72, 0x44, 0x43, 0x43, 0x01, 0x00, 0x05, 0x00, .. id]);

        Hex.Same(EmptyDeviceList, wire.One());
        Assert.True(channel.IsReady);
    }

    [Fact]
    public void A_server_that_announces_itself_again_starts_over()
    {
        var (channel, wire) = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        wire.Take();
        Assert.True(channel.IsReady);

        // The session was reconnected (MS-RDPEFS 3.2.5.1.2): the whole handshake again, devices included.
        channel.Receive(Hex.Bytes("72 44 6e 49 01 00 0d 00 02 00 00 00"));
        Assert.False(channel.IsReady);
        Assert.Equal(2, wire.Take().Length);
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes("72 44 43 43 01 00 0d 00 02 00 00 00"));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        Assert.Equal([Hex.Text(Hex.Bytes(ClientCapabilities.Replace("01 00 0c 00 ff ff", "01 00 0d 00 ff ff"))), EmptyDeviceList], Texts(wire.Take()));
        Assert.Equal(2u, channel.ClientId);
    }

    [Fact]
    public void A_session_that_ends_forgets_the_handshake()
    {
        var (channel, wire) = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        wire.Take();

        channel.Closed();

        Assert.False(channel.IsReady);
        Assert.Equal(0u, channel.ClientId);
        Assert.Equal(0, channel.VersionMinor);

        // Without an announcement nothing is answered.
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        wire.Nothing();
        Assert.Equal(3, wire.Log.Count);
    }

    [Fact]
    public void Capabilities_are_answered_whatever_the_server_lists()
    {
        // No sets at all; a general set of version 1 (no SpecialTypeDeviceCap); sets of unknown types.
        string[] requests =
        [
            "72 44 50 53 00 00 00 00",
            "72 44 50 53 01 00 00 00 01 00 28 00 01 00 00 00 02 00 00 00 00 00 00 00 01 00 0c 00 ff 00 00 00 00 00 00 00 07 00 00 00 00 00 00 00 00 00 00 00",
            "72 44 50 53 02 00 00 00 09 00 0c 00 01 00 00 00 aa bb cc dd 07 00 08 00 01 00 00 00",
        ];
        string[] ioCodes = ["ff ff 00 00", "ff 00 00 00", "ff ff 00 00"];

        for (var i = 0; i < requests.Length; i++)
        {
            var (channel, wire) = Create();
            channel.Receive(Hex.Bytes(ServerAnnounce));
            wire.Take();

            channel.Receive(Hex.Bytes(requests[i]));

            var answer = wire.One();
            Assert.Equal(52, answer.Length);
            Hex.Same(ioCodes[i], answer.AsSpan(28, 4));
            Assert.Empty(wire.Log);
        }
    }

    [Fact]
    public void Everything_else_is_dropped_or_ignored()
    {
        var (channel, wire) = Create();
        channel.Receive(Hex.Bytes(ServerAnnounce));
        wire.Take();
        byte[][] messages =
        [
            [],
            [0x72, 0x44, 0x6e],
            [0x52, 0x50, 0x43, 0x50, 1, 2, 3, 4],                   // printer component: cache data
            [0x52, 0x50, 0x43, 0x55, 1, 2, 3, 4],                   // printer component: XPS mode
            [0x00, 0x00, 0x6e, 0x49, 1, 0, 12, 0, 1, 0, 0, 0],      // an unknown component
            [0x72, 0x44, 0xff, 0xff],                               // an unknown packet
            [0x72, 0x44, 0x41, 0x44, 0, 0, 0, 0],                   // the client's own device list
            [0x72, 0x44, 0x4e, 0x43, 1, 0, 0, 0],                   // the client's own name
            [0x72, 0x44, 0x50, 0x43, 0, 0, 0, 0],                   // the client's own capabilities
            [0x72, 0x44, 0x4d, 0x44, 1, 0, 0, 0, 3, 0, 0, 0],       // the client's own device list remove
            [0x72, 0x44, 0x43, 0x49, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0],   // an I/O completion: the client's
            [0x72, 0x44, 0x52, 0x49, 0xff, 0xff, 0xff, 0xff],       // an I/O request cut short
        ];

        foreach (var message in messages)
        {
            channel.Receive(message);
        }

        wire.Nothing();
        Assert.Equal(messages.Length, wire.Log.Count);

        // The handshake goes on as if nothing had come in between.
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        Assert.Equal([Hex.Text(Hex.Bytes(ClientCapabilities)), EmptyDeviceList], Texts(wire.Take()));
    }

    [Fact]
    public void The_examples_cut_short_are_dropped()
    {
        string[] examples = [ServerAnnounce, ServerCapabilities, ClientIdConfirm, UserLoggedOn, DeviceReply, CreateRequest];
        foreach (var example in examples.Select(Hex.Bytes))
        {
            var (channel, wire) = Create();
            channel.Receive(Hex.Bytes(ServerAnnounce));
            wire.Take();
            for (var length = 0; length < example.Length; length++)
            {
                channel.Receive(example.AsSpan(0, length));
            }

            // Every cut is one line in the log and nothing on the wire.
            wire.Nothing();
            Assert.Equal(example.Length, wire.Log.Count);
            Assert.False(channel.IsReady);
        }
    }

    [Fact]
    public void Random_input_changes_nothing_that_matters()
    {
        var random = new Random(5);
        var seeds = new[] { ServerAnnounce, ServerCapabilities, ClientIdConfirm, UserLoggedOn, DeviceReply, CreateRequest }.Select(Hex.Bytes).ToArray();
        var (channel, wire) = Create();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 30000; round++)
        {
            byte[] message;
            if (random.Next(4) == 0)
            {
                message = new byte[random.Next(0, 80)];
                random.NextBytes(message);
            }
            else
            {
                message = seeds[random.Next(seeds.Length)].ToArray();
                message[random.Next(message.Length)] = (byte)random.Next(256);
                message = random.Next(2) == 0 ? message[..random.Next(message.Length + 1)] : message;
            }

            channel.Receive(message);

            // Whatever goes out is one of the five messages a client of this kind has.
            foreach (var sent in wire.Take())
            {
                Assert.Equal(0x4472, BitConverter.ToUInt16(sent, 0));
                Assert.Contains(BitConverter.ToUInt16(sent, 2), new ushort[] { 0x4343, 0x434E, 0x4350, 0x4441 });
            }

            wire.Log.Clear();
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 200L * 1024 * 1024);

        // And afterwards a handshake from the start works.
        channel.Receive(Hex.Bytes(ServerAnnounce));
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        Assert.Equal(4, wire.Take().Length);
        Assert.True(channel.IsReady);
    }

    // ---- FreeRDP 3.15.0's rdpdr client, hosted the way the clipboard and display plugins were (see
    // FreeRdpCrossCheckTests), with no device configured and the client name "Matgate" -------------------------

    // Fed 4.3, 4.8, 4.7, 4.6, 4.12 and 4.2, it sent the Client Announce Reply of 4.4, the name request below,
    // the capabilities above - and nothing else: no device list, and no answer to the I/O request for an unknown
    // device (it reports ERROR_DEV_NOT_EXIST and drops the request).
    [Fact]
    public void The_handshake_is_the_one_FreeRDP_sends()
    {
        var (channel, wire) = Create(announceEmpty: false);

        channel.Receive(Hex.Bytes(ServerAnnounce));
        channel.Receive(Hex.Bytes(ServerCapabilities));
        channel.Receive(Hex.Bytes(ClientIdConfirm));
        channel.Receive(Hex.Bytes(UserLoggedOn));
        channel.Receive(Hex.Bytes(CreateRequest));
        channel.Receive(Hex.Bytes(DeviceReply));

        Assert.Equal(
            [Hex.Text(Hex.Bytes(ClientAnnounceReply)), Hex.Text(Hex.Bytes(MatgateName)), Hex.Text(Hex.Bytes(ClientCapabilities))],
            Texts(wire.Take()));
    }

    // A server of version 13 with ioCode1 0x3fff and no logged-on PDU: FreeRDP answered
    // "72 44 43 43 01 00 0d 00 07 00 00 00", the name, and the capabilities below.
    [Fact]
    public void A_version_13_server_gets_what_FreeRDP_sends()
    {
        var (channel, wire) = Create(announceEmpty: false);

        channel.Receive(Hex.Bytes("72 44 6e 49 01 00 0d 00 07 00 00 00"));
        channel.Receive(Hex.Bytes("72 44 50 53 01 00 00 00 01 00 2c 00 02 00 00 00 02 00 00 00 00 00 00 00 01 00 0d 00 ff 3f 00 00 00 00 00 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));

        Assert.Equal(
            [
                "72 44 43 43 01 00 0d 00 07 00 00 00",
                Hex.Text(Hex.Bytes(MatgateName)),
                "72 44 50 43 01 00 00 00 01 00 2c 00 02 00 00 00 00 00 00 00 00 00 00 00 01 00 0d 00 ff 3f 00 00 00 00 00 00 07 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00",
            ],
            Texts(wire.Take()));
    }

    [Fact]
    public void The_options_and_the_name()
    {
        var channel = new RdpdrChannel();

        Assert.Equal("rdpdr", channel.Name);
        Assert.Equal("Matgate", channel.ComputerName);

        // FreeRDP announces 0xc0800000; without the compression this client's core does not do.
        Assert.Equal(0xC0000000, channel.Options);

        Assert.Throws<ArgumentNullException>(() => new RdpdrChannel(null!));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel(""));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel("a\0b"));
        Assert.Throws<ArgumentException>(() => new RdpdrChannel(new string('n', 256)));
        Assert.Equal("Rechner-Größe", new RdpdrChannel("Rechner-Größe").ComputerName);
    }

    [Fact]
    public void A_send_that_fails_is_reported()
    {
        var channel = new RdpdrChannel();
        var log = new List<string>();
        channel.Log = log.Add;

        channel.Receive(Hex.Bytes(ServerAnnounce));
        Assert.Equal(2, log.Count);

        channel.Send = _ => throw new IOException("gone");
        channel.Receive(Hex.Bytes(ServerCapabilities));
        Assert.Equal(3, log.Count);
    }
}
