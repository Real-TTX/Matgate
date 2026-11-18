using System.Net.WebSockets;
using System.Text.Json;
using Matgate.Agent;
using Matgate.Display;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// RDP and VNC through Matgate's own engine: the browser's screen page (the one of the agent) on one side, a
// screen session inside Matgate on the other. This is where the connection's host and credentials are added
// - the page never sees them - and where only the kinds of message a viewer may send get through.
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
        var (server, result, user) = await RequireDisplayAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        return Results.Content(ScreenPageHtml(context, user!, server!.Name, server.Id, "display", "/api/display/" + server.Id), "text/html; charset=utf-8");
    }

    private static async Task DisplayScreenSocketAsync(Guid id, HttpContext context, JsonDataStore store, DisplayBridge bridge)
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
            browser.Post(ScreenError("Matgate's own engine is not available here - this connection opens with Guacamole."));
            return;
        }

        var quality = query["quality"].ToString() is { Length: > 0 and <= 16 } wantedQuality ? wantedQuality : "auto";
        var resolution = query["resolution"].ToString() is { Length: > 0 and <= 16 } wantedResolution ? wantedResolution : "auto";
        var fitWidth = int.TryParse(query["fitWidth"], out var parsedWidth) ? Math.Clamp(parsedWidth, 0, 16384) : 0;
        var fitHeight = int.TryParse(query["fitHeight"], out var parsedHeight) ? Math.Clamp(parsedHeight, 0, 16384) : 0;
        // How long this user's sessions keep running without a browser (Account -> Session); zero: not at all.
        var keep = SessionPreferences.KeepSessionTime(user!.Session);

        // A session of this id that is still running - the page was reloaded, the network came back, another
        // device opened the same tab: the viewer gets that session instead of a new connection.
        if (bridge.Held.TryGetValue(session, out var running))
        {
            if (running.UserId != user.Id || running.ServerId != id)
            {
                browser.Post(ScreenError("This session belongs to another connection."));
                return;
            }

            if (!running.Screen.Completion.IsCompleted)
            {
                browser.Post(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = running.Screen.Describe() });
                var mine = running.Take(browser.Post);
                running.Screen.Post("screen.config", new System.Text.Json.Nodes.JsonObject
                {
                    ["quality"] = quality,
                    ["resolution"] = resolution,
                    ["fitWidth"] = fitWidth,
                    ["fitHeight"] = fitHeight,
                }, default);
                var listening = Task.Run(() => ForwardBrowserAsync(socket, running.Screen, context.RequestAborted));
                await WatchScreenAsync(bridge, session, running, mine, listening, browser, keep);
                return;
            }

            // It ended while nobody was looking: what is left of it goes, and this is a new connection.
            await EndHeldScreenAsync(bridge, session, running);
        }

        var target = new ScreenTarget
        {
            Session = session,
            Protocol = server!.Protocol == ServerProtocol.Vnc ? "vnc" : "rdp",
            Host = server.Host,
            Port = server.Port,
            UserName = server.UserName ?? "",
            Password = server.Password ?? "",
            Domain = server.Domain ?? "",
            IgnoreCertificate = server.IgnoreCertificate,
            KeyboardLayout = DisplayBridge.KeyboardLayoutId(server.KeyboardLayout),
            Quality = quality,
            Resolution = resolution,
            FitWidth = fitWidth,
            FitHeight = fitHeight,
        };

        var screen = bridge.Open(target);
        screen.UserId = user.Id;
        screen.OnEvent = browser.Post;
        var held = new DisplayBridge.HeldScreen(screen, user.Id, id);
        var handedOver = false;
        try
        {
            // The viewer's messages go on while the connection is still being made: the session may ask for a
            // password first (screen.auth), and the answer comes from the page.
            var forwarding = Task.Run(() => ForwardBrowserAsync(socket, screen, context.RequestAborted));
            var opening = screen.OpenAsync(context.RequestAborted);
            var first = await Task.WhenAny(opening, forwarding);
            if (first != opening)
            {
                // The viewer left while the connection was being made; whatever that still ends in is of no interest.
                _ = opening.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return;
            }

            var opened = await opening;
            browser.Post(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = opened });
            await RecordRecentConnectionAsync(store, user.Id, id, CancellationToken.None);

            // From here on the session is one a viewer can come back to.
            var mine = held.First();
            bridge.Live[session] = screen;
            bridge.Held[session] = held;
            handedOver = true;
            // Not without end: someone who never comes back to dozens of them must not keep them all.
            if (bridge.Held.Values.Count(other => other.UserId == user.Id) > DisplayBridge.MaxHeldPerUser)
            {
                keep = TimeSpan.Zero;
            }

            await WatchScreenAsync(bridge, session, held, mine, forwarding, browser, keep);
        }
        catch (ScreenException exception)
        {
            browser.Post(ScreenError(exception.Message));
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
        }
        finally
        {
            if (!handedOver)
            {
                await screen.DisposeAsync();
            }
        }
    }

    // A viewer on a running screen, until it leaves, the screen ends, or another viewer takes it. A screen
    // whose viewer just went away stays for the next one as long as the user's setting says.
    private static async Task WatchScreenAsync(
        DisplayBridge bridge,
        string session,
        DisplayBridge.HeldScreen held,
        Task replaced,
        Task forwarding,
        BrowserScreen browser,
        TimeSpan keep)
    {
        try
        {
            var first = await Task.WhenAny(forwarding, held.Screen.Completion, replaced);
            if (first == replaced)
            {
                // Another window or device shows this screen now; it is theirs.
                browser.Post(new AgentFrame { Kind = "evt", Op = "screen.ended", Data = new System.Text.Json.Nodes.JsonObject { ["reason"] = "taken" } });
                return;
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
        }

        if (held.Leave(replaced, keep, () => _ = EndHeldScreenAsync(bridge, session, held)))
        {
            return;
        }

        await EndHeldScreenAsync(bridge, session, held);
    }

    private static async Task EndHeldScreenAsync(DisplayBridge bridge, string session, DisplayBridge.HeldScreen held)
    {
        bridge.Held.TryRemove(new KeyValuePair<string, DisplayBridge.HeldScreen>(session, held));
        bridge.Live.TryRemove(new KeyValuePair<string, ScreenSession>(session, held.Screen));
        try
        {
            await held.Screen.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Ending a session must not fail the request that ended it.
        }
    }

    private static AgentFrame ScreenError(string message) =>
        new() { Kind = "evt", Op = "screen.error", Data = new System.Text.Json.Nodes.JsonObject { ["message"] = message } };

    // The browser's messages, as far as a viewer may send them, to the session.
    private static async Task ForwardBrowserAsync(WebSocket socket, ScreenSession screen, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
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
                if (frame.Kind == "evt" && ScreenOpsFromBrowser.Contains(frame.Op))
                {
                    screen.Post(frame.Op, frame.Data ?? [], frame.Payload);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
            // The viewer is gone, or sent something that is no message: either way this screen is over.
        }
    }

    // A file copied on the remote desktop, streamed in pieces as a download.
    private static async Task DisplayScreenClipAsync(Guid id, HttpContext context, JsonDataStore store, DisplayBridge bridge)
    {
        var (_, result, user) = await RequireDisplayAsync(id, context, store);
        var query = context.Request.Query;
        var session = query["session"].ToString();
        var token = query["token"].ToString();
        if (result is not null || !int.TryParse(query["index"], out var index)
            || !bridge.Live.TryGetValue(session, out var screen) || screen.UserId != user!.Id)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ClipPiece first;
        try
        {
            first = await screen.ClipReadAsync(token, index, 0, context.RequestAborted);
        }
        catch (ScreenException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(exception.Message);
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = first.Size;
        context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = first.Name.Length > 0 ? first.Name : "Datei",
        }.ToString();
        await context.Response.Body.WriteAsync(first.Data, context.RequestAborted);
        var offset = (long)first.Data.Length;
        try
        {
            while (offset < first.Size && !context.RequestAborted.IsCancellationRequested)
            {
                var piece = await screen.ClipReadAsync(token, index, offset, context.RequestAborted);
                if (piece.Data.Length == 0)
                {
                    break;
                }

                await context.Response.Body.WriteAsync(piece.Data, context.RequestAborted);
                offset += piece.Data.Length;
            }
        }
        catch (Exception exception) when (exception is ScreenException or OperationCanceledException)
        {
            // The headers are out already: the download simply ends short, which the browser reports.
        }
    }
}
