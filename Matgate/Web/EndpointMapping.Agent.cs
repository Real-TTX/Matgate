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
        // Not behind the sign-in: an agent has no account - its token is its key (see AgentAttachAsync).
        app.Map("/api/agent/attach", AgentAttachAsync).AllowAnonymous();
        app.MapGet("/api/agent/{id:guid}/logo", AgentLogoAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/system", AgentSystemAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/terminal-socket", AgentTerminalSocketAsync).RequireAuthorization();
        app.MapPost("/api/agent/{id:guid}/terminal/kill", AgentTerminalKillAsync).RequireAuthorization();
        app.MapGet("/api/agent/{id:guid}/sessions", AgentSessionsAsync).RequireAuthorization();
        app.MapPost("/api/agent/{id:guid}/update", AgentUpdateAsync).RequireAuthorization();
        app.MapGet("/agent/{id:guid}/terminal", AgentTerminalPageAsync).RequireAuthorization();
        app.MapGet("/assets/xterm/{file}", XtermAsset);
        app.MapGet("/assets/codemirror/{file}", (string file) => EmbeddedAsset("codemirror/", file));
        MapAgentScreenEndpoints(app);
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
            || (user.FileAccessRules?.Any(rule => rule.ServerId == server.Id) != true && !IsReadOnlyFor(user, server));
    }

    // An agent that connects to Matgate itself (its configuration has Matgate's address): for a machine Matgate
    // cannot reach. It shows its token; the agent connection with that token is the one it is - compared in
    // constant time, and a wrong one gets nothing but the status, a little late. From then on Matgate uses this
    // connection for that agent - files, terminals, screen, tunnel - until it ends.
    private static async Task AgentAttachAsync(HttpContext context, JsonDataStore store, AgentClientPool agents, ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var offered = header.StartsWith("Bearer ", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes(header[7..].Trim()) : [];
        ServerEndpoint? match = null;
        if (offered.Length >= 16)
        {
            foreach (var server in (await store.GetServersAsync(context.RequestAborted)).Where(server => server.Protocol == ServerProtocol.Agent && server.IsEnabled))
            {
                var token = System.Text.Encoding.UTF8.GetBytes((server.Password ?? "").Trim());
                if (token.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(offered, token))
                {
                    match = server;
                }
            }
        }

        var logger = loggers.CreateLogger("Matgate.Agent");
        if (match is null)
        {
            logger.LogWarning("An agent with an unknown token tried to connect from {Address}.", context.Connection.RemoteIpAddress);
            await Task.Delay(500);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        AgentLink link;
        try
        {
            link = await AgentLink.AttachAsync(socket, logger, context.RequestAborted);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return;
        }

        agents.Attach(match.Id, link);
        await RememberAgentPlatformAsync(store, match, link.Hello, context.RequestAborted);
        try
        {
            await link.Completion.WaitAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            agents.Detach(match.Id, link);
            link.Dispose();
        }
    }

    // What the agent said it is, kept with its connection: the start page and the side panel show the
    // system's sign by it. Written only when it changed - this runs on every status call.
    private static async Task RememberAgentPlatformAsync(JsonDataStore store, ServerEndpoint server, AgentHello hello, CancellationToken cancellationToken)
    {
        var platform = (hello.Platform ?? "").Trim();
        if (platform.Length == 0 || string.Equals(server.AgentPlatform, platform, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await store.UpdateServersAsync(servers =>
            {
                var stored = servers.FirstOrDefault(candidate => candidate.Id == server.Id);
                if (stored is not null)
                {
                    stored.AgentPlatform = platform;
                }
            }, cancellationToken);
            server.AgentPlatform = platform;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<IResult> AgentStatusAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents, AgentUpdates updates)
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
            await RememberAgentPlatformAsync(store, server!, hello, context.RequestAborted);
            var terminalsAllowed = CanUseAgentTerminal(user!, server!);
            return Results.Json(new
            {
                online = true,
                logo = await AgentLogoAddressAsync(link, id, context.RequestAborted),
                name = hello.Name,
                machine = hello.Machine,
                user = hello.User,
                os = hello.Os,
                version = hello.Version,
                plugins = hello.Plugins
                    .Where(plugin => (plugin.Kind != "terminal" && plugin.Kind != "screen") || terminalsAllowed)
                    .Select(plugin => new
                    {
                        id = plugin.Id,
                        kind = plugin.Kind,
                        title = plugin.Title,
                        variants = plugin.Variants.Select(variant => new { id = variant.Id, title = variant.Title }),
                        microphone = plugin.Microphone,
                        gamepad = plugin.Gamepad,
                    }),
                update = await UpdateInfoAsync(hello, user!, server!, updates, context.RequestAborted),
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return Results.Json(new { online = false, error = exception.Message, plugins = Array.Empty<object>() });
        }
    }

    // The machine's own picture for its start page (the agent's setup puts it there), as an address the
    // page can show - with the picture's stamp in it, so a browser keeps it until it changes. Null: none,
    // or an agent from before there were logos.
    private static async Task<string?> AgentLogoAddressAsync(AgentLink link, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var stat = await link.RequestAsync("logo.stat", cancellationToken: cancellationToken);
            return stat.Bool("exists") && AgentLogoTypes.Contains(stat.Text("type") ?? "")
                ? $"/api/agent/{id}/logo?v={Uri.EscapeDataString(stat.Text("stamp") ?? "")}"
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    // How busy the machine is (processor, memory), for the status bar. An agent from before this, or one on a system
    // that cannot tell: { available: false }, and the status bar shows nothing.
    private static async Task<IResult> AgentSystemAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        var (server, result, _) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            var answer = await link.RequestAsync("sys.stats", cancellationToken: context.RequestAborted);
            var data = answer.Data ?? [];
            long Number(string name) => data[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
            double? cpu = data["cpu"] is System.Text.Json.Nodes.JsonValue cpuValue && cpuValue.TryGetValue<double>(out var percent) ? percent : null;
            return Results.Json(new
            {
                available = true,
                cpu,
                cores = Number("cores"),
                memTotal = Number("memTotal"),
                memUsed = Number("memUsed"),
                memAvailable = Number("memAvailable"),
                swapTotal = Number("swapTotal"),
                swapUsed = Number("swapUsed"),
                uptime = Number("uptime"),
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return Results.Json(new { available = false });
        }
    }

    private static readonly HashSet<string> AgentLogoTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/svg+xml",
    };

    private static async Task<IResult> AgentLogoAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents)
    {
        var (server, result, _) = await RequireAgentAsync(id, context, store);
        if (result is not null)
        {
            return result;
        }

        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            var answer = await link.RequestAsync("logo.read", cancellationToken: context.RequestAborted);
            var type = answer.Text("type") ?? "";
            if (!AgentLogoTypes.Contains(type) || answer.Payload.Length is 0 or > 1024 * 1024)
            {
                return Results.NotFound();
            }

            // The file is the machine's, not Matgate's: it is shown as a picture and nothing else. An SVG
            // opened by its address must not run a script in Matgate's name.
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("v")
                ? "private, max-age=31536000, immutable"
                : "private, no-cache";
            return Results.Bytes(answer.Payload.ToArray(), type);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            return Results.NotFound();
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
                ["variant"] = query["variant"].ToString(),
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

    // Whether there is a newer agent, and whether this user may install it here.
    private static async Task<object> UpdateInfoAsync(AgentHello hello, MatgateUser user, ServerEndpoint server, AgentUpdates updates, CancellationToken cancellationToken)
    {
        AgentUpdates.AgentRelease? latest = null;
        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quick.CancelAfter(TimeSpan.FromSeconds(4));
            latest = await updates.LatestAsync(hello.Platform, quick.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return new
        {
            current = hello.Version,
            latest = latest?.Version,
            available = AgentUpdates.IsNewer(latest, hello.Version),
            canUpdate = hello.CanUpdate,
            allowed = CanEditServer(user, server),
            automatic = hello.AutoUpdate,
        };
    }

    // "Jetzt aktualisieren": the newest agent onto this machine, now. Whoever may change the connection may do it.
    private static async Task<IResult> AgentUpdateAsync(Guid id, HttpContext context, JsonDataStore store, AgentClientPool agents, AgentUpdates updates)
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

        if (!CanEditServer(user!, server!))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            var link = await agents.GetAsync(server!, context.RequestAborted);
            var latest = await updates.LatestAsync(link.Hello.Platform, context.RequestAborted)
                ?? throw new InvalidOperationException("No agent release can be read right now.");
            if (!AgentUpdates.IsNewer(latest, link.Hello.Version))
            {
                return Results.Ok(new { ok = true, version = link.Hello.Version, current = true });
            }

            await updates.PushAsync(link, latest, context.RequestAborted);
            return Results.Ok(new { ok = true, version = latest.Version });
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TimeoutException)
        {
            return Results.BadRequest(new { error = exception.Message });
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
                    cwd = item["cwd"]?.GetValue<string>() ?? "",
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

        return Results.Content(TerminalPageHtml(context, user!, server!.Name, server.Id, "agent", "/api/agent/" + server.Id, 0), "text/html; charset=utf-8");
    }

    // The terminal page for an agent's programs and for SSH: the same page, its socket elsewhere.
    private static string TerminalPageHtml(HttpContext context, MatgateUser user, string title, Guid serverId, string kind, string basePath, int fontSize)
    {
        var de = HtmlViews.Language(context) == "de";
        var ssh = kind == "ssh";
        return AgentTerminalPage.Html
            .Replace("__TOUCHGUARD__", TouchGuard.Script)
            .Replace("__LANG__", de ? "de" : "en")
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(title))
            .Replace("__SERVER__", serverId.ToString())
            .Replace("__KIND__", kind)
            .Replace("__BASE__", basePath)
            .Replace("__FONT__", fontSize > 0 ? ServerEndpoint.NormalizeTerminalFontSize(fontSize).ToString() : "0")
            .Replace("__CSRF__", System.Net.WebUtility.HtmlEncode(context.User.FindFirst("csrf")?.Value ?? ""))
            .Replace("__KEYS__", System.Net.WebUtility.HtmlEncode(KeyBar.TerminalJson(user, de)))
            .Replace("__TEXT__", System.Net.WebUtility.HtmlEncode(JsonSerializer.Serialize(new
            {
                connecting = de ? "Verbinde ..." : "Connecting ...",
                connected = de ? "Verbunden" : "Connected",
                reattached = de ? "Wieder verbunden - die Sitzung lief weiter" : "Reconnected - the session kept running",
                lost = de ? "Verbindung verloren - verbinde neu ..." : "Connection lost - reconnecting ...",
                ended = ssh ? (de ? "Verbindung beendet" : "Disconnected") : (de ? "Programm beendet" : "Program ended"),
                restart = ssh ? (de ? "Neu verbinden" : "Connect again") : (de ? "Neu starten" : "Start again"),
                kill = ssh ? (de ? "Trennen" : "Disconnect") : (de ? "Beenden" : "End"),
                killConfirm = ssh ? (de ? "SSH-Verbindung trennen?" : "Disconnect the SSH session?") : (de ? "Programm wirklich beenden?" : "Really end the program?"),
                overview = de ? "Agent-Übersicht" : "Agent overview",
                keyBarSetup = de ? "Tastenleiste einrichten" : "Set up the key bar",
                uploading = de ? "Lade hoch ({n}/{count}): {name}" : "Uploading ({n}/{count}): {name}",
                uploaded = de ? "{count} Datei(en) liegen auf dem Server in {folder}" : "{count} file(s) are on the server in {folder}",
                uploadFailed = de ? "Die Datei konnte nicht übertragen werden." : "The file could not be transferred.",
            })));
    }

    private static IResult XtermAsset(string file) => EmbeddedAsset("xterm/", file);

    // A file of a library served from the gateway itself (no CDN), cached by the browser for a day.
    private static IResult EmbeddedAsset(string prefix, string file)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(prefix + file);
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
