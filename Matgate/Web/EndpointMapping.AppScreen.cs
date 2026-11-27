using System.Net.WebSockets;
using System.Text.Json;
using Matgate.Agent;
using Matgate.Display;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The app API, stage 3 (docs/thixgate-plan.md): a session that runs on Matgate - for connections whose login only
// Matgate knows, and for those the user may only watch - and writing connections back from the app.
public static partial class EndpointMapping
{
    // A remote desktop (RDP, VNC) run by Matgate's own engine for an app: the same screen protocol as the browser's
    // screen page (AgentWire frames: screen.ready, screen.monitor, screen.cursor, screen.auth, screen.ended ...), but
    // the picture comes as "screen.tiles" - compressed pieces of the screen the app draws itself - instead of H.264.
    // The login is Matgate's: the stored one, or what the user types when Matgate asks (screen.auth). Read-only
    // for this user: the picture comes, nothing they press goes. The clipboard goes both ways unless watching (screen.clip.*); screen.stats
    // carries hostRtt, how far the remote desktop is from Matgate. Ends with the socket.
    private static async Task AppScreenAsync(Guid id, HttpContext context, JsonDataStore store, AppDeviceStore devices, DisplayBridge bridge)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        if (user is null || device is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var stored = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (stored is null || !AppLibrary.CanWatch(user, stored))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Through its tunnel, if it has one.
        var server = await ResolveServerForUserAsync(id, user, context, store) ?? stored;
        var watchOnly = IsReadOnlyFor(user, stored);
        var query = context.Request.Query;
        var quality = query["quality"].ToString() is { Length: > 0 and <= 16 } wantedQuality ? wantedQuality : "auto";
        var resolution = query["resolution"].ToString() is { Length: > 0 and <= 16 } wantedResolution ? wantedResolution : "auto";
        var fitWidth = int.TryParse(query["fitWidth"], out var parsedWidth) ? Math.Clamp(parsedWidth, 0, 16384) : 0;
        var fitHeight = int.TryParse(query["fitHeight"], out var parsedHeight) ? Math.Clamp(parsedHeight, 0, 16384) : 0;
        var session = Guid.NewGuid().ToString("N");

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        await using var app = new BrowserScreen(socket);
        var target = new ScreenTarget
        {
            Session = session,
            Protocol = server.Protocol == ServerProtocol.Vnc ? "vnc" : "rdp",
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

        var log = AppLog(context);
        var started = DateTimeOffset.UtcNow;
        log.LogInformation("App screen opened: {User} ({App} on {Device}) to {Server} ({Protocol}, {Host}:{Port}){WatchOnly}.",
            user.UserName, device.App, device.Name, stored.Name, stored.Protocol, stored.Host, stored.Port, watchOnly ? ", watching only" : "");
        var screen = bridge.Open(target, tiles: true);
        screen.UserId = user.Id;
        screen.OnEvent = app.Post;
        try
        {
            // The app's messages go on while the connection is being made: Matgate may ask for a login first.
            var forwarding = Task.Run(() => ForwardBrowserAsync(socket, screen, watchOnly, context.RequestAborted));
            var opening = screen.OpenAsync(context.RequestAborted);
            if (await Task.WhenAny(opening, forwarding) != opening)
            {
                _ = opening.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return;
            }

            var opened = await opening;
            opened["watchOnly"] = watchOnly;
            opened["picture"] = ScreenSession.TileFormat;
            app.Post(new AgentFrame { Kind = "evt", Op = "screen.ready", Data = opened });
            await RecordRecentConnectionAsync(store, user.Id, id, CancellationToken.None);
            await Task.WhenAny(forwarding, screen.Completion);
        }
        catch (ScreenException exception)
        {
            app.Post(ScreenError(exception.Message));
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
        }
        finally
        {
            screen.OnEvent = null;
            await screen.DisposeAsync();
            log.LogInformation("App screen closed: {User} to {Server} after {Seconds:0}s.", user.UserName, stored.Name, (DateTimeOffset.UtcNow - started).TotalSeconds);
        }
    }

    // ---- writing back ----------------------------------------------------------------------------------------

    private static async Task<AppLibrary.ConnectionChange?> ReadConnectionChangeAsync(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<AppLibrary.ConnectionChange>(AppLibrary.JsonOptions, context.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult AppProblem(int status, string error, string? field = null) =>
        Results.Json(new { error, field }, statusCode: status);

    // A new connection of the app's user: private with "Create connections", shared with "Manage connections".
    private static async Task<IResult> AppCreateConnectionAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices, GuacamoleConfigWriter configWriter)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        if (user is null || device is null)
        {
            return Results.Unauthorized();
        }

        var change = await ReadConnectionChangeAsync(context);
        if (change is null)
        {
            return AppProblem(StatusCodes.Status400BadRequest, "invalid_request");
        }

        // Not said: private where the user may, shared otherwise.
        var shared = change.Shared ?? !AppLibrary.CanCreate(user, shared: false);
        if (!AppLibrary.CanCreate(user, shared))
        {
            return AppProblem(StatusCodes.Status403Forbidden, "forbidden");
        }

        change = change with { Shared = shared };
        if (AppLibrary.Check(change, null) is { } problem)
        {
            return AppProblem(StatusCodes.Status400BadRequest, problem.Error, problem.Field);
        }

        var server = AppLibrary.Create(change, user, DateTimeOffset.UtcNow);
        await store.UpdateServersAsync(servers =>
        {
            // A folder has one icon: a connection that joins it takes the folder's.
            server.FolderIconKey = ServerFolders.IconFor(servers, server.Id, server.FolderName, "");
            servers.Add(server);
        }, context.RequestAborted);
        await configWriter.SynchronizeAsync(context.RequestAborted);
        AppLog(context).LogInformation("App: {User} ({App} on {Device}) created connection {Server} ({Protocol}, {Scope}).",
            user.UserName, device.App, device.Name, server.Name, server.Protocol, shared ? "shared" : "private");
        return Results.Json(AppLibrary.Item(user, server), AppLibrary.JsonOptions, statusCode: StatusCodes.Status201Created);
    }

    // Changes what a connection is - with "Edit" on it. Password, key, tunnel, owner and permissions stay.
    private static async Task<IResult> AppUpdateConnectionAsync(Guid id, HttpContext context, JsonDataStore store, AppDeviceStore devices, GuacamoleConfigWriter configWriter)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        if (user is null || device is null)
        {
            return Results.Unauthorized();
        }

        var existing = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (existing is null || !(AppLibrary.CanUse(user, existing) || AppLibrary.CanWatch(user, existing) || AppLibrary.CanEdit(user, existing)))
        {
            return AppProblem(StatusCodes.Status404NotFound, "not_found");
        }

        if (!AppLibrary.CanEdit(user, existing))
        {
            return AppProblem(StatusCodes.Status403Forbidden, "forbidden");
        }

        var change = await ReadConnectionChangeAsync(context);
        if (change is null)
        {
            return AppProblem(StatusCodes.Status400BadRequest, "invalid_request");
        }

        if (AppLibrary.Check(change, existing) is { } problem)
        {
            return AppProblem(StatusCodes.Status400BadRequest, problem.Error, problem.Field);
        }

        ServerEndpoint? updated = null;
        await store.UpdateServersAsync(servers =>
        {
            var server = servers.FirstOrDefault(candidate => candidate.Id == id);
            if (server is null)
            {
                return;
            }

            var folderBefore = server.FolderName;
            AppLibrary.Apply(server, change, DateTimeOffset.UtcNow);
            // Staying in its folder keeps the icon, joining another takes that one's.
            server.FolderIconKey = ServerFolders.IconFor(
                servers,
                id,
                server.FolderName,
                string.Equals(folderBefore, server.FolderName, StringComparison.OrdinalIgnoreCase) ? server.FolderIconKey : "");
            updated = server.Clone();
        }, context.RequestAborted);

        if (updated is null)
        {
            return AppProblem(StatusCodes.Status404NotFound, "not_found");
        }

        await configWriter.SynchronizeAsync(context.RequestAborted);
        AppLog(context).LogInformation("App: {User} ({App} on {Device}) changed connection {Server}.", user.UserName, device.App, device.Name, updated.Name);
        return Results.Json(AppLibrary.Item(user, updated), AppLibrary.JsonOptions);
    }

    // Deletes a connection - with "Manage" on it, or as a server manager for a shared one (as in the form).
    private static async Task<IResult> AppDeleteConnectionAsync(
        Guid id,
        HttpContext context,
        JsonDataStore store,
        AppDeviceStore devices,
        GuacamoleConfigWriter configWriter,
        WebsiteProxyService websiteProxy)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        if (user is null || device is null)
        {
            return Results.Unauthorized();
        }

        var existing = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (existing is null || !(AppLibrary.CanUse(user, existing) || AppLibrary.CanWatch(user, existing) || AppLibrary.CanDelete(user, existing)))
        {
            return AppProblem(StatusCodes.Status404NotFound, "not_found");
        }

        if (!AppLibrary.CanDelete(user, existing))
        {
            return AppProblem(StatusCodes.Status403Forbidden, "forbidden");
        }

        await store.UpdateServersAsync(servers => servers.RemoveAll(server => server.Id == id), context.RequestAborted);
        await store.UpdateUsersAsync(users =>
        {
            foreach (var editedUser in users)
            {
                editedUser.FavoriteServerIds ??= [];
                editedUser.ServerAccess.Remove(id);
                editedUser.FavoriteServerIds.RemoveAll(favoriteId => favoriteId == id);
            }
        }, context.RequestAborted);
        await store.UpdateGrantsAsync(grants => grants.RemoveAll(grant => grant.Targets(AccessTargetKind.Server, id)), context.RequestAborted);
        websiteProxy.ForgetServer(id);
        context.RequestServices.GetRequiredService<WebsiteSiteService>().ForgetServer(id);
        await configWriter.SynchronizeAsync(context.RequestAborted);
        AppLog(context).LogInformation("App: {User} ({App} on {Device}) deleted connection {Server}.", user.UserName, device.App, device.Name, existing.Name);
        return Results.NoContent();
    }
}
