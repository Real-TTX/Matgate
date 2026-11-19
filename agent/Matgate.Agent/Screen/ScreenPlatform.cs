using System.Diagnostics;

namespace Matgate.Agent.Screen;

// What the screen asks of the system it runs on. The session - pacing, the viewer's settings, one window as
// the picture, the clipboard's protocol - is the same everywhere; where pictures come from, how they become
// H.264, how a key is pressed and which process may see the monitor differs.
internal abstract class ScreenPlatform
{
    public const int MaxPads = 4;

    public const int AudioSampleRate = 48000;

    public static ScreenPlatform Current { get; } = Create();

    private static ScreenPlatform Create()
    {
#if AGENT_WINDOWS
        return new WindowsScreenPlatform();
#else
        return OperatingSystem.IsMacOS() ? new Unix.Screen.MacScreenPlatform() : new Unix.Screen.LinuxScreenPlatform();
#endif
    }

    // Whether this build can show a screen on this machine at all (the libraries it needs are there).
    public virtual bool Supported => true;

    // Why not, for the log and the setup.
    public virtual string? UnsupportedReason => null;

    // Whether the screen is captured by a helper process rather than by the agent itself.
    public abstract bool UsesHelper { get; }

    // Whether this very process sits where a monitor can be seen.
    public virtual bool CanCaptureHere => true;

    // The agent is the system's own service (LocalSystem, root).
    public virtual bool IsSystem => false;

    // Starts `matgate-agent <arguments>` where the monitor is.
    public abstract Process StartHelper(string arguments);

    // A helper that is no longer where the monitor is ends, and the agent starts a new one there.
    public virtual bool HelperMisplaced() => false;

    public abstract IReadOnlyList<MonitorInfo> Monitors();

    public abstract IScreenSource Source(int monitor);

    public abstract IVideoEncoder Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output);

    public abstract IInputSink Input();

    public abstract IClipboardBridge Clipboard();

    public abstract IDisplayControl Display();

    public abstract IWindowList Windows();

    // Moves the calling thread to the desktop the monitor shows (Windows: sign-in, lock screen, UAC).
    public virtual bool FollowDesktop() => true;

    // An error that means "no picture right now" rather than a bug.
    public virtual bool IsCaptureFailure(Exception exception) => exception is InvalidOperationException;

    // True when the encoder that failed was one this platform has a replacement for: the session makes a new one.
    public virtual bool EncoderFailed(IVideoEncoder encoder, Exception exception) => false;

    // Ctrl+Alt+Del. `fromService`: asked of the agent itself, not of its helper.
    public virtual bool AttentionFromService => false;

    public virtual bool SecureAttention(IInputSink input) => false;

    public virtual string MicrophoneState(string setting) => "off";

    // "ok", "nodriver" or "off" - asked only where controllers are allowed.
    public virtual string GamepadState => "off";

    public virtual IGamepads Gamepads(Action<int, byte, byte> rumble) =>
        throw new InvalidOperationException("This system takes no controllers from the browser yet.");

    public virtual IMicrophone Microphone(string setting, out string name) => throw new InvalidOperationException("off");

    public virtual IDisposable Audio(bool opus, Action<byte[]> packet) =>
        throw new NotSupportedException("This system's sound is not sent yet.");

    // "Paste", as the machine's keyboard says it: Ctrl+V - with Ctrl only pressed when the viewer does not
    // hold it already.
    public virtual void Paste(IInputSink input, bool controlHeld)
    {
        if (!controlHeld)
        {
            input.Key(0x1D, false, true);
        }

        input.Key(0x2F, false, true);
        input.Key(0x2F, false, false);
        if (!controlHeld)
        {
            input.Key(0x1D, false, false);
        }
    }

    // What "screen-probe" says of this system beyond the picture.
    public virtual IEnumerable<string> Describe() => [];

    // Where files pasted by the viewer land.
    public virtual string ClipboardTemp() => Path.GetTempPath();
}

#if AGENT_WINDOWS
internal sealed class WindowsScreenPlatform : ScreenPlatform
{
    public override bool UsesHelper =>
        SessionNative.IsSystemService || Environment.GetEnvironmentVariable("MATGATE_AGENT_SCREEN_HOST") == "1";

    public override bool CanCaptureHere => SessionNative.CurrentSession != 0;

    public override bool IsSystem => SessionNative.IsLocalSystem;

    public override Process StartHelper(string arguments)
    {
        if (!SessionNative.IsSystemService)
        {
            // The tests: a helper of this very user.
            return SessionNative.StartChild(arguments);
        }

        var console = SessionNative.ConsoleSession();
        if (console < 0)
        {
            throw new InvalidOperationException("The machine shows no screen right now.");
        }

        return SessionNative.StartAsSystem(console, arguments);
    }

    public override bool HelperMisplaced() =>
        SessionNative.IsLocalSystem && SessionNative.ConsoleSession() is var console and >= 0 && console != SessionNative.CurrentSession;

    public override IReadOnlyList<MonitorInfo> Monitors() => DesktopCapture.Monitors();

    public override IScreenSource Source(int monitor) => new DesktopCapture(monitor);

    public override IVideoEncoder Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output) =>
        VideoEncoders.Create(width, height, framesPerSecond, bitrate, output);

    public override IInputSink Input() => new InputInjector();

    public override IClipboardBridge Clipboard() => new WindowsClipboard();

    public override IDisplayControl Display() => new WindowsDisplay();

    public override IWindowList Windows() => new WindowsWindowList();

    public override bool FollowDesktop() => InputDesktop.Follow();

    public override bool IsCaptureFailure(Exception exception) =>
        exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or System.Runtime.InteropServices.COMException;

    public override bool EncoderFailed(IVideoEncoder encoder, Exception exception)
    {
        if (encoder is not HardwareH264Encoder || exception is OutOfMemoryException)
        {
            return false;
        }

        VideoEncoders.HardwareFailed(exception);
        return true;
    }

    // Only Windows itself may send it, and only for a LocalSystem service.
    public override bool AttentionFromService => true;

    public override bool SecureAttention(IInputSink input) => SessionNative.IsLocalSystem && SessionNative.SendSecureAttention();

    public override string MicrophoneState(string setting) => MicrophoneDevices.State(setting);

    public override string GamepadState => ViGEmGamepads.Installed ? "ok" : "nodriver";

    public override IGamepads Gamepads(Action<int, byte, byte> rumble) => new ViGEmGamepads(rumble);

    public override IMicrophone Microphone(string setting, out string name)
    {
        if (MicrophoneDevices.IsOff(setting))
        {
            throw new InvalidOperationException("off");
        }

        var player = new MicrophonePlayer(setting);
        name = player.Name;
        return player;
    }

    public override IDisposable Audio(bool opus, Action<byte[]> packet) => new AudioStreamer(opus, packet);

    // A helper running as SYSTEM puts pasted files into the signed-in person's temp folder - their Explorer
    // has to read them.
    public override string ClipboardTemp() => SessionNative.IsLocalSystem
        ? SessionNative.SignedInUserTemp() ?? throw new InvalidOperationException("Nobody is signed in to paste files for.")
        : Path.GetTempPath();
}
#endif

// A picture as PNG, written here: a pointer's shape, on every system.
internal static class PngWriter
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    // `bgra`: four bytes a pixel (blue, green, red, alpha - not premultiplied), row after row.
    public static byte[] FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * 4;
                var to = row + 1 + x * 4;
                raw[to] = bgra[from + 2];
                raw[to + 1] = bgra[from + 1];
                raw[to + 2] = bgra[from];
                raw[to + 3] = bgra[from + 3];
            }
        }

        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(name);
        png.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, name), data) ^ 0xFFFFFFFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        png.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
