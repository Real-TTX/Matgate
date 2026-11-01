using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Matgate.Agent;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Matgate agents: the status of one (what it offers), its terminals in the browser, and the xterm.js
// files the terminal page needs. Files go through the normal file manager (IFileGatewayService).
public static partial class EndpointMapping
{
    private static readonly Regex SessionIdPattern = new("^[A-Za-z0-9-]{8,64}$", RegexOptions.Compiled);

    private static void MapAgentEndpoints(WebApplication app)
    {
        app.MapGet("/api/agent/{id:guid}/status", AgentStatusAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/terminal-socket", AgentTerminalSocketAsync).RequireAuthorization();
        app.MapPost("/api/agent/{id:guid}/terminal/kill", AgentTerminalKillAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/sessions", AgentSessionsAsync).RequireAuthorization();
        app.MapGet("/agent/{id:guid}/terminal", AgentTerminalPageAsync).RequireAuthorization();
        app.MapGet("/assets/xterm/{file}", XtermAsset);
    }

    // The agent connection this user may open, or the reason why not.
    private static async Task<(ServerEndpoint? Server, IResult? Result, MatgateUser? User)> RequireAgentAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return (null, Results.Unauthorized(), null);
        }

        var server = await ResolveServerForUserAsync(id, user, context, store);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server) || server.Protocol != ServerProtocol.Agent)
        {
            return (null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }), user);
        }

        return (server, null, user);
    }

    // A terminal on the machine can do everything the machine's user can - more than any file rule allows.
    // Whoever is limited to a folder or to reading on this connection gets no terminal on it.
    private static bool CanUseAgentTerminal(MatgateUser user, ServerEndpoint server)
    {
        return user.IsAdmin
            || server.OwnerUserId == user.Id
            || user.FileAccessRules?.Any(rule => rule.ServerId == server.Id) != true;
    }

    private static async Task<IResult> AgentStatusAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            var hello = link.Hello;
            var terminalsAllowed = CanUseAgentTerminal(user!, server!);
            return Results.Json(new
            {
                online = true,
                name = hello.Name,
                machine = hello.Machine,
                user = hello.User,
                os = hello.Os,
                version = hello.Version,
                plugins = hello.Plugins
                    .Where(plugin => plugin.Kind != "terminal" || terminalsAllowed)
                    .Select(plugin => new { id = plugin.Id, kind = plugin.Kind, title = plugin.Title }),
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return Results.Json(new { online = false, error = exception.Message, plugins = Array.Empty<object>() });
        }
    }

    private static async Task AgentTerminalSocketAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // A WebSocket carries the cookie but no CSRF header: another site must not be able to open a
        // terminal in the user's name, so the request has to come from a page of this gateway.
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null || !CanUseAgentTerminal(user!, server!))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var query = context.Request.Query;
        var plugin = query["plugin"].ToString();
        var session = query["session"].ToString();
        if (!SessionIdPattern.IsMatch(session))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var browser = new BrowserTerminal(socket);
        AgentLink link;
        try
        {
            link = await agents.GetAsync(server!, context.RequestAborted);
            if (!link.Hello.Plugins.Any(candidate => candidate.Kind == "terminal" && candidate.Id == plugin))
            {
                throw new InvalidOperationException("The agent does not offer this program.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            await browser.SendTextAsync(new { type = "error", message = exception.Message });
            await browser.CloseAsync();
            return;
        }

        link.Listen(session, browser);
        try
        {
            var opened = await link.RequestAsync("pty.open", new JsonObject
            {
                ["plugin"] = plugin,
                ["session"] = session,
                ["cols"] = int.TryParse(query["cols"], out var cols) ? cols : 120,
                ["rows"] = int.TryParse(query["rows"], out var rows) ? rows : 32,
                ["cwd"] = query["cwd"].ToString(),
            }, cancellationToken: context.RequestAborted);
            await browser.SendTextAsync(new { type = "ready", session, attached = opened.Bool("attached") });

            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, received.Count);
                if (message.Length > 1024 * 1024)
                {
                    break;
                }

                if (!received.EndOfMessage)
                {
                    continue;
                }

                var bytes = message.ToArray();
                message.SetLength(0);
                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    await link.SendEventAsync("pty.input", new JsonObject { ["session"] = session }, bytes);
                    continue;
                }

                using var control = JsonDocument.Parse(bytes);
                var type = control.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "";
                if (type == "resize")
                {
                    await link.SendEventAsync("pty.resize", new JsonObject
                    {
                        ["session"] = session,
                        ["cols"] = control.RootElement.GetProperty("cols").GetInt32(),
                        ["rows"] = control.RootElement.GetProperty("rows").GetInt32(),
                    });
                }
                else if (type == "kill")
                {
                    await link.RequestAsync("pty.kill", new JsonObject { ["session"] = session });
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            if (exception is InvalidOperationException)
            {
                await browser.SendTextAsync(new { type = "error", message = exception.Message });
            }
        }
        finally
        {
            link.StopListening(session, browser);
            // The program keeps running - a phone that lost the connection comes back to it.
            if (link.IsOpen)
            {
                try
                {
                    await link.SendEventAsync("pty.detach", new JsonObject { ["session"] = session });
                }
                catch (InvalidOperationException)
                {
                }
            }

            await browser.CloseAsync();
        }
    }

    // The programs running on the machine - so the overview can offer to go back to one.
    private static async Task<IResult> AgentSessionsAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        if (!CanUseAgentTerminal(user!, server!))
        {
            return Results.Json(new { sessions = Array.Empty<object>() });
        }

        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            var titles = link.Hello.Plugins.ToDictionary(plugin => plugin.Id, plugin => plugin.Title);
            var response = await link.RequestAsync("pty.list", cancellationToken: context.RequestAborted);
            var sessions = (response.Data?["sessions"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(item => new
                {
                    session = item["session"]?.GetValue<string>() ?? "",
                    plugin = item["plugin"]?.GetValue<string>() ?? "",
                    title = titles.GetValueOrDefault(item["plugin"]?.GetValue<string>() ?? "", item["plugin"]?.GetValue<string>() ?? ""),
                    started = item["started"]?.GetValue<long>() ?? 0,
                    attached = item["attached"]?.GetValue<bool>() ?? false,
                })
                .OrderByDescending(item => item.started)
                .ToList();
            return Results.Json(new { sessions });
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return Results.Json(new { sessions = Array.Empty<object>(), error = exception.Message });
        }
    }

    // Closing the tab ends the program. The page itself cannot say so - it is gone before it could.
    private static async Task<IResult> AgentTerminalKillAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        if (!ValidateCsrfHeader(context))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        var session = context.Request.Query["session"].ToString();
        if (!CanUseAgentTerminal(user!, server!) || !SessionIdPattern.IsMatch(session))
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") });
        }

        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            await link.RequestAsync("pty.kill", new JsonObject { ["session"] = session }, cancellationToken: context.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            // Nothing to end on an agent that is not there.
        }

        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> AgentTerminalPageAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        if (!CanUseAgentTerminal(user!, server!))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var de = HtmlViews.Language(context) == "de";
        var html = AgentTerminalPage.Html
            .Replace("__LANG__", de ? "de" : "en")
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(server!.Name))
            .Replace("__SERVER__", server.Id.ToString())
            .Replace("__CSRF__", System.Net.WebUtility.HtmlEncode(context.User.FindFirst("csrf")?.Value ?? ""))
            .Replace("__TEXT__", System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(new
            {
                connecting = de ? "Verbinde ..." : "Connecting ...",
                connected = de ? "Verbunden" : "Connected",
                reattached = de ? "Wieder verbunden - die Sitzung lief weiter" : "Reconnected - the session kept running",
                lost = de ? "Verbindung verloren - verbinde neu ..." : "Connection lost - reconnecting ...",
                ended = de ? "Programm beendet" : "Program ended",
                restart = de ? "Neu starten" : "Start again",
                kill = de ? "Beenden" : "End",
                killConfirm = de ? "Programm wirklich beenden?" : "Really end the program?",
            })));
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static IResult XtermAsset(string file)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("xterm/" + file);
        if (stream is null)
        {
            return Results.NotFound();
        }

        var type = file.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8"
            : file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8"
            : "text/plain; charset=utf-8";
        return Results.Stream(stream, type);
    }

    // The browser end of a terminal: output as binary messages, control as small JSON texts.
    private sealed class BrowserTerminal(WebSocket socket) : IAgentTerminalListener
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Task OnDataAsync(ReadOnlyMemory<byte> data) => SendAsync(data, WebSocketMessageType.Binary);

        public Task OnExitAsync(int? code) => code is null
            ? SendTextAsync(new { type = "lost" })
            : SendTextAsync(new { type = "exit", code });

        public Task SendTextAsync(object value) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text);

        public async Task CloseAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
            }
        }

        private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(data, type, endOfMessage: true, CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
