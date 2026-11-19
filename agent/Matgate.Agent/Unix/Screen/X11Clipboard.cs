using System.Collections.Concurrent;
using System.Text;
using Matgate.Agent.Screen;

namespace Matgate.Agent.Unix.Screen;

// The X clipboard. Nobody holds what was copied: the program that copied "owns the selection" and hands the
// content to whoever asks. So the agent watches who owns it (XFixes) and asks for text when that changes;
// and what the viewer copied, the agent owns and hands out itself - text, a picture, or files as the list of
// addresses a file manager pastes from. Read from the machine: text only.
internal sealed unsafe class X11Clipboard : IClipboardBridge
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private volatile bool _running = true;
    private nint _display;
    private nuint _window;
    private nuint _clipboard;
    private nuint _utf8;
    private nuint _targets;
    private nuint _property;
    private nuint _incremental;
    private nuint _png;
    private nuint _uriList;
    private nuint _gnomeFiles;
    private int _fixesEvent = -1;
    private ClipContent? _owned;

    public X11Clipboard()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Matgate clipboard" };
        _thread.Start();
    }

    public event Action<ClipContent>? Changed;

    public Task SetAsync(ClipContent content)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            _owned = content;
            X11.XSetSelectionOwner(_display, _clipboard, _window, 0);
            X11.XFlush(_display);
            if (X11.XGetSelectionOwner(_display, _clipboard) == _window)
            {
                done.TrySetResult();
            }
            else
            {
                done.TrySetException(new InvalidOperationException("The clipboard did not take it."));
            }
        });
        return done.Task;
    }

    private void Run()
    {
        try
        {
            _display = X11.Open();
            _window = X11.XCreateSimpleWindow(_display, X11.XDefaultRootWindow(_display), -10, -10, 1, 1, 0, 0, 0);
            _clipboard = X11.Atom(_display, "CLIPBOARD");
            _utf8 = X11.Atom(_display, "UTF8_STRING");
            _targets = X11.Atom(_display, "TARGETS");
            _property = X11.Atom(_display, "MATGATE_CLIPBOARD");
            _incremental = X11.Atom(_display, "INCR");
            _png = X11.Atom(_display, "image/png");
            _uriList = X11.Atom(_display, "text/uri-list");
            _gnomeFiles = X11.Atom(_display, "x-special/gnome-copied-files");
            if (X11.XFixesQueryExtension(_display, out _fixesEvent, out _) != 0)
            {
                // XFixesSetSelectionOwnerNotifyMask
                X11.XFixesSelectSelectionInput(_display, _window, _clipboard, 1);
            }
            else
            {
                _fixesEvent = -1;
            }

            X11.XFlush(_display);
            XEvent found;
            while (_running)
            {
                while (_work.TryDequeue(out var work))
                {
                    work();
                }

                X11.Wait(_display, 50);
                while (_running && X11.XPending(_display) > 0)
                {
                    X11.XNextEvent(_display, &found);
                    Handle(&found);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AgentLog.Write("Clipboard: " + exception.Message);
        }
        finally
        {
            if (_display != 0)
            {
                X11.XCloseDisplay(_display);
            }
        }
    }

    private void Handle(XEvent* found)
    {
        if (_fixesEvent >= 0 && found->Type == _fixesEvent)
        {
            // XFixesSelectionNotify: window, subtype, owner, selection.
            var owner = (nuint)found->Field[6];
            if (owner != 0 && owner != _window)
            {
                _owned = null;
                X11.XConvertSelection(_display, _clipboard, _utf8, _property, _window, 0);
                X11.XFlush(_display);
            }
        }
        else if (found->Type == X11.SelectionNotify)
        {
            // requestor, selection, target, property - none when the owner has no text.
            if ((nuint)found->Field[7] == 0)
            {
                return;
            }

            var bytes = X11.Property(_display, _window, _property, out var format, out var type, delete: true);
            if (bytes is { Length: > 0 } && format == 8 && type != _incremental)
            {
                Changed?.Invoke(new ClipContent("text", Text: Encoding.UTF8.GetString(bytes)));
            }
        }
        else if (found->Type == X11.SelectionRequest)
        {
            Answer(found);
        }
        else if (found->Type == X11.SelectionClear)
        {
            _owned = null;
        }
    }

    // Somebody pastes: owner, requestor, selection, target, property, time.
    private void Answer(XEvent* request)
    {
        var requestor = (nuint)request->Field[5];
        var target = (nuint)request->Field[7];
        var property = (nuint)request->Field[8];
        if (property == 0)
        {
            property = target;
        }

        var content = _owned;
        var offered = content?.Kind switch
        {
            "text" => new[] { _utf8, X11.AtomString },
            "image" => [_png],
            "files" => [_uriList, _gnomeFiles],
            _ => [],
        };
        var given = true;
        if (target == _targets)
        {
            var list = new nuint[offered.Length + 1];
            list[0] = _targets;
            offered.CopyTo(list, 1);
            fixed (nuint* data = list)
            {
                X11.XChangeProperty(_display, requestor, property, X11.AtomAtom, 32, 0, (byte*)data, list.Length);
            }
        }
        else if (content is not null && offered.Contains(target))
        {
            byte[] bytes;
            if (content.Kind == "text")
            {
                bytes = Encoding.UTF8.GetBytes(content.Text ?? "");
            }
            else if (content.Kind == "image")
            {
                bytes = content.Png ?? [];
            }
            else
            {
                var addresses = (content.Files ?? []).Select(path => new Uri(path).AbsoluteUri);
                bytes = Encoding.UTF8.GetBytes(target == _gnomeFiles ? "copy\n" + string.Join("\n", addresses) : string.Join("\r\n", addresses) + "\r\n");
            }

            fixed (byte* data = bytes)
            {
                X11.XChangeProperty(_display, requestor, property, target, 8, 0, data, bytes.Length);
            }
        }
        else
        {
            given = false;
        }

        // SelectionNotify: requestor, selection, target, property, time.
        XEvent answer = default;
        answer.Type = X11.SelectionNotify;
        answer.Field[2] = 1;
        answer.Field[3] = (ulong)_display;
        answer.Field[4] = requestor;
        answer.Field[5] = request->Field[6];
        answer.Field[6] = target;
        answer.Field[7] = given ? property : 0;
        answer.Field[8] = request->Field[9];
        X11.XSendEvent(_display, requestor, 0, 0, &answer);
        X11.XFlush(_display);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
