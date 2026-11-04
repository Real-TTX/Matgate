using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace Matgate.Agent.Screen;

// What is on a clipboard: text, a picture (PNG) or files (paths on this machine).
public sealed record ClipContent(string Kind, string? Text = null, byte[]? Png = null, IReadOnlyList<string>? Files = null);

public interface IClipboardBridge : IDisposable
{
    // Something on this machine copied: text, a picture or files.
    event Action<ClipContent>? Changed;

    Task SetAsync(ClipContent content);
}

// The Windows clipboard, watched from its own STA thread (the clipboard belongs to a window, and a window
// to a thread with a message loop). What the agent puts there itself is not reported back as a change.
public sealed class WindowsClipboard : IClipboardBridge
{
    private const int MaxImageBytes = 3 * 1024 * 1024;
    private const int MaxTextChars = 1024 * 1024;

    private readonly Thread _thread;
    private readonly TaskCompletionSource<SynchronizationContext> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _own;

    public WindowsClipboard()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Matgate clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait(TimeSpan.FromSeconds(5));
    }

    public event Action<ClipContent>? Changed;

    private void Run()
    {
        var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        using var listener = new Listener(this);
        _ready.TrySetResult(context);
        Application.Run();
    }

    public Task SetAsync(ClipContent content)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready.Task.Result.Post(_ =>
        {
            try
            {
                var data = new DataObject();
                switch (content.Kind)
                {
                    case "text":
                        data.SetData(DataFormats.UnicodeText, content.Text ?? "");
                        break;
                    case "image" when content.Png is not null:
                        using (var stream = new MemoryStream(content.Png))
                        {
                            data.SetImage(new Bitmap(stream));
                        }

                        data.SetData("PNG", new MemoryStream(content.Png));
                        break;
                    case "files" when content.Files is { Count: > 0 }:
                        var list = new StringCollection();
                        list.AddRange([.. content.Files]);
                        data.SetFileDropList(list);
                        // Copy, not move: Explorer pastes a copy and leaves the received files where they are.
                        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
                        break;
                }

                Clipboard.SetDataObject(data, true, 10, 50);
                _own = GetClipboardSequenceNumber();
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }, null);
        return done.Task;
    }

    private void OnUpdate()
    {
        if (GetClipboardSequenceNumber() == _own)
        {
            return;
        }

        try
        {
            ClipContent? content = null;
            if (Clipboard.ContainsFileDropList())
            {
                content = new ClipContent("files", Files: [.. Clipboard.GetFileDropList().Cast<string>()]);
            }
            else if (Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image is not null)
                {
                    using var png = new MemoryStream();
                    image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    if (png.Length <= MaxImageBytes)
                    {
                        content = new ClipContent("image", Png: png.ToArray());
                    }
                }
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                content = new ClipContent("text", Text: text.Length > MaxTextChars ? text[..MaxTextChars] : text);
            }

            if (content is not null)
            {
                Changed?.Invoke(content);
            }
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard open; its next change comes as a new notice.
        }
    }

    public void Dispose()
    {
        if (_ready.Task.IsCompletedSuccessfully)
        {
            _ready.Task.Result.Post(_ => Application.ExitThread(), null);
        }
    }

    // A message-only window that Windows tells about every clipboard change.
    private sealed class Listener : NativeWindow, IDisposable
    {
        private const int ClipboardUpdate = 0x031D;
        private readonly WindowsClipboard _owner;
        private readonly System.Windows.Forms.Timer _settle = new() { Interval = 120 };

        public Listener(WindowsClipboard owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = new nint(-3) });
            AddClipboardFormatListener(Handle);
            // Programs put several formats on the clipboard one after the other: read once they are done.
            _settle.Tick += (_, _) =>
            {
                _settle.Stop();
                _owner.OnUpdate();
            };
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ClipboardUpdate)
            {
                _settle.Stop();
                _settle.Start();
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            _settle.Dispose();
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(nint window);
}

// For the tests: the real clipboard stays untouched. What is set is reported, and comes back a moment
// later as if a program on the machine had copied it - so both directions can be checked.
public sealed class RecordingClipboard(Action<string> report) : IClipboardBridge
{
    public event Action<ClipContent>? Changed;

    public Task SetAsync(ClipContent content)
    {
        report(content.Kind switch
        {
            "text" => "clipboard text " + content.Text,
            "image" => $"clipboard image {content.Png?.Length ?? 0}",
            _ => "clipboard files " + string.Join("|", (content.Files ?? []).Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length)),
        });
        _ = Task.Delay(300).ContinueWith(_ => Changed?.Invoke(content));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
