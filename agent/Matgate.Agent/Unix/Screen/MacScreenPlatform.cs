using System.Diagnostics;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// macOS: not yet.
internal sealed class MacScreenPlatform : ScreenPlatform
{
    public override bool Supported => false;

    public override string? UnsupportedReason => "The agent does not show a Mac's screen yet.";

    public override bool UsesHelper => !ScreenPlugin.TestMode;

    public override Process StartHelper(string arguments) => throw new InvalidOperationException(UnsupportedReason);

    public override IReadOnlyList<MonitorInfo> Monitors() => [];

    public override IScreenSource Source(int monitor) => throw new InvalidOperationException(UnsupportedReason);

    public override IVideoEncoder Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output) =>
        throw new InvalidOperationException(UnsupportedReason);

    public override IInputSink Input() => NoInput.Instance;

    public override IClipboardBridge Clipboard() => new RecordingClipboard(_ => { });

    public override IDisplayControl Display() => new TestDisplay(_ => { });

    public override IWindowList Windows() => new TestWindowList(_ => { });
}
