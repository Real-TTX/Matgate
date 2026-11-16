namespace Matgate.Display.Rdp.Channels.Tests;

// The display control channel (MS-RDPEDISP). The specification has no annotated examples (section 4 reads
// "None"), so the bytes here are the ones FreeRDP 3.15.0's own client plugin reads and writes - see
// FreeRdpCrossCheckTests - and the fields are placed as sections 2.2.1.1, 2.2.2.1 and 2.2.2.2 lay them out.
public class DisplayControlTests
{
    // DISPLAYCONTROL_CAPS_PDU: type 5, 20 bytes, 16 monitors, area factors 8192 and 8192 (what Windows sends).
    private const string WindowsCapabilities = "05 00 00 00 14 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00";

    // DISPLAYCONTROL_MONITOR_LAYOUT_PDU for one primary monitor of 1920 x 1080 at 0,0: type 2, 56 bytes, entries
    // of 40 bytes, one entry; flags 1, left 0, top 0, width, height, physical size 0 x 0, orientation 0,
    // desktop and device scale factor 100.
    private const string Layout1920x1080 = """
        02 00 00 00 38 00 00 00 28 00 00 00 01 00 00 00
        01 00 00 00 00 00 00 00 00 00 00 00 80 07 00 00
        38 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00
        64 00 00 00 64 00 00 00
        """;

    private static byte[] Capabilities(uint monitors, uint factorA, uint factorB) =>
        [.. ClipServer.U32(5), .. ClipServer.U32(20), .. ClipServer.U32(monitors), .. ClipServer.U32(factorA), .. ClipServer.U32(factorB)];

    private static (DisplayControlChannel Display, List<byte[]> Sent, List<string> Log) Opened()
    {
        var display = new DisplayControlChannel();
        var sent = new List<byte[]>();
        var log = new List<string>();
        display.Send = message => sent.Add(message.ToArray());
        display.Log = log.Add;
        return (display, sent, log);
    }

    private static (int Width, int Height) Sent(List<byte[]> sent)
    {
        var layout = sent[^1];
        Assert.Equal(56, layout.Length);
        return ((int)BitConverter.ToUInt32(layout, 28), (int)BitConverter.ToUInt32(layout, 32));
    }

    [Fact]
    public void The_whole_way_through_the_dynamic_channel()
    {
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var display = new DisplayControlChannel();
        var ready = 0;
        display.Ready += () => ready++;
        channels.Register(display);

        // The server asks for the capabilities and opens the channel by its name.
        channels.Receive(DvcServer.Capabilities(3));
        channels.Receive(DvcServer.Create(7, "Microsoft::Windows::RDS::DisplayControl"));
        Assert.Equal(2, wire.Take().Length);
        Assert.False(display.IsReady);
        Assert.False(display.SetSize(1920, 1080));
        wire.Nothing();

        // It says what it takes ...
        channels.Receive(DvcServer.Data(7, Hex.Bytes(WindowsCapabilities)));
        Assert.True(display.IsReady);
        Assert.Equal(1, ready);
        Assert.Equal(16, display.MaxMonitors);
        Assert.Equal(8192, display.MaxAreaFactorA);
        Assert.Equal(8192, display.MaxAreaFactorB);
        Assert.Equal(16L * 8192 * 8192, display.MaxArea);
        Assert.Equal(8192, display.MaxWidth);
        Assert.Equal(8192, display.MaxHeight);

        // ... and the client asks for another size: one Data PDU on channel 7 with the layout in it.
        Assert.True(display.SetSize(1920, 1080));
        Hex.Same("30 07 " + Layout1920x1080, wire.One());

        // The server closes the channel: no more requests.
        channels.Receive(DvcServer.Close(7));
        wire.Take();
        Assert.False(display.IsReady);
        Assert.False(display.SetSize(1920, 1080));
        Assert.Equal(0, display.MaxWidth);
        wire.Nothing();
        Assert.Empty(wire.Log);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1921, 1080, 1920, 1080)]       // the width has to be even
    [InlineData(1919, 1079, 1918, 1079)]
    [InlineData(200, 200, 200, 200)]
    [InlineData(199, 199, 200, 200)]
    [InlineData(201, 201, 200, 201)]
    [InlineData(0, 0, 200, 200)]
    [InlineData(-5, -5, 200, 200)]
    [InlineData(8192, 8192, 8192, 8192)]
    [InlineData(8193, 8193, 8192, 8192)]
    [InlineData(int.MaxValue, int.MaxValue, 8192, 8192)]
    [InlineData(100, 5000, 200, 5000)]
    public void A_size_is_brought_within_what_the_protocol_allows(int width, int height, int sentWidth, int sentHeight)
    {
        var (display, sent, _) = Opened();
        display.Receive(Hex.Bytes(WindowsCapabilities));

        Assert.True(display.SetSize(width, height));

        Assert.Equal((sentWidth, sentHeight), Sent(sent));
        Assert.Equal((sentWidth, sentHeight), display.Fit(width, height));
    }

    // The area of all monitors together must not exceed MaxNumMonitors * MaxMonitorAreaFactorA *
    // MaxMonitorAreaFactorB (MS-RDPEDISP 2.2.2.1 and 3.2.5.2).
    [Theory]
    [InlineData(1u, 1920u, 1080u, 1920, 1080, 1920, 1080)]     // exactly the area
    [InlineData(1u, 1920u, 1080u, 3840, 2160, 1920, 1080)]     // four times the area: half of each side
    [InlineData(1u, 1920u, 1080u, 1922, 1080, 1920, 1079)]     // just over it
    [InlineData(1u, 1024u, 768u, 8192, 200, 3932, 200)]        // the height cannot shrink any further
    [InlineData(1u, 1024u, 768u, 200, 8192, 200, 3932)]        // the width cannot
    [InlineData(1u, 200u, 200u, 1920, 1080, 200, 200)]         // room for the smallest monitor only
    [InlineData(2u, 100u, 200u, 1920, 1080, 200, 200)]
    [InlineData(2u, 400u, 300u, 1920, 1080, 652, 367)]         // the proportions stay as far as whole pixels allow
    [InlineData(16u, 4096u, 2048u, 8192, 8192, 8192, 8192)]
    public void A_size_is_brought_within_the_area_the_server_takes(uint monitors, uint factorA, uint factorB, int width, int height, int sentWidth, int sentHeight)
    {
        var (display, sent, _) = Opened();
        display.Receive(Capabilities(monitors, factorA, factorB));

        Assert.True(display.SetSize(width, height));

        var (w, h) = Sent(sent);
        Assert.Equal((sentWidth, sentHeight), (w, h));
        Assert.True((long)w * h <= display.MaxArea);
    }

    [Fact]
    public void Whatever_is_asked_for_what_goes_out_is_a_size_the_server_can_take()
    {
        var random = new Random(11);
        for (var round = 0; round < 20000; round++)
        {
            var (display, sent, _) = Opened();
            var monitors = (uint)random.Next(1, 20);
            var factorA = (uint)random.Next(1, 9000);
            var factorB = (uint)random.Next(1, 9000);
            display.Receive(Capabilities(monitors, factorA, factorB));
            if ((long)monitors * factorA * factorB < 40000)
            {
                Assert.False(display.IsReady);
                continue;
            }

            Assert.True(display.SetSize(random.Next(-100, 20000), random.Next(-100, 20000)));
            var (width, height) = Sent(sent);

            Assert.InRange(width, 200, 8192);
            Assert.InRange(height, 200, 8192);
            Assert.Equal(0, width % 2);
            Assert.True((long)width * height <= display.MaxArea);
            Assert.InRange(display.MaxWidth, 200, 8192);
            Assert.InRange(display.MaxHeight, 200, 8192);
        }
    }

    [Fact]
    public void Limits_follow_from_the_area()
    {
        var (display, _, _) = Opened();
        Assert.Equal((0, 0, 0L), (display.MaxWidth, display.MaxHeight, display.MaxArea));

        // With room for 1024 x 768 a monitor can be 3932 wide when it is only 200 high, and the other way round.
        display.Receive(Capabilities(1, 1024, 768));
        Assert.Equal((3932, 3932, 786432L), (display.MaxWidth, display.MaxHeight, display.MaxArea));

        // Factors that multiply beyond 64 bits stay a number.
        display.Receive(Capabilities(uint.MaxValue, uint.MaxValue, uint.MaxValue));
        Assert.Equal((8192, 8192, long.MaxValue), (display.MaxWidth, display.MaxHeight, display.MaxArea));
        Assert.Equal(int.MaxValue, display.MaxMonitors);
    }

    [Fact]
    public void Capabilities_may_come_again()
    {
        var (display, sent, log) = Opened();
        var ready = 0;
        display.Ready += () => ready++;

        display.Receive(Capabilities(1, 1024, 768));
        display.Receive(Hex.Bytes(WindowsCapabilities));

        Assert.Equal(2, ready);
        Assert.True(display.SetSize(1920, 1080));
        Assert.Equal((1920, 1080), Sent(sent));
        Assert.Empty(log);
    }

    [Fact]
    public void Messages_that_are_not_capabilities_change_nothing()
    {
        var (display, sent, log) = Opened();
        var capabilities = Hex.Bytes(WindowsCapabilities);

        // Every truncation of the capabilities.
        for (var length = 0; length < capabilities.Length; length++)
        {
            display.Receive(capabilities.AsSpan(0, length));
        }

        // A length that says less than the fields need, or more than was sent.
        display.Receive(Hex.Bytes("05 00 00 00 13 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00"));
        display.Receive(Hex.Bytes("05 00 00 00 15 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00"));
        display.Receive(Hex.Bytes("05 00 00 00 ff ff ff ff 10 00 00 00 00 20 00 00 00 20 00 00"));

        // A monitor layout is the client's message; anything else is unknown.
        display.Receive(Hex.Bytes(Layout1920x1080));
        display.Receive(Hex.Bytes("07 00 00 00 08 00 00 00"));

        // An area that no monitor fits into.
        display.Receive(Capabilities(0, 8192, 8192));
        display.Receive(Capabilities(1, 199, 200));

        Assert.False(display.IsReady);
        Assert.False(display.SetSize(1920, 1080));
        Assert.Empty(sent);
        Assert.Equal(capabilities.Length + 7, log.Count);

        // More bytes than the length counts are not looked at.
        display.Receive([.. capabilities, 0xAA, 0xBB]);
        Assert.True(display.IsReady);
    }

    [Fact]
    public void A_handler_or_a_send_that_throws_is_reported()
    {
        var (display, _, log) = Opened();
        display.Ready += () => throw new InvalidOperationException("not ready for this");
        display.Receive(Hex.Bytes(WindowsCapabilities));
        Assert.True(display.IsReady);
        Assert.Contains("not ready for this", Assert.Single(log));

        display.Send = _ => throw new IOException("gone");
        Assert.False(display.SetSize(1920, 1080));
        Assert.Equal(2, log.Count);

        display.Send = null;
        Assert.False(display.SetSize(1920, 1080));
    }
}
