using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Vnc.Tests;

// The client against real VNC servers. Nothing here runs unless the environment says where they are:
//
//   MATGATE_VNC_TEST_HOST      a TigerVNC server with a desktop to work in: openbox and one xterm (with a
//                              shell and xclip) that has the keyboard, as tests/desktop sets it up
//   MATGATE_VNC_TEST_PORT      its port (5901)
//   MATGATE_VNC_TEST_PASSWORD  its VNC password (demo-pw1)
//   MATGATE_VNC_TEST_SERVERS   optional: the host started by servers/start.sh - more servers, one for every
//                              way of signing in, and a large desktop for the timing
//   MATGATE_VNC_TEST_OUT       optional: a folder for pictures of what the client saw
//
// The servers of the test stack are only reachable inside its Docker network, so the tests run in a
// container there - servers/run.sh does that (and starts the servers of servers/start.sh first).
//
// What a test does on the desktop it checks through the desktop itself: a command typed into the xterm
// puts its result on the clipboard, and the clipboard comes back through VNC. The typed commands also leave
// their results in /tmp of the desktop's container (vnc-test.txt, vnc-clip.txt) for a look from outside.
[Collection("real servers")]
public sealed class ServerTests(ITestOutputHelper output)
{
    private const uint Return = 0xFF0D;
    private const uint ControlLeft = 0xFFE3;

    private static readonly string? Host = Environment.GetEnvironmentVariable("MATGATE_VNC_TEST_HOST");
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("MATGATE_VNC_TEST_PORT"), out var port) ? port : 5901;
    private static readonly string Password = Environment.GetEnvironmentVariable("MATGATE_VNC_TEST_PASSWORD") ?? "demo-pw1";
    private static readonly string? Servers = Environment.GetEnvironmentVariable("MATGATE_VNC_TEST_SERVERS");
    private static readonly string? Out = Environment.GetEnvironmentVariable("MATGATE_VNC_TEST_OUT");

    // Runs only where MATGATE_VNC_TEST_HOST names the desktop.
    private sealed class DesktopFactAttribute : FactAttribute
    {
        public DesktopFactAttribute()
        {
            if (string.IsNullOrEmpty(Host))
            {
                Skip = "MATGATE_VNC_TEST_HOST is not set: there is no VNC server to test against.";
            }
        }
    }

    // Runs only where MATGATE_VNC_TEST_SERVERS names the host of servers/start.sh.
    private sealed class ServersFactAttribute : FactAttribute
    {
        public ServersFactAttribute()
        {
            if (string.IsNullOrEmpty(Servers))
            {
                Skip = "MATGATE_VNC_TEST_SERVERS is not set: the servers of servers/start.sh are not running.";
            }
        }
    }

    // A connection to a server's desktop, with what a test does there.
    private sealed class Desktop : IAsyncDisposable
    {
        private Desktop(VncClient client, RecordingHandler handler, Task running)
        {
            Client = client;
            Handler = handler;
            Running = running;
        }

        public VncClient Client { get; }

        public RecordingHandler Handler { get; }

        public Task Running { get; }

        public static async Task<Desktop> ConnectAsync(VncOptions options, RecordingHandler? handler = null, Action<string>? log = null)
        {
            handler ??= new RecordingHandler();
            var client = new VncClient(options, handler, log);
            try
            {
                await client.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            return new Desktop(client, handler, client.RunAsync(CancellationToken.None));
        }

        public byte[] Frame()
        {
            lock (Client.FrameLock)
            {
                return (byte[])Client.Framebuffer.Clone();
            }
        }

        // The whole desktop has arrived, and nothing has changed for a moment.
        public Task StillAsync(int quiet = 500)
        {
            return Handler.UntilAsync(
                seen => seen.Covered == (0, 0, Client.Width, Client.Height) && Environment.TickCount64 - seen.LastChange >= quiet,
                "the whole desktop, standing still",
                90);
        }

        public async Task ClickAsync(int x, int y, int times = 1)
        {
            Client.Pointer(x, y, 0);
            for (var click = 0; click < times; click++)
            {
                Client.Pointer(x, y, 1);
                Client.Pointer(x, y, 0);
                await Task.Delay(40);
            }
        }

        // Every character of the text as a key: its keysym is its code for all of ASCII, and the server finds
        // the key (and Shift) for it.
        public async Task TypeAsync(string text)
        {
            foreach (var character in text)
            {
                Client.Key(character, true);
                Client.Key(character, false);
            }

            await Task.Delay(50);
        }

        // Gives the xterm the keyboard and an empty command line.
        public async Task TerminalAsync()
        {
            await ClickAsync(300, 300);
            Client.Key(ControlLeft, true);
            Client.Key('c', true);
            Client.Key('c', false);
            Client.Key(ControlLeft, false);
            await Task.Delay(200);
        }

        public async Task RunAsync(string command)
        {
            await TypeAsync(command);
            Client.Key(Return, true);
            Client.Key(Return, false);
            await Task.Delay(100);
        }

        // Runs a command in the xterm and returns what it printed - by way of the server's clipboard. xclip
        // stays until something else takes the clipboard. It does so as a job of the shell ("-quiet" keeps it
        // from making itself a child of nobody), so that the shell clears it away when it ends.
        public async Task<string> OutputOfAsync(string command)
        {
            var before = Handler.Read(seen => seen.Texts.Count);
            await RunAsync(command + " | xclip -quiet -selection clipboard &");
            return await NextTextAsync(before, $"the output of \"{command}\" on the clipboard");
        }

        // Leaves the desktop as it was found: the clipboard is Matgate's (and empty) again, which ends the
        // xclip that held it, and the terminal is clear.
        public async Task TidyAsync()
        {
            Client.SetClipboardText("");
            await Task.Delay(300);
            await RunAsync("clear");
            await Task.Delay(300);
        }

        public async Task<string> NextTextAsync(int before, string what)
        {
            await Handler.UntilAsync(seen => seen.Texts.Count > before, what, 60);
            return Handler.Read(seen => seen.Texts[^1]);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static VncOptions Options(IReadOnlyList<VncEncoding>? encodings = null, string? password = null)
    {
        return new VncOptions { Host = Host!, Port = Port, Password = password ?? Password, Encodings = encodings, ConnectTimeout = TimeSpan.FromSeconds(30) };
    }

    private Task<Desktop> ConnectAsync(IReadOnlyList<VncEncoding>? encodings = null)
    {
        return Desktop.ConnectAsync(Options(encodings), log: output.WriteLine);
    }

    private void Save(string name, Desktop desktop)
    {
        if (!string.IsNullOrEmpty(Out))
        {
            Png.Save(Path.Combine(Out, name + ".png"), desktop.Frame(), desktop.Client.Width, desktop.Client.Height);
        }
    }

    [DesktopFact]
    public async Task The_whole_desktop_arrives()
    {
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();
        Save("desktop", desktop);

        Assert.True(desktop.Client.Width >= 640 && desktop.Client.Height >= 480, $"{desktop.Client.Width} x {desktop.Client.Height}");
        Assert.NotEqual("", desktop.Client.Name);
        var frame = desktop.Frame();
        Assert.Equal(desktop.Client.Width * desktop.Client.Height * 4, frame.Length);

        // A desktop with a window on it: more than a handful of colours, and every pixel opaque.
        var colours = new HashSet<int>();
        for (var pixel = 0; pixel < frame.Length; pixel += 4)
        {
            colours.Add(frame[pixel] | (frame[pixel + 1] << 8) | (frame[pixel + 2] << 16));
            Assert.Equal(255, frame[pixel + 3]);
        }

        Assert.True(colours.Count >= 3, $"The picture has {colours.Count} colours.");
        output.WriteLine($"{desktop.Client.Name}: {desktop.Client.Width} x {desktop.Client.Height}, {colours.Count} colours, {desktop.Client.Rectangles(VncEncoding.Tight)} Tight rectangles.");

        // What this server can do, the client uses.
        Assert.True(desktop.Client.Rectangles(VncEncoding.Tight) > 0);
        Assert.True(desktop.Client.ContinuousUpdates);
        Assert.True(desktop.Client.ExtendedClipboard);
        Assert.True(desktop.Client.CanResize);
    }

    [DesktopFact]
    public async Task Typing_reaches_the_terminal()
    {
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();
        await desktop.TerminalAsync();
        await desktop.RunAsync("rm -f /tmp/vnc-test.txt");
        await desktop.RunAsync("echo vnc-ok > /tmp/vnc-test.txt");
        Assert.Equal("vnc-ok\n", await desktop.OutputOfAsync("cat /tmp/vnc-test.txt"));

        // Everything a keyboard has, upper case and the characters behind Shift included.
        const string All = "The quick brown fox JUMPS over 0123456789 !#%&()*+,-./:;<=>?@[]^_{|}~";
        Assert.Equal(All + "\n", await desktop.OutputOfAsync($"echo '{All}'"));
        await desktop.TidyAsync();
    }

    [DesktopFact]
    public async Task A_double_click_selects_a_word_in_the_terminal()
    {
        const string Word = "vncclickvncclickvncclickvncclickvncclickvncclickvncclickvncclick";
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();
        await desktop.TerminalAsync();

        // The terminal full of one word, so that a click anywhere in it hits the word.
        await desktop.RunAsync($"clear; for i in $(seq 40); do echo {Word}; done");
        await desktop.StillAsync();
        Save("before-click", desktop);

        // What is selected in an xterm is the X selection "PRIMARY", which this server passes on like its clipboard.
        var before = desktop.Handler.Read(seen => seen.Texts.Count);
        await desktop.ClickAsync(200, 250, times: 2);
        Assert.Equal(Word, await desktop.NextTextAsync(before, "the word the double click selected"));

        // And once more through a command, in case a server keeps PRIMARY to itself.
        Assert.Equal(Word, await desktop.OutputOfAsync("xclip -o -selection primary"));
        await desktop.TidyAsync();
    }

    [DesktopFact]
    public async Task Text_goes_to_the_servers_clipboard_and_comes_back()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();
        await desktop.TerminalAsync();

        // From Matgate to the server: UTF-8, two lines.
        var text = $"Grüße von Matgate – {token}\nzweite Zeile";
        desktop.Client.SetClipboardText(text);
        await desktop.RunAsync("xclip -o -selection clipboard > /tmp/vnc-clip.txt");

        // The server's clipboard, changed on the server and sent back: proof that it got there.
        var expected = string.Concat(text.Select(character => character is >= 'a' and <= 'z' ? char.ToUpperInvariant(character) : character));
        Assert.Equal(expected, await desktop.OutputOfAsync("xclip -o -selection clipboard | tr a-z A-Z"));
        Assert.Equal(text, await desktop.OutputOfAsync("cat /tmp/vnc-clip.txt"));

        // From the server to Matgate.
        Assert.Equal($"hello-{token}", await desktop.OutputOfAsync($"printf hello-{token}"));

        await desktop.TidyAsync();
    }

    [DesktopFact]
    public async Task The_desktop_takes_the_size_asked_for()
    {
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();
        Assert.True(desktop.Client.CanResize);
        var width = desktop.Client.Width;
        var height = desktop.Client.Height;
        try
        {
            desktop.Client.RequestSize(1280, 800);
            await desktop.Handler.UntilAsync(seen => seen.Sizes.Contains((1280, 800)), "the desktop at 1280 x 800");
            Assert.Equal((1280, 800), (desktop.Client.Width, desktop.Client.Height));
            lock (desktop.Client.FrameLock)
            {
                Assert.Equal(1280 * 800 * 4, desktop.Client.Framebuffer.Length);
            }

            // The server sends the picture for the new size by itself.
            await desktop.StillAsync();
            Save("resized", desktop);
        }
        finally
        {
            desktop.Client.RequestSize(width, height);
            await desktop.Handler.UntilAsync(seen => seen.Sizes.Count > 0 && seen.Sizes[^1] == (width, height), $"the desktop back at {width} x {height}");
        }

        await desktop.StillAsync();
        Assert.Equal(width * height * 4, desktop.Frame().Length);
    }

    [DesktopFact]
    public async Task The_pointers_shape_arrives()
    {
        await using var desktop = await ConnectAsync();
        await desktop.StillAsync();

        // Over the desktop's background, then over the terminal, where the pointer is another one.
        desktop.Client.Pointer(desktop.Client.Width - 10, desktop.Client.Height - 10, 0);
        await desktop.Handler.UntilAsync(seen => seen.Cursors.Count > 0, "a pointer shape");
        var before = desktop.Handler.Read(seen => seen.Cursors.Count);
        desktop.Client.Pointer(300, 300, 0);
        await desktop.Handler.UntilAsync(seen => seen.Cursors.Count > before, "another pointer shape over the terminal");

        var shape = desktop.Handler.Read(seen => seen.Cursors[^1]);
        output.WriteLine($"The pointer over the terminal: {shape.Width} x {shape.Height}, hot spot {shape.HotX}, {shape.HotY}.");
        Assert.InRange(shape.Width, 1, 256);
        Assert.InRange(shape.Height, 1, 256);
        Assert.InRange(shape.HotX, 0, shape.Width - 1);
        Assert.InRange(shape.HotY, 0, shape.Height - 1);
        Assert.Equal(shape.Width * shape.Height * 4, shape.Rgba.Length);

        // Some of it can be seen, some of it not - and nothing in between.
        var opaque = 0;
        for (var pixel = 3; pixel < shape.Rgba.Length; pixel += 4)
        {
            Assert.True(shape.Rgba[pixel] is 0 or 255);
            opaque += shape.Rgba[pixel] / 255;
        }

        Assert.InRange(opaque, 1, shape.Width * shape.Height - 1);
    }

    [DesktopFact]
    public async Task Every_encoding_shows_the_same_picture()
    {
        await using var reference = await ConnectAsync([VncEncoding.Raw]);
        await reference.StillAsync();
        Assert.Equal(0, reference.Client.Rectangles(VncEncoding.Tight) + reference.Client.Rectangles(VncEncoding.Zrle) + reference.Client.Rectangles(VncEncoding.Hextile));
        Assert.True(reference.Client.Rectangles(VncEncoding.Raw) > 0);

        foreach (var encoding in new[] { VncEncoding.Tight, VncEncoding.Zrle, VncEncoding.Hextile, VncEncoding.Rre })
        {
            await using var desktop = await ConnectAsync([encoding]);
            await AssertSamePictureAsync(reference, desktop, encoding.ToString());
            output.WriteLine($"{encoding}: {desktop.Client.Rectangles(encoding)} rectangles, {desktop.Client.Rectangles(VncEncoding.Raw)} raw ones besides.");
            Assert.True(desktop.Client.Rectangles(encoding) > 0, $"The server did not use {encoding}.");
        }

        // And everything together, as a session has it.
        await using var all = await ConnectAsync();
        await AssertSamePictureAsync(reference, all, "default");
    }

    [DesktopFact]
    public async Task A_window_that_is_dragged_is_copied_and_the_picture_stays_the_same()
    {
        await using var reference = await ConnectAsync([VncEncoding.Raw]);
        await using var desktop = await ConnectAsync([VncEncoding.CopyRect, VncEncoding.Tight]);
        await AssertSamePictureAsync(reference, desktop, "before the drag");
        var before = desktop.Frame();

        // The terminal by its title bar, some way to the right and down: the server says "what was there is
        // here now" instead of sending the window again.
        await DragAsync(desktop, 470, 29, 530, 69);
        await AssertSamePictureAsync(reference, desktop, "dragged");
        Save("dragged", desktop);
        output.WriteLine($"{desktop.Client.Rectangles(VncEncoding.CopyRect)} rectangles were copied.");
        Assert.True(desktop.Client.Rectangles(VncEncoding.CopyRect) > 0, "The server did not use CopyRect.");
        Assert.False(before.AsSpan().SequenceEqual(desktop.Frame()), "The window did not move.");

        // And back to where it was - exactly.
        await Task.Delay(700);
        await DragAsync(desktop, 530, 69, 470, 29);
        await AssertSamePictureAsync(reference, desktop, "dragged back");
        Assert.True(before.AsSpan().SequenceEqual(desktop.Frame()), "The window is not where it was before the test.");
    }

    [DesktopFact]
    public async Task A_wrong_password_is_refused()
    {
        var handler = new RecordingHandler();
        var exception = await Assert.ThrowsAsync<VncException>(() => Desktop.ConnectAsync(Options(password: "wrong-pw"), handler, output.WriteLine));
        Assert.Equal("The password was refused.", exception.Message);

        // The handler was asked for another one and had none.
        Assert.Equal(new[] { (false, true) }, handler.Asked);

        // With the right one from the handler the second attempt gets in. (It also takes this address off
        // the server's list of those that failed.)
        handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials(null, Password) };
        await using var desktop = await Desktop.ConnectAsync(Options(password: "wrong-pw"), handler, output.WriteLine);
        Assert.Equal(new[] { (false, true) }, handler.Asked);
        await desktop.StillAsync();
    }

    [DesktopFact]
    public async Task A_password_that_is_missing_is_asked_for()
    {
        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials(null, Password) };
        var options = new VncOptions { Host = Host!, Port = Port, ConnectTimeout = TimeSpan.FromSeconds(30) };
        await using var desktop = await Desktop.ConnectAsync(options, handler, output.WriteLine);
        Assert.Equal(new[] { (false, false) }, handler.Asked);
        await desktop.StillAsync();
    }

    // How long a full picture takes, from asking for it to having it - the server's packing, the line and
    // the client's unpacking together.
    [DesktopFact]
    public async Task Timing_of_a_full_update()
    {
        foreach (var encoding in new[] { VncEncoding.Raw, VncEncoding.Rre, VncEncoding.Hextile, VncEncoding.Zrle, VncEncoding.Tight })
        {
            await using var desktop = await Desktop.ConnectAsync(Options([encoding]));
            await desktop.StillAsync();
            output.WriteLine(await FullUpdateAsync(desktop, encoding));
        }
    }

    // ---- the servers of servers/start.sh --------------------------------------------------------------------

    [ServersFact]
    public async Task A_server_without_a_password()
    {
        var handler = new RecordingHandler();
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5901 }, handler, output.WriteLine);
        await desktop.StillAsync();
        Assert.Empty(handler.Asked);
        Assert.Equal((800, 600), (desktop.Client.Width, desktop.Client.Height));
    }

    [ServersFact]
    public async Task A_server_with_a_certificate_and_no_password()
    {
        var handler = new RecordingHandler();
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5902 }, handler, output.WriteLine);
        await desktop.StillAsync();
        Assert.Empty(handler.Asked);
    }

    [ServersFact]
    public async Task A_server_with_a_certificate_and_a_VNC_password()
    {
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5903, Password = "vnc-pw-2" }, log: output.WriteLine);
        await desktop.StillAsync();

        // The session itself is encrypted too: keys go in, the picture comes out.
        desktop.Client.Pointer(100, 100, 0);
        await desktop.Handler.UntilAsync(seen => seen.Cursors.Count > 0, "a pointer shape through TLS");

        var handler = new RecordingHandler();
        var exception = await Assert.ThrowsAsync<VncException>(() => Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5903, Password = "wrong" }, handler));
        Assert.Equal("The password was refused.", exception.Message);
        Assert.Equal(new[] { (false, true) }, handler.Asked);
    }

    [ServersFact]
    public async Task A_server_with_a_certificate_and_a_user_name()
    {
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5904, Username = "vnctest", Password = "plain-pw1" }, log: output.WriteLine);
        await desktop.StillAsync();

        // Without a user name in the options, the handler is asked for one.
        var handler = new RecordingHandler { Credentials = (_, _) => new VncCredentials("vnctest", "plain-pw1") };
        await using var asked = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5904, Password = "plain-pw1" }, handler);
        Assert.Equal(new[] { (true, false) }, handler.Asked);

        handler = new RecordingHandler();
        var exception = await Assert.ThrowsAsync<VncException>(() => Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5904, Username = "vnctest", Password = "wrong" }, handler));
        Assert.Equal("The password was refused.", exception.Message);
        Assert.Equal(new[] { (true, true) }, handler.Asked);
    }

    [ServersFact]
    public async Task A_certificate_that_is_checked_and_found_wanting()
    {
        var options = new VncOptions { Host = Servers!, Port = 5902, ValidateCertificate = true };
        var exception = await Assert.ThrowsAsync<VncException>(() => Desktop.ConnectAsync(options));
        Assert.Contains("is not trusted", exception.Message);
    }

    [ServersFact]
    public async Task A_server_with_a_user_name_and_no_encryption()
    {
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5905, Username = "vnctest", Password = "plain-pw1" }, log: output.WriteLine);
        await desktop.StillAsync();
    }

    [ServersFact]
    public async Task A_server_as_TigerVNC_comes_out_of_the_box()
    {
        // It offers TLS without a certificate and the VNC password, both inside VeNCrypt and the password
        // outside as well: the password it is, on the first connection.
        var lines = new List<string>();
        await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5906, Password = "vnc-pw-2" }, log: lines.Add);
        await desktop.StillAsync();
        Assert.DoesNotContain(lines, line => line.Contains("connecting again"));
    }

    [ServersFact]
    public async Task A_server_that_only_has_what_cannot_be_done()
    {
        var exception = await Assert.ThrowsAsync<VncException>(() => Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5907, Password = "x" }));
        output.WriteLine(exception.Message);
        Assert.Contains("offers no way of signing in that Matgate knows", exception.Message);
        Assert.Contains("(257)", exception.Message);
    }

    [ServersFact]
    public async Task Timing_of_a_full_update_in_full_HD()
    {
        foreach (var encoding in new[] { VncEncoding.Raw, VncEncoding.Rre, VncEncoding.Hextile, VncEncoding.Zrle, VncEncoding.Tight })
        {
            await using var desktop = await Desktop.ConnectAsync(new VncOptions { Host = Servers!, Port = 5908, Encodings = [encoding], ConnectTimeout = TimeSpan.FromSeconds(30) });
            await desktop.StillAsync();
            if (encoding == VncEncoding.Raw)
            {
                Save("full-hd", desktop);
            }

            Assert.Equal((1920, 1080), (desktop.Client.Width, desktop.Client.Height));
            output.WriteLine(await FullUpdateAsync(desktop, encoding));
        }
    }

    private static async Task DragAsync(Desktop desktop, int fromX, int fromY, int toX, int toY)
    {
        desktop.Client.Pointer(fromX, fromY, 0);
        await Task.Delay(100);
        desktop.Client.Pointer(fromX, fromY, 1);
        for (var step = 1; step <= 20; step++)
        {
            await Task.Delay(20);
            desktop.Client.Pointer(fromX + (toX - fromX) * step / 20, fromY + (toY - fromY) * step / 20, 1);
        }

        await Task.Delay(100);
        desktop.Client.Pointer(toX, toY, 0);
    }

    // Both connections show the same desktop, so once it stands still they hold the same picture - byte for byte.
    private async Task AssertSamePictureAsync(Desktop reference, Desktop desktop, string name)
    {
        // A TigerVNC server draws the pointer into the picture of a client that did not put it where it is, and
        // takes it out again only when the pointer really moves. So: one client to the corner, the other one
        // next to it and then into the corner too - a real move to where both have put it, after which
        // neither gets it drawn. (The two moves of one client are apart, or the queue makes them one.)
        var corner = (X: reference.Client.Width - 1, Y: reference.Client.Height - 1);
        reference.Client.Pointer(corner.X, corner.Y, 0);
        await Task.Delay(150);
        desktop.Client.Pointer(corner.X - 1, corner.Y - 1, 0);
        await Task.Delay(150);
        desktop.Client.Pointer(corner.X, corner.Y, 0);
        await Task.Delay(200);
        for (var attempt = 0; ; attempt++)
        {
            await reference.StillAsync();
            await desktop.StillAsync();
            var expected = reference.Frame();
            var actual = desktop.Frame();
            if (expected.AsSpan().SequenceEqual(actual))
            {
                return;
            }

            if (attempt == 5)
            {
                Save($"differs-{name}-reference", reference);
                Save($"differs-{name}", desktop);
                var different = 0;
                var first = -1;
                for (var index = 0; index < Math.Min(expected.Length, actual.Length); index += 4)
                {
                    if (!expected.AsSpan(index, 4).SequenceEqual(actual.AsSpan(index, 4)))
                    {
                        different++;
                        first = first < 0 ? index / 4 : first;
                    }
                }

                Assert.Fail($"{name}: {different} pixels differ from the raw picture, the first one at {first % reference.Client.Width}, {first / reference.Client.Width} ({expected.Length} and {actual.Length} bytes).");
            }

            // Something moved between the two looks; once more.
            await Task.Delay(500);
        }
    }

    // Asks for the whole picture a number of times and says how long it took, as the median: from asking
    // for it to having it (the server's packing, the line and the client together), and the client's share
    // of that - from the first byte of the update to the picture.
    private static async Task<string> FullUpdateAsync(Desktop desktop, VncEncoding encoding)
    {
        const int Rounds = 15;
        var whole = new List<double>();
        var client = new List<double>();
        var unpacking = new List<double>();
        var rectangles = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var before = desktop.Client.Rectangles(encoding);
            var done = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = new Stopwatch();
            desktop.Handler.OnUpdated = (x, y, width, height) =>
            {
                if ((x, y, width, height) == (0, 0, desktop.Client.Width, desktop.Client.Height))
                {
                    done.TrySetResult(watch.Elapsed.TotalMilliseconds);
                }
            };
            watch.Start();
            desktop.Client.RequestRefresh();
            whole.Add(await done.Task.WaitAsync(TimeSpan.FromSeconds(60)));
            desktop.Handler.OnUpdated = null;

            // The client notes its own time when the callback has returned.
            await Task.Delay(100);
            client.Add(desktop.Client.LastUpdateTime.TotalMilliseconds);
            unpacking.Add((desktop.Client.LastUpdateTime - desktop.Client.LastUpdateWait).TotalMilliseconds);
            rectangles = desktop.Client.Rectangles(encoding) - before;
        }

        whole.Sort();
        client.Sort();
        unpacking.Sort();
        return $"{encoding,-8} {desktop.Client.Width} x {desktop.Client.Height}: {whole[Rounds / 2],6:F1} ms from request to picture; in the client {client[Rounds / 2],6:F1} ms from the first byte, {unpacking[Rounds / 2],6:F1} ms of them not spent reading from the line ({rectangles} rectangles of this encoding in an update; median of {Rounds})";
    }
}
