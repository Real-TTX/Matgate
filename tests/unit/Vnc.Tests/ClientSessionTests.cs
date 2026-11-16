using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

// The whole client against a scripted server, after the handshake: what the server sends and what the client
// makes of it, and what the client sends.
public class ClientSessionTests
{
    private const int Red = 0xFF0000;
    private const int Green = 0x00FF00;
    private const int Blue = 0x0000FF;
    private const int White = 0xFFFFFF;

    private const int Cursor = -239;
    private const int DesktopSize = -223;
    private const int LastRect = -224;
    private const int ExtendedDesktopSize = -308;

    // A client that is signed in to a scripted server and reads what it sends. The script begins where the
    // handshake ends; the session ends with the script unless the script waits for the client to hang up.
    private sealed class Session : IAsyncDisposable
    {
        private Session(ScriptedServer server, VncClient client, RecordingHandler handler, Task running)
        {
            Server = server;
            Client = client;
            Handler = handler;
            Running = running;
        }

        public ScriptedServer Server { get; }

        public VncClient Client { get; }

        public RecordingHandler Handler { get; }

        public Task Running { get; }

        public static async Task<Session> StartAsync(Func<ServerSide, Task> script, int width = 64, int height = 48, Action<string>? log = null)
        {
            var server = new ScriptedServer(async side =>
            {
                await side.AcceptAsync(width, height);
                await script(side);
            });
            var handler = new RecordingHandler();
            var client = new VncClient(server.Options(), handler, log);
            await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
            return new Session(server, client, handler, client.RunAsync(CancellationToken.None));
        }

        public byte[] Frame()
        {
            lock (Client.FrameLock)
            {
                return (byte[])Client.Framebuffer.Clone();
            }
        }

        // The script has run to its end, and the client has seen the server hang up.
        public async Task EndAsync()
        {
            await Server.FinishAsync();
            await Running.WaitAsync(ScriptedServer.Patience);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static readonly byte[] IncrementalRequest = new Packet().U8(3).U8(1).U16(0).U16(0).U16(64).U16(48).ToArray();

    [Fact]
    public async Task An_update_ends_up_in_the_framebuffer_and_the_next_one_is_asked_for()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            await side.UpdateAsync(1, new Packet().Rectangle(2, 3, 2, 1, 0).Pixel(Red).Pixel(Green));
            Assert.Equal(IncrementalRequest, (await side.ReadMessageAsync()).Bytes);
        });

        await session.EndAsync();
        Assert.Equal(new[] { (2, 3, 2, 1) }, session.Handler.Updates);
        var expected = new Picture(64, 48);
        expected[2, 3] = Red;
        expected[3, 3] = Green;
        expected.AssertShownBy(session.Frame(), 64, 48);
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.Raw));
    }

    [Fact]
    public async Task An_update_of_several_rectangles_is_reported_once_and_whole()
    {
        byte[]? seen = null;
        var release = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            // Three rectangles, and a pause before the last one.
            await side.SendAsync(new Packet().U8(0).U8(0).U16(3)
                .Rectangle(10, 10, 1, 1, 0).Pixel(Red)
                .Rectangle(40, 5, 2, 1, 0).Pixel(Green).Pixel(Green));
            await release.Task;
            await side.SendAsync(new Packet().Rectangle(20, 30, 1, 2, 0).Pixel(Blue).Pixel(Blue));
            await side.ReadMessageAsync();
        });

        session.Handler.OnUpdated = (_, _, _, _) => seen = session.Frame();

        // While the update is under way, nothing of it shows.
        await Task.Delay(300);
        Assert.Equal(0, session.Handler.UpdateCount);
        new Picture(64, 48).AssertShownBy(session.Frame(), 64, 48);

        release.SetResult();
        await session.EndAsync();

        // One call, for the box around all three; and at the time of the call all three were there.
        Assert.Equal(new[] { (10, 5, 32, 27) }, session.Handler.Updates);
        var expected = new Picture(64, 48);
        expected[10, 10] = Red;
        expected.Fill(40, 5, 2, 1, Green);
        expected.Fill(20, 30, 1, 2, Blue);
        expected.AssertShownBy(seen!, 64, 48);
    }

    [Fact]
    public async Task An_update_may_end_with_a_last_rectangle_instead_of_a_count()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            await side.SendAsync(new Packet().U8(0).U8(0).U16(0xFFFF)
                .Rectangle(1, 1, 1, 1, 0).Pixel(Red)
                .Rectangle(0, 0, 0, 0, LastRect));
            await side.ReadMessageAsync();
            await side.UpdateAsync(1, new Packet().Rectangle(2, 2, 1, 1, 0).Pixel(Blue));
            await side.ReadMessageAsync();
        });

        await session.EndAsync();
        Assert.Equal(new[] { (1, 1, 1, 1), (2, 2, 1, 1) }, session.Handler.Updates);
    }

    [Fact]
    public async Task Every_encoding_is_drawn()
    {
        var noise = Picture.Noise(20, 10, seed: 80);
        using var zrlePacker = new Packer();
        using var tightPacker = new Packer();
        await using var session = await Session.StartAsync(async side =>
        {
            var raw = new Packet().Rectangle(0, 0, 20, 10, 0);
            SimpleDecoderTests.Pixels(raw, noise);
            await side.UpdateAsync(1, raw);
            await side.ReadMessageAsync();

            var tile = new Packet().U8(0);
            ZrleTests.Cpixels(tile, noise);
            var tight = new Packet().Rectangle(40, 20, 20, 10, 7).U8(0x00);
            TightTests.Compressed(tight, tightPacker, TightTests.Tpixels(noise));
            await side.UpdateAsync(6, new Packet()
                .Rectangle(30, 0, 20, 10, 1).U16(0).U16(0) // the first rectangle, copied
                .Rectangle(0, 20, 16, 16, 2).U32(1).Pixel(Red).Pixel(Blue).U16(4).U16(4).U16(8).U16(8)
                .Rectangle(20, 20, 16, 16, 5).U8(2).Pixel(Green)
                .Rectangle(0, 37, 20, 10, 16).Bytes(ZrleTests.Rectangle(zrlePacker, tile))
                .Bytes(tight)
                .Rectangle(60, 40, 4, 8, 7).U8(0x80).Tpixel(White));
            await side.ReadMessageAsync();
        });

        await session.EndAsync();
        var expected = new Picture(64, 48);
        expected.Draw(noise, 0, 0);
        expected.Draw(noise, 30, 0);
        expected.Fill(0, 20, 16, 16, Red);
        expected.Fill(4, 24, 8, 8, Blue);
        expected.Fill(20, 20, 16, 16, Green);
        expected.Draw(noise, 0, 37);
        expected.Draw(noise, 40, 20);
        expected.Fill(60, 40, 4, 8, White);
        expected.AssertShownBy(session.Frame(), 64, 48);
        Assert.Equal(new[] { (0, 0, 20, 10), (0, 0, 64, 48) }, session.Handler.Updates);
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.Raw));
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.CopyRect));
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.Rre));
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.Hextile));
        Assert.Equal(1, session.Client.Rectangles(VncEncoding.Zrle));
        Assert.Equal(2, session.Client.Rectangles(VncEncoding.Tight));
    }

    [Fact]
    public async Task A_whole_screen_of_raw_pixels()
    {
        // 1920 x 1080: eight megabytes in one rectangle.
        var noise = Picture.Noise(1920, 1080, seed: 81);
        var pixels = noise.ToBgrx();
        await using var session = await Session.StartAsync(
            async side =>
            {
                await side.SendAsync(new Packet().U8(0).U8(0).U16(1).Rectangle(0, 0, 1920, 1080, 0));
                await side.SendAsync(pixels);
                await side.ReadMessageAsync();
            },
            1920,
            1080);

        await session.EndAsync();
        Assert.Equal(new[] { (0, 0, 1920, 1080) }, session.Handler.Updates);
        Assert.True(pixels.AsSpan().SequenceEqual(session.Frame()));
    }

    [Fact]
    public async Task A_new_desktop_size_replaces_the_framebuffer_and_keeps_the_picture()
    {
        var go = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            await go.Task;
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 2, 1, 0).Pixel(Red).Pixel(Green));
            await side.ReadMessageAsync();

            // Larger, and then smaller than at the beginning.
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 80, 60, DesktopSize));

            // This plain form says nothing about the picture: the client asks for all of it, in the new size.
            Assert.Equal(new Packet().U8(3).U8(0).U16(0).U16(0).U16(80).U16(60).ToArray(), (await side.ReadMessageAsync()).Bytes);
            await side.UpdateAsync(2, new Packet().Rectangle(79, 59, 1, 1, 0).Pixel(Blue).Rectangle(0, 0, 1, 30, DesktopSize));
            Assert.Equal(new Packet().U8(3).U8(0).U16(0).U16(0).U16(1).U16(30).ToArray(), (await side.ReadMessageAsync()).Bytes);
        });

        var before = session.Client.Framebuffer;
        go.SetResult();
        await session.EndAsync();
        Assert.Equal(new[] { (80, 60), (1, 30) }, session.Handler.Sizes);
        Assert.Equal(1, session.Client.Width);
        Assert.Equal(30, session.Client.Height);
        Assert.NotSame(before, session.Client.Framebuffer);

        // One pixel wide: what is left of the first row is its first pixel.
        var expected = new Picture(1, 30);
        expected[0, 0] = Red;
        expected.AssertShownBy(session.Frame(), 1, 30);
    }

    [Fact]
    public async Task A_desktop_size_beyond_all_bounds_ends_the_session()
    {
        await using var session = await Session.StartAsync(side => side.UpdateAsync(1, new Packet().Rectangle(0, 0, 40000, 100, DesktopSize)));
        var exception = await Assert.ThrowsAsync<VncException>(() => session.Running.WaitAsync(ScriptedServer.Patience));
        Assert.Contains("16384", exception.Message);
    }

    [Fact]
    public async Task The_server_can_be_asked_for_another_size_once_it_has_told_its_screens()
    {
        var go = new TaskCompletionSource();
        var asked = new TaskCompletionSource();
        var told = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            // Its layout, unasked: one screen with an id and flags of its own.
            await go.Task;
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 64, 48, ExtendedDesktopSize)
                .U8(1).Zeros(3).U32(0x11223344).U16(0).U16(0).U16(64).U16(48).U32(0x0000A005));
            Assert.Equal(IncrementalRequest, (await side.ReadMessageAsync()).Bytes);
            told.SetResult();

            // SetDesktopSize: the size, one screen - the server's own, at 0, 0, as large as the desktop.
            var request = await side.ReadMessageAsync();
            Assert.Equal(
                new Packet().U8(251).U8(0).U16(100).U16(70).U8(1).U8(0)
                    .U32(0x11223344).U16(0).U16(0).U16(100).U16(70).U32(0x0000A005).ToArray(),
                request.Bytes);

            // "Done, as you asked."
            await side.UpdateAsync(1, new Packet().Rectangle(1, 0, 100, 70, ExtendedDesktopSize)
                .U8(1).Zeros(3).U32(0x55667788).U16(0).U16(0).U16(100).U16(70).U32(0));

            // The picture is not asked for anew (that would be answered with the layout again, for ever).
            Assert.Equal(new Packet().U8(3).U8(1).U16(0).U16(0).U16(100).U16(70).ToArray(), (await side.ReadMessageAsync()).Bytes);
            await asked.Task;

            // The id the server names now is the one that comes back.
            Assert.Equal(0x55667788u, (await side.ReadMessageAsync()).U32(8));
        });

        Assert.False(session.Client.CanResize);
        session.Client.RequestSize(100, 70); // not yet: nothing is sent
        go.SetResult();
        await told.Task.WaitAsync(ScriptedServer.Patience);
        Assert.True(session.Client.CanResize);
        Assert.Empty(session.Handler.Sizes);

        session.Client.RequestSize(100, 70);
        await session.Handler.UntilAsync(seen => seen.Sizes.Count == 1, "the new size");
        Assert.Equal((100, 70), session.Handler.Sizes[0]);
        Assert.Equal(100, session.Client.Width);
        Assert.Equal(70 * 100 * 4, session.Client.Framebuffer.Length);

        session.Client.RequestSize(50000, 0);
        asked.SetResult();
        await session.EndAsync();
    }

    [Theory]
    [InlineData(1, false)] // prohibited: asking again is of no use
    [InlineData(3, true)] // this layout was not accepted; another one may be
    public async Task A_size_the_server_does_not_take_changes_nothing(int status, bool canStillResize)
    {
        var told = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 64, 48, ExtendedDesktopSize)
                .U8(1).Zeros(3).U32(7).U16(0).U16(0).U16(64).U16(48).U32(0));
            await side.ReadMessageAsync();
            Assert.Equal(251, (await side.ReadMessageAsync()).Type);

            // "No" - with a size and a layout that mean nothing.
            await side.UpdateAsync(1, new Packet().Rectangle(1, status, 999, 999, ExtendedDesktopSize).U8(0).Zeros(3));
            await side.ReadMessageAsync();
            told.SetResult();
        });

        await session.Handler.UntilAsync(_ => session.Client.CanResize, "the layout");
        session.Client.RequestSize(100, 70);
        await told.Task.WaitAsync(ScriptedServer.Patience);
        await session.EndAsync();
        Assert.Empty(session.Handler.Sizes);
        Assert.Equal(64, session.Client.Width);
        Assert.Equal(canStillResize, session.Client.CanResize);
    }

    [Fact]
    public async Task The_pointers_shape_arrives_as_RGBA()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            // 2 x 2 with the hot spot at 1, 0; the pixel at the top right is not part of the shape.
            await side.UpdateAsync(1, new Packet().Rectangle(1, 0, 2, 2, Cursor)
                .Pixel(0x112233).Pixel(0x445566).Pixel(0x778899).Pixel(0xAABBCC)
                .U8(0b1000_0000).U8(0b1100_0000));
            await side.ReadMessageAsync();

            // Nothing of the shape can be seen; a shape without size; and one far too large to be a pointer.
            await side.UpdateAsync(3, new Packet()
                .Rectangle(0, 0, 9, 1, Cursor).Zeros(9 * 4).U8(0).U8(0)
                .Rectangle(0, 0, 0, 0, Cursor)
                .Rectangle(0, 0, 300, 2, Cursor).Zeros(300 * 2 * 4 + 38 * 2));
            await side.ReadMessageAsync();

            // A hot spot outside the shape is moved into it.
            await side.UpdateAsync(1, new Packet().Rectangle(7, 9, 1, 1, Cursor).Pixel(White).U8(0x80));
            await side.ReadMessageAsync();
        });

        await session.EndAsync();
        var cursors = session.Handler.Cursors;
        Assert.Equal(4, cursors.Count);
        Assert.Equal((1, 0, 2, 2), (cursors[0].HotX, cursors[0].HotY, cursors[0].Width, cursors[0].Height));
        Assert.Equal("112233FF" + "44556600" + "778899FF" + "AABBCCFF", Convert.ToHexString(cursors[0].Rgba));
        Assert.Equal((0, 0, 0, 0, 0), (cursors[1].HotX, cursors[1].HotY, cursors[1].Width, cursors[1].Height, cursors[1].Rgba.Length));
        Assert.Equal((0, 0, 0), (cursors[2].Width, cursors[2].Height, cursors[2].Rgba.Length));
        Assert.Equal((0, 0, 1, 1), (cursors[3].HotX, cursors[3].HotY, cursors[3].Width, cursors[3].Height));
        Assert.Equal("FFFFFFFF", Convert.ToHexString(cursors[3].Rgba));

        // The pointer is not part of the picture.
        Assert.Empty(session.Handler.Updates);
    }

    [Fact]
    public async Task Text_goes_to_and_from_a_server_that_only_knows_Latin_1()
    {
        var copied = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            await side.SendAsync(new Packet().U8(3).Zeros(3).U32(16).Text("Grüße\r\nvon dort\0"));
            await copied.Task;
            Assert.Equal(new Packet().U8(6).Zeros(3).U32(11).Text("über\nall ?!").ToArray(), (await side.ReadMessageAsync()).Bytes);
        });

        await session.Handler.UntilAsync(seen => seen.Texts.Count == 1, "the text copied on the server");
        Assert.Equal("Grüße\nvon dort", session.Handler.Texts[0]);
        session.Client.SetClipboardText("über\r\nall €!");
        copied.SetResult();
        await session.EndAsync();
    }

    [Fact]
    public async Task Text_goes_to_and_from_a_server_with_the_extended_clipboard()
    {
        const uint Caps = 1u << 24;
        const uint Request = 1u << 25;
        const uint Peek = 1u << 26;
        const uint Notify = 1u << 27;
        const uint Provide = 1u << 28;
        var ready = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            // What the server can do: text and rich text, every action - a size for each of the two formats.
            await side.SendAsync(ExtendedCutText(Caps | Request | Peek | Notify | Provide | 3, new Packet().U32(0).U32(0).ToArray()));

            // The client says what it can do: text, every action, and nothing unasked.
            Assert.Equal(new Packet().U8(6).Zeros(3).S32(-8).U32(0x1F000001).U32(0).ToArray(), (await side.ReadMessageAsync()).Bytes);
            ready.SetResult();

            // The viewer copies: "there is text" - and nothing more until somebody wants it.
            Assert.Equal(new Packet().U8(6).Zeros(3).S32(-4).U32(Notify | 1).ToArray(), (await side.ReadMessageAsync()).Bytes);
            await side.SendAsync(ExtendedCutText(Request | 1, []));
            var provide = await side.ReadMessageAsync();
            Assert.Equal(Provide | 1, provide.U32(8));
            Assert.Equal("über\r\nall €!\0", Unpack(provide.Bytes.AsSpan(12).ToArray()));

            // Asked again, it is still there.
            await side.SendAsync(ExtendedCutText(Request | 1, []));
            Assert.Equal("über\r\nall €!\0", Unpack((await side.ReadMessageAsync()).Bytes.AsSpan(12).ToArray()));

            // "What do you have?"
            await side.SendAsync(ExtendedCutText(Peek, []));
            Assert.Equal(new Packet().U8(6).Zeros(3).S32(-4).U32(Notify | 1).ToArray(), (await side.ReadMessageAsync()).Bytes);

            // Somebody copies on the server: "there is text" - the client asks for it at once.
            await side.SendAsync(ExtendedCutText(Notify | 1, []));
            Assert.Equal(new Packet().U8(6).Zeros(3).S32(-4).U32(Request | 1).ToArray(), (await side.ReadMessageAsync()).Bytes);
            using var packer = new Packer();
            var text = Encoding.UTF8.GetBytes("von dort – drüben\r\nzweite Zeile\0");
            await side.SendAsync(ExtendedCutText(Provide | 1, packer.Pack(new Packet().U32((uint)text.Length).Bytes(text).ToArray())));

            // Something that is no text is on the server's clipboard: nothing to ask for. A provide without
            // text, and one that cannot be unpacked, are passed over - the session goes on.
            await side.SendAsync(ExtendedCutText(Notify | 2, []));
            await side.SendAsync(ExtendedCutText(Provide | 2, packer.Pack(new Packet().U32(3).Text("rtf").ToArray())));
            await side.SendAsync(ExtendedCutText(Provide | 1, [9, 9, 9, 9, 9]));
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 1, 1, 0).Pixel(Red));
            Assert.Equal(3, (await side.ReadMessageAsync()).Type);
        });

        await ready.Task.WaitAsync(ScriptedServer.Patience);
        session.Client.SetClipboardText("über\nall €!");
        await session.EndAsync();
        Assert.Equal(new[] { "von dort – drüben\nzweite Zeile" }, session.Handler.Texts);
    }

    [Fact]
    public async Task A_clipboard_text_that_is_too_long_is_passed_over()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            // 17 megabytes of Latin-1: more than is taken. The session goes on behind it.
            await side.SendAsync(new Packet().U8(3).Zeros(3).U32(17 * 1024 * 1024));
            var chunk = new byte[1024 * 1024];
            for (var index = 0; index < 17; index++)
            {
                await side.SendAsync(chunk);
            }

            await side.SendAsync(new Packet().U8(3).Zeros(3).U32(5).Text("kurz!"));
        });

        await session.EndAsync();
        Assert.Equal(new[] { "kurz!" }, session.Handler.Texts);
    }

    [Fact]
    public async Task A_fence_is_answered_with_the_same_mark()
    {
        const uint Request = 0x80000000;
        await using var session = await Session.StartAsync(async side =>
        {
            // Without "request" there is nothing to answer.
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(1).U8(2).U8(0xAA).U8(0xBB));

            // BlockBefore, SyncNext and a bit nobody knows: what comes back is what the client stands for.
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(Request | 1 | 4 | 0x100).U8(8).Text("12345678"));
            Assert.Equal(new Packet().U8(248).Zeros(3).U32(1).U8(8).Text("12345678").ToArray(), (await side.ReadMessageAsync()).Bytes);

            // The dummy fence a TigerVNC server sends first: empty.
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(Request | 1 | 2).U8(0));
            Assert.Equal(new Packet().U8(248).Zeros(3).U32(3).U8(0).ToArray(), (await side.ReadMessageAsync()).Bytes);

            // 64 bytes are the most.
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(Request).U8(64).Zeros(64));
            Assert.Equal(73, (await side.ReadMessageAsync()).Bytes.Length);
        });

        await session.EndAsync();
    }

    [Fact]
    public async Task A_fence_longer_than_allowed_ends_the_session()
    {
        await using var session = await Session.StartAsync(side => side.SendAsync(new Packet().U8(248).Zeros(3).U32(0).U8(65).Zeros(65)));
        await Assert.ThrowsAsync<VncException>(() => session.Running.WaitAsync(ScriptedServer.Patience));
    }

    [Fact]
    public async Task Continuous_updates_are_switched_on_when_the_server_has_them()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            // EndOfContinuousUpdates right at the beginning: "I can do this."
            await side.SendAsync([150]);
            Assert.Equal(new Packet().U8(150).U8(1).U16(0).U16(0).U16(64).U16(48).ToArray(), (await side.ReadMessageAsync()).Bytes);

            // From now on updates are not asked for one by one: after this one, the next thing the client
            // sends is the answer to the fence.
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 1, 1, 0).Pixel(Red));
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(0x80000001).U8(1).U8(7));
            Assert.Equal(248, (await side.ReadMessageAsync()).Type);

            // A new size: continuous updates are asked for again, for the new area.
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 100, 70, ExtendedDesktopSize)
                .U8(1).Zeros(3).U32(1).U16(0).U16(0).U16(100).U16(70).U32(0));
            Assert.Equal(new Packet().U8(150).U8(1).U16(0).U16(0).U16(100).U16(70).ToArray(), (await side.ReadMessageAsync()).Bytes);
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(0x80000001).U8(1).U8(8));
            Assert.Equal(248, (await side.ReadMessageAsync()).Type);

            // Said twice, it is switched on once.
            await side.SendAsync([150]);
            await side.SendAsync(new Packet().U8(248).Zeros(3).U32(0x80000001).U8(1).U8(9));
            Assert.Equal(248, (await side.ReadMessageAsync()).Type);
        });

        await session.EndAsync();
        Assert.Equal(new[] { (100, 70) }, session.Handler.Sizes);
    }

    [Fact]
    public async Task A_bell_and_a_colour_map_are_passed_over()
    {
        await using var session = await Session.StartAsync(async side =>
        {
            await side.SendAsync(new Packet().U8(2));
            await side.SendAsync(new Packet().U8(1).U8(0).U16(5).U16(3).Zeros(18));
            await side.SendAsync(new Packet().U8(1).U8(0).U16(0).U16(0));
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 1, 1, 0).Pixel(Red));
            await side.ReadMessageAsync();
        });

        await session.EndAsync();
        Assert.Single(session.Handler.Updates);
    }

    [Fact]
    public async Task The_server_hanging_up_between_two_messages_is_the_end_and_no_error()
    {
        await using var session = await Session.StartAsync(_ => Task.CompletedTask);
        await session.EndAsync();
    }

    [Theory]
    [InlineData(new byte[] { 99 })] // a message that does not exist
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 4, 0, 4, 0, 0, 0, 99 })] // an encoding nobody asked for
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 4, 0, 4, 0xFF, 0xFF, 0xFF, 0xE0 })] // a JPEG quality level is no encoding
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 61, 0, 0, 0, 4, 0, 4, 0, 0, 0, 0 })] // a rectangle beyond the right edge
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 0, 0, 45, 0, 4, 0, 4, 0, 0, 0, 2 })] // beyond the bottom
    [InlineData(new byte[] { 0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 5 })]
    [InlineData(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 4, 0, 4, 0, 0, 0, 0, 1, 2, 3 })] // breaks off in the pixels
    [InlineData(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 2, 3, 4 })] // the second rectangle is missing
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 9, 65 })] // text that breaks off
    [InlineData(new byte[] { 3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFE })] // an extended clipboard message too short to be one
    [InlineData(new byte[] { 248, 0, 0 })]
    [InlineData(new byte[] { 0 })]
    public async Task What_cannot_be_read_ends_the_session_with_a_VncException(byte[] sent)
    {
        await using var session = await Session.StartAsync(side => side.SendAsync(sent));
        await Assert.ThrowsAsync<VncException>(() => session.Running.WaitAsync(ScriptedServer.Patience));
        await session.Server.FinishAsync();
    }

    // Whatever a server sends after the handshake: the session ends by itself, with a VncException or - when
    // the noise happens to make sense up to its last byte - without one. Nothing else comes out of RunAsync.
    [Fact]
    public async Task Noise_after_the_handshake_ends_the_session()
    {
        var random = new Random(31337);
        for (var round = 0; round < 150; round++)
        {
            var noise = new byte[random.Next(16, 3000)];
            random.NextBytes(noise);
            switch (round % 3)
            {
                case 1:
                    // The start of an update, so that the noise reaches the decoders: a few rectangles, the
                    // first one inside the desktop and in an encoding that exists.
                    new byte[] { 0, 0, 0, 3, 0, 1, 0, 1, 0, 30, 0, 20, 0, 0, 0, new byte[] { 0, 1, 2, 5, 7, 16 }[round / 3 % 6] }.CopyTo(noise, 0);
                    break;
                case 2:
                    // Small numbers get further than noise.
                    for (var index = 0; index < noise.Length; index++)
                    {
                        noise[index] &= 3;
                    }

                    break;
            }

            await using var session = await Session.StartAsync(side => side.SendAsync(noise));
            try
            {
                await session.Running.WaitAsync(ScriptedServer.Patience);
            }
            catch (VncException)
            {
            }
            catch (Exception exception)
            {
                Assert.Fail($"Round {round}: {exception.GetType().Name} instead of a VncException: {exception.Message} - sent {Convert.ToHexString(noise.AsSpan(0, Math.Min(noise.Length, 100)))}");
            }
        }
    }

    [Fact]
    public async Task Cancelling_ends_the_session_without_an_error()
    {
        var server = new ScriptedServer(async side =>
        {
            await side.AcceptAsync(64, 48);
            await side.ClosedAsync();
        });
        await using var disposing = server;
        await using var client = new VncClient(server.Options(), new RecordingHandler());
        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);

        using var cancel = new CancellationTokenSource();
        var running = client.RunAsync(cancel.Token);
        await Task.Delay(200);
        Assert.False(running.IsCompleted);
        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        await running.WaitAsync(ScriptedServer.Patience);
        Assert.True(watch.ElapsedMilliseconds < 5000);
        await server.FinishAsync();
    }

    [Fact]
    public async Task Disposing_ends_the_session_without_an_error()
    {
        await using var session = await Session.StartAsync(side => side.ClosedAsync());
        await Task.Delay(100);
        await session.Client.DisposeAsync().AsTask().WaitAsync(ScriptedServer.Patience);
        await session.Running.WaitAsync(ScriptedServer.Patience);

        // And everything is quiet afterwards.
        session.Client.Pointer(1, 1, 0);
        session.Client.Key(65, true);
        session.Client.SetClipboardText("x");
        session.Client.RequestSize(10, 10);
        await session.Client.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_from_inside_a_callback_does_not_wait_for_itself()
    {
        var go = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            await go.Task;
            await side.UpdateAsync(1, new Packet().Rectangle(0, 0, 1, 1, 0).Pixel(Red));
            await side.ClosedAsync();
        });

        session.Handler.OnUpdated = (_, _, _, _) => Assert.True(session.Client.DisposeAsync().AsTask().Wait(ScriptedServer.Patience));
        go.SetResult();
        await session.Running.WaitAsync(ScriptedServer.Patience);
        await session.Server.FinishAsync();
    }

    [Fact]
    public async Task The_session_runs_once_and_only_after_connecting()
    {
        await using var server = new ScriptedServer(async side =>
        {
            await side.AcceptAsync(64, 48);
            await side.ClosedAsync();
        });
        await using var client = new VncClient(server.Options(), new RecordingHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(CancellationToken.None));

        // Nothing is connected: nothing is sent, and nothing fails.
        client.Pointer(1, 1, 0);
        client.Key(65, true);
        client.SetClipboardText("x");
        client.RequestSize(10, 10);
        Assert.Empty(client.Framebuffer);

        await client.ConnectAsync(CancellationToken.None).WaitAsync(ScriptedServer.Patience);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(CancellationToken.None));
        var running = client.RunAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunAsync(CancellationToken.None));
        await client.DisposeAsync();
        await running.WaitAsync(ScriptedServer.Patience);
    }

    [Fact]
    public async Task Pointer_and_keys_are_sent_as_the_protocol_wants_them()
    {
        var go = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async side =>
        {
            await go.Task;
            Assert.Equal("05" + "01" + "000A" + "0014", (await side.ReadMessageAsync()).Hex);
            Assert.Equal("04" + "01" + "0000" + "0000FFE3", (await side.ReadMessageAsync()).Hex);
            Assert.Equal("04" + "00" + "0000" + "010020AC", (await side.ReadMessageAsync()).Hex);

            // Outside the desktop the pointer stops at its edge; only eight buttons exist.
            Assert.Equal("05" + "FF" + "0000" + "002F", (await side.ReadMessageAsync()).Hex);
            Assert.Equal("05" + "00" + "003F" + "0000", (await side.ReadMessageAsync()).Hex);
        });

        session.Client.Pointer(10, 20, 1);
        session.Client.Key(0xFFE3, true);
        session.Client.Key(0x010020AC, false);
        session.Client.Pointer(-5, 99999, 0x1FF);
        session.Client.Pointer(int.MaxValue, int.MinValue, 0);
        go.SetResult();
        await session.EndAsync();
    }

    [Fact]
    public async Task Messages_from_many_threads_arrive_whole_and_in_the_order_of_each_thread()
    {
        const int Threads = 8;
        const int PerThread = 2000;
        var next = new int[Threads];
        await using var session = await Session.StartAsync(async side =>
        {
            for (var received = 0; received < Threads * PerThread * 2; received++)
            {
                var message = await side.ReadMessageAsync();
                if (message.Type == 4)
                {
                    // The key says which thread sent it and which of its messages it is.
                    var keysym = message.U32(4);
                    var thread = (int)(keysym >> 24);
                    Assert.Equal(next[thread]++, (int)(keysym & 0xFFFFFF));
                }
                else
                {
                    // A pointer event, with x and y the same.
                    Assert.Equal(5, message.Type);
                    Assert.Equal(message.U16(2), message.U16(4));
                }
            }
        });

        var threads = Enumerable.Range(0, Threads).Select(thread => new Thread(() =>
        {
            for (var index = 0; index < PerThread; index++)
            {
                session.Client.Key(((uint)thread << 24) | (uint)index, true);

                // Buttons no other thread uses, so that no pointer event takes the place of another one.
                session.Client.Pointer(index % 40, index % 40, thread | ((index & 0x1F) << 3));
            }
        })).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        await session.EndAsync();
        Assert.All(next, count => Assert.Equal(PerThread, count));
    }

    [Fact]
    public async Task A_server_that_takes_nothing_does_not_hold_the_caller_up_and_is_given_up_in_the_end()
    {
        var release = new TaskCompletionSource();
        await using var session = await Session.StartAsync(async _ =>
        {
            // Reads nothing at all.
            await release.Task;
        });

        // Megabytes of text, again and again: every call comes back at once, whatever the server does.
        var text = new string('x', 8 * 1024 * 1024);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 12 && !session.Running.IsCompleted; index++)
        {
            session.Client.SetClipboardText(text);
            for (var move = 0; move < 1000; move++)
            {
                session.Client.Pointer(move % 64, move % 48, 0);
                session.Client.Key(65, true);
            }
        }

        Assert.True(watch.ElapsedMilliseconds < 20000, $"The calls took {watch.ElapsedMilliseconds} ms.");
        var exception = await Assert.ThrowsAsync<VncException>(() => session.Running.WaitAsync(ScriptedServer.Patience));
        Assert.Contains("stopped taking input", exception.Message);
        release.SetResult();
    }

    [Fact]
    public async Task The_queue_puts_a_pointer_event_in_the_place_of_the_one_before_it()
    {
        var queue = new VncSendQueue();
        Assert.False(queue.EnqueuePointer(0, 1, 1)); // nothing is connected yet
        queue.Open();

        // Three moves, a button, two more moves, a key, a move: the moves in a row become the last of them.
        queue.EnqueuePointer(0, 1, 1);
        queue.EnqueuePointer(0, 2, 2);
        queue.EnqueuePointer(0, 3, 3);
        queue.EnqueuePointer(1, 3, 3);
        queue.EnqueuePointer(1, 4, 4);
        queue.EnqueuePointer(1, 5, 5);
        queue.Enqueue([4, 1, 0, 0, 0, 0, 0, 65]);
        queue.EnqueuePointer(1, 6, 6);

        var written = new MemoryStream();
        using var stop = new CancellationTokenSource();
        var writing = queue.RunAsync(written, stop.Token);
        while (written.Length < 26)
        {
            await Task.Delay(10);
        }

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal(
            "050000030003" + "050100050005" + "0401000000000041" + "050100060006",
            Convert.ToHexString(written.ToArray()));
    }

    // A ServerCutText in its extended form: a negative length, the flags, what belongs to the action.
    private static byte[] ExtendedCutText(uint flags, byte[] data)
    {
        return new Packet().U8(3).Zeros(3).S32(-(4 + data.Length)).U32(flags).Bytes(data).ToArray();
    }

    // What a server finds in a "provide": the size, and that many bytes.
    private static string Unpack(byte[] packed)
    {
        using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var header = new byte[4];
        zlib.ReadExactly(header);
        var text = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        zlib.ReadExactly(text);
        return Encoding.UTF8.GetString(text);
    }
}
