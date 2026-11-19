using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// H.264 from the Mac's own encoder (VideoToolbox - the chip's, where there is one). Set up for a remote
// screen: no B-frames, real time, key frames only when the viewer asks for one. A picture goes in and its
// frame comes out before Encode returns.
internal sealed unsafe class VideoToolboxEncoder : IVideoEncoder
{
    private readonly Action<byte[], bool> _output;
    private readonly int _framesPerSecond;
    private GCHandle _self;
    private nint _session;
    private long _frames;
    private string? _failure;

    public VideoToolboxEncoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output)
    {
        Width = width;
        Height = height;
        _output = output;
        _framesPerSecond = framesPerSecond;
        _self = GCHandle.Alloc(this);
        // 'avc1'
        var status = Mac.VTCompressionSessionCreate(0, width, height, 0x61766331, 0, 0, 0, &OnFrame, GCHandle.ToIntPtr(_self), out _session);
        if (status != 0 || _session == 0)
        {
            _self.Free();
            throw new InvalidOperationException($"macOS has no H.264 encoder for a picture of {width}x{height} ({status}).");
        }

        Set("kVTCompressionPropertyKey_RealTime", Mac.True);
        Set("kVTCompressionPropertyKey_AllowFrameReordering", Mac.False);
        Set("kVTCompressionPropertyKey_ProfileLevel", Mac.VideoConstant("kVTProfileLevel_H264_Main_AutoLevel"));
        SetNumber("kVTCompressionPropertyKey_AverageBitRate", bitrate);
        SetNumber("kVTCompressionPropertyKey_ExpectedFrameRate", framesPerSecond);
        SetNumber("kVTCompressionPropertyKey_MaxKeyFrameInterval", 100_000);
        Mac.VTCompressionSessionPrepareToEncodeFrames(_session);
    }

    public int Width { get; }

    public int Height { get; }

    public string Name => "VideoToolbox";

    public bool Encode(byte[] nv12, bool keyFrame)
    {
        // '420v': two planes, the colours interleaved - what the session's pictures are.
        if (Mac.CVPixelBufferCreate(0, (nuint)Width, (nuint)Height, 0x34323076, 0, out var buffer) != 0 || buffer == 0)
        {
            throw new InvalidOperationException("macOS gave no memory for a picture.");
        }

        nint properties = 0;
        try
        {
            Mac.CVPixelBufferLockBaseAddress(buffer, 0);
            fixed (byte* source = nv12)
            {
                Copy(source, Width, (byte*)Mac.CVPixelBufferGetBaseAddressOfPlane(buffer, 0), (int)Mac.CVPixelBufferGetBytesPerRowOfPlane(buffer, 0), Width, Height);
                Copy(source + Width * Height, Width, (byte*)Mac.CVPixelBufferGetBaseAddressOfPlane(buffer, 1), (int)Mac.CVPixelBufferGetBytesPerRowOfPlane(buffer, 1), Width, Height / 2);
            }

            Mac.CVPixelBufferUnlockBaseAddress(buffer, 0);
            if (keyFrame)
            {
                properties = Mac.Dictionary(Mac.VideoConstant("kVTEncodeFrameOptionKey_ForceKeyFrame"), Mac.True);
            }

            var time = new CMTime { Value = _frames++, Timescale = _framesPerSecond, Flags = 1 };
            var status = Mac.VTCompressionSessionEncodeFrame(_session, buffer, time, default, properties, 0, out _);
            if (status == 0)
            {
                status = Mac.VTCompressionSessionCompleteFrames(_session, default);
            }

            if (status != 0 || _failure is not null)
            {
                throw new InvalidOperationException($"The Mac's encoder failed ({_failure ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
            }

            return true;
        }
        finally
        {
            if (properties != 0)
            {
                Mac.CFRelease(properties);
            }

            Mac.CFRelease(buffer);
        }
    }

    private static void Copy(byte* source, int sourcePitch, byte* target, int targetPitch, int width, int rows)
    {
        if (target is null)
        {
            return;
        }

        for (var y = 0; y < rows; y++)
        {
            Buffer.MemoryCopy(source + (long)y * sourcePitch, target + (long)y * targetPitch, targetPitch, width);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(nint context, nint frameContext, int status, uint flags, nint sample)
    {
        if (GCHandle.FromIntPtr(context).Target is not VideoToolboxEncoder encoder)
        {
            return;
        }

        try
        {
            if (status != 0 || sample == 0)
            {
                encoder._failure = "status " + status;
                return;
            }

            encoder.Deliver(sample);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            encoder._failure = exception.Message;
        }
    }

    // The frame as macOS hands it over has each unit led by its length; a browser's decoder wants the start
    // code instead - and, before a key frame, the two parameter sets the frame's description carries.
    private void Deliver(nint sample)
    {
        var block = Mac.CMSampleBufferGetDataBuffer(sample);
        if (block == 0)
        {
            return;
        }

        var length = (int)Mac.CMBlockBufferGetDataLength(block);
        var data = new byte[length];
        fixed (byte* target = data)
        {
            if (Mac.CMBlockBufferCopyDataBytes(block, 0, (nuint)length, target) != 0)
            {
                return;
            }
        }

        var key = false;
        for (var at = 0; at + 4 <= length;)
        {
            var size = (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
            if (size <= 0 || at + 4 + size > length)
            {
                break;
            }

            key |= (data[at + 4] & 0x1F) == 5;
            data[at] = data[at + 1] = data[at + 2] = 0;
            data[at + 3] = 1;
            at += 4 + size;
        }

        if (!key)
        {
            _output(data, false);
            return;
        }

        using var stream = new MemoryStream();
        var description = Mac.CMSampleBufferGetFormatDescription(sample);
        for (nuint index = 0; description != 0 && index < 4; index++)
        {
            if (Mac.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, index, out var set, out var size, out var count, out _) != 0 || set == 0)
            {
                break;
            }

            stream.Write([0, 0, 0, 1]);
            stream.Write(new ReadOnlySpan<byte>((void*)set, (int)size));
            if (index + 1 >= count)
            {
                break;
            }
        }

        stream.Write(data);
        _output(stream.ToArray(), true);
    }

    private void Set(string key, nint value)
    {
        var name = Mac.VideoConstant(key);
        if (name != 0 && value != 0)
        {
            Mac.VTSessionSetProperty(_session, name, value);
        }
    }

    private void SetNumber(string key, int value)
    {
        var number = Mac.Number(value);
        Set(key, number);
        Mac.CFRelease(number);
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            Mac.VTCompressionSessionInvalidate(_session);
            Mac.CFRelease(_session);
            _session = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}

// macOS: the screen of the window server, captured by a helper process inside the session of whoever is
// signed in at the screen. The system's service (a LaunchDaemon, root) has no window server of its own; it
// starts the helper in that user's session ("launchctl asuser" - "bsexec" into the loginwindow process no
// longer reaches a screen: tried on macOS 26). When somebody signs in or out the console changes hands, the
// helper ends and a new one is started. The sign-in window itself belongs to nobody's session and is not shown.
internal sealed class MacScreenPlatform : ScreenPlatform
{
    private readonly int _consoleAtStart = Mac.ConsoleUser();

    public override bool UsesHelper => !ScreenPlugin.TestMode;

    public override bool IsSystem => UnixInstaller.IsRoot;

    public override Process StartHelper(string arguments)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        if (UnixInstaller.IsRoot)
        {
            var console = Mac.ConsoleUser();
            if (console <= 0)
            {
                throw new InvalidOperationException("Nobody is signed in at this Mac - its sign-in window cannot be shown yet.");
            }

            start.FileName = "/bin/launchctl";
            start.ArgumentList.Add("asuser");
            start.ArgumentList.Add(console.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(Environment.ProcessPath!);
        }
        else
        {
            // Started by somebody in their own session: the helper is simply theirs.
            start.FileName = Environment.ProcessPath!;
        }

        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The screen helper did not start.");
    }

    public override bool HelperMisplaced() => Mac.ConsoleUser() != _consoleAtStart;

    public override IReadOnlyList<MonitorInfo> Monitors() => MacMonitors.List().Select(monitor => monitor.Info).ToList();

    public override IScreenSource Source(int monitor) => new MacCapture(monitor);

    public override IVideoEncoder Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output) =>
        new VideoToolboxEncoder(width, height, framesPerSecond, bitrate, output);

    public override IInputSink Input()
    {
        if (!MacInput.Allowed)
        {
            AgentLog.Write("Screen: macOS does not let the agent use mouse and keyboard - allow it under System Settings > Privacy & Security > Accessibility.");
        }

        return new MacInput();
    }

    public override IClipboardBridge Clipboard() => new MacClipboard();

    public override IDisplayControl Display() => new FixedDisplay();

    public override IWindowList Windows() => new NoWindows();

    public override bool IsCaptureFailure(Exception exception) => exception is InvalidOperationException or EntryPointNotFoundException or DllNotFoundException;

    // On a Mac "paste" is Command+V - with Control held it would be something else.
    public override void Paste(IInputSink input, bool controlHeld)
    {
        if (controlHeld)
        {
            input.Key(0x1D, false, false);
        }

        input.Key(0x5B, true, true);
        input.Key(0x2F, false, true);
        input.Key(0x2F, false, false);
        input.Key(0x5B, true, false);
        if (controlHeld)
        {
            input.Key(0x1D, false, true);
        }
    }

    public override IEnumerable<string> Describe()
    {
        yield return "console:  user " + Mac.ConsoleUser() + ", agent runs as " + Environment.UserName;
        yield return "allowed:  screen recording " + (Mac.CGPreflightScreenCaptureAccess() != 0 ? "yes" : "no") + ", accessibility " + (MacInput.Allowed ? "yes" : "no");
    }

    // A Mac's resolution is left as it is.
    private sealed class FixedDisplay : IDisplayControl
    {
        public IReadOnlyList<(int Width, int Height)> Modes(MonitorInfo monitor) => [];

        public (int Width, int Height) Saved(MonitorInfo monitor) => (monitor.Width, monitor.Height);

        public bool Apply(MonitorInfo monitor, int width, int height) => false;

        public void Restore(MonitorInfo monitor)
        {
        }
    }

    // The list of windows is not offered on a Mac yet.
    private sealed class NoWindows : IWindowList
    {
        public IReadOnlyList<WindowInfo> List() => [];

        public WindowBounds? Bounds(long id) => null;

        public bool Activate(long id) => false;
    }
}
