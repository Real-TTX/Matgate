using System.Text;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// The windows of an X desktop, as its window manager lists them (EWMH - every usual desktop has it; without
// a window manager the list is empty).
internal sealed unsafe class X11WindowList : IWindowList
{
    private readonly object _gate = new();

    public IReadOnlyList<WindowInfo> List()
    {
        lock (_gate)
        {
            var display = X11Monitors.Display;
            var root = X11.XDefaultRootWindow(display);
            var active = X11.Numbers(display, root, X11.Atom(display, "_NET_ACTIVE_WINDOW")).FirstOrDefault();
            var hidden = X11.Atom(display, "_NET_WM_STATE_HIDDEN");
            var skipped = X11.Atom(display, "_NET_WM_STATE_SKIP_TASKBAR");
            var state = X11.Atom(display, "_NET_WM_STATE");
            var list = new List<WindowInfo>();
            foreach (var window in X11.Numbers(display, root, X11.Atom(display, "_NET_CLIENT_LIST")))
            {
                X11.LastError = 0;
                var states = X11.Numbers(display, window, state);
                if (X11.LastError != 0 || states.Contains(skipped))
                {
                    continue;
                }

                var title = Text(display, window, X11.Atom(display, "_NET_WM_NAME")) ?? Text(display, window, X11.Atom(display, "WM_NAME")) ?? "";
                // WM_CLASS: the instance and the class, each closed by a zero - the class is the program.
                var names = (Text(display, window, X11.Atom(display, "WM_CLASS")) ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (title.Length == 0 && names.Length == 0)
                {
                    continue;
                }

                list.Add(new WindowInfo((long)window, title, names.LastOrDefault() ?? "", window == active, states.Contains(hidden)));
            }

            return list;
        }
    }

    public WindowBounds? Bounds(long id)
    {
        lock (_gate)
        {
            var display = X11Monitors.Display;
            var window = (nuint)id;
            X11.LastError = 0;
            if (X11.Numbers(display, window, X11.Atom(display, "_NET_WM_STATE")).Contains(X11.Atom(display, "_NET_WM_STATE_HIDDEN")))
            {
                return null;
            }

            if (X11.XGetGeometry(display, window, out var root, out _, out _, out var width, out var height, out _, out _) == 0 || X11.LastError != 0)
            {
                return null;
            }

            X11.XTranslateCoordinates(display, window, root, 0, 0, out var left, out var top, out _);
            return X11.LastError != 0 ? null : new WindowBounds(left, top, (int)width, (int)height);
        }
    }

    public bool Activate(long id)
    {
        lock (_gate)
        {
            var display = X11Monitors.Display;
            var root = X11.XDefaultRootWindow(display);
            // A client message to the window manager: window, type, format, then "from a pager" (2).
            XEvent message = default;
            message.Type = X11.ClientMessage;
            message.Field[2] = 1;
            message.Field[3] = (ulong)display;
            message.Field[4] = (ulong)id;
            message.Field[5] = X11.Atom(display, "_NET_ACTIVE_WINDOW");
            message.Field[6] = 32;
            message.Field[7] = 2;
            X11.LastError = 0;
            X11.XSendEvent(display, root, 0, X11.SubstructureNotifyMask | X11.SubstructureRedirectMask, &message);
            X11.XSync(display, 0);
            return X11.LastError == 0;
        }
    }

    private static string? Text(nint display, nuint window, nuint property)
    {
        var bytes = X11.Property(display, window, property, out var format, out _);
        return bytes is { Length: > 0 } && format == 8 ? Encoding.UTF8.GetString(bytes).TrimEnd('\0') : null;
    }
}
