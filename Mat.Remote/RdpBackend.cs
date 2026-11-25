using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Matgate.Display.Rdp;
using Matgate.Display.Rdp.Channels;
using Matgate.Display.Video;

namespace Matgate.Display;

// RDP for a screen session: Matgate's own RDP client plus what a viewer needs around it - asking for a
// password when the server wants one, the clipboard in both directions (text, pictures, files), sound, and a
// desktop that follows the size of the viewer's window.
internal sealed class RdpBackend : IScreenBackend, IRdpHandler
{
    // The id under which this side announces a list of files; the name is what the server goes by.
    private const uint FileListFormat = 0xC0D0;
    private const string FileListName = "FileGroupDescriptorW";
    private const uint TextFormat = 13;
    private const uint BitmapFormat = 8;
    private const int ReadChunk = 256 * 1024;
    private const int MaxUploads = 4;
    private const int MaxUploadFiles = 500;

    private readonly ScreenTarget _target;
    private readonly IScreenHost _host;
    private readonly object _clip = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]?>> _downloads = new();
    private readonly Dictionary<string, Upload> _uploads = [];
    private readonly HashSet<int> _keysDown = [];

    private RdpClient? _client;
    private ClipboardChannel? _clipboard;
    private DisplayControlChannel? _display;
    private SoundChannel? _sound;
    private int _buttonsDown;

    // What the viewer put on the clipboard: 0 nothing, 1 text, 2 a picture, 3 files.
    private int _localKind;
    private string _localText = "";
    private byte[] _localImage = [];
    private List<LocalFile> _localFiles = [];
    private bool _pastePending;

    // What the remote desktop offers.
    private uint _remoteFileFormat;
    private uint _requestedFormat;
    private List<RemoteFile> _remoteFiles = [];
    private string _remoteToken = "";
    private int _streams;

    // The size the viewer wants, and how far the server is with it.
    private int _wantWidth;
    private int _wantHeight;
    private int _askedWidth;
    private int _askedHeight;
    private long _lastLayout;
    private long _resizedAt;
    private bool _reactivating;
    private bool _settled;

    public RdpBackend(ScreenTarget target, IScreenHost host)
    {
        _target = target;
        _host = host;
    }

    private sealed record LocalFile(string Name, string Path)
    {
        public long Size { get; set; }
    }

    private sealed record RemoteFile(string Name, long Size, bool Folder);

    private sealed class Upload
    {
        public required string Folder { get; init; }

        public required List<LocalFile> Files { get; init; }

        public bool Image { get; init; }

        public bool Paste { get; init; }
    }

    public string Name => "rdp";

    public bool ClipImages => true;

    public bool ClipFiles => true;

    public bool Sound => true;

    public int ColorDepth => _client?.ColorDepth ?? 0;

    public bool QualityColours => true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var userName = _target.UserName;
        var password = _target.Password;
        var domain = _target.Domain;
        var (width, height) = _host.WantedSize();
        // Colours and looks are asked for once, here: the fastest response wants 16 bits and no wallpaper,
        // animations or smoothed fonts - less for the server to draw and to send.
        var quality = ScreenQuality.From(_target.Quality);
        var refused = false;
        if (password.Length == 0)
        {
            // Nothing stored: ask before connecting, so the session signs in by itself - instead of the
            // server's own sign-in screen, or a server that wants the password up front refusing outright.
            var first = await _host.AskAsync(["username", "password", "domain"], userName, domain, false, cancellationToken)
                ?? throw new ScreenException("The login was cancelled.");
            userName = first.UserName;
            password = first.Password;
            domain = first.Domain;
        }

        for (var attempt = 0; ; attempt++)
        {
            var clipboard = new ClipboardChannel();
            var display = new DisplayControlChannel();
            var dynamic = new DynamicChannels();
            dynamic.Register(display);
            var sound = new SoundChannel();
            var client = new RdpClient(
                new RdpOptions
                {
                    Host = _target.Host,
                    Port = _target.Port > 0 ? _target.Port : 3389,
                    UserName = userName,
                    Password = password,
                    Domain = domain,
                    Width = width > 0 ? width : 1280,
                    Height = height > 0 ? height : 800,
                    KeyboardLayout = _target.KeyboardLayout,
                    IgnoreCertificate = _target.IgnoreCertificate,
                    Sound = true,
                    ColorDepth = quality.RdpColorDepth,
                    PerformanceFlags = quality.RdpPerformanceFlags,
                },
                this,
                _host.Log);
            client.AddChannel(clipboard);
            client.AddChannel(dynamic);
            client.AddChannel(sound);
            // Windows only sends sound to a client that also has the device channel - with no devices on it.
            client.AddChannel(new RdpdrChannel());
            _clipboard = clipboard;
            _display = display;
            _sound = sound;
            Wire(clipboard, sound);
            try
            {
                await client.ConnectAsync(cancellationToken);
                _client = client;
                return;
            }
            catch (RdpException exception) when (exception.IsLogonFailure && attempt < 3)
            {
                await client.DisposeAsync();
                // The server wants a user name and password before it shows anything, or refused the ones it got.
                var answer = await _host.AskAsync(["username", "password", "domain"], userName, domain, refused, cancellationToken);
                if (answer is null)
                {
                    throw new ScreenException("The login was cancelled.");
                }

                userName = answer.UserName;
                password = answer.Password;
                domain = answer.Domain;
                refused = true;
            }
            catch (RdpException exception)
            {
                await client.DisposeAsync();
                throw new ScreenException(exception.Message);
            }
        }
    }

    public async Task<string?> RunAsync(CancellationToken cancellationToken)
    {
        var client = _client!;
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resizing = Task.Run(() => FollowSizeAsync(client, ticking.Token), CancellationToken.None);
        try
        {
            await client.RunAsync(cancellationToken);
            return client.EndReason;
        }
        catch (RdpException exception)
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
            if (client.Framebuffer.Length == 0)
            {
                return false;
            }

            reader(client.Framebuffer, client.Width * 4, client.Width, client.Height);
            return true;
        }
    }

    // ---- the picture and the pointer, from the client -----------------------------------------------------

    void IRdpHandler.Updated(int x, int y, int width, int height) => _host.Dirty(x, y, width, height);

    void IRdpHandler.Resized(int width, int height)
    {
        _resizedAt = Environment.TickCount64;
        // Whatever the server made of the size asked for is what it is: no asking again for the same.
        _settled = true;
        _host.Dirty(0, 0, width, height);
    }

    void IRdpHandler.Cursor(int hotX, int hotY, int width, int height, ReadOnlySpan<byte> rgba) => _host.Cursor(hotX, hotY, width, height, rgba);

    void IRdpHandler.CursorHidden() => _host.CursorHidden(true);

    void IRdpHandler.CursorDefault() => _host.CursorDefault();

    // ---- the size of the viewer's window -------------------------------------------------------------------

    public void Resize(int width, int height)
    {
        if (width != _wantWidth || height != _wantHeight)
        {
            _wantWidth = width;
            _wantHeight = height;
            _settled = false;
        }
    }

    // A resize is a round trip: the server takes the layout, deactivates the session and brings it back in the
    // new size. One request at a time - a second one in the middle leaves some servers hanging - and asked
    // again after three seconds when nothing started (xrdp drops requests while a login is still running).
    private async Task FollowSizeAsync(RdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, cancellationToken);
                var display = _display;
                var active = client.IsActive;
                if (!active)
                {
                    _reactivating = true;
                }

                if (display is null || !display.IsReady || _wantWidth <= 0 || _settled
                    || (client.Width == _wantWidth && client.Height == _wantHeight))
                {
                    continue;
                }

                var now = Environment.TickCount64;
                var inFlight = _askedWidth > 0 && (client.Width != _askedWidth || client.Height != _askedHeight)
                    && now - _lastLayout < (_reactivating ? 30000 : 3000);
                if (!inFlight && active && now - _lastLayout > 400 && now - _resizedAt > 600)
                {
                    display.SetSize(_wantWidth, _wantHeight);
                    _askedWidth = _wantWidth;
                    _askedHeight = _wantHeight;
                    _lastLayout = now;
                    _reactivating = false;
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
                client.MouseMove(ScreenJson.Int(data, "x"), ScreenJson.Int(data, "y"));
                break;
            case 'b':
                var button = ScreenJson.Int(data, "b");
                var down = ScreenJson.Flag(data, "d");
                if (button is < 0 or > 4)
                {
                    break;
                }

                client.MouseButton(button, down);
                _buttonsDown = down ? _buttonsDown | (1 << button) : _buttonsDown & ~(1 << button);
                break;
            case 'w':
                client.MouseWheel(ScreenJson.Int(data, "dy"), false);
                client.MouseWheel(ScreenJson.Int(data, "dx"), true);
                break;
            case 'k':
                Key(client, ScreenJson.Int(data, "s"), ScreenJson.Flag(data, "e"), ScreenJson.Flag(data, "d"));
                break;
            case 'u':
                // Text from a phone's keyboard. With Ctrl, Alt or the Windows key held (the key bar), a letter
                // is a shortcut and has to arrive as the key it sits on - a character alone is none.
                var shortcut = ShortcutHeld();
                foreach (var unit in ScreenJson.Text(data, "text"))
                {
                    if (shortcut && KeyOf(unit) is { } scancode)
                    {
                        client.Key(scancode, false, true);
                        client.Key(scancode, false, false);
                    }
                    else
                    {
                        client.Unicode(unit);
                    }
                }

                break;
            case 'r':
                // Nothing may stay down when the viewer looks away.
                lock (_keysDown)
                {
                    foreach (var key in _keysDown)
                    {
                        client.Key(key & 0xFF, (key & 0x100) != 0, false);
                    }

                    _keysDown.Clear();
                }

                for (var which = 0; which < 5; which++)
                {
                    if ((_buttonsDown & (1 << which)) != 0)
                    {
                        client.MouseButton(which, false);
                    }
                }

                _buttonsDown = 0;
                break;
        }
    }

    private bool ShortcutHeld()
    {
        lock (_keysDown)
        {
            // Ctrl on either side, the left Alt (the right one is AltGr and types characters), a Windows key.
            return _keysDown.Contains(0x1D) || _keysDown.Contains(0x11D) || _keysDown.Contains(0x38)
                || _keysDown.Contains(0x15B) || _keysDown.Contains(0x15C);
        }
    }

    // Where a letter or digit sits on the keyboard the server was told this session has.
    private int? KeyOf(char character)
    {
        const string Rows = "qwertyuiop    asdfghjkl     zxcvbnm";
        var letter = char.ToLowerInvariant(character);
        var layout = _target.KeyboardLayout & 0xFFFF;
        if (layout is 0x0407 or 0x0807 or 0x100C or 0x040E)
        {
            // QWERTZ: Y and Z have changed places.
            letter = letter switch { 'y' => 'z', 'z' => 'y', _ => letter };
        }
        else if (layout is 0x040C or 0x080C)
        {
            // AZERTY.
            letter = letter switch { 'a' => 'q', 'q' => 'a', 'z' => 'w', 'w' => 'z', 'm' => ';', _ => letter };
            if (char.IsAsciiDigit(letter))
            {
                return null;
            }
        }

        if (letter == ';')
        {
            return 0x27;
        }

        if (char.IsAsciiDigit(letter))
        {
            return letter == '0' ? 0x0B : 0x02 + (letter - '1');
        }

        var at = letter is >= 'a' and <= 'z' ? Rows.IndexOf(letter) : -1;
        return at < 0 ? null : 0x10 + at;
    }

    private void Key(RdpClient client, int scancode, bool extended, bool down)
    {
        if (scancode is <= 0 or > 0xFF)
        {
            return;
        }

        var key = scancode | (extended ? 0x100 : 0);
        lock (_keysDown)
        {
            if (down)
            {
                _keysDown.Add(key);
            }
            else
            {
                _keysDown.Remove(key);
            }
        }

        client.Key(scancode, extended, down);
    }

    // Ctrl+Alt+Del inside the session: the session's own secure attention (lock, task manager, sign out).
    public void SecureAttention()
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        Key(client, 0x1D, false, true);
        Key(client, 0x38, false, true);
        Key(client, 0x53, true, true);
        Key(client, 0x53, true, false);
        Key(client, 0x38, false, false);
        Key(client, 0x1D, false, false);
    }

    public void SoundEnabled(bool on)
    {
        if (_sound is { } sound)
        {
            sound.Enabled = on;
        }
    }

    // ---- the clipboard ---------------------------------------------------------------------------------------

    private void Wire(ClipboardChannel clipboard, SoundChannel sound)
    {
        sound.Samples += (pcm, rate, channels) => _host.Audio(pcm.Span, rate, channels);
        clipboard.Ready += Announce;
        clipboard.RemoteFormats += RemoteCopied;
        clipboard.FormatsAcknowledged += Acknowledged;
        clipboard.DataRequested += RemotePastes;
        clipboard.DataReceived += RemoteData;
        clipboard.FileContentsRequested += RemoteReadsFile;
        clipboard.FileContentsReceived += (stream, data) =>
        {
            if (_downloads.TryRemove(stream, out var waiting))
            {
                waiting.TrySetResult(data);
            }
        };
    }

    // Tells the remote desktop what this side has on its clipboard.
    private void Announce()
    {
        var clipboard = _clipboard;
        if (clipboard is null)
        {
            return;
        }

        IReadOnlyList<ClipFormat> formats;
        lock (_clip)
        {
            formats = _localKind switch
            {
                1 => [new ClipFormat(TextFormat, null)],
                2 => [new ClipFormat(BitmapFormat, null)],
                3 => [new ClipFormat(FileListFormat, FileListName)],
                _ => [],
            };
        }

        clipboard.AnnounceFormats(formats);
    }

    // Something was copied on the remote desktop: fetch it right away, so the viewer has it when pasting.
    private void RemoteCopied(IReadOnlyList<ClipFormat> formats)
    {
        uint files = 0;
        var text = false;
        var image = false;
        foreach (var format in formats)
        {
            if (format.Name == FileListName)
            {
                files = format.Id;
            }
            else if (format.Id == TextFormat)
            {
                text = true;
            }
            else if (format.Id == BitmapFormat)
            {
                image = true;
            }
        }

        uint wanted;
        lock (_clip)
        {
            // The remote side owns the clipboard now.
            _localKind = 0;
            _remoteFileFormat = files;
            // Text or a picture first: many programs (Outlook, browsers, editors) offer a file list next to
            // what was really copied, and only text and pictures reach this device's clipboard. A file list
            // alone is what Explorer copies.
            wanted = text ? TextFormat : image ? BitmapFormat : files;
            _requestedFormat = wanted;
        }

        if (wanted != 0)
        {
            _clipboard?.RequestData(wanted);
        }
    }

    private void Acknowledged(bool accepted)
    {
        bool paste;
        bool announced;
        lock (_clip)
        {
            paste = _pastePending;
            _pastePending = false;
            announced = _localKind != 0;
        }

        if (!announced)
        {
            // The answer to the empty list every session starts with: nothing the viewer is waiting for.
            return;
        }

        if (!accepted)
        {
            _host.Status("clipfailed");
            return;
        }

        if (paste && _client is { } client)
        {
            // Ctrl+V where the remote desktop has its focus; it then asks for the data.
            client.Key(0x1D, false, true);
            client.Key(0x2F, false, true);
            client.Key(0x2F, false, false);
            client.Key(0x1D, false, false);
        }

        _host.Event("screen.clip.done", new JsonObject { ["paste"] = paste });
    }

    // The remote desktop pastes: it asks for what this side announced.
    private void RemotePastes(uint format)
    {
        byte[]? data = null;
        lock (_clip)
        {
            if (format == TextFormat && _localKind == 1)
            {
                data = ClipboardText.Encode(_localText);
            }
            else if (format == BitmapFormat && _localKind == 2)
            {
                data = _localImage;
            }
            else if (format == FileListFormat && _localKind == 3)
            {
                data = FileGroupDescriptor.Encode(_localFiles.Select(file => new ClipFile(file.Name, file.Size, false, null)).ToList());
            }
        }

        _clipboard?.SendData(data);
    }

    private void RemoteData(byte[]? data)
    {
        if (data is null)
        {
            return;
        }

        uint format;
        uint files;
        lock (_clip)
        {
            format = _requestedFormat;
            files = _remoteFileFormat;
        }

        if (files != 0 && format == files)
        {
            var list = FileGroupDescriptor.Decode(data);
            if (list is null || list.Count > 10000)
            {
                return;
            }

            var remote = list.Select(file => new RemoteFile(file.Name.Replace('\\', '/'), file.Size, file.IsDirectory)).ToList();
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            lock (_clip)
            {
                _remoteFiles = remote;
                _remoteToken = token;
            }

            _host.Event("screen.clip", new JsonObject
            {
                ["kind"] = "files",
                ["token"] = token,
                ["files"] = new JsonArray(remote.Select(file => (JsonNode)new JsonObject
                {
                    ["name"] = file.Name,
                    ["size"] = file.Size,
                    ["folder"] = file.Folder,
                }).ToArray()),
            });
        }
        else if (format == TextFormat)
        {
            _host.Event("screen.clip", new JsonObject { ["kind"] = "text" }, Encoding.UTF8.GetBytes(ClipboardText.Decode(data)));
        }
        else if (format == BitmapFormat)
        {
            // A Windows bitmap as a .bmp file - what a browser can decode.
            var bmp = DeviceIndependentBitmap.ToBmp(data);
            if (bmp is not null)
            {
                _host.Event("screen.clip", new JsonObject { ["kind"] = "image", ["format"] = "bmp" }, bmp);
            }
        }
    }

    // The remote desktop reads a file this side pasted (Explorer copying it in).
    private void RemoteReadsFile(FileContentsRequest request)
    {
        var clipboard = _clipboard;
        if (clipboard is null)
        {
            return;
        }

        LocalFile? file = null;
        lock (_clip)
        {
            if (_localKind == 3 && request.Index >= 0 && request.Index < _localFiles.Count)
            {
                file = _localFiles[request.Index];
            }
        }

        if (file is null)
        {
            clipboard.SendFileContents(request.StreamId, null);
            return;
        }

        if (request.IsSizeRequest)
        {
            clipboard.SendFileSize(request.StreamId, file.Size);
            return;
        }

        try
        {
            var count = (int)Math.Clamp(request.Count, 0, 4 * 1024 * 1024);
            var buffer = new byte[count];
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(request.Offset, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
            clipboard.SendFileContents(request.StreamId, read == count ? buffer : buffer.AsSpan(0, read).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            clipboard.SendFileContents(request.StreamId, null);
        }
    }

    // What the viewer pasted, to the remote clipboard (and Ctrl+V there once the remote side has it).
    public void Clip(string op, JsonObject data, ReadOnlyMemory<byte> payload)
    {
        if (_clipboard is null)
        {
            _host.Status("clipfailed");
            return;
        }

        if (op == "screen.clip.set")
        {
            lock (_clip)
            {
                _localText = Encoding.UTF8.GetString(payload.Span);
                DropLocalFiles();
                _localKind = 1;
                _pastePending = ScreenJson.Flag(data, "paste");
            }

            Announce();
            return;
        }

        var id = ScreenJson.Text(data, "id");
        if (op == "screen.clip.begin")
        {
            BeginUpload(id, data);
            return;
        }

        Upload? upload;
        lock (_clip)
        {
            _uploads.TryGetValue(id, out upload);
        }

        if (upload is null)
        {
            return;
        }

        if (op == "screen.clip.chunk")
        {
            var index = ScreenJson.Int(data, "index", -1);
            var offset = ScreenJson.Long(data, "offset");
            if (index >= 0 && index < upload.Files.Count && offset >= 0)
            {
                try
                {
                    using var stream = new FileStream(upload.Files[index].Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    stream.Seek(offset, SeekOrigin.Begin);
                    stream.Write(payload.Span);
                }
                catch (IOException)
                {
                    _host.Status("clipfailed");
                }
            }

            return;
        }

        if (op == "screen.clip.end")
        {
            EndUpload(id, upload);
        }
    }

    private void BeginUpload(string id, JsonObject data)
    {
        if (data["files"] is not JsonArray files || files.Count is 0 or > MaxUploadFiles || id.Length is 0 or > 64)
        {
            _host.Status("clipfailed");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "matgate-clip-" + Guid.NewGuid().ToString("N"));
        var list = new List<LocalFile>();
        try
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < files.Count; i++)
            {
                // Only the last part of the name counts: a path from the viewer's machine says nothing here.
                var name = ScreenJson.Text(files[i] as JsonObject, "name", "Datei");
                var cut = name.LastIndexOfAny(['/', '\\']);
                name = cut >= 0 ? name[(cut + 1)..] : name;
                var file = new LocalFile(name.Length > 0 ? name : "Datei", Path.Combine(folder, i.ToString()));
                File.WriteAllBytes(file.Path, []);
                list.Add(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _host.Status("clipfailed");
            return;
        }

        var upload = new Upload
        {
            Folder = folder,
            Files = list,
            Image = ScreenJson.Text(data, "kind", "files") == "image",
            Paste = ScreenJson.Flag(data, "paste"),
        };
        lock (_clip)
        {
            if (_uploads.Count >= MaxUploads)
            {
                Remove(folder);
                _host.Status("clipfailed");
                return;
            }

            _uploads[id] = upload;
        }
    }

    private void EndUpload(string id, Upload upload)
    {
        lock (_clip)
        {
            _uploads.Remove(id);
            DropLocalFiles();
            if (upload.Image)
            {
                // A picture arrives as a Windows bitmap already (the page converts it).
                try
                {
                    _localImage = File.ReadAllBytes(upload.Files[0].Path);
                }
                catch (IOException)
                {
                    _localImage = [];
                }

                Remove(upload.Folder);
                _localKind = _localImage.Length > 40 ? 2 : 0;
            }
            else
            {
                foreach (var file in upload.Files)
                {
                    file.Size = new FileInfo(file.Path).Length;
                }

                _localFiles = upload.Files;
                _localFolder = upload.Folder;
                _localKind = 3;
            }

            _pastePending = upload.Paste;
        }

        Announce();
    }

    private string? _localFolder;

    private void DropLocalFiles()
    {
        if (_localFolder is { } folder)
        {
            Remove(folder);
        }

        _localFolder = null;
        _localFiles = [];
    }

    private static void Remove(string folder)
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A piece of a file copied on the remote desktop, for a download in the browser.
    public async Task<ClipPiece> ClipReadAsync(string token, int index, long offset, CancellationToken cancellationToken)
    {
        const string Gone = "This file is no longer on the remote clipboard.";
        var clipboard = _clipboard;
        RemoteFile file;
        lock (_clip)
        {
            if (clipboard is null || token.Length == 0 || token != _remoteToken || index < 0 || index >= _remoteFiles.Count
                || _remoteFiles[index].Folder || offset < 0)
            {
                throw new ScreenException(Gone);
            }

            file = _remoteFiles[index];
        }

        var slash = file.Name.LastIndexOf('/');
        var name = slash >= 0 ? file.Name[(slash + 1)..] : file.Name;
        if (offset >= file.Size)
        {
            return new ClipPiece(file.Size, name, ReadOnlyMemory<byte>.Empty);
        }

        if (_downloads.Count >= 32)
        {
            throw new ScreenException("Too many downloads at once.");
        }

        var stream = (uint)Interlocked.Increment(ref _streams);
        var waiting = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloads[stream] = waiting;
        try
        {
            clipboard.RequestFileRange(stream, index, offset, (int)Math.Min(file.Size - offset, ReadChunk));
            var data = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return data is null ? throw new ScreenException(Gone) : new ClipPiece(file.Size, name, data);
        }
        catch (TimeoutException)
        {
            throw new ScreenException(Gone);
        }
        finally
        {
            _downloads.TryRemove(stream, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.Disconnect();
            await client.DisposeAsync();
        }

        foreach (var waiting in _downloads.Values)
        {
            waiting.TrySetResult(null);
        }

        lock (_clip)
        {
            DropLocalFiles();
            foreach (var upload in _uploads.Values)
            {
                Remove(upload.Folder);
            }

            _uploads.Clear();
        }
    }
}
