using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matgate.Agent;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// RDP and VNC through Matgate's own display engine: the browser's screen page (the one of the agent) on one
// side, the engine on the other, Matgate in between - it adds the connection's host and credentials, the
// session's drive, and lets only the kinds of message through a viewer may send.
public static partial class EndpointMapping
{
    private static void MapDisplayEndpoints(WebApplication app)
    {
        app.MapGet("/display/{id:guid}/screen", DisplayScreenPageAsync).RequireAuthorization();
        app.MapGet("/api/display/{id:guid}/screen-socket", DisplayScreenSocketAsync).RequireAuthorization();
        app.MapGet("/api/display/{id:guid}/screen-clip", DisplayScreenClipAsync).RequireAuthorization();
    }

    private static async Task<(ServerEndpoint? Server, IResult? Result, MatgateUser? User)> RequireDisplayAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return (null, Results.Unauthorized(), null);
        }

        var server = await ResolveServerForUserAsync(id, user, context, store);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server) || server.Protocol is not (ServerProtocol.Rdp or ServerProtocol.Vnc))
        {
            return (null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }), user);
        }

        return (server, null, user);
    }

    private static async Task<IResult> DisplayScreenPageAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var (server, result, _) = await RequireDisplayAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        return Results.Content(ScreenPageHtml(context, server!.Name, server.Id, "display", "/api/display/" + server.Id), "text/html; charset=utf-8");
    }

    private static async Task DisplayScreenSocketAsync(Guid id, HttpContext context, JsonDataStore store, DisplayBridge bridge, FileShareService fileShares)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Only a page of this gateway may open it (the cookie alone is not enough).
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var (server, result, user) = await RequireDisplayAsync(id, context, store);
        var query = context.Request.Query;
        var session = query["session"].ToString();
        if (result is not null || !SessionIdPattern.IsMatch(session))
        {
            context.Response.StatusCode = result is null ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var browser = new BrowserScreen(socket);
        if (!DisplayBridge.Available)
        {
            browser.Post(ScreenError("Matgate's display engine is not set up - this connection opens with Guacamole."));
            return;
        }

        // The session's drive "Files" (RDP), the same folder Guacamole would get: a scratch area plus the
        // file areas this user may use.
        var sessionKey = $"{server!.Id:N}-{session}";
        var ephemeral = await store.FindServerByIdAsync(id, context.RequestAborted) is null;
        var view = server.Protocol == ServerProtocol.Rdp ? fileShares.CreateSessionView(user!, server, sessionKey, ephemeral) : null;

        DisplayConnection connection;
        try
        {
            connection = await bridge.ConnectAsync(context.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            browser.Post(ScreenError(exception.Message));
            fileShares.Close(sessionKey, user!.Id);
            return;
        }

        await using (connection)
        {
            connection.UserId = user!.Id;
            connection.OnEvent = frame =>
            {
                browser.Post(frame);
                return Task.CompletedTask;
            };
            bridge.Live[session] = connection;
            using var keepalive = new Timer(_ => fileShares.Keepalive(sessionKey, user.Id), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            try
            {
                var open = new JsonObject
                {
                    ["token"] = DisplayBridge.Key(),
                    ["session"] = session,
                    ["protocol"] = server.Protocol == ServerProtocol.Vnc ? "vnc" : "rdp",
                    ["host"] = server.Host,
                    ["port"] = server.Port,
                    ["username"] = server.UserName,
                    ["password"] = server.Password,
                    ["domain"] = server.Domain,
                    ["ignoreCertificate"] = server.IgnoreCertificate,
                    ["keyboardLayout"] = DisplayBridge.KeyboardLayoutId(server.KeyboardLayout),
                    ["quality"] = query["quality"].ToString() is { Length: > 0 and <= 16 } quality ? quality : "auto",
                    ["resolution"] = query["resolution"].ToString() is { Length: > 0 and <= 16 } resolution ? resolution : "auto",
                    ["fitWidth"] = int.TryParse(query["fitWidth"], out var fitWidth) ? Math.Clamp(fitWidth, 0, 16384) : 0,
                    ["fitHeight"] = int.TryParse(query["fitHeight"], out var fitHeight) ? Math.Clamp(fitHeight, 0, 16384) : 0,
                    ["drivePath"] = view?.Path ?? "",
                };

                // The viewer's messages go on while the connection is still being made: the engine may ask
                // for a password first (screen.auth), and the answer comes from the page.
                var forwarding = Task.Run(() => ForwardBrowserAsync(socket, connection, session, context.RequestAborted));
                var opening = connection.RequestAsync("screen.open", open, TimeSpan.FromMinutes(4), context.RequestAborted);
                var first = await Task.WhenAny(opening, forwarding);
                if (first == opening)
                {
                    var opened = await opening;
                    browser.Post(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = opened.Data });
                    await RecordRecentConnectionAsync(store, user.Id, id, CancellationToken.None);
                    await Task.WhenAny(forwarding, connection.Completion);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
            {
                if (exception is InvalidOperationException)
                {
                    browser.Post(ScreenError(exception.Message));
                }
            }
            finally
            {
                bridge.Live.TryRemove(session, out _);
                fileShares.Close(sessionKey, user.Id);
            }
        }
    }

    private static AgentFrame ScreenError(string message) =>
        new() { Kind = "evt", Op = "screen.error", Data = new JsonObject { ["message"] = message } };

    // The browser's messages, as far as a viewer may send them, to the engine.
    private static async Task ForwardBrowserAsync(WebSocket socket, DisplayConnection connection, string session, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var received = await socket.ReceiveAsync(buffer, cancellationToken);
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

            var frame = AgentWire.Decode(message.ToArray());
            message.SetLength(0);
            if (frame.Kind != "evt" || !ScreenOpsFromBrowser.Contains(frame.Op))
            {
                continue;
            }

            var data = frame.Data ?? [];
            data["session"] = session;
            await connection.SendEventAsync(frame.Op, data, frame.Payload);
        }
    }

    // A file copied on the remote desktop, streamed in pieces as a download.
    private static async Task DisplayScreenClipAsync(Guid id, HttpContext context, JsonDataStore store, DisplayBridge bridge)
    {
        var (_, result, user) = await RequireDisplayAsync(id, context, store);
        var query = context.Request.Query;
        var session = query["session"].ToString();
        if (result is not null || !int.TryParse(query["index"], out var index)
            || !bridge.Live.TryGetValue(session, out var connection) || connection.UserId != user!.Id)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var request = new JsonObject { ["session"] = session, ["token"] = query["token"].ToString(), ["index"] = index, ["offset"] = 0L };
        AgentFrame first;
        try
        {
            first = await connection.RequestAsync("screen.clip.read", request, TimeSpan.FromSeconds(60), context.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(exception.Message);
            return;
        }

        var size = first.Long("size");
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = size;
        context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = first.Text("name") ?? "Datei",
        }.ToString();
        await context.Response.Body.WriteAsync(first.Payload, context.RequestAborted);
        var offset = (long)first.Payload.Length;
        while (offset < size && !context.RequestAborted.IsCancellationRequested)
        {
            request = new JsonObject { ["session"] = session, ["token"] = query["token"].ToString(), ["index"] = index, ["offset"] = offset };
            var piece = await connection.RequestAsync("screen.clip.read", request, TimeSpan.FromSeconds(60), context.RequestAborted);
            if (piece.Payload.Length == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(piece.Payload, context.RequestAborted);
            offset += piece.Payload.Length;
        }
    }
}
