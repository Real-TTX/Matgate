using System.Net;
using System.Security.Claims;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// A website under a host name of its own (see WebsiteSiteService): the routes of the shell that hand out
// such an address, and everything that arrives under one.
public static partial class EndpointMapping
{
    private static void MapWebsiteSiteEndpoints(WebApplication app)
    {
        app.MapPost("/api/website/{id:guid}/site", WebsiteSiteOpenAsync).RequireAuthorization();
    }

    // Requests for a website's host name never reach Matgate's own pages: they are answered here, before
    // sign-in and routing - a website has paths like /login and /api of its own.
    public static void UseWebsiteSites(this WebApplication app)
    {
        if (WebsiteSiteService.PatternProblem is { Length: > 0 } problem)
        {
            app.Logger.LogWarning("{Problem}", problem);
        }

        if (!WebsiteSiteService.Available)
        {
            return;
        }

        app.Logger.LogInformation("Websites open under host names of their own: {Pattern}.", WebsiteSiteService.PatternText);
        app.Use(async (context, next) =>
        {
            var label = WebsiteSiteService.LabelOf(context.Request.Host);
            if (label is null)
            {
                await next();
                return;
            }

            await ServeWebsiteSiteAsync(context, label);
        });
    }

    // A browser tab where websites have no host names of their own: the frame stays under a path of
    // Matgate's, and the address bar still takes any address - each website gets a frame of its own there
    // (see WebsiteProxyService.Browse). Without an address the tab starts empty.
    private static async Task<IResult> WebsiteBrowseUnderPathAsync(HttpContext context, ServerEndpoint server, string typed)
    {
        if (!Guid.TryParse(context.Request.Query["tab"].ToString(), out var tabId) || tabId == Guid.Empty)
        {
            return Results.Ok(new { available = false });
        }

        var address = typed.Length > 0
            ? WebsiteSiteService.ParseAddress(await WebsiteProxyService.WithSchemeAsync(typed, context.RequestAborted))
            : WebsiteSiteService.StartUri(server);
        if (typed.Length > 0 && address is null)
        {
            return Results.BadRequest(new { error = HtmlViews.Translate(context, "This is not a web address.") });
        }

        if (address is null)
        {
            return Results.Ok(new { available = false, browse = true, url = (string?)null });
        }

        var proxy = context.RequestServices.GetRequiredService<WebsiteProxyService>();
        return Results.Ok(new
        {
            available = false,
            browse = true,
            address = address.AbsoluteUri,
            url = proxy.Browse(context, server, tabId, address),
        });
    }

    // The shell asks where a website tab's frame should point. With host names for websites that is an
    // address under the website's own name, carrying a ticket that lets this browser in; without, the shell
    // falls back to the path under Matgate's address.
    private static async Task<IResult> WebsiteSiteOpenAsync(Guid id, HttpContext context, JsonDataStore store, WebsiteSiteService sites)
    {
        var access = await RequireWebsiteServerAsync(id, context, store);
        if (access.Result is not null)
        {
            return access.Result;
        }

        if (!ValidateCsrfHeader(context))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var server = access.Server!;
        var typed = context.Request.Query["url"].ToString().Trim();
        if (!WebsiteSiteService.Available
            || server.WebsiteRenderMode != WebsiteRenderMode.Native
            || server.WebsitePathMode
            // The shell found that this browser does not keep a website's cookie inside the frame.
            || context.Request.Query["path"] == "1"
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return server.WebsiteBrowse && server.WebsiteRenderMode == WebsiteRenderMode.Native
                ? await WebsiteBrowseUnderPathAsync(context, server, typed)
                : Results.Ok(new { available = false });
        }

        var start = WebsiteSiteService.StartUri(server);
        Uri? address = null;
        if (typed.Length > 0)
        {
            // An address from the address bar: within this website, or - in a browser tab - anywhere.
            // Only a browser tab has Matgate find out whether a machine speaks https: a connection stays on its own.
            address = WebsiteSiteService.ParseAddress(server.WebsiteBrowse ? await WebsiteProxyService.WithSchemeAsync(typed, context.RequestAborted) : typed);
            if (address is null)
            {
                return Results.BadRequest(new { error = HtmlViews.Translate(context, "This is not a web address.") });
            }

            if (!server.WebsiteBrowse && (start is null || !address.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new { error = HtmlViews.Translate(context, "This connection only opens its own website.") });
            }
        }

        address ??= start;
        if (address is null)
        {
            // A browser tab that has no page yet.
            return Results.Ok(new { available = true, browse = server.WebsiteBrowse, hosts = WebsiteSiteService.PatternText, url = (string?)null });
        }

        var site = sites.Open(userId, server, address);
        site.ShellOrigin = context.Request.Scheme + "://" + context.Request.Host;
        var path = address.PathAndQuery + address.Fragment;
        return Results.Ok(new
        {
            available = true,
            browse = server.WebsiteBrowse,
            hosts = WebsiteSiteService.PatternText,
            origin = WebsiteSiteService.PublicOrigin(context, site.Label),
            target = site.Origin,
            address = site.Origin + path,
            url = WebsiteSiteService.EnterAddress(context, site, path),
        });
    }

    private static async Task ServeWebsiteSiteAsync(HttpContext context, string label)
    {
        var sites = context.RequestServices.GetRequiredService<WebsiteSiteService>();
        var store = context.RequestServices.GetRequiredService<JsonDataStore>();
        var path = context.Request.Path.Value ?? "/";

        if (!sites.TryGet(label, out var site))
        {
            await WebsiteSiteDeniedAsync(context);
            return;
        }

        if (path.Equals(WebsiteSiteService.EnterPath, StringComparison.Ordinal))
        {
            if (!site.Redeem(context.Request.Query["t"].ToString()))
            {
                await WebsiteSiteDeniedAsync(context);
                return;
            }

            context.Response.Cookies.Append(WebsiteSiteService.CookieName, site.Secret, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
            });
            var to = context.Request.Query["to"].ToString();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Redirect(to.StartsWith('/') && !to.StartsWith("//", StringComparison.Ordinal) && !to.Contains('\\') ? to : "/");
            return;
        }

        if (!PasswordHasher.SecureEquals(context.Request.Cookies[WebsiteSiteService.CookieName], site.Secret))
        {
            await WebsiteSiteDeniedAsync(context);
            return;
        }

        // The user and the connection as they are now: an account that was switched off, a connection that
        // was taken away or changed ends here, whatever the browser still holds.
        var user = await store.FindUserByIdAsync(site.UserId, context.RequestAborted);
        var server = user is null || !user.IsEnabled
            ? null
            : await store.FindServerByIdAsync(site.ServerId, context.RequestAborted)
              ?? context.RequestServices.GetService<EphemeralServerStore>()?.TryResolve(site.ServerId, user.Id);
        if (user is null
            || server is null
            || !server.IsEnabled
            || !CanAccessServer(user, server)
            || !ServerEndpoint.IsWebsiteProtocol(server.Protocol)
            || server.WebsiteRenderMode != WebsiteRenderMode.Native
            || server.WebsitePathMode)
        {
            await WebsiteSiteDeniedAsync(context);
            return;
        }

        if (path.Equals(WebsiteSiteService.ScriptPath, StringComparison.Ordinal))
        {
            context.Response.ContentType = "application/javascript; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(WebsiteSiteService.Script(site, server.WebsiteBrowse), context.RequestAborted);
            return;
        }

        if (path.Equals(WebsiteSiteService.Reserved + "go", StringComparison.Ordinal))
        {
            // From this website to another address: through Matgate where this tab may, directly otherwise.
            context.Response.Headers.CacheControl = "no-store";
            if (!Uri.TryCreate(context.Request.Query["u"].ToString(), UriKind.Absolute, out var address)
                || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.Redirect(
                address.GetLeftPart(UriPartial.Authority).Equals(site.Origin, StringComparison.OrdinalIgnoreCase) ? address.PathAndQuery + address.Fragment
                : WebsiteSiteService.Reaches(site, server, address) ? sites.EnterAddress(context, site, server, address)
                : address.AbsoluteUri);
            return;
        }

        if (path.StartsWith(WebsiteSiteService.Reserved, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await sites.ProxyAsync(context, site, server);
    }

    // No way in: the address is not (or no longer) a website of this browser's user. The page says so, and
    // tells the shell around it - which asks for a fresh address or shows the website the other way.
    private static async Task WebsiteSiteDeniedAsync(HttpContext context)
    {
        var german = HtmlViews.Language(context) == "de";
        var title = german ? "Diese Adresse gehört zu einer Matgate-Sitzung" : "This address belongs to a Matgate session";
        var text = german
            ? "Sie ist abgelaufen oder für diesen Browser nicht geöffnet. Öffne die Website in Matgate noch einmal."
            : "It has expired, or it was not opened for this browser. Open the website in Matgate once more.";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync($$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; font: 16px/1.5 system-ui, sans-serif; background: #f4f5f7; color: #1d2330; }
            main { max-width: 32rem; margin: 1.5rem; padding: 1.5rem 1.75rem; background: #fff; border-radius: 14px; box-shadow: 0 6px 24px rgba(20, 30, 50, .08); }
            h1 { margin: 0 0 .5rem; font-size: 1.2rem; } p { margin: 0; }
            @media (prefers-color-scheme: dark) { body { background: #14171d; color: #e7eaf0; } main { background: #1d222b; } }
            </style></head><body><main id="matgate-site-denied"><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main>
            <script>if (window.parent !== window) { try { window.parent.postMessage({ matgateSite: 1, denied: true }, '*'); } catch (error) { } }</script>
            </body></html>
            """, context.RequestAborted);
    }
}
