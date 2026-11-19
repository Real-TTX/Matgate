using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Matgate.Agent.Unix.Screen;

// Xlib and its extensions, as far as the screen needs them: shared-memory pictures (XShm), "something
// changed" (XDamage), the pointer's shape and the clipboard's owner (XFixes), keys and mouse (XTest),
// monitors and resolutions (XRandR). 64-bit Linux only: a Window, an Atom and a KeySym are unsigned long.
internal static unsafe class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXext = "libXext.so.6";
    private const string LibXfixes = "libXfixes.so.3";
    private const string LibXtst = "libXtst.so.6";
    private const string LibXdamage = "libXdamage.so.1";
    private const string LibXrandr = "libXrandr.so.2";

    public const int ZPixmap = 2;
    public const int ConfigureNotify = 22;
    public const int SelectionClear = 29;
    public const int SelectionRequest = 30;
    public const int SelectionNotify = 31;
    public const int ClientMessage = 33;
    public const nint StructureNotifyMask = 1 << 17;
    public const nint SubstructureNotifyMask = 1 << 19;
    public const nint SubstructureRedirectMask = 1 << 20;
    public const nuint AtomAtom = 4;
    public const nuint AtomCardinal = 6;
    public const nuint AtomString = 31;
    public const nuint AtomWindow = 33;

    private static readonly object Gate = new();
    private static bool _initialised;
    private static string? _missing;

    // The error X reported last (0 = none): a request for a window that is gone must not end the process.
    public static volatile int LastError;

    // Null when everything the screen needs is installed, otherwise which library is not.
    public static string? Missing
    {
        get
        {
            lock (Gate)
            {
                if (!_initialised)
                {
                    _initialised = true;
                    foreach (var name in new[] { LibX11, LibXext, LibXfixes, LibXtst })
                    {
                        if (!NativeLibrary.TryLoad(name, out _))
                        {
                            _missing = name;
                            return _missing;
                        }
                    }

                    XInitThreads();
                    XSetErrorHandler(&OnError);
                }

                return _missing;
            }
        }
    }

    public static bool HasDamage { get; } = NativeLibrary.TryLoad(LibXdamage, out _);

    public static bool HasRandR { get; } = NativeLibrary.TryLoad(LibXrandr, out _);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint error)
    {
        // XErrorEvent: type, display, resource id, serial, then the error code.
        LastError = ((byte*)error)[32];
        return 0;
    }

    // Opens $DISPLAY with $XAUTHORITY.
    public static nint Open()
    {
        if (Missing is { } missing)
        {
            throw new InvalidOperationException($"{missing} is not installed.");
        }

        var display = XOpenDisplay(null);
        return display != 0 ? display : throw new InvalidOperationException("The X display " + (Environment.GetEnvironmentVariable("DISPLAY") ?? "(none)") + " does not let the agent in.");
    }

    public static nuint Atom(nint display, string name) => XInternAtom(display, name, 0);

    // A property of a window as bytes (format 8) or as numbers (format 32, one unsigned long each).
    public static byte[]? Property(nint display, nuint window, nuint property, out int format, out nuint type, bool delete = false)
    {
        format = 0;
        type = 0;
        if (XGetWindowProperty(display, window, property, 0, 1 << 22, delete ? 1 : 0, 0, out type, out format, out var count, out _, out var data) != 0 || data == 0)
        {
            return null;
        }

        try
        {
            var size = format == 32 ? (int)count * sizeof(nuint) : format == 16 ? (int)count * 2 : (int)count;
            return new ReadOnlySpan<byte>((void*)data, size).ToArray();
        }
        finally
        {
            XFree(data);
        }
    }

    public static nuint[] Numbers(nint display, nuint window, nuint property)
    {
        var bytes = Property(display, window, property, out var format, out _);
        return bytes is null || format != 32 ? [] : MemoryMarshal.Cast<byte, nuint>(bytes).ToArray();
    }

    // ---- Xlib -------------------------------------------------------------------------------------------

    [DllImport(LibX11)]
    public static extern int XInitThreads();

    [DllImport(LibX11)]
    public static extern nint XOpenDisplay(string? name);

    [DllImport(LibX11)]
    public static extern int XCloseDisplay(nint display);

    [DllImport(LibX11)]
    private static extern nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, nint, int> handler);

    [DllImport(LibX11)]
    public static extern int XDefaultScreen(nint display);

    [DllImport(LibX11)]
    public static extern nuint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    public static extern nint XDefaultVisual(nint display, int screen);

    [DllImport(LibX11)]
    public static extern int XDefaultDepth(nint display, int screen);

    [DllImport(LibX11)]
    public static extern int XGetGeometry(nint display, nuint drawable, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);

    [DllImport(LibX11)]
    public static extern int XTranslateCoordinates(nint display, nuint source, nuint target, int x, int y, out int targetX, out int targetY, out nuint child);

    [DllImport(LibX11)]
    public static extern int XConnectionNumber(nint display);

    [DllImport(LibX11)]
    public static extern int XPending(nint display);

    [DllImport(LibX11)]
    public static extern int XNextEvent(nint display, XEvent* found);

    [DllImport(LibX11)]
    public static extern int XFlush(nint display);

    [DllImport(LibX11)]
    public static extern int XSync(nint display, int discard);

    [DllImport(LibX11)]
    public static extern int XSelectInput(nint display, nuint window, nint mask);

    [DllImport(LibX11)]
    public static extern nuint XInternAtom(nint display, string name, int onlyIfExists);

    [DllImport(LibX11)]
    public static extern nint XGetAtomName(nint display, nuint atom);

    [DllImport(LibX11)]
    public static extern int XFree(nint data);

    [DllImport(LibX11)]
    public static extern int XQueryPointer(nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);

    [DllImport(LibX11)]
    public static extern byte XKeysymToKeycode(nint display, nuint keysym);

    [DllImport(LibX11)]
    public static extern nuint XkbKeycodeToKeysym(nint display, byte keycode, int group, int level);

    [DllImport(LibX11)]
    public static extern int XkbGetState(nint display, uint device, byte* state);

    [DllImport(LibX11)]
    public static extern int XDisplayKeycodes(nint display, out int lowest, out int highest);

    [DllImport(LibX11)]
    public static extern nuint* XGetKeyboardMapping(nint display, byte first, int count, out int symbolsPerKey);

    [DllImport(LibX11)]
    public static extern int XChangeKeyboardMapping(nint display, int first, int symbolsPerKey, nuint* symbols, int count);

    [DllImport(LibX11)]
    public static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderColour, nuint background);

    [DllImport(LibX11)]
    public static extern int XDestroyWindow(nint display, nuint window);

    [DllImport(LibX11)]
    public static extern int XSetSelectionOwner(nint display, nuint selection, nuint owner, nuint time);

    [DllImport(LibX11)]
    public static extern nuint XGetSelectionOwner(nint display, nuint selection);

    [DllImport(LibX11)]
    public static extern int XConvertSelection(nint display, nuint selection, nuint target, nuint property, nuint requestor, nuint time);

    [DllImport(LibX11)]
    public static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length, int delete, nuint requestedType, out nuint type, out int format, out nuint count, out nuint bytesLeft, out nint data);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, byte* data, int count);

    [DllImport(LibX11)]
    public static extern int XSendEvent(nint display, nuint window, int propagate, nint mask, XEvent* sent);

    // ---- shared-memory pictures ----------------------------------------------------------------------------

    [DllImport(LibXext)]
    public static extern int XShmQueryExtension(nint display);

    [DllImport(LibXext)]
    public static extern XImage* XShmCreateImage(nint display, nint visual, uint depth, int format, nint data, XShmSegmentInfo* segment, uint width, uint height);

    [DllImport(LibXext)]
    public static extern int XShmAttach(nint display, XShmSegmentInfo* segment);

    [DllImport(LibXext)]
    public static extern int XShmDetach(nint display, XShmSegmentInfo* segment);

    [DllImport(LibXext)]
    public static extern int XShmGetImage(nint display, nuint drawable, XImage* image, int x, int y, nuint planes);

    [DllImport("libc", SetLastError = true)]
    public static extern int shmget(int key, nuint size, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern nint shmat(int id, nint address, int flags);

    [DllImport("libc")]
    public static extern int shmdt(nint address);

    [DllImport("libc")]
    public static extern int shmctl(int id, int command, nint buffer);

    [DllImport("libc")]
    public static extern int poll(PollFd* descriptors, nuint count, int timeout);

    // Waits until the X server said something, at most `milliseconds`.
    public static void Wait(nint display, int milliseconds)
    {
        if (XPending(display) > 0)
        {
            return;
        }

        var descriptor = new PollFd { Fd = XConnectionNumber(display), Events = 1 };
        poll(&descriptor, 1, Math.Max(0, milliseconds));
    }

    // ---- extensions ----------------------------------------------------------------------------------------

    [DllImport(LibXfixes)]
    public static extern int XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

    [DllImport(LibXfixes)]
    public static extern XFixesCursorImage* XFixesGetCursorImage(nint display);

    [DllImport(LibXfixes)]
    public static extern void XFixesSelectCursorInput(nint display, nuint window, nuint mask);

    [DllImport(LibXfixes)]
    public static extern void XFixesSelectSelectionInput(nint display, nuint window, nuint selection, nuint mask);

    [DllImport(LibXtst)]
    public static extern int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);

    [DllImport(LibXtst)]
    public static extern int XTestFakeKeyEvent(nint display, uint keycode, int press, nuint delay);

    [DllImport(LibXtst)]
    public static extern int XTestFakeButtonEvent(nint display, uint button, int press, nuint delay);

    [DllImport(LibXtst)]
    public static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);

    [DllImport(LibXdamage)]
    public static extern int XDamageQueryExtension(nint display, out int eventBase, out int errorBase);

    [DllImport(LibXdamage)]
    public static extern nuint XDamageCreate(nint display, nuint drawable, int level);

    [DllImport(LibXdamage)]
    public static extern void XDamageSubtract(nint display, nuint damage, nuint repair, nuint parts);

    [DllImport(LibXrandr)]
    public static extern int XRRQueryExtension(nint display, out int eventBase, out int errorBase);

    [DllImport(LibXrandr)]
    public static extern XRRMonitorInfo* XRRGetMonitors(nint display, nuint window, int active, out int count);

    [DllImport(LibXrandr)]
    public static extern void XRRFreeMonitors(XRRMonitorInfo* monitors);

    [DllImport(LibXrandr)]
    public static extern XRRScreenSize* XRRSizes(nint display, int screen, out int count);

    [DllImport(LibXrandr)]
    public static extern nint XRRGetScreenInfo(nint display, nuint window);

    [DllImport(LibXrandr)]
    public static extern ushort XRRConfigCurrentConfiguration(nint configuration, out ushort rotation);

    [DllImport(LibXrandr)]
    public static extern int XRRSetScreenConfig(nint display, nint configuration, nuint drawable, int size, ushort rotation, nuint time);

    [DllImport(LibXrandr)]
    public static extern void XRRFreeScreenConfigInfo(nint configuration);

    [DllImport(LibXrandr)]
    public static extern void XRRSelectInput(nint display, nuint window, int mask);
}

// Any event: 24 longs. What an event carries is read by its place - after the type, the serial, the "sent"
// flag and the display, every kind has its own fields from the fifth long on.
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal unsafe struct XEvent
{
    [FieldOffset(0)]
    public int Type;

    [FieldOffset(0)]
    public fixed ulong Field[24];
}

[StructLayout(LayoutKind.Sequential)]
internal struct XImage
{
    public int Width;
    public int Height;
    public int XOffset;
    public int Format;
    public nint Data;
    public int ByteOrder;
    public int BitmapUnit;
    public int BitmapBitOrder;
    public int BitmapPad;
    public int Depth;
    public int BytesPerLine;
    public int BitsPerPixel;
    public nuint RedMask;
    public nuint GreenMask;
    public nuint BlueMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XShmSegmentInfo
{
    public nuint Segment;
    public int Id;
    public nint Address;
    public int ReadOnly;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XFixesCursorImage
{
    public short X;
    public short Y;
    public ushort Width;
    public ushort Height;
    public ushort HotX;
    public ushort HotY;
    public nuint Serial;
    public nint Pixels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XRRMonitorInfo
{
    public nuint Name;
    public int Primary;
    public int Automatic;
    public int OutputCount;
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public int MillimetresWide;
    public int MillimetresHigh;
    public nint Outputs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XRRScreenSize
{
    public int Width;
    public int Height;
    public int MillimetresWide;
    public int MillimetresHigh;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int Fd;
    public short Events;
    public short Returned;
}
