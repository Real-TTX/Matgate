using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Matgate.Agent.Screen;

// The screen as a plugin: the machine's real desktop as H.264 video in the browser, with mouse, keyboard,
// sound and a clipboard that works in both directions (text, pictures and files). Like AnyDesk rather
// than RDP: it shows what the monitor shows, so it needs the agent to run in the signed-in user's session
// (user mode), not as a service in session 0.
//
// One viewer at a time: a second one takes the screen over, the first is told so.
public sealed class ScreenPlugin(AgentConfig config)
{
    private readonly object _gate = new();
    private ScreenSession? _session;

    // MATGATE_AGENT_SCREEN_TEST=1: a generated picture, and input/clipboard reported instead of done -
    // the tests must not move anyone's mouse or overwrite anyone's clipboard.
    public static bool TestMode => Environment.GetEnvironmentVariable("MATGATE_AGENT_SCREEN_TEST") == "1";

    public bool IsAvailable => OperatingSystem.IsWindows() && config.Screen && (TestMode || Process.GetCurrentProcess().SessionId != 0);

    public JsonArray Monitors()
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

    public AgentFrame Open(AgentFrame request, Func<AgentFrame, Task> send)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("This agent does not share its screen - it has to run as the signed-in user, not as a service.");
        }

        var id = request.Text("session");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            id = Guid.NewGuid().ToString("N");
        }

        var session = new ScreenSession(id, send, request.Int("monitor", 0), Math.Clamp(request.Int("maxWidth", 2560), 640, 7680),
            request.Text("quality") ?? "auto", request.Int("fps", 30));
        ScreenSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = session;
        }

        previous?.Stop("taken");
        var monitor = session.Start();
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
    public AgentFrame ReadClip(AgentFrame request)
    {
        var session = Find(request.Text("session")) ?? throw new InvalidOperationException("This screen session has ended.");
        var (data, payload) = session.ReadClipFile(request.Text("token") ?? "", request.Int("index", -1), request.Long("offset"));
        return new AgentFrame { Id = request.Id, Kind = "res", Op = request.Op, Data = data, Payload = payload };
    }

    // Matgate's connection went away: a screen nobody can see any more stops.
    public void DetachAll(Func<AgentFrame, Task> send)
    {
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
    private readonly IInputSink _input;
    private readonly IClipboardBridge _clipboard;
    private volatile bool _running = true;
    private volatile bool _keyRequested = true;
    private volatile bool _reconfigure;
    private int _monitor;
    private int _maxWidth;
    private string _quality;
    private int _fps;
    private long _sent;
    private long _acked;
    private DateTime _lastAck = DateTime.UtcNow;
    private MonitorInfo _current = new(0, "", 0, 0, 0, 0, true);
    private IDisposable? _audio;
    private string _clipToken = "";
    private IReadOnlyList<string> _clipFiles = [];

    public ScreenSession(string id, Func<AgentFrame, Task> send, int monitor, int maxWidth, string quality, int fps)
    {
        Id = id;
        Send = send;
        _monitor = monitor;
        _maxWidth = maxWidth;
        _quality = quality;
        _fps = Math.Clamp(fps, 5, 60);
        if (ScreenPlugin.TestMode)
        {
            _input = new RecordingInput(Debug);
            _clipboard = new RecordingClipboard(Debug);
        }
        else
        {
            _input = new InputInjector();
            _clipboard = new WindowsClipboard();
        }

        _clipboard.Changed += OnClipboardChanged;
    }

    public string Id { get; }

    public Func<AgentFrame, Task> Send { get; }

    public MonitorInfo Start()
    {
        new Thread(Loop) { IsBackground = true, Name = "Matgate screen" }.Start();
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
        H264Encoder? encoder = null;
        byte[] picture = [];
        var scale = 1;
        var hasPicture = false;
        var refine = 0;
        var lastEncode = DateTime.MinValue;
        var pointer = new PointerState();
        var unavailable = false;
        try
        {
            try
            {
                source = CreateSource(_monitor);
                _current = source.Monitor;
                _opened.TrySetResult(source.Monitor);
            }
            catch (Exception exception)
            {
                _opened.TrySetException(exception);
                return;
            }

            while (_running)
            {
                if (_reconfigure)
                {
                    _reconfigure = false;
                    if (source.Monitor.Index != _monitor)
                    {
                        source.Dispose();
                        source = CreateSource(_monitor);
                    }

                    encoder?.Dispose();
                    encoder = null;
                }

                if (encoder is not null && (encoder.Width != Nv12.Scaled(source.Monitor.Width, source.Monitor.Height, scale).Width
                    || source.Monitor.Width != _current.Width || source.Monitor.Height != _current.Height))
                {
                    // The resolution changed under way.
                    encoder.Dispose();
                    encoder = null;
                }

                if (encoder is null)
                {
                    _current = source.Monitor;
                    scale = _current.Width > _maxWidth ? 2 : 1;
                    var (width, height) = Nv12.Scaled(_current.Width, _current.Height, scale);
                    encoder = new H264Encoder(width, height, _fps, Bitrate(width, height));
                    picture = new byte[Nv12.Size(width, height)];
                    hasPicture = false;
                    _keyRequested = true;
                    Emit("screen.monitor", new JsonObject
                    {
                        ["monitor"] = _current.Index,
                        ["width"] = _current.Width,
                        ["height"] = _current.Height,
                        ["videoWidth"] = width,
                        ["videoHeight"] = height,
                    });
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

                Grab grab;
                try
                {
                    var target = picture;
                    var factor = scale;
                    var (width, height) = (encoder.Width, encoder.Height);
                    grab = source.Next(refine > 0 || _keyRequested ? 100 : 250, pointer, (pixels, pitch) =>
                        Nv12.FromBgra((byte*)pixels, pitch, factor, target, width, height));
                    if (unavailable && grab != Grab.Lost)
                    {
                        unavailable = false;
                        Emit("screen.status", new JsonObject { ["state"] = "ok" });
                    }
                }
                catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // The lock screen or a UAC prompt: a desktop this process may not see.
                    if (!unavailable)
                    {
                        unavailable = true;
                        Emit("screen.status", new JsonObject { ["state"] = "unavailable" });
                    }

                    Thread.Sleep(500);
                    continue;
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
                encoder.Encode(picture, key, (bytes, isKey) => SendVideo(bytes, isKey, encoder));
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
        }
    }

    private static IScreenSource CreateSource(int monitor) => ScreenPlugin.TestMode ? new TestPattern(monitor) : new DesktopCapture(monitor);

    // Enough for sharp text at full speed; "low" for a phone on the road.
    private int Bitrate(int width, int height)
    {
        var perFullHd = _quality switch
        {
            "low" => 1_500_000,
            "medium" => 4_000_000,
            _ => 8_000_000,
        };
        var bitrate = (long)perFullHd * width * height / (1920 * 1080) * _fps / 30;
        return (int)Math.Clamp(bitrate, 500_000, 40_000_000);
    }

    private void SendVideo(byte[] bytes, bool key, H264Encoder encoder)
    {
        var sequence = Interlocked.Increment(ref _sent);
        Emit("screen.video", new JsonObject
        {
            ["seq"] = sequence,
            ["key"] = key,
            ["width"] = encoder.Width,
            ["height"] = encoder.Height,
        }, bytes);
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
        _quality = frame.Text("quality") ?? _quality;
        _fps = Math.Clamp(frame.Int("fps", _fps), 5, 60);
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

    // Where pasted files land on the machine; older ones are tidied away.
    private static string ClipboardFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Matgate Agent", "Zwischenablage");
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
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
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
