using System.Diagnostics;
using System.Text.Json.Nodes;
using Matgate.Agent;
using Matgate.Display.Video;

namespace Matgate.Display;

// One remote screen for one viewer: a remote desktop (RDP or VNC) on one side, the browser's screen page on
// the other. It speaks the screen protocol of the Matgate agent, so the same page shows both: the picture
// as H.264, the pointer, sound, the clipboard, the resolution of the viewer's window.
//
// The picture is paced by the browser: at most three pictures are on their way (two for the fastest
// response), each one is acknowledged when it has been decoded. Where the screen stands still, the same
// picture is encoded again until the encoder says it is sharp - quick pictures while things move, sharp text
// once they stop. On a slow connection that takes more repeats than on a fast one.
public sealed class ScreenSession : IScreenHost, IAsyncDisposable
{
    private static readonly string[] Modes =
    [
        "3840x2160", "2560x1440", "1920x1200", "1920x1080", "1680x1050", "1600x900",
        "1440x900", "1366x768", "1280x1024", "1280x800", "1280x720", "1024x768",
    ];

    private readonly ScreenTarget _target;
    private readonly IScreenBackend _backend;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _state = new();
    private readonly long[] _sentAt = new long[64];
    private readonly ScreenAudio _audio;

    // The picture as compressed pieces of the screen instead of H.264 (see SendTiles): for an app that draws it
    // itself and has no H.264 decoder on every platform. Lossless, so text is sharp at once.
    private readonly bool _tiles;
    private int _tileWidth;
    private int _tileHeight;

    private Thread? _video;
    private bool _ready;
    private int _disposed;
    private TaskCompletionSource<ScreenCredentials?>? _asking;

    // What the viewer wants.
    private string _resolution;
    private int _fitWidth;
    private int _fitHeight;
    private int _nativeWidth;
    private int _nativeHeight;
    private ScreenQuality _quality = ScreenQuality.Smooth;

    // The picture. Guarded by _state where the browser's messages and the backend's threads meet the video thread.
    private H264Encoder? _encoder;
    private I420Frame? _frame;
    private bool _reopen;
    private bool _dirty;
    private int _dirtyLeft;
    private int _dirtyTop;
    private int _dirtyRight;
    private int _dirtyBottom;
    private bool _keyRequested = true;
    // The picture stands still but is not sharp yet.
    private bool _refine;
    private const int RefineDelay = 200;
    private long _lastEncode;
    private long _sent;
    private long _acked;
    private long _lastAck;

    // Statistics, once a second.
    private long _statStart;
    private long _statFrames;
    private long _statBytes;
    private long _statRtt;
    private long _statRttCount;
    private double _statEncodeMs;

    public ScreenSession(ScreenTarget target, Func<ScreenTarget, IScreenHost, IScreenBackend> backend, ILogger logger, bool tiles = false)
    {
        _target = target;
        _tiles = tiles;
        _logger = logger;
        _resolution = target.Resolution;
        _fitWidth = target.FitWidth;
        _fitHeight = target.FitHeight;
        _audio = new ScreenAudio((frame) => Emit("screen.audio", new JsonObject { ["codec"] = "pcm" }, frame));
        Quality(target.Quality);
        _backend = backend(target, this);
    }

    // Everything for the viewer's page: pictures, the pointer, sound, the clipboard, status.
    public Action<AgentFrame>? OnEvent { get; set; }

    public Guid UserId { get; set; }

    // Ends when the remote desktop went away or the session was closed.
    public Task Completion => _completion.Task;

    // Connects; the answer is what the page gets as "screen.ready". Throws ScreenException.
    public async Task<JsonObject> OpenAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Log($"connecting to {_target.Host}:{_target.Port}");
        try
        {
            await _backend.ConnectAsync(limit.Token);
        }
        catch (ScreenException exception)
        {
            Log("failed: " + exception.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ScreenException("The connection was cancelled.");
        }

        var width = 0;
        var height = 0;
        _backend.WithFrame((_, _, w, h) =>
        {
            width = w;
            height = h;
        });
        _nativeWidth = width;
        _nativeHeight = height;
        Log($"connected, {width}x{height}");

        var now = Environment.TickCount64;
        _lastAck = now;
        _statStart = now;
        _ready = true;
        _video = new Thread(VideoLoop) { IsBackground = true, Name = "screen " + _target.Session };
        _video.Start();
        _ = Task.Run(RunAsync, CancellationToken.None);

        return new JsonObject
        {
            ["session"] = _target.Session,
            ["monitor"] = 0,
            ["width"] = width,
            ["height"] = height,
            ["monitors"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["name"] = _backend.Name,
                ["width"] = width,
                ["height"] = height,
                ["primary"] = true,
            }),
            // What the clipboard of this protocol takes: pictures as a Windows bitmap, files at all.
            ["clipImage"] = _backend.ClipImages ? "dib" : "none",
            ["clipFiles"] = _backend.ClipFiles,
            ["audio"] = _backend.Sound,
            ["colours"] = _backend.ColorDepth,
            ["fewerColours"] = FewerColours,
            ["qualityColours"] = _backend.QualityColours,
        };
    }

    // Whether the remote desktop was asked for fewer colours when this session connected. Colours are settled
    // then: a session that should have other ones needs a new connection (the page offers it).
    private bool FewerColours => _backend.QualityColours && ScreenQuality.From(_target.Quality).RdpColorDepth < 32;

    private async Task RunAsync()
    {
        string? reason = null;
        try
        {
            reason = await _backend.RunAsync(_stop.Token);
        }
        catch (ScreenException exception)
        {
            reason = exception.Message;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "A remote screen ended with an error.");
        }

        Log("ended" + (reason is null ? "" : ": " + reason));
        var data = new JsonObject();
        if (reason is not null)
        {
            data["message"] = reason;
        }

        Emit("screen.ended", data);
        _stop.Cancel();
        _wake.Set();
        _completion.TrySetResult();
    }

    // ---- from the viewer's page ----------------------------------------------------------------------------

    public void Post(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        if (op == "screen.auth")
        {
            // The answer to a question asked while connecting.
            var asking = Interlocked.Exchange(ref _asking, null);
            asking?.TrySetResult(ScreenJson.Flag(data, "cancel")
                ? null
                : new ScreenCredentials(ScreenJson.Text(data, "username"), ScreenJson.Text(data, "password"), ScreenJson.Text(data, "domain")));
            return;
        }

        if (!_ready || _stop.IsCancellationRequested)
        {
            return;
        }

        switch (op)
        {
            case "screen.input":
                _backend.Input(data);
                break;
            case "screen.ack":
                Acknowledge(ScreenJson.Long(data, "seq"));
                break;
            case "screen.key":
                lock (_state)
                {
                    _keyRequested = true;
                }

                _wake.Set();
                break;
            case "screen.config":
                if (ScreenJson.Has(data, "quality"))
                {
                    Quality(ScreenJson.Text(data, "quality"));
                }

                var resolution = ScreenJson.Has(data, "resolution");
                if (resolution)
                {
                    var wanted = ScreenJson.Text(data, "resolution");
                    _resolution = wanted.Length is > 0 and <= 16 ? wanted : "auto";
                }

                _fitWidth = ScreenJson.Int(data, "fitWidth", _fitWidth);
                _fitHeight = ScreenJson.Int(data, "fitHeight", _fitHeight);
                if (resolution || ScreenJson.Has(data, "fitWidth"))
                {
                    var (width, height) = WantedSize();
                    if (width > 0)
                    {
                        _backend.Resize(width, height);
                    }
                }

                break;
            case "screen.audio":
                var on = ScreenJson.Flag(data, "on") && _backend.Sound;
                _audio.Enabled = on;
                _backend.SoundEnabled(on);
                break;
            case "screen.sas":
                _backend.SecureAttention();
                break;
            case "screen.close":
                _stop.Cancel();
                _wake.Set();
                break;
            default:
                if (op.StartsWith("screen.clip.", StringComparison.Ordinal))
                {
                    _backend.Clip(op, data, payload);
                }

                break;
        }
    }

    // A piece of a file copied on the remote desktop, for a download.
    public Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken)
    {
        return _ready
            ? _backend.ClipReadAsync(token, index, offset, cancellationToken)
            : Task.FromException<ClipPiece>(new ScreenException("This file is no longer on the remote clipboard."));
    }

    private void Acknowledge(long sequence)
    {
        lock (_state)
        {
            if (sequence > _sent || sequence <= _acked)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_sent - sequence < _sentAt.Length)
            {
                _statRtt += now - _sentAt[sequence % _sentAt.Length];
                _statRttCount++;
            }

            _acked = sequence;
            _lastAck = now;
        }

        _wake.Set();
    }

    // Fast enough for a mouse that feels like one's own; fewer pictures where data is scarce; or every picture
    // as soon as it can be had (see ScreenQuality). What the remote desktop was asked for when it connected
    // stays as it is.
    private void Quality(string quality)
    {
        var wanted = ScreenQuality.From(quality);
        lock (_state)
        {
            if (!wanted.SameEncoder(_quality))
            {
                // Made again with the next picture.
                _reopen = true;
                _dirty = true;
            }

            _quality = wanted;
        }

        _wake.Set();
    }

    // ---- from the backend ----------------------------------------------------------------------------------

    public void Dirty(int x, int y, int width, int height)
    {
        lock (_state)
        {
            if (!_dirty)
            {
                _dirty = true;
                _dirtyLeft = x;
                _dirtyTop = y;
                _dirtyRight = x + width;
                _dirtyBottom = y + height;
            }
            else
            {
                _dirtyLeft = Math.Min(_dirtyLeft, x);
                _dirtyTop = Math.Min(_dirtyTop, y);
                _dirtyRight = Math.Max(_dirtyRight, x + width);
                _dirtyBottom = Math.Max(_dirtyBottom, y + height);
            }
        }

        _wake.Set();
    }

    public void Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
    {
        var shape = rgba.ToArray();
        // Kept for a viewer that comes later: the remote desktop only says it again when it changes.
        _cursor = () => (new JsonObject { ["x"] = hotX, ["y"] = hotY, ["width"] = width, ["height"] = height, ["format"] = "rgba" }, shape);
        Emit("screen.cursor", _cursor().Data, shape);
        CursorHidden(false);
    }

    public void CursorHidden(bool hidden)
    {
        _cursorHidden = hidden;
        Emit("screen.pointer", new JsonObject { ["visible"] = !hidden, ["local"] = true });
    }

    public void CursorDefault()
    {
        _cursor = null;
        Emit("screen.cursor", new JsonObject { ["format"] = "default" });
        CursorHidden(false);
    }

    // ---- a viewer goes, a viewer comes ---------------------------------------------------------------------

    private Func<(JsonObject Data, byte[] Shape)>? _cursor;
    private bool _cursorHidden;
    private volatile bool _watched = true;

    // Nobody is looking: the connection to the remote desktop stays, but no picture is made of it and no
    // sound is sent. Nothing stays pressed there either.
    public void Detach()
    {
        _watched = false;
        OnEvent = null;
        _audio.Enabled = false;
        if (_ready && !_stop.IsCancellationRequested)
        {
            _backend.SoundEnabled(false);
            _backend.Input(new JsonObject { ["t"] = "r" });
        }
    }

    // A viewer is there (again): it starts with a picture of its own - a key frame, and what describes the
    // screen - and with the pointer as it is now.
    public void Attach(Action<AgentFrame> viewer)
    {
        OnEvent = viewer;
        lock (_state)
        {
            _acked = _sent;
            _lastAck = Environment.TickCount64;
            _reopen = true;
            _dirty = true;
        }

        _watched = true;
        if (_cursor is { } cursor)
        {
            var (data, shape) = cursor();
            Emit("screen.cursor", data, shape);
        }
        else
        {
            Emit("screen.cursor", new JsonObject { ["format"] = "default" });
        }

        Emit("screen.pointer", new JsonObject { ["visible"] = !_cursorHidden, ["local"] = true });
        _wake.Set();
    }

    // What the page gets as "screen.ready" when it comes back to a session that kept running.
    public JsonObject Describe()
    {
        var width = 0;
        var height = 0;
        _backend.WithFrame((_, _, w, h) =>
        {
            width = w;
            height = h;
        });
        return new JsonObject
        {
            ["session"] = _target.Session,
            ["attached"] = true,
            ["monitor"] = 0,
            ["width"] = width,
            ["height"] = height,
            ["monitors"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["name"] = _backend.Name,
                ["width"] = width,
                ["height"] = height,
                ["primary"] = true,
            }),
            ["clipImage"] = _backend.ClipImages ? "dib" : "none",
            ["clipFiles"] = _backend.ClipFiles,
            ["audio"] = _backend.Sound,
            ["colours"] = _backend.ColorDepth,
            ["fewerColours"] = FewerColours,
            ["qualityColours"] = _backend.QualityColours,
        };
    }

    public void Event(string op, JsonObject data, ReadOnlyMemory<byte> payload = default)
    {
        Emit(op, data, payload);
    }

    public void Status(string state, string? message = null)
    {
        var data = new JsonObject { ["state"] = state };
        if (message is not null)
        {
            data["message"] = message;
        }

        Emit("screen.status", data);
    }

    public async Task<ScreenCredentials?> AskAsync(string[] need, string userName, string domain, bool failed, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<ScreenCredentials?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _asking, answer)?.TrySetResult(null);
        Emit("screen.auth", new JsonObject
        {
            ["need"] = new JsonArray(need.Select(field => (JsonNode)field).ToArray()),
            ["username"] = userName,
            ["domain"] = domain,
            ["failed"] = failed,
        });
        try
        {
            return await answer.Task.WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _asking, null, answer);
        }
    }

    public void Audio(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
        _audio.Add(pcm, rate, channels);
    }

    // The smallest and the largest screen a session is asked for.
    private const int MinWidth = 320;
    private const int MinHeight = 200;
    private const int MaxWidth = 4096;
    private const int MaxHeight = 2304;

    public (int Width, int Height) WantedSize()
    {
        var width = 0;
        var height = 0;
        if (_resolution == "auto")
        {
            width = _fitWidth;
            height = _fitHeight;
        }
        else if (_resolution == "native")
        {
            width = _nativeWidth;
            height = _nativeHeight;
        }
        else
        {
            var parts = _resolution.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 320 && h >= 200)
            {
                width = w;
                height = h;
            }
        }

        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        // What remote desktops and the encoder take: even sizes within sensible bounds - and in the shape
        // that was asked for. A phone held upright asks for a narrow, tall screen; bringing only its width up
        // to a minimum would give a picture that no longer fills the window.
        var grow = Math.Max(1.0, Math.Max(MinWidth / (double)width, MinHeight / (double)height));
        var shrink = Math.Min(1.0, Math.Min(MaxWidth / (width * grow), MaxHeight / (height * grow)));
        var scaled = grow * shrink;
        return (Math.Clamp((int)Math.Round(width * scaled), MinWidth, MaxWidth) & ~1, Math.Clamp((int)Math.Round(height * scaled), MinHeight, MaxHeight) & ~1);
    }

    public void Log(string message)
    {
        _logger.LogInformation("screen {Session} ({Protocol} {Host}): {Message}", _target.Session, _target.Protocol, _target.Host, message);
    }

    private void Emit(string op, JsonObject data, ReadOnlyMemory<byte> payload = default)
    {
        OnEvent?.Invoke(new AgentFrame { Kind = "evt", Op = op, Data = data, Payload = payload });
    }

    // ---- the picture ---------------------------------------------------------------------------------------

    private void VideoLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var wait = Tick();
                if (wait > 0)
                {
                    _wake.WaitOne(wait);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogError(exception, "The picture of a remote screen could not be encoded.");
            Status("error", "The picture could not be encoded.");
            _stop.Cancel();
        }
    }

    // Encodes and sends when it is time; returns how many milliseconds the loop may sleep.
    private int Tick()
    {
        var now = Environment.TickCount64;
        if (!_watched)
        {
            // Kept for a viewer that may come back: nothing to encode meanwhile.
            return 500;
        }

        SendStats(now);

        bool dirty;
        bool key;
        bool reopen;
        ScreenQuality quality;
        int left;
        int top;
        int right;
        int bottom;
        lock (_state)
        {
            dirty = _dirty;
            key = _keyRequested;
            quality = _quality;
            var refineDue = _refine && now - _lastEncode >= RefineDelay;
            if (!dirty && !key && !refineDue)
            {
                return _refine ? (int)Math.Max(1, RefineDelay - (now - _lastEncode)) : 500;
            }

            // The browser is behind: wait for it instead of piling up pictures it would show late.
            if (_sent - _acked >= quality.MaxInFlight)
            {
                if (now - _lastAck > 3000)
                {
                    _acked = _sent;
                    _keyRequested = true;
                }

                return 15;
            }

            // Where the screen changes faster than the connection carries, the encoder asks for fewer
            // pictures before it makes them hard to read: it says how long a picture with this much new in
            // it should wait. A key frame does not wait, and neither does a small change - the encoder asks
            // nothing for a cursor or a letter typed. Nor does anything wait where the viewer wants the
            // fastest response: the next picture now, however coarse.
            var interval = 1000 / quality.FramesPerSecond;
            if (quality.Pace && dirty && !key && !_reopen && _encoder is { } pacing)
            {
                var width = Math.Clamp(_dirtyRight, 0, pacing.Width) - Math.Clamp(_dirtyLeft, 0, pacing.Width);
                var height = Math.Clamp(_dirtyBottom, 0, pacing.Height) - Math.Clamp(_dirtyTop, 0, pacing.Height);
                if (width > 0 && height > 0)
                {
                    interval = Math.Max(interval, pacing.SuggestedInterval((long)width * height));
                }
            }

            var slot = _lastEncode + interval - now;
            if (slot > 0)
            {
                return (int)slot;
            }

            reopen = _reopen;
            left = _dirtyLeft;
            top = _dirtyTop;
            right = _dirtyRight;
            bottom = _dirtyBottom;
            _dirty = false;
            _reopen = false;
            _keyRequested = false;
        }

        if (_tiles)
        {
            return SendTiles(now, dirty, key, reopen, left, top, right, bottom);
        }

        var started = Stopwatch.GetTimestamp();
        var opened = false;
        var failed = false;
        var took = _backend.WithFrame((pixels, stride, width, height) =>
        {
            // H.264 wants even sizes; an odd last row or column is left out.
            width &= ~1;
            height &= ~1;
            if (width < 2 || height < 2)
            {
                failed = true;
                return;
            }

            if (_encoder is null || reopen || width != _encoder.Width || height != _encoder.Height)
            {
                _encoder?.Dispose();
                _frame?.Dispose();
                // Sharp text where the screen stands still, a ceiling for the network where it moves.
                _encoder = new H264Encoder(width, height, quality.FramesPerSecond, quality.KilobitsPerSecond(width, height), quality.EncoderOptions());
                _frame = new I420Frame(width, height);
                opened = true;
                key = true;
            }

            if (opened)
            {
                ColorConverter.BgrxToI420(pixels, stride, _frame!, 0, 0, width, height);
            }
            else if (dirty)
            {
                // Only what changed: a blinking cursor on a large screen costs next to nothing.
                var x = Math.Clamp(left, 0, width);
                var y = Math.Clamp(top, 0, height);
                var w = Math.Clamp(right, x, width) - x;
                var h = Math.Clamp(bottom, y, height) - y;
                if (w > 0 && h > 0)
                {
                    ColorConverter.BgrxToI420(pixels, stride, _frame!, x, y, w, h);
                }
            }
        });

        if (!took || failed)
        {
            lock (_state)
            {
                // Still to do, once there is a picture.
                _dirty |= dirty;
                _keyRequested |= key;
            }

            return 100;
        }

        var encoder = _encoder!;
        if (opened)
        {
            Emit("screen.monitor", new JsonObject
            {
                ["monitor"] = 0,
                ["width"] = encoder.Width,
                ["height"] = encoder.Height,
                ["videoWidth"] = encoder.Width,
                ["videoHeight"] = encoder.Height,
                ["modes"] = new JsonArray(Modes.Select(mode => (JsonNode)mode).ToArray()),
                ["saved"] = $"{_nativeWidth}x{_nativeHeight}",
                ["resolution"] = _resolution,
            });
        }

        // Nothing new is the same picture once more: the encoder spends it on sharpness.
        var encoded = encoder.Encode(_frame!, key, out var isKey);
        _refine = !encoder.IsSharp;
        long sequence;
        lock (_state)
        {
            _lastEncode = now;
            if (encoded.IsEmpty)
            {
                return 1;
            }

            sequence = ++_sent;
            _sentAt[sequence % _sentAt.Length] = now;
            _statFrames++;
            _statBytes += encoded.Length;
            _statEncodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // The encoder's buffer is its own; the page gets a copy.
        Emit(
            "screen.video",
            new JsonObject { ["seq"] = sequence, ["key"] = key || isKey, ["width"] = encoder.Width, ["height"] = encoder.Height },
            encoded.ToArray());
        return 1;
    }

    // The picture for an app (see _tiles): the rectangle that changed - all of it after a change of size, for a new
    // viewer or when asked for a key picture - as BGRX rows, compressed with zlib, in one "screen.tiles" message. The
    // pacing is the same as for H.264: at most so many on their way, each acknowledged with "screen.ack".
    private int SendTiles(long now, bool dirty, bool key, bool reopen, int left, int top, int right, int bottom)
    {
        var started = Stopwatch.GetTimestamp();
        byte[]? packed = null;
        int x = 0, y = 0, w = 0, h = 0, screenWidth = 0, screenHeight = 0;
        var whole = false;
        var took = _backend.WithFrame((pixels, stride, width, height) =>
        {
            screenWidth = width;
            screenHeight = height;
            if (width < 1 || height < 1)
            {
                return;
            }

            whole = key || reopen || width != _tileWidth || height != _tileHeight;
            if (whole)
            {
                w = width;
                h = height;
            }
            else if (dirty)
            {
                x = Math.Clamp(left, 0, width);
                y = Math.Clamp(top, 0, height);
                w = Math.Clamp(right, x, width) - x;
                h = Math.Clamp(bottom, y, height) - y;
            }

            if (w > 0 && h > 0)
            {
                packed = PackTile(pixels, stride, x, y, w, h);
            }
        });

        if (!took)
        {
            lock (_state)
            {
                _dirty |= dirty;
                _keyRequested |= key;
                _reopen |= reopen;
            }

            return 100;
        }

        _refine = false;
        if (packed is null)
        {
            lock (_state)
            {
                _lastEncode = now;
            }

            return 1;
        }

        if (whole && (reopen || screenWidth != _tileWidth || screenHeight != _tileHeight))
        {
            _tileWidth = screenWidth;
            _tileHeight = screenHeight;
            Emit("screen.monitor", new JsonObject
            {
                ["monitor"] = 0,
                ["width"] = screenWidth,
                ["height"] = screenHeight,
                ["videoWidth"] = screenWidth,
                ["videoHeight"] = screenHeight,
                ["modes"] = new JsonArray(Modes.Select(mode => (JsonNode)mode).ToArray()),
                ["saved"] = $"{_nativeWidth}x{_nativeHeight}",
                ["resolution"] = _resolution,
            });
        }

        long sequence;
        lock (_state)
        {
            _lastEncode = now;
            sequence = ++_sent;
            _sentAt[sequence % _sentAt.Length] = now;
            _statFrames++;
            _statBytes += packed.Length;
            _statEncodeMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Emit(
            "screen.tiles",
            new JsonObject
            {
                ["seq"] = sequence,
                ["key"] = whole,
                ["format"] = TileFormat,
                ["x"] = x,
                ["y"] = y,
                ["width"] = w,
                ["height"] = h,
                ["screenWidth"] = screenWidth,
                ["screenHeight"] = screenHeight,
            },
            packed);
        return 1;
    }

    // How a tile is packed: 4 bytes a pixel (blue, green, red, unused), row after row without gaps, zlib (RFC 1950).
    public const string TileFormat = "bgrx-zlib";

    public static byte[] PackTile(ReadOnlySpan<byte> pixels, int stride, int x, int y, int width, int height)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = width * 4;
            for (var line = 0; line < height; line++)
            {
                zlib.Write(pixels.Slice((y + line) * stride + x * 4, row));
            }
        }

        return output.ToArray();
    }

    private void SendStats(long now)
    {
        JsonObject stats;
        lock (_state)
        {
            var seconds = (now - _statStart) / 1000.0;
            if (seconds < 1.0)
            {
                return;
            }

            stats = new JsonObject
            {
                ["fps"] = Math.Round(_statFrames / seconds, 1),
                ["kbps"] = (long)(_statBytes * 8 / 1000 / seconds),
                ["rtt"] = _statRttCount > 0 ? JsonValue.Create(_statRtt / _statRttCount) : null,
                ["agentMs"] = _statFrames > 0 ? JsonValue.Create(Math.Round(_statEncodeMs / _statFrames, 1)) : null,
                ["encoder"] = _tiles ? "zlib" : "OpenH264",
                ["fpsLimit"] = _quality.FramesPerSecond,
                ["quality"] = _quality.Name,
                // Bits a pixel as the remote desktop sends them: fewer colours show as bands, and should not
                // look like a fault.
                ["colours"] = _backend.ColorDepth,
            };
            _statStart = now;
            _statFrames = 0;
            _statBytes = 0;
            _statRtt = 0;
            _statRttCount = 0;
            _statEncodeMs = 0;
        }

        Emit("screen.stats", stats);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _wake.Set();
        Interlocked.Exchange(ref _asking, null)?.TrySetResult(null);
        // The video thread is inside the encoder at most for one picture.
        _video?.Join(TimeSpan.FromSeconds(5));
        try
        {
            await _backend.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogDebug(exception, "A remote screen did not close cleanly.");
        }

        _encoder?.Dispose();
        _frame?.Dispose();
        _completion.TrySetResult();
        // _stop and _wake are left to the garbage collector: the backend's last callbacks may still touch them.
    }
}
