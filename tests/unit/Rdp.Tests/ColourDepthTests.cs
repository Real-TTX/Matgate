using System.Buffers.Binary;
using Matgate.Display.Rdp.Channels;

namespace Matgate.Display.Rdp.Tests;

// What the client tells a server about colours and looks. Full colour - every session until "fast" came - has to
// stay what it was to the byte: the three messages below were taken from the client as it was before (commit
// 20f88c9) with the same options. The fastest response asks for 16 bits a pixel and a desktop without what only
// costs pictures, and confirms the depth the server settled on.
public sealed class ColourDepthTests
{
    // GCC client data: the core block (TS_UD_CS_CORE, 216 bytes), cluster, security and the four channels of a
    // session of Matgate's.
    private const string FullColourClientData =
        "01C0D800040008000005200301CA03AA07040000614A00004D0061007400670061007400650000000000000000000000" +
        "000000000000000004000000000000000C00000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000000000000001CA01000000000018000F00" +
        "6F0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000006000000000004C00C00000000000000000002C00C000000000000000000" +
        "03C0380004000000636C697072647200000020C0647264796E766300000000C0726470736E640000000000C072647064" +
        "72000000000000C0";

    // The Client Info (TS_INFO_PACKET with its security header), before the client knows its own address.
    private const string FullColourClientInfo =
        "40000000000000007B010300000008000400000000000000640065006D006F0000007000770000000000000002001000" +
        "30002E0030002E0030002E0030000000400043003A005C00570069006E0064006F00770073005C005300790073007400" +
        "65006D00330032005C006D007300740073006300610078002E0064006C006C0000000000000055005400430000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000055005400430000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000800100000000";

    // The Confirm Active PDU's body for a desktop of 1280 x 800, whatever depth the server settled on.
    private const string FullColourConfirmActive =
        "00000000EA030600B4014D53545343001200000001001800010003000002000000001504000000000000000002001C00" +
        "200001000100010000052003000001000100000801000000030058000000000000000000000000000000000000000000" +
        "010014000000010000002A00010101010100000000000000000000000000010000000000000000000000000000000000" +
        "000000000000000000000000000000001300280000000003580200005802000000040000000000000000000000000000" +
        "000000000000000008000A000100200020000D0058003D0100000704000004000000000000000C000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000F00080000000000100034000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000011000C00000000000000000014000C0000000000400600000C00" +
        "08000100000009000800000000000E0008000100000005000C00000000000200020007000C0000000000000000000A00" +
        "0800060000001A000800000080001B0006000300";

    // Where the core block has highColorDepth; supportedColorDepths and earlyCapabilityFlags follow it.
    private const int HighColorDepth = 140;

    [Fact]
    public void FullColourIsAskedForByteForByteAsBefore()
    {
        var client = Activated(Client(Options()));

        Assert.Equal(FullColourClientData, Convert.ToHexString(client.ClientData()));
        Assert.Equal(FullColourClientInfo, Convert.ToHexString(client.ClientInfo()));
        // 32 bits in the bitmap capability, as always - also where a server limited by policy settled on fewer.
        foreach (var serverDepth in new[] { 32, 24, 16, 15, 8 })
        {
            Assert.Equal(FullColourConfirmActive, Convert.ToHexString(client.ConfirmActiveBody(serverDepth)));
        }
    }

    [Fact]
    public void FewerColoursAreSixteenBitsWithoutThirtyTwoWanted()
    {
        var data = Client(Fewer()).ClientData();

        // highColorDepth 16; supportedColorDepths 24, 16 and 15, should the server not want 16; the early
        // capability flags without RNS_UD_CS_WANT_32BPP_SESSION. Full colour has 24, 32 among the depths, and
        // "32 bits wanted".
        Assert.Equal("10000700" + "6D00", Convert.ToHexString(data, HighColorDepth, 6));
        Assert.Equal("18000F00" + "6F00", FullColourClientData.Substring(HighColorDepth * 2, 12));
        Assert.Equal(new[] { HighColorDepth, HighColorDepth + 2, HighColorDepth + 4 }, Differences(Convert.FromHexString(FullColourClientData), data));
    }

    [Fact]
    public void TheFastestResponseAsksTheDesktopToLeaveOutWhatOnlyCostsPictures()
    {
        var info = Client(Fewer()).ClientInfo();
        var full = Convert.FromHexString(FullColourClientInfo);

        // performanceFlags, right before the length of the (absent) auto-reconnect cookie: smoothed fonts and
        // desktop composition (0x180) for full colour; no wallpaper, window contents while dragging, menu
        // animations, visual styles, cursor shadow or blinking, and neither of the two (0x6F) for the fastest.
        Assert.Equal(0x180u, BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(full.Length - 6)));
        Assert.Equal(0x6Fu, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(info.Length - 6)));
        Assert.Equal(full.Length, info.Length);
        Assert.All(Differences(full, info), offset => Assert.InRange(offset, full.Length - 6, full.Length - 3));
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(15, 15)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(8, 8)]
    [InlineData(0, 16)]
    [InlineData(12, 16)]
    public void FewerColoursConfirmTheDepthTheServerSettledOn(int serverDepth, int confirmed)
    {
        var body = Activated(Client(Fewer())).ConfirmActiveBody(serverDepth);
        var full = Convert.FromHexString(FullColourConfirmActive);

        Assert.Equal(32, BitmapDepth(full));
        Assert.Equal(confirmed, BitmapDepth(body));
        // Nothing else differs from full colour.
        Assert.Equal(full.Length, body.Length);
        Assert.All(Differences(full, body), offset => Assert.InRange(offset, 48, 49));
    }

    [Theory]
    [InlineData(32, 24, 0x000F, 0x006F)]
    [InlineData(64, 24, 0x000F, 0x006F)]
    [InlineData(24, 24, 0x0007, 0x006D)]
    [InlineData(16, 16, 0x0007, 0x006D)]
    [InlineData(15, 15, 0x0007, 0x006D)]
    [InlineData(8, 16, 0x0007, 0x006D)]
    public void TheColourFieldsOfTheCoreData(int asked, int highColorDepth, int supportedColorDepths, int earlyCapabilityFlags)
    {
        Assert.Equal((highColorDepth, supportedColorDepths, earlyCapabilityFlags), RdpClient.ColourFields(asked));
    }

    // As RdpBackend makes them - without the two options of the fastest response.
    private static RdpOptions Options() => new()
    {
        Host = "rdp.example",
        UserName = "demo",
        Password = "pw",
        Width = 1280,
        Height = 800,
        KeyboardLayout = 0x407,
        Sound = true,
    };

    // What ScreenQuality.Fastest asks for.
    private static RdpOptions Fewer() => new()
    {
        Host = "rdp.example",
        UserName = "demo",
        Password = "pw",
        Width = 1280,
        Height = 800,
        KeyboardLayout = 0x407,
        Sound = true,
        ColorDepth = 16,
        PerformanceFlags = 0x6F,
    };

    // With the channels a session of Matgate's has (RdpBackend): they are part of the client data.
    private static RdpClient Client(RdpOptions options)
    {
        var client = new RdpClient(options, new Handler());
        client.AddChannel(new ClipboardChannel());
        var dynamic = new DynamicChannels();
        dynamic.Register(new DisplayControlChannel());
        client.AddChannel(dynamic);
        client.AddChannel(new SoundChannel());
        client.AddChannel(new RdpdrChannel());
        return client;
    }

    // What the server's Demand Active sets before the client confirms it: the desktop's size.
    private static RdpClient Activated(RdpClient client)
    {
        typeof(RdpClient).GetProperty(nameof(RdpClient.Width))!.SetValue(client, 1280);
        typeof(RdpClient).GetProperty(nameof(RdpClient.Height))!.SetValue(client, 800);
        return client;
    }

    // preferredBitsPerPixel of the bitmap capability set in a Confirm Active body.
    private static int BitmapDepth(byte[] body)
    {
        // shareId, originatorId, lengthSourceDescriptor, lengthCombinedCapabilities, the source; then the
        // number of sets and a pad.
        var at = 10 + BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
        at += 4;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            if (type == 0x0002)
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 4));
            }

            at += BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 2));
        }

        throw new Xunit.Sdk.XunitException("The Confirm Active has no bitmap capability.");
    }

    private static int[] Differences(byte[] before, byte[] after) =>
        Enumerable.Range(0, Math.Min(before.Length, after.Length)).Where(i => before[i] != after[i]).ToArray();

    private sealed class Handler : IRdpHandler
    {
        public void Updated(int x, int y, int width, int height)
        {
        }

        public void Resized(int width, int height)
        {
        }

        public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
        {
        }

        public void CursorHidden()
        {
        }

        public void CursorDefault()
        {
        }
    }
}
