using System.Text;
using System.Text.Json.Nodes;
using Matgate.Display.Vnc;

namespace Matgate.Display;

// VNC for a screen session: Matgate's own VNC client plus what a viewer needs around it - asking for the
// password, the text clipboard, and a desktop that follows the viewer's window where the server can do that.
internal sealed class VncBackend : IScreenBackend, IVncHandler
{
    private const uint ControlLeft = 0xFFE3;
    private const uint AltLeft = 0xFFE9;
    private const uint AltRight = 0xFFEA;
    private const uint Level3Shift = 0xFE03;

    private readonly ScreenTarget _target;
    private readonly IScreenHost _host;
    private readonly List<uint> _keysDown = [];
    private VncClient? _client;
    private int _x;
    private int _y;
    private int _buttons;
    private int _wantWidth;
    private int _wantHeight;
    private int _askedWidth;
    private int _askedHeight;
    private long _askedAt;

    public VncBackend(ScreenTarget target, IScreenHost host)
    {
        _target = target;
        _host = host;
    }

    public string Name => "vnc";

    // VNC knows text on the clipboard and nothing else, and has no sound.
    public bool ClipImages => false;

    public bool ClipFiles => false;

    public bool Sound => false;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new VncClient(
            new VncOptions
            {
                Host = _target.Host,
                Port = _target.Port > 0 ? _target.Port : 5900,
                Username = _target.UserName.Length > 0 ? _target.UserName : null,
                Password = _target.Password.Length > 0 ? _target.Password : null,
            },
            this,
            _host.Log);
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (VncException exception)
        {
            await client.DisposeAsync();
            throw new ScreenException(exception.Message);
        }

        _client = client;
        var (width, height) = _host.WantedSize();
        Resize(width, height);
    }

    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        var client = _client!;
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resizing = Task.Run(() => FollowSizeAsync(client, ticking.Token), CancellationToken.None);
        try
        {
            await client.RunAsync(cancellationToken);
            return null;
        }
        catch (VncException exception)
        {
            return exception.Message;
        }
        finally
        {
            ticking.Cancel();
            await resizing;
        }
    }

    public bool WithFrame(FrameReader reader)
    {
        var client = _client;
        if (client is null)
        {
            return false;
        }

        lock (client.FrameLock)
        {
            if (client.Framebuffer.Length == 0 || client.Width < 1 || client.Height < 1)
            {
                return false;
            }

            reader(client.Framebuffer, client.Width * 4, client.Width, client.Height);
            return true;
        }
    }

    // ---- from the client ------------------------------------------------------------------------------------

    void IVncHandler.Updated(int x, int y, int width, int height) => _host.Dirty(x, y, width, height);

    void IVncHandler.Resized(int width, int height) => _host.Dirty(0, 0, width, height);

    void IVncHandler.Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width == 0 || height == 0)
        {
            _host.CursorHidden(true);
            return;
        }

        _host.Cursor(hotX, hotY, width, height, rgba);
    }

    void IVncHandler.ClipboardText(string text)
    {
        _host.Event("screen.clip", new JsonObject { ["kind"] = "text" }, Encoding.UTF8.GetBytes(text));
    }

    async Task<VncCredentials?> IVncHandler.CredentialsAsync(bool needUsername, bool failed, CancellationToken cancellationToken)
    {
        string[] need = needUsername ? ["username", "password"] : ["password"];
        var answer = await _host.AskAsync(need, _target.UserName, "", failed, cancellationToken);
        return answer is null ? null : new VncCredentials(answer.UserName.Length > 0 ? answer.UserName : null, answer.Password);
    }

    // ---- the size of the viewer's window -------------------------------------------------------------------

    public void Resize(int width, int height)
    {
        _wantWidth = width;
        _wantHeight = height;
    }

    // The window has another size than the desktop: the server is asked to follow (it can once it has told
    // its screen layout - not every VNC server does). A new size at most every 300 ms, the same one again
    // only after a while.
    private async Task FollowSizeAsync(VncClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
                if (_wantWidth <= 0 || !client.CanResize || (client.Width == _wantWidth && client.Height == _wantHeight))
                {
                    continue;
                }

                var fresh = _wantWidth != _askedWidth || _wantHeight != _askedHeight;
                var now = Environment.TickCount64;
                if (now - _askedAt > (fresh ? 300 : 5000))
                {
                    client.RequestSize(_wantWidth, _wantHeight);
                    _askedWidth = _wantWidth;
                    _askedHeight = _wantHeight;
                    _askedAt = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- mouse and keyboard ----------------------------------------------------------------------------------

    public void Input(JsonObject data)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        var type = ScreenJson.Text(data, "t");
        switch (type.Length > 0 ? type[0] : ' ')
        {
            case 'm':
                _x = ScreenJson.Int(data, "x");
                _y = ScreenJson.Int(data, "y");
                client.Pointer(_x, _y, _buttons);
                break;
            case 'b':
                var mask = ScreenJson.Int(data, "b") switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    _ => 0,
                };
                _buttons = ScreenJson.Flag(data, "d") ? _buttons | mask : _buttons & ~mask;
                client.Pointer(_x, _y, _buttons);
                break;
            case 'w':
                Wheel(client, ScreenJson.Int(data, "dy"), 8, 16);
                Wheel(client, ScreenJson.Int(data, "dx"), 64, 32);
                break;
            case 'k':
                var keysym = (uint)ScreenJson.Long(data, "ks");
                var down = ScreenJson.Flag(data, "d");
                if (keysym == 0)
                {
                    break;
                }

                if (down && ScreenJson.Flag(data, "ag"))
                {
                    // A character typed with AltGr: the keysym says it all - the Ctrl+Alt a Windows browser
                    // reports for AltGr must not turn it into a shortcut on the server.
                    foreach (var modifier in new[] { ControlLeft, AltRight, Level3Shift })
                    {
                        bool held;
                        lock (_keysDown)
                        {
                            held = _keysDown.Contains(modifier);
                        }

                        if (held)
                        {
                            Key(client, modifier, false);
                        }
                    }
                }

                Key(client, keysym, down);
                break;
            case 'u':
                foreach (var unit in ScreenJson.Text(data, "text"))
                {
                    // Latin-1 has keysyms of its own; everything else is "Unicode keysym".
                    Tap(client, unit < 0x100 ? unit : 0x01000000u | unit);
                }

                break;
            case 'r':
                uint[] held2;
                lock (_keysDown)
                {
                    held2 = [.. _keysDown];
                }

                foreach (var key in held2)
                {
                    Key(client, key, false);
                }

                _buttons = 0;
                client.Pointer(_x, _y, 0);
                break;
        }
    }

    private void Key(VncClient client, uint keysym, bool down)
    {
        lock (_keysDown)
        {
            if (down)
            {
                if (_keysDown.Count < 64 && !_keysDown.Contains(keysym))
                {
                    _keysDown.Add(keysym);
                }
            }
            else
            {
                _keysDown.Remove(keysym);
            }
        }

        client.Key(keysym, down);
    }

    private void Tap(VncClient client, uint keysym)
    {
        Key(client, keysym, true);
        Key(client, keysym, false);
    }

    // The wheel is two more mouse buttons in VNC: a click per notch.
    private void Wheel(VncClient client, int value, int upButton, int downButton)
    {
        var notches = Math.Abs(value) / 120;
        if (notches == 0 && value != 0)
        {
            notches = 1;
        }

        var button = value > 0 ? upButton : downButton;
        for (var i = 0; i < notches && i < 10; i++)
        {
            client.Pointer(_x, _y, _buttons | button);
            client.Pointer(_x, _y, _buttons);
        }
    }

    public void SecureAttention()
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        Key(client, ControlLeft, true);
        Key(client, AltLeft, true);
        Tap(client, 0xFFFF);
        Key(client, AltLeft, false);
        Key(client, ControlLeft, false);
    }

    public void SoundEnabled(bool on)
    {
    }

    // ---- the clipboard ---------------------------------------------------------------------------------------

    public void Clip(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        if (op != "screen.clip.set")
        {
            // Files are not part of VNC.
            if (op == "screen.clip.begin")
            {
                _host.Status("clipfailed");
            }

            return;
        }

        client.SetClipboardText(Encoding.UTF8.GetString(payload.Span));
        var paste = ScreenJson.Flag(data, "paste");
        if (paste)
        {
            // Ctrl+V on the remote desktop.
            Key(client, ControlLeft, true);
            Tap(client, 0x76);
            Key(client, ControlLeft, false);
        }

        _host.Event("screen.clip.done", new JsonObject { ["paste"] = paste });
    }

    public Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken)
    {
        return Task.FromException<ClipPiece>(new ScreenException("This file is no longer on the remote clipboard."));
    }

    public async ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }
}
