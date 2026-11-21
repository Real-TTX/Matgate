using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Matgate.Agent;
using Matgate.Models;
using Matgate.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Matgate.Web;

// SSH in Matgate itself (SSH.NET), shown in the terminal page of the agent's programs (xterm.js): no
// Guacamole, no extra container. A connection without a stored user name or password asks in the terminal,
// as an SSH client would ("login as:", "password:"), and asks again when the password was wrong.
public static partial class EndpointMapping
{
    private static void MapSshEndpoints(WebApplication app)
    {
        app.MapGet("/ssh/{id:guid}/terminal", SshTerminalPageAsync).RequireAuthorization();
        app.MapGet("/api/ssh/{id:guid}/terminal-socket", SshTerminalSocketAsync).RequireAuthorization();
        app.MapPost("/api/ssh/{id:guid}/upload", SshUploadAsync).RequireAuthorization();
    }

    // The terminals that are connected right now, by the page's session id: a file pasted into one goes to
    // the server with the login that terminal was opened with (which may have been typed in, not stored).
    private static readonly ConcurrentDictionary<string, SshLive> SshSessions = new();

    private sealed record SshLive(Guid UserId, Guid ServerId, string Host, int Port, string User, string Password, string PrivateKey);

    // A file pasted or dropped into the terminal (or picked with the toolbar's button): written through SFTP
    // into the folder the login starts in - its home - under its own name, never over a file that is there.
    private static async Task<IResult> SshUploadAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var (server, result, user) = await RequireSshAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var german = HtmlViews.Language(context) == "de";
        if (!SshSessions.TryGetValue(context.Request.Query["session"].ToString(), out var live) || live.UserId != user!.Id || live.ServerId != server!.Id)
        {
            return Results.NotFound(new { error = german ? "Dieses Terminal ist nicht verbunden." : "This terminal is not connected." });
        }

        var name = Path.GetFileName(context.Request.Query["name"].ToString().Replace((char)92, '/'));
        name = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            name = "Datei";
        }

        if (name.Length > 200)
        {
            name = name[..200];
        }

        try
        {
            using var sftp = new SftpClient(SshLogin.For(live.Host, live.Port, live.User, live.Password, live.PrivateKey));
            sftp.HostKeyReceived += (_, e) => e.CanTrust = true;
            await sftp.ConnectAsync(context.RequestAborted);
            var folder = sftp.WorkingDirectory.TrimEnd('/');
            var target = name;
            for (var n = 2; sftp.Exists(folder + "/" + target); n++)
            {
                target = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
            }

            // SSH.NET reads the stream on this thread, as it comes in from the browser.
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body)
            {
                body.AllowSynchronousIO = true;
            }

            await Task.Run(() => sftp.UploadFile(context.Request.Body, folder + "/" + target), context.RequestAborted);
            return Results.Json(new { name = target, folder = folder.Length == 0 ? "/" : folder });
        }
        catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            return Results.BadRequest(new
            {
                error = (german ? "Die Datei konnte auf dem Server nicht geschrieben werden" : "The file could not be written on the server")
                    + (exception is SftpPermissionDeniedException ? (german ? " (keine Berechtigung)." : " (permission denied).") : "."),
            });
        }
    }

    private static async Task<(ServerEndpoint? Server, IResult? Result, MatgateUser? User)> RequireSshAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return (null, Results.Unauthorized(), null);
        }

        var server = await ResolveServerForUserAsync(id, user, context, store);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server) || server.Protocol != ServerProtocol.Ssh)
        {
            return (null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }), user);
        }

        return (server, null, user);
    }

    private static async Task<IResult> SshTerminalPageAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var (server, result, user) = await RequireSshAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        return Results.Content(TerminalPageHtml(context, user!, server!.Name, server.Id, "ssh", "/api/ssh/" + server.Id, server.TerminalFontSize), "text/html; charset=utf-8");
    }

    private static async Task SshTerminalSocketAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (server, result, user) = await RequireSshAsync(id, context, store);
        if (result is not null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var cols = uint.TryParse(context.Request.Query["cols"], out var c) ? Math.Clamp(c, 10u, 1000u) : 120u;
        var rows = uint.TryParse(context.Request.Query["rows"], out var r) ? Math.Clamp(r, 5u, 500u) : 32u;
        var session = context.Request.Query["session"].ToString();
        if (!SessionIdPattern.IsMatch(session))
        {
            session = Guid.NewGuid().ToString("N");
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // How long this user's sessions keep running without a browser (Account -> Session); zero: not at all.
        var keep = SessionPreferences.KeepSessionTime(user!.Session);
        var terminal = new SshTerminal(socket, cols, rows, context.RequestAborted);

        // A shell of this id that is still running - the page was reloaded, the network came back, another
        // device opened the same tab: the page gets that shell, and what it printed last.
        if (SshShells.TryGetValue(session, out var running))
        {
            if (running.UserId != user.Id || running.ServerId != id)
            {
                await terminal.RefuseAsync("This session belongs to another connection.");
                return;
            }

            if (!running.Ended.IsCompleted)
            {
                await terminal.ServeAsync(running, attached: true, keep, () => EndSshShell(session, running));
                return;
            }

            EndSshShell(session, running);
        }

        var shell = await terminal.OpenAsync(server!, user.Id);
        if (shell is null)
        {
            return;
        }

        SshSessions[session] = shell.Login;
        SshShells[session] = shell;
        await RecordRecentConnectionAsync(store, user.Id, id, CancellationToken.None);
        if (SshShells.Values.Count(other => other.UserId == user.Id) > Matgate.Services.DisplayBridge.MaxHeldPerUser)
        {
            // Not without end: someone who never comes back to dozens of them must not keep them all.
            keep = TimeSpan.Zero;
        }

        await terminal.ServeAsync(shell, attached: false, keep, () => EndSshShell(session, shell));
    }

    // The shells a page can come back to, by session id.
    private static readonly ConcurrentDictionary<string, SshShell> SshShells = new();

    private static void EndSshShell(string session, SshShell shell)
    {
        SshShells.TryRemove(new KeyValuePair<string, SshShell>(session, shell));
        SshSessions.TryRemove(new KeyValuePair<string, SshLive>(session, shell.Login));
        shell.End();
    }

    // An SSH shell and the page that shows it right now. The shell runs on its own: what it prints is kept
    // (the last 256 KB) and handed to whichever page is attached, so a page that comes later starts with what
    // was on the screen. One page at a time - the next one takes it.
    private sealed class SshShell
    {
        private const int BacklogBytes = 256 * 1024;
        private readonly SshClient _client;
        private readonly ShellStream _stream;
        private readonly object _gate = new();
        private readonly byte[] _backlog = new byte[BacklogBytes];
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _replaced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _expiry;
        private Func<ReadOnlyMemory<byte>, Task>? _viewer;
        private int _backlogEnd;
        private int _backlogLength;
        private int _ending;

        public SshShell(SshClient client, ShellStream stream, Guid userId, Guid serverId, SshLive login)
        {
            _client = client;
            _stream = stream;
            UserId = userId;
            ServerId = serverId;
            Login = login;
            _ = Task.Run(PumpAsync);
        }

        public Guid UserId { get; }

        public Guid ServerId { get; }

        public SshLive Login { get; }

        // Completes when the shell is over: "exit" on the server, a lost connection, or End().
        public Task Ended => _ended.Task;

        private async Task PumpAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var read = await _stream.ReadAsync(buffer, _stop.Token);
                    if (read <= 0)
                    {
                        break;
                    }

                    Func<ReadOnlyMemory<byte>, Task>? viewer;
                    lock (_gate)
                    {
                        Remember(buffer.AsSpan(0, read));
                        viewer = _viewer;
                    }

                    if (viewer is not null)
                    {
                        try
                        {
                            await viewer(buffer.AsMemory(0, read).ToArray());
                        }
                        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                        {
                            // The page is gone; its handler notices by itself.
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is SshException or OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
            }
            finally
            {
                _ended.TrySetResult();
            }
        }

        private void Remember(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= BacklogBytes)
            {
                bytes = bytes[^BacklogBytes..];
            }

            var first = Math.Min(bytes.Length, BacklogBytes - _backlogEnd);
            bytes[..first].CopyTo(_backlog.AsSpan(_backlogEnd));
            bytes[first..].CopyTo(_backlog);
            _backlogEnd = (_backlogEnd + bytes.Length) % BacklogBytes;
            _backlogLength = Math.Min(BacklogBytes, _backlogLength + bytes.Length);
        }

        // A page takes the shell: whoever had it is told (what comes back first completes when the NEXT page
        // takes it), and a shell that was waiting for someone no longer runs out. Second: what the shell
        // printed last. The page is sent nothing new until it says it is ready for it.
        public (Task Replaced, byte[] Backlog) Take(Func<ReadOnlyMemory<byte>, Task> viewer)
        {
            lock (_gate)
            {
                _expiry?.Cancel();
                _expiry = null;
                var before = _replaced;
                _replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                before.TrySetResult();
                _viewer = viewer;
                var backlog = new byte[_backlogLength];
                var start = (_backlogEnd - _backlogLength + BacklogBytes) % BacklogBytes;
                var first = Math.Min(_backlogLength, BacklogBytes - start);
                _backlog.AsSpan(start, first).CopyTo(backlog);
                _backlog.AsSpan(0, _backlogLength - first).CopyTo(backlog.AsSpan(first));
                return (_replaced.Task, backlog);
            }
        }

        // The page left. True when the shell stays: because another page has it already, or because it is kept
        // for the time given - then `expired` is called when nobody came, or when the shell ended meanwhile.
        public bool Leave(Task mine, TimeSpan keep, Action expired)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(mine, _replaced.Task))
                {
                    return true;
                }

                _viewer = null;
                if (keep <= TimeSpan.Zero || _ended.Task.IsCompleted)
                {
                    return false;
                }

                var expiry = new CancellationTokenSource();
                _expiry = expiry;
                _ = Task.WhenAny(Task.Delay(keep, expiry.Token), _ended.Task).ContinueWith(_ =>
                {
                    if (!expiry.IsCancellationRequested)
                    {
                        expired();
                    }
                }, TaskScheduler.Default);
                return true;
            }
        }

        public async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        public void Resize(uint columns, uint rows)
        {
            try
            {
                _stream.ChangeWindowSize(columns, rows, 0, 0);
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        public void End()
        {
            if (Interlocked.Exchange(ref _ending, 1) != 0)
            {
                return;
            }

            _stop.Cancel();
            try
            {
                _client.Disconnect();
            }
            catch (Exception exception) when (exception is SshException or ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
            }

            _client.Dispose();
            _ended.TrySetResult();
        }
    }

    // One terminal page on its socket: it signs in (asking in the terminal for what is not stored), and then
    // shows a shell - a new one, or one that kept running.
    private sealed class SshTerminal(WebSocket socket, uint columns, uint rows, CancellationToken cancellationToken)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly Queue<byte> _typed = new();
        private uint _columns = columns;
        private uint _rows = rows;
        private SshShell? _shell;

        public async Task RefuseAsync(string message)
        {
            try
            {
                await SendControlAsync(new { type = "error", message });
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
            }
        }

        // Signs in and starts a shell; null when that did not happen (the page has been told why).
        public async Task<SshShell?> OpenAsync(ServerEndpoint server, Guid userId)
        {
            try
            {
                await SendControlAsync(new { type = "ready", attached = false });
                var user = server.UserName;
                var password = server.Password;
                if (string.IsNullOrWhiteSpace(user))
                {
                    user = await PromptAsync("login as: ", echo: true);
                    if (user is null)
                    {
                        return null;
                    }
                }

                SshClient? client = null;
                var key = server.PrivateKey ?? "";
                for (var attempt = 0; attempt < 3 && client is null; attempt++)
                {
                    // With a key the first try goes without asking: the key may need no passphrase.
                    if (string.IsNullOrEmpty(password) && !(attempt == 0 && key.Length > 0))
                    {
                        password = await PromptAsync(key.Length > 0 ? "Passphrase for key (or password): " : $"{user}@{server.Host}'s password: ", echo: false);
                        if (password is null)
                        {
                            return null;
                        }
                    }

                    client = await ConnectAsync(server, user, password ?? "", key);
                    if (client is null)
                    {
                        // Without a password yet, a refused key is no "Access denied": the password comes next.
                        if (!string.IsNullOrEmpty(password) || key.Length == 0)
                        {
                            await WriteAsync("Access denied\r\n");
                        }

                        password = "";
                    }
                }

                if (client is null)
                {
                    await SendControlAsync(new { type = "error", message = "The login was refused." });
                    return null;
                }

                try
                {
                    var stream = client.CreateShellStream("xterm-256color", _columns, _rows, 0, 0, 64 * 1024);
                    return new SshShell(client, stream, userId, server.Id, new SshLive(userId, server.Id, server.Host, server.Port, user, password ?? "", key));
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
            catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await RefuseAsync(exception is System.Net.Sockets.SocketException or SshConnectionException
                        ? $"The SSH server {server.Host}:{server.Port} cannot be reached."
                        : "The SSH connection was lost.");
                }

                return null;
            }
        }

        // Shows the shell until the page leaves, the shell ends, the page asks for the end ("Trennen") or
        // another page takes the shell. A shell whose page just went away stays for the next one as long as
        // the user's setting says; otherwise `end` is called.
        public async Task ServeAsync(SshShell shell, bool attached, TimeSpan keep, Action end)
        {
            _shell = shell;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (replaced, backlog) = shell.Take(async bytes =>
            {
                await ready.Task;
                await SendBytesAsync(bytes);
            });
            var asked = false;
            try
            {
                if (attached)
                {
                    await SendControlAsync(new { type = "ready", attached = true });
                    shell.Resize(_columns, _rows);
                    if (backlog.Length > 0)
                    {
                        await SendBytesAsync(backlog);
                    }
                }

                ready.TrySetResult();
                var input = PumpInputAsync(shell);
                var first = await Task.WhenAny(input, shell.Ended, replaced);
                if (first == replaced)
                {
                    await RefuseAsync("Another window or device shows this terminal now.");
                    return;
                }

                asked = first == input && await input;
                if ((asked || first == shell.Ended) && socket.State == WebSocketState.Open)
                {
                    // Ended on the server (exit) or here ("Trennen"): either way the page shows it as ended.
                    await SendControlAsync(new { type = "exit", code = 0 });
                }
            }
            catch (Exception exception) when (exception is SshException or WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                ready.TrySetResult();
            }

            if (!asked && shell.Leave(replaced, keep, end))
            {
                return;
            }

            end();
        }

        private async Task<SshClient?> ConnectAsync(ServerEndpoint server, string user, string password, string key)
        {
            Renci.SshNet.ConnectionInfo login;
            try
            {
                login = SshLogin.For(server.Host, server.Port, user, password, key);
            }
            catch (Exception exception) when (exception is SshException or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or InvalidOperationException)
            {
                // A key that is encrypted and has no (or the wrong) passphrase yet - or is no key at all.
                return null;
            }

            var client = new SshClient(login);
            // As Guacamole did: the host key is not checked against a list (there is none to keep it in yet).
            client.HostKeyReceived += (_, e) => e.CanTrust = true;
            try
            {
                await client.ConnectAsync(cancellationToken);
                return client;
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();
                return null;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // The page's keystrokes to the shell. True when the page asked for the end.
        private async Task<bool> PumpInputAsync(SshShell shell)
        {
            while (await ReceiveAsync() is { } message)
            {
                if (message.Bytes is { } bytes)
                {
                    await shell.WriteAsync(bytes, cancellationToken);
                }
                else if (message.Kill)
                {
                    return true;
                }
            }

            return false;
        }

        // Asks in the terminal itself; null when the page went away or the person pressed Ctrl+C.
        private async Task<string?> PromptAsync(string question, bool echo)
        {
            await WriteAsync(question);
            var line = new StringBuilder();
            var pending = new List<byte>();
            while (true)
            {
                while (_typed.Count == 0)
                {
                    var message = await ReceiveAsync();
                    if (message is null || message.Value.Kill)
                    {
                        return null;
                    }

                    foreach (var b in message.Value.Bytes ?? [])
                    {
                        _typed.Enqueue(b);
                    }
                }

                var next = _typed.Dequeue();
                switch (next)
                {
                    case 0x0D or 0x0A:
                        await WriteAsync("\r\n");
                        return line.ToString();
                    case 0x03:
                        await WriteAsync("^C\r\n");
                        return null;
                    case 0x7F or 0x08:
                        if (line.Length > 0)
                        {
                            line.Length--;
                            if (echo)
                            {
                                await WriteAsync("\b \b");
                            }
                        }

                        break;
                    case < 0x20:
                        break;
                    default:
                        pending.Add(next);
                        // UTF-8: a character may come in several bytes.
                        var text = Encoding.UTF8.GetString(pending.ToArray());
                        if (!text.Contains('�'))
                        {
                            line.Append(text);
                            pending.Clear();
                            if (echo)
                            {
                                await WriteAsync(text);
                            }
                        }

                        break;
                }
            }
        }

        private readonly record struct Message(byte[]? Bytes, bool Kill);

        // The next keystrokes or control message from the page; null when it went away. A size change is
        // applied right here.
        private async Task<Message?> ReceiveAsync()
        {
            var buffer = new byte[16 * 1024];
            using var collected = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                collected.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    return new Message(collected.ToArray(), false);
                }

                try
                {
                    using var control = JsonDocument.Parse(collected.ToArray());
                    var type = control.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type == "resize")
                    {
                        _columns = (uint)Math.Clamp(control.RootElement.GetProperty("cols").GetInt32(), 10, 1000);
                        _rows = (uint)Math.Clamp(control.RootElement.GetProperty("rows").GetInt32(), 5, 500);
                        _shell?.Resize(_columns, _rows);
                    }
                    else if (type == "kill")
                    {
                        return new Message(null, true);
                    }
                }
                catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                }

                collected.SetLength(0);
            }

            return null;
        }

        private async Task WriteAsync(string text)
        {
            await _send.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }

        private async Task SendBytesAsync(ReadOnlyMemory<byte> bytes)
        {
            await _send.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }

        private async Task SendControlAsync(object value)
        {
            await _send.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }
    }
}
