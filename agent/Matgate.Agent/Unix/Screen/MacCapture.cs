using System.Text;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// A Mac's monitors. macOS places them in points, a picture is in pixels (twice as many on a Retina
// display): a monitor is listed in pixels, and with how many pixels make a point.
internal sealed record MacMonitor(uint Id, MonitorInfo Info, CGRect Points, double Scale);

internal static unsafe class MacMonitors
{
    public static IReadOnlyList<MacMonitor> List()
    {
        var ids = stackalloc uint[16];
        if (Mac.CGGetActiveDisplayList(16, ids, out var count) != 0 || count == 0)
        {
            throw new InvalidOperationException("The Mac names no monitor - the agent is not in a session that has a screen.");
        }

        var main = Mac.CGMainDisplayID();
        var list = new List<MacMonitor>();
        for (var i = 0; i < count; i++)
        {
            var points = Mac.CGDisplayBounds(ids[i]);
            var width = points.Width;
            var height = points.Height;
            var mode = Mac.CGDisplayCopyDisplayMode(ids[i]);
            if (mode != 0)
            {
                width = Mac.CGDisplayModeGetPixelWidth(mode);
                height = Mac.CGDisplayModeGetPixelHeight(mode);
                Mac.CGDisplayModeRelease(mode);
            }

            if (width < 1 || height < 1 || points.Width < 1)
            {
                continue;
            }

            var scale = width / points.Width;
            list.Add(new MacMonitor(ids[i], new MonitorInfo(0, ids[i] == main ? "Main display" : "Display " + (i + 1), (int)Math.Round(points.X * scale), (int)Math.Round(points.Y * scale), (int)width, (int)height, ids[i] == main), points, scale));
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException("The Mac names no monitor with a size.");
        }

        return list.OrderByDescending(monitor => monitor.Info.Primary).Select((monitor, index) => monitor with { Info = monitor.Info with { Index = index } }).ToList();
    }
}

// One monitor of a Mac as pictures: asked for again and again and compared with the one before (what macOS
// offers for "tell me when something changed" needs Objective-C blocks; this needs nothing).
internal sealed unsafe class MacCapture : IScreenSource
{
    private static readonly byte[] Arrow = MakeArrow();
    private readonly MacMonitor _monitor;
    private byte[] _previous = [];
    private bool _shapeSent;
    private int _pointerX = int.MinValue;
    private int _pointerY = int.MinValue;

    public MacCapture(int monitor)
    {
        if (Mac.CGPreflightScreenCaptureAccess() == 0)
        {
            // Asks once: macOS shows where the permission is given.
            Mac.CGRequestScreenCaptureAccess();
            throw new InvalidOperationException("macOS does not let the agent see the screen: allow it under System Settings > Privacy & Security > Screen Recording.");
        }

        var monitors = MacMonitors.List();
        _monitor = monitors[Math.Clamp(monitor, 0, monitors.Count - 1)];
        // The size is the picture's own - whatever the mode says.
        var image = Mac.CGDisplayCreateImage(_monitor.Id);
        if (image == 0)
        {
            throw new InvalidOperationException("macOS gave no picture of the screen.");
        }

        Monitor = _monitor.Info with { Width = (int)Mac.CGImageGetWidth(image), Height = (int)Mac.CGImageGetHeight(image) };
        Mac.CFRelease(image);
    }

    public MonitorInfo Monitor { get; }

    public Grab Next(int timeout, PointerState pointer, Action<nint, int> image)
    {
        var deadline = Environment.TickCount64 + timeout;
        var moved = false;
        while (true)
        {
            moved |= UpdatePointer(pointer);
            var picture = Mac.CGDisplayCreateImage(_monitor.Id);
            if (picture == 0)
            {
                throw new InvalidOperationException("macOS gave no picture of the screen.");
            }

            nint data = 0;
            try
            {
                if ((int)Mac.CGImageGetWidth(picture) != Monitor.Width || (int)Mac.CGImageGetHeight(picture) != Monitor.Height || Mac.CGImageGetBitsPerPixel(picture) != 32)
                {
                    return Grab.Lost;
                }

                data = Mac.CGDataProviderCopyData(Mac.CGImageGetDataProvider(picture));
                if (data == 0)
                {
                    throw new InvalidOperationException("macOS gave no picture of the screen.");
                }

                var pitch = (int)Mac.CGImageGetBytesPerRow(picture);
                var now = new ReadOnlySpan<byte>((void*)Mac.CFDataGetBytePtr(data), (int)Math.Min(Mac.CFDataGetLength(data), (long)pitch * Monitor.Height));
                if (now.Length < (pitch * (Monitor.Height - 1)) + (Monitor.Width * 4))
                {
                    return Grab.Lost;
                }

                if (_previous.Length != now.Length)
                {
                    _previous = new byte[now.Length];
                }
                else if (now.SequenceEqual(_previous))
                {
                    var left = deadline - Environment.TickCount64;
                    if (left <= 0 || moved)
                    {
                        return moved ? Grab.PointerOnly : Grab.Nothing;
                    }

                    Thread.Sleep((int)Math.Min(left, 25));
                    continue;
                }

                now.CopyTo(_previous);
                image(Mac.CFDataGetBytePtr(data), pitch);
                return Grab.Image;
            }
            finally
            {
                if (data != 0)
                {
                    Mac.CFRelease(data);
                }

                Mac.CFRelease(picture);
            }
        }
    }

    private bool UpdatePointer(PointerState pointer)
    {
        var changed = false;
        if (!_shapeSent)
        {
            // The picture has no pointer in it, and the pointer's real shape is the window server's secret: an arrow.
            _shapeSent = true;
            pointer.Shape = Arrow;
            pointer.HotX = 0;
            pointer.HotY = 0;
            pointer.Visible = true;
            pointer.Moved = true;
            changed = true;
        }

        var found = Mac.CGEventCreate(0);
        if (found == 0)
        {
            return changed;
        }

        var place = Mac.CGEventGetLocation(found);
        Mac.CFRelease(found);
        var x = (int)Math.Round((place.X - _monitor.Points.X) * _monitor.Scale);
        var y = (int)Math.Round((place.Y - _monitor.Points.Y) * _monitor.Scale);
        if (x != _pointerX || y != _pointerY)
        {
            (_pointerX, _pointerY) = (x, y);
            pointer.X = x;
            pointer.Y = y;
            pointer.Visible = x >= 0 && y >= 0 && x < Monitor.Width && y < Monitor.Height;
            pointer.Moved = true;
            changed = true;
        }

        return changed;
    }

    private static byte[] MakeArrow()
    {
        string[] rows =
        [
            "X           ", "XX          ", "X.X         ", "X..X        ", "X...X       ", "X....X      ", "X.....X     ",
            "X......X    ", "X.......X   ", "X........X  ", "X.........X ", "X......XXXXX", "X...X..X    ", "X..X X..X   ",
            "X.X  X..X   ", "XX    X..X  ", "X     X..X  ", "       XX   ",
        ];
        var bgra = new byte[12 * rows.Length * 4];
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < 12; x++)
            {
                var at = (y * 12 + x) * 4;
                var colour = rows[y][x] switch { 'X' => (byte)0, '.' => (byte)255, _ => (byte)0 };
                bgra[at] = bgra[at + 1] = bgra[at + 2] = colour;
                bgra[at + 3] = rows[y][x] == ' ' ? (byte)0 : (byte)255;
            }
        }

        return PngWriter.FromBgra(bgra, 12, rows.Length);
    }

    public void Dispose()
    {
    }
}

// The Mac's pasteboard, text in both directions. It says how often it changed; the agent asks twice a second.
internal sealed class MacClipboard : IClipboardBridge
{
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _running = true;
    private long _seen = -1;

    public MacClipboard()
    {
        System.Runtime.InteropServices.NativeLibrary.TryLoad(Mac.AppKit, out _);
        _thread = new Thread(Watch) { IsBackground = true, Name = "Matgate clipboard" };
        _thread.Start();
    }

    public event Action<ClipContent>? Changed;

    public Task SetAsync(ClipContent content)
    {
        if (content.Kind != "text")
        {
            return Task.FromException(new InvalidOperationException("Only text can be pasted to a Mac yet."));
        }

        lock (_gate)
        {
            var pool = Mac.objc_autoreleasePoolPush();
            try
            {
                var board = Board();
                Mac.Send(board, Mac.sel_registerName("clearContents"));
                Mac.Send(board, Mac.sel_registerName("setString:forType:"), Text(content.Text ?? ""), Text("public.utf8-plain-text"));
                _seen = Mac.Send(board, Mac.sel_registerName("changeCount"));
            }
            finally
            {
                Mac.objc_autoreleasePoolPop(pool);
            }
        }

        return Task.CompletedTask;
    }

    private void Watch()
    {
        while (_running)
        {
            try
            {
                string? copied = null;
                lock (_gate)
                {
                    var pool = Mac.objc_autoreleasePoolPush();
                    try
                    {
                        var board = Board();
                        var count = (long)Mac.Send(board, Mac.sel_registerName("changeCount"));
                        if (_seen < 0)
                        {
                            // What was there before the viewer came is not news.
                            _seen = count;
                        }
                        else if (count != _seen)
                        {
                            _seen = count;
                            var text = Mac.Send(board, Mac.sel_registerName("stringForType:"), Text("public.utf8-plain-text"));
                            if (text != 0)
                            {
                                copied = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Mac.Send(text, Mac.sel_registerName("UTF8String")));
                            }
                        }
                    }
                    finally
                    {
                        Mac.objc_autoreleasePoolPop(pool);
                    }
                }

                if (!string.IsNullOrEmpty(copied))
                {
                    Changed?.Invoke(new ClipContent("text", Text: copied));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                AgentLog.Write("Clipboard: " + exception.Message);
                return;
            }

            Thread.Sleep(500);
        }
    }

    private static nint Board() => Mac.Send(Mac.objc_getClass("NSPasteboard"), Mac.sel_registerName("generalPasteboard"));

    private static unsafe nint Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* start = bytes)
        {
            return Mac.Send(Mac.objc_getClass("NSString"), Mac.sel_registerName("stringWithUTF8String:"), (nint)start);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
