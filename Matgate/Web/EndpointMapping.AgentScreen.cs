using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Matgate.Agent;
using Matgate.Services;

namespace Matgate.Web;

// The screen of an agent's machine: a WebSocket that passes the agent's frames (video, sound, pointer,
// clipboard) to the browser as they are, and the browser's input back - only the kinds of message a
// screen viewer may send. Files copied on the machine are downloaded through screen-clip.
public static partial class EndpointMapping
{
    private static readonly HashSet<string> ScreenOpsFromBrowser =
    [
        "screen.input", "screen.ack", "screen.key", "screen.config", "screen.audio",
        "screen.clip.set", "screen.clip.begin", "screen.clip.chunk", "screen.clip.end",
    ];

    private static void MapAgentScreenEndpoints(WebApplication app)
    {
        app.MapGet("/api/agent/{id:guid}/screen-socket", AgentScreenSocketAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/screen-clip", AgentScreenClipAsync).RequireAuthorization();
        app.MapGet("/agent/{id:guid}/screen", AgentScreenPageAsync).RequireAuthorization();
    }

    private static async Task AgentScreenSocketAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Same rule as for a terminal: only a page of this gateway may open it (the cookie alone is not enough).
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            || !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The screen can do whatever the machine's user can: the same people as for a terminal.
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        if (result is not null || !CanUseAgentTerminal(user!, server!))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var query = context.Request.Query;
        var session = query["session"].ToString();
        if (!SessionIdPattern.IsMatch(session))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var browser = new BrowserScreen(socket);
        AgentLink link;
        try
        {
            link = await agents.GetAsync(server!, context.RequestAborted);
            if (!link.Hello.Plugins.Any(candidate => candidate.Kind == "screen"))
            {
                throw new InvalidOperationException("This agent does not share its screen.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            browser.Post(new AgentFrame { Kind = "evt", Op = "screen.error", Data = new JsonObject { ["message"] = exception.Message } });
            return;
        }

        link.ListenScreen(session, browser);
        try
        {
            var opened = await link.RequestAsync("screen.open", new JsonObject
            {
                ["session"] = session,
                ["monitor"] = int.TryParse(query["monitor"], out var monitor) ? monitor : 0,
                ["maxWidth"] = int.TryParse(query["maxWidth"], out var maxWidth) ? maxWidth : 2560,
                ["quality"] = query["quality"].ToString() is { Length: > 0 and <= 16 } quality ? quality : "auto",
            }, cancellationToken: context.RequestAborted);
            browser.Post(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = opened.Data });

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

                var frame = AgentWire.Decode(message.ToArray());
                message.SetLength(0);
                if (frame.Kind != "evt" || !ScreenOpsFromBrowser.Contains(frame.Op))
                {
                    continue;
                }

                // Whatever the browser says, it speaks for its own session only.
                var data = frame.Data ?? [];
                data["session"] = session;
                await link.SendEventAsync(frame.Op, data, frame.Payload);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (exception is InvalidOperationException)
            {
                browser.Post(new AgentFrame { Kind = "evt", Op = "screen.error", Data = new JsonObject { ["message"] = exception.Message } });
            }
        }
        finally
        {
            link.StopListeningScreen(session, browser);
            if (link.IsOpen)
            {
                try
                {
                    await link.SendEventAsync("screen.close", new JsonObject { ["session"] = session });
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    // A file copied on the machine, streamed in pieces as a download.
    private static async Task AgentScreenClipAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        var query = context.Request.Query;
        var session = query["session"].ToString();
        if (result is not null || !CanUseAgentTerminal(user!, server!) || !SessionIdPattern.IsMatch(session) || !int.TryParse(query["index"], out var index))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var request = new JsonObject { ["session"] = session, ["token"] = query["token"].ToString(), ["index"] = index, ["offset"] = 0L };
        AgentLink link;
        AgentFrame first;
        try
        {
            link = await agents.GetAsync(server!, context.RequestAborted);
            first = await link.RequestAsync("screen.clip.read", request, cancellationToken: context.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync(exception.Message);
            return;
        }

        var size = first.Long("size");
        var name = first.Text("name") ?? "Datei";
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = size;
        context.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = name,
        }.ToString();
        await context.Response.Body.WriteAsync(first.Payload, context.RequestAborted);
        var offset = (long)first.Payload.Length;
        while (offset < size && !context.RequestAborted.IsCancellationRequested)
        {
            request["offset"] = offset;
            var piece = await link.RequestAsync("screen.clip.read", JsonNode.Parse(request.ToJsonString())!.AsObject(), cancellationToken: context.RequestAborted);
            if (piece.Payload.Length == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(piece.Payload, context.RequestAborted);
            offset += piece.Payload.Length;
        }
    }

    private static async Task<IResult> AgentScreenPageAsync(Guid id, HttpContext context, JsonDataStore store)
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
        var html = AgentScreenPage.Html
            .Replace("__LANG__", de ? "de" : "en")
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(server!.Name))
            .Replace("__SERVER__", server.Id.ToString())
            .Replace("__TEXT__", System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(new
            {
                connecting = de ? "Verbinde ..." : "Connecting ...",
                connected = de ? "Verbunden" : "Connected",
                lost = de ? "Verbindung verloren - verbinde neu ..." : "Connection lost - reconnecting ...",
                ended = de ? "Die Bildschirmübertragung ist beendet." : "Screen sharing has ended.",
                reconnect = de ? "Neu verbinden" : "Connect again",
                taken = de ? "Ein anderes Fenster oder Gerät zeigt diesen Bildschirm jetzt." : "Another window or device shows this screen now.",
                takeBack = de ? "Wieder hierher holen" : "Bring it back here",
                unavailable = de ? "Der Rechner zeigt gerade einen geschützten Bildschirm (gesperrt oder eine Administrator-Abfrage). Sobald er wieder normal läuft, geht es hier weiter." : "The machine shows a protected screen right now (locked or an administrator prompt). This continues as soon as it is back.",
                error = de ? "Der Bildschirm kann nicht übertragen werden." : "The screen cannot be shared.",
                noDecoder = de ? "Dieser Browser kann den Bildschirm nicht anzeigen (kein WebCodecs). Bitte einen aktuellen Edge, Chrome, Firefox oder Safari nehmen." : "This browser cannot show the screen (no WebCodecs). Please use a current Edge, Chrome, Firefox or Safari.",
                overview = de ? "Agent-Übersicht" : "Agent overview",
                monitor = de ? "Bildschirm" : "Monitor",
                quality = de ? "Bildqualität" : "Picture quality",
                qualityHigh = de ? "Hohe Qualität" : "High quality",
                qualityMedium = de ? "Mittel" : "Medium",
                qualityLow = de ? "Wenig Daten" : "Low data",
                soundOn = de ? "Ton einschalten" : "Turn sound on",
                soundOff = de ? "Ton ausschalten" : "Turn sound off",
                soundBlocked = de ? "Ton: einmal ins Bild klicken" : "Sound: click into the picture once",
                noSound = de ? "Der Rechner hat keine Tonausgabe." : "The machine has no sound output.",
                keyboard = de ? "Tastatur" : "Keyboard",
                fullscreen = de ? "Vollbild" : "Full screen",
                pasting = de ? "Füge ein ..." : "Pasting ...",
                clipFailed = de ? "Die Zwischenablage des Rechners hat das nicht angenommen." : "The machine's clipboard did not take this.",
                clipFiles = de ? "Auf dem Rechner kopierte Dateien" : "Files copied on the machine",
                download = de ? "Herunterladen" : "Download",
                folder = de ? "Ordner (nicht ladbar)" : "Folder (cannot be downloaded)",
                close = de ? "Schließen" : "Close",
            })));
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // The browser's side of a screen: frames are queued and sent by one writer, so a slow browser holds up
    // neither the agent connection (which carries terminals and files too) nor the agent itself. Video is
    // paced by the browser's acknowledgements; if the queue still grows, sound and pointer moves give way.
    private sealed class BrowserScreen : IAgentScreenListener, IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly Channel<AgentFrame> _queue = Channel.CreateUnbounded<AgentFrame>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _writer;
        private int _queued;

        public BrowserScreen(WebSocket socket)
        {
            _socket = socket;
            _writer = Task.Run(WriteAsync);
        }

        public Task OnFrameAsync(AgentFrame frame)
        {
            Post(frame);
            return Task.CompletedTask;
        }

        public Task OnClosedAsync()
        {
            Post(new AgentFrame { Kind = "evt", Op = "screen.error", Data = new JsonObject { ["message"] = "The connection to the agent was lost." } });
            _queue.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Post(AgentFrame frame)
        {
            if (Volatile.Read(ref _queued) > 400 && frame.Op is "screen.audio" or "screen.pointer")
            {
                return;
            }

            Interlocked.Increment(ref _queued);
            _queue.Writer.TryWrite(frame);
        }

        private async Task WriteAsync()
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref _queued);
                if (_socket.State != WebSocketState.Open)
                {
                    continue;
                }

                try
                {
                    await _socket.SendAsync(AgentWire.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                }
                catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            await _writer;
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
            }
        }
    }
}
