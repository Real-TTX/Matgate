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
