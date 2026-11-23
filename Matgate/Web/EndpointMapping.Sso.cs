using System.Text.Json;
using Matgate.Models;
using Matgate.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace Matgate.Web;

// Signing in through a sign-in service (SingleSignOn): the button on the sign-in page leads to the service, the
// service back to /login/sso/done, and there Matgate finds the user by the service's account - or makes one (with
// no rights: those come from groups) - or, when a user with that address exists already, asks once for its
// password before linking the two. Then the user is signed in like everyone else (CompleteSignInAsync). The
// groups the service names decide the membership in the groups linked to them, at every sign-in.
public static partial class EndpointMapping
{
    private const string SsoLinkCookie = "Matgate.SsoLink";

    private static void MapSsoEndpoints(WebApplication app)
    {
        app.MapGet("/login/sso/{provider}", SsoStartAsync);
        app.MapGet("/login/sso/done", SsoDoneAsync);
        app.MapGet("/admin/sso", () => Results.Redirect("/admin?tab=sso")).RequireAuthorization();
        app.MapPost("/admin/sso", SsoSaveAsync).RequireAuthorization();
        app.MapPost("/admin/sso/test", SsoTestAsync).RequireAuthorization();
    }

    // What the form says, with a secret left empty meaning: keep the one there is.
    private static SsoConfig SsoFormConfig(IFormCollection form, SsoConfig before) => new()
    {
        MatIdentityEnabled = IsChecked(form, "matIdentityEnabled"),
        MatIdentityAuthority = form["matIdentityAuthority"].ToString().Trim(),
        MatIdentityClientId = form["matIdentityClientId"].ToString().Trim(),
        MatIdentityClientSecret = form["matIdentityClientSecret"].ToString() is { Length: > 0 } matSecret ? matSecret : before.MatIdentityClientSecret,
        MatIdentityInternalUrl = form["matIdentityInternalUrl"].ToString().Trim(),
        EntraEnabled = IsChecked(form, "entraEnabled"),
        EntraTenantId = form["entraTenantId"].ToString().Trim(),
        EntraClientId = form["entraClientId"].ToString().Trim(),
        EntraClientSecret = form["entraClientSecret"].ToString() is { Length: > 0 } entraSecret ? entraSecret : before.EntraClientSecret,
        SsoOnly = IsChecked(form, "ssoOnly"),
    };

    private static async Task<IResult> SsoSaveAsync(HttpContext context, JsonDataStore store, HtmlViews views, SsoSettingsService sso)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        var config = SsoFormConfig(form, sso.Load());
        // A service switched on without what it needs would only fail at the next sign-in: switched off instead.
        config.MatIdentityEnabled &= config.MatIdentityAuthority.Length > 0 && config.MatIdentityClientId.Length > 0;
        config.EntraEnabled &= config.EntraTenantId.Length > 0 && config.EntraClientId.Length > 0;
        sso.Save(config);
        return Results.Redirect(EmbedAwareRedirect(context, "/admin?tab=sso"));
    }

    // "Check the connection": the service's discovery document, read from where Matgate reads it - with the values in
    // the form (not yet saved). Says what the service calls itself, or why it could not be reached.
    private static async Task<IResult> SsoTestAsync(HttpContext context, JsonDataStore store, SsoSettingsService sso)
    {
        var admin = await RequireAdminAsync(context, store);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var german = HtmlViews.Language(context) == "de";
        if (admin is null || !ValidateCsrf(context, form))
        {
            return Results.Json(new { ok = false, message = german ? "Nicht erlaubt." : "Not allowed." });
        }

        var config = SsoFormConfig(form, sso.Load());
        string address;
        if (form["provider"] == "entra")
        {
            if (config.EntraTenantId.Length == 0 || config.EntraClientId.Length == 0)
            {
                return Results.Json(new { ok = false, message = german ? "Mandanten-ID und Anwendungs-ID fehlen." : "Tenant ID and client ID are missing." });
            }

            address = $"https://login.microsoftonline.com/{Uri.EscapeDataString(config.EntraTenantId)}/v2.0/.well-known/openid-configuration";
        }
        else
        {
            if (!Uri.TryCreate(config.MatIdentityAuthority, UriKind.Absolute, out var authority) || config.MatIdentityClientId.Length == 0)
            {
                return Results.Json(new { ok = false, message = german ? "Adresse und Client-ID fehlen." : "Address and client ID are missing." });
            }

            var basis = config.MatIdentityInternalUrl.Length > 0 && Uri.TryCreate(config.MatIdentityInternalUrl, UriKind.Absolute, out var inner) ? inner : authority;
            address = basis.ToString().TrimEnd('/') + "/.well-known/openid-configuration";
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(address, context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return Results.Json(new { ok = false, message = $"{(int)response.StatusCode} {response.ReasonPhrase} - {address}" });
            }

            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            var issuer = document.RootElement.TryGetProperty("issuer", out var value) ? value.GetString() : null;
            return Results.Json(new { ok = issuer is not null, message = issuer is null ? (german ? "Kein Anmeldedienst unter dieser Adresse." : "No sign-in service at this address.") : (german ? "Erreicht: " : "Reached: ") + issuer });
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Results.Json(new { ok = false, message = (german ? "Nicht erreicht: " : "Not reached: ") + exception.Message });
        }
    }

    private static IResult SsoStartAsync(string provider, HttpContext context, SsoSettings settings)
    {
        var service = settings.Find(provider);
        if (service is null)
        {
            return Results.Redirect("/login");
        }

        var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
        var properties = new AuthenticationProperties { RedirectUri = "/login/sso/done?returnUrl=" + Uri.EscapeDataString(returnUrl) };
        properties.Items["provider"] = service.Key;
        return Results.Challenge(properties, [service.Key]);
    }

    private static async Task<IResult> SsoDoneAsync(
        HttpContext context,
        JsonDataStore store,
        PasswordHasher hasher,
        IDataProtectionProvider protection,
        GuacamoleConfigWriter configWriter,
        SsoSettings settings,
        ILogger<SsoSettings> logger)
    {
        var result = await context.AuthenticateAsync(SingleSignOn.ExternalScheme);
        await context.SignOutAsync(SingleSignOn.ExternalScheme);
        var provider = result.Properties?.Items.TryGetValue("provider", out var key) == true ? key : null;
        if (!result.Succeeded || result.Principal is null || settings.Find(provider) is null
            || SingleSignOn.Read(provider!, result.Principal) is not { } identity)
        {
            return Results.Redirect("/login?sso=failed");
        }

        var returnUrl = NormalizeReturnUrl(context.Request.Query["returnUrl"].ToString());
        var users = await store.GetUsersAsync(context.RequestAborted);
        var user = users.FirstOrDefault(candidate => candidate.ExternalLogins.Any(login => login.Provider == identity.Provider && login.Subject == identity.Subject));

        if (user is null && identity.Email.Length > 0
            && users.FirstOrDefault(candidate => string.Equals(candidate.Email, identity.Email, StringComparison.OrdinalIgnoreCase)) is { } existing
            && existing.PasswordHash.Length > 0)
        {
            // A user with this address exists already. The address alone does not prove that it is the same
            // person: whoever signs in with its password once links the two.
            AppendSsoLink(context, protection, identity, existing.Id);
            logger.LogInformation("Sign-in through {Provider}: an account with that address exists, waiting for its password to link", identity.Provider);
            return Results.Redirect("/login?link=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        if (user is null)
        {
            var now = DateTimeOffset.UtcNow;
            var created = new MatgateUser
            {
                Email = identity.Email,
                DisplayName = identity.Name.Length > 0 ? identity.Name : identity.UserName,
                // No password of its own: this user signs in through the service.
                PasswordHash = "",
                GuacamolePassword = hasher.GenerateSecret(),
                PreferredLanguage = HtmlViews.Language(context) == "de" ? "de" : "en",
                RememberLoginByDefault = true,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await store.UpdateUsersAsync(all =>
            {
                created.UserName = SingleSignOn.NewUserName(identity, name => all.Any(other => string.Equals(other.UserName, name, StringComparison.OrdinalIgnoreCase)));
                if (identity.Email.Length > 0 && all.Any(other => string.Equals(other.Email, identity.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    created.Email = "";
                }

                all.Add(created);
            }, context.RequestAborted);
            user = created;
            logger.LogInformation("Sign-in through {Provider}: made the user {User}", identity.Provider, created.UserName);
        }

        if (!user.IsEnabled)
        {
            return Results.Redirect("/login?sso=disabled");
        }

        await RecordExternalLoginAsync(store, user.Id, identity, context.RequestAborted);
        await configWriter.SynchronizeAsync(context.RequestAborted);
        var fresh = await store.FindUserByIdAsync(user.Id, context.RequestAborted) ?? user;
        // The service vouches for the person; a second factor is the service's business.
        return await CompleteSignInAsync(context, fresh, hasher, returnUrl);
    }

    // The account linked (or its last sign-in noted), and the user put into and out of the groups linked to what
    // the service named.
    private static async Task RecordExternalLoginAsync(JsonDataStore store, Guid userId, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        await store.UpdateUsersAsync(all =>
        {
            if (all.FirstOrDefault(candidate => candidate.Id == userId) is not { } user)
            {
                return;
            }

            var login = user.ExternalLogins.FirstOrDefault(candidate => candidate.Provider == identity.Provider && candidate.Subject == identity.Subject);
            if (login is null)
            {
                login = new ExternalLogin { Provider = identity.Provider, Subject = identity.Subject };
                user.ExternalLogins.Add(login);
            }

            login.Issuer = identity.Issuer;
            login.Email = identity.Email;
            login.LastSignInAt = DateTimeOffset.UtcNow;
            login.LastGroups = [.. identity.Groups];
        }, cancellationToken);
        await store.UpdateGroupsAsync(groups => SingleSignOn.SyncGroups(userId, identity.Provider, identity.Groups, groups), cancellationToken);
    }

    private sealed record SsoLink(Guid UserId, string Provider, string Issuer, string Subject, string Email, bool EmailVerified, string Name, string UserName, List<string> Groups, DateTimeOffset Expires);

    private static IDataProtector SsoLinkProtector(IDataProtectionProvider protection) => protection.CreateProtector("Matgate.SsoLink.v1");

    private static void AppendSsoLink(HttpContext context, IDataProtectionProvider protection, ExternalIdentity identity, Guid userId)
    {
        var link = new SsoLink(userId, identity.Provider, identity.Issuer, identity.Subject, identity.Email, identity.EmailVerified, identity.Name, identity.UserName, [.. identity.Groups], DateTimeOffset.UtcNow.AddMinutes(10));
        context.Response.Cookies.Append(SsoLinkCookie, SsoLinkProtector(protection).Protect(JsonSerializer.Serialize(link)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = link.Expires,
        });
    }

    // The account waiting to be linked, when the one who just proved a password is the user it waits for.
    private static async Task LinkPendingSsoAsync(HttpContext context, MatgateUser user, JsonDataStore store)
    {
        var raw = context.Request.Cookies[SsoLinkCookie];
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        context.Response.Cookies.Delete(SsoLinkCookie);
        SsoLink? link;
        try
        {
            var protection = context.RequestServices.GetRequiredService<IDataProtectionProvider>();
            link = JsonSerializer.Deserialize<SsoLink>(SsoLinkProtector(protection).Unprotect(raw));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return;
        }

        if (link is null || link.UserId != user.Id || link.Expires < DateTimeOffset.UtcNow)
        {
            return;
        }

        var identity = new ExternalIdentity(link.Provider, link.Issuer, link.Subject, link.Email, link.EmailVerified, link.Name, link.UserName, link.Groups);
        await RecordExternalLoginAsync(store, user.Id, identity, context.RequestAborted);
    }

    // The account of a service taken off a user (the user page): from then on that account no longer signs in as
    // this user. A user without a password of its own then cannot sign in at all until it gets one.
    private static async Task<IResult> UnlinkExternalLoginAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        var provider = form["provider"].ToString();
        var subject = form["subject"].ToString();
        await store.UpdateUsersAsync(all =>
        {
            all.FirstOrDefault(candidate => candidate.Id == id)?.ExternalLogins.RemoveAll(login => login.Provider == provider && login.Subject == subject);
        }, context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, $"/admin/users/{id}"));
    }
}
