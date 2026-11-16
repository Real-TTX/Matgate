namespace Matgate.Display.Rdp.Channels.Tests;

// Random input into every Receive: nothing may escape, nothing may pile up, and the channel must still work
// afterwards. The buffers are built from pieces of real messages, so they get past the first checks and into
// the parts of the code that a server's mistakes or malice would reach.
public class RobustnessTests
{
    private const int Rounds = 30000;

    // A random buffer: noise, or a real message with bytes changed, cut off or run on.
    private static byte[] Mangled(Random random, IReadOnlyList<byte[]> seeds)
    {
        var kind = random.Next(4);
        if (kind == 0)
        {
            var noise = new byte[random.Next(0, 64)];
            random.NextBytes(noise);
            return noise;
        }

        var seed = seeds[random.Next(seeds.Count)];
        var message = seed.ToArray();
        for (var flips = random.Next(1, 4); flips > 0 && message.Length > 0; flips--)
        {
            message[random.Next(message.Length)] = (byte)random.Next(256);
        }

        return kind switch
        {
            1 => message,
            2 => message[..random.Next(0, message.Length + 1)],
            _ => [.. message, .. Enumerable.Range(0, random.Next(1, 40)).Select(_ => (byte)random.Next(256))],
        };
    }

    // Allocation over all rounds: a channel that kept what it was fed would need far more.
    private static void Bounded(long before, long limit) =>
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, limit);

    [Fact]
    public void Clipboard()
    {
        var random = new Random(1);
        byte[][] seeds =
        [
            ClipServer.Capabilities(0x3E),
            ClipServer.MonitorReady(),
            ClipServer.LongFormatList((13, null), (0xC0BC, "FileGroupDescriptorW")),
            ClipServer.ShortFormatList(true, (1, "x")),
            ClipServer.FormatListResponse(true),
            ClipServer.DataRequest(13),
            ClipServer.DataResponse([1, 2, 3, 4]),
            ClipServer.FileSizeRequest(1, 0, 5),
            ClipServer.FileRangeRequest(2, 0, 0x100000000, 100),
            ClipServer.FileResponse(1, ClipServer.U64(9)),
            ClipServer.Lock(4),
            ClipServer.Unlock(4),
            Hex.Resource("rdpeclip-4.5.4.txt"),
        ];
        var client = ClipClient.Started();
        client.Channel.AnnounceFormats([new(13, null), new(0xC0D0, "FileGroupDescriptorW")]);

        // The application answers what it is asked and asks things of its own, as a real one would.
        client.Channel.DataRequested += _ => client.Channel.SendData([1]);
        client.Channel.FileContentsRequested += request =>
        {
            if (request.IsSizeRequest)
            {
                client.Channel.SendFileSize(request.StreamId, 10);
            }
            else
            {
                client.Channel.SendFileContents(request.StreamId, new byte[Math.Min(request.Count, 16)]);
            }
        };
        client.Channel.RemoteFormats += formats => client.Channel.RequestData(formats.Count > 0 ? formats[0].Id : 13);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++)
        {
            client.Channel.Receive(Mangled(random, seeds));
            if (round % 100 == 0)
            {
                client.Wire.Take();
                client.Wire.Log.Clear();
                client.Events.Clear();
                client.Data.Clear();
                client.Files.Clear();
                client.FileRequests.Clear();
                client.Remote.Clear();
            }
        }

        Bounded(before, 400L * 1024 * 1024);

        // Still a working clipboard.
        client.Channel.Receive(ClipServer.Capabilities(0x3E));
        client.Channel.Receive(ClipServer.MonitorReady());
        client.Channel.Receive(ClipServer.LongFormatList((13, null)));
        client.Wire.Take();
        client.Data.Clear();
        client.Channel.RequestData(13);
        client.Channel.Receive(ClipServer.DataResponse([7, 0, 0, 0]));
        Hex.Same("07 00 00 00", Assert.Single(client.Data));
    }

    [Fact]
    public void Dynamic_channels_and_display_control()
    {
        var random = new Random(2);
        byte[][] seeds =
        [
            DvcServer.Capabilities(2),
            DvcServer.Capabilities(3),
            DvcServer.Create(3, "testdvc"),
            DvcServer.Create(0x1234, DisplayControlChannel.ChannelName, 1),
            DvcServer.DataFirst(3, 3000, new byte[100]),
            DvcServer.DataFirst(0x1234, 0xFFFFFFFF, new byte[100], 1, 2),
            DvcServer.Data(3, new byte[200]),
            DvcServer.Data(0x1234, Hex.Bytes("05 00 00 00 14 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00"), 1),
            DvcServer.Close(3),
            DvcServer.Close(0x1234, 1),
            [0x64, 0x03, 0x7b, 0x0c, 0xe0, 0x26, 0x38, 0xc4, 0x3f, 0xf4, 0x74, 0x01],
            [0x80, 0x00, 0x08, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00],
        ];
        var channels = new DynamicChannels();
        var wire = new Wire(channels);
        var test = new RecordingChannel("testdvc");
        var display = new DisplayControlChannel();
        channels.Register(test);
        channels.Register(display);

        // The display asks for sizes whenever it can.
        display.Ready += () => display.SetSize(random.Next(-10, 10000), random.Next(-10, 10000));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++)
        {
            channels.Receive(Mangled(random, seeds));
            if (round % 100 == 0)
            {
                wire.Take();
                wire.Log.Clear();
                test.Messages.Clear();
                test.Events.Clear();
            }
        }

        Bounded(before, 400L * 1024 * 1024);

        // Still a working manager.
        channels.Closed();
        channels.Receive(DvcServer.Capabilities(2));
        channels.Receive(DvcServer.Create(9, "testdvc"));
        channels.Receive(DvcServer.Data(9, [1, 2]));
        Hex.Same("01 02", test.Messages[^1]);

        // The display channel on its own, fed directly.
        var alone = new DisplayControlChannel { Send = _ => { }, Log = _ => { } };
        seeds = [Hex.Bytes("05 00 00 00 14 00 00 00 10 00 00 00 00 20 00 00 00 20 00 00")];
        for (var round = 0; round < Rounds; round++)
        {
            alone.Receive(Mangled(random, seeds));
            if (alone.IsReady)
            {
                Assert.True(alone.SetSize(random.Next(), random.Next()));
            }
        }
    }

    [Fact]
    public void Sound()
    {
        var random = new Random(3);
        byte[] Pcm(uint rate, ushort channels) =>
        [
            1, 0, .. ClipServer.U16(channels), .. ClipServer.U32(rate), .. ClipServer.U32(rate * channels * 2u),
            .. ClipServer.U16((ushort)(channels * 2)), 16, 0, 0, 0,
        ];
        byte[][] seeds =
        [
            [0x07, 0, 0x38, 0, .. new byte[14], 2, 0, 0, 8, 0, 0, .. Pcm(48000, 2), .. Pcm(44100, 1)],
            [0x06, 0, 0x08, 0, 0x34, 0x12, 0x08, 0x00, 1, 2, 3, 4],
            [0x02, 0, 0x18, 0, 1, 0, 1, 0, 5, 0, 0, 0, 1, 2, 3, 4],
            [0, 0, 0, 0, .. new byte[12]],
            [0x0D, 0, 0x1C, 0, 1, 0, 0, 0, 6, 0, 0, 0, 1, 2, 3, 4, .. new byte[16]],
            [0x01, 0, 0, 0],
            [0x03, 0, 4, 0, 1, 2, 3, 4],
        ];
        var channel = new SoundChannel();
        var wire = new Wire(channel);
        var played = 0;
        channel.Samples += (pcm, rate, channels) =>
        {
            Assert.Equal(0, pcm.Length % (channels * 2));
            Assert.True(rate is >= 8000 and <= 192000);
            played++;
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++)
        {
            channel.Receive(Mangled(random, seeds));
            if (round % 100 == 0)
            {
                wire.Take();
                wire.Log.Clear();
            }
        }

        Bounded(before, 400L * 1024 * 1024);
        Assert.True(played > 0);

        // Still a working player.
        channel.Closed();
        channel.Receive(seeds[0]);
        wire.Take();
        channel.Receive(seeds[4]);
        Hex.Same("05 00 04 00 01 00 06 00", wire.One());
    }

    [Fact]
    public void Chunks()
    {
        var random = new Random(4);
        var chunks = new List<byte[]>();
        ChannelChunks.Split(new byte[5000], 1600, false, chunk => chunks.Add(chunk.ToArray()));
        ChannelChunks.Split([1, 2, 3], 1600, true, chunk => chunks.Add(chunk.ToArray()));
        var reassembler = new ChannelReassembler { Log = _ => { } };

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < Rounds; round++)
        {
            if (reassembler.Add(Mangled(random, chunks), out var message))
            {
                Assert.InRange(message.Length, 0, 5000 + 64);
            }
        }

        Bounded(before, 400L * 1024 * 1024);
    }
}
