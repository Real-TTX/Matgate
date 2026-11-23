using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Matgate.Agent;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The screen of an agent's machine: a WebSocket that passes the agent's frames (video, sound, pointer,
// clipboard) to the browser as they are, and the browser's input back - only the kinds of message a
// screen viewer may send. Files copied on the machine are downloaded through screen-clip.
public static partial class EndpointMapping
{
    private static readonly HashSet<string> ScreenOpsFromBrowser =
    [
        "screen.input", "screen.ack", "screen.key", "screen.config", "screen.audio", "screen.sas", "screen.auth",
        "screen.clip.set", "screen.clip.begin", "screen.clip.chunk", "screen.clip.end",
        "screen.windows", "screen.window",
        // The viewer's game controllers and microphone, for a machine that can take them.
        "screen.pad", "screen.mic",
    ];

    // What a viewer who may only watch still sends: acknowledgements, the wish for a fresh picture, its size and
    // quality, sound, and the answer to a login question. No keys, no pointer, no clipboard, no controller.
    private static readonly HashSet<string> ScreenOpsWhenWatching =
    [
        "screen.ack", "screen.key", "screen.config", "screen.audio", "screen.auth",
    ];

    private static bool ScreenOpAllowed(string op, bool watchOnly) =>
        watchOnly ? ScreenOpsWhenWatching.Contains(op) : ScreenOpsFromBrowser.Contains(op);

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

        // The screen can do whatever the machine's user can: the same people as for a terminal - and whoever
        // may only watch, with their input dropped.
        var (server, result, user) = await RequireAgentAsync(id, context, store);
        var watchOnly = result is null && IsReadOnlyFor(user!, server!);
        if (result is not null || !(watchOnly || CanUseAgentTerminal(user!, server!)))
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
                // "auto" (fitted to the browser's picture area), "native" or a mode like "1920x1080".
                ["resolution"] = query["resolution"].ToString() is { Length: > 0 and <= 16 } resolution ? resolution : "native",
                ["fitWidth"] = int.TryParse(query["fitWidth"], out var fitWidth) ? Math.Clamp(fitWidth, 0, 16384) : 0,
                ["fitHeight"] = int.TryParse(query["fitHeight"], out var fitHeight) ? Math.Clamp(fitHeight, 0, 16384) : 0,
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
                if (frame.Kind != "evt" || !ScreenOpAllowed(frame.Op, watchOnly))
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

        return Results.Content(ScreenPageHtml(context, user!, server!.Name, server.Id, "agent", "/api/agent/" + server.Id), "text/html; charset=utf-8");
    }

    // The screen page for an agent's machine and for RDP/VNC through Matgate's own engine: the same page, only
    // where its socket and its downloads are differs. It gets the picture quality the settings fix for every
    // new session ("" where each device remembers its own), and the token that lets it end its own session -
    // to connect again with other colours.
    private static string ScreenPageHtml(HttpContext context, MatgateUser user, string title, Guid serverId, string kind, string basePath)
    {
        var de = HtmlViews.Language(context) == "de";
        return AgentScreenPage.Html
            .Replace("__LANG__", de ? "de" : "en")
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(title))
            .Replace("__SERVER__", serverId.ToString())
            .Replace("__KIND__", kind)
            .Replace("__BASE__", basePath)
            .Replace("__QUALITY__", SessionPreferences.NormalizeScreenQuality(user.Session?.ScreenQuality))
            .Replace("__RIGHTCLICK__", SessionPreferences.NormalizeTouchRightClick(user.Session?.TouchRightClick))
            .Replace("__HOLDMS__", SessionPreferences.NormalizeLongPressMs(user.Session?.LongPressMs ?? 550).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__CSRF__", System.Net.WebUtility.HtmlEncode(context.User.FindFirst("csrf")?.Value ?? ""))
            .Replace("__KEYS__", System.Net.WebUtility.HtmlEncode(KeyBar.ScreenJson(user, de)))
            .Replace("__TEXT__", System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(new
            {
                authTitle = de ? "Anmelden" : "Sign in",
                authFailed = de ? "Benutzername oder Passwort waren falsch." : "User name or password was wrong.",
                username = de ? "Benutzer" : "User name",
                password = de ? "Passwort" : "Password",
                domain = de ? "Domäne" : "Domain",
                signIn = de ? "Anmelden" : "Sign in",
                cancel = de ? "Abbrechen" : "Cancel",
                filesUnsupported = de ? "Dateien lassen sich über VNC nicht einfügen - nur Text." : "Files cannot be pasted over VNC - only text.",
                connecting = de ? "Verbinde ..." : "Connecting ...",
                connected = de ? "Verbunden" : "Connected",
                lost = de ? "Verbindung verloren - verbinde neu ..." : "Connection lost - reconnecting ...",
                ended = de ? "Die Bildschirmübertragung ist beendet." : "Screen sharing has ended.",
                reconnect = de ? "Neu verbinden" : "Connect again",
                taken = de ? "Ein anderes Fenster oder Gerät zeigt diesen Bildschirm jetzt." : "Another window or device shows this screen now.",
                takeBack = de ? "Wieder hierher holen" : "Bring it back here",
                openWatched = de
                    ? "Diese Verbindung ist schon offen (seit {time}) - in einem anderen Fenster oder auf einem anderen Gerät. Eine neue Sitzung meldet denselben Benutzer noch einmal an; Windows beendet dann meist die andere oder fragt, welche bleiben soll."
                    : "This connection is open already (since {time}) - in another window or on another device. A new session signs the same user in once more; Windows then mostly ends the other one or asks which one to keep.",
                openWaiting = de
                    ? "Diese Verbindung läuft noch (seit {time}) und wartet darauf, wieder angezeigt zu werden."
                    : "This connection is still running (since {time}) and waits to be shown again.",
                bringHere = de ? "Hierher holen" : "Bring it here",
                newSession = de ? "Neue Sitzung" : "New session",
                unavailable = de ? "Der Rechner zeigt gerade einen geschützten Bildschirm (gesperrt oder eine Administrator-Abfrage). Sobald er wieder normal läuft, geht es hier weiter. Läuft der Agent als Systemdienst, ist auch dieser Bildschirm zu sehen." : "The machine shows a protected screen right now (locked or an administrator prompt). This continues as soon as it is back. With the agent running as a system service, this screen is shown too.",
                error = de ? "Der Bildschirm kann nicht übertragen werden." : "The screen cannot be shared.",
                noDecoder = de ? "Dieser Browser kann den Bildschirm nicht anzeigen (kein WebCodecs). Bitte einen aktuellen Edge, Chrome, Firefox oder Safari nehmen." : "This browser cannot show the screen (no WebCodecs). Please use a current Edge, Chrome, Firefox or Safari.",
                overview = de ? "Agent-Übersicht" : "Agent overview",
                keyBarSetup = de ? "Tastenleiste einrichten" : "Set up the key bar",
                monitor = de ? "Bildschirm" : "Monitor",
                quality = de ? "Bildqualität" : "Picture quality",
                qualityHigh = de ? "Flüssig (60 Bilder/s)" : "Smooth (60 fps)",
                qualityMedium = de ? "Mittel (30 Bilder/s)" : "Medium (30 fps)",
                qualityLow = de ? "Wenig Daten (20 Bilder/s)" : "Low data (20 fps)",
                qualityFast = de ? "Schnellste Reaktion (weniger Farben)" : "Fastest response (fewer colours)",
                // The two lines of each choice in the display dialog.
                qualityHighTitle = de ? "Flüssig" : "Smooth",
                qualityHighHint = de ? "60 Bilder/s" : "60 fps",
                qualityMediumTitle = de ? "Mittel" : "Medium",
                qualityMediumHint = de ? "30 Bilder/s" : "30 fps",
                qualityLowTitle = de ? "Wenig Daten" : "Low data",
                qualityLowHint = de ? "20 Bilder/s" : "20 fps",
                qualityFastTitle = de ? "Schnellste Reaktion" : "Fastest response",
                qualityFastHint = de ? "weniger Farben" : "fewer colours",
                qualityNoteDevice = de ? "Dieses Gerät merkt sich die Wahl für diese Verbindung." : "This device remembers the choice for this connection.",
                qualityForAll = de ? "Für alle Geräte festlegen ›" : "Set for all devices ›",
                qualityNoteFixed = de ? "Neue Sitzungen starten mit „{title}“ – festgelegt in den Einstellungen." : "New sessions start with “{title}” – set in Settings.",
                qualityChange = de ? "Ändern ›" : "Change ›",
                qualityReconnect = de ? "Die Farben ändern sich beim nächsten Verbinden." : "The colours change with the next connection.",
                reconnectNow = de ? "Jetzt neu verbinden" : "Reconnect now",
                later = de ? "Später" : "Later",
                fewerColours = de ? "weniger Farben" : "fewer colours",
                colourBits = de ? "{bits} Bit" : "{bits} bit",
                soundOn = de ? "Ton einschalten" : "Turn sound on",
                soundOff = de ? "Ton ausschalten" : "Turn sound off",
                soundBlocked = de ? "Ton: einmal ins Bild klicken" : "Sound: click into the picture once",
                noSound = de ? "Der Rechner hat keine Tonausgabe." : "The machine has no sound output.",
                micOn = de ? "Mikrofon einschalten" : "Turn the microphone on",
                micOff = de ? "Mikrofon ausschalten" : "Turn the microphone off",
                micStarting = de ? "Mikrofon an ..." : "Microphone on ...",
                micPlaying = de ? "Mikrofon an - auf dem Rechner als „{device}“" : "Microphone on - on the machine as \"{device}\"",
                micNoDevice = de
                    ? "Der Rechner hat kein virtuelles Mikrofon. Dort ein virtuelles Audiokabel installieren (z. B. VB-CABLE) - der Agent spielt das Mikrofon hinein."
                    : "The machine has no virtual microphone. Install a virtual audio cable there (VB-CABLE, for one) - the agent plays the microphone into it.",
                micSwitchedOff = de ? "Das Mikrofon ist in den Einstellungen des Agents abgeschaltet." : "The microphone is switched off in the agent's settings.",
                micDenied = de ? "Der Browser gibt das Mikrofon nicht frei - in den Einstellungen der Seite erlauben." : "The browser does not hand out the microphone - allow it in the page's settings.",
                micUnsupported = de ? "Dieser Browser gibt hier kein Mikrofon heraus (nur über HTTPS)." : "This browser hands out no microphone here (HTTPS only).",
                micFailed = de ? "Das Mikrofon ließ sich nicht öffnen." : "The microphone could not be opened.",
                padConnected = de ? "Controller {n} verbunden - am Rechner ein Xbox-Controller" : "Controller {n} connected - an Xbox controller on the machine",
                padNoDriver = de
                    ? "Controller erkannt, aber dem Rechner fehlt der Treiber dafür (ViGEmBus). Dort installieren, dann geht er."
                    : "A controller was found, but the machine lacks the driver for it (ViGEmBus). Install it there and it works.",
                padSwitchedOff = de ? "Controller sind in den Einstellungen des Agents abgeschaltet." : "Controllers are switched off in the agent's settings.",
                keyboard = de ? "Tastatur" : "Keyboard",
                resolution = de ? "Auflösung des Rechners" : "The machine's resolution",
                resolutionAuto = de ? "An Fenster anpassen" : "Fit to window",
                resolutionNative = de ? "Wie am Rechner" : "As on the machine",
                target = de ? "Bildschirm über den Agent" : "Screen through the agent",
                cad = de ? "Strg+Alt+Entf senden" : "Send Ctrl+Alt+Del",
                noCad = de ? "Strg+Alt+Entf geht nur, wenn der Agent als Systemdienst läuft." : "Ctrl+Alt+Del only works when the agent runs as a system service.",
                switching = de ? "Der Rechner wechselt die Sitzung (Anmeldung oder Abmeldung) - gleich geht es weiter ..." : "The machine is switching sessions (sign-in or sign-out) - one moment ...",
                showBar = de ? "Leiste einblenden" : "Show the bar",
                fps = de ? "B/s" : "fps",
                statsTitle = de
                    ? "{fps} Bilder/s (höchstens {limit}), Hin und zurück {rtt} ms, {kbps} kbit/s, auf dem Rechner {agent} ms je Bild, Encoder: {encoder}"
                    : "{fps} fps (at most {limit}), round trip {rtt} ms, {kbps} kbit/s, {agent} ms per picture on the machine, encoder: {encoder}",
                fullscreen = de ? "Vollbild" : "Full screen",
                pasting = de ? "Füge ein ..." : "Pasting ...",
                clipFailed = de ? "Die Zwischenablage des Rechners hat das nicht angenommen." : "The machine's clipboard did not take this.",
                clipFiles = de ? "Auf dem Rechner kopierte Dateien" : "Files copied on the machine",
                download = de ? "Herunterladen" : "Download",
                folder = de ? "Ordner (nicht ladbar)" : "Folder (cannot be downloaded)",
                close = de ? "Schließen" : "Close",
                perf = de ? "Leistungsanzeige im Bild" : "Performance numbers in the picture",
                perfHint = de
                    ? "Bilder/s gesendet/gezeigt, Laufzeit hin und zurück, Datenrate - darunter Bildgröße, Maßstab, Encoder und Zeit je Bild."
                    : "Pictures per second sent/shown, round trip, data rate - below it picture size, scale, encoder and time per picture.",
                perfPosition = de ? "Leistungsanzeige steht" : "Performance numbers stand",
                perfTopLeft = de ? "Oben links" : "Top left",
                perfTopRight = de ? "Oben rechts" : "Top right",
                perfBottomLeft = de ? "Unten links" : "Bottom left",
                perfBottomRight = de ? "Unten rechts" : "Bottom right",
                perfWaiting = "...",
                scale = de ? "Skalierung" : "Scale",
                fixedSizes = de ? "Feste Auflösung" : "A fixed resolution",
                keyboardResize = de ? "Auflösung an die Tastatur anpassen" : "Fit the resolution to the keyboard",
                keyboardResizeHint = de
                    ? "An: der Rechner bekommt eine Auflösung, die über die Tastatur passt. Aus: das Bild bleibt, wie es ist - mit zwei Fingern verschieben, es folgt dem Zeiger."
                    : "On: the machine gets a resolution that fits above the keyboard. Off: the picture stays as it is - move it with two fingers, it follows the pointer.",
                windows = de ? "Fenster" : "Windows",
                wholeScreen = de ? "Ganzer Bildschirm" : "The whole screen",
                onlyWindow = de ? "Nur das gewählte Fenster zeigen" : "Show the chosen window only",
                onlyWindowHint = de ? "Das Fenster füllt das Bild und bleibt es, auch wenn es verschoben wird." : "The window fills the picture and stays it, wherever it is moved.",
                minimized = de ? "minimiert" : "minimised",
                frontWindow = de ? "vorn" : "in front",
            })));
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
