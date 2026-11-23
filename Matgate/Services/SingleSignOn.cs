using System.Security.Claims;
using Matgate.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Matgate.Services;

// A sign-in service Matgate can send people to, and what it needs for it. Each one is optional and on only when
// its settings are there:
//
//   MatIdentity          MATIDENTITY_AUTHORITY, MATIDENTITY_CLIENT_ID, MATIDENTITY_CLIENT_SECRET
//                        (MATIDENTITY_INTERNAL_URL: where Matgate itself reaches it, when that differs)
//                        redirect URI: <Matgate>/signin-oidc - the same names and path as every app of the Mat family
//   Microsoft Entra ID   MATGATE_ENTRA_TENANT_ID, MATGATE_ENTRA_CLIENT_ID, MATGATE_ENTRA_CLIENT_SECRET
//                        redirect URI: <Matgate>/signin-entra
//
// MATGATE_SSO_ONLY=true hides the password form on the sign-in page (it stays reachable with /login?local=1 -
// the way in when the service is down).
public sealed record SsoProvider(string Key, string Label, string Authority, string ClientId, string ClientSecret, string CallbackPath, string? InternalUrl)
{
    public const string MatIdentity = "matidentity";
    public const string Entra = "entra";
}

public sealed class SsoSettings
{
    public required IReadOnlyList<SsoProvider> Providers { get; init; }

    public bool SsoOnly { get; init; }

    public SsoProvider? Find(string? key) => Providers.FirstOrDefault(provider => string.Equals(provider.Key, key, StringComparison.OrdinalIgnoreCase));

    public static SsoSettings From(IConfiguration configuration)
    {
        static string Value(IConfiguration configuration, string name) => (configuration[name] ?? "").Trim();
        var providers = new List<SsoProvider>();
        var authority = Value(configuration, "MATIDENTITY_AUTHORITY");
        var clientId = Value(configuration, "MATIDENTITY_CLIENT_ID");
        if (authority.Length > 0 && clientId.Length > 0)
        {
            var internalUrl = Value(configuration, "MATIDENTITY_INTERNAL_URL");
            providers.Add(new(SsoProvider.MatIdentity, "MatIdentity", authority.TrimEnd('/') + "/", clientId, Value(configuration, "MATIDENTITY_CLIENT_SECRET"), "/signin-oidc", internalUrl.Length > 0 ? internalUrl.TrimEnd('/') : null));
        }

        var tenant = Value(configuration, "MATGATE_ENTRA_TENANT_ID");
        var entraClient = Value(configuration, "MATGATE_ENTRA_CLIENT_ID");
        if (tenant.Length > 0 && entraClient.Length > 0)
        {
            var entraAuthority = Value(configuration, "MATGATE_ENTRA_AUTHORITY");
            providers.Add(new(SsoProvider.Entra, "Microsoft", entraAuthority.Length > 0 ? entraAuthority.TrimEnd('/') + "/" : $"https://login.microsoftonline.com/{tenant}/v2.0/", entraClient, Value(configuration, "MATGATE_ENTRA_CLIENT_SECRET"), "/signin-entra", null));
        }

        return new SsoSettings
        {
            Providers = providers,
            SsoOnly = providers.Count > 0 && string.Equals(Value(configuration, "MATGATE_SSO_ONLY"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }
}

// Who the service says signed in - the same shape for every service.
public sealed record ExternalIdentity(string Provider, string Issuer, string Subject, string Email, bool EmailVerified, string Name, string UserName, IReadOnlyList<string> Groups);

public static class SingleSignOn
{
    // Where a sign-in through a service lands for the moment it takes Matgate to find or make the user: a short
    // cookie of its own, never the session.
    public const string ExternalScheme = "Matgate.External";

    // Both services are always registered - whether one is on, and with what, is read when it is used (see
    // SsoSettingsService: set up in the administration, saved in sso.json; without that file the environment). A
    // service switched off gets a harmless stand-in configuration: ASP.NET checks every remote scheme on every request.
    public static AuthenticationBuilder AddMatgateSso(this AuthenticationBuilder builder, bool requireHttps)
    {
        builder.AddCookie(ExternalScheme, options =>
        {
            options.Cookie.Name = "Matgate.External";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });
        builder.AddOpenIdConnect(SsoProvider.MatIdentity, "MatIdentity", _ => { });
        builder.AddOpenIdConnect(SsoProvider.Entra, "Microsoft", _ => { });
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<OpenIdConnectOptions>>(services =>
            new SsoOpenIdOptions(services.GetRequiredService<SsoSettingsService>(), requireHttps));
        return builder;
    }

    private sealed class SsoOpenIdOptions(SsoSettingsService sso, bool requireHttps) : Microsoft.Extensions.Options.IConfigureNamedOptions<OpenIdConnectOptions>
    {
        public void Configure(OpenIdConnectOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);

        public void Configure(string? name, OpenIdConnectOptions options)
        {
            if (name is not (SsoProvider.MatIdentity or SsoProvider.Entra))
            {
                return;
            }

            var provider = sso.Current.Find(name);
            options.SignInScheme = ExternalScheme;
            options.CallbackPath = name == SsoProvider.MatIdentity ? "/signin-oidc" : "/signin-entra";
            if (provider is null)
            {
                options.Authority = "https://sso-off.invalid/";
                options.ClientId = "off";
                return;
            }

            options.Authority = provider.Authority;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret.Length > 0 ? provider.ClientSecret : null;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.RequireHttpsMetadata = provider.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            if (name == SsoProvider.MatIdentity)
            {
                // MatIdentity sends the groups (when switched on for this app) with the roles.
                options.Scope.Add("roles");
            }

            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
            if (!options.RequireHttpsMetadata || !requireHttps)
            {
                // A service on plain http (a test, a LAN): its correlation cookies cannot be "Secure; SameSite=None".
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
            }

            if (provider.InternalUrl is { } internalUrl)
            {
                options.BackchannelHttpHandler = new InternalAddressHandler(new Uri(provider.Authority), new Uri(internalUrl + "/"));
            }

            options.Events.OnRemoteFailure = context =>
            {
                // Refused or cancelled at the service, or a stale callback: back to the sign-in page with a word.
                context.HandleResponse();
                context.Response.Redirect("/login?sso=failed");
                return Task.CompletedTask;
            };
        }
    }

    // What a service said about the person, in one shape. Null when it did not say who (no subject).
    public static ExternalIdentity? Read(string provider, ClaimsPrincipal principal)
    {
        string Claim(string type) => principal.FindFirst(type)?.Value?.Trim() ?? "";
        var issuer = Claim("iss");
        // Entra: the object id is the account's id in the tenant ("sub" differs per app); with the tenant, unique.
        var subject = provider == SsoProvider.Entra && Claim("oid").Length > 0
            ? Claim("tid") + "/" + Claim("oid")
            : Claim("sub");
        if (subject.Length == 0)
        {
            return null;
        }

        var email = Claim("email");
        if (email.Length == 0 && provider == SsoProvider.Entra)
        {
            // Entra names the sign-in address "preferred_username" (or "upn"); "email" only when it is set.
            email = Claim("preferred_username").Contains('@') ? Claim("preferred_username") : Claim("upn");
        }

        var groups = principal.FindAll("groups").Concat(principal.FindAll("roles")).Concat(principal.FindAll("role"))
            .Select(claim => claim.Value.Trim())
            .Where(value => value.Length > 0)
            .Select(value => provider + ":" + value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preferred = Claim("preferred_username");
        return new ExternalIdentity(
            provider,
            issuer,
            subject,
            email,
            // Entra: an address the tenant itself gives out; MatIdentity says whether it was confirmed.
            provider == SsoProvider.Entra || string.Equals(Claim("email_verified"), "true", StringComparison.OrdinalIgnoreCase),
            Claim("name"),
            preferred.Length > 0 ? preferred : email,
            groups);
    }

    // A user name for someone new: from their name at the service ("anna.berg@firma.de" -> "anna.berg"), made
    // unique with a number.
    public static string NewUserName(ExternalIdentity identity, Func<string, bool> taken)
    {
        var source = identity.UserName.Length > 0 ? identity.UserName : identity.Email.Length > 0 ? identity.Email : identity.Name;
        var at = source.IndexOf('@');
        if (at > 0)
        {
            source = source[..at];
        }

        var clean = new string(source.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_').ToArray()).Trim('.', '-', '_');
        if (clean.Length < 3)
        {
            clean = (identity.Provider + "-" + clean).Trim('-');
        }

        clean = clean.Length > 56 ? clean[..56] : clean;
        var name = clean;
        for (var number = 2; taken(name); number++)
        {
            name = clean + number;
        }

        return name;
    }

    // Puts the user into the groups linked to what the service named, and out of those it no longer names. Only
    // groups linked to at least one group of THIS service are touched. True when anything changed.
    public static bool SyncGroups(Guid userId, string provider, IReadOnlyCollection<string> named, List<UserGroup> groups)
    {
        var changed = false;
        foreach (var group in groups)
        {
            var linked = group.ExternalGroups.Where(key => key.StartsWith(provider + ":", StringComparison.OrdinalIgnoreCase)).ToList();
            if (linked.Count == 0)
            {
                continue;
            }

            var belongs = linked.Any(key => named.Contains(key, StringComparer.OrdinalIgnoreCase));
            var member = group.MemberIds.Contains(userId);
            if (belongs && !member)
            {
                group.MemberIds.Add(userId);
                group.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
            else if (!belongs && member)
            {
                group.MemberIds.Remove(userId);
                group.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }

        return changed;
    }

    // Sends Matgate's own requests to the service (its discovery document, keys, the token) to an internal address
    // when the public one is not reachable from where Matgate runs (a container next to it).
    private sealed class InternalAddressHandler(Uri publicBase, Uri internalBase) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri && string.Equals(uri.Authority, publicBase.Authority, StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri) { Scheme = internalBase.Scheme, Host = internalBase.Host, Port = internalBase.Port };
                request.RequestUri = builder.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
