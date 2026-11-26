using System.Net.Sockets;
using System.Net.WebSockets;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The app API (docs/thixgate-plan.md, stage 2): what ThixGate - a remote manager on Windows and macOS - needs from a
// Matgate. The sign-in goes through the browser and ends in a device token (AppDeviceStore); with it the app reads
// its library and connects through Matgate to the connections in it (the relay). Every /app/api request carries the
// token as "Authorization: Bearer"; the sign-in cookie of the browser counts nowhere here, so there is nothing for a
// foreign page to forge.
public static partial class EndpointMapping
{
    private static void MapAppEndpoints(WebApplication app)
    {
        app.MapGet("/app/authorize", AppAuthorizeAsync).RequireAuthorization();
        app.MapPost("/app/authorize", AppAuthorizeAnswerAsync).RequireAuthorization();
        app.MapPost("/app/api/token", AppTokenAsync).RequireRateLimiting("login");
        app.MapGet("/app/api/me", AppMeAsync);
        app.MapGet("/app/api/library", AppLibraryAsync);
        app.MapPost("/app/api/logout", AppLogoutAsync);
        app.Map("/app/api/relay/{id:guid}", AppRelayAsync);
        app.MapPost("/account/devices/{id:guid}/remove", RemoveAppDeviceAsync).RequireAuthorization();
    }

    // "Allow ThixGate on ANNA-PC?" - for the user signed in in this browser (the cookie sends everyone else to the
    // sign-in first, and back here afterwards).
    private static async Task<IResult> AppAuthorizeAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var query = context.Request.Query;
        var request = new HtmlViews.AppAuthorizeRequest(
            query["client_id"].ToString(),
            query["redirect_uri"].ToString(),
            query["code_challenge"].ToString(),
            query["state"].ToString(),
            query["device"].ToString());
        var problem = AppRequestProblem(context, request, query["code_challenge_method"].ToString());
        if (problem is not null)
        {
            return Results.Content(views.AppAuthorizeProblem(context, user, problem), "text/html");
        }

        return Results.Content(views.AppAuthorize(context, user, request, AppDeviceStore.Apps[request.Client]), "text/html");
    }

    // The answer: back to the app with a one-time code (allowed) or with "access_denied".
    private static async Task<IResult> AppAuthorizeAnswerAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var request = new HtmlViews.AppAuthorizeRequest(
            form["client"].ToString(),
            form["redirect_uri"].ToString(),
            form["code_challenge"].ToString(),
            form["state"].ToString(),
            form["device"].ToString());
        var problem = AppRequestProblem(context, request, "S256");
        if (problem is not null)
        {
            return Results.Content(views.AppAuthorizeProblem(context, user, problem), "text/html");
        }

        var parameters = new Dictionary<string, string?>();
        if (form["decision"].ToString() == "allow")
        {
            parameters["code"] = devices.CreateCode(user.Id, request.Client, request.Device, request.RedirectUri, request.CodeChallenge, DateTimeOffset.UtcNow);
            AppLog(context).LogInformation("App sign-in: {User} allowed {App} on {Device}.", user.UserName, request.Client, AppDeviceStore.CleanName(request.Device));
        }
        else
        {
            parameters["error"] = "access_denied";
        }

        if (request.State.Length > 0)
        {
            parameters["state"] = request.State;
        }

        return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(request.RedirectUri, parameters));
    }

    // Why a sign-in request cannot be answered; null when it can. A request that is wrong is never sent back to its
    // address: that address may be the very thing that is wrong.
    private static string? AppRequestProblem(HttpContext context, HtmlViews.AppAuthorizeRequest request, string method)
    {
        var de = HtmlViews.Language(context) == "de";
        if (!AppDeviceStore.Apps.ContainsKey(request.Client))
        {
            return de ? "Diese App kennt Matgate nicht." : "Matgate does not know this app.";
        }

        if (!AppDeviceStore.IsLoopbackRedirect(request.RedirectUri))
        {
            return de ? "Die App hat keine Rücksprungadresse auf diesem Gerät angegeben." : "The app gave no return address on this device.";
        }

        if (method != "S256" || !AppDeviceStore.IsValidChallenge(request.CodeChallenge))
        {
            return de ? "Die Anfrage der App ist unvollständig. Bitte aktualisiere die App." : "The app's request is incomplete. Please update the app.";
        }

        return request.State.Length > 200 || request.Device.Length > 200
            ? (de ? "Die Anfrage der App ist zu lang." : "The app's request is too long.")
            : null;
    }

    public sealed record AppTokenRequest(string? Code, string? CodeVerifier, string? RedirectUri);

    // The app trades its code for its token.
    private static async Task<IResult> AppTokenAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices)
    {
        AppTokenRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<AppTokenRequest>(context.RequestAborted);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            body = null;
        }

        var now = DateTimeOffset.UtcNow;
        var pending = devices.RedeemCode(body?.Code, body?.CodeVerifier, body?.RedirectUri, now);
        var user = pending is null ? null : await store.FindUserByIdAsync(pending.UserId, context.RequestAborted);
        if (pending is null || user is not { IsEnabled: true })
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status400BadRequest);
        }

        var issued = devices.AddDevice(pending, ClientAddress(context), now);
        AppLog(context).LogInformation("App sign-in: {App} on {Device} signed in for {User}.", issued.Device.App, issued.Device.Name, user.UserName);
        return Results.Json(new
        {
            token = issued.Token,
            deviceId = issued.Device.Id,
            user = AppUser(user),
        });
    }

    private static async Task<IResult> AppMeAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        return user is null || device is null
            ? Results.Unauthorized()
            : Results.Json(new { user = AppUser(user), deviceId = device.Id, deviceName = device.Name, version = HtmlViews.ApplicationVersion() });
    }

    // The library, with its revision as ETag: an app that asks again with If-None-Match gets 304 while nothing changed.
    private static async Task<IResult> AppLibraryAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices)
    {
        var (user, _) = await AppUserAsync(context, store, devices);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var library = AppLibrary.Build(user, await store.GetServersAsync(context.RequestAborted));
        var etag = "\"" + library.Revision + "\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Json(library, AppLibrary.JsonOptions);
    }

    // The app signs itself out: its device goes, its token with it.
    private static async Task<IResult> AppLogoutAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices)
    {
        var (user, device) = await AppUserAsync(context, store, devices);
        if (user is null || device is null)
        {
            return Results.Unauthorized();
        }

        devices.Remove(user.Id, device.Id);
        AppLog(context).LogInformation("App sign-in: {App} on {Device} signed out for {User}.", device.App, device.Name, user.UserName);
        return Results.NoContent();
    }

    // Account -> Security -> Devices: signs a device out.
    private static async Task<IResult> RemoveAppDeviceAsync(Guid id, HttpContext context, JsonDataStore store, AppDeviceStore devices, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        if (devices.Remove(user.Id, id))
        {
            AppLog(context).LogInformation("App sign-in: {User} signed out device {Device}.", user.UserName, id);
        }

        return Results.Redirect(EmbedAwareRedirect(context, "/account?tab=security&saved=device"));
    }

    // The relay: a WebSocket over which Matgate opens one TCP connection to the host and port of exactly this
    // connection - no other address, so it is no open proxy. It needs "Connect" on the connection (AppLibrary.CanUse);
    // through a tunnel when the connection goes through one. The app runs its own engine over it, with its own login.
    private static async Task AppRelayAsync(Guid id, HttpContext context, JsonDataStore store, AppDeviceStore devices)
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

        var server = await store.FindServerByIdAsync(id, context.RequestAborted);
        if (server is null || !AppLibrary.CanUse(user, server))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var log = AppLog(context);
        var target = server;
        if (server.TunnelId is not null && context.RequestServices.GetService<TunnelService>() is { } tunnels)
        {
            try
            {
                target = await tunnels.RouteAsync(server, context.RequestAborted);
            }
            catch (InvalidOperationException exception)
            {
                log.LogWarning("Relay to {Server}: {Message}", server.Name, exception.Message);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
        }

        using var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(target.Host, target.Port, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            log.LogWarning("Relay to {Server} ({Host}:{Port}) for {User}: not reachable ({Message}).", server.Name, server.Host, server.Port, user.UserName, exception.Message);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        var started = DateTimeOffset.UtcNow;
        log.LogInformation("Relay opened: {User} ({App} on {Device}) to {Server} ({Protocol}, {Host}:{Port}).",
            user.UserName, device.App, device.Name, server.Name, server.Protocol, server.Host, server.Port);
        var (sent, received) = await AppRelay.RelayAsync(socket, tcp.GetStream(), context.RequestAborted);
        log.LogInformation("Relay closed: {User} to {Server} after {Seconds:0}s, {Up} bytes up, {Down} bytes down.",
            user.UserName, server.Name, (DateTimeOffset.UtcNow - started).TotalSeconds, sent, received);
    }

    // The user and device of the request's token; nothing for no token, an unknown one, or a user who is gone or
    // switched off.
    private static async Task<(MatgateUser? User, AppDevice? Device)> AppUserAsync(HttpContext context, JsonDataStore store, AppDeviceStore devices)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var device = devices.FindByToken(header[scheme.Length..].Trim());
        if (device is null)
        {
            return (null, null);
        }

        var user = await store.FindUserByIdAsync(device.UserId, context.RequestAborted);
        if (user is null)
        {
            devices.RemoveUser(device.UserId);
            return (null, null);
        }

        if (!user.IsEnabled)
        {
            return (null, null);
        }

        devices.Touch(device, ClientAddress(context), DateTimeOffset.UtcNow);
        return (user, device);
    }

    private static object AppUser(MatgateUser user) => new
    {
        id = user.Id,
        userName = user.UserName,
        displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName,
    };

    private static string ClientAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "";

    private static ILogger AppLog(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Matgate.App");
}
