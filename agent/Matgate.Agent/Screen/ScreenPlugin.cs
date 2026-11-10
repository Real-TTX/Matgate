using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Matgate.Agent.Screen;

// The screen as a plugin: the machine's real desktop as H.264 video in the browser, with mouse, keyboard,
// sound and a clipboard that works in both directions (text, pictures and files). Like AnyDesk rather
// than RDP: it shows what the monitor shows.
//
// Where it runs: in this process when the agent runs as the signed-in user (user mode); through a helper in
// the console session when the agent is a LocalSystem service (session 0 has no screen) - then also the
// sign-in screen, the lock screen, UAC prompts and administrator windows, and Ctrl+Alt+Del.
//
// One viewer at a time: a second one takes the screen over, the first is told so.
public sealed class ScreenPlugin
{
    private readonly AgentConfig _config;
    private readonly object _gate = new();
    private readonly bool _helper;
    private readonly HostLink? _host;
    private readonly ConcurrentDictionary<string, Viewer> _viewers = new();
    private ScreenSession? _session;

    // inThisProcess: the helper itself, which always does the work here.
    public ScreenPlugin(AgentConfig config, bool inThisProcess = false)
    {
        _config = config;
        _helper = !inThisProcess && OperatingSystem.IsWindows()
            && (SessionNative.IsSystemService || Environment.GetEnvironmentVariable("MATGATE_AGENT_SCREEN_HOST") == "1");
        if (_helper)
        {
            _host = new HostLink("screen", StartHelper, null, OnHelperEvent, OnHelperExit);
        }
    }

    // MATGATE_AGENT_SCREEN_TEST=1: a generated picture, and input/clipboard reported instead of done -
    // the tests must not move anyone's mouse or overwrite anyone's clipboard.
    public static bool TestMode => Environment.GetEnvironmentVariable("MATGATE_AGENT_SCREEN_TEST") == "1";

    public bool IsAvailable => OperatingSystem.IsWindows() && _config.Screen && (TestMode || _helper || SessionNative.CurrentSession != 0);

    public static JsonArray Monitors()
    {
        var list = new JsonArray();
        foreach (var monitor in TestMode ? TestPattern.Monitors() : DesktopCapture.Monitors())
        {
            list.Add(new JsonObject
            {
                ["index"] = monitor.Index,
                ["name"] = monitor.Name,
                ["width"] = monitor.Width,
                ["height"] = monitor.Height,
                ["primary"] = monitor.Primary,
            });
        }

        return list;
    }

    public async Task<AgentFrame> OpenAsync(AgentFrame request, Func<AgentFrame, Task> send)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("This agent does not share its screen - it has to run as the signed-in user or as a LocalSystem service.");
        }

        var id = request.Text("session");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            id = Guid.NewGuid().ToString("N");
        }

        if (_helper)
        {
            var data = (JsonObject)(request.Data?.DeepClone() ?? new JsonObject());
            data["session"] = id;
            var viewer = new Viewer(id, send, data);
            _viewers[id] = viewer;
            try
            {
                var opened = await OpenInHelperAsync(viewer);
                return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = opened };
            }
            catch
            {
                _viewers.TryRemove(id, out _);
                throw;
            }
        }

        var session = new ScreenSession(id, send, request);
        ScreenSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = session;
        }

        previous?.Stop("taken");
        var monitor = await Task.Run(session.Start);
        return new AgentFrame
        {
            Id = request.Id,
            Kind = "res",
            Op = request.Op,
            Data = new JsonObject
            {
                ["session"] = id,
                ["monitor"] = monitor.Index,
                ["width"] = monitor.Width,
                ["height"] = monitor.Height,
                ["monitors"] = Monitors(),
            },
        };
    }

    public void Event(AgentFrame frame)
    {
        if (_helper)
        {
            var id = frame.Text("session") ?? "";
            if (!_viewers.TryGetValue(id, out var viewer))
            {
                return;
            }

            switch (frame.Op)
            {
                case "screen.sas" when !TestMode:
                    // Ctrl+Alt+Del: Windows takes it from the service itself.
                    if (!SessionNative.IsLocalSystem || !SessionNative.SendSecureAttention())
                    {
                        _ = viewer.Send(Status(id, "nocad"));
                    }

                    return;
                case "screen.close":
                    _viewers.TryRemove(id, out _);
                    break;
                case "screen.config":
                    // Kept for a new helper (another session): it opens with the latest wishes.
                    foreach (var (name, value) in frame.Data ?? [])
                    {
                        viewer.Open[name] = value?.DeepClone();
                    }

                    break;
            }

            _host!.Post(frame.Op, frame.Data ?? [], frame.Payload);
            return;
        }

        var session = Find(frame.Text("session"));
        if (session is null)
        {
            return;
        }

        switch (frame.Op)
        {
            case "screen.input":
                session.Input(frame);
                break;
            case "screen.ack":
                session.Ack(frame.Long("seq"));
                break;
            case "screen.key":
                session.RequestKeyFrame();
                break;
            case "screen.config":
                session.Configure(frame);
                break;
            case "screen.audio":
                session.Audio(frame.Bool("on"), frame.Text("codec") ?? "opus");
                break;
            case "screen.sas":
                session.SecureAttention();
                break;
            case "screen.clip.set":
            case "screen.clip.begin":
            case "screen.clip.chunk":
            case "screen.clip.end":
                session.Clip(frame);
                break;
            case "screen.close":
                Close(session, "closed");
                break;
        }
    }

    // A file the machine copied, read in pieces for a download in the browser.
    public async Task<AgentFrame> ReadClipAsync(AgentFrame request)
    {
        if (_helper)
        {
            var answer = await _host!.RequestAsync("screen.clip.read", request.Data ?? []);
            return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = answer.Data, Payload = answer.Payload };
        }

        var session = Find(request.Text("session")) ?? throw new InvalidOperationException("This screen session has ended.");
        var (data, payload) = session.ReadClipFile(request.Text("token") ?? "", request.Int("index", -1), request.Long("offset"));
        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = data, Payload = payload };
    }

    // Matgate's connection went away: a screen nobody can see any more stops.
    public void DetachAll(Func<AgentFrame, Task> send)
    {
        if (_helper)
        {
            foreach (var viewer in _viewers.Values.Where(viewer => Equals(viewer.Send, send)).ToList())
            {
                _viewers.TryRemove(viewer.Id, out _);
                _host!.Post("screen.close", new JsonObject { ["session"] = viewer.Id });
            }

            return;
        }

        ScreenSession? session;
        lock (_gate)
        {
            session = _session is not null && Equals(_session.Send, send) ? _session : null;
        }

        if (session is not null)
        {
            Close(session, "gone");
        }
    }

    private void Close(ScreenSession session, string reason)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        session.Stop(reason);
    }

    private ScreenSession? Find(string? id)
    {
        lock (_gate)
        {
            return _session is not null && _session.Id == id ? _session : null;
        }
    }

    // ---- through the helper ------------------------------------------------------------------------------

    private static Process StartHelper(string arguments)
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

    private async Task<JsonObject> OpenInHelperAsync(Viewer viewer)
    {
        await _host!.EnsureStartedAsync();
        var answer = await _host.RequestAsync("screen.open", (JsonObject)viewer.Open.DeepClone(), timeout: TimeSpan.FromSeconds(20));
        return answer.Data ?? [];
    }

    private void OnHelperEvent(AgentFrame frame)
    {
        if (!_viewers.TryGetValue(frame.Text("session") ?? "", out var viewer))
        {
            return;
        }

        if (frame.Op == "screen.ended")
        {
            _viewers.TryRemove(viewer.Id, out _);
        }

        // In order and at the browser's pace, as in this process.
        try
        {
            viewer.Send(frame).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
    }

    // The helper ended - usually because the monitor now shows another session (someone signed out, or the
    // sign-in screen gave way to a desktop). A new helper takes over there, and whoever watched sees it again.
    private void OnHelperExit()
    {
        foreach (var viewer in _viewers.Values)
        {
            _ = viewer.Send(Status(viewer.Id, "switching"));
        }

        if (!_viewers.IsEmpty)
        {
            _ = Task.Run(ReopenAsync);
        }
    }

    private int _reopening;

    private async Task ReopenAsync()
    {
        if (Interlocked.Exchange(ref _reopening, 1) == 1)
        {
            return;
        }

        try
        {
            for (var attempt = 0; attempt < 40 && !_viewers.IsEmpty; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 2));
                // The newest viewer only: the helper takes one at a time anyway.
                var viewer = _viewers.Values.LastOrDefault();
                if (viewer is null)
                {
                    return;
                }

                try
                {
                    var opened = await OpenInHelperAsync(viewer);
                    opened["session"] = viewer.Id;
                    await viewer.Send(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = opened });
                    return;
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    AgentLog.Write("Screen: waiting for the new session - " + exception.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reopening, 0);
        }
    }

    private static AgentFrame Status(string session, string state) => new()
    {
        Kind = "evt",
        Op = "screen.status",
        Data = new JsonObject { ["session"] = session, ["state"] = state },
    };

    private sealed class Viewer(string id, Func<AgentFrame, Task> send, JsonObject open)
    {
        public string Id { get; } = id;

        public Func<AgentFrame, Task> Send { get; } = send;

        // What the screen was opened with, updated by every change of settings.
        public JsonObject Open { get; } = open;
    }
}

// Input on a thread of its own: it can follow the monitor onto the sign-in screen and back (a thread that owns
// windows could not), and nothing overtakes anything else.
internal sealed class InputThread : IInputSink, IDisposable
{
    private readonly BlockingCollection<Action<IInputSink>> _queue = new();
    private readonly IInputSink _sink;
    private readonly Thread _thread;

    public InputThread(IInputSink sink)
    {
        _sink = sink;
        _thread = new Thread(Run) { IsBackground = true, Name = "Matgate input" };
        _thread.Start();
    }

    public void MoveTo(int x, int y) => Add(sink => sink.MoveTo(x, y));

    public void Button(int button, bool down) => Add(sink => sink.Button(button, down));

    public void Wheel(int vertical, int horizontal) => Add(sink => sink.Wheel(vertical, horizontal));

    public void Key(int scanCode, bool extended, bool down) => Add(sink => sink.Key(scanCode, extended, down));

    public void Text(string text) => Add(sink => sink.Text(text));

    private void Add(Action<IInputSink> action)
    {
        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // Ended.
        }
    }

    private void Run()
    {
        var followed = 0L;
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            if (Environment.TickCount64 - followed > 250)
            {
                InputDesktop.Follow();
                followed = Environment.TickCount64;
            }

            try
            {
                action(_sink);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AgentLog.Write("Screen input: " + exception.Message);
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

internal sealed unsafe class ScreenSession
{
    private const int MaxInFlight = 3;
    private const int ScanControl = 0x1D;
    private const int ScanV = 0x2F;

    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource<MonitorInfo> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inputGate = new();
    private readonly HashSet<(int Code, bool Extended)> _keysDown = [];
    private readonly HashSet<int> _buttonsDown = [];
    private readonly Dictionary<string, Upload> _uploads = [];
    private readonly ConcurrentDictionary<long, long> _sentAt = new();
    private readonly InputThread _input;
    private readonly IClipboardBridge _clipboard;
    private readonly IDisplayControl _display;
    private Thread? _thread;
    private volatile bool _running = true;
    private volatile bool _keyRequested = true;
    private volatile bool _reconfigure;
    private volatile bool _resolutionDirty = true;
    private int _monitor;
    private int _maxWidth;
    private string _quality;
    private int _fps;
    private bool _fpsGiven;
    private string _resolution;
    private int _fitWidth;
    private int _fitHeight;
    private MonitorInfo? _changedDisplay;
    private long _sent;
    private long _acked;
    private DateTime _lastAck = DateTime.UtcNow;
    private MonitorInfo _current = new(0, "", 0, 0, 0, 0, true);
    private IDisposable? _audio;
    private string _clipToken = "";
    private IReadOnlyList<string> _clipFiles = [];

    // What the statistics count, per second.
    private long _statFrames;
    private long _statBytes;
    private long _statRttTicks;
    private long _statRttCount;
    private double _statAgentMs;
    private string _encoderName = "";
    private bool _followWarned;

    public ScreenSession(string id, Func<AgentFrame, Task> send, AgentFrame request)
    {
        Id = id;
        Send = send;
        _monitor = request.Int("monitor", 0);
        _maxWidth = Math.Clamp(request.Int("maxWidth", 2560), 640, 7680);
        _quality = request.Text("quality") ?? "auto";
        _fpsGiven = request.Data?["fps"] is not null;
        _fps = _fpsGiven ? Math.Clamp(request.Int("fps", 30), 5, 60) : FramesFor(_quality);
        _resolution = request.Text("resolution") ?? "native";
        _fitWidth = request.Int("fitWidth", 0);
        _fitHeight = request.Int("fitHeight", 0);
        if (ScreenPlugin.TestMode)
        {
            _input = new InputThread(new RecordingInput(Debug));
            _clipboard = new RecordingClipboard(Debug);
            _display = new TestDisplay(Debug);
        }
        else
        {
            _input = new InputThread(new InputInjector());
            _clipboard = new WindowsClipboard();
            _display = new WindowsDisplay();
        }

        _clipboard.Changed += OnClipboardChanged;
    }

    public string Id { get; }

    public Func<AgentFrame, Task> Send { get; }

    public MonitorInfo Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Matgate screen" };
        _thread.Start();
        if (!_opened.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            Stop("error");
            throw new InvalidOperationException("The screen could not be captured.");
        }

        return _opened.Task.Result;
    }

    public void Stop(string reason)
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _wake.Set();
        ReleaseAll();
        // The monitor gets its own resolution back before anyone else changes it (a viewer taking over).
        if (_thread is not null && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _input.Dispose();
        _audio?.Dispose();
        _audio = null;
        _clipboard.Changed -= OnClipboardChanged;
        _clipboard.Dispose();
        lock (_uploads)
        {
            foreach (var upload in _uploads.Values)
            {
                upload.Dispose();
            }

            _uploads.Clear();
        }

        if (reason != "gone")
        {
            Emit("screen.ended", new JsonObject { ["reason"] = reason });
        }
    }

    // ---- picture -------------------------------------------------------------------------------------

    private void Loop()
    {
        IScreenSource? source = null;
        IVideoEncoder? encoder = null;
        byte[] picture = [];
        var scale = 1;
        var hasPicture = false;
        var refine = 0;
        var lastEncode = DateTime.MinValue;
        var pointer = new PointerState();
        var unavailable = false;
        var opened = false;
        var statClock = Stopwatch.StartNew();
        try
        {
            while (_running)
            {
                // The source of pictures: made (again) at the start, after the monitor switched desktops or
                // resolution, and when another monitor was chosen.
                if (source is null)
                {
                    try
                    {
                        if (!InputDesktop.Follow() && SessionNative.IsLocalSystem && !_followWarned)
                        {
                            // As SYSTEM this should always work; if not, the sign-in and lock screen stay dark.
                            _followWarned = true;
                            AgentLog.Write("Screen: could not move to the desktop the monitor shows (sign-in, lock screen, UAC).");
                        }

                        source = CreateSource(_monitor);
                    }
                    catch (Exception exception) when (IsCaptureFailure(exception))
                    {
                        if (!opened)
                        {
                            _opened.TrySetException(exception);
                            return;
                        }

                        // The lock screen or a UAC prompt, for a process that may not see them.
                        if (!unavailable)
                        {
                            unavailable = true;
                            Emit("screen.status", new JsonObject { ["state"] = "unavailable" });
                        }

                        _wake.WaitOne(500);
                        continue;
                    }

                    if (!opened)
                    {
                        opened = true;
                        _current = source.Monitor;
                        _opened.TrySetResult(source.Monitor);
                    }
                }

                if (_reconfigure)
                {
                    _reconfigure = false;
                    encoder?.Dispose();
                    encoder = null;
                    if (source.Monitor.Index != _monitor)
                    {
                        RestoreDisplay();
                        source.Dispose();
                        source = null;
                        _resolutionDirty = true;
                        continue;
                    }
                }

                if (_resolutionDirty)
                {
                    _resolutionDirty = false;
                    if (ApplyResolution(source.Monitor))
                    {
                        // The monitor has its new mode now: capture afresh.
                        source.Dispose();
                        source = null;
                        continue;
                    }
                }

                if (encoder is not null && (source.Monitor.Width != _current.Width || source.Monitor.Height != _current.Height))
                {
                    // The resolution changed under way.
                    encoder.Dispose();
                    encoder = null;
                }

                if (encoder is null)
                {
                    _current = source.Monitor;
                    // A resolution chosen for the viewer (fitted or picked) is shown as it is; the machine's own one is
                    // halved where it is far larger than the viewer's screen.
                    scale = _current.Width > (_resolution == "native" ? _maxWidth : Math.Max(_maxWidth, 3840)) ? 2 : 1;
                    var (width, height) = Nv12.Scaled(_current.Width, _current.Height, scale);
                    encoder = VideoEncoders.Create(width, height, _fps, Bitrate(width, height), (bytes, key) => SendVideo(bytes, key, width, height));
                    _encoderName = encoder.Name;
                    picture = new byte[Nv12.Size(width, height)];
                    hasPicture = false;
                    _keyRequested = true;
                    EmitMonitor(width, height);
                }

                if (statClock.ElapsedMilliseconds >= 1000)
                {
                    EmitStats(statClock.Elapsed.TotalSeconds);
                    statClock.Restart();
                }

                // The browser is behind: wait for it instead of piling up pictures it would show late.
                if (Interlocked.Read(ref _sent) - Interlocked.Read(ref _acked) >= MaxInFlight)
                {
                    if (DateTime.UtcNow - _lastAck > TimeSpan.FromSeconds(3))
                    {
                        // No answer for long: start afresh with a key frame rather than stall for ever.
                        Interlocked.Exchange(ref _acked, Interlocked.Read(ref _sent));
                        _keyRequested = true;
                    }

                    _wake.WaitOne(50);
                    continue;
                }

                // At most _fps pictures a second; the desktop keeps collecting changes meanwhile.
                var slot = lastEncode + TimeSpan.FromMilliseconds(1000.0 / _fps) - DateTime.UtcNow;
                if (slot > TimeSpan.Zero)
                {
                    Thread.Sleep(slot);
                }

                var started = Stopwatch.GetTimestamp();
                Grab grab;
                try
                {
                    var target = picture;
                    var factor = scale;
                    var (width, height) = (encoder.Width, encoder.Height);
                    grab = source.Next(refine > 0 || _keyRequested ? 100 : 250, pointer, (pixels, pitch) =>
                        Nv12.FromBgra((byte*)pixels, pitch, factor, target, width, height));
                }
                catch (Exception exception) when (IsCaptureFailure(exception))
                {
                    source.Dispose();
                    source = null;
                    continue;
                }

                if (grab == Grab.Lost)
                {
                    // The desktop switched (lock screen, sign-in, UAC) or the mode changed: capture afresh.
                    source.Dispose();
                    source = null;
                    continue;
                }

                if (unavailable)
                {
                    unavailable = false;
                    Emit("screen.status", new JsonObject { ["state"] = "ok" });
                }

                SendPointer(pointer);
                if (grab == Grab.Image)
                {
                    hasPicture = true;
                    refine = 2;
                }
                else if (!hasPicture || !(_keyRequested || (refine > 0 && DateTime.UtcNow - lastEncode > TimeSpan.FromMilliseconds(300))))
                {
                    continue;
                }
                else
                {
                    // Nothing new on the screen: the same picture once more, so it gets sharp where the last
                    // ones had to be quick - or as the key frame the browser asked for.
                    refine--;
                }

                var key = _keyRequested;
                _keyRequested = false;
                try
                {
                    if (!encoder.Encode(picture, key))
                    {
                        // The graphics card is still busy: this picture is skipped, the next one comes soon.
                        _keyRequested |= key;
                        continue;
                    }
                }
                catch (Exception exception) when (encoder is HardwareH264Encoder && exception is not OutOfMemoryException)
                {
                    VideoEncoders.HardwareFailed(exception);
                    encoder.Dispose();
                    encoder = null;
                    continue;
                }

                _statAgentMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lastEncode = DateTime.UtcNow;
            }
        }
        catch (Exception exception) when (_running)
        {
            AgentLog.Write("Screen: " + exception.Message);
            Emit("screen.status", new JsonObject { ["state"] = "error", ["message"] = "The screen could not be captured." });
        }
        finally
        {
            encoder?.Dispose();
            source?.Dispose();
            RestoreDisplay();
            _opened.TrySetException(new InvalidOperationException("The screen could not be captured."));
        }
    }

    private static bool IsCaptureFailure(Exception exception) =>
        exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or System.Runtime.InteropServices.COMException;

    private static IScreenSource CreateSource(int monitor) => ScreenPlugin.TestMode ? new TestPattern(monitor) : new DesktopCapture(monitor);

    // Fast enough for a mouse that feels like one's own; fewer pictures where data is scarce.
    private static int FramesFor(string quality) => quality switch
    {
        "low" => 20,
        "medium" => 30,
        _ => 60,
    };

    // Enough for sharp text at full speed; "low" for a phone on the road.
    private int Bitrate(int width, int height)
    {
        var perFullHd = _quality switch
        {
            "low" => 1_500_000,
            "medium" => 4_000_000,
            _ => 8_000_000,
        };
        // More pictures need more data, but not twice as much: most of a screen stays as it was.
        var bitrate = (long)perFullHd * width * height / (1920 * 1080) * (15 + _fps / 2) / 30;
        return (int)Math.Clamp(bitrate, 500_000, 40_000_000);
    }

    private void SendVideo(byte[] bytes, bool key, int width, int height)
    {
        var sequence = Interlocked.Increment(ref _sent);
        _sentAt[sequence] = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _statFrames);
        Interlocked.Add(ref _statBytes, bytes.Length);
        Emit("screen.video", new JsonObject
        {
            ["seq"] = sequence,
            ["key"] = key,
            ["width"] = width,
            ["height"] = height,
        }, bytes);
    }

    private void EmitMonitor(int videoWidth, int videoHeight)
    {
        var modes = new JsonArray();
        try
        {
            foreach (var mode in _display.Modes(_current))
            {
                modes.Add(Resolution.Text(mode));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        string saved;
        try
        {
            saved = Resolution.Text(_display.Saved(_current));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            saved = Resolution.Text((_current.Width, _current.Height));
        }

        Emit("screen.monitor", new JsonObject
        {
            ["monitor"] = _current.Index,
            ["width"] = _current.Width,
            ["height"] = _current.Height,
            ["videoWidth"] = videoWidth,
            ["videoHeight"] = videoHeight,
            ["modes"] = modes,
            ["saved"] = saved,
            ["resolution"] = _resolution,
        });
    }

    // Once a second: how many pictures, how much data, how long the way to the browser and back takes.
    private void EmitStats(double seconds)
    {
        var frames = Interlocked.Exchange(ref _statFrames, 0);
        var bytes = Interlocked.Exchange(ref _statBytes, 0);
        var rttTicks = Interlocked.Exchange(ref _statRttTicks, 0);
        var rttCount = Interlocked.Exchange(ref _statRttCount, 0);
        var agentMs = _statAgentMs;
        _statAgentMs = 0;
        Emit("screen.stats", new JsonObject
        {
            ["fps"] = Math.Round(frames / seconds, 1),
            ["kbps"] = (long)(bytes * 8 / 1000 / seconds),
            ["rtt"] = rttCount > 0 ? Math.Round(rttTicks * 1000.0 / Stopwatch.Frequency / rttCount, 1) : null,
            ["agentMs"] = frames > 0 ? Math.Round(agentMs / frames, 1) : null,
            ["encoder"] = _encoderName,
            ["fpsLimit"] = _fps,
        });
    }

    // ---- resolution ----------------------------------------------------------------------------------

    // Sets the monitor to what the viewer wants; true when its mode changed.
    private bool ApplyResolution(MonitorInfo monitor)
    {
        (int Width, int Height)? target;
        try
        {
            target = _resolution switch
            {
                "auto" => Resolution.Pick(_display.Modes(monitor), _fitWidth, _fitHeight),
                "native" => null,
                _ => Resolution.Parse(_resolution) is { } size && _display.Modes(monitor).Contains(size) ? size : null,
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            target = null;
        }

        if (target is null || (_changedDisplay is not null && target == _display.Saved(monitor)))
        {
            // As Windows has it saved.
            if (_changedDisplay is null)
            {
                return false;
            }

            RestoreDisplay();
            return true;
        }

        if (target == (monitor.Width, monitor.Height))
        {
            return false;
        }

        if (!_display.Apply(monitor, target.Value.Width, target.Value.Height))
        {
            AgentLog.Write($"Screen: the monitor does not take {Resolution.Text(target.Value)}.");
            return false;
        }

        _changedDisplay = monitor;
        return true;
    }

    private void RestoreDisplay()
    {
        if (_changedDisplay is { } monitor)
        {
            _changedDisplay = null;
            try
            {
                _display.Restore(monitor);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AgentLog.Write("Screen: the resolution could not be put back - " + exception.Message);
            }
        }
    }

    private int _lastPointerX = int.MinValue;
    private int _lastPointerY = int.MinValue;
    private bool _lastPointerVisible;

    private void SendPointer(PointerState pointer)
    {
        if (pointer.Shape is { } shape)
        {
            pointer.Shape = null;
            Emit("screen.cursor", new JsonObject { ["x"] = pointer.HotX, ["y"] = pointer.HotY }, shape);
        }

        if (pointer.Moved)
        {
            pointer.Moved = false;
            if (pointer.X != _lastPointerX || pointer.Y != _lastPointerY || pointer.Visible != _lastPointerVisible)
            {
                (_lastPointerX, _lastPointerY, _lastPointerVisible) = (pointer.X, pointer.Y, pointer.Visible);
                Emit("screen.pointer", new JsonObject { ["x"] = pointer.X, ["y"] = pointer.Y, ["visible"] = pointer.Visible });
            }
        }
    }

    public void Ack(long sequence)
    {
        if (sequence > Interlocked.Read(ref _sent))
        {
            // Not one of this session's pictures (a browser still counting those of a helper before).
            return;
        }

        if (_sentAt.TryRemove(sequence, out var sentAt))
        {
            Interlocked.Add(ref _statRttTicks, Stopwatch.GetTimestamp() - sentAt);
            Interlocked.Increment(ref _statRttCount);
        }

        if (_sentAt.Count > 32)
        {
            foreach (var old in _sentAt.Keys.Where(old => old < sequence))
            {
                _sentAt.TryRemove(old, out _);
            }
        }

        long current;
        do
        {
            current = Interlocked.Read(ref _acked);
            if (sequence <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _acked, sequence, current) != current);
        _lastAck = DateTime.UtcNow;
        _wake.Set();
    }

    public void RequestKeyFrame()
    {
        _keyRequested = true;
        _wake.Set();
    }

    public void Configure(AgentFrame frame)
    {
        _monitor = frame.Int("monitor", _monitor);
        _maxWidth = Math.Clamp(frame.Int("maxWidth", _maxWidth), 640, 7680);
        if (frame.Text("quality") is { } quality && quality != _quality)
        {
            _quality = quality;
            if (!_fpsGiven)
            {
                _fps = FramesFor(quality);
            }
        }

        if (frame.Data?["fps"] is not null)
        {
            _fpsGiven = true;
            _fps = Math.Clamp(frame.Int("fps", _fps), 5, 60);
        }

        if (frame.Text("resolution") is { } resolution || frame.Data?["fitWidth"] is not null)
        {
            _resolution = frame.Text("resolution") ?? _resolution;
            _fitWidth = frame.Int("fitWidth", _fitWidth);
            _fitHeight = frame.Int("fitHeight", _fitHeight);
            _resolutionDirty = true;
        }

        _reconfigure = true;
        _wake.Set();
    }

    // ---- input ---------------------------------------------------------------------------------------

    public void Input(AgentFrame frame)
    {
        lock (_inputGate)
        {
            switch (frame.Text("t"))
            {
                case "m":
                    _input.MoveTo(_current.Left + Math.Clamp(frame.Int("x"), 0, _current.Width - 1), _current.Top + Math.Clamp(frame.Int("y"), 0, _current.Height - 1));
                    break;
                case "b":
                    var button = frame.Int("b");
                    var down = frame.Bool("d");
                    if (down ? _buttonsDown.Add(button) : _buttonsDown.Remove(button))
                    {
                        _input.Button(button, down);
                    }

                    break;
                case "w":
                    _input.Wheel(Math.Clamp(frame.Int("dy"), -1200, 1200), Math.Clamp(frame.Int("dx"), -1200, 1200));
                    break;
                case "k":
                    var key = (frame.Int("s") & 0xFF, frame.Bool("e"));
                    var pressed = frame.Bool("d");
                    if (pressed)
                    {
                        _keysDown.Add(key);
                    }
                    else
                    {
                        _keysDown.Remove(key);
                    }

                    _input.Key(key.Item1, key.Item2, pressed);
                    break;
                case "u":
                    var text = frame.Text("text") ?? "";
                    if (text.Length is > 0 and <= 4096)
                    {
                        _input.Text(text);
                    }

                    break;
                case "r":
                    ReleaseAllLocked();
                    break;
            }
        }
    }

    // Ctrl+Alt+Del: only Windows itself may send it, and only for a LocalSystem service.
    public void SecureAttention()
    {
        if (ScreenPlugin.TestMode)
        {
            Debug("sas");
        }
        else if (!SessionNative.IsLocalSystem || !SessionNative.SendSecureAttention())
        {
            Emit("screen.status", new JsonObject { ["state"] = "nocad" });
        }
    }

    // Nothing stays pressed when the viewer leaves or looks away (a held Ctrl would make every later click a Ctrl+click).
    private void ReleaseAll()
    {
        lock (_inputGate)
        {
            ReleaseAllLocked();
        }
    }

    private void ReleaseAllLocked()
    {
        foreach (var (code, extended) in _keysDown)
        {
            _input.Key(code, extended, false);
        }

        foreach (var button in _buttonsDown)
        {
            _input.Button(button, false);
        }

        _keysDown.Clear();
        _buttonsDown.Clear();
    }

    // Ctrl+V on the machine, after the clipboard got what the browser pasted.
    private void PressPaste()
    {
        lock (_inputGate)
        {
            var control = _keysDown.Contains((ScanControl, false)) || _keysDown.Contains((ScanControl, true));
            if (!control)
            {
                _input.Key(ScanControl, false, true);
            }

            _input.Key(ScanV, false, true);
            _input.Key(ScanV, false, false);
            if (!control)
            {
                _input.Key(ScanControl, false, false);
            }
        }
    }

    // ---- sound ---------------------------------------------------------------------------------------

    public void Audio(bool on, string codec)
    {
        _audio?.Dispose();
        _audio = null;
        if (!on || !_running)
        {
            return;
        }

        var opus = codec == "opus";
        try
        {
            void Packet(byte[] packet) => Emit("screen.audio", new JsonObject { ["codec"] = opus ? "opus" : "pcm" }, packet);
            _audio = ScreenPlugin.TestMode ? new TestTone(opus, Packet) : new AudioStreamer(opus, Packet);
        }
        catch (Exception exception)
        {
            AgentLog.Write("Screen sound: " + exception.Message);
            Emit("screen.status", new JsonObject { ["state"] = "nosound" });
        }
    }

    // ---- clipboard -----------------------------------------------------------------------------------

    private void OnClipboardChanged(ClipContent content)
    {
        switch (content.Kind)
        {
            case "text":
                Emit("screen.clip", new JsonObject { ["kind"] = "text" }, Encoding.UTF8.GetBytes(content.Text ?? ""));
                break;
            case "image" when content.Png is not null:
                Emit("screen.clip", new JsonObject { ["kind"] = "image" }, content.Png);
                break;
            case "files" when content.Files is { Count: > 0 } files:
                _clipToken = Guid.NewGuid().ToString("N");
                _clipFiles = files;
                var list = new JsonArray();
                foreach (var path in files)
                {
                    var file = new FileInfo(path);
                    list.Add(new JsonObject
                    {
                        ["name"] = file.Name,
                        ["size"] = file.Exists ? file.Length : 0,
                        ["folder"] = !file.Exists && Directory.Exists(path),
                    });
                }

                Emit("screen.clip", new JsonObject { ["kind"] = "files", ["token"] = _clipToken, ["files"] = list });
                break;
        }
    }

    public (JsonObject Data, byte[] Payload) ReadClipFile(string token, int index, long offset)
    {
        if (token.Length == 0 || token != _clipToken || index < 0 || index >= _clipFiles.Count)
        {
            throw new InvalidOperationException("This file is no longer on the machine's clipboard.");
        }

        using var stream = new FileStream(_clipFiles[index], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = Math.Clamp(offset, 0, stream.Length);
        var buffer = new byte[(int)Math.Min(AgentWire.ChunkSize, stream.Length - stream.Position)];
        stream.ReadExactly(buffer);
        return (new JsonObject { ["size"] = stream.Length, ["name"] = Path.GetFileName(_clipFiles[index]) }, buffer);
    }

    public void Clip(AgentFrame frame)
    {
        try
        {
            switch (frame.Op)
            {
                case "screen.clip.set":
                    // Text: small enough to come in one piece.
                    var text = AgentWire.Utf8(frame.Payload);
                    _clipboard.SetAsync(new ClipContent("text", Text: text)).ContinueWith(task => Pasted(task, frame.Bool("paste")));
                    break;
                case "screen.clip.begin":
                    BeginUpload(frame);
                    break;
                case "screen.clip.chunk":
                    Upload? target;
                    lock (_uploads)
                    {
                        _uploads.TryGetValue(frame.Text("id") ?? "", out target);
                    }

                    target?.Write(frame.Int("index"), frame.Long("offset"), frame.Payload.Span);
                    break;
                case "screen.clip.end":
                    EndUpload(frame.Text("id") ?? "");
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AgentLog.Write("Screen clipboard: " + exception.Message);
            Emit("screen.status", new JsonObject { ["state"] = "clipfailed" });
        }
    }

    private void Pasted(Task set, bool paste)
    {
        if (set.IsFaulted)
        {
            AgentLog.Write("Screen clipboard: " + set.Exception?.InnerException?.Message);
            Emit("screen.status", new JsonObject { ["state"] = "clipfailed" });
            return;
        }

        if (paste && _running)
        {
            PressPaste();
        }

        Emit("screen.clip.done", new JsonObject { ["paste"] = paste });
    }

    private void BeginUpload(AgentFrame frame)
    {
        var id = frame.Text("id") ?? "";
        if (id.Length is 0 or > 64 || frame.Data?["files"] is not JsonArray files || files.Count is 0 or > 500)
        {
            throw new InvalidOperationException("Nothing to paste.");
        }

        var folder = Path.Combine(ClipboardFolder(), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id[..Math.Min(8, id.Length)]);
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.OfType<JsonObject>())
        {
            var name = SafeName(item["name"]?.GetValue<string>() ?? "Datei");
            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = Path.GetFileNameWithoutExtension(name) + $" ({n})" + Path.GetExtension(name);
            }

            paths.Add(Path.Combine(folder, unique));
        }

        lock (_uploads)
        {
            _uploads[id] = new Upload(frame.Text("kind") == "image" ? "image" : "files", paths, frame.Bool("paste"));
        }
    }

    private void EndUpload(string id)
    {
        Upload? upload;
        lock (_uploads)
        {
            _uploads.Remove(id, out upload);
        }

        if (upload is null)
        {
            return;
        }

        upload.Dispose();
        var content = upload.Kind == "image"
            ? new ClipContent("image", Png: File.ReadAllBytes(upload.Paths[0]))
            : new ClipContent("files", Files: upload.Paths);
        _clipboard.SetAsync(content).ContinueWith(task => Pasted(task, upload.Paste));
    }

    // Where pasted files land on the machine; older ones are tidied away. A helper running as SYSTEM puts them
    // into the signed-in person's temp folder - their Explorer has to read them.
    private static string ClipboardFolder()
    {
        var temp = SessionNative.IsLocalSystem && !ScreenPlugin.TestMode
            ? SessionNative.SignedInUserTemp() ?? throw new InvalidOperationException("Nobody is signed in to paste files for.")
            : Path.GetTempPath();
        var root = Path.Combine(temp, "Matgate Agent", "Zwischenablage");
        Directory.CreateDirectory(root);
        foreach (var old in Directory.EnumerateDirectories(root).Where(folder => Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddDays(-1)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return root;
    }

    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "Datei" : name.Length > 120 ? name[..120] : name;
    }

    // ---- sending -------------------------------------------------------------------------------------

    private void Debug(string text) => Emit("screen.debug", new JsonObject { ["text"] = text });

    private void Emit(string op, JsonObject data, byte[]? payload = null)
    {
        data["session"] = Id;
        try
        {
            Send(new AgentFrame { Kind = "evt", Op = op, Data = data, Payload = payload ?? ReadOnlyMemory<byte>.Empty }).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException)
        {
            // Matgate is gone; DetachAll ends the session.
        }
    }

    private sealed class Upload(string kind, IReadOnlyList<string> paths, bool paste) : IDisposable
    {
        private readonly Dictionary<int, FileStream> _open = [];

        public string Kind { get; } = kind;

        public IReadOnlyList<string> Paths { get; } = paths;

        public bool Paste { get; } = paste;

        public void Write(int index, long offset, ReadOnlySpan<byte> bytes)
        {
            if (index < 0 || index >= Paths.Count)
            {
                return;
            }

            lock (_open)
            {
                if (!_open.TryGetValue(index, out var stream))
                {
                    stream = new FileStream(Paths[index], FileMode.Create, FileAccess.Write, FileShare.Read);
                    _open[index] = stream;
                }

                stream.Position = offset;
                stream.Write(bytes);
            }
        }

        public void Dispose()
        {
            lock (_open)
            {
                foreach (var path in Paths.Where((_, index) => !_open.ContainsKey(index)))
                {
                    // An empty file sends no chunk at all.
                    if (!File.Exists(path))
                    {
                        File.WriteAllBytes(path, []);
                    }
                }

                foreach (var stream in _open.Values)
                {
                    stream.Dispose();
                }

                _open.Clear();
            }
        }
    }
}
