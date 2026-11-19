using System.Runtime.InteropServices;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// The monitors of an X display and its resolutions (XRandR).
internal static unsafe class X11Monitors
{
    private static readonly object Gate = new();
    private static nint _display;

    // One connection for the questions asked now and then (monitors, sizes, windows).
    public static nint Display
    {
        get
        {
            lock (Gate)
            {
                return _display != 0 ? _display : _display = X11.Open();
            }
        }
    }

    public static IReadOnlyList<MonitorInfo> List() => List(Display);

    public static IReadOnlyList<MonitorInfo> List(nint display)
    {
        var root = X11.XDefaultRootWindow(display);
        var list = new List<MonitorInfo>();
        if (X11.HasRandR && X11.XRRQueryExtension(display, out _, out _) != 0)
        {
            var monitors = X11.XRRGetMonitors(display, root, 1, out var count);
            if (monitors is not null)
            {
                for (var i = 0; i < count; i++)
                {
                    var name = X11.XGetAtomName(display, monitors[i].Name);
                    var text = name != 0 ? Marshal.PtrToStringUTF8(name) ?? "" : "";
                    if (name != 0)
                    {
                        X11.XFree(name);
                    }

                    if (monitors[i].Width > 0 && monitors[i].Height > 0)
                    {
                        list.Add(new MonitorInfo(0, text, monitors[i].X, monitors[i].Y, monitors[i].Width, monitors[i].Height, monitors[i].Primary != 0));
                    }
                }

                X11.XRRFreeMonitors(monitors);
            }
        }

        if (list.Count == 0)
        {
            // No XRandR (or a server that names no monitor): the whole screen is the one monitor.
            X11.XGetGeometry(display, root, out _, out _, out _, out var width, out var height, out _, out _);
            list.Add(new MonitorInfo(0, "Screen", 0, 0, (int)width, (int)height, true));
        }

        // The primary one first, as on Windows; then from left to right.
        var ordered = list.OrderByDescending(monitor => monitor.Primary).ThenBy(monitor => monitor.Left).ThenBy(monitor => monitor.Top).ToList();
        if (!ordered[0].Primary)
        {
            ordered[0] = ordered[0] with { Primary = true };
        }

        return ordered.Select((monitor, index) => monitor with { Index = index }).ToList();
    }
}

// The resolution of an X screen with one monitor (XRandR's sizes). With several monitors the agent leaves
// the layout alone: each keeps the size the desktop gave it.
internal sealed unsafe class X11DisplayControl : IDisplayControl
{
    private (int Width, int Height)? _saved;

    public IReadOnlyList<(int Width, int Height)> Modes(MonitorInfo monitor)
    {
        var display = X11Monitors.Display;
        if (!X11.HasRandR || X11.XRRQueryExtension(display, out _, out _) == 0 || X11Monitors.List(display).Count != 1)
        {
            return [];
        }

        var sizes = X11.XRRSizes(display, X11.XDefaultScreen(display), out var count);
        var list = new List<(int Width, int Height)>();
        for (var i = 0; sizes is not null && i < count; i++)
        {
            if (sizes[i].Width >= 640 && sizes[i].Height >= 480)
            {
                list.Add((sizes[i].Width, sizes[i].Height));
            }
        }

        return list.Distinct().OrderByDescending(size => (long)size.Width * size.Height).ToList();
    }

    public (int Width, int Height) Saved(MonitorInfo monitor) => _saved ??= (monitor.Width, monitor.Height);

    public bool Apply(MonitorInfo monitor, int width, int height)
    {
        Saved(monitor);
        return Set(width, height);
    }

    public void Restore(MonitorInfo monitor)
    {
        if (_saved is { } saved)
        {
            Set(saved.Width, saved.Height);
        }
    }

    private static bool Set(int width, int height)
    {
        var display = X11Monitors.Display;
        var root = X11.XDefaultRootWindow(display);
        var sizes = X11.XRRSizes(display, X11.XDefaultScreen(display), out var count);
        for (var i = 0; sizes is not null && i < count; i++)
        {
            if (sizes[i].Width != width || sizes[i].Height != height)
            {
                continue;
            }

            var configuration = X11.XRRGetScreenInfo(display, root);
            if (configuration == 0)
            {
                return false;
            }

            try
            {
                X11.XRRConfigCurrentConfiguration(configuration, out var rotation);
                var done = X11.XRRSetScreenConfig(display, configuration, root, i, rotation, 0) == 0;
                X11.XSync(display, 0);
                return done;
            }
            finally
            {
                X11.XRRFreeScreenConfigInfo(configuration);
            }
        }

        return false;
    }
}

// One monitor of an X display as pictures: the server writes each one into memory it shares with the agent
// (XShm), and says when something changed (XDamage) - while the screen stands still, nothing is fetched.
// Without XDamage every picture is fetched and compared with the one before.
internal sealed unsafe class X11Capture : IScreenSource
{
    private readonly nint _display;
    private readonly nuint _root;
    private readonly XImage* _image;
    private XShmSegmentInfo* _segment;
    private readonly nuint _damage;
    private readonly int _damageEvent = -1;
    private readonly int _fixesEvent = -1;
    private readonly int _randrEvent = -1;
    private readonly uint _rootWidth;
    private readonly uint _rootHeight;
    private readonly byte[]? _previous;
    private bool _dirty = true;
    private bool _cursorDirty = true;
    private bool _lost;
    private int _pointerX = int.MinValue;
    private int _pointerY = int.MinValue;
    private long _geometryChecked;

    public X11Capture(int monitor)
    {
        _display = X11.Open();
        try
        {
            _root = X11.XDefaultRootWindow(_display);
            var monitors = X11Monitors.List(_display);
            Monitor = monitors[Math.Clamp(monitor, 0, monitors.Count - 1)];
            X11.XGetGeometry(_display, _root, out _, out _, out _, out _rootWidth, out _rootHeight, out _, out _);
            if (X11.XShmQueryExtension(_display) == 0)
            {
                throw new InvalidOperationException("The X display has no shared-memory pictures (MIT-SHM).");
            }

            var screen = X11.XDefaultScreen(_display);
            _segment = (XShmSegmentInfo*)NativeMemory.AllocZeroed((nuint)sizeof(XShmSegmentInfo));
            _image = X11.XShmCreateImage(_display, X11.XDefaultVisual(_display, screen), (uint)X11.XDefaultDepth(_display, screen), X11.ZPixmap, 0, _segment, (uint)Monitor.Width, (uint)Monitor.Height);
            if (_image is null || _image->BitsPerPixel != 32 || _image->RedMask != 0xFF0000)
            {
                throw new InvalidOperationException("The X display's colours are not 24 or 32 bit (this one: " + (_image is null ? "unknown" : _image->BitsPerPixel + " bits a pixel") + ").");
            }

            _segment->Id = X11.shmget(0, (nuint)_image->BytesPerLine * (nuint)_image->Height, 0x200 | 0x180);
            if (_segment->Id < 0)
            {
                throw new InvalidOperationException("No shared memory for the picture (shmget).");
            }

            _segment->Address = X11.shmat(_segment->Id, 0, 0);
            // Marked for removal at once: it goes when the server and the agent have let go of it, however that happens.
            if (_segment->Address == -1)
            {
                X11.shmctl(_segment->Id, 0, 0);
                throw new InvalidOperationException("No shared memory for the picture (shmat).");
            }

            _image->Data = _segment->Address;
            LinuxScreenPlatform.GiveToServer(_segment->Id);
            X11.LastError = 0;
            X11.XShmAttach(_display, _segment);
            X11.XSync(_display, 0);
            X11.shmctl(_segment->Id, 0, 0);
            if (X11.LastError != 0)
            {
                throw new InvalidOperationException("The X server does not take the agent's shared memory - it runs somewhere else (a container, another machine).");
            }

            if (X11.XFixesQueryExtension(_display, out _fixesEvent, out _) != 0)
            {
                X11.XFixesSelectCursorInput(_display, _root, 1);
            }
            else
            {
                _fixesEvent = -1;
            }

            if (X11.HasDamage && X11.XDamageQueryExtension(_display, out _damageEvent, out _) != 0)
            {
                // XDamageReportNonEmpty: one event when something changed, the next one after Subtract.
                _damage = X11.XDamageCreate(_display, _root, 3);
            }
            else
            {
                _damageEvent = -1;
                _previous = new byte[_image->BytesPerLine * _image->Height];
            }

            X11.XSelectInput(_display, _root, X11.StructureNotifyMask);
            if (X11.HasRandR && X11.XRRQueryExtension(_display, out _randrEvent, out _) != 0)
            {
                // RRScreenChangeNotifyMask | RRCrtcChangeNotifyMask
                X11.XRRSelectInput(_display, _root, 1 | 2);
            }
            else
            {
                _randrEvent = -1;
            }

            X11.XFlush(_display);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MonitorInfo Monitor { get; } = new(0, "", 0, 0, 0, 0, true);

    public Grab Next(int timeout, PointerState pointer, Action<nint, int> image)
    {
        var deadline = Environment.TickCount64 + timeout;
        var moved = false;
        while (true)
        {
            Drain();
            if (_lost || GeometryChanged())
            {
                return Grab.Lost;
            }

            moved |= UpdatePointer(pointer);
            if (_dirty || _damage == 0)
            {
                break;
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0 || moved)
            {
                return moved ? Grab.PointerOnly : Grab.Nothing;
            }

            // In short slices: the pointer is asked for, not announced.
            X11.Wait(_display, (int)Math.Min(left, 12));
        }

        if (_damage != 0)
        {
            // Before the picture is fetched: what changes while it is, is reported again.
            _dirty = false;
            X11.XDamageSubtract(_display, _damage, 0, 0);
        }

        X11.LastError = 0;
        if (X11.XShmGetImage(_display, _root, _image, Monitor.Left, Monitor.Top, nuint.MaxValue) == 0 || X11.LastError != 0)
        {
            // The screen got smaller under way, or belongs to another session now.
            return Grab.Lost;
        }

        if (_previous is not null)
        {
            var now = new ReadOnlySpan<byte>((void*)_image->Data, _previous.Length);
            if (now.SequenceEqual(_previous))
            {
                var left = deadline - Environment.TickCount64;
                if (left > 0 && !moved)
                {
                    Thread.Sleep((int)Math.Min(left, 15));
                }

                return moved ? Grab.PointerOnly : Grab.Nothing;
            }

            now.CopyTo(_previous);
        }

        image(_image->Data, _image->BytesPerLine);
        return Grab.Image;
    }

    private void Drain()
    {
        XEvent found;
        while (X11.XPending(_display) > 0)
        {
            X11.XNextEvent(_display, &found);
            if (found.Type == _damageEvent && _damageEvent >= 0)
            {
                _dirty = true;
            }
            else if (_fixesEvent >= 0 && found.Type == _fixesEvent + 1)
            {
                _cursorDirty = true;
            }
            else if (found.Type == X11.ConfigureNotify || (_randrEvent >= 0 && (found.Type == _randrEvent || found.Type == _randrEvent + 1)))
            {
                _geometryChecked = 0;
            }
        }
    }

    // The screen or the monitor has another size or place: the session captures afresh.
    private bool GeometryChanged()
    {
        var now = Environment.TickCount64;
        if (now - _geometryChecked < 2000)
        {
            return false;
        }

        _geometryChecked = now;
        X11.XGetGeometry(_display, _root, out _, out _, out _, out var width, out var height, out _, out _);
        if (width != _rootWidth || height != _rootHeight)
        {
            return _lost = true;
        }

        var monitors = X11Monitors.List(_display);
        var same = monitors.Count > Monitor.Index && monitors[Monitor.Index] is var current
            && current.Left == Monitor.Left && current.Top == Monitor.Top && current.Width == Monitor.Width && current.Height == Monitor.Height;
        return _lost = !same;
    }

    private bool UpdatePointer(PointerState pointer)
    {
        var changed = false;
        if (X11.XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out _) != 0 && (x != _pointerX || y != _pointerY))
        {
            (_pointerX, _pointerY) = (x, y);
            pointer.X = x - Monitor.Left;
            pointer.Y = y - Monitor.Top;
            pointer.Visible = pointer.X >= 0 && pointer.Y >= 0 && pointer.X < Monitor.Width && pointer.Y < Monitor.Height;
            pointer.Moved = true;
            changed = true;
        }

        if (_cursorDirty && _fixesEvent >= 0)
        {
            _cursorDirty = false;
            var cursor = X11.XFixesGetCursorImage(_display);
            if (cursor is not null)
            {
                int width = cursor->Width, height = cursor->Height;
                if (width is > 0 and <= 256 && height is > 0 and <= 256)
                {
                    // One unsigned long a pixel: alpha, red, green, blue - the colours already multiplied by alpha.
                    var pixels = (nuint*)cursor->Pixels;
                    var bgra = new byte[width * height * 4];
                    for (var i = 0; i < width * height; i++)
                    {
                        var pixel = (uint)pixels[i];
                        var alpha = pixel >> 24;
                        byte Plain(uint value) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);
                        bgra[i * 4] = Plain(pixel & 0xFF);
                        bgra[i * 4 + 1] = Plain((pixel >> 8) & 0xFF);
                        bgra[i * 4 + 2] = Plain((pixel >> 16) & 0xFF);
                        bgra[i * 4 + 3] = (byte)alpha;
                    }

                    pointer.Shape = PngWriter.FromBgra(bgra, width, height);
                    pointer.HotX = cursor->HotX;
                    pointer.HotY = cursor->HotY;
                    pointer.Visible = true;
                    pointer.Moved = true;
                    changed = true;
                }

                X11.XFree((nint)cursor);
            }
        }

        return changed;
    }

    public void Dispose()
    {
        if (_segment is not null)
        {
            if (_segment->Address is not (0 or -1))
            {
                X11.XShmDetach(_display, _segment);
                X11.XSync(_display, 0);
                X11.shmdt(_segment->Address);
            }

            NativeMemory.Free(_segment);
            _segment = null;
        }

        if (_image is not null)
        {
            // The struct only: its picture was the shared memory.
            X11.XFree((nint)_image);
        }

        if (_display != 0)
        {
            X11.XCloseDisplay(_display);
        }
    }
}
