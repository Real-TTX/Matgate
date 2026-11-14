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

    private sealed record SshLive(Guid UserId, Guid ServerId, string Host, int Port, string User, string Password);

    private static Renci.SshNet.ConnectionInfo SshConnectionInfo(string host, int port, string user, string password)
    {
        var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };
        return new Renci.SshNet.ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

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
            using var sftp = new SftpClient(SshConnectionInfo(live.Host, live.Port, live.User, live.Password));
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
        var (server, result, _) = await RequireSshAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        return Results.Content(TerminalPageHtml(context, server!.Name, server.Id, "ssh", "/api/ssh/" + server.Id, server.TerminalFontSize), "text/html; charset=utf-8");
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
        var terminal = new SshTerminal(socket, cols, rows, context.RequestAborted,
            (login, password) => SshSessions[session] = new SshLive(user!.Id, server!.Id, server.Host, server.Port, login, password));
        try
        {
            await terminal.RunAsync(server!);
        }
        finally
        {
            SshSessions.TryRemove(session, out _);
        }

        await RecordRecentConnectionAsync(store, user!.Id, id, CancellationToken.None);
    }

    // One SSH shell for one terminal page.
    private sealed class SshTerminal(WebSocket socket, uint columns, uint rows, CancellationToken cancellationToken, Action<string, string> connected)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly Queue<byte> _typed = new();
        private uint _columns = columns;
        private uint _rows = rows;
        private ShellStream? _shell;

        public async Task RunAsync(ServerEndpoint server)
        {
            await SendControlAsync(new { type = "ready", attached = false });
            var user = server.UserName;
            var password = server.Password;
            try
            {
                if (string.IsNullOrWhiteSpace(user))
                {
                    user = await PromptAsync("login as: ", echo: true);
                    if (user is null)
                    {
                        return;
                    }
                }

                SshClient? client = null;
                for (var attempt = 0; attempt < 3 && client is null; attempt++)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        password = await PromptAsync($"{user}@{server.Host}'s password: ", echo: false);
                        if (password is null)
                        {
                            return;
                        }
                    }

                    client = await ConnectAsync(server, user, password);
                    if (client is null)
                    {
                        await WriteAsync("Access denied\r\n");
                        password = "";
                    }
                }

                if (client is null)
                {
                    await SendControlAsync(new { type = "error", message = "The login was refused." });
                    return;
                }

                using (client)
                {
                    connected(user, password!);
                    _shell = client.CreateShellStream("xterm-256color", _columns, _rows, 0, 0, 64 * 1024);
                    var output = Task.Run(PumpOutputAsync);
                    var input = Task.Run(PumpInputAsync);
                    await Task.WhenAny(output, input);
                    if (socket.State == WebSocketState.Open)
                    {
                        // Ended on the server (exit) or here ("Trennen"): either way the page shows it as ended.
                        await SendControlAsync(new { type = "exit", code = 0 });
                    }

                    client.Disconnect();
                }
            }
            catch (Exception exception) when (exception is SshException or System.Net.Sockets.SocketException or WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                if (socket.State == WebSocketState.Open)
                {
                    await SendControlAsync(new { type = "error", message = exception is System.Net.Sockets.SocketException or SshConnectionException
                        ? $"The SSH server {server.Host}:{server.Port} cannot be reached."
                        : "The SSH connection was lost." });
                }
            }
        }

        private async Task<SshClient?> ConnectAsync(ServerEndpoint server, string user, string password)
        {
            var client = new SshClient(SshConnectionInfo(server.Host, server.Port, user, password));
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

        private async Task PumpOutputAsync()
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await _shell!.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    return;
                }

                await _send.WaitAsync(cancellationToken);
                try
                {
                    await socket.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, true, cancellationToken);
                }
                finally
                {
                    _send.Release();
                }
            }
        }

        private async Task PumpInputAsync()
        {
            while (await ReceiveAsync() is { } message)
            {
                if (message.Bytes is { } bytes)
                {
                    await _shell!.WriteAsync(bytes, cancellationToken);
                    await _shell.FlushAsync(cancellationToken);
                }
                else if (message.Kill)
                {
                    return;
                }
            }
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
                        _shell?.ChangeWindowSize(_columns, _rows, 0, 0);
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
