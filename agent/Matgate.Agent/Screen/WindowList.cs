using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Matgate.Agent.Screen;

// One window of the machine's desktop, as the viewer picks it from a list: what Alt+Tab would offer.
public sealed record WindowInfo(long Id, string Title, string App, bool Active, bool Minimized);

// Where a window is on the desktop, in the desktop's own pixels.
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

// The windows of the desktop: which there are, where one is, and bringing one to the front. The viewer has
// no Alt+Tab of its own worth the name - on a phone least of all - so the machine's windows are offered as
// a list, and one of them can be the whole picture.
public interface IWindowList
{
    IReadOnlyList<WindowInfo> List();

    // Null when the window is gone or minimised.
    WindowBounds? Bounds(long id);

    // In front of everything and with the keyboard; a minimised window comes back first.
    bool Activate(long id);
}

public sealed class WindowsWindowList : IWindowList
{
    private const int GwlExStyle = -20;
    private const long ExToolWindow = 0x00000080;
    private const long ExAppWindow = 0x00040000;
    private const long ExNoActivate = 0x08000000;
    private const uint GwOwner = 4;
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;
    private const int ShowRestore = 9;

    public IReadOnlyList<WindowInfo> List()
    {
        var foreground = GetForegroundWindow();
        var handles = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (IsOffered(window))
            {
                handles.Add(window);
            }

            return true;
        }, 0);

        var names = new Dictionary<uint, string>();
        var list = new List<WindowInfo>(handles.Count);
        foreach (var window in handles)
        {
            GetWindowThreadProcessId(window, out var process);
            if (!names.TryGetValue(process, out var app))
            {
                app = ProcessName(process);
                names[process] = app;
            }

            list.Add(new WindowInfo(window, Title(window), app, window == foreground, IsIconic(window)));
        }

        return list;
    }

    public WindowBounds? Bounds(long id)
    {
        var window = (nint)id;
        if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window))
        {
            return null;
        }

        // What is drawn of the window: GetWindowRect counts the invisible border a window is resized by.
        if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out frame))
        {
            return null;
        }

        return new WindowBounds(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    public bool Activate(long id)
    {
        var window = (nint)id;
        if (!IsWindow(window))
        {
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindow(window, ShowRestore);
        }

        // Windows lets only the process that has the keyboard hand it on. Joining the input of the thread
        // that has it is the way a task switcher takes that right; SwitchToThisWindow is the fallback.
        var foreground = GetForegroundWindow();
        var owner = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var joined = owner != 0 && owner != self && AttachThreadInput(self, owner, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (joined)
            {
                AttachThreadInput(self, owner, false);
            }
        }

        if (GetForegroundWindow() != window)
        {
            SwitchToThisWindow(window, true);
        }

        return GetForegroundWindow() == window;
    }

    // What Alt+Tab shows: visible, with a title, not a tool window, not owned by another window (unless it
    // asks for a place in the task bar), and not one of the windows Windows keeps hidden ("cloaked").
    private static bool IsOffered(nint window)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0)
        {
            return false;
        }

        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        var app = (style & ExAppWindow) != 0;
        if ((style & ExNoActivate) != 0 || ((style & ExToolWindow) != 0 && !app))
        {
            return false;
        }

        if (GetWindow(window, GwOwner) != 0 && !app)
        {
            return false;
        }

        return DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private static string Title(nint window)
    {
        var length = GetWindowTextLength(window);
        var text = new StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string ProcessName(uint process)
    {
        try
        {
            using var found = Process.GetProcessById((int)process);
            return found.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone meanwhile, or not ours to look at.
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint relation);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint window, bool viaAltTab);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint thread, uint to, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}

// For the tests: three windows on the generated picture, one of them minimised. Bringing one to the front is
// reported back instead of done.
public sealed class TestWindowList(Action<string> report) : IWindowList
{
    private static readonly (long Id, string Title, string App, WindowBounds Bounds, bool Minimized)[] Windows =
    [
        (101, "notes.txt - Editor", "notepad", new WindowBounds(100, 80, 640, 400), false),
        (102, "Terminal", "WindowsTerminal", new WindowBounds(300, 200, 501, 301), false),
        (103, "Rechner", "CalculatorApp", new WindowBounds(0, 0, 320, 480), true),
    ];

    // Asked from the thread that takes the pictures and from the one that takes the input.
    private readonly object _gate = new();
    private readonly HashSet<long> _restored = [];
    private long _active = 101;

    public IReadOnlyList<WindowInfo> List()
    {
        lock (_gate)
        {
            return [.. Windows.Select(window => new WindowInfo(window.Id, window.Title, window.App, window.Id == _active, window.Minimized && !_restored.Contains(window.Id)))];
        }
    }

    public WindowBounds? Bounds(long id)
    {
        lock (_gate)
        {
            foreach (var window in Windows)
            {
                if (window.Id == id)
                {
                    return window.Minimized && !_restored.Contains(id) ? null : window.Bounds;
                }
            }

            return null;
        }
    }

    public bool Activate(long id)
    {
        lock (_gate)
        {
            if (Windows.All(window => window.Id != id))
            {
                return false;
            }

            _restored.Add(id);
            _active = id;
        }

        report($"window {id} front");
        return true;
    }
}
